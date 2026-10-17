using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 状态标签的粒度。状态列只写简短级别，细节留给右侧的日志与活动块
/// （docx 23 / B-2026-123）。
/// </summary>
[Collection("engine")]
public sealed class StateLabelTests
{
    /// <summary>六个状态标签都不超过三个字：「部分完成」缩短为「部分」即本条要求。</summary>
    [Fact]
    [Trait("Bug", "B-2026-123")]
    public void StateLabels_MustStayWithinThreeCharacters()
    {
        var labels = new[]
        {
            States.Queued, States.Running, States.Done,
            States.Failed, States.Skipped, States.Partial,
        };

        Assert.All(labels, text => Assert.True(text.Length <= 3, $"「{text}」超过了三个字"));
    }

    /// <summary>缩短之后部分与完成仍可区分：前者是安全停下，却有内容未解。</summary>
    [Fact]
    public void Partial_MustStayDistinctFromDone()
    {
        Assert.NotEqual(States.Done, States.Partial);
    }

    /// <summary>汇总须报四项：完成、失败、因故跳过与用户停止。漏掉最后一项时，被停下的那一批会被算进「跳过」（B-2026-126）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-126"), Trait("Bug", "B-2026-226")]
    public void BatchFinished_MustCarryTheSkippedCount()
    {
        var filled = string.Format(CultureInfo.InvariantCulture, Ui.BatchFinishedAt, 1, 2, 3, 4);

        // 钉「已跳过 3」而不是「跳过 3」：后者是前者的子串，取值从「跳过」改成「已跳过」之后
        // 断言会**靠巧合继续通过** —— 那样它就验不出「与状态列同词」这件事了。
        Assert.Contains("已跳过 3", filled, StringComparison.Ordinal);
        Assert.Contains("已停止 4", filled, StringComparison.Ordinal);
        // 第一格与图例、状态列同词（都数 JobState.Done）⇒ 钉「完成 1」。
        Assert.Contains("完成 1", filled, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取消与因故跳过在状态机里同属「已跳过」那一档，状态列的词要分开：
    /// 用户点停止的写「已停止」，程序因故没解的写「已跳过」。跑批一开就被停的那一批走的就是这条路。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-226")]
    public async Task CancelledRows_MustReadAsStopped()
    {
        var items = new List<Job> { Item() };
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(
            items, TestPaths.NewEngine(), vault, new AppConfig(), new RunnerHooks(), shouldStop: () => true);

        await runner.RunAsync();

        Assert.Equal(JobState.Skipped, items[0].State);
        Assert.Equal(Errors.Stopped, Runner.StatusLabel(items[0]));
        // 备注为什么是空的：见 Runner.NoteFor 里取消那一支。
        Assert.Null(items[0].Reason);
    }

    /// <summary>
    /// 用户点「停止」时**正在解压**的那一项走的是结果映射那一支（不是开跑前那一支）：它同样写「已停止」、备注同样留空。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-226")]
    public async Task StoppingWhileAnItemRuns_MustAlsoReadAsStopped()
    {
        using var temp = new TestDirectory();
        var source = temp.Combine("x.zip");
        File.WriteAllText(source, "payload");
        var items = new List<Job> { new() { ArchivePath = source, Kind = "ZIP", DisplayName = "x.zip" } };
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        using var cts = new CancellationTokenSource();

        // 这一项一进「解压中」就取消：穿透层动手之前先看取消，于是这一趟以 Cancelled 收口，结果照常映射到清单行。
        var runner = new Runner(
            items,
            TestPaths.NewEngine(),
            vault,
            new AppConfig(),
            new RunnerHooks
            {
                OnItem = item =>
                {
                    if (item.State is JobState.Running)
                    {
                        cts.Cancel();
                    }
                },
            },
            cancellationToken: cts.Token);

        await runner.RunAsync();

        Assert.Equal(JobState.Skipped, items[0].State);
        Assert.Equal(Errors.Stopped, Runner.StatusLabel(items[0]));
        Assert.Null(items[0].Reason);
    }

    /// <summary>
    /// 因故跳过的三档照旧写「已跳过」，起因留在备注里；不可运行的行与文件夹汇总行也不许借状态词。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-226")]
    public void SkippedForAReason_MustKeepItsWordAndItsCause()
    {
        var skipped = Item(JobState.Skipped);
        Assert.Equal(States.Skipped, Runner.StatusLabel(skipped));

        // 输出目录已存在：状态是「已跳过」，备注点名那个包 —— 这一档不是用户停的。
        var exists = new PierceResult { Ok = true, StopReason = StopReason.OutputExists, StopDetail = "输出目录已存在：x" };
        Assert.Equal(JobState.Skipped, Runner.MapState(exists));
        Assert.NotNull(Runner.NoteFor(exists));

        Assert.Equal(Ui.NotRunnableState, Runner.StatusLabel(Item(runnable: false)));
        Assert.Equal(string.Empty, Runner.StatusLabel(Item(isDir: true)));
    }

    /// <summary>
    /// 引擎退出码 1：有东西没解全、引擎没报错。整趟落「部分完成」而不是失败，提示写警告那一句，
    /// 命令行那一侧也不算成功。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-228")]
    public void EngineWarning_MustLandInPartialWithItsOwnSentence()
    {
        var warned = Warned();

        Assert.Equal(JobState.Partial, Runner.MapState(warned));
        Assert.False(Runner.IsDone(warned));
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Format.EngineWarningExitCodeAt, 1), Runner.NoteFor(warned));

        // 结论那一句也不许说「失败」：引擎自己没报错，说失败会让人把已解好的东西删掉重来。
        Assert.Contains(States.Partial, warned.Summary(), StringComparison.Ordinal);
        Assert.DoesNotContain(PierceText.StopExtractFailed, warned.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 失败那一行的备注要说清这是哪一类失败（停止原因的既有说法），而不是一行引擎原话：
    /// 原话由同一行的说明栏另外挂着（<c>ReasonDetail</c>），备注再抄一遍就成了「解压失败 · 解压失败」。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-232")]
    public void FailedRow_MustNameTheKindOfFailureOnce()
    {
        var failed = new PierceResult
        {
            Ok = false,
            StopReason = StopReason.ExtractFailed,
            StopDetail = "ERROR: The system cannot find the file specified",
        };

        Assert.Equal(JobState.Failed, Runner.MapState(failed));
        Assert.Equal(PierceText.StopExtractFailed, Runner.NoteFor(failed));

        // 引擎原话留在说明栏那一格（界面把它与备注拼成一条），所以这里不许再抄进备注。
        Assert.DoesNotContain("cannot find the file", Runner.NoteFor(failed)!, StringComparison.Ordinal);
    }

    /// <summary>一趟解出过内容、但引擎报了警告的结果：层结果算成功，警告那一句挂在层备注上。</summary>
    private static PierceResult Warned()
    {
        var result = new PierceResult { Ok = true, StopReason = StopReason.NoArchive };
        result.Layers.Add(new LayerResult
        {
            Depth = new PackLayer(1),
            Target = "x.zip",
            OutDir = "out",
            Ok = true,
            Warned = true,
            Note = string.Format(CultureInfo.InvariantCulture, Format.EngineWarningExitCodeAt, 1),
        });
        return result;
    }

    private static Job Item(JobState state = JobState.Queued, bool runnable = true, bool isDir = false) => new()
    {
        ArchivePath = "x.zip",
        Kind = "ZIP",
        DisplayName = "x.zip",
        State = state,
        Runnable = runnable,
        IsDir = isDir,
    };
}
