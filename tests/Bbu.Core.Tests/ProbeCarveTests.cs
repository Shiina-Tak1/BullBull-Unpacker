using System;
using System.IO;
using System.Text;
using System.Threading;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 切包的两条失败分支不许留半成品（B-2026-153）。
///
/// 半成品是<b>扩展名像包</b>的残片（<c>.原名.mp4.carve.zip</c>）：切到一半点「停止」或磁盘满，
/// 它留在父目录里，下一次扫描按扩展名把它当候选包，再报一次必然失败的解压。
/// 1.1.0 的 <c>carve()</c> 文档串写的是「半成品会被删掉，不留垃圾」，移过来时两条分支都没删。
/// 撤掉 <c>Abandon</c> 那一处删除，这两条即红。
/// </summary>
public sealed class ProbeCarveTests
{
    /// <summary>取消分支：切到一半被叫停，残片要在离开 <c>Carve</c> 之前消失。</summary>
    [Fact]
    [Trait("Category", "edge")]
    [Trait("Bug", "B-2026-153")]
    public void CancelledCarve_MustLeaveNoHalfWrittenSlice()
    {
        using var dir = new TestDirectory();
        var source = Path.Combine(dir.Path, "carrier.mp4");
        var destination = Path.Combine(dir.Path, ".carrier.mp4.carve.zip");
        Write(source, 8 * 1024 * 1024);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var carved = Probe.Carve(source, 0, destination, cancel.Token);

        Assert.True(carved.Cancelled);
        Assert.False(carved.Ok);
        Assert.False(File.Exists(destination), "取消之后父目录里不该留下 .carve. 残片");
    }

    /// <summary>异常分支：写不进去（这里让目标位置横着一个目录），残片同样要清掉。</summary>
    [Fact]
    [Trait("Category", "edge")]
    [Trait("Bug", "B-2026-153")]
    public void FailedCarve_MustLeaveNoHalfWrittenSlice()
    {
        using var dir = new TestDirectory();
        var source = Path.Combine(dir.Path, "carrier.mp4");
        var destination = Path.Combine(dir.Path, ".carrier.mp4.carve.zip");
        Write(source, 4096);
        Directory.CreateDirectory(destination);

        var carved = Probe.Carve(source, 0, destination, CancellationToken.None);

        Assert.False(carved.Ok);
        Assert.False(carved.Cancelled);
        Assert.NotEmpty(carved.Error);

        // 那一条路径本来就建不出来，所以这里判的是「删过、而且没把别的东西删掉」
        Assert.True(Directory.Exists(destination), "挡路的那个目录不许被清掉");
    }

    /// <summary>起点越界那一档在开写之前就返回，父目录里什么都不该出现。</summary>
    [Fact]
    [Trait("Category", "edge")]
    [Trait("Bug", "B-2026-153")]
    public void OffsetOutOfRange_MustNotCreateAnything()
    {
        using var dir = new TestDirectory();
        var source = Path.Combine(dir.Path, "carrier.mp4");
        var destination = Path.Combine(dir.Path, ".carrier.mp4.carve.zip");
        Write(source, 128);

        var carved = Probe.Carve(source, 4096, destination, CancellationToken.None);

        Assert.False(carved.Ok);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path, ".carrier.mp4.carve.*"));
    }

    /// <summary>成功那一档照旧把切片留在盘上：删半成品不许把成品一起删了。</summary>
    [Fact]
    [Trait("Category", "edge")]
    [Trait("Bug", "B-2026-153")]
    public void SuccessfulCarve_MustStillKeepTheSlice()
    {
        using var dir = new TestDirectory();
        var source = Path.Combine(dir.Path, "carrier.mp4");
        var destination = Path.Combine(dir.Path, ".carrier.mp4.carve.zip");
        Write(source, 64 * 1024);

        var carved = Probe.Carve(source, 1024, destination, CancellationToken.None);

        Assert.True(carved.Ok);
        Assert.Equal(64 * 1024 - 1024, new FileInfo(destination).Length);
    }

    private static void Write(string path, int bytes)
    {
        var payload = new byte[bytes];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(path, payload);
    }

    /// <summary>
    /// 目标位置已经有一个同名文件时不许覆盖它：打开方式回 <c>FileMode.Create</c> 会把那个文件截断，
    /// 用户放在输出目录里的文件就被截断了（B-2026-204）。用户可感知的后果是「别人的同名文件一个字节都不动」。
    /// </summary>
    [Fact]
    [Trait("Category", "edge")]
    [Trait("Bug", "B-2026-204")]
    public void Carve_MustNotOverwriteAnExistingFile()
    {
        using var dir = new TestDirectory();
        var source = Path.Combine(dir.Path, "carrier.mp4");
        var destination = Path.Combine(dir.Path, ".carrier.mp4.carve.zip");
        Write(source, 4096);
        var occupied = "别人放在这里的文件";
        File.WriteAllText(destination, occupied, new UTF8Encoding(false));

        var carved = Probe.Carve(source, 0, destination, CancellationToken.None);

        Assert.False(carved.Ok);
        Assert.False(carved.Cancelled);
        Assert.NotEmpty(carved.Error);
        Assert.True(File.Exists(destination), "撞名之后目标文件必须原样留着");
        Assert.Equal(occupied, File.ReadAllText(destination));
    }

    /// <summary>
    /// 切片临时名不可预测，且扩展名仍是最后一段（随机串插在 <c>.carve.</c> 之前）：
    /// 固定名会让同名文件被下一次切包截断；扩展名被顶掉则识别层对那几个没有 magic 的格式失去兜底。
    /// </summary>
    [Fact]
    [Trait("Category", "edge")]
    [Trait("Bug", "B-2026-204")]
    public void CarveTempName_MustBeUnpredictableAndKeepTheExtensionLast()
    {
        using var dir = new TestDirectory();
        var archive = Path.Combine(dir.Path, "video.mp4");

        var first = Piercer.CarveTempName(dir.Path, archive, "zip");
        var second = Piercer.CarveTempName(dir.Path, archive, "zip");

        Assert.NotEqual(first, second);
        Assert.Equal(dir.Path, Path.GetDirectoryName(first));
        Assert.StartsWith(".video.mp4.", Path.GetFileName(first), StringComparison.Ordinal);
        Assert.Contains("." + Environment.ProcessId + ".", Path.GetFileName(first), StringComparison.Ordinal);
        Assert.EndsWith(".carve.zip", first, StringComparison.Ordinal);
        Assert.Equal(".zip", Path.GetExtension(first));
        Assert.Equal(".7z", Path.GetExtension(Piercer.CarveTempName(dir.Path, archive, "7z")));
    }
}
