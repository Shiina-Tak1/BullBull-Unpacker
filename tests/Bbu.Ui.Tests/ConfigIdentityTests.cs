using System;
using System.IO;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Ui.Avalonia.Views;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 配置实例只有一份。宿主与设置页共用同一个 <see cref="AppConfig"/>，
/// 保存、列宽落盘与恢复默认都就地改字段（B-2026-106）。
///
/// 换掉实例的后果是两处各持一份：页面手里那份没有列宽，下一次保存把盘上的列宽写回旧值；
/// 宿主手里那份没有新排除名单，扫描与运行照旧读旧的。这三条断言拦的就是它。
/// </summary>
public sealed class ConfigIdentityTests
{
    /// <summary>宿主与设置页拿到的是同一个对象，改一项之后仍是。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-106")]
    public void HostAndSettingsPage_MustShareOneConfigInstance()
    {
        var (window, _) = Show();

        Assert.Same(HostConfig(window), Page(window).Current);

        Page(window).Push();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(HostConfig(window), Page(window).Current);
    }

    /// <summary>
    /// 拖过列宽再动设置页任意一项，盘上的列宽仍是新值。
    /// 页面手里若是旧对象，那一次保存会把列宽写回拖之前的取值。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-106")]
    public void ColumnWidthSave_MustNotLeaveThePageWithAStaleCopy()
    {
        var (window, store) = Show();
        var shared = HostConfig(window);

        var cols = ColumnLayout.Default();
        cols[0] += 40;
        SetColumns(window, cols);
        SaveColumns(window);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(shared, HostConfig(window));
        Assert.Same(shared, Page(window).Current);
        Assert.Equal(cols, store.Load().TableColumnWidths);

        ((SettingsNumberField)Page(window).Model["MaxDepth"]).Value = 9;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(cols, store.Load().TableColumnWidths);
    }

    /// <summary>
    /// 点过「恢复默认」之后改一项：实例没被换掉，宿主读到的排除名单就是刚写的那一份。
    /// 换实例的话宿主手里还是旧名单，扫描与运行绕开刚钉住的那条保证（B-2026-100）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-106")]
    public void RestoreDefaults_MustKeepTheInstanceTheHostReads()
    {
        var (window, store) = Show();
        var shared = HostConfig(window);
        var page = Page(window);

        ((SettingsActionField)page.Model["RestoreDefaults"]).Invoke!();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(shared, HostConfig(window));
        Assert.Same(shared, page.Current);

        ((SettingsTextField)page.Model["ExcludedExtensions"]).Value = "apk";
        page.Push();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "apk" }, HostConfig(window).ExcludedExtensions);
        Assert.Equal(new[] { "apk" }, store.Load().ExcludedExtensions);
    }

    /// <summary>
    /// 列宽落盘失败不许从 UI 事件里逃出去，且必须在日志文件里留一句（B-2026-152）。
    ///
    /// 上游是 <c>HookColumns</c> 里那个 700ms 去抖定时器：拖完列宽它就调到 <c>SaveColumns</c>，
    /// 这一档失败没有第二处兜底 —— 从前 <c>Save</c> 会抛，异常直接从定时器 tick 逃出去。
    /// 撤掉那一处 <c>if (!Save(...))</c>：异常抛穿这条用例即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-152")]
    public void ColumnWidthSaveFailure_MustNotEscapeTheTimerAndMustBeLogged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbu-config-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 目标位置上横着一个目录：临时文件写得进去，替换那一步必失败（sharing violation 与只读目录同属一类）。
        Directory.CreateDirectory(Path.Combine(dir, "config.json"));

        // 判据要读盘：两参构造拿到的是不落盘的替身，日志实例必须显式传进去。
        var log = new RunLog(Path.Combine(dir, AppInfo.LogFileName));
        var window = new MainWindow(dir, dir, log);
        window.Show();
        for (var spin = 0; spin < 20; spin++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        var cols = ColumnLayout.Default();
        cols[0] += 40;
        SetColumns(window, cols);

        SaveColumns(window);
        Dispatcher.UIThread.RunJobs();

        // 写句柄常开：读回走共享读取那一处，自己开文件会撞 Windows 的共享规则。
        var text = RingLogFile.ReadText(Path.Combine(dir, AppInfo.LogFileName));
        Assert.Contains("保存失败", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真窗口加一次性临时目录：不构造无参窗口，它会写作者的 <c>config.json</c>。
    /// <c>Opened</c> 里那条异步链先跑完，之后量到的状态才与真机一致。
    /// </summary>
    private static (MainWindow Window, JsonConfigStore Store) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbu-config-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var window = new MainWindow(dir, dir);
        window.Show();

        for (var spin = 0; spin < 20; spin++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        return (window, new JsonConfigStore(Path.Combine(dir, "config.json")));
    }

    private static AppConfig HostConfig(MainWindow window) => (AppConfig)Field("_config").GetValue(window)!;

    private static SettingsPage Page(MainWindow window) => window.FindControl<SettingsPage>("SetPage")!;

    private static void SetColumns(MainWindow window, double[] cols) => Field("_cols").SetValue(window, cols);

    private static void SaveColumns(MainWindow window) => Method("SaveColumns").Invoke(window, null);

    private static FieldInfo Field(string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static MethodInfo Method(string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
