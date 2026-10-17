using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>算一个产物目录要的输入。</summary>
public sealed record OutputDirRequest(
    string ArchivePath,
    string? Parent = null,
    string? OutputRoot = null,
    ConflictPolicy Conflict = ConflictPolicy.Rename);

/// <summary>
/// 产物目录的结论。<see cref="Directory"/> 为空表示按策略跳过，此时 <see cref="BlockedBy"/> 是被挡住的名字。
/// 本层自己按入口命名把日志落下去，调用方不必再拼一遍后缀、也不必再判一次级别。
/// </summary>
public sealed record OutputDirResult(
    string? Directory,
    string? BlockedBy)
{
    public bool Skipped => Directory is null;
}

/// <summary>
/// 产物目录命名与重名冲突的唯一入口。
/// <c>normalize_for_match</c>。
///
/// 这一层只算名字：不写日志、不解压、不碰引擎。唯一访问磁盘的地方是 <c>output_root</c>
/// 那一条，它必须实际创建一次才知道能不能用，失败即回退源包同级。
/// </summary>
public static class OutputNaming
{
    /// <summary>无扩展名的包：目录名后缀，用来与源包自己的路径错开。</summary>
    public const string NoExtSuffix = "_unpack";

    /// <summary>规避检测的「删」字，夹在扩展名里，如 <c>.z删 i删 p删</c>。</summary>
    private static readonly Regex DelChar = new(@"[\s]*删[\s]*", RegexOptions.Compiled);

    /// <summary>下载站/论坛标识：<c>[xxx.com]</c>、<c>{www.aaa.net}</c>、<c>【某某论坛】</c>。</summary>
    private static readonly Regex SiteBracket = new(
        @"[\[\{【（(][^\]\}】）)]*(?:\.(?:com|net|org|cc|cn|me|xyz|top|info)|论坛|社区|首发|分享)[^\]\}】）)]*[\]\}】）)]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>结尾的副本编号：<c>(1)</c>、<c>[2]</c>、<c>_1</c>。</summary>
    private static readonly Regex TrailingCopy = new(
        @"(?:[_\-\s]+(?:\(\d+\)|\[\d+\]|\d{1,2}))+$", RegexOptions.Compiled);

    /// <summary>归一化时要扔掉的标点与空白。</summary>
    private static readonly Regex MatchNoise = new(@"[\s\-_.,:;!?，。：；！？、·|]+", RegexOptions.Compiled);

    /// <summary>Windows 不允许出现在文件名里的字符。表取自运行时，不另维护副本。</summary>
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>去掉文件名里用于规避检测的「删」字：<c>学习资料.z删 i删 p删</c> → <c>学习资料.zip</c>。</summary>
    public static string CleanDeleteChars(string name) => DelChar.Replace(name, string.Empty);

    /// <summary>去掉下载站标识、括号广告与结尾的副本编号，扩展名保留原样。</summary>
    public static string StripSiteNoise(string name)
    {
        var (stem, ext) = SplitExtension(name);
        stem = SiteBracket.Replace(stem, string.Empty);
        stem = TrailingCopy.Replace(stem, string.Empty);
        return stem.Trim(' ', '.', '_', '-') + ext;
    }

    /// <summary>
    /// 归一化：去扩展名、去站名、去「删」字、全角转半角、去空白、大小写折叠。
    /// 标点表里没有半角括号，全角括号转过半角之后会留下。
    /// 它只服务「按文件名匹配」，输出不得拿去落盘 —— 那样会改写用户目录的目录名。
    /// </summary>
    public static string NormalizeForMatch(string name)
    {
        var stem = SplitExtension(CleanDeleteChars(name)).Stem;
        stem = SiteBracket.Replace(stem, string.Empty);
        return MatchNoise.Replace(TextNormalize.ToHalfWidth(stem), string.Empty).ToLowerInvariant();
    }

    /// <summary>名字的保留长度上限：NTFS 的单段上限是 255，减去 <see cref="NoExtSuffix"/> 的 7 个字符
    /// 与冲突序号的 6 个字符得到 242；超过它就裁到这个数，之后再加任何一种后缀都不越界。</summary>
    private const int MaxStemLength = 242;

    /// <summary>重名冲突改名的计数上界：到它就换回落名，不再无限加序号。</summary>
    private const int MaxConflictTries = 1000;

    /// <summary>回落名里随机串的字符数。加上分隔符正好落在 13 个字符的后缀预算内。</summary>
    private const int FallbackTokenChars = 10;

    /// <summary>回落名最多试这么多次随机串。</summary>
    private const int MaxFallbackTries = 64;

    /// <summary>这一层产物目录的名字部分：分卷取归组主卷的 base，其余取包名，洗两遍再清成合法名。</summary>
    public static string StemFor(string archive) => LegalName(RawStem(archive));

    private static string RawStem(string archive)
    {
        var info = Probe.ClassifyVolume(archive);
        var stem = info.Kind != VolKind.None && info.IsSplit
            ? Path.GetFileName(info.Base)
            : SplitExtension(Path.GetFileName(archive)).Stem;

        stem = CleanDeleteChars(stem);

        // 第二次剥只在尾段是已知扩展名时做：无判据地剥会连版本号一起剥掉。
        var second = SplitExtension(stem);
        return second.Stem.Length > 0 && Probe.IsKnownExtension(second.Ext) ? second.Stem : stem;
    }

    private static string LegalName(string stem) => LegalName(stem, out _);

    /// <summary>
    /// 把名字清成 Windows 上真能落盘、且与日志里写的逐字相同的名字：去掉运行时表里的非法字符、
    /// 超过 <see cref="MaxStemLength"/> 时保留开头裁短、避开保留设备名、清掉结尾的点与空格；
    /// 后两样会被 Windows 规范化成另一个名字。<paramref name="trimmedFrom"/> 非空表示裁过。
    /// </summary>
    private static string LegalName(string stem, out string? trimmedFrom)
    {
        var name = new string([.. stem.Where(ch => Array.IndexOf(InvalidNameChars, ch) < 0)]);
        trimmedFrom = null;
        if (name.Length > MaxStemLength)
        {
            trimmedFrom = name;
            name = name[..MaxStemLength];
        }

        name = name.TrimEnd(' ', '.');
        if (name.Length == 0)
        {
            return NoExtSuffix;
        }

        return Probe.ReservedDeviceOf(name) is null ? name : name + NoExtSuffix;
    }

    /// <summary>算这一层的产物目录。跳过时 <see cref="OutputDirResult.Directory"/> 为空。</summary>
    public static OutputDirResult Resolve(OutputDirRequest request, IRunLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var archive = Path.GetFullPath(request.ArchivePath);
        var stem = LegalName(RawStem(archive), out var trimmedFrom);
        var baseDir = ResolveBaseDirectory(request, archive);
        var outdir = Path.Combine(baseDir, stem);
        if (trimmedFrom is not null)
        {
            log?.Problem(NamingText.LongNameTrimmedAt, trimmedFrom, stem);
        }

        // 无扩展名的包算出来正好是源包自己的路径。先错开：不改名的话下面
        // 「同名的是文件」会把它叫成 (1) 照解，Skip 档就永远挡不住，而且每跑一次多一份。
        if (SamePath(outdir, archive))
        {
            stem += NoExtSuffix;
            outdir = Path.Combine(baseDir, stem);
            log?.Note(NamingText.NoExtSuffixLoggedAt, Path.GetFileName(archive), stem);
        }

        // 同名的是个文件而不是目录。Skip 必须在这里就挡住 —— 判定排在改名之前，
        // 改完名字这条路径是全新的，下面那段的 Skip 分支再也进不去。
        if (File.Exists(outdir))
        {
            if (request.Conflict == ConflictPolicy.Skip)
            {
                return Skipped(outdir, log);
            }

            var renamed = FreeName(outdir);
            log?.Note(NamingText.SameNameIsFileRenamedAt, Path.GetFileName(outdir), Path.GetFileName(renamed));
            outdir = renamed;
        }

        if (Directory.Exists(outdir))
        {
            if (request.Conflict == ConflictPolicy.Skip)
            {
                return Skipped(outdir, log);
            }

            if (request.Conflict != ConflictPolicy.Overwrite)
            {
                outdir = FreeName(outdir);
            }
        }

        return new OutputDirResult(outdir, null);
    }

    /// <summary>基准目录三档：<c>Parent</c> 优先，其次 <c>OutputRoot</c>，最后源包同级。</summary>
    private static string ResolveBaseDirectory(OutputDirRequest request, string archive)
    {
        if (!string.IsNullOrEmpty(request.Parent))
        {
            return Path.GetFullPath(request.Parent);
        }

        if (string.IsNullOrEmpty(request.OutputRoot))
        {
            return SourceDirectory(archive);
        }

        var root = Path.GetFullPath(request.OutputRoot);
        try
        {
            // 必须真试建一次才知道这个位置能不能当输出目录。用户把「指定输出目录」
            // 设成一个文件时，CreateDirectory 会失败 —— 那正是要判的情形（B-2026-068）。
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return SourceDirectory(archive);
        }
    }

    private static string SourceDirectory(string archive)
    {
        var dir = Path.GetDirectoryName(archive);
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }

    /// <summary>返回第一个未被占用的名字，从 <c>名字 (1)</c> 起；数到上界还没空位就换成带随机串的回落名。</summary>
    /// <remarks>上界不许无界：同名候选堆满时，没有终点的循环会让命名卡住。回落名先裁主名再拼随机串，随机串因此不会被截掉。</remarks>
    private static string FreeName(string outdir)
    {
        for (var i = 1; i <= MaxConflictTries; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{outdir} ({i})");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return FallbackName(outdir);
    }

    /// <summary>回落名：主名接一段随机串，仍过一遍命名清洗。</summary>
    private static string FallbackName(string outdir)
    {
        var directory = Path.GetDirectoryName(outdir) ?? string.Empty;
        var name = Path.GetFileName(outdir);
        var room = MaxStemLength - (FallbackTokenChars + 1);
        var head = name.Length > room ? name[..room] : name;
        var candidate = outdir;
        for (var attempt = 0; attempt < MaxFallbackTries; attempt++)
        {
            var stem = LegalName(head + "-" + Guid.NewGuid().ToString("N")[..FallbackTokenChars]);
            candidate = directory.Length > 0 ? Path.Combine(directory, stem) : stem;
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidate;
    }

    private static OutputDirResult Skipped(string blockedBy, IRunLog? log)
    {
        log?.Note(NamingText.SkippedAt, Path.GetFileName(blockedBy));
        return new OutputDirResult(null, blockedBy);
    }

    /// <summary>两个路径是否指向同一处。大小写与结尾分隔符都不算差异。</summary>
    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按 1.1.0 的口径切扩展名：点开头的名字不算扩展名，<c>.zip</c> 整名都是主名。
    /// .NET 的 <c>Path.GetFileNameWithoutExtension</c> 在这里不同，它会把 <c>.zip</c> 的主名切成空串，
    /// 于是产物目录会算成基准目录本身。判据是「路径自撞」，两种口径混用就漏判。
    /// </summary>
    private static (string Stem, string Ext) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        var sep = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (dot <= sep)
        {
            return (name, string.Empty);
        }

        for (var i = sep + 1; i < dot; i++)
        {
            if (name[i] != '.')
            {
                return (name[..dot], name[dot..]);
            }
        }

        return (name, string.Empty);
    }

    private static string Fill(string template, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
