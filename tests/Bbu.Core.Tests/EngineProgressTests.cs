using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 真实解压进度：引擎自己报的百分比要走到进度通道上，而不是只显示秒表。
///
/// 分两层测：**解析**照实测行格式钉在
/// <see cref=" .PercentParsing_MustFollowEachEngineShape"/>；这里测**接线** ——
/// 真跑一条命令时进度确实到了 <see cref="IRunLog"/>，且进度行没有漏进结果文本。
///
/// 2026-10-08 之前 7z 的命令行写死 <c>-bsp0</c>，这一整条通道是空的：撤掉 <c>-bsp1</c>
/// 这两条断言就一个进度点都收不到。「够大的包上采样到多个中间值」那一条靠一次性实测，
/// 记录在批次的实测结果里 —— 仓内夹具都在几百 KB 以下，引擎只来得及报一个点。
/// </summary>
[Collection("engine")]
public sealed class EngineProgressTests
{
    private static string Fixture(string name) => TestPaths.Fixture(name);

    [Fact, Trait("Category", "edge")]
    public async Task Test_MustReportEnginePercent()
    {
        var log = new RecordingLog();
        var result = await TestPaths.NewEngine(log).TestAsync(Fixture("slow-big.zip"));

        Assert.True(result.Ok, result.Output);

        var facts = log.ProgressFacts.Where(fact => fact.Stage is ProgressStage.Extract).ToList();
        Assert.NotEmpty(facts);

        // 百分比那一格是它自己，不是「已试几个 / 共几个」
        Assert.All(facts, fact => Assert.Equal(100, fact.Total));
        Assert.All(facts, fact => Assert.InRange(fact.Tried, 0, 100));

        // 引擎结语只进排错档（原宿主 .LongCommand_MustReportHeartbeat）：
        // 命令行与结果各一条独立记录，正文里不带换行。
        Assert.Contains(log.Debugs, line => line.Contains(result.Brief, StringComparison.Ordinal));
    }

    /// <summary>
    /// 同一条纪律的另一半：**穿透里试密码**那一支也不许报引擎百分比（B-2026-146）。
    /// 手输密码那一支直接调 <c>TestAsync</c>，漏传 <c>reportPercent: false</c> 时
    /// <c>7z</c> 对单条目验证打出的 <c>0%</c> 就会冒充「解压进度」。
    ///
    /// 断言取一次**没解压成**的运行：三次手输密码全错、这一层从头到尾没解压，
    /// 因此进度通道里**一条解压帧都不该有** —— 有就是试密码那几跑漏出来的。撤掉修复即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-146")]
    public async Task PiercePassword_MustNotReportEnginePercent()
    {
        using var temp = new TestDirectory();
        var log = new RecordingLog();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var asked = 0;
        var options = new PierceOptions
        {
            MaxDepth = 5,
            MinFreeGb = 0,
            OutputRoot = temp.Combine("out"),
            Ask = (_, _) =>
            {
                asked++;
                return Task.FromResult<string?>($"WrongPassword{asked}");
            },
        };
        Directory.CreateDirectory(options.OutputRoot);
        var piercer = new Piercer(TestPaths.NewEngine(log), vault, options, CancellationToken.None);

        var result = await piercer.RunAsync(Fixture(Path.Combine("vault", "zip-aes256.zip")));

        Assert.True(asked > 0, "这一跑没有走到问密码那一支，量不到要量的事");
        Assert.Equal(StopReason.PasswordExhausted, result.StopReason);
        Assert.DoesNotContain(log.ProgressFacts, fact => fact.Stage is ProgressStage.Extract);
    }

    /// <summary>
    /// 试密码那一路**不许**报引擎百分比（B-2026-146）。候选循环每试一个密码就按
    /// 「最小的加密条目」跑一次 <c>t</c>，而 <c>Test</c> 这条命令带 <c>-bsp1</c> ——
    /// 7z 对单条目验证照样打出 <c>0%</c>，那一条进了活动槽就成了「解压中 0%」，
    /// 把「已试几个 / 共几个」顶掉。撤掉修复即红。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-146")]
    public async Task Verify_MustNotReportEnginePercent()
    {
        var log = new RecordingLog();
        var engine = TestPaths.NewEngine(log);
        var archive = Fixture(Path.Combine("vault", "zip-aes256.zip"));
        var info = await engine.InspectAsync(archive);
        var result = await engine.VerifyAsync(archive, "BbuPw2026", null, info);

        Assert.True(result.Ok, result.Output);
        Assert.DoesNotContain(log.ProgressFacts, fact => fact.Stage is ProgressStage.Extract);
    }

    /// <summary>
    /// 枚举心跳那条进度行是「还在跑」的陈述，不是「需要注意」：活动槽按级别取色
    /// （<c>SetLevelClass(ActDoing, line.Level)</c>），挂 Warn 会把整条枚举进度显示成警告色
    ///
    /// 级别表退役之后这一条改钉**站点入口**：这一句的档不再由文案查表得到，它就在那一处调用的形上 ——
    /// 那一处走记录级入口 <c>Write(LogRecord)</c> 且档写死 <c>LogLevel.Debug</c>（它还要带层号，语义入口带不了）。
    /// 把那一处改回点名 Warn 即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-148")]
    public void SearchHeartbeat_MustNotBeAWarning()
    {
        var vault = File.ReadAllText(
            Path.Combine(TestPaths.Root, "src", "Bbu.Core", "Services", "Vault.cs"));
        var at = vault.IndexOf("VaultText.SearchHeartbeat", StringComparison.Ordinal);
        Assert.True(at > 0, "枚举心跳那句文案在 Vault.cs 里找不到了 —— 先确认它去了哪儿");

        // 这一处的形：记录级入口在前、进度对象在后（它是第五个实参），所以窗口要跨过文案两侧。
        var site = vault[Math.Max(0, at - 600)..Math.Min(vault.Length, at + 600)];
        Assert.Contains("Log?.Write(new LogRecord(", site, StringComparison.Ordinal);
        Assert.Contains("LogLevel.Debug,", site, StringComparison.Ordinal);
        Assert.Contains("new LogProgress(seconds", site, StringComparison.Ordinal);
        Assert.DoesNotContain("LogLevel.Warn", site, StringComparison.Ordinal);
        Assert.DoesNotContain("LogLevel.Error", site, StringComparison.Ordinal);
    }

    /// <summary>
    /// 进度行不许漏进结果文本：它是每 100ms 一条的噪音，漏进去会把 <c>Tail</c> 挤成百分比行，
    /// 而失败详情取的就是 <c>Tail</c>。
    /// </summary>
    [Fact, Trait("Category", "edge")]
    public async Task ProgressLines_MustStayOutOfTheResultText()
    {
        var result = await TestPaths.NewEngine().TestAsync(Fixture("slow-big.zip"));

        Assert.True(result.Ok, result.Output);
        Assert.DoesNotContain("%", result.Output);
        Assert.DoesNotContain("%", result.Tail);
    }

    /// <summary>解压那一路也带进度。</summary>
    [Fact, Trait("Category", "edge")]
    public async Task Extract_MustReportEnginePercent()
    {
        var log = new RecordingLog();
        using var dir = new TestDirectory();
        var result = await TestPaths.NewEngine(log).ExtractAsync(
            Fixture("slow-big.zip"), dir.Path);

        Assert.True(result.Ok, result.Output);

        var facts = log.ProgressFacts.Where(fact => fact.Stage is ProgressStage.Extract).ToList();
        Assert.NotEmpty(facts);
        Assert.All(facts, fact => Assert.Equal(100, fact.Total));
        Assert.All(facts, fact => Assert.InRange(fact.Tried, 0, 100));
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 够大的包上要采样到多个中间值，而不是只有首尾两个点 —— 这是「进度是真值而不是秒表」那一条判据。
    ///
    /// 仓内夹具都在几百 KB 以下，引擎只来得及报一个点，所以这一条挂在仓外的 914 MB 真样本上：
    /// UnRAR 按字节报，1% 一跳。属真机档，手动跑；样本不在的机器上直接返回。
    /// </summary>
    [Fact]
    [Trait("Category", "machine")]
    public async Task BigSample_MustReportManyIntermediatePercents()
    {
        if (!File.Exists(TestPaths.BigSample))
        {
            return;
        }

        var log = new RecordingLog();
        var result = await TestPaths.NewEngine(log).TestAsync(TestPaths.BigSample);
        Assert.True(result.Ok, result.Output);

        var percents = log.ProgressFacts
            .Where(fact => fact.Stage is ProgressStage.Extract)
            .Select(fact => fact.Tried)
            .ToList();

        Assert.True(percents.Count >= 5, $"只收到 {percents.Count} 个进度点");
        Assert.Contains(percents, percent => percent is > 0 and < 100);

        // UnRAR 到 99% 就直接收尾，不报 100（7z 才报满）——所以这里盯的是「跑到接近结束」而不是等于 100
        Assert.True(percents.Max() >= 90, $"最大只到 {percents.Max()}%");
        Assert.Equal(percents.Distinct().Count(), percents.Count);
    }
}
