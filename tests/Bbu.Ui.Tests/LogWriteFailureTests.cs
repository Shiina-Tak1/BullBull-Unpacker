using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bbu.Core;
using Bbu.Core.Logging;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 日志写不进磁盘时界面要出一条提示条，且只出一条（W7 判据 9）。
///
/// 落盘那一路不抛异常，写失败只落在 <c>RunLog.WriteFailed</c> 上，而生产形态是 <c>WinExe</c>、
/// 没有控制台 —— 提示条是唯一可见的通道。一次为限由界面侧的闸守，不在 <c>RunLog</c> 上：
/// 首次写是入口的会话头，那一刻窗口还没订阅，所以断言要在窗口起来之后再触发。
/// </summary>
public sealed class LogWriteFailureTests
{
    /// <summary>
    /// 同名目录占住日志落点：文件打不开，此后每一次写都失败（比改目录 ACL 可移植）。
    /// 撤掉 <c>MainWindow</c> 里那道「已提示过」的闸即红：第二次失败会重建一条提示条。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-309")]
    public void WriteFailure_MustToastExactlyOnce()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, AppInfo.LogFileName));
        var log = new RunLog(Path.Combine(dir, AppInfo.LogFileName));

        var window = Show(dir, log);
        var host = window.FindControl<ToastHost>("Toasts")!;

        log.Note("窗口起来之后写一行");
        Drain(window);

        Assert.Single(ToastStackOf(host).Children);
        var first = ToastOf(host);
        var prefix = UiText.ToastLogUnavailableAt.Split('{')[0];
        Assert.StartsWith(prefix, Text(first), StringComparison.Ordinal);
        Assert.True(Text(first).Length > prefix.Length, "提示条里没有写失败的原因");

        // 再失败两次：提示条不许重建（重建就等于又提示了一次）。
        log.Note("再写一行");
        log.Note("还写一行");
        Drain(window);

        Assert.Same(first, ToastOf(host));
        Assert.Single(ToastStackOf(host).Children);

        window.Close();
    }

    private static MainWindow Show(string dir, RunLog log)
    {
        var window = new MainWindow(dir, dir, log);
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        Drain(window);
        return window;
    }

    private static void Drain(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static StackPanel ToastStackOf(ToastHost host) => host.FindControl<StackPanel>("ToastStack")!;

    private static Border ToastOf(ToastHost host) => (Border)ToastStackOf(host).Children[0];

    private static string Text(Border toast) => ((TextBlock)toast.Child!).Text!;

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-logfail-").FullName;
}
