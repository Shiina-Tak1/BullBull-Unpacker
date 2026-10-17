using System;
using Bbu.Core.Texts;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 把一个 7z 包读成 <see cref="SevenZipLayout"/>。
///
/// 两句话概括 7z 的布局：
/// <code>
/// [32 字节签名头][packed data][next header]
/// next header 首字节
///   0x01 kHeader        → 明文头，直接解析
///   0x17 kEncodedHeader → 它自己也是一段（压缩 +）加密的数据，要先解出来才知道主流在哪
/// </code>
/// 判据只关心：folder 的 coder 链里有没有 7zAES、salt/IV 是什么、密文在哪、解出来的期望 CRC 是多少。
/// </summary>
internal static class SevenZipLayoutReader
{
    /// <summary>next header 的上限。大过头说明不是本实现要判的那种包，放弃。</summary>
    private const long MaxNextHeaderSize = 64L * 1024 * 1024;

    public static SevenZipLayout Read(ISevenZipSource source, long archiveOffset = 0)
    {
        Span<byte> signature = stackalloc byte[SevenZipHeaderReader.SignatureSize];
        if (source.ReadAt(archiveOffset, signature) < signature.Length
            || !SevenZipHeaderReader.HasMagic(signature))
        {
            throw new SevenZipFormatException(ManagedFastPathText.NotSevenZip);
        }

        var start = SevenZipHeaderReader.ReadStartHeader(signature, archiveOffset + SevenZipHeaderReader.SignatureSize);
        if (start.Major != 0)
        {
            throw new SevenZipFormatException(ManagedFastPathText.MajorVersionUnknown(start.Major));
        }

        if (start.NextHeaderSize == 0)
        {
            // 纯签名 + 空头：这个包没有条目，判据用不上。
            return new SevenZipLayout
            {
                DataBase = start.DataBase,
                NextHeader = [],
                NextHeaderCrc = start.NextHeaderCrc,
                EncodedHeader = null,
                MainStreams = null,
            };
        }

        if (start.NextHeaderSize > (ulong)MaxNextHeaderSize
            || start.NextHeaderOffset > long.MaxValue - (ulong)start.DataBase)
        {
            throw new SevenZipFormatException(ManagedFastPathText.NextHeaderRangeInvalid);
        }

        var nextHeader = new byte[start.NextHeaderSize];
        if (source.ReadAt(start.DataBase + (long)start.NextHeaderOffset, nextHeader) != nextHeader.Length)
        {
            throw new SevenZipFormatException(ManagedFastPathText.NextHeaderTruncated);
        }

        // 与密码无关的完整性判据：头字节自己的 CRC。对 -mhe=on 这份 CRC 是对**密文**算的，
        // 所以它能证明包没坏，但证明不了密码对。
        if (Crc32.Standard(nextHeader) != start.NextHeaderCrc)
        {
            throw new SevenZipFormatException(ManagedFastPathText.NextHeaderCrcMismatch);
        }

        var cursor = new SevenZipCursor(nextHeader);
        var first = cursor.ReadId();

        SevenZipStreams? encoded = null;
        SevenZipStreams? mainStreams = null;

        if (first == SevenZipId.Header)
        {
            mainStreams = SevenZipHeaderReader.ReadMainStreams(nextHeader.AsSpan(cursor.Position));
        }
        else if (first == SevenZipId.EncodedHeader)
        {
            encoded = SevenZipHeaderReader.ReadStreamsInfo(ref cursor);
        }
        else
        {
            throw new SevenZipFormatException(ManagedFastPathText.NextHeaderKindUnknown(first));
        }

        return new SevenZipLayout
        {
            DataBase = start.DataBase,
            NextHeader = nextHeader,
            NextHeaderCrc = start.NextHeaderCrc,
            EncodedHeader = encoded,
            MainStreams = mainStreams,
        };
    }
}
