using System;
using System.IO;
using System.Linq;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// core→UI 的日志与进度契约：进度行带结构、构成条六段取 SegmentToken 推的令牌、
/// 活动槽不自己数。这几条破了都不会报错，只会显示成另一个样子，所以钉在结构上。
/// </summary>
public sealed class ProgressContractTests
{
    /// <summary>进度行带的四个数：界面拿得到秒数、已试个数、总数与阶段，不必解析正文。</summary>
    [Fact]
    public void LogProgress_MustCarryTheFourFacts()
    {
        var progress = typeof(LogProgress);
        var missing = new[] { "Seconds", "Tried", "Total", "Stage" }
            .Where(name => progress.GetProperty(name) is null)
            .ToList();

        Assert.Empty(missing);
        Assert.NotNull(typeof(LogRecord).GetProperty("Progress"));
        Assert.NotNull(typeof(ProgressStage).GetField("Scan"));
        Assert.NotNull(typeof(ProgressStage).GetField("Search"));

        // 2026-10-08 新增的一档：真实解压百分比。撤掉它，活动槽只能显示秒表。
        Assert.NotNull(typeof(ProgressStage).GetField("Extract"));
        Assert.Equal(100, LogProgress.Extracting(1.0, 42).Total);
        Assert.Equal(42, LogProgress.Extracting(1.0, 42).Tried);
        Assert.Equal(ProgressStage.Extract, LogProgress.Extracting(1.0, 42).Stage);

        // 2026-10-13（-11）新增的一档：扫描清单。两个数是已扫目录数与已扫文件数，
        // 扫描是枚举、没有分母，所以这一档不成比例。
        var scan = LogProgress.Scanning(12, 340);
        Assert.Equal(ProgressStage.Scan, scan.Stage);
        Assert.Equal(12, scan.Tried);
        Assert.Equal(340, scan.Total);
    }

    /// <summary>
    /// 成员集合钉死。界面那几处分支是两选一，加成员会**静默**落进 else ——
    /// 新的一档会被渲染成密码枚举进度，且活动槽门禁只认「有没有结构」不看阶段。
    /// 加成员必须显式来改这张清单与那几处分支，照 ` ` 的先例。
    /// </summary>
    [Fact]
    public void ProgressStage_MustStayExhaustive()
    {
        Assert.Equal(
            new[] { ProgressStage.Scan, ProgressStage.Search, ProgressStage.Extract },
            Enum.GetValues<ProgressStage>());
    }

    /// <summary>
    /// 活动槽那一行要把真实百分比摆出来，而不是只写「解压中 x.x秒」：
    /// 这是 2026-10-08 换掉秒表的那一条，撤掉即退回旧观感。
    /// 层号与耗时都不在这一行重复（看图反馈：层号上方已有专门区域）。
    /// </summary>
    [Fact]
    public void ActivitySlot_MustRenderTheEnginePercent()
    {
        var code = ViewCode("MainWindow.axaml.cs");
        var at = code.IndexOf("ProgressStage.Extract", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到解压进度那一分支");

        var block = code[at..code.IndexOf("return", at, StringComparison.Ordinal)];
        var statement = code[at..code.IndexOf('}', code.IndexOf("return", at, StringComparison.Ordinal))];

        Assert.Contains("UiText.ActivityExtractAt", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivityElapsedAt", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("Layer", block, StringComparison.Ordinal);
    }

    /// <summary>带结构的那条通道要出现在接口上：少了它，界面只能回去解析正文。</summary>
    [Fact]
    public void RunLog_MustOfferTheStructuredProgressChannel()
    {
        var method = typeof(IRunLog)
            .GetMethod("Progress", [typeof(LogProgress), typeof(string)]);

        Assert.NotNull(method);
    }

    /// <summary>
    /// 构成条六段的底色取 SegmentToken 推的令牌，顺序是成功、部分、跳过、失败、进行中、待处理 ——
    /// 完成的从左边开始填，未完成的两档固定在右边（docx 19b + 的口径）。
    /// </summary>
    [Fact]
    public void SegmentColors_MustFollowTheLevelTable()
    {
        var xaml = View("MainWindow.axaml");

        foreach (var (name, token, column) in new (string Name, string Token, int Column)[]
        {
            ("ProgSegDone", "Ok", 0),
            ("ProgSegPartial", "Warn", 1),
            ("ProgSegSkipped", "Warn", 2),
            ("ProgSegFailed", "Err", 3),
            ("ProgSegRunning", "Accent", 4),
            ("ProgSegQueued", "Dim", 5),
        })
        {
            var element = Element(xaml, name);
            Assert.Contains($"Background=\"{{DynamicResource {token}}}\"", element, StringComparison.Ordinal);
            Assert.Contains($"Grid.Column=\"{column}\"", element, StringComparison.Ordinal);
        }

        // 映射只有一处：段色从状态推，视图里不留第二份；档的取值由 钉住，这里只判名字在不在。
        var levels = ViewCode("Levels.cs");
        Assert.Contains("SegmentToken", levels, StringComparison.Ordinal);
        Assert.Contains("RunLog.StateLevel", levels, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一条都没有时六段收起：全为 0* 时 Grid 会等分铺满，清空列表后条上留下六段残色。
    /// 构成条也不套用筛选 —— 它是计数，不是筛列表的入口。
    /// </summary>
    [Fact]
    public void EmptyProgressBar_MustCollapseAndNotFilter()
    {
        var code = ViewCode("MainWindow.axaml.cs");

        Assert.Contains("new GridLength(0)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HookSegment", code, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Border.pfill.clickable",
            File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 进度不进面板：面板下沿那一条浮动心跳与活动槽说的是同一件事（docx 24）。
    /// </summary>
    [Fact]
    public void Progress_MustStayOutOfThePanel()
    {
        Assert.DoesNotContain("ProgRow", View("MainWindow.axaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("ProgRow", ViewCode("MainWindow.axaml.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 活动槽那一行的级别由这一条进度自己带：引擎心跳取中档、密码枚举取提示档。
    /// 标记里写死前景色会让两种阶段看起来一模一样（docx 19b）。
    /// </summary>
    [Fact]
    public void ActivitySlot_MustTakeItsLevelFromTheProgress()
    {
        Assert.Contains(
            "<TextBlock Name=\"ActDoing\" Classes=\"lvl-info\"", View("MainWindow.axaml"), StringComparison.Ordinal);
        Assert.Contains("SetLevelClass(ActDoing, record.Level);", ViewCode("MainWindow.axaml.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 构成条跟着每一项的状态走：条要一段一段长出来，等整批跑完才出现整条等于没给进度。
    /// </summary>
    [Fact]
    public void ProgressBar_MustFollowEveryItemUpdate()
    {
        var code = ViewCode("MainWindow.axaml.cs");
        var at = code.IndexOf("OnItem = job =>", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到任务项刷新的接线");

        var block = code[at..code.IndexOf("}),", at, StringComparison.Ordinal)];
        Assert.Contains("RefreshProgress()", block, StringComparison.Ordinal);
    }

    /// <summary>筛选过的面板不被后来的行打乱：新行落地时就按同一条判据收起（docx 20）。</summary>
    [Fact]
    public void NewLogRow_MustObeyTheCurrentFilter()
    {
        var code = ViewCode("MainWindow.axaml.cs");

        Assert.Contains("row.Tag = record.Level;", code, StringComparison.Ordinal);
        Assert.Contains("row.IsVisible = LogRowVisible(record.Level);", code, StringComparison.Ordinal);
    }

    /// <summary>最左那个按钮承担「开始 / 继续」，第二个恒为「暂停」，两者互斥（docx 26）。</summary>
    [Fact]
    public void StartAndPause_MustBeMutuallyExclusive()
    {
        var actions = ViewCode("Actions.cs");

        Assert.Contains("public static bool CanPause(bool running, bool paused)", actions, StringComparison.Ordinal);
        Assert.Contains("public static bool CanResume(bool running, bool paused)", actions, StringComparison.Ordinal);

        var code = ViewCode("MainWindow.axaml.cs");
        Assert.Contains("TxtStart.Text = canResume ? UiText.Resume : UiText.Start;", code, StringComparison.Ordinal);
        Assert.Contains("BtnPause.IsEnabled = canPause;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 活动槽那一行取 core 给的进度结构（docx 24）：界面不自己数，也不复读日志正文。
    /// </summary>
    [Fact]
    public void ActivitySlot_MustRenderTheProgressStructure()
    {
        var code = ViewCode("MainWindow.axaml.cs");

        Assert.Contains("ActDoing.Text = ActivityProgressText();", code, StringComparison.Ordinal);
        Assert.Contains("_progress = record.Progress;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastLogText", code, StringComparison.Ordinal);
    }

    /// <summary>命名层把级别随正文一起交回来：成品正文在文案表里查不到级别。</summary>
    [Fact]
    public void NamingLayer_MustCarryTheLevelBackWithTheText()
    {
        var naming = Code("Services", "OutputNaming.cs");
        var loop = Code("Services", "PierceLoop.cs");

        // 并通道之后形状变了、意图不变：命名层仍不把级别丢掉 —— 站点按入口命名，级别由入口承载；
        // 「成品＋级别」那一套（NamingLog／LogLines）与穿透层那一圈转发都不再存在。
        Assert.Contains("log?.Problem(NamingText.LongNameTrimmedAt", naming, StringComparison.Ordinal);
        Assert.DoesNotContain("NamingLog", naming, StringComparison.Ordinal);
        Assert.DoesNotContain("LogLines", loop, StringComparison.Ordinal);
    }

    /// <summary>
    /// 跟随日志时先让布局跑一轮再滚到底。这一条的**后果**在 headless 里量不出来 ——
    /// 后续布局会把偏移补齐，撤掉那一次 <c>UpdateLayout</c> 也量不出差（2026-10-07 实测，见 报告），
    /// 所以这里钉的是形态：滚之前那一次布局在场（docx 11）。
    /// </summary>
    [Fact]
    public void LogFollow_MustLayOutBeforeScrollingToTheEnd()
    {
        var code = ViewCode("MainWindow.axaml.cs");
        var at = code.IndexOf("ChkFollow.IsChecked == true", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到跟随滚动那一处");

        var block = code[at..code.IndexOf("ScrollToEnd()", at, StringComparison.Ordinal)];
        Assert.Contains("UpdateLayout()", block, StringComparison.Ordinal);
    }

    /// <summary>活动槽那一格只有一个标题，不挂状态图标（看图反馈）。</summary>
    [Fact]
    public void ActivityCard_MustNotCarryAStatusIcon()
    {
        Assert.DoesNotContain("ActIcon", View("MainWindow.axaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("ActIcon", ViewCode("MainWindow.axaml.cs"), StringComparison.Ordinal);
    }

    private static string Code(params string[] parts) =>
        File.ReadAllText(Path.Combine([Repo.Src, "Bbu.Core", .. parts]));

    private static string ViewCode(string fileName) =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", fileName));

    private static string View(string fileName) =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", fileName));

    /// <summary>取某个 Name 的元素整块文本：从名字起到该元素自己的结束尖括号。</summary>
    private static string Element(string xaml, string name)
    {
        var at = xaml.IndexOf($"Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"MainWindow.axaml 里找不到 {name}");

        return xaml[at..xaml.IndexOf('>', at)];
    }
}
