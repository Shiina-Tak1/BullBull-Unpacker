using System;
using System.Collections.Generic;
using System.IO;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;

namespace Bbu.Core.Tests;

/// <summary>
/// 测试用的路径与引擎装配。
///
/// 夹具在 <c>tests\fixtures\</c>，可重复重建。
/// 两个引擎都用仓内随包的那两份：跑断言的二进制与发布包里的是同一份。
/// 注意测试进程的程序目录是 <c>bin\</c>，不是 <c>src\Bbu.App\</c>，所以这里显式指向仓内路径 ——
/// 「程序目录旁」那条由  用临时目录验。
/// </summary>
internal static class TestPaths
{
    public static string Root { get; } = FindRoot();

    public static string Fixtures => Path.Combine(Root, "tests", "fixtures");

    public static string BundleFolder => Path.Combine(Root, "src", "Bbu.App", "engines", "7z");

    public static string BundledSevenZip => Path.Combine(BundleFolder, "7z.exe");

    /// <summary>随包 UnRAR 的目录与二进制。它只有随包一处，2026-10-07 起 rar 那一路全靠它。</summary>
    public static string UnRarFolder => Path.Combine(Root, "src", "Bbu.App", "engines", "unrar");

    public static string BundledUnRar => Path.Combine(UnRarFolder, "UnRAR.exe");

    /// <summary>
    /// 真样本目录（仓外，只读）。取值来自环境变量 <c>BBU_REAL_SAMPLE_ROOT</c>：
    /// 这个路径只在本机存在，写成字面量会跟着同步进公开仓（由「本机绝对路径门禁」判）。
    /// 取不到时是空串，用到它的断言自己先判存在。
    /// </summary>
    public static string RealSampleRoot =>
        Environment.GetEnvironmentVariable("BBU_REAL_SAMPLE_ROOT") ?? string.Empty;

    /// <summary>
    /// 真机档用的大样本：914 MB 的 rar。进度那一条要它才采样得到多个中间值 —— 仓内夹具都在几百 KB 以下，
    /// 引擎只来得及报一个点。取值来自环境变量 <c>BBU_BIG_SAMPLE</c>，与 <see cref="RealSampleRoot"/> 一样
    /// 是仓外文件，取不到即空串，不在时断言自己返回。
    /// </summary>
    public static string BigSample =>
        Environment.GetEnvironmentVariable("BBU_BIG_SAMPLE") ?? string.Empty;

    /// <summary>
    /// 真样本自己的口令。它与样本文件一样只存在于开发机，所以同样取自环境变量
    /// <c>BBU_SAMPLE_PASSWORD</c>，取不到即空串。写成字面量会跟着同步进公开仓。
    /// </summary>
    public static string RealSamplePassword =>
        Environment.GetEnvironmentVariable("BBU_SAMPLE_PASSWORD") ?? string.Empty;

    public static string Fixture(string name) => Path.Combine(Fixtures, name);

    /// <summary>识别层夹具的根：<c>tests\fixtures\probe\</c>。</summary>
    public static string ProbeRoot => Path.Combine(Fixtures, "probe");

    public static string Probe(string name) => Path.Combine(ProbeRoot, name);

    /// <summary>穿透层夹具的根：<c>tests\fixtures\pierce\</c>，每个子目录是一份样本。</summary>
    public static string PierceRoot => Path.Combine(Fixtures, "pierce");

    /// <summary>不传 <paramref name="freeBytesProbe"/> 时用真的磁盘余量。</summary>
    public static SevenZipEngine NewEngine(
        IRunLog? log = null,
        double timeoutSeconds = SevenZipEngine.DefaultTimeoutSeconds,
        double spaceCheckSeconds = SevenZipEngine.DefaultSpaceCheckSeconds,
        Func<string, long?>? freeBytesProbe = null) =>
        new(new EnginePaths(BundledSevenZip, BundledUnRar), log, timeoutSeconds, spaceCheckSeconds, freeBytesProbe);

    /// <summary>rar 那一路的引擎：现在与 <see cref="NewEngine"/> 是同一份装配，保留这个入口是为了让测试桩的意图可读。</summary>
    public static SevenZipEngine NewRarEngine(IRunLog? log = null) => NewEngine(log);

    /// <summary>长命令：7z 的自带基准测试，本机实测跑 11 秒。取消与超时的断言都靠它。</summary>
    public static IReadOnlyList<string> LongRunningCommand =>
        [BundledSevenZip, "b", "-mmt1", "-md=24"];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bbu.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("定位不到仓库根");
    }
}

/// <summary>
/// 把日志收到的行分桶记下来，供「文案是否说清原因」这类断言用。
/// 分桶判据：带进度结构的进 <see cref="Progresses"/>，其余按级别进 <see cref="Debugs"/> 或 <see cref="Infos"/>；
/// 七个入口记进同一批清单，级别按方法名给（与真实现同口径，模板照填）。
/// </summary>
internal sealed class RecordingLog : IRunLog
{
    /// <summary>假实现只记账，不广播也不报写失败：这两条订阅面由真实现那一侧钉。</summary>
    public event Action<LogRecord>? LineWritten
    {
        add { }
        remove { }
    }

    public event Action<string>? WriteFailed
    {
        add { }
        remove { }
    }

    public List<string> Infos { get; } = [];

    /// <summary><see cref="Infos"/> 里每一行的级别，与它同序。</summary>
    public List<LogLevel> InfoLevels { get; } = [];

    public List<string> Debugs { get; } = [];

    /// <summary>进度通道收到的行。</summary>
    public List<string> Progresses { get; } = [];

    /// <summary>进度通道收到的结构，与 <see cref="Progresses"/> 同序。</summary>
    public List<LogProgress> ProgressFacts { get; } = [];

    public void Progress(LogProgress progress, string message)
    {
        Progresses.Add(message);
        ProgressFacts.Add(progress);
    }

    public void Trace(string template, params object?[] args) => Debugs.Add(ScanText.Fill(template, args));

    public void Note(string template, params object?[] args)
    {
        Infos.Add(ScanText.Fill(template, args));
        InfoLevels.Add(LogLevel.Info);
    }

    public void Problem(string template, params object?[] args)
    {
        Infos.Add(ScanText.Fill(template, args));
        InfoLevels.Add(LogLevel.Warn);
    }

    public void Failure(string template, params object?[] args)
    {
        Infos.Add(ScanText.Fill(template, args));
        InfoLevels.Add(LogLevel.Error);
    }

    public void Result(JobState state, StopReason reason, string subject, string summary) =>
        Write(new LogRecord(
            RunLog.LevelFor(state, reason),
            ScanText.Fill(ScanText.ItemFinishedAt, subject, summary)));

    /// <summary>记录级入口：判据与真实现同一处 —— 带进度结构的按进度行收，其余按结果行收。
    /// 少了这一分流，走 <c>Write</c> 进来的进度行会落进 <see cref="Infos"/>，
    /// <see cref="ProgressFacts"/> 就一直是空的。</summary>
    public void Write(LogRecord record)
    {
        if (record.Progress is { } progress)
        {
            Progress(progress, RunLog.Compose(record));
            return;
        }

        Infos.Add(RunLog.Compose(record));
        InfoLevels.Add(record.Level);
    }
}

/// <summary>
/// 把 <see cref="IRunLog"/> 收到的一行原样交给一个「(级别, 正文)」收集器。
/// 夹具里原本就是这么一个 lambda（<c>Log = (_, message) => logs.Add(message)</c>）；
/// 通道并成接口之后用这个替身把它包住，断言一个字都不用改。
/// 进度那条通道按原样单独收：并之前它本来就不走这个 lambda。
/// </summary>
internal sealed class ChannelLog(Action<LogLevel, string> sink, Action<LogProgress>? onProgress = null) : IRunLog
{
    /// <summary>假实现只转发，不广播也不报写失败：这两条订阅面由真实现那一侧钉。</summary>
    public event Action<LogRecord>? LineWritten
    {
        add { }
        remove { }
    }

    public event Action<string>? WriteFailed
    {
        add { }
        remove { }
    }

    public List<string> Progresses { get; } = [];

    public List<LogProgress> ProgressFacts { get; } = [];

    public void Progress(LogProgress progress, string message)
    {
        Progresses.Add(message);
        ProgressFacts.Add(progress);
        onProgress?.Invoke(progress);
    }

    public void Trace(string template, params object?[] args) => sink(LogLevel.Debug, Compose(template, args));

    public void Note(string template, params object?[] args) => sink(LogLevel.Info, Compose(template, args));

    public void Problem(string template, params object?[] args) => sink(LogLevel.Warn, Compose(template, args));

    public void Failure(string template, params object?[] args) => sink(LogLevel.Error, Compose(template, args));

    public void Result(JobState state, StopReason reason, string subject, string summary) =>
        Write(new LogRecord(
            RunLog.LevelFor(state, reason),
            ScanText.Fill(ScanText.ItemFinishedAt, subject, summary)));

    /// <summary>记录级入口：判据与真实现同一处 —— 带进度结构的走 <see cref="Progress"/>，
    /// 其余走 <paramref name="sink"/>。</summary>
    public void Write(LogRecord record)
    {
        if (record.Progress is { } progress)
        {
            Progress(progress, RunLog.Compose(record));
            return;
        }

        sink(record.Level, RunLog.Compose(record));
    }

    private static string Compose(string template, object?[] args) => ScanText.Fill(template, args);
}

