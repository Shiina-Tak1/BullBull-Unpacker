using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Bbu.Core.Models;

namespace Bbu.Core.Services;

/// <summary>
/// RAR5：档案级加密头（`-hp`）与文件级加密记录（`-p`）。判据是头里的 8 字节 PswCheck，
/// 它等于 `<c>PBKDF2-HMAC-SHA256(密码UTF8, salt, (1&lt;&lt;KDFCount)+32, 32)</c>` 按 8 字节异或折叠。
///
/// 头块在 <see cref="Open"/> 里只走一次（校验点与密码无关），<see cref="Judge"/> 只做 PBKDF2 比对。
/// 分卷不必单开一条路：RA每一卷开头都带档案加密头，所以只传入主卷时判据照样成立。
/// </summary>
internal sealed class Rar5FastPathSession : IFastPathSession
{
    /// <summary>RA签名，8 字节。</summary>
    internal static readonly byte[] Magic = [0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00];

    /// <summary>KDF 轮数超过这个就不再进程内验：那会比直接起引擎还慢。</summary>
    private const int MaxKdfLog2 = 18;

    /// <summary>
    /// RA密码上限是 127 个 UTF-16 码元：建包端（`Rar.exe`）与 7-Zip 读包端都会截断到那个长度，
    /// 拿完整密码算必然对不上。判据单位是码元，不是码点也不是字节（B-2026-053）。
    /// </summary>
    private const int MaxPasswordUnits = 127;

    /// <summary>头总在开头，1MB 够。</summary>
    private const int Window = 1 << 20;

    /// <summary>
    /// 头长度字段的合法上界。官方 <c>archive.hpp</c> 的 <c>MAX_HEADER_SIZE_RAR5</c> 是 <c>0x200000</c>，
    /// 这里对齐它。当前窗口（<see cref="Window"/> 1MB）下它不会触发，留着是给将来放宽窗口时兜底。
    /// </summary>
    private const int MaxHeaderSize = 0x200000;

    private readonly List<CheckPoint> _points;

    private Rar5FastPathSession(List<CheckPoint> points) => _points = points;

    /// <summary>读一次头块。一个校验点都收不到就返回 <c>null</c>（整轮不进快路）。</summary>
    internal static Rar5FastPathSession? Open(FileStream stream)
    {
        stream.Position = 0;
        var data = new byte[Math.Min(Window, stream.Length)];
        var read = 0;
        while (read < data.Length)
        {
            var got = stream.Read(data, read, data.Length - read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        var points = CheckPoints(data, read);
        return points.Count == 0 ? null : new Rar5FastPathSession(points);
    }

    public FastPathVerdict Judge(string password)
    {
        if (password.Length == 0)
        {
            // 空密码不进快路：它由候选循环直接试一次，语义上不是「快路判掉的」。
            return FastPathVerdict.Unknown;
        }

        if (Utf16UnitCount(password) > MaxPasswordUnits)
        {
            // 超过上限时把判定交回引擎。这里不许自己截断：按码点截对含代理对的密码不等价。
            return FastPathVerdict.Unknown;
        }

        if (HasUnrepresentableUnit(password))
        {
            // 官方把密码转字节时在 U+0000 处截断、孤立代理项编成 3 字节（`unicode.cpp` 与 `crypt5.cpp`），
            // 而 .NET 的 `Encoding.UTF8` 把它编成替换字符。口径不同时算出的校验值必然对不上，
            // 于是**正确密码会被判死** —— 这类候选一律回「不知道」。
            return FastPathVerdict.Unknown;
        }

        foreach (var point in _points)
        {
            if (Matches(point, password))
            {
                return FastPathVerdict.Unknown;
            }
        }

        return FastPathVerdict.ProbablyWrong;
    }

    public void Dispose()
    {
        // 会话不持非托管资源：校验点是托管数组。
    }

    /// <summary>
    /// 官方把密码转成字节时在 U+0000 处截断，且孤立代理项编成 3 字节（`unicode.cpp` 与 `crypt5.cpp`），
    /// 而 .NET 的 `Encoding.UTF8` 会把孤立代理项编成替换字符 —— 口径不同时算出的校验值必然对不上。
    /// </summary>
    private static bool HasUnrepresentableUnit(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var unit = value[i];
            if (unit == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(unit))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 全零的 PswCheck 不能当校验点：RAR 5.21 及更早的 service 记录就写全零（官方 `arcread.cpp`
    /// 对此有明确特例）。拿它去比，任何密码都对不上，正确密码会被判死。
    /// </summary>
    private static bool AllZero(byte[] check)
    {
        foreach (var b in check)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(CheckPoint point, string password)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, point.Salt, (1 << point.KdfCount) + 32, HashAlgorithmName.SHA256, 32);
        for (var i = 0; i < 8; i++)
        {
            if ((derived[i] ^ derived[i + 8] ^ derived[i + 16] ^ derived[i + 24]) != point.Check[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 走一遍头块，收集所有可用的校验点。`hsize` 已经把 extra 区算进去了，
    /// 所以下一块的起点是「头起点加 hsize 加数据长度」，不能再加一遍 extra。
    /// </summary>
    private static List<CheckPoint> CheckPoints(byte[] data, int length)
    {
        var points = new List<CheckPoint>();
        var at = 8;
        while (at + 4 < length)
        {
            var crcAt = at;
            at += 4;
            var headerSize = ReadVint(data, length, ref at);
            var start = at;
            var type = ReadVint(data, length, ref at);
            var flags = ReadVint(data, length, ref at);
            var extra = 0L;
            var dataSize = 0L;
            if ((flags & 0x0001) != 0)
            {
                extra = ReadVint(data, length, ref at);
            }

            if ((flags & 0x0002) != 0)
            {
                dataSize = ReadVint(data, length, ref at);
            }

            if (headerSize <= 0 || headerSize > MaxHeaderSize || start + headerSize > length)
            {
                break;
            }

            // 头块 CRC32 覆盖「`Header size` 字段起到块尾」，初值终值都取反（unrar `rawread.cpp`
            // 的 `GetCRC50`；实测 `rar5-hp.rar` 的 34 字节覆盖区命中）。不符即这块坏了或读偏了：
            // 坏头里的密码校验值不能信（unrar `arcread.cpp` 的「header is damaged」原文），
            // 所以既不收点也不判错。
            if (!HeaderCrcMatches(data, crcAt, start, (int)headerSize))
            {
                break;
            }

            var body = new ArraySegment<byte>(data, at, (int)(start + headerSize - at));
            if (type == 4)
            {
                AddArchiveHeader(body, points);
                break;
            }

            if (type is 2 or 3)
            {
                AddFileHeaders(body, extra, points);
            }

            at = (int)(start + headerSize + dataSize);
            if (type == 5)
            {
                break;
            }
        }

        return points;
    }

    /// <summary>
    /// 头块 CRC32 是否相符：覆盖区从 `Header size` 字段起到块尾。
    /// 不符时调用方放弃这块 —— 头坏了就无法确定下一块的起点。
    /// </summary>
    private static bool HeaderCrcMatches(byte[] data, int crcAt, int start, int size)
    {
        var actual = Crc32.Compute(0xffffffff, data.AsSpan(crcAt + 4, start + size - crcAt - 4)) ^ 0xffffffff;
        return actual == BitConverter.ToUInt32(data, crcAt);
    }

    private static void AddArchiveHeader(ArraySegment<byte> body, List<CheckPoint> points)
    {
        var at = 0;
        var version = ReadVint(body, ref at);
        var flags = ReadVint(body, ref at);
        if (at >= body.Count)
        {
            return;
        }

        var kdf = body[at++];
        var salt = Slice(body, at, 16);
        at += 16;
        if ((flags & 0x0001) == 0 || kdf > MaxKdfLog2 || at + 12 > body.Count)
        {
            return;
        }

        var check = Slice(body, at, 8);
        if (version != 0 || AllZero(check) || !CsumMatches(body, at + 8, check))
        {
            // 版本非 0 表示字段布局可能已经变了（unrar `crypt.hpp` 的 `CRYPT_VERSION` 为 0，
            // `arcread.cpp` 在版本超前时放弃该头）；csum 不符按同上口径放弃。
            return;
        }

        points.Add(new CheckPoint(kdf, salt, check));
    }

    /// <summary>
    /// PswCheck 之后那 4 字节是 <c>SHA256(PswCheck)</c> 的前 4 字节，用来在头块 CRC32 之外
    /// 单独复核校验值本身（unrar `arcread.cpp` 用 `memcmp(csum, Digest, 4)` 定 `UsePswCheck`）。
    /// 缺这几个字节时同样不采信该点。
    /// </summary>
    private static bool CsumMatches(ArraySegment<byte> buffer, int at, byte[] check)
    {
        if (at + 4 > buffer.Count)
        {
            return false;
        }

        var digest = SHA256.HashData(check);
        for (var i = 0; i < 4; i++)
        {
            if (digest[i] != buffer[at + i])
            {
                return false;
            }
        }

        return true;
    }

    private static void AddFileHeaders(ArraySegment<byte> body, long extra, List<CheckPoint> points)
    {
        try
        {
            var at = 0;
            var fileFlags = ReadVint(body, ref at);
            ReadVint(body, ref at);
            ReadVint(body, ref at);
            if ((fileFlags & 0x0002) != 0)
            {
                at += 4;
            }

            if ((fileFlags & 0x0004) != 0)
            {
                at += 4;
            }

            ReadVint(body, ref at);
            ReadVint(body, ref at);
            var nameLength = (int)ReadVint(body, ref at);
            at += nameLength;
            if (at < 0 || at > body.Count)
            {
                return;
            }

            if (extra <= 0 || extra > body.Count)
            {
                return;
            }

            var record = new ArraySegment<byte>(body.Array!, body.Offset + body.Count - (int)extra, (int)extra);
            var cursor = 0;
            while (cursor < record.Count)
            {
                var size = ReadVint(record, ref cursor);
                var recordStart = cursor;
                if (size <= 0)
                {
                    return;
                }

                var recordType = ReadVint(record, ref cursor);
                if (recordType == 1)
                {
                    AddFileEncryption(record, cursor, recordStart, (int)size, points);
                }

                cursor = recordStart + (int)size;
            }
        }
        catch (Exception)
        {
            // 解析失败时放弃该点：单条判不了不等于密码不对。
        }
    }

    private static void AddFileEncryption(
        ArraySegment<byte> record, int at, int recordStart, int size, List<CheckPoint> points)
    {
        var end = Math.Min(recordStart + size, record.Count);
        if (at >= end)
        {
            return;
        }

        try
        {
            var version = ReadVint(record, ref at);
            var flags = ReadVint(record, ref at);
            if (at >= end)
            {
                return;
            }

            var kdf = record[at++];
            var salt = Slice(record, at, 16);
            at += 16 + 16;
            if ((flags & 0x0001) == 0 || kdf > MaxKdfLog2 || at + 12 > end)
            {
                return;
            }

            var check = Slice(record, at, 8);
            if (version != 0 || AllZero(check) || !CsumMatches(record, at + 8, check))
            {
                // 同上：版本超前或 csum 不符时这段记录读不出来，不收点，也不据此判错。
                return;
            }

            points.Add(new CheckPoint(kdf, salt, check));
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    /// <summary>RA可变长整数：低 7 位在第一个字节，最高位表示还有后续字节，小端。</summary>
    private static long ReadVint(ArraySegment<byte> buffer, ref int at)
    {
        long value = 0;
        var shift = 0;
        while (at < buffer.Count)
        {
            var b = buffer[at++];
            value |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        return -1;
    }

    private static long ReadVint(byte[] buffer, int length, ref int at)
    {
        long value = 0;
        var shift = 0;
        while (at < length)
        {
            var b = buffer[at++];
            value |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        return -1;
    }

    private static byte[] Slice(ArraySegment<byte> buffer, int at, int count)
    {
        var take = Math.Min(count, buffer.Count - at);
        var result = new byte[count];
        if (take > 0)
        {
            Array.Copy(buffer.Array!, buffer.Offset + at, result, 0, take);
        }

        return result;
    }

    /// <summary>UTF-16 码元数。代理对算两个；「按码元截断」这条实测量的是 <c>Rar.exe</c>，随包 UnRAR 未复测。</summary>
    private static int Utf16UnitCount(string value) => Encoding.Unicode.GetByteCount(value) / 2;

    private sealed record CheckPoint(int KdfCount, byte[] Salt, byte[] Check);
}
