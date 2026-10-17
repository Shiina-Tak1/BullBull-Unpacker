using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 排版两处的样式侧。
///
/// 这几条只由样式表的一段文本决定：字号取哪个令牌、间距刻度有没有那一档。
/// </summary>
public sealed partial class TypographyTests
{
    /// <summary>等宽域取 12 至 13 之间的令牌，不跟着正文走。</summary>
    [Fact]
    public void MonoFontSize_MustSitBetweenTwelveAndThirteen()
    {
        var tokens = Tokens();

        var mono = Token(tokens, "FsMono");
        Assert.InRange(mono, 12, 13);
        Assert.NotEqual(Token(tokens, "FsBody"), mono);
    }

    /// <summary>间距刻度只有 4 / 8 / 12 / 16 / 24 五档。</summary>
    [Fact]
    public void SpacingScale_MustCarryTheFiveSteps()
    {
        var tokens = Tokens();

        Assert.Equal(4, Token(tokens, "Sp4"));
        Assert.Equal(8, Token(tokens, "Sp8"));
        Assert.Equal(12, Token(tokens, "Sp12"));
        Assert.Equal(16, Token(tokens, "Sp16"));
        Assert.Equal(24, Token(tokens, "Sp24"));
    }

    /// <summary>等宽域只剩日志面板那两处：时间戳与正文，两处都取同一个令牌。</summary>
    [Fact]
    public void MonoDomain_MustTakeTheMonoToken()
    {
        var styles = Styles();

        foreach (var selector in new[] { "TextBlock.lg-ts", "TextBlock.logline" })
        {
            Assert.Contains("FsMono", StyleBlock(styles, selector), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 无限循环动画只允许一处，而且只能挂在运行中那个图标上。
    /// 旧口径是「一律不进样式表」（2026-10-05 改）：旋转的样式此前写过，但没有元素挂它，被当作死样式删除；
    /// 挂不挂由代码按行状态判定：样式表里不留无人挂载的样式。
    /// </summary>
    [Fact]
    public void InfiniteAnimation_MustOnlyBeTheSpinningRunningIcon()
    {
        var styles = Comment().Replace(Styles(), string.Empty);

        Assert.Equal(1, Count(styles, "IterationCount=\"INFINITE\""));
        Assert.Contains("Path.spin", styles, StringComparison.Ordinal);

        var view = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("Classes.Set(\"spin\"", view, StringComparison.Ordinal);
    }

    private static double Token(string tokens, string key)
    {
        var match = TokenPattern().Match(tokens);
        while (match.Success)
        {
            if (match.Groups[1].Value == key)
            {
                return double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            match = match.NextMatch();
        }

        Assert.Fail($"令牌表里找不到 {key}");
        return 0;
    }

    private static string Tokens() =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Tokens.axaml"));

    private static string Styles() =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));

    private static string StyleBlock(string xaml, string selector)
    {
        var start = xaml.IndexOf($"Selector=\"{selector}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"样式表里找不到 {selector}");

        var open = xaml.IndexOf('>', start);
        var close = xaml.IndexOf("</Style>", open, StringComparison.Ordinal);
        return xaml[open..close];
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var at = text.IndexOf(token, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [GeneratedRegex("""x:Key="([A-Za-z0-9]+)">([0-9.]+)<""")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
