using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 日志层四个语义入口：级别由方法名承载，模板进去、成句出来，落到的级别就是名字说的那一档。
/// 落盘那一行与那个文件的形态也在这里判（W7 判据 2/3/4）：`[时间戳] [级别] 正文`、时间戳与区域无关、
/// 单文件、长度按需增长且不超过容量、不生成 `.1`。
///
/// 破了它的后果是「名字说是失败、盘上却是陈述」：调用点读起来是失败，颜色与筛选取的是另一档，
/// 而这条链上已经不查级别表了 —— 没有断言就没人会发现。
/// </summary>
public sealed class RunLogEntryTests
{
    /// <summary>判据 3 的行格式，逐字：<c>[时间戳] [级别] 正文</c>。</summary>
    private static readonly Regex LogLine = new(
        @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}\] \[(?<tag>DEBUG|INFO|WARN|ERROR)\] ",
        RegexOptions.Compiled);

    /// <summary>模板与它填好之后的样子：两个占位符，第二个是不变文化下的小数。</summary>
    private const string Template = "第 {0} 层：{1} 秒";

    private const string Filled = "第 2 层：1.5 秒";

    /// <summary>排错那一档：级别是 <c>Debug</c>，只进日志文件（不进面板）。</summary>
    [Fact]
    public void Trace_MustLandAtDebug()
    {
        var line = Capture(log => log.Trace(Template, 2, 1.5));

        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Null(line.Progress);
        Assert.Equal(Filled, line.Text);
    }

    /// <summary>陈述那一档：级别是 <c>Info</c>，进面板。</summary>
    [Fact]
    public void Note_MustLandAtInfo()
    {
        var line = Capture(log => log.Note(Template, 2, 1.5));

        Assert.Equal(LogLevel.Info, line.Level);
        Assert.Null(line.Progress);
        Assert.Equal(Filled, line.Text);

        // 层号前缀只有一处来源：记录带了层号，正文排版就必须把它补成「第 N 层 正文」，不带方括号。
        Assert.StartsWith("第 3 层 ", RunLog.Compose(line with { Layer = 3 }), StringComparison.Ordinal);
    }

    /// <summary>该注意那一档：级别是 <c>Warn</c>。</summary>
    [Fact]
    public void Problem_MustLandAtWarn()
    {
        var line = Capture(log => log.Problem(Template, 2, 1.5));

        Assert.Equal(LogLevel.Warn, line.Level);
        Assert.Null(line.Progress);
        Assert.Equal(Filled, line.Text);
    }

    /// <summary>失败那一档：级别是 <c>Error</c>。</summary>
    [Fact]
    public void Failure_MustLandAtError()
    {
        var line = Capture(log => log.Failure(Template, 2, 1.5));

        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Null(line.Progress);
        Assert.Equal(Filled, line.Text);
    }

    /// <summary>
    /// 结果行：级别不由入口名给，而是由停止原因定 —— 这一条钉的是**接线**（<c>Result</c> 里那一次 <c>LevelFor</c>）。
    /// 撤掉它（换成恒一档）时，<c>LevelFor</c> 自己的取值断言仍然通过，只有这里会红；第一例还钉了
    /// 「日志行与清单格有意不同色」—— 清单是提示档、日志是陈述档。
    /// </summary>
    [Theory]
    [InlineData(JobState.Skipped, StopReason.OutputExists, LogLevel.Info, LogLevel.Warn)]
    [InlineData(JobState.Partial, StopReason.MaxDepth, LogLevel.Warn, LogLevel.Warn)]
    [InlineData(JobState.Failed, StopReason.ExtractFailed, LogLevel.Error, LogLevel.Error)]
    public void Result_MustLandAtTheLevelForTheStopReason(
        JobState state, StopReason reason, LogLevel level, LogLevel stateLevel)
    {
        var line = Capture(log => log.Result(state, reason, "包.7z", "结果说明"));

        Assert.Equal(level, line.Level);
        Assert.Equal(RunLog.LevelFor(state, reason), line.Level);

        // 清单格那一侧不跟着走：第一例就是「有意不同色」。
        Assert.Equal(stateLevel, RunLog.StateLevel(state));

        Assert.Null(line.Progress);
        // 结果行的正文就是模板填好的那一句：正文里零记号，级别只由颜色承载。
        var body = ScanText.Fill(ScanText.ItemFinishedAt, "包.7z", "结果说明");
        Assert.Equal(body, line.Text);
    }

    /// <summary>
    /// 跑一次入口，接住广播出来的那一行：模板按不变文化填，级别由入口自己带。
    /// 落盘那一行也在这里一并核：它带的是同一个语义级别的标签（标签取级别、不是通道 ——
    /// 取通道的话「需要注意」与「失败」两档根本打不出来）。撤掉标签即红。
    /// </summary>
    private static LogRecord Capture(Action<IRunLog> call)
    {
        using var directory = new TestDirectory();
        var log = new RunLog(directory.Combine(AppInfo.LogFileName));
        var seen = new List<LogRecord>();
        log.LineWritten += seen.Add;

        call(log);

        var line = Assert.Single(seen);

        // 写句柄是常开的：读回必须走共享读取那一处，自己开文件会撞 Windows 的共享规则。
        var written = RingLogFile.ReadText(log.LogPath);
        Assert.Contains($"[{RunLog.Tag(line.Level)}] ", written, StringComparison.Ordinal);
        Assert.DoesNotContain("[详细]", written, StringComparison.Ordinal);

        return line;
    }

    /// <summary>
    /// W7 判据 3：落盘的每一行都是 `[时间戳] [级别] 正文`，级别标签取四个大写档名。
    /// 撤掉标签、改成小写、或把排错那一档写成 `TRACE` 即红。
    /// </summary>
    [Fact]
    public void LogLine_MustBeATimestampedTaggedBody()
    {
        using var directory = new TestDirectory();
        var log = new RunLog(directory.Combine(AppInfo.LogFileName));

        log.Trace("排错那一行");
        log.Note("陈述那一行");
        log.Problem("注意那一行");
        log.Failure("失败那一行");

        var lines = Lines(RingLogFile.ReadText(log.LogPath));

        Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.Matches(LogLine, line));
        Assert.Equal(
            new[] { "DEBUG", "INFO", "WARN", "ERROR" },
            lines.Select(line => LogLine.Match(line).Groups["tag"].Value));
        Assert.All(lines, line => Assert.EndsWith("那一行", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// W7 判据 4：时间戳与线程区域无关 —— `th-TH` 的佛历会写 2569、`ar-SA` 的历法会写 1448，
    /// 不传 culture 就正是这两个数。撤掉那一次 `InvariantCulture` 即红。
    /// </summary>
    [Theory, Trait("Bug", "B-2026-306")]
    [InlineData("th-TH", "2569")]
    [InlineData("ar-SA", "1448")]
    public void Timestamp_MustNotFollowTheThreadCulture(string culture, string forbiddenYear)
    {
        using var directory = new TestDirectory();
        var log = new RunLog(directory.Combine(AppInfo.LogFileName));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            log.Note("区域无关那一行");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var line = Assert.Single(Lines(RingLogFile.ReadText(log.LogPath)));
        Assert.StartsWith($"[{DateTimeOffset.Now.Year:0000}-", line, StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenYear, line, StringComparison.Ordinal);
        Assert.Matches(LogLine, line);
    }

    /// <summary>
    /// W7 判据 2 的门面那一半：数据目录里只有这一个日志文件，没有 `.1`、也没有第二个文件，
    /// 长度按需增长而不是预分配容量。
    /// 反向对照：把老形态的第二个 sink、`.1` 轮转、或构造即定长到容量的做法任一加回来，三条里就有一条红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-307")]
    public void LogFile_MustBeTheOnlyFileInItsDirectory()
    {
        using var directory = new TestDirectory();
        var log = new RunLog(directory.Combine(AppInfo.LogFileName));

        log.Note("门面那一行");

        var text = RingLogFile.ReadText(log.LogPath);
        var line = Assert.Single(Lines(text));
        var length = new FileInfo(log.LogPath).Length;

        // 未绕圈时长度就是写进去的字节数（行末一个换行）：预分配或轮转都会在这两条上红。
        Assert.Equal(Encoding.UTF8.GetByteCount(line) + 1L, length);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), length);
        Assert.True(length < RingLogFile.Capacity);
        Assert.Equal(log.LogPath, Assert.Single(Directory.GetFiles(directory.Path)));
        Assert.False(File.Exists(log.LogPath + ".1"));
    }

    /// <summary>读回文本切成非空行。</summary>
    private static List<string> Lines(string text) =>
        text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();
}
