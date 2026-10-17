using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 穿透层：逐层解、层数口径、停止原因、账目对账、判重与安全线。
/// 每条断言上面都写了它在钉哪一条。
/// </summary>
[Collection("engine")]
public sealed class PierceTests
{
    /// <summary>
    /// 链长 1 / 2 / 3 与层数预算 1 / 2 / 3 的九格矩阵。
    ///
    /// 预算够时最里层的产物必须在盘上，而且解压动作次数要精确等于链长；
    /// 预算不够时停在预算那一层属于正确行为，但残留名单必须非空 ——
    /// 「达到层数上限」而账是空的，说明判据放错了位置。
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [Trait("Bug", "B-2026-010"), Trait("Category", "ledger")]
    public async Task DepthMatrix_InnermostPayloadMustExistWhenTheBudgetIsEnough(int chain, int cap)
    {
        var run = await PierceHarness.RunAsync($"chain-{chain}", maxDepth: cap);
        try
        {
            if (cap >= chain)
            {
                Assert.Equal(chain, run.Result.MaxDepthReached);
                Assert.False(run.Result.Partial);
                Assert.Equal(StopReason.NoArchive, run.Result.StopReason);
                Assert.True(File.Exists(Path.Combine(run.ProductRoot, "content.txt")),
                    $"链长 {chain} 预算 {cap}：最里层的产物没落盘");
            }
            else
            {
                Assert.Equal(cap, run.Result.MaxDepthReached);
                Assert.Equal(StopReason.MaxDepth, run.Result.StopReason);
                Assert.NotEmpty(run.Result.Leftover);
                Assert.False(run.Result.Ok, "撞了层数上限就是没解完，这一位不许为真");
                Assert.True(run.Result.Partial, "有内容确实没解时必须判成有内容没解");
                Assert.False(File.Exists(Path.Combine(run.ProductRoot, "content.txt")),
                    "预算不够时最里层的产物不许出现");
            }
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 层号只由出口施加，日志里出现的层号必须与真实层深对得上。
    /// 三条一起才是网：① 任何一处把 `Layer` 传大（+1 或别的层）⇒ 出界即红；
    /// ② 任何一处传成 0 ⇒ 那一层的「开始」或「完成」行失去前缀即红；③ 层号集合必须正好是 1..层深。
    /// 这是 W6 遗留第 4 条（F2 那批层号传值原先只有文件名白名单、没有网）的落地。
    /// </summary>
    [Theory]
    [InlineData("chain-1")]
    [InlineData("chain-2")]
    [InlineData("chain-3")]
    public async Task LayerPrefix_MustMatchTheRealDepth(string sample)
    {
        var run = await PierceHarness.RunAsync(sample);
        try
        {
            var deepest = run.Result.MaxDepthReached;
            Assert.True(deepest > 0, $"{sample}：一层都没解出来，这条判据量不到东西");

            var seen = new List<int>();
            foreach (var line in run.Logs)
            {
                var match = Regex.Match(line, @"^第 (\d+) 层 ");
                if (match.Success)
                {
                    seen.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }

            Assert.All(seen, layer => Assert.InRange(layer, 1, deepest));
            Assert.Equal(Enumerable.Range(1, deepest), seen.Distinct().OrderBy(layer => layer));

            for (var layer = 1; layer <= deepest; layer++)
            {
                Assert.Contains(run.Logs, line =>
                    line.StartsWith($"第 {layer} 层 ", StringComparison.Ordinal)
                    && line.Contains("解压到", StringComparison.Ordinal));
                Assert.Contains(run.Logs, line =>
                    line.StartsWith($"第 {layer} 层 完成", StringComparison.Ordinal));
            }
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 一条链的产物树形状：三层同名套娃各自收壳之后，产物只留一层目录。
    /// 这条同时钉住「中间层包解完删掉」与「产物上提收壳」两件事。
    /// </summary>
    [Theory]
    [InlineData("chain-1")]
    [InlineData("chain-2")]
    [InlineData("chain-3")]
    public async Task Chain_MustCollapseToASingleProductDirectory(string sample)
    {
        var run = await PierceHarness.RunAsync(sample);
        try
        {
            Assert.Equal(
                ["outer.zip", "outer/", "outer/content.txt"],
                run.Tree(run.Directory));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 五十层深链：层数真的走到五十、最里层的载荷真的在盘上，而且这一趟跑得完。
    /// 「跑得完并返回」本身就是无栈溢出、无无限递归的证据。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-067"), Trait("Category", "ledger")]
    public async Task DeepChain_MustReachFiftyLayers()
    {
        var run = await PierceHarness.RunAsync("deep-50", maxDepth: 60);
        try
        {
            Assert.Equal(50, run.Result.MaxDepthReached);
            Assert.Equal(50, run.Result.DeepestLayer.Value);
            Assert.Equal([$"L1.zip", "L1/", "L1/payload.txt"], run.Tree(run.Directory));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 一百层深链：五十层那一档考的是"回收与去重跟得上"，一百层考的是**层数预算**本身 ——
    /// 预算没给够时穿透会停在半路，而"停在半路"与"链条只有那么深"在结果里长得一样。
    ///
    /// 挂真机档而不是 档：成本随层数线性涨，
    /// 不该进日常套件。五十层那一档留在 <c>ledger</c> 里。
    /// </summary>
    [Fact, Trait("Category", "machine")]
    public async Task DeepChain_MustReachOneHundredLayers()
    {
        var run = await PierceHarness.RunAsync("deep-100", maxDepth: 110);
        try
        {
            Assert.Equal(100, run.Result.MaxDepthReached);
            Assert.Equal(100, run.Result.DeepestLayer.Value);
            Assert.Equal([$"L1.zip", "L1/", "L1/payload.txt"], run.Tree(run.Directory));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 同层多包：每个包各解一份、内容逐字节正确 —— 两份内容相同的**兄弟**包也不例外
    /// （判重的参照集是链上的祖先，兄弟不是祖先）；坏包明确失败，真失败胜出。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-019"), Trait("Category", "ledger")]
    public async Task SameLevel_EachPackageSolvedAndTheRealFailureWins()
    {
        var run = await PierceHarness.RunAsync("same-level");
        try
        {
            var root = run.Directory;
            Assert.Equal("payload A\n", File.ReadAllText(Path.Combine(root, "a", "a.txt")));
            Assert.Equal("payload B\n", File.ReadAllText(Path.Combine(root, "b", "b.txt")));

            // 内容相同的两份同处一层：各解一份，都不记残留。
            Assert.Equal("duplicated payload\n", File.ReadAllText(Path.Combine(root, "same1", "same.txt")));
            Assert.Equal("duplicated payload\n", File.ReadAllText(Path.Combine(root, "same2", "same.txt")));
            Assert.DoesNotContain(
                run.Result.Leftover, name => name.Contains("same2.zip", StringComparison.Ordinal));

            Assert.Contains(run.Result.Layers, layer => !layer.Ok);
            Assert.False(run.Result.Ok);
            Assert.False(run.Result.StopReason.IsCleanStop());
            Assert.Equal(StopReason.ExtractFailed, run.Result.StopReason);
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 安全停下不许把真失败顶掉。三层不变量一起钉：
    /// 有失败层时最终原因不许落在安全停下那一类、结果不许判成安全停下、
    /// 「有失败层却一切正常」这个组合必须不可能。
    ///
    /// 更早那处干净停下由本格自己造：产物目录已存在、按跳过档处理（`OUTPUT_EXISTS`，候选排在字母序前面），
    /// 坏包随后失败 ⇒ 日志里先「已跳过」、后失败。两道机制各挡一层（分类函数与收尾收敛），
    /// 撤掉其中一道不会红，两道都撤才红；分类函数那四条由 `FailureClassification_MustFollowTheFourRules` 单钉。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-073"), Trait("Bug", "B-2026-074")]
    public async Task RealFailure_MustOverrideTheCleanStopRecordedEarlier()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("override");
        Directory.CreateDirectory(sample);
        WriteStoredZip(Path.Combine(sample, "a.zip"), [("a.txt", "payload A\n")]);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "z_broken.zip"),
            Path.Combine(sample, "z_broken.zip"));
        Directory.CreateDirectory(Path.Combine(sample, "a"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var logs = new List<string>();
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions
            {
                MaxDepth = 5,
                MinFreeGb = 0,
                Conflict = ConflictPolicy.Skip,
                Log = new ChannelLog((_, message) => logs.Add(message)),
            });
        var result = await piercer.RunAsync(sample);

        // 前提也要钉住：更早那处干净停下必须真的发生过、而且排在真失败之前。
        // 不钉它，前提一旦消失，这条断言照样通过。
        var skipped = logs.FindIndex(line => line.Contains("已跳过", StringComparison.Ordinal));
        var failedAt = logs.FindIndex(line => line.Contains("引擎无法读取该压缩包", StringComparison.Ordinal));
        Assert.True(skipped >= 0, "更早那处干净停下没有发生，这一格失去前提");
        Assert.True(failedAt > skipped, "真失败必须发生在干净停下之后");

        Assert.Contains(result.Layers, layer => !layer.Ok);
        Assert.False(result.Ok);
        Assert.False(result.StopReason.IsCleanStop());
        Assert.False(result.Ok && !result.Partial, "有失败层时不许判成「一切正常」");
        Assert.False(Runner.IsDone(result), "命令行口径也不许判成功");
        Assert.Equal(StopReason.ExtractFailed, result.StopReason);
    }

    /// <summary>
    /// 分类函数的四条判据：真失败盖掉已记下的安全停下；安全停下之间先到先得；
    /// 真失败之间先到先得；空槽位照记。
    /// </summary>
    [Fact]
    public void FailureClassification_MustFollowTheFourRules()
    {
        var clean = StopReason.SearchIncomplete;
        var otherClean = StopReason.AlreadyVisited;
        var real = StopReason.ExtractFailed;
        var otherReal = StopReason.PasswordExhausted;

        var empty = Piercer.PreferFailure(null, clean, "d");
        Assert.Equal(clean, empty.Reason);

        var cleanThenClean = Piercer.PreferFailure(empty, otherClean, "d2");
        Assert.Equal(clean, cleanThenClean.Reason);

        var cleanThenReal = Piercer.PreferFailure(empty, real, "d3");
        Assert.Equal(real, cleanThenReal.Reason);

        var realThenReal = Piercer.PreferFailure(cleanThenReal, otherReal, "d4");
        Assert.Equal(real, realThenReal.Reason);
    }

    /// <summary>
    /// 退出码 1 的那一层算解出来了（有东西没解全、引擎没报错），整趟结果才可能落「部分完成」；
    /// 2 是真失败、0 是成功，被本实现主动中止的那几档也不得当成功。撤掉「或警告」这一支，退出码 1 的层会落回失败。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-228")]
    public void LayerSucceeded_MustCountTheWarningCodeAsExtracted()
    {
        Assert.True(Piercer.LayerSucceeded(new EngineResult { Code = 0 }));
        Assert.True(Piercer.LayerSucceeded(new EngineResult { Code = 1 }));

        Assert.False(Piercer.LayerSucceeded(new EngineResult { Code = 2 }));
        Assert.False(Piercer.LayerSucceeded(new EngineResult { Code = 1, Cancelled = true }));
        Assert.False(Piercer.LayerSucceeded(new EngineResult { Code = 1, SpaceGuardHit = true }));
    }

    /// <summary>
    /// 层失败的备注两半都要在：结论说清是哪一类失败，引擎原话照抛不改。密码错那一档的结论只由
    /// <c>Brief</c> 给、原话不给，备注因此要把原话补上 —— 只剩一句「密码错误」时用户没有复核的依据；
    /// 其它档的原话已经并进 <c>Brief</c> 了，备注不许再拼一遍。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-233")]
    public void LayerFailureNote_MustKeepTheEngineWordsNextToTheConclusion()
    {
        var wrong = new EngineResult
        {
            Code = 2,
            WrongPassword = true,
            Output = "ERROR: Wrong password : x.zip\n",
        };

        Assert.Equal(
            string.Format(Format.ConclusionWithOutputAt, Errors.ExitPassword, wrong.Tail),
            Piercer.LayerFailureNote(wrong));

        // 其它档：结论与原文已经合成一条，这里原样交出去。
        var structural = new EngineResult { Code = 2, Output = "ERROR: Cannot open the file as archive\n" };
        Assert.Equal(structural.Brief, Piercer.LayerFailureNote(structural));
        Assert.Contains("Cannot open the file as archive", Piercer.LayerFailureNote(structural), StringComparison.Ordinal);

        // 连可读原话都没有时退到结论本身，不许留一个空尾巴。
        Assert.Equal(Errors.ExitPassword, Piercer.LayerFailureNote(new EngineResult { Code = 2, WrongPassword = true }));

        // 被本实现主动中止的那几档排在密码错前面（次序与 Brief 一致）：那是用户或本程序主动停的，
        // 说成密码错会把用户支去改密码。三档各钉一次，免得次序被调换时没人发现。
        Assert.StartsWith(
            Errors.Stopped,
            Piercer.LayerFailureNote(WrongBut(new EngineResult { Cancelled = true })),
            StringComparison.Ordinal);
        Assert.StartsWith(
            Errors.Timeout,
            Piercer.LayerFailureNote(WrongBut(new EngineResult { TimedOut = true })),
            StringComparison.Ordinal);
        Assert.StartsWith(
            Errors.SpaceGuardAborted,
            Piercer.LayerFailureNote(WrongBut(new EngineResult { SpaceGuardHit = true })),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Errors.ExitPassword,
            Piercer.LayerFailureNote(WrongBut(new EngineResult { TimedOut = true })),
            StringComparison.Ordinal);
    }

    /// <summary>密码错与「被本实现主动中止」同时成立的那一格：引擎原话里确实有密码错的字样。</summary>
    private static EngineResult WrongBut(EngineResult stopped) => stopped with
    {
        Code = 2,
        WrongPassword = true,
        Output = "ERROR: Wrong password : x.zip\n",
    };

    /// <summary>
    /// 残留名单的对账只销「本进程删掉的」条目。
    ///
    /// 三种结局分开：还在盘上就留；不在了但本进程没删过也留（那是没解释的消失，必须报）；
    /// 不在了且本进程删过，才是真销账。按「此刻在不在盘上」销账会把外部删掉的那一条静默销账。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-013"), Trait("Bug", "B-2026-084"), Trait("Category", "ledger")]
    public void ReconcileLeftover_MustOnlyClearWhatThisProcessDeleted()
    {
        using var temp = new TestDirectory();
        var stillHere = temp.Combine("still-here.zip");
        File.WriteAllText(stillHere, "x");
        var deletedByUs = temp.Combine("deleted-by-us.zip");
        var deletedOutside = temp.Combine("deleted-outside.zip");

        var registry = new PierceRegistry();
        registry.NoteRemoved(deletedByUs);
        var piercer = new Piercer(
            TestPaths.NewEngine(), new Vault(), new PierceOptions { Registry = registry });
        var result = new PierceResult
        {
            Ok = true,
            OutputDir = temp.Path,
            StopReason = StopReason.MaxDepth,
        };
        result.Leftover.AddRange(["still-here.zip", "deleted-by-us.zip", "deleted-outside.zip"]);

        piercer.ReconcileLeftover(result);

        Assert.Equal(["still-here.zip", "deleted-outside.zip"], result.Leftover);
    }

    /// <summary>
    /// 提前收工之后，点名里的每一个包都必须能在盘上找到；日志里点名的同样要能找到。
    /// 这是「点名的不许是幽灵包」这条判据的可核对形式。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-013"), Trait("Category", "ledger")]
    public async Task LeftoverNames_MustExistOnDiskAfterTheRunReturns()
    {
        var run = await PierceHarness.RunAsync("deep-50", maxDepth: 3);
        try
        {
            Assert.Equal(StopReason.MaxDepth, run.Result.StopReason);
            Assert.NotEmpty(run.Result.Leftover);
            foreach (var relative in run.Result.Leftover)
            {
                var full = Path.Combine(run.Result.OutputDir, relative);
                Assert.True(File.Exists(full), "残留名单点名了盘上没有的包：" + relative);
            }

            Assert.Contains(run.Logs, line => line.Contains("已达到层数上限", StringComparison.Ordinal));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 内容指纹只是预筛：两个首尾各 64KB 相同、只有中间一个字节不同的包是**两个**包，
    /// 必须各解一份，而且日志里不许断言「内容相同」。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-080"), Trait("Category", "ledger")]
    public async Task FingerprintCollision_MustNotSwallowTheSecondPackage()
    {
        var run = await PierceHarness.RunAsync("fingerprint");
        try
        {
            Assert.True(File.Exists(Path.Combine(run.Directory, "a", "payload.bin")));
            Assert.True(File.Exists(Path.Combine(run.Directory, "b", "payload.bin")),
                "指纹碰撞时第二个包的内容没落盘");
            Assert.Empty(run.Result.Leftover);
            Assert.NotEqual(StopReason.AlreadyVisited, run.Result.StopReason);
            Assert.DoesNotContain(run.Logs, line => line.Contains(PierceText.SameContent, StringComparison.Ordinal)
                && !line.Contains(PierceText.SameContentUnverified, StringComparison.Ordinal));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 判重的参照集是**链上的祖先**，不是这一批见过的所有包：同一个目录里两个内容相同的包
    /// 是兄弟而不是祖先，两个入口都必须**各解一份**，都不记残留（B-2026-090）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-090"), Trait("Category", "ledger")]
    public async Task SameDirectoryDuplicates_MustEachBeExtractedOnBothEntryPoints()
    {
        var source = Path.Combine(TestPaths.PierceRoot, "same-level", "same1.zip");

        // 目录入口：一趟穿透跑完整个目录，两份都解出来。
        using (var direct = new TestDirectory())
        {
            File.Copy(source, direct.Combine("dup-a.zip"));
            File.Copy(source, direct.Combine("dup-b.zip"));

            var result = await RunOneAsync(direct.Path, new PierceRegistry());

            Assert.True(result.Ok);
            Assert.False(result.Partial, "两份内容相同也各解一份，不许报「有内容没解」");
            Assert.Equal(2, CountPayloads(direct.Path));
            Assert.Empty(result.Leftover);
        }

        // 清单入口：扫成两行，再让批处理跑一遍。两边的结论必须一致 —— 都各解一份。
        using (var listed = new TestDirectory())
        {
            File.Copy(source, listed.Combine("dup-a.zip"));
            File.Copy(source, listed.Combine("dup-b.zip"));

            var items = Scan.Run([listed.Path]).ToList();
            var vault = new Vault();
            vault.SetEntries(Array.Empty<string>());
            var runner = new Runner(
                items,
                TestPaths.NewEngine(),
                vault,
                new AppConfig(),
                new RunnerHooks());
            _ = await runner.RunAsync();

            Assert.Equal(2, CountPayloads(listed.Path));
            Assert.DoesNotContain(items, item => item.State is JobState.Partial);
        }
    }

    /// <summary>
    /// 单文件入口与目录入口的结论必须一致：解压结果、命令行成功口径、第一层产物名三处。
    /// 矩阵：输出根 有/无 × 冲突策略 改名/跳过 × 预建同名目录 是/否。
    /// </summary>
    [Theory]
    [InlineData(false, ConflictPolicy.Rename, false)]
    [InlineData(false, ConflictPolicy.Rename, true)]
    [InlineData(false, ConflictPolicy.Skip, false)]
    [InlineData(false, ConflictPolicy.Skip, true)]
    [InlineData(true, ConflictPolicy.Rename, false)]
    [InlineData(true, ConflictPolicy.Rename, true)]
    [InlineData(true, ConflictPolicy.Skip, false)]
    [InlineData(true, ConflictPolicy.Skip, true)]
    [Trait("Bug", "B-2026-082"), Trait("Bug", "B-2026-083"), Trait("Category", "ledger")]
    public async Task BothEntryPoints_MustAgree(bool useOutputRoot, ConflictPolicy conflict, bool preCreate)
    {
        var fileResult = await EntryScenarioAsync(asDirectory: false, useOutputRoot, conflict, preCreate);
        var dirResult = await EntryScenarioAsync(asDirectory: true, useOutputRoot, conflict, preCreate);

        Assert.Equal(fileResult.Ok, dirResult.Ok);
        Assert.Equal(fileResult.IsDone, dirResult.IsDone);
        Assert.Equal(fileResult.FirstProductName, dirResult.FirstProductName);
    }

    /// <summary>入口层的落点：目录入口的入口层同样走输出根，更深层才跟着上一层走。</summary>
    [Fact]
    [Trait("Bug", "B-2026-082"), Trait("Category", "ledger")]
    public async Task DirectoryEntry_MustLandInTheOutputRoot()
    {
        using var temp = new TestDirectory();
        var sample = CopySampleInto(temp.Combine("sample"), "chain-2");
        var outputRoot = temp.Combine("out");
        Directory.CreateDirectory(outputRoot);

        var result = await RunOneAsync(sample, new PierceRegistry(), outputRoot: outputRoot);

        Assert.True(Directory.Exists(Path.Combine(outputRoot, "outer")),
            "目录入口的产物没落进输出根");
        Assert.False(Directory.Exists(Path.Combine(sample, "outer")),
            "目录入口的产物落回了源目录");
        Assert.True(File.Exists(Path.Combine(outputRoot, "outer", "content.txt")));
        Assert.True(result.Ok);
    }

    /// <summary>
    /// 归档内带穿越名的条目必须零逃逸：产物目录之外一个字节都不许落。
    /// 反例同时钉住：正常条目确实解出来了 —— 否则「零逃逸」可以是「什么都没解」。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-009")]
    public async Task ArchivePathTraversal_MustNotEscapeTheProductDirectory()
    {
        var run = await PierceHarness.RunAsync("traversal");
        try
        {
            Assert.True(File.Exists(Path.Combine(run.Directory, "escape", "normal.txt")),
                "正常条目没解出来，零逃逸这条断言就没有意义");

            // 产物目录的上级也不许多出东西：父目录里只该有这一份样本。
            var parent = Path.GetDirectoryName(run.Directory)!;
            Assert.Single(Directory.EnumerateFileSystemEntries(parent));
            Assert.Equal(["escape.zip", "escape/", "escape/a/", "escape/a/b/", "escape/a/b/escape4.txt",
                    "escape/escape1.txt", "escape/escape2.txt", "escape/escape3.txt", "escape/normal.txt"],
                run.Tree(run.Directory));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 伪装包：垫了垫片、后面接一个完整的包。引擎读不了原文件，必须先切出来再解，原文件一个字节都不动。
    /// </summary>
    [Fact]
    public async Task DisguisedPackage_MustBeCarvedAndTheOriginalLeftUntouched()
    {
        var run = await PierceHarness.RunAsync("disguised");
        try
        {
            var original = Path.Combine(run.Directory, "video.mp4");
            Assert.True(File.Exists(original));
            Assert.Equal(1_228_971, new FileInfo(original).Length);
            Assert.Equal("carved payload\n", File.ReadAllText(Path.Combine(run.Directory, "video", "carrier.txt")));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 单子目录上提：包解出来自带一层同名目录，产物必须提到顶层。
    /// 拿平铺的包测等于没测 —— 夹具必须是真套娃形状。
    /// </summary>
    [Fact]
    public async Task SingleChildShell_MustBeLiftedOut()
    {
        var run = await PierceHarness.RunAsync("fold-single");
        try
        {
            Assert.Equal(
                ["same-name.zip", "same-name/", "same-name/content.txt"],
                run.Tree(run.Directory));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 取消在穿透层真的生效：当前工作目录里一个子目录都没有时也必须停住。
    /// 这里取消钩子只挂在「可能一次都不执行」的回调里，所以必须独立查一次。
    /// 取消属于安全停下，所以结果仍判「不是异常」。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-081"), Trait("Category", "ledger")]
    public async Task Cancel_MustStopEvenWithoutAnySubdirectory()
    {
        using var temp = new TestDirectory();
        CopySampleInto(temp.Combine("sample"), "chain-1");
        var entry = Path.Combine(temp.Path, "sample", "outer.zip");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) },
            cancellation.Token);
        var result = await piercer.RunAsync(entry);

        Assert.Empty(result.Layers);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.True(result.Ok, "取消属于安全停下，结果是「这不是异常」");
        Assert.False(File.Exists(Path.Combine(temp.Path, "sample", "outer", "content.txt")),
            "取消之后产物不许落盘");
    }

    /// <summary>
    /// 引擎报成功不等于真解出东西：判据是「这一层新增了条目」。覆盖模式下旧路径被重写、
    /// 条目名集合没变，所以那种情况退化成「目录里还有没有东西」。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-030")]
    public void HasPayload_MustLookAtNewEntriesAndFallBackForOverwrite()
    {
        using var temp = new TestDirectory();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var readOnly = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MinFreeGb = 0 });
        var overwrite = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MinFreeGb = 0, Conflict = ConflictPolicy.Overwrite });

        var before = new HashSet<string>(StringComparer.Ordinal) { "old.txt" };
        Assert.False(readOnly.HasPayload(temp.Path, before), "一个字节都没新增却算有产出");

        File.WriteAllText(temp.Combine("fresh.txt"), "payload");
        Assert.True(readOnly.HasPayload(temp.Path, before));

        // 覆盖模式：条目名集合与解压之前一样，改看目录里到底还有没有东西。
        var sameNames = new HashSet<string>(StringComparer.Ordinal) { "old.txt", "fresh.txt" };
        Assert.True(overwrite.HasPayload(temp.Path, sameNames));
    }

    /// <summary>
    /// 往下找没找完时不许报「没有可解压的压缩包」：
    /// 那种说法会让界面显示「完成」，而盘上确实还有包。
    ///
    /// 这里用"无法读取子文件夹"制造没找完 —— 另一条来源（两道保险丝）在  里钉。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-037"), Trait("Bug", "B-2026-044")]
    public async Task SearchTruncated_MustBeReportedAsIncompleteNotAsNoArchive()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("many");
        CopySampleInto(sample, "chain-1");
        var locked = Path.Combine(sample, "locked");
        Directory.CreateDirectory(locked);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"), Path.Combine(locked, "hidden.zip"));

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) });

        // 独占那个子目录：它在、但读不动，枚举它必抛 IOException。
        using var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, (FileOptions)0x02000000);
        var result = await piercer.RunAsync(sample);

        Assert.Equal(StopReason.SearchIncomplete, result.StopReason);
        Assert.True(result.Ok);
        Assert.True(result.Partial);
        Assert.Contains("子文件夹扫描未完成", result.Summary(), StringComparison.Ordinal);
        Assert.Contains(logs, line => line.Contains("无法读取子文件夹", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains(PierceText.StopNoArchive, StringComparison.Ordinal));
    }

    /// <summary>
    /// 往下找没找完但已经找到候选时，先把找到的解完，最后如实报没找完；
    /// 子目录里的包同样要解出来，本层与子目录一视同仁。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-040"), Trait("Bug", "B-2026-002")]
    public async Task NestedPackageBelowTheFirstLevel_MustStillBeSolved()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("nested");
        Directory.CreateDirectory(Path.Combine(sample, "sub"));
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(sample, "a.zip"));
        // 两份内容必须不同：相同的话第二份会被内容判重挡下，测的就不是「子目录有没有被漏解」了。
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "a.zip"),
            Path.Combine(sample, "sub", "b.zip"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0 });
        var result = await piercer.RunAsync(sample);

        Assert.True(File.Exists(Path.Combine(sample, "a", "content.txt")), "本层的包没解出来");
        Assert.True(File.Exists(Path.Combine(sample, "b", "a.txt")),
            "子目录里的包被静默漏解了");
        Assert.Contains(result.Layers, layer => Path.GetFileName(layer.Target) == "b.zip");
    }

    /// <summary>多解歧义时总数与未列出个数要同时出现在日志与细节里。</summary>
    [Fact]
    [Trait("Bug", "B-2026-085"), Trait("Category", "ledger")]
    public async Task Ambiguous_MustReportTheTotalCount()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("carriers");
        Directory.CreateDirectory(sample);
        var pad = new byte[Probe.EmbedMinSize + 4096];
        new Random(20261025).NextBytes(pad);
        for (var index = 0; index < 6; index++)
        {
            var body = new byte[64];
            new Random(index).NextBytes(body);
            // 追加一个最小的合法 zip：尾部目录定位才认得出来。
            var name = "v" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp4";
            File.WriteAllBytes(Path.Combine(sample, name), [.. pad, .. MinimalStoredZip(body)]);
        }

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)), ScanAppended = true });
        var result = await piercer.RunAsync(sample);

        Assert.Equal(StopReason.Ambiguous, result.StopReason);
        Assert.Equal(6, result.Leftover.Count);
        Assert.Contains(logs, line => line.Contains("共 6 个候选", StringComparison.Ordinal));
        Assert.Contains("另有 2 个未列出", result.StopDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「不处理的文件类型」在本层只剩它一个文件时同样生效：原封不动、日志说清跳过，
    /// 停止原因是没有可解压的包，不是失败。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-034"), Trait("Category", "ledger")]
    public async Task ExcludedExtensionAlone_MustNotBeSolved()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("excluded");
        Directory.CreateDirectory(sample);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(sample, "x.apk"));

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, ExcludeExts = ["apk"], Log = new ChannelLog((_, message) => logs.Add(message)) });
        var result = await piercer.RunAsync(sample);

        Assert.Equal(StopReason.NoArchive, result.StopReason);
        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(sample, "x.apk")), "排除名单里的文件被动了");
        Assert.Contains(logs, line => line.Contains("已设置为不处理", StringComparison.Ordinal));
    }

    /// <summary>
    /// 一条坏包的链：必须精确停在坏的那一层、明确失败，而且不许用「有内容没解」把它盖过去。
    /// 坏的那一层是在第一层解完之后才被弄坏的，所以这是确定性的、不是时序抖动。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-066")]
    public async Task CorruptedInnerLayer_MustFailExactlyAtThatLayer()
    {
        using var temp = new TestDirectory();
        var sample = CopySampleInto(temp.Combine("sample"), "chain-2");
        var entry = Path.Combine(sample, "outer.zip");
        var broken = File.ReadAllBytes(Path.Combine(TestPaths.PierceRoot, "same-level", "z_broken.zip"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions
            {
                MaxDepth = 5,
                MinFreeGb = 0,
                // 第一层解完的那一刻把**第二层那个真包**弄坏：它是外层包里唯一的内层包，
                // 所以这一趟只能停在第二层。注入一个重名文件等于多解一层，测的就不是这件事了。
                OnLayer = (depth, _target, done) =>
                {
                    if (done && depth.Value == 1)
                    {
                        File.WriteAllBytes(Path.Combine(sample, "outer", "l2in.zip"), broken);
                    }
                },
            });
        var result = await piercer.RunAsync(entry);

        Assert.Equal(StopReason.ExtractFailed, result.StopReason);
        Assert.False(result.Ok);
        Assert.False(result.Partial);
        Assert.Equal(2, result.MaxDepthReached);
        Assert.False(File.Exists(Path.Combine(sample, "outer", "l2in", "content.txt")));
    }

    /// <summary>坏包之后同一批里的好包照样跑完：一个包出事不许带走整批。</summary>
    [Fact]
    [Trait("Bug", "B-2026-012"), Trait("Category", "ledger")]
    public async Task OneBrokenPackage_MustNotStopTheRestOfTheBatch()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("batch");
        Directory.CreateDirectory(sample);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "z_broken.zip"),
            Path.Combine(sample, "a_broken.zip"));
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(sample, "z_good.zip"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0 });
        var result = await piercer.RunAsync(sample);

        Assert.True(File.Exists(Path.Combine(sample, "z_good", "content.txt")),
            "坏包把同一批里的好包带走了");
        Assert.Contains(result.Layers, layer => layer.Ok && Path.GetFileName(layer.Target) == "z_good.zip");
        Assert.False(result.Ok);
    }

    /// <summary>结果总结里层号取的是最深包层，不是解压动作次数。</summary>
    [Fact]
    [Trait("Bug", "B-2026-016"), Trait("Category", "ledger")]
    public async Task Summary_MustUseTheDeepestPackLayer()
    {
        var run = await PierceHarness.RunAsync("same-level");
        try
        {
            // 五个候选里有一份被判重跳过、一份是坏包，解压动作次数大于层数。
            Assert.Equal(1, run.Result.DeepestLayer.Value);
            Assert.True(run.Result.MaxDepthReached > run.Result.DeepestLayer.Value,
                "这个夹具要在解压动作次数与最深包层之间拉开差距");
            Assert.Contains("1 层", run.Result.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>层号事件与层清单必须配对：运行中报过的层号，结束后不许退回。</summary>
    [Fact]
    [Trait("Bug", "B-2026-036"), Trait("Category", "ledger")]
    public async Task LayerEvents_MustMatchTheRecordedLayers()
    {
        var run = await PierceHarness.RunAsync("chain-3");
        try
        {
            Assert.NotEmpty(run.Layers);
            Assert.Equal(run.Result.DeepestLayer.Value, run.Layers.Max(entry => entry.Depth.Value));
            Assert.Equal(run.Result.Layers.Count, run.Layers.Select(entry => entry.Target).Distinct().Count());
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>
    /// 解压中途源包被删时，备注要给真原因（引擎的原话或「源文件已不存在」），
    /// 而不是只有一句退出码。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-031"), Trait("Category", "ledger")]
    public async Task SourceGoneMidway_MustReportTheRealReason()
    {
        using var temp = new TestDirectory();
        var sample = CopySampleInto(temp.Combine("sample"), "chain-1");
        var entry = Path.Combine(sample, "outer.zip");

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0 });
        File.Delete(entry);
        var result = await piercer.RunAsync(entry);

        Assert.False(result.Ok);
        Assert.Contains("源文件已不存在", result.StopDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("退出码", result.StopDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 保留设备名不进入任何读路径。读设备会永久阻塞，取消与超时都没有机会执行，
    /// 而指纹这一条路在解压之前就走 —— 它必须先挡下来，而且不许碰盘。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-001")]
    public void DeviceName_MustNotBeReadByTheFingerprintPath()
    {
        var fingerprint = ContentIdentity.Of("CON");
        Assert.Equal(-1, fingerprint.Size);

        Assert.False(Probe.IsDevicePath(Path.Combine(AppContext.BaseDirectory, "Bbu.Core.dll")));
    }

    /// <summary>归档里带保留设备名的条目：7z 改名后数据必须完好，解压本身不算失败。</summary>
    [Fact]
    [Trait("Bug", "B-2026-021"), Trait("Category", "ledger")]
    public async Task ReservedDeviceEntryInsideTheArchive_MustKeepItsData()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("devinside");
        Directory.CreateDirectory(sample);
        var zip = Path.Combine(sample, "dev.zip");
        WriteStoredZip(zip, [("CON.txt", "console payload\n"), ("NUL.txt", "nul payload\n"), ("ok.txt", "ok\n")]);

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0 });
        var result = await piercer.RunAsync(zip);

        Assert.Equal(StopReason.NoArchive, result.StopReason);
        var product = Path.Combine(sample, "dev");
        Assert.True(Directory.Exists(product));
        var names = Directory.EnumerateFiles(product).Select(Path.GetFileName).ToList();
        Assert.Contains("ok.txt", names);
        Assert.Equal("ok\n", File.ReadAllText(Path.Combine(product, "ok.txt")));
        Assert.True(names.Count >= 3, "带设备名的条目被整条丢掉了：" + string.Join(",", names));
    }

    /// <summary>
    /// 同一内容的两份包各解一份，谁都不许被记成「有内容没解」；
    /// 对照：正常解完的包也不许被判成有内容没解（B-2026-007）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-007"), Trait("Category", "ledger")]
    public async Task DuplicateSecondCopy_MustAlsoBeExtractedAndTheNormalCaseMustNotBeUnfinished()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("dup");
        Directory.CreateDirectory(sample);
        // 同名会让产物目录撞上，所以用两个不同的外名、相同的内容。
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "same1.zip"),
            Path.Combine(sample, "dup1.zip"));
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "same1.zip"),
            Path.Combine(sample, "dup2.zip"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0 });
        var duplicated = await piercer.RunAsync(sample);

        Assert.True(duplicated.Ok);
        Assert.False(duplicated.Partial, "第二份也是解出来的，不许报「有内容没解」");
        Assert.DoesNotContain(duplicated.Leftover, name => name.Contains("dup2.zip", StringComparison.Ordinal));
        Assert.Equal(2, CountPayloads(sample));

        var normal = await PierceHarness.RunAsync("chain-1");
        try
        {
            Assert.True(normal.Result.Ok);
            Assert.False(normal.Result.Partial);
        }
        finally
        {
            normal.Cleanup();
        }
    }

    /// <summary>
    /// 跨目录 ＋ 嵌套：同一输出根下先解过一份内容相同的独立包之后，后面那个包第 1 层切出来的内层包
    /// 与那一份逐字节相同 —— 两条链互不相干，**各解一份**：载体包照解到第 2 层，产物落在它自己的目录里。
    ///
    /// 判重的参照集是链上的祖先，不是这一批见过的所有包（B-2026-311）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-311"), Trait("Category", "ledger")]
    public async Task CrossDirectoryNestedDuplicate_MustAlsoBeExtracted()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("cross-dir");
        Directory.CreateDirectory(sample);
        var outputRoot = temp.Combine("out");
        Directory.CreateDirectory(outputRoot);

        // X.zip 与 carrier.zip：carrier 第 1 层只有一份条目，内容 = X.zip 的逐字节副本。
        // 本夹具是**故意撞车**的那一类（测的就是判重那条路）。
        var standalone = Path.Combine(sample, "X.zip");
        WriteStoredZip(standalone, [("payload.txt", "cross-dir nested duplicate, W9 B-2026-311\n")]);
        var carrier = Path.Combine(sample, "carrier.zip");
        WriteStoredZipRaw(carrier, [("X.zip", File.ReadAllBytes(standalone))]);

        // 一项一本账，与 Runner 给每一项各建一份是同一形态。
        var first = await RunOneAsync(standalone, new PierceRegistry(), outputRoot);
        Assert.True(first.Ok);
        Assert.True(
            File.Exists(Path.Combine(first.OutputDir, "payload.txt")), "先解的那一份必须真的解出来");

        var second = await RunOneAsync(carrier, new PierceRegistry(), outputRoot);

        Assert.True(second.Ok);
        Assert.False(second.Partial, "跨链的同内容各解一份，不许报「有内容没解」");
        Assert.Empty(second.Leftover);
        Assert.Equal(JobState.Done, Runner.MapState(second));
        Assert.Equal(StopReason.NoArchive, second.StopReason);

        // 载体这一项自己也要有产物 —— 现场（漏产物）的判据：载体项自己的产物目录里必须有解出来的内容。
        Assert.True(Directory.Exists(second.OutputDir));
        Assert.True(second.Layers.Count >= 2, "载体包必须解到第 2 层");
        Assert.Empty(Directory.EnumerateFiles(second.OutputDir, "*.zip", SearchOption.AllDirectories));

        // 输出根下恰好两份内容：先解的那一份，与载体解出来的那一份。
        Assert.Equal(2, Directory.EnumerateFiles(outputRoot, "payload.txt", SearchOption.AllDirectories).Count());
    }

    /// <summary>
    /// 同一夹具在「同批」与「单独」两种上下文下，**载体那一项的结论与产物**必须一致：
    /// 状态、停止原因都一样，载体都解到第 2 层、产物目录里都不留包。
    ///
    /// 同批那趟的输出根下多出一份 payload，那是独立包自己的产物 —— 清单里还有谁不许影响这一项解出什么。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-311"), Trait("Category", "ledger")]
    public async Task CrossDirectoryNestedDuplicate_MustReadTheSameAsTheSoloRun()
    {
        var batched = await RunCarrierCrossDirAsync(withStandalone: true);
        var solo = await RunCarrierCrossDirAsync(withStandalone: false);

        Assert.Equal(solo.Ok, batched.Ok);
        Assert.Equal(solo.Partial, batched.Partial);
        Assert.Equal(solo.State, batched.State);
        Assert.Equal(solo.Reason, batched.Reason);
        Assert.False(batched.Partial, "同批那一趟不许报「有内容没解」");
        Assert.Equal(JobState.Done, batched.State);
        Assert.Equal(JobState.Done, solo.State);
        Assert.Equal(2, batched.Payloads);
        Assert.Equal(1, solo.Payloads);
        Assert.False(batched.CarrierHasPackages, "同批那一趟的产物目录里不许留下包");
        Assert.False(solo.CarrierHasPackages, "单独那一趟的产物目录里也不许留下包");
    }

    /// <summary>
    /// 链内绕回自己那一档的两个分支：撞上的那一份在链上是祖先时，
    /// **它确实解出过** ⇒ 收掉这一份、不记残留、文案给出祖先产物目录的全路径（`Done`）；
    /// **它只是登记过、没解成功** ⇒ 仍然跳过、记残留、报「有内容没解」。
    ///
    /// 真实链里构造不出（一个包装不下逐字节相同的自己），所以靠**注入一本已经登记、可以顺带记过落点的账**
    /// 把这条路走一遍 —— 这两条文案手改极易写反（现场发生过一次），这一格钉的就是它们。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-311"), Trait("Category", "ledger")]
    public async Task InChainDuplicate_MustSplitOnWhetherThatCopyWasReallySolved()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("in-chain");
        Directory.CreateDirectory(sample);
        var outputRoot = temp.Combine("out");
        Directory.CreateDirectory(outputRoot);
        var solvedDir = temp.Combine("solved");
        Directory.CreateDirectory(solvedDir);

        var standalone = Path.Combine(sample, "X.zip");
        WriteStoredZip(standalone, [("payload.txt", "in-chain duplicate, 16-B02\n")]);
        var carrier = Path.Combine(sample, "carrier.zip");
        WriteStoredZipRaw(carrier, [("X.zip", File.ReadAllBytes(standalone))]);

        // 第一路：账上「这一份的内容已经解出来过」，落点是真实存在的产物目录。
        var solved = new PierceRegistry();
        solved.Register(ContentIdentity.Of(standalone), standalone);
        solved.NoteSolved(ContentIdentity.Of(standalone), standalone, solvedDir);

        var collected = await RunOneAsync(carrier, solved, outputRoot);

        Assert.True(collected.Ok);
        Assert.False(collected.Partial, "内容确已解出过那一档不记残留");
        Assert.Equal(JobState.Done, Runner.MapState(collected));
        Assert.Empty(collected.Leftover);
        Assert.Empty(Directory.EnumerateFiles(collected.OutputDir, "*.zip", SearchOption.AllDirectories));
        Assert.Contains(solvedDir, collected.StopDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("内容已在", collected.StopDetail, StringComparison.Ordinal);

        // 第二路：只是登记过、没记过落点（那一层解压不完整）⇒ 仍然跳过并如实报「有内容没解」。
        var registeredOnly = new PierceRegistry();
        registeredOnly.Register(ContentIdentity.Of(standalone), standalone);

        var skipped = await RunOneAsync(carrier, registeredOnly, outputRoot);

        Assert.True(skipped.Partial, "没解成功那一档要报「有内容没解」");
        Assert.Contains(skipped.Leftover, name => name.Contains("X.zip", StringComparison.Ordinal));
        Assert.Contains("这一份尚未解压", skipped.StopDetail, StringComparison.Ordinal);
    }

    /// <summary>上面那一格要带出来的那几个事实。临时目录在建它的那个方法里销毁，所以只把结论带出来。</summary>
    private sealed record CrossDirOutcome(
        bool Ok, bool Partial, JobState State, StopReason Reason, int Payloads, bool CarrierHasPackages);

    /// <summary>「跨目录 ＋ 嵌套」的现场各跑一次：带独立包那趟先解它；两趟各自一本账（与 Runner 同形态）。</summary>
    private static async Task<CrossDirOutcome> RunCarrierCrossDirAsync(bool withStandalone)
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("cross-dir");
        Directory.CreateDirectory(sample);
        var outputRoot = temp.Combine("out");
        Directory.CreateDirectory(outputRoot);

        var standalone = Path.Combine(sample, "X.zip");
        WriteStoredZip(standalone, [("payload.txt", "cross-dir nested duplicate, W9 B-2026-311\n")]);
        var carrier = Path.Combine(sample, "carrier.zip");
        WriteStoredZipRaw(carrier, [("X.zip", File.ReadAllBytes(standalone))]);

        if (withStandalone)
        {
            var first = await RunOneAsync(standalone, new PierceRegistry(), outputRoot);
            Assert.True(first.Ok);
        }

        var second = await RunOneAsync(carrier, new PierceRegistry(), outputRoot);
        var carrierDir = second.OutputDir;
        return new CrossDirOutcome(
            second.Ok,
            second.Partial,
            Runner.MapState(second),
            second.StopReason,
            Directory.EnumerateFiles(outputRoot, "payload.txt", SearchOption.AllDirectories).Count(),
            Directory.Exists(carrierDir)
                && Directory.EnumerateFiles(carrierDir, "*.zip", SearchOption.AllDirectories).Any());
    }

    /// <summary>
    /// 「假头 + 绝对偏移」的包：目录入口必须把它选成候选并真的解出来。
    /// 断言的形式是「选没选中」，不是「耗时」—— 计时不稳，而且断不到这件事。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-095"), Trait("Category", "ledger")]
    public async Task AbsoluteLayoutCarrier_MustBeSelectedByTheDirectoryEntry()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("abs");
        Directory.CreateDirectory(sample);
        var carrier = Path.Combine(TestPaths.Fixture("probe"), "carrier-abs", "x.mp4");
        File.Copy(carrier, Path.Combine(sample, "x.mp4"));

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)), ScanAppended = true });
        var result = await piercer.RunAsync(sample);

        Assert.NotEmpty(result.Layers);
        Assert.Equal("x.mp4", Path.GetFileName(result.Layers[0].Target));
        Assert.True(File.Exists(Path.Combine(sample, "x", "payload.bin")),
            "目录入口没选中这个包，也就没解出东西");
    }

    /// <summary>
    /// 包藏在七层子目录里也必须解到底：往下找不按目录深度设限，改按目录数设保险丝。
    /// 同时钉住「同层的第二个包不消耗层数预算」—— 两个包都是第 1 层，
    /// 解压动作次数是二，最深包层是一。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-033"), Trait("Category", "ledger")]
    public async Task DeepSubdirectory_MustNotBeLimitedByTheLayerBudget()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("deep-dir");
        var deep = sample;
        for (var index = 1; index <= 7; index++)
        {
            deep = Path.Combine(deep, "d" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(deep);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(sample, "top.zip"));
        File.Copy(Path.Combine(TestPaths.PierceRoot, "same-level", "a.zip"),
            Path.Combine(deep, "inner.zip"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 2, MinFreeGb = 0 });
        var result = await piercer.RunAsync(sample);

        Assert.True(File.Exists(Path.Combine(sample, "top", "content.txt")), "本层的包没解出来");
        Assert.True(File.Exists(Path.Combine(sample, "inner", "a.txt")),
            "七层子目录里的包被漏解了");
        Assert.Equal(2, result.MaxDepthReached);
        Assert.Equal(1, result.DeepestLayer.Value);
        Assert.Equal(StopReason.NoArchive, result.StopReason);
    }

    /// <summary>
    /// 本层既有正常包、又有伪装包时两个都要解，顺序是正常包在前。
    /// 收集伪装包那一段排在提前返回之后就会被永远跳过 —— 表现是伪装包静默漏解还报完成。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-075")]
    public async Task CarrierAlongsideANormalPackage_MustBothBeSolved()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("mixed");
        Directory.CreateDirectory(sample);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(sample, "normal.zip"));

        var pad = new byte[Probe.EmbedMinSize + 4096];
        new Random(20261026).NextBytes(pad);
        File.WriteAllBytes(
            Path.Combine(sample, "video.mp4"),
            [.. pad, .. MinimalStoredZip(System.Text.Encoding.UTF8.GetBytes("carved payload\n"))]);

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MaxDepth = 5, MinFreeGb = 0 });
        var result = await piercer.RunAsync(sample);

        Assert.True(File.Exists(Path.Combine(sample, "normal", "content.txt")), "正常包没解出来");
        Assert.True(File.Exists(Path.Combine(sample, "video", "secret.txt")),
            "同层有正常包时伪装包被静默漏解了");
        Assert.Contains(result.Layers, layer => Path.GetFileName(layer.Target) == "normal.zip");
        Assert.Contains(result.Layers, layer => Path.GetFileName(layer.Target) == "video.mp4");
        Assert.True(result.Ok);
        Assert.False(result.Partial);
    }

    /// <summary>
    /// 「未能解压」那一行只说一遍、括号配对、同一信息不重复。
    /// 那一行是直接进用户日志面板的，重复或括号不配对会被用户当成工具坏了。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-063"), Trait("Category", "ledger")]
    public async Task NotUnlockedLine_MustAppearOnceWithBalancedParens()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("locked");
        Directory.CreateDirectory(sample);
        var entry = Path.Combine(sample, "encrypted.zip");
        File.Copy(Path.Combine(TestPaths.Fixtures, "vault", "zip-aes256.zip"), entry);

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) });
        var result = await piercer.RunAsync(entry);

        Assert.Equal(StopReason.PasswordExhausted, result.StopReason);
        var lines = logs.Where(line => line.Contains("未能解压", StringComparison.Ordinal)).ToList();
        Assert.Single(lines);

        var line = lines[0];
        Assert.Equal(1, line.Count(ch => ch == '（'));
        Assert.Equal(1, line.Count(ch => ch == '）'));
        Assert.Equal(1, Occurrences(line, "文件仍在"));
        Assert.DoesNotContain("文件还在", line, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// 目录入口下收壳不许把归档内容提到用户自己的文件夹里。
    ///
    /// 目录入口时产物目录的父目录正好就是用户拖进来的那个目录，收壳那处守卫
    /// 「父目录 == workdir」天然成立，于是归档条目会被搬到用户目录里，静默覆盖同名文件、
    /// 把同名的**文件**删掉换成目录（外部审计第 1 轮 F-01）。
    /// 三种冲突策略 × 删原包开／关，六格。
    /// </summary>
    [Theory, Trait("Bug", "B-2026-184")]
    [InlineData(ConflictPolicy.Rename, false)]
    [InlineData(ConflictPolicy.Rename, true)]
    [InlineData(ConflictPolicy.Overwrite, false)]
    [InlineData(ConflictPolicy.Overwrite, true)]
    [InlineData(ConflictPolicy.Skip, false)]
    [InlineData(ConflictPolicy.Skip, true)]
    public async Task DirectoryEntry_MustNotLiftArchiveContentIntoTheUserFolder(
        ConflictPolicy conflict, bool removeSource)
    {
        using var temp = new TestDirectory();
        var userdir = temp.Combine("userdir");
        Directory.CreateDirectory(userdir);
        File.WriteAllText(Path.Combine(userdir, "发票.pdf"), "USER ORIGINAL PRECIOUS DATA");
        File.WriteAllText(Path.Combine(userdir, "2024"), "USER 2024 FILE");

        using (var zip = ZipFile.Open(Path.Combine(userdir, "发票.zip"), ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("发票.pdf").Open()))
            {
                writer.Write("FROM ARCHIVE");
            }

            using (var writer = new StreamWriter(zip.CreateEntry("2024/预算.txt").Open()))
            {
                writer.Write("archive budget");
            }
        }

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions
            {
                MaxDepth = 5,
                MinFreeGb = 0,
                Conflict = conflict,
                RemoveSource = removeSource,
            });

        await piercer.RunAsync(userdir);

        // 用户既有文件的内容一个字节都不许变，也不许被替换成目录
        Assert.Equal("USER ORIGINAL PRECIOUS DATA", File.ReadAllText(Path.Combine(userdir, "发票.pdf")));
        Assert.True(File.Exists(Path.Combine(userdir, "2024")), "用户的 2024 文件被目录顶掉了");
        Assert.Equal("USER 2024 FILE", File.ReadAllText(Path.Combine(userdir, "2024")));
    }

    /// <summary>
    /// 非覆盖档（改名、跳过）要真的传到引擎：产物目录是新的，而包内可以自带两条同名条目，
    /// 引擎写第二条时第一条已经在盘上了 —— 这是「已存在的同名文件」在穿透入口唯一可达的一档。
    /// Skip 档下产物目录一旦存在，命名层先返回跳过、引擎不会被调用，所以那一档构造不出这个现场。
    ///
    /// 判据：重名时非覆盖模式一律不覆盖已存在的东西，B-2026-219）。
    /// 反向对照：把 `PierceExtract` 里 `Overwrite` 那一行撤掉（吃默认的 true，出 `-aoa`）时
    /// `dup.txt` 落成 SECOND。
    /// </summary>
    [Theory, Trait("Bug", "B-2026-219")]
    [InlineData(ConflictPolicy.Rename)]
    [InlineData(ConflictPolicy.Skip)]
    public async Task NonOverwriteConflict_MustReachTheEngineSoTheFirstSameNameEntrySurvives(
        ConflictPolicy conflict)
    {
        using var temp = new TestDirectory();
        var archive = temp.Combine("dup.zip");
        WriteStoredZip(archive, [("dup.txt", "FIRST"), ("dup.txt", "SECOND"), ("only.txt", "ONLY")]);

        var result = await RunOneAsync(archive, new PierceRegistry(), conflict: conflict);

        var product = temp.Combine("dup");

        // 先钉住这一层真解开了：只判「同名的那份没被换掉」时，「一个文件都没解」也会让断言绿。
        Assert.Equal("ONLY", File.ReadAllText(Path.Combine(product, "only.txt")));
        Assert.Equal("FIRST", File.ReadAllText(Path.Combine(product, "dup.txt")));
        Assert.True(result.Ok, "这一层没解开，上面的断言量的就不是覆盖行为");
    }

    /// <summary>
    /// 覆盖档要真的覆盖：用户选了这一档之后，产物目录里既有的同名文件由包内的替换掉，
    /// 目录里其它文件留着（界面那句确认正文就是按这个写的）。
    /// 它是上一条的反向对照：把那一行写死成 false 时这一条变红。
    /// 这个前置条件（产物目录已存在）也只有覆盖档成立，改名档会另起一个名字。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-219")]
    public async Task OverwriteConflict_MustReplaceTheSameNameFileInPlace()
    {
        using var temp = new TestDirectory();
        var archive = temp.Combine("dup.zip");
        WriteStoredZip(archive, [("dup.txt", "FROM ARCHIVE"), ("only.txt", "ONLY")]);
        var product = temp.Combine("dup");
        Directory.CreateDirectory(product);
        File.WriteAllText(Path.Combine(product, "dup.txt"), "USER SENTINEL");
        File.WriteAllText(Path.Combine(product, "keep.txt"), "USER KEPT");

        var result = await RunOneAsync(archive, new PierceRegistry(), conflict: ConflictPolicy.Overwrite);

        Assert.Equal("FROM ARCHIVE", File.ReadAllText(Path.Combine(product, "dup.txt")));
        Assert.Equal("USER KEPT", File.ReadAllText(Path.Combine(product, "keep.txt")));
        Assert.Equal("ONLY", File.ReadAllText(Path.Combine(product, "only.txt")));
        Assert.True(result.Ok, "这一层没解开，上面的断言量的就不是覆盖行为");
    }

    /// <summary>
    /// 删的是用户自己拖进来的那个原包，日志就说「原压缩包」；删更深层的嵌套包才说「中间包」。
    /// 两条路共用一句时，用户按日志回溯会找错对象 —— 而那也正是这类永久删除在界面上留下的唯一痕迹。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-227")]
    public async Task RemovingPackages_MustTellTheSourceApartFromTheIntermediates()
    {
        using var temp = new TestDirectory();
        var dir = temp.Combine("userdir");
        Directory.CreateDirectory(dir);

        byte[] innerBytes;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("b.txt").Open());
                writer.Write("inner payload");
            }

            innerBytes = buffer.ToArray();
        }

        var source = Path.Combine(dir, "学习资料.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("a.txt").Open()))
            {
                writer.Write("outer payload");
            }

            using var entry = zip.CreateEntry("内层.zip").Open();
            entry.Write(innerBytes, 0, innerBytes.Length);
        }

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault,
            new PierceOptions
            {
                MaxDepth = 5,
                MinFreeGb = 0,
                RemoveSource = true,
                RemoveIntermediate = true,
                Log = new ChannelLog((_, message) => logs.Add(message)),
            });

        var result = await piercer.RunAsync(source);

        // 先把现场钉住，否则下面那两条断言量的是一个空集。
        Assert.False(File.Exists(source), "原包没被删掉，这条断言就量不到要量的事");
        Assert.Equal(2, result.Layers.Count);

        Assert.Contains(logs, line => line == PierceText.Fill(PierceText.SourceRemovedAt, "学习资料.zip"));
        Assert.Contains(logs, line => line == PierceText.Fill(PierceText.IntermediateRemovedAt, "内层.zip"));
    }

    /// <summary>
    /// 点停止时正在解压的那一项：取消那一层要记进结果，而最终停止原因仍是取消 ——
    /// 收敛不许把它改写成「解压失败」，否则状态列写「失败」、汇总里的「已停止」也少算一格（B-2026-312）。
    ///
    /// 三段断言：① 取消那一层真的进了 `Layers`（**前提**，不成立就是这次没量到，不是判据不成立）；
    /// ② 最终停止原因是取消；③ 清单行那三处后果见下面那一条（同夹具的另一半）。
    /// 取消由解压路径自己发起（引擎报出一帧解压进度那一刻）：那时识别已经做完、这一层正在解，
    /// 而子进程必然还在跑 ⇒ 取消落在写盘途中。这一步依赖 `SlowEntry` 的包慢到至少报出一帧进度：
    /// 报不出来时这几条不会假绿，而是最长挂到引擎默认超时（1800 秒）才红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-313")]
    public async Task CancelledLayer_MustKeepCancelledInsteadOfAnExtractFailure()
    {
        using var temp = new TestDirectory();
        var (entry, root) = SlowEntry(temp, "slow1");

        using var cts = new CancellationTokenSource();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        // 取消由解压路径自己发起：引擎报「解压」进度时识别已经做完、这一层正在解，而那一刻子进程必然还在跑
        // （它正在往外吐进度）⇒ 取消落在写盘途中，不落在识别阶段。换成测试线程定时取消会有两个不确定窗口：
        // 太早识别还没完、太晚整包已解完。
        var log = new ChannelLog((_, _) => { }, progress =>
        {
            if (progress.Stage == ProgressStage.Extract)
            {
                cts.Cancel();
            }
        });
        var piercer = new Piercer(
            TestPaths.NewEngine(log), vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, OutputRoot = root }, cts.Token);

        var result = await piercer.RunAsync(entry);

        // ① 前提：取消那一层真的记进了结果。不成立 ⇒ 这次没量到 D2，该动的是实测而不是产品代码。
        var cancelledLayers = result.Layers.Where(layer => !layer.Ok && layer.Reason == StopReason.Cancelled).ToArray();
        Assert.True(
            cancelledLayers.Length > 0,
            $"先决条件不成立：{result.Layers.Count} 层里没有一层记着「取消」，这次没量到 D2");

        // ② 被测结论：最终停止原因仍是取消。
        Assert.Equal(StopReason.Cancelled, result.StopReason);
    }

    /// <summary>
    /// 取消那一项在清单上读作「已停止」：不是失败、起因是用户停止、备注留空（B-2026-312）。
    /// 与上一条同一段链的另一半：上一条钉收敛那一格，这一条钉用户在清单上看到的三处。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-313")]
    public async Task CancelledItem_MustReadAsStoppedInsteadOfFailed()
    {
        using var temp = new TestDirectory();
        var (entry, root) = SlowEntry(temp, "slow2");

        var items = new List<Job> { new() { ArchivePath = entry, Kind = "ZIP", DisplayName = "slow2" } };
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        using var cts = new CancellationTokenSource();
        // 与「取消那一层记进结果」那条同一个信号源：只认解压路径报出来的进度（识别阶段的日志、盘上出现的
        // 中间文件在识别时就有了，用它们当同步点会把「太晚」那个窗口换成「太早」）。
        var log = new ChannelLog((_, _) => { }, progress =>
        {
            if (progress.Stage == ProgressStage.Extract)
            {
                cts.Cancel();
            }
        });
        var runner = new Runner(
            items, TestPaths.NewEngine(log), vault,
            new AppConfig { OutputMode = OutputMode.Custom, OutputDirectory = root },
            cancellationToken: cts.Token);

        await runner.RunAsync();

        Assert.Equal(StopReason.Cancelled, items[0].StopReason!.Value);
        Assert.Equal(JobState.Skipped, items[0].State);
        Assert.True(items[0].StoppedByUser, "取消那一项没有被记成「用户停止」");
        Assert.Equal(Errors.Stopped, Runner.StatusLabel(items[0]));
    }

    /// <summary>
    /// 撞层数上限之后把上限调大，这一项就该被下一趟选中（B-2026-313）。
    /// 这是「按原因放行重跑」的端到端那一半：真跑一趟落 `Partial` ＋ `MaxDepth`，
    /// 再按 `Actions.Batch` 用的那条判据（可运行 ＋ `NeedsRun`）判它会不会进这一趟。
    /// `Actions` 在界面程序集里、本工程看不到它，所以这里判的是它用的那条判据本身。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-314")]
    public async Task MaxDepthPartial_MustBePickedUpByTheNextRun()
    {
        using var temp = new TestDirectory();
        var entry = PierceHarness.CopySample("deep-50");
        try
        {
            var root = temp.Combine("out");
            Directory.CreateDirectory(root);
            var items = new List<Job> { new() { ArchivePath = entry, Kind = "ZIP", DisplayName = "deep-50" } };
            var vault = new Vault();
            vault.SetEntries(Array.Empty<string>());
            var runner = new Runner(
                items, TestPaths.NewEngine(), vault,
                new AppConfig { MaxDepth = 3, OutputMode = OutputMode.Custom, OutputDirectory = root });

            await runner.RunAsync();

            Assert.Equal(JobState.Partial, items[0].State);
            Assert.Equal(StopReason.MaxDepth, items[0].StopReason!.Value);
            Assert.True(
                items[0].Runnable && Runner.NeedsRun(items[0]),
                "撞了层数上限：把上限调大之后这一项该被下一趟选中，否则点开始无法修好它");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(entry)!, recursive: true);
        }
    }

    /// <summary>造一个慢包并放进它自己的入口目录：大象目让解压慢到能在写盘途中取消。</summary>
    private static (string Entry, string Root) SlowEntry(TestDirectory temp, string name)
    {
        var entry = temp.Combine(name);
        Directory.CreateDirectory(entry);
        using (var zip = ZipFile.Open(Path.Combine(entry, name + ".zip"), ZipArchiveMode.Create))
        {
            for (var i = 0; i < 10000; i++)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"d{i % 20}/f{i}.txt").Open());
                writer.Write(new string('x', 200));
            }
        }

        var root = temp.Combine(name + "-out");
        Directory.CreateDirectory(root);
        return (entry, root);
    }

    /// <summary>
    /// 取消之后结果里要能指出半成品在哪。
    ///
    /// 取消走的是「这一层没成功」的提前返回，而 `OutputDir` 原先只在该层成功之后才赋值，
    /// 于是结果里是空串：用户在大包解压到一半点「停止」，盘上留下成千个已写出的文件，
    /// 界面上却拿不到任何定位（外部审计第 1 轮 D-6）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-185"), Trait("Category", "ledger")]
    public async Task CancelledRun_MustStillReportWhereThePartialOutputIs()
    {
        using var temp = new TestDirectory();
        var archive = temp.Combine("slow.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            for (var i = 0; i < 10000; i++)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"d{i % 20}/f{i}.txt").Open());
                writer.Write(new string('x', 200));
            }
        }

        var root = temp.Combine("out");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        // 与「取消那一层记进结果」那条同一个信号源：只认解压路径报出来的进度，那时识别已经做完、
        // 盘上也有写出来的东西了（报出的产物目录因此非空）。
        var log = new ChannelLog((_, _) => { }, progress =>
        {
            if (progress.Stage == ProgressStage.Extract)
            {
                cts.Cancel();
            }
        });
        var piercer = new Piercer(
            TestPaths.NewEngine(log), vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, OutputRoot = root }, cts.Token);

        var result = await piercer.RunAsync(archive);

        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.NotEmpty(result.OutputDir);
        Assert.True(Directory.Exists(result.OutputDir), $"报出的产物目录不存在：{result.OutputDir}");
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(result.OutputDir));
    }

    /// <summary>
    /// 失败与取消之后盘上不留空产物目录，而写出过东西的目录照旧留着（`D-4`）。
    ///
    /// 产物目录是引擎在解压之前创建的，超长条目名让 7z 以退出码 2 收场时它已经建出来却一个文件都没有；
    /// 收尾判据是「这个目录里有没有本次写出来的东西」，不是「目录在不在」。
    /// 本次之前就存在的目录、以及写过东西的目录都不许动，写过东西的那些正是取消时要找的半成品（`D-6` 报的就是它）。
    /// 两个方向各一条断言：只判「空目录被删」会漏掉「删过头」这种更坏的错。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-187"), Trait("Category", "ledger")]
    public async Task FailedRun_MustDropTheVacantProductDirectoryAndKeepWhatWasWritten()
    {
        using var temp = new TestDirectory();

        // ① 整包只有一个超长条目名：7z 退出码 2、一个文件都没写出来 ⇒ 产物目录不许留在盘上。
        var vacant = temp.Combine("vacant");
        var broken = Path.Combine(vacant, "broken.zip");
        Directory.CreateDirectory(vacant);
        using (var zip = ZipFile.Open(broken, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(new string('a', 300) + ".txt").Open());
            writer.Write("x");
        }

        var failed = await RunOneAsync(broken, new PierceRegistry());
        Assert.False(failed.Ok);
        Assert.False(
            Directory.Exists(Path.Combine(vacant, "broken")),
            "失败之后那个空产物目录还留在盘上");

        // ② 一个正常条目接一个超长条目：7z 写出第一个之后才失败 ⇒ 有内容的目录照旧留着。
        var written = temp.Combine("written");
        var partial = Path.Combine(written, "partial.zip");
        Directory.CreateDirectory(written);
        using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("first.txt").Open()))
            {
                writer.Write("first");
            }

            using (var writer = new StreamWriter(zip.CreateEntry(new string('b', 300) + ".txt").Open()))
            {
                writer.Write("x");
            }
        }

        var kept = await RunOneAsync(partial, new PierceRegistry());
        Assert.False(kept.Ok);
        var product = Path.Combine(written, "partial");
        Assert.True(Directory.Exists(product), "写出过文件的产物目录被收掉了");
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(product));

        // ③ 本次之前就存在的目录一个都不许动：覆盖模式下产物目录本来就指着一个已存在的目录，
        //    它虽然空着，也不是这一趟建出来的。
        var existed = temp.Combine("existed");
        var pre = Path.Combine(existed, "broken.zip");
        Directory.CreateDirectory(existed);
        using (var zip = ZipFile.Open(pre, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(new string('c', 300) + ".txt").Open());
            writer.Write("x");
        }

        Directory.CreateDirectory(Path.Combine(existed, "broken"));
        var keptVacant = await RunOneAsync(pre, new PierceRegistry(), conflict: ConflictPolicy.Overwrite);
        Assert.False(keptVacant.Ok);
        Assert.True(
            Directory.Exists(Path.Combine(existed, "broken")),
            "本次之前就存在的空目录被动过了");
    }

    /// <summary>
    /// 弱命名的 `.NNN`：同一个包，拖目录进去与拖文件进去必须得到同一结论（`D-7`）。
    ///
    /// 四类分卷命名里只有 `.NNN` 是纯文件名判不出「分卷第几卷」还是「独立包」的一档：
    /// 一个内容完整的包恰好叫 `report.202` 时，目录入口按分卷判、以「缺少主卷」静默漏解，
    /// 而单文件入口直接解开了；真分卷的次卷单独在场时，单文件入口又会拿次卷去送引擎。
    /// 判据收敛到识别层一处，本体是 `Probe.IsStandaloneVolume`。
    ///
    /// 断言必须同时跑两个入口再比，只测一个入口等于没测。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-188"), Trait("Category", "ledger")]
    public async Task VolumeLookingName_MustGiveBothEntryPointsTheSameVerdict()
    {
        using var temp = new TestDirectory();

        // ① 恰好叫 `.202` 的独立包：两入口都该把它解开，产物目录名也一样，而且谁都不许报「缺少主卷」。
        var standaloneFolder = Path.Combine(temp.Combine("standalone"), "byFolder");
        var standaloneFile = Path.Combine(temp.Combine("standalone"), "byFile");
        CopyFixture("ok-small.zip", Path.Combine(standaloneFolder, "report.202"));
        CopyFixture("ok-small.zip", Path.Combine(standaloneFile, "report.202"));

        var (folderSays, folderLogs) = await EntryRunAsync(standaloneFolder);
        var (fileSays, fileLogs) = await EntryRunAsync(Path.Combine(standaloneFile, "report.202"));
        Assert.Equal(folderSays, fileSays);
        Assert.True(fileSays.Layers > 0, "恰好叫 .202 的独立包两入口都该解开");
        Assert.Equal("report", fileSays.Product);
        Assert.DoesNotContain(folderLogs, line => line.Contains("缺少主卷", StringComparison.Ordinal));
        Assert.DoesNotContain(fileLogs, line => line.Contains("缺少主卷", StringComparison.Ordinal));

        // ② 真分卷的次卷单独在场：两入口都该认作缺主卷，谁都不去解。
        var orphanFolder = Path.Combine(temp.Combine("orphan"), "byFolder");
        var orphanFile = Path.Combine(temp.Combine("orphan"), "byFile");
        CopyFixture(Path.Combine("probe", "vol-numeric", "x.002"), Path.Combine(orphanFolder, "x.002"));
        CopyFixture(Path.Combine("probe", "vol-numeric", "x.002"), Path.Combine(orphanFile, "x.002"));

        var (orphanSaysFolder, orphanLogsFolder) = await EntryRunAsync(orphanFolder);
        var (orphanSaysFile, _) = await EntryRunAsync(Path.Combine(orphanFile, "x.002"));
        Assert.Equal(orphanSaysFolder, orphanSaysFile);
        Assert.Equal(0, orphanSaysFile.Layers);
        Assert.Equal(StopReason.NoArchive, orphanSaysFile.Reason);
        Assert.Contains(orphanLogsFolder, line => line.Contains("缺少主卷", StringComparison.Ordinal));

        // ③ 同一个包放进子目录：往下找那条通道也是同一判据，不许只认同一层的。
        var nestedRoot = temp.Combine("nested");
        CopyFixture("ok-small.zip", Path.Combine(nestedRoot, "sub", "deep.202"));

        var (nestedSays, _) = await EntryRunAsync(nestedRoot);
        Assert.True(nestedSays.Layers > 0, "子目录里恰好叫 .202 的独立包也该解开");
        Assert.Equal("deep", nestedSays.Product);

        // ④ 同 basename 的独立包与首卷并存：两个都是各自完整的包（内容不同，免得被判重跳过），两个都要解，
        //    谁也不许被当成对方的续卷，也不许冒出「缺少主卷」。
        var siblings = temp.Combine("siblings");
        MakeZip(Path.Combine(siblings, "report.001"), "first");
        MakeZip(Path.Combine(siblings, "report.202"), "second");

        var (bothSays, bothLogs) = await EntryRunAsync(siblings);
        Assert.Equal(2, bothSays.Layers);
        Assert.DoesNotContain(bothLogs, line => line.Contains("缺少主卷", StringComparison.Ordinal));
    }

    /// <summary>
    /// 撞了层数上限不许报成功（`D-8`）。
    ///
    /// 层预算用尽时必然还有包没解，用户拿不到那一层以里的内容，所以它落进「有内容没解」那一档：
    /// 结果那一位、界面状态、命令行口径与日志四处都要能看出是「到上限了」而不是「解完了」。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-189"), Trait("Category", "ledger")]
    public async Task HittingTheDepthBudget_MustNotReportSuccess()
    {
        var run = await PierceHarness.RunAsync("chain-3", maxDepth: 2);
        try
        {
            Assert.Equal(StopReason.MaxDepth, run.Result.StopReason);
            Assert.False(run.Result.Ok, "撞了层数上限就是没解完，这一位不许为真");
            Assert.True(run.Result.Partial, "它属于有内容没解那一档");
            Assert.Equal(JobState.Partial, Runner.MapState(run.Result));
            Assert.False(Runner.IsDone(run.Result), "命令行口径同样不许判成功");
            Assert.Contains(run.Logs, line => line.Contains("达到层数上限", StringComparison.Ordinal));
        }
        finally
        {
            run.Cleanup();
        }
    }

    /// <summary>把夹具复制到指定路径；两个入口各跑一次，落点必须互不相干。</summary>
    private static void CopyFixture(string relative, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(TestPaths.Fixture(relative), target, overwrite: true);
    }

    /// <summary>造一个只有一个条目的 zip：两个包内容相同时会被判重跳过，用它把内容错开。</summary>
    private static void MakeZip(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("payload.txt").Open());
        writer.Write(content);
    }

    /// <summary>一个入口跑一趟之后的结论。两个入口比的是这个元组，不是路径。</summary>
    private sealed record EntryVerdict(bool Ok, StopReason Reason, int Layers, string Product);

    /// <summary>一趟的结论与日志：日志口径也要比，不许冒出「缺少主卷」那种误报。</summary>
    private static async Task<(EntryVerdict Verdict, List<string> Logs)> EntryRunAsync(string entry)
    {
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var logs = new List<string>();
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MaxDepth = 5, MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) });
        var result = await piercer.RunAsync(entry);
        return (new EntryVerdict(
            result.Ok,
            result.StopReason,
            result.Layers.Count,
            result.Layers.Count > 0 ? Path.GetFileName(result.Layers[0].OutDir) : string.Empty),
            logs);
    }
    private static async Task<PierceResult> RunOneAsync(
        string target, PierceRegistry registry, string? outputRoot = null,
        ConflictPolicy conflict = ConflictPolicy.Rename, List<string>? logs = null)
    {
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions
            {
                MaxDepth = 5,
                MinFreeGb = 0,
                Registry = registry,
                OutputRoot = outputRoot,
                Conflict = conflict,
                Log = logs is null ? null : new ChannelLog((_, message) => logs.Add(message)),
            });
        return await piercer.RunAsync(target);
    }

    private static int CountPayloads(string directory) =>
        Directory.EnumerateFiles(directory, "same.txt", SearchOption.AllDirectories).Count();

    private sealed record EntryOutcome(bool Ok, bool IsDone, string? FirstProductName);

    /// <summary>同一个场景分别走单文件入口与目录入口，把三处结论带回来比对。</summary>
    private static async Task<EntryOutcome> EntryScenarioAsync(
        bool asDirectory, bool useOutputRoot, ConflictPolicy conflict, bool preCreate)
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("sample");
        CopySampleInto(sample, "chain-1");
        var entry = asDirectory ? sample : Path.Combine(sample, "outer.zip");
        var outputRoot = useOutputRoot ? temp.Combine("out") : null;
        if (outputRoot is not null)
        {
            Directory.CreateDirectory(outputRoot);
        }

        if (preCreate)
        {
            var target = Path.Combine(outputRoot ?? sample, "outer");
            Directory.CreateDirectory(target);
        }

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions
            {
                MaxDepth = 5,
                MinFreeGb = 0,
                Conflict = conflict,
                OutputRoot = outputRoot,
            });
        var result = await piercer.RunAsync(entry);
        return new EntryOutcome(
            result.Ok,
            Runner.IsDone(result),
            result.Layers.Count > 0 ? Path.GetFileName(result.Layers[0].OutDir) : null);
    }

    private static string CopySampleInto(string target, string sample)
    {
        var source = Path.Combine(TestPaths.PierceRoot, sample);
        CopyTree(source, target);
        return target;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    /// <summary>造一个只有尾部目录的最小合法 zip，给伪装包夹具用。</summary>
    private static byte[] MinimalStoredZip(byte[] payload)
    {
        var crc = Crc32(payload);
        var name = System.Text.Encoding.UTF8.GetBytes("secret.txt");
        var local = new List<byte>();
        local.AddRange("PK\u0003\u0004"u8.ToArray());
        local.AddRange([20, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        local.AddRange(BitConverter.GetBytes(crc));
        local.AddRange(BitConverter.GetBytes((uint)payload.Length));
        local.AddRange(BitConverter.GetBytes((uint)payload.Length));
        local.AddRange(BitConverter.GetBytes((ushort)name.Length));
        local.AddRange([0, 0]);
        local.AddRange(name);
        local.AddRange(payload);

        var central = new List<byte>();
        central.AddRange("PK\u0001\u0002"u8.ToArray());
        central.AddRange([20, 0, 20, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        central.AddRange(BitConverter.GetBytes(crc));
        central.AddRange(BitConverter.GetBytes((uint)payload.Length));
        central.AddRange(BitConverter.GetBytes((uint)payload.Length));
        central.AddRange(BitConverter.GetBytes((ushort)name.Length));
        central.AddRange([0, 0, 0, 0, 0, 0, 0, 0]);
        central.AddRange(BitConverter.GetBytes(0u));
        central.AddRange(BitConverter.GetBytes(0u));
        central.AddRange(name);

        var eocd = new List<byte>();
        eocd.AddRange("PK\u0005\u0006"u8.ToArray());
        eocd.AddRange([0, 0, 0, 0, 1, 0, 1, 0]);
        eocd.AddRange(BitConverter.GetBytes((uint)central.Count));
        eocd.AddRange(BitConverter.GetBytes((uint)local.Count));
        eocd.AddRange([0, 0]);

        return [.. local, .. central, .. eocd];
    }

    /// <summary>把若干文本条目写成一个存储型 zip。</summary>
    private static void WriteStoredZip(string path, IReadOnlyList<(string Name, string Body)> entries) =>
        WriteStoredZipRaw(
            path,
            [.. entries.Select(entry => (entry.Name, System.Text.Encoding.UTF8.GetBytes(entry.Body)))]);

    /// <summary>
    /// 把若干条目写成一个存储型 zip，条目正文按字节给。本地头 30 字节，字段个数少一个整包就错位。
    ///
    /// 要造「内层包与另一份逐字节相同」的夹具只能走这一版：正文得是那个包的原始字节，
    /// 按文本转一道会把它改掉。
    /// </summary>
    private static void WriteStoredZipRaw(string path, IReadOnlyList<(string Name, byte[] Body)> entries)
    {
        var body = new List<byte>();
        var central = new List<byte>();
        foreach (var (name, payload) in entries)
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(name);
            var crc = Crc32(payload);
            var at = body.Count;
            body.AddRange("PK\u0003\u0004"u8.ToArray());
            body.AddRange([20, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            body.AddRange(BitConverter.GetBytes(crc));
            body.AddRange(BitConverter.GetBytes((uint)payload.Length));
            body.AddRange(BitConverter.GetBytes((uint)payload.Length));
            body.AddRange(BitConverter.GetBytes((ushort)raw.Length));
            body.AddRange([0, 0]);
            body.AddRange(raw);
            body.AddRange(payload);

            central.AddRange("PK\u0001\u0002"u8.ToArray());
            central.AddRange([20, 0, 20, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            central.AddRange(BitConverter.GetBytes(crc));
            central.AddRange(BitConverter.GetBytes((uint)payload.Length));
            central.AddRange(BitConverter.GetBytes((uint)payload.Length));
            central.AddRange(BitConverter.GetBytes((ushort)raw.Length));
            central.AddRange([0, 0, 0, 0, 0, 0, 0, 0]);
            central.AddRange(BitConverter.GetBytes(0u));
            central.AddRange(BitConverter.GetBytes((uint)at));
            central.AddRange(raw);
        }

        var eocd = new List<byte>();
        eocd.AddRange("PK\u0005\u0006"u8.ToArray());
        eocd.AddRange([0, 0, 0, 0]);
        eocd.AddRange(BitConverter.GetBytes((ushort)entries.Count));
        eocd.AddRange(BitConverter.GetBytes((ushort)entries.Count));
        eocd.AddRange(BitConverter.GetBytes((uint)central.Count));
        eocd.AddRange(BitConverter.GetBytes((uint)body.Count));
        eocd.AddRange([0, 0]);

        File.WriteAllBytes(path, [.. body, .. central, .. eocd]);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
