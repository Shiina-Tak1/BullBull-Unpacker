using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 密码本的格式与编码。逐条对着 的 <c>assert_</c> 写。
/// </summary>
public sealed class PasswordBookTests
{
    private static readonly string[] Tricky =
    [
        "abc123",
        "带中文的密码",
        "🔒🔒",
        "",
        " leading",
        "trailing ",
        "#井号开头",
        "\\\\B64:YWJj",
        "带\t制表符",
        "带\n换行",
        "带\u2028行分隔符",
        "带\x85下一行",
    ];

    [Fact]
    public void RoundTrip_MustKeepEveryTrickyPassword()
    {
        var entries = Tricky.Select(p => new VaultEntry(p, 0)).ToList();

        var text = PasswordBook.Render(entries, []);
        var parsed = PasswordBook.Parse(text);

        // 空密码不产生条目：候选里照样试一次空密码，但它不是密码本里的一条。
        Assert.Equal(Tricky.Length - 1, parsed.Count);
        foreach (var password in Tricky.Where(p => p.Length > 0))
        {
            Assert.Contains(parsed, entry => entry.Password == password);
        }
    }

    [Fact, Trait("Bug", "B-2026-004")]
    public void B64PrefixPassword_MustSurviveRoundTrip()
    {
        // B-2026-004：密码本身就是 `\\B64:...` 时，明文写出去会被当成编码行解开。
        const string password = "\\\\B64:YWJj";
        var text = PasswordBook.Render([new VaultEntry(password, 3)], []);
        var parsed = PasswordBook.Parse(text);

        Assert.Single(parsed);
        Assert.Equal(password, parsed[0].Password);
        Assert.Equal(3, parsed[0].Hits);
        Assert.DoesNotContain("abc", text.Replace(password, string.Empty, StringComparison.Ordinal));
    }

    [Fact, Trait("Bug", "B-2026-051")]
    public void LineBreakCharacters_InTheMiddleMustNotSplitTheEntry()
    {
        // B-2026-051：写侧字符集必须等于读侧 splitlines 的断点集，漏一个就裂成两条。
        char[] breaks = ['\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029'];
        foreach (var ch in breaks)
        {
            var password = $"abc{ch}xyz";
            var text = PasswordBook.Render([new VaultEntry(password, 7)], []);
            var parsed = PasswordBook.Parse(text);

            Assert.True(PasswordBook.NeedsEncode(password), $"字符 {(int)ch} 必须走编码行");
            Assert.Single(parsed);
            Assert.Equal(password, parsed[0].Password);
            Assert.Equal(7, parsed[0].Hits);
        }
    }

    [Fact]
    public void NeedsEncode_MustMatchTheReader()
    {
        Assert.True(PasswordBook.NeedsEncode(string.Empty));
        Assert.True(PasswordBook.NeedsEncode(" x"));
        Assert.True(PasswordBook.NeedsEncode("x "));
        Assert.True(PasswordBook.NeedsEncode("#x"));
        Assert.True(PasswordBook.NeedsEncode(PasswordBook.RawPrefix + "YWJj"));
        Assert.True(PasswordBook.NeedsEncode("a\tb"));
        Assert.False(PasswordBook.NeedsEncode("abc123"));
        Assert.False(PasswordBook.NeedsEncode("带中文的密码"));
    }

    [Fact, Trait("Bug", "B-2026-059")]
    public void TabWithNonDigitTail_MustKeepTheWholeLineAsThePassword()
    {
        // B-2026-059：TAB 后面不是数字时整行按密码保留，备注不许被静默截掉。
        var parsed = PasswordBook.Parse("mypass1\t这是备注");

        Assert.Single(parsed);
        Assert.Equal("mypass1\t这是备注", parsed[0].Password);
        Assert.Equal(0, parsed[0].Hits);
    }

    [Fact]
    public void TabWithDigitTail_MustParseAsHits()
    {
        var parsed = PasswordBook.Parse("mypass1\t123");

        Assert.Single(parsed);
        Assert.Equal("mypass1", parsed[0].Password);
        Assert.Equal(123, parsed[0].Hits);
    }

    [Fact]
    public void TabWithFullWidthDigits_MustStillParseAsHits()
    {
        var parsed = PasswordBook.Parse("mypass1\t１２３");

        Assert.Equal("mypass1", parsed[0].Password);
        Assert.Equal(123, parsed[0].Hits);
    }

    [Fact]
    public void TabWithCircledDigit_MustKeepTheWholeLine()
    {
        // `①` 是 No 类数字：Python 的 isdigit() 认它，但紧随其后的 int() 会抛异常，
        // 于是整本密码读不出来。C# 判为非数字，整行按密码保留。
        var parsed = PasswordBook.Parse("mypass1\t①");

        Assert.Equal("mypass1\t①", parsed[0].Password);
        Assert.Equal(0, parsed[0].Hits);
    }

    [Fact]
    public void HashLinesBelowTheMarker_MustBePasswords()
    {
        var text = string.Join('\n',
            VaultText.BookHeaderFirst,
            VaultText.NotesMarker,
            "# 我的备注",
            VaultText.CommentMarker,
            "#我的密码",
            "abc123") + "\n";

        var parsed = PasswordBook.Parse(text);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("#我的密码", parsed[0].Password);
        Assert.Single(PasswordBook.Notes(text));
        Assert.Equal("# 我的备注", PasswordBook.Notes(text)[0]);
    }

    [Fact]
    public void LegacyFormat_MustTreatHashLinesAsCommentsAndKeepThemAsNotes()
    {
        const string text = "# 密码本 —— 老格式\n# 工具说明第二行\nabc123\n# 用户手写的一行\nmypass\n";

        var parsed = PasswordBook.Parse(text);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(new[] { "# 用户手写的一行" }, PasswordBook.LegacyNotes(text));
        Assert.True(PasswordBook.LooksLikeBook(text));
        Assert.False(PasswordBook.LooksLikeBook("abc123\nmypass\n"));
    }

    [Fact]
    public void DuplicatePasswords_MustMergeTakingTheLargerHits()
    {
        var parsed = PasswordBook.Parse("abc\t1\nxyz\nabc\t5\n");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("abc", parsed[0].Password);
        Assert.Equal(5, parsed[0].Hits);
        Assert.Equal("xyz", parsed[1].Password);
    }

    [Fact]
    public void Sort_MustKeepFileOrderForEqualHits()
    {
        var sorted = PasswordBook.Sort(
        [
            new VaultEntry("first", 0),
            new VaultEntry("second", 9),
            new VaultEntry("third", 0),
            new VaultEntry("fourth", 9),
        ]);

        Assert.Equal(
            new[] { "second", "fourth", "first", "third" },
            sorted.Select(e => e.Password));
    }

    [Fact]
    public void SplitLines_MustMatchPythonSplitlines()
    {
        Assert.Empty(PasswordBook.SplitLines(string.Empty));
        Assert.Equal(new[] { "a" }, PasswordBook.SplitLines("a\n"));
        Assert.Equal(new[] { "a", string.Empty }, PasswordBook.SplitLines("a\n\n"));
        Assert.Equal(new[] { "a", "b" }, PasswordBook.SplitLines("a\r\nb"));
        Assert.Equal(new[] { "a", "b" }, PasswordBook.SplitLines("a\u2028b"));
        Assert.Equal(new[] { "a", "b" }, PasswordBook.SplitLines("a\u0085b"));
        Assert.Equal(new[] { "abc", "xyz" }, PasswordBook.SplitLines("abc\vxyz"));
    }

    [Fact, Trait("Bug", "B-2026-005"), Trait("Bug", "B-2026-006")]
    public void TextEncoding_MustReadAllEightEncodings()
    {
        const string content = VaultText.BookHeaderFirst + "\n" + VaultText.CommentMarker + "\nabc123\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var cases = new (string Name, byte[] Bytes)[]
        {
            ("utf-8", new UTF8Encoding(false).GetBytes(content)),
            ("utf-8 BOM", new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(content)).ToArray()),
            ("utf-16 BOM", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(content)).ToArray()),
            ("utf-16be BOM", Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(content)).ToArray()),
            ("utf-16le 无 BOM", Encoding.Unicode.GetBytes(content)),
            ("utf-16be 无 BOM", Encoding.BigEndianUnicode.GetBytes(content)),
            ("utf-32 BOM", Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(content)).ToArray()),
            ("utf-32be 无 BOM", new UTF32Encoding(true, false).GetBytes(content)),
            ("gbk", Encoding.GetEncoding(936).GetBytes(content)),
        };

        foreach (var (name, bytes) in cases)
        {
            var text = PasswordBook.DecodeText(bytes);
            Assert.True(text.StartsWith(VaultText.BookHeaderPrefix, StringComparison.Ordinal), name);
            Assert.Contains("abc123", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\ufffd', text);
        }
    }

    [Fact, Trait("Bug", "B-2026-054")]
    public void LoneNulInsideSingleByteText_MustNotBeGuessedAsWideEncoding()
    {
        // B-2026-054：错宽度的猜测会把整本读成乱码，随后任何保存都把乱码写回。
        Assert.Equal("aaa\0", PasswordBook.DecodeText("aaa\0"u8.ToArray()));
        Assert.Equal("aaaa\0", PasswordBook.DecodeText("aaaa\0"u8.ToArray()));
        Assert.Equal("ab", PasswordBook.DecodeText("a\0b\0"u8.ToArray()));
        Assert.Equal("\0\0", PasswordBook.DecodeText(new byte[] { 0, 0 }));
    }

    [Fact]
    public void SingleByteTextWithALoneNul_MustNotProduceChineseCharacters()
    {
        // B-2026-054 的判据原文：`b'aaa\x00' -> 'aaa\x00'`，且结果里没有凭空冒出的中日韩汉字。
        var text = PasswordBook.DecodeText("aaa\0"u8.ToArray());

        Assert.Equal("aaa\0", text);
        Assert.DoesNotContain(text, ch => char.IsBetween(ch, '一', '鿿'));
    }

    [Fact]
    public void Latin1Fallback_MustStillReadPlainAscii()
    {
        Assert.Equal("abc", PasswordBook.DecodeText("abc"u8.ToArray()));
        Assert.Equal("é", PasswordBook.DecodeText(new byte[] { 0xe9 }));
    }

    [Fact]
    public void DecodeRawLine_MustRejectNonCanonicalForms()
    {
        Assert.Null(PasswordBook.DecodeRawLine(PasswordBook.RawPrefix + "!!!"));
        Assert.Null(PasswordBook.DecodeRawLine(PasswordBook.RawPrefix + "YWJjZA"));

        var ok = PasswordBook.DecodeRawLine(PasswordBook.RawPrefix + "YWJj\t5");
        Assert.NotNull(ok);
        Assert.Equal("abc", ok.Value.Password);
        Assert.Equal(5, ok.Value.Hits);
    }

    [Fact]
    public void ImportPasswords_MustKeepEveryLineWhenNotVaultLike()
    {
        var lines = PasswordBook.ImportPasswords("a\n\n b \n", vaultLike: false);

        Assert.Equal(["a", string.Empty, " b "], lines);
    }
}
