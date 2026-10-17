using System.Collections.Generic;

namespace Bbu.Core.Models;

/// <summary>分卷的命名方案。</summary>
public enum VolKind
{
    /// <summary>不是分卷。</summary>
    None,

    /// <summary>`x.part1.rar` / `x.part01.rar`，主卷是序号最小的那个。</summary>
    Part,

    /// <summary>`x.rar` 加 `x.r00` / `x.r01`，主卷是 `x.rar`。</summary>
    OldRar,

    /// <summary>`x.z01` 加 `x.zip`，主卷是 `x.zip`。</summary>
    ZipSplit,

    /// <summary>`x.001` 加 `x.002`，主卷是 `x.001`。</summary>
    Numeric,
}

/// <summary>一个文件的分卷身份。<see cref="Index"/> 只用于组内排序，不是"第几卷"。</summary>
/// <remarks>
/// `.z01` 与 `.r00` 的索引与各自的主卷撞号，反推"第 N 卷叫什么"要用 <c>VolumeName</c>。
/// </remarks>
public sealed record VolumeInfo(VolKind Kind, string Base, int Index, bool IsFirst)
{
    public bool IsSplit => Kind is not VolKind.None;
}

/// <summary>同目录、同 basename 的一组分卷。只把 <see cref="Main"/> 送进引擎。</summary>
public sealed class VolumeGroup
{
    public required string Base { get; init; }

    public required VolKind Kind { get; init; }

    /// <summary>主卷的完整路径。首卷缺席时它指向"应该在的那个名字"，盘上没有这个文件。</summary>
    public required string Main { get; init; }

    public required IReadOnlyList<string> Others { get; init; }

    /// <summary>主卷真的在场吗。用户只下到 `part2` / `z01` / `r00` 时为假。</summary>
    public required bool FirstPresent { get; init; }

    /// <summary>这一组在场的卷文件有几个，首卷缺席时不算它。</summary>
    public int Count => Others.Count + (FirstPresent ? 1 : 0);

    /// <summary>
    /// 真的是分卷吗，只有 1 个成员时不算。它<em>不是</em>"能不能送进引擎"的判据：
    /// 只下到一个次卷时这里为假，而那一组恰恰解不开，该判的是 <see cref="FirstPresent"/>。
    /// </summary>
    public bool IsSplit => Count > 1;
}

/// <summary>马甲文件是哪一档认出来的，兼作"切不切包"的判据。</summary>
public enum Carrier
{
    /// <summary>没有找到包。值不值得处理看 <see cref="CarrierProbe.CarrierLike"/>。</summary>
    None,

    /// <summary>尾部 4MB 里的 EOCD 反推出了 ZIP 起点，要切包。</summary>
    TailDirectory,

    /// <summary>尾部 4MB 里的 RAR5 或 7z 明文签名加头部 CRC，要切包。</summary>
    TailMagic,

    /// <summary>全盘扫到文件<em>中段</em>的包，要切包。</summary>
    DeepScan,

    /// <summary>假头加绝对偏移布局：包内偏移相对文件开头，切掉假头会让它整体错位，不许切包。</summary>
    AbsoluteLayout,

    /// <summary>引擎自己会跳过假头，不切包。</summary>
    EngineDirect,
}

/// <summary>在某个文件里找到的内嵌包。</summary>
public sealed record Embedded(ArchiveFormat Format, long Offset, Carrier How)
{
    /// <summary>切出来之后给它什么扩展名。引擎也按内容判，这里只为可读。</summary>
    public string Ext => Format switch
    {
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.Rar or ArchiveFormat.Rar5 => "rar",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.GZip => "gz",
        ArchiveFormat.Tar => "tar",
        _ => "bin",
    };
}

/// <summary>一个马甲文件的探测结论。</summary>
/// <remarks>
/// 两个问题不得合并成同一个判断：<see cref="CarrierLike"/> 说的是文件本身像不像马甲，
/// <see cref="Embedded"/> 说的是里面确实有包、从哪开始。后者才够切包。
/// </remarks>
public sealed record CarrierProbe(Carrier Kind, Embedded? Embedded, bool CarrierLike)
{
    /// <summary>不像马甲，或压根没探。</summary>
    public static CarrierProbe NotCarrier { get; } = new(Carrier.None, null, false);

    /// <summary>值不值得收成候选：找到包、或能直读原文件、或布局是绝对偏移。</summary>
    public bool WorthHandling =>
        Embedded is not null || Kind is Carrier.AbsoluteLayout or Carrier.EngineDirect;

    /// <summary>别切包、原样直读。</summary>
    public bool Direct => Kind is Carrier.AbsoluteLayout or Carrier.EngineDirect;
}

/// <summary>识别一个文件的结论。三态在这里分开，"读不了"有独立取值。</summary>
public enum ProbeStatus
{
    /// <summary>可以交给引擎试：magic 认得出，或扩展名在压缩包名单里。</summary>
    Candidate,

    /// <summary>读到了文件头，magic 与扩展名名单都不认，而且不像马甲。</summary>
    NotArchive,

    /// <summary>打不开或读不到文件头。与 <see cref="NotArchive"/> 必须分开。</summary>
    Unreadable,

    /// <summary>Windows 保留设备名，绝不打开。</summary>
    ReservedDevice,
}

/// <summary>识别一个文件的结果。用户可见的说明在 <see cref="Note"/>。</summary>
public sealed record ProbeResult(
    string Path,
    ProbeStatus Status,
    ArchiveFormat Format,
    VolKind VolKind,
    string Note,
    CarrierProbe Carrier)
{
    /// <summary>是标准候选：magic 或扩展名名单认得出。</summary>
    public bool IsCandidate => Status is ProbeStatus.Candidate;

    /// <summary>要不要交给穿透：标准候选，或者马甲里有东西。</summary>
    public bool ShallHandle => IsCandidate || Carrier.WorthHandling;

    /// <summary>处理这个文件时不许切包。</summary>
    public bool Direct => Carrier.Direct;
}

/// <summary>一次"往下找"的结果。</summary>
/// <remarks>
/// <see cref="Truncated"/> 说的是没找完，调用方看到它必须如实说，不许报成"没有可解压的压缩包"。
/// <see cref="Cancelled"/> 与它必须分开：取消不是没找完。
/// </remarks>
public sealed class DirSearch
{
    /// <summary>收全的标准候选。</summary>
    public List<string> Found { get; } = [];

    /// <summary>这一批文件逐条的识别结论。单文件入口与目录入口的结论靠它比对。</summary>
    public List<ProbeResult> Results { get; } = [];

    /// <summary>子目录里疑似藏了内嵌包、但不在标准候选判据里的文件。</summary>
    public List<string> Carriers { get; } = [];

    /// <summary>读不了的子目录。</summary>
    public List<string> Unreadable { get; } = [];

    public int ScannedDirs { get; set; }

    /// <summary>已经枚举到多少个文件。保险丝按它算，不按秒 —— 秒是设备相关的。</summary>
    public int ScannedFiles { get; set; }

    /// <summary>这一次「往下找」里被判成不是压缩包的条数。日志按整批说一次。</summary>
    public int SkippedNotArchive { get; set; }

    /// <summary>没找完：保险丝烧了，或某个子目录读不了。</summary>
    public bool Truncated { get; set; }

    /// <summary>用户点了停止。</summary>
    public bool Cancelled { get; set; }
}

/// <summary>随包引擎对这个格式的能力。</summary>
public enum EngineAbility
{
    /// <summary>读得了。</summary>
    CanRead,

    /// <summary>编解码器在 <c>7z.dll</c> 里，而随包只有 <c>7z.exe</c>。</summary>
    NeedsCodecDll,

    /// <summary>不是压缩包，不该送给引擎。</summary>
    NotArchive,
}
