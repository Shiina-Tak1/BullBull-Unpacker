using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 识别层的结构门禁：判断"这个文件是什么"只许有一处，而且不许按扩展名猜。
///
/// R2 收尾时引擎分派还是按扩展名临时猜的（<c>EngineRouter.ProvisionalFormatFromExtension</c>）。
/// 扩展名不可信，猜错的表现是"能解的说解不了"或"拿错引擎解"，所以本轮把那条路下线，
/// 并且不留一条能悄悄长回来的路。
/// </summary>
public sealed class RecognitionLayerTests
{
    /// <summary>判据 12：临时分派必须彻底下线，源码里不许留残留。</summary>
    [Fact]
    public void Src_MustNotContainProvisionalDispatch()
    {
        var hits = Repo.Files(Repo.Src, ".cs")
            .Where(file => File.ReadAllText(file).Contains("Provisional", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Repo.Root, file))
            .ToArray();

        Assert.Empty(hits);
    }

    /// <summary>
    /// 判据 12 的另一半：调用点都改走了按文件头判格式的那条路。
    ///
    /// 光有"没有 Provisional"不够：把调用点删掉、让它兜底成内置 7-Zip 也能过上一关。
    /// </summary>
    [Fact]
    public void EngineDispatch_MustGoThroughDetectFormat()
    {
        var router = Path.Combine(Repo.Src, "Bbu.Core", "Services", "EngineRouter.cs");
        var routerText = File.ReadAllText(router);
        Assert.Contains("Probe.DetectFormat", routerText, StringComparison.Ordinal);
        Assert.Contains("KindForPath", routerText, StringComparison.Ordinal);

        var engine = Path.Combine(Repo.Src, "Bbu.Core", "Services", "SevenZipEngine.cs");
        Assert.Contains("EngineRouter.KindForPath", File.ReadAllText(engine), StringComparison.Ordinal);

        // 界面不许出现引擎选择：路径先交给 Scan 出 Job（Kind 在那一层定），
        // 引擎选择全在 Core 内。界面里出现 EngineRouter 即绕开识别层。
        var strays = Repo.Files(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia"), ".cs")
            .Where(file => File.ReadAllText(file).Contains("EngineRouter", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Repo.Root, file))
            .ToArray();

        Assert.Empty(strays);
    }

    /// <summary>
    /// 识别层的结论只许来自文件头：<c>Probe.DetectFormat</c> 之外不许再有一处读 magic 的判据。
    /// 这条拦的是"在别处另写一份格式判断"。
    /// </summary>
    [Fact]
    public void MagicReading_MustLiveInTheProbeLayerOnly()
    {
        var owners = Repo.Files(Repo.Src, ".cs")
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                return name.StartsWith("Probe", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Contains("ProbeEmbedded.cs", owners);
        Assert.Contains("Probe.cs", owners);

        var strays = Repo.Files(Repo.Src, ".cs")
            .Where(file => !Path.GetFileName(file).StartsWith("Probe", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("Rar!\\x1a\\x07", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Repo.Root, file))
            .ToArray();

        Assert.Empty(strays);
    }
}
