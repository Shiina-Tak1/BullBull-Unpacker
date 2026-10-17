using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Bbu.Core.Tests;

/// <summary>
/// 真机档：快路那两条路的并行归属测量。跑一次把整张表打出来。
///
/// 要回答的问题只有一个：**每线程各持一个 <c>IInArchive</c> 能不能并行**，
/// 还是 dll 内部那把 KDF 缓存锁把路堵死了。判据是「平均核数」= 进程 CPU 时间 ÷ 墙钟，
/// 与同一次运行内的 zip（进程内 PBKDF2，已知能并行）对照。每候选换新密码，避开 dll 的 KDF 缓存。
///
/// 之后 7z 那一格多一行「纯托管」—— 那是产品实际走的路；dll 那两行留作对照，
/// 三行的核数差就是「那把锁拿掉之后多核能不能用上」的答案。
/// </summary>
[Trait("Category", "machine")]
public sealed class FastPathConcurrencyMeasureTests
{
    /// <summary>取 64 而不是十几：zip 每候选只要零点几毫秒，样本过小测不出并行。</summary>
    private const int PerThread = 64;

    private static readonly int[] Widths = [1, 4, 16];

    private readonly ITestOutputHelper _output;

    public FastPathConcurrencyMeasureTests(ITestOutputHelper output) => _output = output;

    internal static string ReportPath =>
        Environment.GetEnvironmentVariable("BBU_FASTPATH_CONCURRENCY_OUT")
        ?? Path.Combine(TestPaths.Root, ".scratch", "perf", "fastpath-concurrency.txt");

    [Fact]
    public void FastPath_ConcurrencyMustBeMeasured()
    {
        // 守卫是进程级状态：同进程里别的用例可能已经触发它（真机档存在这种情况），
        // 而这里量的是这条路本身的表现，所以先复位再量。
        FastPathFailureGuard.Reset();

        var zip = Path.Combine(TestPaths.Fixtures, "vault", "zip-aes256.zip");
        var seven = Path.Combine(TestPaths.Fixtures, "vault", "seven-plain.7z");
        if (!File.Exists(zip) || !File.Exists(seven))
        {
            _output.WriteLine("缺少密码层夹具");
            return;
        }

        // 先探一次会话：失败时跳过 7z 那几路，不把 null 往下传
        var sevenReady = SevenZipFastPathSession.Open(seven) is not null;
        // 托管那一路不吃 dll，所以它有自己的「开不开得了庭」。
        var managedReady = new CompositeFastPath().Open(seven) is not null;

        var report = new StringBuilder();
        void Say(string text = "")
        {
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        Say("==============================================================================");
        Say($"快路并发归属测量   C#   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"机器 {Environment.ProcessorCount} 逻辑核   每线程 {PerThread} 个候选（每候选换新密码）");
        Say("==============================================================================");
        // 分派改过之后这一行会跟着变，所以把它打出来：报告自己说明量的是哪条路。
        using (var probe = new CompositeFastPath().Open(seven))
        {
            Say($"7z 复合入口实际开的会话：{probe?.GetType().Name ?? "(整轮不进快路)"}");
        }

        Say();
        Say("  路                                      线程    墙钟 s    ms/候选   平均核数");

        var holders = new List<IDisposable>();
        foreach (var width in Widths)
        {
            var zipRow = Run(width, PerThreadZip(zip));
            Say($"  zip AES-256（进程内 PBKDF2，对照）      {width,3}  {zipRow.Seconds,8:F2}  {zipRow.Ms,9:F2}  {zipRow.Cores,9:F2}");
        }

        if (managedReady)
        {
            foreach (var width in Widths)
            {
                var row = Run(width, PerThreadSevenComposite(seven));
                Say($"  7z 明文头（纯托管：产品实际走的）       {width,3}  {row.Seconds,8:F2}  {row.Ms,9:F2}  {row.Cores,9:F2}");
            }
        }
        else
        {
            Say("  7z 明文头（纯托管：产品实际走的）       跳过：这一次复合入口没开庭");
        }

        if (sevenReady)
        {
            foreach (var width in Widths)
            {
                var row = Run(width, PerThreadSevenSession(seven));
                Say($"  7z 明文头（会话那一把锁）               {width,3}  {row.Seconds,8:F2}  {row.Ms,9:F2}  {row.Cores,9:F2}");
            }
        }
        else
        {
            Say("  7z 明文头（会话那一把锁）              跳过：这一次拿不到快路会话");
        }

        if (sevenReady)
        {
            foreach (var width in Widths)
            {
                var row = Run(width, PerThreadSevenArchive(seven, holders));
                Say($"  7z 明文头（每线程一个 IInArchive）      {width,3}  {row.Seconds,8:F2}  {row.Ms,9:F2}  {row.Cores,9:F2}");
            }
        }
        else
        {
            Say("  7z 明文头（每线程一个 IInArchive）     跳过：这一次拿不到快路会话");
        }

        foreach (var holder in holders)
        {
            holder.Dispose();
        }

        Say();
        Say("读法：核数接近 1 说明这条路在串行；zip 那一列是「这台机器与这套测量能并行」的对照。");
        var path = ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        Say("（报告已写入 " + path + "）");
    }

    private static Func<int, Func<string, int>> PerThreadZip(string path)
    {
        var session = new CompositeFastPath().Open(path);
        return _ => password => session is null ? -1 : (int)session.Judge(password);
    }

    /// <summary>
    /// 7z 那一段**产品实际走的**路：<see cref="CompositeFastPath"/> 对 7z 现在先开托管会话，
    /// 判不了才回退 dll。走复合入口而不是直接开托管会话，是为了让这一行量到的就是产品路径；
    /// 分派被改动时报告头部那行「实际开的会话」会跟着变，不会静默改掉这一行的含义。
    /// </summary>
    private static Func<int, Func<string, int>> PerThreadSevenComposite(string path)
    {
        var session = new CompositeFastPath().Open(path);
        return _ => password => session is null ? -1 : (int)session.Judge(password);
    }

    private static Func<int, Func<string, int>> PerThreadSevenSession(string path)
    {
        var session = SevenZipFastPathSession.Open(path);
        return _ => password => session is null ? -1 : (int)session.Judge(password);
    }

    private static Func<int, Func<string, int>> PerThreadSevenArchive(string path, List<IDisposable> holders)
    {
        var host = SevenZipHost.Acquire(null);
        return _ =>
        {
            var archive = host is null ? null : SevenZipArchive.Create(host, SevenZipArchive.Volumes(path));
            if (archive is not null)
            {
                holders.Add(archive);
            }

            return password => archive is null ? -1 : (archive.Verdict(password) ? 1 : 0);
        };
    }

    /// <summary>每个线程拿到一个「试一个密码」的动作。返回墙钟、平均核数与每候选毫秒。</summary>
    private static (double Seconds, double Cores, double Ms) Run(int threads, Func<int, Func<string, int>> perThread)
    {
        var startCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var index = t;
            tasks[t] = Task.Run(() =>
            {
                var tryOne = perThread(index);
                for (var i = 0; i < PerThread; i++)
                {
                    tryOne($"pw-{index * PerThread + i:0000}");
                }
            });
        }

        Task.WaitAll(tasks);
        watch.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - startCpu;
        var seconds = watch.Elapsed.TotalSeconds;
        return (seconds, cpu.TotalSeconds / seconds, seconds * 1000 / (threads * PerThread));
    }
}
