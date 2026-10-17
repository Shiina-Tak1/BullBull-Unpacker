using Bbu.Core.Logging;
using Bbu.Core.Models;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 结果行的两张表：日志那一行按停止原因取档、清单那一格按状态取档。
///
/// 分开钉是因为两者判据不同：日志行问「用户要不要为它做点什么」，清单格说「这一项是什么状态」，
/// 两个出口取不同档是有意的（「输出目录已存在、按设置跳过」在清单是提示档、在日志是陈述档）。
/// </summary>
public sealed class ResultLevelTests
{
    [Theory]
    [InlineData(JobState.Done, StopReason.NoArchive, LogLevel.Info)]
    [InlineData(JobState.Done, StopReason.AlreadyVisited, LogLevel.Info)]
    [InlineData(JobState.Done, StopReason.OutputExists, LogLevel.Info)]
    [InlineData(JobState.Done, StopReason.Cancelled, LogLevel.Info)]
    [InlineData(JobState.Done, StopReason.PasswordSkipped, LogLevel.Info)]
    [InlineData(JobState.Done, StopReason.PasswordExhausted, LogLevel.Warn)]
    [InlineData(JobState.Done, StopReason.MaxDepth, LogLevel.Warn)]
    [InlineData(JobState.Done, StopReason.SearchIncomplete, LogLevel.Warn)]
    [InlineData(JobState.Done, StopReason.Ambiguous, LogLevel.Warn)]
    [InlineData(JobState.Done, StopReason.ExtractFailed, LogLevel.Error)]
    [InlineData(JobState.Done, StopReason.EmptyOutput, LogLevel.Error)]
    [InlineData(JobState.Done, StopReason.NoSpace, LogLevel.Error)]
    [InlineData(JobState.Done, StopReason.SpaceGuard, LogLevel.Error)]

    // 有内容没解压：它比停止原因那一条更重，压过 reason 的取值（两个方向各一例）
    [InlineData(JobState.Partial, StopReason.NoArchive, LogLevel.Warn)]
    [InlineData(JobState.Partial, StopReason.ExtractFailed, LogLevel.Warn)]
    public void LevelFor_MustFollowTheStopReason(JobState state, StopReason reason, LogLevel level) =>
        Assert.Equal(level, RunLog.LevelFor(state, reason));

    [Theory]
    [InlineData(JobState.Queued, LogLevel.Info)]
    [InlineData(JobState.Running, LogLevel.Info)]
    [InlineData(JobState.Done, LogLevel.Info)]
    [InlineData(JobState.Failed, LogLevel.Error)]
    [InlineData(JobState.Skipped, LogLevel.Warn)]
    [InlineData(JobState.Partial, LogLevel.Warn)]

    // 枚举的未命名值（外部数据强转来的）按陈述档，不落到失败档
    [InlineData((JobState)99, LogLevel.Info)]
    public void StateLevel_MustFollowTheState(JobState state, LogLevel level) =>
        Assert.Equal(level, RunLog.StateLevel(state));
}
