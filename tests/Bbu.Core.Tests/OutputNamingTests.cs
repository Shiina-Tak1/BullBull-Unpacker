using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 产物目录名字本身：去「删」字、剥站点噪声、归一化的边界，以及名字算出来的那两条判据。
///
/// 重名冲突三档在 （档）：它挂的是 号。
/// </summary>
public sealed class OutputNamingTests
{
    /// <summary>判据 2：去「删」字逐例。取样自 1.1.0 的写法与它的注释。</summary>
    [Theory]
    [InlineData("学习资料.z删 i删 p删", "学习资料.zip")]
    [InlineData("学习资料.z删i删p删", "学习资料.zip")]
    [InlineData("资料.7删 z", "资料.7z")]
    [InlineData("删.zip", ".zip")]
    [InlineData("学习资料.zip", "学习资料.zip")]
    public void CleanDeleteChars_MustMatchTheRealExamples(string input, string expected)
    {
        Assert.Equal(expected, OutputNaming.CleanDeleteChars(input));
    }

    /// <summary>判据 2 的另一半：剥掉伪装扩展名之后才是产物目录名。</summary>
    [Theory]
    [InlineData("学习资料.z删 i删 p删", "学习资料")]
    [InlineData("1067.mp4", "1067")]
    [InlineData("x.zip", "x")]
    [InlineData("无扩展名包", "无扩展名包")]
    public void StemFor_MustStripTheDisguisedExtension(string name, string expected)
    {
        Assert.Equal(expected, OutputNaming.StemFor(name));
    }

    /// <summary>
    /// 第二次剥只剥认得出来的扩展名（包扩展名或伪装白名单里的一个）。
    /// 版本号、尺寸、分辨率都不是扩展名，不许当成伪装后缀一起剥掉 ——
    /// 撤掉那次判据（改回无判据地再剥一层）即红。带 ★ 的三条取自现场样本与真样本。
    /// </summary>
    [Theory]
    [InlineData("1.0.zip", "1.0")]
    [InlineData("1.2.3.zip", "1.2.3")]
    [InlineData("v1.0.zip", "v1.0")]
    [InlineData("111.222.mp4", "111.222")]      // ★ 现场样本
    [InlineData("movie.2024.mp4", "movie.2024")]
    [InlineData("[SLG官中] 45号电车 1.0.6.3+存档.mp4", "[SLG官中] 45号电车 1.0.6.3+存档")]  // ★ 现场样本
    [InlineData("S000.rar.lz4", "S000")]        // ★ 真样本：两层都是包后缀，仍然剥到 S000
    [InlineData("x.tar.gz", "x")]
    [InlineData("xxx.zip.mp4", "xxx")]
    public void StemFor_MustStripOnlyKnownExtensions(string name, string expected)
    {
        Assert.Equal(expected, OutputNaming.StemFor(name));
    }

    /// <summary>
    /// 算出来的目录名要能真的落盘：非法字符、保留设备名、结尾的点与空格三样都会让 Windows
    /// 把名字改写成另一个。撤掉那一次清洗即红； B-2026-157。
    /// </summary>
    [Theory]
    [Trait("Bug", "B-2026-157")]
    [InlineData("a..zip", "a")]
    [InlineData("xy..zip", "xy")]
    [InlineData("aa...zip", "aa")]
    [InlineData("a .zip", "a")]
    [InlineData("a<b.zip", "ab")]
    [InlineData("a|b.zip", "ab")]
    [InlineData("a?b.zip", "ab")]
    [InlineData("a*b.zip", "ab")]
    [InlineData("a\"b.zip", "ab")]
    [InlineData("CON.zip", "CON_unpack")]
    [InlineData("COM1.7z", "COM1_unpack")]
    [InlineData("..zip", "_unpack")]
    [InlineData("啊？...艾草2？.7z", "啊？...艾草2？")]
    public void StemFor_MustProduceALegalWindowsName(string name, string expected)
    {
        Assert.Equal(expected, OutputNaming.StemFor(name));
    }

    /// <summary>
    /// 名字超过保留长度上限时保留开头裁短：NTFS 的单段上限 255 减去错开后缀（7）
    /// 与冲突序号（6）得到 242，所以 243 起才裁、裁到 242。撤掉裁法即红（B-2026-072）。
    ///
    /// 256 个字符的单段名在 NTFS 上不存在，所以这一条只构造字符串、不落盘 —— 它判的正是
    /// 「算出来的目录名合法」，与盘上有没有那个包无关。
    /// </summary>
    [Theory]
    [InlineData(242, 242)]
    [InlineData(243, 242)]
    [InlineData(256, 242)]
    [InlineData(300, 242)]
    public void StemFor_MustTrimTheOverlongNameKeepingItsHead(int length, int expected)
    {
        var name = new string('长', length) + ".zip";

        var stem = OutputNaming.StemFor(name);

        Assert.Equal(expected, stem.Length);
        Assert.Equal(new string('长', expected), stem);
    }

    /// <summary>裁短之后仍要清结尾的点与空格：截断点正好落在它们前面时，名字会带着它们落盘。</summary>
    [Fact]
    public void StemFor_MustCleanTheEndAfterTrimming()
    {
        var name = new string('长', 241) + " " + new string('长', 20) + ".zip";

        var stem = OutputNaming.StemFor(name);

        Assert.Equal(241, stem.Length);
        Assert.EndsWith("长", stem, StringComparison.Ordinal);
    }

    /// <summary>
    /// 裁短要在日志里说出来（裁定）：用户看到的目录名与包名对不上时，
    /// 只有日志能告诉他为什么。撤掉那条日志即红。
    /// </summary>
    [Fact]
    public void Resolve_MustLogTheTrimmedName()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine(new string('长', 256) + ".zip");

        var levels = new List<LogLevel>();
        var lines = new List<string>();
        var naming = OutputNaming.Resolve(
            new OutputDirRequest(archive),
            new ChannelLog((level, message) =>
            {
                levels.Add(level);
                lines.Add(message);
            }));

        var name = Assert.IsType<string>(naming.Directory);
        Assert.Equal(242, Path.GetFileName(name).Length);
        var line = Assert.Single(lines);
        Assert.Equal(
            string.Format(
                CultureInfo.InvariantCulture,
                NamingText.LongNameTrimmedAt,
                new string('长', 256),
                new string('长', 242)),
            line);
        // 自动截断算不算「用户该知道」：裁 Warn（灰 → 琥珀），不再等于文案表的域默认。
        Assert.Equal(LogLevel.Warn, Assert.Single(levels));
    }

    /// <summary>判据 1：分卷包取归组主卷的 base，产物目录不进 <c>.001</c> 那一层。</summary>
    [Theory]
    [InlineData("x.part1.rar", "x")]
    [InlineData("x.7z.001", "x")]
    [InlineData("x.z01", "x")]
    public void SplitVolumePackages_MustNameTheirDirectoryAfterTheGroupBase(string name, string expected)
    {
        Assert.Equal(expected, OutputNaming.StemFor(name));
    }

    /// <summary>
    /// 判据 3：剥下载站标识与结尾的副本编号。样例取自 <c>probe.py</c> 注释里列的那几种写法
    /// （<c>[xxx.com]</c> / <c>{www.aaa.net}</c> / <c>【某某论坛】</c> / <c>(1)</c> / <c>_1</c>）。
    /// </summary>
    [Theory]
    [InlineData("学习资料[xxx.com].zip", "学习资料.zip")]
    [InlineData("学习资料{www.aaa.net}.zip", "学习资料.zip")]
    [InlineData("学习资料【某某论坛】.zip", "学习资料.zip")]
    [InlineData("学习资料 (1).zip", "学习资料.zip")]
    [InlineData("学习资料_1.zip", "学习资料.zip")]
    [InlineData("学习资料【社区】[www.b.cc]_2.zip", "学习资料.zip")]
    public void StripSiteNoise_MustRemoveTheSiteNamesAndTheCopyNumber(string input, string expected)
    {
        Assert.Equal(expected, OutputNaming.StripSiteNoise(input));
    }

    /// <summary>
    /// 判据 4：归一化只服务「按文件名匹配」，它不改变落盘用的目录名。
    /// 两者混用会把用户目录名改花 —— 全角括号被转成半角、站名与空格被抹掉。
    ///
    /// 归一化那一边的取值照抄 1.1.0 的标点表：它去空白、去站名、全角转半角，
    /// 但半角括号不在它那张表里，所以结果留着括号。
    /// </summary>
    [Fact]
    public void NormalizeForMatch_MustStayOutOfTheOnDiskName()
    {
        using var dir = new TestDirectory();
        const string name = "学习资料 （合集）【某某论坛】.zip";
        var archive = dir.Combine(name);
        File.WriteAllText(archive, string.Empty);

        var naming = OutputNaming.Resolve(new OutputDirRequest(archive));

        Assert.Equal("学习资料(合集)", OutputNaming.NormalizeForMatch(name));
        Assert.Equal("学习资料 （合集）【某某论坛】", Path.GetFileName(naming.Directory));
    }

    /// <summary>
    /// 判据 5：判据是「路径自撞」而不是「有没有扩展名」，两种口径对这两个名字会一个漏判一个误判。
    ///
    /// <c>.zip</c>：.NET 的取主名函数返回空串，而 Python 的 <c>splitext</c> 保留整名；
    /// <c>a.</c>：路径规范化会把结尾的点去掉，于是它和同目录下一个叫 <c>a</c> 的文件是同一处。
    /// 两条都只有按「算出来的路径与源包是不是同一个」判才错得开。
    /// </summary>
    [Theory]
    [InlineData(".zip")]
    [InlineData("a.")]
    public void PathSelfCollision_MustBeJudgedByThePathNotByTheExtension(string name)
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine(name);
        File.WriteAllText(archive, string.Empty);

        var naming = OutputNaming.Resolve(new OutputDirRequest(archive));

        Assert.NotEqual(Path.GetFileName(archive), Path.GetFileName(naming.Directory));
        Assert.EndsWith("_unpack", Path.GetFileName(naming.Directory), StringComparison.Ordinal);
    }

    /// <summary>基准目录三档的顺序：<c>parent</c> 优先于 <c>output_root</c>，两者都没给才用源包同级。</summary>
    [Fact]
    public void BaseDirectory_MustPreferParentThenOutputRootThenTheSourceFolder()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        var parent = dir.Combine("上层");
        var root = dir.Combine("用户的输出目录");
        Directory.CreateDirectory(parent);

        var byParent = OutputNaming.Resolve(
            new OutputDirRequest(archive, Parent: parent, OutputRoot: root));
        var byRoot = OutputNaming.Resolve(new OutputDirRequest(archive, OutputRoot: root));
        var bySource = OutputNaming.Resolve(new OutputDirRequest(archive));

        Assert.Equal(parent, Path.GetDirectoryName(byParent.Directory));
        Assert.Equal(root, Path.GetDirectoryName(byRoot.Directory));
        Assert.Equal(dir.Path, Path.GetDirectoryName(bySource.Directory));
    }

    /// <summary>
    /// B-2026-068：<c>output_root</c> 指向一个文件时不得改写那个文件，也不许抛异常。
    /// 回退到源包同级是已知取舍；把那个文件当成目录写进去才是数据损坏。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-068")]
    public void OutputRootPointingAtAFile_MustFallBackWithoutTouchingIt()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        var blocker = dir.Combine("占位文件");
        File.WriteAllText(blocker, "原始内容");

        var naming = OutputNaming.Resolve(new OutputDirRequest(archive, OutputRoot: blocker));

        Assert.Null(naming.BlockedBy);
        Assert.Equal(dir.Path, Path.GetDirectoryName(naming.Directory));
        Assert.Equal("原始内容", File.ReadAllText(blocker));
    }
}
