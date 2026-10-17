using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Configuration;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 扫描：单文件入口与目录入口各自的清单形状与备注。
///
/// 「哪个文件算一个包」与「往下找」两件事都走识别层那一份共享实现 ——
/// 界面列出来的和实际会解的不是同一批的话，用户报的就是「显示完成、包还在原地」。
/// </summary>
public sealed class ScanTests
{
    /// <summary>单个包拖进来是一行，而且纠正到主卷。</summary>
    [Fact]
    public void SinglePackage_MustProduceOneRunnableRow()
    {
        var items = Scan.Run([Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip")]);

        Assert.Single(items);
        Assert.True(items[0].Runnable);
        Assert.Equal("outer.zip", items[0].DisplayName);
        Assert.Equal(JobState.Queued, items[0].State);
    }

    /// <summary>文件夹是一行汇总加每个包一行，汇总行不参与执行。</summary>
    [Fact]
    public void Folder_MustProduceASummaryRowPlusOneRowPerPackage()
    {
        using var temp = new TestDirectory();
        var sample = CopySample(temp, "same-level");

        var items = Scan.Run([sample]);

        Assert.True(items[0].IsDir);
        Assert.False(items[0].Runnable);
        Assert.Equal(5, items[0].ChildCount);
        Assert.Equal(5, items[0].Children.Count);
        // 四个包（a / b / same1 / same2）加一个坏包。
        Assert.Equal(5, items.Count(item => item.Runnable));
        Assert.All(items.Skip(1), item => Assert.True(item.Indent));
    }

    /// <summary>
    /// 同一个包被两条路径到达时只列一行：拖进来一组分卷，每一卷都会解析成同一个主卷，
    /// 不去重就会生成四个一样的任务、把同一个包解四遍。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-086"), Trait("Category", "ledger")]
    public void SamePackageViaTwoPaths_MustBeListedOnce()
    {
        using var temp = new TestDirectory();
        var sample = CopySample(temp, "chain-1");
        var first = Path.Combine(sample, "outer.zip");
        // 第二条路径指向同一个文件：8.3 短名与硬链接都能做到，这里用一个等价写法。
        var parent = Path.Combine(sample, ".", "outer.zip");

        var items = Scan.Run([first, parent]);

        Assert.Single(items);
    }

    /// <summary>文件夹里真的没有包时只写那一句；有包时不许写它。</summary>
    [Fact]
    [Trait("Bug", "B-2026-044"), Trait("Category", "ledger")]
    public void FolderNote_MustNotClaimThereIsNoPackageWhenThereIsOne()
    {
        using var temp = new TestDirectory();
        var withPackage = CopySample(temp, "chain-1");

        var items = Scan.Run([withPackage]);

        Assert.DoesNotContain(items, item => item.Reason is not null
            && item.Reason.Contains(ScanText.NoteNoArchive, StringComparison.Ordinal));

        var empty = temp.Combine("empty");
        Directory.CreateDirectory(empty);
        var emptyItems = Scan.Run([empty]);
        Assert.Contains(emptyItems, item => item.Reason is not null
            && item.Reason.Contains(ScanText.NoteNoArchive, StringComparison.Ordinal));
    }

    /// <summary>
    /// 拖父目录与子目录时，子目录里那些包已经被父项那一次递归扫描列进清单：
    /// 子目录不该再多出一行假的「文件夹里没有可解压的压缩包」，也不该多出一条可执行任务。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-091"), Trait("Category", "ledger")]
    public void ParentAndChildFolders_MustNotProduceADuplicateTask()
    {
        using var temp = new TestDirectory();
        var parent = temp.Combine("parent");
        var child = Path.Combine(parent, "sub");
        Directory.CreateDirectory(child);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(child, "b.zip"));

        var items = Scan.Run([parent, child]);

        Assert.DoesNotContain(items, item => item.Reason is not null
            && item.Reason.Contains(ScanText.NoteNoArchive, StringComparison.Ordinal));
        Assert.Single(items, item => item.Runnable);
        Assert.Equal("b.zip", items.Single(item => item.Runnable).DisplayName);
    }

    /// <summary>排除名单里的类型仍然列出来，但标明不处理。</summary>
    [Fact]
    [Trait("Bug", "B-2026-041"), Trait("Category", "ledger")]
    public void ExcludedExtension_MustBeListedButNotRunnable()
    {
        using var temp = new TestDirectory();
        var sample = temp.Combine("apk");
        Directory.CreateDirectory(sample);
        File.Copy(Path.Combine(TestPaths.PierceRoot, "chain-1", "outer.zip"),
            Path.Combine(sample, "x.apk"));

        var items = Scan.Run([sample], new ScanOptions { ExcludeExts = ["apk"] });

        var row = items.Single(item => item.DisplayName == "x.apk");
        Assert.False(row.Runnable);
        Assert.Contains("APK", row.Kind, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「往下找」不为子目录里那批文件付内嵌探测的代价：实测解压产物上成百上千次探测、报备产出零。
    /// 本层仍付探测 —— 用户直接拖进来的那批伪装包由它认出。
    /// </summary>
    [Fact]
    [Trait("Category", "edge")]
    public void Subfolders_MustNotPayForEmbeddedProbing()
    {
        using var temp = new TestDirectory();
        var root = temp.Combine("root");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.Copy(TestPaths.Probe(Path.Combine("carrier-tail", "x.mp4")), Path.Combine(sub, "x.mp4"));

        // 判据层：扫描侧给「往下找」的那份声明，一次尾部探测都不许有。
        var counters = new ProbeCounters();
        var search = Probe.FindArchivesBelow(
            root, new ProbeOptions { Deep = false, Carriers = false, Counters = counters });
        Assert.Equal(0, counters.TailProbes);
        Assert.Empty(search.Carriers);

        // 行为层：子目录里那个伪装包不再进"疑似藏包"的报备。
        var items = Scan.Run([root]);
        Assert.DoesNotContain(items, item => item.Reason is not null
            && item.Reason.Contains("疑似藏了内嵌", StringComparison.Ordinal));
    }

    /// <summary>
    /// 扫描范围三档：只扫这一层不进子目录；往下找（默认）只找现成的包、不为子目录里
    /// "藏没藏包"读尾部；深挖内嵌那一档才把报备也收上来。
    /// </summary>
    [Fact]
    [Trait("Category", "edge")]
    public void FolderScan_MustHaveThreeDistinctTiers()
    {
        using var temp = new TestDirectory();
        var root = temp.Combine("root");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(sub, "inner.zip"));
        File.Copy(TestPaths.Probe(Path.Combine("carrier-tail", "x.mp4")), Path.Combine(sub, "x.mp4"));

        // 只扫这一层：子目录里的包一行都不出。
        var topOnly = Scan.Run([root], new ScanOptions { FolderScan = FolderScan.TopOnly });
        Assert.DoesNotContain(topOnly, item => item.DisplayName == "inner.zip");

        // 往下找（默认）：子目录里的现成包列出来，但那个伪装包不进报备。
        var below = Scan.Run([root]);
        Assert.Contains(below, item => item.DisplayName == "inner.zip");
        Assert.DoesNotContain(below, item => item.Reason is not null
            && item.Reason.Contains("疑似藏了内嵌", StringComparison.Ordinal));

        // 深挖内嵌：子目录里那个伪装包才进报备。
        var deep = Scan.Run([root], new ScanOptions { FolderScan = FolderScan.Deep });
        Assert.Contains(deep, item => item.Reason is not null
            && item.Reason.Contains("疑似藏了内嵌", StringComparison.Ordinal));
    }

    /// <summary>取消之后不再展开后面的路径。</summary>
    [Fact]
    [Trait("Bug", "B-2026-081"), Trait("Category", "ledger")]
    public void Cancelled_MustStopExpanding()
    {
        using var temp = new TestDirectory();
        var sample = CopySample(temp, "chain-1");

        var items = Scan.Run([sample, sample], new ScanOptions { Cancellation = CancelledToken() });

        Assert.Empty(items);
    }

    /// <summary>既不是压缩包、名字也不像压缩包的文件：列出来但标成不可执行。</summary>
    [Fact]
    public void NotAnArchive_MustBeListedButNotRunnable()
    {
        using var temp = new TestDirectory();
        var file = temp.Combine("note.txt");
        File.WriteAllText(file, "这不是压缩包");

        var items = Scan.Run([file]);

        Assert.Single(items);
        Assert.False(items[0].Runnable);
        Assert.Contains(ScanText.NoteNotArchive, items[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>文件夹里没有任何压缩包时仍然生成一行可执行项，交给穿透自己去找。</summary>
    [Fact]
    public void FolderWithoutPackages_MustStillProduceARunnableRow()
    {
        using var temp = new TestDirectory();
        var empty = temp.Combine("empty");
        Directory.CreateDirectory(empty);

        var items = Scan.Run([empty]);

        Assert.Single(items);
        Assert.True(items[0].IsDir);
        Assert.True(items[0].Runnable);
    }

    private static string CopySample(TestDirectory temp, string sample)
    {
        var source = Path.Combine(TestPaths.PierceRoot, sample);
        var target = temp.Combine(sample);
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

    private static System.Threading.CancellationToken CancelledToken()
    {
        var source = new System.Threading.CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }
}
