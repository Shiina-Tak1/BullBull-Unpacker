using System;
using System.Collections.Generic;

namespace Bbu.Core.Services.Managed7z;

/// <summary>7z 头解析失败。判据把它当成「这个包判不了」，绝不当成「密码错」。</summary>
internal sealed class SevenZipFormatException : Exception
{
    public SevenZipFormatException(string message) : base(message)
    {
    }
}

/// <summary>7z 头里的属性 ID（<c>7zHeader.h</c> 的 <c>NID::EEnum</c>）。</summary>
internal static class SevenZipId
{
    public const ulong End = 0x00;
    public const ulong Header = 0x01;
    public const ulong ArchiveProperties = 0x02;
    public const ulong AdditionalStreamsInfo = 0x03;
    public const ulong MainStreamsInfo = 0x04;
    public const ulong FilesInfo = 0x05;
    public const ulong PackInfo = 0x06;
    public const ulong UnpackInfo = 0x07;
    public const ulong SubStreamsInfo = 0x08;
    public const ulong Size = 0x09;
    public const ulong Crc = 0x0A;
    public const ulong Folder = 0x0B;
    public const ulong CodersUnpackSize = 0x0C;
    public const ulong NumUnpackStream = 0x0D;
    public const ulong EncodedHeader = 0x17;
}

/// <summary>coder 的 method id（<c>7zHeader.h</c> 的 <c>k_*</c>）。</summary>
internal static class SevenZipMethod
{
    public const ulong Copy = 0x00;
    public const ulong Lzma2 = 0x21;
    public const ulong Lzma = 0x30101;
    public const ulong Aes = 0x6F10701;
}

/// <summary>一个 folder 里的一个 coder：method id、它自己的 properties、入口流个数。</summary>
internal sealed class SevenZipCoder
{
    public SevenZipCoder(ulong id, byte[] properties, int inStreams)
    {
        Id = id;
        Properties = properties;
        InStreams = inStreams;
    }

    public ulong Id { get; }

    /// <summary>原样存下来的 coder properties（7zAES 的 salt/IV/NumCyclesPower 就在这里面）。</summary>
    public byte[] Properties { get; }

    public int InStreams { get; }

    public override string ToString() => $"0x{Id:X}";
}

/// <summary>
/// 一个 folder：一串 coder（<b>按解码顺序</b>，<c>Coders[0]</c> 离 packed data 最近）加它们各自的输出大小。
/// </summary>
internal sealed class SevenZipFolder
{
    public List<SevenZipCoder> Coders { get; } = [];

    /// <summary>每个 coder 的输出大小，与 <see cref="Coders"/> 同序；主输出那一格由 <see cref="MainStreamIndex"/> 指。</summary>
    public List<ulong> CoderUnpackSizes { get; } = [];

    /// <summary>主输出流（真正的解压结果）在第几个 coder 上。</summary>
    public int MainStreamIndex { get; set; }

    /// <summary>这个 folder 的第一个 pack stream 在 <see cref="SevenZipStreams.PackSizes"/> 里的下标。</summary>
    public int FirstPackStream { get; set; }

    public int PackStreamCount { get; set; }

    /// <summary>folder 解压结果的 CRC32。头里没写就是 <c>null</c>。</summary>
    public uint? UnpackedCrc { get; set; }

    /// <summary>folder 解压结果的长度。</summary>
    public ulong UnpackedSize =>
        MainStreamIndex >= 0 && MainStreamIndex < CoderUnpackSizes.Count ? CoderUnpackSizes[MainStreamIndex] : 0;

    /// <summary>这个 folder 的第一个 coder（解码第一步）。</summary>
    public SevenZipCoder? First => Coders.Count == 0 ? null : Coders[0];

    public override string ToString() => string.Join(" + ", Coders);
}

/// <summary>
/// 一段 StreamsInfo：packed 数据相对某个基准的偏移、每个 pack stream 的大小、以及 folder 表。
/// </summary>
internal sealed class SevenZipStreams
{
    /// <summary>packed 数据的起点，相对「这段头所在的流的基准」。文件头那一段是相对 32 字节之后。</summary>
    public ulong DataOffset { get; set; }

    public List<ulong> PackSizes { get; } = [];

    public List<uint?> PackCrcs { get; } = [];

    public List<SevenZipFolder> Folders { get; } = [];

    /// <summary>第 <paramref name="index"/> 个 pack stream 在这段数据里的起点。</summary>
    public ulong PackStreamOffset(int index)
    {
        var offset = DataOffset;
        for (var i = 0; i < index && i < PackSizes.Count; i++)
        {
            offset += PackSizes[i];
        }

        return offset;
    }

    /// <summary>一个 folder 的 packed 字节起点（相对基准）与长度。</summary>
    public (ulong Offset, ulong Size) PackedRange(SevenZipFolder folder)
    {
        var offset = PackStreamOffset(folder.FirstPackStream);
        ulong size = 0;
        for (var i = 0; i < folder.PackStreamCount; i++)
        {
            var index = folder.FirstPackStream + i;
            if (index < PackSizes.Count)
            {
                size += PackSizes[index];
            }
        }

        return (offset, size);
    }
}

/// <summary>7z 的 32 字节签名头。</summary>
internal sealed class SevenZipStartHeader
{
    public required byte Major { get; init; }

    public required byte Minor { get; init; }

    public required uint StartHeaderCrc { get; init; }

    public required ulong NextHeaderOffset { get; init; }

    public required ulong NextHeaderSize { get; init; }

    public required uint NextHeaderCrc { get; init; }

    /// <summary>签名头之后的数据基准偏移（正常是 32；被嵌在别的容器里时更大）。</summary>
    public required long DataBase { get; init; }
}

/// <summary>
/// 一个 7z 包解析到判据够用的程度：头是明文还是要先解、主流在哪、每个 folder 的期望 CRC 是多少。
/// </summary>
internal sealed class SevenZipLayout
{
    /// <summary>签名头之后的数据基准偏移。</summary>
    public required long DataBase { get; init; }

    /// <summary>next header 的原始字节（-mhe=on 时是密文）。</summary>
    public required byte[] NextHeader { get; init; }

    public required uint NextHeaderCrc { get; init; }

    /// <summary>next header 首字节就是 <c>kEncodedHeader</c>：它自己先要解（-mhe=on / -mhc=on）。</summary>
    public required SevenZipStreams? EncodedHeader { get; init; }

    /// <summary>
    /// 明文头里的主流。加密头要等 key 算出来了才知道，所以判据会回填它。
    /// </summary>
    public SevenZipStreams? MainStreams { get; set; }

    /// <summary>解出来的明文头字节（-mhe=on 时由判据填）。</summary>
    public byte[]? DecodedHeader { get; set; }
}
