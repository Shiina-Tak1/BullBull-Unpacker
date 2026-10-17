using System;

namespace Bbu.Core.Logging;

/// <summary>心跳的两条边界：间隔多长，以及哪一条值得发。</summary>
public static class HeartbeatPolicy
{
    /// <summary>密码枚举的心跳间隔。判据是「界面秒表要连续地走」：候选每秒能试上千个，间隔一长秒表就一跳一跳。</summary>
    public const double SearchSeconds = 0.1;

    /// <summary>距上一条的间隔是否已经满了。</summary>
    public static bool Due(double sinceLastSeconds, double interval) => sinceLastSeconds >= interval;

    public static bool Meaningful(double seconds) => Math.Round(seconds) >= 1;
}
