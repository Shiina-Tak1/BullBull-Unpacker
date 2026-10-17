using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 穿透的主循环：从当前工作目录开始继续往下解，直到没有可解的为止。
///
/// 同一层有多个互不相关的包时逐个都解：当前这条链先走，其余的进排队（记着哪个目录、
/// 第几层、只解哪个包），这条链走完了再回来接着解。失败也不是立刻收工 ——
/// 先记下最该报的那个原因，把同层其它包解完，最后再报。
///
/// 这里 <c>depth</c> 的含义是「当前这个工作项的包层」：入队时连同包层一起存下来，
/// 切回来时复用 —— 所以同层的第二个包不消耗层数预算。
/// </summary>
public sealed partial class Piercer
{
    private async Task<(StopReason Reason, string Detail)> PierceAsync(
        string startWorkdir, PierceResult result, PackLayer startLayer)
    {
        var depth = startLayer;

        // 预算在入口处夹一次：0 与 1 同义，两种入口读的是同一个数（见 PierceOptions.Budget）。
        var budget = PierceOptions.Budget(_options.MaxDepth);
        var todo = new List<(string Workdir, PackLayer Depth, string? Only, PierceRegistry Chain)>();
        string? only = null;
        (StopReason Reason, string Detail)? failure = null;
        string workdir = startWorkdir;

        // 判重的账跟着链走：根账里是入口包（目录入口为空），往下每解出一层都记进同一条链；
        // 同层有多个候选时每个候选各分叉一条，兄弟之间互不可见。
        var chain = _registry;

        // 入口层 = 用户直接拖进来的那个目录里的那些包。只有目录入口才有：
        // 单文件入口从第 2 层开始。它们的落点要跟单文件入口同一口径。
        PackLayer? entryLayer = startLayer.Value == 1 ? PackLayer.First : null;

        while (true)
        {
            if (_options.CleanDelete)
            {
                CleanDeleteNames(workdir);
            }

            // 已经在排队的同层兄弟：它们各自的包层由队列带着，这里不能再捡一次 ——
            // 再捡一次就会按更深一层记账，同层的第二个包多消耗一层预算。
            var queued = todo.Where(task => task.Only is not null).Select(task => task.Only!).ToList();
            PickResult pick;
            if (only is not null)
            {
                pick = new PickResult { Path = only };
                only = null;
            }
            else
            {
                pick = PickTarget(workdir, queued);
            }

            if (!pick.Found)
            {
                if (Cancelled)
                {
                    return CancelledOutcome(failure);
                }

                if (pick.Ambiguous.Count > 0)
                {
                    var outcome = AmbiguousOutcome(
                        result, pick, result.OutputDir.Length > 0 ? result.OutputDir : workdir, depth);
                    failure = PreferFailure(failure, outcome.Reason, outcome.Detail);
                    if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                    {
                        continue;
                    }

                    return failure.Value;
                }

                if (pick.SearchIncomplete)
                {
                    NoteLeftover(result, result.OutputDir.Length > 0 ? result.OutputDir : workdir, pick.Unfinished);
                    failure = PreferFailure(failure, StopReason.SearchIncomplete, pick.SearchDetail);
                    if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                    {
                        continue;
                    }

                    return failure.Value;
                }

                if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                {
                    continue;
                }

                return failure ?? (StopReason.NoArchive, string.Empty);
            }

            // 取到候选之后、真正开解之前再独立查一次取消。上面那个检查长在「没有候选」的分支里，
            // 只有这一层没候选才轮到它；而往下找的取消钩子挂在子目录遍历内部，
            // 目录里一个子目录都没有时它一次都不会执行 —— 那恰恰是套娃的常态。
            if (Cancelled)
            {
                return CancelledOutcome(failure);
            }

            // 层数预算按包层判，而且只在「这一层真有包要解」时才判。
            // 放在候选之前拿上一轮解完加一的层号去比，会假报一次「达到层数上限」而账是空的。
            if (depth > new PackLayer(budget))
            {
                var blocked = pick.Targets.Count > 0
                    ? pick.Targets
                    : pick.Path is not null ? [pick.Path] : [];
                _log?.Problem(PierceText.MaxDepthHitAt, budget);
                SettleTarget(
                    result,
                    result.OutputDir.Length > 0 ? result.OutputDir : workdir,
                    blocked,
                    depth,
                    TargetFate.Unfinished);
                failure = PreferFailure(
                    failure, StopReason.MaxDepth, PierceText.Fill(PierceText.MaxDepthHitAt, budget));
                if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                {
                    continue;
                }

                return failure.Value;
            }

            if (pick.Targets.Count > 1)
            {
                _log?.Write(new LogRecord(
                    LogLevel.Info,
                    PierceText.Fill(
                        PierceText.MultipleTargetsAt,
                        pick.Targets.Count,
                        string.Join('、', pick.Targets.Take(6).Select(Path.GetFileName))),
                    depth.Value));
                foreach (var extra in pick.Targets.Skip(1))
                {
                    todo.Add((workdir, depth, extra, chain.Fork()));
                }
            }

            if (pick.SearchIncomplete)
            {
                // 没找完但已经找到了候选：先把找到的解完，最后如实报。
                // 别覆盖更早的失败原因 —— 第一个失败才是要报给用户的那个。
                if (pick.Unfinished.Count > 0)
                {
                    NoteLeftover(result, result.OutputDir.Length > 0 ? result.OutputDir : workdir, pick.Unfinished);
                }

                var wasEmpty = failure is null;
                failure = PreferFailure(failure, StopReason.SearchIncomplete, pick.SearchDetail);
                if (wasEmpty)
                {
                    _log?.Problem(PierceText.SearchIncompleteFirstAt, pick.SearchDetail);
                }
            }

            var target = pick.Targets.Count > 0 ? pick.Targets[0] : pick.Path!;

            if (!File.Exists(target) && !Directory.Exists(target))
            {
                // 队列里排着的包不在了，这里有三种语义完全不同的情形，不许合并成一句话。
                if (chain.WasRemovedByUs(target))
                {
                    _log?.Note(PierceText.AlreadyRemovedAt, Path.GetFileName(target));
                    if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                    {
                        continue;
                    }

                    return failure ?? (StopReason.NoArchive, string.Empty);
                }

                var baseDir = result.OutputDir.Length > 0 ? result.OutputDir : workdir;
                SettleTarget(result, baseDir, [target], depth, TargetFate.Unfinished);
                _log?.Problem(PierceText.QueuedTargetGoneAt, Path.GetFileName(target));
                failure = PreferFailure(
                    failure,
                    StopReason.ExtractFailed,
                    PierceText.Fill(PierceText.QueuedTargetGoneDetailAt, Path.GetFileName(target)));
                if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                {
                    continue;
                }

                return failure.Value;
            }

            var fingerprint = ContentIdentity.Of(target);
            var known = chain.Registered(fingerprint);

            // 包层大于 1 的才是本趟解出来的嵌套包：目录入口的入口层是第 1 层，单文件入口的主循环从第 2 层起。
            if (known.Count > 0
                && DuplicateOutcome(
                    result,
                    target,
                    fingerprint,
                    known,
                    result.OutputDir.Length > 0 ? result.OutputDir : workdir,
                    depth,
                    chain,
                    intermediate: depth.Value > 1) is { } duplicate)
            {
                failure = PreferFailure(failure, duplicate.Reason, duplicate.Detail);
                if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                {
                    continue;
                }

                return failure.Value;
            }

            chain.Register(fingerprint, target);

            // 入口层的落点跟单文件入口同一口径：输出根优先。更深层仍跟着上一层走，
            // 否则每一层都在输出根下另建目录，同名目录只能靠序号区分。
            var entryNow = entryLayer is { } layer && depth == layer;
            var parent = entryNow && _options.OutputRoot is not null ? null : workdir;
            var resolved = ResolveTarget(target, parent);
            if (resolved.Directory is null)
            {
                failure = PreferFailure(
                    failure,
                    StopReason.OutputExists,
                    PierceText.Fill(PierceText.OutputExistsBlockedAt, Path.GetFileName(target)));
                if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                {
                    continue;
                }

                return failure.Value;
            }

            var outdir = resolved.Directory;

            // 解压之前先记下父目录里有什么：重名等于覆盖时父目录本来就有旧东西，
            // 不然「这一层解出来只有一个子目录」永远判不出来。
            var parentBefore = Snapshot(Path.GetDirectoryName(outdir) ?? ".");
            var extracted = await ExtractLayerAsync(result, target, outdir, depth, _lastPassword).ConfigureAwait(false);
            RecordLayer(result, extracted);

            if (!extracted.Ok)
            {
                var reason = extracted.Reason ?? StopReason.ExtractFailed;
                var detail = extracted.Note.Length > 0
                    ? Path.GetFileName(target) + "：" + extracted.Note
                    : Path.GetFileName(target);
                failure = PreferFailure(failure, reason, detail);
                if (NextTask(todo, ref chain, ref workdir, ref depth, ref only))
                {
                    continue;
                }

                return failure.Value;
            }

            workdir = outdir;
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                // 中间包删掉了、上一层目录因此只剩这一个子目录，把它拆平。
                // 不收壳的话三层套娃会留下三层同名目录。
                var collapsed = CollapseShell(Path.GetDirectoryName(target) ?? ".", outdir, parentBefore);
                if (!string.Equals(collapsed, outdir, StringComparison.Ordinal))
                {
                    extracted.OutDir = collapsed;
                    workdir = collapsed;
                    if (string.Equals(
                            PierceRegistry.Normalize(result.OutputDir),
                            PierceRegistry.Normalize(outdir),
                            StringComparison.Ordinal))
                    {
                        result.OutputDir = collapsed;
                    }

                    // 收壳与上提共用一条模板（裁「合并成一条」）：收壳没有连提说明，传空串。
                    _log?.Write(new LogRecord(
                        LogLevel.Info,
                        PierceText.Fill(PierceText.LiftedAt, Path.GetFileName(collapsed), string.Empty),
                        depth.Value));
                }
            }

            // 这一层真的解出来了、产物确定（收壳之后才是终值）—— 另记一笔「内容在哪儿」，与解压前那本账是两件事；引擎报了警告退出码时不记（B-2026-311）。
            if (!extracted.Warned)
            {
                chain.NoteSolved(fingerprint, target, workdir);
            }

            // 往下走了一层：这个包的内容（含里面可能有的包）是更深一个包层。
            // 即使上面收了壳（工作目录回到上一层），包层也必须加一 ——
            // 收壳只是把内容搬到浅一点的目录里，包层没变浅。
            depth = depth + 1;
        }
    }

    /// <summary>用户点了停止：已经发生的真失败优先，不许用取消把它盖掉。</summary>
    private static (StopReason Reason, string Detail) CancelledOutcome(
        (StopReason Reason, string Detail)? failure) =>
        failure is { } known && !known.Reason.IsCleanStop()
            ? known
            : (StopReason.Cancelled, string.Empty);

    /// <summary>多个伪装包分不清主次。总数与未列出个数要同时进日志与细节。</summary>
    private (StopReason Reason, string Detail) AmbiguousOutcome(
        PierceResult result, PickResult pick, string baseDir, PackLayer depth)
    {
        var total = pick.Ambiguous.Count;
        var names = string.Join('、', pick.Ambiguous.Take(4).Select(Path.GetFileName));
        var rest = total - Math.Min(4, total);
        var more = rest > 0 ? PierceText.Fill(PierceText.AmbiguousMoreAt, rest) : string.Empty;
        var detail = PierceText.Fill(PierceText.AmbiguousDetailAt, total, more, names);
        _log?.Problem(PierceText.AmbiguousAt, total, more, names);
        SettleTarget(result, baseDir, pick.Ambiguous, depth, TargetFate.Unfinished);
        if (pick.Unfinished.Count > 0)
        {
            NoteLeftover(result, baseDir, pick.Unfinished);
        }

        return (StopReason.Ambiguous, detail);
    }

    /// <summary>
    /// 内容指纹命中之后的**统一处置**：确认、文案、账、以及「这一份要不要收掉」都在这里做完。三种结果：
    /// ① 真相同 ＋ 本趟解出来的嵌套包（撞上的那一份在链上是祖先）＋ 那一份确已成功解出过 ⇒ 收掉它、**不记残留**，文案给出内容所在的全路径；
    /// ② 真相同 ＋「只是登记过、没解成功」⇒ 仍然跳过并指出，如实报「有内容没解」；
    /// ③ 确认不了 ⇒ 仍然跳过（防死循环不许失效），文案降级成「疑似」并仍然记残留。
    /// 命中给结论；指纹碰撞、逐字节都不同则返回空，调用方照解。
    /// </summary>
    /// <param name="chain">当前这条解压链的账。落点那一笔要从它查 —— 分叉出来的链各有各的账。</param>
    /// <param name="intermediate">这一份是不是**本趟解压出来的嵌套包**（包层大于 1），见 B-2026-311。</param>
    private (StopReason Reason, string Detail)? DuplicateOutcome(
        PierceResult result, string target, Fingerprint fingerprint, IReadOnlyList<string> known,
        string baseDir, PackLayer depth, PierceRegistry chain, bool intermediate)
    {
        var identity = FileIdentity.Of(target);
        string? sameAs = null;
        var unverifiable = false;
        foreach (var previous in known)
        {
            if (!File.Exists(previous))
            {
                unverifiable = true;
                continue;
            }

            if (string.Equals(FileIdentity.Of(previous), identity, StringComparison.Ordinal)
                || ContentIdentity.SameContent(target, previous))
            {
                sameAs = previous;
                break;
            }
        }

        if (sameAs is null && !unverifiable)
        {
            // 全都还在、且逐字节都不同：根本不是同一个包，照解。
            return null;
        }

        string where;
        string why;
        if (sameAs is not null)
        {
            where = PierceText.Fill(PierceText.SameAsAt, Locate(sameAs));
            why = PierceText.SameContent;
        }
        else
        {
            where = PierceText.SameAsEarlier;
            why = PierceText.SameContentUnverified;
        }

        // 内容确已解出过、且这一份是本趟解出来的嵌套包：它没有「第二份产物」可言，内容已经在 solvedDir 里了。
        // 收掉它并且**不记残留** —— 这一层的账因此不会变成「有内容没解」。
        if (sameAs is not null
            && intermediate
            && chain.SolvedAt(fingerprint, sameAs) is { Length: > 0 } solvedDir)
        {
            SettleTarget(result, baseDir, [target], depth, TargetFate.AlreadySolved);

            if (_log is not null)
            {
                _log.Note(PierceText.SameContentSolvedAt, where, why, solvedDir);
            }

            return (StopReason.AlreadyVisited, PierceText.Fill(PierceText.SameContentSolvedAt, where, why, solvedDir));
        }

        _log?.Problem(PierceText.DuplicateSkippedAt, where, why, Locate(target));
        NoteLeftover(result, baseDir, [target]);
        return (StopReason.AlreadyVisited, PierceText.Fill(PierceText.DuplicateSkippedAt, where, why, Locate(target)));
    }

    /// <summary>还有排队的包就切过去，账也一并换成那一条链的。</summary>
    private static bool NextTask(
        List<(string Workdir, PackLayer Depth, string? Only, PierceRegistry Chain)> todo,
        ref PierceRegistry chain, ref string workdir, ref PackLayer depth, ref string? only)
    {
        if (todo.Count == 0)
        {
            return false;
        }

        var task = todo[0];
        todo.RemoveAt(0);
        chain = task.Chain;
        workdir = task.Workdir;
        depth = task.Depth;
        only = task.Only;
        return true;
    }

    /// <summary>算一层的产物目录。跳过时返回的结论里带着被挡住的那个名字。</summary>
    private OutputDirResult ResolveTarget(string target, string? parent)
    {
        return OutputNaming.Resolve(
            new OutputDirRequest(target, parent, _options.OutputRoot, _options.Conflict), _log);
    }

    /// <summary>记下目录里现在有哪些条目，用来区分这次解出来的与原来就在的。</summary>
    private static HashSet<string> Snapshot(string directory)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                names.Add(entry.Name);
            }

            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 这一层真的往产物目录里写出东西了吗。
    ///
    /// 只看新增的条目；覆盖模式旧路径被重写、条目名集合没变，所以那种情况退化成
    /// 「目录里还有没有东西」。校验必须排在上提之前，上提会把产物搬走。
    /// </summary>
    internal bool HasPayload(string outdir, HashSet<string> before)
    {
        var now = Snapshot(outdir);
        return now.Except(before, StringComparer.Ordinal).Any()
            || (_options.Conflict is ConflictPolicy.Overwrite && now.Count > 0);
    }

    /// <summary>对账：把残留名单里本进程自己解完并删掉的条目剔掉。</summary>
    /// <remarks>
    /// 撞层数上限那一刻是把工作目录里当时看得见的包整个记下来的，随后排队又把其中几个
    /// 正常解开并按默认设置删掉 —— 账记了没销。各分支就地销账做不到：它们不知道后面还会解掉谁。
    /// 三种结局分开：还在盘上就留；不在了但本进程没删过也留（那是没解释的消失，必须报）；
    /// 不在了且本进程删过，才是真销账。
    /// </remarks>
    internal void ReconcileLeftover(PierceResult result)
    {
        if (result.Leftover.Count == 0)
        {
            return;
        }

        var kept = new List<string>();
        foreach (var relative in result.Leftover)
        {
            var full = Path.IsPathRooted(relative) || result.OutputDir.Length == 0
                ? relative
                : Path.Combine(result.OutputDir, relative);

            if (File.Exists(full) || Directory.Exists(full) || !_registry.WasRemovedByUs(full))
            {
                kept.Add(relative);
            }
        }

        result.Leftover.Clear();
        result.Leftover.AddRange(kept);
    }

    /// <summary>撞层数上限时，把最终确实还留在盘上的包点名一次。</summary>
    /// <remarks>
    /// 不在撞上限那一刻点名：那一刻的名单只是快照，后面排队还会把其中几个解开并删掉，
    /// 于是日志会点出事后并不存在的包。日志与界面必须说同一件事，两边都取对账之后的最终态。
    /// </remarks>
    private void ReportLeftover(PierceResult result)
    {
        if (result.Leftover.Count == 0 || result.StopReason is not StopReason.MaxDepth)
        {
            return;
        }

        _log?.Problem(
            PierceText.LeftoverReportAt,
            result.Leftover.Count,
            string.Join('、', result.Leftover.Take(4).Select(Path.GetFileName)));
    }
}
