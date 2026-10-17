using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Models;

namespace Bbu.Core.Services;

/// <summary>
/// ZIP：WinZip AES 与老式 ZipCrypto。
///
/// 元数据在 <see cref="Open"/> 里只解析一次（<see cref="ZipEntry"/> 与密码无关），
/// <see cref="Judge"/> 只做派生的密钥流比对。两条取元数据的路：正常包走中央目录
/// （与 1.1.0 同口径），分卷的数字卷没有可读的中央目录，改从第一个本地头取。
/// 两条路的判据相同，安全线各自独立。
/// </summary>
internal sealed class ZipFastPathSession : IFastPathSession
{
    internal static readonly byte[] LocalHeaderSignature = [0x50, 0x4b, 0x03, 0x04];

    private const int LocalHeaderBytes = 30;

    /// <summary>32 位格里放不下时写的占位符，真值在 ZIP64 结构里。</summary>
    private const uint Zip64Marker = 0xFFFFFFFFu;

    /// <summary>AES 强度到 salt 与密钥长度。</summary>
    private static readonly Dictionary<int, (int Salt, int Key)> AesLengths = new()
    {
        [1] = (8, 16),
        [2] = (12, 24),
        [3] = (16, 32),
    };

    private readonly ZipEntry _entry;

    private ZipFastPathSession(ZipEntry entry) => _entry = entry;

    /// <summary>解析一次元数据。返回 <c>null</c> 表示这个包判不了（整轮不进快路）。</summary>
    internal static ZipFastPathSession? Open(string archive, FileStream stream)
    {
        var entry = FromCentralDirectory(archive) ?? FromFirstLocalHeader(stream);
        return entry is null ? null : new ZipFastPathSession(entry);
    }

    public FastPathVerdict Judge(string password)
    {
        if (password.Length == 0)
        {
            // 空密码不进快路：它由候选循环直接试一次，语义上不是「快路判掉的」。
            return FastPathVerdict.Unknown;
        }

        if (_entry.Kind is ZipEntryKind.Aes)
        {
            if (!IsAscii(password))
            {
                // 规范说 AES 的密码按 UTF-8 转字节，但造包的工具不都遵守：WinZip 的
                // `-ycAES256` 用本地代码页（2026-10-01 用 `zip-cn-aes.zip` 复算 PBKDF2 实测）。
                // 与 ZipCrypto 同一处置 —— 编码假设错了会误杀正确密码。
                return FastPathVerdict.Unknown;
            }

            var derived = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                password, _entry.Salt, 1000,
                System.Security.Cryptography.HashAlgorithmName.SHA1,
                (2 * _entry.KeyLength) + 2);
            return derived[^2..].SequenceEqual(_entry.Verifier)
                ? FastPathVerdict.Unknown
                : FastPathVerdict.ProbablyWrong;
        }

        if (!IsAscii(password))
        {
            // 老式 ZipCrypto 没有规范说密码按哪种编码转字节（历史实现用本地代码页）。
            // 编码假设一旦错了，算出来的密钥流必然对不上，正确密码会被划掉 —— 那是硬要求禁止的。
            return FastPathVerdict.Unknown;
        }

        return ZipCryptoMatches(_entry, password) ? FastPathVerdict.Unknown : FastPathVerdict.ProbablyWrong;
    }

    public void Dispose()
    {
        // 会话不持非托管资源：元数据是托管数组。
    }

    private static bool IsAscii(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 中央目录那条路。取第一个加密条目：只有加密条目的测试结果能证明密码对不对。
    /// 本地头读到的 30 字节必须以本地头签名开头，否则判不了 ——
    /// 中央目录与本地头的基准对不上时，偏移会指到垃圾数据上，解出来的校验值跟谁都不匹配。
    /// </summary>
    private static ZipEntry? FromCentralDirectory(string archive)
    {
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var (centralAt, centralSize) = FindCentralDirectory(stream);
        if (centralAt < 0)
        {
            return null;
        }

        stream.Position = centralAt;
        var central = ReadExactly(stream, (int)Math.Min(centralSize, 1 << 20));
        var at = 0;
        while (at + 46 <= central.Length && central[at] == 0x50 && central[at + 1] == 0x4b
               && central[at + 2] == 0x01 && central[at + 3] == 0x02)
        {
            var flags = BitConverter.ToUInt16(central, at + 8);
            var crc = BitConverter.ToUInt32(central, at + 16);
            var nameLength = BitConverter.ToUInt16(central, at + 28);
            var extraLength = BitConverter.ToUInt16(central, at + 30);
            var commentLength = BitConverter.ToUInt16(central, at + 32);
            var extraAt = at + 46 + nameLength;

            if ((flags & 0x0001) != 0)
            {
                if (!TryAesStrength(central, extraAt, extraLength, out var strength))
                {
                    return null;
                }

                var localAt = LocalHeaderOffset(central, at, extraAt, extraLength);
                if (localAt < 0)
                {
                    return null;
                }

                stream.Position = localAt;
                var local = ReadExactly(stream, LocalHeaderBytes);
                if (local.Length < LocalHeaderBytes || !local.AsSpan(0, 4).SequenceEqual(LocalHeaderSignature))
                {
                    return null;
                }

                // AES 的两个条件缺一不可（APPNOTE §E.4 与 §E.5：method 99 加 0x9901）。
                // 对不上说明这一条取偏了：落 ZipCrypto 会拿 salt 当 12 字节加密头去比
                // 一字节 CRC 高字节，正确密码约 255/256 被判死 —— 所以只回 null。
                if ((BitConverter.ToUInt16(local, 8) == 99) != (strength > 0))
                {
                    return null;
                }

                var nameInLocal = BitConverter.ToUInt16(local, 26);
                var extraInLocal = BitConverter.ToUInt16(local, 28);
                stream.Position = localAt + LocalHeaderBytes + nameInLocal + extraInLocal;
                return BuildEntry(stream, strength, flags, crc, BitConverter.ToUInt16(local, 10));
            }

            at += 46 + nameLength + extraLength + commentLength;
        }

        return null;
    }

    /// <summary>
    /// 分卷数字卷那条路：直接从文件开头的本地头取元数据。
    /// 中央目录在最后一卷，只传入首卷时读不到，但首卷开头的本地头是完整的。
    /// </summary>
    private static ZipEntry? FromFirstLocalHeader(FileStream stream)
    {
        stream.Position = 0;
        var local = ReadExactly(stream, LocalHeaderBytes);
        if (local.Length < LocalHeaderBytes || !local.AsSpan(0, 4).SequenceEqual(LocalHeaderSignature))
        {
            return null;
        }

        var flags = BitConverter.ToUInt16(local, 6);
        if ((flags & 0x0001) == 0)
        {
            return null;
        }

        var nameLength = BitConverter.ToUInt16(local, 26);
        var extraLength = BitConverter.ToUInt16(local, 28);
        var extra = ReadExactly(stream, extraLength, LocalHeaderBytes + nameLength);
        if (!TryAesStrength(extra, 0, extraLength, out var strength)
            || (BitConverter.ToUInt16(local, 8) == 99) != (strength > 0))
        {
            // 同上：扩展区读不出合法强度，或它与 method 对不上，都只回 null。
            return null;
        }

        return BuildEntry(
            stream, strength, flags, BitConverter.ToUInt32(local, 14), BitConverter.ToUInt16(local, 10));
    }

    private static ZipEntry? BuildEntry(
        FileStream stream, int strength, ushort flags, uint crc, ushort dosTime)
    {
        if (strength > 0)
        {
            if (!AesLengths.TryGetValue(strength, out var lengths))
            {
                return null;
            }

            var head = ReadExactly(stream, lengths.Salt + 2);
            if (head.Length < lengths.Salt + 2)
            {
                return null;
            }

            return new ZipEntry
            {
                Kind = ZipEntryKind.Aes,
                Salt = head[..lengths.Salt],
                Verifier = head[lengths.Salt..],
                KeyLength = lengths.Key,
            };
        }

        var crypto = ReadExactly(stream, 12);
        return crypto.Length < 12
            ? null
            : new ZipEntry
            {
                Kind = ZipEntryKind.ZipCrypto,
                CryptoHead = crypto,
                Flags = flags,
                Crc = crc,
                DosTime = dosTime,
            };
    }

    /// <summary>
    /// 扩展字段里找 0x9901 那一条，取出 AES 强度。返回 <c>false</c> 表示「这条字段在、
    /// 但读不出合法强度」（数据区容不下强度格，或强度不在 1 至 3）：那是取偏了，整轮不进快路。
    /// 返回 <c>true</c> 而强度为 0 表示没有这条字段，可以落 ZipCrypto。
    /// </summary>
    private static bool TryAesStrength(byte[] buffer, int at, int length, out int strength)
    {
        strength = 0;
        var end = Math.Min(at + length, buffer.Length);
        while (at + 4 <= end)
        {
            var id = BitConverter.ToUInt16(buffer, at);
            var size = BitConverter.ToUInt16(buffer, at + 2);
            if (id == 0x9901)
            {
                // 强度那一格在字段数据区的第 5 字节（WinZip AES 字段表：ver 2 加 vendor 2 后是它）。
                if (size < 5 || at + 9 > end)
                {
                    return false;
                }

                strength = buffer[at + 8];
                return strength is 1 or 2 or 3;
            }

            at += 4 + size;
        }

        return true;
    }

    /// <summary>
    /// 老式 ZipCrypto：用密钥流解密数据开头 12 字节，最后一字节对得上 CRC 高字节（或 DOS 时间高字节）。
    /// 校验值只有一字节，所以假阳性是 1/256；那是格式自带的，划不掉的候选交给引擎。
    /// </summary>
    private static bool ZipCryptoMatches(ZipEntry entry, string password)
    {
        var keys = new uint[] { 0x12345678, 0x23456789, 0x34567890 };
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(password))
        {
            UpdateKeys(keys, b);
        }

        byte last = 0;
        for (var i = 0; i < entry.CryptoHead.Length; i++)
        {
            var temp = (keys[2] | 2) & 0xffff;
            var key = (byte)(((temp * (temp ^ 1)) >> 8) & 0xff);
            last = (byte)(entry.CryptoHead[i] ^ key);
            UpdateKeys(keys, last);
        }

        var expected = (entry.Flags & 0x0008) != 0
            ? (byte)((entry.DosTime >> 8) & 0xff)
            : (byte)((entry.Crc >> 24) & 0xff);
        return last == expected;
    }

    private static void UpdateKeys(uint[] keys, byte value)
    {
        keys[0] = (keys[0] >> 8) ^ Crc32.Table[(keys[0] ^ value) & 0xff];
        keys[1] = (keys[1] + (keys[0] & 0xff)) & 0xffffffff;
        keys[1] = (keys[1] * 134775813 + 1) & 0xffffffff;
        keys[2] = (keys[2] >> 8) ^ Crc32.Table[(keys[2] ^ (keys[1] >> 24)) & 0xff];
    }

    /// <summary>
    /// 中央目录那一条的本地头偏移。32 位格是 0xFFFFFFFF 时真值在 ZIP64 扩展字段（id 0x0001）里，
    /// 且那 8 字节的位置随「前面哪些格也是占位符」浮动：未压缩大小、压缩大小各占 8 字节。
    /// 取不到就回 <c>-1</c>（整轮不进快路），绝不猜一个偏移去判密码。
    /// </summary>
    private static long LocalHeaderOffset(byte[] central, int at, int extraAt, int extraLength)
    {
        var wide = BitConverter.ToUInt32(central, at + 42);
        if (wide != Zip64Marker)
        {
            return wide;
        }

        var end = Math.Min(extraAt + extraLength, central.Length);
        var p = extraAt;
        while (p + 4 <= end)
        {
            var id = BitConverter.ToUInt16(central, p);
            var size = BitConverter.ToUInt16(central, p + 2);
            if (id == 0x0001)
            {
                var q = p + 4;
                if (BitConverter.ToUInt32(central, at + 20) == Zip64Marker)
                {
                    q += 8;
                }

                if (BitConverter.ToUInt32(central, at + 24) == Zip64Marker)
                {
                    q += 8;
                }

                // 真值同样要以字段**声明长度**与缓冲剩余长度两条界为限（同 TryAesStrength）。
                var fieldEnd = Math.Min(p + 4 + size, end);
                return q + 8 <= fieldEnd ? (long)BitConverter.ToUInt64(central, q) : -1;
            }

            p += 4 + size;
        }

        return -1;
    }

    private static (long At, long Size) FindCentralDirectory(FileStream stream)
    {
        var length = stream.Length;
        var window = (int)Math.Min(length, 66000);
        stream.Position = length - window;
        var tail = ReadExactly(stream, window);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (tail[i] != 0x50 || tail[i + 1] != 0x4b || tail[i + 2] != 0x05 || tail[i + 3] != 0x06)
            {
                continue;
            }

            var size = BitConverter.ToUInt32(tail, i + 12);
            var at = BitConverter.ToUInt32(tail, i + 16);
            if (at != Zip64Marker && size != Zip64Marker)
            {
                return (at, size);
            }

            // ZIP64：经典 EOCD 那两格是占位符，真值在紧挨它前面的 ZIP64 EOCD 里。
            // 解不出来就直接回「判不了」，**不许**再往前找别的 EOCD —— 那一个是文件里
            // 别的包（嵌套或追加）的，拿它的条目判密码会误杀这个包的正确密码。
            return Zip64CentralDirectory(stream, tail, i, out var zip64) ? zip64 : (-1, 0);
        }

        return (-1, 0);
    }

    /// <summary>
    /// ZIP64 的中央目录位置：经典 EOCD 前面 20 字节是 locator，locator 指向 ZIP64 EOCD，
    /// 那里才有 8 字节的中央目录偏移与大小。
    /// </summary>
    /// <remarks>
    /// 快路看到的文件永远是「包起点 = 0」—— 切出来的包偏移已归零，直读的那份本来就是绝对偏移，
    /// 所以 locator 里的位置按文件绝对位置读，不必像穿透层那样先算基准。
    /// </remarks>
    private static bool Zip64CentralDirectory(FileStream stream, byte[] tail, int eocd, out (long At, long Size) found)
    {
        found = (-1, 0);
        var locator = eocd - 20;
        if (locator < 0
            || tail[locator] != 0x50 || tail[locator + 1] != 0x4b
            || tail[locator + 2] != 0x06 || tail[locator + 3] != 0x07)
        {
            return false;
        }

        var recordAt = (long)BitConverter.ToUInt64(tail, locator + 8);
        if (recordAt < 0 || recordAt + 56 > stream.Length)
        {
            return false;
        }

        stream.Position = recordAt;
        var record = ReadExactly(stream, 56);
        if (record.Length < 56
            || record[0] != 0x50 || record[1] != 0x4b || record[2] != 0x06 || record[3] != 0x06)
        {
            return false;
        }

        found = ((long)BitConverter.ToUInt64(record, 48), (long)BitConverter.ToUInt64(record, 40));
        return found.Size > 0;
    }

    private static byte[] ReadExactly(FileStream stream, int count, long? at = null)
    {
        if (at is { } position)
        {
            stream.Position = position;
        }

        var buffer = new byte[count];
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }

    private enum ZipEntryKind
    {
        Aes,
        ZipCrypto,
    }

    private sealed record ZipEntry
    {
        public required ZipEntryKind Kind { get; init; }

        public byte[] Salt { get; init; } = [];
        public byte[] Verifier { get; init; } = [];
        public int KeyLength { get; init; }
        public byte[] CryptoHead { get; init; } = [];
        public ushort Flags { get; init; }
        public uint Crc { get; init; }
        public ushort DosTime { get; init; }
    }
}
