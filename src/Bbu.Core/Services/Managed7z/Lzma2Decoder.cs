using System;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 纯托管 LZMA2 解码内核。从 7-Zip 的**公有领域**内核移植
/// （<c>C/LzmaDec.c</c> 的 <c>LZMA_DECODE_REAL</c> + <c>C/Lzma2Dec.c</c> 的 chunk 层）；
/// 出处与许可登记在 <c>docs\THIRD-PARTY-NOTICES.md</c>。两处刻意的偏差：源码为流式、可中断、可恢复设计
/// （<c>remainLen</c> / <c>tempBuf</c> / <c>finishMode</c> 那一整套），这里做成一次性解码 ——
/// 字典就是输出缓冲本身（连续、不回绕），<c>end marker</c> 按失败处理而不是按流结束；
/// 安全的原因是 LZMA2 的字典重置（control &gt;= 0xE0）只把 <c>processedPos</c> 归零、
/// **不动 dicPos**，输出始终连续追加。错误密码可能引用越界或撞上格式校验，此时一律回失败 ——
/// 判据当成「不知道」，绝不当「密码错」。
/// </summary>
internal sealed class Lzma2Decoder
{
    // ---- 概率表布局（LzmaDec.c:139-151，基址取数组开头，故与源码的 1664 偏移错开） ----
    private const int SpecPos = 0;
    private const int IsRep0Long = SpecPos + 128;
    private const int RepLenCoder = IsRep0Long + (16 << 4);
    private const int LenCoder = RepLenCoder + 512;
    private const int IsMatch = LenCoder + 512;
    private const int Align = IsMatch + (16 << 4);
    private const int IsRep = Align + 16;
    private const int IsRepG0 = IsRep + 12;
    private const int IsRepG1 = IsRepG0 + 12;
    private const int IsRepG2 = IsRepG1 + 12;
    private const int PosSlot = IsRepG2 + 12;
    private const int Literal = PosSlot + (4 << 6);

    /// <summary><c>NUM_BASE_PROBS</c>：源码里那个 1984 的断言。</summary>
    private const int BaseProbs = Literal;

    private const int LitSize = 0x300;
    private const int NumStates = 12;
    private const int NumLitStates = 7;
    private const int ProbInit = 2048 >> 1;
    private const int MoveBits = 5;
    private const int TopValue = 1 << 24;

    private readonly byte[] _output;
    private readonly byte[] _input;
    private readonly int _outputLimit;
    private readonly int _inputLimit;
    private readonly uint _dicSize;

    private ushort[] _probs = [];
    private int _lc;
    private int _lp;
    private int _pb;

    private int _state;
    private uint _rep0 = 1;
    private uint _rep1 = 1;
    private uint _rep2 = 1;
    private uint _rep3 = 1;

    private uint _range;
    private uint _code;
    private uint _processedPos;
    private uint _checkDicSize;

    private int _dicPos;
    private int _inputPos;
    private bool _failed;

    private Lzma2Decoder(byte[] input, int inputLength, byte[] output, int outputLength, uint dicSize)
    {
        _input = input;
        _inputLimit = inputLength;
        _output = output;
        _outputLimit = outputLength;
        _dicSize = dicSize;
    }

    /// <summary>
    /// 解一段 LZMA2 流。<paramref name="prop"/> 是 7z 头里 LZMA2 coder 的那一个 properties 字节。
    /// 成功时 <paramref name="output"/> 的前 <paramref name="outputLength"/> 字节就是明文。
    ///
    /// <paramref name="produced"/> 无论成败都给出「已经解出多少字节」——
    /// 判据靠它区分「格式当场就不成立」（强信号）与「走到很深才失败」（可能只是解码器的边界，
    /// 那就保守回「不知道」）。
    /// </summary>
    public static bool TryDecode(
        byte[] input,
        int inputLength,
        byte prop,
        byte[] output,
        int outputLength,
        out int produced)
    {
        // LZMA2_DIC_SIZE_FROM_PROP
        if (prop > 40)
        {
            produced = 0;
            return false;
        }

        var dicSize = prop == 40 ? uint.MaxValue : (2u | (prop & 1u)) << (prop / 2 + 11);
        var decoder = new Lzma2Decoder(input, inputLength, output, outputLength, dicSize);
        decoder.InitProps(Lzma2OldPropsLcLp, 0, 0);
        var ok = decoder.Run();
        produced = decoder._dicPos;
        return ok;
    }

    /// <summary><c>LZMA2_LCLP_MAX</c>：LZMA2 的初始 props 就是 lc=4、lp=0、pb=0。</summary>
    private const int Lzma2OldPropsLcLp = 4;

    private void InitProps(int lc, int lp, int pb)
    {
        _lc = lc;
        _lp = lp;
        _pb = pb;
        var numProbs = BaseProbs + (LitSize << (lc + lp));
        _probs = new ushort[numProbs];
        ResetState();
    }

    private void ResetState()
    {
        Array.Fill(_probs, (ushort)ProbInit);
        _rep0 = _rep1 = _rep2 = _rep3 = 1;
        _state = 0;
    }

    private bool Run()
    {
        // Lzma2Dec.c:97 的 needInitLevel：第一个 chunk 必须自己把字典摆正。
        var needInitLevel = 0xE0;

        for (; ; )
        {
            if (_inputPos >= _inputLimit)
            {
                return false;
            }

            var control = _input[_inputPos++];
            if (control == 0)
            {
                // 结束标记。输出必须正好填满 —— 少一个字节都说明解错了。
                return _dicPos == _outputLimit;
            }

            if ((control & 0x80) == 0)
            {
                // 未压缩 chunk
                if (control == 1)
                {
                    needInitLevel = 0xC0;
                }
                else if (control > 2 || needInitLevel == 0xE0)
                {
                    return false;
                }

                if (!TryReadUInt16(out var unpackSize))
                {
                    return false;
                }

                unpackSize++;
                if (control == 1)
                {
                    _processedPos = 0;
                    _checkDicSize = 0;
                }

                if (_dicPos + unpackSize > _outputLimit || _inputPos + unpackSize > _inputLimit)
                {
                    return false;
                }

                Array.Copy(_input, _inputPos, _output, _dicPos, unpackSize);
                _inputPos += unpackSize;
                _dicPos += unpackSize;
                NoteOutput(unpackSize);
                continue;
            }

            // LZMA chunk
            if (control < needInitLevel)
            {
                return false;
            }

            needInitLevel = 0;

            var unpackSizeLzma = (control & 0x1F) << 16;
            if (!TryReadUInt16(out var unpackLow))
            {
                return false;
            }

            unpackSizeLzma |= unpackLow;
            unpackSizeLzma++;

            if (!TryReadUInt16(out var packSize))
            {
                return false;
            }

            packSize++;

            if ((control & 0x40) != 0)
            {
                if (_inputPos >= _inputLimit)
                {
                    return false;
                }

                if (!SetProps(_input[_inputPos++]))
                {
                    return false;
                }
            }

            // 0xE0+：重置字典（processedPos 归零）；0xA0+：重置状态（概率 + state + reps）。
            if (control >= 0xA0)
            {
                ResetState();
            }

            if (control >= 0xE0)
            {
                _processedPos = 0;
                _checkDicSize = 0;
            }

            if (_dicPos + unpackSizeLzma > _outputLimit || _inputPos + packSize > _inputLimit)
            {
                return false;
            }

            if (!DecodeLzmaChunk(unpackSizeLzma, packSize))
            {
                return false;
            }
        }
    }

    /// <summary><c>Lzma2Dec.c:145</c> 的 props：lc/lp/pb 打包在一个字节里，且 lc+lp 不许超过 4。</summary>
    private bool SetProps(byte value)
    {
        if (value >= 9 * 5 * 5)
        {
            return false;
        }

        var d = value;
        var lc = d % 9;
        d /= 9;
        var pb = d / 5;
        var lp = d % 5;
        if (lc + lp > 4)
        {
            return false;
        }

        if (lc == _lc && lp == _lp && pb == _pb)
        {
            return true;
        }

        _lc = lc;
        _lp = lp;
        _pb = pb;

        var numProbs = BaseProbs + (LitSize << (lc + lp));
        if (_probs.Length < numProbs)
        {
            _probs = new ushort[numProbs];
        }

        return true;
    }

    private bool TryReadUInt16(out int value)
    {
        if (_inputPos + 1 >= _inputLimit)
        {
            value = 0;
            return false;
        }

        value = (_input[_inputPos] << 8) | _input[_inputPos + 1];
        _inputPos += 2;
        return true;
    }

    private void NoteOutput(int count)
    {
        if (_checkDicSize == 0 && _dicSize - _processedPos <= (uint)count)
        {
            _checkDicSize = _dicSize;
        }

        _processedPos += (uint)count;
    }

    /// <summary>解一个 LZMA chunk：range coder 每个 chunk 都重新起（5 字节头，第一个必须是 0）。</summary>
    private bool DecodeLzmaChunk(int unpackSize, int packSize)
    {
        if (packSize < 5 || _input[_inputPos] != 0)
        {
            return false;
        }

        var chunkEnd = _inputPos + packSize;
        _code = ((uint)_input[_inputPos + 1] << 24)
            | ((uint)_input[_inputPos + 2] << 16)
            | ((uint)_input[_inputPos + 3] << 8)
            | _input[_inputPos + 4];
        _inputPos += 5;
        _range = 0xFFFFFFFF;

        var outEnd = _dicPos + unpackSize;
        while (_dicPos < outEnd)
        {
            if (!DecodeSymbol(outEnd, chunkEnd))
            {
                return false;
            }
        }

        // chunk 尾部的 range coder flush 字节直接跳过。
        _inputPos = chunkEnd;
        return true;
    }

    private int DecodeBit(int index, int chunkEnd)
    {
        var prob = _probs[index];
        if (_range < TopValue)
        {
            if (_inputPos >= chunkEnd)
            {
                _failed = true;
                return 0;
            }

            _range <<= 8;
            _code = (_code << 8) | _input[_inputPos++];
        }

        var bound = (_range >> 11) * prob;
        if (_code < bound)
        {
            _range = bound;
            _probs[index] = (ushort)(prob + ((2048 - prob) >> MoveBits));
            return 0;
        }

        _range -= bound;
        _code -= bound;
        _probs[index] = (ushort)(prob - (prob >> MoveBits));
        return 1;
    }

    /// <summary><c>TREE_DECODE</c>：正的二叉树，返回 0..(1&lt;&lt;bits)-1。</summary>
    private int DecodeTree(int baseIndex, int bits, int chunkEnd)
    {
        var m = 1;
        for (var i = 0; i < bits; i++)
        {
            m = (m << 1) | DecodeBit(baseIndex + m, chunkEnd);
        }

        return m - (1 << bits);
    }

    private bool DecodeSymbol(int outEnd, int chunkEnd)
    {
        _failed = false;
        var posState = (int)(_processedPos & ((1u << _pb) - 1)) << 4;

        if (DecodeBit(IsMatch + posState + _state, chunkEnd) == 0)
        {
            var literalBase = Literal;
            if (_processedPos != 0 || _checkDicSize != 0)
            {
                var previous = _dicPos == 0 ? 0 : _output[_dicPos - 1];
                var lpMask = (0x100 << _lp) - (0x100 >> _lc);
                literalBase += 3 * (int)((((uint)_processedPos << 8) + previous) & (uint)lpMask) << _lc;
            }

            _processedPos++;
            var symbol = 1;

            if (_state < NumLitStates)
            {
                _state -= _state < 4 ? _state : 3;
                do
                {
                    symbol = (symbol << 1) | DecodeBit(literalBase + symbol, chunkEnd);
                }
                while (symbol < 0x100 && !_failed);
            }
            else
            {
                if (_dicPos < _rep0)
                {
                    return false;
                }

                var matchByte = _output[_dicPos - (int)_rep0];
                var offs = 0x100;
                _state -= _state < 10 ? 3 : 6;
                do
                {
                    matchByte <<= 1;
                    var bit = offs;
                    offs &= matchByte;
                    var decoded = DecodeBit(literalBase + offs + bit + symbol, chunkEnd);
                    symbol = (symbol << 1) | decoded;
                    if (decoded == 0)
                    {
                        offs ^= bit;
                    }
                }
                while (symbol < 0x100 && !_failed);
            }

            if (_failed)
            {
                return false;
            }

            _output[_dicPos++] = (byte)symbol;
            return true;
        }

        int len;
        if (DecodeBit(IsRep + _state, chunkEnd) == 0)
        {
            // 新匹配
            _state += NumStates;
            len = DecodeLen(LenCoder, posState, chunkEnd);
            if (_failed)
            {
                return false;
            }

            var posSlot = (uint)DecodeTree(PosSlot + ((len < 4 ? len : 3) << 6), 6, chunkEnd);
            if (_failed)
            {
                return false;
            }

            var distance = posSlot;
            if (distance >= 4)
            {
                var numDirectBits = (int)((distance >> 1) - 1);
                distance = 2 | (distance & 1);

                if (posSlot < 14)
                {
                    distance <<= numDirectBits;
                    distance++;
                    var m = 1u;
                    var bits = numDirectBits;
                    do
                    {
                        if (DecodeBit(SpecPos + (int)distance, chunkEnd) == 0)
                        {
                            distance += m;
                            m += m;
                        }
                        else
                        {
                            m += m;
                            distance += m;
                        }
                    }
                    while (--bits != 0 && !_failed);

                    distance -= m;
                }
                else
                {
                    var bits = numDirectBits - 4;
                    do
                    {
                        if (_range < TopValue)
                        {
                            if (_inputPos >= chunkEnd)
                            {
                                return false;
                            }

                            _range <<= 8;
                            _code = (_code << 8) | _input[_inputPos++];
                        }

                        _range >>= 1;
                        _code -= _range;
                        var t = 0u - (_code >> 31);
                        distance = (distance << 1) + (t + 1);
                        _code += _range & t;
                    }
                    while (--bits != 0);

                    distance <<= 4;
                    var i = 1u;
                    i += DecodeBit(Align + (int)i, chunkEnd) == 0 ? 1u : 2u;
                    i += DecodeBit(Align + (int)i, chunkEnd) == 0 ? 2u : 4u;
                    i += DecodeBit(Align + (int)i, chunkEnd) == 0 ? 4u : 8u;
                    if (DecodeBit(Align + (int)i, chunkEnd) == 0)
                    {
                        i -= 8;
                    }

                    distance |= i;

                    if (_failed)
                    {
                        return false;
                    }

                    // 0xFFFFFFFF 是 LZMA 的 end marker：7z 的 folder 数据里不该出现。
                    if (distance == uint.MaxValue)
                    {
                        return false;
                    }
                }

                if (_failed)
                {
                    return false;
                }
            }

            _rep3 = _rep2;
            _rep2 = _rep1;
            _rep1 = _rep0;
            _rep0 = distance + 1;
            _state = _state < NumStates + NumLitStates ? NumLitStates : NumLitStates + 3;

            // 距离必须落在「自字典重置以来已输出」的范围内（LzmaDec.c:537）。
            if (distance >= (_checkDicSize == 0 ? _processedPos : _checkDicSize))
            {
                return false;
            }

            len += 2;
        }
        else
        {
            // 重复距离
            if (DecodeBit(IsRepG0 + _state, chunkEnd) == 0)
            {
                if (DecodeBit(IsRep0Long + posState + _state, chunkEnd) == 0)
                {
                    // 短重复：复制 1 个字节就完事
                    if (_failed || _dicPos < _rep0)
                    {
                        return false;
                    }

                    _state = _state < NumLitStates ? 9 : 11;
                    _output[_dicPos] = _output[_dicPos - (int)_rep0];
                    _dicPos++;
                    _processedPos++;
                    return true;
                }
            }
            else
            {
                uint distance;
                if (DecodeBit(IsRepG1 + _state, chunkEnd) == 0)
                {
                    distance = _rep1;
                }
                else
                {
                    if (DecodeBit(IsRepG2 + _state, chunkEnd) == 0)
                    {
                        distance = _rep2;
                    }
                    else
                    {
                        distance = _rep3;
                        _rep3 = _rep2;
                    }

                    _rep2 = _rep1;
                }

                _rep1 = _rep0;
                _rep0 = distance;
            }

            if (_failed)
            {
                return false;
            }

            _state = _state < NumLitStates ? 8 : 11;
            len = DecodeLen(RepLenCoder, posState, chunkEnd);
            if (_failed)
            {
                return false;
            }

            len += 2;
        }

        // 复制匹配。逐字节走，因为距离可能小于长度（重叠复制）。
        if (len > outEnd - _dicPos || _dicPos < _rep0)
        {
            return false;
        }

        var source = _dicPos - (int)_rep0;
        for (var i = 0; i < len; i++)
        {
            _output[_dicPos++] = _output[source++];
        }

        NoteOutput(len);
        return true;
    }

    private int DecodeLen(int lenCoderBase, int posState, int chunkEnd)
    {
        int len;
        if (DecodeBit(lenCoderBase, chunkEnd) == 0)
        {
            var b = lenCoderBase + posState;
            len = 1;
            len = (len << 1) | DecodeBit(b + len, chunkEnd);
            len = (len << 1) | DecodeBit(b + len, chunkEnd);
            len = (len << 1) | DecodeBit(b + len, chunkEnd);
            len -= 8;
        }
        else if (DecodeBit(lenCoderBase + 8, chunkEnd) == 0)
        {
            var b = lenCoderBase + posState + 8;
            len = 1;
            len = (len << 1) | DecodeBit(b + len, chunkEnd);
            len = (len << 1) | DecodeBit(b + len, chunkEnd);
            len = (len << 1) | DecodeBit(b + len, chunkEnd);
        }
        else
        {
            len = DecodeTree(lenCoderBase + 256, 8, chunkEnd) + 16;
        }

        return len;
    }
}
