using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 日志面板的两条通道与筛选：进度行原地刷新、真结果进来落终态，
/// 「仅错误」筛掉的是整行而不是正文那一格（B-2026-136），跟随要滚到可滚范围的下界。
/// </summary>
public sealed class LogPanelTests
{
    /// <summary>
    /// 「仅错误」筛掉的是整行。只藏正文那一格时，被筛掉的行会原地留下一串时间戳
    /// —— 那正是 docx 20 看到的样子（B-2026-136，B-2026-111 两列化时引入的回归）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-136")]
    public void ErrorOnly_MustHideTheWholeRow()
    {
        var window = Show();

        Feed(window, Result(LogLevel.Info, "普通一行"));
        Feed(window, Result(LogLevel.Error, "失败一行"));

        var only = window.FindControl<ToggleButton>("BtnErrOnly")!;
        only.IsChecked = true;
        only.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var rows = Rows(window);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsVisible);
        Assert.True(rows[1].IsVisible);

        // 留下来的每一行，正文都看得见：只藏正文的写法在这里变红（时间戳还在、正文没了）。
        Assert.All(rows.Where(row => row.IsVisible), row => Assert.True(Body(row).IsVisible));

        window.Close();
    }

    /// <summary>
    /// 进度不进面板：连着两条进度后面板还是空的，只有真结果才占一行（docx 24）。
    /// </summary>
    [AvaloniaFact]
    public void Progress_MustNotReachThePanel()
    {
        var window = Show();

        Feed(window, Progress(12, 3, 120));
        Feed(window, Progress(17, 9, 120));

        Assert.Empty(Rows(window));

        Feed(window, Result(LogLevel.Info, "解压完成"));

        Assert.Single(Rows(window));

        window.Close();
    }

    /// <summary>筛选过的面板不被后来的行打乱：筛完再来的普通行也收起（docx 20）。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-136")]
    public void ErrorOnly_MustAlsoHideRowsThatArriveLater()
    {
        var window = Show();

        var only = window.FindControl<ToggleButton>("BtnErrOnly")!;
        only.IsChecked = true;
        only.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Feed(window, Result(LogLevel.Info, "后来的普通一行"));
        Feed(window, Result(LogLevel.Warn, "后来的提示一行"));

        var rows = Rows(window);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsVisible);
        Assert.True(rows[1].IsVisible);

        window.Close();
    }

    /// <summary>
    /// 活动槽那一行按 core 给的进度结构拼（docx 24）：有枚举个数与耗时，且不复读日志正文。
    /// </summary>
    [AvaloniaFact]
    public void ActivitySlot_MustComeFromTheProgressStructure()
    {
        var window = Show();
        var line = Progress(12, 3, 120);

        Set(window, "_progress", line.Progress);
        Set(window, "_progressAt", DateTime.UtcNow);
        var text = ActivityText(window);

        Assert.Contains("密码枚举 3/120", text, StringComparison.Ordinal);
        Assert.Contains("已耗时 12.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(line.Text, text, StringComparison.Ordinal);

        // 还没收到第一条心跳时先占一句「准备中」，不是空白也不是上一句日志。
        Set(window, "_progress", null);
        Assert.Equal(UiText.ActivityStarting, ActivityText(window));

        window.Close();
    }

    /// <summary>
    /// 耗时按秒表走：两次进度之间按本地时钟续上，不是每来一条心跳才跳一下（docx 24）。
    /// 把基准往前推 30 秒，显示的就该是 35 秒那一档；实现退回「只显示 core 给的秒数」时这条变红。
    /// </summary>
    [AvaloniaFact]
    public void ActivitySlot_MustKeepTheClockRunningBetweenBeats()
    {
        var window = Show();

        Set(window, "_progress", new LogProgress(5, 3, 120, ProgressStage.Search));
        Set(window, "_progressAt", DateTime.UtcNow.AddSeconds(-30));

        Assert.Contains("已耗时 35", ActivityText(window), StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// 耗时那一句写「已耗时 x.x秒」；过了一分钟仍写秒，只是不再带小数。
    /// </summary>
    [AvaloniaFact]
    public void ActivitySlot_MustSayElapsedWithOneDecimal()
    {
        var window = Show();

        Set(window, "_progress", new LogProgress(12.3, 3, 120, ProgressStage.Search));
        Set(window, "_progressAt", DateTime.UtcNow);
        var text = ActivityText(window);
        Assert.Contains("已耗时 12.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("已耗时 12秒", text, StringComparison.Ordinal);

        Set(window, "_progress", new LogProgress(75, 3, 120, ProgressStage.Search));
        Set(window, "_progressAt", DateTime.UtcNow);
        Assert.Contains("已耗时 75 秒", ActivityText(window), StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// 分流只在 <c>OnLine</c> 一处判：带结构的进活动槽、不带结构的进面板（并把进度位清掉）。
    /// 撤掉那道分流、让不带结构的也进活动槽即红（B-2026-148）。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-148")]
    public void ActivitySlot_MustIgnoreProgressWithoutStructure()
    {
        var window = Show();

        Feed(window, Progress(12, 3, 120));
        Assert.Contains("密码枚举 3/120", ActivityText(window), StringComparison.Ordinal);
        Assert.Empty(Rows(window));

        Feed(window, new LogRecord(LogLevel.Info, "解压中（已耗时 12 秒）"));

        // 不带结构的进面板，活动槽回到「准备中」：它描述的那件事已经有结论了。
        Assert.Single(Rows(window));
        Assert.Equal(UiText.ActivityStarting, ActivityText(window));

        window.Close();
    }

    /// <summary>
    /// 面板那一列的时刻取**记录自带的那一个**，不自己取当前时间：面板与日志文件里那一行因此是同一个时刻。
    /// 记录的时刻故意落在 2099 年 —— 改成 <c>DateTime.Now</c> 时这一列会显示今天的时刻，这条立刻红。
    /// </summary>
    [AvaloniaFact]
    public void Timestamp_MustComeFromTheRecord()
    {
        var window = Show();
        var at = new DateTimeOffset(2099, 12, 31, 23, 58, 57, TimeSpan.FromHours(8));

        Feed(window, new LogRecord(LogLevel.Info, "一行正文", 0, null, at));

        var row = Assert.Single(Rows(window));
        var stamp = Stamp(row);
        Assert.Equal(at.ToString("HH:mm:ss", CultureInfo.InvariantCulture), stamp.Text);

        window.Close();
    }

    /// <summary>
    /// 清空列表后构成条不留残色：六段都收成绝对零宽 —— 全为 0* 时 Grid 按等分铺满，
    /// 条上会剩下六段颜色（作者实机反馈）。
    /// </summary>
    [AvaloniaFact]
    public void EmptyProgressBar_MustCollapseToNothing()
    {
        var window = Show();
        Call(window, "SetSegments", 0, 0, 0, 0, 0, 0);

        var segments = window.FindControl<Grid>("ProgSegs")!;
        Assert.False(segments.IsVisible);
        Assert.All(segments.ColumnDefinitions, column =>
        {
            Assert.Equal(0, column.Width.Value);
            Assert.Equal(GridUnitType.Pixel, column.Width.GridUnitType);
        });

        // 刚打开的窗口也是空的：初始化那条路上也要把它收起来。
        var fresh = Show();
        Assert.False(fresh.FindControl<Grid>("ProgSegs")!.IsVisible);
        fresh.Close();

        window.Close();
    }

    /// <summary>
    /// 排错行只进日志文件、不进面板：面板阈值默认是「陈述」，正文级别低于它的行一律不占位。
    /// 只按通道过滤时，正文级别是 <see cref="LogLevel.Debug"/> 而行走在结果通道上的行照样会进来。
    /// </summary>
    [AvaloniaFact]
    public void DebugText_MustStayOutOfThePanel()
    {
        var window = Show();

        Feed(window, Result(LogLevel.Debug, "排错一行"));
        Assert.Empty(Rows(window));

        Feed(window, Result(LogLevel.Info, "普通一行"));
        Assert.Single(Rows(window));

        window.Close();
    }

    /// <summary>四档都不加粗：级别只由颜色承载，颜色足够辨识（8）。</summary>
    [AvaloniaFact]
    public void WarnAndError_MustNotBeBold()
    {
        var window = Show();

        Feed(window, Result(LogLevel.Info, "普通一行"));
        Feed(window, Result(LogLevel.Warn, "要注意一行"));
        Feed(window, Result(LogLevel.Error, "失败一行"));

        var rows = Rows(window);
        Assert.NotEqual(FontWeight.Bold, Body(rows[0]).FontWeight);
        Assert.NotEqual(FontWeight.Bold, Body(rows[1]).FontWeight);
        Assert.NotEqual(FontWeight.Bold, Body(rows[2]).FontWeight);

        window.Close();
    }

    /// <summary>
    /// 主窗口自己发的行（停止、关闭、队列提示）必须经日志口出去：直连面板的写法只落面板、不进日志文件
    /// 与「面板只是订阅方」是同一件事。
    /// </summary>
    [Fact]
    public void WindowRaisedLines_MustGoThroughTheLogPort()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        // 唯一允许留着的是判据例外那一处：拼出来的整行（那条断言钉着它）。
        // 它的档也走记录级入口，但级别取自 Levels 的承载 —— 视图层不再出现级别字面量。
        Assert.Contains(
            "OnLine(new LogRecord(Levels.BatchFatalLevel, ScanText.ItemFailed(ex.Message)))",
            source, StringComparison.Ordinal);
        // 恰好一处：那是 允许的例外 —— 拼出来的整行，
        // BatchFatalLine_MustCarryThePrefixTheDetailAndTheLogPointer 钉着它。
        Assert.Equal(1, source.Split("OnLine(new LogRecord", StringSplitOptions.None).Length - 1);
        Assert.Contains("_log.Problem(UiText.ScanStoppedIncomplete", source, StringComparison.Ordinal);
        Assert.Contains("_log.Failure(UiText.CloseStopTimeout", source, StringComparison.Ordinal);
        Assert.Contains("_log.Note(UiText.QueueContinuesAt", source, StringComparison.Ordinal);
    }

    /// <summary>仓根：本文件只用来读源码，沿用同族测试的找法（往上找 Bbu.slnx）。</summary>
    private static string RepoRoot() => TestRepo.Root();

    private static LogRecord Result(LogLevel level, string text) => new(level, text);

    private static LogRecord Progress(double seconds, int tried, int total) => new(
        LogLevel.Debug,
        string.Format(VaultText.SearchHeartbeat, "x.rar", seconds, tried, total),
        0,
        new LogProgress(seconds, tried, total, ProgressStage.Search));

    private static void Feed(MainWindow window, LogRecord record)
    {
        typeof(MainWindow)
            .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [record]);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static string ActivityText(MainWindow window) =>
        (string)typeof(MainWindow)
            .GetMethod("ActivityProgressText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static void Set(MainWindow window, string field, object? value) =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void Call(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static IReadOnlyList<Grid> Rows(MainWindow window) =>
        window.FindControl<StackPanel>("LogHost")!.Children.OfType<Grid>().ToList();

    private static TextBlock Body(Grid row) => (TextBlock)row.Children[1];

    private static TextBlock Stamp(Grid row) => (TextBlock)row.Children[0];

    /// <summary>尺寸要在 Show 之后设：XAML 里那份基准尺寸是 InitializeComponent 写的，先设会被它盖掉。</summary>
    private static MainWindow Show()
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();

        window.Width = 1160;
        window.Height = 660;

        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return window;
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    /// <summary>落点用一次性临时目录：默认目录会覆盖作者的 <c>config.json</c>。</summary>
    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-log-").FullName;
}
