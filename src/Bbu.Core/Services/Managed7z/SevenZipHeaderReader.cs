using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Bbu.Core.Texts;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 7z 头解析。照 <c>7zIn.cpp</c> 的 <c>ReadStreamsInfo</c> / <c>ReadPackInfo</c> /
/// <c>ReadUnpackInfo</c> / <c>ReadSubStreamsInfo</c> 逐条搬过来，只保留快路判据要的那几样：
/// packed 数据在哪、folder 的 coder 链是什么、解压结果的期望 CRC 是多少。
///
/// 解析不出来一律抛 <see cref="SevenZipFormatException"/> —— 判据把它当「这个包判不了」，
/// 绝不当「密码错」。
/// </summary>
internal static class SevenZipHeaderReader
{
    /// <summary>签名头长度（6 签名 + 2 版本 + 4 startHeaderCRC + 20 startHeader）。</summary>
    public const int SignatureSize = 32;

    /// <summary>7z 的 6 字节魔数。</summary>
    public static ReadOnlySpan<byte> Magic => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>一个头里所有 folder 的 coder 累加量上限：单看 folder 数或单 folder 的 coder 数都挡不住乘积。</summary>
    internal const int MaxTotalCoders = 1 << 22;

    public static bool HasMagic(ReadOnlySpan<byte> head) =>
        head.Length >= Magic.Length && head[..Magic.Length].SequenceEqual(Magic);

    /// <summary>
    /// 读 32 字节签名头。<paramref name="dataBase"/> 是「文件里 packed 数据的基准」，
    /// 正常就是 32（签名头之后），被嵌在别的容器里时由调用方加偏移。
    /// </summary>
    public static SevenZipStartHeader ReadStartHeader(ReadOnlySpan<byte> signature, long dataBase = SignatureSize)
    {
        if (signature.Length < SignatureSize)
        {
            throw new SevenZipFormatException(ManagedFastPathText.SignatureTooShort);
        }

        var startHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(signature[8..]);
        var computed = Crc32.Standard(signature.Slice(12, 20));
        if (computed != startHeaderCrc)
        {
            throw new SevenZipFormatException(ManagedFastPathText.StartHeaderCrcMismatch);
        }

        return new SevenZipStartHeader
        {
            Major = signature[6],
            Minor = signature[7],
            StartHeaderCrc = startHeaderCrc,
            NextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(signature[12..]),
            NextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(signature[20..]),
            NextHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(signature[28..]),
            DataBase = dataBase,
        };
    }

    /// <summary>
    /// 解析一段 StreamsInfo。返回时游标停在 StreamsInfo 自己的 <c>kEnd</c> 之后。
    ///
    /// 子块的 <c>kEnd</c> 归子块自己消费，外层再读一个 ID 当下一块的起点 ——
    /// 所以三个子块各一个 <c>kEnd</c>，StreamsInfo 自己还有第四个。
    /// </summary>
    public static SevenZipStreams ReadStreamsInfo(ref SevenZipCursor cursor)
    {
        var streams = new SevenZipStreams();
        var type = cursor.ReadId();

        if (type == SevenZipId.PackInfo)
        {
            streams.DataOffset = cursor.ReadNumber();
            ReadPackInfo(ref cursor, streams);
            type = cursor.ReadId();
        }

        if (type == SevenZipId.UnpackInfo)
        {
            ReadUnpackInfo(ref cursor, streams);
            type = cursor.ReadId();
        }

        if (type == SevenZipId.SubStreamsInfo)
        {
            ReadSubStreamsInfo(ref cursor, streams);
            type = cursor.ReadId();
        }

        if (type != SevenZipId.End)
        {
            throw new SevenZipFormatException(ManagedFastPathText.StreamsInfoNotEnd(type));
        }

        return streams;
    }

    /// <summary>
    /// 从明文头里取主流。返回 <c>null</c> 表示这段头里没有判据用得上的东西
    /// （例如带 AdditionalStreamsInfo 的包，这里直接放弃）。
    /// </summary>
    public static SevenZipStreams? ReadMainStreams(ReadOnlySpan<byte> header)
    {
        var cursor = new SevenZipCursor(header);
        var type = cursor.ReadId();

        if (type == SevenZipId.ArchiveProperties)
        {
            SkipArchiveProperties(ref cursor);
            type = cursor.ReadId();
        }

        if (type == SevenZipId.AdditionalStreamsInfo)
        {
            return null;
        }

        return type == SevenZipId.MainStreamsInfo ? ReadStreamsInfo(ref cursor) : null;
    }

    private static void SkipArchiveProperties(ref SevenZipCursor cursor)
    {
        for (; ; )
        {
            var type = cursor.ReadId();
            if (type == SevenZipId.End)
            {
                return;
            }

            cursor.SkipData();
        }
    }

    private static void ReadPackInfo(ref SevenZipCursor cursor, SevenZipStreams streams)
    {
        var numPackStreams = checked((int)cursor.ReadNumber());
        if (numPackStreams < 0 || numPackStreams > 1 << 20)
        {
            throw new SevenZipFormatException(ManagedFastPathText.TooManyPackStreams(numPackStreams));
        }

        WaitId(ref cursor, SevenZipId.Size);
        for (var i = 0; i < numPackStreams; i++)
        {
            streams.PackSizes.Add(cursor.ReadNumber());
        }

        for (; ; )
        {
            var type = cursor.ReadId();
            if (type == SevenZipId.End)
            {
                return;
            }

            if (type == SevenZipId.Crc)
            {
                var crcs = ReadHashDigests(ref cursor, numPackStreams);
                streams.PackCrcs.Clear();
                streams.PackCrcs.AddRange(crcs);
                continue;
            }

            cursor.SkipData();
        }
    }

    private static void ReadUnpackInfo(ref SevenZipCursor cursor, SevenZipStreams streams)
    {
        WaitId(ref cursor, SevenZipId.Folder);
        var numFolders = checked((int)cursor.ReadNumber());
        if (numFolders < 0 || numFolders > 1 << 20)
        {
            throw new SevenZipFormatException(ManagedFastPathText.TooManyFolders(numFolders));
        }

        var folders = streams.Folders;
        var packStreamIndex = 0;
        var coderUnpackSizeBase = 0;

        // CStreamSwitch::Set(this, dataVector)：写入端在这里固定写一个 0（7zOut.cpp:391）。
        // 非 0 表示 folder 段来自 AdditionalStreamsInfo 解出的数据 —— 那条路不判，直接放弃。
        if (cursor.ReadByte() != 0)
        {
            throw new SevenZipFormatException(ManagedFastPathText.StreamSwitchUnsupported);
        }

        for (var fo = 0; fo < numFolders; fo++)
        {
            var folder = new SevenZipFolder();
            var numInStreams = 0;
            var numCoders = checked((int)cursor.ReadNumber());
            if (numCoders == 0 || numCoders > 64)
            {
                throw new SevenZipFormatException(ManagedFastPathText.TooManyCoders(numCoders));
            }

            for (var ci = 0; ci < numCoders; ci++)
            {
                var mainByte = cursor.ReadByte();
                if ((mainByte & 0xC0) != 0)
                {
                    throw new SevenZipFormatException(ManagedFastPathText.CoderReservedBits);
                }

                var idSize = mainByte & 0xF;
                if (idSize > 8)
                {
                    throw new SevenZipFormatException(ManagedFastPathText.CoderIdTooLong);
                }

                ulong id = 0;
                for (var j = 0; j < idSize; j++)
                {
                    id = (id << 8) | cursor.ReadByte();
                }

                var coderInStreams = 1;
                if ((mainByte & 0x10) != 0)
                {
                    coderInStreams = checked((int)cursor.ReadNumber());
                    if (cursor.ReadNumber() != 1)
                    {
                        throw new SevenZipFormatException(ManagedFastPathText.CoderOutputsNotOne);
                    }
                }

                numInStreams += coderInStreams;
                if (numInStreams > 64)
                {
                    throw new SevenZipFormatException(ManagedFastPathText.TooManyInStreams);
                }

                byte[] properties = [];
                if ((mainByte & 0x20) != 0)
                {
                    properties = cursor.ReadSpan(checked((int)cursor.ReadNumber())).ToArray();
                }

                folder.Coders.Add(new SevenZipCoder(id, properties, coderInStreams));
            }

            int numPackStreams;
            int mainStreamIndex;
            if (numCoders == 1 && numInStreams == 1)
            {
                mainStreamIndex = 0;
                numPackStreams = 1;
            }
            else
            {
                var numBonds = numCoders - 1;
                if (numInStreams < numBonds)
                {
                    throw new SevenZipFormatException(ManagedFastPathText.InStreamsLessThanBonds);
                }

                var streamUsed = new bool[numInStreams];
                var coderUsed = new bool[numCoders];
                for (var i = 0; i < numBonds; i++)
                {
                    var inIndex = checked((int)cursor.ReadNumber());
                    if (inIndex >= numInStreams || streamUsed[inIndex])
                    {
                        throw new SevenZipFormatException(ManagedFastPathText.BondInStreamIndexInvalid);
                    }

                    streamUsed[inIndex] = true;

                    var coderIndex = checked((int)cursor.ReadNumber());
                    if (coderIndex >= numCoders || coderUsed[coderIndex])
                    {
                        throw new SevenZipFormatException(ManagedFastPathText.BondCoderIndexInvalid);
                    }

                    coderUsed[coderIndex] = true;
                }

                numPackStreams = numInStreams - numBonds;
                if (numPackStreams != 1)
                {
                    for (var i = 0; i < numPackStreams; i++)
                    {
                        var index = checked((int)cursor.ReadNumber());
                        if (index >= numInStreams || streamUsed[index])
                        {
                            throw new SevenZipFormatException(ManagedFastPathText.PackStreamIndexInvalid);
                        }

                        streamUsed[index] = true;
                    }
                }

                mainStreamIndex = 0;
                while (mainStreamIndex < numCoders && coderUsed[mainStreamIndex])
                {
                    mainStreamIndex++;
                }

                if (mainStreamIndex >= numCoders)
                {
                    throw new SevenZipFormatException(ManagedFastPathText.NoMainOutputCoder);
                }
            }

            folder.MainStreamIndex = mainStreamIndex;
            folder.FirstPackStream = packStreamIndex;
            folder.PackStreamCount = numPackStreams;
            packStreamIndex += numPackStreams;
            coderUnpackSizeBase += numCoders;
            if (coderUnpackSizeBase > MaxTotalCoders)
            {
                throw new SevenZipFormatException(ManagedFastPathText.TooManyCodersTotal(coderUnpackSizeBase));
            }

            folders.Add(folder);
        }

        WaitId(ref cursor, SevenZipId.CodersUnpackSize);
        var sizes = new List<ulong>(coderUnpackSizeBase);
        for (var i = 0; i < coderUnpackSizeBase; i++)
        {
            sizes.Add(cursor.ReadNumber());
        }

        var cursorInto = 0;
        foreach (var folder in folders)
        {
            for (var i = 0; i < folder.Coders.Count; i++)
            {
                folder.CoderUnpackSizes.Add(sizes[cursorInto++]);
            }
        }

        for (; ; )
        {
            var type = cursor.ReadId();
            if (type == SevenZipId.End)
            {
                return;
            }

            if (type == SevenZipId.Crc)
            {
                var crcs = ReadHashDigests(ref cursor, numFolders);
                for (var i = 0; i < numFolders && i < crcs.Count; i++)
                {
                    folders[i].UnpackedCrc = crcs[i];
                }

                continue;
            }

            cursor.SkipData();
        }
    }

    private static void ReadSubStreamsInfo(ref SevenZipCursor cursor, SevenZipStreams streams)
    {
        var folders = streams.Folders;
        var numFolders = folders.Count;
        var perFolder = new int[numFolders];
        Array.Fill(perFolder, 1);

        ulong type;
        for (; ; )
        {
            type = cursor.ReadId();
            if (type == SevenZipId.NumUnpackStream)
            {
                for (var i = 0; i < numFolders; i++)
                {
                    perFolder[i] = checked((int)cursor.ReadNumber());
                }

                continue;
            }

            if (type is SevenZipId.Crc or SevenZipId.Size or SevenZipId.End)
            {
                break;
            }

            cursor.SkipData();
        }

        if (type == SevenZipId.Size)
        {
            for (var i = 0; i < numFolders; i++)
            {
                var count = perFolder[i];
                for (var j = 1; j < count; j++)
                {
                    cursor.ReadNumber();
                }
            }

            type = cursor.ReadId();
        }

        // 这一段的 digest 判据用不上，但字节数必须消耗对，否则后面的头会错位。
        var numDigests = 0;
        for (var i = 0; i < numFolders; i++)
        {
            if (perFolder[i] != 1 || folders[i].UnpackedCrc is null)
            {
                numDigests += perFolder[i];
            }
        }

        for (; ; )
        {
            if (type == SevenZipId.End)
            {
                return;
            }

            if (type == SevenZipId.Crc)
            {
                var defined = ReadBoolVector2(ref cursor, numDigests);
                var k = 0;
                for (var i = 0; i < numFolders; i++)
                {
                    var count = perFolder[i];
                    if (count == 1 && folders[i].UnpackedCrc is not null)
                    {
                        continue;
                    }

                    for (var j = 0; j < count; j++)
                    {
                        uint? crc = null;
                        if (k < defined.Length && defined[k])
                        {
                            crc = cursor.ReadUInt32();
                        }

                        k++;

                        // folder 只有一个 substream 时，这条 digest 就是 folder 解压结果的 CRC
                        // —— 夹具 vault\seven-mhe.7z 与 seven-plain.7z 实测：7-Zip 就是这么放的
                        //（UnpackInfo 里反而没有 kCRC）。
                        if (count == 1 && crc is not null && j == 0)
                        {
                            folders[i].UnpackedCrc = crc;
                        }
                    }
                }
            }
            else
            {
                cursor.SkipData();
            }

            type = cursor.ReadId();
        }
    }

    private static List<uint?> ReadHashDigests(ref SevenZipCursor cursor, int numItems)
    {
        var defined = ReadBoolVector2(ref cursor, numItems);
        var values = new List<uint?>(numItems);
        for (var i = 0; i < numItems; i++)
        {
            values.Add(defined[i] ? cursor.ReadUInt32() : null);
        }

        return values;
    }

    private static bool[] ReadBoolVector2(ref SevenZipCursor cursor, int numItems)
    {
        var allDefined = cursor.ReadByte();
        if (allDefined == 0)
        {
            return ReadBoolVector(ref cursor, numItems);
        }

        var all = new bool[numItems];
        Array.Fill(all, true);
        return all;
    }

    /// <summary>位掩码，每字节从高位往低位（<c>7zIn.cpp:1158</c>）。</summary>
    private static bool[] ReadBoolVector(ref SevenZipCursor cursor, int numItems)
    {
        var values = new bool[numItems];
        byte current = 0;
        byte mask = 0;
        for (var i = 0; i < numItems; i++)
        {
            if (mask == 0)
            {
                current = cursor.ReadByte();
                mask = 0x80;
            }

            values[i] = (current & mask) != 0;
            mask >>= 1;
        }

        return values;
    }

    /// <summary>一直读到指定的属性 ID，中间不认识的按「长度 + 内容」跳过。</summary>
    private static void WaitId(ref SevenZipCursor cursor, ulong id)
    {
        for (; ; )
        {
            var type = cursor.ReadId();
            if (type == id)
            {
                return;
            }

            cursor.SkipData();
        }
    }
}
