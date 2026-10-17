using System;
using System.IO;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Ui.Avalonia.Views;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 浮窗卡片的高度：空本与有密码的密码本共用**同一个固定值**。
///
/// 看图反馈两条：下限不够高、有无内容该是同一高度。改成固定高度之后
/// 逻辑也简化了 —— 不再有"内容不足时靠下限兜住"这一档（B-2026-132）。
/// </summary>
[Trait("Bug", "B-2026-132")]
public sealed class PopupSizeTests
{
    /// <summary>空本与六条两种情形下，卡片高度一模一样，且不低于固定值。</summary>
    [AvaloniaFact]
    public void LibraryPopup_MustKeepOneFixedHeight()
    {
        var window = Show(1160, 660);
        OpenLibrary(window);

        var card = window.FindControl<Border>("PopupCard")!;
        var empty = card.Bounds.Height;
        Assert.True(empty >= 499.5, $"空本时浮窗只有 {empty:0.#} 高");

        var vault = (Vault)Field("_vault").GetValue(window)!;
        for (var index = 0; index < 6; index++)
        {
            vault.Add($"pw-{index}");
        }

        window.FindControl<LibraryPage>("LibPage")!.Attach(vault, (IRunLog)Field("_log").GetValue(window)!);
        Settle(window);

        Assert.Equal(empty, card.Bounds.Height, 1);
        Assert.True(
            card.Bounds.Height <= window.ClientSize.Height * 0.82 + 0.5,
            $"浮窗高 {card.Bounds.Height:0.#}，越过了窗口的 82%");

        window.Close();
    }

    /// <summary>
    /// 固定高度真的挂在浮窗卡片上：样式绑的是 <c>HPopupFixed</c>，令牌值就是那一档。
    /// 行为断言量的是"高度等于多少"，这一条钉住"它是从令牌来的"。
    /// </summary>
    [Fact]
    public void PopupHeight_MustBeBoundToTheToken()
    {
        var controls = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));
        var popup = controls[controls.IndexOf("Border.popup", StringComparison.Ordinal)..];
        var head = popup[..popup.IndexOf("</Style>", StringComparison.Ordinal)];

        Assert.Contains("Property=\"Height\"", head, StringComparison.Ordinal);
        Assert.Contains("HPopupFixed", head, StringComparison.Ordinal);
        Assert.Contains("MinWidth", head, StringComparison.Ordinal);
        Assert.Contains("WPopupMin", head, StringComparison.Ordinal);

        var tokens = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Bbu.Ui.Avalonia", "Theme", "Tokens.axaml"));
        Assert.Contains("x:Key=\"HPopupFixed\">500<", tokens, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"WPopupMin\">320<", tokens, StringComparison.Ordinal);
    }

    private static void OpenLibrary(MainWindow window)
    {
        window.FindControl<Button>("BtnLibrary")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.True(window.FindControl<Panel>("PopupHost")!.IsVisible, "密码本浮窗没打开");
    }

    private FieldInfo Field(string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

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

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    /// <summary>仓库根：往上一路找 Bbu.slnx，不写死层数。</summary>
    private static string RepoRoot() => TestRepo.Root();

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-popup-").FullName;
}
