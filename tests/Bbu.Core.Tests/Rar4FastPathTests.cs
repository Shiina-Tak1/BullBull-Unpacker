using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Services.Rar4;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// RAR4 快路的判据（接线）。核心性质与  同一条：**绝不误杀正确密码**；
///
/// 不并进  的理由：那边的 <c>Covered</c> / <c>Uncovered</c> 是四种格式混在一起的
/// 矩阵、一份夹具一行，而这里要把五档逐档点名（其中两档是同一份夹具的 `-hp` 版），
/// 还要用一份「退回旧口径」的对照证明修复本身有效 —— 粒度和证据形态都不一样。
/// `-ma4`，7.x 删了它）。
/// </summary>
public sealed class Rar4FastPathTests
{
    /// <summary>包就是用这句建的：与造夹具那份脚本同值。</summary>
    private const string Password = "RarPw123";

    private static readonly IFastPath FastPath = new CompositeFastPath();

    /// <summary>`-p` 那一档：判据是 32 位 FILE_CRC，错密码的结论是确定的。</summary>
    public static TheoryData<string> FileEncrypted => new(
        "rar4-p.rar",
        "rar4-pad5.rar");

    /// <summary>
    /// `-hp` 那一档：错密码在第一步只过 16 位 HEAD_CRC，另有约一半候选解出的头段长度
    /// 落在合法区间之外或超出密文长度，于是回「不知道」——两档都安全，见本文件那条阈值断言。
    /// </summary>
    public static TheoryData<string> HeaderEncrypted => new(
        "rar4-hp.rar",
        "rar4-hp-pad5.rar",
        "rar4-hp-arccmt.rar");

    /// <summary>判得了的五档：`-p`、`-hp`、各自的填充版、`-hp` 加档案注释（加密链首块是 SERVICE）。</summary>
    public static TheoryData<string> Judgeable => new(
        "rar4-p.rar",
        "rar4-pad5.rar",
        "rar4-hp.rar",
        "rar4-hp-pad5.rar",
        "rar4-hp-arccmt.rar");

    /// <summary>引擎读得了、但 RAR4 快路必须整轮不进的那些：明文、压缩条目、分卷的每一卷。</summary>
    public static TheoryData<string> NotJudgeable => new(
        "rar4-plain.rar",
        "rar4-compressed.rar",
        "rar4-split.part1.rar",
        "rar4-split.part2.rar",
        "rar4-split.part3.rar",
        "rar4-split.part4.rar",
        "rar4-split.part5.rar");

    private static string Vault(string name) => Path.Combine(TestPaths.Fixtures, "vault", name);

    [Theory, MemberData(nameof(Judgeable))]
    public void CorrectPassword_MustNeverBeRejected(string name)
    {
        using var session = FastPath.Open(Vault(name));

        // Assert.NotNull 不是装饰：快路拒不开庭时 Judge 也无从调用，不钉住"庭真的开了"，
        // 下面那句对不覆盖的形态就是一条恒真的空断言。
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
    }

    /// <summary>`-p`：错密码必须逐条被划掉 —— 32 位 CRC 对不上是确定的。</summary>
    [Theory, MemberData(nameof(FileEncrypted))]
    public void WrongPassword_MustBeRejected(string name)
    {
        using var session = FastPath.Open(Vault(name));
        Assert.NotNull(session);

        Assert.Equal(FastPathVerdict.ProbablyWrong, session!.Judge("RarPw124"));

        var rejected = Enumerable.Range(0, 50)
            .Count(i => session.Judge($"wrong-{i:000}") is FastPathVerdict.ProbablyWrong);
        Assert.Equal(50, rejected);
    }

    /// <summary>
    /// `-hp`：错密码只过 16 位 HEAD_CRC，所以只判"多数被划掉"。
    /// 阈值取 10/50（期望值约 25）而不是 50/50：剩下那半数走的是"头段长度读不出"这条回退路，
    /// 它与"划掉"一样安全。这一档的硬要求仍由上面那条正确密码不误杀兜住。
    /// </summary>
    [Theory, MemberData(nameof(HeaderEncrypted))]
    public void WrongPassword_MustBeRejectedForMostCandidates(string name)
    {
        using var session = FastPath.Open(Vault(name));
        Assert.NotNull(session);

        var rejected = Enumerable.Range(0, 50)
            .Count(i => session!.Judge($"wrong-{i:000}") is FastPathVerdict.ProbablyWrong);

        Assert.True(rejected >= 10, $"{name} 只划掉 {rejected}/50");
    }

    [Theory, MemberData(nameof(NotJudgeable))]
    public void PlainCompressedAndSplit_MustNotEnterFastPath(string name)
    {
        Assert.Null(FastPath.Open(Vault(name)));
    }

    /// <summary>安全线：空密码与超过 127 个码元的候选不进判据，一律回「不知道」。</summary>
    [Fact]
    public void EmptyOrOverlongPassword_MustReturnUnknown()
    {
        using var session = FastPath.Open(Vault("rar4-p.rar"));
        Assert.NotNull(session);

        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(string.Empty));
        Assert.Equal(FastPathVerdict.Unknown, session.Judge(new string('Z', 128)));
    }

    /// <summary>
    /// 反向对照：把判据退回「按 PACK_SIZE 全长算 CRC」那一版，`rar4-pad5.rar` 上的正确密码必须被误杀。
    ///
    /// 三条断言同一次跑完才有意义：前两条用同一段代码、只换 CRC 的覆盖范围 ——
    /// 按 UNP_SIZE 是「不知道」，按 PACK_SIZE 是「肯定不对」。这就同时证明了夹具确实是填充档、
    /// 且分离这两种结论的**只有** CRC 的覆盖范围（坐标、KDF、解密三段都一样，不成立时前置断言先红）。
    /// 第三条钉住产品实现的是前面那一版；撤掉那条修复，第三条立刻变红。
    /// </summary>
    [Fact]
    public void PaddedEntry_RevertedFullLengthCrc_WouldRejectTheCorrectPassword()
    {
        var path = Vault("rar4-pad5.rar");

        Assert.Equal(FastPathVerdict.Unknown, PaddedEntryVerdict(path, Password, coverFullPack: false));
        Assert.Equal(FastPathVerdict.ProbablyWrong, PaddedEntryVerdict(path, Password, coverFullPack: true));

        using var session = FastPath.Open(path);
        Assert.NotNull(session);
        Assert.Equal(FastPathVerdict.Unknown, session!.Judge(Password));
    }

    /// <summary>
    /// <c>rar4-pad5.rar</c> 上文件级加密那一段判据的独立复现。KDF 与 AES 用产品那一份，
    /// <paramref name="coverFullPack"/> 选 CRC 覆盖「PACK_SIZE 全长」还是「UNP_SIZE」。
    /// </summary>
    private static FastPathVerdict PaddedEntryVerdict(string path, string password, bool coverFullPack)
    {
        var bytes = File.ReadAllBytes(path);

        // 第一个块是 MAIN_HEAD（它的 hsize 在第 12 字节），FILE_HEAD 紧接其后：
        // hsize@+5、pack@+7、unp@+11、CRC@+16、method@+25、nameSize@+26。
        var at = 7 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        Assert.Equal((byte)0x74, bytes[at + 2]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 5));
        var pack = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 7));
        var unp = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 11));
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 16));
        var method = bytes[at + 25];
        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 26));

        // 坐标自检：这些不成立时下面那句对照测的就不是 CRC 的覆盖范围，而是一条恒真的空断言。
        Assert.Equal(Rar4FastPathSession.Magic, bytes[..7]);
        Assert.Equal((byte)0x30, method);
        Assert.Equal(16u, pack);
        Assert.Equal(5u, unp);
        Assert.Equal(pack, (unp + 15) & ~15u);
        Assert.True(headerSize >= 32 + nameSize + 8);

        var salt = bytes.AsSpan(at + 32 + nameSize, 8).ToArray();
        var cipher = bytes.AsSpan(at + headerSize, (int)pack).ToArray();
        var plain = new byte[cipher.Length];
        var (key, init) = Rar4Kdf.SetKey30(password, salt);
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = init;
            using var transform = aes.CreateDecryptor();
            transform.TransformBlock(cipher, 0, cipher.Length, plain, 0);
        }

        var covered = plain.AsSpan(0, coverFullPack ? plain.Length : (int)unp);
        var actual = Crc32.Compute(0xffffffff, covered) ^ 0xffffffff;
        return actual == crc ? FastPathVerdict.Unknown : FastPathVerdict.ProbablyWrong;
    }

    /// <summary>
    /// `-hp` 的 salt 落在头缓冲之外。块链把 MAIN_HEAD 推到 1 MiB 顶点，字段声称的 salt 起点
    /// 正好等于缓冲长度：判据必须以**已读到的长度**为界。撤掉那条界，这条断言抛
    /// `ArgumentOutOfRangeException`（异常会被候选循环的兜底接住，但那是另一档行为）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-194")]
    public void HpSaltPastTheHeadBuffer_MustFallBackInsteadOfThrowing()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("rar4-hp-salt-past-head.rar");
        var bytes = SaltPastHeadSample(path);

        // 坐标自检：不成立时下面那句 Null 就是恒真的空断言 —— 样本没走到 -hp 那一支。
        const int second = 1048569;
        Assert.Equal((byte)0x73, bytes[second + 2]);
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(second + 3)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(second + 5)));

        // 两条界各自算一遍：旧界（整文件长度）放行这个样本，新界（缓冲实读长度）拦下它。
        var saltAt = (long)second + 7;
        Assert.Equal(1 << 20, saltAt);
        Assert.True(saltAt + 8 + 16 <= bytes.Length, "旧界本该放行这个样本");
        Assert.True(saltAt + 8 > (1 << 20), "salt 本该落在缓冲之外");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Null(Rar4FastPathSession.Open(stream));
    }

    /// <summary>
    /// 1 MiB + 4096 字节的 -hp 头，按字节布局写死：第一块 FILE_HEAD（hsize=32、pack=1048530、
    /// 不带 LHD_PASSWORD）把 at 推到 1048569；第二块 MAIN_HEAD（hsize=7、带 0x0080）的
    /// salt 起点因此正好等于 1 MiB 缓冲的末尾。
    /// </summary>
    private static byte[] SaltPastHeadSample(string path)
    {
        var bytes = new byte[(1 << 20) + 4096];
        Rar4FastPathSession.Magic.CopyTo(bytes, 0);

        const int first = 7;
        bytes[first + 2] = 0x74;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(first + 3), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(first + 5), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(first + 7), 1048530);

        const int second = 1048569;
        Assert.Equal(second, first + 32 + 1048530);
        bytes[second + 2] = 0x73;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(second + 3), 0x0080);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(second + 5), 7);

        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    /// <summary>
    /// `-hp` 头链里声明长度 16 的 FILE 块：判据要读的字段在偏移 16 就出了这块头。
    /// 短于 32 的头块是合法的（MAIN_HEAD 与注释块都更短），所以这里只能回「不知道」——
    /// 按「密码不对」处置会把正确密码划掉。撤掉那条守卫，这条断言抛 `ArgumentOutOfRangeException`。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-194")]
    public void ShortFileHeaderInTheEncryptedChain_MustReturnUnknown()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("rar4-hp-short-file-header.rar");
        var plain = ShortEncryptedFileHeaderSample(path, Password);

        // 坐标自检：这块头声明 hsize=16，而判据要 read 到偏移 26 的 nameSize。
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(5)));
        Assert.Equal((byte)0x74, plain[2]);
        Assert.True(plain.Length < 26 + 2, "这块头本该短到读不出 nameSize");

        var verdict = OnItsOwnThread(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var session = Rar4FastPathSession.Open(stream);
            Assert.NotNull(session);
            return session!.Judge(Password);
        });

        Assert.Equal(FastPathVerdict.Unknown, verdict);
    }

    /// <summary>
    /// 38 字节的 -hp 头：MAIN_HEAD（hsize=7、带 0x0080）之后是 8 字节 salt 与一个 16 字节头块。
    /// 头块明文按 RAR4 布局写死（HEAD_CRC 在 [0,2)、type=FILE_HEAD 在 [2]、hsize 在 [5,7)），
    /// 用产品那份 KDF 与 AES 参数加密 —— 判据解出来就是它。
    /// </summary>
    private static byte[] ShortEncryptedFileHeaderSample(string path, string password)
    {
        const int headerSize = 16;
        var plain = new byte[headerSize];
        plain[2] = 0x74;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(5), headerSize);

        // HEAD_CRC 覆盖 HEAD_TYPE 起的 headerSize 字节（rawread.cpp:165，ProcessedOnly=false）。
        var headCrc = (ushort)((Crc32.Compute(0xffffffff, plain.AsSpan(2)) ^ 0xffffffff) & 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(0), headCrc);

        var salt = new byte[8];
        for (var i = 0; i < salt.Length; i++)
        {
            salt[i] = (byte)(0x11 + i);
        }

        var (key, init) = Rar4Kdf.SetKey30(password, salt);
        var cipher = new byte[headerSize];
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = init;
            using var transform = aes.CreateEncryptor();
            transform.TransformBlock(plain, 0, plain.Length, cipher, 0);
        }

        const int mainAt = 7;
        var bytes = new byte[mainAt + 7 + salt.Length + cipher.Length];
        Rar4FastPathSession.Magic.CopyTo(bytes, 0);
        bytes[mainAt + 2] = 0x73;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(mainAt + 3), 0x0080);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(mainAt + 5), 7);
        salt.CopyTo(bytes, mainAt + 7);
        cipher.CopyTo(bytes, mainAt + 7 + salt.Length);

        File.WriteAllBytes(path, bytes);
        return plain;
    }

    /// <summary>
    /// 在一条全新线程上跑判据：明文缓冲按线程复用，同线程先跑过别的样本时那块缓冲已经被撑大，
    /// 越界读就不会出现 —— 断言会变成恒真的空断言。
    /// </summary>
    private static FastPathVerdict OnItsOwnThread(Func<FastPathVerdict> body)
    {
        FastPathVerdict? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(result is not null, "独立线程没有给出结论");
        return result!.Value;
    }
}
