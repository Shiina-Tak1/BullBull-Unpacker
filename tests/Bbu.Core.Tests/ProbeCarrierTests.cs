using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层边界档：伪装包与内嵌检测。
///
/// 这一层的判据不只是"认不认得出"，还有"该不该付那笔代价"：垫片包要切、绝对偏移包不许切、
/// 每多一档就要多读一遍文件。所以每条断言都连着计次 —— 只看结论不看代价，
/// 一个每次全盘扫的实现也能得出全部正确结论（B-2026-094 / 096 / 097）。
/// </summary>
[Trait("Category", "edge")]
[Collection("engine")]
public sealed class ProbeCarrierTests
{
    /// <summary>夹具里垫片的确切长度。</summary>
    private const long PadBytes = 1200 * 1024;

    /// <summary>
    /// 兜底那一问必须排在尾部签名之后，而且只补前几档都没认出来的那些。
    ///
    /// 第二问是保险：真引擎那一支目前没有观测到改变结论，
    /// 所以这里用合成回调只钉两件机器判得了的事 —— 它能把「前几档都没结果」的文件收成直读，
    /// 以及**不许**抢在尾部签名前面（那个包要留着切包，切出来那一份才吃得到快路）。
    /// </summary>
    [Fact]
    public void FallbackEngineProbe_MustRescueOnlyWhatTheCheapTiersMissed()
    {
        using var dir = new TestDirectory();
        var shelled = ShelledRar4(dir);

        // 第一个引擎（7-Zip）认不出它：前几档一个结果都没有。
        var first = Probe.InspectCarrier(shelled, new ProbeOptions { EngineProbe = _ => false });
        Assert.Equal(Carrier.None, first.Kind);
        Assert.False(first.WorthHandling);

        // 第二个引擎（UnRAR）说读得动 → 这一档才成立，而且是「直读不切」。
        var both = Probe.InspectCarrier(shelled, new ProbeOptions
        {
            EngineProbe = _ => false,
            FallbackEngineProbe = _ => true,
        });
        Assert.Equal(Carrier.EngineDirect, both.Kind);
        Assert.True(both.Direct);
        Assert.Null(both.Embedded);

        // 反向对照：兜底不许抢在尾部签名前面 —— 那个包要留着切包。
        var shimmed = TestPaths.Probe(Path.Combine("carrier-tail", "x.mp4"));
        var keep = Probe.InspectCarrier(shimmed, new ProbeOptions
        {
            EngineProbe = _ => true,
            FallbackEngineProbe = _ => true,
        });
        Assert.Equal(Carrier.TailDirectory, keep.Kind);
        Assert.NotNull(keep.Embedded);
    }

    /// <summary>造「MZ 壳 + 一整个 RAR4」的马甲：壳 1.3 MB（≥1MB 才算马甲），扩展名是 mp4。</summary>
    private static string ShelledRar4(TestDirectory dir)
    {
        var body = File.ReadAllBytes(Path.Combine(TestPaths.Fixtures, "vault", "rar4-p.rar"));
        var bytes = new byte[(1300 * 1024) + body.Length];
        byte[] mz = [0x4d, 0x5a];
        mz.CopyTo(bytes, 0);
        body.CopyTo(bytes, 1300 * 1024);

        var path = dir.Combine("shelled-rar4.mp4");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// 判据 5 与 B-2026-094 的对照组②：≥1MB 垫片 + 尾部完整 ZIP，要认出伪装并给出内嵌偏移。
    ///
    /// 偏移量必须精确等于垫片长度：差一个字节，切出来的包就解不开。
    /// 它同时是"绝对偏移那一档不许改宽"的对照 —— 这一类的引擎也能直读，
    /// 拿引擎问询判它就会从<em>切包</em>变成<em>直读</em>，产物还对，是拿正确性换速度。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-094")]
    public void ShimmedPackage_MustBeLocatedAtTheRealOffset()
    {
        var path = TestPaths.Probe(Path.Combine("carrier-tail", "x.mp4"));

        var counters = new ProbeCounters();
        var classified = Probe.Classify(path, new ProbeOptions { Counters = counters });

        Assert.Equal(ProbeStatus.NotArchive, classified.Status);
        Assert.Equal(Carrier.TailDirectory, classified.Carrier.Kind);
        Assert.NotNull(classified.Carrier.Embedded);
        Assert.Equal(ArchiveFormat.Zip, classified.Carrier.Embedded!.Format);
        Assert.Equal(PadBytes, classified.Carrier.Embedded.Offset);
        Assert.False(classified.Direct);
        Assert.True(classified.ShallHandle);
        Assert.Contains("内嵌", Probe.CarrierNote(classified.Carrier), StringComparison.Ordinal);

        // 尾部目录定位是毫秒级的那一档：不读整文件、不起进程。
        Assert.Equal(0, counters.DeepScans);
        Assert.Equal(0, counters.EngineProbes);
    }

    /// <summary>
    /// 判据 5 / 6 与 B-2026-094：假头 + 绝对偏移布局 —— 认出来、不许切包、更不许为它全盘扫。
    ///
    /// "不许切"有实测依据：包里那几个偏移是相对**文件开头**算的，切掉假头反而整体错位。
    /// 随包 7z.exe 对整个文件是能读的（Everything is Ok），所以正确动作是"原样直读"。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-094")]
    public async Task AbsoluteLayout_MustBeReadDirectlyWithoutScanning()
    {
        var path = TestPaths.Probe(Path.Combine("carrier-abs", "x.mp4"));

        var counters = new ProbeCounters();
        var classified = Probe.Classify(path, new ProbeOptions { Counters = counters });

        Assert.Equal(Carrier.AbsoluteLayout, classified.Carrier.Kind);
        Assert.True(classified.Direct);
        Assert.True(classified.ShallHandle);
        Assert.Null(classified.Carrier.Embedded);
        Assert.Equal(ProbeText.CarrierAbsoluteLayout, Probe.CarrierNote(classified.Carrier));

        // 这条判据的本体：一次全盘扫都不许有。全盘扫一次要 2 到 7 秒。
        Assert.Equal(0, counters.DeepScans);
        Assert.Equal(0, counters.EngineProbes);

        // 真跑一次：引擎自己会跳过假头，所以"不许切包"不是猜测。
        var engine = TestPaths.NewEngine();
        var result = await engine.TestAsync(path, kind: EngineKind.SevenZip);
        Assert.True(result.Ok, "引擎读不了绝对偏移布局，判据要重算：" + result.Output);
    }

    /// <summary>
    /// 欠账表 `debt 56` 的落点：`Carrier.EngineDirect` 原来只有桩断言，这一条走**真引擎**。
    ///
    /// 1300 KiB 假头（2 字节 MZ 加零填充）加一整个真 RAR4、扩展名 `.mp4`：7-Zip 自己跳过假头读得动
    /// （实测 `7z l` 退出码 0、1 个条目、`Offset 1331200`），所以这一档必须命中、且不必切包。
    /// 判据与生产 `CanEngineRead` 同源 —— 走 `InspectAsync`（即 `7z l`）而不是 `TestAsync`：
    /// `rar4-p.rar` 是 `-p` 加密的，`7z t` 会要密码。
    /// </summary>
    [Fact]
    public async Task EngineDirect_MustBeReachedByTheRealEngine()
    {
        using var dir = new TestDirectory();
        var shelled = ShelledRar4(dir);
        var engine = TestPaths.NewEngine();

        // 先核夹具形态：真引擎读不动它，这一条就没有被测对象
        var info = await engine.InspectAsync(shelled);
        Assert.True(info.ReadOk && info.EntryCount > 0, "真引擎读不动这个壳包，夹具要换一个");

        var probe = Probe.InspectCarrier(shelled, new ProbeOptions
        {
            EngineProbe = path => Task.Run(() => engine.InspectAsync(path)).GetAwaiter().GetResult()
                is { ReadOk: true, EntryCount: > 0 },
        });

        Assert.Equal(Carrier.EngineDirect, probe.Kind);
        Assert.True(probe.WorthHandling);
    }

    /// <summary>
    /// 判据 6 与 B-2026-096：包在文件<em>中段</em>时只有全盘扫找得到，
    /// 所以"扫不扫"必须是调用方声明的代价决策，不是探测函数自己决定的。
    ///
    /// 目录报备那一路（`FindArchivesBelow` 的 `DirSearch.Carriers`）按目录里每个文件跑，
    /// 一旦它做全盘扫，一个装满真视频的目录就是每个多读一遍整文件。
    /// 末一段同时是 B-2026-094 的对照组③：引擎读不了的零填充马甲，深扫兜底必须还在
    /// —— 为了省代价把兜底一并删掉，中段的包就永远找不着了。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-096")]
    [Trait("Bug", "B-2026-094")]
    public void DeepScan_MustBeOptInAndMustBeTheOnlyTierThatReadsEverything()
    {
        var path = TestPaths.Probe(Path.Combine("carrier-deep", "x.mp4"));

        var cheap = new ProbeCounters();
        var shallow = Probe.InspectCarrier(path, new ProbeOptions { Counters = cheap });
        Assert.Equal(Carrier.None, shallow.Kind);
        Assert.False(shallow.WorthHandling);
        Assert.Equal(0, cheap.DeepScans);

        var deep = new ProbeCounters();
        var found = Probe.InspectCarrier(path, new ProbeOptions { Counters = deep, Deep = true });
        Assert.Equal(Carrier.DeepScan, found.Kind);
        Assert.Equal(ArchiveFormat.Rar5, found.Embedded!.Format);
        Assert.Equal(PadBytes, found.Embedded.Offset);
        Assert.Equal(1, deep.DeepScans);
        Assert.Equal(1, deep.TailProbes);
    }

    /// <summary>
    /// 判据 6 与"超过 8MB 上限的那档"：尾部签名是廉价档里最靠后的一条，
    /// 它认的是 RAR5 / 7z 的明文签名<b>加头部 CRC</b> —— 只认签名的实现会把随机数据当包。
    /// </summary>
    [Fact]
    public void TailMagic_MustFindThePackageWithoutReadingTheWholeFile()
    {
        var path = TestPaths.Probe(Path.Combine("carrier-tailmagic", "x.mp4"));

        var counters = new ProbeCounters();
        var probe = Probe.InspectCarrier(path, new ProbeOptions { Counters = counters });

        Assert.Equal(Carrier.TailMagic, probe.Kind);
        Assert.Equal(ArchiveFormat.Rar5, probe.Embedded!.Format);
        Assert.Equal(PadBytes, probe.Embedded.Offset);
        Assert.Equal(1, counters.TailProbes);
        Assert.Equal(0, counters.DeepScans);

        // 反向对照：同样的字节放在 RAR5 签名后面但头部 CRC 是坏的，必须认不出来。
        using var tmp = new TestDirectory();
        var broken = tmp.Combine("broken.mp4");
        var bytes = File.ReadAllBytes(path);
        var at = (int)probe.Embedded.Offset;
        bytes[at + 8] ^= 0xFF;
        File.WriteAllBytes(broken, bytes);
        Assert.Null(Probe.TailMagic(broken));
    }

    /// <summary>
    /// 判据 7 与 B-2026-061：`.tgz` / `.tar.zst` / `.tzst` 是压缩包的正常叫法，不许标成"伪装成视频"。
    ///
    /// 三个名字的扩展名都在压缩包名单里，所以"内容像包、名字不像包"这个判据对它们不成立。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-061")]
    public void RenamedTarballs_MustNotBeMarkedAsDisguisedOrCarrier()
    {
        var folder = TestPaths.Probe("renamed");
        var names = new[] { "x.tgz", "x.tar.zst", "x.tzst" };
        var paths = names.Select(name => Path.Combine(folder, name)).ToList();

        foreach (var path in paths)
        {
            Assert.False(Probe.IsDisguised(path), Path.GetFileName(path) + " 被标成了伪装");

            var classified = Probe.Classify(path);
            Assert.True(classified.IsCandidate, Path.GetFileName(path) + " 不是候选");
            Assert.True(classified.Format.IsArchive());
            Assert.Equal(Carrier.None, classified.Carrier.Kind);

            // 廉价档也不许点它们：它们是标准候选，不是"藏起来的包"。
            Assert.False(Probe.InspectCarrier(path, new ProbeOptions { Deep = false }).WorthHandling);
        }
    }

    /// <summary>
    /// 判据 8 与 B-2026-077：排除名单对伪装包分支同样生效。
    ///
    /// 名单是调用方的过滤动作，识别层只给判据 —— 但这条判据必须在<b>所有</b>通道上都能命中，
    /// 马甲这一支也不例外：名单里写着 mp4 时用户照样被解出一个包，就是这里漏的。
    /// 伪装按内容判、排除按名字判，两者正交，谁都不许压过谁。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-077")]
    public void ExcludedExtension_MustReachTheCarrierBranchToo()
    {
        var path = TestPaths.Probe(Path.Combine("carrier-tail", "x.mp4"));

        // 反例先行：不给名单时它是值得处理的那种。
        Assert.True(Probe.Classify(path).ShallHandle);

        // 四种写法都要认：名单是用户手填的，大小写、星号、前导点都不该影响结论。
        foreach (var entry in new[] { "mp4", "MP4", "*.mp4", ".mp4" })
        {
            Assert.Equal("mp4", Probe.ExcludedExt(path, [entry]));
        }

        // 而"名单命中"不许污染识别结论：它照样是个里头有包的载体，
        // 调用方拿这个结论去报"有东西没解"，不是拿它当"不是压缩包"。
        var classified = Probe.Classify(path);
        Assert.Equal(ProbeStatus.NotArchive, classified.Status);
        Assert.True(classified.ShallHandle);

        // 没命中的名单不许误伤。
        Assert.Equal(string.Empty, Probe.ExcludedExt(path, ["txt"]));
        Assert.True(Probe.Classify(path).ShallHandle);
    }

    /// <summary>
    /// B-2026-020 的识别侧一半：ZS 裸流（lz4 / zstd 帧）是包，不是"不是压缩包"。
    ///
    /// 本条只结识别侧：这两种文件在标准候选通道里就够了，不该落到马甲通道。
    /// 穿透侧（继续解开、产物逐字节一致）归 R6。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-020")]
    public void ZsBareStreams_MustBeCandidatesNotCarriers()
    {
        var lz4 = TestPaths.Probe(Path.Combine("zs-skeleton", "x.lz4"));
        var zstd = TestPaths.Probe(Path.Combine("zs-skeleton", "x.zst"));

        Assert.Equal(ArchiveFormat.Lz4, Probe.DetectFormat(lz4));
        Assert.Equal(ArchiveFormat.Zstd, Probe.DetectFormat(zstd));

        foreach (var path in new[] { lz4, zstd })
        {
            var classified = Probe.Classify(path);
            Assert.True(classified.IsCandidate, Path.GetFileName(path) + " 被判成了不是压缩包");
            Assert.NotEqual(ProbeStatus.NotArchive, classified.Status);
            Assert.True(classified.ShallHandle);
            Assert.Contains(path, Probe.CandidatesFrom([path]));
        }

        Assert.False(Probe.InspectCarrier(lz4, new ProbeOptions { Deep = false }).WorthHandling);
    }
}
