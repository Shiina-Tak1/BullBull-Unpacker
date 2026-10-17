using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;

namespace Bbu.Core.Services.Rar4;

/// <summary>
/// RAR4（RAR 3.x 格式）快路的判据。一个包只开庭一次：<see cref="Open"/> 解析与密码无关的
/// 元数据并把要判的密文读进内存，<see cref="Judge"/> 只做 KDF、解密与 CRC，必须线程安全。
///
/// 判据分两级。<c>-p</c>（文件级加密）：FILE_HEAD 明文，salt 在头块之内紧跟文件名；密文是数据区，
/// AES-128-CBC、IV = AESInit；CRC32(明文前 UNP_SIZE 字节) 对 FILE_CRC（extract.cpp:898）。
/// <c>-hp</c>（档案级加密）：MAIN_HEAD 明文带 0x0080，其后每块各自独立 CBC（IV 都是 AESInit）、
/// 每块前各有 8 字节 salt；先解头块校验 HEAD_CRC，再解该条目的数据段对 FILE_CRC，
/// 头链里的 SERVICE/CMT 块要跳过自己的数据区继续走（arcread.cpp:249/428）。
/// **只有 stored（method=0x30）条目判得动**：压缩条目的 CRC 要解开压缩流才算得出（extract.cpp:940）；
/// 判不了就让整轮不进快路（<see cref="Open"/> 回 <c>null</c>）或回「不知道」。
/// </summary>
internal sealed class Rar4FastPathSession : IFastPathSession
{
    /// <summary>超过这个大小就不判：解密与 CRC 的代价已经压过起一次进程。</summary>
    internal const int MaxStoredBytes = 8 << 20;

    /// <summary>RAR4 签名，7 字节。</summary>
    internal static readonly byte[] Magic = [0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00];

    /// <summary>-hp 时一次解密用来读出 hsize 的探测长度（头段按 16 对齐，正常远小于它）。</summary>
    private const int HeadProbe = 1024;

    /// <summary>单个头块长度上限：超过它只能是密码不对解出的乱码。</summary>
    private const int MaxHeaderSize = 0x8000;

    /// <summary>FILE_HEAD / SERVICE_HEAD 的最小长度：这两类头块的字段一直排到偏移 28。</summary>
    private const int MinFileHeaderSize = 32;

    /// <summary>头链最多走这么多块（-hp 时每块一次 AES 与可能的 KDF）。</summary>
    private const int MaxHeaderBlocks = 256;

    private const byte HeadMain = 0x73;
    private const byte HeadFile = 0x74;
    private const byte HeadService = 0x7a;
    private const byte HeadEndArc = 0x7b;

    private const ushort LhdSplitBefore = 0x0001;
    private const ushort LhdSplitAfter = 0x0002;
    private const ushort LhdPassword = 0x0004;
    private const ushort LhdComment = 0x0008;
    private const ushort LhdDirMask = 0x00e0;
    private const ushort LhdDirectory = 0x00e0;
    private const ushort LhdLarge = 0x0100;
    private const ushort LhdSalt = 0x0400;
    private const ushort MhdEncrypted = 0x0080;

    private const byte MethodStored = 0x30;

    /// <summary>旧加密档（arcread.cpp:291-299）：这些 UNP_VER 不走 SetKey30，判据无从下手。</summary>
    private static bool IsLegacyCrypt(byte unpVer) => unpVer is 13 or 15 or 20 or 26;

    private readonly byte[] _cipher;
    private readonly byte[] _salt;
    private readonly bool _headerEncrypted;
    private readonly uint _crc;
    private readonly int _plainLength;

    private Rar4FastPathSession(byte[] cipher, byte[] salt, bool headerEncrypted, uint crc, int plainLength)
    {
        _cipher = cipher;
        _salt = salt;
        _headerEncrypted = headerEncrypted;
        _crc = crc;
        _plainLength = plainLength;
    }

    /// <summary>这个包的判据长什么样，给探针与排错读（与 <c>ManagedSevenZipSession.Describe</c> 同形）。</summary>
    internal string Describe() => _headerEncrypted
        ? Rar4FastPathText.HeaderEncrypted
        : Rar4FastPathText.JudgeEntry(_plainLength, _crc);

    /// <summary>读一次元数据。判不了返回 <c>null</c>（整轮不进快路）。</summary>
    internal static Rar4FastPathSession? Open(FileStream stream)
    {
        if (stream.Length < 32 || stream.Length > int.MaxValue)
        {
            return null;
        }

        var head = new byte[(int)Math.Min(stream.Length, 1 << 20)];
        stream.Position = 0;
        var read = 0;
        while (read < head.Length)
        {
            var got = stream.Read(head, read, head.Length - read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        var at = 7L;
        while (at + 7 <= read)
        {
            var i = (int)at;
            var headerType = head[i + 2];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(i + 3));
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(i + 5));
            if (headerSize < 7 || at + headerSize > read)
            {
                return null;
            }

            if (headerType == HeadMain && (flags & MhdEncrypted) != 0)
            {
                // -hp：MAIN_HEAD 之后整个后续部分都加密，布局是
                // [salt 8][ALIGN(hsize,16) 密文][条目数据 pack][salt 8][下一块密文]…
                // （archive.cpp:290 的 FullHeaderSize 与 arcread.cpp:157）
                var saltAt = at + headerSize;
                if (saltAt + 8 > read || saltAt + 8 + 16 > stream.Length)
                {
                    return null;
                }

                var salt = head.AsSpan((int)saltAt, 8).ToArray();
                var cipher = ReadRange(stream, saltAt + 8, MaxStoredBytes + HeadProbe);
                return cipher is null ? null : new Rar4FastPathSession(cipher, salt, true, 0, 0);
            }

            if (headerType is HeadFile or HeadService)
            {
                if (headerSize < MinFileHeaderSize)
                {
                    return null;
                }

                // 头链必须跳过数据区，否则下一个「块头」读到的是数据（arcread.cpp:428）。
                var pack = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(i + 7));
                if (headerType == HeadFile && (flags & LhdPassword) != 0)
                {
                    var session = BuildFileEncrypted(stream, head, i, flags, headerSize);
                    if (session is not null)
                    {
                        return session;
                    }
                }

                at += (long)headerSize + pack;
            }
            else
            {
                at += headerSize;
            }
        }

        return null;
    }

    /// <summary>一个 FILE_HEAD 能不能拿来判：不满足就跳过它继续找下一个（判不了比判错好）。</summary>
    private static bool IsJudgeable(
        ushort flags, byte unpVer, byte method, uint pack, uint unp, int headerSize, int nameSize)
    {
        if ((flags & (LhdSplitBefore | LhdSplitAfter)) != 0)
            return false;  // 分卷：FILE_CRC 不覆盖整文件
        if ((flags & LhdLarge) != 0)
            return false;  // 高 32 位尺寸：读序不同
        if ((flags & LhdDirMask) == LhdDirectory)
            return false;  // 目录条目（arcread.cpp:267）
        if ((flags & LhdComment) != 0)
            return false;  // 旧式头内注释：两套 HEAD_CRC 口径
        if ((flags & LhdSalt) == 0)
            return false;  // RAR30 加密必然带 salt
        if (IsLegacyCrypt(unpVer))
            return false;  // 旧加密档不走 SetKey30
        if (method != MethodStored)
            return false;  // 压缩条目没有判据
        if (pack == 0 || pack > MaxStoredBytes)
            return false;
        if ((pack & 15) != 0)
            return false;  // 密文必须整块
        if (unp > pack || pack != Align16(unp))
            return false;  // 填充只有 0..15 字节
        if (32 + nameSize + 8 > headerSize)
            return false;
        return true;
    }

    private static Rar4FastPathSession? BuildFileEncrypted(
        FileStream stream, byte[] head, int at, ushort flags, int headerSize)
    {
        var pack = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(at + 7));
        var unp = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(at + 11));
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(at + 16));
        var unpVer = head[at + 24];
        var method = head[at + 25];
        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(at + 26));

        if (!IsJudgeable(flags, unpVer, method, pack, unp, headerSize, nameSize))
        {
            return null;
        }

        // salt 在**头块之内**、紧跟文件名之后（arcread.cpp:341/389），不是块起点加 hsize。
        var saltAt = at + 32 + nameSize;
        if (saltAt + 8 > at + headerSize || (long)at + headerSize + pack > stream.Length)
        {
            return null;
        }

        var salt = head.AsSpan(saltAt, 8).ToArray();
        var cipher = ReadRange(stream, at + headerSize, (int)pack);
        return cipher is null || cipher.Length != pack
            ? null
            : new Rar4FastPathSession(cipher, salt, false, crc, (int)unp);
    }

    /// <summary>「密码肯定不对」返回 <see cref="FastPathVerdict.ProbablyWrong"/>，其余一律「不知道」。</summary>
    public FastPathVerdict Judge(string password)
    {
        if (password.Length == 0 || password.Length > Rar4Kdf.MaxPasswordUnits)
        {
            return FastPathVerdict.Unknown;
        }

        return _headerEncrypted ? JudgeHeaderEncrypted(password) : JudgeFileEncrypted(password);
    }

    private FastPathVerdict JudgeFileEncrypted(string password)
    {
        var (key, init) = Rar4Kdf.SetKey30(password, _salt);
        var plain = RentBuffer(ref _plainBuffer, _cipher.Length);
        Decrypt(_cipher, 0, _cipher.Length, plain, key, init);

        // CRC 只覆盖 UNP_SIZE 字节：加密 stored 条目的密文是 ALIGN(UNP_SIZE,16)，
        // 尾部 0..15 字节是填充（5 字节文件 pack=16）。按 pack 全长算会误杀正确密码。
        return Crc32Of(plain.AsSpan(0, _plainLength)) == _crc
            ? FastPathVerdict.Unknown
            : FastPathVerdict.ProbablyWrong;
    }

    private FastPathVerdict JudgeHeaderEncrypted(string password)
    {
        // _cipher 的坐标：Open 已经把 MAIN_HEAD 之后那 8 字节 salt 剥掉，所以 [0] 起就是
        // 第一个头块的密文；之后每块自己的 salt 在它的密文之前。
        //   _cipher: [块0 密文 ALIGN(hsize0,16)][条目0 数据 pack0][salt 8][块1 密文]…
        var keys = new Dictionary<string, (byte[] Key, byte[] Init)>(StringComparer.Ordinal);
        var salt = _salt;
        var pos = 0L;
        for (var block = 0; block < MaxHeaderBlocks; block++)
        {
            var (key, init) = Derive(password, salt, keys);
            var take = Math.Min((int)((_cipher.Length - pos) & ~15L), HeadProbe);
            if (take < 16)
            {
                return FastPathVerdict.Unknown;
            }

            var probe = RentBuffer(ref _probeBuffer, take);
            Decrypt(_cipher, (int)pos, take, probe, key, init);
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(5));
            if (headerSize < 7 || headerSize > MaxHeaderSize)
            {
                return FastPathVerdict.ProbablyWrong;  // 解出来不是头块：密码不对
            }

            var segLen = Align16(headerSize);
            if (pos + segLen > _cipher.Length)
            {
                return FastPathVerdict.Unknown;
            }

            byte[] plain;
            if (segLen <= take)
            {
                plain = probe;
            }
            else
            {
                plain = RentBuffer(ref _headerBuffer, segLen);
                Decrypt(_cipher, (int)pos, segLen, plain, key, init);
            }

            var type = plain[2];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(3));

            // LHD_COMMENT（RAR 2.9 旧式头内文件注释）：官方对这个标志要过**两道不同口径**的
            // HEAD_CRC —— arcread.cpp:431 只算到 ReadPos，arcread.cpp:514 算满 DataSize。
            // 一个 16 位校验复刻不了「两处都过」，所以这里保守拒判。
            if ((flags & LhdComment) != 0)
            {
                return FastPathVerdict.Unknown;
            }

            // HEAD_CRC 覆盖 HEAD_TYPE 起的 headerSize 字节（rawread.cpp:165，ProcessedOnly=false）。
            if ((Crc32Of(plain.AsSpan(2, headerSize - 2)) & 0xFFFF)
                != BinaryPrimitives.ReadUInt16LittleEndian(plain))
            {
                return FastPathVerdict.ProbablyWrong;
            }

            if (type == HeadEndArc)
            {
                return FastPathVerdict.Unknown;
            }

            long next;
            if (type is HeadFile or HeadService)
            {
                // 字段排到偏移 28；短于它只能回「不知道」——按「密码不对」处置会误杀合法头块。
                if (headerSize < MinFileHeaderSize)
                {
                    return FastPathVerdict.Unknown;
                }

                var pack = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(7));
                var unp = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(11));
                var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(16));
                var unpVer = plain[24];
                var method = plain[25];
                var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(26));

                if (type == HeadFile && (flags & LhdPassword) != 0
                    && IsJudgeable(flags, unpVer, method, pack, unp, headerSize, nameSize))
                {
                    var dataSeg = pos + segLen;
                    if (dataSeg + pack > _cipher.Length)
                    {
                        return FastPathVerdict.Unknown;
                    }

                    // 数据段用它自己的 salt（在头块之内）重新起链 —— 按官方口径（extract.cpp:607
                    // 取 hd->Salt）就该分开算，不依赖两者恰好同值。
                    var fileSalt = plain.AsSpan(32 + nameSize, 8).ToArray();
                    var (fileKey, fileInit) = Derive(password, fileSalt, keys);
                    var data = RentBuffer(ref _plainBuffer, (int)pack);
                    Decrypt(_cipher, (int)dataSeg, (int)pack, data, fileKey, fileInit);
                    return Crc32Of(data.AsSpan(0, (int)unp)) == fileCrc
                        ? FastPathVerdict.Unknown
                        : FastPathVerdict.ProbablyWrong;
                }

                next = pos + segLen + pack;  // SERVICE 或不可判的 FILE：跳过数据区继续走
            }
            else
            {
                next = pos + segLen;
            }

            // 下一块：它自己的 8 字节 salt 在前（arcread.cpp:157、archive.cpp:298）
            if (next + 8 + 16 > _cipher.Length)
            {
                return FastPathVerdict.Unknown;
            }

            salt = _cipher.AsSpan((int)next, 8).ToArray();
            pos = next + 8;
        }

        return FastPathVerdict.Unknown;
    }

    private static (byte[] Key, byte[] Init) Derive(
        string password, byte[] salt, Dictionary<string, (byte[] Key, byte[] Init)> cache)
    {
        var id = Convert.ToHexString(salt);
        if (!cache.TryGetValue(id, out var derived))
        {
            derived = Rar4Kdf.SetKey30(password, salt);
            cache[id] = derived;
        }

        return derived;
    }

    private static int Align16(uint value) => (int)((value + 15) & ~15u);

    /// <summary>标准 CRC32（RAR 的 FILE_CRC 与 HEAD_CRC 都是它）。</summary>
    private static uint Crc32Of(ReadOnlySpan<byte> data) => Crc32.Standard(data);

    /// <summary>AES-128-CBC、无填充、每段各自从 AESInit 起链；密文段就地取，不另拷一份。</summary>
    private static void Decrypt(byte[] cipher, int at, int length, byte[] plain, byte[] key, byte[] init)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = init;
        using var transform = aes.CreateDecryptor();
        transform.TransformBlock(cipher, at, length, plain, 0);
    }

    /// <summary>
    /// 每线程一份的明文缓冲。候选循环是并发的，所以不能共享同一块；而同一线程上连着判几百个候选，
    /// 按候选各分配一次是 8 MB 条目乘 16 路约 128 MB 的瞬时分配，整轮都压在 GC 上 —— 复用它。
    /// 三个槽互不重叠，判定过程中不会互相踩（数据段与头段分别落在两个槽里）。
    /// </summary>
    [ThreadStatic]
    private static byte[]? _plainBuffer;

    [ThreadStatic]
    private static byte[]? _probeBuffer;

    [ThreadStatic]
    private static byte[]? _headerBuffer;

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

    private static byte[]? ReadRange(FileStream stream, long at, int count)
    {
        if (at < 0 || at >= stream.Length)
        {
            return null;
        }

        var take = (int)Math.Min(count, stream.Length - at);
        var buffer = new byte[take];
        stream.Position = at;
        var read = 0;
        while (read < take)
        {
            var got = stream.Read(buffer, read, take - read);
            if (got <= 0)
            {
                return null;
            }

            read += got;
        }

        return buffer;
    }

    public void Dispose()
    {
        // 会话不持非托管资源：密文与 salt 都是托管数组。
    }
}
