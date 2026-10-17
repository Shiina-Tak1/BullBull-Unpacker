using System;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 输出收尾的那一步有上限：杀不掉的子进程会把无限等的收尾拖成永久挂起（外部审计第 1 轮 `D-5`）。
///
/// 真机上造不出「进程杀不掉、管道也不关」的替身 —— 本机试过的两种子进程都会在被杀时把管道交回去 ——
/// 所以判据下沉成纯函数：一个永不完成的收尾任务必须在上限内被判成「没等到」。
/// 调用点真接上了它，由 ` ` 那条真起进程的断言承担。
/// </summary>
public sealed class EngineOutputDrainTests
{
    /// <summary>撤掉上限即红：撤掉之后「没等到」那一档会一直等下去，本类第一条断言因此超时变红。</summary>
    [Fact, Trait("Bug", "B-2026-213")]
    public async Task DrainedAsync_MustGiveUpAtItsDeadline()
    {
        var never = new TaskCompletionSource<string[]>();
        var waiting = SevenZipEngine.DrainedAsync(never.Task, TimeSpan.FromMilliseconds(50));

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(never.Task.IsCompleted);
    }

    /// <summary>反向那半：收尾真到了就必须算「等到了」，否则结果里的输出会被整段丢掉。</summary>
    [Fact, Trait("Bug", "B-2026-213")]
    public async Task DrainedAsync_MustSeeAFinishedDrain()
    {
        var done = Task.FromResult(new[] { "ok" });
        Assert.True(await SevenZipEngine.DrainedAsync(done, TimeSpan.FromMilliseconds(50)));
    }
}
