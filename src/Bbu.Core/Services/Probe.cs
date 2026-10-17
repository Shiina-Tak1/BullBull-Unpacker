using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>识别层的诊断计次。传进 <see cref="ProbeOptions.Counters"/> 就能钉住代价花在哪一档。</summary>
public sealed class ProbeCounters
{
    public int DeepScans { get; private set; }

    public int TailProbes { get; private set; }

    public int EngineProbes { get; private set; }

    /// <summary>尾部 EOCD 反推循环里认过的候选条数：每条都要付一次窗口内回扫加一次尾部外读，是这一档代价的直接度量。</summary>
    public int EocdHits { get; private set; }

    internal void CountDeepScan() => DeepScans++;

    internal void CountTailProbe() => TailProbes++;

    internal void CountEngineProbe() => EngineProbes++;

    internal void CountEocdHit() => EocdHits++;
}

/// <summary>
/// 识别一个文件或一批文件时要付的代价：调用方声明自己愿付到哪一档，判据本身不做代价决策。
/// 这里没有"不处理的文件类型"，那是调用方的过滤动作，判据在 <see cref="Probe.ExcludedExt"/>。
/// </summary>
public sealed record ProbeOptions
{
    public static ProbeOptions Default { get; } = new();

    /// <summary>愿不愿意付全盘扫的代价。只在"这一层没有别的候选可解"时才该为真。</summary>
    public bool Deep { get; init; }

    /// <summary>愿不愿意为"文件里藏没藏包"付尾部定位那几档；假则只判到候选为止，只影响报备、不影响解什么。</summary>
    public bool Carriers { get; init; } = true;

    /// <summary>「往下找」的两道上限，只给断言注入小值用；生产走 <see cref="Probe"/> 里那两个常量。</summary>
    public int MaxDirs { get; init; } = Probe.NestedScanMaxDirs;

    public int MaxFiles { get; init; } = Probe.NestedScanMaxFiles;

    /// <summary>问引擎"能不能把这个文件当包直读"。不传表示一个进程都不起。</summary>
    public Func<string, bool>? EngineProbe { get; init; }

    /// <summary>
    /// 第二个引擎（UnRAR）的同一问，排在尾部签名定位之后：尾部签名能定位的包要留着切包
    /// （切出来那一份才吃得到快路），所以这一问只能补前几档都没认出来的。不传表示不问。
    /// 它是保险不是负载路径 —— 量过的那批「壳 + 包」与真 WinRAR SFX 里，
    /// 这一问一次都没改变结论。
    /// </summary>
    public Func<string, bool>? FallbackEngineProbe { get; init; }

    public ProbeCounters? Counters { get; init; }

    /// <summary>「往下找」的计数回传：已扫目录数、已扫文件数。按节流调，不按每个文件调。</summary>
    public Action<int, int>? OnScanProgress { get; init; }

    /// <summary>撞上保险丝时的询问：参数是已扫目录数与文件数，返回真表示继续。不传表示没人可问。</summary>
    public Func<int, int, bool>? AskFuseContinue { get; init; }

    public CancellationToken Cancellation { get; init; }

    /// <summary>识别结论那一行的回传。级别由产出点给，界面不解析文案。</summary>
    public IRunLog? Log { get; init; }

}

/// <summary>
/// 识别层：一个文件真实是什么格式、算不算候选包、属于哪一组分卷、是不是伪装包。
/// 它只做判断，不移动文件、不解压、不试密码，也不决定产物目录。
/// </summary>
public static partial class Probe
{
    /// <summary>小于 1MB 的文件不值得做垫片伪装探测。</summary>
    public const long EmbedMinSize = 1L << 20;

    /// <summary>找 EOCD 与尾部签名只看文件尾部这么多字节。EOCD 注释上限 64KB，4MB 留得够宽。</summary>
    public const int EmbedTailBytes = 4 << 20;

    public const int EmbedChunkBytes = 8 << 20;

    /// <summary>尾部 EOCD 反推循环每遍最多认 1024 条候选。命中数只由窗口大小决定，正常文件的 EOCD 只有一两条，
    /// 1024 给真包留足余量，同时把铺满伪记录的畸形文件钉住。</summary>
    public const int EocdHitLimit = 1024;

    /// <summary>「往下找」的两道保险丝（目录数与文件数各一道），不按秒。它是**询问点**、不是截断点。</summary>
    public const int NestedScanMaxDirs = 10000;

    /// <summary>同上，按已枚举到的文件数算。</summary>
    public const int NestedScanMaxFiles = 10000;

    /// <summary>同一次操作里只问一次：答过「继续」之后，后面的根不再问，取舍由那一次决定。</summary>
    /// <remarks>答「不再找」的那些根不置真，下一个根照问。调用方在同一批里串行调用，闭包状态不跨批复用。</remarks>
    internal static Func<int, int, bool>? AskOncePerBatch(Func<int, int, bool>? ask)
    {
        if (ask is null)
        {
            return null;
        }

        var answered = false;
        return (directories, files) => answered || (answered = ask(directories, files));
    }

    /// <summary>tar 的 magic 在偏移 257 处，所以文件头要读到 265 字节。</summary>
    private const int TarMagicOffset = 257;

    private const int ReadHeadBytes = 512;

    /// <summary>
    /// 名字本来就该是压缩包的扩展名。它同时是"用户以为这是压缩包"的判据：
    /// 命中的文件即使没有 magic 也交给引擎试，猜错只是引擎报一句错。
    /// </summary>
    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz",
        "cab", "arj", "lzh", "lzma", "zst", "z", "iso", "001", "z01", "r00",
        "lz4", "tlz4", "lz5", "tlz5", "tzst", "br", "brotli", "tbr", "liz", "tliz",
    };

    /// <summary>brotli 裸流没有任何 magic，只能靠这几个扩展名兜底。</summary>
    private static readonly HashSet<string> BrotliExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "brotli", "tbr",
    };

    /// <summary>有可能垫了压缩包的马甲家族。刻意收窄：全盘扫一个文件要读完整文件。</summary>
    private static readonly HashSet<string> CarrierExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "avi", "mov", "wmv", "flv", "ts", "rmvb", "rm", "m4v",
        "mpg", "mpeg", "webm", "3gp", "f4v",
        "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "avif",
        "txt", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "epub",
        "apk", "exe", "dll", "bin", "dat", "msi",
    };

    /// <summary>8.3 时代沿用至今的保留设备名。文档只写 1 到 9，没有 COM0 与 LPT0。</summary>
    private static readonly HashSet<string> ReservedDevices = BuildReservedDevices();

    /// <summary>去掉长路径前缀时要认的两种写法。</summary>
    private static readonly string[] LongPathPrefixes = [@"\\?\", @"\\.\"];

    private static HashSet<string> BuildReservedDevices()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        };
        for (var i = 1; i <= 9; i++)
        {
            set.Add("COM" + i.ToString(CultureInfo.InvariantCulture));
            set.Add("LPT" + i.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var mark in "¹²³")
        {
            set.Add("COM" + mark);
            set.Add("LPT" + mark);
        }

        return set;
    }

    /// <summary>读文件头判真实格式。读不了时返回 <see cref="ArchiveFormat.Unknown"/>。</summary>
    /// <remarks>
    /// 本方法只回答"格式是什么"。要区分"读不了"与"真不是压缩包"，用 <see cref="Classify"/>：
    /// 那两种结论在用户侧是两件事。
    /// </remarks>
    public static ArchiveFormat DetectFormat(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (IsDevicePath(path))
        {
            return ArchiveFormat.Unknown;
        }

        var (head, _) = ReadHead(path);
        return head is null ? ArchiveFormat.Unknown : FormatOfHead(head, path);
    }

    /// <summary>
    /// 识别一个文件的<b>唯一入口</b>。单文件入口与目录入口都调它，
    /// 于是"同一个文件单独拖进来与放进文件夹给出同一结论"是结构性成立的。
    /// 它只回答"真实是什么"，不看用户配置的不处理名单，那个判断在 <see cref="ExcludedExt"/>。
    /// </summary>
    public static ProbeResult Classify(string path, ProbeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var opts = options ?? ProbeOptions.Default;
        var name = Path.GetFileName(path);

        if (IsDevicePath(path))
        {
            return new ProbeResult(
                path, ProbeStatus.ReservedDevice, ArchiveFormat.Unknown, VolKind.None,
                Fill(ProbeText.ReservedDeviceAt, name), CarrierProbe.NotCarrier);
        }

        var volume = ClassifyVolume(path);
        var (head, why) = ReadHead(path);
        if (head is null)
        {
            return new ProbeResult(
                path, ProbeStatus.Unreadable, ArchiveFormat.Unknown, volume.Kind,
                Fill(ProbeText.UnreadableAt, name, why), CarrierProbe.NotCarrier);
        }

        var magic = FormatOfMagic(head);
        var ext = ExtOf(path);
        var format = magic is ArchiveFormat.Unknown && BrotliExts.Contains(ext) ? ArchiveFormat.Brotli : magic;
        if (magic.IsArchive())
        {
            // magic 判出来的：结论就是格式本身，不带兜底说明。
            return new ProbeResult(
                path, ProbeStatus.Candidate, magic, volume.Kind, string.Empty, CarrierProbe.NotCarrier);
        }

        if (ArchiveExts.Contains(ext))
        {
            // 按扩展名兜底猜的：名字像包而文件头里没有签名，这一档要在结论里说清是猜的。
            return new ProbeResult(
                path, ProbeStatus.Candidate, format, volume.Kind,
                Fill(ProbeText.CandidateByExtensionAt, ext), CarrierProbe.NotCarrier);
        }

        // 文件头不是包、名字也不像包，只剩"身体里藏着一个"这一种可能。
        var carrier = opts.Carriers ? InspectCarrier(path, opts) : CarrierProbe.NotCarrier;
        var note = carrier.WorthHandling ? CarrierNote(carrier) : ProbeText.NotArchive;
        return new ProbeResult(path, ProbeStatus.NotArchive, format, volume.Kind, note, carrier);
    }

    /// <summary>内容像压缩包，但扩展名不是压缩包，也就是伪装。分卷成员不算：四种分卷命名都带数字后缀，不是伪装。</summary>
    public static bool IsDisguised(string path) =>
        DetectFormat(path).IsArchive()
        && !ArchiveExts.Contains(ExtOf(path))
        && ClassifyVolume(path).Kind is VolKind.None;

    /// <summary>取扩展名，小写、不含点。无扩展名返回空串。</summary>
    /// <remarks>
    /// 语义照抄 Python 的 <c>rpartition</c> 版：以点开头的隐藏文件名也算有扩展名，
    /// 而 <c>Path.GetExtension</c> 会把它们判成没有扩展名。两者不一致会让伪装判据漏判。
    /// </remarks>
    public static string ExtOf(string path)
    {
        var name = Path.GetFileName(path);
        var at = name.LastIndexOf('.');
        if (at < 0 || name.Length == 1)
        {
            return string.Empty;
        }

        return name[(at + 1)..].ToLowerInvariant();
    }

    /// <summary>这个扩展名是不是包扩展名或马甲家族里的一个。</summary>
    public static bool IsKnownExtension(string ext)
    {
        var token = ext.TrimStart('.');
        return token.Length > 0 && (ArchiveExts.Contains(token) || CarrierExts.Contains(token));
    }

    /// <summary>随包引擎读不读得了这个格式。编解码器在 <c>7z.dll</c> 里，只有 exe 时读不了。</summary>
    public static EngineAbility ReadabilityOf(ArchiveFormat format, string? sevenZipExe)
    {
        if (!format.IsArchive())
        {
            return EngineAbility.NotArchive;
        }

        if (!format.NeedsCodecDll())
        {
            return EngineAbility.CanRead;
        }

        var directory = sevenZipExe is null ? null : Path.GetDirectoryName(sevenZipExe);
        return directory is not null && File.Exists(Path.Combine(directory, "7z.dll"))
            ? EngineAbility.CanRead
            : EngineAbility.NeedsCodecDll;
    }

    /// <summary>能力档的一句话结论。读得了与判不出都返回空串，调用方按需填自己的说法。</summary>
    public static string AbilityNote(EngineAbility ability) => ability switch
    {
        EngineAbility.NeedsCodecDll => ProbeText.EngineNeedsCodecDll,
        EngineAbility.NotArchive => ProbeText.NotArchive,
        _ => string.Empty,
    };

    /// <summary>格式的中文名。</summary>
    public static string FormatName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ProbeText.FormatZip,
        ArchiveFormat.SevenZip => ProbeText.FormatSevenZip,
        ArchiveFormat.Rar => ProbeText.FormatRar,
        ArchiveFormat.Rar5 => ProbeText.FormatRar5,
        ArchiveFormat.Tar => ProbeText.FormatTar,
        ArchiveFormat.GZip => ProbeText.FormatGZip,
        ArchiveFormat.Lz4 => ProbeText.FormatLz4,
        ArchiveFormat.Lz5 => ProbeText.FormatLz5,
        ArchiveFormat.Zstd => ProbeText.FormatZstd,
        ArchiveFormat.Brotli => ProbeText.FormatBrotli,
        ArchiveFormat.Mp4 => ProbeText.FormatMp4,
        _ => ProbeText.FormatUnknown,
    };

    /// <summary>
    /// 格式的短名。用于「内嵌 ZIP」这类紧凑写法，也用于清单类型列的短标签 ——
    /// 类型列宽只放得下 2 至 4 字，长串截断之后信息量为零。
    /// </summary>
    public static string ShortFormat(ArchiveFormat format) => (format switch
    {
        ArchiveFormat.Zip => ProbeText.ShortZip,
        ArchiveFormat.SevenZip => ProbeText.ShortSevenZip,
        ArchiveFormat.Rar => ProbeText.ShortRar,
        ArchiveFormat.Rar5 => ProbeText.ShortRar,
        ArchiveFormat.Tar => ProbeText.ShortTar,
        ArchiveFormat.GZip => ProbeText.ShortGZip,
        ArchiveFormat.Lz4 => ProbeText.ShortLz4,
        ArchiveFormat.Lz5 => ProbeText.ShortLz5,
        ArchiveFormat.Zstd => ProbeText.ShortZstd,
        ArchiveFormat.Brotli => ProbeText.ShortBrotli,
        ArchiveFormat.Mp4 => ProbeText.ShortVideo,
        _ => ProbeText.ShortUnknown,
    });

    /// <summary>给界面用的一行描述。次卷不写成"分卷 N"，那会把人引到包坏了那个方向。</summary>
    public static string Describe(ArchiveFormat format, VolumeInfo? volume = null)
    {
        var label = FormatName(format);
        if (volume is null || !volume.IsSplit)
        {
            return label;
        }

        return label + (volume.IsFirst
            ? Fill(ProbeText.VolumePartAt, volume.Index)
            : ProbeText.VolumeLater);
    }

    /// <summary>马甲文件的一句话结论。没有找到包时是空串。</summary>
    public static string CarrierNote(CarrierProbe carrier) => carrier switch
    {
        { Embedded: { } embedded } => Fill(
            ProbeText.EmbeddedAt, FormatName(embedded.Format), HumanSize(embedded.Offset)),
        { Kind: Carrier.AbsoluteLayout } => ProbeText.CarrierAbsoluteLayout,
        { Kind: Carrier.EngineDirect } => ProbeText.CarrierEngineDirect,
        _ => string.Empty,
    };

    /// <summary>字节数换算成给人看的写法。</summary>
    public static string HumanSize(long bytes) => bytes switch
    {
        >= 1L << 30 => Fill(ProbeText.SizeGbAt, bytes / (double)(1L << 30)),
        >= 1L << 20 => Fill(ProbeText.SizeMbAt, bytes / (double)(1L << 20)),
        >= 1L << 10 => Fill(ProbeText.SizeKbAt, bytes / 1024.0),
        _ => Fill(ProbeText.SizeBytesAt, bytes),
    };

    /// <summary>把用户填的扩展名名单规整成小写、不带点、不带前导星号。</summary>
    public static HashSet<string> NormalizeExts(IEnumerable<string>? exts)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in exts ?? [])
        {
            var value = raw.Trim().ToLowerInvariant().TrimStart('*').TrimStart('.');
            if (value.Length > 0)
            {
                set.Add(value);
            }
        }

        return set;
    }

    /// <summary>这个文件的扩展名在不处理的名单里吗。命中就返回那个扩展名，否则空串。</summary>
    /// <remarks>
    /// "要不要跳过它"的唯一判据，识别层的结论里刻意不带这一档：名单来自用户配置而不是文件里的事实，
    /// 判据跟着调用方走才谈得上两个入口同一结论。命中的文件仍然要列出来写明不处理 ——
    /// 从候选单里悄悄抹掉，用户看到的是"没有可解压的压缩包"，而真相是"有，按你的名单没解"。
    /// 伪装那一支同样管住：一个文件是不是马甲，不改变名单命中就不处理这件事。
    /// </remarks>
    public static string ExcludedExt(string path, IEnumerable<string>? exts)
    {
        var ext = ExtOf(path);
        return ext.Length > 0 && NormalizeExts(exts).Contains(ext) ? ext : string.Empty;
    }

    /// <summary>这个路径的名字是不是 Windows 保留设备，是就返回设备名。</summary>
    public static string? ReservedDeviceOf(string path)
    {
        var raw = path;
        foreach (var prefix in LongPathPrefixes)
        {
            if (raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                raw = raw[prefix.Length..];
                break;
            }
        }

        var name = Path.GetFileName(raw);
        if (string.IsNullOrEmpty(name))
        {
            name = raw;
        }

        if (name.Length == 0)
        {
            return null;
        }

        var stem = name.Split('.')[0].TrimEnd();
        if (stem.Length == 0)
        {
            stem = name.Trim();
        }

        if (stem.Length > 0 && ReservedDevices.Contains(stem.ToUpperInvariant()))
        {
            return stem.ToUpperInvariant();
        }

        var stripped = name.TrimEnd('.', ' ');
        if (stripped.Length > 0)
        {
            var head = stripped.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (ReservedDevices.Contains(head))
            {
                return head;
            }
        }

        return null;
    }

    /// <summary>
    /// 这个路径<b>现在</b>会走到设备而不是文件吗。名字命中保留设备，且它不是一个真实文件时为真。
    /// 读文件头之前必须过这一道：读设备会永久阻塞，取消与超时都没有机会执行。
    /// </summary>
    public static bool IsDevicePath(string path)
    {
        if (ReservedDeviceOf(path) is null)
        {
            return false;
        }

        try
        {
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>保留设备名的一句话说明，给界面与日志当失败原因用。</summary>
    public static string DescribeReserved(string name) => Fill(ProbeText.ReservedDeviceDetailAt, name);

    internal static string Fill(string template, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);

    /// <summary>读文件头。返回 <c>null</c> 表示读不了，第二项是原因。</summary>
    private static (byte[]? Head, string Why) ReadHead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[ReadHeadBytes];
            var read = stream.ReadAtLeast(buffer, ReadHeadBytes, throwOnEndOfStream: false);
            return (read == ReadHeadBytes ? buffer : buffer[..read], string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>文件头到格式，含 Brotli 的扩展名兜底。只看 magic 与名字，不碰盘。</summary>
    private static ArchiveFormat FormatOfHead(ReadOnlySpan<byte> head, string path)
    {
        var magic = FormatOfMagic(head);
        return magic is ArchiveFormat.Unknown && BrotliExts.Contains(ExtOf(path))
            ? ArchiveFormat.Brotli
            : magic;
    }

    /// <summary>只认签名的那一档。兜底猜的格式不算在它里面，两种结论由 <see cref="Classify"/> 分开。</summary>
    private static ArchiveFormat FormatOfMagic(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(ZipLocalMagic) || head.StartsWith(ZipEocdMagic) || head.StartsWith(ZipDataMagic))
        {
            return ArchiveFormat.Zip;
        }

        if (head.StartsWith(Rar5Magic))
        {
            return ArchiveFormat.Rar5;
        }

        if (head.StartsWith(Rar4Magic))
        {
            return ArchiveFormat.Rar;
        }

        if (head.StartsWith(SevenZMagic))
        {
            return ArchiveFormat.SevenZip;
        }

        if (head.StartsWith(GzipMagic))
        {
            return ArchiveFormat.GZip;
        }

        if (head.StartsWith(Lz4Magic))
        {
            return ArchiveFormat.Lz4;
        }

        if (head.StartsWith(Lz5Magic))
        {
            return ArchiveFormat.Lz5;
        }

        if (head.StartsWith(ZstdMagic))
        {
            return ArchiveFormat.Zstd;
        }

        if (head.Length > TarMagicOffset + 5 && head.Slice(TarMagicOffset, 5).SequenceEqual(TarMagic))
        {
            return ArchiveFormat.Tar;
        }

        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual(FtypMagic))
        {
            return ArchiveFormat.Mp4;
        }

        return ArchiveFormat.Unknown;
    }
}
