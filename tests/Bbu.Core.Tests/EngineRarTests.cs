using System;
using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// rar 那一路：2026-10-07 起走随包的 <c>UnRAR.exe</c>，所以整类进**默认档** ——
/// 引擎在任何机器上都在场（判据 1：目标机没装 WinRAR 时 rar / rar5 全解），
/// 不再需要开发机先装一份 WinRAR 才跑得动；只有那条要看本地有没有 listfile 的对照进边界档。
///
/// 它验的是 7-Zip 的表里**没有**的那个码：rar 的密码错误是 11，有时表现为 3。
/// 只看 7z 的表或只看「非零即失败」都验不到这一格。
/// </summary>
[Collection("engine")]
public sealed class EngineRarTests
{
    private const string Fixture = "rar-encrypted.rar";

    private const string Password = "RarPw123";

    /// <summary>跑的就是仓内随包那一份，路径里不含 WinRAR：判据 1 的形态面。</summary>
    [Fact]
    public void UnRar_MustBeTheBundledCopy()
    {
        var paths = TestPaths.NewEngine().Paths;

        Assert.Equal(TestPaths.BundledUnRar, paths.UnRar);
        Assert.True(File.Exists(paths.UnRar), "随包缺 UnRAR.exe：rar / rar5 一个都解不了");
        Assert.DoesNotContain("WinRAR", paths.UnRar!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 空密码改派不许绕过「分卷非首卷」那道保险：7-Zip 打不开非首卷，改派过去等于必然失败。
    ///
    /// 撤掉修复即红：改派那一处原来是无条件的，两个动作的命令行第一个参数都会变成 `7z.exe`。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-138")]
    public async Task EmptyPassword_MustNotSwitchAwayFromUnRarOnLaterVolumes()
    {
        var engine = TestPaths.NewEngine();
        var later = TestPaths.Probe(Path.Combine("vol-part", "x.part3.rar"));
        using var dir = new TestDirectory();

        var extracted = await engine.ExtractAsync(later, dir.Path, string.Empty);
        Assert.Equal(TestPaths.BundledUnRar, extracted.Command[0]);

        var tested = await engine.TestAsync(later, string.Empty);
        Assert.Equal(TestPaths.BundledUnRar, tested.Command[0]);

        // 对照：首卷照旧改派 7-Zip（空密码走它，因为 UnRAR 的裸 `-p` 会去读 stdin）
        var first = await engine.TestAsync(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")), string.Empty);
        Assert.Equal(TestPaths.BundledSevenZip, first.Command[0]);
    }

    /// <summary>错密码必须落成 11 —— 那是 rar 专用的语义，不是别的失败。</summary>
    [Fact]
    public async Task WrongPassword_MustReportRarCodeEleven()
    {
        var engine = TestPaths.NewRarEngine();

        var result = await engine.TestAsync(
            TestPaths.Fixture(Fixture), "not-the-password", EngineKind.UnRar);

        Assert.Equal(EngineExitCodes.RarWrongPasswordCode, result.Code);
        Assert.Equal(11, result.Code);
        Assert.False(result.Ok);
        Assert.Equal(Errors.ExitPassword, EngineExitCodes.Describe(result.Code, EngineKind.UnRar));

        // UnRAR 的措辞是 `Incorrect password for …`，属强证据，可单独定罪。
        Assert.True(EngineOutput.LooksLikeWrongPassword(result.Output), result.Output);

        // 走的必须是随包 UnRAR，不是被悄悄改派到 7-Zip：改派了就验不到这一个码。
        Assert.Equal(TestPaths.BundledUnRar, result.Command[0]);
    }

    /// <summary>对密码必须过，而且真解得出来（上面那条要是引擎没能启动也会「红得对」）。</summary>
    [Fact]
    public async Task RightPassword_MustPassAndExtract()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewRarEngine();
        var archive = TestPaths.Fixture(Fixture);

        var test = await engine.TestAsync(archive, Password, EngineKind.UnRar);
        Assert.Equal(0, test.Code);
        Assert.True(test.Ok, test.Output);

        var extract = await engine.ExtractAsync(
            archive, output.Path, Password, new ExtractOptions { Kind = EngineKind.UnRar });

        Assert.True(extract.Ok, extract.Output);
        Assert.True(File.Exists(Path.Combine(output.Path, "rar-payload.txt")));
    }

    /// <summary>
    /// 判据 1 的另一半：RAR5 也解得开。两条走同一个引擎，但头格式不同（老式块链 vs RAR5），
    /// 所以分开钉；夹具体在 vault 那一组。
    /// </summary>
    [Fact]
    public async Task Rar5_MustAlsoExtractOnTheBundledEngine()
    {
        var archive = Path.Combine(TestPaths.Fixtures, "vault", "rar5-p.rar");
        Assert.True(File.Exists(archive), "缺 RAR5 夹具：" + archive);

        using var output = new TestDirectory();
        var engine = TestPaths.NewRarEngine();

        var result = await engine.ExtractAsync(
            archive, output.Path, "BbuPw2026", new ExtractOptions { Kind = EngineKind.UnRar });

        Assert.True(result.Ok, result.Output);
        Assert.NotEmpty(Directory.GetFiles(output.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 不给密码时不许停在交互式提示上：裸 <c>-p</c> 在 UnRAR 里是「请提示我输入密码」，
    /// 所以命令行里必须显式带 <c>-p-</c>，这条断言盯的是命令行而不是引擎输出。
    /// </summary>
    [Fact]
    public void TestCommand_MustCarryTheExplicitNoPasswordSwitch()
    {
        var command = EngineCommand.Test(
            EngineKind.UnRar, TestPaths.BundledUnRar, TestPaths.Fixture(Fixture), password: null);

        Assert.Contains(EngineCommand.NoPasswordArgument, command);
        Assert.Contains("-idc", command);
        Assert.DoesNotContain("-bso0", command);
    }

    /// <summary>
    /// 条目名以 `@` 开头时 UnRAR 会把它当 listfile 去读本地文件，而它的 `--` 管不住这一条；
    /// `-@` 关掉这条解析。真机对照（2026-10-13，随包 UnRAR 7.30 beta 1）：不带 `-@` 退出 6
    /// —— `Program aborted` 加 `Cannot open no-such-listfile.txt`；带上退出 10 —— `No files to extract`。
    /// 撤掉 `-@` 这条即红（B-2026-217）。
    /// </summary>
    [Fact, Trait("Category", "edge"), Trait("Bug", "B-2026-217")]
    public async Task EntryStartingWithAtSign_MustNotBeReadAsAListFile()
    {
        var engine = TestPaths.NewRarEngine();

        var result = await engine.TestAsync(
            TestPaths.Fixture(Fixture), Password, EngineKind.UnRar, "@no-such-listfile.txt");

        Assert.Contains("-@", result.Command);
        Assert.Equal(10, result.Code);
    }

    /// <summary>
    /// 兜底那一档的第二次问询：UnRAR 读不读得动这个文件。
    ///
    /// 判据取**标准输出里的条目行数**：文件不是 RAR 时 <c>lb</c> 的**退出码仍然是 0、两条流都空**
    /// （2026-10-08 实测五例：zip、`.rar` 名的 zip、垃圾 `.rar`、0 字节、截断包），所以只能按条目行数判；
    /// 也不能拿特征词表去筛标准输出 —— 那上面是裸条目名。三条一起钉：真 RAR 认得出、非 RAR 认不出、
    /// 文件名加密的包（<c>-hp</c>）也认不出（让位给切包那条路；那一档退出码非零，RAR5 是 11、RAR4 是 3）。
    /// </summary>
    [Fact]
    public async Task CanUnRarRead_MustJudgeByListedEntriesNotByExitCode()
    {
        var engine = TestPaths.NewRarEngine();

        Assert.True(await engine.CanUnRarReadAsync(Path.Combine(TestPaths.Fixtures, "vault", "rar4-p.rar")));
        Assert.True(await engine.CanUnRarReadAsync(Path.Combine(TestPaths.Fixtures, "vault", "rar5-p.rar")));

        // 非 RAR：退出码 0，但一行条目都列不出来。写「按退出码判」的实现这里会红。
        Assert.False(await engine.CanUnRarReadAsync(TestPaths.Fixture("ok-small.zip")));

        // 同一个陷阱的另一种形状：**扩展名是 .rar 而内容不是 RAR** —— 引擎按内容判，
        // 按扩展名猜的实现会在这里红（就地造，不进夹具）。
        using var dir = new TestDirectory();
        var fake = dir.Combine("not-really.rar");
        File.Copy(TestPaths.Fixture("ok-small.zip"), fake, overwrite: true);
        Assert.False(await engine.CanUnRarReadAsync(fake));

        // 文件名也加密：不给密码就列不出来 → 不算「读得动」，那一档交给切包。
        Assert.False(await engine.CanUnRarReadAsync(Path.Combine(TestPaths.Fixtures, "vault", "rar5-hp.rar")));
    }
}
