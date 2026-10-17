using System.Collections.Generic;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 命令行拼装。
///
/// 三处最容易抄错的：UnRAR 把输出目录当位置参数（末尾那个分隔符不能省）；单条目验证的 7z **不给**
/// <c>-bso0</c>（否则条目名不匹配会变成假阳性）；<c>-p</c> 与密码必须是一个参数。
///
/// 形状在 2026-10-07 对 UnRAR 7.30.1 与 Rar 7.23 逐条复测过：本节拼出来的每一条
/// 两个引擎都接受，退出码与产物逐档一致。
/// 两处开关都是为了拿真实进度：7z 用 <c>-bsp1</c>、UnRAR 用 <c>-idc</c>。
/// 2026-10-11（-1）7z 的各条命令加 <c>-sccUTF-8</c>，输出与宿主代码页脱钩。
/// </summary>
public sealed class EngineCommandTests
{
    private const string SevenZip = @"C:\程序\7z.exe";
    private const string UnRar = @"C:\程序\UnRAR.exe";
    private const string Archive = @"D:\包\中文包.zip";
    private const string OutDir = @"D:\产物\中文包";

    [Fact, Trait("Bug", "B-2026-219")]
    public void ExtractSevenZip_MustMatchTheContract()
    {
        Assert.Equal(
            new[] { SevenZip, "x", "-o" + OutDir, "-y", "-aoa", "-bso0", "-bsp1", "-sccUTF-8", "-p秘密", "--", Archive },
            EngineCommand.Extract(EngineKind.SevenZip, SevenZip, Archive, OutDir, "秘密"));

        // 跳过同名那一档：`-aos` 与 UnRAR 的 `-o-` 同义。撤掉它，将来接上 Skip 会静默变成覆盖。
        Assert.Equal(
            new[] { SevenZip, "x", "-o" + OutDir, "-y", "-aos", "-bso0", "-bsp1", "-sccUTF-8", "-p-", "--", Archive },
            EngineCommand.Extract(EngineKind.SevenZip, SevenZip, Archive, OutDir, null, overwrite: false));
    }

    [Fact, Trait("Bug", "B-2026-217")]
    public void ExtractUnRar_MustPutTheDirectoryLastAndHonourOverwrite()
    {
        Assert.Equal(
            new[] { UnRar, "x", "-o+", "-y", "-idc", "-@", "-p秘密", "--", Archive, OutDir + "\\" },
            EngineCommand.Extract(EngineKind.UnRar, UnRar, Archive, OutDir, "秘密"));
        Assert.Equal(
            new[] { UnRar, "x", "-o-", "-y", "-idc", "-@", "-p-", "--", Archive, OutDir + "\\" },
            EngineCommand.Extract(EngineKind.UnRar, UnRar, Archive, OutDir, null, overwrite: false));
    }

    [Fact]
    public void TestSevenZip_MustHideOutputExceptForSingleEntry()
    {
        Assert.Equal(
            new[] { SevenZip, "t", "-y", "-bso0", "-bsp1", "-sccUTF-8", "-p-", "--", Archive },
            EngineCommand.Test(EngineKind.SevenZip, SevenZip, Archive, null));
        Assert.Equal(
            new[] { SevenZip, "t", "-y", "-bsp1", "-sccUTF-8", "-p-", "--", Archive, "内层/文件.bin" },
            EngineCommand.Test(EngineKind.SevenZip, SevenZip, Archive, null, "内层/文件.bin"));
    }

    /// <summary>
    /// `@` 开头的条目名对两家引擎是不同的东西：UnRAR 会拿它去读本地文件，7z 自己的 `--` 就把它停了。
    /// 所以只有 UnRAR 侧加 `-@`，7z 侧一个字都不动（B-2026-217）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-217")]
    public void AtSignEntry_MustDisableListFileParsingOnUnRarOnly()
    {
        Assert.Contains("-@", EngineCommand.Test(EngineKind.UnRar, UnRar, Archive, "秘密", "@条目"));
        Assert.Contains("-@", EngineCommand.Extract(EngineKind.UnRar, UnRar, Archive, OutDir, "秘密"));
        Assert.DoesNotContain("-@", EngineCommand.Test(EngineKind.SevenZip, SevenZip, Archive, "秘密", "@条目"));
        Assert.DoesNotContain("-@", EngineCommand.Extract(EngineKind.SevenZip, SevenZip, Archive, OutDir, "秘密"));
    }

    [Fact, Trait("Bug", "B-2026-217")]
    public void TestUnRar_MustMatchTheContract()
    {
        Assert.Equal(
            new[] { UnRar, "t", "-y", "-idc", "-@", "-p秘密", "--", Archive, "内层/文件.bin" },
            EngineCommand.Test(EngineKind.UnRar, UnRar, Archive, "秘密", "内层/文件.bin"));
    }

    [Fact]
    public void List_MustBeMachineReadableAndNeverTryPasswords()
    {
        Assert.Equal(
            new[] { SevenZip, "l", "-slt", "-sccUTF-8", "-p-", "--", Archive },
            EngineCommand.List(SevenZip, Archive));
    }

    [Fact]
    public void Password_MustBeOneArgumentAndEmptyMeansBareSwitch()
    {
        Assert.Equal(new[] { "-p-" }, EngineCommand.Password(null));
        Assert.Equal(new[] { "-p" }, EngineCommand.Password(string.Empty));
        Assert.Equal(new[] { "-pa b\"c&d" }, EngineCommand.Password("a b\"c&d"));
        Assert.Single(EngineCommand.Password("a b"));
    }

    [Fact, Trait("Bug", "B-2026-218")]
    public void NeedsManual_MustCatchOnlyTheUnpassableCharacters()
    {
        Assert.True(EngineCommand.NeedsManual("有\"引号"));
        Assert.True(EngineCommand.NeedsManual("换\n行"));
        Assert.True(EngineCommand.NeedsManual("截\0断"));
        Assert.False(EngineCommand.NeedsManual("普通密码 带空格"));
        Assert.False(EngineCommand.NeedsManual(string.Empty));
        Assert.False(EngineCommand.NeedsManual(null));
    }

    [Fact]
    public void Display_MustNotMaskAnything()
    {
        var command = new List<string> { SevenZip, "x", "-p秘密", "--", Archive };
        Assert.Equal(SevenZip + " x -p秘密 -- " + Archive, EngineCommand.Display(command));
    }
}
