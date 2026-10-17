using System;
using System.Collections.Generic;
using System.Linq;

namespace Bbu.Core.Launching;

/// <summary>
/// 命令行参数里哪些是"要处理的路径"。
///
/// 判据只到这里：双横线开头的算本程序自己的开关，其余按路径收，两端引号与空白先剥掉。
/// 通配展开、跳过 <c>.lnk</c>、攒批去重留给穿透那一轮（见 1.1.0 的 <c>launchargs.py</c>）。
/// </summary>
public static class LaunchArguments
{
    public static IReadOnlyList<string> Paths(string[] args) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Select(Unquote)
            .Where(a => a.Length > 0)
            .ToList();

    /// <summary>资源管理器传过来的路径可能带引号。</summary>
    private static string Unquote(string raw) => raw.Trim().Trim('"').Trim();
}
