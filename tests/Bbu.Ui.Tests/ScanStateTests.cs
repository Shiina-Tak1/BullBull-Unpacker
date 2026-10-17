using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 扫描态的界面口径：扫描期活动槽给目录与文件两个数、中央槽位不显示空状态、
/// 「停止」可用并且能取消扫描、「开始」排队等扫完。判据都落在状态与那一条文案上。
/// </summary>
public sealed class ScanStateTests
{
    /// <summary>活动槽那一行照扫描的计数渲染：扫描没有总数，两个数是目录与文件，不给百分比。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-176")]
    public void ActivitySlot_MustShowTheScanCounts()
    {
        var window = Show();

        Feed(window, Progress(12, 340));
        Assert.Equal("扫描中：文件夹 12、文件 340", ActivityText(window));

        window.Close();
    }

    /// <summary>空清单时显示空状态，但它要让位给扫描：扫描中清单还是空的，提示「拖到这里」与事实相抵。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-181")]
    public void EmptyState_MustGiveWayWhileScanning()
    {
        var window = Show();
        var empty = window.FindControl<Control>("EmptyState")!;
        Call(window, "ShowCentreSlot");
        Assert.True(empty.IsVisible, "前置没成立：空清单时先要显示空状态");

        Set(window, "_scanning", true);
        Call(window, "ShowCentreSlot");
        Assert.False(empty.IsVisible, "扫描中空状态没有让位，屏幕上是「拖到这里」而它正在扫");

        Set(window, "_scanning", false);
        Call(window, "ShowCentreSlot");
        Assert.True(empty.IsVisible, "扫描结束后空状态没有回来");

        window.Close();
    }

    /// <summary>扫描期「停止」可用：不接管的话按钮是灰的，用户看着一个正在扫的大目录无路可走。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-180")]
    public void Stop_MustBeEnabledWhileScanning()
    {
        var window = Show();
        var stop = window.FindControl<Button>("BtnStop")!;
        Call(window, "RefreshChrome");
        Assert.False(stop.IsEnabled, "前置没成立：空闲时「停止」本来就该是灰的");

        Set(window, "_scanning", true);
        Call(window, "RefreshChrome");
        Assert.True(stop.IsEnabled, "扫描期「停止」是灰的，用户停不下来");

        window.Close();
    }

    /// <summary>扫描期点「停止」取消的是这一趟扫描：取消令要真的发出去，不能只把按钮点亮。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-180")]
    public void Stop_MustCancelTheRunningScan()
    {
        var window = Show();
        using var cancellation = new CancellationTokenSource();
        Set(window, "_scanning", true);
        Set(window, "_cts", cancellation);

        Call(window, "StopRun");

        Assert.True(cancellation.IsCancellationRequested, "扫描期点「停止」没有取消扫描");

        window.Close();
    }

    /// <summary>
    /// 扫描期点「开始」是排队，不是直接起跑：扫描还在往清单里填，此刻开跑会让 Runner 手里的那份清单
    /// 被扫描收尾那一次整表重建顶掉（B-2026-179）。按钮保持可点，只是延后。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-179")]
    public void Start_MustQueueWhileScanning()
    {
        var window = Show();
        Set(window, "_scanning", true);

        CallAsync(window, "RunAsync").GetAwaiter().GetResult();

        Assert.False((bool)Get(window, "_running")!, "扫描期点「开始」直接开跑了");
        Assert.True((bool)Get(window, "_queued")!, "扫描期点「开始」没有排进队列");

        window.Close();
    }

    /// <summary>
    /// 清单里只有一行不可执行的项时「清空列表」也要能点：以前它判的是「有没有可执行的项」，
    /// 于是拖一个不是压缩包的文件进来之后，那一行反而清不掉（真机反馈， B-2026-183）。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-183")]
    public void ClearList_MustBeUsableWithOnlyUnrunnableRows()
    {
        var window = Show();
        var jobs = (List<Job>)Get(window, "_jobs")!;
        jobs.Add(new Job
        {
            ArchivePath = Path.Combine(Path.GetTempPath(), "readme.txt"),
            DisplayName = "readme.txt",
            Kind = ScanText.NoteNotArchive,
            Runnable = false,
        });

        Call(window, "RefreshChrome");

        Assert.True(
            window.FindControl<Button>("BtnClearList")!.IsEnabled,
            "清单里只有不可执行的项时「清空列表」是灰的");

        window.Close();
    }

    /// <summary>
    /// 清单还空着时「开始解压」的禁用原因说的是「先加入压缩包或文件夹」：
    /// 那一刻该做的是先加东西，讲「已完成的不会重复解压」是对着一个空列表说的。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-243")]
    public void StartDisabled_MustSayAddSomethingWhileTheListIsEmpty()
    {
        var window = Show();
        Call(window, "RefreshChrome");

        var start = window.FindControl<Button>("BtnStart")!;
        Assert.False(start.IsEnabled, "前置没成立：空清单时「开始解压」本来就该是灰的");
        Assert.Equal(UiText.DisabledNothingAdded, ToolTip.GetTip(start));
        Assert.Equal(UiText.ProgressWaiting, window.FindControl<TextBlock>("ProgCount")!.Text);

        window.Close();
    }

    /// <summary>
    /// 清单里有项、但一项都跑不动时（例如只拖进一个 `.iso`）顶栏不许写「等待加入文件」：
    /// 文件早就加进来了，是它们不用解 —— 照旧那一句，用户会以为文件没加进去。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-243")]
    public void ProgressCaption_MustNotSayWaitingWhenNoRowCanRun()
    {
        var window = Show();
        var jobs = (List<Job>)Get(window, "_jobs")!;
        jobs.Add(new Job
        {
            ArchivePath = Path.Combine(Path.GetTempPath(), "image.iso"),
            DisplayName = "image.iso",
            Kind = ScanText.NoteNotArchive,
            Runnable = false,
        });

        Call(window, "RefreshChrome");
        Call(window, "RefreshProgress");

        Assert.Equal(UiText.ProgressNoRunnable, window.FindControl<TextBlock>("ProgCount")!.Text);

        window.Close();
    }

    /// <summary>跑完一批之后禁用原因保留那一句讲重跑的：那正是用户眼前这件事，分档不许把它一并改掉。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-243")]
    public void StartDisabled_MustKeepTheRerunNoteAfterABatch()
    {
        var window = Show();
        var jobs = (List<Job>)Get(window, "_jobs")!;
        jobs.Add(new Job
        {
            ArchivePath = Path.Combine(Path.GetTempPath(), "done.zip"),
            DisplayName = "done.zip",
            Kind = "ZIP",
            State = JobState.Done,
        });

        Call(window, "RefreshChrome");

        var start = window.FindControl<Button>("BtnStart")!;
        Assert.False(start.IsEnabled, "前置没成立：这一批都跑完了，「开始解压」本来就该是灰的");
        Assert.Equal(ScanText.NothingToDo, ToolTip.GetTip(start));

        window.Close();
    }

    /// <summary>
    /// 扫描保险丝那个问句的否定项取文本表给的那一个：`Ui.ScanFuseAskCancel`，现值「停止」。
    /// 正文 `Ui.ScanFuseAskBodyAt` 照文本表逐字比对，它写的是继续的代价与「是否继续」。
    /// 照壳子默认的「取消」写，用户会以为是「取消这次添加」。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-247")]
    public async Task ScanFuseAsk_MustNameWhatTheNegativeAnswerDoes()
    {
        var window = Show();
        var asked = Task.Run(() => (bool)Result(window, "AskScanFuseContinue", 12, 340)!);
        for (var spin = 0; spin < 40 && !window.FindControl<ConfirmPrompt>("Ask")!.IsOpen; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        var prompt = window.FindControl<ConfirmPrompt>("Ask")!;
        Assert.True(prompt.IsOpen, "保险丝那一路没有问出来");
        Assert.Equal(UiText.ScanFuseAskContinue, prompt.FindControl<Button>("PromptOk")!.Content);
        Assert.Equal(UiText.ScanFuseAskCancel, prompt.FindControl<Button>("PromptCancel")!.Content);
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, UiText.ScanFuseAskBodyAt, 12, 340),
            prompt.FindControl<TextBlock>("PromptBody")!.Text);

        prompt.FindControl<Button>("PromptCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await asked);

        window.Close();
    }

    /// <summary>
    /// 运行中拖进来的项进清单排队（不丢文件），跑着的那一批原样留着 ——
    /// 整表重扫会把跑着的那几项换成新对象，进度与层号随之丢掉。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-249")]
    public async Task RunningAdd_MustQueueTheDroppedItems()
    {
        var window = Show();
        var jobs = (List<Job>)Get(window, "_jobs")!;
        var running = new Job
        {
            ArchivePath = Path.Combine(Path.GetTempPath(), "跑着的.zip"),
            DisplayName = "跑着的.zip",
            Kind = "ZIP",
        };
        running.State = JobState.Running;
        running.Layer = 2;
        jobs.Add(running);
        Set(window, "_running", true);

        var folder = Directory.CreateTempSubdirectory("bbu-queue-").FullName;
        var added = new[] { Path.Combine(folder, "新来的.zip"), Path.Combine(folder, "另一个.zip") };
        foreach (var path in added)
        {
            File.WriteAllBytes(path, ZipWithOneEntry());
        }

        await (Task)Result(window, "AddPathsAsync", (IReadOnlyList<string>)added)!;

        // 跑着的那一项还是原来那个对象，状态与层号一个字没动
        Assert.Same(running, jobs.First(item => item.DisplayName == "跑着的.zip"));
        Assert.Equal(JobState.Running, running.State);
        Assert.Equal(2, running.Layer);

        // 新拖进来的两项都在清单里排队，而且可运行（真会轮到它们）
        foreach (var path in added)
        {
            var item = jobs.Single(job => job.ArchivePath == path);
            Assert.True(item.Runnable, $"{Path.GetFileName(path)} 进了清单却不可运行");
            Assert.Equal(JobState.Queued, item.State);
        }

        Set(window, "_running", false);
        window.Close();
    }

    /// <summary>
    /// 一批跑完时清单里还排着队就接着开下一批：这一趟里后加的那一项因此**真的**被跑掉，
    /// 而不是停在「排队中」等用户再点一次「开始」。只接排队、不碰失败。
    /// </summary>
    /// <remarks>
    /// 后加的那一项要落在**这一批跑着的时候**：本用例把这一批按在暂停门上（跑批在每一项之前问一次该不该暂停），
    /// 趁它停住的时候把路径交给拖入那一路，再放开。撤掉接续那一环，这一项就永远停在「排队中」。
    /// </remarks>
    [AvaloniaFact, Trait("Bug", "B-2026-249")]
    public async Task Queued_MustBePickedUpWhenTheBatchEnds()
    {
        var window = Show();
        var jobs = (List<Job>)Get(window, "_jobs")!;
        for (var index = 0; index < 200; index++)
        {
            jobs.Add(Missing($"第一批-{index}.zip"));
        }

        var run = CallAsync(window, "RunAsync");
        for (var spin = 0; spin < 500 && !(bool)Get(window, "_running")!; spin++)
        {
            await Task.Delay(1);
        }

        Assert.True((bool)Get(window, "_running")!, "这一批没有跑起来");
        Set(window, "_paused", true);

        var folder = Directory.CreateTempSubdirectory("bbu-queue-").FullName;
        var late = Path.Combine(folder, "后来的.zip");
        File.WriteAllBytes(late, ZipWithOneEntry());
        await (Task)Result(window, "AddPathsAsync", (IReadOnlyList<string>)new[] { late })!;

        Assert.Equal(JobState.Queued, jobs.Single(job => job.ArchivePath == late).State);

        // 分母在开跑那一刻定死：跑到一半加进来的那一项不算进这一批的分母，否则进度条会往回跳。
        var progCount = (TextBlock)Get(window, "ProgCount")!;
        Assert.Contains("/ 200 项", progCount.Text, StringComparison.Ordinal);

        Set(window, "_paused", false);
        await run;

        Assert.NotEqual(JobState.Queued, jobs.Single(job => job.ArchivePath == late).State);
        window.Close();
    }

    /// <summary>点过「停止」之后不再接续：接着开下一批等于停不下来。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-249")]
    public void Stopped_MustNotContinueTheQueue()
    {
        var window = Show();
        var jobs = (List<Job>)Get(window, "_jobs")!;
        jobs.Add(Missing("排着的.zip"));
        Set(window, "_stop", true);

        Assert.False((bool)Result(window, "ContinuesQueue")!, "点过停止之后还在接着开下一批");
        Assert.True(Runner.HasQueued(jobs), "前置没成立：清单里确实还有排队的项");

        Set(window, "_stop", false);
        Assert.True((bool)Result(window, "ContinuesQueue")!, "清单里还有排队的项却没有接着跑");

        window.Close();
    }

    /// <summary>一项指向不存在的源文件的可运行项：它一跑就落一个终态（源文件已不存在），不必起引擎。</summary>
    private static Job Missing(string name) => new()
    {
        ArchivePath = Path.Combine(Path.GetTempPath(), "bbu-queue-missing", name),
        DisplayName = name,
        Kind = "ZIP",
    };

    /// <summary>一个合法的单条目 zip：扫描按**内容**认格式，写纯文本会被判成「不是包」而不可运行。</summary>
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

    private static LogRecord Progress(int directories, int files) => new(
        LogLevel.Info,
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            UiText.ActivityScanningAt,
            directories,
            files),
        0,
        LogProgress.Scanning(directories, files));

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

    private static object? Get(MainWindow window, string field) =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Call(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    /// <summary>调一个带返回值的私有成员。保险丝那一路是同步阻塞的，只能从别的线程调它。</summary>
    private static object? Result(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) =>
        (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, args)!;

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
    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-scan-").FullName;
}
