using System;
using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 磁盘余量的两个时刻。
/// 事前那道按包的**声明**体积判、还没起引擎就拒绝；事中那道看**实际**余量、跌破下限就中止。
///
/// 两条都断言「引擎有没有真的起来过」：只断言返回值的话，一个先解压到一半再报空间不够的
/// 实现同样能过，而那种实现已经把盘写满了。
/// </summary>
[Collection("engine")]
public sealed class EngineDiskFloorTests
{
    private const long Giga = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(10 * Giga, 5 * Giga, 1 * Giga, DiskFloorVerdict.Ok)]
    [InlineData(10 * Giga, 20 * Giga, 1 * Giga, DiskFloorVerdict.NotEnoughForThisPack)]
    [InlineData(2 * Giga, 4 * Giga, 1 * Giga, DiskFloorVerdict.NotEnoughForThisPack)]
    [InlineData(3 * Giga, 0, 5 * Giga, DiskFloorVerdict.BelowFloor)]
    [InlineData(10 * Giga, 0, 5 * Giga, DiskFloorVerdict.Ok)]
    public void Decide_MustJudgeByDeclaredSizeNotOnlyFreeSpace(
        long free, long declared, long minFree, DiskFloorVerdict expected) =>
        Assert.Equal(expected, DiskFloor.Decide(free, declared, minFree));

    /// <summary>
    /// 声明体积巨大时事前判据不许溢出成「够用」：写成 <c>free &lt; declaredBytes + minFreeBytes</c>
    /// 时右边溢出为负、判定恒为 Ok，事前该拒的没拒（B-2026-203）。
    /// 最后一行是反方向对照：正常声明体积照旧判得对，改判据时不许把正常包一起拒掉。
    /// </summary>
    [Theory]
    [InlineData(1 * Giga, long.MaxValue, 1 * Giga, DiskFloorVerdict.NotEnoughForThisPack)]
    [InlineData(1 * Giga, (long.MaxValue / 2) + 1, (long.MaxValue / 2) + 1, DiskFloorVerdict.NotEnoughForThisPack)]
    [InlineData(1 * Giga, long.MaxValue - 1, 1, DiskFloorVerdict.NotEnoughForThisPack)]
    [InlineData(10 * Giga, 5 * Giga, 1 * Giga, DiskFloorVerdict.Ok)]
    [Trait("Bug", "B-2026-203")]
    public void Decide_MustNotOverflowOnAHugeDeclaredSize(
        long free, long declared, long minFree, DiskFloorVerdict expected) =>
        Assert.Equal(expected, DiskFloor.Decide(free, declared, minFree));

    [Fact]
    public void Decide_MustPassThroughWhenNoFloorIsSet()
    {
        Assert.Equal(DiskFloorVerdict.Ok, DiskFloor.Decide(1024, 200 * Giga, 0));
        Assert.Equal(DiskFloorVerdict.Ok, DiskFloor.Decide(1024, 200 * Giga, -1));
    }

    [Fact]
    public void Decide_MustNotKillTheTaskWhenFreeSpaceIsUnknown()
    {
        Assert.Equal(DiskFloorVerdict.Ok, DiskFloor.Decide(null, 200 * Giga, 200 * Giga));
    }

    [Fact]
    public void BelowFloor_MustIgnoreUnsetFloor() =>
        Assert.False(DiskFloor.BelowFloor(0, 0));

    [Fact]
    public void BelowFloor_MustIgnoreUnknownFreeSpace() =>
        Assert.False(DiskFloor.BelowFloor(null, 1));

    [Fact]
    public void FreeBytes_MustReadAFamiliarDrive()
    {
        var free = DiskFloor.FreeBytes(TestPaths.Root);

        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    [Fact]
    public void FreeBytes_MustReturnNullForGarbagePath() =>
        Assert.Null(DiskFloor.FreeBytes("Z:\\这\\个\\盘\\不\\存\\在\\ing"));

    /// <summary>
    /// 几百字节的小包在余量高于下限时照解：事前判据扣掉的是**包声明的体积**，不是下限本身。
    /// 余量连下限都够不着时任何包都被拒 —— 下限的语义是「解完还要剩这么多」。
    /// </summary>
    [Fact]
    public async Task SmallPack_MustExtractWhenFreeSpaceClearsTheFloor()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine(freeBytesProbe: _ => 4L * Giga);

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("ok-small.zip"),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip, MinFreeBytes = 2L * Giga });

        Assert.True(result.Ok, result.Output);
        Assert.NotEmpty(Directory.GetFiles(output.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>余量连下限都够不着时小包也被拒：这是「解完还要剩这么多」的直接后果。</summary>
    [Fact]
    public async Task AnyPack_MustBeRefusedWhenFreeSpaceIsBelowTheFloorItself()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine(freeBytesProbe: _ => 1024);

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("ok-small.zip"),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip, MinFreeBytes = 2L * Giga });

        Assert.True(result.OutOfSpace);
        Assert.Empty(result.Command);
    }

    /// <summary>事前拒绝：连引擎都不该起，命令行是空的。</summary>
    [Fact]
    public async Task HugeDeclaredPack_MustBeRefusedBeforeStartingTheEngine()
    {
        using var output = new TestDirectory();
        var probes = 0;
        var engine = TestPaths.NewEngine(freeBytesProbe: _ =>
        {
            probes++;
            return 1024;
        });

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("declared-big.zip"),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip, MinFreeBytes = Giga });

        Assert.True(result.OutOfSpace);
        Assert.False(result.Ok);
        Assert.False(result.SpaceGuardHit);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Command);
        Assert.Equal(1, probes);
        Assert.Contains("解压后可用磁盘空间将低于设定下限", result.Output);
        Assert.Empty(Directory.GetFiles(output.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>判不出体积时节退化成绝对下限，措辞与「这个包太大」不同。</summary>
    [Fact]
    public async Task UnknownSize_MustFallBackToTheAbsoluteFloor()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine(freeBytesProbe: _ => 1024);

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("declared-big.zip"),
            output.Path,
            password: null,
            new ExtractOptions
            {
                Kind = EngineKind.SevenZip,
                DeclaredBytes = 0,
                MinFreeBytes = Giga,
            });

        Assert.True(result.OutOfSpace);
        Assert.Contains("可用磁盘空间低于设定下限", result.Output);
    }

    /// <summary>
    /// 事中中止：解压过程中余量跌破下限，进程被杀掉。
    ///
    /// 用自带基准测试当长命令，而不是解一个真实的包：后者谁快谁慢取决于机器，
    /// 而且这个判据盯的是产物所在盘的余量，与命令干什么无关。
    /// </summary>
    [Fact]
    public async Task DroppingFreeSpace_MustAbortTheRunningCommand()
    {
        using var output = new TestDirectory();
        var log = new RecordingLog();
        var engine = TestPaths.NewEngine(log, spaceCheckSeconds: 0.1, freeBytesProbe: _ => 0);

        var result = await engine.RunAsync(
            TestPaths.LongRunningCommand,
            EngineKind.SevenZip,
            new EngineRunOptions { MinFreeBytes = Giga, GuardDirectory = output.Path });

        Assert.True(result.SpaceGuardHit);
        Assert.False(result.Ok);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(result.Seconds < 60, "中止后没有及时返回");

        // 原因要说出来。Output 是引擎被杀前的原话，结论那一句在 Brief 与日志里。
        Assert.Equal(Errors.SpaceGuardAborted, result.Brief);
        Assert.Contains(log.Infos, line => line.Contains("已停止"));
    }

    /// <summary>同一条长命令、同样的余量，只是没设下限：不得被中止。证明确实是下限在起作用。</summary>
    [Fact, Trait("Category", "edge")]
    public async Task SameCommandWithoutFloor_MustOnlyHitTheTimeout()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine(timeoutSeconds: 1.0, freeBytesProbe: _ => 0);

        var result = await engine.RunAsync(
            TestPaths.LongRunningCommand,
            EngineKind.SevenZip,
            new EngineRunOptions { MinFreeBytes = 0, GuardDirectory = output.Path });

        Assert.False(result.SpaceGuardHit);
        Assert.True(result.TimedOut);
    }

    /// <summary>解压中读不到余量时不得中止：那种情况要由别的判据处理。</summary>
    [Fact]
    public async Task UnknownFreeSpaceDuringRun_MustNotAbort()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine(spaceCheckSeconds: 0.1, freeBytesProbe: _ => null);

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("ok-small.zip"),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip, MinFreeBytes = Giga });

        Assert.False(result.SpaceGuardHit);
        Assert.True(result.Ok, result.Output);
    }
}
