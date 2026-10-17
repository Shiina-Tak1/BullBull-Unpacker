using System;
using System.Text;

namespace Bbu.Core.Services.Rar4;

/// <summary>
/// RAR4（RAR 3.x 格式）的 KDF：官方 <c>crypt3.cpp</c> 的 <c>SetKey30</c>。
/// 产出 AES-128 的密钥与 CBC 的 IV。
///
/// **不能用 <c>SHA1</c> 或 <c>Rfc2898DeriveBytes</c> 代替**：这里那支 SHA-1 是 RAR 私有的变体
/// （<c>sha1_process_rar29</c>），它在处理每个整块之后把消息扩展的 W[64..79] 按小端写回输入缓冲，
/// 于是下一轮的输入是上一轮的产物。三处容易写错：AESInit 取 <c>digest[4]</c> 的**最低字节**；
/// 写回的是 W[64..79]（环形工作区的最终内容）而不是 W[0..15]；写回是就地改写调用方的缓冲，必须接住。
/// </summary>
internal static class Rar4Kdf
{
    /// <summary>RAR 的密码上限：官方只取前 127 个 UTF-16 码元（<c>MAXPASSWORD_RAR - 1</c>）。</summary>
    internal const int MaxPasswordUnits = 127;

    private const int Rounds = 0x40000;

    /// <summary>按官方口径截断。调用方在超限时应当回「不知道」，不要依赖它替自己截。</summary>
    internal static string TakeRarPassword(string password) =>
        password.Length <= MaxPasswordUnits ? password : password[..MaxPasswordUnits];

    /// <summary>密码按 UTF-16LE 落成原始字节，再接 8 字节 salt。</summary>
    internal static byte[] RawPassword(string password, byte[] salt)
    {
        var raw = new byte[(password.Length * 2) + salt.Length];
        Encoding.Unicode.GetBytes(password, raw);
        salt.CopyTo(raw, password.Length * 2);
        return raw;
    }

    /// <summary>crypt3.cpp::SetKey30。线程安全：状态全在栈上，调用者各拿各的。</summary>
    internal static (byte[] Key, byte[] Init) SetKey30(string password, byte[] salt)
    {
        var raw = RawPassword(TakeRarPassword(password), salt);
        var context = new Sha1Context();
        var init = new byte[16];
        var step = Rounds / 16;
        Span<byte> digest = stackalloc byte[20];

        for (var i = 0; i < Rounds; i++)
        {
            context.ProcessRar29(raw, raw.Length);
            context.Process(i);
            if (i % step == 0)
            {
                context.Digest(digest);
                init[i / step] = digest[19];
            }
        }

        context.Digest(digest);
        var key = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            var word = (uint)((digest[i * 4] << 24) | (digest[(i * 4) + 1] << 16)
                | (digest[(i * 4) + 2] << 8) | digest[(i * 4) + 3]);
            for (var j = 0; j < 4; j++)
            {
                key[(i * 4) + j] = (byte)(word >> (j * 8));
            }
        }

        return (key, init);
    }

    /// <summary>官方的 <c>sha1_context</c>：跨轮累积计数，所以每轮的字节对齐位置都在变。</summary>
    private sealed class Sha1Context
    {
        private readonly uint[] _state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];
        private readonly byte[] _buffer = new byte[64];
        private readonly byte[] _scratch = new byte[8];
        private readonly uint[] _w = new uint[80];
        private long _count;

        /// <summary>传入三个字节的轮数（<c>PswNum</c>）。</summary>
        internal void Process(int value)
        {
            _scratch[0] = (byte)value;
            _scratch[1] = (byte)(value >> 8);
            _scratch[2] = (byte)(value >> 16);
            Core(_scratch, 0, 3, rar29: false);
        }

        /// <summary><c>sha1_process_rar29</c>：与标准那支只差写回 W[64..79] 这一处。</summary>
        internal void ProcessRar29(byte[] data, int length) => Core(data, 0, length, rar29: true);

        /// <summary>
        /// 照抄 <c>sha1_process</c> 的骨架：跨 buffer 边界的那一块在 buffer 里做，
        /// 只有直接从 <paramref name="data"/> 上取的整块才写回。
        /// </summary>
        private void Core(byte[] data, int offset, int length, bool rar29)
        {
            var j = (int)(_count & 63);
            _count += length;
            var i = 0;
            if (j + length > 63)
            {
                i = 64 - j;
                Array.Copy(data, offset, _buffer, j, i);
                Transform(_buffer, 0, writeTail: false);
                while (i + 63 < length)
                {
                    Transform(data, offset + i, writeTail: rar29);
                    i += 64;
                }

                j = 0;
            }

            if (length > i)
            {
                Array.Copy(data, offset + i, _buffer, j, length - i);
            }
        }

        internal void Digest(Span<byte> destination)
        {
            var clone = new Sha1Context();
            Array.Copy(_state, clone._state, 5);
            Array.Copy(_buffer, clone._buffer, 64);
            clone._count = _count;

            var bits = (ulong)_count * 8;
            clone._scratch[0] = 0x80;
            clone.Core(clone._scratch, 0, 1, rar29: false);
            clone._scratch[0] = 0x00;
            while ((clone._count & 63) != 56)
            {
                clone.Core(clone._scratch, 0, 1, rar29: false);
            }

            clone._scratch[0] = (byte)(bits >> 56);
            clone._scratch[1] = (byte)(bits >> 48);
            clone._scratch[2] = (byte)(bits >> 40);
            clone._scratch[3] = (byte)(bits >> 32);
            clone._scratch[4] = (byte)(bits >> 24);
            clone._scratch[5] = (byte)(bits >> 16);
            clone._scratch[6] = (byte)(bits >> 8);
            clone._scratch[7] = (byte)bits;
            clone.Core(clone._scratch, 0, 8, rar29: false);

            for (var i = 0; i < 5; i++)
            {
                var word = clone._state[i];
                destination[i * 4] = (byte)(word >> 24);
                destination[(i * 4) + 1] = (byte)(word >> 16);
                destination[(i * 4) + 2] = (byte)(word >> 8);
                destination[(i * 4) + 3] = (byte)word;
            }
        }

        /// <summary>
        /// <c>SHA1Transform</c>。<paramref name="writeTail"/> 为真时把 W[64..79] 小端写回
        /// <paramref name="data"/> 的对应位置，这就是 RAR 那支的全部特殊之处。
        /// </summary>
        private void Transform(byte[] data, int offset, bool writeTail)
        {
            var w = _w;
            for (var i = 0; i < 16; i++)
            {
                var o = offset + (i * 4);
                w[i] = (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
            }

            for (var i = 16; i < 80; i++)
            {
                var v = w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16];
                w[i] = (v << 1) | (v >> 31);
            }

            var a = _state[0];
            var b = _state[1];
            var c = _state[2];
            var d = _state[3];
            var e = _state[4];

            for (var i = 0; i < 80; i++)
            {
                uint f, k;
                if (i < 20)
                {
                    f = (b & c) | (~b & d);
                    k = 0x5A827999;
                }
                else if (i < 40)
                {
                    f = b ^ c ^ d;
                    k = 0x6ED9EBA1;
                }
                else if (i < 60)
                {
                    f = (b & c) | (b & d) | (c & d);
                    k = 0x8F1BBCDC;
                }
                else
                {
                    f = b ^ c ^ d;
                    k = 0xCA62C1D6;
                }

                var temp = ((a << 5) | (a >> 27)) + f + e + k + w[i];
                e = d;
                d = c;
                c = (b << 30) | (b >> 2);
                b = a;
                a = temp;
            }

            _state[0] += a;
            _state[1] += b;
            _state[2] += c;
            _state[3] += d;
            _state[4] += e;

            if (!writeTail)
            {
                return;
            }

            for (var k = 0; k < 16; k++)
            {
                var v = w[64 + k];
                var o = offset + (k * 4);
                data[o] = (byte)v;
                data[o + 1] = (byte)(v >> 8);
                data[o + 2] = (byte)(v >> 16);
                data[o + 3] = (byte)(v >> 24);
            }
        }
    }
}
