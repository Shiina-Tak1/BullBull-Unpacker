using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 密码本的状态与落盘。逐条对着 的 <c>assert_</c> 写。
/// </summary>
[Collection("engine")]
public sealed class VaultStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bbu-vault-" + Guid.NewGuid().ToString("N"));

    public VaultStateTests() => Directory.CreateDirectory(_directory);

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

    private string Book => Path.Combine(_directory, VaultText.BookFileName);

    private Vault NewVault()
    {
        var vault = new Vault { BookPath = Book };
        vault.Load();
        return vault;
    }

    [Fact]
    public void Add_MustPersistAndBeReadBack()
    {
        var vault = NewVault();

        Assert.True(vault.Add("first"));
        Assert.False(vault.Add("first"));
        Assert.False(vault.Add(string.Empty));

        var reopened = NewVault();
        Assert.Single(reopened.Entries);
        Assert.Equal("first", reopened.Entries[0].Password);
    }

    [Fact]
    public void AddMany_MustReportAddedDuplicateAndEmpty()
    {
        var vault = NewVault();

        var (added, duplicate, empty) = vault.AddMany(["a", "b", "a", string.Empty, " b "]);

        Assert.Equal(3, added);
        Assert.Equal(1, duplicate);
        Assert.Equal(1, empty);
        Assert.Equal(3, NewVault().Entries.Count);
    }

    [Fact, Trait("Bug", "B-2026-173")]
    public void RemoveMany_MustWriteTheWholeBatchOnce()
    {
        // B-2026-173：判据量落盘次数，不量耗时。
        var vault = NewVault();
        var wanted = Enumerable.Range(0, 200).Select(i => $"pw-{i:000}").ToList();
        vault.AddMany(wanted);
        var saves = vault.Saves;

        Assert.Equal(200, vault.RemoveMany(wanted));

        Assert.Equal(saves + 1, vault.Saves);
        Assert.Empty(vault.Entries);
        Assert.Empty(NewVault().Entries);

        // 一条都没命中不算删过：返回 0，也不再落盘。
        var after = vault.Saves;
        Assert.Equal(0, vault.RemoveMany(["never-there"]));
        Assert.Equal(after, vault.Saves);
    }

    [Fact]
    public void Remove_MustPersist()
    {
        var vault = NewVault();
        vault.AddMany(["a", "b"]);

        Assert.True(vault.Remove("a"));
        Assert.False(vault.Remove("a"));
        Assert.Equal(new[] { "b" }, NewVault().Entries.Select(e => e.Password));
    }

    [Fact, Trait("Bug", "B-2026-055")]
    public void RememberWriteFailure_MustRollTheMemoryBackExactly()
    {
        // B-2026-055：落盘失败时内存要精确退回，不然界面显示的和文件里的不一致。
        // 致败手法：密码本的父路径故意做成一个文件，建目录必然失败。
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");
        var vault = new Vault { BookPath = Path.Combine(blocker, VaultText.BookFileName) };
        vault.SetEntries([new VaultEntry("known", 1)]);

        Assert.False(vault.Remember("fresh"));
        Assert.Null(vault.Find("fresh"));
        Assert.Equal(1, vault.Find("known")!.Hits);
        Assert.NotEqual(string.Empty, vault.LastWriteError);

        Assert.False(vault.Add("another"));
        Assert.Null(vault.Find("another"));

        var (added, _, _) = vault.AddMany(["batch-1", "batch-2"]);
        Assert.Equal(0, added);
        Assert.Null(vault.Find("batch-1"));
    }

    [Fact, Trait("Bug", "B-2026-055")]
    public void Remember_MustRaiseTheHitsOfAnExistingEntry()
    {
        var vault = NewVault();
        vault.Add("known");

        Assert.True(vault.Remember("known"));

        Assert.Equal(1, NewVault().Find("known")!.Hits);
    }

    [Fact, Trait("Bug", "B-2026-056")]
    public void Save_MustNotBeBlockedByAnOccupiedFixedTempName()
    {
        // B-2026-056：临时名带进程号与随机串，固定的 <本>.tmp 被占住也照样能存。
        Directory.CreateDirectory(Book + ".tmp");
        var vault = NewVault();

        Assert.True(vault.Add("survives"));
        Assert.True(File.Exists(Book));
    }

    [Fact, Trait("Bug", "B-2026-056")]
    public void TwoWriters_MustNotOverwriteEachOther()
    {
        // B-2026-056 ③④：两个写者各持一份内存副本交错 remember，盘上一条都不许少，
        // 而且别的写者删掉的条目不许被旧副本复活。
        var first = NewVault();
        first.AddMany(["base"]);
        var second = NewVault();
        Assert.True(first.Remove("base"));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(second.Remember($"second-{i}"));
            Assert.True(first.Remember($"first-{i}"));
        }

        var merged = NewVault();
        Assert.Null(merged.Find("base"));
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(merged.Find($"first-{i}"));
            Assert.NotNull(merged.Find($"second-{i}"));
        }
    }

    [Fact, Trait("Bug", "B-2026-056")]
    public void Load_MustKeepTheMemoryWhenTheBookCannotBeRead()
    {
        // B-2026-056 ⑤：读盘抛异常时不许清空内存。
        var vault = NewVault();
        vault.Add("kept");
        var locked = new FileStream(Book, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            vault.Load();
        }
        finally
        {
            locked.Dispose();
        }

        Assert.NotNull(vault.Find("kept"));
    }

    [Fact]
    public void LegacyBook_MustBeUpgradedAndKeepTheUserNotes()
    {
        File.WriteAllText(
            Book,
            VaultText.BookHeaderFirst + "\n# 工具说明第二行\nabc123\n# 用户手写的一行\n",
            new System.Text.UTF8Encoding(false));

        var vault = NewVault();

        Assert.True(vault.UpgradedFromLegacy);
        Assert.Equal(new[] { "# 用户手写的一行" }, vault.Notes);
        var text = File.ReadAllText(Book);
        Assert.Contains(VaultText.CommentMarker, text, StringComparison.Ordinal);
        Assert.Contains(NewVault().Entries, e => e.Password == "abc123");
    }

    [Fact]
    public void ForeignTextFile_MustNotBeTouched()
    {
        File.WriteAllText(Book, "abc123\nmypass\n", new System.Text.UTF8Encoding(false));

        var vault = NewVault();

        Assert.False(vault.UpgradedFromLegacy);
        Assert.Equal("abc123\nmypass\n", File.ReadAllText(Book));
    }

    [Fact]
    public void Save_MustNotLeaveTempFilesBehind()
    {
        var vault = NewVault();
        vault.Add("one");
        vault.Add("two");

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.True(File.Exists(Book + ".lock"));
    }

    [Fact]
    public void Save_MustWriteLfOnly()
    {
        var vault = NewVault();
        vault.Add("one");

        var bytes = File.ReadAllBytes(Book);

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    /// <summary>
    /// 写盘前读不出盘上的密码本时拒绝写盘，并把原因交回调用方（B-2026-205）。
    ///
    /// 致败手法：拿一个独占句柄占住密码本，`ReadBookText` 五次重试全失败。
    /// 这条钉的是「拒绝写盘 ＋ 交回**本仓自己的**原因 ＋ 内存精确退回」。本机造不出
    /// 「读失败而替换仍成功」那一档 —— 任何持有句柄都会让 `File.Move(overwrite: true)` 一起被拒
    /// （实测见 01-实测结果.md），所以「整本覆盖真的落地」这条致败路径没有常驻断言：
    /// 撤掉拒绝分支后，这条红在 `LastWriteError` 上是系统原文而不是这句判据文案。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-205")]
    public void Add_MustBeRefusedWhenTheBookCannotBeRead()
    {
        var vault = NewVault();
        vault.AddMany(["base"]);
        var saves = vault.Saves;
        var before = File.ReadAllText(Book);

        using (var hold = new FileStream(Book, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(vault.Add("fresh"));
            Assert.Equal(VaultText.BookUnreadableForMerge, vault.LastWriteError);
            Assert.Null(vault.Find("fresh"));
        }

        Assert.Equal(saves, vault.Saves);
        Assert.Equal(before, File.ReadAllText(Book));
    }

    /// <summary>
    /// 写者锁拿不到时不许无声往下写：`&lt;密码本&gt;.lock` 被占成一个目录，锁文件根本建不出来。
    /// 撤掉拒绝那一支，这次保存会照旧写盘并报成功、`LastWriteError` 是空串，这条因此变红
    /// （B-2026-206）。用户可感知的后果是「密码本正被占用时，这次保存会被明确拒掉，
    /// 而不是悄悄写下去」。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-206")]
    public void Add_MustBeRefusedWhenTheWriteLockIsUnavailable()
    {
        var vault = NewVault();
        vault.AddMany(["base"]);
        var saves = vault.Saves;
        var before = File.ReadAllText(Book);
        File.Delete(Book + ".lock");
        Directory.CreateDirectory(Book + ".lock");

        try
        {
            Assert.False(vault.Add("fresh"));
            Assert.Equal(VaultText.LockUnusable, vault.LastWriteError);
            Assert.Null(vault.Find("fresh"));
            Assert.Equal(saves, vault.Saves);
            Assert.Equal(before, File.ReadAllText(Book));
        }
        finally
        {
            Directory.Delete(Book + ".lock");
        }
    }

    /// <summary>
    /// 「锁拿不到 ＋ 合并也读不出来」同时发生时，结果只能是「没写盘且报失败」（B-2026-205／206）。
    ///
    /// 原因必须落在**锁那一档**：这一支先判锁，所以撤掉锁的拒绝之后原因会变成合并那一档、这条变红。
    /// F-16 那一处的撤销由 <see cref="Add_MustBeRefusedWhenTheBookCannotBeRead"/> 承担 ——
    /// 两处拒绝互为掩蔽，只看「返回 false ＋ 内容不变」分不出是谁挡住的，这一条把顺序也钉住。
    /// </summary>
    [Fact]
    [Trait("Category", "ledger")]
    [Trait("Bug", "B-2026-205")]
    [Trait("Bug", "B-2026-206")]
    public void Add_MustRefuseWhenNeitherTheLockNorTheMergeWorks()
    {
        var vault = NewVault();
        vault.AddMany(["base"]);
        var saves = vault.Saves;
        var before = File.ReadAllText(Book);
        File.Delete(Book + ".lock");
        Directory.CreateDirectory(Book + ".lock");

        try
        {
            using (var hold = new FileStream(Book, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(vault.Add("fresh"));
                Assert.Equal(VaultText.LockUnusable, vault.LastWriteError);
                Assert.Null(vault.Find("fresh"));
            }
        }
        finally
        {
            Directory.Delete(Book + ".lock");
        }

        Assert.Equal(saves, vault.Saves);
        Assert.Equal(before, File.ReadAllText(Book));
    }

    /// <summary>
    /// 含 NUL 的候选进不了命令行，跳过它继续试下一个：过程提示在场，整轮结语不是「密码已全部试完」那一条。
    /// 撤掉 NUL 判据或撤掉那句提示即红（B-2026-218）。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-218")]
    public async Task CandidateWithNul_MustBeSkippedAndTheUserToldToUnpackByHand()
    {
        var log = new RecordingLog();
        var engine = TestPaths.NewEngine(log);
        var archive = TestPaths.Fixture("rar-encrypted.rar");
        var vault = NewVault();
        vault.SetEntries(["AAA\0BBB", "CCC\0DDD"]);

        var result = await vault.UnlockAsync(engine, archive, new UnlockOptions { Workers = 1 });

        Assert.False(result.Ok);

        // 两个候选都含 NUL，所以这句文案不带计量单位时只在第一次划掉候选时报一次：
        // 逐候选各报一次会连着刷同一句。
        Assert.Single(log.Infos, line => line.Contains(VaultText.NeedsManualSkipped, StringComparison.Ordinal));

        // 结语按跳过的个数现算，不写死。
        var skipped = vault.CandidatesFor(archive).Count(candidate => EngineCommand.NeedsManual(candidate.Value));
        Assert.Equal(string.Format(VaultText.NeedsManualSummary, skipped), result.StoppedReason);
    }
}
