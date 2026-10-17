using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>跑批时往上层抛的三条通道。界面只认识这些回调，不认识穿透层。</summary>
public sealed record RunnerHooks
{
    /// <summary>带级别的日志口：域内的薄方法与中间层都收在这一个出口上。</summary>
    public IRunLog? Log { get; init; }

    public Action<Job>? OnItem { get; init; }

    /// <summary>
    /// 问用户要密码的通道。不设就照旧报「密码耗尽」；
    /// 只有 <see cref="UnlockResult.WorthAskingUser"/> 为真时才会被调用。
    /// </summary>
    public AskPasswordAsync? AskPassword { get; init; }

    /// <summary>撞上保险丝时问一句要不要继续。不设表示没人可问，按「不再往下找」收口。</summary>
    public Func<int, int, bool>? AskFuseContinue { get; init; }
}

/// <summary>
/// 顺序执行清单里的可运行项。这一批就是点开始时清单里那些要跑的，**快照**下来：跑的过程中用户又拖了新文件进来，当前这批绝不能把它们也跑了——新进来的那一项刚在界面线程里建好行，
/// 配置与输出目录都是按这一批算的，混进来只会失败得莫名其妙。新项留在清单里等于排队。一个包出意外绝不能带走整批：这一条标失败并写清原因，接着跑下一个。
/// </summary>
public sealed class Runner
{
    private readonly List<Job> _items;
    private readonly SevenZipEngine _engine;
    private readonly Vault _vault;
    /// <summary>
    /// 当前的配置。它不是构造时定死的：跑之前把它换成最新的一份，设置页改完保存之后同一个批次就能看到新名单（B-2026-100）。调用方在跑那一刻现取，不许自己留副本。
    /// </summary>
    private AppConfig _config;

    private readonly RunnerHooks _hooks;
    private readonly Func<bool> _shouldStop;
    private readonly Func<bool> _shouldPause;
    private readonly CancellationToken _cancellation;

    public Runner(
        IReadOnlyList<Job> items,
        SevenZipEngine engine,
        Vault vault,
        AppConfig? config = null,
        RunnerHooks? hooks = null,
        Func<bool>? shouldStop = null,
        Func<bool>? shouldPause = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(vault);
        _items = [.. items];
        _engine = engine;
        _vault = vault;
        _config = config ?? new AppConfig();
        _hooks = hooks ?? new RunnerHooks();
        _shouldStop = shouldStop ?? (() => false);
        _shouldPause = shouldPause ?? (() => false);
        _cancellation = cancellationToken;
    }

    public IReadOnlyList<Job> Items => _items;

    /// <summary>跑之前换一份最新配置。扫描与运行读的必须是同一份（B-2026-100）。</summary>
    public void UseConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>这一批要用哪份排除名单。界面与测试都从此处取值，其他位置不得另存副本。</summary>
    public IReadOnlyList<string> ExcludedExtensions => _config.ExcludedExtensions;

    public async Task<IReadOnlyList<Job>> RunAsync()
    {
        var batch = _items.Where(item => item.Runnable && NeedsRun(item)).ToList();
        if (batch.Count == 0)
        {
            _hooks.Log?.Note(ScanText.NothingToDo);
        }

        // 只问第一次：同一批里后面的目标不再打扰，取舍由那一次决定。
        var askFuseContinue = Probe.AskOncePerBatch(_hooks.AskFuseContinue);

        foreach (var item in batch)
        {
            // 暂停期间不开新任务；正在跑的那一条由引擎层真挂起。
            while (_shouldPause() && !Cancelled)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }

            if (Cancelled)
            {
                item.State = JobState.Skipped;
                item.StoppedByUser = true;
                item.Reason = null;
                Emit(item);
                continue;
            }

            // 源文件在排队期间被删或移走：直接说清楚，不得让引擎只回一句系统找不到指定的文件。
            if (!File.Exists(item.ArchivePath) && !Directory.Exists(item.ArchivePath))
            {
                item.State = JobState.Failed;
                item.Reason = ScanText.SourceGoneWhileQueued;
                _hooks.Log?.Problem(ScanText.ItemFinishedAt, item.DisplayName, item.Reason);
                Emit(item);
                continue;
            }

            try
            {
                await RunOneAsync(item, askFuseContinue).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                item.State = JobState.Failed;
                item.Reason = ScanText.Fill(ScanText.ItemFailedAt, ex.Message);
                _hooks.Log?.Problem(ScanText.ItemFinishedAt, item.DisplayName, item.Reason);
                _hooks.Log?.Note(ScanText.DetailsInLogFile);
                Emit(item);
            }
        }

        Rollup();
        return _items;
    }

    private async Task RunOneAsync(Job item, Func<int, int, bool>? askFuseContinue)
    {
        item.Reset();
        item.MaxLayer = PierceOptions.Budget(_config.MaxDepth);
        item.State = JobState.Running;
        Emit(item);
        _hooks.Log?.Note(ScanText.ItemStartedAt, item.DisplayName);

        var started = Stopwatch.StartNew();
        var piercer = new Piercer(_engine, _vault, OptionsFor(item, askFuseContinue), _cancellation);
        var result = await piercer.RunAsync(item.ArchivePath).ConfigureAwait(false);
        item.Seconds = started.Elapsed.TotalSeconds;
        Apply(item, result);
        Emit(item);
    }

    private PierceOptions OptionsFor(Job item, Func<int, int, bool>? askFuseContinue) => new()
    {
        MaxDepth = _config.MaxDepth,
        MinFreeGb = _config.MinFreeGb,
        FlattenSingleChild = _config.FlattenSingleChild,
        RemoveSource = _config.RemoveSource,
        RemoveIntermediate = _config.RemoveIntermediate,
        ScanAppended = _config.ScanAppended,
        CleanDelete = _config.CleanDelete,
        Workers = _config.PasswordWorkers,
        ExcludeExts = _config.ExcludedExtensions,
        OutputRoot = _config.OutputMode is OutputMode.Custom && _config.OutputDirectory.Trim().Length > 0
            ? _config.OutputDirectory.Trim()
            : null,
        Conflict = _config.Conflict,
        // 判重的账一项一本：跨清单项的同内容各解一份，只有本链自己绕回来才拦。
        Registry = new PierceRegistry(),
        FastPath = new CompositeFastPath(null, _hooks.Log),
        Pause = _shouldPause,
        Ask = _hooks.AskPassword,
        AskFuseContinue = askFuseContinue,

        // 穿透层自己按入口把级别定好，这里原样转交。
        Log = _hooks.Log,
        OnLayer = (depth, _name, _done) => OnLayer(item, depth),
    };

    private void OnLayer(Job item, PackLayer depth)
    {
        // 运行中界面的层号只能往前走。
        item.MaxLayer = PierceOptions.Budget(_config.MaxDepth);
        if (depth > new PackLayer(item.Layer))
        {
            item.Layer = depth.Value;
        }

        Emit(item);
    }

    /// <summary>把一趟的结果映射到清单行的每一栏。映射结论只有这里一处。</summary>
    private void Apply(Job item, PierceResult result)
    {
        item.OutDir = result.OutputDir;
        item.OutputTop = result.Layers.Count > 0 ? result.Layers[0].OutDir : result.OutputDir;
        item.StopReasonText = result.StopReason.Label();
        item.StopReason = result.StopReason;
        item.StoppedByUser = result.StopReason is StopReason.Cancelled;
        item.ReasonDetail = result.StopDetail;

        // 层深用最深包层，不是解压动作次数；而且单调不减——运行中报过的层号结束后不许退回。
        item.Layer = Math.Max(item.Layer, result.DeepestLayer.Value);
        item.MaxLayer = PierceOptions.Budget(_config.MaxDepth);
        item.Timeline.Clear();
        foreach (var layer in result.Layers)
        {
            item.Timeline.Add((
                ScanText.TimelineLayer(layer.Depth.Value),
                Path.GetFileName(layer.Target),
                layer.Ok ? ScanText.OutcomeDone : ScanText.OutcomeFailed));
        }

        // 密码来源取第一个真正用上密码的层：那一层的密码值就在层结果里，不必回密码库旁路查。
        foreach (var layer in result.Layers)
        {
            if (layer.Password.Length > 0)
            {
                item.PasswordSource = layer.PasswordOrigin.Length > 0 ? layer.PasswordOrigin : item.PasswordSource;
                item.Password = layer.Password;
                break;
            }
        }

        if (result.Layers.Count > 0
            && result.Layers.All(layer => layer.PasswordOrigin == PierceText.OriginNoPassword))
        {
            item.PasswordSource = PierceText.OriginNoPassword;
        }

        item.State = MapState(result);
        item.Reason = NoteFor(result);

        // 结果行整条交给通道：级别按停止原因定、记号仍按状态，正文由通道拼。
        _hooks.Log?.Result(item.State, result.StopReason, item.DisplayName, result.Summary());
    }

    /// <summary>结果到清单状态。判定次序本身就是逻辑：有内容没解、取消、输出目录已存在都必须排在完成前面——它们是安全停下，而安全停下不等于做完了。</summary>
    public static JobState MapState(PierceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Partial)
        {
            return JobState.Partial;
        }

        return result.StopReason switch
        {
            StopReason.NoArchive => result.Ok ? JobState.Done : JobState.Failed,
            StopReason.MaxDepth => result.Ok ? JobState.Done : JobState.Failed,
            StopReason.AlreadyVisited => result.Ok ? JobState.Done : JobState.Failed,
            StopReason.Ambiguous => result.Ok ? JobState.Done : JobState.Failed,
            StopReason.SearchIncomplete => result.Ok ? JobState.Done : JobState.Failed,
            StopReason.Cancelled => JobState.Skipped,
            StopReason.OutputExists => JobState.Skipped,
            StopReason.PasswordExhausted => JobState.Skipped,
            StopReason.PasswordSkipped => JobState.Skipped,
            StopReason.ExtractFailed => JobState.Failed,
            StopReason.EmptyOutput => JobState.Failed,
            StopReason.NoSpace => JobState.Failed,
            StopReason.SpaceGuard => JobState.Failed,
            _ => JobState.Failed,
        };
    }

    /// <summary>清单行的备注。有内容没解、或外层文件夹只整理了一半时，必须点名是哪些包。</summary>
    public static string? NoteFor(PierceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var moved = result.Layers.SelectMany(layer => layer.PartialLift).ToList();
        if (moved.Count > 0)
        {
            return ScanText.Fill(ScanText.PartialLiftNoteAt, moved.Count, string.Join('、', moved.Take(3)));
        }

        if (result.Partial)
        {
            if (result.EngineWarning.Length > 0)
            {
                return result.EngineWarning;
            }

            return result.Leftover.Count > 0
                ? ScanText.Fill(
                    ScanText.PartialNoteAt,
                    result.Leftover.Count,
                    result.StopReason.Label(),
                    string.Join('、', result.Leftover.Take(3)))
                : result.StopReason.Label();
        }

        // 取消那一档的状态词本身就是「已停止」（见 StatusLabel），备注再说一遍就是「已停止（已停止）」。
        if (result.StopReason is StopReason.Cancelled)
        {
            return null;
        }

        // 输出目录已存在要**排在那一位之前**：它安全停下了，但那个包确实没解开，备注必须点名，否则界面就是「已跳过 + 空备注」。
        if (result.StopReason is StopReason.OutputExists)
        {
            return result.StopDetail.Length > 0 ? result.StopDetail : result.StopReason.Label();
        }

        if (result.Ok)
        {
            return null;
        }

        // 失败那一行只写这是哪一类失败；引擎原话由同一行的说明栏挂着，这里再抄一遍就是「解压失败 · 解压失败」。
        return result.StopReason.Label();
    }

    /// <summary>
    /// 命令行那一侧的「成功」口径：安全停下不算成功。取消、已跳过、到上限、没找完、解压失败都算没解完，退出码因此非零。
    /// </summary>
    public static bool IsDone(PierceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Ok && !result.Partial && result.StopReason is not StopReason.Cancelled;
    }

    /// <summary>这一项要不要跑：失败要重跑，已完成不要；已跳过与部分完成里只有「改配置能改掉」的那几种放行。</summary>
    /// <remarks>按停止原因判，不按状态判：撞层数上限、重名跳过、密码耗尽这三种用户改完配置正该重跑；
    /// 判重、歧义、没找完与用户当时的选择不放行，放行只会给一个永远无法修好的重试按钮。</remarks>
    public static bool NeedsRun(Job item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.State is JobState.Queued or JobState.Failed)
        {
            return true;
        }

        return item.StopReason is StopReason.MaxDepth
            or StopReason.OutputExists
            or StopReason.PasswordExhausted;
    }

    /// <summary>清单里还有没有排着队、可运行的项——一批跑完据此决定接不接着开下一批；只认排队，失败的不自动重试（否则一个永远解不开的包会变成死循环）。</summary>
    public static bool HasQueued(IEnumerable<Job> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Any(item => item.Runnable && item.State is JobState.Queued);
    }

    /// <summary>这一项是不是已经出了结论。部分完成也算——它不重跑，进度必须跟着它往前走，否则含它的那一批会从一半直接跳到满（B-2026-028）。</summary>
    public static bool IsSettled(JobState state) =>
        state is JobState.Done or JobState.Partial or JobState.Skipped or JobState.Failed;

    /// <summary>六段构成条的底数。每一个状态都有一个键，包括计数为零的——漏一个键就是无保护的下标累加崩掉整批。</summary>
    public static IReadOnlyDictionary<JobState, int> Summarize(IEnumerable<Job> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var counts = new Dictionary<JobState, int>();
        foreach (var state in Enum.GetValues<JobState>())
        {
            counts[state] = 0;
        }

        foreach (var item in items.Where(item => item.Runnable))
        {
            counts[item.State]++;
        }

        return counts;
    }

    /// <summary>清单里真正参与这一批的项数。屏幕上的每个计数都从这里出，不数清单的全部行。</summary>
    public static int RunnableCount(IEnumerable<Job> items) => Summarize(items).Values.Sum();

    /// <summary>已经出了结论的项数（进度分子）。只数可运行项，与 <see cref="RunnableCount"/> 同一个集合。</summary>
    public static int SettledCount(IEnumerable<Job> items) =>
        items.Count(item => item.Runnable && IsSettled(item.State));

    /// <summary>被用户点「停止」停下的项数：从 Skipped 那一格里剔出来，与状态列的词一个口径。</summary>
    public static int StoppedCount(IEnumerable<Job> items) =>
        items.Count(item => item.Runnable && item.State is JobState.Skipped && item.StoppedByUser);

    /// <summary>因故跳过的项数：Skipped 那一格减去被用户停下的那些。</summary>
    public static int SkippedCount(IEnumerable<Job> items) =>
        Summarize(items)[JobState.Skipped] - StoppedCount(items);

    /// <summary>状态列那一格的状态词：用户点「停止」停下的写「已停止」，因故跳过的写「已跳过」。</summary>
    public static string StatusLabel(Job item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDir)
        {
            return string.Empty;
        }

        if (!item.Runnable)
        {
            return Ui.NotRunnableState;
        }

        return item.State is JobState.Skipped && item.StoppedByUser ? Errors.Stopped : item.State.Label();
    }

    /// <summary>
    /// 跑出过产物的那些项各自的第一层产物目录：被取消与失败的两项也算（B-2026-185）。只取第一层——最深那层的目录是套娃里最后一个小目录，用户要的是这次解压的根。
    /// </summary>
    public static IReadOnlyList<string> OutputDirs(IEnumerable<Job> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // 还在排队或正在跑的项，产物目录这一刻还没定。
            if (item.State is JobState.Queued or JobState.Running)
            {
                continue;
            }

            var dir = item.OutputTop ?? item.OutDir;
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full) || !seen.Add(PierceRegistry.Normalize(full)))
            {
                continue;
            }

            output.Add(full);
        }

        return output;
    }

    /// <summary>
    /// 一批输出目录的公共上级。四种情形没有意义、返回空串：只有一个目录、跨盘符、只剩盘符根、只剩 UNC 共享根；返回空串时界面退化成逐条列出。
    /// </summary>
    public static string OutputRootOf(IReadOnlyList<string> dirs)
    {
        ArgumentNullException.ThrowIfNull(dirs);
        if (dirs.Count < 2)
        {
            return string.Empty;
        }

        var full = dirs.Select(Path.GetFullPath).ToList();

        // 根单独拿出来比：UNC 的共享根是两段（\\server\share），按分隔符切词会去掉前导的反斜杠，
        // 于是 server\share 会被当成一个相对且不存在的路径返回给界面（B-2026-156）。盘符档的结论与 1.1.0 一致，不因 UNC 那一条修复而变。
        var root = Path.GetPathRoot(full[0]);
        if (string.IsNullOrEmpty(root)
            || full.Any(path => !string.Equals(
                Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        var parts = full
            .Select(path => path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var common = new List<string>();
        for (var index = 0; ; index++)
        {
            if (index >= parts[0].Length)
            {
                break;
            }

            var token = parts[0][index];
            if (parts.Any(part => index >= part.Length
                    || !string.Equals(part[index], token, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            common.Add(token);
        }

        // 只剩根就是「没有公共上级」，与跨盘符同一档。UNC 的根不带尾分隔符，盘符的带。
        return common.Count == 0
            ? string.Empty
            : root.TrimEnd('\\', '/') + "\\" + string.Join('\\', common);
    }

    private void Rollup()
    {
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (item.Runnable || item.Children.Count == 0)
            {
                continue;
            }

            var kids = item.Children.Where(child => child < _items.Count).Select(child => _items[child]).ToList();
            var done = kids.Count(kid => kid.State is JobState.Done);
            var failed = kids.Count(kid => kid.State is JobState.Failed);
            var skipped = kids.Count(kid => kid.State is JobState.Skipped && !kid.StoppedByUser);
            var stopped = kids.Count(kid => kid.State is JobState.Skipped && kid.StoppedByUser);
            var partial = kids.Count(kid => kid.State is JobState.Partial);

            if (kids.Any(kid => kid.State is JobState.Running))
            {
                item.State = JobState.Running;
            }
            else if (done == kids.Count)
            {
                item.State = JobState.Done;
            }
            else if (failed == kids.Count)
            {
                item.State = JobState.Failed;
            }
            else if (partial > 0 && failed == 0)
            {
                item.State = JobState.Partial;
            }
            else
            {
                item.State = done > 0 ? JobState.Done : JobState.Failed;
            }

            var bits = new List<string> { ScanText.Fill(ScanText.RollupPackCountAt, kids.Count) };
            if (done > 0)
            {
                bits.Add(ScanText.Fill(ScanText.RollupDoneAt, done));
            }

            if (partial > 0)
            {
                bits.Add(ScanText.Fill(ScanText.RollupPartialAt, partial));
            }

            if (failed > 0)
            {
                bits.Add(ScanText.Fill(ScanText.RollupFailedAt, failed));
            }

            if (skipped > 0)
            {
                bits.Add(ScanText.Fill(ScanText.RollupSkippedAt, skipped));
            }

            if (stopped > 0)
            {
                bits.Add(ScanText.Fill(ScanText.RollupStoppedAt, stopped));
            }

            item.Reason = string.Join("、", bits);
            item.Layer = kids.Count > 0 ? kids.Max(kid => kid.Layer) : 0;
            item.Seconds = kids.Sum(kid => kid.Seconds);
            Emit(item);
        }
    }

    private bool Cancelled => _cancellation.IsCancellationRequested || _shouldStop();

    private void Emit(Job item) => _hooks.OnItem?.Invoke(item);
}
