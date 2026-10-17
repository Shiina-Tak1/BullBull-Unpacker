using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 引擎探测。两个引擎**都只有随包一处** ——
/// 程序目录旁的 <c>engines\7z\7z.exe</c> 与 <c>engines\unrar\UnRAR.exe</c>，在场才算有这个引擎。
///
/// 2026-10-07 之前这里跟着 Python 版探测用户机器：WinRAR 的固定安装路径与
/// <c>SOFTWARE\WinRAR</c> 注册表、7-Zip 的配置值与系统安装目录、两者的 PATH 兜底。
/// 那一套整体撤掉了，理由是**自包含**（用户机器上装了什么与本产品无关）与**正确性**
/// （用户那份 stock 7-Zip 不是 ZS 变体，lz4 一族读不了，顶上来只会报 Unsupported Method）。
///
/// 用临时程序目录造场景，不去依赖本机安装了什么：否则断言会因本机安装情况变红，与代码无关。
/// </summary>
public sealed class EngineLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bbu-locator-" + Guid.NewGuid().ToString("N"));

    public EngineLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响断言
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    [Fact]
    public void BundledPaths_MustSitNextToProgramDirectory()
    {
        Assert.Equal(Path.Combine(_root, "engines", "7z", "7z.exe"), EngineLocator.BundledSevenZipAt(_root));
        Assert.Equal(Path.Combine(_root, "engines", "unrar", "UnRAR.exe"), EngineLocator.BundledUnRarAt(_root));
        Assert.Equal("engines", EngineLocator.EnginesFolderName);
        Assert.Equal("7z", EngineLocator.BundledFolderName);
        Assert.Equal("unrar", EngineLocator.UnRarFolderName);
        Assert.Equal("UnRAR.exe", EngineLocator.UnRarExeName);
    }

    [Fact]
    public void BundledCopies_MustBeFound()
    {
        var seven = Put(@"engines\7z\7z.exe");
        var unrar = Put(@"engines\unrar\UnRAR.exe");

        var paths = EngineLocator.Resolve(_root);

        Assert.Equal(seven, paths.SevenZip);
        Assert.Equal(unrar, paths.UnRar);
        Assert.True(paths.Has(EngineKind.SevenZip));
        Assert.True(paths.Has(EngineKind.UnRar));
        Assert.Equal(unrar, paths.PathOf(EngineKind.UnRar));
        Assert.Equal(seven, paths.PathOf(EngineKind.SevenZip));
    }

    /// <summary>
    /// 机器上那份 <c>Rar.exe</c> 摆到哪儿都不作数。这一条是 2026-10-07 撤掉 WinRAR 探测之后
    /// 的形态断言：程序目录里放一份同名的、再放一份在子目录里，结果仍然是「没有 rar 引擎」。
    /// </summary>
    [Fact]
    public void WinRarCopies_MustNotBePickedUp()
    {
        Put(@"Rar.exe");
        Put(@"WinRAR\Rar.exe");
        Put(@"Program Files\WinRAR\UnRAR.exe");

        var paths = EngineLocator.Resolve(_root);

        Assert.Null(paths.UnRar);
        Assert.False(paths.Has(EngineKind.UnRar));
    }

    /// <summary>
    /// 程序目录里什么都没有时如实报告没有引擎 —— 本机真装了 WinRAR 也一并不作数，
    /// 因为判据是「结果只可能来自程序目录旁那一份」，而不是「机器上找得到就行」。
    /// </summary>
    [Fact]
    public void MissingBundles_MustReportNoEngine()
    {
        var paths = EngineLocator.Resolve(_root);

        Assert.Null(paths.SevenZip);
        Assert.Null(paths.UnRar);
        Assert.False(paths.Has(EngineKind.SevenZip));
        Assert.False(paths.Has(EngineKind.UnRar));
    }

    /// <summary>缺一个不影响另一个：只有 7z 时 7-Zip 照常可用，rar 那一路报缺引擎。</summary>
    [Fact]
    public void PartialBundle_MustReportOnlyWhatIsThere()
    {
        var seven = Put(@"engines\7z\7z.exe");

        var paths = EngineLocator.Resolve(_root);

        Assert.Equal(seven, paths.SevenZip);
        Assert.True(paths.Has(EngineKind.SevenZip));
        Assert.Null(paths.UnRar);
        Assert.False(paths.Has(EngineKind.UnRar));
    }

    [Fact]
    public void MissingProgramDirectoryArgument_MustBeRejected()
    {
        Assert.Throws<ArgumentException>(() => EngineLocator.Resolve(string.Empty));
    }

    [Fact]
    public void EnginePaths_MustReportMissingEngine()
    {
        var paths = new EnginePaths(null, null);

        Assert.False(paths.Has(EngineKind.SevenZip));
        Assert.Null(paths.PathOf(EngineKind.UnRar));
    }

    /// <summary>
    /// 打包形态：三份外置组件要与包内清单逐份相符才算在场；对不上的那一份按「不用它」处理，
    /// 用户看到的是既有的缺引擎失败，而另一份不受牵连（B-2026-208）。
    /// 反向对照：撤掉校验、退回只看文件在不在，这一条即红（文件明明还在）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-208")]
    public void TamperedComponent_MustNotBeUsedEvenThoughTheFileIsStillThere()
    {
        var root = Package("tampered");
        var logs = new List<string>();
        var intact = EngineLocator.Resolve(root, new ChannelLog((_, message) => logs.Add(message)));

        Assert.Equal(Path.Combine(root, "engines", "7z", "7z.exe"), intact.SevenZip);
        Assert.Equal(Path.Combine(root, "engines", "unrar", "UnRAR.exe"), intact.UnRar);
        Assert.Empty(logs);

        // 改一个字节：文件名与位置都没变，变的只有哈希。
        FlipOneByte(Path.Combine(root, "engines", "7z", "7z.dll"));
        var afterSevenZip = EngineLocator.Resolve(root, new ChannelLog((_, message) => logs.Add(message)));

        Assert.Null(afterSevenZip.SevenZip);
        Assert.NotNull(afterSevenZip.UnRar);
        Assert.Equal(
            [string.Format(LogText.ComponentDigestDiffersAt, @"engines\7z\7z.dll")],
            logs);

        // 对不上的是 UnRAR 时，只有 rar 那一路报缺引擎。
        var other = Package("unrar-tampered");
        FlipOneByte(Path.Combine(other, "engines", "unrar", "UnRAR.exe"));
        var afterUnRar = EngineLocator.Resolve(other);

        Assert.NotNull(afterUnRar.SevenZip);
        Assert.Null(afterUnRar.UnRar);
    }

    /// <summary>
    /// 开发树与集成测试形态没有那份清单：跳过核对、程序照常启动，不把「没核」当作拒绝。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-208")]
    public void MissingManifest_MustSkipTheCheckInsteadOfRefusing()
    {
        var root = Package("no-manifest", manifest: false);
        var logs = new List<string>();

        var paths = EngineLocator.Resolve(root, new ChannelLog((_, message) => logs.Add(message)));

        Assert.NotNull(paths.SevenZip);
        Assert.NotNull(paths.UnRar);
        Assert.Equal([LogText.ComponentCheckSkipped], logs);
    }

    /// <summary>
    /// 组件在但读不了（被独占打开、权限不足）时按「对不上」处置：这一层不把异常抛给调用方
    /// 反向对照：撤掉哈希读取处的兜底，这条即红 —— 异常直接穿透 `Resolve`。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-208")]
    public void UnreadableComponent_MustBeTreatedAsMismatchNotAsAnException()
    {
        var root = Package("locked");
        var logs = new List<string>();

        using (File.Open(Path.Combine(root, @"engines\7z\7z.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var paths = EngineLocator.Resolve(root, new ChannelLog((_, message) => logs.Add(message)));

            Assert.Null(paths.SevenZip);
            Assert.NotNull(paths.UnRar);
            Assert.Equal([string.Format(LogText.ComponentDigestDiffersAt, @"engines\7z\7z.dll")], logs);
        }
    }

    /// <summary>造一份便携包形态的程序目录：三份组件照抄仓内随包那份，清单按实际字节写。</summary>
    private string Package(string name, bool manifest = true)
    {
        var root = Path.Combine(_root, name);
        foreach (var relative in Shipped)
        {
            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Source(relative), target);
        }

        if (manifest)
        {
            var rows = Shipped.Select(
                relative => $"{Sha256(Path.Combine(root, relative))}  {relative.Replace('\\', '/')}");
            var sums = Path.Combine(root, "SHA256SUMS");
            File.WriteAllLines(sums, rows, new UTF8Encoding(false));
        }

        return root;
    }

    private static string Source(string relative) => relative switch
    {
        @"engines\7z\7z.exe" => TestPaths.BundledSevenZip,
        @"engines\7z\7z.dll" => Path.Combine(TestPaths.BundleFolder, "7z.dll"),
        _ => TestPaths.BundledUnRar,
    };

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void FlipOneByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>外置引擎那三份，与本批的核对面同源。</summary>
    private static readonly string[] Shipped =
    [
        @"engines\7z\7z.exe",
        @"engines\7z\7z.dll",
        @"engines\unrar\UnRAR.exe",
    ];

    private string Put(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x4d, 0x5a]);
        return full;
    }
}
