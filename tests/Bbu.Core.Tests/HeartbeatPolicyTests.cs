using Bbu.Core.Logging;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 枚举心跳的两条边界：跑不到一个间隔不许发，秒数四舍五入成 0 的那一条也不许发。
/// 破了它的后果是日志文件与活动槽被同一条噪音刷满，而没有任何一处会报错。
/// </summary>
public sealed class HeartbeatPolicyTests
{
    /// <summary>不足一个间隔不算到期，达到或超过才算；把 <c>&gt;=</c> 写成 <c>&gt;</c> 时这条断言失败。</summary>
    [Theory]
    [InlineData(HeartbeatPolicy.SearchSeconds)]
    public void Due_MustWaitTheFullInterval(double interval)
    {
        Assert.False(HeartbeatPolicy.Due(interval - 0.01, interval));
        Assert.True(HeartbeatPolicy.Due(interval, interval));
    }

    /// <summary>显示成「已耗时 0 秒」的那一条什么也没说，不该发。</summary>
    [Fact]
    public void Meaningful_MustRefuseASecondThatRoundsToZero()
    {
        Assert.False(HeartbeatPolicy.Meaningful(0));
        Assert.False(HeartbeatPolicy.Meaningful(0.4));
        Assert.True(HeartbeatPolicy.Meaningful(1));
    }
}
