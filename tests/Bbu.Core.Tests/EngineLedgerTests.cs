using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 档：100 条真实受损场景里，落在引擎这一层的那些。
///
/// 每条方法挂着自己的 号，判据照抄 的 <c>assert_</c>。进这一档的都是中低严重度：
/// 破了它们用户会受损，但不是「机器被写满」那个量级 —— 那些在默认档。
/// </summary>
[Trait("Category", "ledger")]
[Collection("engine")]
public sealed class EngineLedgerTests
{
    private const long Giga = 1024L * 1024 * 1024;

    /// <summary>
    /// B-2026-008：损坏输入必须全部明确失败，不许错报成功。
    /// 一条判成成功，用户就会以为解完了。
    /// </summary>
    [Theory]
    [InlineData("error-empty.zip")]
    [InlineData("error-random.zip")]
    [InlineData("error-text.zip")]
    [InlineData("error-truncated.zip")]
    [InlineData("error-onlyheader.zip")]
    [InlineData("error-wrongsig.zip")]
    [InlineData("error-badcentral.zip")]
    [InlineData("error-crc.zip")]
    [Trait("Bug", "B-2026-008")]
    public async Task CorruptInput_MustFailLoudly(string name)
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(
            TestPaths.Fixture(name),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip });

        Assert.False(result.Ok, name + " 被判成了成功：" + result.Output);
        Assert.False(result.Cancelled);
        Assert.False(result.SpaceGuardHit);
        Assert.NotEqual(0, result.Code);

        // 这里不断言产物目录为空：损坏包常常已经写出半成品才报错。
        // 要的是「不许错报成功」，不是「一个字节都不许落盘」。
    }

    /// <summary>
    /// B-2026-008 的两条反例：后缀错但内容完好、完好包，都必须成功。
    /// 没有这一组，一个「一律判失败」的实现也能把上面 8 条全过掉。
    /// </summary>
    [Theory]
    [InlineData("ok-small.zip")]
    [InlineData("ok-renamed.mp4")]
    [Trait("Bug", "B-2026-008")]
    public async Task IntactInput_MustSucceed(string name)
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(
            TestPaths.Fixture(name),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip });

        Assert.True(result.Ok, name + " 被判成了失败：" + result.Output);
        Assert.NotEmpty(Directory.GetFiles(output.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// B-2026-018：未加密但损坏的包被误诊成「密码错误」，界面上还会弹密码框问用户要一个
    /// 根本不存在的密码，真正的原因（包损坏）被盖住。
    ///
    /// 成因是「CRC / 校验和」这两个词对「加密包加错密码」与「未加密包加损坏」同样成立。
    /// 所以弱证据必须再确认这个包真的加密了才算数。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-018")]
    public async Task UnencryptedCorruptPack_MustNotBeBlamedOnThePassword()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("error-crc.zip"),
            output.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip });

        Assert.False(result.Ok);
        Assert.False(result.WrongPassword);
        Assert.Contains("数据损坏，不是密码问题", result.Output);
        Assert.Contains("该压缩包未加密", result.Output);
    }

    /// <summary>
    /// B-2026-018 的另一档：`checksum error`。它与 `crc failed` 一样对「加密包加错密码」与
    /// 「未加密包的数据损坏」两种情形同时成立，单独出现不足以判成密码问题 —— 必须回查这个包加没加密。
    /// 夹具是仓内那份数据区被翻转的 rar（随包 `UnRAR.exe` 对它报 `checksum error`、退出码 3）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-018")]
    public async Task UnencryptedChecksumErrorPack_MustNotBeBlamedOnThePassword()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(TestPaths.Fixture("error-checksum.rar"), output.Path);

        Assert.False(result.Ok);
        Assert.False(result.WrongPassword);
        Assert.Contains("数据损坏，不是密码问题", result.Output);
        Assert.Contains("该压缩包未加密", result.Output);
    }

    /// <summary>B-2026-018 的对照：真加密的包加错密码时，仍旧要如实说是密码问题。</summary>
    [Fact]
    [Trait("Bug", "B-2026-018")]
    public async Task EncryptedPackWithWrongPassword_MustStillBeBlamedOnThePassword()
    {
        using var output = new TestDirectory();
        var engine = TestPaths.NewEngine();

        var result = await engine.ExtractAsync(
            TestPaths.Fixture("chinese-encrypted.7z"),
            output.Path,
            password: "错的密码",
            new ExtractOptions { Kind = EngineKind.SevenZip });

        Assert.False(result.Ok);
        Assert.True(result.WrongPassword);
        Assert.DoesNotContain("不是密码问题", result.Output);
    }

    /// <summary>
    /// B-2026-042：同一条下限之下，包越大越先被拒。
    /// 判据是「解完还要剩够下限」，所以看的是余量减掉**这个包声明的体积**。
    ///
    /// 余量取下限加 1MB：单独看下限是够的，不够的只有声明体积超过 1MB 的那个包。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-042")]
    public async Task SameFloor_MustRefuseTheBigPackAndLetTheSmallOneThrough()
    {
        using var bigOutput = new TestDirectory();
        using var smallOutput = new TestDirectory();
        var engine = TestPaths.NewEngine(freeBytesProbe: _ => Giga + (1024 * 1024));

        var big = await engine.ExtractAsync(
            TestPaths.Fixture("declared-big.zip"),
            bigOutput.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip, MinFreeBytes = Giga });

        var small = await engine.ExtractAsync(
            TestPaths.Fixture("ok-small.zip"),
            smallOutput.Path,
            password: null,
            new ExtractOptions { Kind = EngineKind.SevenZip, MinFreeBytes = Giga });

        Assert.True(big.OutOfSpace);
        Assert.False(big.Ok);
        Assert.True(small.Ok, small.Output);
        Assert.NotEmpty(Directory.GetFiles(smallOutput.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// B-2026-069：退出码语义不许只留在注释里，要逐码有断言，并用真夹具钉住 0 与 2。
    /// 那一组「表里每个码都有非空语义」在默认档，这里补的是真引擎真跑出来的码。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-069")]
    public async Task RealFixtures_MustReturnTheDocumentedCodes()
    {
        var engine = TestPaths.NewEngine();

        var ok = await engine.TestAsync(
            TestPaths.Fixture("ok-small.zip"), password: null, kind: EngineKind.SevenZip);
        Assert.Equal(0, ok.Code);
        Assert.True(ok.Ok);

        var wrong = await engine.TestAsync(
            TestPaths.Fixture("chinese-encrypted.7z"), password: "错的密码", kind: EngineKind.SevenZip);
        Assert.Equal(2, wrong.Code);
        Assert.False(wrong.Ok);
        Assert.True(wrong.WrongPassword);

        var notArchive = await engine.TestAsync(
            TestPaths.Fixture("error-text.zip"), password: null, kind: EngineKind.SevenZip);
        Assert.Equal(2, notArchive.Code);
        Assert.False(notArchive.Ok);
        Assert.False(notArchive.WrongPassword);
    }
}
