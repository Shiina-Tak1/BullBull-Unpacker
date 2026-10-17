namespace Bbu.Core.Services;

/// <summary>
/// 一次列目录就能拿到的包信息，避免反复调引擎。<c>Encrypted</c> 是三态：加密、明确没加密、
/// 判不了 —— 判不了不等于「没加密」，对未加密的包引擎会忽略 <c>-p</c>，验证必然「成功」。
/// </summary>
public sealed record ArchiveInfo
{
    /// <summary>判不了时为 <c>null</c>。</summary>
    public bool? Encrypted { get; init; }

    /// <summary>连文件名都加密了：没密码根本列不出目录。</summary>
    public bool HeaderEncrypted { get; init; }

    /// <summary>第一个文件条目。目录条目不能用来验证密码。</summary>
    public string? FirstEntry { get; init; }

    /// <summary>最小的文件条目，便宜验证优先用它。</summary>
    public string? SmallestEntry { get; init; }

    /// <summary>列出来的条目数，含目录。</summary>
    public int EntryCount { get; init; }

    /// <summary>所有文件条目声明的大小之和。声明值不可信，只用来挡「误拖一个巨大的包」。</summary>
    public long TotalSize { get; init; }

    /// <summary>引擎能把这个文件当压缩包读出来。</summary>
    public bool ReadOk { get; init; }

    /// <summary>判不了时的默认值。</summary>
    public static ArchiveInfo Unknown { get; } = new();
}
