using System;
using System.Buffers.Binary;
using System.IO;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Services.Managed7z;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 托管 7z 头解析的两条健壮性判据。
///
/// ① coder 累加量有上限，且判据落在累加的当处（B-2026-195）。folder 个数与单个 folder 的
/// coder 数各有一条上限，但两者的乘积没有：每个 coder 的字节能压到 3 字节以下，于是 64 MiB 的头
/// 能声明千万级 coder，`new List&lt;ulong&gt;` 与 coder 对象图一起把堆耗尽。
/// ② 解析抛非预期异常时当场放掉源（B-2026-196）。<c>ManagedSevenZipSession.Open(string)</c>
/// 原先只在「解析回 null」那条路上释放，头里的长度字段溢出时异常逃出去，句柄要等到 GC。
///
/// 这一层此前没有测试类，样本是**手写的字节流**：7z 的属性 id 与 vint 编码逐字节摆在下面的
/// helper 里，布局依据是 7-Zip 官方源码包的 7zIn.cpp。
/// </summary>
public sealed class Managed7zHeaderLimitTests
{
    /// <summary>与 <c>vault\seven-*.7z</c> 那两张夹具同值。</summary>
    private const string Password = "BbuPw2026";

    /// <summary>样本里每个 folder 的 coder 数：单 folder 的上限就是 64，取满时每字节能声明的累加量最大。</summary>
    private const int CodersPerFolder = 64;

    /// <summary>
    /// 声明到刚过累加上限的 coder 个数：判据要在累加的当处抛，而不是把四百多万个 coder 建出来
    /// 之后才失败。撤掉那条上限，这条从抛异常变成解析成功（下面那句消息断言一起红）。
    /// </summary>
    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-195")]
    public void CoderUnpackSizeTotalPastTheLimit_MustFailFast()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("seven-coder-flood.7z");
        var header = CoderFloodHeader(path, out var declaredCoders);

        // 坐标自检：样本没超过上限的话，下面那句 Throws 测的是别的失败。
        Assert.True(declaredCoders > SevenZipHeaderReader.MaxTotalCoders, "样本本该声明到上限之外");
        Assert.True(header.Length > declaredCoders, "样本本该把头里的 unpack size 也写出来");

        using var source = SevenZipFileSource.Open(path);
        var thrown = Assert.Throws<SevenZipFormatException>(() =>
        {
            SevenZipLayoutReader.Read(source);
        });

        // 报的必须是累加量那一条：撞在头尾上也是 SevenZipFormatException，那样这条就测错了东西。
        Assert.Contains("coder 累计个数超出上限", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反方向：上限不许压到正常包上。既有夹具照旧开庭，正确密码照旧不被误杀。
    /// </summary>
    [Fact]
    public void NormalArchives_MustStillOpenAndKeepTheirCorrectPassword()
    {
        foreach (var name in new[] { "seven-mhe.7z", "seven-plain.7z" })
        {
            var path = Path.Combine(TestPaths.Fixtures, "vault", name);
            using var session = ManagedSevenZipSession.Open(path);
            Assert.NotNull(session);
            Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
        }
    }

    /// <summary>
    /// 量真进程内存。同一个头，撤掉上限时会建出四百多万个 <c>SevenZipCoder</c>（数亿字节），
    /// 修好之后只分配头自己的字节数组。64 MB 这道线只用来把两种量级分开，不是精确阈值。
    /// </summary>
    [Fact, Trait("Category", "machine"), Trait("Bug", "B-2026-195")]
    public void CoderFloodHeader_MustNotGrowTheHeapWithoutBound()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("seven-coder-flood-heap.7z");
        CoderFloodHeader(path, out _);
        var fileBytes = new FileInfo(path).Length;

        var before = GC.GetTotalMemory(forceFullCollection: true);
        using (var source = SevenZipFileSource.Open(path))
        {
            try
            {
                SevenZipLayoutReader.Read(source);
            }
            catch (SevenZipFormatException)
            {
                // 上限把它拦下来正是这条断言要的结局；判据是堆增量，不是这次调用成功与否。
            }
        }

        var grown = GC.GetTotalMemory(forceFullCollection: true) - before;
        Assert.True(
            grown < 64L * 1024 * 1024,
            $"这一个头让堆长出 {grown / (1024 * 1024)} MB（文件只有 {fileBytes / (1024 * 1024)} MB）");
    }

    /// <summary>
    /// 头里的长度字段大于 <c>int.MaxValue</c>：解析抛 `OverflowException`，而那条路原先不释放源。
    /// 断言钉的是**当场**能改名 —— 句柄还占着时 <c>FileShare</c> 不含 <c>Delete</c>，改名一定失败。
    /// 中间不做任何分配、不触发 GC：句柄有终结器，一回收这两档就分不开了。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-196")]
    public void OpenAfterAParseFailure_MustReleaseTheSourceAtOnce()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("seven-overflow.7z");
        OverflowingHeaderSample(path);

        Assert.Throws<OverflowException>(() =>
        {
            ManagedSevenZipSession.Open(path);
        });

        var moved = path + ".moved";
        File.Move(path, moved);
        Assert.True(File.Exists(moved));
    }

    /// <summary>
    /// 一个把 coder 累加量声明到上限之外的 7z 头。<c>numFolders = 上限/64 + 1</c>，每 folder 64 个
    /// coder。每个 coder 只占 1 字节主字节（id 长度为 0、无 properties），每 folder 另有 63 组
    /// bond（各 2 字节），所以一个 folder 占 191 字节。<c>codersUnpackSize</c> 真写出来（全 0）
    /// 是为了让「撤掉上限」那一次能解析到底 —— 否则它也会撞在头尾上，两种结局就分不开了。
    /// </summary>
    private static byte[] CoderFloodHeader(string path, out int declaredCoders)
    {
        var numFolders = (SevenZipHeaderReader.MaxTotalCoders / CodersPerFolder) + 1;
        declaredCoders = numFolders * CodersPerFolder;

        var folder = new byte[1 + CodersPerFolder + (2 * (CodersPerFolder - 1))];
        folder[0] = CodersPerFolder;
        for (var i = 0; i < CodersPerFolder - 1; i++)
        {
            folder[65 + (i * 2)] = (byte)i;
            folder[66 + (i * 2)] = (byte)i;
        }

        var prefix = new byte[]
        {
            (byte)SevenZipId.Header,
            (byte)SevenZipId.MainStreamsInfo,
            (byte)SevenZipId.PackInfo,
            0,                              // DataOffset
            1,                              // numPackStreams
            (byte)SevenZipId.Size,
            0,                              // packSize
            (byte)SevenZipId.End,           // PackInfo 结束
            (byte)SevenZipId.UnpackInfo,
            (byte)SevenZipId.Folder,
        };

        var header = new byte[
            prefix.Length + 3 + 1 + (numFolders * folder.Length) + 1 + declaredCoders + 2];
        var at = 0;
        prefix.CopyTo(header, at);
        at += prefix.Length;

        // numFolders 的 vint：小于 2097152 的值写 3 字节（首字节 0xC0 加高 5 位）。
        header[at++] = (byte)(0xC0 | ((numFolders >> 16) & 0x1F));
        header[at++] = (byte)(numFolders & 0xFF);
        header[at++] = (byte)((numFolders >> 8) & 0xFF);
        header[at++] = 0;                   // CStreamSwitch

        for (var i = 0; i < numFolders; i++)
        {
            folder.CopyTo(header, at);
            at += folder.Length;
        }

        header[at++] = (byte)SevenZipId.CodersUnpackSize;
        at += declaredCoders;               // 每个 coder 的输出大小，全 0
        header[at++] = (byte)SevenZipId.End;    // UnpackInfo 结束
        header[at++] = (byte)SevenZipId.End;    // StreamsInfo 结束
        Assert.Equal(header.Length, at);

        WriteArchive(path, header);
        return header;
    }

    /// <summary>
    /// 9 字节的 next header：kHeader → kMainStreamsInfo → kPackInfo → DataOffset=0 →
    /// numPackStreams 写成 2^31（vint `F0 00 00 00 80`）。`checked((int)…)` 就在那一格抛溢出。
    /// </summary>
    private static void OverflowingHeaderSample(string path)
    {
        var header = new byte[]
        {
            (byte)SevenZipId.Header,
            (byte)SevenZipId.MainStreamsInfo,
            (byte)SevenZipId.PackInfo,
            0,                              // DataOffset
            0xF0, 0x00, 0x00, 0x00, 0x80,   // numPackStreams = 2^31
        };

        WriteArchive(path, header);
    }

    /// <summary>
    /// 把一段 next header 包成最小的 7z 文件：32 字节签名头加头自己（<c>NextHeaderOffset</c> 为 0），
    /// 两处 CRC 都按官方口径算（startHeaderCRC 覆盖偏移 12 起的 20 字节，nextHeaderCRC 覆盖头自己）。
    /// </summary>
    private static void WriteArchive(string path, byte[] header)
    {
        var file = new byte[SevenZipHeaderReader.SignatureSize + header.Length];
        SevenZipHeaderReader.Magic.CopyTo(file);
        file[6] = 0;                        // Major：只认 0
        file[7] = 4;                        // Minor
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(20), (ulong)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), Crc32.Standard(header));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), Crc32.Standard(file.AsSpan(12, 20)));
        header.CopyTo(file, SevenZipHeaderReader.SignatureSize);

        File.WriteAllBytes(path, file);
    }
}
