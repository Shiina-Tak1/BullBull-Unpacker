using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Ui.Avalonia.Theme;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using CoreRunner = Bbu.Core.Services.Runner;
using LogsText = Bbu.Core.Texts.LogText;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 两档自适应与工具条紧凑形。
///
/// 断言只看形态：各块的行列位置、文字的显隐、面板是否收起。
/// 宽度算得对不对由 <see cref="Breakpoints"/> 的纯函数测试负责，
/// 这里只核对视图真按那份结论摆了。
/// </summary>
public sealed class TierLayoutTests
{
    private const double WideWidth = 1160;

    /// <summary>中档且工具条仍带文字：在紧凑阈值之上。</summary>
    private const double MidWidth = 880;

    /// <summary>窗口下限，同时是最窄的中档。</summary>
    private const double CompactWidth = 700;

    /// <summary>基准宽度是宽档：任务区与运行信息并排，日志恒显，没有折叠开关。</summary>
    [AvaloniaFact]
    public void WideTier_MustPlaceTheSideRailBesideTheTaskArea()
    {
        var window = Show(WideWidth, 660);

        var body = window.FindControl<Grid>("Body")!;
        var side = window.FindControl<Grid>("Side")!;

        Assert.Equal(2, body.ColumnDefinitions.Count);
        Assert.Equal(1, Grid.GetColumn(side));
        Assert.Equal(0, Grid.GetRow(side));

        var rail = body.ColumnDefinitions[1].ActualWidth;
        Assert.True(
            rail >= Breakpoints.SideMin && rail <= Breakpoints.SideMax,
            $"右栏 {rail:0.#}px 不在 {Breakpoints.SideMin} 至 {Breakpoints.SideMax} 之间");

        Assert.False(window.FindControl<Button>("LogToggle")!.IsVisible, "宽档不该有折叠开关");
        Assert.True(window.FindControl<Border>("LogHead")!.IsVisible, "宽档的日志头部应恒显");
        Assert.True(window.FindControl<ScrollViewer>("LogScroll")!.IsVisible, "宽档的日志面板应恒显");
        Assert.False(window.FindControl<TextBlock>("FoldSummary")!.IsVisible, "宽档不折面板，摘要不该占位");

        window.Close();
    }

    /// <summary>中档单列两行：运行信息落到任务区下面，日志默认收成一行摘要。</summary>
    [AvaloniaFact]
    public void MidTier_MustFoldTheSideRailUnderTheTaskArea()
    {
        var window = Show(MidWidth, 660);

        var body = window.FindControl<Grid>("Body")!;
        var side = window.FindControl<Grid>("Side")!;

        Assert.Single(body.ColumnDefinitions);
        Assert.Equal(2, body.RowDefinitions.Count);
        Assert.Equal(0, Grid.GetColumn(side));
        Assert.Equal(1, Grid.GetRow(side));

        Assert.True(window.FindControl<Button>("LogToggle")!.IsVisible, "折叠那两档要有开关");
        Assert.False(window.FindControl<Border>("LogHead")!.IsVisible, "默认收起：日志头部不占位");
        Assert.False(window.FindControl<ScrollViewer>("LogScroll")!.IsVisible, "默认收起：日志面板不占位");

        var summary = window.FindControl<TextBlock>("FoldSummary")!;
        Assert.True(summary.IsVisible, "收起时那一行摘要要顶上来");
        Assert.False(string.IsNullOrWhiteSpace(summary.Text), "摘要行是空的，收起后这一档就没有活动信息了");
        Assert.Equal(window.FindControl<TextBlock>("ActSummary")!.Text, summary.Text);

        window.Close();
    }

    /// <summary>
    /// 收起的形态在跑任务时也要看得到进度：收起只该让日志面板让位，不该把进度行一起藏掉
    /// —— 那样同一格在宽档与窄档的信息量就不一样了（看图反馈， B-2026-150）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-150")]
    public void FoldedActivity_MustStillShowProgressWhileRunning()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        jobs[0].State = JobState.Running;
        jobs[0].Layer = 1;
        jobs[0].MaxLayer = 5;
        Field("_running").SetValue(window, true);
        Method("RebuildRows").Invoke(window, null);
        Method("RefreshActivity").Invoke(window, null);
        Settle(window);

        Assert.True(window.FindControl<TextBlock>("ActName")!.IsVisible, "收起时包名必须可见");
        Assert.True(window.FindControl<StackPanel>("ActLive")!.IsVisible, "收起时层级灯与层号那一格必须可见");
        Assert.True(window.FindControl<StackPanel>("ActTicks")!.IsVisible, "有层上限时层级灯必须可见");
        Assert.False(
            string.IsNullOrWhiteSpace(window.FindControl<TextBlock>("ActLayer")!.Text), "层号不能为空");
        var doing = window.FindControl<TextBlock>("ActDoing")!;
        Assert.True(doing.IsVisible, "收起时进度行必须可见");
        Assert.False(string.IsNullOrWhiteSpace(doing.Text), "进度行为空，收起档就看不到进度");
        Assert.False(window.FindControl<TextBlock>("FoldSummary")!.IsVisible, "运行中摘要行不可见");

        Field("_running").SetValue(window, false);
        window.Close();
    }

    /// <summary>展开与收起只翻一个状态：展开给 286 高，收起回到自适应高度。</summary>
    [AvaloniaFact]
    public void FoldToggle_MustSwapBetweenTheSummaryAndThePanel()
    {
        var window = Show(MidWidth, 660);

        var toggle = window.FindControl<Button>("LogToggle")!;
        var side = window.FindControl<Grid>("Side")!;
        var caption = window.FindControl<TextBlock>("TxtLogToggle")!;

        Assert.Equal(UiText.LogExpand, caption.Text);

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);

        Assert.Equal(Breakpoints.FoldOpen, side.Height);
        Assert.True(window.FindControl<Border>("LogHead")!.IsVisible, "展开后日志头部要回来");
        Assert.True(window.FindControl<ScrollViewer>("LogScroll")!.IsVisible, "展开后日志面板要回来");
        Assert.False(window.FindControl<TextBlock>("FoldSummary")!.IsVisible, "展开后摘要要让位");
        Assert.Equal(UiText.LogCollapse, caption.Text);

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);

        Assert.True(double.IsNaN(side.Height), "收起后高度应交回布局算");
        Assert.Equal(UiText.LogExpand, caption.Text);

        window.Close();
    }

    /// <summary>三个主操作在各档下都必须带文字：它们是这一屏唯一的出口。</summary>
    [AvaloniaFact]
    public void MainActions_MustKeepTheirTextInEveryTier()
    {
        foreach (var width in new[] { WideWidth, MidWidth, CompactWidth })
        {
            var window = Show(width, 660);

            foreach (var name in new[] { "TxtStart", "TxtPause", "TxtStop" })
            {
                var text = window.FindControl<TextBlock>(name)!;
                Assert.True(text.IsVisible, $"{width:0}px 下 {name} 被收成了纯图标");
                Assert.False(string.IsNullOrWhiteSpace(text.Text), $"{width:0}px 下 {name} 没有文字");
            }

            window.Close();
        }
    }

    /// <summary>紧凑形才收图标：宽档与中档的工具条仍带文字，紧凑阈值本身也算中档。</summary>
    [AvaloniaFact]
    public void SecondaryActions_MustLoseTheirTextOnlyInTheCompactForm()
    {
        var wide = Show(WideWidth, 660);
        Assert.True(wide.FindControl<TextBlock>("TxtSelectAll")!.IsVisible, "宽档不该收掉工具条文字");
        wide.Close();

        var mid = Show(MidWidth, 660);
        Assert.True(mid.FindControl<TextBlock>("TxtSelectAll")!.IsVisible, "中档不该收掉工具条文字");
        mid.Close();

        var compact = Show(CompactWidth, 620);
        Assert.False(compact.FindControl<TextBlock>("TxtSelectAll")!.IsVisible, "紧凑形应把工具条收成图标");
        Assert.False(compact.FindControl<TextBlock>("TxtOpenOut")!.IsVisible, "紧凑形应把打开输出目录收成图标");
        compact.Close();
    }

    /// <summary>
    /// 收成图标之后，按钮可用时提示要补上功能名 —— 否则图标没有名字可读
    /// 宽档有文字，提示留空。四个添加按钮同一款提示，逐个都要有。
    /// </summary>
    [AvaloniaFact]
    public void CompactForm_MustNameTheIconOnlyButtons()
    {
        var buttons = new[]
        {
            ("BtnAdd", UiText.AddFiles),
            ("BtnAdd2", UiText.AddFiles),
            ("BtnAddDir", UiText.AddFolder),
            ("BtnAddDir2", UiText.AddFolder),
        };

        var compact = Show(CompactWidth, 620);
        foreach (var (name, caption) in buttons)
        {
            Assert.Equal(caption, ToolTip.GetTip(compact.FindControl<Button>(name)!));
        }

        compact.Close();

        var wide = Show(WideWidth, 660);
        foreach (var (name, _) in buttons)
        {
            Assert.Null(ToolTip.GetTip(wide.FindControl<Button>(name)!));
        }

        wide.Close();
    }

    /// <summary>
    /// 开始/继续与暂停是互斥的两个按钮，换的是最左那个按钮里那段文字 ——
    /// 整块换内容会把图标一起顶掉（B-2026-113）。
    /// 〔2026-10-07 取代：原文是「暂停按钮在暂停与继续之间换文案」；按 docx 26 改成
    /// 「开始/继续在最左那个按钮上、暂停按钮恒为暂停」，两者互斥。〕
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-113")]
    public void PauseButton_MustSwapOnlyItsCaption()
    {
        var window = Show(WideWidth, 660);
        var start = window.FindControl<TextBlock>("TxtStart")!;
        var pause = window.FindControl<TextBlock>("TxtPause")!;

        Field("_running").SetValue(window, true);
        Method("TogglePause").Invoke(window, null);
        Settle(window);

        // 暂停态：最左那个写「继续」且可点，「暂停」禁用。
        Assert.Equal(UiText.Resume, start.Text);
        Assert.Equal(UiText.Pause, pause.Text);
        Assert.True(window.FindControl<Button>("BtnStart")!.IsEnabled);
        Assert.False(window.FindControl<Button>("BtnPause")!.IsEnabled);

        Method("TogglePause").Invoke(window, null);
        Settle(window);

        // 运行态：最左那个回到「开始解压」并禁用（这一趟已经在跑），「暂停」可点。
        Assert.Equal(UiText.Start, start.Text);
        Assert.False(window.FindControl<Button>("BtnStart")!.IsEnabled);
        Assert.True(window.FindControl<Button>("BtnPause")!.IsEnabled);

        // 交回未运行态再关：运行中关闭会走确认那条路，那不是本条要测的行为。
        Field("_running").SetValue(window, false);
        window.Close();
    }

    /// <summary>
    /// 整批暂停时清单里**每一行**运行态都改口：图标换掉、转圈摘掉、状态写「已暂停」；排队那一行不动。
    /// 撤掉叠加即红；只刷第一行也红 —— 第二行会留着「解压中」与转圈（B-2026-303）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-303")]
    public void PausedBatch_MustShowItOnTheRunningRow()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        jobs.Add(NewJob("b.zip", "ZIP"));
        jobs.Add(NewJob("c.zip", "ZIP"));
        jobs[0].State = JobState.Running;
        jobs[1].State = JobState.Running;
        jobs[2].State = JobState.Queued;
        Field("_running").SetValue(window, true);
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var runningIcon = RowIcon(window, 0).Data!.Bounds;
        var queuedIcon = RowIcon(window, 2).Data!.Bounds;

        Method("TogglePause").Invoke(window, null);
        Settle(window);

        foreach (var index in new[] { 0, 1 })
        {
            Assert.Equal(UiText.RowPaused, RowStatus(window, index));
            Assert.DoesNotContain("spin", RowIcon(window, index).Classes);
            Assert.NotEqual(runningIcon, RowIcon(window, index).Data!.Bounds);
        }

        // 排队那一行不跟着改：暂停只罩着正在跑的那几条。
        Assert.Equal(CoreRunner.StatusLabel(jobs[2]), RowStatus(window, 2));
        Assert.Equal(queuedIcon, RowIcon(window, 2).Data!.Bounds);

        Method("TogglePause").Invoke(window, null);
        Settle(window);

        foreach (var index in new[] { 0, 1 })
        {
            Assert.Equal(CoreRunner.StatusLabel(jobs[index]), RowStatus(window, index));
            Assert.Contains("spin", RowIcon(window, index).Classes);
        }

        Field("_running").SetValue(window, false);
        window.Close();
    }

    /// <summary>
    /// 一次暂停、一次继续各只出一条日志：十几个引擎实例各打一条会把面板刷满（B-2026-304）。
    /// 撤掉批级这一条即红 —— 界面里一条都不剩（引擎那两条走排错档，界面忽略它）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-304")]
    public void PauseAndResume_MustLogOnceEach()
    {
        // 判据要收日志行：两参构造拿到的是不落盘的替身，日志实例必须显式注入。
        var log = new RunLog(Path.Combine(TempDir(), AppInfo.LogFileName));
        var window = Show(MidWidth, 660, log);
        var seen = new List<LogRecord>();
        log.LineWritten += line => seen.Add(line);

        Field("_running").SetValue(window, true);
        Method("TogglePause").Invoke(window, null);
        Settle(window);
        Method("TogglePause").Invoke(window, null);
        Settle(window);

        Assert.Equal(1, seen.Count(line => line.Text == LogsText.BatchPaused));
        Assert.Equal(1, seen.Count(line => line.Text == LogsText.BatchResumed));

        Field("_running").SetValue(window, false);
        window.Close();
    }

    /// <summary>
    /// 中档在窗口下限也成立：四列之和等于可视宽，且各列不窄于其表头所需宽度。
    /// 两条同时成立，同一行的格子才与表头对齐、文字不重叠（B-2026-124）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-124")]
    public void ListColumns_MustFitTheViewportAtTheMinimumWidth()
    {
        foreach (var (width, height) in new[] { (CompactWidth, 620.0), (MidWidth, 660.0) })
        {
            var window = Show(width, height);

            var head = window.FindControl<Grid>("HeadCols")!;
            var list = window.FindControl<ScrollViewer>("ListScroll")!;
            var widths = head.ColumnDefinitions.Select(c => c.ActualWidth).ToArray();

            Assert.Equal(ColumnLayout.Count, widths.Length);
            Assert.All(widths, w => Assert.True(w > 0, $"{width:0}px 下有列被压成了零宽"));

            var need = new[] { "HeadKind", "HeadFile", "HeadPassword", "HeadStatus" }
                .Select(name =>
                {
                    var block = window.FindControl<TextBlock>(name)!;
                    block.Measure(Size.Infinity);
                    return block.DesiredSize.Width;
                })
                .ToArray();
            for (var i = 0; i < need.Length; i++)
            {
                Assert.True(
                    widths[i] >= need[i],
                    $"{width:0}px 下第 {i + 1} 列只有 {widths[i]:0.#}px，表头需要 {need[i]:0.#}px");
            }

            var box = list.Viewport.Width - (2 * ColumnLayout.RowPad);
            Assert.True(
                Math.Abs(widths.Sum() - box) < 1.0,
                $"{width:0}px 下四列之和 {widths.Sum():0.#}px 与内容盒宽 {box:0.#}px 对不上");

            // 行与表头必须共用同一份列宽。旧的最窄一档里表头与行都是两列，而建行时按四列那份配置
            // 写宽度，行的文字就会与表头边界错开（B-2026-124 的成因）。
            var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
            jobs.Clear();
            jobs.Add(NewJob("a.zip", "ZIP"));
            jobs.Add(NewJob("b.7z", "7Z"));
            Method("RebuildRows").Invoke(window, null);
            Settle(window);

            foreach (var grid in (List<Grid>)Field("_rowCols").GetValue(window)!)
            {
                var rowWidths = grid.ColumnDefinitions.Select(c => c.ActualWidth).ToArray();
                Assert.Equal(widths.Length, rowWidths.Length);
                Assert.All(
                    rowWidths.Zip(widths, (row, headWidth) => Math.Abs(row - headWidth)),
                    gap => Assert.True(gap < 1.0, $"行的列宽与表头差 {gap:0.#}px，同一行的文字会与表头错位"));
            }

            window.Close();
        }
    }

    /// <summary>
    /// 类型列等宽：同一批里不同类型标签的行，类型格的实际宽度一致 ——
    /// 行首展开箭头去掉之后，最左即为类型格（docx 18）。
    /// </summary>
    [AvaloniaFact]
    public void KindCells_MustShareTheSameWidth()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        jobs.Add(NewJob("b.mp4", "内嵌"));
        jobs.Add(NewJob("c.part1.rar", "分卷"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var rows = ((List<Grid>)Field("_rowCols").GetValue(window)!).ToArray();
        Assert.Equal(jobs.Count, rows.Length);

        var widths = rows.Select(grid => grid.ColumnDefinitions[0].ActualWidth).Distinct().ToArray();
        Assert.Single(widths);
        Assert.True(widths[0] > 0, "类型格被压成了零宽");

        window.Close();
    }

    /// <summary>
    /// 筛选与顶栏那条进度构成条同列，并且点它仍套用对应筛选 ——
    /// 位置变更后，联动仍须保留（docx 10）。
    /// </summary>
    [AvaloniaFact]
    public void Filter_MustSitBesideTheProgressBarAndStillApply()
    {
        var window = Show(WideWidth, 660);

        foreach (var name in new[] { "SegAll", "SegFail" })
        {
            // 两个筛选按钮同属一个容器，量取该容器的列号。
            var group = (Control)window.FindControl<ToggleButton>(name)!.Parent!;
            Assert.Equal(2, Grid.GetColumn(group));
        }

        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("ok.zip", "ZIP"));
        jobs.Add(NewJob("bad.zip", "ZIP"));
        jobs[1].State = JobState.Failed;
        Method("RebuildRows").Invoke(window, null);
        Settle(window);
        Assert.Equal(2, VisibleRows(window));

        window.FindControl<ToggleButton>("SegFail")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(1, VisibleRows(window));

        window.Close();
    }

    /// <summary>
    /// 槽位标题恒为「当前活动」：空清单、有上一批结果、跑批中三种情形下都不变 ——
    /// 该标题描述这一格的用途，不随格内当前显示哪一批而变（docx 21）。
    /// </summary>
    [AvaloniaFact]
    public void ActivityTitle_MustStayTheSameInEveryState()
    {
        var window = Show(WideWidth, 660);
        var title = window.FindControl<TextBlock>("ActTitle")!;
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;

        jobs.Clear();
        Method("RebuildRows").Invoke(window, null);
        Method("RefreshActivity").Invoke(window, null);
        Assert.Equal(UiText.ActivityTitle, title.Text);

        jobs.Add(NewJob("done.zip", "ZIP"));
        Field("_lastBatchText").SetValue(window, "5 完成 · 1 失败");
        Method("RebuildRows").Invoke(window, null);
        Method("RefreshActivity").Invoke(window, null);
        Assert.Equal(UiText.ActivityTitle, title.Text);

        jobs[0].State = JobState.Running;
        Field("_running").SetValue(window, true);
        Method("RebuildRows").Invoke(window, null);
        Method("RefreshActivity").Invoke(window, null);
        Assert.Equal(UiText.ActivityTitle, title.Text);

        Field("_running").SetValue(window, false);
        window.Close();
    }

    /// <summary>
    /// 行内只有一行：说明与原因挂状态格的提示，行里不再有第二行 ——
    /// 清单行是宿主面板的直接子元素，中间没有包装层（作者看图反馈，2026-10-05）。
    /// </summary>
    [AvaloniaFact]
    public void ListRow_MustStayOneLine()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var panel = window.FindControl<StackPanel>("RowHost")!;
        var root = Assert.IsType<Border>(Assert.Single(panel.Children));
        var grid = Assert.IsType<Grid>(root.Child);
        Assert.Equal(ColumnLayout.Count, grid.ColumnDefinitions.Count);

        window.Close();
    }

    /// <summary>
    /// 状态格只留状态词（外加文件夹行的子项数）：灯条、耗时与层号都归右栏的活动槽，
    /// 在清单里重复一遍既冗余，又把这一列挤宽（作者看图反馈，2026-10-05）。
    /// </summary>
    [AvaloniaFact]
    public void StatusCell_MustCarryOnlyTheStateWord()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var rows = (System.Collections.IEnumerable)Field("_rows").GetValue(window)!;
        foreach (var row in rows)
        {
            var cell = (StackPanel)row!.GetType().GetField("StatusCell")!.GetValue(row)!;
            Assert.Equal(3, cell.Children.Count);
        }

        window.Close();
    }

    /// <summary>清空之后右栏回到「未添加」：上一批的收尾摘要不得保留（B-2026-127）。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-127")]
    public async System.Threading.Tasks.Task ClearList_MustResetTheActivitySummary()
    {
        var window = Show(WideWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Field("_lastBatchText").SetValue(window, "本批结束：完成 0 · 失败 0 · 已跳过 1");
        Method("RefreshActivity").Invoke(window, null);
        Settle(window);
        Assert.Equal(
            "本批结束：完成 0 · 失败 0 · 已跳过 1",
            window.FindControl<TextBlock>("ActSummary")!.Text);

        await (System.Threading.Tasks.Task)Method("ClearListAsync").Invoke(window, null)!;
        Settle(window);

        Assert.Equal(UiText.ActivityIdle, window.FindControl<TextBlock>("ActSummary")!.Text);
        window.Close();
    }

    /// <summary>
    /// 运行中那一个图标旋转：样式表给 <c>Path.spin</c>，代码按行状态挂类。
    /// </summary>
    [AvaloniaFact]
    public void RunningIcon_MustSpinWhileRunning()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        jobs[0].State = JobState.Running;
        Method("RebuildRows").Invoke(window, null);
        Settle(window);
        Assert.Contains("spin", RowIcon(window).Classes);

        jobs[0].State = JobState.Done;
        Method("RefreshAllRows").Invoke(window, null);
        Assert.DoesNotContain("spin", RowIcon(window).Classes);

        window.Close();
    }

    /// <summary>
    /// 每列只有一种提示，而且挂在整格上：类型格给全称、密码格给点击复制、
    /// 状态格只在跳过 / 部分 / 失败时解释原因；文件格不给（作者看图反馈，2026-10-05）。
    /// </summary>
    [AvaloniaFact]
    public void RowTips_MustBeOnePerColumn()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var grid = ((List<Grid>)Field("_rowCols").GetValue(window)!)[0];
        Assert.NotNull(ToolTip.GetTip(grid.Children[0]));
        Assert.Null(ToolTip.GetTip(grid.Children[1]));
        Assert.NotNull(ToolTip.GetTip(grid.Children[2]));
        Assert.Null(ToolTip.GetTip(grid.Children[3]));

        jobs[0].State = JobState.Skipped;
        jobs[0].Reason = "已有同名目录";
        Method("RefreshAllRows").Invoke(window, null);
        Settle(window);
        Assert.NotNull(ToolTip.GetTip(grid.Children[3]));

        window.Close();
    }

    /// <summary>
    /// 行内四列各自都带色阶类：漏一列时该列继承窗口最亮的前景色，成为整张表中唯一高亮的一列（B-2026-129）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-129")]
    public void RowCells_MustEachCarryTheirOwnColorClass()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var grid = ((List<Grid>)Field("_rowCols").GetValue(window)!)[0];
        var kindCell = Assert.IsType<Border>(grid.Children[0]);
        var kind = Assert.IsType<TextBlock>(kindCell.Child);
        Assert.Contains("faint", kind.Classes);
        Assert.Contains("dim", grid.Children[1].Classes);

        window.Close();
    }

    /// <summary>
    /// 全选那个按钮是开关：清单已经全选时再点一次就是取消，按钮文字也跟着换。
    /// 单向的全选只能加不能减，取消选中要逐行点。
    /// </summary>
    [AvaloniaFact]
    public void SelectAllButton_MustToggleBack()
    {
        var window = Show(WideWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        jobs.Add(NewJob("b.7z", "7Z"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var button = window.FindControl<Button>("BtnSelectAll")!;
        var caption = window.FindControl<TextBlock>("TxtSelectAll")!;
        Assert.Equal(UiText.SelectAll, caption.Text);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(2, SelectedRows(window));
        Assert.Equal(UiText.DeselectAll, caption.Text);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(0, SelectedRows(window));
        Assert.Equal(UiText.SelectAll, caption.Text);

        window.Close();
    }

    /// <summary>清单里当前处于选中态的行数。</summary>
    private static int SelectedRows(MainWindow window)
    {
        var rows = (System.Collections.IEnumerable)Field("_rows").GetValue(window)!;
        var count = 0;
        foreach (var row in rows)
        {
            var root = (Border)row!.GetType().GetField("Root")!.GetValue(row)!;
            if (root.Classes.Contains("sel"))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>行内的状态图标：位于状态格内，是该行唯一的 ShapePath。</summary>
    private static global::Avalonia.Controls.Shapes.Path RowIcon(MainWindow window, int index = 0)
    {
        var row = Row(window, index);
        return (global::Avalonia.Controls.Shapes.Path)row.GetType().GetField("Icon")!.GetValue(row)!;
    }

    /// <summary>清单里当前可见的行数。过滤改的是每行行根的可见性，不重建表。</summary>
    private static int VisibleRows(MainWindow window)
    {
        var rows = (System.Collections.IEnumerable)Field("_rows").GetValue(window)!;
        var count = 0;
        foreach (var row in rows)
        {
            var root = (Border)row!.GetType().GetField("Root")!.GetValue(row)!;
            if (root.IsVisible)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>挂了入场类的行数。</summary>
    private static int EnteringRows(MainWindow window)
    {
        var rows = (System.Collections.IEnumerable)Field("_rows").GetValue(window)!;
        var count = 0;
        foreach (var row in rows)
        {
            var root = (Border)row!.GetType().GetField("Root")!.GetValue(row)!;
            if (root.Classes.Contains("enter"))
            {
                count++;
            }
        }

        return count;
    }

    private static Job NewJob(string name, string kind) => new()
    {
        ArchivePath = Path.Combine(@"C:\示例", name),
        Kind = kind,
        KindShort = kind,
        DisplayName = name,
    };

    /// <summary>清单第 <paramref name="index"/> 行的状态文案。</summary>
    private static string? RowStatus(MainWindow window, int index = 0)
    {
        var row = Row(window, index);
        return ((TextBlock)row.GetType().GetField("Status")!.GetValue(row)!).Text;
    }

    /// <summary>清单第 <paramref name="index"/> 行那个行对象。</summary>
    private static object Row(MainWindow window, int index = 0) =>
        ((System.Collections.IEnumerable)Field("_rows").GetValue(window)!).Cast<object>().ElementAt(index);

    /// <summary>断点归属：边界值归上一档；窗口下限落在中档，工具条紧凑形另行判定。</summary>
    [Fact]
    public void Breakpoints_MustAssignTheBoundaryToTheUpperTier()
    {
        Assert.Equal(ShellTier.Wide, Breakpoints.Of(Breakpoints.WideMin));
        Assert.Equal(ShellTier.Mid, Breakpoints.Of(Breakpoints.WideMin - 1));
        Assert.Equal(ShellTier.Mid, Breakpoints.Of(700));

        Assert.False(Breakpoints.Compact(Breakpoints.CompactMin));
        Assert.True(Breakpoints.Compact(Breakpoints.CompactMin - 1));
    }

    /// <summary>右栏宽度随内容区变，但始终夹在 380 至 460 之间，且不是那个死值 420。</summary>
    [Fact]
    public void SideWidth_MustStayInsideTheRangeAndFollowTheWidth()
    {
        foreach (var width in new[] { 1000.0, 1160.0, 1400.0 })
        {
            var rail = Breakpoints.SideWidth(width);
            Assert.True(
                rail >= Breakpoints.SideMin && rail <= Breakpoints.SideMax,
                $"{width:0}px 下右栏算出 {rail:0.#}px");
        }

        Assert.Equal(Breakpoints.SideMin, Breakpoints.SideWidth(1000));
        Assert.Equal(Breakpoints.SideMax, Breakpoints.SideWidth(1400));
        Assert.NotEqual(420, Breakpoints.SideWidth(WideWidth));
        Assert.True(
            Breakpoints.SideWidth(WideWidth) > Breakpoints.SideWidth(1000),
            "右栏宽度没有跟着内容区变，还是死值");
    }

    /// <summary>入场动画只给前 12 行：100 个任务不该产生 100 个动画对象。</summary>
    [AvaloniaFact]
    public void RowEnter_MustCapAtTheFirstTwelve()
    {
        var window = Show(MidWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        for (var i = 0; i < 20; i++)
        {
            jobs.Add(NewJob($"{i}.zip", "ZIP"));
        }

        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        Assert.Equal(12, EnteringRows(window));
        window.Close();
    }

    /// <summary>
    /// 运行中四个添加按钮都可点：收进来的项排到清单里，等这一批跑完接着处理（与拖入同一个后果）。
    /// 同时钉住另外三个**仍然**不可点 —— 清空与移除要等扫描／跑批把清单写完，打开产物目录要有产物，
    /// 把它们也放开是越界。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-250")]
    public void Running_MustLeaveTheAddButtonsClickable()
    {
        var window = Show(WideWidth, 660);
        Field("_running").SetValue(window, true);
        Method("RefreshChrome").Invoke(window, null);

        foreach (var name in new[] { "BtnAdd", "BtnAddDir", "BtnAdd2", "BtnAddDir2" })
        {
            Assert.True(window.FindControl<Button>(name)!.IsEnabled, $"运行中「{name}」是灰的，加不进来");
        }

        foreach (var name in new[] { "BtnClearList", "BtnRemoveSelected", "BtnOutSum" })
        {
            Assert.False(window.FindControl<Button>(name)!.IsEnabled, $"运行中「{name}」不该可点");
        }

        window.Close();
    }

    /// <summary>
    /// 往右拖最右那条分界线就是压状态列：拖多少次都停在它自己的内容下限上（B-2026-310）。
    /// 走的是把手真正调用的那个入口，只绕开 Thumb 自身的接线。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void ListColumns_MustStopAtTheFloorWhenDraggedToTheEnd()
    {
        var window = Show(WideWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(NewJob("a.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        for (var i = 0; i < 3; i++)
        {
            Method("DragColumn").Invoke(window, new object[] { 2, 400.0 });
        }

        Settle(window);

        var widths = ColumnWidthsOf(window);
        var head = widths[0];
        Assert.All(
            widths,
            w => Assert.True(
                w[3] >= ColumnLayout.StatusContentFloor,
                $"状态列被拖到 {w[3]:0.#}px，低于内容下限 {ColumnLayout.StatusContentFloor}px"));
        foreach (var row in widths.Skip(1))
        {
            Assert.All(
                row.Zip(head, (r, h) => Math.Abs(r - h)),
                gap => Assert.True(gap < 1.0, $"行的列宽与表头差 {gap:0.#}px"));
        }

        window.Close();
    }

    /// <summary>
    /// 盘上那份窄列宽回读时就要抬到下限上：这是「关掉软件再打开，下限仍在」那条症状（B-2026-310）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void ListColumns_MustHoldTheFloorAfterReload()
    {
        var dir = TempDir();
        var store = new JsonConfigStore(Path.Combine(dir, AppInfo.ConfigFileName));
        var config = store.Load();
        config.TableColumnWidths = [56, 56, 56, 56];
        Assert.True(store.Save(config), "写不进那份配置，这一条就量不到回读路径");

        var window = new MainWindow(dir, dir);
        var loaded = (double[])Field("_cols").GetValue(window)!;
        Assert.True(
            loaded[3] >= ColumnLayout.StatusContentFloor,
            $"回读那一刻状态列就是 {loaded[3]:0.#}px，低于内容下限 {ColumnLayout.StatusContentFloor}px");

        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        window.Width = WideWidth;
        window.Height = 660;
        Settle(window);

        Assert.All(
            ColumnWidthsOf(window),
            w => Assert.True(
                w[3] >= ColumnLayout.StatusContentFloor,
                $"回读之后状态列是 {w[3]:0.#}px，低于内容下限 {ColumnLayout.StatusContentFloor}px"));

        window.Close();
    }

    /// <summary>
    /// 表头右键「重置」按内容盒宽重摊：四列之和等于内容盒宽，且逐列不低于自己的下限（B-2026-310）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void MenuReset_MustRefitToTheContentBoxAndKeepTheFloor()
    {
        var window = Show(WideWidth, 660);
        Method("DragColumn").Invoke(window, new object[] { 2, 400.0 });
        Settle(window);

        window.FindControl<MenuItem>("MenuReset")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(window);

        var floors = (double[])Method("ColumnFloors").Invoke(window, null)!;
        var list = window.FindControl<ScrollViewer>("ListScroll")!;
        var box = list.Viewport.Width - (2 * ColumnLayout.RowPad);
        foreach (var widths in ColumnWidthsOf(window))
        {
            Assert.True(
                Math.Abs(widths.Sum() - box) < 1.0,
                $"重置之后四列之和 {widths.Sum():0.#}px 与内容盒宽 {box:0.#}px 对不上");
            for (var i = 0; i < ColumnLayout.Count; i++)
            {
                Assert.True(
                    widths[i] >= floors[i] - 1.0,
                    $"重置之后第 {i + 1} 列是 {widths[i]:0.#}px，低于下限 {floors[i]:0.#}px");
            }
        }

        window.Close();
    }

    /// <summary>
    /// 四列之和放进清单区内容盒：表头那侧不越出它的裁剪盒，行那侧不越出视口（B-2026-310）。
    /// 配合「最坏内容不宽过它自己的列」，居中的内容才整段可见。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void ListColumns_MustNotSqueezeOutTheContentBox()
    {
        var window = Show(WideWidth, 660);
        var list = window.FindControl<ScrollViewer>("ListScroll")!;
        var headArea = window.FindControl<Panel>("HeadArea")!;
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        for (var i = 0; i < 3; i++)
        {
            jobs.Add(NewJob($"{i}.zip", "ZIP"));
        }

        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var widths = ColumnWidthsOf(window);
        var head = widths[0];
        var box = list.Viewport.Width - (2 * ColumnLayout.RowPad);
        Assert.True(
            head.Sum() <= box + 1.0,
            $"四列之和 {head.Sum():0.#}px 越出内容盒 {box:0.#}px，最右一列会被裁");
        Assert.True(
            head.Sum() <= headArea.Bounds.Width + 1.0,
            $"表头 {head.Sum():0.#}px 越出裁剪盒 {headArea.Bounds.Width:0.#}px");
        Assert.All(
            widths.Skip(1),
            w => Assert.True(Math.Abs(w.Sum() - head.Sum()) < 1.0, "行与表头的总宽不同源"));

        window.Close();
    }

    /// <summary>
    /// 滚动条出现与消失之后仍要满足上一条：它只改视口宽，ScrollViewer 自身尺寸不变（B-2026-310）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void ListColumns_MustStayInsideTheContentBoxAfterTheScrollBarAppears()
    {
        var window = Show(WideWidth, 660);
        var list = window.FindControl<ScrollViewer>("ListScroll")!;
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        Settle(window);

        var empty = list.Viewport.Width - (2 * ColumnLayout.RowPad);
        Assert.True(
            Math.Abs(ColumnWidthsOf(window)[0].Sum() - empty) < 1.0,
            $"空清单时四列之和与内容盒宽 {empty:0.#}px 就对不上");

        for (var i = 0; i < 40; i++)
        {
            jobs.Add(NewJob($"{i}.zip", "ZIP"));
        }

        Method("RebuildRows").Invoke(window, null);
        Settle(window);
        Assert.True(
            list.Extent.Height > list.Viewport.Height,
            "这 40 行没有溢出，滚动条没出现，这一条就没量到该量的事");

        var filled = list.Viewport.Width - (2 * ColumnLayout.RowPad);
        Assert.True(
            Math.Abs(ColumnWidthsOf(window)[0].Sum() - filled) < 1.0,
            $"滚动条出现之后四列之和与内容盒宽 {filled:0.#}px 对不上，最右一列会被裁");

        jobs.Clear();
        Method("RebuildRows").Invoke(window, null);
        Settle(window);
        jobs.Add(NewJob("b.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var back = list.Viewport.Width - (2 * ColumnLayout.RowPad);
        Assert.True(
            Math.Abs(ColumnWidthsOf(window)[0].Sum() - back) < 1.0,
            $"清空再加回来之后四列之和与内容盒宽 {back:0.#}px 对不上");

        // 再钉一次「清单内容变化之后重算」这条接线：把列宽人为置为过期值，
        // 视口没变时只有那条接线能把它拉回内容盒（headless 的滚动条是叠加式的，视口宽不缩）。
        Field("_cols").SetValue(window, new double[] { 200, 300, 200, 200 });
        jobs.Add(NewJob("c.zip", "ZIP"));
        Method("RebuildRows").Invoke(window, null);
        Settle(window);
        var settled = (double[])Field("_cols").GetValue(window)!;
        Assert.True(
            Math.Abs(settled.Sum() - back) < 1.0,
            $"清单变化之后列宽没被拉回内容盒宽 {back:0.#}px，而是 {settled.Sum():0.#}px");
        Assert.True(
            Math.Abs(ColumnWidthsOf(window)[0].Sum() - back) < 1.0,
            $"拉回来的列宽没写进表头，表头仍是 {ColumnWidthsOf(window)[0].Sum():0.#}px");

        window.Close();
    }

    /// <summary>四列下限之和必须小于内容盒宽，否则「四列之和恒等于内容盒宽」不成立。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void ColumnFloors_MustFitInsideTheTaskArea()
    {
        var window = Show(WideWidth, 660);
        var list = window.FindControl<ScrollViewer>("ListScroll")!;
        var floors = (double[])Method("ColumnFloors").Invoke(window, null)!;
        var box = list.Viewport.Width - (2 * ColumnLayout.RowPad);

        Assert.True(
            floors.Sum() < box,
            $"四列下限之和 {floors.Sum():0.#}px 不小于内容盒宽 {box:0.#}px，摊不进去");

        window.Close();
    }

    /// <summary>
    /// 最宽的状态格不许超过 <see cref="ColumnLayout.StatusContentFloor"/>：常量是量出来的，不是猜的（B-2026-310）。
    /// 取最坏的两种行：文件夹汇总行的子项数、不可运行行的状态词。它一超，那条下限就兜不住内容，
    /// 该按实测改常量，而不是把断言放松。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void StatusCell_WidestContent_MustFitInTheStatusFloor()
    {
        var window = Show(WideWidth, 660);
        var jobs = (List<Job>)Field("_jobs").GetValue(window)!;
        jobs.Clear();
        jobs.Add(new Job
        {
            ArchivePath = Path.Combine(@"C:\示例", "文件夹"),
            Kind = "文件夹",
            KindShort = "文件夹",
            DisplayName = "文件夹",
            IsDir = true,
            ChildCount = 999,
        });
        jobs.Add(new Job
        {
            ArchivePath = Path.Combine(@"C:\示例", "不会处理.zip"),
            Kind = "ZIP",
            KindShort = "ZIP",
            DisplayName = "不会处理.zip",
            Runnable = false,
        });
        Method("RebuildRows").Invoke(window, null);
        Settle(window);

        var widest = 0.0;
        foreach (var row in (System.Collections.IEnumerable)Field("_rows").GetValue(window)!)
        {
            var cell = (StackPanel)row!.GetType().GetField("StatusCell")!.GetValue(row)!;
            cell.Measure(Size.Infinity);
            widest = Math.Max(widest, cell.DesiredSize.Width);
        }

        Assert.True(widest > 0, "两行都没量到宽度，这一条没量到该量的事");
        Assert.True(
            widest <= ColumnLayout.StatusContentFloor,
            $"最宽的状态格要 {widest:0.#}px，超过常量 {ColumnLayout.StatusContentFloor}px");

        var cols = (double[])Field("_cols").GetValue(window)!;
        Assert.True(widest <= cols[3] + 1.0, $"状态格 {widest:0.#}px 宽过它那一列 {cols[3]:0.#}px");

        window.Close();
    }

    /// <summary>
    /// 建窗那一刻表头与行就同源：还没布局，表头的列定义已经按 _cols 写宽（B-2026-310）。
    /// 不同源时开窗第一帧看到的是表头停在 XAML 字面量上，与行逐列错位。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void HeadColumns_MustShareTheSameWidthsBeforeTheFirstLayout()
    {
        var dir = TempDir();
        var store = new JsonConfigStore(Path.Combine(dir, AppInfo.ConfigFileName));
        var config = store.Load();
        config.TableColumnWidths = [96, 288, 168, 190];
        Assert.True(store.Save(config), "写不进那份配置，这一条就量不到同源");

        var window = new MainWindow(dir, dir);
        var cols = (double[])Field("_cols").GetValue(window)!;
        var head = window.FindControl<Grid>("HeadCols")!;
        var headWidths = head.ColumnDefinitions.Select(c => c.Width.Value).ToArray();

        Assert.Equal(cols, headWidths);
    }

    /// <summary>清单各处的列宽：第一份是表头，其余是每一行。</summary>
    private static List<double[]> ColumnWidthsOf(MainWindow window)
    {
        var result = new List<double[]>
        {
            window.FindControl<Grid>("HeadCols")!.ColumnDefinitions.Select(c => c.ActualWidth).ToArray(),
        };
        foreach (var grid in (List<Grid>)Field("_rowCols").GetValue(window)!)
        {
            result.Add(grid.ColumnDefinitions.Select(c => c.ActualWidth).ToArray());
        }

        return result;
    }

    /// <summary>
    /// 落点是一次性临时目录；要收日志行的那几条显式传一份真 logger，其余用不写盘的替身。
    /// 令牌与样式表显式装：headless 宿主不预装这两份，列宽与字号都靠它们。
    /// </summary>
    private static MainWindow Show(double width, double height, IRunLog? log = null)
    {
        var window = new MainWindow(TempDir(), TempDir(), log);
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();

        // 尺寸要在 Show 之后设：XAML 里那份基准尺寸是 InitializeComponent 写的，先设会被它盖掉。
        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    private static ResourceDictionary Tokens() =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static Styles Controls() =>
        (Styles)AvaloniaXamlLoader.Load(new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    /// <summary>布局要跑够几轮：档位翻转会重建列定义，一轮量不到新尺寸。</summary>
    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static FieldInfo Field(string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static MethodInfo Method(string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>落点用一次性临时目录：默认目录会覆盖作者的 <c>config.json</c>。</summary>
    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-tier-").FullName;
}
