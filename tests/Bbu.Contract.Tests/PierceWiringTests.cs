using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 穿透层的结构门禁：停止原因每加一个成员，文案表与状态映射两处都得有分支。
///
/// 破了它的后果是静默的：新成员的文案会从文案表缺失处抛异常，
/// 或者状态映射掉进兜底分支被显示成「失败」。加成员的人很容易只改枚举，
/// 所以把它写成断言，而不是靠记住。
/// </summary>
public sealed class PierceWiringTests
{
    /// <summary>
    /// 穿透层要把暂停传进试密码那一路（B-2026-149）：不传的话枚举期间点暂停什么也不会发生 ——
    /// 判定是进程内的 CPU 活，没人问它就不会停。撤掉那一行即红。
    /// </summary>
    [Fact]
    public void PiercePassword_MustCarryThePauseHook()
    {
        var code = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "PiercePassword.cs"));
        Assert.Contains("Pause = _options.Pause", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStopReasonMember_MustHaveATextAndAStateBranch()
    {
        var text = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Texts", "PierceText.cs"));
        var runner = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Runner.cs"));

        var members = Enum.GetNames<StopReason>();
        Assert.NotEmpty(members);

        var missing = new List<string>();
        foreach (var name in members)
        {
            if (!text.Contains($"StopReason.{name} =>", StringComparison.Ordinal))
            {
                missing.Add($"{name} 在文案表里没有分支");
            }

            if (!runner.Contains($"StopReason.{name} =>", StringComparison.Ordinal))
            {
                missing.Add($"{name} 在状态映射里没有分支");
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>文案的取值域只能是这十三个成员，运行时逐条查一遍：空文案等于没说清为什么停。</summary>
    [Fact]
    public void EveryStopReasonLabel_MustBeNonEmpty()
    {
        foreach (var reason in Enum.GetValues<StopReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(reason.Label()), reason.ToString());
        }
    }

    /// <summary>
    /// 它是结果、状态映射与退出码三处判据的分界线。
    /// </summary>
    [Fact]
    public void IsCleanStop_MustMatchTheDocumentedTable()
    {
        StopReason[] clean =
        [
            StopReason.NoArchive, StopReason.MaxDepth, StopReason.AlreadyVisited,
            StopReason.Ambiguous, StopReason.Cancelled, StopReason.OutputExists,
            StopReason.SearchIncomplete,
        ];

        foreach (var reason in Enum.GetValues<StopReason>())
        {
            Assert.Equal(clean.Contains(reason), reason.IsCleanStop());
        }
    }

    /// <summary>
    /// 「这一卷是不是独立包」的判据只有一处本体，调用点逐条钉住（新增唯一入口的调用点时，
    /// 调用点清单要跟着加一行。少一处就有一处判据不生效。
    /// </summary>
    [Fact]
    public void StandaloneVolumeVerdict_MustBeReachedFromEveryEntry()
    {
        (string File, int AtLeast)[] callSites =
        [
            ("ProbeVolumes.cs", 3),  // 本体一处，加组内过滤与缺卷判定两处调用
            ("ProbeScan.cs", 2),     // 本层候选与往下找两条通道
            ("Scan.cs", 2),          // 界面清单的本层与单文件入口
            ("Pierce.cs", 1),        // 穿透的单文件入口
            ("PierceExtract.cs", 1), // 删原包时的整组删除范围
        ];

        foreach (var (file, atLeast) in callSites)
        {
            var text = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", file));
            var hits = text.Split("IsStandaloneVolume", StringSplitOptions.None).Length - 1;
            Assert.True(hits >= atLeast, $"{file} 里只找到 {hits} 处，判据的调用点清单漏了一处");
        }

        // 本体只有一处：上面第一份文件里那个就是它。
        var volumes = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "ProbeVolumes.cs"));
        Assert.Contains("public static bool IsStandaloneVolume", volumes, StringComparison.Ordinal);
    }

    /// <summary>
    /// 它比安全停下窄，撞层数上限不在其中（报它时必然还有包没解）。
    /// </summary>
    [Fact]
    public void CountsAsDone_MustMatchTheDocumentedTable()
    {
        StopReason[] done =
        [
            StopReason.NoArchive, StopReason.AlreadyVisited, StopReason.Ambiguous,
            StopReason.Cancelled, StopReason.OutputExists, StopReason.SearchIncomplete,
        ];

        foreach (var reason in Enum.GetValues<StopReason>())
        {
            Assert.Equal(done.Contains(reason), reason.CountsAsDone());
        }
    }

    /// <summary>
    /// 判重的账是**链**的态：Runner 给每一项各建一份，链内同层有多个候选时各分叉一份。
    /// 一批共用一本会让「这一项的结果取决于同批里还有谁」。
    /// </summary>
    [Fact]
    public void FingerprintRegistry_MustBeOwnedByTheChain()
    {
        var runner = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Runner.cs"));
        var pierce = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Pierce.cs"));
        var loop = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "PierceLoop.cs"));

        // 一项一本：Runner 注入的是新账，不是批共用的那一本。
        Assert.Contains("Registry = new PierceRegistry()", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry = _registry", runner, StringComparison.Ordinal);
        // 穿透层自己不许再建一份：调用方给了就用调用方那份。
        Assert.Contains("_options.Registry ?? new PierceRegistry()", pierce, StringComparison.Ordinal);
        // 分叉在主循环里。这几条只防形态回退（换一种等价写法能蒙过去），行为由
        // `SameDirectoryDuplicates_MustEachBeExtractedOnBothEntryPoints` 与
        // `DuplicateSecondCopy_MustAlsoBeExtractedAndTheNormalCaseMustNotBeUnfinished` 兜住。
        Assert.Contains("chain.Fork()", loop, StringComparison.Ordinal);
    }

    /// <summary>
    /// 解压**之前**那次登记不许挪到成功之后：防死循环靠的是「还没解就先查得到」。
    ///
    /// 另一条是防死循环那条安全线：入口包也必须先登记再解 —— 链上没有它，
    /// 「这一份里再出现自己的副本」就拦不住，那一层会一直往下套到层数上限。
    /// </summary>
    [Fact]
    public void Registry_MustStillRegisterBeforeExtracting()
    {
        var loop = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "PierceLoop.cs"));
        var pierce = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Pierce.cs"));

        var registered = loop.IndexOf("chain.Register(fingerprint, target);", StringComparison.Ordinal);
        var extracted = loop.IndexOf("await ExtractLayerAsync(result, target, outdir, depth", StringComparison.Ordinal);
        Assert.True(registered >= 0, "主循环里那次登记不见了");
        Assert.True(extracted >= 0, "取不到解压那一步");
        Assert.True(registered < extracted, "登记必须留在解压之前：防死循环靠它");

        // 入口包同理：它进链之后，里面那份「自己的副本」才撞得上祖先。
        var entryRegistered = pierce.IndexOf("_registry.Register(fingerprint, start);", StringComparison.Ordinal);
        var entryExtracted = pierce.IndexOf("await ExtractLayerAsync(result, start, entryOutdir", StringComparison.Ordinal);
        Assert.True(entryRegistered >= 0, "入口包那次登记不见了");
        Assert.True(entryExtracted >= 0, "取不到入口解压那一步");
        Assert.True(entryRegistered < entryExtracted, "入口包也要先登记再解：防死循环靠它");
    }

    /// <summary>
    /// 引擎报了警告退出码的那一层**也同样收掉包**：它按成功层处理，只有删包一步被跳过本就不自洽，
    /// 而「留给用户重试」那个意图在现状下实现不了（这一档落「有内容没解」，而部分完成不重跑）。
    ///
    /// 这一档在 7z 解压路径上造不出真夹具，
    /// 所以它只能是结构断言：**旧形态那一句「警告时不删」的闸必须消失**，善后必须走同一个收敛点。
    /// </summary>
    [Fact]
    public void WarningLayer_MustStillCollectThePackage()
    {
        var extract = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "PierceExtract.cs"));

        var warned = extract.IndexOf("if (extracted.Warned)", StringComparison.Ordinal);
        var collect = extract.IndexOf(
            "SettleTarget(result, outdir, [archive], depth, TargetFate.Extracted);", StringComparison.Ordinal);
        Assert.True(warned >= 0, "警告那一支不见了");
        Assert.True(collect > warned, "收包那一步必须在警告那一支之后照常执行");

        // 警告那一支里只许有那句日志：多一个 return 就等于又跳过了删包这一步。
        Assert.DoesNotContain("return", extract[warned..collect], StringComparison.Ordinal);

        // 收包只有一处出口：四路善后都进 SettleTarget，包的去留不在这里各判一次。
        Assert.Contains("private void SettleTarget(", extract, StringComparison.Ordinal);
    }

    /// <summary>层号事件与层清单只有一个入口：两处分写会让运行中与结束后的层号对不上。</summary>
    [Fact]
    public void LayerRecordingAndReporting_MustLiveInOnePlace()
    {
        var pierce = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Pierce.cs"));

        Assert.Contains("result.Layers.Add(layer);", pierce, StringComparison.Ordinal);
        Assert.Contains("LayerEvent(layer.Depth, layer.Target, layer.Ok);", pierce, StringComparison.Ordinal);
        Assert.Contains("void RecordLayer(PierceResult result, LayerResult layer)", pierce, StringComparison.Ordinal);
    }

    /// <summary>
    /// 逐层上报那个布尔值的含义只有一处口径：它是「这一层结束了没有」，不是「成功了没有」。
    /// 按「是否成功」读会在层开始那一档读错（它传的是假），于是把层号提前推进一格（外部审计第 1 轮 `D-1`）。
    /// 公开的那份选项文档与内部那个形参名两处同口径；任一处改回旧措辞即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-215")]
    public void LayerEventDocumentation_MustSayWhetherTheLayerEnded()
    {
        var pierce = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Pierce.cs"));

        Assert.Contains("这一层有没有结束", pierce, StringComparison.Ordinal);
        Assert.Contains("void LayerEvent(PackLayer depth, string target, bool done)", pierce, StringComparison.Ordinal);
    }

    /// <summary>
    /// 结果不许把异常抛给调用方：它是最后一道网，界面与命令行都直接调它。
    /// </summary>
    [Fact]
    public void RunMustNotThrow_MustKeepTheLastNet()
    {
        var pierce = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Pierce.cs"));

        Assert.Contains("catch (Exception ex)", pierce, StringComparison.Ordinal);
        Assert.Contains("UnexpectedFailure(start, result, ex)", pierce, StringComparison.Ordinal);
    }

    /// <summary>
    /// 识别层的两个引擎问询都要接上：只接一个的话，兜底那一档在生产路径上永远不会被问到，
    /// 而它自己的单元断言传的是桩探针，撤掉接线仍会通过（2026-10-08 审查指出）。
    /// </summary>
    [Fact]
    public void CarrierProbe_MustWireBothEngineAsks()
    {
        var extract = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "PierceExtract.cs"));

        Assert.Contains("EngineProbe = CanEngineRead", extract, StringComparison.Ordinal);
        Assert.Contains("FallbackEngineProbe = CanUnRarRead", extract, StringComparison.Ordinal);

        // 记录级入口是**层号唯一的通道**（语义入口带不了层号，params 占末位）⇒ 它只许出现在会带层号的那几族里；
        // 别处绕过语义入口、又不带层号，等于把层号来源又开了一条。清单按「谁真的在报层」维护。
        var bypass = Repo.Files(Repo.Src, ".cs")
            .Where(file => File.ReadAllText(file).Contains("Write(new LogRecord(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => name is not ("PierceExtract.cs" or "PiercePassword.cs" or "PierceLoop.cs" or "Vault.cs"))
            .ToList();
        Assert.Empty(bypass);
    }

    /// <summary>
    /// 目录报备那一趟必须停在廉价档：它按目录里每个文件跑，一个装满真视频的目录就是每个多读一遍整文件，
    /// 或者每个多起一次引擎进程。这条性质原先由 `Probe.SuspectedCarriers` 在**内部**按下去，
    /// 那个入口在 -4 删掉（欠账表 `debt 59`）之后改由**调用方声明**，所以这里钉两处生产调用点的声明
    /// </summary>
    [Fact]
    public void DirectoryReport_MustStayOnTheCheapTiers()
    {
        var scan = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Scan.cs"));
        var target = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "PierceTarget.cs"));

        // 扫描侧那一档整条关掉载体探测
        Assert.Contains("probeOptions with { Deep = false, Carriers = false }", scan, StringComparison.Ordinal);

        // 穿透侧：目录报备那一趟的选项块里不得出现引擎问询（目前依赖默认 null，这条钉住它不被加回）。
        // 取该方法的选项块而非全文匹配：同一个文件里另一个方法显式写着两个 null，全文匹配会把那两行当成证据。
        var start = target.IndexOf("var probeOptions = new ProbeOptions", StringComparison.Ordinal);
        Assert.True(start >= 0, "PierceTarget 里找不到目录报备用的选项块，判据要重写");
        var block = target[start..target.IndexOf("};", start, StringComparison.Ordinal)];
        Assert.Contains("Deep = false,", block, StringComparison.Ordinal);
        Assert.DoesNotContain("EngineProbe", block, StringComparison.Ordinal);
        Assert.DoesNotContain("FallbackEngineProbe", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「同一批只问第一次」的两个批次入口都要包上 `AskOncePerBatch`（B-2026-178）：
    /// 只在扫描或只在运行那一侧包，另一侧仍会反复打扰用户，而两种写法都编译得过。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-178")]
    public void ScanFuse_MustBeWrappedAtBothBatchEntries()
    {
        var scan = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Scan.cs"));
        var runner = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Runner.cs"));

        Assert.Contains("= Probe.AskOncePerBatch(", scan, StringComparison.Ordinal);
        Assert.Contains("= Probe.AskOncePerBatch(", runner, StringComparison.Ordinal);
    }
}
