namespace Bbu.Core.Texts;

/// <summary>
/// 识别层的文案：格式名、分卷说明、探测结论、扫描日志。
/// 本类只放用户能看到的字符串。
/// </summary>
public static class ProbeText
{
    public const string FormatUnknown = "未知格式";

    public const string FormatZip = "ZIP 压缩包";

    public const string FormatSevenZip = "7z 压缩包";

    public const string FormatRar = "RAR 压缩包";

    public const string FormatRar5 = "RAR5 压缩包";

    public const string FormatTar = "TAR 归档";

    public const string FormatGZip = "GZip 压缩";

    public const string FormatLz4 = "LZ4 压缩";

    public const string FormatLz5 = "LZ5 压缩";

    public const string FormatZstd = "Zstd 压缩";

    public const string FormatBrotli = "Brotli 压缩";

    public const string FormatMp4 = "视频（非压缩包）";

    // ── 清单类型列的短标签 ──
    // 类型列宽只放得下 2 至 4 字，长串截断之后信息量为零；全称改挂 title。

    public const string ShortZip = "ZIP";

    public const string ShortSevenZip = "7z";

    public const string ShortRar = "RAR";

    public const string ShortTar = "tar";

    public const string ShortGZip = "gz";

    public const string ShortLz4 = "lz4";

    public const string ShortLz5 = "lz5";

    public const string ShortZstd = "zstd";

    public const string ShortBrotli = "br";

    public const string ShortVideo = "视频";

    public const string ShortUnknown = "未识别";

    /// <summary>主卷的卷号后缀。占位符是序号。</summary>
    public const string VolumePartAt = "，分卷 {0}";

    /// <summary>次卷不许被标成"分卷 N"，那个写法会把用户引到包坏了那个方向。</summary>
    public const string VolumeLater = "，次卷（不是主卷）";

    /// <summary>内嵌压缩包的一句话。占位符是格式名与偏移量。</summary>
    public const string EmbeddedAt = "内嵌 {0}，偏移 {1}";

    /// <summary>绝对偏移布局：不许切包，原样交给引擎。</summary>
    public const string CarrierAbsoluteLayout = "尾部绝对偏移布局，原样直读";

    /// <summary>引擎自己会跳过假头。</summary>
    public const string CarrierEngineDirect = "内嵌压缩包，引擎可直接读";

    public const string SizeBytesAt = "{0} 字节";

    public const string SizeKbAt = "{0:F1} KB";

    public const string SizeMbAt = "{0:F1} MB";

    public const string SizeGbAt = "{0:F2} GB";

    /// <summary>无法读取文件头。占位符是文件名与失败原因。</summary>
    public const string UnreadableAt = "无法读取，未能判断是否为压缩包（{0}）：{1}";

    /// <summary>保留设备名，在读文件头之前挡下。</summary>
    public const string ReservedDeviceAt = "{0} 是 Windows 保留设备名，已跳过";

    /// <summary>保留设备名的完整说明，给需要解释原因的调用方。</summary>
    public const string ReservedDeviceDetailAt =
        "{0} 是 Windows 保留设备名（DOS 设备），磁盘上无法读取它：在部分系统上读它会永久卡住，已跳过";

    /// <summary>缺主卷。占位符是缺的文件名与这一组在场的卷数。</summary>
    public const string MissingVolumeAt = "缺少主卷 {0}，{1} 个分卷已跳过";

    public const string NotArchive = "不是压缩包，跳过";

    /// <summary>文件名像压缩包、文件头里却没有签名：这一档是按扩展名交给引擎尝试的，结论要说清是猜的。占位符是扩展名。</summary>
    public const string CandidateByExtensionAt = "文件名看起来像压缩包（.{0}），文件头里没有签名：按扩展名交给引擎尝试";

    /// <summary>扩展名在不处理的名单里。判据由 <c>Probe.ExcludedExt</c> 给，这句给调用方展示用。</summary>
    public const string ExcludedAt = "{0} 已排除";

    /// <summary>编解码器只在 7z.dll 里，而随包只有 7z.exe。</summary>
    public const string EngineNeedsCodecDll = "引擎无法读取该格式（缺少 7z.dll 的编解码器）";

    /// <summary>往下找的入口文件夹无法读取。占位符是文件夹名与原因。</summary>
    public const string LogDirectoryUnreadableAt = "无法读取这个文件夹，未往下找：{0}（{1}）";

    /// <summary>子文件夹无法读取，跳过它继续。占位符是文件夹名与原因。</summary>
    public const string LogSubdirectoryUnreadableAt = "无法读取子文件夹，已跳过：{0}（{1}）";

    /// <summary>往下找撞上保险丝且用户没让继续。「停止」的措辞与取消那一条统一，不出现两种说法。</summary>
    public const string LogScanFuseAt = "已停止：扫描范围过大（超过 {0} 个文件夹或 {1} 个文件），子文件夹扫描未完成（这一层可能还有压缩包未列出）";

    /// <summary>用户点了停止。</summary>
    public const string LogScanCancelled = "已停止：不再往下找";

    /// <summary>跳过链接文件夹的记账。占位符是跳过的个数。</summary>
    public const string LogSkippedLinksAt = "跳过 {0} 个符号链接/junction 文件夹（未扫描其内容）";

    /// <summary>一次扫描里被跳过的非压缩包条数。占位符是个数；本层与「往下找」共用这一句。</summary>
    public const string LogSkippedNotArchiveAt = "{0} 个文件不是压缩包，已跳过";
}
