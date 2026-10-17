using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 枚举调度的两条生产线。顺序结算不许变，
/// 快的候选不许为慢的候选让路。全部用注入的桩，不起引擎、不碰夹具，所以进默认档。
/// </summary>
public sealed class CandidateSweepTests
{
    private const int Count = 40;

    private static EngineResult Wrong() => new() { Code = 7, WrongPassword = true };

    private static IReadOnlyList<PasswordCandidate> Book(int count = Count) =>
        [.. Enumerable.Range(0, count).Select(i => new PasswordCandidate($"pw-{i:0000}", PasswordOrigin.Book))];

    [Fact]
    public async Task Outcome_MustFollowTheCandidateOrder()
    {
        // 引擎那一条路故意让后一个先回来：结论仍须按候选下标出来。
        var seen = new List<string>();
        var sweep = new CandidateSweep(
            Book(4),
            null,
            async (candidate, _) =>
            {
                await Task.Delay((4 - int.Parse(candidate.Value[3..])) * 30).ConfigureAwait(false);
                return Wrong();
            },
            4,
            CancellationToken.None);

        await foreach (var outcome in sweep.RunAsync(CancellationToken.None))
        {
            seen.Add(outcome.Candidate.Value);
        }

        Assert.Equal(["pw-0000", "pw-0001", "pw-0002", "pw-0003"], seen);
    }

    /// <summary>
    /// 判定卡在慢 Judge 里时取消：产线收尾的 <see cref="CandidateSweep"/> 那一步还没轮到跑，
    /// 没填的槽保持没填，而消费者正在等槽 —— 取消只能由取值处接住（B-2026-172）。
    /// 慢判定是这条断言成立的条件：判定一返回，产线收尾就会把没填的槽填上取消结论，
    /// 那时候撤掉取值处的接住也不会红。
    /// </summary>
    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-172")]
    public async Task CancelledWhileALaneIsBusy_MustComeBackAsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var judging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = new CandidateSweep(
            Book(8),
            new CountingSession(() => judging.TrySetResult(), sleepMs: 300),
            (candidate, _) => Task.FromResult(Wrong()),
            4,
            cancellation.Token);

        var seen = new List<EngineResult>();
        var run = Task.Run(async () =>
        {
            await foreach (var outcome in sweep.RunAsync(cancellation.Token))
            {
                seen.Add(outcome.Result);
            }
        });

        await judging.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEmpty(seen);
        Assert.True(seen[0].Cancelled, "取消之后取到的第一条结论不是取消");
    }

    [Fact, Trait("Bug", "B-2026-147")]
    public async Task EngineCandidate_MustNotHoldBackTheFastLane()
    {
        // 第一个候选卡在引擎那一条路上：快路这边必须照旧把整本判完。
        // 按窗口推进时判定会停在这一窗上，这一条就会红。
        var judged = 0;
        var book = Book();
        var release = new TaskCompletionSource<EngineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = new CandidateSweep(
            book,
            new CountingSession(() => Interlocked.Increment(ref judged)),
            (candidate, _) =>
            {
                if (candidate.Value == "pw-0000")
                {
                    reached.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult(Wrong());
            },
            4,
            CancellationToken.None);

        var consume = Task.Run(async () =>
        {
            await foreach (var outcome in sweep.RunAsync(CancellationToken.None))
            {
                _ = outcome;
            }
        });

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var done = await WaitUntilAsync(() => Volatile.Read(ref judged) >= book.Count, TimeSpan.FromSeconds(5));
        release.TrySetResult(Wrong());
        await consume;

        Assert.True(done, $"引擎候选卡住时快路只判了 {judged}/{book.Count} 个");
    }

    /// <summary>
    /// 上一条为什么算数：把**旧结构**（窗按 1 → 2 → 4 放大、窗内发起、整窗按序收拢）
    /// 照原样摆在这里，同一条断言在它上面必须做不到 —— 一个候选卡在引擎上，整窗就都不往下走。
    /// 这一条是「撤掉修复会红」的可执行证据，不随产品代码改动而失效。
    /// </summary>
    [Fact]
    public async Task LegacyWindowShape_CouldNotWalkTheBookWhileOneCandidateStalled()
    {
        var judged = 0;
        var book = Book();
        var release = new TaskCompletionSource<EngineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new CountingSession(() => Interlocked.Increment(ref judged));

        async Task<EngineResult> AttemptAsync(int index)
        {
            if (session.Judge(book[index].Value) is FastPathVerdict.Unknown)
            {
                return book[index].Value == "pw-0000" ? await release.Task : Wrong();
            }

            return Wrong();
        }

        var consume = Task.Run(async () =>
        {
            var at = 0;
            var width = 1;
            while (at < book.Count)
            {
                var take = Math.Min(width, book.Count - at);
                var window = new Task<EngineResult>[take];
                for (var i = 0; i < take; i++)
                {
                    window[i] = AttemptAsync(at + i);
                }

                for (var i = 0; i < take; i++)
                {
                    await window[i].ConfigureAwait(false);
                }

                at += take;
                width = Math.Min(width * 2, 16);
            }
        });

        await Task.Delay(500);
        var stalled = Volatile.Read(ref judged);
        release.TrySetResult(Wrong());
        await consume;

        Assert.True(stalled < book.Count, $"旧结构也判完了 {stalled}/{book.Count} 个，上一条断言失去意义");
    }

    [Fact]
    public async Task Break_MustStopBothLanes()
    {
        var judged = 0;
        var sweep = new CandidateSweep(
            Book(4000),
            new CountingSession(() => Interlocked.Increment(ref judged)),
            (_, _) => Task.FromResult(Wrong()),
            4,
            CancellationToken.None);

        var taken = 0;
        await foreach (var outcome in sweep.RunAsync(CancellationToken.None))
        {
            _ = outcome;
            if (++taken == 8)
            {
                break;
            }
        }

        // break 之后 RunAsync 的收尾已经 join 过两条生产线，判定数不该再动。
        var after = Volatile.Read(ref judged);
        await Task.Delay(100);
        Assert.Equal(after, Volatile.Read(ref judged));
    }

    [Fact]
    public async Task CancelledToken_MustEndWithoutWaitingForTheRemainingSlots()
    {
        using var cts = new CancellationTokenSource();
        var sweep = new CandidateSweep(
            Book(),
            null,
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                return Wrong();
            },
            4,
            cts.Token);

        var consume = Task.Run(async () =>
        {
            await foreach (var outcome in sweep.RunAsync(cts.Token))
            {
                _ = outcome;
            }
        });

        await Task.Delay(50);
        cts.Cancel();

        var finished = await Task.WhenAny(consume, Task.Delay(TimeSpan.FromSeconds(10))) == consume;
        Assert.True(finished, "取消之后没有收工");
    }

    /// <summary>
    /// 暂停停在取活之前，继续之后一个候选都不漏（B-2026-149）。
    /// 判定是进程内的 CPU 活，不拦住生产线，「暂停」就只是把引擎进程挂起来 ——
    /// 撤掉 Lane 那一段等待即红。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-149")]
    public async Task Pause_MustStopTheLanesAndResumeCleanly()
    {
        var judged = 0;
        var paused = 0;
        var book = Book(4000);
        var sweep = new CandidateSweep(
            book,
            new CountingSession(() => Interlocked.Increment(ref judged), sleepMs: 1),
            (_, _) => Task.FromResult(Wrong()),
            4,
            CancellationToken.None,
            () => Volatile.Read(ref paused) == 1);

        var consume = Task.Run(async () =>
        {
            await foreach (var outcome in sweep.RunAsync(CancellationToken.None))
            {
                _ = outcome;
            }
        });

        await WaitUntilAsync(() => Volatile.Read(ref judged) > 0, TimeSpan.FromSeconds(5));

        Volatile.Write(ref paused, 1);
        await Task.Delay(150);
        var at = Volatile.Read(ref judged);
        await Task.Delay(250);
        Assert.Equal(at, Volatile.Read(ref judged));

        Volatile.Write(ref paused, 0);
        await consume;
        Assert.Equal(book.Count, Volatile.Read(ref judged));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>只记调用次数、一律回「不知道」的桩快路。<paramref name="sleepMs"/> 用来把判定拉慢，
    /// 让「暂停」这类时序断言有窗口可测。</summary>
    private sealed class CountingSession(Action onJudge, int sleepMs = 0) : IFastPathSession
    {
        public FastPathVerdict Judge(string password)
        {
            onJudge();
            if (sleepMs > 0)
            {
                Thread.Sleep(sleepMs);
            }

            return FastPathVerdict.Unknown;
        }

        public void Dispose()
        {
        }
    }
}
