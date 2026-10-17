using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Models;
using Bbu.Core.Services;

namespace Bbu.Core.Tests;

/// <summary>
/// 穿透层夹具的搬运与驱动。
///
/// 样本在 <c>tests\fixtures\pierce\&lt;样本名&gt;\</c>。
/// 每次都把样本**复制**到临时目录再跑：穿透默认把产物开在源文件旁边，
/// 原地跑一次就把夹具污染了，而且第二次跑的结论会变成「输出目录已存在」。
/// </summary>
internal static class PierceHarness
{
    /// <summary>把一份样本复制到临时目录，返回那份副本的路径。</summary>
    public static string CopySample(string sample)
    {
        var source = Path.Combine(TestPaths.PierceRoot, sample);
        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException("找不到穿透层夹具：" + source);
        }

        var target = Path.Combine(
            AppContext.BaseDirectory, "_test-tmp", "pierce", Guid.NewGuid().ToString("N"), sample);
        CopyTree(source, target);
        return target;
    }

    /// <summary>样本的入口：里面恰好一个文件或一个目录时用它，否则用目录本身。</summary>
    public static string EntryOf(string directory)
    {
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        return entries.Count == 1 ? entries[0].FullName : directory;
    }

    /// <summary>跑一趟穿透，把结果与日志一起带回来。</summary>
    public static async Task<PierceRun> RunAsync(
        string sample,
        int maxDepth = 5,
        double minFreeGb = 0,
        ConflictPolicy conflict = ConflictPolicy.Rename,
        string? outputRoot = null,
        bool removeIntermediate = true,
        bool removeSource = false,
        bool flatten = true,
        bool scanAppended = true,
        IReadOnlyList<string>? excludeExts = null,
        Func<bool>? pause = null,
        CancellationToken cancellationToken = default,
        string? entryOverride = null)
    {
        var directory = CopySample(sample);
        var logs = new List<string>();
        var layers = new List<(PackLayer Depth, string Target, bool Done)>();
        var options = new PierceOptions
        {
            MaxDepth = maxDepth,
            MinFreeGb = minFreeGb,
            Conflict = conflict,
            OutputRoot = outputRoot,
            RemoveIntermediate = removeIntermediate,
            RemoveSource = removeSource,
            FlattenSingleChild = flatten,
            ScanAppended = scanAppended,
            ExcludeExts = excludeExts ?? [],
            Pause = pause,
            Log = new ChannelLog((_, message) => logs.Add(message)),
            OnLayer = (depth, target, done) => layers.Add((depth, target, done)),
        };

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(TestPaths.NewEngine(), vault, options, cancellationToken);
        var entry = entryOverride ?? EntryOf(directory);
        var result = await piercer.RunAsync(entry).ConfigureAwait(false);
        return new PierceRun(directory, entry, result, logs, layers);
    }

    /// <summary>把样本树复制过去。只复制文件与目录，不跟随重解析点。</summary>
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

/// <summary>一趟穿透的现场：产物目录、结果、日志、层号事件。</summary>
internal sealed record PierceRun(
    string Directory,
    string Entry,
    PierceResult Result,
    List<string> Logs,
    List<(PackLayer Depth, string Target, bool Done)> Layers)
{
    /// <summary>样本副本里、入口包旁边的那个产物目录（按 1.1.0 的默认落点）。</summary>
    public string ProductRoot => Path.Combine(Directory, Path.GetFileNameWithoutExtension(Entry));

    /// <summary>把这个样本副本整棵删掉。副本落在 <c>_test-tmp\pierce\&lt;guid&gt;\</c> 下，删的是那一层。</summary>
    public void Cleanup()
    {
        try
        {
            System.IO.Directory.Delete(
                System.IO.Path.GetDirectoryName(Directory)!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言结果
        }
    }

    /// <summary>产物树里的相对路径（目录与文件都算），用来比对形状。</summary>
    public IReadOnlyList<string> Tree(string root)
    {
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        var lines = new List<string>();
        Walk(root, string.Empty);
        return [.. lines.OrderBy(line => line, StringComparer.Ordinal)];

        void Walk(string current, string prefix)
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos()
                         .OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                lines.Add(entry is DirectoryInfo ? relative + "/" : relative);
                if (entry is DirectoryInfo dir)
                {
                    Walk(dir.FullName, relative);
                }
            }
        }
    }
}
