using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 五个浮层宿主的锚点：都铺满根 Grid 的全部四行。
///
/// 确认框原先漏了这一对属性，于是落进 44 高的顶栏那一行 —— 遮罩只剩顶上一横条、
/// 卡片被那一行夹住（B-2026-131）。这里既钉"五个同一套"，也钉确认框真铺满窗口。
/// </summary>
[Trait("Bug", "B-2026-131")]
public sealed class OverlayAnchorTests
{
    /// <summary>四个 Panel 宿主与确认框用同一套 Grid.Row / Grid.RowSpan。</summary>
    [AvaloniaFact]
    public void OverlayHosts_MustAllSpanEveryRow()
    {
        var window = Show(1160, 660);

        foreach (var name in new[] { "OverlayHost", "OutPanel", "PopupHost", "PwHost" })
        {
            var host = window.FindControl<Panel>(name)!;
            Assert.Equal(0, Grid.GetRow(host));
            Assert.Equal(4, Grid.GetRowSpan(host));
        }

        var ask = window.FindControl<ConfirmPrompt>("Ask")!;
        Assert.Equal(0, Grid.GetRow(ask));
        Assert.Equal(4, Grid.GetRowSpan(ask));

        window.Close();
    }

    /// <summary>确认框弹出时遮罩铺满整窗，卡片整个落在窗口里且横向居中。</summary>
    [AvaloniaFact]
    public void ConfirmPrompt_MustCoverTheWindowAndCentreItsCard()
    {
        var window = Show(1160, 660);
        var ask = window.FindControl<ConfirmPrompt>("Ask")!;
        var pending = ask.AskAsync(UiText.LibraryDeleteAskTitle, "正文", UiText.LibraryDelete);
        Settle(window);

        var host = ask.FindControl<Panel>("PromptHost")!;
        var scrim = ask.FindControl<Border>("PromptScrim")!;
        var card = ask.FindControl<Border>("PromptCard")!;
        var client = window.ClientSize;

        Assert.True(host.IsVisible);
        Assert.Equal(client.Width, scrim.Bounds.Width, 1);
        Assert.Equal(client.Height, scrim.Bounds.Height, 1);

        var at = card.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(
            at.X >= -0.5 && at.X + card.Bounds.Width <= client.Width + 0.5,
            $"卡片横着出了窗口：{at.X:0.#} + {card.Bounds.Width:0.#} > {client.Width:0.#}");
        Assert.True(
            at.Y >= -0.5 && at.Y + card.Bounds.Height <= client.Height + 0.5,
            $"卡片竖着出了窗口：{at.Y:0.#} + {card.Bounds.Height:0.#} > {client.Height:0.#}");
        Assert.Equal((client.Width - card.Bounds.Width) / 2, at.X, 1);

        // 纵向也在窗口中间。卡片自己带 60 的底边距，所以容差放到 40 —— 真机截图对照时
        // 卡片贴顶就是这一条红的样子（B-2026-131 的 before）。
        var centerY = at.Y + card.Bounds.Height / 2;
        Assert.True(
            Math.Abs(centerY - client.Height / 2) <= 40,
            $"卡片纵向没居中：中心 {centerY:0.#}，窗口中心 {client.Height / 2:0.#}");

        ask.Cancel();
        _ = pending;
        window.Close();
    }

    /// <summary>密码本页的删除走的就是这同一个确认框，不是它自己另弹一个。</summary>
    [AvaloniaFact]
    public void DeletePassword_MustGoThroughTheSamePrompt()
    {
        var window = Show(1160, 660);
        var library = window.FindControl<LibraryPage>("LibPage")!;
        var ask = window.FindControl<ConfirmPrompt>("Ask")!;

        Assert.False(ask.IsOpen);

        var pending = library.Confirm!(UiText.LibraryDeleteAskTitle, "正文", UiText.LibraryDelete);
        Settle(window);

        Assert.True(ask.IsOpen, "密码本页的删除没有走宿主那一个确认框");

        ask.Cancel();
        _ = pending;
        window.Close();
    }

    /// <summary>尺寸要在 Show 之后设：XAML 里那份基准尺寸是 InitializeComponent 写的，先设会被它盖掉。</summary>
    private static MainWindow Show(double width, double height)
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();

        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-anchor-").FullName;
}
