using System;
using System.Collections.Generic;

namespace Bbu.Core.Configuration;

/// <summary>产物落点：跟源包同目录，还是用 <see cref="AppConfig.OutputDirectory"/>。</summary>
public enum OutputMode
{
    Same,
    Custom,
}

/// <summary>产物目录重名时怎么办：改名 / 覆盖 / 跳过。</summary>
public enum ConflictPolicy
{
    Rename,
    Overwrite,
    Skip,
}

/// <summary>
/// 用户配置。
/// 键名不沿用 1.1.0。
///
/// 两条容错语义与 1.1.0 一致：缺字段回落默认值；单个字段类型不对时只回落那一个字段，
/// 不整份丢弃（整份丢弃会静默抹掉用户调过的所有值）。
///
/// 本类型不提供复制一份的入口。宿主与设置页共用同一个实例，保存、列宽落盘与恢复默认
/// 都就地改字段；换实例会让两处各持一份，列宽与排除名单因此互相写回旧值。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置结构版本。升级时按它决定要不要迁移。</summary>
    public const int CurrentVersion = 1;

    /// <summary>不处理的扩展名（界面项名「不处理的文件类型」）。这一份是唯一来源。</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedExtensions = new[]
    {
        "apk", "xapk", "apks", "iso", "img", "dmg", "msi", "deb", "rpm", "jar",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx",
    };

    public int Version { get; set; } = CurrentVersion;

    /// <summary>单个任务超时（分钟）。</summary>
    public double TimeoutMinutes { get; set; } = 30.0;

    public int MaxDepth { get; set; } = 5;

    public double MinFreeGb { get; set; } = 1.0;

    public bool FlattenSingleChild { get; set; } = true;

    public bool RemoveSource { get; set; }

    /// <summary>删中间层包（只删嵌在里面的，不碰用户原包）。</summary>
    public bool RemoveIntermediate { get; set; } = true;

    public bool CleanDelete { get; set; } = true;

    public bool ScanAppended { get; set; } = true;

    /// <summary>扫描文件夹时往下找的范围，见 <see cref="FolderScan"/>。</summary>
    public FolderScan FolderScan { get; set; } = FolderScan.Below;

    /// <summary>试密码的并行度。0 = 自动 = 本机逻辑核数。</summary>
    public int PasswordWorkers { get; set; }

    public OutputMode OutputMode { get; set; } = OutputMode.Same;

    /// <summary><see cref="OutputMode.Custom"/> 时的输出根。</summary>
    public string OutputDirectory { get; set; } = "";

    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;

    /// <summary>不处理的扩展名，小写、无点。</summary>
    public IReadOnlyList<string> ExcludedExtensions { get; set; } = DefaultExcludedExtensions;

    public string Language { get; set; } = "zh-CN";

    public IReadOnlyList<double> TableColumnWidths { get; set; } = Array.Empty<double>();
}

/// <summary>扫描文件夹时往下找多深：只扫这一层 / 往下找 / 往下找并深挖内嵌包。</summary>
public enum FolderScan
{
    TopOnly,
    Below,
    Deep,
}
