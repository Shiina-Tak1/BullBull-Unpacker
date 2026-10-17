using System;
using System.Linq;
using Bbu.Core.Integration;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 右键菜单的键位、命令拼装与旧动词清理。
///
/// 纯函数那几条在默认档跑；真正读写注册表的那几条挂真机档，因为它们要写 HKCU，
/// 而本机开发沙箱拒写注册表。真机档用临时根，不碰用户的真菜单。
/// </summary>
public sealed class ShellMenuTests
{
    /// <summary>真机档用的临时根。跑完必须清掉，绝不落在用户的真菜单上。</summary>
    private const string TestRoot = @"Software\BBUTest\Classes";

    [Fact]
    public void Places_MustCoverFileFolderAndBackground()
    {
        Assert.Equal(3, ShellMenu.Places.Count);
        Assert.Equal(["*", "Directory", @"Directory\Background"], ShellMenu.Places.Select(place => place.Key));
        Assert.Equal(["%1", "%1", "%V"], ShellMenu.Places.Select(place => place.Placeholder));
    }

    /// <summary>
    /// B-2026-029 的判据②：命令是两段各自带引号，不产出多余的空参数。
    /// 1.1.0 的形态里第二段是源码版的 run.py、冻结版为空串，空串会拼出一个空引号段。
    /// C# 侧只有一个 exe，那个分支不存在，这条钉的是拼出来的形状。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-029")]
    public void CommandFor_MustQuoteBothPartsWithoutAnEmptyArgument()
    {
        var command = ShellMenu.CommandFor(@"C:\Program Files\BBU\Bbu.App.exe", "%1");

        Assert.Equal("\"C:\\Program Files\\BBU\\Bbu.App.exe\" \"%1\"", command);
        Assert.DoesNotContain("\"\"", command, StringComparison.Ordinal);
    }

    /// <summary>空白处的占位符是当前目录，不是被点中的那一个文件。</summary>
    [Fact]
    public void BackgroundPlace_MustTakeTheCurrentDirectory()
    {
        var background = ShellMenu.Places.Single(place => place.Key == @"Directory\Background");
        Assert.Equal("%V", background.Placeholder);
    }

    [Fact]
    public void LegacyVerbs_MustNotContainTheCurrentVerb()
    {
        Assert.DoesNotContain(ShellMenu.CurrentVerb, ShellMenu.LegacyVerbs);
        Assert.Contains("BBUUnpack", ShellMenu.LegacyVerbs);
    }

    [Fact]
    public void KeyPath_MustSitUnderTheRootAndTheCurrentVerb()
    {
        var key = ShellMenu.KeyPath(ShellMenu.Places[0], root: @"Software\BBUTest");
        Assert.Equal(@"Software\BBUTest\*\shell\BBUnpacker", key);
    }

    [Fact]
    public void Root_MustHonourTheEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable(ShellMenu.EnvOverride);
        try
        {
            Environment.SetEnvironmentVariable(ShellMenu.EnvOverride, @"Software\BBUTest\Classes");
            Assert.Equal(@"Software\BBUTest\Classes", ShellMenu.Root());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellMenu.EnvOverride, previous);
        }

        Assert.Equal(ShellMenu.DefaultRoot, ShellMenu.Root());
    }

    /// <summary>显式给的根优先于环境变量：测试两条路都不碰真菜单。</summary>
    [Fact]
    public void Root_MustPreferTheExplicitArgument()
    {
        Assert.Equal(@"Software\Explicit", ShellMenu.Root(@"Software\Explicit"));
    }

    // ── 真机档：真正写注册表 ──

    [Fact]
    [Trait("Category", "machine")]
    public void Install_MustWriteAllThreePlacesAndUninstallMustClearThem()
    {
        Cleanup();
        try
        {
            var written = ShellMenu.Install(@"C:\Program Files\BBU\Bbu.App.exe", TestRoot);
            Assert.Equal(3, written.Count);

            var state = ShellMenu.State(TestRoot);
            Assert.True(state.Installed, "三处都写过之后必须是已注册");
            Assert.Equal(3, state.Places.Count);

            // 命令、菜单文案、多选模型与图标四样都要在。
            Assert.True(Registry.TryReadString(KeyOf("*") + @"\command", null, out var command));
            Assert.Equal("\"C:\\Program Files\\BBU\\Bbu.App.exe\" \"%1\"", command);
            Assert.True(Registry.TryReadString(KeyOf("*"), null, out var text));
            Assert.Equal(ShellMenu.MenuText, text);
            Assert.True(Registry.TryReadString(KeyOf("*"), "MultiSelectModel", out var multi));
            Assert.Equal(ShellMenu.MultiSelectModel, multi);
            Assert.True(Registry.TryReadString(KeyOf("*"), "Icon", out var icon));
            Assert.EndsWith(",0", icon, StringComparison.Ordinal);

            var removed = ShellMenu.Uninstall(TestRoot);
            Assert.Equal(3, removed.Count);
            Assert.False(ShellMenu.State(TestRoot).Installed);
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// B-2026-071 的判据：升级之后不许出现两条一模一样的菜单项。
    /// 先按旧动词造一份"上一版留下的键"，再跑卸载，它必须一起消失。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-071")]
    [Trait("Category", "machine")]
    public void Uninstall_MustAlsoClearTheKeysLeftByLegacyVerbs()
    {
        Cleanup();
        try
        {
            foreach (var verb in ShellMenu.LegacyVerbs)
            {
                foreach (var place in ShellMenu.Places)
                {
                    var key = ShellMenu.KeyPath(place, verb, TestRoot);
                    Assert.True(Registry.TryWriteString(key, null, ShellMenu.MenuText));
                    Assert.True(Registry.TryWriteString(key + @"\command", null, "\"old.exe\" \"%1\""));
                }
            }

            ShellMenu.Install(@"C:\BBU\Bbu.App.exe", TestRoot);
            ShellMenu.Uninstall(TestRoot);

            foreach (var verb in ShellMenu.LegacyVerbs)
            {
                foreach (var place in ShellMenu.Places)
                {
                    Assert.False(
                        Registry.TryReadString(ShellMenu.KeyPath(place, verb, TestRoot) + @"\command", null, out _),
                        $"旧动词 {verb} 的键还在：{place.Key}");
                }
            }
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// 卸载报的是"**真的删掉了几处**"。没装过时必须报零 ——
    /// 把"本来就不存在"的键也算成删掉了，用户会看到「已卸载 9 处」（三处现行加六处旧动词）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-116")]
    [Trait("Category", "machine")]
    public void Uninstall_MustCountOnlyWhatItReallyRemoved()
    {
        Cleanup();
        try
        {
            Assert.Empty(ShellMenu.Uninstall(TestRoot));

            ShellMenu.Install(@"C:\BBU\Bbu.App.exe", TestRoot);
            Assert.Equal(3, ShellMenu.Uninstall(TestRoot).Count);
            Assert.Empty(ShellMenu.Uninstall(TestRoot));
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    [Trait("Category", "machine")]
    public void PurgeLegacy_MustNeverTouchTheCurrentVerb()
    {
        Cleanup();
        try
        {
            ShellMenu.Install(@"C:\BBU\Bbu.App.exe", TestRoot);
            ShellMenu.PurgeLegacy(TestRoot);

            Assert.True(ShellMenu.State(TestRoot).Installed, "清旧动词时不许连带把正在用的那条删掉");
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// 失败要带得出 Win32 错误码；换 BCL 之后那个码只能从异常的 <c>HResult</c> 低 16 位取回来
    /// （<c>Registry.Win32Code</c>）。这一条钉住取码的语义：超长键名在 BCL 的键名校验里就被挡下
    /// （不碰注册表，沙箱里也跑得了），低 16 位是 87（ERROR_INVALID_PARAMETER）——
    /// 把取码写成常量，这条立刻红。
    /// </summary>
    [Fact]
    public void RegistryFailureCode_MustComeFromTheException()
    {
        Assert.False(Registry.TryWriteString(@"Software\BBUTest\" + new string('x', 300), null, "x"));
        Assert.Equal(87, Registry.LastError);
    }

    private static string KeyOf(string place) =>
        ShellMenu.KeyPath(ShellMenu.Places.Single(item => item.Key == place), root: TestRoot);

    /// <summary>删掉整个临时根。写不进去时也不抛：清理不该把测试的结论带偏。</summary>
    private static void Cleanup() => Registry.TryDeleteTree(TestRoot);
}
