using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 状态映射与任务编排。
/// 以及「扫描与任务编排」那一条入口一致性。
/// </summary>
[Collection("engine")]
public sealed class RunnerTests
{
    /// <summary>
    /// 十三个停止原因各自映射到哪个状态。判定次序本身就是逻辑：
    /// 有内容没解必须排在完成前面，取消与输出目录已存在也必须排在完成前面。
    ///
    /// 撞层数上限那一行期望「部分完成」而不是「完成」：它必然还有包没解（外部审计第 1 轮 D-8），
    /// 这一位即便为真也不许显示成完成。
    /// </summary>
    [Theory]
    [InlineData(StopReason.NoArchive, true, JobState.Done)]
    [InlineData(StopReason.MaxDepth, true, JobState.Partial)]
    [InlineData(StopReason.MaxDepth, false, JobState.Partial)]
    [InlineData(StopReason.AlreadyVisited, true, JobState.Done)]
    [InlineData(StopReason.Ambiguous, true, JobState.Done)]
    [InlineData(StopReason.SearchIncomplete, true, JobState.Partial)]
    [InlineData(StopReason.Cancelled, true, JobState.Skipped)]
    [InlineData(StopReason.OutputExists, true, JobState.Skipped)]
    [InlineData(StopReason.PasswordExhausted, false, JobState.Skipped)]
    [InlineData(StopReason.PasswordSkipped, false, JobState.Skipped)]
    [InlineData(StopReason.ExtractFailed, false, JobState.Failed)]
    [InlineData(StopReason.EmptyOutput, false, JobState.Failed)]
    [InlineData(StopReason.NoSpace, false, JobState.Failed)]
    [InlineData(StopReason.SpaceGuard, false, JobState.Failed)]
    public void MapState_MustCoverEveryStopReason(StopReason reason, bool ok, JobState expected)
    {
        var result = new PierceResult { Ok = ok, StopReason = reason };

        Assert.Equal(expected, Runner.MapState(result));
    }

    /// <summary>
    /// 「这一趟算不算做完」的口径与 1.1.0 逐字相同：取消与「有内容没解」算没做完，
    /// 密码全试完也是没做完（那一位是「不是异常」为假），其余按那一位判。
    ///
    /// 输出目录已存在按这个口径**算做完**（它安全停下了、也没有内容没解）——
    /// 这是 1.1.0 的口径，本仓保持同一答案；界面状态仍然是「已跳过」并点名那个没解的包，
    /// 所以不会把没解的东西显示成完成。要改成算没做完，动的就是这一个函数。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-014"), Trait("Bug", "B-2026-017"), Trait("Bug", "B-2026-039"),
     Trait("Category", "ledger")]
    public void MapState_UnfinishedContentMustWinOverDone()
    {
        var partial = new PierceResult { Ok = true, StopReason = StopReason.MaxDepth };
        partial.Leftover.Add("x.zip");
        Assert.Equal(JobState.Partial, Runner.MapState(partial));
        Assert.False(Runner.IsDone(partial), "有内容没解时命令行口径不许判成功");

        // 取消：产物可能只有百分之几，状态不许是完成，命令行也不许判成功。
        var cancelled = new PierceResult { Ok = true, StopReason = StopReason.Cancelled };
        Assert.Equal(JobState.Skipped, Runner.MapState(cancelled));
        Assert.False(Runner.IsDone(cancelled));

        // 密码全试完：那一位是「不是异常」为假，所以命令行不判成功。
        var exhausted = new PierceResult { Ok = false, StopReason = StopReason.PasswordExhausted };
        Assert.Equal(JobState.Skipped, Runner.MapState(exhausted));
        Assert.False(Runner.IsDone(exhausted));

        // 输出目录已存在、按设置跳过：状态是「已跳过」并点名那个包；口径上它算安全停下。
        var exists = new PierceResult { Ok = true, StopReason = StopReason.OutputExists };
        Assert.Equal(JobState.Skipped, Runner.MapState(exists));
        Assert.NotNull(Runner.NoteFor(exists));
        Assert.True(Runner.IsDone(exists));

        // 对照：真的解完了才是完成，命令行退出码为 0。
        var done = new PierceResult { Ok = true, StopReason = StopReason.NoArchive };
        Assert.Equal(JobState.Done, Runner.MapState(done));
        Assert.True(Runner.IsDone(done));
    }

    /// <summary>有内容没解时备注要点名是哪些包：计数加前三个名字。</summary>
    [Fact]
    [Trait("Bug", "B-2026-085"), Trait("Category", "ledger")]
    public void NoteFor_MustNameTheUnfinishedPackages()
    {
        var result = new PierceResult { Ok = true, StopReason = StopReason.Ambiguous };
        result.Leftover.AddRange(["a.zip", "b.zip", "c.zip", "d.zip", "e.zip"]);

        var note = Runner.NoteFor(result);

        Assert.NotNull(note);
        Assert.Contains("有 5 个包未能解压", note, StringComparison.Ordinal);
        Assert.Contains("a.zip", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// 计数卡的键要覆盖每一个状态，包括计数为零的。
    /// 漏一个键就是无保护的下标累加崩掉整批，不是断言红。
    /// </summary>
    [Fact]
    public void Summarize_MustHaveAKeyForEveryState()
    {
        var items = new List<Job>
        {
            new() { ArchivePath = "a", DisplayName = "a", Kind = "x" },
            new() { ArchivePath = "b", DisplayName = "b", Kind = "x", IsDir = true, Runnable = false },
        };

        var counts = Runner.Summarize(items);

        Assert.Equal(Enum.GetValues<JobState>().Length, counts.Count);
        foreach (var state in Enum.GetValues<JobState>())
        {
            Assert.True(counts.ContainsKey(state), "计数卡少了这个状态：" + state);
        }

        Assert.Equal(1, counts[JobState.Queued]);
    }

    /// <summary>
    /// 可重跑按**停止原因**判，不按状态判：排队与失败要；
    /// 已跳过与部分完成里，只有「改配置能改掉」的三种放行 —— 撞层数上限（把上限调大）、
    /// 重名跳过（改成改名）、密码耗尽（补上密码）。这不是放松判据：下面那一半逐条钉住不该放行的仍不放行。
    /// </summary>
    [Fact]
    public void NeedsRun_MustFollowTheStopReasonInsteadOfTheState()
    {
        Assert.True(Runner.NeedsRun(Job(JobState.Queued)));
        Assert.True(Runner.NeedsRun(Job(JobState.Failed)));
        Assert.False(Runner.NeedsRun(Job(JobState.Done)));

        foreach (var state in new[] { JobState.Partial, JobState.Skipped })
        {
            Assert.True(Runner.NeedsRun(Job(state, StopReason.MaxDepth)), $"{state} 撞层数上限该可重跑");
            Assert.True(Runner.NeedsRun(Job(state, StopReason.OutputExists)), $"{state} 重名跳过该可重跑");
            Assert.True(Runner.NeedsRun(Job(state, StopReason.PasswordExhausted)), $"{state} 密码耗尽该可重跑");

            foreach (var reason in new[]
            {
                StopReason.AlreadyVisited, StopReason.Ambiguous, StopReason.SearchIncomplete,
                StopReason.PasswordSkipped, StopReason.Cancelled, StopReason.ExtractFailed,
            })
            {
                Assert.False(Runner.NeedsRun(Job(state, reason)), $"{state} 的 {reason} 不该放行");
            }

            Assert.False(Runner.NeedsRun(Job(state)), $"{state} 没有原因时不该放行");
        }
    }

    /// <summary>重跑之前要把上一次的停止原因清掉，否则会带进下一次的判定。</summary>
    [Fact]
    public void Reset_MustDropTheStopReason()
    {
        var item = Job(JobState.Partial, StopReason.MaxDepth);

        item.Reset();

        Assert.Null(item.StopReason);
        Assert.Equal(JobState.Queued, item.State);
    }

    /// <summary>「打开输出目录」取的是第一层产物目录，而且部分完成的也要给。</summary>
    [Fact]
    public void OutputDirs_MustPreferTheFirstLayerAndIncludePartial()
    {
        using var temp = new TestDirectory();
        var first = temp.Combine("first");
        Directory.CreateDirectory(first);

        var items = new List<Job>
        {
            new() { ArchivePath = "a", DisplayName = "a", Kind = "x", State = JobState.Done, OutputTop = first },
            new() { ArchivePath = "b", DisplayName = "b", Kind = "x", State = JobState.Partial, OutputTop = first },
            new() { ArchivePath = "c", DisplayName = "c", Kind = "x", State = JobState.Failed, OutputTop = first },
            new() { ArchivePath = "d", DisplayName = "d", Kind = "x", State = JobState.Done, OutputTop = temp.Combine("missing") },
        };

        var dirs = Runner.OutputDirs(items);

        Assert.Equal([first], dirs);
    }

    /// <summary>
    /// 取消或失败的那一项照样要进「打开输出目录」：半成品正是这两档留在盘上的东西，
    /// 按状态把它们筛掉，用户既看不到产物在哪、也没有清理入口（B-2026-185）；还在排队或正在跑的项不算 —— 产物目录那一刻还没定。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-231")]
    public void OutputDirs_MustIncludeWhatCancelledAndFailedRunsLeft()
    {
        using var temp = new TestDirectory();
        var leftovers = temp.Combine("leftovers");
        Directory.CreateDirectory(leftovers);

        var items = new List<Job>
        {
            new() { ArchivePath = "a", DisplayName = "a", Kind = "x", State = JobState.Skipped, OutputTop = leftovers },
            new() { ArchivePath = "b", DisplayName = "b", Kind = "x", State = JobState.Failed, OutputTop = leftovers },
        };

        // 取消项（Skipped）与失败项都在里面：它们正是半成品留在盘上的那两档。
        Assert.Equal([leftovers], Runner.OutputDirs(items));

        var unsettled = new List<Job>
        {
            new() { ArchivePath = "c", DisplayName = "c", Kind = "x", State = JobState.Running, OutputTop = leftovers },
            new() { ArchivePath = "d", DisplayName = "d", Kind = "x", State = JobState.Queued, OutputTop = leftovers },
        };

        Assert.Empty(Runner.OutputDirs(unsettled));
    }

    /// <summary>
    /// 「跳过」那一格按起因分开：用户点「停止」停下的另记一格，因故跳过的仍算「跳过」，
    /// 两者相加等于 Skipped 那一格。图例与批末摘要取这一组判据，文件夹汇总行自己数（见 <c>Rollup_MustTellStoppedFromSkipped</c>）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-226")]
    public void StoppedAndSkipped_MustSplitTheSkippedBucket()
    {
        var items = new List<Job>
        {
            new() { ArchivePath = "a", DisplayName = "a", Kind = "x", State = JobState.Skipped, StoppedByUser = true },
            new() { ArchivePath = "b", DisplayName = "b", Kind = "x", State = JobState.Skipped },
            new() { ArchivePath = "c", DisplayName = "c", Kind = "x", State = JobState.Done },
            new() { ArchivePath = "d", DisplayName = "d", Kind = "x", State = JobState.Running, StoppedByUser = true },
        };

        Assert.Equal(1, Runner.StoppedCount(items));
        Assert.Equal(1, Runner.SkippedCount(items));
        Assert.Equal(
            Runner.Summarize(items)[JobState.Skipped],
            Runner.StoppedCount(items) + Runner.SkippedCount(items));
    }

    /// <summary>
    /// 文件夹汇总行的那一格同样按起因分开：因故跳过与用户停止各记一个数，合成一个数时这一行就少一位。
    /// 那一行的分法只有这里钉得住 —— <c>Rollup</c> 是 private，跑一批带子项的清单是唯一入口。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-226")]
    public async Task Rollup_MustTellStoppedFromSkipped()
    {
        var folder = new Job { ArchivePath = "资料", DisplayName = "资料", Kind = "DIR", IsDir = true, Runnable = false };
        folder.Children.AddRange([1, 2, 3]);
        var items = new List<Job>
        {
            folder,
            new() { ArchivePath = "a", DisplayName = "a", Kind = "x", State = JobState.Skipped, StoppedByUser = true },
            new() { ArchivePath = "b", DisplayName = "b", Kind = "x", State = JobState.Skipped },
            new() { ArchivePath = "c", DisplayName = "c", Kind = "x", State = JobState.Done },
        };

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        await new Runner(items, TestPaths.NewEngine(), vault, new AppConfig(), new RunnerHooks()).RunAsync();

        Assert.Equal("3 个包、1 成功、1 已跳过、1 已停止", folder.Reason);
    }

    /// <summary>公共上级没有意义时返回空串：只有一个目录、跨盘符、或只剩盘符根。</summary>
    [Fact]
    public void OutputRootOf_MustReturnEmptyWhenItMeansNothing()
    {
        using var temp = new TestDirectory();
        var left = temp.Combine("a", "x");
        var right = temp.Combine("a", "y");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);

        Assert.Equal(string.Empty, Runner.OutputRootOf([]));
        Assert.Equal(string.Empty, Runner.OutputRootOf([left]));
        Assert.Equal(temp.Combine("a"), Runner.OutputRootOf([left, right]));
        Assert.Equal(string.Empty, Runner.OutputRootOf([@"D:\a\b", @"E:\a\b"]));
    }

    /// <summary>
    /// UNC 的公共上级要保留前缀：共享根是两段（<c>\\server\share</c>），
    /// 按分隔符切词会去掉前导的反斜杠，于是返回一个相对且不存在的路径（B-2026-156）。
    /// 撤掉「根单独拿出来比」即红；盘符档的结论与 1.1.0 一致。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-156")]
    public void OutputRootOf_MustKeepTheUncPrefix()
    {
        const string root = @"\\server\share";

        Assert.Equal(root + @"\a", Runner.OutputRootOf([root + @"\a\x", root + @"\a\y"]));

        // 公共上级只剩共享根，与「只剩盘符根」同一档：没有意义。
        Assert.Equal(string.Empty, Runner.OutputRootOf([root + @"\a", root + @"\b"]));

        // 跨共享同样没意义。
        Assert.Equal(string.Empty, Runner.OutputRootOf([@"\\s1\share\a", @"\\s2\share\b"]));

        // 盘符档与 1.1.0 一致。
        Assert.Equal(@"C:\a", Runner.OutputRootOf([@"C:\a\x", @"C:\a\y"]));
    }

    /// <summary>半搬要在清单行的备注里点名：外层文件夹只整理了一半（B-2026-155）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-155")]
    public void NoteFor_MustNameThePartialLift()
    {
        var result = new PierceResult { Ok = true, StopReason = StopReason.NoArchive };
        result.Layers.Add(new LayerResult
        {
            Depth = new PackLayer(1),
            Target = "x.zip",
            OutDir = "out",
            Ok = true,
            PartialLift = ["a.txt", "b.txt"],
        });

        var note = Runner.NoteFor(result);

        Assert.NotNull(note);
        Assert.Contains("已经搬走 2 项", note, StringComparison.Ordinal);
        Assert.Contains("a.txt", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// 跑批不许把整批带走：一个包出事，这一条标失败，接着跑下一个。
    /// 而这一批是点开始时那份快照，跑的过程中新加进清单的不属于这一批。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-012"), Trait("Category", "ledger")]
    public async Task Batch_MustSurviveOneFailureAndIgnoreLateArrivals()
    {
        using var temp = new TestDirectory();
        var broken = temp.Combine("a_broken.zip");
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "z_broken.zip"), broken);
        var good = temp.Combine("z_good.zip");
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"), good);

        var items = new List<Job>
        {
            new() { ArchivePath = broken, DisplayName = "a_broken.zip", Kind = "ZIP" },
            new() { ArchivePath = good, DisplayName = "z_good.zip", Kind = "ZIP" },
        };

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(
            items,
            TestPaths.NewEngine(),
            vault,
            new AppConfig(),
            // 跑第一条的时候往**调用方的清单**里加一条：它是下一批的事，不许混进来。
            new RunnerHooks { OnItem = _ => items.Add(Late()) });
        var finished = await runner.RunAsync();

        Assert.Equal(2, finished.Count);
        Assert.Equal(JobState.Failed, finished[0].State);
        Assert.Equal(JobState.Done, finished[1].State);
        Assert.True(File.Exists(Path.Combine(temp.Path, "z_good", "content.txt")));
    }

    private static Job Late() => new()
    {
        ArchivePath = "late.zip",
        DisplayName = "late.zip",
        Kind = "ZIP",
    };

    /// <summary>
    /// 跑批期间源文件被删掉：直接说清楚，不得让引擎只回一句「系统找不到指定的文件」。
    /// 而且这一批的结论不许是全部成功。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-084"), Trait("Bug", "B-2026-031"), Trait("Category", "ledger")]
    public async Task QueuedSourceGone_MustFailHonestly()
    {
        using var temp = new TestDirectory();
        var first = temp.Combine("a.zip");
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"), first);
        var second = temp.Combine("b.zip");
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "a.zip"), second);

        var items = new List<Job>
        {
            new() { ArchivePath = first, DisplayName = "a.zip", Kind = "ZIP" },
            new() { ArchivePath = second, DisplayName = "b.zip", Kind = "ZIP" },
        };

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        File.Delete(second);
        var runner = new Runner(items, TestPaths.NewEngine(), vault, new AppConfig(), new RunnerHooks());
        var finished = await runner.RunAsync();

        Assert.Equal(JobState.Failed, finished[1].State);
        Assert.Contains("源文件已不存在", finished[1].Reason!, StringComparison.Ordinal);
        Assert.Contains(finished, item => item.State is JobState.Failed);
    }

    /// <summary>
    /// 暂停期间不开新任务；正在跑的那一条由引擎层真挂起。
    /// 这里只钉「不开新任务」这一半，真挂起那一条在引擎侧的单测里。
    /// </summary>
    [Fact]
    public async Task Pause_MustNotStartNewItems()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("a.zip");
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"), sample);

        var paused = true;
        var items = new List<Job> { new() { ArchivePath = sample, DisplayName = "a.zip", Kind = "ZIP" } };
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(
            items, TestPaths.NewEngine(), vault, new AppConfig(), new RunnerHooks(),
            shouldPause: () => paused);

        var run = runner.RunAsync();
        var early = await Task.WhenAny(run, Task.Delay(500));
        Assert.NotSame(run, early);
        Assert.Equal(JobState.Queued, items[0].State);

        paused = false;
        _ = await run;
        Assert.Equal(JobState.Done, items[0].State);
    }

    /// <summary>
    /// 排除名单只有一个来源：跑批读的是配置里现取的那一份。
    /// 换上新配置之后同一批立刻看到新名单 —— 撤掉「现取」改成启动副本，这条变红
    /// （B-2026-100）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-100"), Trait("Category", "ledger")]
    public void UseConfig_MustSwapTheExclusionListTheRunSees()
    {
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(
            new List<Job>(), TestPaths.NewEngine(), vault, new AppConfig { ExcludedExtensions = ["apk"] });

        Assert.Equal(new[] { "apk" }, runner.ExcludedExtensions);

        runner.UseConfig(new AppConfig { ExcludedExtensions = ["apk", "docx"] });

        Assert.Equal(new[] { "apk", "docx" }, runner.ExcludedExtensions);
    }

    /// <summary>
    /// 不许再缓存一份：整份配置换掉之后，跑批看到的必须只剩新那一份里的项，
    /// 旧名单里那些不许以任何形式留下来。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-100"), Trait("Category", "ledger")]
    public void UseConfig_MustLeaveNoTraceOfThePreviousList()
    {
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(
            new List<Job>(), TestPaths.NewEngine(), vault, new AppConfig { ExcludedExtensions = ["apk", "docx"] });

        runner.UseConfig(new AppConfig { ExcludedExtensions = [] });

        Assert.Empty(runner.ExcludedExtensions);
    }

    /// <summary>
    /// 「清理文件名中的「删」字」是配置里的一项：关掉之后，拖进来的目录里那些文件的名字一个字都不许改
    /// （清理本身照旧，开着的时候改法不变）。撤销穿透层那一处读取点、回到无条件清理即红。
    /// </summary>
    [Theory]
    [Trait("Bug", "B-2026-237")]
    [InlineData(false, "笔记.t删 x删 t")]
    [InlineData(true, "笔记.txt")]
    public async Task CleanDeleteSwitch_MustDecideWhetherFileNamesAreRewritten(bool cleanDelete, string expected)
    {
        using var temp = new TestDirectory();
        var folder = temp.Combine("拖进来的目录");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "笔记.t删 x删 t"), "x");
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"), Path.Combine(folder, "包.zip"));

        var items = new List<Job>
        {
            new() { ArchivePath = folder, DisplayName = "拖进来的目录", Kind = "文件夹", IsDir = true },
        };
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(
            items, TestPaths.NewEngine(), vault, new AppConfig { CleanDelete = cleanDelete }, new RunnerHooks());
        await runner.RunAsync();

        Assert.Equal(JobState.Done, items[0].State);
        Assert.Equal(
            ["包.zip", expected],
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// 「最大嵌套层数」为 0 时按 1 执行：拖文件夹与拖单个包两种入口的层数上限必须是同一个数，
    /// 而盘上的那个 0 一个字都不许被改写（用户没动过配置）。改回「0 = 一层都不解」即红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-239")]
    public async Task MaxDepthZero_MustMeanOneLayerForBothEntries()
    {
        using var temp = new TestDirectory();
        var folder = temp.Combine("文件夹入口");
        var single = temp.Combine("单包入口");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(single);
        WriteNestedZip(Path.Combine(folder, "甲.zip"), "甲.txt");
        var one = Path.Combine(single, "乙.zip");
        WriteNestedZip(one, "乙.txt");

        var store = new JsonConfigStore(temp.Combine("config.json"));
        store.Save(new AppConfig { MaxDepth = 0 });
        var config = store.Load();

        var items = new List<Job>
        {
            new() { ArchivePath = folder, DisplayName = "文件夹入口", Kind = "文件夹", IsDir = true },
            new() { ArchivePath = one, DisplayName = "乙.zip", Kind = "ZIP" },
        };
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var runner = new Runner(items, TestPaths.NewEngine(), vault, config, new RunnerHooks());
        await runner.RunAsync();

        Assert.Equal(1, items[0].Layer);
        Assert.Equal(items[0].Layer, items[1].Layer);
        Assert.Equal(1, items[0].MaxLayer);
        Assert.Equal(items[0].MaxLayer, items[1].MaxLayer);
        Assert.Equal(JobState.Partial, items[0].State);
        Assert.Equal(JobState.Partial, items[1].State);
        Assert.Equal(0, config.MaxDepth);
        Assert.Equal(0, store.Load().MaxDepth);
    }

    /// <summary>造一个两层套娃：外层里放内层，内层里放一个载荷。载荷名由调用方给，两份样本的内容因此可以不同。</summary>
    private static void WriteNestedZip(string path, string payload)
    {
        using var inner = new MemoryStream();
        using (var zip = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(payload).Open();
            entry.WriteByte((byte)'x');
        }

        inner.Position = 0;
        using var outer = new MemoryStream();
        using (var zip = new ZipArchive(outer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(payload + ".zip").Open();
            inner.CopyTo(entry);
        }

        File.WriteAllBytes(path, outer.ToArray());
    }

    private static Job Job(JobState state, StopReason? reason = null) => new()
    {
        ArchivePath = "x",
        DisplayName = "x",
        Kind = "x",
        State = state,
        StopReason = reason,
    };
}
