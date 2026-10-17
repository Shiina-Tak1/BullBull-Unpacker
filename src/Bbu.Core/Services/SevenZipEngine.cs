using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>跑一条引擎命令时的运行期约束。</summary>
public sealed record EngineRunOptions
{
    /// <summary>不传则用引擎的默认时限。</summary>
    public double? TimeoutSeconds { get; init; }

    /// <summary>解压中看一次剩余量的间隔秒数。</summary>
    public double? SpaceCheckSeconds { get; init; }

    /// <summary>事中监控的下限字节数。不大于 0 表示关掉监控。</summary>
    public long MinFreeBytes { get; init; }

    /// <summary>事中盯哪个目录所在的盘。不传则用当前目录。</summary>
    public string? GuardDirectory { get; init; }

    /// <summary>
    /// 「现在该暂停吗」。不传表示这一趟不支持暂停。
    ///
    /// 暂停靠操作系统挂起子进程实现：7z 与 UnRAR 没有暂停接口，而解压到一半重来
    /// 对大包来说代价太大。挂起期间不计入超时，磁盘监控也一并跳过。
    /// </summary>
    public Func<bool>? Pause { get; init; }

    /// <summary>
    /// 本次引擎运行是否把引擎报的百分比送进进度通道。试密码那一路置假（B-2026-146）：
    /// 单条目验证每候选一次、报出的是「解压中 0%」，会覆盖活动槽上的枚举进度；
    /// </summary>
    public bool ReportPercent { get; init; } = true;

    /// <summary>引擎进程起来之后回调一次，带它的进程号。</summary>
    public Action<int>? OnProcessStarted { get; init; }
}

/// <summary>解压一次的选项。</summary>
public sealed record ExtractOptions
{
    /// <summary>不传则按文件头判真实格式再分派，见 <see cref="EngineRouter.KindForPath"/>。</summary>
    public EngineKind? Kind { get; init; }

    public bool Overwrite { get; init; } = true;

    /// <summary>包声明的解压体积。不传则自己列一次目录取，见 <see cref="SevenZipEngine.InspectAsync"/>。</summary>
    public long? DeclaredBytes { get; init; }

    /// <summary>最低剩余空间。不大于 0 表示不判余量，事中监控也一并关掉。</summary>
    public long MinFreeBytes { get; init; }

    public string? GuardDirectory { get; init; }

    public double? TimeoutSeconds { get; init; }

    /// <summary>暂停钩子，语义与 <see cref="EngineRunOptions.Pause"/> 相同。</summary>
    public Func<bool>? Pause { get; init; }
}

/// <summary>
/// 引擎调用的薄壳层：拼命令行、解码输出、翻译退出码、管取消与超时。
///
/// 命令行与退出码语义照抄它，不许重新发明；
/// 它不决定产物目录、密码与格式，那些是上层的事。
/// </summary>
public sealed class SevenZipEngine
{
    /// <summary>超时默认 1800 秒。整包 4.5GB 的 <c>7z t</c> 实测 27 秒，量级留得下。</summary>
    public const double DefaultTimeoutSeconds = 1800.0;

    /// <summary>解压中看一次剩余量的间隔。一次系统调用很便宜，但不必每 100ms 问一遍。</summary>
    public const double DefaultSpaceCheckSeconds = 1.0;

    /// <summary>中止之后等它退出的上限，也是等两条输出流收尾的上限：中止路径上这两步同一个时限。</summary>
    public const double OutputDrainSeconds = 5.0;

    private const int PollMilliseconds = 100;

    /// <summary>同一层里两次百分比之间的最小间隔。判据是「重复的百分比不发」：引擎按固定节奏重复报同一个数，那一条不带新信息。</summary>
    private const double ProgressMinSeconds = 0.1;

    private readonly EnginePaths _paths;
    private readonly IRunLog? _log;
    private readonly double _timeoutSeconds;
    private readonly double _spaceCheckSeconds;
    private readonly Func<string, long?> _freeBytes;

    public SevenZipEngine(
        EnginePaths paths,
        IRunLog? log = null,
        double timeoutSeconds = DefaultTimeoutSeconds,
        double spaceCheckSeconds = DefaultSpaceCheckSeconds,
        Func<string, long?>? freeBytesProbe = null)
    {
        _paths = paths;
        _log = log;
        _timeoutSeconds = timeoutSeconds;
        _spaceCheckSeconds = spaceCheckSeconds;
        _freeBytes = freeBytesProbe ?? DiskFloor.FreeBytes;
    }

    public EnginePaths Paths => _paths;

    /// <summary>日志通道。密码层的心跳与快路汇总走它，为 <c>null</c> 时静默。</summary>
    public IRunLog? Log => _log;

    public double TimeoutSeconds => _timeoutSeconds;

    /// <summary>
    /// 一次 <c>7z l -slt</c> 拿到加不加密、文件名是否也加密、可验证密码的条目与解压后总体积。
    /// 判不了时返回 <see cref="ArchiveInfo.Unknown"/>，它不等于「没加密」：对未加密的包，
    /// 引擎会忽略 <c>-p</c>，验证必然「成功」。
    /// </summary>
    public async Task<ArchiveInfo> InspectAsync(string archive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        var exe = _paths.SevenZip;
        if (exe is null)
        {
            return ArchiveInfo.Unknown;
        }

        var result = await RunAsync(
            EngineCommand.List(exe, archive), EngineKind.SevenZip, null, cancellationToken).ConfigureAwait(false);

        // 没给密码却连目录都列不出来：文件名也是加密的。这里只认强证据 —— 判错的后果很重：
        // 数据坏掉的未加密包会被当成「文件名也加密」，加密判断于是永远撤不掉，
        // 而弱证据的确认逻辑正好依赖它给出「没加密」。
        if (EngineOutput.LooksLikeWrongPassword(result.Output) || EngineOutput.LooksHeaderEncrypted(result.Output))
        {
            return ArchiveInfo.Unknown with { Encrypted = true, HeaderEncrypted = true };
        }

        if (!result.Ok)
        {
            return ArchiveInfo.Unknown;
        }

        return ParseListOutput(archive, result.Output);
    }

    /// <summary>
    /// 随包 UnRAR 能不能把这个文件当压缩包列出来 —— 兜底那一档的第二次问询，是**保险**：
    /// 量过的那批「壳 + 包」与真 WinRAR SFX 里，这一问一次都没改变结论
    /// 判据取**标准输出里的条目行数**：文件不是 RAR 时 <c>lb</c> 的退出码仍是 0、两条流都空
    /// （2026-10-08 实测），所以不能按退出码判。文件名也加密的包（<c>-hp</c>）不给密码时零行、
    /// 退出码非零（RAR5 是 11，RAR4 是 3），同样算读不出 —— 那种包走切包那条路。
    /// </summary>
    public async Task<bool> CanUnRarReadAsync(string archive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        var exe = _paths.UnRar;
        if (exe is null)
        {
            return false;
        }

        var result = await RunAsync(
            [exe, "lb", EngineCommand.NoPasswordArgument, "--", archive],
            EngineKind.UnRar,
            null,
            cancellationToken).ConfigureAwait(false);

        // 不拿特征词表去筛：`lb` 打的是裸条目名，包里只要有个叫 `is not archive` 的条目就会被误判。
        return result.Ok
            && result.StdOut.Split('\n').Any(line => line.Trim().Length > 0);
    }

    /// <summary>
    /// 验证密码与完整性。给了 <paramref name="entry"/> 就只验证这一个条目，大包逐个试密码靠它。
    /// 条目名没匹配上时 7z 仍返回 0，那种情况记 <see cref="EngineExitCodes.EntryNotMatchedCode"/>，
    /// 不许当作密码通过。
    ///
    /// <paramref name="reportPercent"/> 为假时不送进度：试密码那一路（<see cref="VerifyAsync"/>）传假。
    /// </summary>
    public async Task<EngineResult> TestAsync(
        string archive,
        string? password = null,
        EngineKind? kind = null,
        string? entry = null,
        CancellationToken cancellationToken = default,
        bool reportPercent = true,
        Func<bool>? pause = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        var engineKind = kind ?? EngineRouter.KindForPath(archive);

        // 裸 `-p` 在 RAR 命令行里是「请提示我输入密码」：它去读 stdin、返回 12，
        // 于是被上层当成「引擎报错」。7-Zip 的裸 `-p` 才是「空密码」，它也能验 rar5 的密码。
        // 但分卷的非首卷 7-Zip 打不开，那一档必须留在 UnRAR 上 —— 这道保险与「空密码改派」缺一不可。
        if (engineKind is EngineKind.UnRar && password is "" && !IsLaterVolume(archive))
        {
            engineKind = EngineKind.SevenZip;
        }

        var exe = _paths.PathOf(engineKind);
        if (exe is null)
        {
            return MissingEngine(engineKind);
        }

        var result = await RunAsync(
            EngineCommand.Test(engineKind, exe, archive, password, entry),
            engineKind,
            reportPercent && pause is null
                ? null
                : new EngineRunOptions
                {
                    ReportPercent = reportPercent,
                    Pause = pause,
                },
            cancellationToken).ConfigureAwait(false);

        if (entry is not null && result.Ok && EngineOutput.NothingMatched(result.Output))
        {
            result = result with
            {
                Code = EngineExitCodes.EntryNotMatchedCode,
                Output = (result.Output + "\n" + Errors.EntryNotMatchedNotVerified).Trim(),
            };
        }

        return await SettleWeakWrongPasswordAsync(result, archive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 便宜地验证一个密码。三条取舍都由实测驱动：
    ///
    /// * 文件名也加密的包（<c>-mhe=on</c> / <c>-hp</c>）只列目录：解密文件头就足以证明密码，
    ///   读几十 KB；换成整包验证，一个 11GB 的分卷包会变成每个候选读一遍全包。
    /// * 文件名没加密的包只验证最小的那个加密条目：4.5GB 的包里有小文件时，
    ///   一次验证从 27 秒降到几秒。
    /// * 条目名没匹配上时退回整包再验证一次：乱码条目名会把正确密码判成错的。
    ///
    /// <paramref name="info"/> 不传就自己列一次目录。候选循环必须传：包信息与密码无关，
    /// 每个候选再取一次等于每个候选多起一个进程。
    /// </summary>
    public async Task<EngineResult> VerifyAsync(
        string archive,
        string? password,
        EngineKind? kind = null,
        ArchiveInfo? info = null,
        CancellationToken cancellationToken = default,
        Func<bool>? pause = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        info ??= await InspectAsync(archive, cancellationToken).ConfigureAwait(false);

        if (info.HeaderEncrypted)
        {
            var sevenZip = _paths.SevenZip;
            if (sevenZip is null)
            {
                return MissingEngine(EngineKind.SevenZip);
            }

            // 这一路已知文件名是加密的，弱证据在这里定得住，不必再问一次加不加密 ——
            // 试密码是热路径，每个候选多起一个列目录进程不可接受。
            return await RunAsync(
                EngineCommand.List(sevenZip, archive, password),
                EngineKind.SevenZip,
                pause is null ? null : new EngineRunOptions { Pause = pause },
                cancellationToken)
                .ConfigureAwait(false);
        }

        var engineKind = kind ?? EngineRouter.KindForPath(archive);
        if (kind is null
            && _paths.SevenZip is not null
            && info.ReadOk
            && engineKind is EngineKind.UnRar
            && !IsLaterVolume(archive))
        {
            // UnRAR 起一次进程约是 7-Zip 的 2.6 倍，而 7-Zip 只要十几毫秒；换引擎之前先确认它列得出这个包。
            engineKind = EngineKind.SevenZip;
        }

        var entry = info.SmallestEntry ?? info.FirstEntry;
        // 试密码这一整路都不报百分比（B-2026-146），退回整包那一次同样不报。
        var result = await TestAsync(
            archive, password, engineKind, entry, cancellationToken, reportPercent: false, pause: pause)
            .ConfigureAwait(false);
        if (entry is not null && result.EntryNotMatched)
        {
            result = await TestAsync(
                archive, password, engineKind, null, cancellationToken, reportPercent: false, pause: pause)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 是不是分卷的非首卷。7-Zip 打不开非首卷，UnRAR 能，所以这种情况不许改派 7-Zip。
    /// 判不出来时返回真，即按原引擎走。
    /// </summary>
    private static bool IsLaterVolume(string archive)
    {
        try
        {
            var volume = Probe.ClassifyVolume(archive);
            return volume.Kind is VolKind.Part && !volume.IsFirst;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 解压到 <paramref name="outdir"/>，目录由本方法创建。设了最低剩余空间时先按包的声明体积判一次
    /// （还没起引擎就拒绝），再在解压过程中盯实际余量。输出目录建不起来时返回带原因的失败，
    /// 不把异常抛给上层。
    /// </summary>
    public async Task<EngineResult> ExtractAsync(
        string archive,
        string outdir,
        string? password = null,
        ExtractOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        ArgumentException.ThrowIfNullOrEmpty(outdir);
        options ??= new ExtractOptions();

        var engineKind = options.Kind ?? EngineRouter.KindForPath(archive);
        if (engineKind is EngineKind.UnRar && password is "" && !IsLaterVolume(archive))
        {
            _log?.Note(LogText.EmptyPasswordSwitchToSevenZip);
            engineKind = EngineKind.SevenZip;
        }

        var exe = _paths.PathOf(engineKind);
        if (exe is null)
        {
            return MissingEngine(engineKind);
        }

        var guardDirectory = options.GuardDirectory ?? outdir;
        if (options.MinFreeBytes > 0)
        {
            var declaredBytes = options.DeclaredBytes
                ?? (await InspectAsync(archive, cancellationToken).ConfigureAwait(false)).TotalSize;
            var verdict = DiskFloor.Decide(_freeBytes(guardDirectory), declaredBytes, options.MinFreeBytes);
            if (verdict is not DiskFloorVerdict.Ok)
            {
                return new EngineResult
                {
                    OutOfSpace = true,
                    Output = SpaceRefusalText(verdict, options.MinFreeBytes),
                    Command = [],
                };
            }
        }

        try
        {
            Directory.CreateDirectory(outdir);
        }
        catch (Exception ex)
        {
            // 任何建目录失败都要转成明确的失败返回：放出去会冒泡到上层的跑批循环，
            // 最坏的结果是整批中断、状态停在「运行中」。
            return new EngineResult
            {
                Code = EngineExitCodes.OutDirFailedCode,
                Output = string.Format(Format.OutputDirFailedAt, outdir, ex.Message),
            };
        }

        var runOptions = new EngineRunOptions
        {
            TimeoutSeconds = options.TimeoutSeconds,
            MinFreeBytes = options.MinFreeBytes,
            GuardDirectory = guardDirectory,
            Pause = options.Pause,
        };
        var result = await RunAsync(
            EngineCommand.Extract(engineKind, exe, archive, outdir, password, options.Overwrite),
            engineKind,
            runOptions,
            cancellationToken).ConfigureAwait(false);

        return await SettleWeakWrongPasswordAsync(result, archive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 跑一条引擎命令。取消、超时、事中磁盘中止三件事都在这里收口。
    /// 用 100ms 一轮的轮询而不是「启动后干等」：后者在用户点停止时掐不断正在进行的解压。
    /// </summary>
    public async Task<EngineResult> RunAsync(
        IReadOnlyList<string> command,
        EngineKind kind,
        EngineRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException(nameof(command));
        }

        options ??= new EngineRunOptions();
        var timeoutSeconds = options.TimeoutSeconds ?? _timeoutSeconds;
        var spaceCheckSeconds = options.SpaceCheckSeconds ?? _spaceCheckSeconds;
        var display = EngineCommand.Display(command);

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = EngineRunDirectory(command[0]),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (var i = 1; i < command.Count; i++)
        {
            startInfo.ArgumentList.Add(command[i]);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new EngineResult { Code = EngineExitCodes.CannotLaunchCode, LaunchFailed = true, Command = command };
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new EngineResult
            {
                Code = EngineExitCodes.CannotLaunchCode,
                LaunchFailed = true,
                Output = string.Format(Format.CannotLaunchEngineAt, ex.Message),
                Command = command,
            };
        }

        // 进度只从 stdout 解析（两个引擎的进度行都在 stdout），stderr 是纯错误通道。
        // 进度行不进 Output：留着会把 Tail 挤成百分比行，而失败详情取的就是 Tail。
        // 观测口：回调失败不影响这一趟引擎运行。
        try
        {
            options.OnProcessStarted?.Invoke(process.Id);
        }
        catch (Exception)
        {
            // 忽略：它只服务测试与被观测方，没有影响引擎结果的理由。
        }

        var lastPercent = -1;
        TimeSpan lastPercentAt = TimeSpan.Zero;
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void ReportPercent(int percent)
        {
            if (!options.ReportPercent)
            {
                return;
            }

            var at = stopwatch.Elapsed;
            if (percent == lastPercent
                || (lastPercent >= 0 && percent < 100 && (at - lastPercentAt).TotalSeconds < ProgressMinSeconds))
            {
                return;
            }

            lastPercent = percent;
            lastPercentAt = at;
            _log?.Progress(
                LogProgress.Extracting(at.TotalSeconds, percent),
                ScanText.Fill(Format.EnginePercentAt, percent));
        }

        var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, kind, ReportPercent, pumpStop.Token);
        var stderrTask = PumpAsync(process.StandardError.BaseStream, kind, null, pumpStop.Token);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        var cancelled = false;
        var timedOut = false;
        var spaceGuardHit = false;
        var lastSpaceCheck = TimeSpan.Zero;
        var pausedTotal = TimeSpan.Zero;
        TimeSpan? pausedAt = null;
        var paused = false;
        var pauseTried = false;

        while (!exitTask.IsCompleted)
        {
            var slice = await Task.WhenAny(exitTask, Task.Delay(PollMilliseconds)).ConfigureAwait(false);
            if (slice == exitTask)
            {
                break;
            }

            var elapsed = stopwatch.Elapsed;

            // 暂停：挂起子进程。挂起期间不计入超时；挂起失败不算暂停（EnginePausePolicy），磁盘监控与超时抵扣照旧按没暂停算。
            if (options.Pause?.Invoke() == true && !cancellationToken.IsCancellationRequested)
            {
                if (!pauseTried)
                {
                    pauseTried = true;
                    var suspended = ProcessSuspend.Set(process.Id, true);
                    paused = EnginePausePolicy.CountsAsPaused(true, suspended);
                    if (paused)
                    {
                        pausedAt = elapsed;
                    }

                    if (suspended)
                    {
                        // 逐实例回执：并发十几条生产线各一条，降在排错档；批级那句由界面出。
                        _log?.Trace(LogText.PauseSuspended);
                    }
                    else
                    {
                        // 挂不上要留 info：它是「请求了但没挂上」在全仓唯一的痕迹。
                        _log?.Note(LogText.PauseOnly);
                    }
                }
            }
            else if (pauseTried)
            {
                pauseTried = false;
                if (paused)
                {
                    ProcessSuspend.Set(process.Id, false);
                    pausedTotal += elapsed - pausedAt.GetValueOrDefault();
                    paused = false;
                    pausedAt = null;
                    // 逐实例回执，同挂起那条。
                    _log?.Trace(LogText.PauseResumed);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            // 挂起期间引擎没在写盘，这段时间盘被别的进程写满不算到这次解压头上，跳过磁盘监控。
            if (options.MinFreeBytes > 0
                && !paused
                && (elapsed - lastSpaceCheck).TotalSeconds >= spaceCheckSeconds)
            {
                lastSpaceCheck = elapsed;
                var free = _freeBytes(options.GuardDirectory ?? Environment.CurrentDirectory);
                if (DiskFloor.BelowFloor(free, options.MinFreeBytes))
                {
                    spaceGuardHit = true;
                    _log?.Failure(Format.SpaceGuardHitAt, Giga(options.MinFreeBytes));
                    break;
                }
            }

            var pausedFor = pausedTotal + (paused ? elapsed - pausedAt.GetValueOrDefault() : TimeSpan.Zero);
            if ((elapsed - pausedFor).TotalSeconds > timeoutSeconds)
            {
                timedOut = true;
                break;
            }
        }

        // 进程已经跑完时也要收尾，否则进程号被复用时会被误判成「已经挂起了」。
        if (pausedAt is not null)
        {
            ProcessSuspend.Set(process.Id, false);
            pausedTotal += stopwatch.Elapsed - pausedAt.Value;
        }

        if (cancelled || timedOut || spaceGuardHit)
        {
            KillTree(process);
            try
            {
                await exitTask.WaitAsync(TimeSpan.FromSeconds(OutputDrainSeconds)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 杀不掉时也只能如实记下「没等到它退出」，不能在这里再等下去
            }
        }
        else
        {
            await exitTask.ConfigureAwait(false);
        }

        var drainTask = Task.WhenAll(stdoutTask, stderrTask);
        var drained = await DrainedAsync(drainTask, TimeSpan.FromSeconds(OutputDrainSeconds)).ConfigureAwait(false);

        // 超时那一档这两条任务还在读流、也不再有观测方：取消它们，免得返回之后再往日志写进度。
        if (!drained)
        {
            await pumpStop.CancelAsync().ConfigureAwait(false);
        }

        var stdout = drained ? await stdoutTask.ConfigureAwait(false) : "";
        var stderr = drained ? await stderrTask.ConfigureAwait(false) : "";
        stopwatch.Stop();

        var result = new EngineResult
        {
            // 被中止时这里是杀进程留下的退出码，不是引擎给的语义码；原因看那几个标志
            Code = process.HasExited ? process.ExitCode : EngineExitCodes.CannotLaunchCode,
            Output = JoinOutput(stderr, stdout),
            StdOut = stdout,
            Seconds = stopwatch.Elapsed.TotalSeconds,
            Command = command,
            Cancelled = cancelled,
            TimedOut = timedOut,
            SpaceGuardHit = spaceGuardHit,
            OutputDrained = drained,
        };

        if (!result.Ok && !cancelled && !timedOut && !spaceGuardHit)
        {
            // RAR 有专用退出码，优先信它；其余靠输出特征词兜底。这里用粗判：
            // 弱证据单独出现时还不能结论，要等知道包路径的一方确认。
            var wrongPassword = kind is EngineKind.UnRar && result.Code == EngineExitCodes.RarWrongPasswordCode;
            result = result with { WrongPassword = wrongPassword || EngineOutput.WrongPasswordHint(result.Output) };
        }

        // 命令行与结果各一条独立记录：一条记录＝一行，正文里不带换行（环的写指针不变量靠它）。
        _log?.Trace("{0}", display);
        _log?.Trace("{0}", result.Brief);
        return result;
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已经退了
        }
        catch (Win32Exception)
        {
            // 权限不足等情况：后面的等待会如实反映出来
        }
        catch (NotSupportedException)
        {
            // 拿不到进程树时记下即可
        }
    }

    /// <summary>
    /// 边读一条流边切成行（CR 与 LF 都切），逐行挑编码，进度行交给 <paramref name="onPercent"/>、其余进返回值。
    /// 行缓冲跨读取块保留：UTF-8 的多字节字符会被块边界切开，只有在分隔符处切才谈得上「逐行挑编码」。
    /// </summary>
    private static async Task<string> PumpAsync(
        Stream stream,
        EngineKind kind,
        Action<int>? onPercent,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var pending = new List<byte>(256);
        var chunk = new byte[8192];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n' && chunk[i] != (byte)'\r')
                    {
                        continue;
                    }

                    pending.AddRange(chunk.AsSpan(start, i - start));
                    start = i + 1;
                    if (chunk[i] == (byte)'\r' && i + 1 < read && chunk[i + 1] == (byte)'\n')
                    {
                        i++;
                        start = i + 1;
                    }

                    EmitLine(text, pending, kind, onPercent);
                }

                pending.AddRange(chunk.AsSpan(start, read - start));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 进程被杀时管道会断；收尾超时那一档由令牌取消，同样是这里收口
        }

        if (pending.Count > 0)
        {
            EmitLine(text, pending, kind, onPercent);
        }

        return text.ToString();
    }

    private static void EmitLine(StringBuilder text, List<byte> pending, EngineKind kind, Action<int>? onPercent)
    {
        var line = EngineOutput.DecodeLine(pending.ToArray());
        pending.Clear();
        if (onPercent is not null && EngineOutput.TryReadPercent(line, kind, out var percent))
        {
            onPercent(percent);
            return;
        }

        text.Append(line).Append('\n');
    }

    /// <summary>只有弱证据撑着「密码错误」时才需要再确认一次，见 <see cref="SettleWeakWrongPasswordAsync"/>。</summary>
    private static bool IsWeakWrongPasswordOnly(EngineResult result) =>
        !result.Ok
        && result.WrongPassword
        && !result.Cancelled
        && !result.TimedOut
        && !result.SpaceGuardHit
        && !EngineOutput.LooksLikeWrongPassword(result.Output)
        && EngineOutput.HasWeakWrongPasswordHint(result.Output);

    /// <summary>
    /// 弱证据的收口。CRC 与校验和字样对「加密包加错密码」与「未加密包加损坏」同样成立，
    /// 没有区分力：不确认的话，一个只是数据坏掉的未加密包会被判成密码错误，
    /// 上层于是弹密码框问用户要一个根本不存在的密码，真正的原因被盖住。
    /// </summary>
    private async Task<EngineResult> SettleWeakWrongPasswordAsync(
        EngineResult result,
        string archive,
        CancellationToken cancellationToken)
    {
        if (!IsWeakWrongPasswordOnly(result))
        {
            return result;
        }

        var encrypted = (await InspectAsync(archive, cancellationToken).ConfigureAwait(false)).Encrypted;
        if (encrypted is true)
        {
            return result;
        }

        var why = encrypted is false ? Errors.ArchiveNotEncrypted : Errors.ArchiveUnreadable;
        return result with
        {
            WrongPassword = false,
            Output = (result.Output + "\n" + string.Format(Format.WeakEvidenceIsCorruption, why)).Trim(),
        };
    }

    /// <summary>
    /// 解析 <c>7z l -slt</c> 的输出：一条记录一段，<c>Path</c> 开头，后面跟若干字段。
    /// 三处与判据有关：跳过压缩包自己那条记录；跳过目录条目（测它会连整棵子树一起测，
    /// 一次便宜验证会退化成整包验证）；只有加密条目能证明密码对不对。
    /// </summary>
    private static ArchiveInfo ParseListOutput(string archive, string output)
    {
        var lines = output.Split('\n');
        var encrypted = false;
        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("encrypted = +", StringComparison.OrdinalIgnoreCase))
            {
                encrypted = true;
                break;
            }
        }

        var target = SafeFullPath(archive);
        var entryCount = 0;
        long totalSize = 0;
        string? firstEntry = null;
        string? smallestEntry = null;
        var smallestSize = -1L;

        string? path = null;
        var folder = false;
        long size = 0;
        var entryEncrypted = false;

        Flush();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                Flush();
                path = line["Path = ".Length..].Trim();
            }
            else if (line.StartsWith("Folder = ", StringComparison.Ordinal))
            {
                folder = line["Folder = ".Length..].Trim() == "+";
            }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal))
            {
                size = long.TryParse(line["Size = ".Length..].Trim(), out var parsed) ? parsed : 0;
            }
            else if (line.StartsWith("Encrypted = ", StringComparison.Ordinal))
            {
                entryEncrypted = line["Encrypted = ".Length..].Trim() == "+";
            }
        }

        Flush();

        return new ArchiveInfo
        {
            ReadOk = true,
            Encrypted = encrypted,
            EntryCount = entryCount,
            TotalSize = totalSize,
            FirstEntry = firstEntry,
            SmallestEntry = smallestEntry,
        };

        void Flush()
        {
            if (path is null)
            {
                return;
            }

            var current = path;
            path = null;
            if (string.Equals(SafeFullPath(current), target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            entryCount++;
            if (folder)
            {
                return;
            }

            totalSize += size;
            firstEntry ??= current;
            if (!entryEncrypted)
            {
                return;
            }

            if (smallestSize < 0 || size < smallestSize)
            {
                smallestEntry = current;
                smallestSize = size;
            }
        }
    }

    /// <summary>引擎跑在它自己那份的目录里，命令里没带目录时退回当前目录（`ProcessStartInfo` 不许空串）。</summary>
    internal static string EngineRunDirectory(string executable)
    {
        var directory = Path.GetDirectoryName(executable);
        return string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory;
    }

    /// <summary>输出收尾等到了没有：到了返回真，超时返回假（判据下沉在这里，真机上造不出不关管道的替身）。</summary>
    internal static async Task<bool> DrainedAsync(Task<string[]> drainTask, TimeSpan within) =>
        await Task.WhenAny(drainTask, Task.Delay(within)).ConfigureAwait(false) == drainTask;

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
        catch (PathTooLongException)
        {
            return path;
        }
    }

    private static EngineResult MissingEngine(EngineKind kind) =>
        new()
        {
            Code = EngineExitCodes.EngineMissingCode,
            Output = string.Format(Format.MissingEngineAt, KindName(kind)),
        };

    private static string KindName(EngineKind kind) =>
        kind is EngineKind.UnRar ? "UnRAR" : "7-Zip";

    private static string SpaceRefusalText(DiskFloorVerdict verdict, long minFreeBytes) =>
        verdict is DiskFloorVerdict.NotEnoughForThisPack
            ? string.Format(Format.SpaceWillFallBelowAt, Giga(minFreeBytes))
            : string.Format(Format.SpaceBelowFloorAt, Giga(minFreeBytes));

    private static double Giga(long bytes) => bytes / (1024.0 * 1024 * 1024);

    private static string JoinOutput(string stderr, string stdout)
    {
        var head = stderr.Trim();
        var tail = stdout.Trim();
        if (head.Length == 0)
        {
            return tail;
        }

        return tail.Length == 0 ? head : head + "\n" + tail;
    }
}
