using System;
using System.Threading.Tasks;
using Bbu.Core.Texts;

namespace Bbu.Core.Logging;

/// <summary>
/// 三个全局异常钩子的处理器：未捕获异常也要落一行带堆栈的 <c>[ERROR]</c>。
/// 处理器是普通静态方法，断言直接调它，不必真造一次未捕获异常。
/// </summary>
public static class CrashHooks
{
    /// <summary>进程级：<c>AppDomain.UnhandledException</c>。</summary>
    public const string SourceAppDomain = "AppDomain";

    /// <summary>进程级：<c>TaskScheduler.UnobservedTaskException</c>。</summary>
    public const string SourceTask = "TaskScheduler";

    /// <summary>Avalonia 的 Dispatcher：只能在界面起来之后装，因此那一条接线在 <c>App</c> 里。</summary>
    public const string SourceDispatcher = "Dispatcher";

    /// <summary>装进程级的两个钩子。装的位置在入口，日志实例已就绪。</summary>
    public static void Install(IRunLog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(log, SourceAppDomain, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Report(log, SourceTask, e.Exception);
    }

    /// <summary>写一行失败档：来源与异常原文（含堆栈）都要在。</summary>
    public static void Report(IRunLog log, string source, Exception? error) =>
        log.Failure(LogText.CrashAt, source, Describe(error));

    /// <summary>堆栈压成一行：正文里带换行会落成多行，打穿「每行都是 <c>[时间戳] [级别] 正文</c>」。</summary>
    private static string Describe(Exception? error) =>
        error is null ? string.Empty : error.ToString().ReplaceLineEndings("；");
}
