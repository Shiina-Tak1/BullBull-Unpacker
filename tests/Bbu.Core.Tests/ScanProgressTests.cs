using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 扫描期的实时进度：已扫目录数与已扫文件数一路传到界面。
/// 判据是「扫描一开始就有一条」与「两个数只增不减」—— 扫描是枚举、没有总数，给不了百分比。
/// </summary>
public sealed class ScanProgressTests
{
    /// <summary>一层套两层各放一个非压缩包：目录数与文件数都要数到本层与子目录两段。</summary>
    [Fact, Trait("Bug", "B-2026-176")]
    public void Scan_MustReportDirectoriesAndFiles()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("tree");
        var inner = Path.Combine(root, "inner");
        var deep = Path.Combine(inner, "deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(root, "root.txt"), "不是压缩包");
        File.WriteAllText(Path.Combine(inner, "a.txt"), "不是压缩包");
        File.WriteAllText(Path.Combine(deep, "b.txt"), "不是压缩包");

        var log = new ChannelLog((_, _) => { });
        Scan.Run([root], new ScanOptions { Log = log });
        var facts = log.ProgressFacts;

        Assert.NotEmpty(facts);
        Assert.All(facts, fact => Assert.Equal(ProgressStage.Scan, fact.Stage));

        // 第一项先报零：界面立刻从「准备中」翻成扫描进度，不必等第一个目录扫完。
        Assert.Equal(0, facts[0].Tried);
        Assert.Equal(0, facts[0].Total);

        // 两个数都只增不减：界面上的数只许往前走（答「继续」接着扫、换下一个根时也不许回头）。
        Assert.Equal(facts.Select(fact => fact.Tried).OrderBy(value => value), facts.Select(fact => fact.Tried));
        Assert.Equal(facts.Select(fact => fact.Total).OrderBy(value => value), facts.Select(fact => fact.Total));

        // 收尾那一条把两段补齐：本层一个目录一个文件，子目录两层各一个文件。
        var last = facts[^1];
        Assert.Equal(3, last.Tried);
        Assert.Equal(3, last.Total);
    }

    /// <summary>
    /// 拖两个文件夹时第二个根不回退：第一个根扫完的数要先并进本层，再叠第二个根自己的计数
    /// （B-2026-176）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-176")]
    public void Scan_MustNotRewindBetweenRoots()
    {
        using var tmp = new TestDirectory();
        var first = ManyFiles(tmp.Combine("first"), 40);
        var second = ManyFiles(tmp.Combine("second"), 5);

        var log = new ChannelLog((_, _) => { });
        Scan.Run([first, second], new ScanOptions { Log = log });
        var facts = log.ProgressFacts;

        Assert.Equal(facts.Select(fact => fact.Tried).OrderBy(value => value), facts.Select(fact => fact.Tried));
        Assert.Equal(facts.Select(fact => fact.Total).OrderBy(value => value), facts.Select(fact => fact.Total));
        Assert.Equal(45, facts[^1].Total);
    }

    /// <summary>
    /// 本层探测期间点「停止」要立刻停：几百个文件的目录在本层逐个读头、大的还要全盘扫，
    /// 停不下来就是几十秒不响应（真机反馈， B-2026-182）。
    /// 判据是「已扫文件数停在中途」—— 没有那处取消检查时会一路数到 100。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-182")]
    public void Scan_MustStopDuringTheTopLevelPass()
    {
        using var tmp = new TestDirectory();
        var root = ManyFiles(tmp.Combine("many-files"), 100);

        using var cancellation = new CancellationTokenSource();
        // 收到第 64 个文件那一条就取消，等于用户在本层扫到一半时按下了停止。
        var log = new ChannelLog((_, _) => { }, progress =>
        {
            if (progress.Total >= 64)
            {
                cancellation.Cancel();
            }
        });
        Scan.Run([root], new ScanOptions
        {
            Cancellation = cancellation.Token,
            Log = log,
        });
        var facts = log.ProgressFacts;

        Assert.NotEmpty(facts);
        Assert.True(facts[^1].Total < 100, $"本层没有停：已扫 {facts[^1].Total} 个文件");
    }

    /// <summary>造一个只放非压缩包的目录，返回它的路径。</summary>
    private static string ManyFiles(string root, int count)
    {
        Directory.CreateDirectory(root);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(Path.Combine(root, $"note-{index:D3}.txt"), "不是压缩包");
        }

        return root;
    }

    /// <summary>单个文件也先报一条零：扫描期活动槽上写的必须是扫描，而不是那个短命的「准备中」。</summary>
    [Fact, Trait("Bug", "B-2026-176")]
    public void Scan_MustReportBeforeTheFirstPath()
    {
        using var tmp = new TestDirectory();
        var file = tmp.Combine("readme.txt");
        File.WriteAllText(file, "不是压缩包");

        var log = new ChannelLog((_, _) => { });
        Scan.Run([file], new ScanOptions { Log = log });
        var facts = log.ProgressFacts;

        var fact = Assert.Single(facts);
        Assert.Equal(0, fact.Tried);
        Assert.Equal(0, fact.Total);
    }
}
