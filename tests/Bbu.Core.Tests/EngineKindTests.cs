using System;
using System.IO;
using System.Linq;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 引擎成员的穷举门禁。
///
/// `EngineKind` 的消费点是开放式的（`EngineCommand` 是 `if…else`，`EngineExitCodes` 与 `EngineRouter`
/// 是三元），加第三个成员会**静默**落进各自的 else 支 —— 只显示错、不变红的不算看住
/// 这一条把成员集合钉死：加成员即红，逼人显式改清单与那几处分支。
/// </summary>
public sealed class EngineKindTests
{
    [Fact]
    public void EngineKind_MustStayExhaustive()
    {
        Assert.Equal(
            new[] { EngineKind.SevenZip, EngineKind.UnRar },
            Enum.GetValues<EngineKind>());
    }

    /// <summary>
    /// 每个成员都要真的走出自己那条命令行与退出码表：两个引擎的参数形状不同（7z 用 `-o` 指目录、
    /// UnRAR 把目录当位置参数并带尾分隔符），落到同一支就说明分派写错了。
    /// 退出码那一侧用 14 探 —— 它只在 UnRAR 的表里，SevenZip 侧必须回「未知退出码 14」；
    /// 用 0 探不出东西：两张表都把 0 定义成「成功」，任何一支都返回它。
    /// </summary>
    [Fact]
    public void EveryKind_MustBuildItsOwnCommandLine()
    {
        var extract = Enum.GetValues<EngineKind>()
            .ToDictionary(kind => kind, kind => EngineCommand.Extract(kind, "x.exe", "a.rar", "out", null));

        Assert.All(extract.Values, command => Assert.NotEmpty(command));
        Assert.Contains("-oout", extract[EngineKind.SevenZip]);
        Assert.Contains("out" + Path.DirectorySeparatorChar, extract[EngineKind.UnRar]);

        Assert.Equal(Errors.ExitDelete, EngineExitCodes.Describe(14, EngineKind.UnRar));
        Assert.Contains("未知", EngineExitCodes.Describe(14, EngineKind.SevenZip), StringComparison.Ordinal);
    }
}
