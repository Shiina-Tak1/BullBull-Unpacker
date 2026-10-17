using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Bbu.Ui.Avalonia.Views;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 几何下限。1.1.0 的问题正出在这一条：最小高宽是手算的常数，算漏了日志卡头部与六处间距，
/// 结果拖到最小尺寸时列表只剩 111px、拖拽引导的标题被压成 7–8px（B-2026-092）。
///
/// C# 侧的最小尺寸也是手算的（`MainWindow.axaml` 的 MinWidth 与 MinHeight），
/// 所以「成因换了一套数、风险仍在」这句话照旧成立 —— 这几条断言就是拦它的。
/// 最小尺寸落在窄档，所以这一组量的是窄档那两列。
/// </summary>
public sealed class WindowGeometryTests
{
    /// <summary>表头 28 加三行 32：窗口缩到最小时仍保证清单内容可见。</summary>
    private const double TableMinHeight = 28 + (32 * 3);

    /// <summary>
    /// 清单区在两页里都要留着下限：有行的那一页与空状态那一页共用同一个槽位，
    /// 两页都不许被压到看不见。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-092")]
    public void ListArea_MustKeepTheTableMinimumAtTheSmallestSize()
    {
        var window = ShowAtMinimum();

        var listHeight = window.FindControl<ScrollViewer>("ListScroll")!.Bounds.Height;

        Assert.True(
            listHeight >= TableMinHeight,
            $"最小尺寸下列表区只有 {listHeight:0.#}px，低于表格下限 {TableMinHeight}px");
    }

    /// <summary>空状态那一页同样不许被压扁：标题要占得住它自己的高度。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-092")]
    public void EmptyState_MustNotBeSquashedAtTheSmallestSize()
    {
        var window = ShowAtMinimum();

        var empty = window.FindControl<StackPanel>("EmptyState")!.Bounds.Height;

        Assert.True(empty > 0, "空状态那一页被压成了零高度");
    }

    /// <summary>
    /// 表头不被裁：最小宽度落在中档，清单区仍是四列，之和仍等于内容盒宽，不产生横向滚动条。
    /// 宽度量的是布局后的实际值，不是列定义里那份取值。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-092")]
    public void Columns_MustNotBeSquashedAtTheSmallestSize()
    {
        var window = ShowAtMinimum();

        var head = window.FindControl<Grid>("HeadCols")!;
        var list = window.FindControl<ScrollViewer>("ListScroll")!;

        Assert.Equal(ColumnLayout.Count, head.ColumnDefinitions.Count);

        var widths = head.ColumnDefinitions.Select(c => c.ActualWidth).ToArray();
        Assert.All(widths, w => Assert.True(w > 0, "有列被压成了零宽"));

        var total = widths.Sum();
        var box = list.Viewport.Width - (2 * ColumnLayout.RowPad);
        Assert.True(
            Math.Abs(total - box) < 1.0,
            $"四列之和 {total:0.#}px 与内容盒宽 {box:0.#}px 对不上，会长出横向滚动条");
    }

    /// <summary>
    /// 表头那份列宽字面量与 <see cref="ColumnLayout.DefaultSpec"/> 是同一个数，两处必须逐字一致 ——
    /// 只改一处时构建与全量测试都不会红，表现是开窗那一帧表头与行不同源。
    /// </summary>
    [Fact]
    public void HeadColumns_MustMatchTheDefaultSpec()
    {
        var xaml = File.ReadAllText(
            Path.Combine(TestRepo.Root(), "src", "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml"));
        var head = xaml[xaml.IndexOf("Name=\"HeadCols\"", StringComparison.Ordinal)..];
        var spec = head[..head.IndexOf('>')];

        Assert.Contains($"ColumnDefinitions=\"{ColumnLayout.DefaultSpec}\"", spec, StringComparison.Ordinal);
    }

    /// <summary>窗口自己的最小尺寸不许为 0：那是「没设下限」的另一种写法。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-092")]
    public void MinimumSize_MustBeSetOnTheWindow()
    {
        var window = ShowAtMinimum();

        Assert.True(window.MinWidth > 0, "窗口没设最小宽度");
        Assert.True(window.MinHeight > 0, "窗口没设最小高度");
    }

    /// <summary>
    /// 按窗口自己的最小尺寸摆一次再量。
    /// 尺寸取 `MinWidth` / `MinHeight` 而不是另写一个常数 —— 断言要跟着窗口走，
    /// 换了下限就该重新量（这正是 1.1.0 那次的教训）。
    /// 令牌与样式表要显式装：headless 宿主不预装这两份，量宽度靠它们（`PadRow` 与字号都在里面）。
    /// </summary>
    private static MainWindow ShowAtMinimum()
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static ResourceDictionary Tokens() =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static Styles Controls() =>
        (Styles)AvaloniaXamlLoader.Load(new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    /// <summary>
    /// 落点用一次性临时目录：数据目录会写配置与日志，使用默认目录会覆盖作者的
    /// <c>config.json</c>（同一个进程里还有别的测试在跑）。
    /// </summary>
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "bbu-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
