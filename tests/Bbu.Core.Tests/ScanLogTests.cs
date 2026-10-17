using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 扫描期的日志粒度：按被探测的每个文件写，扫一个普通目录会刷出上万条「不是压缩包」
/// （B-2026-108）；粒度改成「进清单的项」之后，条数与清单项数同阶。
///
/// 三条断言钉三件事：条数与清单项数同阶、名单里的类型不出现在日志里、清单项该说的那句还在；
/// 另外「往下找」那一条判的是**条数与那一句的原文**，不判子串（B-2026-108）。
/// </summary>
public sealed class ScanLogTests
{
    /// <summary>一大堆非压缩包加少量真包：日志条数不许跟着被探测的文件数走。</summary>
    [Fact]
    [Trait("Bug", "B-2026-108")]
    public void ScanLog_MustBeSameOrderAsTheList()
    {
        using var tmp = new TestDirectory();
        var folder = tmp.Combine("heap");
        Directory.CreateDirectory(folder);
        Heap(folder, 60);
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(folder, "样本.zip"));

        var logs = new List<(LogLevel Level, string Text)>();
        var jobs = Scan.Run(
            [folder],
            new ScanOptions { Log = new ChannelLog((level, text) => logs.Add((level, text))) });

        Assert.Contains(jobs, job => job.DisplayName == "样本.zip");
        Assert.True(
            logs.Count <= jobs.Count + 1,
            $"日志 {logs.Count} 条对清单 {jobs.Count} 项：粒度又回到按被探测的每个文件写了");
    }

    /// <summary>
    /// 往下找同样不许逐目录说：60 个非压缩包只出**一条**整批汇总，且那一条的原文就是汇总句。
    /// 判条数与原文而不是判子串 —— 汇总句里也有「不是压缩包」这几个字，子串判法分不出
    /// 「汇总一条」与「逐目录刷屏」（B-2026-108）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-108")]
    [Trait("Bug", "B-2026-177")]
    public void Walking_MustNotLogEveryNonArchiveFileBelowTheFolder()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("walk");
        var inner = Path.Combine(root, "inner");
        Directory.CreateDirectory(inner);
        Heap(inner, 60);
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(inner, "样本.zip"));

        var logs = new List<(LogLevel Level, string Text)>();
        var search = Probe.FindArchivesBelow(
            root,
            new ProbeOptions { Log = new ChannelLog((level, text) => logs.Add((level, text))) });

        Assert.Single(search.Found);
        Assert.Equal(60, search.SkippedNotArchive);

        var line = Assert.Single(logs);
        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, ProbeText.LogSkippedNotArchiveAt, 60),
            line.Text);
    }

    /// <summary>名单里的类型不探测、也不进日志：清单上已经写明它不会解。</summary>
    [Fact]
    [Trait("Bug", "B-2026-108")]
    public void ExcludedFile_MustNotBeProbedOrLogged()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("excluded");
        var inner = Path.Combine(root, "inner");
        Directory.CreateDirectory(inner);
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(inner, "样本.zip"));
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(inner, "报告.docx"));

        var logs = new List<(LogLevel Level, string Text)>();
        var search = Probe.FindArchivesBelow(
            root,
            new ProbeOptions { Log = new ChannelLog((level, text) => logs.Add((level, text))) },
            excludeExts: ["docx"]);

        Assert.Single(search.Found);
        Assert.DoesNotContain(search.Results, item => item.Path.EndsWith("报告.docx", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Text.Contains("docx", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 收敛之后清单项该说的那句仍在：拖进来一个不是压缩包的文件，日志里要有它一条。
    /// 这一条拦的是「把日志删干净」这种反向改法。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-108")]
    public void SingleNonArchiveFile_MustStillSayWhyItIsNotHandled()
    {
        using var tmp = new TestDirectory();
        var file = tmp.Combine("readme.txt");
        File.WriteAllText(file, "不是压缩包");

        var logs = new List<(LogLevel Level, string Text)>();
        var jobs = Scan.Run([file], new ScanOptions { Log = new ChannelLog((level, text) => logs.Add((level, text))) });

        var job = Assert.Single(jobs);
        Assert.False(job.Runnable);
        var line = Assert.Single(logs);
        Assert.Contains("readme.txt", line.Text, StringComparison.Ordinal);
    }

    /// <summary>一堆非压缩包文件：内容任意，识别层按头与扩展名都判得出它们不是包。</summary>
    private static void Heap(string folder, int count)
    {
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(
                Path.Combine(folder, $"note-{index:D2}.txt"),
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"不是压缩包 {index}"));
        }
    }
}
