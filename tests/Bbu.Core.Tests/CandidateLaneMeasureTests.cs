using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Bbu.Core.Tests;

/// <summary>
/// 真机档：候选枚举的并行度。以候选数与路数为自变量，量墙钟与等价并行度（同候选数下串行时长除以并行时长）。
///
/// 两组并排（不接快路与接快路），用来分辨「判定线程和引擎抢核」与「引擎那一路本来串行」两种成因。
///
/// 它**不写断言**：测量不是判据。报告落点见 <see cref="ReportPath"/>，可用环境变量改。
/// </summary>
[Trait("Category", "machine")]
[Collection("engine")]
public sealed class CandidateLaneMeasureTests
{
    private static readonly int[] Counts = [8, 16, 32, 64];
    private static readonly int[] Lanes = [2, 4, 8, 16];

    /// <summary>纯进程创建对照起多少个进程。</summary>
    private const int SpawnCount = 64;

    private readonly ITestOutputHelper _output;

    public CandidateLaneMeasureTests(ITestOutputHelper output) => _output = output;

    internal static string ReportPath =>
        Environment.GetEnvironmentVariable("BBU_CANDIDATE_LANES_OUT")
        ?? Path.Combine(TestPaths.Root, ".scratch", "perf", "candidate-lanes.txt");

    [Fact]
    public async Task Parallelism_MustBeMeasuredByCandidatesAndWorkers()
    {
        var engine = TestPaths.NewRarEngine();
        var archive = Path.Combine(TestPaths.Fixtures, "vault", "zip-aes256.zip");
        var info = await engine.InspectAsync(archive);

        var report = new StringBuilder();

        void Say(string text = "")
        {
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        Say("==============================================================================");
        Say($"候选枚举的并行度   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"机器 {Environment.ProcessorCount} 逻辑核   包 {Path.GetFileName(archive)}   等价并行度 = 一路时长 ÷ 该路数时长");
        Say("==============================================================================");

        foreach (var (label, fastPath, why) in new (string, IFastPath?, string)[]
                 {
                     ("不接快路", null, "候选全落引擎，判定线程不占核"),
                     ("接快路", VaultUnlockTests.FastPath, "判定与引擎同抢核"),
                 })
        {
            Say();
            Say($"== {label}（{why}）==");
            Say("  候选   一路      2 路            4 路            8 路           16 路");
            foreach (var count in Counts)
            {
                var single = await WalkAsync(engine, archive, info, count, 1, fastPath);
                var line = $"  {count,4} {single,6:F2}s";
                foreach (var workers in Lanes)
                {
                    var seconds = await WalkAsync(engine, archive, info, count, workers, fastPath);
                    line += $"  {seconds,5:F2}s(×{single / Math.Max(seconds, 1e-6),4:F2})";
                }

                Say(line);
            }
        }

        // 与候选枚举无关的两段对照：纯进程创建。引擎那条路每候选起一个进程，且每个只活几十毫秒，
        // 所以它的并行度上限由「起进程这一步」决定，而不由候选枚举的调度决定。
        Say();
        Say($"== 纯进程创建（cmd /c exit × {SpawnCount}，每条线程自己起自己等）==");
        Say("  路数      总时长");
        var spawnBase = SpawnSeconds(["/c", "exit"], SpawnCount, 1);
        Say($"  {1,4} {spawnBase,10:F2}s");
        foreach (var workers in Lanes)
        {
            var seconds = SpawnSeconds(["/c", "exit"], SpawnCount, workers);
            Say($"  {workers,4} {seconds,10:F2}s(×{spawnBase / Math.Max(seconds, 1e-6),4:F2})");
        }

        Say();
        Say("== 单次进程活多久，与并行度什么关系（同一次运行里量两档）==");
        Say("  命令                                      次数      一路      16 路      倍率");
        foreach (var (label, args, repetitions) in new (string, string[], int)[]
                 {
                     ("cmd /c exit（约 30 毫秒）", ["/c", "exit"], SpawnCount),
                     ("cmd /c ping -n 2 127.0.0.1（约 1 秒）", ["/c", "ping", "-n", "2", "127.0.0.1"], 8),
                 })
        {
            var one = SpawnSeconds(args, repetitions, 1);
            var many = SpawnSeconds(args, repetitions, 16);
            Say($"  {label,-40} {repetitions,4} {one,9:F2}s {many,8:F2}s  ×{one / Math.Max(many, 1e-6),5:F2}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        File.WriteAllText(ReportPath, report.ToString(), new UTF8Encoding(false));
        _output.WriteLine($"报告落 {ReportPath}");
    }

    /// <summary>
    /// 走产品那条路（<see cref="Vault.UnlockAsync"/> → <c>CandidateSweep</c>）：一本纯内存的账、
    /// 一批必定试不完的错密码，量整轮到收工花了多久。
    /// </summary>
    private static async Task<double> WalkAsync(
        SevenZipEngine engine, string archive, ArchiveInfo? info, int count, int workers, IFastPath? fastPath)
    {
        var vault = new Vault();
        vault.SetEntries(Enumerable.Range(0, count).Select(i => new VaultEntry($"pw-{i:0000}")));

        var watch = Stopwatch.StartNew();
        await vault.UnlockAsync(engine, archive, new UnlockOptions { Workers = workers }, fastPath);
        watch.Stop();
        return watch.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// 纯进程创建：<paramref name="count"/> 次 <c>cmd</c> 加 <paramref name="args"/> 摊给
    /// <paramref name="workers"/> 条真线程，每条线程自己起自己等 —— 引擎那条路每候选起一个进程，
    /// 这一步的吞吐就是它的地板。计数器装进数组按元素取引用：捕获的局部变量不能取 ref。
    /// </summary>
    private static double SpawnSeconds(string[] args, int count, int workers)
    {
        var watch = Stopwatch.StartNew();
        var next = new int[1];
        var threads = new List<Thread>();
        for (var i = 0; i < Math.Max(1, Math.Min(workers, count)); i++)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    if (Interlocked.Increment(ref next[0]) - 1 >= count)
                    {
                        return;
                    }

                    var startInfo = new ProcessStartInfo("cmd")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    foreach (var arg in args)
                    {
                        startInfo.ArgumentList.Add(arg);
                    }

                    using var process = Process.Start(startInfo);
                    process?.WaitForExit();
                }
            });
            threads.Add(thread);
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        watch.Stop();
        return watch.Elapsed.TotalSeconds;
    }
}
