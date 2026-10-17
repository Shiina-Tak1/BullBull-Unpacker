using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 一本密码加候选生成与验证。
///
/// 三件事收在这一个类里，因为它们共享同一份状态：密码本的文件层（读、写、合并、锁）、
/// 候选层（生成、去重、变体展开）、验证循环（顺序、并发、证据分级）。
/// </summary>
public sealed class Vault
{
    /// <summary>候选少于这个数不值得并行：线程开销加前几个通常就命中了。</summary>
    public const int ParallelMinCandidates = 8;

    /// <summary>条目偏多的提醒线。</summary>
    public const int BookSoftWarn = 10000;

    /// <summary>暂停期间两次探问之间的间隔。</summary>
    private const int PausePollMs = 20;

    private const string LockSuffix = ".lock";
    private const double LockWaitSeconds = 10.0;
    private const int ReadRetries = 5;
    private const int WriteRetries = 10;
    private const double RetrySleepSeconds = 0.05;

    /// <summary>盘上的样子（密码到次数）。写盘前的三方合并靠它认出别的写者加过或删过哪一条。</summary>
    private Dictionary<string, int> _diskSnapshot = new(StringComparer.Ordinal);

    private List<VaultEntry> _entries = [];
    private List<string> _notes = [];

    /// <summary>密码本路径。为 <c>null</c> 时是纯内存的一本，不落盘也不是失败。</summary>
    public string? BookPath { get; init; }

    /// <summary>上一次写盘失败的原因。空串表示上次写成功。</summary>
    public string LastWriteError { get; private set; } = string.Empty;

    /// <summary>本实例成功落盘的次数。判据拿它区分「一批写一次」与「逐条各写一次」。</summary>
    internal int Saves { get; private set; }

    /// <summary>这一次读盘是否把老格式升级成了新格式，界面想提示就用它。</summary>
    public bool UpgradedFromLegacy { get; private set; }

    public IReadOnlyList<VaultEntry> Entries => _entries;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>按目录取一本：路径是那个目录下的 <c>密码本.txt</c>。</summary>
    public static Vault FromDirectory(string directory)
    {
        var vault = new Vault { BookPath = Path.Combine(directory, VaultText.BookFileName) };
        vault.Load();
        return vault;
    }

    /// <summary>
    /// 从磁盘重读。老格式（没有分界行、但首行是 `# 密码本`）就地升级成新格式，
    /// 升级前先把用户手写的 `#` 行搬进备注区。读不出来时**保留内存里已有的条目**。
    /// </summary>
    public void Load()
    {
        UpgradedFromLegacy = false;
        if (BookPath is null || !File.Exists(BookPath))
        {
            _entries = [];
            _notes = [];
            _diskSnapshot = new Dictionary<string, int>(StringComparer.Ordinal);
            return;
        }

        var text = ReadBookText();
        if (text is null)
        {
            return;
        }

        _entries = [.. PasswordBook.Parse(text)];
        _notes = [.. PasswordBook.Notes(text)];
        Snapshot();
        if (!text.Contains(VaultText.CommentMarker, StringComparison.Ordinal)
            && PasswordBook.LooksLikeBook(text))
        {
            _notes = [.. PasswordBook.LegacyNotes(text), .. _notes];
            if (Save())
            {
                UpgradedFromLegacy = true;
            }
        }
    }

    /// <summary>直接设定内容，不落盘。测试与「记事本建空本」用。</summary>
    public void SetEntries(IEnumerable<string> passwords) =>
        _entries = [.. passwords.Where(p => p.Length > 0).Select(p => new VaultEntry(p))];

    /// <summary>带成功次数的那一份。只为测试与将来的导入工具准备，不落盘。</summary>
    public void SetEntries(IEnumerable<VaultEntry> entries) => _entries = [.. entries];

    public VaultEntry? Find(string password) =>
        _entries.FirstOrDefault(entry => entry.Password == password);

    /// <summary>用户手动加一条，次数从 0 开始，立刻落盘。空串与重复都不加。</summary>
    public bool Add(string password)
    {
        var value = password ?? string.Empty;
        if (value.Length == 0 || Find(value) is not null)
        {
            return false;
        }

        _entries.Add(new VaultEntry(value));
        if (Save())
        {
            return true;
        }

        if (BookPath is not null)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        return false;
    }

    /// <summary>批量追加到末尾，整批只落盘一次。返回新增、重复、空三条计数。</summary>
    public (int Added, int Duplicate, int Empty) AddMany(IEnumerable<string> passwords)
    {
        var known = new HashSet<string>(_entries.Select(entry => entry.Password), StringComparer.Ordinal);
        var fresh = new List<VaultEntry>();
        int duplicate = 0, empty = 0;
        foreach (var password in passwords)
        {
            var value = password ?? string.Empty;
            if (value.Length == 0)
            {
                empty++;
                continue;
            }

            if (!known.Add(value))
            {
                duplicate++;
                continue;
            }

            fresh.Add(new VaultEntry(value));
        }

        if (fresh.Count == 0)
        {
            return (0, duplicate, empty);
        }

        _entries.AddRange(fresh);
        if (!Save() && BookPath is not null)
        {
            _entries.RemoveRange(_entries.Count - fresh.Count, fresh.Count);
            return (0, duplicate, empty);
        }

        return (fresh.Count, duplicate, empty);
    }

    /// <summary>删一条。落盘失败时内存精确退回原样。</summary>
    public bool Remove(string password)
    {
        var before = _entries.ToList();
        _entries.RemoveAll(entry => entry.Password == password);
        if (_entries.Count == before.Count)
        {
            return false;
        }

        if (BookPath is not null && !Save())
        {
            _entries = before;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 批量删，整批只落盘一次，返回真的删掉的条数。语义与逐条 <see cref="Remove"/> 一致：
    /// 一条密码出现几次就删几次；落盘失败时内存精确退回原样。
    /// </summary>
    public int RemoveMany(IEnumerable<string> passwords)
    {
        var wanted = new HashSet<string>(passwords, StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return 0;
        }

        var before = _entries.ToList();
        _entries.RemoveAll(entry => wanted.Contains(entry.Password));
        var removed = before.Count - _entries.Count;
        if (removed == 0)
        {
            return 0;
        }

        if (BookPath is not null && !Save())
        {
            _entries = before;
            return 0;
        }

        return removed;
    }

    /// <summary>
    /// 记一次成功：次数加一并落盘，没有的就新建一条。落盘失败要把内存精确退回去，
    /// 否则界面显示的和文件里的不一致，而跑批那条路连一句提示都没有（B-2026-055）。
    /// </summary>
    public bool Remember(string password)
    {
        var value = password ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        var entry = Find(value);
        var added = entry is null;
        if (added)
        {
            _entries.Add(new VaultEntry(value, 1));
        }
        else
        {
            _entries[_entries.IndexOf(entry!)] = entry! with { Hits = entry.Hits + 1 };
        }

        if (Save())
        {
            return true;
        }

        if (BookPath is not null)
        {
            if (added)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
            else
            {
                var index = _entries.FindIndex(e => e.Password == value);
                _entries[index] = _entries[index] with { Hits = _entries[index].Hits - 1 };
            }
        }

        return false;
    }

    /// <summary>
    /// 给一个包生成排好序、去过重、展开过变体的候选序列。
    /// 顺序固定四档：文件名里带的、密码本、空密码、弹窗问；本方法只出前三档。
    /// </summary>
    public IReadOnlyList<PasswordCandidate> CandidatesFor(string archivePath)
    {
        var raw = new List<PasswordCandidate>();
        var guess = NamePassword.ExtractFromPath(archivePath);
        if (guess is not null)
        {
            raw.Add(new PasswordCandidate(guess.Value, PasswordOrigin.FileName, guess.Source));
        }

        foreach (var entry in PasswordBook.Sort(_entries))
        {
            raw.Add(new PasswordCandidate(
                entry.Password,
                PasswordOrigin.Book,
                entry.Hits > 0 ? string.Format(VaultText.BookHitDetail, entry.Hits) : string.Empty));
        }

        raw.Add(new PasswordCandidate(string.Empty, PasswordOrigin.Empty));
        return DedupExpand(raw);
    }

    /// <summary>
    /// 去重（保留最靠前的来源）加变体展开（保持顺序）。展开出来的每一条都记住它出自哪一条原文，
    /// 命中变体时才算得回原条目（B-2026-057）。
    /// </summary>
    internal static IReadOnlyList<PasswordCandidate> DedupExpand(IEnumerable<PasswordCandidate> raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<PasswordCandidate>();
        foreach (var candidate in raw)
        {
            var variants = NamePassword.Variants(candidate.Value);
            if (variants.Count == 0)
            {
                variants = [candidate.Value];
            }

            foreach (var variant in variants)
            {
                if (!seen.Add(variant))
                {
                    continue;
                }

                var detail = candidate.Detail;
                if (variant != candidate.Value)
                {
                    detail = (detail + VaultText.VariantDetailSuffix).Trim(' ', '，');
                }

                output.Add(new PasswordCandidate(variant, candidate.Origin, detail, candidate.Value));
            }
        }

        return output;
    }

    /// <summary>
    /// 按固定顺序逐个验证，命中即停。并发只影响快慢，判定仍按候选顺序走，
    /// 所以并行与串行的结论逐项一致。
    /// </summary>
    public async Task<UnlockResult> UnlockAsync(
        SevenZipEngine engine,
        string archivePath,
        UnlockOptions? options = null,
        IFastPath? fastPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        options ??= new UnlockOptions();

        var candidates = CandidatesFor(archivePath);
        if (options.Skip is { Count: > 0 } skip)
        {
            candidates = [.. candidates.Where(candidate => !skip.Contains(candidate.Value))];
        }

        var info = candidates.Count > 0 ? await SafeInspectAsync(engine, archivePath, cancellationToken)
            : null;

        // 分卷不全：先判再试。只在末尾兜底会让一个根本解不开的包照样把整本密码试一遍、
        // 还可能弹窗问用户要密码（B-2026-038）。
        var missing = Probe.MissingVolumeNote(archivePath);
        if (missing is not null)
        {
            return new UnlockResult
            {
                Ok = false,
                StoppedReason = string.Format(VaultText.MissingVolumeAsk, missing),
                Problem = UnlockProblem.MissingVolume,
            };
        }

        // 引擎读不出这个包也不是密码问题。这一道必须在缺分卷之后：缺分卷的包同样列不出目录。
        if (EngineCannotRead(engine, info))
        {
            return new UnlockResult
            {
                Ok = false,
                StoppedReason = EngineCannotReadNote(
                    Probe.ReadabilityOf(Probe.DetectFormat(archivePath), engine.Paths.SevenZip)),
                Problem = UnlockProblem.EngineError,
            };
        }

        // 明确没加密：一个密码都不试。引擎对未加密的包会忽略 `-p`，试下去必然假命中，
        // 界面就会显示一个假的密码来源（B-2026-018、B-2026-027）。
        if (info is { Encrypted: false })
        {
            return new UnlockResult
            {
                Ok = true,
                StoppedReason = VaultText.NoPasswordNeeded,
                Problem = UnlockProblem.NotEncrypted,
            };
        }

        // 开庭一次：包级元数据只解析一次，7z 的常驻对象也跟着这个会话走。
        // 顺序有意：开庭排在 PickWorkers 之前，并发路数才看得见「这个包快路判得了判不了」。
        using var session = fastPath?.Open(archivePath);
        var workers = PickWorkers(candidates.Count, info, options.Workers, session is not null);
        return await RunCandidatesAsync(
            engine, archivePath, candidates, info, workers, session, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>引擎列不出目录时该返回哪一句：缺 <c>7z.dll</c> 编解码器那一档要说清是能力不足，不写成「包坏了」（B-2026-201）。</summary>
    internal static string EngineCannotReadNote(EngineAbility ability) =>
        ability == EngineAbility.NeedsCodecDll ? Probe.AbilityNote(ability) : VaultText.EngineCannotRead;

    /// <summary>
    /// 搜索心跳报出去的秒数：墙钟减去暂停累计的那一段（B-2026-305）。
    /// 判据下沉成纯函数，与 <see cref="EnginePausePolicy"/> 同一种做法：真机上造不出那个时长，传进去才判得了。
    /// </summary>
    internal static double SearchedSeconds(long nowMs, long startedMs, TimeSpan held) =>
        (nowMs - startedMs - (long)held.TotalMilliseconds) / 1000.0;

    private async Task<UnlockResult> RunCandidatesAsync(
        SevenZipEngine engine,
        string archivePath,
        IReadOnlyList<PasswordCandidate> candidates,
        ArchiveInfo? info,
        int workers,
        IFastPathSession? session,
        UnlockOptions options,
        CancellationToken cancellationToken)
    {
        var tried = new List<PasswordCandidate>();
        // 两个计数要在并发里自增，所以装进数组按元素取引用：捕获的局部变量不能取 ref。
        var counters = new int[2];
        var manual = new int[1];
        var finished = false;
        var started = Environment.TickCount64;
        var lastBeat = started;
        var hold = new PauseHold();

        // 暂停的观测点只有这一个：串行路的候选边界与并发路的 lane 查的都是它，于是「停了多久」在这里累计。
        // 引擎那条路按「真挂上」抵扣（SevenZipEngine 的 pausedTotal）；这里按「请求即停」抵扣 —— 枚举在
        // 暂停期间停在候选边界上，一个候选都没试。
        Func<bool>? pause = options.Pause is null
            ? null
            : () => hold.Observe(options.Pause() == true, Environment.TickCount64);

        void Note(PasswordCandidate candidate, EngineResult result)
        {
            try
            {
                options.OnTry?.Invoke(candidate, result);
            }
            catch (Exception)
            {
                // 报进度失败绝不能影响找密码。
            }
        }

        void Beat()
        {
            var now = Environment.TickCount64;
            if (!HeartbeatPolicy.Due((now - lastBeat) / 1000.0, HeartbeatPolicy.SearchSeconds))
            {
                return;
            }

            lastBeat = now;
            var seconds = SearchedSeconds(now, started, hold.Held(now));
            if (!HeartbeatPolicy.Meaningful(seconds))
            {
                return;
            }

            // 进度行只进进度位与日志文件，不进日志面板；挂 Warn 会把整条枚举进度染成警示色（活动槽按级别取色）。
            engine.Log?.Write(new LogRecord(
                LogLevel.Debug,
                ScanText.Fill(
                    VaultText.SearchHeartbeat,
                    Path.GetFileName(archivePath),
                    seconds,
                    tried.Count,
                    candidates.Count),
                options.Layer,
                new LogProgress(seconds, tried.Count, candidates.Count, ProgressStage.Search)));
        }

        // 取消每候选读一次，不节流：按间隔抽查时，候选少于 64 条时整个候选集读不到取消标记，
        // 结论会落成「密码已全部试完」。
        UnlockResult? Stopped()
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            Report();
            return new UnlockResult
            {
                Ok = false,
                Tried = tried,
                StoppedReason = Errors.Stopped,
                Problem = UnlockProblem.Cancelled,
            };
        }

        /// <summary>暂停时停在候选边界上；取消时立刻放行，交给 <see cref="Stopped"/> 收工。</summary>
        async Task WaitWhilePausedAsync()
        {
            while (pause?.Invoke() == true && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PausePollMs).ConfigureAwait(false);
            }
        }

        async Task<EngineResult> AttemptAsync(PasswordCandidate candidate)
        {
            if (candidate.Value.Length > 0
                && session is not null
                && await JudgeAsync(session, candidate.Value).ConfigureAwait(false)
                    is FastPathVerdict.ProbablyWrong)
            {
                Interlocked.Increment(ref counters[0]);
                return new EngineResult
                {
                    Code = EngineExitCodes.FastPathRejectedCode,
                    WrongPassword = true,
                    Output = VaultText.FastPathRejected,
                };
            }

            Interlocked.Increment(ref counters[1]);
            return await engine.VerifyAsync(
                archivePath, candidate.Value, null, info, cancellationToken, pause)
                .ConfigureAwait(false);
        }

        void AnnouncePolicy()
        {
            if (candidates.Count == 0)
            {
                return;
            }

            if (workers > 1)
            {
                engine.Log?.Write(new LogRecord(LogLevel.Info, string.Format(
                    VaultText.ParallelStarted, candidates.Count, workers, string.Empty), options.Layer));
                return;
            }

            if (candidates.Count >= ParallelMinCandidates)
            {
                var why = options.Workers == 1
                    ? VaultText.SerialBecauseConfigured
                    : VaultText.SerialBecauseNotCheap;
                engine.Log?.Write(new LogRecord(LogLevel.Info, string.Format(
                    VaultText.SerialFallback, candidates.Count, why), options.Layer));
            }
        }

        CandidateSweep? sweep = null;

        void Report()
        {
            var (cut, started) = sweep is null
                ? (counters[0], counters[1])
                : (sweep.Rejected, sweep.EngineTried);
            if (cut == 0 && started == 0)
            {
                return;
            }

            var counted = string.Format(VaultText.FastPathReport, cut, started);
            engine.Log?.Trace("{0}", finished
                ? string.Format(VaultText.FastPathReportWhole, counted, candidates.Count)
                : string.Format(VaultText.FastPathReportPartial, counted, candidates.Count, tried.Count));
        }

        if (workers <= 1)
        {
            AnnouncePolicy();
            foreach (var candidate in candidates)
            {
                await WaitWhilePausedAsync().ConfigureAwait(false);
                Beat();
                if (Stopped() is { } gone)
                {
                    return gone;
                }

                var result = await AttemptAsync(candidate).ConfigureAwait(false);
                Note(candidate, result);
                if (Settle(engine.Log, candidate, result, tried, archivePath, info, manual) is { } done)
                {
                    Report();
                    return done;
                }
            }
        }
        else
        {
            // 判定与引擎各走各的生产线：落到引擎那一档的候选不再拖住同批的快路候选。
            // 顺序语义由 CandidateSweep 按候选下标收拢，与串行逐项一致。
            AnnouncePolicy();
            sweep = new CandidateSweep(
                candidates,
                session,
                (candidate, token) => engine.VerifyAsync(
                    archivePath, candidate.Value, null, info, token, pause),
                workers,
                cancellationToken,
                pause);
            await foreach (var outcome in sweep.RunAsync(cancellationToken).ConfigureAwait(false))
            {
                Note(outcome.Candidate, outcome.Result);
                if (Settle(engine.Log, outcome.Candidate, outcome.Result, tried, archivePath, info, manual) is { } done)
                {
                    Report();
                    return done;
                }

                Beat();
                if (Stopped() is { } gone)
                {
                    return gone;
                }
            }
        }

        finished = true;
        Report();
        var reason = candidates.Count == 0 ? VaultText.NoCandidates : VaultText.ProblemExhausted;
        if (manual[0] > 0)
        {
            reason = string.Format(VaultText.NeedsManualSummary, manual[0]);
        }
        var lateMissing = Probe.MissingVolumeNote(archivePath);
        return lateMissing is not null
            ? new UnlockResult
            {
                Ok = false,
                Tried = tried,
                StoppedReason = string.Format(VaultText.MissingVolumeAsk, lateMissing),
                Problem = UnlockProblem.MissingVolume,
            }
            : new UnlockResult
            {
                Ok = false,
                Tried = tried,
                StoppedReason = reason,
                Problem = UnlockProblem.Exhausted,
            };
    }

    /// <summary>
    /// 串行那一路的快路判定：同步的 CPU 工作（zip 的 PBKDF2、7z 的 dll 调用），丢给线程池执行，
    /// 免得占着调用方的上下文。并行那一路不走这里：<see cref="CandidateSweep"/> 用专用线程取活，
    /// 判定天然是并发的（B-2026-103 量过「日志写着 16 路、判定并发度却是 1」那件事）。
    /// 实现方不必为此改动：接口契约本来就要求 <c>Judge</c> 线程安全，见 <see cref="IFastPathSession"/>。
    /// </summary>
    private static Task<FastPathVerdict> JudgeAsync(IFastPathSession session, string password) =>
        Task.Run(() => session.Judge(password));

    /// <summary>按候选顺序判定一个结果：返回非空表示到此为止。</summary>
    private static UnlockResult? Settle(
        IRunLog? log, PasswordCandidate candidate, EngineResult result, List<PasswordCandidate> tried, string archivePath,
        ArchiveInfo? info, int[] manual)
    {
        tried.Add(candidate);
        if (result.Ok)
        {
            return new UnlockResult
            {
                Ok = true,
                Password = candidate.Value,
                Candidate = candidate,
                Tried = tried,
            };
        }

        if (result.Cancelled)
        {
            return new UnlockResult
            {
                Ok = false,
                Tried = tried,
                StoppedReason = Errors.Stopped,
                Problem = UnlockProblem.Cancelled,
            };
        }

        if (result.TimedOut)
        {
            return new UnlockResult
            {
                Ok = false,
                Tried = tried,
                StoppedReason = VaultText.ProblemTimeout,
                Problem = UnlockProblem.Timeout,
            };
        }

        if (result.WrongPassword)
        {
            return null;
        }

        // 这个密码命令行传不过去（含引号、换行或 NUL）：引擎报的错只说明**这一个候选**传不过去，
        // 跳过该候选继续下一个，单个候选不得终止整轮搜索；过程只报一次，结语按跳过个数另写。
        if (EngineCommand.NeedsManual(candidate.Value))
        {
            if (Interlocked.Increment(ref manual[0]) == 1)
            {
                log?.Note(VaultText.NeedsManualSkipped);
            }

            return null;
        }

        var detail = TailLine(result);
        if (File.Exists(archivePath) && EngineOutput.LooksLikeMissingVolume(result.Output))
        {
            return new UnlockResult
            {
                Ok = false,
                Tried = tried,
                StoppedReason = string.Format(VaultText.MissingVolumeSummary, detail),
                Problem = UnlockProblem.MissingVolume,
            };
        }

        // 引擎没给结论，但也没有明确指出这个包读不了：按「这个候选不对」继续试。
        // 停在这里等于把整本密码判死，而正确密码通常还在后面（B-2026-101：
        // zstd / brotli / lz4 / lz5 / lizard 的错密码只回一句泛化的「未指定的错误」）。
        if (info is { Encrypted: true } && !EngineOutput.LooksLikeStructuralFailure(result.Output))
        {
            return null;
        }

        return new UnlockResult
        {
            Ok = false,
            Tried = tried,
            StoppedReason = string.Format(VaultText.EngineErrorSummary, result.Brief, detail),
            Problem = UnlockProblem.EngineError,
        };
    }

    private static string TailLine(EngineResult result)
    {
        var tail = result.Tail;
        var at = tail.LastIndexOf('\n');
        return at >= 0 ? tail[(at + 1)..] : tail.Length > 0 ? tail : result.Brief;
    }

    /// <summary>
    /// 这个包能不能走便宜路径。能定位小条目、或者文件名也加密（只读文件头）都算便宜；
    /// 整包验证是 IO 密集，多开会互相抢盘，那种保持串行。
    ///
    /// <paramref name="fastPathReady"/> 为真时不再看包贵不贵：那两句理由说的是**引擎**，
    /// 而快路不起进程、判定阶段也不读盘 —— 压成一路只会让快路的核闲着。
    /// </summary>
    public static int PickWorkers(int candidateCount, ArchiveInfo? info, int want, bool fastPathReady = false)
    {
        var cpus = Environment.ProcessorCount;
        if (cpus < 2 || candidateCount < ParallelMinCandidates || (info is null && !fastPathReady))
        {
            return 1;
        }

        // 属性模式写成 `is { … }` 而不是 `info.X`：开庭成功时 info 允许为 null
        // （列不出目录的包照样可能判得了），那样写编译器证不出非空。
        var cheap = fastPathReady
            || (info is { ReadOk: true } && (info.SmallestEntry is not null || info.FirstEntry is not null))
            || info is { HeaderEncrypted: true };
        return cheap ? Math.Max(1, Math.Min(cpus, want > 0 ? want : cpus)) : 1;
    }

    private static async Task<ArchiveInfo?> SafeInspectAsync(
        SevenZipEngine engine, string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            return await engine.InspectAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 引擎连包都列不出来：这不是密码问题，一个密码都不该试（B-2026-027）。
    /// 只有真的跑过列目录才算数，缺 7-Zip 时的默认值会把所有包都误判成损坏。
    /// </summary>
    private static bool EngineCannotRead(SevenZipEngine engine, ArchiveInfo? info) =>
        info is not null
        && !info.ReadOk
        && info.Encrypted is null
        && engine.Paths.SevenZip is not null;

    private string? ReadBookText()
    {
        if (BookPath is null)
        {
            return string.Empty;
        }

        if (!File.Exists(BookPath))
        {
            return string.Empty;
        }

        for (var attempt = 0; attempt < ReadRetries; attempt++)
        {
            try
            {
                return PasswordBook.ReadText(BookPath);
            }
            catch (IOException)
            {
                Thread.Sleep((int)(RetrySleepSeconds * 1000 * (attempt + 1)));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep((int)(RetrySleepSeconds * 1000 * (attempt + 1)));
            }
        }

        return null;
    }

    /// <summary>
    /// 整本写回磁盘：写者锁、三方合并、唯一临时名、fsync、原子替换。
    ///
    /// 合并这一步不能省：锁只保证两次写不互相踩，丢更新发生在「读改写」这一层 ——
    /// 两个实例各持一份内存副本时，后写的会整本覆盖前一个写进去的条目（B-2026-056）。
    /// 失败时内存一个字都不动。
    /// </summary>
    public bool Save()
    {
        if (BookPath is null)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(BookPath);
        IReadOnlyList<VaultEntry> entries = _entries;
        IReadOnlyList<string> notes = _notes;
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            for (var attempt = 0; attempt < WriteRetries; attempt++)
            {
                try
                {
                    using (var bookLock = BookLock.Acquire(BookPath, LockWaitSeconds))
                    {
                        if (!bookLock.Held)
                        {
                            // 拿不到锁不写盘：合并兜不住「这中间其他写者写了什么」，结果与原因交回调用方去拒绝写盘（B-2026-206）。
                            LastWriteError = bookLock.Failure;
                            return false;
                        }

                        var merged = MergeFromDisk();
                        if (merged is not { } pair)
                        {
                            // 盘读不出来时拒绝写盘：拿内存副本覆盖整本会抹掉这期间别的写者加进去的条目，
                            // 而用户完全不知情（B-2026-205）。
                            LastWriteError = VaultText.BookUnreadableForMerge;
                            return false;
                        }

                        entries = pair.Entries;
                        notes = pair.Notes;

                        var data = PasswordBook.Render(entries, notes);
                        var tmp = UniqueTempName();
                        try
                        {
                            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var bytes = new UTF8Encoding(false).GetBytes(data);
                                stream.Write(bytes, 0, bytes.Length);
                                // 只保证「要么旧要么新」是不够的：断电时最后一次写入会连同文件一起消失。
                                stream.Flush(true);
                            }

                            File.Move(tmp, BookPath, overwrite: true);
                        }
                        finally
                        {
                            if (File.Exists(tmp))
                            {
                                File.Delete(tmp);
                            }
                        }
                    }

                    break;
                }
                catch (IOException) when (attempt < WriteRetries - 1)
                {
                    // 别的进程正打开着这个密码本（另一个实例的读、用户的记事本）会让替换失败。
                    Thread.Sleep((int)(RetrySleepSeconds * 1000 * (attempt + 1)));
                }
            }

            _entries = [.. entries];
            _notes = [.. notes];
            Snapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastWriteError = ex.Message;
            return false;
        }

        LastWriteError = string.Empty;
        Saves++;
        return true;
    }

    /// <summary>
    /// 写盘前把别的写者在这期间加进来的条目并回来。三条规则：两边都有取次数大的；
    /// 盘上有而这边没有、且上次盘上也没有的，是别的写者新加的，并进来；
    /// 上次盘上有而这次没有的，是有人删过它，尊重删除。
    /// </summary>
    private (IReadOnlyList<VaultEntry> Entries, IReadOnlyList<string> Notes)? MergeFromDisk()
    {
        var text = ReadBookText();
        if (text is null)
        {
            return null;
        }

        var remoteEntries = PasswordBook.Parse(text);
        var remote = remoteEntries.ToDictionary(entry => entry.Password, entry => entry.Hits, StringComparer.Ordinal);
        var local = _entries.ToDictionary(entry => entry.Password, entry => entry.Hits, StringComparer.Ordinal);
        var merged = new List<VaultEntry>();
        foreach (var entry in _entries)
        {
            if (remote.TryGetValue(entry.Password, out var hits))
            {
                merged.Add(entry with { Hits = Math.Max(entry.Hits, hits) });
            }
            else if (!_diskSnapshot.ContainsKey(entry.Password))
            {
                merged.Add(entry);
            }
        }

        var seen = new HashSet<string>(merged.Select(entry => entry.Password), StringComparer.Ordinal);
        foreach (var entry in remoteEntries)
        {
            if (seen.Contains(entry.Password)
                || local.ContainsKey(entry.Password)
                || _diskSnapshot.ContainsKey(entry.Password))
            {
                continue;
            }

            merged.Add(entry);
        }

        var notes = _notes.Count > 0 ? _notes : PasswordBook.Notes(text);
        return (merged, notes);
    }

    private void Snapshot() =>
        _diskSnapshot = _entries.ToDictionary(entry => entry.Password, entry => entry.Hits, StringComparer.Ordinal);

    /// <summary>临时名带进程号与随机串。固定名会让两个写者同时写同一个文件。</summary>
    private string UniqueTempName() =>
        $"{BookPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
}

/// <summary>
/// 暂停时长的累计：传入「现在该暂停吗」与当前时刻，算出已停住的累计（B-2026-305）。
/// 做成纯逻辑是因为真机上造不出那个时长；两条路（串行与并发）查的是同一个观测点，所以只有一份。
/// </summary>
internal sealed class PauseHold
{
    private readonly object _gate = new();
    private TimeSpan _total;
    private long? _at;

    /// <summary>问一次「现在该暂停吗」，同时把这一段停住的时长记下来，答案原样返回给调用方。</summary>
    internal bool Observe(bool requested, long nowMs)
    {
        lock (_gate)
        {
            if (requested)
            {
                _at ??= nowMs;
                return true;
            }

            if (_at is { } at)
            {
                _total += TimeSpan.FromMilliseconds(nowMs - at);
                _at = null;
            }

            return false;
        }
    }

    /// <summary>截至目前已停住的累计，含正在进行的那一段。</summary>
    internal TimeSpan Held(long nowMs)
    {
        lock (_gate)
        {
            return _total + (_at is { } at ? TimeSpan.FromMilliseconds(nowMs - at) : TimeSpan.Zero);
        }
    }
}
