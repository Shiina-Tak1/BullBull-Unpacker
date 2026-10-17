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
/// 真机档：密码枚举速度矩阵。跑一次把整张表打出来。
///
/// 它**不写断言**：测量不是判据。判决落地之后，那条结论另有断言钉住
/// （见  与）。
///
/// 口径与 <c>.scratch\perf\measure_vault.py</c> 逐格对齐：同一批夹具（<c>tests\fixtures\vault\</c>）、
/// 同一个最坏场景（500 条候选 × 3 层嵌套、每层密码都不同）、同一条进程创建地板。
/// 三列并排（1.0.0 / 1.1.0 / C#）的数字分别来自那两个 tag 的导出树与本次运行。
///
/// 报告落点见 <see cref="ReportPath"/>。
/// </summary>
[Trait("Category", "machine")]
[Collection("engine")]
public sealed class VaultMeasureTests
{
    private const string Password = "BbuPw2026";
    private const string Wrong = "definitely-not-it";
    private static readonly string[] ChainPasswords = ["L1p@ss", "L2p@ss", "L3p@ss"];

    /// <summary>每格取几次错密码做平均。25 次是矩阵那一次的口径；同一个量两次差 50% 是常态。</summary>
    private const int Calls = 25;

    /// <summary>判决线看的那个候选条数。</summary>
    private const int WorstCount = 500;

    private readonly ITestOutputHelper _output;

    public VaultMeasureTests(ITestOutputHelper output) => _output = output;

    internal static string VaultRoot => Path.Combine(TestPaths.Fixtures, "vault");

    internal static string ReportPath =>
        Environment.GetEnvironmentVariable("BBU_VAULT_MATRIX_OUT")
        ?? Path.Combine(TestPaths.Root, ".scratch", "perf", "matrix-cs.txt");

    /// <summary>格：夹具相对路径与它的人读名字。顺序与 Python 侧的测量台一致。</summary>
    internal static IReadOnlyList<(string Rel, string Label)> Cells { get; } =
    [
        ("zip-aes256.zip", "zip / AES-256"),
        ("zip-zipcrypto.zip", "zip / ZipCrypto"),
        ("seven-mhe.7z", "7z / -mhe=on"),
        ("seven-plain.7z", "7z / -mhe=off"),
        ("rar5-hp.rar", "rar5 / -hp"),
        ("rar5-p.rar", "rar5 / -p"),
        ("plain.zip", "zip / 不加密"),
        ("plain.7z", "7z / 不加密"),
        ("plain.rar", "rar5 / 不加密"),
        ("plain.tar", "tar / 不加密"),
        ("plain.tar.gz", "gz / 不加密"),
        ("plain.lz4", "lz4 / 不加密"),
        ("carrier-tail.mp4", "伪装包 / AES zip 追加在垫片后"),
        ("embedded-rar5.lz4", "内嵌包 / lz4 裹 rar5 -hp"),
        (Path.Combine("vol-rar-part", "x.part1.rar"), "分卷 / .partN.rar 主卷"),
        (Path.Combine("vol-rar-part", "x.part3.rar"), "分卷 / .partN.rar 非首卷"),
        (Path.Combine("vol-zip-num", "x.001"), "分卷 / .001 主卷"),
        (Path.Combine("vol-zip-num", "x.003"), "分卷 / .001 非首卷"),
    ];

    [Fact]
    public async Task Vault_MatrixMustBeMeasured()
    {
        if (!Directory.Exists(VaultRoot))
        {
            _output.WriteLine("缺少密码层夹具：" + VaultRoot);
            return;
        }

        var engine = TestPaths.NewRarEngine();
        var report = new StringBuilder();

        void Say(string text = "")
        {
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        Say("==============================================================================");
        Say($"R5 密码枚举速度矩阵   C#   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"机器 {Environment.ProcessorCount} 逻辑核   夹具 {VaultRoot}");
        Say("==============================================================================");

        // ① 引擎起进程单价。cmd 那一档是进程创建地板，7z 那一档是引擎实际起价。
        Say();
        Say("== 1. 引擎起进程单价（同一条最短命令连跑，报最好与中位）==");
        Say($"  cmd /c exit（进程创建地板） {await ShortestAsync("cmd", "/c", "exit")}（{Calls} 次）");
        Say($"  7z i（引擎起来就退）       {await ShortestAsync(TestPaths.BundledSevenZip, "i")}（{Calls} 次）");
        // UnRAR 那一档是 rar 那一路的实际起价。2026-10-07 换引擎之前这里量的是
        // 机器上那份 `Rar.exe`；现在这一档量到的是随包的那一份。
        if (File.Exists(TestPaths.BundledUnRar))
        {
            Say($"  UnRAR.exe -?（rar 那一路） {await ShortestAsync(TestPaths.BundledUnRar, "-?")}（{Calls} 次）");
        }

        // ② 每候选单价（串行）。一列复用包信息，一列不复用，差的就是每候选多起一个进程的钱。
        Say();
        Say($"== 2. 每候选单价（串行，{Calls} 次错密码取平均）==");
        Say("  格                                      每候选 ms    含 inspect");
        foreach (var (rel, label) in Cells)
        {
            var full = Path.Combine(VaultRoot, rel);
            if (!File.Exists(full))
            {
                Say($"  {label,-34} {"-",10}  缺夹具");
                continue;
            }

            var info = await engine.InspectAsync(full);
            var cost = await MarginalAsync(engine, full, info);
            var legacy = await MarginalAsync(engine, full, null);
            Say($"  {label,-34} {cost,10:F1}  {legacy,11:F1}");
        }

        // ③ 并行：同一格、同一批候选，串行与满路各跑一遍。
        Say();
        Say("== 3. 每候选单价（并行）：同一格、串行 vs 满路 ==");
        var parallelBook = Book(Enumerable.Range(0, 200).Select(i => $"pw-{i:0000}"));
        foreach (var rel in new[] { "zip-aes256.zip", "seven-mhe.7z", "rar5-p.rar" })
        {
            var full = Path.Combine(VaultRoot, rel);
            if (!File.Exists(full))
            {
                continue;
            }

            var info = await engine.InspectAsync(full);
            var seq = await UnlockAsync(engine, full, parallelBook, 1, info);
            var par = await UnlockAsync(engine, full, parallelBook, Environment.ProcessorCount, info);
            Say($"  {rel,-20} 串行 {seq.Seconds,6:F2}s（{seq.Tries} 次，{seq.MsPerTry,6:F1} ms/候选）"
                + $"   满路 {par.Seconds,6:F2}s（{par.Tries} 次，{par.MsPerTry,6:F1} ms/候选）"
                + $"   ×{seq.Seconds / Math.Max(par.Seconds, 1e-6):F2}");
        }

        // ④ 每包最坏耗时：判决线看的就是这一行。
        Say();
        Say($"== 4. 每包最坏耗时（{WorstCount} 条候选 × 3 层嵌套，每层密码都不同）==");
        var chain = new[]
        {
            Path.Combine(VaultRoot, "chain-outer.zip"),
            Path.Combine(VaultRoot, "chain-middle.7z"),
            Path.Combine(VaultRoot, "chain-inner.rar"),
        };
        var head = Enumerable.Range(0, WorstCount - ChainPasswords.Length)
            .Select(i => $"pw-{i:0000}").ToList();

        foreach (var (label, tail) in new (string Label, List<string> Tail)[]
                 {
                     ("密码排最后 10%", [.. ChainPasswords]),
                     ("密码不在本里", []),
                 })
        {
            var book = Book([.. head, .. tail]);
            foreach (var (name, judge) in new (string Name, IFastPath? Judge)[]
                     {
                         ("无快路", null),
                         ("有快路", new CompositeFastPath()),
                     })
            {
                var total = 0.0;
                var detail = new List<string>();
                for (var i = 0; i < chain.Length; i++)
                {
                    var info = await engine.InspectAsync(chain[i]);
                    var run = await UnlockAsync(
                        engine, chain[i], book, Environment.ProcessorCount, info, judge);
                    total += run.Seconds;
                    detail.Add($"第{i + 1}层 {run.Seconds,6:F2}s（试 {run.Tries}）");
                }

                Say($"  {label,-14} {name} 合计 {total,7:F2}s   " + string.Join("  ", detail));
            }
        }

        var single = Book(Enumerable.Range(0, WorstCount).Select(i => $"pw-{i:0000}"));
        var singleRun = await UnlockAsync(
            engine, chain[0], single, Environment.ProcessorCount, await engine.InspectAsync(chain[0]));
        Say($"  单层 {WorstCount} 条全不中（对照旧版的 501 次） {singleRun.Seconds,7:F2}s（试 {singleRun.Tries}）");

        // ⑤ 命中第一个候选：真实使用里最常见的那一档。
        Say();
        Say("== 5. 命中第一个候选（端到端一次尝试）==");
        foreach (var (rel, label) in new[]
                 {
                     ("zip-aes256.zip", "zip AES-256"),
                     ("seven-mhe.7z", "7z -mhe=on"),
                     ("rar5-p.rar", "rar5 -p"),
                 })
        {
            var full = Path.Combine(VaultRoot, rel);
            if (!File.Exists(full))
            {
                continue;
            }

            var info = await engine.InspectAsync(full);
            var run = await UnlockAsync(engine, full, [Password], 1, info);
            Say($"  {label,-14} {run.Seconds * 1000,7:F1} ms（命中={run.Hit}，试 {run.Tries}）");
        }

        // ⑥ 密码本解析：读盘加解析，再加一次候选生成。
        Say();
        Say("== 6. 密码本解析（1000 行读盘加解析）==");
        var bookPath = Path.Combine(Path.GetTempPath(), "bbu-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bookPath);
        var bookFile = Path.Combine(bookPath, "密码本.txt");
        File.WriteAllText(
            bookFile,
            PasswordBook.Render(Enumerable.Range(0, 1000).Select(i => new VaultEntry($"pw-{i:0000}")), []),
            new UTF8Encoding(false));
        var reloads = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var vault = new Vault { BookPath = bookFile };
            vault.Load();
            watch.Stop();
            reloads.Add(watch.Elapsed.TotalMilliseconds);
        }

        reloads.Sort();
        var measureVault = new Vault { BookPath = bookFile };
        measureVault.Load();
        var candidatesWatch = Stopwatch.StartNew();
        var candidateCount = measureVault.CandidatesFor(Path.Combine(VaultRoot, "chain-outer.zip")).Count;
        candidatesWatch.Stop();
        Say($"  1000 行 reload 最好 {reloads[0]:F2} ms  中位 {reloads[reloads.Count / 2]:F2} ms");
        Say($"  CandidatesFor（1001 条） {candidatesWatch.Elapsed.TotalMilliseconds:F2} ms → {candidateCount} 个候选");
        Directory.Delete(bookPath, recursive: true);

        // ⑦ 快路能力：逐格式量划掉率、假阴性与每秒候选数。
        Say();
        Say("== 7. 快路能力（实现覆盖 zip / rar5 / 7z 三条）==");
        var fastPath = new CompositeFastPath();
        foreach (var rel in new[]
                 {
                     "zip-aes256.zip", "zip-zipcrypto.zip", "rar5-hp.rar", "rar5-p.rar",
                     "seven-mhe.7z", "seven-plain.7z", "plain.zip", "plain.7z",
                     Path.Combine("vol-zip-num", "x.001"),
                     Path.Combine("vol-seven-num", "x.7z.001"),
                     Path.Combine("vol-seven-plain", "x.7z.001"),
                 })
        {
            var full = Path.Combine(VaultRoot, rel);
            if (!File.Exists(full))
            {
                continue;
            }

            const int tries = 200;
            using var session = fastPath.Open(full);
            var watch = Stopwatch.StartNew();
            var killed = 0;
            for (var i = 0; i < tries; i++)
            {
                if (session is not null && session.Judge($"wrong-{i:0000}") is FastPathVerdict.ProbablyWrong)
                {
                    killed++;
                }
            }

            watch.Stop();
            var perCandidate = watch.Elapsed.TotalMilliseconds / tries;
            // 会话为 null = 这个包判不了，正确密码当然不会被误杀；只有判了却回「肯定不对」才是误杀。
            var keepsRight = session is null || session.Judge(Password) is FastPathVerdict.Unknown;
            Say($"  {rel,-24} 划掉 {killed}/{tries}（{killed * 100.0 / tries,5:F1}%）"
                + $"  {perCandidate,7:F3} ms/候选  {1000 / Math.Max(perCandidate, 1e-6),7:F0} 个/秒"
                + $"  正确密码{(keepsRight ? "留下" : "!!被误杀")}");
        }

        var path = ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        Say();
        Say("（报告已写入 " + path + "）");
    }

    /// <summary>最短命令连跑 N 次，报最好与中位。中位比平均抗一次抖动。</summary>
    private static async Task<string> ShortestAsync(string exe, params string[] args)
    {
        var samples = new List<double>();
        for (var i = 0; i < Calls; i++)
        {
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            var watch = Stopwatch.StartNew();
            using var process = Process.Start(start)!;

            // 必须并发排空两条管道。`7z i` 的输出有几十 KB，超过管道缓冲区：
            // 只等退出不读，子进程会卡在写输出上，父进程卡在等它退出。
            var drain = Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
            await drain;
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return $"最好 {samples[0],6:F1} ms  中位 {samples[samples.Count / 2],6:F1} ms";
    }

    /// <summary>一个候选一次引擎调用，取平均。</summary>
    private static async Task<double> MarginalAsync(SevenZipEngine engine, string archive, ArchiveInfo? info)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Calls; i++)
        {
            await engine.VerifyAsync(archive, $"{Wrong}-{i}", null, info);
        }

        watch.Stop();
        return watch.Elapsed.TotalMilliseconds / Calls;
    }

    /// <summary>
    /// 候选循环的最小复刻：按顺序试、命中即停、串行或按窗口 1→2→4 放大。
    /// 它与最终的 <c>Vault</c> 是两件事：这里只为量出「每个候选多贵」。
    /// </summary>
    private static async Task<(bool Hit, int Tries, double Seconds, double MsPerTry)> UnlockAsync(
        SevenZipEngine engine, string archive, IReadOnlyList<string> candidates, int workers,
        ArchiveInfo info, IFastPath? fastPath = null)
    {
        using var limiter = new SemaphoreSlim(workers);
        using var session = fastPath?.Open(archive);
        var tries = 0;
        var hit = false;
        var watch = Stopwatch.StartNew();

        async Task<bool> TryAsync(string candidate)
        {
            if (candidate.Length > 0
                && session is not null
                && session.Judge(candidate) is FastPathVerdict.ProbablyWrong)
            {
                return false;
            }

            await limiter.WaitAsync();
            try
            {
                return (await engine.VerifyAsync(archive, candidate, null, info)).Ok;
            }
            finally
            {
                limiter.Release();
            }
        }

        if (workers <= 1)
        {
            foreach (var candidate in candidates)
            {
                tries++;
                if (await TryAsync(candidate))
                {
                    hit = true;
                    break;
                }
            }
        }
        else
        {
            var width = 1;
            for (var at = 0; at < candidates.Count;)
            {
                var window = new List<Task<bool>>();
                for (var i = at; i < Math.Min(at + width, candidates.Count); i++)
                {
                    window.Add(TryAsync(candidates[i]));
                }

                foreach (var task in window)
                {
                    tries++;
                    if (await task)
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    break;
                }

                at += width;
                width = Math.Min(width * 2, workers);
            }
        }

        watch.Stop();
        var seconds = watch.Elapsed.TotalSeconds;
        return (hit, tries, seconds, seconds * 1000 / Math.Max(tries, 1));
    }

    private static List<string> Book(IEnumerable<string> passwords)
    {
        var list = new List<string>(passwords) { string.Empty };
        return list;
    }
}
