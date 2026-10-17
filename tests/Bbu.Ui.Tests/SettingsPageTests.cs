using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 设置页。每一项对应一个配置字段，改完即存；排除名单是它与扫描、运行之间那条链的起点
/// （B-2026-100）。
///
/// 数据目录由用例传进去，页面不自己解析 —— <c>DataDirectory</c> 带静态缓存，
/// 连续两次测试会互相污染，也会把作者自己的 config.json 写掉。
/// </summary>
public sealed class SettingsPageTests
{
    /// <summary>回填时控件跟着配置走。</summary>
    [AvaloniaFact]
    public void LoadFrom_MustFillEveryControl()
    {
        using var temp = new TempConfig();
        var config = new AppConfig
        {
            MaxDepth = 7,
            TimeoutMinutes = 45,
            MinFreeGb = 2.5,
            RemoveSource = true,
            CleanDelete = false,
            ExcludedExtensions = ["apk", "docx"],
        };
        var page = Show(temp, config);

        Assert.Equal(7m, Number(page, "MaxDepth").Value);
        Assert.Equal(45m, Number(page, "TimeoutMinutes").Value);
        Assert.Equal(2.5m, Number(page, "MinFreeGb").Value);
        Assert.True(Toggle(page, "RemoveSource").Value);
        Assert.False(Toggle(page, "CleanDelete").Value);
        Assert.Contains("docx", Excluded(page).Value);
    }

    /// <summary>超范围的旧值夹到最近的一端，而不是原样留在控件上。</summary>
    [AvaloniaFact]
    public void LoadFrom_MustClampAnOutOfRangeValue()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig { MaxDepth = 999 });

        Assert.Equal(20m, Number(page, "MaxDepth").Value);
    }

    /// <summary>
    /// 改一项就落盘，盘上读回来是改过的那一份。
    /// 「改动即存」只有这一条路，不存在某个字段忘了同步。
    /// </summary>
    [AvaloniaFact]
    public void Push_MustWriteTheChangeToDisk()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        Number(page, "MaxDepth").Value = 9;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(9, temp.Store.Load().MaxDepth);
    }

    /// <summary>
    /// 排除名单按三种分隔切开、统一成小写不带点，写进配置的那一份就是扫描与运行要读的那一份
    /// （B-2026-100 的第 ① 与 ② 条）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-100")]
    public void Push_MustNormalizeTheExclusionList()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        Excluded(page).Value = "APK; docx, xlsx";
        page.Push();

        var saved = temp.Store.Load().ExcludedExtensions;
        Assert.Equal(new[] { "apk", "docx", "xlsx" }, saved);
    }

    /// <summary>
    /// 保存之后扫描立刻看到新名单：名单只在建 <c>ScanOptions</c> 那一刻现取，
    /// 界面里不留启动副本（B-2026-100 的第 ② 条）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-100")]
    public void SavedExclusionList_MustBeWhatScanReadsNext()
    {
        using var temp = new TempConfig();
        var config = new AppConfig { ExcludedExtensions = ["apk"] };
        var page = Show(temp, config);

        Excluded(page).Value = "apk docx";
        page.Push();

        var options = new ScanOptions { ExcludeExts = config.ExcludedExtensions };
        Assert.Contains("docx", options.ExcludeExts);
    }

    /// <summary>
    /// 端到端：拿保存后的名单去扫一个含 <c>报告.docx</c> 的目录，那个 docx 必须被标成已排除，
    /// 而不是被当作未排除项进入清单并参与解压（B-2026-100 的第 ③ 条）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-100")]
    public void SavedExclusionList_MustMarkTheFileInTheScanResult()
    {
        using var temp = new TempConfig();
        var config = new AppConfig { ExcludedExtensions = ["apk"] };
        var page = Show(temp, config);

        var folder = temp.NewFolder();
        File.WriteAllBytes(Path.Combine(folder, "报告.docx"), ZipWithOneEntry());
        File.WriteAllBytes(Path.Combine(folder, "样本.zip"), ZipWithOneEntry());

        Excluded(page).Value = "apk docx";
        page.Push();

        var jobs = Scan.Run([folder], new ScanOptions { ExcludeExts = config.ExcludedExtensions });
        var docx = jobs.FirstOrDefault(job => job.DisplayName == "报告.docx");

        Assert.NotNull(docx);
        Assert.Contains("DOCX", docx!.Kind);
        Assert.False(docx.Runnable);
    }

    /// <summary>
    /// 最小的 ZIP 夹具。docx 与 zip 的 magic 一样，识别层认的是文件头，
    /// 所以夹具必须有合法 ZIP 头 —— 写一个纯文本的 <c>报告.docx</c> 会被判成「不是包」，
    /// 那条路根本走不到排除名单上（第一版断言就是这么红的）。
    /// </summary>
    private static byte[] ZipWithOneEntry()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("a.txt").Open();
            entry.WriteByte((byte)'x');
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 恢复默认与别的项走同一条写盘路径：点完读回来的**盘上配置**就是默认值
    /// （只回填控件是不够的 —— 那样关掉面板再打开又是旧值）。本页以外的项（底栏的输出与重名、列宽）
    /// 不在这一项里，一个都不动 —— 范围写在反馈那半句里。撤回落盘那一半即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-238")]
    public void RestoreDefaults_MustWriteTheDefaultsToDisk()
    {
        using var temp = new TempConfig();
        var config = new AppConfig
        {
            MaxDepth = 3,
            TimeoutMinutes = 45,
            RemoveSource = true,
            CleanDelete = false,
            ExcludedExtensions = ["apk"],
            OutputMode = OutputMode.Custom,
            OutputDirectory = @"Z:\别处",
            Conflict = ConflictPolicy.Overwrite,
        };
        temp.Store.Save(config);
        var page = Show(temp, config);
        var toasts = new List<string>();
        page.Toast = toasts.Add;

        ((SettingsActionField)page.Model["RestoreDefaults"]).Invoke!();

        Assert.Equal(5m, Number(page, "MaxDepth").Value);
        var saved = temp.Store.Load();
        var defaults = new AppConfig();
        Assert.Equal(defaults.MaxDepth, saved.MaxDepth);
        Assert.Equal(defaults.TimeoutMinutes, saved.TimeoutMinutes);
        Assert.Equal(defaults.MinFreeGb, saved.MinFreeGb);
        Assert.Equal(defaults.PasswordWorkers, saved.PasswordWorkers);
        Assert.Equal(defaults.RemoveSource, saved.RemoveSource);
        Assert.Equal(defaults.RemoveIntermediate, saved.RemoveIntermediate);
        Assert.Equal(defaults.FlattenSingleChild, saved.FlattenSingleChild);
        Assert.Equal(defaults.ScanAppended, saved.ScanAppended);
        Assert.Equal(defaults.FolderScan, saved.FolderScan);
        Assert.Equal(defaults.CleanDelete, saved.CleanDelete);
        Assert.Equal(defaults.ExcludedExtensions, saved.ExcludedExtensions);

        // 本页以外的项保持用户原样：这一项按钮管不到底栏。
        Assert.Equal(OutputMode.Custom, saved.OutputMode);
        Assert.Equal(ConflictPolicy.Overwrite, saved.Conflict);
        Assert.Equal([UiText.SettingsRestoreSaved], toasts);
    }

    /// <summary>
    /// 层数上限填 0 时界面显示 1、配置里那一位仍是 0（不写回）：界面写的与执行的是同一个数
    /// （两种入口都按 1 层跑，见 Core 的  那一条）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-239")]
    public void MaxDepthZero_MustShowOneWithoutRewritingTheConfig()
    {
        using var temp = new TempConfig();
        var config = new AppConfig { MaxDepth = 0 };
        var page = Show(temp, config);

        Assert.Equal(1m, Number(page, "MaxDepth").Value);
        Assert.Equal(0, config.MaxDepth);
    }

    /// <summary>
    /// 保存成功要有一次用户看得见的反馈：存到盘上一次说一次，去抖合并成一次写盘时也只说一次；
    /// 没存成的那一次走既有那句失败提示，不许说「已保存」。撤掉接线即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-242")]
    public void SavedFeedback_MustAppearOncePerSave()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());
        var toasts = new List<string>();
        page.Toast = toasts.Add;

        Number(page, "MaxDepth").Value = 9;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(9, temp.Store.Load().MaxDepth);
        Assert.Equal([UiText.SettingsSaved], toasts);
    }

    /// <summary>
    /// 排除名单逐字符落笔，去抖把连着的几次合并成一次写盘：窗口里既不许落盘、也不许说
    /// 「设置已保存」（三次落笔变成三条提示就是刷屏）。到期那一步走的就是 <see cref="SettingsPage.Push"/>：
    /// headless 不跑真实计时器、也没有推进时钟的入口，量不到到期那一刻（与拖放宽限那条同一个口径）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-242")]
    public void ExcludedTyping_MustToastOnceForTheMergedSave()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());
        var toasts = new List<string>();
        page.Toast = toasts.Add;

        Excluded(page).Value = "apk";
        Excluded(page).Value = "apk docx";
        Excluded(page).Value = "apk docx xlsx";
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(toasts);
        Assert.False(File.Exists(temp.Store.Path), "去抖窗口里就落盘了，半截名单会留在盘上");

        page.Push();

        Assert.Equal(new[] { "apk", "docx", "xlsx" }, temp.Store.Load().ExcludedExtensions);
        Assert.Equal([UiText.SettingsSaved], toasts);
    }

    /// <summary>
    /// 写盘没成的那一次不许说「设置已保存」：走既有那句失败提示，且要带得上原因。
    /// 另外钉**档位**：这一句走的是 `Failure` 入口 ⇒ 日志里那一行是失败档，撤成 `Note` 即红
    /// </summary>
    [AvaloniaFact]
    public void SaveFailure_MustNotSayTheSettingsWereSaved()
    {
        using var temp = new TempConfig();
        var page = new SettingsPage();
        var toasts = new List<string>();
        page.Toast = toasts.Add;

        // 读页面这一路日志收到的行：级别由 `_log` 的入口名承载，这里看的就是它交出去的那一档。
        var lines = new List<LogRecord>();
        temp.Log.LineWritten += lines.Add;

        // 路径指到一个目录上：写盘必失败，且原因就是存储层留给 <see cref="JsonConfigStore.LastError"/> 的那一条。
        var store = new JsonConfigStore(temp.Dir);
        page.Attach(new AppConfig(), store, temp.Log, temp.Dir, null);

        Number(page, "MaxDepth").Value = 9;

        Assert.Single(toasts);
        Assert.Equal(UiText.SettingsSaveFailed(store.LastError), toasts[0]);
        Assert.Equal(
            LogLevel.Error,
            Assert.Single(lines, line => line.Text == UiText.SettingsSaveFailed(store.LastError)).Level);
    }

    /// <summary>
    /// 设置页不再有主题项。主题只留深色，切主题的入口一处都不许留
    /// 三个单选不在，主题那一组卡片也不在。
    /// </summary>
    [AvaloniaFact]
    public void SettingsPage_MustNotOfferAThemeSwitch()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        Assert.Null(page.FindControl<RadioButton>("SetThemeDark"));
        Assert.Null(page.FindControl<RadioButton>("SetThemeLight"));
        Assert.Null(page.FindControl<RadioButton>("SetThemeSystem"));
    }

    /// <summary>
    /// 设置页不再有引擎那一组（docx p000，2026-10-07）：两个引擎都随包，用户既不需要指路径、
    /// 也不该看到「已找到 / 未找到」。字段那一层与控件那一层都不许剩 —— 上一版这里有
    /// 「WinRAR 路径」「7-Zip 路径」两项加一个「重新检测」按钮。
    /// </summary>
    [AvaloniaFact]
    public void SettingsPage_MustNotOfferEngineDetection()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        var keys = page.Model.Groups
            .SelectMany(group => group.AllItems)
            .Select(item => item.Key)
            .ToArray();

        Assert.DoesNotContain("WinRarPath", keys);
        Assert.DoesNotContain("SevenZipPath", keys);
        Assert.DoesNotContain("Redetect", keys);

        // 控件那一层：路径那套模板（浏览按钮 + 输入框 + 状态标记）整块不在。
        Assert.Null(page.FindControl<Button>("PathBrowse"));
        Assert.DoesNotContain(
            page.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Name == "PathState");
    }

    /// <summary>
    /// 数据目录那一项有「打开」按钮，点它交出去的必须是这一处真实路径（p013）。
    /// 页面不自己起进程：打开能力由宿主注入，这里换成一个记录用的委托。
    /// </summary>
    [AvaloniaFact]
    public void DataDirectory_MustOpenFromTheButton()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        var dir = (SettingsDataDirField)page.Model["DataDirectory"];
        string? opened = null;
        dir.Open = () => opened = dir.Value;

        var open = page.Part<Button>("DataDirOpen")!;
        Assert.Equal(UiText.Open, open.Content);
        Assert.Equal(temp.Dir, dir.Value);

        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(temp.Dir, opened);
    }

    /// <summary>
    /// 设置页的分组与项数：解压九、排除一、Windows 集成一，外加数据目录那一组。
    /// 引擎那一组随 撤掉了（两个引擎都随包，没有可检测的东西）。
    /// 分组是这一页唯一的分类手段，每一项都要带自己的标题。
    ///
    /// 这条原先在  里数 XAML 里的 <c>frow</c> 与 <c>card</c>；
    /// 项与控件改由描述表与模板生成之后，XAML 里数不出来，判据内核搬到这里按运行时钉（T-10）。
    /// </summary>
    [AvaloniaFact]
    public void SettingsPage_MustKeepTheGroupsAndRows()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        Assert.Equal(2, page.Model.Groups.Count);
        Assert.Equal(10, page.Model.Groups[0].Items.Count);
        Assert.Equal(3, page.Model.Groups[1].Items.Count + page.Model.Groups[1].WideItems.Count);

        // 排除名单跨整行：它不在两列里，单独排在解压组下面。
        Assert.Single(page.Model.Groups[0].WideItems);
        Assert.Equal("ExcludedExtensions", page.Model.Groups[0].WideItems[0].Key);

        Assert.All(
            page.Model.Groups.SelectMany(group => group.Items.Concat(group.WideItems)),
            item => Assert.False(string.IsNullOrWhiteSpace(item.Label), $"{item.Key} 没有标题"));

        // 两列只给解压组：系统那一组单列（看图反馈）。
        Assert.True(page.Model.Groups[0].TwoColumn, "解压组该是两列");
        Assert.False(page.Model.Groups[1].TwoColumn, "系统组该是单列");
        Assert.Equal("系统", UiText.SettingsGroupShell);
    }

    /// <summary>
    /// 两列面板是 <c>UniformGrid Columns="2"</c>：项按次序成对填，XAML 一行都没管「谁跟谁配」，
    /// 配对完全由 <c>SettingsCatalog.Build</c> 的次序决定，次序因此是判据
    /// （`CleanDelete` 挪回第 10 位即红）。五对依次是限额（层数｜超时）、资源（剩余空间｜线程数）、
    /// 删除（原包｜中间包）、产物整理（中间文件夹｜文件名里的「删」字）、扫描（内嵌包｜文件夹范围）。
    /// 只钉字段键，不钉标题 —— 标题归 `Texts\Ui.cs`，别处换个名字不该弄红这一条。
    /// 两列是自动成对的：普通项为奇数时末项独占半行，一并钉住（加一项要凑成偶数或让那一项跨整行）。
    /// </summary>
    [AvaloniaFact]
    public void ExtractRows_MustPairUpInTheCatalogOrder()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        var group = page.Model.Groups[0];
        Assert.True(group.TwoColumn, "解压组该是两列：不是两列就没有「成对」这回事");

        // 加一项时先要过这一条：两列自动成对，项数为奇数时末项独占半行。
        // 它排在次序那条之前 —— 次序那条先红的话，报出来的是次序不符，看不出真正的原因。
        var count = group.Items.Count;
        Assert.True(
            count % 2 == 0 || group.Items[^1].FullRow,
            $"两列的普通项是 {count} 项：奇数时最后一项独占半行，加项要凑成偶数或让末项跨整行");

        // 逐项写出来：只钉项数、或只当集合来比，两项对调也会过。
        Assert.Equal(
            new[]
            {
                "MaxDepth", "TimeoutMinutes",
                "MinFreeGb", "PasswordWorkers",
                "RemoveSource", "RemoveIntermediate",
                "FlattenSingleChild", "CleanDelete",
                "ScanAppended", "FolderScan",
            },
            group.Items.Select(item => item.Key));
    }

    /// <summary>
    /// 两列：同一组里相邻两项落在同一行上，后一项在前一项右边（p012）。
    /// 这是"密度"那条诉求的唯一可量判据 —— 单列时两行的高度不同，这条会红。
    /// </summary>
    [AvaloniaFact]
    public void SettingsItems_MustSitInTwoColumns()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        var boxes = page.GetVisualDescendants()
            .OfType<NumericUpDown>()
            .Where(box => box.Name == "NumberBox" && box.IsEffectivelyVisible)
            .ToArray();
        Assert.True(boxes.Length >= 2, $"数值项只有 {boxes.Length} 条，量不了两列");

        var first = boxes[0].TranslatePoint(new Point(0, 0), page)!.Value;
        var second = boxes[1].TranslatePoint(new Point(0, 0), page)!.Value;

        Assert.True(
            Math.Abs(first.Y - second.Y) < 2,
            $"同一组的前两条不在同一行：Y {first.Y:0.#} 与 {second.Y:0.#}");
        Assert.True(second.X > first.X + 50, "第二条没有排在第一条右边");
    }

    /// <summary>
    /// 排除名单的显示是**空格分隔**、解析还认**全角逗号** —— 两条都照 Python 版
    /// （显示走 join 空格；解析先把逗号、全角逗号、分号换成空格再切）。
    /// 第一版显示成逗号加空格、解析也不认全角逗号（B-2026-133）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-133")]
    public void ExcludedList_MustUseSpacesAndAcceptFullWidthCommas()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig { ExcludedExtensions = ["apk", "docx"] });

        Assert.Equal("apk docx", Excluded(page).Value);
        Assert.DoesNotContain(",", Excluded(page).Value, StringComparison.Ordinal);

        // 全角逗号也当分隔：中文输入法下常打出来。
        Excluded(page).Value = "apk，docx";
        page.Push();

        var saved = temp.Store.Load().ExcludedExtensions;
        Assert.Equal(2, saved.Count);
        Assert.Contains("apk", saved);
        Assert.Contains("docx", saved);
    }

    /// <summary>
    /// 线程档位是「自动」加 2 的幂（到本机核数为止），与 1.1.0 的选项同口径 ——
    /// 不是每一档都列出来。
    ///
    /// 被测档位取 1 而不是 4：档位表按 <c>Environment.ProcessorCount</c> 截断，
    /// 4 只在 4 核以上的机器上存在，写死「第 3 档是 4」等于把断言绑在本机核数上；
    /// 核数更少的机器（CI 的 runner）取不到那个下标，报的是越界而不是判据不成立。
    /// </summary>
    [AvaloniaFact]
    public void WorkerTiers_MustBePowersOfTwo()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig { PasswordWorkers = 1 });

        var field = (SettingsChoiceField)page.Model["PasswordWorkers"];
        var expected = 1 + new[] { 1, 2, 4, 8, 16 }.Count(count => count <= Environment.ProcessorCount);
        Assert.Equal(expected, field.Options.Count);

        Assert.Equal("自动", field.Options[0]);
        Assert.Equal("1", field.Options[1]);
        Assert.Equal(1, field.Index);

        foreach (var option in field.Options.Skip(1))
        {
            var count = int.Parse(option, CultureInfo.InvariantCulture);
            Assert.True((count & (count - 1)) == 0, $"档位 {option} 不是 2 的幂");
        }
    }

    /// <summary>页脚撤掉了：反馈走主窗口的提示条，页内不再留那一行硬边。</summary>
    [AvaloniaFact]
    public void SettingsPage_MustNotCarryAFooter()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        Assert.Null(page.FindControl<TextBlock>("SetStatus"));
    }

    /// <summary>
    /// 数据目录分裂那半句要带上**另一份的实际路径**：只说「有一份数据目录」用户不知道它在哪、
    /// 也不知道该迁哪一个。另一份落点是 <c>alternativeDirectory</c>（另一侧的用户数据目录）。
    /// 撤回落点（改回不带路径那一句）即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-241")]
    public void DataDirNotice_MustNameTheOtherDirectory()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig(), alternative: temp.Dir);

        var notice = page.Part<TextBlock>("DataDirNotice");
        Assert.NotNull(notice);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, UiText.SettingsDataDirSplitAt, temp.Dir),
            notice!.Text);
    }

    /// <summary>
    /// 没有另一份数据目录时那一行说明不出现：说明只留给「另一处还有一份密码本」那一档。
    /// </summary>
    [AvaloniaFact]
    public void DataDirNotice_MustStaySilentWithoutASecondDirectory()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        Assert.Null(page.Part<TextBlock>("DataDirNotice"));
    }

    /// <summary>
    /// 排除名单的写法提示要看得见：框里永远有默认名单，Watermark 只在空框时露出来，
    /// 所以同一句还要挂在标题提示上 —— 框里有内容时也查得到正确写法。撤回落点即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-240")]
    public void ExcludedHint_MustBeReachableWhileTheBoxHasContent()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig { ExcludedExtensions = ["apk", "docx"] });

        Assert.False(string.IsNullOrEmpty(Excluded(page).Value), "框里该有默认名单，不然这条判据不成立");
        var label = page.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(block => block.Text == UiText.SettingsExcluded);

        Assert.Equal(UiText.SettingsExcludedHint, ToolTip.GetTip(label));
        Assert.Equal(UiText.SettingsExcludedHint, page.Part<TextBox>("TextField")!.Watermark);
    }

    /// <summary>
    /// 四条设置项提示都挂在**标题那一格**上（`ToolTip.Tip`），照本页多行文本模板与 Shell 状态那处的既有挂法：
    /// 鼠标停在项名上就能读到这句说明。提示逐字等于 `Texts\Ui.cs` 里的那条常量，常量本身也不许是空串
    /// —— 两边都空时值比对看不出问题。撤掉任一处的绑定即红。
    /// </summary>
    [AvaloniaFact]
    public void SettingHints_MustBeReachableFromTheLabels()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig());

        (string Key, string Hint)[] wanted =
        [
            ("TimeoutMinutes", UiText.SettingsTimeoutHint),
            ("FlattenSingleChild", UiText.SettingsFlattenHint),
            ("ScanAppended", UiText.SettingsScanAppendedHint),
            ("FolderScan", UiText.SettingsFolderScanHint),
        ];

        foreach (var (key, hint) in wanted)
        {
            Assert.False(string.IsNullOrWhiteSpace(hint), $"{key} 的提示常量是空串，等于把提示取消了");
            var label = page.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(block => block.Text == page.Model[key].Label);
            var tip = ToolTip.GetTip(label) as string;
            Assert.True(tip == hint, $"{key} 的提示没挂到标题上：期望「{hint}」，实到「{tip}」");
        }
    }

    /// <summary>
    /// 下拉收起的那一格只有 120 宽，最长那一档装不下 —— 改前「往下找（推荐）」的右括号直接被切掉，
    /// 而且不挂省略号，用户看不出这句没显示完。判的是**机制**、不是某个字串多宽：
    /// 选中值的呈现层挂字符省略号，装不下就以「…」收尾（`F-42` 把选项文字改长也不用回来调宽度）。
    /// 另一半钉住候选列表：展开时每一档的正文都要读得全，省略号不许跟到 Popup 里去。
    /// 撤掉模板里那条样式即红。
    /// </summary>
    [AvaloniaFact]
    public void ChoiceBoxSelection_MustTrimWithEllipsisWhileTheListStaysWhole()
    {
        using var temp = new TempConfig();
        var page = Show(temp, new AppConfig { FolderScan = FolderScan.Below });

        // 样式写在模板上，这一页每个档位下拉都该有 —— 与选项文字多长无关。
        Assert.All(
            page.GetVisualDescendants().OfType<ComboBox>(),
            choice => Assert.Equal(TextTrimming.CharacterEllipsis, SelectionText(choice).TextTrimming));

        var field = (SettingsChoiceField)page.Model["FolderScan"];
        var box = page.GetVisualDescendants()
            .OfType<ComboBox>()
            .Single(choice => ReferenceEquals(choice.ItemsSource, field.Options));

        Assert.Equal(field.Options[field.Index], SelectionText(box).Text);

        // 展开：候选列表是 Popup 里的 ComboBoxItem，不在这条样式上 —— 每一档都要看全、都不许裁。
        box.IsDropDownOpen = true;
        for (var spin = 0; spin < 5; spin++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(field.Options.Count, box.ItemCount);
        for (var i = 0; i < field.Options.Count; i++)
        {
            var container = box.ContainerFromIndex(i);
            Assert.NotNull(container);
            var text = container!.GetVisualDescendants().OfType<TextBlock>().First();
            Assert.Equal(field.Options[i], text.Text);
            Assert.Equal(TextTrimming.None, text.TextTrimming);
        }
    }

    /// <summary>收起那一格里真正摆字的那一个：它的正文就是当前选中项。</summary>
    private static TextBlock SelectionText(ComboBox choice) =>
        choice.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(block => block.Text == choice.SelectedItem as string);

    /// <summary>设置项是模板生成的：判断"接没接对"看字段，不按控件名找控件。</summary>
    private static SettingsNumberField Number(SettingsPage page, string key) =>
        (SettingsNumberField)page.Model[key];

    private static SettingsToggleField Toggle(SettingsPage page, string key) =>
        (SettingsToggleField)page.Model[key];

    private static SettingsTextField Excluded(SettingsPage page) =>
        (SettingsTextField)page.Model["ExcludedExtensions"];

    private static SettingsPage Show(
        TempConfig temp,
        AppConfig config,
        string? alternative = null)
    {
        var page = new SettingsPage();
        page.Attach(config, temp.Store, temp.Log, temp.Dir, alternative);
        var window = new Window { Content = page, Width = 900, Height = 640 };

        // 样式要装：项高、数值框高度、内边距都来自 Theme 的令牌与样式表，
        // 不装的话量到的是「无样式」的形态（2026-10-06 诊断时发现）。
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();

        // 项与控件由模板生成，要跑过布局才在可视树里。
        for (var spin = 0; spin < 5; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return page;
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    /// <summary>一次性临时目录加一份配置。离开作用域即删，绝不碰作者自己的 config.json。</summary>
    private sealed class TempConfig : IDisposable
    {
        public TempConfig()
        {
            Directory.CreateDirectory(Dir);
            Store = new JsonConfigStore(Path.Combine(Dir, "config.json"));
            Log = new RunLog(Path.Combine(Dir, AppInfo.LogFileName));
        }

        public string Dir { get; } = Path.Combine(
            Path.GetTempPath(), "bbu-set-" + Guid.NewGuid().ToString("N"));

        public JsonConfigStore Store { get; }

        public RunLog Log { get; }

        public string NewFolder()
        {
            var path = Path.Combine(Dir, "scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响断言结论
            }
        }
    }
}
