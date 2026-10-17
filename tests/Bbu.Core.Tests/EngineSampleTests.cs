using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 真机档：拿仓外的真样本跑一遍。这些断言的价值就是「真字节」，合成夹具替代不了。
///
/// 样本不在的机器上整类跳过：它是作者的下载目录，不是仓库的一部分，
/// 断言不能因为同事没这个文件就变红。真机档本来就是手动跑的。
/// </summary>
[Trait("Category", "machine")]
[Collection("engine")]
public sealed class EngineSampleTests
{
    /// <summary>真样本：lz4 帧包裹的 rar5，条目名是中文，密码取自密码本。</summary>
    private const string SampleName = "S000.rar.lz4";

    private static string SamplePassword => TestPaths.RealSamplePassword;

    private static string Sample => Path.Combine(TestPaths.RealSampleRoot, SampleName);

    private static bool Available => File.Exists(Sample);

    /// <summary>
    /// 真样本的第一层是 lz4 帧，`7z.dll` 随包之后引擎读得了它。
    ///
    /// 它列出的是一个成员 `S000.rar`（296480673 字节）—— 里面那个 RAR5 才是加密包。
    /// dll 缺席时这条命令直接失败（`Cannot open encrypted archive. Wrong password?`），
    /// 所以这一条同时是"编解码器载体在场"的真字节对照。中文条目名在**第二层**才看得到，
    /// 那要先把 `S000.rar` 解出来，属于穿透层（R6）。
    /// </summary>
    [Fact]
    public async Task EncryptedSample_OuterLz4FrameMustBeReadable()
    {
        if (!Available)
        {
            return;
        }

        var engine = TestPaths.NewEngine();

        var result = await engine.RunAsync(
            EngineCommand.List(TestPaths.BundledSevenZip, Sample), EngineKind.SevenZip);

        Assert.True(result.Ok, "引擎读不了这个 lz4 帧：" + result.Output);
        Assert.Contains("Type = lz4", result.Output, StringComparison.Ordinal);
        Assert.Contains("S000.rar", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain('\ufffd', result.Output);
    }

    /// <summary>
    /// 判据 ②：中文文件名的包列出来不许乱码。
    ///
    /// 乱码不只是难看：乱码的条目名拿去贴到「只验证这一个条目」的命令行上，7z 匹配不到却返回 0，
    /// 于是变成「密码通过」的假阳性。
    ///
    /// 真样本的**外层**是 lz4 帧，第一层只有一个 ASCII 成员名；中文名在帧里的 RAR5 里。
    /// 所以这条断言的是外层那一条：读得出、没有替换字符、而且不会被归到「密码错误」那一类。
    /// 中文名的完整验证要等穿透层把它解出来（R6）。
    /// </summary>
    [Fact]
    public async Task SampleWithPassword_MustListNamesWithoutGarbage()
    {
        if (!Available)
        {
            return;
        }

        var engine = TestPaths.NewEngine();

        // 走 EngineCommand 而不是手拼：字符集开关在那个唯一出处里。手拼的那条少了它，
        // 而解码侧按 UTF-8 优先，条目名里的中文会变成替换字符。
        var command = EngineCommand.List(TestPaths.BundledSevenZip, Sample, SamplePassword);

        var result = await engine.RunAsync(command, EngineKind.SevenZip);

        // 路径本身含「下载」，所以拿它当断言等于什么都没断言：要断言的是**条目名**。
        var paths = result.Output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("Path = ", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.DoesNotContain('\ufffd', path));

        Assert.False(EngineOutput.LooksLikeWrongPassword(result.Output), result.Output);
    }

    /// <summary>
    /// 外层的 lz4 帧不需要密码，所以"不给密码"这件事在外层不构成失败。
    ///
    /// 这条是 R2 那条「不给密码必须落成密码问题」的替代：dll 随包之后，同一条命令
    /// 在外层是成功的（退出码 0），密码要等进到里面那个 RAR5 才谈得上。
    /// 仍然要钉住的是：**不许**把它误归成密码错误。
    /// </summary>
    [Fact]
    public async Task SampleWithoutPassword_MustNotBeBlamedOnThePassword()
    {
        if (!Available)
        {
            return;
        }

        var engine = TestPaths.NewEngine();

        var result = await engine.RunAsync(
            EngineCommand.List(TestPaths.BundledSevenZip, Sample), EngineKind.SevenZip);

        Assert.True(result.Ok, "外层 lz4 帧不需要密码，失败说明前提变了：" + result.Output);
        Assert.False(EngineOutput.LooksLikeWrongPassword(result.Output), result.Output);
    }

    /// <summary>
    /// 真样本的整包验证：dll 随包之后，正确密码下这条路是成功的。
    ///
    /// 这里<b>不断言 <c>Output</c> 非空</b>：<c>TestAsync</c> 给 7z 传了 `-bso0`，
    /// 成功时本来就没有输出（R2 那条断言能过，是因为旧的失败路径把话写进了 stderr）。
    /// 要钉的是结果本身：成功、没被取消、没超时、有给人看的结论。
    /// </summary>
    [Fact]
    public async Task SampleFullTest_MustReportWhatTheEngineSaid()
    {
        if (!Available)
        {
            return;
        }

        var engine = TestPaths.NewEngine();

        var result = await engine.TestAsync(Sample, SamplePassword, EngineKind.SevenZip);

        Assert.True(result.Ok, "外层 lz4 帧 + 正确密码，这条路该是成功的：" + result.Output);
        Assert.Equal(0, result.Code);
        Assert.False(result.WrongPassword);
        Assert.True(result.Seconds >= 0);
        Assert.False(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.True(result.Brief.Length > 0);
        Assert.Equal(EngineExitCodes.IsSuccess(result.Code), result.Ok);
    }

    /// <summary>仓内自带的那份 7z 必须能跑起来：真机档里最基础的一条。</summary>
    [Fact]
    public async Task BundledEngine_MustRun()
    {
        Assert.True(File.Exists(TestPaths.BundledSevenZip), "仓内没有自带 7z：" + TestPaths.BundledSevenZip);

        var engine = TestPaths.NewEngine();

        var result = await engine.RunAsync(
            new[] { TestPaths.BundledSevenZip, "i" }, EngineKind.SevenZip);

        Assert.Equal(0, result.Code);
        Assert.Contains("7-Zip", result.Output);
        Assert.DoesNotContain('\ufffd', result.Output);
    }
}
