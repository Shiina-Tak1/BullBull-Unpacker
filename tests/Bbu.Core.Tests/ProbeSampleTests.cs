using System;
using System.IO;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层真机档：拿仓外的真样本跑一遍识别与归因。合成夹具替代不了真字节。
///
/// 样本不在的机器上整类跳过（它是作者的下载目录，不是仓库的一部分）。
/// 这一类的另一半是引擎跑得动不动，在  里；这里只判"是什么"。
/// </summary>
[Trait("Category", "machine")]
[Collection("engine")]
public sealed class ProbeSampleTests
{
    private const string SampleName = "S000.rar.lz4";

    private static string Sample => Path.Combine(TestPaths.RealSampleRoot, SampleName);

    private static bool Available => File.Exists(Sample);

    /// <summary>
    /// B-2026-020：真样本是 lz4 帧包着一个 RAR5，识别层要认出 lz4 而且把它当候选。
    ///
    /// dll 随包之后能力档是"读得了"。内层 RAR5 仍然要密码，但那是密码层的事（R5），
    /// 与"引擎能不能读这个格式"是两回事 —— 这条只判后者。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-020")]
    public void RealSample_MustBeRecognizedAsLz4()
    {
        if (!Available)
        {
            return;
        }

        var format = Probe.DetectFormat(Sample);

        Assert.Equal(ArchiveFormat.Lz4, format);
        Assert.True(format.IsArchive());

        var classified = Probe.Classify(Sample);
        Assert.True(classified.IsCandidate);
        Assert.True(classified.ShallHandle);

        Assert.Equal(
            EngineAbility.CanRead,
            Probe.ReadabilityOf(format, TestPaths.BundledSevenZip));
    }

    /// <summary>
    /// 判据 16 的真字节对照（dll 随包之后的形态）：引擎真的读得了这个 lz4 帧包。
    ///
    /// dll 缺席时同一条命令报 `Cannot open encrypted archive. Wrong password?`，
    /// 与真密码错误逐字相同。
    /// 现在 dll 随包，退出码 0 加 `Everything is Ok` 且<b>不需要密码</b> ——
    /// lz4 这一层不碰内层 RA文件头。所以"读得了格式"与"解得开内容"是两件事，
    /// 后者要密码层（R5）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-020")]
    public async System.Threading.Tasks.Task RealSample_EngineMustReadTheLz4FrameWithoutPassword()
    {
        if (!Available)
        {
            return;
        }

        var engine = TestPaths.NewEngine();
        var result = await engine.TestAsync(Sample, kind: EngineKind.SevenZip);

        Assert.True(result.Ok, "引擎读不了这个 lz4 帧包：" + result.Output);
        Assert.False(result.WrongPassword, "不该被归到密码错误：" + result.Output);

        // 能力档与真跑一致：识别层说读得了，引擎就真的读得了。
        var ability = Probe.ReadabilityOf(Probe.DetectFormat(Sample), TestPaths.BundledSevenZip);
        Assert.Equal(EngineAbility.CanRead, ability);
        Assert.Equal(string.Empty, Probe.AbilityNote(ability));
    }
}
