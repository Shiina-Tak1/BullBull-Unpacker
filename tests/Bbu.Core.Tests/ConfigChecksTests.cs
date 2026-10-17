using Bbu.Core.Configuration;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 开跑前的配置自检：配置不完整时必须在动手之前拦下。
/// 悄悄换一个输出目录比报错更糟 —— 文件散到包旁边，用户过几天才发现（B-2026-024）。
/// </summary>
public sealed class ConfigChecksTests
{
    /// <summary>选了「指定目录」却留空：必须给出一句理由，而且理由里要有那两条出路。</summary>
    [Fact]
    [Trait("Bug", "B-2026-024")]
    public void CustomModeWithoutDirectory_MustBlockTheRun()
    {
        var config = new AppConfig { OutputMode = OutputMode.Custom, OutputDirectory = "   " };

        var reason = ConfigChecks.BlockingReason(config);

        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Contains(Ui.OutputSameDir, reason!);

        // 拦住的同时不许把配置改掉：界面写着「指定目录」，配置里就不许变成原目录。
        // 这一条由签名（只读参数）保证，断言在这里是为了防止以后有人把它改成实例方法。
        Assert.Equal(OutputMode.Custom, config.OutputMode);
        Assert.Equal("   ", config.OutputDirectory);
    }

    /// <summary>填了路径就照跑。「指定目录」这一档只有留空才是问题。</summary>
    [Fact]
    [Trait("Bug", "B-2026-024")]
    public void CustomModeWithDirectory_MustNotBlock()
    {
        var config = new AppConfig { OutputMode = OutputMode.Custom, OutputDirectory = @"D:\产物" };

        Assert.Null(ConfigChecks.BlockingReason(config));
    }

    /// <summary>原目录那一档不看路径 —— 它是从源文件推出来的，从来没有输入框。</summary>
    [Fact]
    [Trait("Bug", "B-2026-024")]
    public void SameDirectoryMode_MustNotBlock()
    {
        var config = new AppConfig { OutputMode = OutputMode.Same, OutputDirectory = "" };

        Assert.Null(ConfigChecks.BlockingReason(config));
    }
}
