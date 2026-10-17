namespace Bbu.Core.Models;

/// <summary>
/// 包的真实格式，按文件头判定，不看扩展名。
///
/// 四个 ZS 格式分列而不并成一个裸流：它们的 magic 各不相同，逐格式断言要求分得开；
/// 引擎能力对它们同档，那一档由 <see cref="ArchiveFormats.NeedsCodecDll"/> 回答。
/// <see cref="Mp4"/> 与 <see cref="Unknown"/> 也分开：前者是"真视频、正常跳过"，
/// 后者是"判不出来、值得再试"，两种用户可见结论。
/// </summary>
public enum ArchiveFormat
{
    /// <summary>判不出来。它不等于"不是压缩包"，那两种结论由 <see cref="ProbeStatus"/> 分。</summary>
    Unknown,

    Zip,

    SevenZip,

    Rar,

    Rar5,

    Tar,

    GZip,

    /// <summary>LZ4 帧。</summary>
    Lz4,

    Lz5,

    Zstd,

    /// <summary>Brotli 裸流没有 magic，只能靠扩展名兜底。</summary>
    Brotli,

    /// <summary>视频，不是压缩包。</summary>
    Mp4,
}

/// <summary>格式集上的判定。</summary>
public static class ArchiveFormats
{
    /// <summary>算不算压缩包。判不出格式与视频都不算。</summary>
    public static bool IsArchive(this ArchiveFormat format) =>
        format is not (ArchiveFormat.Unknown or ArchiveFormat.Mp4);

    /// <summary>
    /// 编解码器是否只在随包的 <c>7z.dll</c> 里，而不在 <c>7z.exe</c> 本身。
    /// 这一档的格式只有 exe 时读不了，报出来的错和密码错误逐字相同。
    /// </summary>
    public static bool NeedsCodecDll(this ArchiveFormat format) =>
        format is ArchiveFormat.Lz4 or ArchiveFormat.Lz5 or ArchiveFormat.Zstd or ArchiveFormat.Brotli;
}
