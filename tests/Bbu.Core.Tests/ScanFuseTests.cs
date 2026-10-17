using System;
using System.Collections.Generic;
using System.IO;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 「往下找」撞上保险丝：不再是静默截断，而是问一句要不要继续（`todo 78`）。
/// 同一批只问第一次：答「继续」之后就一路找下去，后面的根不再问（2026-10-16 改）。
/// 两道阈值在这一类里由 <see cref="ProbeOptions.MaxDirs"/> / <see cref="ProbeOptions.MaxFiles"/>
/// 注入小值，不依赖任何真实磁盘上的目录。
/// </summary>
public sealed class ScanFuseTests
{
    /// <summary>答「继续」就一直找下去：只问这一次，扫到底且不报停止原因。</summary>
    [Fact, Trait("Bug", "B-2026-178")]
    public void Fuse_MustKeepGoingWhenTheUserSaysYes()
    {
        using var tmp = new TestDirectory();
        var root = ManyFolders(tmp.Combine("many"), 6);

        var asked = new List<(int Dirs, int Files)>();
        var logs = new List<string>();
        var search = Probe.FindArchivesBelow(
            root,
            new ProbeOptions
            {
                MaxDirs = 2,
                MaxFiles = 2,
                AskFuseContinue = (dirs, files) =>
                {
                    asked.Add((dirs, files));
                    return true;
                },
                Log = new ChannelLog((_, text) => logs.Add(text)),
            });

        Assert.Single(asked);
        Assert.True(asked[0].Dirs >= 2, $"问早了：{asked[0].Dirs} 个目录");
        Assert.False(search.Truncated);

        // 答「继续」之后就不该有「已停止」那一条：报了说明它其实没接着找。
        Assert.DoesNotContain(logs, line => line.Contains("已停止", StringComparison.Ordinal));
    }

    /// <summary>答「不再找」就停在那一刻，并如实报一条停止原因 —— 撞上了而不说，用户以为找完了。</summary>
    [Fact, Trait("Bug", "B-2026-178")]
    public void Fuse_MustStopAndSayWhyWhenTheUserSaysNo()
    {
        using var tmp = new TestDirectory();
        var root = ManyFolders(tmp.Combine("many"), 6);

        var logs = new List<string>();
        var asked = new List<(int Dirs, int Files)>();
        var search = Probe.FindArchivesBelow(
            root,
            new ProbeOptions
            {
                MaxDirs = 2,
                MaxFiles = 2,
                AskFuseContinue = (dirs, files) =>
                {
                    asked.Add((dirs, files));
                    return false;
                },
                Log = new ChannelLog((_, text) => logs.Add(text)),
            });

        Assert.True(search.Truncated);

        // 停下那条日志报的是阈值本身那两个数，而且与问用户的那两个数同源。
        var only = Assert.Single(asked);
        Assert.Contains(logs, line => string.Equals(
            line, ScanText.Fill(ProbeText.LogScanFuseAt, only.Dirs, only.Files), StringComparison.Ordinal));
    }

    /// <summary>同一批只问第一次：答过「继续」之后，后面的根不再打扰用户（2026-10-16 改）。</summary>
    [Fact, Trait("Bug", "B-2026-178")]
    public void Fuse_MustAskOnlyOncePerBatch()
    {
        var inner = 0;
        var ask = Probe.AskOncePerBatch((_, _) =>
        {
            inner++;
            return true;
        });
        Assert.NotNull(ask);
        Assert.True(ask!(2, 2));
        Assert.True(ask!(4, 4));
        Assert.True(ask!(6, 6));
        Assert.Equal(1, inner);

        // 答「不再找」的那一次不占名额：下一个根照问。
        var refused = 0;
        var next = Probe.AskOncePerBatch((_, _) =>
        {
            refused++;
            return false;
        });
        Assert.NotNull(next);
        Assert.False(next!(2, 2));
        Assert.False(next!(4, 4));
        Assert.Equal(2, refused);

        // 没人可问时保持「没人可问」。
        Assert.Null(Probe.AskOncePerBatch(null));
    }

    /// <summary>没人可问时按「不再往下找」收口：不能既不停、也不问，那就成了静默截断。</summary>
    [Fact, Trait("Bug", "B-2026-178")]
    public void Fuse_MustStopWhenNobodyCanBeAsked()
    {
        using var tmp = new TestDirectory();
        var root = ManyFolders(tmp.Combine("many"), 6);

        var logs = new List<string>();
        var search = Probe.FindArchivesBelow(
            root,
            new ProbeOptions { MaxDirs = 2, MaxFiles = 2, Log = new ChannelLog((_, text) => logs.Add(text)) });

        Assert.True(search.Truncated);
        Assert.Contains(logs, line => line.Contains("已停止", StringComparison.Ordinal));
    }

    /// <summary>两道阈值常量钉在 10000；改一处要同时改另一处（B-2026-178）。</summary>
    [Fact, Trait("Bug", "B-2026-178")]
    public void FuseLimits_MustStayAt10000()
    {
        Assert.Equal(10000, Probe.NestedScanMaxDirs);
        Assert.Equal(10000, Probe.NestedScanMaxFiles);
    }

    /// <summary>造若干个各带一个非压缩包的子目录。</summary>
    private static string ManyFolders(string root, int count)
    {
        Directory.CreateDirectory(root);
        for (var index = 0; index < count; index++)
        {
            var folder = Path.Combine(root, $"dir-{index:D2}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"note-{index:D2}.txt"), "不是压缩包");
        }

        return root;
    }
}
