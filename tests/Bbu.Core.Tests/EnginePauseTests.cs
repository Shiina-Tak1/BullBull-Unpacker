using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 引擎的暂停，
/// 以及枚举心跳报出去的秒数要扣掉停住的那一段（B-2026-305）。
/// </summary>
[Collection("engine")]
public sealed class EnginePauseTests
{
    /// <summary>
    /// 挂起的那一段不算进超时：挂得比时限还久也不许被判超时。
    /// 同时钉住「真的挂上了」：引擎那两条回执是逐实例的排错证据，只进排错档。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-304")]
    public async Task Pause_MustNotCountTowardTheTimeout()
    {
        var log = new RecordingLog();
        var engine = TestPaths.NewEngine(log, timeoutSeconds: 2.0);
        var paused = true;
        using var cancellation = new CancellationTokenSource();

        var running = engine.RunAsync(
            TestPaths.LongRunningCommand,
            EngineKind.SevenZip,
            new EngineRunOptions { Pause = () => paused },
            cancellation.Token);

        await Task.Delay(3500);
        paused = false;
        await Task.Delay(400);
        await cancellation.CancelAsync();
        var result = await running;

        Assert.False(result.TimedOut, "挂起的那一段被算进了超时");
        Assert.True(result.Cancelled);
        Assert.Contains(log.Debugs, line => line.Contains("已挂起引擎进程", StringComparison.Ordinal));
        Assert.Contains(log.Debugs, line => line.Contains("已继续", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Infos, line => line.Contains("已挂起引擎进程", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Infos, line => line.Contains("已继续", StringComparison.Ordinal));
    }

    /// <summary>
    /// 多条引擎生产线吃同一个暂停沿：每一条各留一份排错证据，而用户可见那一档里一条回执都没有。
    /// 撤掉降级即红 —— 每个实例各冒一条进 <see cref="RecordingLog.Infos"/>，面板正是这样被刷满的。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-304")]
    public async Task PauseReceipt_MustStayOffTheUserChannelWithManyEngineInstances()
    {
        const int lanes = 8;
        var log = new RecordingLog();
        var paused = true;
        using var cancellation = new CancellationTokenSource();

        var engines = Enumerable.Range(0, lanes)
            .Select(_ => TestPaths.NewEngine(log, timeoutSeconds: 30).RunAsync(
                TestPaths.LongRunningCommand,
                EngineKind.SevenZip,
                new EngineRunOptions { Pause = () => paused },
                cancellation.Token))
            .ToArray();

        await Task.Delay(3500);
        paused = false;
        await Task.Delay(600);
        await cancellation.CancelAsync();
        await Task.WhenAll(engines);

        Assert.DoesNotContain(log.Infos, line => line.Contains("已挂起引擎进程", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Infos, line => line.Contains("已继续", StringComparison.Ordinal));

        // 每个实例恰好一条：实例有几个这一档就有几行 —— 用户可见那一档因此一条都不能收。
        Assert.Equal(lanes, log.Debugs.Count(line => line.Contains("已挂起引擎进程", StringComparison.Ordinal)));
        Assert.Equal(lanes, log.Debugs.Count(line => line.Contains("已继续", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 暂停要真停：挂起期间目标进程的处理器时间不再增长。
    /// 只看「函数返回了」不足以证明停住了 —— 那个在干等时同样成立。
    /// 盯的是**这一趟引擎起的那一个进程号**，由引擎起来时回调交回来。
    /// </summary>
    [Fact, Trait("Category", "edge")]
    public async Task Pause_MustReallySuspendTheEngineProcess()
    {
        var engine = TestPaths.NewEngine(timeoutSeconds: 120);
        var paused = false;
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = engine.RunAsync(
            TestPaths.LongRunningCommand,
            EngineKind.SevenZip,
            new EngineRunOptions { Pause = () => paused, OnProcessStarted = id => started.TrySetResult(id) },
            cancellation.Token);

        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var process = Process.GetProcessById(pid);

        var beforePause = process.TotalProcessorTime;
        await Task.Delay(800);
        var runningDelta = (process.TotalProcessorTime - beforePause).TotalMilliseconds;

        paused = true;
        await Task.Delay(500);
        var suspendAt = process.TotalProcessorTime;
        await Task.Delay(900);
        var pausedDelta = (process.TotalProcessorTime - suspendAt).TotalMilliseconds;

        paused = false;
        await cancellation.CancelAsync();
        _ = await running;

        Assert.True(runningDelta > 20, "没挂起时进程的处理器时间本来应该在涨：" + runningDelta);
        Assert.True(pausedDelta < runningDelta, "挂起期间进程还在跑：" + pausedDelta + " 对 " + runningDelta);
    }

    /// <summary>挂起一个不存在的进程号不会抛：它只回「失败」，调用方照样往下走。</summary>
    [Fact]
    public void SuspendAnUnknownProcess_MustFailWithoutThrowing()
    {
        Assert.False(ProcessSuspend.Set(-1, true));
        Assert.False(ProcessSuspend.Set(-1, false));
    }

    /// <summary>
    /// 身份读不出来时不许动手：那正是「这个号码现在是谁」判不出来的情形，动了就是拿号码去挂无关进程。
    /// 撤掉复检即红 —— 这里会真把子进程挂住，它的处理器时间不再增长（B-2026-216）。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-216")]
    public async Task Suspend_MustRefuseWhenTheProcessIdentityIsUnreadable()
    {
        using var child = StartQuietLongRun();
        try
        {
            // 生产路径靠同一个进程的两次读数比对身份，读数本身必须稳定，否则挂上之后恢复不了。
            Assert.Equal(StartTimeOf(child.Id), StartTimeOf(child.Id));

            Assert.False(ProcessSuspend.Set(child.Id, true, _ => null));

            var before = child.TotalProcessorTime;
            await Task.Delay(600);
            Assert.True((child.TotalProcessorTime - before).TotalMilliseconds > 20, "身份读不出来时子进程被挂住了");

            // 反方向：没挂过时恢复必须回假。
            Assert.False(ProcessSuspend.Set(child.Id, false, _ => child.StartTime));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// 表里留着旧键、而进程号已被复用：不许回报「已挂」（它其实没挂），也不许拿旧键去恢复别的进程。
    /// 撤掉身份复检即红（B-2026-216）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-216")]
    public async Task Suspend_MustNotReportHeldForAReusedProcessId()
    {
        using var child = StartQuietLongRun();
        try
        {
            var recorded = child.StartTime;
            var reused = recorded.AddSeconds(1);

            Assert.True(ProcessSuspend.Set(child.Id, true, _ => recorded));
            Assert.False(ProcessSuspend.Set(child.Id, true, _ => reused));
            Assert.False(ProcessSuspend.Set(child.Id, false, _ => reused));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    /// <summary>一个安静跑十一秒的子进程：只用来验挂起，输出全关掉，免得管道缓冲把它堵住。</summary>
    private static Process StartQuietLongRun() =>
        Process.Start(new ProcessStartInfo(TestPaths.BundledSevenZip)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            ArgumentList = { "b", "-mmt1", "-md=24", "-bso0", "-bsp0" },
        })!;

    /// <summary>每次新建进程对象，读的是内核里那一刻的真值，不是上一次的缓存。</summary>
    private static DateTime StartTimeOf(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime;
    }

    /// <summary>
    /// 停住的时长只在观测点上累计：反复问「还停着吗」不把同一段算两遍，恢复那一刻结算一次。
    /// 判据是纯逻辑，把时刻传进去就判得了（B-2026-305）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-305")]
    public void PauseHold_MustCountEachStretchOnce()
    {
        var hold = new PauseHold();

        Assert.True(hold.Observe(true, 1_000));
        Assert.True(hold.Observe(true, 3_000));
        Assert.Equal(TimeSpan.FromSeconds(2), hold.Held(3_000));

        Assert.False(hold.Observe(false, 4_000));
        Assert.Equal(TimeSpan.FromSeconds(3), hold.Held(9_000));

        Assert.True(hold.Observe(true, 10_000));
        Assert.False(hold.Observe(false, 11_500));
        Assert.Equal(TimeSpan.FromSeconds(4.5), hold.Held(20_000));
    }

    /// <summary>
    /// 心跳报出去的秒数要扣掉停住的那一段：不扣的话恢复后一次跳增，把暂停补进「已耗时」（B-2026-305）。
    /// 撤掉抵扣即红 —— 同样两个实参传进去，报出来的是含暂停的墙钟。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-305")]
    public void SearchHeartbeat_MustDiscountTheHeldStretch()
    {
        Assert.Equal(2.0, Vault.SearchedSeconds(nowMs: 12_000, startedMs: 0, held: TimeSpan.FromSeconds(10)));
        Assert.Equal(12.0, Vault.SearchedSeconds(nowMs: 12_000, startedMs: 0, held: TimeSpan.Zero));
    }

    /// <summary>
    /// 两条路（串行与并发）报出去的秒数都扣掉暂停那一段：暂停期间枚举停在候选边界上，那不算「已耗时」。
    /// 撤掉观测点（改回直接问 options.Pause）即红 —— 报出来的秒数会把暂停补进去（B-2026-305）。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-305")]
    public async Task SearchHeartbeat_MustDiscountTheHeldStretchOnBothPaths()
    {
        var serial = await SearchWithPauseAsync(booked: 1);
        var parallel = await SearchWithPauseAsync(booked: 30);

        Assert.True(serial.Candidates < Vault.ParallelMinCandidates, $"串行那趟的候选数没落在线下：{serial.Candidates}");
        Assert.True(parallel.Candidates >= Vault.ParallelMinCandidates, $"并发那趟的候选数没到线上：{parallel.Candidates}");

        Assert.True(serial.Reported < serial.Wall - HeldSeconds * 0.8,
            $"串行路报出来的秒数没扣掉暂停：报 {serial.Reported:0.##}，墙钟 {serial.Wall:0.##}");
        Assert.True(parallel.Reported < parallel.Wall - HeldSeconds * 0.8,
            $"并发路报出来的秒数没扣掉暂停：报 {parallel.Reported:0.##}，墙钟 {parallel.Wall:0.##}");
    }

    /// <summary>暂停挡住的那一段。够心跳发几条，也够看出扣没扣。</summary>
    private const double HeldSeconds = 1.5;

    /// <summary>假快路判定一个候选花多久：让枚举慢到心跳发得出来，又不真起引擎。</summary>
    private const int SlowJudgeMs = 1500;

    /// <summary>跑一趟带暂停的枚举：暂停挡在第一个候选边界上，恢复之后让它真跑一段再取消。</summary>
    private static async Task<(double Reported, double Wall, int Candidates)> SearchWithPauseAsync(int booked)
    {
        var log = new RecordingLog();
        var vault = new Vault { BookPath = null };
        for (var index = 0; index < booked; index++)
        {
            vault.Add("candidate-" + index);
        }

        var paused = true;
        using var cancellation = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var running = vault.UnlockAsync(
            TestPaths.NewEngine(log),
            TestPaths.Fixture(Path.Combine("vault", "zip-zipcrypto.zip")),
            new UnlockOptions { Workers = 4, Pause = () => paused },
            new SlowFastPath(),
            cancellation.Token);

        await Task.Delay((int)(HeldSeconds * 1000));
        paused = false;
        await Task.Delay((int)(HeldSeconds * 1000) + 300);
        await cancellation.CancelAsync();
        await running;

        var wall = clock.Elapsed.TotalSeconds;
        Assert.NotEmpty(log.ProgressFacts);
        return (log.ProgressFacts[^1].Seconds, wall, log.ProgressFacts[^1].Total);
    }

    /// <summary>判什么都说「肯定不对」，于是这一趟不起引擎；判定本身慢，好让心跳发得出来。</summary>
    private sealed class SlowFastPath : IFastPath
    {
        public IFastPathSession? Open(string archive) => new Session();

        private sealed class Session : IFastPathSession
        {
            public FastPathVerdict Judge(string password)
            {
                Thread.Sleep(SlowJudgeMs);
                return FastPathVerdict.ProbablyWrong;
            }

            public void Dispose()
            {
            }
        }
    }
}
