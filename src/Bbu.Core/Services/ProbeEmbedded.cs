using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Bbu.Core.Models;

namespace Bbu.Core.Services;

/// <summary>
/// 识别层的伪装与内嵌探测：垫片包、尾部追加包、绝对偏移包。
/// </summary>
public static partial class Probe
{
    private static readonly byte[] ZipLocalMagic = "PK\x03\x04"u8.ToArray();

    private static readonly byte[] ZipEocdMagic = "PK\x05\x06"u8.ToArray();

    private static readonly byte[] ZipDataMagic = "PK\x07\x08"u8.ToArray();

    private static readonly byte[] PkCentralMagic = "PK\x01\x02"u8.ToArray();

    private static readonly byte[] PkZip64Eocd = "PK\x06\x06"u8.ToArray();

    private static readonly byte[] PkZip64Locator = "PK\x06\x07"u8.ToArray();

    private static readonly byte[] Rar4Magic = "Rar!\x1a\x07\x00"u8.ToArray();

    private static readonly byte[] Rar5Magic = "Rar!\x1a\x07\x01\x00"u8.ToArray();

    /// <summary>RAR5 头长度字段的上限。两处解析器共用这一处，改一处就够。</summary>
    private const int Rar5MaxHeadBytes = 1 << 20;

    // 下面三个签名里有大于 0x7F 的字节，只能逐个字节写：
    // `u8` 字面量是"这个字符串的 UTF-8 编码"，`"\xbc"u8` 会编成两个字节（U+00BC 的 UTF-8），
    // 于是签名永远匹配不上。2026-10-01 由逐格式断言逮到（7z / gzip / zstd 三个都判不出来）。
    private static readonly byte[] SevenZMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    private static readonly byte[] GzipMagic = [0x1F, 0x8B];

    private static readonly byte[] Lz4Magic = "\x04\x22\x4d\x18"u8.ToArray();

    private static readonly byte[] Lz5Magic = "\x05\x22\x4d\x18"u8.ToArray();

    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    private static readonly byte[] TarMagic = "ustar"u8.ToArray();

    private static readonly byte[] FtypMagic = "ftyp"u8.ToArray();

    /// <summary>签名到校验器的唯一一张表，两个窗口共用；只认头部 CRC 可校验的 RAR5 与 7z。</summary>
    private static readonly (byte[] Magic, ArchiveFormat Format, Func<ReadOnlySpan<byte>, bool> Check)[] MagicTable =
    [
        (Rar5Magic, ArchiveFormat.Rar5, Rar5HeaderOk),
        (SevenZMagic, ArchiveFormat.SevenZip, SevenZHeaderOk),
    ];

    /// <summary>值不值得为它做内嵌探测：扩展名像马甲，且体积够大。</summary>
    public static bool LooksLikeCarrier(string path)
    {
        try
        {
            if (new FileInfo(path).Length < EmbedMinSize)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        var ext = ExtOf(path);
        return ext.Length == 0 || CarrierExts.Contains(ext);
    }

    /// <summary>这个文件里是不是藏着一个压缩包，返回它从哪开始；文件头本身就是包时返回 <c>null</c>。</summary>
    public static Embedded? FindEmbedded(string path, bool deep = true, ProbeOptions? options = null)
    {
        if (DetectFormat(path).IsArchive())
        {
            return null;
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (size < EmbedMinSize)
        {
            return null;
        }

        var baseOffset = ZipBaseInTail(path, size, options);
        if (baseOffset is not null)
        {
            return new Embedded(ArchiveFormat.Zip, baseOffset.Value, Carrier.TailDirectory);
        }

        if (!deep)
        {
            return null;
        }

        var hit = ScanMagic(path, options ?? ProbeOptions.Default);
        return hit is null ? null : new Embedded(hit.Value.Format, hit.Value.Offset, Carrier.DeepScan);
    }

    /// <summary>
    /// 尾部有<b>自洽的</b> ZIP 中央目录，但起点推不出来，也就是包内偏移是绝对的那种布局。
    /// 这类文件不该切包，也不该为它全盘扫：切掉假头会让包内偏移整体错位，扫了必然是空。
    /// 只读尾部 4MB，毫秒级 —— 前提是候选条数有上界，理由见 <see cref="EocdHitLimit"/>。
    /// </summary>
    public static bool HasZipAbsoluteLayout(string path, ProbeOptions? options = null)
    {
        var opts = options ?? ProbeOptions.Default;
        if (DetectFormat(path).IsArchive())
        {
            return false;
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
            if (size < EmbedMinSize)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        var start = Math.Max(0, size - EmbedTailBytes);
        FileStream? stream = null;
        byte[] tail;
        try
        {
            stream = File.OpenRead(path);
            tail = ReadTail(stream, start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stream?.Dispose();
            return false;
        }

        try
        {
            var pos = tail.Length;
            var hits = 0;
            while (true)
            {
                if (opts.Cancellation.IsCancellationRequested)
                {
                    return false;
                }

                var i = LastIndexOf(tail, ZipEocdMagic, 0, pos);
                if (i < 0)
                {
                    return false;
                }

                pos = i;
                if (++hits > EocdHitLimit)
                {
                    return false;
                }

                opts.Counters?.CountEocdHit();
                if (i + 22 > tail.Length)
                {
                    continue;
                }

                var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12, 4));
                var cdOff = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16, 4));
                if (cdOff == 0xFFFFFFFFu)
                {
                    if (Zip64AbsoluteLayout(stream, tail, start, i, size))
                    {
                        return true;
                    }

                    continue;
                }

                if (cdSize is 0 or 0xFFFFFFFFu)
                {
                    continue;
                }

                var cdPos = start + i - cdSize;
                if (cdPos <= 0 || !ReadAt(stream, cdPos, 4).AsSpan().SequenceEqual(PkCentralMagic))
                {
                    continue;
                }

                var baseOffset = cdPos - cdOff;
                if (baseOffset > 0 && baseOffset < size)
                {
                    return !IsZipMagic(ReadAt(stream, baseOffset, 4));
                }

                return true;
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>尾部窗口读进来。起点由调用方算好。</summary>
    private static byte[] ReadTail(FileStream stream, long start)
    {
        stream.Seek(start, SeekOrigin.Begin);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// 只在尾部 4MB 窗口里找 RAR5 与 7z 的明文签名加头部 CRC。
    /// 它是深扫前面的廉价筛子：不命中<b>不能</b>断定里面没有包，包可能在中段。
    /// </summary>
    public static Embedded? TailMagic(string path, ProbeOptions? options = null)
    {
        var opts = options ?? ProbeOptions.Default;
        if (DetectFormat(path).IsArchive())
        {
            return null;
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (size < EmbedMinSize || opts.Cancellation.IsCancellationRequested)
        {
            return null;
        }

        var start = Math.Max(0, size - EmbedTailBytes);
        byte[] data;
        try
        {
            using var stream = File.OpenRead(path);
            stream.Seek(start, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        opts.Counters?.CountTailProbe();
        var hit = MagicIn(data, start, path);
        return hit is null ? null : new Embedded(hit.Value.Format, hit.Value.Offset, Carrier.TailMagic);
    }

    /// <summary>
    /// 马甲文件的<b>唯一入口</b>：是不是马甲、里面有没有包、值不值得处理。
    /// 判据按代价从低到高排，命中即返回。
    /// </summary>
    public static CarrierProbe InspectCarrier(string path, ProbeOptions? options = null)
    {
        var opts = options ?? ProbeOptions.Default;
        if (!LooksLikeCarrier(path))
        {
            return CarrierProbe.NotCarrier;
        }

        var embedded = FindEmbedded(path, deep: false, opts);
        if (embedded is not null)
        {
            return new CarrierProbe(embedded.How, embedded, true);
        }

        if (HasZipAbsoluteLayout(path, opts))
        {
            return new CarrierProbe(Carrier.AbsoluteLayout, null, true);
        }

        if (opts.EngineProbe is not null)
        {
            opts.Counters?.CountEngineProbe();
            if (opts.EngineProbe(path))
            {
                return new CarrierProbe(Carrier.EngineDirect, null, true);
            }
        }

        embedded = TailMagic(path, opts);
        if (embedded is not null)
        {
            return new CarrierProbe(embedded.How, embedded, true);
        }

        // 第二个引擎那一问必须排在尾部签名之后：排在前面会把「垫片加一整个 RAR5」
        // 从切包改成直读，切出来那一份才吃得到快路。它是兜底，目前没有观测到它改变结论。
        if (opts.FallbackEngineProbe is not null)
        {
            opts.Counters?.CountEngineProbe();
            if (opts.FallbackEngineProbe(path))
            {
                return new CarrierProbe(Carrier.EngineDirect, null, true);
            }
        }

        if (opts.Deep)
        {
            embedded = FindEmbedded(path, deep: true, opts);
            if (embedded is not null)
            {
                return new CarrierProbe(embedded.How, embedded, true);
            }
        }

        return new CarrierProbe(Carrier.None, null, true);
    }

    private static bool Zip64AbsoluteLayout(FileStream stream, byte[] tail, long start, int eocdIndex, long size)
    {
        var loc = LastIndexOf(tail, PkZip64Locator, Math.Max(0, eocdIndex - 128), eocdIndex);
        var z64 = loc < 0 ? -1 : LastIndexOf(tail, PkZip64Eocd, 0, loc);
        if (z64 < 0 || z64 + 56 > tail.Length || loc + 20 > tail.Length)
        {
            return false;
        }

        var cdOff = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(z64 + 48, 8));
        if (!ReadAt(stream, cdOff, 4).AsSpan().SequenceEqual(PkCentralMagic))
        {
            return false;
        }

        var z64Off = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8, 8));
        var baseOffset = start + z64 - z64Off;
        return baseOffset <= 0 || baseOffset >= size;
    }

    private static long? ZipBaseInTail(string path, long size, ProbeOptions? options = null)
    {
        var opts = options ?? ProbeOptions.Default;
        var start = Math.Max(0, size - EmbedTailBytes);
        FileStream? stream = null;
        byte[] tail;
        try
        {
            stream = File.OpenRead(path);
            tail = ReadTail(stream, start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stream?.Dispose();
            return null;
        }

        try
        {
            var pos = tail.Length;
            var hits = 0;
            while (true)
            {
                if (opts.Cancellation.IsCancellationRequested)
                {
                    return null;
                }

                var i = LastIndexOf(tail, ZipEocdMagic, 0, pos);
                if (i < 0)
                {
                    return null;
                }

                pos = i;
                if (++hits > EocdHitLimit)
                {
                    return null;
                }

                opts.Counters?.CountEocdHit();
                if (i + 22 > tail.Length)
                {
                    continue;
                }

                var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12, 4));
                var cdOff = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16, 4));
                var absEocd = start + i;

                long? baseOffset = null;
                if (cdSize is not (0 or 0xFFFFFFFFu) && cdOff is not (0 or 0xFFFFFFFFu))
                {
                    var cdPos = absEocd - cdSize;
                    if (cdPos > 0 && ReadAt(stream, cdPos, 4).AsSpan().SequenceEqual(PkCentralMagic))
                    {
                        var candidate = cdPos - cdOff;
                        if (candidate > 0 && candidate < size)
                        {
                            baseOffset = candidate;
                        }
                    }
                }

                baseOffset ??= Zip64Base(stream, tail, start, i, size);
                if (baseOffset is null)
                {
                    continue;
                }

                if (IsZipMagic(ReadAt(stream, baseOffset.Value, 4)))
                {
                    return baseOffset;
                }
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static long? Zip64Base(FileStream stream, byte[] tail, long start, int eocdIndex, long size)
    {
        var loc = LastIndexOf(tail, PkZip64Locator, Math.Max(0, eocdIndex - 128), eocdIndex);
        if (loc < 0 || loc + 20 > tail.Length)
        {
            return null;
        }

        var z64Off = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8, 8));
        var z64 = LastIndexOf(tail, PkZip64Eocd, 0, loc);
        if (z64 < 0 || z64 + 56 > tail.Length)
        {
            return null;
        }

        var cdOff = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(z64 + 48, 8));
        var baseOffset = start + z64 - z64Off;
        if (baseOffset <= 0 || baseOffset >= size)
        {
            return null;
        }

        return ReadAt(stream, baseOffset + cdOff, 4).AsSpan().SequenceEqual(PkCentralMagic) ? baseOffset : null;
    }

    /// <summary>全盘扫 RAR5 与 7z 签名。代价是包离文件头的距离，命中即返回。</summary>
    private static (ArchiveFormat Format, long Offset)? ScanMagic(string path, ProbeOptions options)
    {
        var longest = 0;
        foreach (var entry in MagicTable)
        {
            longest = Math.Max(longest, entry.Magic.Length);
        }

        options.Counters?.CountDeepScan();
        // 固定一块窗口只分配这一次；块头留给上一轮的尾巴，签名可能跨在两次读之间。
        var carry = 0;
        long offset = 0;
        try
        {
            using var stream = File.OpenRead(path);
            var window = new byte[EmbedChunkBytes + longest];
            while (true)
            {
                if (options.Cancellation.IsCancellationRequested)
                {
                    return null;
                }

                var read = stream.Read(window, carry, window.Length - carry);
                if (read <= 0)
                {
                    return null;
                }

                var data = window.AsSpan(0, carry + read);
                var hit = MagicIn(data, offset - carry, path);
                if (hit is not null)
                {
                    return hit;
                }

                carry = Math.Min(longest, data.Length);
                data[^carry..].CopyTo(window);
                offset += read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (ArchiveFormat Format, long Offset)? MagicIn(ReadOnlySpan<byte> data, long baseOffset, string path)
    {
        foreach (var (magic, format, check) in MagicTable)
        {
            var at = data.IndexOf(magic);
            while (at >= 0)
            {
                var absolute = baseOffset + at;
                if (absolute > 0)
                {
                    var head = data.Slice(at, Math.Min(64, data.Length - at)).ToArray();
                    if (head.Length < 64)
                    {
                        head = ReadAt(path, absolute, 64);
                    }

                    if (check(head))
                    {
                        return (format, absolute);
                    }
                }

                var next = data[(at + 1)..].IndexOf(magic);
                at = next < 0 ? -1 : at + 1 + next;
            }
        }

        return null;
    }

    /// <summary>RAR5 主档头校验：HEAD_CRC 覆盖 HEAD_SIZE 字段本身加它声明的长度。</summary>
    private static bool Rar5HeaderOk(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 16 || !buffer.StartsWith(Rar5Magic))
        {
            return false;
        }

        var want = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        var (size, after) = Rar5Vint(buffer, 12);
        if (size <= 0 || size > Rar5MaxHeadBytes || after + size > buffer.Length)
        {
            return false;
        }

        return Crc32Of(buffer.Slice(12, after + size - 12)) == want;
    }

    /// <summary>7z 起始头校验：Start Header CRC32 覆盖第 12 至 32 字节。</summary>
    private static bool SevenZHeaderOk(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 32 || !buffer.StartsWith(SevenZMagic))
        {
            return false;
        }

        return Crc32Of(buffer.Slice(12, 20)) == BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
    }

    /// <summary>
    /// RAR5 头部的可变长整数：低 7 位在**第一个**字节，最高位表示还有后续。
    /// 最多 5 字节，再长的续位一律回 <c>-1</c>；因此任意输入都返回一个值，不抛也不溢出。
    ///
    /// 字节序拿真包验过：233 字节的头按大端读出来 CRC 对不上，按小端对得上（RAR 7.23，`-v2k`）。
    /// </summary>
    private static (int Value, int Next) Rar5Vint(ReadOnlySpan<byte> buffer, int at)
    {
        var value = 0;
        var shift = 0;
        while (at < buffer.Length)
        {
            var b = buffer[at];
            at++;
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (value, at);
            }

            shift += 7;
            if (shift > 28)
            {
                return (-1, at);
            }
        }

        return (-1, at);
    }

    internal static byte[] ReadAt(string path, long offset, int count)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadAt(stream, offset, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    internal static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        try
        {
            if (offset < 0 || offset >= stream.Length)
            {
                return [];
            }

            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            return read == count ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool IsZipMagic(ReadOnlySpan<byte> data) =>
        data.StartsWith(ZipLocalMagic) || data.StartsWith(ZipEocdMagic) || data.StartsWith(ZipDataMagic);

    private static int LastIndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        if (start < 0)
        {
            start = 0;
        }

        if (end > haystack.Length)
        {
            end = haystack.Length;
        }

        if (start >= end)
        {
            return -1;
        }

        var at = haystack.AsSpan(start, end - start).LastIndexOf(needle);
        return at < 0 ? -1 : start + at;
    }

    /// <summary>CRC-32/ISO-HDLC（初值/终值都取反）；表与算法取 <see cref="Crc32"/> 那一份，本类不再自建。</summary>
    private static uint Crc32Of(ReadOnlySpan<byte> data) => Crc32.Compute(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}
