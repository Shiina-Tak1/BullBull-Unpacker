using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 候选从哪里来、什么顺序、变体怎么放大。
/// </summary>
public sealed class VaultCandidateTests
{
    private static Vault NewVault(params (string Password, int Hits)[] entries)
    {
        var vault = new Vault();
        vault.SetEntries(entries.Select(e => new VaultEntry(e.Password, e.Hits)));
        return vault;
    }

    [Fact]
    public void Order_MustBeFileNameThenBookThenEmpty()
    {
        var vault = NewVault(("book-hit", 2), ("book-cold", 0));

        var candidates = vault.CandidatesFor(Path.Combine("C:", "资料", "包.zip密码777"));

        Assert.Equal(PasswordOrigin.FileName, candidates[0].Origin);
        Assert.Equal("777", candidates[0].Value);
        Assert.Equal(PasswordOrigin.Book, candidates[1].Origin);
        Assert.Equal("book-hit", candidates[1].Value);
        Assert.Equal(PasswordOrigin.Book, candidates[2].Origin);
        Assert.Equal("book-cold", candidates[2].Value);
        Assert.Equal(PasswordOrigin.Empty, candidates[^1].Origin);
        Assert.Equal(string.Empty, candidates[^1].Value);
    }

    [Fact]
    public void Order_BookEntriesMustBeSortedByHitsKeepingFileOrderForTies()
    {
        var vault = NewVault(("zero-a", 0), ("five", 5), ("zero-b", 0), ("five-b", 5));

        var books = vault.CandidatesFor("x.zip").Where(c => c.Origin == PasswordOrigin.Book).ToList();

        Assert.Equal(["five", "five-b", "zero-a", "zero-b"], books.Select(c => c.Value));
    }

    [Fact]
    public void NameExtraction_MustTakeTheLastKeywordAndStopAtADot()
    {
        Assert.Equal("123", NamePassword.ExtractFromName("包.zip解压密码123")!.Value);
        Assert.Equal("123", NamePassword.ExtractFromName("包.zip解压密码:123")!.Value);
        Assert.Equal("123", NamePassword.ExtractFromName("包.zip解压密码123.rar")!.Value);
        Assert.Equal("123", NamePassword.ExtractFromName("包.zip密码123")!.Value);
        Assert.Equal("123", NamePassword.ExtractFromName("包.zippw123")!.Value);
        Assert.Equal("123", NamePassword.ExtractFromName("包.zip解压码123")!.Value);
    }

    [Fact]
    public void NameExtraction_LongerKeywordMustWinAtTheSamePosition()
    {
        // `pwd` 与 `pw` 在同一位置命中：取更长的那个，否则密码会多出前面的 `d`。
        var guess = NamePassword.ExtractFromName("x.zippwd123");

        Assert.Equal("pwd", guess!.Keyword);
        Assert.Equal("123", guess.Value);
    }

    [Fact]
    public void NameExtraction_ExtensionPasswordIsOnlyForFileNames()
    {
        Assert.Equal("123", NamePassword.ExtractFromName("包.zip123")!.Value);
        Assert.Null(NamePassword.ExtractFromName("包.zip123", allowExtensionDigits: false));
        Assert.Null(NamePassword.ExtractFromName("包.zipmp4"));
        Assert.Null(NamePassword.ExtractFromName("包.zip"));
    }

    [Fact]
    public void NameExtraction_MustWalkUpToTheFolderName()
    {
        var root = Path.Combine(Path.GetTempPath(), "bbu-cand", "例例包合集解压密码:123");

        var guess = NamePassword.ExtractFromPath(Path.Combine(root, "包.zip"));

        Assert.Equal("123", guess!.Value);
        Assert.Contains("文件夹名", guess.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Variants_MustKeepTheOriginalFirst()
    {
        var variants = NamePassword.Variants("abc 123");

        Assert.Equal("abc 123", variants[0]);
        Assert.Equal(["abc 123", "abc123"], variants);
    }

    [Fact]
    public void Variants_MustAddHalfWidthForms()
    {
        var variants = NamePassword.Variants("ｐｗ１");

        Assert.Equal("ｐｗ１", variants[0]);
        Assert.Equal("pw1", variants[1]);
    }

    [Fact]
    public void DedupExpand_MustKeepTheEarliestOrigin()
    {
        var vault = new Vault();
        vault.SetEntries(["dup", "dup-b"]);

        var candidates = vault.CandidatesFor(Path.Combine("C:", "dup.zip密码dup"));

        // 文件名那一档先出，密码本里的同一条就被去掉了。
        Assert.Single(candidates, c => c.Value == "dup");
        Assert.Equal(PasswordOrigin.FileName, candidates.First(c => c.Value == "dup").Origin);
    }

    [Fact, Trait("Category", "ledger"), Trait("Bug", "B-2026-057")]
    public void VariantHit_MustRememberTheOriginalEntry()
    {
        // B-2026-057：命中的是变体时，记回原条目；不然密码本里会多出一条用户从没写过的东西。
        var directory = Path.Combine(Path.GetTempPath(), "bbu-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var vault = new Vault { BookPath = Path.Combine(directory, VaultText.BookFileName) };
            Assert.True(vault.Add("abc 123"));

            var hit = vault.CandidatesFor("x.zip").Single(c => c.Value == "abc123");

            Assert.Equal("abc 123", hit.SourceValue);
            Assert.Equal("abc 123", hit.RememberValue);
            Assert.Contains("变体", hit.Detail, StringComparison.Ordinal);
            Assert.True(vault.Remember(hit.RememberValue));
            Assert.Equal(1, vault.Find("abc 123")!.Hits);
            Assert.Null(vault.Find("abc123"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmptyCandidate_MustStillBeOneCandidate()
    {
        var vault = new Vault();

        var candidates = vault.CandidatesFor("x.zip");

        Assert.Single(candidates);
        Assert.Equal(PasswordOrigin.Empty, candidates[0].Origin);
    }

    [Fact]
    public void Describe_MustShowTheOriginAndTheValue()
    {
        var candidate = new PasswordCandidate("abc", PasswordOrigin.Book, "命中 2 次");

        Assert.Equal("密码本：abc（命中 2 次）", candidate.Describe());
        Assert.Equal("空密码：（空密码）", new PasswordCandidate(string.Empty, PasswordOrigin.Empty).Describe());
    }
}
