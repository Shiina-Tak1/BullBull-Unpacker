using System;
using System.Collections.Generic;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 计数与进度分子的口径：六种状态各报各的，谁都不折进别人；部分完成算已出结论。
/// 两件事错了都不报错，只是数字不对，而用户会照着那个数字决定要不要删掉已解好的产物。
/// </summary>
public sealed class RunnerCountTests
{
    private static Job Item(JobState state, bool runnable = true) => new()
    {
        ArchivePath = $@"C:\demo\{state}.zip",
        Kind = "zip",
        DisplayName = $"{state}.zip",
        State = state,
        Runnable = runnable,
    };

    /// <summary>六种状态各报各的，谁都不折进别人。</summary>
    [Fact]
    [Trait("Bug", "B-2026-023")]
    public void Summarize_MustNotFoldSkippedOrPartialIntoFailed()
    {
        var items = Enum.GetValues<JobState>().Select(state => Item(state)).ToList();

        var counts = Runner.Summarize(items);

        foreach (var state in Enum.GetValues<JobState>())
        {
            Assert.Equal(1, counts[state]);
        }

        // 这条就是本条的回归：把跳过与部分完成加回「失败」那一格，上面那两条立刻变红
        Assert.Equal(1, counts[JobState.Failed]);
        Assert.Equal(1, counts[JobState.Skipped]);
        Assert.Equal(1, counts[JobState.Partial]);
    }

    /// <summary>每个状态都必须有一个键，包括计数为零的：漏一个键就是无保护累加崩掉整批。</summary>
    [Fact]
    [Trait("Bug", "B-2026-023")]
    public void Summarize_MustHaveAKeyForEveryStateEvenAtZero()
    {
        var counts = Runner.Summarize([Item(JobState.Done)]);

        foreach (var state in Enum.GetValues<JobState>())
        {
            Assert.True(counts.ContainsKey(state), state.ToString());
        }
    }

    /// <summary>汇总行只统计可运行项，文件夹汇总行不进账。</summary>
    [Fact]
    [Trait("Bug", "B-2026-023")]
    public void Summarize_MustSkipRowsThatNeverRun()
    {
        var counts = Runner.Summarize([Item(JobState.Done), Item(JobState.Failed, runnable: false)]);

        Assert.Equal(1, counts[JobState.Done]);
        Assert.Equal(0, counts[JobState.Failed]);
    }

    /// <summary>哪几个状态算已经出了结论。部分完成也算，它不重跑。</summary>
    [Fact]
    [Trait("Bug", "B-2026-028")]
    public void Settled_MustIncludePartial()
    {
        Assert.True(Runner.IsSettled(JobState.Done));
        Assert.True(Runner.IsSettled(JobState.Partial));
        Assert.True(Runner.IsSettled(JobState.Skipped));
        Assert.True(Runner.IsSettled(JobState.Failed));
        Assert.False(Runner.IsSettled(JobState.Queued));
        Assert.False(Runner.IsSettled(JobState.Running));
    }

    /// <summary>
    /// 一批跑完之后还接不接着开下一批：只有「排队中」那一档算，失败的不算 ——
    /// 把失败也算进来，一个永远解不开的包会自己一轮一轮地重试成死循环。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-249")]
    public void HasQueued_MustOnlyCountTheQueuedBucket()
    {
        Assert.True(Runner.HasQueued([Item(JobState.Queued)]));
        Assert.True(Runner.HasQueued([Item(JobState.Done), Item(JobState.Queued)]));

        Assert.False(Runner.HasQueued([Item(JobState.Failed)]));
        Assert.False(Runner.HasQueued([Item(JobState.Done), Item(JobState.Partial), Item(JobState.Skipped)]));
        Assert.False(Runner.HasQueued([Item(JobState.Queued, runnable: false)]));
        Assert.False(Runner.HasQueued([]));
    }

    /// <summary>
    /// 进度分子认哪几档。把部分完成从这一档里去掉，含它的那一批就会从一半直接跳到满。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-028")]
    public void SettledCount_MustWalkEverySettledState()
    {
        var batch = new List<Job> { Item(JobState.Queued), Item(JobState.Queued), Item(JobState.Queued) };

        Assert.Equal(0, Settled(batch));

        batch[0].State = JobState.Done;
        Assert.Equal(1, Settled(batch));

        // 部分完成也出了结论：漏掉它，这一批会卡在三分之一，直到最后一项才跳满
        batch[1].State = JobState.Partial;
        Assert.Equal(2, Settled(batch));

        batch[2].State = JobState.Failed;
        Assert.Equal(3, Settled(batch));

        static int Settled(IReadOnlyList<Job> items) => items.Count(item => Runner.IsSettled(item.State));
    }
}
