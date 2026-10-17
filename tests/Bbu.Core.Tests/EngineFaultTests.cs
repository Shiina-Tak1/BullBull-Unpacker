using System;
using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 运行期会真实伤到用户的两种失败，各自挂着 号。
///
/// 两条都是 High，所以进默认档：破掉它们不是「数字变了」，而是机器被写满、
/// 或者整批任务因为一个目录建不出来而中断。
/// </summary>
[Collection("engine")]
public sealed class EngineFaultTests
{
    private const long Giga = 1024L * 1024 * 1024;

    /// <summary>
    /// B-2026-043：解压体积没有上界，几 MB 的包可以写满系统盘，而本工具是无人值守批量跑的。
    /// 事中那道监控是真兜底：一旦余量跌破下限就把进程杀掉，并如实说成「主动拦下」。
    ///
    /// 用自带基准测试当长命令，而不是解一个真实的包：后者快慢取决于机器，
    /// 这个判据盯的是产物所在盘的余量，与命令干什么无关。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-043")]
    public async Task VolumeBomb_MustBeStoppedMidFlightAndBeSaidSo()
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
        Assert.Equal(Errors.SpaceGuardAborted, result.Brief);
        Assert.NotEqual(Errors.SpaceNotEnough, result.Brief);

        var hit = Assert.Single(log.Infos, line => line.Contains("解压中可用磁盘空间低于设定下限"));
        Assert.Contains("已停止", hit);

        // 归因不许甩给包：这句话要说清是本程序按设置里的下限主动拦下的。
        Assert.DoesNotContain("包", hit);
    }

    /// <summary>
    /// B-2026-043 的对照：同一个「盘满」的桩下，用户明确说不管剩多少时监控关闭，命令照跑。
    /// 没有这一条，一个「见到盘小就中止」的实现也能过上一条。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-043")]
    public async Task VolumeBomb_MustNotBeWatchedWhenTheUserSetNoFloor()
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

    /// <summary>
    /// B-2026-072：产物目录的创建没有任何异常边界，任何失败都会冒泡到上层的跑批循环，
    /// 最坏的结果是整批中断、状态停在「运行中」。
    ///
    /// 这里真触发：产物目录撞一个已存在的**文件**，得到 WinError 183。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-072")]
    public async Task OutDirBlockedByAFile_MustFailWithAReasonInsteadOfThrowing()
    {
        using var output = new TestDirectory();
        var blocker = output.Combine("我本来是个文件.txt");
        await File.WriteAllTextAsync(blocker, "别动我");
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("ok-small.zip"),
            blocker,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip });

        Assert.True(result.OutDirFailed);
        Assert.False(result.Ok);
        Assert.Equal(EngineExitCodes.OutDirFailedCode, result.Code);

        // 结论走中文那一支，退出码只留在 Code 上可查：这个数是本程序自己记的，
        // 写成「退出码 -4」用户拿去查引擎的码表查不到。
        Assert.Contains("输出目录创建失败", result.Brief);
        Assert.DoesNotContain("退出码 -4", result.Brief);
        Assert.Contains("输出目录创建失败", result.Output);
        Assert.Contains(blocker, result.Output);
        Assert.Equal("别动我", await File.ReadAllTextAsync(blocker));
    }

    /// <summary>同一个失败发生在两级目录之外时也一样：撞文件的是路径中段。</summary>
    [Fact]
    [Trait("Bug", "B-2026-072")]
    public async Task OutDirBlockedByAFileInTheMiddle_MustAlsoBeCaught()
    {
        using var output = new TestDirectory();
        var blocker = output.Combine("中段.txt");
        await File.WriteAllTextAsync(blocker, "别动我");
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("ok-small.zip"),
            Path.Combine(blocker, "再一层"),
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip });

        Assert.True(result.OutDirFailed);
        Assert.Contains("输出目录创建失败", result.Output);
        Assert.Equal("别动我", await File.ReadAllTextAsync(blocker));
    }
}
