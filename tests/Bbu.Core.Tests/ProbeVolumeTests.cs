using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层边界档：分卷归组。
///
/// 判的是"哪一份该送进引擎"。判错的代价不是报错，是用户被告知"包损坏"或者整批漏解，
/// 所以每类命名各一条，还要有反例（完整分卷不许被报成缺卷）。
/// </summary>
[Trait("Category", "edge")]
public sealed class ProbeVolumeTests
{
    private static IReadOnlyList<string> FilesIn(string folder) =>
        Directory.EnumerateFiles(folder).OrderBy(path => path, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 判据 4：四类命名各一条断言，主卷指名要对。
    ///
    /// `.z01` 排在 `.zip` 前面只是文件名顺序，不许据此把它当主卷（B-2026-048）；
    /// `.r00` 是第 2 卷，`x.rar` 才是第 1 卷。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-048")]
    public void FourNamingSchemes_MustNameTheirMainVolume()
    {
        var part = FilesIn(TestPaths.Probe("vol-part"));
        var partMain = TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar"));
        Assert.Equal(VolKind.Part, Probe.ClassifyVolume(partMain).Kind);
        Assert.True(Probe.ClassifyVolume(partMain).IsFirst);
        Assert.Equal(new[] { partMain }, Probe.CandidatesFrom(part));

        var oldrar = FilesIn(TestPaths.Probe("vol-oldrar"));
        var oldMain = TestPaths.Probe(Path.Combine("vol-oldrar", "x.rar"));
        Assert.Equal(VolKind.OldRar, Probe.ClassifyVolume(oldMain).Kind);
        Assert.True(Probe.ClassifyVolume(oldMain).IsFirst);
        Assert.False(Probe.ClassifyVolume(TestPaths.Probe(Path.Combine("vol-oldrar", "x.r00"))).IsFirst);
        Assert.Equal(new[] { oldMain }, Probe.CandidatesFrom(oldrar));

        // `.z01` 与 `.z02` 都排在 `.zip` 前面：主卷仍旧是 `.zip`。
        var split = FilesIn(TestPaths.Probe("vol-zipsplit"));
        Assert.True(
            string.CompareOrdinal(Path.GetFileName(split[0]), "x.zip") < 0,
            "夹具顺序变了，这条判据的前提没了");
        var zipMain = TestPaths.Probe(Path.Combine("vol-zipsplit", "x.zip"));
        Assert.Equal(VolKind.ZipSplit, Probe.ClassifyVolume(zipMain).Kind);
        Assert.True(Probe.ClassifyVolume(zipMain).IsFirst);
        Assert.Equal(new[] { zipMain }, Probe.CandidatesFrom(split));

        var numeric = FilesIn(TestPaths.Probe("vol-numeric"));
        var numericMain = TestPaths.Probe(Path.Combine("vol-numeric", "x.001"));
        Assert.Equal(VolKind.Numeric, Probe.ClassifyVolume(numericMain).Kind);
        Assert.Equal(new[] { numericMain }, Probe.CandidatesFrom(numeric));
    }

    /// <summary>归组本身：主卷路径、成员数、有没有分开卷。</summary>
    [Fact]
    public void Grouping_MustKeepEveryMemberOfOneBase()
    {
        var files = FilesIn(TestPaths.Probe("vol-part"));
        var groups = Probe.GroupVolumes(files);

        var group = Assert.Single(groups);
        Assert.Equal(VolKind.Part, group.Kind);
        Assert.True(group.FirstPresent);
        Assert.True(group.IsSplit);
        Assert.Equal(files.Count, group.Count);
        Assert.Equal(files.Count, group.Others.Count + 1);
        Assert.Equal(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")), group.Main);

        // 单独一个 zip 也算一组，但成员数只有 1，不是分卷。
        var single = Assert.Single(Probe.GroupVolumes([TestPaths.Fixture("ok-small.zip")]));
        Assert.False(single.IsSplit);
        Assert.True(single.FirstPresent);
    }

    /// <summary>
    /// B-2026-079：缺主卷时要点名缺的是哪一个，而且不许把编号最小的次卷送去解。
    ///
    /// 以前这里退化成"取组内第一个"，于是"缺少主卷"这道防线恒不触发，
    /// 用户看到的是引擎报的"包损坏"。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-079")]
    public void MissingMainVolume_MustBeNamedAndNeverUsed()
    {
        var folder = TestPaths.Probe("vol-missing-main");
        var files = FilesIn(folder);
        var logs = new List<(LogLevel Level, string Text)>();

        var candidates = Probe.CandidatesFrom(
            files,
            new ProbeOptions { Log = new ChannelLog((level, message) => logs.Add((level, message))) });

        Assert.Empty(candidates);
        var logged = Assert.Single(logs);
        Assert.Equal(LogLevel.Warn, logged.Level);
        Assert.Contains("缺少主卷", logged.Text, StringComparison.Ordinal);
        Assert.Contains("x.part1.rar", logged.Text, StringComparison.Ordinal);

        var group = Assert.Single(Probe.GroupVolumes(files));
        Assert.False(group.FirstPresent);
        Assert.Equal(Path.Combine(folder, "x.part1.rar"), group.Main);
        Assert.Equal(files.Count, group.Count);

        Assert.Equal("x.part1.rar", Probe.MissingVolumeNote(files[0]));
        Assert.Null(Probe.MainVolumeOf(files[0], files));
    }

    /// <summary>
    /// 缺末卷的点名，以及它的两条反例。
    ///
    /// 分卷头里没有"总共有几卷"，所以缺末卷只有 RAR5 卷头的"后面还有"标志答得了（B-2026-079）。
    /// 反例很重要：把完整的分卷包报成缺卷，会让这一组永远等一个不存在的 partN，一个密码都不试。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-079")]
    public void MissingLastVolume_MustBeToldApartFromACompleteSet()
    {
        var complete = FilesIn(TestPaths.Probe("vol-part"));
        Assert.Null(Probe.MissingVolumeNote(complete[0]));

        var truncated = FilesIn(TestPaths.Probe("vol-missing-last"));
        var last = Path.GetFileName(truncated[^1]);
        var expected = "x.part" + (int.Parse(last.Split(".part")[1].Split('.')[0]) + 1) + ".rar";
        Assert.Equal(expected, Probe.MissingVolumeNote(truncated[0]));

        // 单卷 `.zip` / `.001` 没有"下一卷"可言，不许从这里冒出一个不存在的名字。
        Assert.Null(Probe.MissingVolumeNote(TestPaths.Fixture("ok-small.zip")));
        Assert.Null(Probe.MissingVolumeNote(TestPaths.Probe(Path.Combine("vol-numeric", "x.001"))));
    }

    /// <summary>四类命名的"第 N 卷叫什么"，用缺席叫法反推：缺主卷时点的是组内第一个名字。</summary>
    [Fact]
    public void VolumeNames_MustFollowTheNamingScheme()
    {
        Assert.Equal(
            VolKind.Part,
            Probe.ClassifyVolume(TestPaths.Probe(Path.Combine("vol-missing-last", "x.part1.rar"))).Kind);

        // `.z01` / `.r00` 的索引与主卷撞号，所以"第几卷"必须换算，不能直接拿索引当卷号。
        var z01 = Probe.ClassifyVolume(TestPaths.Probe(Path.Combine("vol-zipsplit", "x.z01")));
        Assert.Equal(1, z01.Index);
        Assert.False(z01.IsFirst);

        var r00 = Probe.ClassifyVolume(TestPaths.Probe(Path.Combine("vol-oldrar", "x.r00")));
        Assert.Equal(1, r00.Index);
        Assert.False(r00.IsFirst);

        // 主卷在场时 MainVolumeOf 给出同一个答案，两条通道不许各说各的。
        var files = FilesIn(TestPaths.Probe("vol-zipsplit"));
        var main = TestPaths.Probe(Path.Combine("vol-zipsplit", "x.zip"));
        Assert.Equal(main, Probe.MainVolumeOf(files[0], files));
        Assert.Null(Probe.MainVolumeOf(TestPaths.Fixture("ok-small.zip"), files));
    }

    /// <summary>
    /// 名字里的数字段超过 <c>int</c> 上限时按「不是分卷号」处理：不抛、也不判为分卷（B-2026-210）。
    /// 识别层这四个正则把数字段限死在三位内，样本走不到那处解析，所以这条是防回归。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-210")]
    public void OversizedNumberInTheName_MustNotBeTreatedAsAVolume()
    {
        var info = Probe.ClassifyVolume("backup.20261013235959");

        Assert.Equal(VolKind.None, info.Kind);
        Assert.Empty(Probe.GroupVolumes(["backup.20261013235959"]));
    }
}
