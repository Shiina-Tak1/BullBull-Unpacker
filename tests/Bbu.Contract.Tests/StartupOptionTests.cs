using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 启动开关表：每个开关都要有人读。解析了没人用的开关看着像功能，其实什么也没做 ——
/// <c>--seed</c> 自 R1 起没有消费者（B-2026-114）。
/// </summary>
public sealed partial class StartupOptionTests
{
    /// <summary>读取点必须在 <c>StartupOptions</c> 之外：只解析不读等于没有这个开关。</summary>
    [Fact]
    [Trait("Bug", "B-2026-114")]
    public void EverySwitch_MustHaveAConsumer()
    {
        var options = Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "StartupOptions.cs");
        var consumers = string.Join(
            "\n",
            Repo.Files(Repo.Src, ".cs")
                .Where(path => !string.Equals(path, options, StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        var missing = Switches()
            .Matches(File.ReadAllText(options))
            .Select(match => match.Groups[1].Value)
            .Where(name => !consumers.Contains($"StartupOptions.{name}", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, $"这些开关解析了没人用：{string.Join(", ", missing)}");
    }

    /// <summary>
    /// 可以给多次的开关必须收全。<c>--drop=</c> 只取第一个的话，一次排五个任务时后四个
    /// 静默丢掉 —— 而「清单里同时有已完成 / 进行中 / 排队中」正是一次多个的应用。
    /// 复数名与逐个收值两处都要在：只改名字不改取值，照样只留下第一个。
    /// </summary>
    [Fact]
    public void RepeatableSwitch_MustCollectEveryValue()
    {
        var options = Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "StartupOptions.cs");
        var text = File.ReadAllText(options);

        Assert.Contains("IReadOnlyList<string> DropPaths", text, StringComparison.Ordinal);
        Assert.Contains("DropPaths = Values(args, \"--drop=\")", text, StringComparison.Ordinal);

        // 单数属性名 + 单值取值是要拦住的那一种写法：它只认第一个 --drop=。
        Assert.DoesNotContain("DropPath = Value(", text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"public static [A-Za-z0-9_<>?()., ]+ ([A-Za-z0-9_]+) \{ get; set; \}")]
    private static partial Regex Switches();
}
