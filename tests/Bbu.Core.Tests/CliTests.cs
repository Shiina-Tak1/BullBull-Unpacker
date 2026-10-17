using System;
using System.Linq;
using Bbu.Core.Integration;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 入口的参数表。这张表同时是解析的输入与帮助的输出，「帮助里的每一条都有实现」
/// 因此可以机检：每个开关都要真的改变解析结果，否则它只是一行文字。
/// </summary>
public sealed class CliTests
{
    /// <summary>不给任何参数时的解析结果，用来判"这个开关起到了作用"。</summary>
    private static readonly CliOptions Baseline = Cli.Parse([]);

    [Fact]
    public void EveryDocumentedOption_MustChangeTheParseResult()
    {
        foreach (var option in Cli.Documented)
        {
            var parsed = Cli.Parse([option.Name]);
            Assert.Null(parsed.Error);
            Assert.True(
                Fingerprint(parsed) != Fingerprint(Baseline),
                $"{option.Name} 在帮助里，但解析它之后什么都没变：这条没有实现");
        }
    }

    /// <summary>
    /// 解析结果的形状。不直接用记录自带的相等：它里面的路径列表是按引用比的，
    /// 那样每个开关看起来都"变了"。
    /// </summary>
    private static string Fingerprint(CliOptions options) =>
        $"{options.Action}|{options.Auto}|{options.SingleInstanceProbe}|{options.HoldMs}|"
        + $"{string.Join(';', options.Paths)}|{options.Error}";

    [Fact]
    public void EveryDocumentedOption_MustAppearInTheHelpText()
    {
        var help = Cli.HelpText("Bbu.App.exe");
        foreach (var option in Cli.Documented)
        {
            Assert.Contains(option.HelpRow, help, StringComparison.Ordinal);
        }
    }

    /// <summary>自检开关不进帮助：它们不是给用户用的参数。</summary>
    [Fact]
    public void HiddenOptions_MustStayOutOfTheHelpText()
    {
        var help = Cli.HelpText("Bbu.App.exe");
        foreach (var option in Cli.Options.Where(item => item.Hidden))
        {
            Assert.DoesNotContain(option.Name, help, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HelpText_MustCarryAllThreeExitCodes()
    {
        var help = Cli.HelpText("Bbu.App.exe");
        Assert.Equal(0, Cli.ExitDone);
        Assert.Equal(1, Cli.ExitUndone);
        Assert.Equal(2, Cli.ExitUsage);
        foreach (var code in new[] { Cli.ExitDone, Cli.ExitUndone, Cli.ExitUsage })
        {
            Assert.Contains(
                $"  {code}",
                help.Replace(Environment.NewLine, "\n", StringComparison.Ordinal),
                StringComparison.Ordinal);
        }
    }

    /// <summary>版权尾注跟着退出码表出：版权行与 SPDX 标识符各一行，与关于页同源（同一组常量）。</summary>
    [Fact]
    public void HelpText_MustCarryTheCopyrightFooter()
    {
        var help = Cli.HelpText("Bbu.App.exe");

        Assert.Contains(IntegrationText.CliCopyright, help, StringComparison.Ordinal);
        Assert.Contains(IntegrationText.CliLicense, help, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSwitch_MustBeAUsageError()
    {
        var parsed = Cli.Parse(["--nope"]);

        Assert.Equal(CliAction.OpenWindow, parsed.Action);
        Assert.StartsWith("未知开关", parsed.Error ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>界面层那批开关既不进本表也不判未知：它们由界面层自己解析。</summary>
    [Fact]
    public void Passthrough_MustNotBeTreatedAsUnknown()
    {
        var parsed = Cli.Parse(["--light", "--size=880x660", "D:\\a.zip"], ["--light", "--size="]);

        Assert.Null(parsed.Error);
        Assert.Equal(["D:\\a.zip"], parsed.Paths);
    }

    [Fact]
    public void PassThrough_WithAValue_MustNotSwallowTheNextPath()
    {
        // 带值的开关整段写在一个参数里，因此不会把后面的路径当值吃掉。
        var parsed = Cli.Parse(["--drop=D:\\a.zip", "D:\\b.zip"], ["--drop="]);

        Assert.Null(parsed.Error);
        Assert.Equal(["D:\\b.zip"], parsed.Paths);
    }

    [Fact]
    public void Paths_MustKeepTheirOrderAndLoseTheirQuotes()
    {
        var parsed = Cli.Parse(["\"D:\\带 空格\\a.zip\"", "D:\\b.zip"]);

        Assert.Null(parsed.Error);
        Assert.Equal(["D:\\带 空格\\a.zip", "D:\\b.zip"], parsed.Paths);
    }

    [Fact]
    public void Auto_MustBeAToggleAndNotAnAction()
    {
        var parsed = Cli.Parse(["--auto", "D:\\a.zip"]);

        Assert.True(parsed.Auto);
        Assert.Equal(CliAction.OpenWindow, parsed.Action);
    }

    [Fact]
    public void Hold_MustReadItsValue()
    {
        Assert.Equal(1500, Cli.Parse(["--hold=1500"]).HoldMs);
        Assert.Equal(0, Cli.Parse(["--hold=abc"]).HoldMs);
        Assert.Equal(0, Cli.Parse(["--hold=-3"]).HoldMs);
    }

    [Fact]
    public void EveryAction_MustHaveItsOwnOption()
    {
        var covered = Cli.Options.Where(option => !option.Hidden).Select(option => option.Action).ToList();

        Assert.Contains(CliAction.Help, covered);
        Assert.Contains(CliAction.Version, covered);
        Assert.Contains(CliAction.Where, covered);
        Assert.Contains(CliAction.InstallShellMenu, covered);
        Assert.Contains(CliAction.UninstallShellMenu, covered);
    }

    [Fact]
    public void WhereText_MustNameEveryLandingSpot()
    {
        var text = Cli.WhereText(@"D:\data", @"D:\app", "abc123");

        Assert.Contains(@"D:\data", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\app", text, StringComparison.Ordinal);
        Assert.Contains("config.json", text, StringComparison.Ordinal);
        Assert.Contains("abc123", text, StringComparison.Ordinal);

        // 2026-10-07：两个引擎各占一行。它们来源与许可都不同，
        // 缺哪一个的影响也不同：缺 7-Zip 时 zip / 7z / tar 一族都解不开，缺 UnRAR 时 rar 与 rar5 都解不开。
        Assert.Contains("7-Zip", text, StringComparison.Ordinal);
        Assert.Contains("UnRAR", text, StringComparison.Ordinal);
    }
}
