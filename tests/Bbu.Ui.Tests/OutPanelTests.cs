using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 输出设置浮层：页脚排布（p030），以及从这一层弹出来的覆盖档确认框上回车归谁。
/// 回车那三条钉在这里，是因为只有这一刻 `OutSave` 与 `PromptCancel` 的 <c>IsDefault</c> 同时为真 ——
/// 根上的默认按钮回退先注册者先吃，确认框并不因此在根上守住回车（B-2026-316）。
/// </summary>
public sealed class OutPanelTests
{
    /// <summary>保存排在取消左边：主要动作靠左，取消收在右端。</summary>
    [AvaloniaFact]
    public void OutPanel_MustPutSaveLeftOfCancel()
    {
        var window = Show(1160, 660);

        // 浮层平时不可见，不可见的控件不参与布局也就没有坐标 —— 先让它可见再量。
        window.FindControl<Panel>("OutPanel")!.IsVisible = true;
        Settle(window);

        var save = window.FindControl<Button>("OutSave")!;
        var cancel = window.FindControl<Button>("OutCancel")!;
        var saveAt = save.TranslatePoint(new Point(0, 0), window)!.Value;
        var cancelAt = cancel.TranslatePoint(new Point(0, 0), window)!.Value;

        Assert.True(
            saveAt.X < cancelAt.X,
            $"保存按钮在 {saveAt.X:0.#}，取消在 {cancelAt.X:0.#}：保存没有排在取消左边");

        window.Close();
    }

    /// <summary>焦点本来就落在确认框内时，回车归它；底下的「保存」一下都不许被点到。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void OutPanel_EnterWithFocusInsideThePrompt_MustAnswerThePrompt()
    {
        var (window, pending) = OverwriteAsk();
        var saveClicks = 0;
        window.FindControl<Button>("OutSave")!.Click += (_, _) => saveClicks++;

        Press(window, Key.Enter, PhysicalKey.Enter, "\r");

        Assert.True(pending.IsCompleted, "回车没有答掉确认框");
        Assert.False(pending.GetAwaiter().GetResult());
        Assert.Equal(0, saveClicks);
        Assert.True(window.FindControl<Panel>("OutPanel")!.IsVisible);

        window.Close();
    }

    /// <summary>
    /// 焦点不在任何一处时（被清空）回车仍归确认框：不兜底的话这一下会落到输出设置的「保存」上，
    /// 而确认框一直没被答、还开着（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void OutPanel_EnterWithNoFocus_MustStillAnswerThePrompt()
    {
        var (window, pending) = OverwriteAsk();
        var saveClicks = 0;
        window.FindControl<Button>("OutSave")!.Click += (_, _) => saveClicks++;

        window.FocusManager!.ClearFocus();
        Press(window, Key.Enter, PhysicalKey.Enter, "\r");

        Assert.True(pending.IsCompleted, "回车没有答掉确认框");
        Assert.False(pending.GetAwaiter().GetResult());
        Assert.Equal(0, saveClicks);

        window.Close();
    }

    /// <summary>
    /// 焦点被 Tab 带出确认框之后回车仍归它，主界面的按钮一下都不许被点到：不兜底的话，
    /// 这一下会落到当前焦点那颗按钮上（实测是主界面「密码本」），确认框却还开着（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void OutPanel_EnterAfterTabLeftThePrompt_MustNotReachTheMainWindow()
    {
        var (window, pending) = OverwriteAsk();
        var libraryClicks = 0;
        window.FindControl<Button>("BtnLibrary")!.Click += (_, _) => libraryClicks++;

        Press(window, Key.Tab, PhysicalKey.Tab, "\t");
        Press(window, Key.Enter, PhysicalKey.Enter, "\r");

        Assert.True(pending.IsCompleted, "回车没有答掉确认框");
        Assert.False(pending.GetAwaiter().GetResult());
        Assert.Equal(0, libraryClicks);

        window.Close();
    }

    /// <summary>
    /// C3 的现场：输出设置浮层开着，覆盖档的确认框从它上面弹出来。这一刻两颗 <c>IsDefault</c> 同时为真。
    /// </summary>
    private static (MainWindow Window, Task<bool> Pending) OverwriteAsk()
    {
        var window = Show(1160, 660);
        window.FindControl<Panel>("OutPanel")!.IsVisible = true;
        Settle(window);

        var pending = window.FindControl<ConfirmPrompt>("Ask")!.AskAsync(
            UiText.ConflictOverwriteAskTitle, UiText.ConflictOverwriteAskBody, UiText.ConflictOverwrite);
        Dispatcher.UIThread.RunJobs();
        Settle(window);
        return (window, pending);
    }

    /// <summary>按一次键。两参数的 <c>KeyPress</c> 已标过时，用带物理键的那个重载。</summary>
    private static void Press(MainWindow window, Key key, PhysicalKey physical, string symbol)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, symbol);
        Dispatcher.UIThread.RunJobs();
        Settle(window);
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
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-outpanel-").FullName;
}
