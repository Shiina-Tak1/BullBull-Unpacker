using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Configuration;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 产物上提与收壳。三种形状：单子目录上提、同名链上提、中间包收壳。
///
/// 三条底线：不覆盖用户的东西、搬不动时一个字节都不动、递归合并的深度上限是一票否决。
/// </summary>
public sealed partial class Piercer
{
    /// <summary>递归合并的深度上限。它防的是耗时与栈，<b>不是</b>用户的层数预算。</summary>
    /// <remarks>
    /// 撞上它的后果是这一层整个不提，全有或全无，而且再跑一次也不动 ——
    /// 所以必须有一句日志说清，免得用户对着十几层同名目录不知道发生了什么。
    /// </remarks>
    private const int CanLiftMaxDepth = 16;

    /// <summary>干跑或真搬被挡住的三种原因。三者互不相干，告警不许合并成一句话。</summary>
    internal enum LiftBlock
    {
        None,
        Conflict,
        Depth,
        Io,
    }

    /// <summary>一次真搬的结论：是否成功、被什么挡住、已经搬走了哪些。失败且后者非空就是「半搬」。</summary>
    /// <remarks>半搬不做事务化（覆盖模式已删掉被替换的旧文件），只能如实报出来。</remarks>
    internal sealed record LiftOutcome(bool Ok, LiftBlock Block, IReadOnlyList<string> Moved);

    /// <summary>一次上提的结论：提动了哪个目录、被哪个子目录挡住、半搬走了哪些。</summary>
    internal sealed record FoldOutcome(
        string? Lifted, string? Blocked, LiftBlock Block, IReadOnlyList<string> Moved);

    /// <summary>一条同名链的结论：连提了哪几层、被哪个子目录挡住、半搬走了哪些。</summary>
    internal sealed record FlattenOutcome(
        IReadOnlyList<string> Lifted, string? Blocked, LiftBlock Block, IReadOnlyList<string> Moved);

    /// <summary>
    /// 这一层解出来只有一个文件夹时，把它的内容提到上一层，避免同名目录层层套娃。
    ///
    /// 关键在「这一层解出来的」：重名等于覆盖时目录里本来就有旧东西，
    /// 按「整个目录只有一个子目录、且没有别的文件」判会永远不上提，覆盖之后就成了套娃。
    /// <paramref name="bornAfter"/> 是解压之前拍的快照，用它把新出现的条目挑出来。
    /// </summary>
    internal static FoldOutcome FlattenSingleChild(
        string directory, HashSet<string>? bornAfter, bool overwrite, bool mergeDirs)
    {
        var inner = SoleChild(directory, bornAfter);
        if (inner is null)
        {
            return new FoldOutcome(null, null, LiftBlock.None, []);
        }

        var innerName = Path.GetFileName(inner);
        var lifted = LiftChild(directory, inner, overwrite, mergeDirs);
        return lifted.Ok
            ? new FoldOutcome(innerName, null, LiftBlock.None, lifted.Moved)
            : new FoldOutcome(null, innerName, lifted.Block, lifted.Moved);
    }

    /// <summary>这一层是不是只有一个新出现的子目录、且没有新文件。是就返回它的路径。</summary>
    /// <remarks>
    /// 单独抽出来是因为有两个调用方要用同一份判据：真去提的那一处，和提不动时判断
    /// 「是哪个目录挡住了」的那一处。判据只有一份，不许各写一遍。
    /// </remarks>
    internal static string? SoleChild(string directory, HashSet<string>? bornAfter)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var known = bornAfter ?? [];
        var fresh = entries.Where(entry => !known.Contains(entry.Name)).ToList();
        var dirs = fresh.Where(entry => entry is DirectoryInfo).ToList();
        var files = fresh.Where(entry => entry is FileInfo).ToList();
        return dirs.Count == 1 && files.Count == 0 ? dirs[0].FullName : null;
    }

    /// <summary>
    /// 把同名空壳一路提平，返回连提了哪几层。
    ///
    /// 单子目录上提一次只提一层，而三层套娃每层解出来的目录都同名，只提一次用户拿到的
    /// 还是同名套同名。继续循环的条件是子目录名与当前目录同名：
    /// 有意义的层级不会继续往下提。
    /// </summary>
    /// <remarks>
    /// 第一次提根本不看名字：单子目录就提，搬完之后才轮到「是否同名」决定要不要继续。
    /// 所以子目录不同名时只提这一层就停，返回值里包含第一次那一层 —— 它确实被提了。
    /// </remarks>
    internal FlattenOutcome FlattenSameNameShells(
        string directory, HashSet<string>? bornAfter, bool overwrite, int limit = LiftLimit)
    {
        var lifted = new List<string>();
        var born = bornAfter;
        for (var round = 0; round < Math.Max(1, limit); round++)
        {
            var name = Path.GetFileName(directory.TrimEnd('\\', '/'));
            var got = FlattenSingleChild(directory, born, overwrite, mergeDirs: true);
            if (got.Lifted is null)
            {
                if (got.Block != LiftBlock.None)
                {
                    ReportLiftBlocked(got.Block, name, got.Blocked ?? name);
                }

                return new FlattenOutcome(lifted, got.Blocked, got.Block, got.Moved);
            }

            lifted.Add(got.Lifted);
            if (!string.Equals(got.Lifted, name, StringComparison.OrdinalIgnoreCase))
            {
                // 同名链到此为止：这是正常收口，不是被挡住。
                // 这一支与循环外那一处上报不是同一个出口：那一处只服务「跑满上限」一种语义，共用会让普通上提也报「嵌套太深」（B-2026-315）。
                return new FlattenOutcome(lifted, null, LiftBlock.None, []);
            }

            // 提过一次之后就不再看快照：目录内容已经换了。
            born = null;
        }

        // 走到这一行只剩一种可能：同名链比上限还长，跑到上限也没提平。
        // 连提跑满上限也要如实报「上提未完成」：硬挡与跑满上限都得有一条日志，否则用户无从判断这一层发生了什么。
        var stop = Path.GetFileName(directory.TrimEnd('\\', '/'));
        ReportLiftBlocked(LiftBlock.Depth, stop, stop, limit);
        return new FlattenOutcome(lifted, null, LiftBlock.Depth, []);
    }

    /// <summary>上提被挡住时按原因记一条日志。深度那两档各报自己的上限数。</summary>
    private void ReportLiftBlocked(LiftBlock block, string name, string innerName, int? depthLimit = null)
    {
        switch (block)
        {
            case LiftBlock.Depth:
                _log?.Problem(PierceText.LiftFuseBlownAt, depthLimit ?? CanLiftMaxDepth, name, innerName);
                break;
            case LiftBlock.Conflict:
                _log?.Problem(PierceText.LiftBlockedConflictAt, name, innerName);
                break;
            default:
                _log?.Problem(PierceText.LiftBlockedIoAt, name, innerName);
                break;
        }
    }

    /// <summary>这个目录是不是用户拖进来的入口根。目录入口下收壳那处守卫天然成立（外部审计第 1 轮 `F-01`）。</summary>
    private bool AtEntryRoot(string directory) =>
        _entryRoot is not null
        && string.Equals(PierceRegistry.Normalize(directory), _entryRoot, StringComparison.Ordinal);

    /// <summary>
    /// 把中间包删掉后剩下的空壳层拆平，返回下一层该用的工作目录。
    ///
    /// 只在中间包确实被删掉了、且这一层只解出来一个子目录时动手，
    /// 所以「保留中间包」与「第一层原文件」这两种情况都不会被误伤。
    /// </summary>
    internal string CollapseShell(string workdir, string outdir, HashSet<string>? bornAfter)
    {
        if (AtEntryRoot(workdir))
        {
            return outdir;
        }

        if (!Directory.Exists(workdir) || !Directory.Exists(outdir))
        {
            return outdir;
        }

        var parent = Path.GetDirectoryName(PierceRegistry.Normalize(outdir));
        if (parent is null
            || !string.Equals(parent, PierceRegistry.Normalize(workdir), StringComparison.Ordinal))
        {
            // 产物不在这一层目录下（指定了输出目录那种），不动。
            return outdir;
        }

        return FlattenSingleChild(
            workdir, bornAfter, overwrite: _options.Conflict is ConflictPolicy.Overwrite, mergeDirs: false).Lifted
            is null
            ? outdir
            : workdir;
    }

    /// <summary>
    /// 干跑一遍：这个子目录到底能不能整个搬进目标目录；不能就说清是被什么挡住的。
    ///
    /// <b>必须干跑</b>：真搬是一边遍历一边搬的，遇到搬不动的项就返回失败，
    /// 而前面已经搬走的东西不会回来 —— 调用方以为没动过，继续在已经被搬空的目录里找下一层，
    /// 多层包就从这里断掉。所以先干跑，再真搬。
    /// </summary>
    internal static LiftBlock CheckLift(
        string directory, string inner, bool overwrite, bool mergeDirs, int depth = 0)
    {
        if (depth > CanLiftMaxDepth)
        {
            return LiftBlock.Depth;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(inner).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LiftBlock.Io;
        }

        foreach (var entry in entries)
        {
            var destination = Path.Combine(directory, entry.Name);
            var exists = File.Exists(destination) || Directory.Exists(destination);
            if (!exists)
            {
                continue;
            }

            if (entry is DirectoryInfo && Directory.Exists(destination))
            {
                if (overwrite || mergeDirs)
                {
                    var nested = CheckLift(destination, entry.FullName, overwrite, mergeDirs, depth + 1);
                    if (nested != LiftBlock.None)
                    {
                        return nested;
                    }

                    continue;
                }

                return LiftBlock.Conflict;
            }

            // 文件撞文件只在覆盖模式下放行；其余情形（含类型不一致）看 overwrite。
            if (entry is FileInfo && File.Exists(destination) && overwrite)
            {
                continue;
            }

            if (!overwrite)
            {
                return LiftBlock.Conflict;
            }
        }

        return LiftBlock.None;
    }

    /// <summary>
    /// 把子目录里的东西全部移到目标目录，然后删掉空了的子目录。
    ///
    /// 同名时：覆盖模式且都是文件就直接替换；都是目录就递归合并；
    /// 只允许目录合并那一档（<paramref name="mergeDirs"/>）**文件同名就放弃**，不覆盖用户的东西；
    /// 类型不一致时只有覆盖模式才删掉挡路的那个。按条目类型分派：<c>File.Move</c> 搬不了目录。
    /// </summary>
    internal static LiftOutcome LiftChild(string directory, string inner, bool overwrite, bool mergeDirs)
    {
        var blocked = CheckLift(directory, inner, overwrite, mergeDirs);
        if (blocked != LiftBlock.None)
        {
            return new LiftOutcome(false, blocked, []);
        }

        var moved = new List<string>();

        // 递归合并里搬过东西也算动过，否则计数还是零，会误报「没提动」。
        var worked = false;
        try
        {
            foreach (var entry in new DirectoryInfo(inner).EnumerateFileSystemInfos().ToList())
            {
                var destination = Path.Combine(directory, entry.Name);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if (entry is DirectoryInfo && Directory.Exists(destination) && (overwrite || mergeDirs))
                    {
                        var nested = LiftChild(destination, entry.FullName, overwrite, mergeDirs);
                        if (!nested.Ok)
                        {
                            return new LiftOutcome(false, nested.Block, [.. moved, .. nested.Moved]);
                        }

                        moved.AddRange(nested.Moved);
                        worked = true;
                        continue;
                    }

                    if (!overwrite)
                    {
                        return new LiftOutcome(false, LiftBlock.Conflict, moved);
                    }

                    if (entry is FileInfo && File.Exists(destination))
                    {
                        File.Move(entry.FullName, destination, overwrite: true);
                        moved.Add(entry.Name);
                        continue;
                    }

                    // 类型不一样：把挡路的删掉再搬。
                    if (Directory.Exists(destination))
                    {
                        if (Directory.EnumerateFileSystemEntries(destination).Any())
                        {
                            return new LiftOutcome(false, LiftBlock.Conflict, moved);
                        }

                        Directory.Delete(destination);
                    }
                    else
                    {
                        File.Delete(destination);
                    }
                }

                // 走到这一行 destination 必然不存在，所以目录也能整块搬。
                if (entry is DirectoryInfo)
                {
                    Directory.Move(entry.FullName, destination);
                }
                else
                {
                    File.Move(entry.FullName, destination);
                }

                moved.Add(entry.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LiftOutcome(false, LiftBlock.Io, moved);
        }

        try
        {
            Directory.Delete(inner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 壳没删掉不影响内容已经提上去了这一件事。
        }

        return new LiftOutcome(moved.Count > 0 || worked, LiftBlock.None, moved);
    }
}
