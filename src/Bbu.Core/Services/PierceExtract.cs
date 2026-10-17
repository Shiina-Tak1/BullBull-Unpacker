using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 解一层：伪装包先切出内嵌的真包，再交给引擎；密码层与磁盘余量都在这一层判。目标是「垫了真视频」的伪装文件时
/// 不能直接把原文件交给引擎：引擎不会去文件中间找压缩包。切出来的临时文件用完就删，用户的原文件一个字节都不动。
/// </summary>
public sealed partial class Piercer
{
    /// <summary>切出内嵌包。<paramref name="Error"/> 非空表示这一层已经失败。</summary>
    /// <remarks>
    /// 判据走识别层的唯一入口，与选目标那条通道的区别只有两个参数：这里传引擎（走到这里已经确定是目标，一次问询换覆盖面值得），
    /// 并且开深扫（是最后手段）。几段的顺序不许调换，把引擎判据提前会让垫片加相对偏移那类包从切包变成直读原文件。
    /// </remarks>
    private (string Source, string? Temp, string Error, StopReason? Reason) CarveSource(
        string archive, string outdir)
    {
        if (!_options.ScanAppended)
        {
            return (archive, null, string.Empty, null);
        }

        var probe = Probe.InspectCarrier(archive, new ProbeOptions
        {
            Deep = true,
            EngineProbe = CanEngineRead,
            FallbackEngineProbe = CanUnRarRead,
            Cancellation = _cancellation,
            Log = _log,
        });

        if (probe.Direct || probe.Embedded is not { } embedded)
        {
            return (archive, null, string.Empty, null);
        }

        long total;
        try
        {
            total = new FileInfo(archive).Length - embedded.Offset;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 探测之后文件被删或被占用：说一句再继续。静默返回会让「这个伪装包为什么没解出来」
            // 在日志里毫无线索。
            _log?.Problem(PierceText.CarrierSizeUnreadableAt, Path.GetFileName(archive), ex.Message);
            return (archive, null, string.Empty, null);
        }

        var baseDir = Path.GetDirectoryName(outdir) is { Length: > 0 } parent
            ? parent
            : Path.GetDirectoryName(archive) is { Length: > 0 } fallback ? fallback : ".";
        var temp = CarveTempName(baseDir, archive, embedded.Ext);
        _log?.Note(
            PierceText.CarrierDetectedAt,
            Path.GetFileName(archive),
            Probe.HumanSize(embedded.Offset),
            Probe.FormatName(embedded.Format),
            Probe.HumanSize(total));

        // 切出来的临时文件跟正式产物一样占地方，先看余量再动手。
        if (!HasSpace(baseDir, total))
        {
            return (archive, null, PierceText.Fill(
                PierceText.CarveNeedsSpaceAt, Probe.HumanSize(total), _options.MinFreeGb), StopReason.NoSpace);
        }

        var carved = Probe.Carve(archive, embedded.Offset, temp, _cancellation);
        if (!carved.Ok)
        {
            return (archive, null, PierceText.CarveFailed,
                carved.Cancelled ? StopReason.Cancelled : StopReason.ExtractFailed);
        }

        return (temp, temp, string.Empty, null);
    }

    /// <summary>切片临时文件的名字：「.原名.进程号.随机串.carve.扩展名」，扩展名留在最后一段。
    /// 固定名可预测，同名文件会被下一次切包截断（B-2026-204）。</summary>
    internal static string CarveTempName(string baseDir, string archive, string ext) =>
        Path.Combine(
            baseDir,
            "." + Path.GetFileName(archive)
            + "." + Environment.ProcessId
            + "." + Guid.NewGuid().ToString("N")
            + ".carve." + ext);

    /// <summary>引擎能不能把这个文件直接当压缩包读出来。判不了时按读不了处理。</summary>
    /// <remarks>
    /// 识别层要的是同步的探针；引擎全链路都不回到调用方的同步上下文，所以这里在线程池上等它，
    /// 不会把调用方锁住。
    /// </remarks>
    private bool CanEngineRead(string path)
    {
        try
        {
            var info = Task.Run(() => _engine.InspectAsync(path, _cancellation)).GetAwaiter().GetResult();
            return info.ReadOk && info.EntryCount > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>第二个引擎（UnRAR）读不读得动，见 <see cref="ProbeOptions.FallbackEngineProbe"/>：兜底那一档的保险。</summary>
    private bool CanUnRarRead(string path)
    {
        try
        {
            return Task.Run(() => _engine.CanUnRarReadAsync(path, _cancellation)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal void Discard(string? temp)
    {
        if (temp is null)
        {
            return;
        }

        try
        {
            File.Delete(temp);
            _log?.Note(PierceText.TempDiscardedAt, Path.GetFileName(temp));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不影响这一层的结果，但留在盘上的那个对象必须说出来：否则用户既不知道它在，也不知道为什么在。
            if (_log is not null)
            {
                _log.Problem(PierceText.TempDiscardFailedAt, Path.GetFileName(temp), ex.Message);
            }
        }
    }

    /// <summary>
    /// 解压一层。目标是伪装文件时先切出内嵌的压缩包再解。
    /// 这里只兜文件系统错误：一层里的文件系统错误是这一层失败，不是整批崩掉。
    /// 非文件系统的逻辑异常不许在这里吞下去 —— 那会让 bug 伪装成「这个包解不开」。
    /// </summary>
    private async Task<LayerResult> ExtractLayerAsync(
        PierceResult result, string archive, string outdir, PackLayer depth, string? passwordHint)
    {
        // 记下本次的落点：取消或失败要能说出半成品在哪（外部审计第 1 轮 D-6）。
        _lastOutdir = outdir;
        var started = Stopwatch.StartNew();

        // 产物目录由引擎创建，本次之前不存在的那些层都是本次建出来的（D-4）。
        var vacant = VacantChain(outdir);
        var (source, temp, error, reason) = CarveSource(archive, outdir);
        if (error.Length > 0)
        {
            _log?.Write(new LogRecord(LogLevel.Error, error, depth.Value));
            return Settle(
                new LayerResult
                {
                    Depth = depth,
                    Target = archive,
                    OutDir = outdir,
                    Ok = false,
                    Seconds = started.Elapsed.TotalSeconds,
                    Note = error,
                    Reason = reason ?? StopReason.ExtractFailed,
                },
                vacant);
        }

        try
        {
            return Settle(
                await ExtractOneAsync(result, source, archive, outdir, depth, passwordHint, started)
                    .ConfigureAwait(false),
                vacant);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Write(new LogRecord(LogLevel.Error, ex.Message, depth.Value));
            return Settle(
                new LayerResult
                {
                    Depth = depth,
                    Target = archive,
                    OutDir = outdir,
                    Ok = false,
                    Seconds = started.Elapsed.TotalSeconds,
                    Note = ex.Message,
                    Reason = StopReason.ExtractFailed,
                },
                vacant);
        }
        finally
        {
            // 临时的切片文件无论成败都要清掉，不得在用户目录里留下一个几 GB 的临时文件。
            Discard(temp);
        }
    }

    /// <summary>产物目录这条链上、本次之前不存在的那些层，从最外面那一层排到 <paramref name="outdir"/>。</summary>
    private static List<string> VacantChain(string outdir)
    {
        var chain = new List<string>();
        var current = outdir;
        while (current.Length > 0 && !Directory.Exists(current))
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length == 0 || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>失败与取消的收尾：本次建出来、此刻仍然空着的那些目录收掉，其余目录不动。</summary>
    private LayerResult Settle(LayerResult layer, List<string> vacant)
    {
        if (layer.Ok || vacant.Count == 0)
        {
            return layer;
        }

        var deepest = string.Empty;
        for (var i = vacant.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(vacant[i], recursive: false);
                deepest = vacant[i];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // 非空，或删不掉（被占用）：这一层留着，它外面几层也就更不可能空，到此为止。
                break;
            }
        }

        if (deepest.Length > 0)
        {
            _log?.Note(PierceText.VacantOutputRemovedAt, Path.GetFileName(deepest));
        }

        return layer;
    }

    /// <summary>这一层算不算解出来了：引擎报成功，或者报了警告退出码（有东西没解全、但引擎没报错）。</summary>
    internal static bool LayerSucceeded(EngineResult extracted) => extracted.Ok || extracted.Warned;

    /// <summary>密码错那一档 Brief 只给结论（原话并进它会在 Vault 那一格说两遍），所以备注在这里补上原话。</summary>
    internal static string LayerFailureNote(EngineResult extracted) =>
        extracted.WrongPassword && extracted.Tail.Length > 0
            ? string.Format(Format.ConclusionWithOutputAt, extracted.Brief, extracted.Tail)
            : extracted.Brief;

    private async Task<LayerResult> ExtractOneAsync(
        PierceResult result, string source, string archive, string outdir, PackLayer depth, string? passwordHint,
        Stopwatch started)
    {
        var display = Path.GetFileName(archive);
        _log?.Write(new LogRecord(
            LogLevel.Info, PierceText.Fill(PierceText.LayerStartedAt, display, Path.GetFileName(outdir)), depth.Value));
        LayerEvent(depth, archive, false);

        // 交给引擎之前先核一次源包还在不在：解压中途源包被删时引擎只回一句「系统找不到指定的文件」，先判一次就能说清是源文件不在了。
        if (!File.Exists(source))
        {
            var gone = PierceText.SourceGone;
            _log?.Write(new LogRecord(LogLevel.Warn, PierceText.Fill(PierceText.SourceGoneAt, display), depth.Value));
            return Failed(depth, archive, outdir, started, gone, StopReason.ExtractFailed);
        }

        var kind = EngineRouter.KindForPath(source);
        var info = await _engine.InspectAsync(source, _cancellation).ConfigureAwait(false);
        string? password = null;
        string? rememberValue = null;
        var origin = string.Empty;

        // 空间：按这个包自己声明的解压体积判，而不是只看还剩多少。判不出体积时它退化成绝对下限。
        // 放在试密码之前：余量不够就不必试几十万个密码。
        var need = info.TotalSize;
        if (!HasSpace(Path.GetDirectoryName(outdir) ?? ".", need))
        {
            var detail = need > 0
                ? PierceText.Fill(PierceText.DeclaredSpaceAt, Probe.HumanSize(need), _options.MinFreeGb)
                : PierceText.SpaceBelowFloor(_options.MinFreeGb);
            _log?.Write(new LogRecord(LogLevel.Error, PierceText.Fill(PierceText.NoSpaceAt, detail), depth.Value));
            return Failed(depth, archive, outdir, started, detail, StopReason.NoSpace);
        }

        if (info.Encrypted is false)
        {
            origin = PierceText.OriginNoPassword;
            _log?.Write(new LogRecord(LogLevel.Info, PierceText.NotEncryptedAt, depth.Value));
        }
        else
        {
            var outcome = await ResolvePasswordAsync(source, kind, info, depth, passwordHint, outdir)
                .ConfigureAwait(false);
            if (outcome.FailureReason is { } failureReason)
            {
                return Failed(depth, archive, outdir, started, outcome.FailureNote, failureReason);
            }

            password = outcome.Password;
            origin = outcome.Origin;
            rememberValue = outcome.RememberValue;
        }

        var outdirBefore = Snapshot(outdir);
        var extracted = await _engine.ExtractAsync(
            source,
            outdir,
            password,
            new ExtractOptions
            {
                Kind = kind,
                // 用户选的冲突档要落到引擎开关上：非覆盖档（改名、跳过）出 -aos，与上提、收壳两处同一个问法。
                // 吃 ExtractOptions 的默认值等于一律覆盖，那一行漏掉时「跳过同名」就只在命名层有效。
                Overwrite = _options.Conflict is Configuration.ConflictPolicy.Overwrite,
                DeclaredBytes = need,
                MinFreeBytes = _minFreeBytes,
                GuardDirectory = Path.GetDirectoryName(outdir),
                Pause = _options.Pause,
            },
            _cancellation).ConfigureAwait(false);

        var seconds = started.Elapsed.TotalSeconds;
        if (extracted.Cancelled)
        {
            // 用户点了停止：子进程已经被杀掉，产物可能不完整。原因由停止原因说，
            // 备注留空，免得界面上出现「已停止（已停止）」。
            _log?.Write(new LogRecord(LogLevel.Info, Errors.Stopped, depth.Value));
            return Failed(depth, archive, outdir, started, string.Empty, StopReason.Cancelled);
        }

        if (extracted.SpaceGuardHit)
        {
            var detail = PierceText.Fill(PierceText.SpaceGuardDetailAt, _options.MinFreeGb);
            _log?.Write(new LogRecord(LogLevel.Error, detail, depth.Value));
            return Failed(depth, archive, outdir, started, detail, StopReason.SpaceGuard);
        }

        if (LayerSucceeded(extracted))
        {
            // 引擎说成功不等于真解出东西了。判据是这一层新增了条目，三个边界都要避开：
            // 合法的零条目空包不判；覆盖模式改看目录里还有没有东西；校验排在上提之前。
            if (info.EntryCount > 0 && !HasPayload(outdir, outdirBefore))
            {
                var empty = PierceText.EmptyOutputAt;
                _log?.Write(new LogRecord(LogLevel.Warn, PierceText.EmptyOutputAt, depth.Value));
                return Failed(depth, archive, outdir, started, empty, StopReason.EmptyOutput, origin, password);
            }

            // 只有真的用了密码才更新外层密码，否则未加密的中间层会把上一层的密码清掉。
            if (password is not null)
            {
                _lastPassword = password;
                RememberPassword(depth, rememberValue ?? password);
            }

            IReadOnlyList<string> partialLift = [];
            if (_options.FlattenSingleChild)
            {
                var folded = FlattenSameNameShells(
                    outdir, outdirBefore, overwrite: _options.Conflict is Configuration.ConflictPolicy.Overwrite);
                if (folded.Lifted.Count > 0)
                {
                    _log?.Write(new LogRecord(
                        LogLevel.Info,
                        PierceText.Fill(
                            PierceText.LiftedAt,
                            folded.Lifted[0],
                            folded.Lifted.Count > 1
                                ? PierceText.Fill(PierceText.LiftedManyAt, folded.Lifted.Count)
                                : string.Empty),
                        depth.Value));
                }

                // 半搬：搬移中断时已经搬走的那部分要如实报出来。
                if (folded.Block != LiftBlock.None && folded.Moved.Count > 0)
                {
                    partialLift = folded.Moved;
                    _log?.Problem(PierceText.LiftPartialAt, folded.Moved.Count, string.Join('、', folded.Moved));
                }
            }

            _log?.Write(new LogRecord(LogLevel.Info, PierceText.Fill(PierceText.LayerDoneAt, seconds), depth.Value));

            // 清掉已经解开的包以释放磁盘空间。善后只有一处出口：SettleTarget 按「这一层的结果」决定收不收它。
            // 引擎报了警告退出码也同样收掉（2026-10-16 裁定）：这一层按成功层处理，只有删包一步被跳过本就不自洽，
            // 而「留给用户重试」这个意图在现状下实现不了 —— 这一档落「有内容没解」，而部分完成不重跑。
            if (extracted.Warned)
            {
                _log?.Write(new LogRecord(LogLevel.Warn, extracted.Brief, depth.Value));
            }

            SettleTarget(result, outdir, [archive], depth, TargetFate.Extracted);

            return new LayerResult
            {
                Depth = depth,
                Target = archive,
                OutDir = outdir,
                Ok = true,
                Warned = extracted.Warned,
                Seconds = seconds,
                PasswordOrigin = origin,
                Password = password ?? string.Empty,
                PartialLift = partialLift,
                Note = extracted.Warned ? extracted.Brief : string.Empty,
            };
        }

        // 备注由 LayerFailureNote 一处组装：中文结论在前、引擎原话照抛在后；拿原话盖掉结论，界面上只剩一行英文。
        var note = LayerFailureNote(extracted);

        _log?.Write(new LogRecord(LogLevel.Error, PierceText.Fill(PierceText.LayerFailedAt, note), depth.Value));
        return Failed(depth, archive, outdir, started, note, StopReason.ExtractFailed, origin, password);
    }

    private LayerResult Failed(
        PackLayer depth, string archive, string outdir, Stopwatch started, string note, StopReason reason,
        string origin = "", string? password = null) =>
        new()
        {
            Depth = depth,
            Target = archive,
            OutDir = outdir,
            Ok = false,
            Seconds = started.Elapsed.TotalSeconds,
            PasswordOrigin = origin,
            Password = password ?? string.Empty,
            Note = note,
            Reason = reason,
        };

    /// <summary>这一份包在这一层里的结果：解出来了 / 内容已在别处解出过、不必再解 / 没能解。</summary>
    internal enum TargetFate
    {
        Extracted,
        AlreadySolved,
        Unfinished,
    }

    /// <summary>这一层的结果已定之后的唯一善后点：这一份包收不收、留下来时记不记进残留。</summary>
    /// <remarks>五路共用：解压成功、判重且内容确已解出过、撞层数上限、多解歧义、队列里的包被外部删掉。它只管**包**的去留 —— 空目录回收仍在 <c>Settle</c>、
    /// 收壳仍在 <c>CollapseShell</c> 与 <c>FlattenSameNameShells</c>，三件事不许合并。</remarks>
    private void SettleTarget(
        PierceResult result, string baseDir, IEnumerable<string> targets, PackLayer depth, TargetFate fate)
    {
        // 第一层是用户自己拖进来的原包，只有显式开了才删；更深层是解压出来的嵌套包，默认删。
        var collect = fate switch
        {
            TargetFate.Extracted => depth.Value > 1 ? _options.RemoveIntermediate : _options.RemoveSource,
            TargetFate.AlreadySolved => depth.Value > 1 && _options.RemoveIntermediate,
            // 没能解的一个字节都不动：删除它会破坏用户的原始文件。只如实记进残留。
            _ => false,
        };

        foreach (var target in targets)
        {
            if (collect)
            {
                RemoveGroup(target, intermediate: depth.Value > 1);
                continue;
            }

            if (fate is TargetFate.Unfinished)
            {
                NoteLeftover(result, baseDir, [target]);
            }
        }
    }

    /// <summary>删掉一个包。分卷整组删，否则留下孤儿分卷；用户的原包与解压产生的嵌套包分别记不同的那一句（第 1 层是原包）。</summary>
    private void RemoveGroup(string archive, bool intermediate)
    {
        var removedAt = intermediate ? PierceText.IntermediateRemovedAt : PierceText.SourceRemovedAt;
        var failedAt = intermediate ? PierceText.IntermediateRemoveFailedAt : PierceText.SourceRemoveFailedAt;
        var info = Probe.ClassifyVolume(archive);
        var targets = new List<string> { archive };
        if (info.Kind is not VolKind.None && info.IsSplit)
        {
            var directory = Path.GetDirectoryName(archive) ?? ".";
            List<string> names;
            try
            {
                names = [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                names = [];
            }

            // 同组的兄弟整组删；弱命名的独立包不是这一组成员，不随组删。
            foreach (var name in names)
            {
                var other = Path.Combine(directory, name);
                var otherInfo = Probe.ClassifyVolume(other);
                if (otherInfo.Kind == info.Kind
                    && string.Equals(
                        PierceRegistry.Normalize(otherInfo.Base),
                        PierceRegistry.Normalize(info.Base),
                        StringComparison.Ordinal)
                    && !Probe.IsStandaloneVolume(other))
                {
                    targets.Add(other);
                }
            }
        }

        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(target);
                // 记下这一笔是本进程销的：判「队列里那个包为什么不在了」与对账都只认这个名单。
                _registry.NoteRemoved(target);
                _log?.Note(removedAt, Path.GetFileName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉多半是被占用（杀软扫描或还在被别的程序打开）。说一句，不得让用户无从判断。
                _log?.Problem(failedAt, Path.GetFileName(target), ex.Message);
            }
        }
    }
}
