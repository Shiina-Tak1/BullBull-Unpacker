using System;
using System.IO;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 伪装包与快路的关系：快路认不认伪装文件、认不认切出来的那个包。
///
/// </summary>
public sealed class CarrierFastPathTests
{
    private static string Vault(string name) => Path.Combine(TestPaths.Fixtures, "vault", name);

    private const string Password = "BbuPw2026";

    /// <summary>
    /// 伪装文件本身**不进快路**：垫片挡在包前面，头 8 字节不是任何一种 magic。
    /// 这不是缺陷 —— 快路只负责"这个密码肯定不对"，认不出就回「不知道」，代价是走引擎。
    /// </summary>
    [Fact]
    public void DisguisedFile_Itself_MustNotEnterTheFastPath()
    {
        var carrier = Vault("carrier-tail.mp4");

        // 前提自检：这个文件确实是被垫片伪装的，不是裸包。
        Assert.Equal(ArchiveFormat.Unknown, Probe.DetectFormat(carrier));

        Assert.Null(new CompositeFastPath().Open(carrier));
    }

    /// <summary>
    /// 切出内嵌包之后，**那一份**进得了快路，判据与普通包一致。
    ///
    /// 穿透试密码传的是切出来的临时文件（`PiercePassword` 传给 `UnlockAsync` 的是 `source`），
    /// 所以伪装包在穿透那条路上照样吃得到快路 —— 前提是先认出内嵌包并切出来。
    /// </summary>
    [Fact]
    public void CarvedPackage_MustEnterTheFastPathWithTheSameJudgementAsAPlainOne()
    {
        var carrier = Vault("carrier-tail.mp4");
        var embedded = Probe.FindEmbedded(carrier);
        Assert.NotNull(embedded);
        Assert.Equal(ArchiveFormat.Zip, embedded!.Format);

        var temp = Path.Combine(Path.GetTempPath(), "bbu-carve-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var carved = Probe.Carve(carrier, embedded.Offset, temp, default);
            Assert.True(carved.Ok, "切包失败：" + carved.Error);

            using var session = new CompositeFastPath().Open(temp);
            Assert.NotNull(session);

            // 正确密码不许被判死；错密码要判得出来（说明这条路真的在判，不是整轮回「不知道」）。
            Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
            Assert.Equal(FastPathVerdict.ProbablyWrong, session.Judge("definitely-not-it"));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// 绝对偏移布局的伪装包**本身**也要进快路（B-2026-144）。
    ///
    /// 这一档穿透层**不切包**：`PierceExtract` 见到 `probe.Direct`（7-Zip 能直接读它）就把
    /// 伪装包自己交给试密码，于是快路拿到的就是伪装包路径 —— 而它的头是垫片，
    /// 按 magic 分派必然回 <c>null</c>，每个候选都要起一次引擎。可它的中央目录与本地头偏移
    /// 都是**绝对位置**，直接读伪装包就对，所以分派里补一条「尾部是不是绝对偏移布局」。
    /// 夹具就地合成：拿 `zip-aes256.zip` 把每处偏移加上垫片长度，再前置一段假 mp4 头。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-144"), Trait("Category", "ledger")]
    public void AbsoluteLayoutCarrier_Itself_MustEnterTheFastPath()
    {
        using var dir = new TestDirectory();
        var carrier = AbsoluteLayoutCarrier(dir);

        // 前提自检：头认出来是 mp4（不是任何一种包），且确实是绝对偏移布局 ——
        // 两条都不成立就测错了对象。仓外那份真样本也是这个形态。
        Assert.Equal(ArchiveFormat.Mp4, Probe.DetectFormat(carrier));
        Assert.True(Probe.HasZipAbsoluteLayout(carrier));

        using var session = new CompositeFastPath().Open(carrier);
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
        Assert.Equal(FastPathVerdict.ProbablyWrong, session.Judge("definitely-not-it"));
    }

    /// <summary>
    /// ZIP64 的绝对偏移布局伪装包**本身**也要进快路（B-2026-145）。
    ///
    /// 穿透层照样判它直读，所以快路拿到的还是伪装包本身；而快路原先只解 32 位 EOCD，
    /// 读到 <c>0xFFFFFFFF</c> 就拿它当偏移去 seek，整轮回 <c>null</c>。仓外那份 6.6 GB 的
    /// 真样本正是这一档，里面有一条真 AES 条目。
    /// </summary>
    /// <param name="placeholderSize">经典 EOCD 的「中央目录大小」写占位符（常见写包器）还是写真值（真样本那种）。</param>
    [Theory, Trait("Bug", "B-2026-145"), Trait("Category", "ledger")]
    [InlineData(true)]
    [InlineData(false)]
    public void Zip64AbsoluteLayoutCarrier_Itself_MustEnterTheFastPath(bool placeholderSize)
    {
        using var dir = new TestDirectory();
        var carrier = Zip64AbsoluteLayoutCarrier(dir, placeholderSize);

        // 前提自检：头认出来是 mp4，且穿透层判得出来是绝对偏移布局 —— 不成立就测错了对象。
        Assert.Equal(ArchiveFormat.Mp4, Probe.DetectFormat(carrier));
        Assert.True(Probe.HasZipAbsoluteLayout(carrier));

        using var session = new CompositeFastPath().Open(carrier);
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
        Assert.Equal(FastPathVerdict.ProbablyWrong, session.Judge("definitely-not-it"));
    }

    /// <summary>
    /// 把 vault 的 AES 夹具升成 ZIP64 的「假 mp4 头 + 绝对偏移布局」伪装包。
    /// </summary>
    /// <remarks>
    /// 升法的要点：中央目录每一条的 32 位本地头偏移改写成占位符，真值塞进 ZIP64 扩展字段
    /// （id 0x0001，此时它只装这一个 8 字节值），末尾补齐 ZIP64 EOCD、locator 与经典 EOCD。
    /// 垫片取 1.3 MB 的理由见 <see cref="AbsoluteLayoutCarrier"/>。
    /// </remarks>
    private static string Zip64AbsoluteLayoutCarrier(TestDirectory dir, bool placeholderSize)
    {
        const int Pad = 1_300_000;
        var bytes = File.ReadAllBytes(Vault("zip-aes256.zip"));

        var eocd = FindEocd(bytes);
        Assert.True(eocd > 0, "夹具里找不到 EOCD");
        var centralAt = (int)BitConverter.ToUInt32(bytes, eocd + 16);
        var count = BitConverter.ToUInt16(bytes, eocd + 10);

        // 中央目录重写一遍：每条后面接一个只装本地头偏移的 ZIP64 扩展字段。
        using var rewritten = new MemoryStream();
        var at = centralAt;
        for (var i = 0; i < count; i++)
        {
            var nameLength = BitConverter.ToUInt16(bytes, at + 28);
            var extraLength = BitConverter.ToUInt16(bytes, at + 30);
            var commentLength = BitConverter.ToUInt16(bytes, at + 32);
            var localAt = BitConverter.ToUInt32(bytes, at + 42);

            var head = bytes[at..(at + 46 + nameLength)];
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(head, 42);
            BitConverter.GetBytes((ushort)(extraLength + 12)).CopyTo(head, 30);

            var extra = new byte[extraLength + 12];
            bytes.AsSpan(at + 46 + nameLength, extraLength).CopyTo(extra);
            extra[extraLength] = 0x01;
            extra[extraLength + 2] = 0x08;
            BitConverter.GetBytes((long)localAt + Pad).CopyTo(extra, extraLength + 4);

            rewritten.Write(head);
            rewritten.Write(extra);
            rewritten.Write(bytes, at + 46 + nameLength + extraLength, commentLength);
            at += 46 + nameLength + extraLength + commentLength;
        }

        var central = rewritten.ToArray();
        var centralAbs = (long)Pad + centralAt;
        var zip64At = centralAbs + central.Length;

        using var output = new MemoryStream();

        // 前置假 mp4 头：真样本那批伪装包的垫片都是 MB 级，这里同形。
        output.Write(new byte[Pad]);
        output.Write(bytes, 0, centralAt);
        output.Write(central);
        output.Write(Zip64End(central.Length, centralAbs, count));
        output.Write(Zip64Locator(zip64At));
        output.Write(ClassicEnd(central.Length, placeholderSize));

        var shifted = output.ToArray();
        byte[] ftyp = [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70];
        ftyp.CopyTo(shifted, 0);

        var path = dir.Combine($"zip64-carrier-{placeholderSize}.mp4");
        File.WriteAllBytes(path, shifted);
        return path;
    }

    /// <summary>ZIP64 EOCD：中央目录的 8 字节偏移与大小只在这里。</summary>
    private static byte[] Zip64End(long centralSize, long centralAt, int count)
    {
        var record = new byte[56];
        byte[] signature = [0x50, 0x4b, 0x06, 0x06];
        signature.CopyTo(record, 0);
        BitConverter.GetBytes(44L).CopyTo(record, 4);
        BitConverter.GetBytes((ushort)45).CopyTo(record, 12);
        BitConverter.GetBytes((ushort)45).CopyTo(record, 14);
        BitConverter.GetBytes((long)count).CopyTo(record, 24);
        BitConverter.GetBytes((long)count).CopyTo(record, 32);
        BitConverter.GetBytes(centralSize).CopyTo(record, 40);
        BitConverter.GetBytes(centralAt).CopyTo(record, 48);
        return record;
    }

    private static byte[] Zip64Locator(long zip64At)
    {
        var locator = new byte[20];
        byte[] signature = [0x50, 0x4b, 0x06, 0x07];
        signature.CopyTo(locator, 0);
        BitConverter.GetBytes(zip64At).CopyTo(locator, 8);
        BitConverter.GetBytes(1).CopyTo(locator, 16);
        return locator;
    }

    private static byte[] ClassicEnd(long centralSize, bool placeholderSize)
    {
        var classic = new byte[22];
        byte[] signature = [0x50, 0x4b, 0x05, 0x06];
        signature.CopyTo(classic, 0);
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(classic, 10);
        BitConverter.GetBytes(placeholderSize ? 0xFFFFFFFFu : (uint)centralSize).CopyTo(classic, 12);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(classic, 16);
        return classic;
    }

    /// <summary>把 vault 的 AES 夹具改成「假 mp4 头 + 绝对偏移布局」的伪装包。</summary>
    /// <remarks>
    /// 垫片取 1.3 MB：文件总长要过 `Probe.EmbedMinSize` 这道闸，
    /// 垫片小于该值时整份文件都过不了闸。
    /// </remarks>
    private static string AbsoluteLayoutCarrier(TestDirectory dir)
    {
        const int Pad = 1_300_000;
        var bytes = File.ReadAllBytes(Vault("zip-aes256.zip"));

        var eocd = FindEocd(bytes);
        Assert.True(eocd > 0, "夹具里找不到 EOCD");
        var centralAt = (int)BitConverter.ToUInt32(bytes, eocd + 16);
        var count = BitConverter.ToUInt16(bytes, eocd + 10);

        var shifted = new byte[Pad + bytes.Length];
        bytes.CopyTo(shifted, Pad);

        // 中央目录每一条的「本地头偏移」，以及 EOCD 里的「中央目录偏移」，都加上垫片长度。
        var at = centralAt;
        for (var i = 0; i < count; i++)
        {
            var localAt = (int)BitConverter.ToUInt32(bytes, at + 42);
            BitConverter.GetBytes(localAt + Pad).CopyTo(shifted, Pad + at + 42);
            at += 46
                + BitConverter.ToUInt16(bytes, at + 28)
                + BitConverter.ToUInt16(bytes, at + 30)
                + BitConverter.ToUInt16(bytes, at + 32);
        }

        BitConverter.GetBytes(centralAt + Pad).CopyTo(shifted, Pad + eocd + 16);

        // 前 8 字节写成 mp4 的 ftyp 头，与真样本同形（这样它才是个「伪装包」）。
        byte[] ftyp = [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70];
        ftyp.CopyTo(shifted, 0);

        var path = dir.Combine("abs-carrier.mp4");
        File.WriteAllBytes(path, shifted);
        return path;
    }

    private static int FindEocd(byte[] bytes)
    {
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
            {
                return i;
            }
        }

        return -1;
    }
}
