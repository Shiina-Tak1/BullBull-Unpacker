using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 识别层的扫描：这一层的候选包，以及"往下找"。
/// 扫描与穿透共用这一份判据，两处各写一份必然漂移到"界面列出来的"和"实际解的不是同一批"。
/// </summary>
public static partial class Probe
{
    /// <summary>只看这一层：从一批文件里挑出可以直接送引擎的候选包，不含子目录。</summary>
    /// <remarks>
    /// 两条通道合起来出结论：逐个识别（magic 或扩展名名单），以及分卷归组只留主卷。
    /// 伪装包不算这里的候选，它由 <see cref="InspectCarrier"/> 那条通道单独收，理由是全盘扫的代价高。
    /// 两条通道的产物都要过 <paramref name="excludeExts"/>：分卷主卷是另一条路径，
    /// 只在逐个识别那一侧判排除的话，名单里写着 zip 也挡不住那个刚好是 zipsplit 主卷的包。
    /// </remarks>
    public static IReadOnlyList<string> CandidatesFrom(
        IEnumerable<string> files,
        ProbeOptions? options = null,
        IEnumerable<string>? excludeExts = null)
    {
        var opts = options ?? ProbeOptions.Default;
        var list = files.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var plains = new List<string>();
        var skipped = 0;
        foreach (var path in list)
        {
            // 分卷成员走归组那条通道；弱命名的 `.NNN` 是例外，见 Probe.IsStandaloneVolume。
            if (ClassifyVolume(path).Kind is not VolKind.None && !IsStandaloneVolume(path))
            {
                continue;
            }

            // 名单里的文件不探测、也不写日志：它既不会被解，也不该在日志里逐条出现。
            if (ExcludedExt(path, excludeExts).Length > 0)
            {
                continue;
            }

            var classified = Classify(path, opts);
            if (classified.IsCandidate)
            {
                plains.Add(path);
            }

            if (classified.Note.Length == 0)
            {
                continue;
            }

            // 「不是压缩包」按批汇总一条，其余结论逐条说，理由见 Collect。
            if (classified.Status is ProbeStatus.NotArchive && !classified.Carrier.WorthHandling)
            {
                skipped++;
                continue;
            }

            Report(opts, classified);
        }

        if (skipped > 0)
        {
            AnnounceNotArchive(opts, skipped);
        }

        return Keep(MainVolumes(GroupVolumes(list), opts.Log), excludeExts).Concat(plains).ToList();
    }

    /// <summary>「不是压缩包」的整批汇总：本层与「往下找」共用这一个入口，逐目录说会刷屏。</summary>
    private static void AnnounceNotArchive(ProbeOptions opts, int count) =>
        opts.Log?.Note(ProbeText.LogSkippedNotArchiveAt, count);

    /// <summary>一条识别结论按结论本身分档说出来：读不了、保留设备名要用户注意，其余是陈述。</summary>
    /// <remarks>成品正文按一条实参传：它带着用户文件名，本身可能含花括号，不能再当模板解析一遍。</remarks>
    private static void Report(ProbeOptions opts, ProbeResult classified)
    {
        if (classified.Status is ProbeStatus.Unreadable or ProbeStatus.ReservedDevice)
        {
            opts.Log?.Problem("{0}", classified.Note);
            return;
        }

        opts.Log?.Note("{0}", classified.Note);
    }

    /// <summary>名单命中的路径全部剔掉。这是"要不要跳过"的唯一判据在通道上的落地。</summary>
    private static IEnumerable<string> Keep(IEnumerable<string> paths, IEnumerable<string>? excludeExts) =>
        paths.Where(path => ExcludedExt(path, excludeExts).Length == 0);

    /// <summary>「往下找」进度回传的节流：每 32 个目录一次，或距上一次过了 200 毫秒。</summary>
    private const int ScanProgressEveryDirs = 32;

    private const long ScanProgressEveryMs = 200;

    /// <summary>在目录的子目录里找候选包，收全而不是只找前两个。</summary>
    /// <remarks>
    /// 三条纪律：
    /// 一是按目录数与文件数各设一道保险丝，不按目录深度，深度是包层的事；
    /// 二是走显式栈，遇到很深的目录树不靠递归；
    /// 三是读不了就记下来继续找别的目录，而"这个目录不在"与"读不动它"要分开，前者没有东西可找。
    /// <paramref name="excludeExts"/> 在这里也要过一遍分卷主卷那条通道，理由与
    /// <see cref="CandidatesFrom"/> 相同：同一个判据管不住全部通道就等于没有。
    /// </remarks>
    public static DirSearch FindArchivesBelow(
        string directory,
        ProbeOptions? options = null,
        IEnumerable<string>? excludeExts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var opts = options ?? ProbeOptions.Default;
        var result = new DirSearch();
        var skippedLinks = 0;

        List<FileSystemInfo> top;
        try
        {
            top = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录不在时 found 为空已经说清"没有东西可找"，不许再置 truncated，
            // 否则产物目录被删掉会被报成"子文件夹没往下找完"。
            result.Truncated = Directory.Exists(directory);
            if (result.Truncated)
            {
                result.Unreadable.Add(directory);
            }

            var name = Path.GetFileName(directory);
            opts.Log?.Problem(ProbeText.LogDirectoryUnreadableAt, name.Length > 0 ? name : directory, ex.Message);
            return result;
        }

        var roots = top.Where(IsRealDir)
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Cast<DirectoryInfo>()
            .ToList();
        skippedLinks += top.Count(IsLinkDir);

        var stack = new Stack<DirectoryInfo>(roots.AsEnumerable().Reverse());
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var lastReport = Environment.TickCount64;

        // 本趟至多问一次：置真之后保险丝不再触发，反复询问只会打断同一趟扫描。
        var asked = false;
        while (stack.Count > 0)
        {
            if (opts.Cancellation.IsCancellationRequested)
            {
                // 用户点了停止：取消不是"没找完"，报成没找完会让用户以为程序自己放弃了。
                result.Cancelled = true;
                opts.Log?.Note(ProbeText.LogScanCancelled);
                break;
            }

            if (!asked
                && (result.ScannedDirs >= opts.MaxDirs || result.ScannedFiles >= opts.MaxFiles))
            {
                // 到点不是直接截断：有人可问就问一句，答「继续」就一直找下去。
                if (opts.AskFuseContinue is { } ask && ask(result.ScannedDirs, result.ScannedFiles))
                {
                    asked = true;
                    continue;
                }

                result.Truncated = true;
                opts.Log?.Problem(ProbeText.LogScanFuseAt, opts.MaxDirs, opts.MaxFiles);
                break;
            }

            var current = stack.Pop();
            if (!visited.Add(FileIdentity.Of(current.FullName)))
            {
                continue;
            }

            result.ScannedDirs++;
            List<FileSystemInfo> entries;
            try
            {
                entries = current.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Truncated = true;
                result.Unreadable.Add(current.FullName);
                opts.Log?.Problem(ProbeText.LogSubdirectoryUnreadableAt, current.Name, ex.Message);
                continue;
            }

            Collect(files: [.. entries.Where(IsPlainFile).Select(entry => entry.FullName)], opts: opts, excludeExts: excludeExts, result: result);

            // 按节流回传：每个目录回传一次会到上万次，界面光排队就来不及画。
            if (opts.OnScanProgress is { } report
                && (result.ScannedDirs % ScanProgressEveryDirs == 0
                    || Environment.TickCount64 - lastReport >= ScanProgressEveryMs))
            {
                lastReport = Environment.TickCount64;
                report(result.ScannedDirs, result.ScannedFiles);
            }

            var subdirs = new List<DirectoryInfo>();
            foreach (var entry in entries)
            {
                if (IsRealDir(entry))
                {
                    subdirs.Add((DirectoryInfo)entry);
                }
                else if (IsLinkDir(entry))
                {
                    skippedLinks++;
                }
            }

            foreach (var dir in subdirs.OrderBy(entry => entry.FullName, StringComparer.Ordinal).Reverse())
            {
                stack.Push(dir);
            }
        }

        if (skippedLinks > 0)
        {
            // 跳过链接不算"没找完"，这是主动不跟；但必须说出来，
            // 用户的目录树里真有 junction 时里面的包就是不会被解，一声不响等于静默漏解。
            opts.Log?.Problem(ProbeText.LogSkippedLinksAt, skippedLinks);
        }

        // 收尾再报一次：节流会把最后一段丢掉，界面上的数停在半路。
        opts.OnScanProgress?.Invoke(result.ScannedDirs, result.ScannedFiles);

        // 整批一条：粒度与「进清单的项」同阶，不随被探测的文件数增长。
        if (result.SkippedNotArchive > 0)
        {
            AnnounceNotArchive(opts, result.SkippedNotArchive);
        }

        DedupFound(result);
        return result;
    }

    private static void Collect(
        List<string> files,
        ProbeOptions opts,
        IEnumerable<string>? excludeExts,
        DirSearch result)
    {
        var skipped = 0;
        result.ScannedFiles += files.Count;
        foreach (var path in files)
        {
            if (ClassifyVolume(path).Kind is not VolKind.None && !IsStandaloneVolume(path))
            {
                continue;
            }

            // 名单里的文件不探测、也不写日志：它既不会被解，也不该让面板按文件数刷屏。
            if (ExcludedExt(path, excludeExts).Length > 0)
            {
                continue;
            }

            var classified = Classify(path, opts);
            result.Results.Add(classified);
            if (classified.IsCandidate)
            {
                result.Found.Add(path);
            }
            else if (classified.Carrier.WorthHandling)
            {
                result.Carriers.Add(path);
            }

            if (classified.Note.Length == 0)
            {
                continue;
            }

            // 「不是压缩包」是最常见的一种，按目录汇总一条；其余结论（读不了、保留设备名、
            // 缺主卷）少见且要用户注意，逐条写，级别按结论本身分档。
            if (classified.Status is ProbeStatus.NotArchive && !classified.Carrier.WorthHandling)
            {
                skipped++;
                continue;
            }

            Report(opts, classified);
        }

        if (skipped > 0)
        {
            result.SkippedNotArchive += skipped;
        }

        result.Found.AddRange(Keep(MainVolumes(GroupVolumes(files), opts.Log), excludeExts));
    }

    /// <summary>
    /// 两个名单都按物理身份去重。同一个包被两条路径到达时按路径去重看不出来，
    /// 下游会把两个名单当成"要解"与"没解"两件事，重了就会既解又报没解。
    /// </summary>
    private static void DedupFound(DirSearch result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (var path in result.Found)
        {
            if (seen.Add(FileIdentity.Of(path)))
            {
                unique.Add(path);
            }
        }

        result.Found.Clear();
        result.Found.AddRange(unique);

        var known = new HashSet<string>(unique.Select(FileIdentity.Of), StringComparer.Ordinal);
        var carriers = new List<string>();
        seen.Clear();
        foreach (var path in result.Carriers)
        {
            var key = FileIdentity.Of(path);
            if (known.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            carriers.Add(path);
        }

        result.Carriers.Clear();
        result.Carriers.AddRange(carriers);
    }

    private static bool IsRealDir(FileSystemInfo entry)
    {
        var attributes = AttributesOf(entry);
        return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsLinkDir(FileSystemInfo entry)
    {
        var attributes = AttributesOf(entry);
        return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsPlainFile(FileSystemInfo entry) =>
        (AttributesOf(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    private static FileAttributes AttributesOf(FileSystemInfo entry)
    {
        try
        {
            return entry.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileAttributes.None;
        }
    }
}
