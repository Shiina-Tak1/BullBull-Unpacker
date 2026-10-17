using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 识别层 档：RAR5 卷头的长度字段要有上限，变长整数要有界。
///
/// 这一档破了的后果不是报错，而是把原因报错：畸形卷头让
/// <c>pos = after + size</c> 溢出，随后按负数下标读缓冲，异常穿出识别层，
/// 用户看到的是「意外错误」，而真实结论「缺分卷」被顶掉。
/// 样本就地生成，不入库（分卷夹具只借 <c>vol-part</c> 与 <c>vol-oldrar</c> 的首卷）。
/// </summary>
[Trait("Category", "ledger")]
[Collection("engine")]
public sealed class ProbeVolumeHeadTests
{
    /// <summary>RAR5 签名 + 4 字节 CRC 占位 + 值域上界的长度 vint（<c>FF FF FF FF 07</c>）。
    /// 它的值加上推进量会溢出成负数，解析器于是回头读负数下标的字节。</summary>
    private static byte[] OverflowHead()
    {
        var bytes = new byte[19];
        Rar5Prefix().CopyTo(bytes, 0);
        byte[] size = [0xFF, 0xFF, 0xFF, 0xFF, 0x07];
        size.CopyTo(bytes, 12);
        return bytes;
    }

    /// <summary>长度 vint 用满 6 个字节（第 5 字节仍带续位）。它在旧写法里被折成 8，于是第一次
    /// 迭代落到偏移 26，把那里的一段填充读成一个「还有下一卷」的 FILE 头。</summary>
    private static byte[] LongVintHead()
    {
        var bytes = new byte[40];
        Rar5Prefix().CopyTo(bytes, 0);
        byte[] size = [0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        size.CopyTo(bytes, 12);
        bytes[18] = 0x00;
        bytes[30] = 0x01;
        bytes[31] = 0x02;
        bytes[32] = 0x10;
        return bytes;
    }

    /// <summary>RAR5 签名与 CRC 占位一共 12 字节；长度字段从偏移 12 起。</summary>
    private static byte[] Rar5Prefix()
    {
        var bytes = new byte[12];
        "Rar!\x1a\x07\x01\x00"u8.ToArray().CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>畸形卷头不许把「缺分卷」报成意外错误：两条样本都如实返回「判不出来」。</summary>
    /// <remarks>
    /// 第一条是核实结论里那一份（19 字节）。第二条钉的是变长整数的字节数上限：
    /// 撤销上限之后它会被读成一个「还有下一卷」的头，于是这里返回一个不存在的 <c>x.r00</c> 之后的名字。
    /// </remarks>
    [Fact]
    [Trait("Bug", "B-2026-198")]
    public void MalformedVolumeHead_MustAnswerInsteadOfThrowing()
    {
        using var dir = new TestDirectory();

        var part = Path.Combine(dir.Combine("part"), "x.part1.rar");
        Directory.CreateDirectory(dir.Combine("part"));
        File.Copy(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")), part);
        File.WriteAllBytes(Path.Combine(dir.Combine("part"), "x.part2.rar"), OverflowHead());

        var oldrar = Path.Combine(dir.Combine("oldrar"), "x.rar");
        Directory.CreateDirectory(dir.Combine("oldrar"));
        File.Copy(TestPaths.Probe(Path.Combine("vol-oldrar", "x.rar")), oldrar);
        File.WriteAllBytes(Path.Combine(dir.Combine("oldrar"), "x.r00"), LongVintHead());

        // 溢出那一份：撤销长度上限即从 read-only 的下标读出去，异常一路穿到穿透层。
        Assert.Null(Probe.MissingVolumeNote(part));
        Assert.Null(Probe.MissingVolumeNote(oldrar));
    }

    /// <summary>任意输入都返回一个值：一组结构不对的卷头逐个过识别层，一个都不许抛。</summary>
    [Fact]
    [Trait("Bug", "B-2026-198")]
    public void ArbitraryVolumeHead_MustAlwaysReturnAValue()
    {
        byte[][] heads =
        [
            OverflowHead(),
            LongVintHead(),
            Rar5Prefix(),
            [.. Rar5Prefix(), .. Enumerable.Repeat((byte)0xFF, 20)],
            [.. Rar5Prefix(), 0x00],
            [.. Rar5Prefix(), 0x7F, 0x00, 0x00, 0x00],
            Enumerable.Repeat((byte)0xFF, 256).ToArray(),
        ];

        using var dir = new TestDirectory();
        var index = 0;
        foreach (var head in heads)
        {
            var folder = dir.Combine("heads", index++.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var first = Path.Combine(folder, "x.part1.rar");
            File.Copy(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")), first);
            File.WriteAllBytes(Path.Combine(folder, "x.part2.rar"), head.Length == 0 ? [0x00] : head);

            Assert.Null(Probe.MissingVolumeNote(first));
            Assert.Null(Probe.MissingVolumeNote(Path.Combine(folder, "x.part2.rar")));
        }
    }

    /// <summary>端到端：畸形卷头不再让异常穿出识别层，卷真的缺席时「分卷不全」照旧报得出来。</summary>
    /// <remarks>
    /// 驱动的是试密码入口 <c>Vault.UnlockAsync</c> —— 识别层的缺卷判定就在它里面两处调用，
    /// 修前那个越界异常从这里穿到穿透层的兜底 catch，用户看到的是「意外错误」。
    /// 结论由引擎给：畸形中间卷实测报「数据损坏」与「引擎无法读取该压缩包」，都不是「分卷不全」，
    /// 那一句只在卷缺席时给（第三条样本）。两种结论分得开才算如实。
    /// </remarks>
    [Fact]
    [Trait("Bug", "B-2026-198")]
    public async Task MalformedVolumeHead_MustNotEndAsUnexpectedFailure()
    {
        using var dir = new TestDirectory();

        // 样本一：RAR5 首卷 ＋ 19 字节的畸形次卷（溢出那一份）。
        var part = dir.Combine("part");
        Directory.CreateDirectory(part);
        File.Copy(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")), Path.Combine(part, "x.part1.rar"));
        File.WriteAllBytes(Path.Combine(part, "x.part2.rar"), OverflowHead());

        // 样本二：老式 `.rar` 首卷 ＋ 字节数超限的畸形 `.r00`。
        var oldrar = dir.Combine("oldrar");
        Directory.CreateDirectory(oldrar);
        File.Copy(TestPaths.Probe(Path.Combine("vol-oldrar", "x.rar")), Path.Combine(oldrar, "x.rar"));
        File.WriteAllBytes(Path.Combine(oldrar, "x.r00"), LongVintHead());

        foreach (var (path, label) in new[]
                 {
                     (Path.Combine(part, "x.part1.rar"), "畸形 part2"),
                     (Path.Combine(oldrar, "x.rar"), "畸形 r00"),
                 })
        {
            var unlocked = await UnlockAsync(path);

            // 结论必须落在引擎给的如实归因上，而不是「意外错误」——
            // 修前这里返回不了：异常从识别层穿上去，落到穿透层的兜底 catch。
            Assert.NotEqual(UnlockProblem.MissingVolume, unlocked.Problem);
            Assert.DoesNotContain("意外错误", unlocked.StoppedReason ?? string.Empty, StringComparison.Ordinal);
        }

        // 样本三（对照）：卷真的缺席时「分卷不全」照旧报得出来。
        var gap = dir.Combine("gap");
        Directory.CreateDirectory(gap);
        File.Copy(TestPaths.Probe(Path.Combine("vol-part", "x.part1.rar")), Path.Combine(gap, "x.part1.rar"));
        File.Copy(TestPaths.Probe(Path.Combine("vol-part", "x.part3.rar")), Path.Combine(gap, "x.part3.rar"));

        var gapUnlock = await UnlockAsync(Path.Combine(gap, "x.part1.rar"));
        Assert.Equal(UnlockProblem.MissingVolume, gapUnlock.Problem);
        Assert.Contains("分卷不全", gapUnlock.StoppedReason ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("x.part2.rar", gapUnlock.StoppedReason ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>走试密码入口，用的装配与生产一致：随包引擎 ＋ 空密码本。</summary>
    private static async Task<UnlockResult> UnlockAsync(string archive)
    {
        var dir = Path.GetDirectoryName(archive)!;
        var vault = new Vault { BookPath = Path.Combine(dir, VaultText.BookFileName) };
        vault.SetEntries(Array.Empty<string>());
        return await vault.UnlockAsync(TestPaths.NewRarEngine(), archive);
    }
}
