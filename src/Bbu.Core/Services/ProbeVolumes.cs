using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 识别层的分卷归组：四类命名方案、主卷判定、缺卷点名。
/// </summary>
public static partial class Probe
{
    private static readonly Regex PartRe =
        new(@"^(?<base>.+?)\.part(?<num>\d{1,3})\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OldRarRe =
        new(@"^(?<base>.+?)\.r(?<num>\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ZipSplitRe =
        new(@"^(?<base>.+?)\.z(?<num>\d{2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericRe =
        new(@"^(?<base>.+?)\.(?<num>\d{3})$", RegexOptions.Compiled);

    private static readonly Regex PlainRarRe =
        new(@"^(?<base>.+?)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlainZipRe =
        new(@"^(?<base>.+?)\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int Rar5HeadFileType = 2;

    private const int Rar5SplitAfterFlag = 0x0010;

    /// <summary>判断单个文件属于哪种分卷形态。</summary>
    public static VolumeInfo ClassifyVolume(string path)
    {
        var name = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        string Join(string stem) => directory.Length > 0 ? Path.Combine(directory, stem) : stem;

        var match = PartRe.Match(name);
        if (match.Success && Number(match.Groups["num"].Value) is { } partNumber)
        {
            return new VolumeInfo(VolKind.Part, Join(match.Groups["base"].Value), partNumber, partNumber == 1);
        }

        match = ZipSplitRe.Match(name);
        if (match.Success && Number(match.Groups["num"].Value) is { } zipNumber)
        {
            // z01 是次卷，主卷是同目录的 .zip，由归组时配对。
            return new VolumeInfo(VolKind.ZipSplit, Join(match.Groups["base"].Value), zipNumber, false);
        }

        match = OldRarRe.Match(name);
        if (match.Success && Number(match.Groups["num"].Value) is { } oldNumber)
        {
            // r00 是第 2 卷，第 1 卷是 x.rar。
            return new VolumeInfo(VolKind.OldRar, Join(match.Groups["base"].Value), oldNumber + 1, false);
        }

        match = NumericRe.Match(name);
        if (match.Success && Number(match.Groups["num"].Value) is { } numericNumber)
        {
            return new VolumeInfo(VolKind.Numeric, Join(match.Groups["base"].Value), numericNumber, numericNumber == 1);
        }

        match = PlainZipRe.Match(name);
        if (match.Success)
        {
            return new VolumeInfo(VolKind.ZipSplit, Join(match.Groups["base"].Value), 1, true);
        }

        match = PlainRarRe.Match(name);
        if (match.Success)
        {
            return new VolumeInfo(VolKind.OldRar, Join(match.Groups["base"].Value), 1, true);
        }

        return new VolumeInfo(VolKind.None, Join(Path.GetFileNameWithoutExtension(name)), 1, true);
    }

    /// <summary>把一批文件按分卷归组。每组之后只会产出 1 个任务。</summary>
    public static IReadOnlyList<VolumeGroup> GroupVolumes(IEnumerable<string> paths)
    {
        var all = paths.ToList();
        var buckets = new Dictionary<(string Base, VolKind Kind), List<VolumeInfo>>();
        foreach (var path in all)
        {
            var info = ClassifyVolume(path);

            // 弱命名的独立包不进任何一组：进了这一组就会少一个首卷，于是报出一句「缺少主卷」。
            if (info.Kind is VolKind.None || IsStandaloneVolume(path))
            {
                continue;
            }

            var key = (NormCase(info.Base), info.Kind);
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
            }

            list.Add(info);
        }

        var groups = new List<VolumeGroup>();
        foreach (var ((stem, kind), infos) in buckets)
        {
            infos.Sort((left, right) => left.Index.CompareTo(right.Index));
            var main = infos.FirstOrDefault(info => info.IsFirst);

            // 首卷不在场时 main 指向"应该在的那个名字"，一个不存在的路径。
            // 退化成 infos[0] 会让缺主卷在数据模型里无法表达，次卷被当主卷送进引擎。
            groups.Add(new VolumeGroup
            {
                Base = stem,
                Kind = kind,
                Main = main is not null ? ResolveMainPath(main, all) : VolumeName(infos[0].Base, kind, 1),
                Others = infos.Where(info => !ReferenceEquals(info, main)).Select(info => info.Base).ToList(),
                FirstPresent = main is not null,
            });
        }

        return groups;
    }

    /// <summary>从分卷分组里挑出可以送进引擎的主卷。首卷不在场时跳过并说一声。</summary>
    /// <remarks>
    /// 判据是"这一组的首卷在不在"，不许先看 <see cref="VolumeGroup.IsSplit"/>：
    /// 只下到一个次卷时成员数为 1，那道守卫进不去，次卷照样会被送进引擎。
    /// </remarks>
    public static IReadOnlyList<string> MainVolumes(
        IEnumerable<VolumeGroup> groups,
        IRunLog? log = null)
    {
        var mains = new List<string>();
        foreach (var group in groups)
        {
            if (!group.FirstPresent)
            {
                log?.Problem(ProbeText.MissingVolumeAt, Path.GetFileName(group.Main), group.Count);
                continue;
            }

            mains.Add(group.Main);
        }

        return mains;
    }

    /// <summary>这一组分卷不全吗，不全就返回缺的那一卷文件名。</summary>
    /// <remarks>
    /// 连续到场不许直接当成还差下一卷：分卷头里没有"总共有几卷"这个信息，
    /// 那样写会把每一个完整的分卷包都报成缺卷，从此一个密码都不试。
    /// 缺末尾卷只有 RA卷头答得了，判不出来就如实返回 <c>null</c>。
    /// </remarks>
    public static string? MissingVolumeNote(string path)
    {
        var info = ClassifyVolume(path);
        if (info.Kind is VolKind.None || IsStandaloneVolume(path))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
        {
            folder = ".";
        }

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var present = new Dictionary<int, string>();
        var firstPresent = false;
        var hasLater = false;
        foreach (var entry in entries)
        {
            var candidate = ClassifyVolume(entry);
            if (candidate.Kind != info.Kind || !string.Equals(NormCase(candidate.Base), NormCase(info.Base), StringComparison.Ordinal))
            {
                continue;
            }

            present[VolumeSeq(candidate)] = entry;
            firstPresent |= candidate.IsFirst;
            hasLater |= !candidate.IsFirst;
        }

        if (!firstPresent)
        {
            return Path.GetFileName(VolumeName(info.Base, info.Kind, 1));
        }

        // .zip / .rar / .NNN 单个不算分卷：这一组里连一个次卷成员都没有，就没有"下一卷"可言。
        // .partN.rar 不在此列，那个命名只在多卷模式下出现。
        if (info.Kind is not VolKind.Part && !hasLater)
        {
            return null;
        }

        var top = present.Keys.Max();
        var seq = 1;
        while (present.ContainsKey(seq))
        {
            seq++;
        }

        if (seq <= top)
        {
            return Path.GetFileName(VolumeName(info.Base, info.Kind, seq));
        }

        if (top == 1)
        {
            return Path.GetFileName(VolumeName(info.Base, info.Kind, 2));
        }

        return Rar5HasMoreVolumes(present[top])
            ? Path.GetFileName(VolumeName(info.Base, info.Kind, top + 1))
            : null;
    }

    /// <summary>给定一个文件，若它是分卷成员，返回同组主卷路径。首卷不在场时返回 <c>null</c>。</summary>
    public static string? MainVolumeOf(string path, IEnumerable<string> siblings)
    {
        var info = ClassifyVolume(path);
        if (info.Kind is VolKind.None)
        {
            return null;
        }

        var candidates = siblings.Select(item => item).ToList();
        foreach (var group in GroupVolumes(candidates))
        {
            if (group.Kind == info.Kind && string.Equals(NormCase(group.Base), NormCase(info.Base), StringComparison.Ordinal))
            {
                return group.FirstPresent ? group.Main : null;
            }
        }

        return null;
    }

    /// <summary>这一卷该不该当独立包送进引擎：`.NNN` 里不是首卷、而自身内容是一个完整包的那些。</summary>
    /// <remarks>`Classify` 读不出包头的就是裸切片，也就是真分卷的次卷；</remarks>
    public static bool IsStandaloneVolume(string path)
    {
        var info = ClassifyVolume(path);
        return info.Kind is VolKind.Numeric && !info.IsFirst && Classify(path).IsCandidate;
    }

    /// <summary>这一卷在组内的卷号，1 是首卷。`.z01` 与 `.r00` 的索引与首卷撞号，在这里换算。</summary>
    private static int VolumeSeq(VolumeInfo info) =>
        info.Kind is VolKind.ZipSplit or VolKind.OldRar
            ? (info.IsFirst ? 1 : info.Index + 1)
            : info.Index;

    /// <summary>这一组里第 <paramref name="seq"/> 卷该叫什么文件名。首卷缺席时用它说出缺的那一卷。</summary>
    private static string VolumeName(string stem, VolKind kind, int seq) => kind switch
    {
        VolKind.Part => stem + ".part" + Text(seq) + ".rar",
        VolKind.Numeric => stem + "." + Text(seq, 3),
        VolKind.ZipSplit => seq == 1 ? stem + ".zip" : stem + ".z" + Text(seq - 1, 2),
        VolKind.OldRar => seq == 1 ? stem + ".rar" : stem + ".r" + Text(seq - 2, 2),
        _ => stem,
    };

    private static string Text(int value, int digits = 0) =>
        value.ToString(digits == 0 ? "D" : "D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>在原始路径列表里找出该组的主卷文件。必须连是不是首卷一起比。</summary>
    private static string ResolveMainPath(VolumeInfo info, List<string> paths)
    {
        foreach (var path in paths)
        {
            var candidate = ClassifyVolume(path);
            if (candidate.Base == info.Base && candidate.Kind == info.Kind
                && candidate.Index == info.Index && candidate.IsFirst == info.IsFirst)
            {
                return path;
            }
        }

        return info.Base;
    }

    /// <summary>
    /// 这一卷的卷头声明"后面还有卷"吗。这是唯一能判"缺的是最后一卷"的凭据：
    /// 文件名里没有"总共有几卷"这个信息，光看名字看不出还差一个 part4。
    /// 读不到、不是 RAR5、结构不对，一律如实返回假，不猜。
    /// 长度字段与 <c>Rar5HeaderOk</c> 同口径设上限，所以推进量不会溢出，畸形卷头也不靠外层 catch 兜。
    /// </summary>
    private static bool Rar5HasMoreVolumes(string path)
    {
        var buffer = ReadAt(path, 0, 256);
        if (buffer.Length < 8 || !buffer.AsSpan().StartsWith(Rar5Magic))
        {
            return false;
        }

        var pos = 8;
        while (pos + 4 < buffer.Length)
        {
            var at = pos + 4;
            var (size, after) = Rar5Vint(buffer, at);
            if (size <= 0 || size > Rar5MaxHeadBytes)
            {
                return false;
            }

            var (headType, next) = Rar5Vint(buffer, after);
            var (flags, _) = Rar5Vint(buffer, next);
            if (headType == Rar5HeadFileType)
            {
                return (flags & Rar5SplitAfterFlag) != 0;
            }

            pos = after + size;
        }

        return false;
    }

    /// <summary>卷号数字段。解析不出来或超过 <c>int</c> 上限时回 <c>null</c>，按「不是分卷号」处理（B-2026-210）。</summary>
    private static int? Number(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>路径规范化的大小写折叠，对齐 Python 的 <c>os.path.normcase</c>。</summary>
    private static string NormCase(string value) =>
        OperatingSystem.IsWindows() ? value.ToLowerInvariant() : value;
}
