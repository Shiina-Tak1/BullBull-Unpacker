using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Services.Managed7z;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 快路判据。核心性质只有一条：**绝不误杀正确密码**。
///
/// 这一档只做不花钱的那一半：正确密码不许被划掉、认不出的格式必须回「不知道」、
/// 六种失败模式全回退。与真引擎逐条对照那一半在 （边界档）。
/// </summary>
public sealed class FastPathTests
{
    private const string Password = "BbuPw2026";

    /// <summary>
    /// 非 ASCII 密码那一档，与 `chinese-encrypted.7z` 用同一个密码。
    ///
    /// 单独一个常量、单独几张夹具，是因为建包与判据是**两条编码路径**：建包时按一种编码写进去、
    /// 判据按另一种读出候选，两边不一致就会把**正确**的密码判成"肯定不对"。
    /// 夹具由 Info-ZIP（zip）与 `Rar.exe`（rar5）现造。
    /// </summary>
    private const string NonAsciiPassword = "密码123";

    private static readonly IFastPath FastPath = new CompositeFastPath();

    /// <summary>快路覆盖的格：四种加密形态、两种 7z 头形态，加三种分卷。</summary>
    public static TheoryData<string> Covered => new(
        "zip-aes256.zip",
        "zip-zipcrypto.zip",
        "rar5-hp.rar",
        "rar5-p.rar",
        "seven-mhe.7z",
        "seven-plain.7z",
        Path.Combine("vol-rar-part", "x.part1.rar"),
        Path.Combine("vol-zip-num", "x.001"),
        Path.Combine("vol-seven-num", "x.7z.001"),
        Path.Combine("vol-seven-plain", "x.7z.001"));

    /// <summary>引擎读得了、但快路故意不判的形态。</summary>
    public static TheoryData<string> Uncovered => new(
        "plain.zip",
        "plain.7z",
        "plain.rar",
        "plain.tar",
        "plain.tar.gz",
        "plain.lz4",
        "carrier-tail.mp4",
        "embedded-rar5.lz4");

    /// <summary>
    /// 非 ASCII 密码的真夹具：包就是用那句中文密码建的，不是拿 ASCII 包配中文候选。
    ///
    /// 既有的 `ZipCryptoWithNonAsciiPassword_...` 判的是**判据本身**（候选非 ASCII 就回「不知道」），
    /// 那是拿 ASCII 建的包配中文候选 —— 它测不到"编码对得上时，正确的中文密码真解得开"。
    /// </summary>
    public static TheoryData<string> NonAscii => new(
        "zip-cn-pw.zip",
        "rar5-cn-pw.rar");

    /// <summary>
    /// AES 加密 + 非 ASCII 密码。与上面那组的区别是**加密算法**：AES 与 ZipCrypto 走两条
    /// 完全不同的校验值路径。中文密码在 AES 上多一层"密码按 UTF-8 转字节"的规范背书，
    /// 而**那层背书在 WinZip 造的包上不成立**（它按本地代码页转），见本文件那条
    /// `ZipCryptoWithNonAsciiPassword_...` 里被推翻的前提。
    /// 这一格只有 WinZip 命令行的 `-ycAES256` 造得出来，Info-ZIP 只出 ZipCrypto。
    /// 它自带的密码下限是 8 个字符，所以这里用的不是上面那句 5 个字符的 `NonAsciiPassword`。
    /// </summary>
    public const string NonAsciiAesPassword = "中文密码测试甲乙丙";

    public static TheoryData<string> NonAsciiAes => new("zip-cn-aes.zip");

    private static string Vault(string name) => Path.Combine(TestPaths.Fixtures, "vault", name);

    /// <summary>开一次庭、判一个密码。判不了（开庭回 <c>null</c>）就等于「不知道」。</summary>
    private static FastPathVerdict Verdict(string name, string password, string? dll = null) =>
        VerdictAt(Vault(name), password, dll);

    /// <summary>同上，直接给路径：改过字节的夹具住在临时目录里。</summary>
    private static FastPathVerdict VerdictAt(string path, string password, string? dll = null)
    {
        using var session = new CompositeFastPath(dll).Open(path);
        return session is null ? FastPathVerdict.Unknown : session.Judge(password);
    }

    [Theory, MemberData(nameof(Covered))]
    public void CorrectPassword_MustNeverBeRejected(string name)
    {
        Assert.Equal(FastPathVerdict.Unknown, Verdict(name, Password));
    }

    /// <summary>
    /// 非 ASCII 密码那一档：正确的中文密码不许被判死。
    ///
    /// 这一档原来 zip 与 rar5 两样都没有夹具 —— 理由是"`7z a -tzip` 传中文密码报「参数错误」"
    /// 与"rar 会同时踩命令行编码与建包两条坑"。两条现在都不
    /// 成立了：zip 交给 Info-ZIP 的 `zip.exe`、rar5 交给 `Rar.exe`。
    ///
    /// `Assert.NotNull(session)` 不是装饰：`Verdict()` 在快路拒不开庭时也返回 <c>Unknown</c>，
    /// 不钉住"庭真的开了"，下面那句对不覆盖的格式就是一条恒真的空断言。
    /// </summary>
    [Theory, MemberData(nameof(NonAscii))]
    public void NonAsciiCorrectPassword_MustNeverBeRejected(string name)
    {
        using var session = FastPath.Open(Vault(name));

        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(NonAsciiPassword));
    }

    /// <summary>AES 那一格：中文密码在 AES 的校验值路径上同样不许被误杀。</summary>
    [Theory, MemberData(nameof(NonAsciiAes))]
    [Trait("Bug", "B-2026-105")]
    public void NonAsciiAesCorrectPassword_MustNeverBeRejected(string name)
    {
        using var session = FastPath.Open(Vault(name));

        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(NonAsciiAesPassword));
    }

    /// <summary>
    /// 大载荷多卷那一档：既有的两组都是 8KB 切 5 卷，「跨卷拼数据」的代价在那个尺寸上看不出来。
    /// 这一组是 3MB 切 512KB 出 7 卷（见 `VAULT_BIG_PAYLOAD`）。
    /// </summary>
    [Fact]
    public void LargeVolumeSet_CorrectPasswordMustNotBeRejected()
    {
        Assert.Equal(
            FastPathVerdict.Unknown,
            Verdict(Path.Combine("vol-seven-large", "x.7z.001"), Password));
    }

    [Theory, MemberData(nameof(Covered))]
    public void WrongPasswords_MustBeRejectedForAlmostEveryCandidate(string name)
    {
        using var session = FastPath.Open(Vault(name));
        Assert.NotNull(session);

        var rejected = Enumerable.Range(0, 50)
            .Count(i => session!.Judge($"wrong-{i:000}") is FastPathVerdict.ProbablyWrong);

        // ZipCrypto 的校验值只有一字节，假阳性 1/256 是格式自带的；其余的应当一条不漏。
        Assert.True(rejected >= 45, $"{name} 只划掉 {rejected}/50");
    }

    [Theory, MemberData(nameof(Uncovered))]
    public void UncoveredFormats_MustAlwaysReturnUnknown(string name)
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(FastPathVerdict.Unknown, Verdict(name, $"wrong-{i}"));
        }
    }

    [Fact]
    public void EmptyPasswordAndMissingFile_MustReturnUnknown()
    {
        Assert.Equal(FastPathVerdict.Unknown, Verdict("zip-aes256.zip", string.Empty));
        Assert.Null(FastPath.Open(Path.Combine(TestPaths.Fixtures, "there-is-no-such-file.zip")));
    }

    /// <summary>
    /// 元数据只解析一次（判据 2）。证据是**开庭之后把文件移走**：判定阶段若还要碰盘，
    /// 它就只能回「不知道」或者抛异常；结论照旧说明这一层不再读盘。
    /// 7z 那一条不适用这条证据（它的判据本来就要读一点数据），另见下面那条计数器断言。
    /// </summary>
    [Theory]
    [InlineData("zip-aes256.zip")]
    [InlineData("rar5-hp.rar")]
    public void Metadata_MustBeParsedOnlyOncePerArchive(string name)
    {
        using var dir = new TestDirectory();
        var copy = dir.Combine("moved.zip");
        File.Copy(Vault(name), copy);
        using var session = FastPath.Open(copy);
        Assert.NotNull(session);

        var first = session!.Judge("definitely-not-it");
        Assert.Equal(FastPathVerdict.ProbablyWrong, first);
        File.Delete(copy);

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(first, session.Judge($"wrong-{i:000}"));
        }
    }

    /// <summary>
    /// 7z 那一格：元数据是「最小的加密条目」，它跟密码无关，一个包只该扫一次。
    /// 明文头那条路的判据本身要解一点数据，所以「不再碰盘」不适用于它，判据换成扫描计数。
    /// </summary>
    [Fact]
    public void SevenZipMetadata_MustBeScannedOnlyOncePerArchive()
    {
        using var session = SevenZipFastPathSession.Open(Vault("seven-plain.7z"));
        Assert.NotNull(session);

        for (var i = 0; i < 50; i++)
        {
            session!.Judge($"wrong-{i:000}");
        }

        Assert.Equal(1, session!.MetadataScans);
    }

    /// <summary>保留设备名挡在一切读操作之前：真去读会永久阻塞，取消与超时都没有机会执行。</summary>
    [Fact, Trait("Bug", "B-2026-001")]
    public void ReservedDevicePath_MustNotEnterTheFastPath()
    {
        using var dir = new TestDirectory();
        var device = dir.Combine("CON");

        Assert.Null(FastPath.Open(device));
        Assert.Null(FastPath.Open(dir.Combine("NUL.txt")));
    }

    /// <summary>
    /// 同一个会话复用给几百个候选：失败状态若留在常驻对象里，后续每个正确密码都会被误回「不知道」。
    /// 所以「错 错 对 错 对 错」里每一次「对」都要被认出来。
    /// </summary>
    [Theory]
    [InlineData("seven-mhe.7z")]
    [InlineData("seven-plain.7z")]
    [InlineData("rar5-hp.rar")]
    [InlineData("zip-aes256.zip")]
    public void ReusedSession_MustNotLoseACorrectPasswordInTheMiddle(string name)
    {
        using var session = FastPath.Open(Vault(name));
        Assert.NotNull(session);

        var wrong = new[] { "aaa-1", "aaa-2", "aaa-4", "aaa-6" };
        var at = 0;
        foreach (var step in new[] { "错", "错", "对", "错", "对", "错" })
        {
            if (step == "对")
            {
                Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
                continue;
            }

            Assert.Equal(FastPathVerdict.ProbablyWrong, session!.Judge(wrong[at++]));
        }
    }

    [Fact, Trait("Bug", "B-2026-053")]
    public void Rar5PasswordLongerThanOneHundredAndTwentySevenUnits_MustBeHandedBackToTheEngine()
    {
        // B-2026-053：建包端（Rar.exe）与 7-Zip 读包端都把密码截到 127 个 UTF-16 码元，
        // 拿完整密码算必然对不上 —— 那不是「密码不对」，是把判定交回引擎。
        Assert.Equal(FastPathVerdict.Unknown, Verdict("rar5-hp.rar", new string('Z', 128)));

        // 64 个 emoji 是 64 个码点、128 个 UTF-16 码元：判据单位必须是码元。
        Assert.Equal(
            FastPathVerdict.Unknown, Verdict("rar5-p.rar", string.Concat(Enumerable.Repeat("🔒", 64))));

        // 对照：同样长度的错密码在 zip 那一档照旧被划掉，说明上面两条不是「整条快路都不判了」。
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("zip-aes256.zip", new string('Z', 128)));
    }

    [Fact]
    public void ZipCryptoWithNonAsciiPassword_MustBeHandedBackToTheEngine()
    {
        // 老式 ZipCrypto 没有规范说密码按哪种编码转字节：编码假设错了会把正确密码划掉。
        // 所以非 ASCII 的候选一律回「不知道」，代价是那几条候选走引擎。
        Assert.Equal(FastPathVerdict.Unknown, Verdict("zip-zipcrypto.zip", "中文密码"));
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("zip-zipcrypto.zip", "ascii-wrong"));

        // AES 那一档原来靠"规范背书（密码按 UTF-8）"判得了，**这条前提已被实测推翻**：
        // WinZip 的 `-ycAES256` 造出来的包要按本地代码页（GBK）复算才对得上
        
        // 于是它与 ZipCrypto 同一处置：非 ASCII 一律回「不知道」，不再判「肯定不对」。
        Assert.Equal(FastPathVerdict.Unknown, Verdict("zip-aes256.zip", "中文密码"));

        // ASCII 那一档照旧判得了，说明上面几条不是"整条 AES 快路都不判了"。
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("zip-aes256.zip", "ascii-wrong"));
    }

    [Fact, Trait("Bug", "B-2026-093")]
    public void SplitZipVolume_MustBeJudgedFromItsFirstLocalHeader()
    {
        // 分卷 zip 的中央目录在最后一卷，1.1.0 只能回退；首卷开头的本地头是完整的，所以这里判得了。
        Assert.Equal(FastPathVerdict.Unknown, Verdict("vol-zip-num/x.001", Password));
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("vol-zip-num/x.001", "definitely-not-it"));
    }

    /// <summary>7z 的分卷：各卷拼成一条虚拟流交给 handler，所以只传入首卷也判得了。</summary>
    [Theory]
    [InlineData("vol-seven-num/x.7z.001")]
    [InlineData("vol-seven-plain/x.7z.001")]
    public void SevenZipSplitVolume_MustBeJudgedFromItsVolumes(string first)
    {
        Assert.Equal(FastPathVerdict.Unknown, Verdict(first, Password));
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict(first, "definitely-not-it"));
    }

    /// <summary>
    /// 被传入非首卷时的两层分工：**分派层**按文件头走，非首卷没有魔数，回「不知道」
    /// （产品在进快路之前已用探针纠正到主卷）；**会话层**拿到非首卷要能自己找回头。
    /// </summary>
    [Fact]
    public void SevenZipNonFirstVolume_MustBeCorrectedToTheFirstOne()
    {
        Assert.Equal(FastPathVerdict.Unknown, Verdict("vol-seven-num/x.7z.003", "definitely-not-it"));

        using var session = SevenZipFastPathSession.Open(
            Path.Combine(TestPaths.Fixtures, "vault", "vol-seven-num", "x.7z.003"));
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
        Assert.Equal(FastPathVerdict.ProbablyWrong, session.Judge("definitely-not-it"));
    }

    /// <summary>缺卷时停在缺口：不回「肯定不对」，也不许猜。</summary>
    [Fact]
    public void SevenZipMissingVolume_MustFallBackInsteadOfGuessing()
    {
        using var dir = new TestDirectory();
        var group = Directory.CreateDirectory(dir.Combine("vol")).FullName;
        var parts = Directory.GetFiles(Path.Combine(TestPaths.Fixtures, "vault", "vol-seven-num"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(parts.Length >= 3, "7z 分卷夹具至少要有三卷才谈得上缺卷");
        foreach (var part in parts)
        {
            File.Copy(part, Path.Combine(group, Path.GetFileName(part)));
        }

        File.Delete(Path.Combine(group, Path.GetFileName(parts[1])));
        using var session = FastPath.Open(Path.Combine(group, "x.7z.001"));
        if (session is not null)
        {
            Assert.Equal(FastPathVerdict.Unknown, session.Judge(Password));
            Assert.Equal(FastPathVerdict.Unknown, session.Judge("definitely-not-it"));
        }
    }

    /// <summary>
    /// 分卷 7z 打开会话拿到的是**托管**那一段。落地前这一格量的是 dll 的结论，所以它同时是覆盖面的对照。
    /// </summary>
    [Theory]
    [InlineData("vol-seven-num/x.7z.001")]
    [InlineData("vol-seven-plain/x.7z.001")]
    [InlineData("vol-seven-large/x.7z.001")]
    public void SplitVolume_MustBeJudgedOnTheManagedPath(string first)
    {
        using var session = FastPath.Open(Vault(first));

        Assert.IsType<ManagedSevenZipFastPathSession>(session);
    }

    /// <summary>
    /// 两条路对同一份分卷夹具要给同一结论：托管那条先打开会话之后，dll 那条的判据靠这一条并排比才看住。
    /// </summary>
    [Theory]
    [InlineData("vol-seven-num/x.7z.001")]
    [InlineData("vol-seven-plain/x.7z.001")]
    [InlineData("vol-seven-large/x.7z.001")]
    public void SplitVolumeVerdicts_MustMatchTheDllPath(string first)
    {
        Assert.Equal(FastPathVerdict.Unknown, DllVerdict(first, Password));
        Assert.Equal(DllVerdict(first, Password), Verdict(first, Password));
        Assert.Equal(DllVerdict(first, "definitely-not-it"), Verdict(first, "definitely-not-it"));
    }

    /// <summary>
    /// 多卷源自己那一层：一条读跨卷边界要读完，边界两侧各取一字节不能错位，越界回 0 而不是抛。
    /// </summary>
    [Fact]
    public void MultiVolumeSource_ReadAtMustSpanVolumeBoundaries()
    {
        var volumes = Directory.GetFiles(Path.Combine(TestPaths.Fixtures, "vault", "vol-seven-large"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var joined = volumes.SelectMany(File.ReadAllBytes).ToArray();

        using var source = SevenZipMultiVolumeSource.Open(volumes);
        Assert.Equal(joined.Length, source.Length);

        // 从第 4 卷中段一直读到最后三字节之前：一条读要跨 3 个卷边界。
        var start = (joined.Length / 2) - 7;
        var target = new byte[joined.Length - start - 3];
        Assert.Equal(target.Length, source.ReadAt(start, target));
        Assert.Equal(joined[start..(start + target.Length)], target);

        foreach (var at in new[] { 0L, 524287L, 524288L, joined.Length - 1 })
        {
            var one = new byte[1];
            Assert.Equal(1, source.ReadAt(at, one));
            Assert.Equal(joined[(int)at], one[0]);
        }

        Assert.Equal(0, source.ReadAt(joined.Length, new byte[4]));
        Assert.Equal(0, source.ReadAt(-1, new byte[4]));
    }

    /// <summary>
    /// 缺口时托管那一段**交回**，不拿短流去判。删第 2 卷时卷表只剩首卷、落回单文件源那条；
    /// 删第 4 卷时卷表还有三卷、走多卷源那条 —— 两种形态各一格，后者才是新源自己的假阴性证据。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SplitVolumeWithAGap_MustHandBackInsteadOfJudging(int missing)
    {
        using var dir = new TestDirectory();
        var group = Directory.CreateDirectory(dir.Combine("vol")).FullName;
        var parts = Directory.GetFiles(Path.Combine(TestPaths.Fixtures, "vault", "vol-seven-large"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        foreach (var part in parts)
        {
            File.Copy(part, Path.Combine(group, Path.GetFileName(part)));
        }

        var main = Path.Combine(group, "x.7z.001");
        using (var complete = ManagedSevenZipFastPathSession.Open(main))
        {
            Assert.NotNull(complete);
        }

        File.Delete(Path.Combine(group, Path.GetFileName(parts[missing])));

        using var gapped = ManagedSevenZipFastPathSession.Open(main);
        Assert.Null(gapped);
    }

    /// <summary>
    /// 「托管认不了就交回 dll」这条覆盖面不退：链尾是 ZSTD 与 LZ4 的两个形态都不许被托管收下。
    /// </summary>
    [Theory]
    [InlineData("seven-zstd-plain.7z")]
    [InlineData("seven-lz4-plain.7z")]
    public void ManagedPath_MustStillHandBackWhatItCannotJudge(string name)
    {
        using var session = FastPath.Open(Vault(name));

        Assert.NotNull(session);
        Assert.IsNotType<ManagedSevenZipFastPathSession>(session);
    }

    /// <summary>
    /// 非首卷那一格：分派层按魔数走，非首卷是裸切片 ⇒ 整轮不进快路；会话层被直接传入时要自己找回头。
    /// </summary>
    [Fact]
    public void SevenZipNonFirstVolume_MustStayNullAtTheDispatchLayer()
    {
        Assert.Null(FastPath.Open(Vault("vol-seven-num/x.7z.003")));

        using var session = ManagedSevenZipFastPathSession.Open(Vault("vol-seven-num/x.7z.003"));
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
    }

    /// <summary>
    /// 六种失败模式全回「不知道」且不抛（判据 7）。资源类失败（dll 缺失、加载不了）
    /// 与运行期异常分开：前者不计数，把 dll 放回来立刻复活；后者连续三次才关掉这一段。
    /// </summary>
    [Fact]
    public void SevenZipFailureModes_MustAllFallBackWithoutThrowing()
    {
        using var dir = new TestDirectory();
        var missing = dir.Combine("no-such-7z.dll");
        var notPe = dir.Combine("not-a-dll.dll");
        File.WriteAllText(notPe, "this is not a portable executable");

        // ① dll 缺失 ② 初始化失败（不是 PE）。这两条判的是 **dll 那一段**的资源类失败：
        // 托管那一段不吃 dll，所以这里直接调它 —— 经 CompositeFastPath 会先走托管，
        // 而托管照样判得了这两张夹具，就验不到「dll 缺失回不知道」这件事了。
        Assert.Null(SevenZipFastPathSession.Open(Vault("seven-mhe.7z"), missing));
        Assert.Null(SevenZipFastPathSession.Open(Vault("seven-plain.7z"), notPe));

        // ③ 不是 7z：zip 夹具直接交给 7z 那一段。
        using (var wrongKind = SevenZipFastPathSession.Open(Vault("zip-aes256.zip")))
        {
            if (wrongKind is not null)
            {
                Assert.Equal(FastPathVerdict.Unknown, wrongKind.Judge("definitely-not-it"));
            }
        }

        // ④ 魔数对但内容是垃圾 ⑤ 文件不存在。
        var garbage = dir.Combine("garbage.7z");
        File.WriteAllBytes(garbage, [.. new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }, .. new byte[4096]]);
        using (var session = FastPath.Open(garbage))
        {
            if (session is not null)
            {
                Assert.Equal(FastPathVerdict.Unknown, session.Judge("definitely-not-it"));
            }
        }

        Assert.Equal(
            FastPathVerdict.Unknown, Verdict("there-is-no-such-file.7z", "definitely-not-it"));
    }

    /// <summary>连续三次异常关掉这一段；复原之后这条路照旧能工作（判据 7 的后半条）。</summary>
    [Fact]
    public void SevenZipAfterThreeFailures_MustGoQuietAndComeBackOnReset()
    {
        FastPathFailureGuard.Reset();
        try
        {
            FastPathFailureGuard.NoteFailure();
            FastPathFailureGuard.NoteFailure();
            Assert.False(FastPathFailureGuard.Disabled);

            FastPathFailureGuard.NoteFailure();
            Assert.True(FastPathFailureGuard.Disabled);
            Assert.Equal(FastPathVerdict.Unknown, DllVerdict("seven-mhe.7z", "definitely-not-it"));

            FastPathFailureGuard.Reset();
            Assert.False(FastPathFailureGuard.Disabled);
            Assert.Equal(FastPathVerdict.ProbablyWrong, DllVerdict("seven-mhe.7z", "definitely-not-it"));
            Assert.Equal(FastPathVerdict.Unknown, DllVerdict("seven-mhe.7z", Password));

            // 成功一次即清零：「连续」三次才算数，不是累计三次。
            FastPathFailureGuard.NoteFailure();
            FastPathFailureGuard.Succeeded();
            FastPathFailureGuard.NoteFailure();
            FastPathFailureGuard.NoteFailure();
            Assert.False(FastPathFailureGuard.Disabled);
        }
        finally
        {
            FastPathFailureGuard.Reset();
        }
    }

    /// <summary>
    /// 只走 dll 那一段。失败计数与「连续三次关掉」那把开关是它的状态，
    /// 而 <see cref="CompositeFastPath"/> 对 7z 现在先走托管，所以这两条要用这个入口判。
    /// </summary>
    private static FastPathVerdict DllVerdict(string name, string password)
    {
        using var session = SevenZipFastPathSession.Open(Vault(name));
        return session is null ? FastPathVerdict.Unknown : session.Judge(password);
    }

    /// <summary>
    /// 7z 那一格**先走纯托管**，dll 只当回退（dll 那条被一把
    /// 进程级静态锁串起来，托管那条没有）。把托管这一段从分派里撤掉，这条就变红。
    /// 反向对照：托管判不了的形态（链尾是 ZSTD 一类）必须照旧回退 dll，一格都不许减。
    /// </summary>
    [Fact]
    public void SevenZip_ManagedPathMustBeTakenBeforeDll()
    {
        using var managed = FastPath.Open(Vault("seven-mhe.7z"));
        Assert.IsType<ManagedSevenZipFastPathSession>(managed);

        using var fallback = FastPath.Open(Vault("seven-zstd-plain.7z"));
        Assert.NotNull(fallback);
        Assert.IsNotType<ManagedSevenZipFastPathSession>(fallback);
    }

    [Fact]
    public void BadOffsetZip_MustFallBackInsteadOfRejectingEverything()
    {
        // 中央目录里的偏移被改坏时，解出来的校验值跟谁都不匹配。判据是允许的，但绝不许
        // 因为「解出垃圾」就把正确密码也划掉 —— 那条路只能回「不知道」。
        using var dir = new TestDirectory();
        var broken = dir.Combine("bad-offset.zip");
        var bytes = File.ReadAllBytes(Vault("zip-aes256.zip"));
        var central = FindCentralDirectory(bytes);
        if (central <= 0)
        {
            return;
        }

        bytes[central] = 59;
        File.WriteAllBytes(broken, bytes);
        using var session = FastPath.Open(broken);
        if (session is not null)
        {
            Assert.Equal(FastPathVerdict.Unknown, session.Judge(Password));
        }
    }

    private static int FindCentralDirectory(byte[] bytes)
    {
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// B-2026-102：<c>Extract</c> 连结果码都拿不到时（zstd / lz4 这类 ZS 系编码会这样），
    /// 快路既不判错也不置位，于是每个候选都多付一次 <c>Open</c>+<c>Extract</c>。
    /// 连续两次拿不到结果就是包级结论，整条关断 —— 之后连 <c>Open</c> 都不该再付。
    /// </summary>
    [Theory, InlineData("seven-zstd-plain.7z"), InlineData("seven-lz4-plain.7z")]
    [Trait("Category", "ledger"), Trait("Bug", "B-2026-102")]
    public void EmptyExtractVerdict_MustShutTheWholePathDown(string name)
    {
        using var session = SevenZipFastPathSession.Open(
            Path.Combine(TestPaths.Fixtures, "vault", name));
        Assert.NotNull(session);

        Assert.False(session!.NoVerdict);
        Assert.Equal(FastPathVerdict.Unknown, session.Judge("wrong-1"));
        Assert.Equal(FastPathVerdict.Unknown, session.Judge("wrong-2"));
        Assert.True(session.NoVerdict, "两次拿不到结果码之后必须认定这个包判不了");

        // 关断之后照旧只回「不知道」，正确密码不许被划掉。
        Assert.Equal(FastPathVerdict.Unknown, session.Judge(Password));
        Assert.Equal(1, session.MetadataScans);
    }

    /// <summary>
    /// B-2026-104：卷大小是包级常量。每候选重新 <c>stat</c> 一次，
    /// 就在候选循环里破坏了「包级元数据只在开庭时解析一次」这条契约。
    /// </summary>
    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-104")]
    public void VolumeSizes_MustBeResolvedOncePerSession()
    {
        using var dir = new TestDirectory();
        var copy = dir.Combine("seven-plain.7z");
        File.Copy(Path.Combine(TestPaths.Fixtures, "vault", "seven-plain.7z"), copy);

        using var session = SevenZipFastPathSession.Open(copy);
        Assert.NotNull(session);
        session!.Judge("wrong-1");
        var before = session.StreamTotal;
        Assert.Equal(new FileInfo(copy).Length, before);

        using (var stream = new FileStream(copy, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(new byte[4096]);
        }

        session.Judge("wrong-2");
        Assert.Equal(before, session.StreamTotal);
    }

    /// <summary>改过字节的 `rar5-hp.rar` 头块坐标：hsize 与 type 各占一字节，故其后各字段偏移固定。</summary>
    private const int Rar5HpHeaderCrcAt = 8;

    private const int Rar5HpVersionAt = 15;
    private const int Rar5HpPswCheckAt = 34;
    private const int Rar5HpCsumAt = 42;
    private const int Rar5HpHeaderEnd = 46;

    /// <summary>
    /// B-2026-140：头块里的 PswCheck 被改坏时不许收点。
    ///
    /// 缺这道校验的后果不是「判不了」而是「判错」：「-hp」档案只收一个校验点，坏掉的那个
    /// 让正确密码算出的真值对不上，直接回 `ProbablyWrong`，而 `Vault` 会把这种候选连引擎
    /// 都不起就丢掉。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-140")]
    public void Rar5DamagedPswCheck_MustNotBeJudgedAsWrongPassword()
    {
        using var dir = new TestDirectory();
        var damaged = Rar5HpPatched(dir, "damaged-pswcheck.rar", bytes => bytes[Rar5HpPswCheckAt] ^= 0xff);

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, Password));
        // 敏感的是下面这一条：撤掉那道闸 `Open` 就会开庭，它立刻红。
        Assert.Null(FastPath.Open(damaged));

        // 反向对照：同一张夹具不改字节时错密码照旧判得了，说明上面两条不是「整条 RAR5 快路都不判了」。
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("rar5-hp.rar", "definitely-not-it"));
    }

    /// <summary>
    /// B-2026-140：存着的那 4 字节 CRC32 被改坏（覆盖区没动）同样不许收点 ——
    /// 这是「档案本身有一处损坏」那一档，正确密码在改之前算得出来、改之后连庭都不开。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-140")]
    public void Rar5DamagedHeaderCrc_MustNotBeJudged()
    {
        using var dir = new TestDirectory();
        var damaged = Rar5HpPatched(dir, "damaged-headercrc.rar", bytes => bytes[Rar5HpHeaderCrcAt] ^= 0x01);

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, "definitely-not-it"));
        Assert.Null(FastPath.Open(damaged));
    }

    /// <summary>
    /// B-2026-140：只把 csum 改坏（头块 CRC 重算回相符）时，靠 csum 这一道单独拦下。
    /// 这一档专测头块 CRC 之外的那道复核 —— 撤掉它这条即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-140")]
    public void Rar5DamagedPswCheckCsum_MustNotBeJudged()
    {
        using var dir = new TestDirectory();
        var damaged = Rar5HpPatched(dir, "damaged-csum.rar", bytes =>
        {
            bytes[Rar5HpCsumAt] ^= 0xff;
            RewriteRar5HeaderCrc(bytes);
        });

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, Password));
        // 敏感的是下面这一条：撤掉 csum 那道复核 `Open` 就会开庭，它立刻红。
        Assert.Null(FastPath.Open(damaged));
    }

    /// <summary>
    /// B-2026-140：加密记录的版本字段非 0（官方 `CRYPT_VERSION` 为 0）时不收点：
    /// 版本是「后面这些字段还是不是这个布局」的唯一开关，超前时照旧布局取值等于默认格式不再演进。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-140")]
    public void Rar5UnknownEncryptionVersion_MustNotBeJudged()
    {
        using var dir = new TestDirectory();
        var damaged = Rar5HpPatched(dir, "future-version.rar", bytes =>
        {
            bytes[Rar5HpVersionAt] = 1;
            RewriteRar5HeaderCrc(bytes);
        });

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, "definitely-not-it"));
        Assert.Null(FastPath.Open(damaged));
    }

    /// <summary>
    /// B-2026-142：全零的 `PswCheck` 不能当校验点用。
    ///
    /// RAR 5.21 及更早的 service 记录就写全零（官方 `arcread.cpp` 对此有明确特例），
    /// 而拿全零去比，**任何**密码都对不上 —— 正确密码会被判成「肯定不对」。
    /// 夹具把 8 字节抹成 0，并把 csum 与头块 CRC 都按新值重算，
    /// 好让前面那两道闸放行、单独验「全零不许收点」这一条：撤掉它这条就红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-142"), Trait("Category", "ledger")]
    public void Rar5ZeroPswCheck_MustNotBeUsedAsACheckPoint()
    {
        using var dir = new TestDirectory();
        var zeroed = Rar5HpPatched(dir, "zero-pswcheck.rar", bytes =>
        {
            for (var i = 0; i < 8; i++)
            {
                bytes[Rar5HpPswCheckAt + i] = 0;
            }

            var digest = System.Security.Cryptography.SHA256.HashData(
                bytes.AsSpan(Rar5HpPswCheckAt, 8));
            for (var i = 0; i < 4; i++)
            {
                bytes[Rar5HpCsumAt + i] = digest[i];
            }

            RewriteRar5HeaderCrc(bytes);
        });

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(zeroed, Password));
        Assert.Null(FastPath.Open(zeroed));
    }

    /// <summary>
    /// B-2026-143：官方把密码转字节时在 U+0000 处截断、孤立代理项编成 3 字节
    /// （`unicode.cpp` 与 `crypt5.cpp`），而 .NET 的 `Encoding.UTF8` 把它编成替换字符。
    /// 口径不同时算出的校验值必然对不上，于是正确密码会被判死 —— 这类候选一律回「不知道」。
    /// 反向对照：成对的代理项（emoji）两边口径一致，照旧判得了。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-143")]
    public void Rar5UnrepresentablePassword_MustNotBeJudged()
    {
        Assert.Equal(FastPathVerdict.Unknown, Verdict("rar5-hp.rar", "a\0b"));
        Assert.Equal(FastPathVerdict.Unknown, Verdict("rar5-hp.rar", "abc\uD800"));
        Assert.Equal(FastPathVerdict.Unknown, Verdict("rar5-hp.rar", "\uDC00abc"));
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("rar5-hp.rar", "abc\uD83D\uDE00"));
    }

    /// <summary>
    /// B-2026-141：`method == 99` 与 `0x9901` 两个条件缺一即判不了。
    ///
    /// 只看有没有 `0x9901` 时，缺那一格的 AES 包会落进 ZipCrypto 分支，拿 salt 当 12 字节
    /// 加密头去比一字节 CRC 高字节，正确密码约 255/256 被判死 —— 这是 zip 面上唯一一条
    /// 走到误杀正确密码的路。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-141")]
    public void ZipAesMethodWithoutAesExtra_MustNotFallIntoZipCrypto()
    {
        using var dir = new TestDirectory();
        var damaged = ZipAes256Patched(dir, "no-aes-extra.zip", (bytes, fields) =>
        {
            foreach (var field in fields)
            {
                bytes[field.At] = 0x02;   // 0x9901 → 0x9902：扩展区里再没有 AES 那一格
            }
        });

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, Password));
        Assert.Null(FastPath.Open(damaged));

        // 反向对照：同一张夹具不改字节时错密码照旧判得了。
        Assert.Equal(FastPathVerdict.ProbablyWrong, Verdict("zip-aes256.zip", "definitely-not-it"));
    }

    /// <summary>
    /// B-2026-141：`0x9901` 在、可强度那一格是 0（WinZip AES 只允许 1 至 3）时同样判不了。
    /// 旧实现把「强度值非法」与「没有这个字段」共用一个 0，于是这一档也掉进 ZipCrypto。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-141")]
    public void ZipAesIllegalStrength_MustNotFallIntoZipCrypto()
    {
        using var dir = new TestDirectory();
        var damaged = ZipAes256Patched(dir, "zero-strength.zip", (bytes, fields) =>
        {
            foreach (var field in fields)
            {
                bytes[field.At + 8] = 0;
            }
        });

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, Password));
        // 敏感的是下面这一条：撤掉「强度非法即判不了」`Open` 就会开庭，它立刻红。
        Assert.Null(FastPath.Open(damaged));
    }

    /// <summary>
    /// B-2026-141：`0x9901` 声明的数据长度（4 字节）容不下强度那一格时判不了。
    /// 旧实现的边界按整个缓冲区算，会读到这条扩展字段之外的那一格。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-141")]
    public void ZipAesTruncatedStrengthField_MustNotFallIntoZipCrypto()
    {
        using var dir = new TestDirectory();
        var damaged = ZipAes256Patched(dir, "short-aes-extra.zip", (bytes, fields) =>
        {
            foreach (var field in fields)
            {
                BitConverter.GetBytes((ushort)4).CopyTo(bytes, field.At + 2);
            }
        });

        Assert.Equal(FastPathVerdict.Unknown, VerdictAt(damaged, Password));
        // 敏感的是下面这一条：撤掉长度边界那处修复 `Open` 就会开庭，它立刻红。
        Assert.Null(FastPath.Open(damaged));
    }

    /// <summary>
    /// 改字节之前先把 `rar5-hp.rar` 的坐标与两道校验当场核一遍：hsize 33、type 4、版本 0，
    /// 头块 CRC 与 PswCheck 的 csum 都复算相符。夹具重造后这里先报红，
    /// 免得下面的改字节改到别的字段上还以为是判据错了。
    /// </summary>
    private static string Rar5HpPatched(TestDirectory dir, string target, Action<byte[]> patch)
    {
        var bytes = File.ReadAllBytes(Vault("rar5-hp.rar"));
        Assert.Equal((byte)33, bytes[12]);
        Assert.Equal((byte)4, bytes[13]);
        Assert.Equal((byte)0, bytes[Rar5HpVersionAt]);
        Assert.Equal(
            BitConverter.ToUInt32(bytes, Rar5HpHeaderCrcAt),
            Crc32.Compute(0xffffffff, bytes.AsSpan(12, Rar5HpHeaderEnd - 12)) ^ 0xffffffff);
        Assert.Equal(
            SHA256.HashData(bytes[Rar5HpPswCheckAt..Rar5HpCsumAt])[..4].ToArray(),
            bytes[Rar5HpCsumAt..(Rar5HpCsumAt + 4)]);

        patch(bytes);
        var copy = dir.Combine(target);
        File.WriteAllBytes(copy, bytes);
        return copy;
    }

    /// <summary>改过字节之后把头块 CRC 重算回相符，好让「只坏 csum / 只坏版本」两档单独成立。</summary>
    private static void RewriteRar5HeaderCrc(byte[] bytes)
    {
        var crc = Crc32.Compute(0xffffffff, bytes.AsSpan(12, Rar5HpHeaderEnd - 12)) ^ 0xffffffff;
        BitConverter.GetBytes(crc).CopyTo(bytes, Rar5HpHeaderCrcAt);
    }

    /// <summary>`zip-aes256.zip` 里两处 `0x9901` 的坐标：本地头扩展区一处、中央目录扩展区一处。</summary>
    private static string ZipAes256Patched(
        TestDirectory dir, string target, Action<byte[], List<(int At, int Size)>> patch)
    {
        var bytes = File.ReadAllBytes(Vault("zip-aes256.zip"));
        var fields = AesExtraFields(bytes);

        // 坐标先当场核对：method 99，两处 0x9901 的强度都是 3（256 位档）。
        Assert.Equal((ushort)99, BitConverter.ToUInt16(bytes, 8));
        Assert.Equal(2, fields.Count);
        Assert.All(fields, field => Assert.Equal((byte)3, bytes[field.At + 8]));

        patch(bytes, fields);
        var copy = dir.Combine(target);
        File.WriteAllBytes(copy, bytes);
        return copy;
    }

    private static List<(int At, int Size)> AesExtraFields(byte[] bytes)
    {
        var fields = new List<(int At, int Size)>();
        CollectAesField(bytes, 30 + BitConverter.ToUInt16(bytes, 26), BitConverter.ToUInt16(bytes, 28), fields);

        var central = FindCentralDirectory(bytes);
        Assert.True(central > 0);
        CollectAesField(
            bytes,
            central + 46 + BitConverter.ToUInt16(bytes, central + 28),
            BitConverter.ToUInt16(bytes, central + 30),
            fields);
        return fields;
    }

    private static void CollectAesField(byte[] bytes, int at, int length, List<(int At, int Size)> fields)
    {
        var end = at + length;
        while (at + 4 <= end)
        {
            var size = BitConverter.ToUInt16(bytes, at + 2);
            if (BitConverter.ToUInt16(bytes, at) == 0x9901)
            {
                fields.Add((at, size));
                return;
            }

            at += 4 + size;
        }
    }

    /// <summary>
    /// ZIP64 扩展字段的声明长度被改坏：`0x0001` 那条声称 65535 字节，而中央目录在它之前就结束了。
    /// 偏移真值要同时以**字段声明长度**与**缓冲剩余长度**为界（同 <c>TryAesStrength</c> 的写法），
    /// 只按声明长度判就会读到中央目录之外。撤掉那条界，这条断言抛 `ArgumentOutOfRangeException`。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-194")]
    public void Zip64ExtraPastTheCentralDirectory_MustFallBackInsteadOfThrowing()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("zip64-extra-too-long.zip");
        var bytes = Zip64ExtraTooLongSample(path);

        // 坐标自检：不成立时下面那句 Null 就是恒真的空断言 —— 样本没走到 ZIP64 那一支。
        Assert.Equal(0x0001, BitConverter.ToUInt16(bytes, 8));       // 加密标志
        Assert.Equal(8, BitConverter.ToUInt16(bytes, 30));           // extra 长度
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(bytes, 42)); // 本地头偏移占位符
        Assert.Equal(0x0001, BitConverter.ToUInt16(bytes, 46));      // ZIP64 字段 id
        Assert.Equal(0xFFFF, BitConverter.ToUInt16(bytes, 48));      // 声明长度（改坏的那一格）

        // 两条界各自算一遍：声明长度放行这个样本，缓冲剩余长度拦下它。
        const int fieldAt = 46;
        var q = fieldAt + 4;
        Assert.True(q + 8 <= fieldAt + 4 + 0xFFFF, "声明长度本该放行这个样本");
        Assert.True(q + 8 > 54, "真值本该落在中央目录之外");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Null(ZipFastPathSession.Open(path, stream));
    }

    /// <summary>
    /// 76 字节的最小 ZIP：一条中央目录记录（加密标志、本地头偏移是 0xFFFFFFFF、extra 里只有一条
    /// 声明 65535 字节的 0x0001 字段），后面接经典 EOCD。中央目录总共 54 字节，所以那条声明
    /// 长度一定越过它。
    /// </summary>
    private static byte[] Zip64ExtraTooLongSample(string path)
    {
        var bytes = new byte[76];
        bytes[0] = 0x50;
        bytes[1] = 0x4b;
        bytes[2] = 0x01;
        bytes[3] = 0x02;
        bytes[8] = 0x01;         // 加密（flags bit0）
        bytes[30] = 0x08;        // extra 长度 8
        for (var i = 42; i < 46; i++)
        {
            bytes[i] = 0xFF;     // 本地头偏移 = ZIP64 占位符
        }

        bytes[46] = 0x01;        // extra id 0x0001
        bytes[48] = 0xFF;        // 声明长度 65535（真值 0 字节）
        bytes[49] = 0xFF;

        bytes[54] = 0x50;        // EOCD
        bytes[55] = 0x4b;
        bytes[56] = 0x05;
        bytes[57] = 0x06;
        bytes[66] = 54;          // 中央目录大小

        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    /// <summary>时间戳那一类名字：数字段远超 <c>int</c> 上限，而名字里一个非数字字符都不多。</summary>
    private const string OversizedName = "backup.20261013235959";

    /// <summary>包内清单那一组：三份外置组件与清单本身都取仓内随包那份，两处口径只此一份。</summary>
    private static readonly Dictionary<string, string> PackageSources = new()
    {
        [@"engines\7z\7z.exe"] = TestPaths.BundledSevenZip,
        [@"engines\7z\7z.dll"] = Path.Combine(TestPaths.BundleFolder, "7z.dll"),
        [@"engines\unrar\UnRAR.exe"] = TestPaths.BundledUnRar,
    };

    /// <summary>
    /// B-2026-210：归档名的数字段超过 <c>int</c> 上限时按「不是分卷号」处理，不抛也不猜。
    /// 反向对照：撤掉 <c>int.TryParse</c> 改回 <c>int.Parse</c> ⇒ 开庭时抛异常被吞成 null，
    /// 用户看到的是这类包整段快路失效，这条即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-210")]
    public void OversizedVolumeNumber_MustNotKillTheFastPath()
    {
        FastPathFailureGuard.Reset();
        try
        {
            using var dir = new TestDirectory();
            var renamed = dir.Combine(OversizedName);
            File.Copy(Vault("seven-plain.7z"), renamed);

            using var session = SevenZipFastPathSession.Open(renamed);

            Assert.NotNull(session);
            Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
            Assert.Equal(FastPathVerdict.ProbablyWrong, session.Judge("definitely-not-it"));
            Assert.Equal(1, FastPathFailureGuard.InputFailures);
            Assert.False(FastPathFailureGuard.Disabled);
        }
        finally
        {
            FastPathFailureGuard.Reset();
        }
    }

    /// <summary>
    /// 输入类失败与 dll 内部失败分开计数：前者有自己的一本账，连来几次也不许把这一段关掉。
    /// 反向对照：把输入类并回 <c>MaxFailures</c> ⇒ 三次就永久关掉 dll 那一段，这条即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-210")]
    public void InputClassFailure_MustNotConsumeTheDllFailureBudget()
    {
        FastPathFailureGuard.Reset();
        try
        {
            for (var i = 0; i < FastPathFailureGuard.MaxFailures + 1; i++)
            {
                using var session = SevenZipFastPathSession.Open(OversizedName);
                Assert.NotNull(session);
            }

            Assert.Equal(FastPathFailureGuard.MaxFailures + 1, FastPathFailureGuard.InputFailures);
            Assert.False(FastPathFailureGuard.Disabled, "输入类失败不许把 dll 那一段永久关掉");
        }
        finally
        {
            FastPathFailureGuard.Reset();
        }
    }

    /// <summary>
    /// 随包 7z.dll 加载之前先与包内清单核对：同一份 dll 字节，清单对得上就照常开庭判定，对不上就一次都不用
    /// （B-2026-208）。反向对照：撤掉加载前那次核对 ⇒ ②即红 —— 那份 dll 本身加载得了。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-208")]
    public void BundledDll_MustBeVerifiedAgainstThePackageManifest()
    {
        FastPathFailureGuard.Reset();
        try
        {
            using var dir = new TestDirectory();
            var verified = Package(dir, "verified");
            var tampered = Package(dir, "tampered");
            FlipOneByte(Path.Combine(tampered, @"engines\7z\7z.dll"));

            var logs = new List<string>();

            // ① 清单对得上：照旧加载并开庭判定（这一条同时说明那份 dll 本身加载得了）
            using (var session = SevenZipFastPathSession.Open(
                       Vault("seven-plain.7z"),
                       Path.Combine(verified, @"engines\7z\7z.dll"),
                       new ChannelLog((_, message) => logs.Add(message))))
            {
                Assert.NotNull(session);
                Assert.Equal(FastPathVerdict.ProbablyWrong, session!.Judge("definitely-not-it"));
            }

            Assert.Empty(logs);

            // ② 改过一个字节的那一份：不加载它，用户看到的是「与发布清单不符」那一句
            Assert.Null(SevenZipFastPathSession.Open(
                Vault("seven-plain.7z"),
                Path.Combine(tampered, @"engines\7z\7z.dll"),
                new ChannelLog((_, message) => logs.Add(message))));
            Assert.Equal([string.Format(LogText.ComponentDigestDiffersAt, @"engines\7z\7z.dll")], logs);
            Assert.False(FastPathFailureGuard.Disabled);
        }
        finally
        {
            FastPathFailureGuard.Reset();
        }
    }

    /// <summary>
    /// 清单记错、文件一个字节都没改时也要拒：这一条与上一条配对，说明拦下来的是那次核对本身，
    /// 而不是「这份 dll 加载不了」。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-208")]
    public void MismatchedManifest_MustRefuseALoadableDll()
    {
        using var dir = new TestDirectory();
        var root = Package(dir, "wrong-digest");
        var dll = Path.Combine(root, @"engines\7z\7z.dll");
        var sums = Path.Combine(root, "SHA256SUMS");
        File.WriteAllLines(
            sums,
            File.ReadAllLines(sums).Select(
                line => line.EndsWith(@"  engines/7z/7z.dll", StringComparison.Ordinal)
                    ? new string('f', 64) + @"  engines/7z/7z.dll"
                    : line),
            new UTF8Encoding(false));

        var logs = new List<string>();

        Assert.Null(SevenZipFastPathSession.Open(
            Vault("seven-plain.7z"), dll, new ChannelLog((_, message) => logs.Add(message))));
        Assert.Equal([string.Format(LogText.ComponentDigestDiffersAt, @"engines\7z\7z.dll")], logs);
    }

    /// <summary>没有那份清单时跳过核对、照常加载，不把「没核」当作拒绝。</summary>
    [Fact, Trait("Bug", "B-2026-208")]
    public void MissingManifest_MustStillLoadTheDll()
    {
        using var dir = new TestDirectory();
        var root = Package(dir, "no-manifest", manifest: false);
        var logs = new List<string>();

        using var session = SevenZipFastPathSession.Open(
            Vault("seven-plain.7z"),
            Path.Combine(root, @"engines\7z\7z.dll"),
            new ChannelLog((_, message) => logs.Add(message)));

        Assert.NotNull(session);
        Assert.Equal([LogText.ComponentCheckSkipped], logs);
    }

    /// <summary>造一份便携包形态的临时程序目录：三份外置组件照抄仓内随包那份，可选按实际字节写清单。</summary>
    private static string Package(TestDirectory dir, string name, bool manifest = true)
    {
        var root = dir.Combine(name);
        foreach (var (relative, source) in PackageSources)
        {
            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }

        if (manifest)
        {
            var rows = PackageSources.Keys.Select(
                relative => $"{Sha256(Path.Combine(root, relative))}  {relative.Replace('\\', '/')}");
            File.WriteAllLines(Path.Combine(root, "SHA256SUMS"), rows, new UTF8Encoding(false));
        }

        return root;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void FlipOneByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>
/// 边界档：把快路划掉的候选逐条交给真引擎复核（B-2026-011 的唯一硬证据）。
/// 每个格式一个加密包乘若干候选，判据是「快路说不行的，引擎也必须说不行」。
/// </summary>
[Trait("Category", "edge")]
[Collection("engine")]
public sealed class FastPathDifferenceTests
{
    private const string Password = "BbuPw2026";

    private static readonly IFastPath FastPath = new CompositeFastPath();

    public static TheoryData<string> Covered => new(
        "zip-aes256.zip",
        "zip-zipcrypto.zip",
        "rar5-hp.rar",
        "rar5-p.rar",
        "seven-mhe.7z",
        "seven-plain.7z",
        Path.Combine("vol-zip-num", "x.001"),
        Path.Combine("vol-seven-num", "x.7z.001"));

    [Theory, MemberData(nameof(Covered))]
    [Trait("Bug", "B-2026-011")]
    public async Task EveryRejectedCandidate_MustAlsoFailTheRealEngine(string name)
    {
        var archive = Path.Combine(TestPaths.Fixtures, "vault", name);
        var engine = TestPaths.NewRarEngine();
        var info = await engine.InspectAsync(archive);

        using var session = FastPath.Open(archive);
        var rejected = 0;
        for (var i = 0; i < 12; i++)
        {
            var candidate = $"wrong-{i:000}";
            if (session is null || session.Judge(candidate) is not FastPathVerdict.ProbablyWrong)
            {
                continue;
            }

            rejected++;
            var result = await engine.VerifyAsync(archive, candidate, null, info);
            Assert.False(result.Ok, $"{name} 的快路划掉了引擎认为正确的候选：{candidate}");
        }

        Assert.True(rejected >= 10, $"{name} 复核对照只覆盖了 {rejected} 条");
    }

    [Theory, MemberData(nameof(Covered))]
    [Trait("Bug", "B-2026-011")]
    public async Task SurvivingCandidates_MustStillGoThroughTheEngine(string name)
    {
        var archive = Path.Combine(TestPaths.Fixtures, "vault", name);
        var engine = TestPaths.NewRarEngine();
        var info = await engine.InspectAsync(archive);

        using var session = FastPath.Open(archive);
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
        var result = await engine.VerifyAsync(archive, Password, null, info);
        Assert.True(result.Ok, name);
    }

    [Fact, Trait("Bug", "B-2026-011")]
    public async Task VaultWithFastPath_MustReachTheSameHitAsWithoutIt()
    {
        var archive = Path.Combine(TestPaths.Fixtures, "vault", "zip-aes256.zip");
        var book = Enumerable.Range(0, 40).Select(i => $"pw-{i:0000}").Concat([Password]).ToArray();

        var withFastPath = await UnlockAsync(archive, book, new CompositeFastPath());
        var withoutFastPath = await UnlockAsync(archive, book, null);

        Assert.True(withFastPath.Ok);
        Assert.Equal(withoutFastPath.Password, withFastPath.Password);
        Assert.Equal(withoutFastPath.Tries, withFastPath.Tries);
    }

    private static async Task<UnlockResult> UnlockAsync(string archive, string[] book, IFastPath? fastPath)
    {
        var vault = new Vault();
        vault.SetEntries(book.Select(p => new VaultEntry(p)));
        return await vault.UnlockAsync(
            TestPaths.NewRarEngine(), archive, new UnlockOptions { Workers = 4 }, fastPath);
    }
}
