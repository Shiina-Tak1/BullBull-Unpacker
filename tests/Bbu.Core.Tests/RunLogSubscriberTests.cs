using System;
using System.IO;
using Bbu.Core.Logging;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 写日志这条路不许被订阅方拖下水（外部审计第 1 轮 `EP-03(a)`）。
///
/// 判据面是「任何直接订阅 <c>LineWritten</c> 并同步抛异常的订阅者」：它一抛，
/// 写日志这个动作会把异常带回业务侧，一次解压跟着失败，而不是少显示一行。
/// 落盘那一路本身也有兜底（<c>RingLogFile</c> 吞异常），真正没有 <c>catch</c> 覆盖的是订阅者回调这里。
///
/// 生产侧唯一那个订阅方（<c>MainWindow</c> 里那一处 <c>Dispatcher.UIThread.Post</c>）只入队、
/// 同步不抛，真正跑的 <c>OnLine</c> 在 UI 线程上、不在 <c>RunLog.Write</c> 的调用栈里 ⇒
/// 生产路径上造不出可感的失败场景。这一档的用户可感知性因此按「直接订阅方」记，不按界面记。
/// </summary>
public sealed class RunLogSubscriberTests
{
    /// <summary>
    /// 订阅者必抛时，<c>Note</c> 必须照常返回、那一行照样落进日志文件。
    /// 撤掉那个 <c>try/catch</c> 即红：异常直接抛给调用方。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-213")]
    public void LineWritten_MustNotLetASubscriberBreakTheCallerOrTheFile()
    {
        using var directory = new TestDirectory();
        var logPath = directory.Combine(AppInfo.LogFileName);
        var log = new RunLog(logPath);

        log.LineWritten += _ => throw new InvalidOperationException("订阅方坏了");

        log.Note("这一行照旧要落盘");

        Assert.Contains("这一行照旧要落盘", RingLogFile.ReadText(logPath));
    }

    /// <summary>
    /// 对照组：订阅方收到的还是原来那些行。包 <c>try/catch</c> 不许把广播本身改掉 ——
    /// 那样界面就少了一个通道，而不是少一次崩溃。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-213")]
    public void LineWritten_MustStillReceiveEveryLine()
    {
        using var directory = new TestDirectory();
        var log = new RunLog(directory.Combine(AppInfo.LogFileName));
        LogRecord? seen = null;

        log.LineWritten += line => seen = line;
        log.Note("界面看得到这一行");

        Assert.True(seen.HasValue);
        Assert.Equal("界面看得到这一行", seen.Value.Text);
    }
}
