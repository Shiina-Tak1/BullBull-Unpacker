using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层 档：尾部 EOCD 反推循环要有命中上界与取消检查，同一文件不许跑第二遍载体检测。
///
/// 这一档的代价只由尾部窗口里有多少条 <c>PK\x05\x06</c> 决定：
/// 每条候选都要付一次窗口内回扫加一次尾部外读，所以铺满伪记录的文件能把一次识别拖成分钟级。
/// 样本就地生成，不入库：5 MiB 的文件正好把 4 MiB 窗口铺满约 19 万条记录。
/// </summary>
[Trait("Bug", "B-2026-199")]
public sealed class ProbeCarrierWorkTests
{
    /// <summary>耗时上限，秒。撤销命中上界并改回「每次命中开一份文件」时，这一档实测 31.6 秒
    /// （3 遍 × 190,650 条命中）；修后是 3 × 1024 条命中，同一份样本 0.1 秒量级。
    /// 阈值取在两者之间，两侧各留一个数量级以上。不引用审计报告那个倍数口径：核实结论判那次外推无效。</summary>
    private const double BudgetSeconds = 5.0;

    /// <summary>5 MiB 的文件里，窗口内每 22 字节一条 <c>PK\x05\x06</c>，两条字段都写 1
    /// （非 0 也非 0xFFFFFFFF），使每条候选都走到尾部外读那一档。</summary>
    private static string WriteSample(TestDirectory dir, string name, bool records)
    {
        var path = dir.Combine(name);
        var bytes = new byte[5 << 20];
        if (records)
        {
            var start = Math.Max(0, bytes.Length - Probe.EmbedTailBytes);
            for (var at = start; at + 22 <= bytes.Length; at += 22)
            {
                "PK\x05\x06"u8.CopyTo(bytes.AsSpan(at));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12, 4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 16, 4), 1);
            }
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>命中条数有上界：铺满伪记录的样本也只在 3 遍 × 上界之内。</summary>
    /// <remarks>
    /// 3 遍的来源：<c>FindEmbedded(deep:false)</c> 的 <c>ZipBaseInTail</c>、<c>HasZipAbsoluteLayout</c>、
    /// <c>FindEmbedded(deep:true)</c> 的 <c>ZipBaseInTail</c>。遍数变了这条就该红 —— 它是代价的判决式。
    /// </remarks>
    [Fact]
    public void EocdCandidates_MustStayUnderThePerPassLimit()
    {
        using var dir = new TestDirectory();
        var counters = new ProbeCounters();
        var amplified = WriteSample(dir, "amplified.mp4", records: true);

        var classified = Probe.Classify(amplified, new ProbeOptions { Deep = true, Counters = counters });

        Assert.Equal(ProbeStatus.NotArchive, classified.Status);
        Assert.Equal(3 * Probe.EocdHitLimit, counters.EocdHits);

        // 对照：同样大小、不含伪记录的文件一条候选都不该认。
        var cheap = new ProbeCounters();
        var control = WriteSample(dir, "control.mp4", records: false);
        Assert.Equal(ProbeStatus.NotArchive, Probe.Classify(control, new ProbeOptions { Deep = true, Counters = cheap }).Status);
        Assert.Equal(0, cheap.EocdHits);
    }

    /// <summary>取消之后这条路径一条候选都不认，也就是立刻回来。</summary>
    /// <remarks>「在 1 秒内返回」不测挂钟：这一档的耗时全在命中上，命中数为 0 即蕴含它没有继续跑。</remarks>
    [Fact]
    public void CancelledCarrierProbe_MustDoNoWorkAtAll()
    {
        using var dir = new TestDirectory();
        var amplified = WriteSample(dir, "amplified.mp4", records: true);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var counters = new ProbeCounters();
        var classified = Probe.Classify(
            amplified,
            new ProbeOptions { Deep = true, Cancellation = cancelled.Token, Counters = counters });

        Assert.Equal(ProbeStatus.NotArchive, classified.Status);
        Assert.Equal(0, counters.EocdHits);
    }

    /// <summary>真实耗时：铺满伪记录的 5 MiB 文件识别一次不超过 <see cref="BudgetSeconds"/> 秒。</summary>
    /// <remarks>真机档：量挂钟，由作者在正常终端跑；沙箱里的数没有判决力。</remarks>
    [Fact]
    [Trait("Category", "machine")]
    public void AmplifiedEocdFile_MustClassifyWithinTheBudget()
    {
        using var dir = new TestDirectory();
        var amplified = WriteSample(dir, "amplified.mp4", records: true);

        var clock = Stopwatch.StartNew();
        var classified = Probe.Classify(amplified, new ProbeOptions { Deep = true });
        clock.Stop();

        Assert.Equal(ProbeStatus.NotArchive, classified.Status);
        Assert.True(
            clock.Elapsed.TotalSeconds < BudgetSeconds,
            $"铺满伪记录的样本用时 {clock.Elapsed.TotalSeconds:N1} 秒，上限 {BudgetSeconds:N1} 秒");

        // 对照：同样大小、不含伪记录的文件照旧快。
        var control = WriteSample(dir, "control.mp4", records: false);
        var controlClock = Stopwatch.StartNew();
        Probe.Classify(control, new ProbeOptions { Deep = true });
        controlClock.Stop();

        Assert.True(
            controlClock.Elapsed.TotalSeconds < BudgetSeconds,
            $"对照样本用时 {controlClock.Elapsed.TotalSeconds:N1} 秒，上限 {BudgetSeconds:N1} 秒");
    }
}
