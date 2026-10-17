using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Bbu.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Bbu.Core.Tests;

/// <summary>
/// 真机档：热路径那两处「每次都分配」的测量（欠账表 debt 15）。
///
/// 量两处：深扫那条路的 <c>ScanMagic</c>（本批之前每轮 <c>new byte[8MB]</c> 加一份同长的 <c>data</c>，
/// 都是 LOH 分配；现在只剩一块固定窗口），以及 <c>7z.dll</c> 虚拟流的 <c>Read</c>
/// （本批之前每次 <c>new byte[min(want, 64KB)]</c>，Gen0；现在复用字段缓冲）。
/// 判据是同一路径同一样本的总分配字节数降幅与墙钟不退化，门槛与结论见交接目录
/// <c>14-BBU-债务清理与流水线-已完成\04-已完成-代码形态清理\04-评估.md</c>。
/// 跑一次打印全部数字，报告落 <c>.scratch\perf\hotpath-alloc.txt</c>（<c>BBU_HOTPATH_ALLOC_OUT</c> 可改落点）。
/// </summary>
[Trait("Category", "machine")]
public sealed class HotPathAllocationMeasureTests
{
    /// <summary>深扫那条路的轮数：夹具 6.2 MB，每轮读一次整文件，50 轮的分配量足以看出降幅。</summary>
    private const int DeepRounds = 50;

    /// <summary>虚拟流的轮数：每次只读 64KB，单次差异小，所以轮数取大一些才有足够信号。</summary>
    private const int StreamRounds = 500;

    private readonly ITestOutputHelper _output;

    public HotPathAllocationMeasureTests(ITestOutputHelper output) => _output = output;

    internal static string ReportPath =>
        Environment.GetEnvironmentVariable("BBU_HOTPATH_ALLOC_OUT")
        ?? Path.Combine(TestPaths.Root, ".scratch", "perf", "hotpath-alloc.txt");

    [Fact]
    public void HotPath_AllocationMustBeMeasured()
    {
        var report = new StringBuilder();
        void Say(string text = "")
        {
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        Say("==============================================================================");
        Say($"热路径分配测量   C#   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"深扫 {DeepRounds} 轮、虚拟流 {StreamRounds} 轮，单线程；");
        Say("分配取 GC.GetAllocatedBytesForCurrentThread 的前后差（每处先预热一次）");
        Say("==============================================================================");

        MeasureDeepScan(Say);
        MeasureNativeStream(Say);

        var path = ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        Say("（报告已写入 " + path + "）");
    }

    /// <summary>深扫：本批之前每轮一块 8MB 的 <c>buffer</c> 加一份 carry 拼 read 的 <c>data</c>，现在只剩一块固定窗口。</summary>
    private static void MeasureDeepScan(Action<string> say)
    {
        var sample = TestPaths.Probe(Path.Combine("carrier-deep", "x.mp4"));
        if (!File.Exists(sample))
        {
            say("深扫          跳过：缺少夹具 probe\\carrier-deep\\x.mp4");
            return;
        }

        // 预热：把 JIT 与首次读盘排除在计数之外
        _ = Probe.FindEmbedded(sample, deep: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < DeepRounds; i++)
        {
            _ = Probe.FindEmbedded(sample, deep: true);
        }

        watch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        say($"深扫（样本 {new FileInfo(sample).Length / 1024.0 / 1024.0:F1} MB，每轮读满 EmbedChunkBytes）");
        say($"  分配 {bytes / 1024.0 / 1024.0,9:F1} MB   墙钟 {watch.Elapsed.TotalMilliseconds,8:F1} ms   " +
            $"每轮 {bytes / (double)DeepRounds / 1024,8:F0} KB");
    }

    /// <summary>7z.dll 的虚拟流：<c>Read</c> 每次 <c>new byte[min(want, 64KB)]</c>。</summary>
    private static void MeasureNativeStream(Action<string> say)
    {
        var sample = TestPaths.Probe(Path.Combine("carrier-deep", "x.mp4"));
        if (!File.Exists(sample))
        {
            say("虚拟流        跳过：缺少样本");
            return;
        }

        var stream = new NativeStreamServer();
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            stream.Bind([sample]);
            stream.Read(buffer, 64 * 1024, IntPtr.Zero);   // 预热

            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < StreamRounds; i++)
            {
                // Bind 复位读位置；卷集合没变，所以它只做复位、不重算卷大小
                stream.Bind([sample]);
                stream.Read(buffer, 64 * 1024, IntPtr.Zero);
            }

            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            say("7z.dll 虚拟流（每轮一次 64KB 读）");
            say($"  分配 {bytes / 1024.0 / 1024.0,9:F1} MB   墙钟 {watch.Elapsed.TotalMilliseconds,8:F1} ms   " +
                $"每次读 {bytes / (double)StreamRounds,8:F0} B");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            stream.Close();
        }
    }
}
