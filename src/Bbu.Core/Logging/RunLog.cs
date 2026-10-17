using System;
using System.Globalization;
using System.IO;
using System.Text;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Logging;

/// <summary>日志的对外形状。界面与业务层只依赖它，不依赖具体文件。</summary>
public interface IRunLog
{
    /// <summary>面板订阅这一行来追加显示。进度行也走这里，订阅方按 <see cref="LogRecord.Progress"/> 是否为空分流。</summary>
    event Action<LogRecord>? LineWritten;

    /// <summary>这一次写盘失败的原因原文：每次失败都报，去重由订阅方自己守。</summary>
    event Action<string>? WriteFailed;

    /// <summary>
    /// 排错才看的一行（<see cref="LogLevel.Debug"/>，只进日志文件）。
    /// 四个按语义命名的入口在这里：级别由方法名承载，正文照模板填好再落，调用点只说「这一句是哪一档」。
    /// 这四个成员**不给默认实现**：给了就等于让实现者静默继承一个不记录、不落盘的版本，漏改的代价必须响亮。
    /// </summary>
    void Trace(string template, params object?[] args);

    /// <summary>用户该知道的一行（<see cref="LogLevel.Info"/>）。</summary>
    void Note(string template, params object?[] args);

    /// <summary>该注意但没出错的一行（<see cref="LogLevel.Warn"/>）。</summary>
    void Problem(string template, params object?[] args);

    /// <summary>失败与出错归因的一行（<see cref="LogLevel.Error"/>）。</summary>
    void Failure(string template, params object?[] args);

    /// <summary>带结构的进度（<see cref="LogLevel.Debug"/>）：进活动槽与日志文件，不进面板。</summary>
    void Progress(LogProgress progress, string message);

    /// <summary>结果行：级别按停止原因定（<see cref="StopReason"/>），是入口名之外的第二个指派点。</summary>
    void Result(JobState state, StopReason reason, string subject, string summary);

    /// <summary>把一条记录交给实现：层号只有这条路能带，别的调用点一律用四个语义入口。</summary>
    void Write(LogRecord record);
}

/// <summary>
/// 一个文件：数据根下 <c>userdata\BullBullUnpacker.log</c> 记每一行日志，环形覆盖、长度恒定。
/// 密码**不打码**：命令行里的 <c>-p&lt;密码&gt;</c> 与「命中密码本」都原样落盘，这是既定口径。
/// </summary>
public sealed class RunLog : IRunLog
{
    /// <summary>进度落盘间隔。判据是「进度行不许把结果行顶出环外」：环已按 4 MiB 封顶，所以它防的不是文件变大。</summary>
    private const double ProgressLogSeconds = 5.0;

    private readonly RingLogFile _file;
    private readonly object _gate = new();
    private bool _complainedToConsole;

    private DateTime _lastProgressLogged = DateTime.MinValue;

    public RunLog(string logFilePath)
    {
        LogPath = logFilePath;
        _file = new RingLogFile(logFilePath);
    }

    public event Action<LogRecord>? LineWritten;

    public event Action<string>? WriteFailed;

    /// <summary>日志文件的完整路径。</summary>
    public string LogPath { get; }

    /// <summary>收尾：关掉文件句柄。之后写入只置失败原因、不抛。</summary>
    public void Close() => _file.Close();

    public void Trace(string template, params object?[] args) =>
        Emit(new LogRecord(LogLevel.Debug, ScanText.Fill(template, args)));

    public void Note(string template, params object?[] args) =>
        Emit(new LogRecord(LogLevel.Info, ScanText.Fill(template, args)));

    public void Problem(string template, params object?[] args) =>
        Emit(new LogRecord(LogLevel.Warn, ScanText.Fill(template, args)));

    public void Failure(string template, params object?[] args) =>
        Emit(new LogRecord(LogLevel.Error, ScanText.Fill(template, args)));

    public void Progress(LogProgress progress, string message) =>
        Emit(new LogRecord(LogLevel.Debug, message, 0, progress));

    public void Result(JobState state, StopReason reason, string subject, string summary) =>
        Emit(new LogRecord(LevelFor(state, reason), ResultBody(subject, summary)));

    /// <summary>结果行的级别：判据是「用户要不要为它做点什么」，它与清单状态格的档**有意不同色**。</summary>
    /// 清单说这一项的状态，日志说这件事的严重程度（「输出目录已存在」在清单是提示档、在日志是陈述档）。
    internal static LogLevel LevelFor(JobState state, StopReason reason) =>
        state is JobState.Partial ? LogLevel.Warn : reason switch
        {
            StopReason.NoArchive or StopReason.AlreadyVisited or StopReason.OutputExists
                or StopReason.Cancelled or StopReason.PasswordSkipped => LogLevel.Info,
            StopReason.PasswordExhausted or StopReason.MaxDepth
                or StopReason.SearchIncomplete or StopReason.Ambiguous => LogLevel.Warn,
            StopReason.ExtractFailed or StopReason.EmptyOutput
                or StopReason.NoSpace or StopReason.SpaceGuard => LogLevel.Error,
            // 未命名的枚举值（外部数据强转来的）按最严档，不静默降到陈述档。
            _ => LogLevel.Error,
        };

    /// <summary>清单状态的档：清单那一格照旧按状态取，不跟着结果行的细分走。</summary>
    public static LogLevel StateLevel(JobState state) => state switch
    {
        JobState.Failed => LogLevel.Error,
        JobState.Skipped => LogLevel.Warn,
        JobState.Partial => LogLevel.Warn,
        _ => LogLevel.Info,
    };

    /// <summary>把一条已经成形的记录交给本对象：落盘并广播。上层既有的通道用它转发。</summary>
    public void Write(LogRecord record) => Emit(record);

    /// <summary>会话第一行：版本、形态、构建指纹、argv。出问题先看这一行。</summary>
    public void SessionHeader(string version, string flavor, string buildId, string[] args)
    {
        var body = string.Join(" ",
            LogText.SessionStarted,
            version,
            flavor,
            buildId,
            string.Join(' ', args));

        // 只落盘不广播：这一行带 argv，面板不重复显示命令行（其中可能有明文密码）。
        Emit(new LogRecord(LogLevel.Info, body), broadcast: false);
    }

    /// <summary>记录进本对象的收口：时刻只在**这一处**盖，落盘那一行与广播出去的那条记录共用它。</summary>
    private void Emit(LogRecord record, bool broadcast = true)
    {
        var stamped = record with { At = DateTimeOffset.Now };
        var failure = Persist(stamped);
        if (broadcast)
        {
            Raise(() => LineWritten?.Invoke(stamped));
        }

        ReportFailure(failure);
    }

    /// <summary>唯一一处决定**正文**长什么样（前缀各拼各的）：面板与日志文件共用这一句。</summary>
    public static string Compose(LogRecord record) =>
        record.Layer > 0 ? ScanText.Fill(PierceText.LayerNoteAt, record.Layer, record.Text) : record.Text;

    /// <summary>结果行的正文：模板只在排版这一族里填（与 <see cref="Compose"/> 同处），出口只提供两半原料。</summary>
    private static string ResultBody(string subject, string summary) =>
        ScanText.Fill(ScanText.ItemFinishedAt, subject, summary);

    /// <summary>落盘节流：进度行按间隔放行，其余全放。它防的是进度行挤掉结果行（进度按 0.1 秒一条，不节流几分钟就把环绕满）。</summary>
    private bool ShouldLog(bool progressing)
    {
        if (!progressing)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        if (now - _lastProgressLogged < TimeSpan.FromSeconds(ProgressLogSeconds))
        {
            return false;
        }

        _lastProgressLogged = now;
        return true;
    }

    /// <summary>落一行并回报这一次的写失败原因：控制台兜底只报一次，广播每次都给。</summary>
    private string? Persist(LogRecord record)
    {
        lock (_gate)
        {
            if (ShouldLog(record.Progress is not null))
            {
                _file.WriteLine(Capped(
                    $"[{Stamp(record.At)}] [{Tag(record.Level)}] ", OneLine(Compose(record))));
            }

            var failure = _file.LastError;
            if (failure is null)
            {
                return null;
            }

            if (!_complainedToConsole)
            {
                _complainedToConsole = true;
                Complain(string.Format(Format.LogFileUnavailableAt, _file.Path, failure));
            }

            return failure;
        }
    }

    /// <summary>正文压成一行：判据是**落盘形态**（每一行都是 <c>[时间戳] [级别] 正文</c>），
    /// 而正文来源有 600+ 条文案与引擎原话，落盘侧不兜底就堵不住第二个来源。
    /// 源头那一处（<c>EngineResult.Tail</c> 与 <c>CrashHooks</c>）也压过一遍，两处互相幂等：已经是单行的原样返回。</summary>
    private static string OneLine(string text) => text.ReplaceLineEndings("；");

    /// <summary>整行按 UTF-8 字节数封顶：截断只落在正文，时间戳与级别标签不受影响。</summary>
    private static string Capped(string head, string body)
    {
        var tail = " " + LogText.LineTruncated;
        var room = RingLogFile.MaxLineBytes - Encoding.UTF8.GetByteCount(head) - Encoding.UTF8.GetByteCount(tail);
        if (Encoding.UTF8.GetByteCount(body) <= room)
        {
            return head + body;
        }

        var used = 0;
        var cut = 0;
        foreach (var rune in body.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > room)
            {
                break;
            }

            used += rune.Utf8SequenceLength;
            cut += rune.Utf16SequenceLength;
        }

        return head + body[..cut] + tail;
    }

    /// <summary>把这一次的写失败原因广播给提示条：只出一条提示由订阅方自己守。</summary>
    private void ReportFailure(string? failure)
    {
        if (failure is not null)
        {
            Raise(() => WriteFailed?.Invoke(failure));
        }
    }

    /// <summary>订阅方是界面与提示条：它那边发生什么都与写日志这条路无关。</summary>
    private static void Raise(Action notify)
    {
        try
        {
            notify();
        }
        catch (Exception)
        {
            // 一次解压不该因为少显示一行而失败。
        }
    }

    /// <summary>界面起来之前的唯一兜底通道：那一档还没有提示条可挂，只能写 stderr。</summary>
    private static void Complain(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            // 没有 stderr 可用时只能放弃：报错本身不能把程序带崩
        }
    }

    /// <summary>时间戳只此一处取法：定宽全 ASCII，字典序即时间序，环按它定写指针。</summary>
    private static string Stamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>级别标签名（不带方括号）：档名大写，级别文本只此一处。</summary>
    internal static string Tag(LogLevel level) => level.ToString().ToUpperInvariant();
}
