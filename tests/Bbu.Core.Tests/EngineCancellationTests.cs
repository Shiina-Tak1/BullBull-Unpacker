using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 取消与超时。取消要真杀，不许等它自己跑完；
/// 超时默认 1800 秒。
///
/// 断言的对象是**进程真的没了**，不是「函数返回了一个 bool」：启动后干等的写法也能返回
/// 「已停止」，而后台那个 7z 还在写盘。
///
/// 被断言的是**这一趟引擎起的那一个进程号**，由引擎起来时回调交回来。
/// </summary>
[Collection("engine")]
public sealed class EngineCancellationTests
{
    [Fact, Trait("Category", "edge")]
    public async Task Cancel_MustKillTheEngineProcess()
    {
        var engine = TestPaths.NewEngine(timeoutSeconds: 120);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = engine.RunAsync(
            TestPaths.LongRunningCommand,
            EngineKind.SevenZip,
            new EngineRunOptions { OnProcessStarted = id => started.TrySetResult(id) },
            cancellation.Token);

        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();
        var result = await running;

        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
        Assert.False(result.TimedOut);
        Assert.True(result.Seconds < 60, "取消后没有及时返回");
        Assert.False(IsAlive(pid), "取消之后引擎进程还活着");
    }

    [Fact, Trait("Category", "edge")]
    public async Task Timeout_MustKillTheEngineProcess()
    {
        var engine = TestPaths.NewEngine(timeoutSeconds: 1.0);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await engine.RunAsync(
            TestPaths.LongRunningCommand,
            EngineKind.SevenZip,
            new EngineRunOptions { OnProcessStarted = id => started.TrySetResult(id) });

        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
        Assert.False(result.Cancelled);
        Assert.True(result.Seconds < 60, "超时后没有及时返回");

        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(IsAlive(pid), "超时之后引擎进程还活着");
    }

    [Fact]
    public async Task AlreadyCancelledToken_MustNotStartAnything()
    {
        var engine = TestPaths.NewEngine(timeoutSeconds: 120);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await engine.RunAsync(TestPaths.LongRunningCommand, EngineKind.SevenZip, null, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
    }

    /// <summary>这一个进程号还在不在。已经退出的号码查不到，算它不在了。</summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
