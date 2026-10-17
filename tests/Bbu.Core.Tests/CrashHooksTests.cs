using System;
using System.IO;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 三个全局异常钩子的处理器（W7 判据 8）：未捕获异常也要落一行失败档、带堆栈。
///
/// 处理器是普通静态方法，所以这里直接调它，不真造未捕获异常 —— 真造出来的那种会把测试进程带走。
/// 接线三处在入口与 <c>App</c> 里，那一条只是次要层：主判据是处理器落下来的那一行。
/// </summary>
public sealed class CrashHooksTests
{
    /// <summary>三个来源各一条：级别是失败档，正文带来源、异常原文与堆栈帧。</summary>
    [Theory, Trait("Bug", "B-2026-308")]
    [InlineData(CrashHooks.SourceAppDomain)]
    [InlineData(CrashHooks.SourceTask)]
    [InlineData(CrashHooks.SourceDispatcher)]
    public void Report_MustWriteOneFailureLineWithTheStack(string source)
    {
        var log = new RecordingLog();
        var error = Caught();

        CrashHooks.Report(log, source, error);

        var line = Assert.Single(log.Infos);
        Assert.Equal(LogLevel.Error, Assert.Single(log.InfoLevels));
        Assert.StartsWith(
            ScanText.Fill(LogText.CrashAt, source, string.Empty),
            line,
            StringComparison.Ordinal);
        Assert.Contains("钩子样本", line, StringComparison.Ordinal);
        Assert.Contains(" at ", line, StringComparison.Ordinal);
        Assert.Contains(nameof(CrashHooksTests), line, StringComparison.Ordinal);
    }

    /// <summary>
    /// 抛出来的东西不是异常时（<c>AppDomain.UnhandledException</c> 的载荷允许不是）也要落一行：
    /// 撤掉那一次空判即红，钩子会在崩溃路径上再抛一次。
    /// </summary>
    [Fact]
    public void Report_MustStillWriteOneLineWhenThePayloadIsNotAnException()
    {
        var log = new RecordingLog();

        CrashHooks.Report(log, CrashHooks.SourceAppDomain, null);

        var line = Assert.Single(log.Infos);
        Assert.Equal(LogLevel.Error, Assert.Single(log.InfoLevels));
        Assert.StartsWith(
            ScanText.Fill(LogText.CrashAt, CrashHooks.SourceAppDomain, string.Empty),
            line,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 两处接线都在场：不装钩子，处理器写得再好也收不到东西。这一条是次要层，主判据是上面两条。
    /// </summary>
    [Fact]
    public void Install_MustBeCalledByTheEntryPointAndByTheDispatcherHook()
    {
        var program = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Bbu.App", "Program.cs"));
        Assert.Contains("CrashHooks.Install(log);", program, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Bbu.App", "App.axaml.cs"));
        Assert.Contains("CrashHooks.SourceDispatcher", app, StringComparison.Ordinal);
    }

    /// <summary>抛一个带已知正文的异常，让堆栈里有本类的帧。</summary>
    private static Exception Caught()
    {
        try
        {
            throw new InvalidOperationException("钩子样本");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
