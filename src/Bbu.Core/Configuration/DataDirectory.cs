using System;
using System.Globalization;
using System.IO;
using Bbu.Core.Integration;
using Bbu.Core.Texts;
using IOPath = System.IO.Path;

namespace Bbu.Core.Configuration;

/// <summary>数据目录的三种取值。判定集中在 <see cref="DataDirectory.Resolve"/> 一处。</summary>
public enum DataDirectoryKind
{
    Pinned,

    Portable,

    Roaming,
}

/// <summary>
/// 用户数据放哪儿：<c>config.json</c> / <c>密码本.txt</c> / <c>BullBullUnpacker.log</c>。
///
/// 分两层：**数据根**（程序目录或 <c>%APPDATA%\BullBullUnpacker</c>，判定只在这一层做）
/// 与它下面的 <c>userdata\</c>（那三样都收在里面，便携包根目录因此只剩程序本体与几个目录）。
///
/// 顺序：<c>BBU_DATA_DIR</c> 环境变量 → 程序目录（真写探针）→ <c>%APPDATA%\BullBullUnpacker</c>。
/// 真写探针是判据：<c>os.access</c> 这类"看起来能写"的判定在只读盘与虚拟化目录上不可信。
/// 三档都创建失败时降级而不是抛，**启动路径上的目录创建不许抛**（B-2026-151）。
/// </summary>
public static class DataDirectory
{
    /// <summary>钉住数据根的环境变量（测试用；正常使用不设）。</summary>
    public const string EnvOverride = "BBU_DATA_DIR";

    /// <summary>文件系统里这一层的名字。</summary>
    public const string AppFolderName = "BullBullUnpacker";

    private static string? _cachedRoot;
    private static string? _noteTemplate;
    private static string? _noteRoot;

    /// <summary>降级时生成的说明（原委与实际落点），由装配处在日志就绪后写入；未降级时为 <c>null</c>。</summary>
    public static string? LastNote =>
        _noteTemplate is null ? null : string.Format(CultureInfo.CurrentCulture, _noteTemplate, _noteRoot);

    /// <summary>该说明的文案模板，实参是 <see cref="ResolveRoot"/> 的结果；未降级时为 <c>null</c>。</summary>
    public static string? LastNoteTemplate => _noteTemplate;

    /// <summary>数据根。判定只在这里做；<paramref name="packaged"/> 是包身份的显式覆盖，默认按真实探测取值。</summary>
    public static string ResolveRoot(string? appDirectory = null, bool useCache = true, bool? packaged = null)
    {
        if (useCache && _cachedRoot is not null)
        {
            return _cachedRoot;
        }

        var resolved = ResolveCore(appDirectory, packaged ?? PackageIdentity.IsPackaged);
        if (useCache)
        {
            _cachedRoot = resolved;
        }

        return resolved;
    }

    /// <summary>用户数据目录：数据根下的 <c>userdata\</c>，设置、密码本与日志都在它下面。</summary>
    public static string Resolve(string? appDirectory = null, bool useCache = true, bool? packaged = null)
    {
        var userData = UserDataOf(ResolveRoot(appDirectory, useCache, packaged));

        // 这一层同样在启动路径上：创建失败照样把路径交回去，写盘那一步会走失败通道。
        TryCreate(userData);
        return userData;
    }

    /// <summary>某个数据根对应的用户数据目录。只拼一层，不改判定。</summary>
    public static string UserDataOf(string root) =>
        IOPath.Combine(Normalize(root), AppInfo.UserDataFolderName);

    /// <summary>日志文件的全路径：用户数据目录下平铺一个文件，不再单开一层。</summary>
    public static string LogPath(string? appDirectory = null, bool useCache = true, bool? packaged = null) =>
        IOPath.Combine(Resolve(appDirectory, useCache, packaged), AppInfo.LogFileName);

    /// <summary>
    /// 这次用的是哪一档，供设置页显示。参数是**数据根**，不是 userdata 那一层。
    /// 「钉住」判的是落点是不是那个目录：钉住的目录创建失败时判定会忽略它（B-2026-151）。
    /// </summary>
    public static DataDirectoryKind Classify(string root, string? appDirectory = null)
    {
        var current = Normalize(root);
        if (IsPinned(out var pinned) && string.Equals(current, pinned, StringComparison.OrdinalIgnoreCase))
        {
            return DataDirectoryKind.Pinned;
        }

        var app = Normalize(appDirectory ?? AppContext.BaseDirectory);
        return string.Equals(current, app, StringComparison.OrdinalIgnoreCase)
            ? DataDirectoryKind.Portable
            : DataDirectoryKind.Roaming;
    }

    /// <summary>
    /// 另一侧的**用户数据目录**，仅当那里真有一本密码本时返回；钉住根或另一侧没有时返回空。参数是数据根。
    /// 只看"目录存在"会在另一侧从来没放过密码本的机器上误报（B-2026-058）。
    /// </summary>
    public static string? AlternativeCandidate(string root, string? appDirectory = null)
    {
        if (IsPinned(out _))
        {
            return null;
        }

        var app = Normalize(appDirectory ?? AppContext.BaseDirectory);
        var current = Normalize(root);
        var otherRoot = string.Equals(current, app, StringComparison.OrdinalIgnoreCase)
            ? RoamingDirectory()
            : app;
        if (string.Equals(current, Normalize(otherRoot), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return HasVault(otherRoot) ? UserDataOf(otherRoot) : null;
    }

    /// <summary>那一侧的数据根里有没有一本密码本。文件在但读不动时按「有」处理：报出优于漏报。</summary>
    private static bool HasVault(string root)
    {
        try
        {
            return File.Exists(IOPath.Combine(UserDataOf(root), VaultText.BookFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string ResolveCore(string? appDirectory, bool packaged)
    {
        _noteTemplate = null;
        var app = Normalize(appDirectory ?? AppContext.BaseDirectory);

        // ① 钉住的一档优先，**创建失败就忽略它**接着往下判：原话是「不许抛：data_dir() 在
        // 启动路径上，抛出去就是起不来」。说明在落点定下后一并记入。
        var pinned = IsPinned(out var pinPath) ? pinPath : null;
        var pinIgnored = pinned is not null && !TryCreate(pinned);
        if (pinned is not null && !pinIgnored)
        {
            return pinned;
        }

        // ② 打包形态跳过真写探针（容器把写入报成假成功）；③ 其余情况按程序目录能否真写落档。
        if (packaged || !IsWritable(app))
        {
            var roaming = RoamingDirectory();
            if (TryCreate(roaming))
            {
                return Finish(roaming, pinIgnored
                    ? LogText.DataDirPinIgnoredAt
                    : packaged ? null : LogText.DataDirRoamingAt);
            }

            // ④ 用户目录也创建失败 → 退回程序目录并**照常启动**：报错位置与探针一致。
            return Finish(app, pinIgnored ? LogText.DataDirPinIgnoredAt : LogText.DataDirFallbackAt);
        }

        return Finish(app, pinIgnored ? LogText.DataDirPinIgnoredAt : null);
    }

    /// <summary>定下数据根并留下说明；模板为空表示这一档就是首选，不需要解释。</summary>
    private static string Finish(string root, string? template)
    {
        _noteTemplate = template;
        _noteRoot = root;
        return root;
    }

    /// <summary>建目录，创建失败只回报不抛。启动路径上的目录创建一律走它，理由见类注释。</summary>
    private static bool TryCreate(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsPinned(out string directory)
    {
        var raw = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            directory = Normalize(raw);
            return true;
        }

        directory = "";
        return false;
    }

    /// <summary>用户目录那一档的根：先读 <c>%APPDATA%</c>，取不到时回落系统的 ApplicationData 目录。</summary>
    private static string RoamingDirectory()
    {
        var baseDirectory = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        return IOPath.Combine(baseDirectory, AppFolderName);
    }

    private static string Normalize(string path) =>
        IOPath.TrimEndingDirectorySeparator(IOPath.GetFullPath(path));

    private static bool IsWritable(string directory)
    {
        if (!TryCreate(directory))
        {
            return false;
        }

        try
        {
            var probe = IOPath.Combine(directory, $".bbu-write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
