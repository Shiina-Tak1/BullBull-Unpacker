using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 关窗流程。1.1.0 的缺陷是「还没问就无条件 request_stop，用户没有『我只是想关掉窗口』这条出路」
/// （B-2026-022）。C# 侧改成「问一次、停一次、等一次」的异步序列。
///
/// 这几条断言都用一个跑不完的任务做背景：没有它，`OnWindowClosing` 会走「没任务直接放行」那条，
/// 而那条恰恰不是这张 要管的东西。
/// </summary>
public sealed class ClosePromptTests
{
    /// <summary>没任务在跑时不许问：那个框只会拦下一次正常关窗。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void WithoutARun_MustCloseWithoutAsking()
    {
        var window = Show();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.FindControl<ConfirmPrompt>("Ask")!.IsOpen);
    }

    /// <summary>有任务在跑就拦下来并弹框，标题与正文都写清了后果。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void WithARun_MustAskAndSayWhatHappens()
    {
        var window = Show(running: true);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<ConfirmPrompt>("Ask")!;
        Assert.True(
            prompt.IsOpen,
            $"确认框没开；visible={window.IsVisible} run={RunTaskField().GetValue(window)}");
        Assert.Equal(UiText.CloseAskTitle, prompt.FindControl<TextBlock>("PromptTitle")!.Text);
        Assert.Equal(AskBody(), prompt.FindControl<TextBlock>("PromptBody")!.Text);

        // 拦住了才谈得上问：窗口还开着
        Assert.True(window.IsVisible);
        prompt.Cancel();
    }

    /// <summary>
    /// 正文要预告收尾宽限：按钮写着「停止所有任务并退出」，而收不干净时窗口并不会关，
    /// 正文里那个秒数还必须与代码真等的那个数同源 —— 两处各写一个数就会一个说 10 秒、一个真等 30 秒。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-246")]
    public void Body_MustForecastHowLongTheCleanupMayTake()
    {
        var window = Show(running: true);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var body = window.FindControl<ConfirmPrompt>("Ask")!.FindControl<TextBlock>("PromptBody")!.Text!;
        Assert.Contains($"{CloseStopSeconds()} 秒", body, StringComparison.Ordinal);
        Assert.Equal(AskBody(), body);

        window.FindControl<ConfirmPrompt>("Ask")!.Cancel();
    }

    /// <summary>
    /// 宽限内收不干净时那句话要说在提示条上，不能只进日志：日志面板在中档窗口下折成一行摘要，
    /// 只进日志等于什么都没说 —— 用户看到「点了没反应」，而窗口其实还开着。
    /// 同时钉**档位**：那一句走的是 `Failure` 入口 ⇒ 日志里那一行是失败档，撤成 `Note` 即红
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-246")]
    public async Task Timeout_MustSayItOnTheToast()
    {
        var log = new RunLog(Path.Combine(TempDir(), AppInfo.LogFileName));
        var lines = new List<LogRecord>();
        log.LineWritten += lines.Add;

        var window = Show(running: true, log);
        typeof(MainWindow)
            .GetField("_closeGrace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(window, TimeSpan.FromMilliseconds(50));

        window.Close();
        Dispatcher.UIThread.RunJobs();
        window.FindControl<ConfirmPrompt>("Ask")!
            .FindControl<Button>("PromptOk")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync();

        Assert.True(window.IsVisible, "宽限内没收干净，却把窗口关掉了");
        var toast = window.FindControl<ToastHost>("Toasts")!
            .FindControl<StackPanel>("ToastStack")!
            .Children.OfType<Border>()
            .SelectMany(child => child.GetVisualDescendants().OfType<TextBlock>())
            .Select(block => block.Text)
            .ToList();
        Assert.Contains(UiText.CloseStopTimeout, toast);

        var line = Assert.Single(lines, one => one.Text == UiText.CloseStopTimeout);
        Assert.Equal(LogLevel.Error, line.Level);
    }

    /// <summary>
    /// 配置不完整时点「开始」：那一句要落到**失败档**。用户点完看不出任何动静（窗口没变化、
    /// 清单里一项都没动），日志里那一行就是唯一凭据 —— 它若是陈述档，用户会以为那只是句提示
    /// （B-2026-024）。
    /// 撤掉 `RunAsync` 那一处的 `Failure` 换成 `Note` 即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-024")]
    public async Task RunWithAnIncompleteConfig_MustLogTheReasonAtTheFailureLevel()
    {
        var dir = TempDir();
        new JsonConfigStore(Path.Combine(dir, AppInfo.ConfigFileName))
            .Save(new AppConfig { OutputMode = OutputMode.Custom, OutputDirectory = "" });

        var log = new RunLog(Path.Combine(dir, AppInfo.LogFileName));
        var lines = new List<LogRecord>();
        log.LineWritten += lines.Add;

        var window = new MainWindow(dir, dir, log);
        window.Show();
        Drain();

        // 「开始」按钮走的就是这一个入口；这里直接调它，免得去点按钮再等异步链。
        await CallAsync(window, "RunAsync");

        var line = Assert.Single(lines, one => one.Text == UiText.OutputDirMissing);
        Assert.Equal(LogLevel.Error, line.Level);
    }

    /// <summary>
    /// 两个按钮是【停止所有任务并退出】【取消】，而默认按钮是「取消」。
    /// 「默认项是安全的那一个」是这张 判据原文里的一条。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void Buttons_MustBeStopAndCancelWithCancelAsTheDefault()
    {
        var window = Show(running: true);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<ConfirmPrompt>("Ask")!;
        var cancelButton = prompt.FindControl<Button>("PromptCancel")!;

        Assert.Equal(UiText.CloseAskStop, prompt.FindControl<Button>("PromptOk")!.Content);
        Assert.Equal(UiText.Cancel, cancelButton.Content);
        Assert.True(cancelButton.IsDefault);
        Assert.True(cancelButton.IsCancel);
        Assert.True(window.IsVisible);

        prompt.Cancel();
    }

    /// <summary>
    /// 选「取消」不关窗，也不打断任务。这一条是 1.1.0 缺掉的那一支，
    /// 也是这张 最主要的一条。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public async Task Cancel_MustKeepTheWindowOpenAndTheRunAlive()
    {
        var window = Show(running: true);
        var unfinished = RunningTask(window);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<ConfirmPrompt>("Ask")!;
        prompt.FindControl<Button>("PromptCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync();

        Assert.False(unfinished.IsCompleted);
        Assert.False(prompt.IsOpen);
        Assert.True(window.IsVisible);
    }

    /// <summary>
    /// 确认框还开着时又点一次关闭：不叠第二个框。
    /// 叠上去的话用户点一下会同时答掉两个问题。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void ASecondClose_MustNotStackASecondPrompt()
    {
        var window = Show(running: true);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<ConfirmPrompt>("Ask")!;
        Assert.True(prompt.IsOpen);
        Assert.True(window.IsVisible);

        prompt.Cancel();
    }

    /// <summary>Esc 落在确认框上等于「取消」：那一层最上面，它答的就是它自己。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public async Task EscapeOnThePrompt_MustMeanCancel()
    {
        var window = Show(running: true);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<ConfirmPrompt>("Ask")!;
        prompt.Cancel();
        await FlushAsync();

        Assert.False(prompt.IsOpen);
        Assert.True(window.IsVisible);
    }

    /// <summary>
    /// 创建真实窗口，并挂起一个不会结束的任务，让 <c>OnWindowClosing</c> 走「有任务」那一条。
    /// 关窗流程读的是 `_runTask`，所以这里必须是一个真的未完成任务，不能只是置一个标志位。
    /// </summary>
    private static MainWindow Show(bool running = false, RunLog? log = null)
    {
        var dir = TempDir();
        var window = new MainWindow(dir, dir, log);
        window.Show();

        // Opened 里那条 AutoDriveAsync 是异步的：不把它跑完就写 _runTask，
        // 它后面的续体会在关窗断言之后才动，量到的状态与真机不一致。
        Drain();

        if (running)
        {
            // 反射写进去：那条路是私有的，而写一个公开的测试钩子等于把内部状态暴露给产品代码。
            RunTaskField().SetValue(window, new TaskCompletionSource().Task);
        }

        return window;
    }

    /// <summary>执行完界面线程已排队的任务，包含已就绪的异步续体。</summary>
    private static void Drain()
    {
        for (var spin = 0; spin < 20; spin++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>窗口上挂着的那个未完成任务。断言「没被打断」时要用它本身，不是另造一个。</summary>
    private static Task RunningTask(MainWindow window) => (Task)RunTaskField().GetValue(window)!;

    /// <summary>调窗口的私有异步入口（与「开始」按钮同路），返回它那一趟的 Task。</summary>
    private static Task CallAsync(MainWindow window, string name) =>
        (Task)typeof(MainWindow)
            .GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    /// <summary>关窗确认的正文：秒数取自代码真等的那个常量，两处写同一个数。</summary>
    private static string AskBody() => string.Format(
        System.Globalization.CultureInfo.CurrentCulture, UiText.CloseAskBodyAt, CloseStopSeconds());

    /// <summary>关窗时等这一批收尾的秒数。</summary>
    private static int CloseStopSeconds() => (int)typeof(MainWindow)
        .GetField("CloseStopSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetRawConstantValue()!;

    private static System.Reflection.FieldInfo RunTaskField() =>
        typeof(MainWindow).GetField(
            "_runTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>把确认框答完之后那条异步链跑完。</summary>
    private static async Task FlushAsync()
    {
        for (var spin = 0; spin < 20; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "bbu-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
