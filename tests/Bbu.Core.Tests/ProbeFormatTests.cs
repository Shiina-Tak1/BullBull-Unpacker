using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层默认档：一个文件真实是什么格式、三态怎么分、随包引擎读不读得了它。
///
/// 这一层破了不会报错，只会安静地做错事 —— 把包当垃圾跳过、把读不动说成不是包、
/// 或者拿"密码错误"去解释一个编解码器缺失。所以它在默认档，不进慢档。
/// </summary>
[Collection("engine")]
public sealed class ProbeFormatTests
{
    /// <summary>逐格式的签名表。分卷、伪装那一类判据各自另有文件。</summary>
    private static readonly (string Name, byte[] Bytes, ArchiveFormat Want)[] Formats =
    [
        ("zip 本地头", Concat("PK\x03\x04", 26), ArchiveFormat.Zip),
        ("zip 空包尾记录", Concat("PK\x05\x06", 18), ArchiveFormat.Zip),
        ("zip 跨卷数据描述符", Concat("PK\x07\x08", 20), ArchiveFormat.Zip),
        ("7z", Concat("7z\xbc\xaf\x27\x1c", 28), ArchiveFormat.SevenZip),
        ("rar4", Concat("Rar!\x1a\x07\x00", 32), ArchiveFormat.Rar),
        ("rar5", Concat("Rar!\x1a\x07\x01\x00", 32), ArchiveFormat.Rar5),
        ("gzip", Concat("\x1f\x8b", 30), ArchiveFormat.GZip),
        ("lz4 帧", Concat("\x04\x22\x4d\x18", 28), ArchiveFormat.Lz4),
        ("lz5 帧", Concat("\x05\x22\x4d\x18", 28), ArchiveFormat.Lz5),
        ("zstd 帧", Concat("\x28\xb5\x2f\xfd", 28), ArchiveFormat.Zstd),
        ("tar（magic 在偏移 257）", Tar, ArchiveFormat.Tar),
        ("mp4（ftyp 在偏移 4）", Mp4, ArchiveFormat.Mp4),
    ];

    private static byte[] Concat(string head, int tail) =>
        Encoding.Latin1.GetBytes(head).Concat(new byte[tail]).ToArray();

    private static byte[] Tar
    {
        get
        {
            var buffer = new byte[512];
            Encoding.ASCII.GetBytes("ustar").CopyTo(buffer, 257);
            return buffer;
        }
    }

    /// <summary>
    /// mp4 的尺寸字段是二进制，不能拼进字符串字面量：<c>"\x18f"</c> 会被 C# 当成
    /// <b>三</b>位十六进制转义（U+018F），于是 ftyp 的偏移整整错一位。
    /// </summary>
    private static byte[] Mp4
    {
        get
        {
            var buffer = new byte[32];
            buffer[3] = 0x18;
            Encoding.ASCII.GetBytes("ftypisom").CopyTo(buffer, 4);
            return buffer;
        }
    }

    /// <summary>判据 2：magic 逐格式断言。扩展名与实际内容不一致时以内容为准。</summary>
    [Fact]
    public void FormatTable_MustMatchTheRealSignature()
    {
        using var tmp = new TestDirectory();
        foreach (var (name, bytes, want) in Formats)
        {
            var path = tmp.Combine("x.bin");
            File.WriteAllBytes(path, bytes);

            var got = Probe.DetectFormat(path);
            Assert.True(got == want, $"{name}：期望 {want}，实际 {got}");

            var classified = Probe.Classify(path);
            Assert.Equal(want, classified.Format);

            if (want is ArchiveFormat.Mp4)
            {
                // 真视频是"正常跳过"这一档：既不是候选，也不是读不了。
                Assert.Equal(ProbeStatus.NotArchive, classified.Status);
                Assert.False(classified.ShallHandle);
                continue;
            }

            Assert.True(classified.IsCandidate, $"{name}：认出来了却不是候选");
        }
    }

    /// <summary>
    /// 判据 2 的两个边界：4 字节的短文件与空文件。
    /// 两道都不许抛异常：它们是"读到多少算多少"的输入，不是错误输入。
    /// </summary>
    [Fact]
    public void ShortAndEmptyFiles_MustStillGetAnAnswer()
    {
        using var tmp = new TestDirectory();

        var shortOne = tmp.Combine("short.bin");
        File.WriteAllBytes(shortOne, Encoding.Latin1.GetBytes("PK\x03\x04"));
        Assert.Equal(ArchiveFormat.Zip, Probe.DetectFormat(shortOne));

        var empty = tmp.Combine("empty.bin");
        File.WriteAllBytes(empty, []);
        Assert.Equal(ArchiveFormat.Unknown, Probe.DetectFormat(empty));

        // 空文件读得到（读到 0 字节），所以它是"不是包"，不是"读不了"。
        Assert.Equal(ProbeStatus.NotArchive, Probe.Classify(empty).Status);
    }

    /// <summary>
    /// 判据 3 / B-2026-064：读不了与不是包必须分得开。
    ///
    /// 判据是反向对照：拿一个 <c>FileShare.None</c> 的句柄占住一个**真包**，
    /// 结论必须是"读不了"；同一个真包不占时必须是候选。分不开的话，
    /// 个别文件读不动会被当成"不是压缩包"静默跳过。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-064")]
    public void UnreadableFile_MustNotBeReportedAsNotArchive()
    {
        var pack = TestPaths.Fixture("ok-small.zip");

        using (var hold = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = Probe.Classify(pack);
            Assert.Equal(ProbeStatus.Unreadable, blocked.Status);
            Assert.NotEqual(ProbeStatus.NotArchive, blocked.Status);
            Assert.Equal(ArchiveFormat.Unknown, blocked.Format);
            Assert.Contains("无法读取", blocked.Note);
            Assert.False(blocked.ShallHandle);
        }

        // 对照：不占它时是候选。没有这一半，一个"一律报读不了"的实现也能过。
        Assert.Equal(ProbeStatus.Candidate, Probe.Classify(pack).Status);
    }

    /// <summary>判据 3 的另一半：真不是包的文件落在"不是包"，不许落到"读不了"。</summary>
    [Fact]
    public void PlainFile_MustBeNotArchiveNotUnreadable()
    {
        using var tmp = new TestDirectory();
        var text = tmp.Combine("readme.txt");
        File.WriteAllText(text, new string('a', 4096));

        var classified = Probe.Classify(text);
        Assert.Equal(ProbeStatus.NotArchive, classified.Status);
        Assert.Equal(ProbeText.NotArchive, classified.Note);
    }

    /// <summary>
    /// 扩展名不可信：一个真 zip 叫 `.mp4` 时，判出来的必须是 zip。
    /// 本轮之后任何按扩展名下结论的地方都是 bug。
    /// </summary>
    [Fact]
    public void Detection_MustNotTrustTheExtension()
    {
        Assert.Equal(ArchiveFormat.Zip, Probe.DetectFormat(TestPaths.Fixture("ok-renamed.mp4")));
        Assert.True(Probe.IsDisguised(TestPaths.Fixture("ok-renamed.mp4")));

        // 反过来：名字像包、内容不是包时，格式判不出来，但名字仍把它送进候选。
        using var tmp = new TestDirectory();
        var fake = tmp.Combine("x.zip");
        File.WriteAllText(fake, new string('z', 4096));
        Assert.Equal(ArchiveFormat.Unknown, Probe.DetectFormat(fake));
        Assert.Equal(ProbeStatus.Candidate, Probe.Classify(fake).Status);
    }

    /// <summary>
    /// 判据 16：认出 lz4 / lz5 / zstd / brotli 之后，引擎能力要**另判一次**。
    ///
    /// 这几个格式的编解码器在 <c>7z.dll</c> 里。随包，所以现在
    /// 能力档是"读得了"；但判据本身仍是"跟着 dll 在不在走"，不许退化成按格式名硬编码 ——
    /// 所以反面照旧要钉：把 dll 撤走就必须变成读不了。
    /// </summary>
    [Fact]
    public void EngineAbility_MustFollowTheCodecDll()
    {
        var exe = TestPaths.BundledSevenZip;
        Assert.True(File.Exists(exe), "仓内没有自带 7z：" + exe);

        // 前提：随包这一份旁边有 7z.dll（2026-10-01 裁决随包）。
        var dll = Path.Combine(Path.GetDirectoryName(exe)!, "7z.dll");
        Assert.True(File.Exists(dll), "随包缺 7z.dll，lz4 一族读不了：" + dll);

        Assert.Equal(EngineAbility.CanRead, Probe.ReadabilityOf(ArchiveFormat.Zip, exe));
        Assert.Equal(EngineAbility.CanRead, Probe.ReadabilityOf(ArchiveFormat.Rar5, exe));

        foreach (var format in new[]
                 {
                     ArchiveFormat.Lz4, ArchiveFormat.Lz5, ArchiveFormat.Zstd, ArchiveFormat.Brotli,
                 })
        {
            Assert.Equal(EngineAbility.CanRead, Probe.ReadabilityOf(format, exe));
        }

        // 反面：把 dll 撤走就必须变成"读不了这个格式"。没有这一半，
        // 一个"这几个格式一律报读得了"的实现也能过上面那几条。
        using var tmp = new TestDirectory();
        File.Copy(exe, tmp.Combine("7z.exe"));
        var bare = tmp.Combine("7z.exe");
        foreach (var format in new[]
                 {
                     ArchiveFormat.Lz4, ArchiveFormat.Lz5, ArchiveFormat.Zstd, ArchiveFormat.Brotli,
                 })
        {
            var ability = Probe.ReadabilityOf(format, bare);
            Assert.True(ability == EngineAbility.NeedsCodecDll, $"{format}：期望读不了，实际 {ability}");
            Assert.Equal(ProbeText.EngineNeedsCodecDll, Probe.AbilityNote(ability));
        }

        // 不是包的不许落进"引擎读不了这个格式"：那两件事的用户结论不同。
        Assert.Equal(EngineAbility.NotArchive, Probe.ReadabilityOf(ArchiveFormat.Unknown, exe));
        Assert.Equal(EngineAbility.NotArchive, Probe.ReadabilityOf(ArchiveFormat.Mp4, exe));
    }

    /// <summary>
    /// 判据 16 的真跑对照：拿随包 7z.exe 去读 lz4 骨架包（帧头是 lz4，里面裹着 RAR5）。
    ///
    /// dll 随包之后引擎读得了它（退出码 0 加 `Everything is Ok`，2026-10-01 实测）。
    /// 这条与 `EngineAbility_MustFollowTheCodecDll` 的分工：那边判"能力档跟着 dll 走"，
    /// 这条判"识别层的结论与引擎的实际行为对得上" —— 认成 lz4 之后引擎确实解得动，
    /// 不许出现"识别说能、引擎说不能"的错位。
    /// </summary>
    [Fact]
    public async Task Lz4Skeleton_MustBeRecognizedAndBeReadableByTheEngine()
    {
        var skeleton = TestPaths.Probe(Path.Combine("zs-skeleton", "x.lz4"));

        Assert.Equal(ArchiveFormat.Lz4, Probe.DetectFormat(skeleton));

        var classified = Probe.Classify(skeleton);
        Assert.True(classified.IsCandidate);
        Assert.Equal(ArchiveFormat.Lz4, classified.Format);
        Assert.False(Probe.IsDisguised(skeleton));

        Assert.Equal(
            EngineAbility.CanRead,
            Probe.ReadabilityOf(classified.Format, TestPaths.BundledSevenZip));

        var engine = TestPaths.NewEngine();
        var result = await engine.TestAsync(skeleton, kind: EngineKind.SevenZip);

        Assert.True(result.Ok, "引擎读不了这个 lz4 骨架，判据 16 要重算：" + result.Output);
        Assert.False(result.WrongPassword);

        // 这一条是"不许落成不是压缩包"的根：它是候选，不是"跳过"。
        Assert.True(classified.ShallHandle);
    }

    /// <summary>格式名与"次卷不是分卷 N"的两条文案判据，落在识别层的描述接口上。</summary>
    [Fact]
    public void Descriptions_MustNotLeadTheUserAstray()
    {
        Assert.Equal(ProbeText.FormatUnknown, Probe.FormatName(ArchiveFormat.Unknown));
        Assert.Equal(ProbeText.FormatZip, Probe.FormatName(ArchiveFormat.Zip));
        Assert.Equal(ProbeText.FormatMp4, Probe.FormatName(ArchiveFormat.Mp4));

        var first = Probe.ClassifyVolume(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")));
        var later = Probe.ClassifyVolume(TestPaths.Probe(Path.Combine("vol-part", "x.part2.rar")));

        Assert.Contains("分卷 1", Probe.Describe(ArchiveFormat.Rar5, first), StringComparison.Ordinal);
        var laterText = Probe.Describe(ArchiveFormat.Rar5, later);
        Assert.DoesNotContain("分卷", laterText, StringComparison.Ordinal);
        Assert.Contains("次卷", laterText, StringComparison.Ordinal);

        // 不是分卷时只给格式名，不许凭空加卷号。
        var plain = Probe.ClassifyVolume(TestPaths.Fixture("ok-small.zip"));
        Assert.True(plain.IsSplit, "单卷 zip 也算 ZipSplit 组，只是成员数为 1");
        Assert.Equal(ProbeText.FormatZip, Probe.Describe(ArchiveFormat.Zip, null));
    }

    /// <summary>
    /// B-2026-200：按扩展名兜底送的候选，结论里要写清它不是按 magic 判出来的。
    ///
    /// 两条对着钉：纯文本的 <c>readme.br</c>（Brotli 没有签名，只能靠扩展名）必须带那句兜底说明，
    /// 真包那一条必须不带 —— 两档共用一句结论文案就等于没分开。
    /// 内容探测这一档**实测不可行**（brotli 允许未压缩 meta-block，<c>'a'</c> 一类文本本身就是合法流），
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-200")]
    public void CandidateByExtension_MustSaySoInItsVerdict()
    {
        using var tmp = new TestDirectory();

        var guessed = tmp.Combine("readme.br");
        File.WriteAllText(guessed, new string('a', 4096));
        var byName = Probe.Classify(guessed);
        Assert.Equal(ProbeStatus.Candidate, byName.Status);
        Assert.Contains("按扩展名", byName.Note, StringComparison.Ordinal);
        Assert.Contains(".br", byName.Note, StringComparison.Ordinal);

        // 清单里也要看得见这一句，用户才知道这个结论是猜的。两个入口都要带上这一句，不许只在一处带。
        var job = Assert.Single(Scan.Run([guessed]), item => item.DisplayName == "readme.br");
        Assert.True(job.Runnable);
        Assert.Contains("按扩展名", job.Reason, StringComparison.Ordinal);

        var inFolder = Assert.Single(Scan.Run([tmp.Path]), item => item.DisplayName == "readme.br");
        Assert.Contains("按扩展名", inFolder.Reason, StringComparison.Ordinal);

        // 对照：magic 判得出的真包不带兜底说明。
        var real = Probe.Classify(TestPaths.Fixture("ok-small.zip"));
        Assert.Equal(ProbeStatus.Candidate, real.Status);
        Assert.Equal(string.Empty, real.Note);

        // 另一条同效通路（名字在包名单里、内容不是包）同样要带上它。
        var text = tmp.Combine("x.zip");
        File.WriteAllText(text, new string('z', 4096));
        Assert.Contains("按扩展名", Probe.Classify(text).Note, StringComparison.Ordinal);
    }
}
