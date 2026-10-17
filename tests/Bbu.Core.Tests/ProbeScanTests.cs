using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层的扫描：单文件入口与目录入口必须给出同一份结论，往下找能被打断、能如实说没找完。
///
/// 这一组里"结论一致"是结构性成立的：两个入口都调 <see cref="Probe.Classify"/>，
/// 所以断言不是比字符串，而是比逐字段的结论。
/// </summary>
public sealed class ProbeScanTests
{
    /// <summary>夹具树里除了分卷成员之外的全部文件。分卷成员由归组负责，不在逐个识别的范围里。</summary>
    private static IReadOnlyList<string> PlainFiles() =>
        Directory.EnumerateFiles(TestPaths.ProbeRoot, "*", SearchOption.AllDirectories)
            .Where(path => Probe.ClassifyVolume(path).Kind is VolKind.None)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 判据 9 与 B-2026-060 / 078：同一批文件，单文件入口与目录入口的结论逐字段相同。
    ///
    /// 以前两个入口各写一份判据：单独拖一个 `.cab` 能解，放进文件夹就静默漏解还报"完成"。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-060")]
    [Trait("Bug", "B-2026-078")]
    public void SingleAndDirectoryEntries_MustAgreeFieldByField()
    {
        var expected = PlainFiles();
        Assert.True(expected.Count >= 12, "夹具树太小，这条断言会变成空转：" + expected.Count);

        var search = Probe.FindArchivesBelow(TestPaths.ProbeRoot);
        var byPath = search.Results.ToDictionary(item => item.Path, StringComparer.Ordinal);

        Assert.False(search.Cancelled);
        Assert.False(search.Truncated);

        foreach (var path in expected)
        {
            Assert.True(byPath.TryGetValue(path, out var fromDirectory), "目录入口漏了这个文件：" + path);
            var single = Probe.Classify(path);

            Assert.Equal(single.Status, fromDirectory.Status);
            Assert.Equal(single.Format, fromDirectory.Format);
            Assert.Equal(single.VolKind, fromDirectory.VolKind);
            Assert.Equal(single.Note, fromDirectory.Note);
            Assert.Equal(single.Carrier.Kind, fromDirectory.Carrier.Kind);
            Assert.Equal(single.Carrier.Embedded?.Offset, fromDirectory.Carrier.Embedded?.Offset);
        }
    }

    /// <summary>
    /// 判据 15 与 B-2026-076：子目录里的伪装包必须被发现。
    ///
    /// 只按"标准候选"找的话，子目录里只有一个马甲文件时两边都不管：候选单是空的、
    /// 往下找也说没有，最后报"文件夹里已没有可解压的压缩包"，而里面的东西一个字节都没解出来。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-076")]
    public void SubdirectoryCarrier_MustBeReported()
    {
        var search = Probe.FindArchivesBelow(TestPaths.ProbeRoot);
        var carrier = TestPaths.Probe(Path.Combine("carrier-tail", "x.mp4"));

        Assert.Contains(carrier, search.Carriers);
        Assert.DoesNotContain(carrier, search.Found);

        // 反例：马甲不许混进"要解的那一批"，也不许被报成"不是压缩包、跳过"。
        var entry = Assert.Single(search.Results, item => item.Path == carrier);
        Assert.Equal(ProbeStatus.NotArchive, entry.Status);
        Assert.NotEqual(ProbeText.NotArchive, entry.Note);
        Assert.True(entry.ShallHandle);
    }

    /// <summary>
    /// 判据 10 与 B-2026-086 / 047：同一个物理文件被两条路径到达时只算一个包。
    ///
    /// 硬链接在仓里存不住（git 只存内容，clone 出来就是两份独立的文件），
    /// 所以这一对夹具在断言里现造。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-086")]
    [Trait("Bug", "B-2026-047")]
    public void SamePhysicalFile_MustCountOnce()
    {
        using var tmp = new TestDirectory();
        var folder = tmp.Combine("scan");
        // 两个名字都放在**子目录**里：往下找不扫传入目录自己的文件。
        var inner = Path.Combine(folder, "inner");
        Directory.CreateDirectory(inner);

        var pack = Path.Combine(inner, "pack.zip");
        File.Copy(TestPaths.Fixture("ok-small.zip"), pack);
        var alias = Path.Combine(inner, "alias.zip");
        Assert.True(CreateHardLink(alias, pack, IntPtr.Zero), "造不出硬链接，这条判据验不了");

        var search = Probe.FindArchivesBelow(folder);
        Assert.Single(search.Found);

        // 两个名字都能单独识别成候选：去重发生在"清单"这一层，不是识别层把其中一个当成包。
        Assert.Equal(ProbeStatus.Candidate, Probe.Classify(pack).Status);
        Assert.Equal(ProbeStatus.Candidate, Probe.Classify(alias).Status);
    }

    /// <summary>
    /// B-2026-047 的另一半：往下找不许跟着 junction 走，但必须把跳过这件事说出来。
    ///
    /// 不跟进去是对的（跟进去会绕圈、会把同一个包报很多次），一声不响是错的：
    /// 用户的目录树里真有 junction 时，里面的包就是不会被解。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-047")]
    public void Junction_MustNotBeFollowedAndMustBeAccountedFor()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("root");
        var outside = tmp.Combine("outside");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateDirectory(outside);
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(root, "real", "pack.zip"));
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(outside, "hidden.zip"));

        var link = Path.Combine(root, "link");
        if (!CreateJunction(link, outside))
        {
            // 造不出 junction 的机器上不静默通过：显式报出来，免得这条判据变成摆设。
            throw new InvalidOperationException("造不出 junction：" + link);
        }

        var logs = new List<(LogLevel Level, string Text)>();
        var search = Probe.FindArchivesBelow(
            root,
            new ProbeOptions { Log = new ChannelLog((level, message) => logs.Add((level, message))) });

        var found = Assert.Single(search.Found);
        Assert.Equal(Path.Combine(root, "real", "pack.zip"), found);
        Assert.False(search.Truncated);
        var noted = Assert.Single(logs, line => line.Text.Contains("junction", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warn, noted.Level);
    }

    /// <summary>
    /// （B-2026-045）往下找要能被"停止"打断，而且取消不是"没找完"。
    ///
    /// 取消的时刻从**进度回传**里给：界面上看得到的进度在走，就说明遍历正在跑，此时点停止才是真的"走到一半被叫停"。
    /// 扳机绑在进度回传上是有意的：回传按每 32 个目录、或距上一次 200 毫秒节流，遍历结束还会补一次，
    /// 所以夹具摆 128 个目录。目录太少时那唯一一次回传发生在遍历结束之后，取消已经来不及（见欠账表 `debt 98`）。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-045")]
    public void Walking_MustBeCancellableAndTellCancelledApartFromTruncated()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        var progress = 0;
        var logs = new List<string>();

        var full = Probe.FindArchivesBelow(TestPaths.Probe("scan-cancel"));
        Assert.False(full.Cancelled);
        Assert.False(full.Truncated);
        Assert.Equal(128, full.ScannedDirs);

        var search = Probe.FindArchivesBelow(
            TestPaths.Probe("scan-cancel"),
            new ProbeOptions
            {
                Cancellation = cts.Token,
                OnScanProgress = (_, _) =>
                {
                    if (++progress == 1)
                    {
                        cts.Cancel();
                    }
                },
                Log = new ChannelLog((_, line) => logs.Add(line)),
            });

        Assert.True(search.Cancelled, "点了停止却没停下来：" + string.Join(" | ", logs));
        Assert.False(search.Truncated);
        Assert.InRange(search.ScannedDirs, 1, 127);
        Assert.Contains(logs, line => line.Contains("已停止", StringComparison.Ordinal));
    }

    /// <summary>
    /// B-2026-049：目录不在与目录读不了是两件事。
    ///
    /// 产物目录被删掉时，"没有东西可找"已经说清了结论；那时报"子文件夹扫描未完成"
    /// 会让用户去找一个不存在的子文件夹。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-049")]
    public void MissingDirectory_IsNotATruncatedScan()
    {
        using var tmp = new TestDirectory();

        var missing = Probe.FindArchivesBelow(tmp.Combine("not-there"));
        Assert.Empty(missing.Found);
        Assert.False(missing.Truncated);
        Assert.Empty(missing.Unreadable);
        Assert.Equal(0, missing.ScannedDirs);

        // 传进来一个文件路径也一样：不抛异常、不报"没找完"。
        var file = tmp.Combine("a-file.zip");
        File.Copy(TestPaths.Fixture("ok-small.zip"), file);
        var notAFolder = Probe.FindArchivesBelow(file);
        Assert.Empty(notAFolder.Found);
        Assert.False(notAFolder.Truncated);
    }

    /// <summary>
    /// 两道保险丝（目录数 / 文件数）烧到时都必须如实说"没找完"，不许报成"没有可解压的压缩包"。
    ///
    /// 上限按目录数与文件数算而不按秒：秒是设备相关的，同一批文件在机械盘上慢十几倍，
    /// 拿秒当上限只会让清单在慢机上被截断。
    /// </summary>
    [Fact]
    [Trait("Category", "edge")]
    public void Fuse_MustReportAnIncompleteSearch()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("many");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 4; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, "d" + index.ToString("D1", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var logs = new List<(LogLevel Level, string Text)>();
        var byDir = Probe.FindArchivesBelow(
            root,
            new ProbeOptions { MaxDirs = 3, Log = new ChannelLog((level, message) => logs.Add((level, message))) });

        Assert.True(byDir.Truncated);
        Assert.False(byDir.Cancelled);
        Assert.Equal(3, byDir.ScannedDirs);
        var noted = Assert.Single(logs, line => line.Text.Contains("子文件夹扫描未完成", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warn, noted.Level);

        // 文件数那一道：一个子目录里放 5 个文件、上限 3 —— 扫完它就该停（本层自己不算，故仍要有第二个目录）。
        var many = Path.Combine(root, "files");
        Directory.CreateDirectory(many);
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(
                Path.Combine(many, "f" + index.ToString("D1", System.Globalization.CultureInfo.InvariantCulture) + ".txt"),
                "x");
        }

        Directory.CreateDirectory(Path.Combine(root, "zz"));
        logs.Clear();
        var byFile = Probe.FindArchivesBelow(
            root,
            new ProbeOptions { MaxFiles = 3, Log = new ChannelLog((level, message) => logs.Add((level, message))) });

        Assert.True(byFile.Truncated);
        Assert.True(byFile.ScannedFiles >= 3);
        var byFileNote = Assert.Single(logs, line => line.Text.Contains("子文件夹扫描未完成", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warn, byFileNote.Level);
    }

    /// <summary>
    /// 欠账 39：子文件夹无法读取必须如实报"扫描尚未完成"，不许当成"里面没有包"。
    ///
    /// 造法是"独占打开那个子目录"（<c>FileShare.None</c> + <c>FILE_FLAG_BACKUP_SEMANTICS</c>）：
    /// 此后枚举它必抛 <c>IOException</c>，而 <c>Directory.Exists</c> 仍为真 —— 这正是
    /// "目录在、只是读不动"那一档。不需要权限或超长路径，沙箱里是确定的，释放句柄即恢复。
    /// </summary>
    [Fact]
    [Trait("Category", "edge")]
    public void SubdirectoryUnreadable_MustBeReportedAsIncomplete()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("root");
        var locked = Path.Combine(root, "locked");
        var open = Path.Combine(root, "open");
        Directory.CreateDirectory(locked);
        Directory.CreateDirectory(open);
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(locked, "hidden.zip"));
        File.Copy(TestPaths.Fixture("ok-small.zip"), Path.Combine(open, "visible.zip"));

        var logs = new List<(LogLevel Level, string Text)>();
        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, (FileOptions)0x02000000))
        {
            var search = Probe.FindArchivesBelow(
                root,
                new ProbeOptions { Log = new ChannelLog((level, message) => logs.Add((level, message))) });

            Assert.True(search.Truncated);
            var blocked = Assert.Single(search.Unreadable);
            Assert.Equal(locked, blocked);
            var noted = Assert.Single(logs, line => line.Text.Contains("无法读取子文件夹", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warn, noted.Level);

            // 读不了的那个目录里的包看不见，但另一个子目录里那个照旧找得到 —— 这两件事必须同时成立。
            var found = Assert.Single(search.Found);
            Assert.EndsWith("visible.zip", found, StringComparison.Ordinal);
        }

        // 释放句柄之后"读不了"就消失：证明造出来的那一档只持续到句柄释放。
        Assert.False(Probe.FindArchivesBelow(root).Truncated);
    }

    /// <summary>
    /// B-2026-041 / 035：子目录里的普通包不许被整批漏掉，排除名单在<b>两条通道</b>上都要命中。
    ///
    /// "排除"是调用方的过滤动作（识别层只给判据），所以这条断言的重点是<b>判据覆盖得全不全</b>：
    /// `.zip` 走的是"分卷主卷"那条通道（它同时是 zipsplit 的主卷形态），
    /// 只在"逐个识别"那条通道上判排除的话，名单里写着 zip 也一个都挡不住。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-041")]
    [Trait("Bug", "B-2026-035")]
    public void SubdirectoryCandidates_MustRespectTheExclusionList()
    {
        var folder = TestPaths.Probe("scan-exclude");

        var all = Probe.FindArchivesBelow(folder);
        Assert.Equal(3, all.Found.Count);
        Assert.Contains(all.Found, path => path.EndsWith("nested.zip", StringComparison.Ordinal));

        // 名单命中：两条通道的产物一起被剔掉，一个都不剩。
        var filtered = Probe.FindArchivesBelow(folder, excludeExts: ["zip"]);
        Assert.Empty(filtered.Found);
        Assert.Empty(filtered.Carriers);

        // 没命中的名单不许把子目录里的包滤掉：漏解比多解更难被发现。
        var kept = Probe.FindArchivesBelow(folder, excludeExts: ["docx"]);
        Assert.Equal(all.Found, kept.Found);

        // 单文件那一层入口同一口径：同一份判据管住两条通道，不是两条各判一次。
        var one = Probe.CandidatesFrom(
            Directory.EnumerateFiles(Path.Combine(folder, "b"), "*.zip"), excludeExts: ["zip"]);
        Assert.Empty(one);
    }

    /// <summary>往下找找到的候选只留主卷，而且分卷成员不进 Results（归组管它们）。</summary>
    [Fact]
    [Trait("Category", "edge")]
    public void SubdirectoryVolumes_MustCollapseToTheirMainVolume()
    {
        using var tmp = new TestDirectory();
        var root = tmp.Combine("vols");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        foreach (var name in Directory.EnumerateFiles(TestPaths.Probe("vol-part")))
        {
            File.Copy(name, Path.Combine(root, "sub", Path.GetFileName(name)));
        }

        var search = Probe.FindArchivesBelow(root);

        var main = Assert.Single(search.Found);
        Assert.Equal(Path.Combine(root, "sub", "x.part1.rar"), main);
        Assert.Empty(search.Results);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>
    /// junction 要的是目录，<c>cmd /c mklink /J</c> 不需要管理员权限；目录符号链接要 Developer Mode，
    /// 这台机器上会 UnauthorizedAccessException（2026-10-01 实测），所以只用 junction。
    /// 走 ShellExecute 不经管道：沙箱拒命名管道，重定向子进程输出会失败。
    /// </summary>
    private static bool CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\" >nul 2>nul")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(start);
        process?.WaitForExit();
        if (!Directory.Exists(link))
        {
            return false;
        }

        return (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
    }
}
