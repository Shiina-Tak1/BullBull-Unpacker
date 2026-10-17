using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 试密码的端到端结论。会静默受损的那几条不带档位标记，进默认档；
/// </summary>
[Collection("engine")]
public sealed class VaultUnlockTests : IDisposable
{
    private const string Password = "BbuPw2026";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bbu-unlock-" + Guid.NewGuid().ToString("N"));

    public VaultUnlockTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论。
        }
    }

    internal static string Vault1(string name) => Path.Combine(TestPaths.Fixtures, "vault", name);

    internal Vault Booked(params string[] passwords)
    {
        var vault = new Vault { BookPath = Path.Combine(_directory, VaultText.BookFileName) };
        foreach (var password in passwords)
        {
            vault.Add(password);
        }

        return vault;
    }

    internal static IFastPath FastPath { get; } = new CompositeFastPath();

    [Fact]
    public async Task Hit_MustTakeTheFirstCandidateAndNameTheOrigin()
    {
        var result = await Booked(Password).UnlockAsync(TestPaths.NewRarEngine(), Vault1("zip-aes256.zip"));

        Assert.True(result.Ok);
        Assert.Equal(Password, result.Password);
        Assert.InRange(result.Tries, 1, 2);
        Assert.Contains(
            VaultText.OriginLabels[(int)PasswordOrigin.Book], result.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 非 ASCII 密码端到端：包就是用那句中文密码建出来的，试密码这条路要能命中。
    ///
    /// 快路那一侧判的是"不把正确的非 ASCII 候选划掉"（见 ` ` 的 `NonAscii`），
    /// 这一条判的是真的解得开。两条合起来才是"非 ASCII 密码不被误杀"。
    /// 载体取 7z 与 rar5：这两种容器的密码按 UTF-16 或 UTF-8 存，与宿主代码页无关。
    /// zip 那一路不适用 —— 它的密码字节按 OEM 代码页转，非中文代码页的机器上解不开
    /// 那份夹具仍留在 ` ` 里守快路判定。
    /// </summary>
    [Theory]
    [InlineData("7z-cn-pw.7z")]
    [InlineData("rar5-cn-pw.rar")]
    public async Task NonAsciiPassword_MustHit(string name)
    {
        var result = await Booked("错的密码", "密码123")
            .UnlockAsync(TestPaths.NewRarEngine(), Vault1(name), new UnlockOptions(), FastPath);

        Assert.True(result.Ok);
        Assert.Equal("密码123", result.Password);
    }

    /// <summary>
    /// AES + 非 ASCII 密码那一格，取 7z 的头部加密包：文件名与数据一起加密，判据比不加密那份更严。
    /// 原先是 WinZip 的 `-ycAES256` 那份，换掉的理由同 <see cref="NonAsciiPassword_MustHit"/>。
    /// </summary>
    [Fact]
    public async Task NonAsciiAesPassword_MustHit()
    {
        const string password = "中文密码测试甲乙丙";

        var result = await Booked("错的密码", password)
            .UnlockAsync(TestPaths.NewRarEngine(), Vault1("7z-cn-hp.7z"), new UnlockOptions(), FastPath);

        Assert.True(result.Ok);
        Assert.Equal(password, result.Password);
    }

    [Fact]
    public async Task Miss_MustTryEveryCandidateThenReportExhausted()
    {
        var result = await Booked("wrong-1", "wrong-2")
            .UnlockAsync(TestPaths.NewRarEngine(), Vault1("zip-aes256.zip"));

        Assert.False(result.Ok);
        Assert.Equal(UnlockProblem.Exhausted, result.Problem);
        Assert.True(result.WorthAskingUser);
        Assert.Equal(3, result.Tries);
    }

    [Fact]
    public async Task UnencryptedArchive_MustNotTryAnyPasswordAndMustNotClaimAWrongOne()
    {
        // 引擎对未加密的包会忽略 -p，试下去必然假命中，界面就会显示一个假的密码来源。
        var result = await Booked(Password, "another")
            .UnlockAsync(TestPaths.NewRarEngine(), Vault1("plain.zip"));

        Assert.Equal(0, result.Tries);
        Assert.Equal(UnlockProblem.NotEncrypted, result.Problem);
        Assert.Null(result.Password);
        Assert.False(result.WorthAskingUser);
        Assert.DoesNotContain("均不正确", result.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skip_MustDropTheCandidatesTheCallerAlreadyTried()
    {
        var result = await Booked(Password, "wrong").UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions { Skip = new HashSet<string> { Password } });

        Assert.False(result.Ok);
        Assert.Equal(UnlockProblem.Exhausted, result.Problem);
    }

    [Fact]
    public void PickWorkers_MustOnlyParalleliseTheCheapPath()
    {
        var cheap = new ArchiveInfo { ReadOk = true, SmallestEntry = "a.txt" };
        var headerOnly = new ArchiveInfo { ReadOk = false, Encrypted = true, HeaderEncrypted = true };
        var expensive = new ArchiveInfo { ReadOk = true };

        Assert.True(Vault.PickWorkers(100, cheap, 0) >= 2);
        Assert.True(Vault.PickWorkers(100, headerOnly, 0) >= 2);
        Assert.Equal(1, Vault.PickWorkers(100, expensive, 0));
        Assert.Equal(1, Vault.PickWorkers(3, cheap, 0));
        Assert.Equal(1, Vault.PickWorkers(100, null, 0));
        Assert.Equal(1, Vault.PickWorkers(100, cheap, 1));

        // 快路判得了这个包时不再看它贵不贵：快路不起进程、判定阶段不读盘，
        // 而「整包验证会抢盘」说的是引擎。连包信息都拿不到时也一样 —— 判得了就是判得了。
        Assert.True(Vault.PickWorkers(100, expensive, 0, fastPathReady: true) >= 2);
        Assert.True(Vault.PickWorkers(100, null, 0, fastPathReady: true) >= 2);
        // 用户显式要串行、候选太少：这两条压得住快路那一路（「单核机器」那一条由 PickWorkers 的
        // cpus < 2 分支管，这里造不出单核环境，故不断言）。
        Assert.Equal(1, Vault.PickWorkers(100, cheap, 1, fastPathReady: true));
        Assert.Equal(1, Vault.PickWorkers(3, cheap, 0, fastPathReady: true));
    }

    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-018")]
    public async Task UnencryptedArchive_MustNotBeDiagnosedAsAWrongPassword()
    {
        // B-2026-018：未加密的包不许被当成密码问题，否则界面会弹框问一个根本不存在的密码。
        var result = await Booked(Password).UnlockAsync(TestPaths.NewRarEngine(), Vault1("plain.7z"));

        Assert.Equal(UnlockProblem.NotEncrypted, result.Problem);
        Assert.Equal(0, result.Tries);
        Assert.False(result.WorthAskingUser);
    }

    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-027")]
    public async Task ArchiveTheEngineCannotRead_MustNotBeReportedAsAWrongPassword()
    {
        // B-2026-027：只有文件头前缀的 zip 引擎列不出来，一个密码都不该试。
        var result = await Booked(Password)
            .UnlockAsync(TestPaths.NewRarEngine(), TestPaths.Fixture("error-onlyheader.zip"));

        Assert.Equal(0, result.Tries);
        Assert.Equal(UnlockProblem.EngineError, result.Problem);
        Assert.Contains(VaultText.EngineCannotRead, result.Summary(), StringComparison.Ordinal);
        Assert.DoesNotContain("均不正确", result.Summary(), StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-038")]
    public async Task MissingVolume_MustNotTryAnyPasswordAndMustNameTheMissingOne()
    {
        // B-2026-038：判据写好了却从没接线时，缺分卷的包会把整本密码试一遍再要密码。
        var archive = Path.Combine(TestPaths.ProbeRoot, "vol-missing-main", "x.part2.rar");

        var result = await Booked(Password, "another").UnlockAsync(TestPaths.NewRarEngine(), archive);

        Assert.Equal(0, result.Tries);
        Assert.Equal(UnlockProblem.MissingVolume, result.Problem);
        Assert.False(result.WorthAskingUser);
        Assert.Contains("x.part1.rar", result.StoppedReason, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-070")]
    public async Task CancelledSearch_MustSayStopped()
    {
        // B-2026-070：取消的措辞统一写「已停止」，不许写「用户中止」；并行与串行两条路共用这一条措辞
        //（串行那一路的取消另有断言，见 B-2026-174）。
        using var source = new CancellationTokenSource();
        var book = Enumerable.Range(0, 70).Select(i => $"pw-{i:0000}").ToArray();

        var result = await Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions { OnTry = (_, _) => source.Cancel() },
            FastPath,
            source.Token);

        Assert.Equal(UnlockProblem.Cancelled, result.Problem);
        Assert.Equal(Errors.Stopped, result.StoppedReason);
        Assert.Equal(Errors.Stopped, result.Summary());
        Assert.Equal(Errors.Stopped, new EngineResult { Cancelled = true }.Brief);
        Assert.DoesNotContain("用户中止", result.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 候选少于并行门槛时必然走串行：取消只该停在第一条，结论落「已停止」且不弹密码框。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-174")]
    public async Task CancelledOnAShortSerialList_MustSayStoppedWithoutAsking()
    {
        using var source = new CancellationTokenSource();

        // 三条候选：少于 ParallelMinCandidates，串行没有第二种走法。
        var book = new[] { "pw-0001", "pw-0002", "pw-0003" };

        var result = await Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions { OnTry = (_, _) => source.Cancel() },
            FastPath,
            source.Token);

        Assert.Equal(UnlockProblem.Cancelled, result.Problem);
        Assert.Equal(Errors.Stopped, result.Summary());
        // 试完第一条就该停下：这是「取消不再被吞」的可数形态。
        Assert.Equal(1, result.Tries);
        Assert.False(result.WorthAskingUser);
    }

    /// <summary>
    /// 取消之后还有大量候选没试完：这时收口落在后续某个没填的槽上，而不是候选边界上的询问。
    /// 取消不得以异常逃出，结论照「已停止」收口（B-2026-172；取值处那条钉在 ` `）。
    /// </summary>
    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-172")]
    public async Task CancelledWhileWaitingForASlot_MustStillSayStopped()
    {
        using var source = new CancellationTokenSource();
        var book = Enumerable.Range(0, 200)
            .Select(i => $"pw-{i:0000}")
            .ToArray();

        // 不接快路：候选全落到引擎那一路，槽要等引擎真跑完才填上，取消正好落在等待期间。
        var result = await Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions { Workers = 16, OnTry = (_, _) => source.Cancel() },
            cancellationToken: source.Token);

        Assert.Equal(UnlockProblem.Cancelled, result.Problem);
        Assert.Equal(Errors.Stopped, result.Summary());
    }

    [Fact, Trait("Category", "ledger")]
    public async Task ThreeLayerChain_MustUnlockEveryLayerWithItsOwnPassword()
    {
        var vault = Booked("L3p@ss", "L1p@ss", "L2p@ss");
        var engine = TestPaths.NewRarEngine();

        foreach (var (name, expected) in new[]
                 {
                     ("chain-outer.zip", "L1p@ss"),
                     ("chain-middle.7z", "L2p@ss"),
                     ("chain-inner.rar", "L3p@ss"),
                 })
        {
            var result = await vault.UnlockAsync(engine, Vault1(name), null, FastPath);
            Assert.True(result.Ok, name);
            Assert.Equal(expected, result.Password);
        }
    }

    [Fact, Trait("Category", "ledger")]
    public async Task ParallelAndSerial_MustReachTheSameConclusion()
    {
        // 并发只影响快慢：判定按候选顺序走，最早命中的赢。
        var book = Enumerable.Range(0, 40).Select(i => $"pw-{i:0000}").Concat([Password]).ToArray();

        var serial = await Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(), Vault1("zip-aes256.zip"), new UnlockOptions { Workers = 1 }, FastPath);
        var parallel = await Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(), Vault1("zip-aes256.zip"), new UnlockOptions { Workers = 16 }, FastPath);

        Assert.Equal(serial.Password, parallel.Password);
        Assert.Equal(serial.Tries, parallel.Tries);
    }

    /// <summary>
    /// 暂停在候选边界上生效，继续之后一个候选都不漏、照旧命中（B-2026-149）。
    /// 这一条不接快路：每个候选都要起一次引擎，搜索才够长到能暂停。
    /// </summary>
    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-149")]
    public async Task Pause_MustStopTheSearchAndResumeWithoutSkippingACandidate()
    {
        var book = Enumerable.Range(0, 60).Select(i => $"pw-{i:0000}").Concat([Password]).ToArray();
        var tried = 0;
        var paused = 0;

        var search = Task.Run(() => Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions
            {
                Workers = 16,
                Pause = () => Volatile.Read(ref paused) == 1,
                OnTry = (_, _) => Interlocked.Increment(ref tried),
            }));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref tried) < 5 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Volatile.Write(ref paused, 1);
        await Task.Delay(400);
        var at = Volatile.Read(ref tried);
        await Task.Delay(400);
        Assert.Equal(at, Volatile.Read(ref tried));

        Volatile.Write(ref paused, 0);
        var result = await search;

        Assert.True(result.Ok);
        Assert.Equal(Password, result.Password);
        Assert.Equal(book.Length, result.Tries);
    }

    [Fact, Trait("Category", "ledger")]
    public async Task OnTry_MustBeCalledForEveryCandidateAndSurviveExceptions()
    {
        var seen = new List<string>();

        var result = await Booked("wrong").UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions
            {
                OnTry = (candidate, _) =>
                {
                    seen.Add(candidate.Value);
                    throw new InvalidOperationException("回调里的异常不许影响搜索");
                },
            });

        Assert.Equal(2, seen.Count);
        Assert.Equal(UnlockProblem.Exhausted, result.Problem);
    }

    /// <summary>
    /// B-2026-101：zstd / brotli / lz4 / lz5 / lizard 这几个编码的**错密码**，
    /// 引擎只回一句泛化的致命错误，里面没有「密码错」的任何特征词。
    /// 照「不是密码错就停」的口径，第 1 个候选就会把整本密码判死 —— 密码本里有正确密码也解不开。
    /// </summary>
    [Theory, InlineData("seven-zstd-plain.7z"), InlineData("seven-lz4-plain.7z")]
    [Trait("Bug", "B-2026-101")]
    public async Task SlowCodecArchive_MustStillWalkTheWholeBook(string name)
    {
        var book = Enumerable.Range(0, 5).Select(i => $"pw-{i:0000}").Concat([Password]).ToArray();

        var result = await Booked(book).UnlockAsync(TestPaths.NewRarEngine(), Vault1(name));

        Assert.True(result.Ok, name);
        Assert.Equal(Password, result.Password);
        Assert.True(result.Tries > 1, "正确密码不在第一位时不得就此停止");
    }

    /// <summary>
    /// B-2026-103：<c>Judge</c> 在窗口发起的 <c>for</c> 里同步调用时，<c>AttemptAsync</c>
    /// 会在引擎那个 <c>await</c> 之前同步执行完，窗口宽度就覆盖不到 <c>Judge</c> ——
    /// 日志写着 16 路，判定并发度却是 1，zip 那条快路的十几核因此拿不到。
    /// </summary>
    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-103")]
    public async Task FastPathJudge_MustRunWideInsideTheCandidateWindow()
    {
        var probe = new ConcurrencyProbe();
        var book = Enumerable.Range(0, 32).Select(i => $"pw-{i:0000}").ToArray();

        await Booked(book).UnlockAsync(
            TestPaths.NewRarEngine(),
            Vault1("zip-aes256.zip"),
            new UnlockOptions { Workers = 16 },
            probe);

        Assert.True(probe.Max > 1, $"快路判定的最大并发只有 {probe.Max}，窗口宽度没有覆盖到它");
    }

    /// <summary>
    /// 缺 <c>7z.dll</c> 的编解码器时不许说「包可能坏了」（B-2026-201）：
    /// 用户可感知的后果是这一档的结论改成「引擎读不了这个格式（缺 7z.dll 的编解码器）」。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-201")]
    public void EngineCannotReadNote_MustNameAMissingCodecDll()
    {
        var note = Vault.EngineCannotReadNote(EngineAbility.NeedsCodecDll);

        Assert.Equal(ProbeText.EngineNeedsCodecDll, note);
        Assert.NotEqual(VaultText.EngineCannotRead, note);
    }

    /// <summary>
    /// 反向对照：其余两个能力档照旧说「不是压缩包，或者文件已损坏」，改判据不许把这两档一起改掉。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-201")]
    public void EngineCannotReadNote_MustKeepTheOldWordsForOtherAbilities()
    {
        Assert.Equal(VaultText.EngineCannotRead, Vault.EngineCannotReadNote(EngineAbility.CanRead));
        Assert.Equal(VaultText.EngineCannotRead, Vault.EngineCannotReadNote(EngineAbility.NotArchive));
    }

    /// <summary>
    /// 能力档的判据本身：编解码器只在 <c>7z.dll</c> 里、而同目录没有那一份时才是「缺编解码器」；
    /// 不需要 dll 的格式与不是压缩包的格式各归各档。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-201")]
    public void ReadabilityOf_MustRequireTheCodecDllNextToTheExe()
    {
        using var dir = new TestDirectory();
        var exeWithoutDll = Path.Combine(dir.Path, "7z.exe");

        Assert.Equal(EngineAbility.NeedsCodecDll, Probe.ReadabilityOf(ArchiveFormat.Lz4, null));
        Assert.Equal(EngineAbility.NeedsCodecDll, Probe.ReadabilityOf(ArchiveFormat.Lz4, exeWithoutDll));
        Assert.Equal(EngineAbility.CanRead, Probe.ReadabilityOf(ArchiveFormat.Lz4, TestPaths.BundledSevenZip));
        Assert.Equal(EngineAbility.CanRead, Probe.ReadabilityOf(ArchiveFormat.Zip, exeWithoutDll));
        Assert.Equal(EngineAbility.NotArchive, Probe.ReadabilityOf(ArchiveFormat.Mp4, TestPaths.BundledSevenZip));
    }
}

/// <summary>
/// 桩快路：<c>Judge</c> 里记并发度并等待片刻以拉开窗口，用来量窗口宽度是否覆盖到判定。
/// 永远回「不知道」，所以候选全部落到引擎那一条路。
/// </summary>
internal sealed class ConcurrencyProbe : IFastPath
{
    private readonly object _gate = new();
    private int _current;

    public int Max { get; private set; }

    public IFastPathSession? Open(string archive) => new Session(this);

    private sealed class Session(ConcurrencyProbe owner) : IFastPathSession
    {
        public FastPathVerdict Judge(string password)
        {
            var now = Interlocked.Increment(ref owner._current);
            lock (owner._gate)
            {
                if (now > owner.Max)
                {
                    owner.Max = now;
                }
            }

            Thread.Sleep(3);
            Interlocked.Decrement(ref owner._current);
            return FastPathVerdict.Unknown;
        }

        public void Dispose()
        {
        }
    }
}
