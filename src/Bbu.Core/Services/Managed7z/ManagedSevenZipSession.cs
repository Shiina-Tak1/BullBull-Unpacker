using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services.Managed7z;

/// <summary>判据结果附一句可读的原因。生产路径只用 <see cref="Verdict"/>，原因是给排错看的。</summary>
internal readonly record struct ManagedJudgeOutcome(FastPathVerdict Verdict, string Reason);

/// <summary>
/// 7z 快路的纯托管会话。与产品里 <c>SevenZipFastPathSession</c> 同形：一个包一个对象、
/// <see cref="Judge"/> 可被并发调用、<see cref="IDisposable.Dispose"/> 幂等；
/// 与 dll 那条的根本区别是**没有那把进程级静态锁**，16 路真能并行。
///
/// 判据只有一条：由候选密码算 KDF、AES-256-CBC 解出 folder，再比头里声明的 folder CRC32；
/// 解不出来、结构不认识、长度对不上，一律回 <see cref="FastPathVerdict.Unknown"/> ——
/// 绝不误杀正确密码。
/// </summary>
internal sealed class ManagedSevenZipSession : IDisposable
{
    /// <summary>
    /// 单个 folder 愿意解到多大。超了就交回 dll / 引擎：快路的收益是「省一次起引擎」，
    /// 拿十几 MB 的 LZMA2 去换划不来。
    /// </summary>
    private const long MaxJudgeSize = 16L * 1024 * 1024;

    /// <summary>
    /// LZMA2 解压失败时，「早到多少字节以内」算结构性失败。单位是**字节**：错误密码解出的字节
    /// 是随机的，前 4 KB 里必然撞碎 LZMA2 的格式约束。
    ///
    /// 这是取舍不是定律：万一本实现的解码边界撞上某个合法流，失败点会在很浅的位置，
    /// 那就宁可漏判也不能误杀。
    /// </summary>
    private const int StructuralFailLimit = 4096;

    private readonly ISevenZipSource _source;
    private readonly SevenZipLayout _layout;
    private readonly SevenZipStreams _streams;
    private readonly SevenZipFolder _folder;
    private readonly SevenZipAesKey _aesKey;
    private readonly bool _ownsSource;
    private readonly byte _lzma2Prop;

    private ManagedSevenZipSession(
        ISevenZipSource source,
        SevenZipLayout layout,
        SevenZipStreams streams,
        SevenZipFolder folder,
        SevenZipAesKey aesKey,
        int lzma2Prop,
        bool ownsSource)
    {
        _source = source;
        _layout = layout;
        _streams = streams;
        _folder = folder;
        _aesKey = aesKey;
        _lzma2Prop = (byte)lzma2Prop;
        _ownsSource = ownsSource;
    }

    /// <summary>链上 AES 之后是否还挂着 LZMA2（= 解出来还是压缩流，要先解压才能算 CRC）。</summary>
    private bool NeedsLzma2 => _folder.Coders.Count == 2;

    /// <summary>这个文件夹的判据长什么样，给探针/日志看。</summary>
    public string Describe()
    {
        var header = _layout.EncodedHeader is not null
            ? ManagedFastPathText.EncodedHeader
            : ManagedFastPathText.PlainHeader;
        var crc = _folder.UnpackedCrc is { } c ? $"0x{c:X8}" : ManagedFastPathText.NoCrc;
        var tail = NeedsLzma2 ? ManagedFastPathText.Lzma2Tail(_lzma2Prop) : string.Empty;
        return header + "，" +
               ManagedFastPathText.JudgeFolder(_folder, _folder.UnpackedSize) +
               ManagedFastPathText.ExpectedCrc(crc) + _aesKey.Describe() + tail;
    }

    /// <summary>
    /// 开庭。返回 <c>null</c> = **这条托管路**判不了（结构不认识、链上有本实现解不了的压缩器、
    /// 没有期望 CRC…）。调用方应当接着去试 dll 那条，而不是整轮放弃快路。
    /// 单文件形态走这里；分卷形态见 <see cref="OpenVolumes"/>。
    /// </summary>
    public static ManagedSevenZipSession? Open(string archive) =>
        OpenOwned(() => SevenZipFileSource.Open(archive));

    /// <summary>分卷形态：卷表由调用方排好序传进来，命名规则与缺口判定都不在这一层。</summary>
    public static ManagedSevenZipSession? OpenVolumes(IReadOnlyList<string> volumes) =>
        OpenOwned(() => SevenZipMultiVolumeSource.Open(volumes));

    /// <summary>已经持有打开好的源时用它（调用方保留所有权）。</summary>
    public static ManagedSevenZipSession? Open(ISevenZipSource source) => Open(source, ownsSource: false);

    /// <summary>
    /// 造源并打开会话。三个出口都当场放掉源：造源抛（由源自己的 Open 放未完全打开的句柄）、打开会话回
    /// <c>null</c> 与解析异常逃出去（<c>finally</c> 里 <c>session is null</c> 时放，异常路径同样走它）。
    /// </summary>
    private static ManagedSevenZipSession? OpenOwned(Func<ISevenZipSource> open)
    {
        ISevenZipSource source;
        try
        {
            source = open();
        }
        catch (Exception)
        {
            return null;
        }

        ManagedSevenZipSession? session = null;
        try
        {
            session = Open(source, ownsSource: true);
        }
        finally
        {
            if (session is null)
            {
                source.Dispose();
            }
        }

        return session;
    }

    private static ManagedSevenZipSession? Open(ISevenZipSource source, bool ownsSource)
    {
        try
        {
            var layout = SevenZipLayoutReader.Read(source);
            var streams = layout.EncodedHeader ?? layout.MainStreams;
            if (streams is null || streams.Folders.Count == 0)
            {
                return null;
            }

            if (!TryPickFolder(streams, out var folder, out var key, out var lzma2Prop))
            {
                return null;
            }

            return new ManagedSevenZipSession(source, layout, streams, folder, key, lzma2Prop, ownsSource);
        }
        catch (SevenZipFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 挑一个「能判」的 folder：链首必须是 7zAES、必须有期望 CRC、大小在预算内；
    /// 多个候选时取最小的 —— 解密/解压成本与它成正比。
    ///
    /// 链尾允许的形态只有两种（夹具 `vault\seven-mhe.7z` 与 `seven-plain.7z` 实测）：<c>-mhe=on</c> 的
    /// 加密头是纯 AES，解出来直接就是明文；<c>-mhe=off</c> 是 <c>AES → LZMA2</c>，解密后再用纯托管
    /// LZMA2 内核解压。链上出现 ZSTD / LZ4 / BROTLI / LIZARD（7-Zip-zstd 的扩展）时这里判不了，
    /// 交回 dll 那条 —— 那几格的解码器不在公有领域内核里，也不该为快路引入新依赖。
    /// </summary>
    private static bool TryPickFolder(
        SevenZipStreams streams,
        out SevenZipFolder folder,
        out SevenZipAesKey key,
        out int lzma2Prop)
    {
        folder = null!;
        key = null!;
        lzma2Prop = 0;

        SevenZipFolder? best = null;
        SevenZipAesKey? bestKey = null;
        var bestProp = 0;
        foreach (var candidate in streams.Folders)
        {
            if (candidate.Coders.Count == 0
                || candidate.Coders[0].Id != SevenZipMethod.Aes
                || candidate.UnpackedCrc is null
                || candidate.UnpackedSize == 0
                || candidate.UnpackedSize > (ulong)MaxJudgeSize)
            {
                continue;
            }

            var prop = 0;
            if (candidate.Coders.Count == 2)
            {
                var tail = candidate.Coders[1];
                if (tail.Id != SevenZipMethod.Lzma2 || tail.Properties.Length != 1)
                {
                    continue;
                }

                prop = tail.Properties[0];
            }
            else if (candidate.Coders.Count != 1)
            {
                continue;
            }

            if (!SevenZipAesKey.TryParse(candidate.Coders[0].Properties, out var parsed))
            {
                continue;
            }

            if (best is null || candidate.UnpackedSize < best.UnpackedSize)
            {
                best = candidate;
                bestKey = parsed;
                bestProp = prop;
            }
        }

        if (best is null || bestKey is null)
        {
            return false;
        }

        folder = best;
        key = bestKey;
        lzma2Prop = bestProp;
        return true;
    }

    /// <summary>
    /// 判一个候选密码。<b>并发安全</b>：共享状态只读；中间缓冲按线程各持一份，
    /// 所以多线程同时判不同密码不会互相踩。
    /// </summary>
    public FastPathVerdict Judge(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return FastPathVerdict.Unknown;
        }

        return JudgeDetailed(password).Verdict;
    }

    /// <summary>
    /// 带原因的判据。<see cref="Judge"/> 是它的薄壳：分开是为了让排错的人看见
    /// 「为什么判不了」，而不必把内部状态暴露出去。
    /// </summary>
    public ManagedJudgeOutcome JudgeDetailed(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.EmptyPassword);
        }

        try
        {
            var key = _aesKey.DeriveKey(PasswordBytes(password));
            var (offset, size) = _streams.PackedRange(_folder);
            if (size == 0 || size > int.MaxValue || (size & 0xF) != 0)
            {
                return new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.CipherLengthInvalid(size));
            }

            var length = (int)size;
            var cipher = RentBuffer(ref _cipherBuffer, length);
            var cipherSpan = cipher.AsSpan(0, length);
            if (_source.ReadAt(_layout.DataBase + (long)offset, cipherSpan) != cipherSpan.Length)
            {
                return new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.CipherTruncated);
            }

            var plain = RentBuffer(ref _plainBuffer, length);
            if (!SevenZipAesKey.TryDecrypt(key, _aesKey.IV, cipherSpan, plain, out var written))
            {
                return new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.AesFailed);
            }

            var unpackedSize = _folder.UnpackedSize;
            if (_folder.UnpackedCrc is not { } expected || unpackedSize == 0)
            {
                return new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.NoUsableCrc);
            }

            // 纯 AES 那条链，解出来的就是明文，长度必须落在解密结果之内；
            // 带 LZMA2 时 unpackedSize 是**解压后**的长度，比密文大是正常的。
            if (!NeedsLzma2 && unpackedSize > (ulong)written)
            {
                return new ManagedJudgeOutcome(
            FastPathVerdict.Unknown, ManagedFastPathText.DeclaredTooLong(unpackedSize, written));
            }

            byte[] data;
            if (NeedsLzma2)
            {
                // 链尾的 LZMA2 的输入长度是「AES 那一级的输出大小」——它可能比密文短
                // （CBC 的最后一块是补齐出来的，实测 32 字节密文对 31 字节 LZMA2 流）。
                var compressedLength = _folder.CoderUnpackSizes.Count > 0
                    ? (int)_folder.CoderUnpackSizes[0]
                    : (int)unpackedSize;
                if (compressedLength <= 0 || compressedLength > written)
                {
                    return new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.Lzma2LengthInvalid);
                }

                data = RentBuffer(ref _plaintextBuffer, (int)unpackedSize);
                if (!Lzma2Decoder.TryDecode(plain, compressedLength, _lzma2Prop, data, (int)unpackedSize, out var produced))
                {
                    // 解压失败有两种含义。
                    //   失败点浅于 StructuralFailLimit → 这串字节几乎不可能是编码器写出来的合法流 → 判错；
                    //   走到很深才崩 → 更可能是本实现的解码边界 → 保守回「不知道」。
                    return produced < StructuralFailLimit
                        ? new ManagedJudgeOutcome(
                FastPathVerdict.ProbablyWrong, ManagedFastPathText.Lzma2StructFailed(produced))
                        : new ManagedJudgeOutcome(
                FastPathVerdict.Unknown, ManagedFastPathText.Lzma2FailedLate(produced));
                }
            }
            else
            {
                data = plain;
            }

            var actual = Crc32.Standard(data.AsSpan(0, (int)unpackedSize));
            return actual != expected
                ? new ManagedJudgeOutcome(
                FastPathVerdict.ProbablyWrong, ManagedFastPathText.CrcMismatch(actual, expected))
                : new ManagedJudgeOutcome(FastPathVerdict.Unknown, ManagedFastPathText.CrcMatch);
        }
        catch (Exception ex)
        {
            // 判据永远不许因为自己出错而断言「密码错」。
            return new ManagedJudgeOutcome(
            FastPathVerdict.Unknown, ManagedFastPathText.JudgeThrew(ex.GetType().Name));
        }
    }

    /// <summary>
    /// 7z 的密码字节：7-Zip 收的是 UTF-16 的 BSTR，<c>CryptoSetPassword</c> 拿到的就是 UTF-16LE 无 BOM。
    /// 中文密码这一档必须与 dll 那条走同一种编码，否则会把**对**的密码判死。
    /// </summary>
    private static byte[] PasswordBytes(string password) => Encoding.Unicode.GetBytes(password);

    /// <summary>
    /// 每线程一份的候选缓冲。候选循环本身是并发的，所以不能共享同一块；
    /// 但同一线程上连着判很多密码时复用能省掉每候选三次分配 —— 单价里能看见的那点开销就在这。
    /// </summary>
    [ThreadStatic]
    private static byte[]? _cipherBuffer;

    [ThreadStatic]
    private static byte[]? _plainBuffer;

    [ThreadStatic]
    private static byte[]? _plaintextBuffer;

    private static byte[] RentBuffer(ref byte[]? slot, int size)
    {
        var buffer = slot;
        if (buffer is null || buffer.Length < size)
        {
            buffer = new byte[size];
            slot = buffer;
        }

        return buffer;
    }

    public void Dispose()
    {
        if (_ownsSource)
        {
            _source.Dispose();
        }
    }

}
