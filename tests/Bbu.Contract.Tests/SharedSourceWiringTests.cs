using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 调用点与文案落点：同一屏上的读数只能有一个出处（状态词、日志徽标、工具条计数、打开输出目录的入口），
/// 已经出了结论的那一层要把结论带到用户看得见的地方（失败行的中文归类、转发失败的原因、配置回落的留痕）——
/// 各写一份时屏幕上是两个数或两个词，谁也不会报错，用户照着那个错的决定往下走。
///
/// 行为那一半钉在各层的单测里（、、、
///），这一份钉调用点没绕过它们。 B-2026-226 至 231 与 233 / 235 / 236。
/// </summary>
public sealed class SharedSourceWiringTests
{
    /// <summary>
    /// 跑过一批之后才写「已结束」：刚加入、一次没跑就写它，与同屏那一格的「还没有跑过任务」自相矛盾。
    /// 两处取的是同一个判据（跑过没跑过），撤掉就退回按「清单里有没有可运行项」写。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-230")]
    public void LogBadge_MustWaitForABatchToFinish()
    {
        var code = View("MainWindow.axaml.cs");

        var at = code.IndexOf("LogStatus.Text =", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到日志徽标那一处赋值");

        // 徽标那一句自己按「跑过没跑过」判，不按「清单里有没有可运行项」判。
        var statement = code[at..code.IndexOf(';', at)];
        Assert.Contains("_lastBatchText", statement, StringComparison.Ordinal);
        Assert.Contains("UiText.LogBadgeDone", statement, StringComparison.Ordinal);

        // 同屏那一格的「还没有跑过任务」取的是同一个判据。
        Assert.Contains(
            "_lastBatchText.Length > 0 ? _lastBatchText : UiText.PreviousBatchNone",
            code,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 工具条的计数与顶栏那个「x / y 项」取同一个汇总函数：工具条数可运行项、不会处理的行另计一句，
    /// 两处不各数各的 —— 连顶栏那 100ms 一次的刷新也不许就地数。
    /// </summary>
    /// <remarks>顶栏那两处数的清单由 <c>ProgressItems()</c> 一处给（跑批时是本批、空闲时是整份清单），
    /// 所以这里钉的是**那个实参**：换名字或就地改成 `_jobs` 都要在这里过一道。</remarks>
    [Fact]
    [Trait("Bug", "B-2026-229")]
    public void ToolbarCount_MustComeFromTheSameSummary()
    {
        var code = View("MainWindow.axaml.cs");

        Assert.Contains("Runner.RunnableCount(_jobs)", code, StringComparison.Ordinal);
        Assert.Contains("Runner.SettledCount(_jobs)", code, StringComparison.Ordinal);
        Assert.Contains("Runner.Summarize(ProgressItems())", code, StringComparison.Ordinal);
        Assert.Contains("private IReadOnlyList<Job> ProgressItems() =>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("UiText.TaskCountAt, _jobs.Count", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Count(item => item.Runnable)", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 图例那一格与状态列的词一个口径：被用户停下的另记一格、不并进「跳过」。
    /// </summary>
    /// <remarks>本类只钉调用点与常量落点：两档怎么分由  钉着，批末那一句的格数由  钉着。
    /// 两处数的清单各不相同（图例是 <c>ProgressItems()</c>、批末摘要是本批 <c>_batch</c>），所以**逐处钉实参**。</remarks>
    [Fact]
    [Trait("Bug", "B-2026-226")]
    public void Legend_MustTellStoppedFromSkipped()
    {
        var code = View("MainWindow.axaml.cs");

        // 图例那一处调用传的是「跳过减已停止」与「已停止」两格，不是一个合并的跳过数。
        var at = code.IndexOf("SetLegend(queued", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到图例那一处调用");
        Assert.Contains("skipped - stopped, stopped", code[at..code.IndexOf(';', at)], StringComparison.Ordinal);

        Assert.Contains("Runner.StoppedCount(ProgressItems())", code, StringComparison.Ordinal);
        Assert.Contains("Runner.SkippedCount(_batch)", code, StringComparison.Ordinal);
        Assert.Contains("ScanText.RollupStoppedAt", Source("Bbu.Core/Services/Runner.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 警告退出码在生产路径上至少要有一个人读：只在 tests 里用，界面读不到这一档，
    /// 退出码 1 会被说成「失败（退出码 1）」（B-2026-228）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-228")]
    public void WarningExitCode_MustHaveAProductionCallPoint()
    {
        var hits = Repo.Files(Repo.Src, ".cs")
            .Where(file => Path.GetFileName(file) != "EngineExitCodes.cs")
            .Where(file => File.ReadAllText(file).Contains("EngineExitCodes.IsWarning", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Repo.Root, file).Replace('\\', '/'))
            .ToList();

        Assert.Contains("src/Bbu.Core/Services/EngineResult.cs", hits);
    }

    /// <summary>
    /// 警告档的接线要有人看住：层判定走 <c>LayerSucceeded</c>（引擎报警告也算解出来了），
    /// 而且层结果必须把 <c>Warned</c> 带出去 —— 少任何一半，退出码 1 的那一层都会落回失败。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-228")]
    public void WarningLayer_MustReachTheLayerResult()
    {
        var extract = Source("Bbu.Core/Services/PierceExtract.cs");

        Assert.Contains("if (LayerSucceeded(extracted))", extract, StringComparison.Ordinal);
        Assert.Contains("Warned = extracted.Warned,", extract, StringComparison.Ordinal);
    }

    /// <summary>打开输出目录的入口与它的可用性判据取同一个函数：各判一次会出现按钮亮着却打不开（B-2026-231）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-231")]
    public void OpenOutput_MustShareItsListWithTheAvailabilityGate()
    {
        var code = View("MainWindow.axaml.cs");

        Assert.Contains("Runner.OutputDirs(_jobs)", code, StringComparison.Ordinal);
        Assert.Contains("Actions.CanOpenOutput(_jobs)", code, StringComparison.Ordinal);
        Assert.Contains("Runner.OutputDirs(jobs)", View("Actions.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 失败那一层的结论要带着中文归类进结果行：层里只留引擎原话时，用户拿到的是一行英文，
    /// 看不出这是哪一类失败。撤掉这一处取结论的写法即红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-233")]
    public void FailedLayer_MustCarryTheConclusionIntoTheResultRow()
    {
        var extract = Source("Bbu.Core/Services/PierceExtract.cs");

        Assert.Contains("var note = LayerFailureNote(extracted);", extract, StringComparison.Ordinal);
        Assert.DoesNotContain("note = extracted.Tail", extract, StringComparison.Ordinal);
    }

    /// <summary>
    /// 转发失败要落在用户看得见的那条通道上、并且带上原因：换回调试那一档，界面上一个字都没有，
    /// 而原因（管道连不上时捕获到的异常原文）也随之丢掉。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-235")]
    public void ForwardFailure_MustReachTheUserFacingChannelWithItsReason()
    {
        var program = File.ReadAllText(Path.Combine(Repo.Root, "src", "Bbu.App", "Program.cs"));

        Assert.Contains("log.Failure(Format.ForwardFailedAt", program, StringComparison.Ordinal);
        Assert.Contains("HandOverToRunningInstance(payload, log, out var why)", program, StringComparison.Ordinal);

        // 原因本身在转发那一层捕获：只报一句「没成功」等于把失败原因丢掉。
        var forwarder = Source("Bbu.Core/SingleInstance/SingleInstanceForwarder.cs");
        Assert.Contains("out string reason", forwarder, StringComparison.Ordinal);
        Assert.Contains("reason = ex.Message;", forwarder, StringComparison.Ordinal);
    }

    /// <summary>
    /// 配置回落默认值要经界面那一处的日志出口报出去：Core 里写得出这一句，可装配处不接上，
    /// 用户什么也看不到 —— 静默回落与「我的设置丢了」在他眼里是同一件事。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-236")]
    public void ConfigFallback_MustReachTheWindowLog()
    {
        var code = View("MainWindow.axaml.cs");

        Assert.Contains(
            "new JsonConfigStore(Path.Combine(dataDirectory, AppInfo.ConfigFileName), _log)",
            code,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 提示条的停留时长只有一处：到点撤下与时长都只许出现一次，
    /// 别处再写一份「哪句停多久」时，同一个意思的两句话会停不一样久。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-248")]
    public void ToastHold_MustComeFromTheOneRule()
    {
        var host = View("ToastHost.axaml.cs");

        // 到点撤下只此一处，它取的时长就是那一个常量。
        Assert.Single(Regex.Matches(host, @"RunOnce\(\(\) => Dismiss\(toast\)").Cast<Match>());
        Assert.Contains("Dismiss(toast), Hold)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("HoldFor", host, StringComparison.Ordinal);
        Assert.DoesNotContain("2600", host, StringComparison.Ordinal);
    }

    /// <summary>
    /// 运行中拖入与接续两处都接上了：清单照收（拖入那一路不许有「正在跑就回一句提示再 return」的闸，有它文件就被丢掉），
    /// 一批跑完时按「清单里还有排队的」接着开下一批。撤掉任一处即红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-249")]
    public void QueuedRun_MustContinueIntoTheNextBatch()
    {
        var code = View("MainWindow.axaml.cs");

        Assert.Contains("await RunBatchAsync();", code, StringComparison.Ordinal);
        Assert.Contains("while (ContinuesQueue());", code, StringComparison.Ordinal);
        Assert.Contains("Runner.HasQueued(_jobs)", code, StringComparison.Ordinal);

        // 拖入那一路不许有「正在跑就拒绝」的闸。
        var at = code.IndexOf("private async Task AddPathsAsync(", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到 AddPathsAsync");
        var body = code[at..code.IndexOf("\n    private ", at, StringComparison.Ordinal)];
        Assert.DoesNotContain("if (_running)", body, StringComparison.Ordinal);

        // 两个选择器与拖入同一条口径：它们自己不许再拦一道，否则按钮点得动、点了没反应。
        foreach (var picker in new[] { "private async Task PickFilesAsync()", "private async Task PickFolderAsync()" })
        {
            var head = code.IndexOf(picker, StringComparison.Ordinal);
            Assert.True(head >= 0, $"找不到 {picker}");
            var pickerBody = code[head..code.IndexOf("\n    private ", head, StringComparison.Ordinal)];
            Assert.DoesNotContain("if (_running)", pickerBody, StringComparison.Ordinal);
        }

        // 另外三个运行中仍要拒绝的入口一个都不许放开。
        Assert.Contains("BtnClearList.IsEnabled = canClear;", code, StringComparison.Ordinal);
        Assert.Contains("BtnRemoveSelected.IsEnabled = canRemove;", code, StringComparison.Ordinal);
        Assert.Contains("BtnOutSum.IsEnabled = !_running;", code, StringComparison.Ordinal);
    }

    private static string View(string fileName) =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", fileName));

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(Repo.Src, relative.Replace('/', Path.DirectorySeparatorChar)));
}
