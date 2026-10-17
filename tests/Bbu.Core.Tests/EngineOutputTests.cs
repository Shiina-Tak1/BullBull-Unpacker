using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 输出解码与特征词判定，外加中文名的端到端断言。
/// 与「引擎与编码」的实测表。
///
/// 要害是**按行挑编码**：7z 与 rar 的输出是混合编码的，整段只挑一种必有一边乱码；
/// 而乱码的文件名拿去做单条目验证时，7z 匹配不到却返回 0。
/// </summary>
[Collection("engine")]
public sealed class EngineOutputTests
{
    [Fact]
    public void DecodeLine_MustTryUtf8ThenChineseThenSystemThenLatin1()
    {
        // 「下载」的 UTF-8 与 GBK 两种字节都解得出同一个词
        Assert.Equal("下载", EngineOutput.DecodeLine(new byte[] { 0xE4, 0xB8, 0x8B, 0xE8, 0xBD, 0xBD }));
        Assert.Equal("下载", EngineOutput.DecodeLine(new byte[] { 0xCF, 0xC2, 0xD4, 0xD8 }));

        // 任何一档都解不了时才落到 latin-1：0xFF 在 UTF-8 与 CP936 里都非法
        Assert.Equal("ÿþ", EngineOutput.DecodeLine(new byte[] { 0xFF, 0xFE }));
        Assert.Equal(string.Empty, EngineOutput.DecodeLine([]));
    }

    [Fact]
    public void Decode_MustPickEncodingPerLine()
    {
        // 同一段里前面是 ASCII，中间是 GBK 的中文路径，后面又是 ASCII
        var raw = new byte[] { (byte)'a', (byte)'\n', 0xCF, 0xC2, 0xD4, 0xD8, (byte)'\n', (byte)'b' };
        Assert.Equal("a\n下载\nb", EngineOutput.Decode(raw));
    }

    [Fact]
    public void KeywordJudgements_MustStayApart()
    {
        // 条目名没匹配上：7z 仍返回 0，只能看这几个字
        Assert.True(EngineOutput.NothingMatched("No files to process\nEverything is Ok\n\nFiles: 0"));
        Assert.False(EngineOutput.NothingMatched("Everything is Ok\nFiles: 3"));

        // 强证据可单独定罪；弱证据对「加密包加错密码」与「未加密包加损坏」同样成立
        Assert.True(EngineOutput.LooksLikeWrongPassword("ERROR: Wrong password : payload.bin"));
        Assert.True(EngineOutput.HasWeakWrongPasswordHint("ERROR: CRC Failed in encrypted file. Wrong password? : x"));
        Assert.False(EngineOutput.HasWeakWrongPasswordHint("Everything is Ok"));

        Assert.True(EngineOutput.LooksHeaderEncrypted("Cannot open encrypted archive. Wrong password?"));
        Assert.False(EngineOutput.LooksHeaderEncrypted("Cannot open the file as archive"));
    }

    [Fact]
    public void MissingVolumeJudgement_MustCheckTheSourceIsStillThere()
    {
        const string output = "ERROR: 系统找不到指定的文件。";
        Assert.True(EngineOutput.LooksLikeMissingVolume(output));

        using var dir = new TestDirectory();
        var archive = dir.Combine("x.part1.rar");
        File.WriteAllText(archive, "part1");
        Assert.True(EngineOutput.LooksLikeMissingVolume(output, archive));

        // 源包真的不在时不许判成缺分卷：否则「源包被删」会被误诊成「分卷不全」
        File.Delete(archive);
        Assert.False(EngineOutput.LooksLikeMissingVolume(output, archive));
    }

    [Fact]
    public void Decode_MustSplitOnBothCrAndLf()
    {
        // 7z 的进度靠 CR 原地刷新，整轮可能一个 LF 都没有：只按 LF 切会把几十个百分比连成一行
        Assert.Equal("a\nb\nc", EngineOutput.Decode("a\rb\nc"u8.ToArray()));
        Assert.Equal("a\nb", EngineOutput.Decode("a\r\nb"u8.ToArray()));
        Assert.Equal("a\nb\n", EngineOutput.Decode("a\nb\n"u8.ToArray()));
    }

    /// <summary>
    /// 进度百分比的解析。两边的行格式都照实测原话钉（官方只定义 <c>-bs</c> 的语义，不给行格式），
    /// 撤掉解析即红：活动槽会退回「只有秒数」。
    /// </summary>
    [Fact]
    public void PercentParsing_MustFollowEachEngineShape()
    {
        // 7z：百分比在行首；扫描行的数字后面不是 '%'，不命中
        Assert.True(EngineOutput.TryReadPercent(" 63% 110 T S001\\GameAssembly.dll", EngineKind.SevenZip, out var seven));
        Assert.Equal(63, seven);
        Assert.True(EngineOutput.TryReadPercent(" 41% 78 - S001\\fallen_Data\\x.bundle", EngineKind.SevenZip, out var unpack));
        Assert.Equal(41, unpack);
        Assert.True(EngineOutput.TryReadPercent("100% T chain-middle.7z", EngineKind.SevenZip, out var full));
        Assert.Equal(100, full);
        Assert.False(EngineOutput.TryReadPercent("  0M Scan D:\\x\\vault\\", EngineKind.SevenZip, out _));
        Assert.False(EngineOutput.TryReadPercent("ERROR: Wrong password : x", EngineKind.SevenZip, out _));

        // UnRAR：百分比在行内被退格原地刷新，一行里串起好几个，取最后一个
        Assert.True(EngineOutput.TryReadPercent("Extracting  ...bundle                          26% 27%   OK", EngineKind.UnRar, out var rar));
        Assert.Equal(27, rar);
        Assert.True(EngineOutput.TryReadPercent("Testing     vault-payload.txt                                           100%  OK ", EngineKind.UnRar, out var done));
        Assert.Equal(100, done);
        Assert.False(EngineOutput.TryReadPercent("All OK", EngineKind.UnRar, out _));
    }

    /// <summary>
    /// 同名的目录或文件挡住这一层时的归类：7z 要把挡路的非空目录换成包里的文件，报
    /// <c>Cannot delete output folder</c> 并返回 2。不归类的话用户只看到那句英文原话。
    /// 这个域插在「无法读取该压缩包」之后、「头加密」之前，顺序即优先级，更宽的既有两域抢不走它。
    ///
    /// 另一半钉住「只收 7z 那一句」：UnRAR 的 <c>Cannot create</c> 对磁盘满、权限、路径非法同样成立，
    /// 归进这里会把那几种误说成同名冲突，所以它必须继续逐字照抛原话。
    /// 反向对照：撤掉 <c>Conclusion</c> 里那个分支时正向那半回到只照抛原话、变红。
    /// </summary>
    [Fact]
    public void SameNameBlockedConclusion_MustCoverSevenZipOnly()
    {
        // 实测原话（随包 26.02 ZS，退出码 2）：目标里已有同名目录，而包内同名的是文件
        const string sevenZip = "ERROR: Cannot delete output folder : 目录不是空的。 : C:\\demo\\same.txt";
        Assert.True(EngineOutput.LooksLikeSameNameBlocked(sevenZip));
        Assert.Equal(Errors.SameNameBlocksTheLayer, EngineOutput.Conclusion(sevenZip));

        // 用户看到的是「归类 ＋ 原话」两半，原话不许被吞掉（与 B-2026-233 同一个口径）
        var result = new EngineResult { Code = 2, Output = sevenZip };
        Assert.Equal(
            string.Format(Format.ConclusionWithOutputAt, Errors.SameNameBlocksTheLayer, result.Tail),
            result.Brief);
        Assert.Contains("Cannot delete output folder", result.Brief, System.StringComparison.Ordinal);

        // UnRAR 的创建失败（实测：outdir 路径本身是个文件时退出码 9）不进这一域，继续只照抛原话
        const string unRar = "Cannot create C:\\demo\\same.txt\\inner.txt\n系统找不到指定的路径。";
        Assert.False(EngineOutput.LooksLikeSameNameBlocked(unRar));
        Assert.Equal(string.Empty, EngineOutput.Conclusion(unRar));
    }

    /// <summary>
    /// 不是压缩包的包必须判成「结构性失败」，而不是「密码全不对」。
    ///
    /// 撤掉修复即红：旧词表那条是 <c>can not open the file as archive</c>（两个词），
    /// 而随包 26.02 ZS 对判不出格式那份包说的是 <c>Cannot open the file as archive</c>，子串匹配永不命中。
    /// 判成假时上层会继续试完整本密码 —— 那是 2026-10-08 修掉的真缺陷（B-2026-137）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-137")]
    public async Task NotAnArchive_MustBeJudgedAsStructuralFailure()
    {
        var engine = TestPaths.NewEngine();

        // 0 字节包：7z 判不出格式，只回 Cannot open the file as archive
        var empty = await engine.TestAsync(TestPaths.Fixture("error-empty.zip"), "一个无关密码");
        Assert.False(empty.Ok);
        Assert.True(EngineOutput.LooksLikeStructuralFailure(empty.Output), empty.Output);

        // 非 rar 文件交给 UnRAR：它说的是 is not RAR archive（中间多一个 RAR，旧词表同样不命中）
        var rar = await engine.TestAsync(TestPaths.Fixture("error-random.zip"), "一个无关密码", EngineKind.UnRar);
        Assert.False(rar.Ok);
        Assert.True(EngineOutput.LooksLikeStructuralFailure(rar.Output), rar.Output);

        // 带扩展名的坏包走另一句（Is not archive），修复前后都命中，这里只是把两句话都钉住
        var junk = await engine.TestAsync(TestPaths.Fixture("error-text.zip"), "一个无关密码");
        Assert.True(EngineOutput.LooksLikeStructuralFailure(junk.Output), junk.Output);

        // 截断包：Unexpected end of archive，与密码无关，同样收进结构失败
        var cut = await engine.TestAsync(TestPaths.Fixture("error-truncated.zip"), "一个无关密码");
        Assert.True(EngineOutput.LooksLikeStructuralFailure(cut.Output), cut.Output);
    }

    [Fact]
    public async Task ChineseArchive_MustListWithIntactNames()
    {
        var info = await TestPaths.NewEngine().InspectAsync(TestPaths.Fixture("中文加密包.7z"));

        Assert.True(info.ReadOk);
        Assert.True(info.Encrypted);
        Assert.False(info.HeaderEncrypted);
        Assert.Equal("中文文件名.txt", info.FirstEntry);
        Assert.Equal("中文文件名.txt", info.SmallestEntry);
        Assert.Equal(1, info.EntryCount);
        Assert.True(info.TotalSize > 0);
    }

    [Fact]
    public async Task WrongPasswordOutput_MustEchoTheChineseNameIntact()
    {
        var result = await TestPaths.NewEngine().TestAsync(TestPaths.Fixture("中文加密包.7z"), "不是这个密码");

        Assert.False(result.Ok);
        Assert.True(result.WrongPassword);
        Assert.Contains("中文文件名.txt", result.Output);
        Assert.DoesNotContain('\ufffd', result.Output);
    }

    [Fact]
    public async Task ChineseArchive_Extract_MustWriteTheChineseName()
    {
        using var dir = new TestDirectory();
        var result = await TestPaths.NewEngine().ExtractAsync(
            TestPaths.Fixture("中文加密包.7z"), dir.Path, "密码123");

        Assert.True(result.Ok, result.Output);
        var extracted = Path.Combine(dir.Path, "中文文件名.txt");
        Assert.True(File.Exists(extracted), "解压结果里没有那个中文名文件");
        Assert.Contains("中文内容", File.ReadAllText(extracted, Encoding.UTF8));
    }

    [Fact]
    public async Task CorrectPassword_MustPassTest()
    {
        var result = await TestPaths.NewEngine().TestAsync(TestPaths.Fixture("中文加密包.7z"), "密码123");

        Assert.True(result.Ok, result.Output);
        Assert.False(result.WrongPassword);
    }

    [Fact]
    public async Task SingleEntryVerification_MustNotPassOnAMismatchedName()
    {
        var engine = TestPaths.NewEngine();
        var archive = TestPaths.Fixture("中文加密包.7z");

        var missing = await engine.TestAsync(archive, "密码123", entry: "根本不存在的条目.txt");
        Assert.False(missing.Ok);
        Assert.True(missing.EntryNotMatched);

        var present = await engine.TestAsync(archive, "密码123", entry: "中文文件名.txt");
        Assert.True(present.Ok, present.Output);
    }

    [Fact]
    public async Task EmptyPasswordOnTheRarRoute_MustSwitchToSevenZip()
    {
        var engine = TestPaths.NewEngine(new RecordingLog());
        var archive = TestPaths.Fixture("中文加密包.7z");
        using var dir = new TestDirectory();

        // 空密码 + UnRAR 路由：裸 `-p` 在 RAR 命令行里是「请提示输入密码」，必须改派 7-Zip。
        var result = await engine.ExtractAsync(
            archive, dir.Path, string.Empty, new ExtractOptions { Kind = EngineKind.UnRar });

        Assert.Equal(TestPaths.BundledSevenZip, result.Command[0]);
        Assert.Contains("-p", result.Command);
        Assert.False(result.EngineMissing);

        // 对照：非空密码不改派，命令里那一份仍是随包 UnRAR。它打不开 7z 包，所以这一条必然失败，
        // 但「改派」与「改派出去也没解开」是两件事，这里钉的只是没改派。
        using var other = new TestDirectory();
        var notSwitched = await engine.ExtractAsync(
            archive, other.Path, "密码123", new ExtractOptions { Kind = EngineKind.UnRar });
        Assert.Equal(TestPaths.BundledUnRar, notSwitched.Command[0]);
        Assert.False(notSwitched.Ok, notSwitched.Output);
    }

    [Fact]
    public async Task Inspect_OnSomethingThatIsNotAnArchive_MustSayItCannotRead()
    {
        var info = await TestPaths.NewEngine().InspectAsync(TestPaths.Fixture("error-text.zip"));

        Assert.False(info.ReadOk);
        Assert.Null(info.Encrypted);
        Assert.Equal(ArchiveInfo.Unknown, info);
    }

    /// <summary>
    /// 回显的条目名不得交给特征词判定。
    ///
    /// `7z l -slt` 必然回显条目名（<c>Path = …</c>），而条目名由归档作者完全控制：一个叫
    /// <c>how to open encrypted archive.txt</c> 的条目曾让**完全未加密**的包被判成加密包，
    /// 进而把密码本里的错误密码记成命中（外部审计第 1 轮 F-04）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-186")]
    public async Task Inspect_MustNotReadTheEncryptionVerdictOutOfEntryNames()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("how to open encrypted archive.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "how to open encrypted archive.txt", "enter password here.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("this archive is NOT encrypted at all");
            }
        }

        var info = await TestPaths.NewEngine().InspectAsync(archive);

        Assert.True(info.ReadOk);
        Assert.False(info.Encrypted);
        Assert.False(info.HeaderEncrypted);
    }

    /// <summary>
    /// 剥离只去数据、不去诊断：引擎自己说的话照旧算数，回显的路径与条目名一律不算。
    ///
    /// 覆盖**本仓实测**的七种回显形态：`-slt` 的字段行、`Listing archive:`、`Testing archive:`、
    /// `Extracting from`、UnRAR 把条目名嵌进错密码那句的写法，以及 `ERROR: <路径> : <诊断>` 与
    /// `ERROR: <诊断> : <名字>` 两种排法。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-186")]
    public void KeywordJudgements_MustIgnoreEchoedPathsAndNames()
    {
        // 引擎自己的话：算数
        Assert.True(EngineOutput.LooksHeaderEncrypted("Cannot open encrypted archive. Wrong password?"));
        Assert.True(EngineOutput.LooksLikeWrongPassword("ERROR: Wrong password : payload.bin"));
        Assert.True(EngineOutput.LooksLikeMissingVolume("ERROR: 系统找不到指定的文件。"));

        // 回显的条目名与路径：不算
        Assert.False(EngineOutput.LooksLikeWrongPassword("Path = how to open encrypted archive.txt"));
        Assert.False(EngineOutput.LooksHeaderEncrypted("Path = enter password here.txt"));
        Assert.False(EngineOutput.LooksLikeStructuralFailure("Listing archive: D:\\x\\is not archive.zip"));
        Assert.False(EngineOutput.LooksLikeStructuralFailure("Testing archive: D:\\x\\is not archive.zip"));
        Assert.False(EngineOutput.LooksLikeMissingVolume("Extracting from D:\\x\\no such file.rar"));

        // UnRAR 把条目名嵌在诊断句里：话留着，名字去掉
        Assert.True(EngineOutput.LooksLikeWrongPassword("Incorrect password for wrong password.txt"));
        Assert.False(EngineOutput.LooksLikeStructuralFailure("Incorrect password for is not archive.txt"));

        // 引擎把条目名接在诊断句尾：同样只留话术
        Assert.True(EngineOutput.LooksLikeWrongPassword("ERROR: Wrong password : wrong password.txt"));
        Assert.False(EngineOutput.LooksLikeStructuralFailure("ERROR: Data Error : is not archive.txt"));
        Assert.False(EngineOutput.LooksLikeMissingVolume("ERROR: Data Error : no such file.rar"));

        // 路径在前那一种排法：丢的是路径，诊断留着
        var path = "downloads" + Path.DirectorySeparatorChar + "pack.zip";
        Assert.True(EngineOutput.LooksHeaderEncrypted(
            "ERROR: " + path + " : Cannot open encrypted archive. Wrong password?"));
        Assert.True(EngineOutput.LooksLikeStructuralFailure("ERROR: " + path + " : is not archive"));
    }

    [Fact]
    public async Task DebugChannel_MustCarryTheCommandLineUnmasked()
    {
        var log = new RecordingLog();
        using var dir = new TestDirectory();
        await TestPaths.NewEngine(log).ExtractAsync(
            TestPaths.Fixture("中文加密包.7z"), dir.Path, "密码123");

        Assert.Contains(log.Debugs, line => line.Contains("密码123", System.StringComparison.Ordinal));
        Assert.DoesNotContain(log.Infos, line => line.Contains("密码123", System.StringComparison.Ordinal));
    }
}
