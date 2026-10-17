using System;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 暂停的判据：挂起没成功就不算暂停（B-2026-197）。
///
/// 引擎那一侧有三条后果，都读同一个结论：日志不说「已挂起」、事中磁盘余量检查照旧生效、
/// 超时不被抵扣。后两条在真机上造不出挂起失败那一档 —— <c>ProcessSuspend.Set</c> 的失败只在
/// 目标进程已消失或权限不足时出现，而那时轮询循环已经结束了 —— 所以判据下沉到这里，
/// 挂起成功那一路由既有的  继续守着，本类不重复。
/// </summary>
public sealed class EnginePausePolicyTests
{
    /// <summary>四格真值表：只有「请求了、而且挂起成功」才算暂停。撤掉那条判据即红。</summary>
    [Fact, Trait("Bug", "B-2026-197")]
    public void CountsAsPaused_MustRequireASuccessfulSuspend()
    {
        Assert.False(EnginePausePolicy.CountsAsPaused(requested: false, suspended: false));
        Assert.False(EnginePausePolicy.CountsAsPaused(requested: false, suspended: true));
        Assert.False(EnginePausePolicy.CountsAsPaused(requested: true, suspended: false));
        Assert.True(EnginePausePolicy.CountsAsPaused(requested: true, suspended: true));
    }

    /// <summary>
    /// 身份复检的四格：进程号与启动时刻这一对两边都读到、且逐刻相等，才算同一个进程。
    /// 进程号会被复用，只按进程号认就会挂到无关进程的线程上；撤掉这条比对即红（B-2026-216）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-216")]
    public void IdentityHolds_MustRequireBothStartTimesAndExactEquality()
    {
        var recorded = new DateTime(2026, 10, 13, 9, 30, 0, DateTimeKind.Local);

        Assert.True(ProcessSuspend.IdentityHolds(recorded, recorded));
        Assert.False(ProcessSuspend.IdentityHolds(recorded, recorded.AddMilliseconds(1)));
        Assert.False(ProcessSuspend.IdentityHolds(null, recorded));
        Assert.False(ProcessSuspend.IdentityHolds(recorded, null));
    }
}
