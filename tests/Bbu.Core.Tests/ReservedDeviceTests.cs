using System.Globalization;
using System.IO;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// Windows 保留设备名的守卫与它的描述文案。
///
/// 这一层破了有两种结果，都很难查：漏判会让程序在读文件头时永久阻塞（取消与超时都没有机会
/// 执行），误判会把用户完全能读的 <c>CON.zip</c> 一类文件当成设备跳过。所以两侧都要断言。
/// </summary>
public sealed class ReservedDeviceTests
{
    /// <summary>名字命中保留设备的写法。判定规则见 <c>Probe.ReservedDeviceOf</c>。</summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("CON.tar.gz")]
    [InlineData("CON .zip")]
    [InlineData("CON.")]
    [InlineData("COM1")]
    [InlineData("COM¹")]
    [InlineData("LPT9.rar")]
    [InlineData("NUL.txt")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData(@"\\.\CON")]
    [InlineData(@"\\?\NUL.txt")]
    [Trait("Bug", "B-2026-001")]
    public void ReservedNames_MustBeRecognized(string name)
    {
        Assert.False(string.IsNullOrEmpty(Probe.ReservedDeviceOf(name)));
    }

    /// <summary>
    /// 名字不像保留设备的那一批。文档只列 <c>COM1</c> 至 <c>COM9</c> 与 <c>LPT1</c> 至 <c>LPT9</c>，
    /// 没有 <c>COM0</c> / <c>LPT0</c>；<c>CON</c> 只在整段名字上成立，不认前缀。
    /// </summary>
    [Theory]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("COM")]
    [InlineData("LPT")]
    [InlineData("COM10")]
    [InlineData("CONX")]
    [InlineData("console.txt")]
    [InlineData("contraband.zip")]
    [InlineData("AUX_")]
    [InlineData("NULa")]
    [InlineData("PRN1")]
    [InlineData("report..txt")]
    [InlineData("我的CON.zip")]
    [InlineData("资料CON.zip")]
    [Trait("Bug", "B-2026-001")]
    public void DeviceLikeNames_MustNotBeTakenForReserved(string name)
    {
        Assert.Null(Probe.ReservedDeviceOf(name));
    }

    /// <summary>
    /// 名字命中、但盘上真有这个文件 → 它<b>不是</b>设备路径，照常进入读路径。
    /// 现代 Windows 把「名字.扩展名」按普通文件处理；只看名字会把这一批全部误判成设备。
    /// </summary>
    [Theory]
    [InlineData("CON.zip")]
    [InlineData("COM1.zip")]
    [InlineData("NUL.txt")]
    [InlineData("LPT9.tar.gz")]
    [InlineData("AUX.7z")]
    [InlineData("PRN.rar")]
    [InlineData("CONIN$.zip")]
    [InlineData("CONOUT$.txt")]
    [Trait("Bug", "B-2026-001")]
    public void RealFilesWithReservedNames_MustNotBeTreatedAsDevices(string name)
    {
        using var dir = new TestDirectory();
        var path = dir.Combine(name);
        File.WriteAllText(path, string.Empty);

        Assert.NotNull(Probe.ReservedDeviceOf(path));
        Assert.False(Probe.IsDevicePath(path));
    }

    /// <summary>
    /// 判据只取路径最后一段：父目录叫 <c>CON</c> 与这个文件无关。
    /// 用户把包放在名为 <c>CON</c> 的文件夹里是完全正常的输入，不得被挡下。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-001")]
    public void AReservedNameInAParentFolder_MustNotBlockTheFile()
    {
        using var dir = new TestDirectory();
        var sub = Directory.CreateDirectory(dir.Combine("CON"));
        var path = Path.Combine(sub.FullName, "资料.zip");
        File.WriteAllText(path, string.Empty);

        Assert.Null(Probe.ReservedDeviceOf(path));
        Assert.False(Probe.IsDevicePath(path));
    }

    /// <summary>
    /// 挡在「读文件头」之前：这个路径根本不存在，若守卫失效就会落到「读不了」。
    /// 结论落在保留设备那一档，才说明它没进读路径 —— 真读设备会永久阻塞。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-001")]
    public void ReservedPath_MustBeStoppedBeforeAnyRead()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("CON");

        var result = Probe.Classify(path);

        Assert.Equal(ProbeStatus.ReservedDevice, result.Status);
        Assert.NotEqual(ProbeStatus.Unreadable, result.Status);
    }

    /// <summary>描述文案出自文案表，并且说清「为什么跳过」而不只是「跳过」。</summary>
    [Fact]
    [Trait("Bug", "B-2026-001")]
    public void DescribeReserved_MustSayWhyItIsSkipped()
    {
        var text = Probe.DescribeReserved("CON");

        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, ProbeText.ReservedDeviceDetailAt, "CON"), text);
        Assert.Contains("CON", text, System.StringComparison.Ordinal);
        Assert.Contains("保留设备名", text, System.StringComparison.Ordinal);
    }
}
