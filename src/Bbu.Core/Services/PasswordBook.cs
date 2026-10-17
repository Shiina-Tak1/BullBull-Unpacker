using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 密码本文件的读写。
///
/// 三条不变量：用户写什么就存什么；写出的东西读回来逐字相同；解析只有这一份实现。
/// </summary>
public static class PasswordBook
{
    /// <summary>
    /// 「明文一行表达不了」的密码用这个前缀加标准 base64 存。字面是两个反斜杠。
    /// 判据不对称时会出现「写出去、读回来裂成两条」，见 <see cref="NeedsEncode"/>。
    /// </summary>
    public const string RawPrefix = "\\\\B64:";

    /// <summary>
    /// 读侧的断点集，也是写侧要拦的字符集。两处必须是同一份：<c>str.splitlines()</c> 在这些
    /// 字符上断行，而 <c>TAB</c> 是次数分隔符，所以它也在名单里。
    /// </summary>
    private static readonly char[] LineBreakChars =
        ['\r', '\n', '\t', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029'];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static bool NeedsEncode(string password)
    {
        if (password.Length == 0)
        {
            return true;
        }

        if (password != StripPy(password))
        {
            return true;
        }

        if (password.StartsWith(RawPrefix, StringComparison.Ordinal))
        {
            // 密码本身长得像标记行：不编码写出去，读回来会被当成编码行解开。
            return true;
        }

        if (password[0] == '#')
        {
            // `#` 开头本来就能明文存，编码是为了与 1.1.0 写出的文件逐字节一致。
            return true;
        }

        return password.IndexOfAny(LineBreakChars) >= 0;
    }

    public static string EncodeRawLine(string password) =>
        RawPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(password));

    /// <summary>`\\B64:&lt;base64&gt;[\t次数]` 解回取值与次数。不是本程序的规范编码就返回空。</summary>
    public static (string Password, int Hits)? DecodeRawLine(string line)
    {
        var body = line[RawPrefix.Length..];
        var tab = body.IndexOf('\t');
        var encoded = tab >= 0 ? body[..tab] : body;
        var tail = tab >= 0 ? body[(tab + 1)..] : string.Empty;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        // 规范形状才算本仓的编码行：用户手写一行长得像标记的东西不能被误读。
        if (Convert.ToBase64String(bytes) != encoded)
        {
            return null;
        }

        string password;
        try
        {
            password = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return (password, ParseCount(tail) ?? 0);
    }

    /// <summary>
    /// 解析密码本。分界行以上整段是说明，以下每一行都是密码（`#` 开头的也算）；
    /// 没有分界行的老文件退回「`#` 开头当注释」。重复的取值合并次数，位置保留先出现的那个。
    /// </summary>
    public static IReadOnlyList<VaultEntry> Parse(string text)
    {
        var lines = SplitLines(text);
        var markerAt = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (StripPy(lines[i]) == VaultText.CommentMarker)
            {
                markerAt = i;
                break;
            }
        }

        var entries = new List<VaultEntry>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 0; n < lines.Count; n++)
        {
            var line = StripPy(lines[n]);
            if (line.Length == 0)
            {
                continue;
            }

            if (markerAt >= 0)
            {
                if (n <= markerAt)
                {
                    continue;
                }
            }
            else if (line[0] == '#')
            {
                continue;
            }

            var hits = 0;
            var password = line;
            var decoded = line.StartsWith(RawPrefix, StringComparison.Ordinal) ? DecodeRawLine(line) : null;
            if (decoded is { } pair)
            {
                password = pair.Password;
                hits = pair.Hits;
            }
            else if (line.IndexOf('\t') >= 0)
            {
                var tab = line.IndexOf('\t');
                var head = line[..tab];
                var count = ParseCount(line[(tab + 1)..]);
                if (count is { } parsed)
                {
                    password = StripPy(head);
                    hits = parsed;
                }
                else
                {
                    // TAB 后面不是数字：整行按密码原样保留（含 TAB）。截掉会把用户写的备注静默丢掉。
                    password = line;
                }
            }

            if (password.Length == 0)
            {
                continue;
            }

            if (index.TryGetValue(password, out var at))
            {
                entries[at] = entries[at] with { Hits = Math.Max(entries[at].Hits, hits) };
                continue;
            }

            index[password] = entries.Count;
            entries.Add(new VaultEntry(password, hits));
        }

        return entries;
    }

    /// <summary>用户自己的备注：两个分界行之间那几行，保存时原样写回。</summary>
    public static IReadOnlyList<string> Notes(string text)
    {
        var lines = SplitLines(text);
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (StripPy(lines[i]) == VaultText.NotesMarker)
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0)
        {
            return [];
        }

        for (var i = start; i < lines.Count; i++)
        {
            if (StripPy(lines[i]) == VaultText.CommentMarker)
            {
                return lines.GetRange(start, i - start);
            }
        }

        return lines.GetRange(start, lines.Count - start);
    }

    /// <summary>
    /// 老格式里用户手写的 `#` 行：工具的文件头永远是开头连续的一段，所以第一个数据行之后
    /// 出现的 `#` 行就是用户的。升级时把它们搬进备注区，避免升级本身丢数据。
    /// </summary>
    public static IReadOnlyList<string> LegacyNotes(string text)
    {
        var notes = new List<string>();
        var seenData = false;
        foreach (var raw in SplitLines(text))
        {
            var line = StripPy(raw);
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                if (seenData)
                {
                    notes.Add(line);
                }

                continue;
            }

            seenData = true;
        }

        return notes;
    }

    /// <summary>是不是本程序的密码本（第一个非空行以 `# 密码本` 开头）。不是就一个字节都不动。</summary>
    public static bool LooksLikeBook(string text)
    {
        foreach (var raw in SplitLines(text))
        {
            var line = StripPy(raw);
            if (line.Length > 0)
            {
                return line.StartsWith(VaultText.BookHeaderPrefix, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>按成功次数降序；次数相同的保持文件里的顺序。</summary>
    public static IReadOnlyList<VaultEntry> Sort(IEnumerable<VaultEntry> entries) =>
        entries.OrderByDescending(entry => entry.Hits).ToList();

    /// <summary>渲染整份密码本。结构：工具说明 → 备注分界 → 用户备注 → 密码分界 → 密码段。</summary>
    public static string Render(IEnumerable<VaultEntry> entries, IEnumerable<string> notes)
    {
        var head = new List<string>
        {
            VaultText.BookHeaderFirst,
            VaultText.BookHeaderSecond,
            VaultText.NotesMarker,
        };
        head.AddRange(notes);
        head.Add(VaultText.CommentMarker);

        var body = new List<string>();
        foreach (var entry in Sort(entries))
        {
            var line = NeedsEncode(entry.Password) ? EncodeRawLine(entry.Password) : entry.Password;
            body.Add(entry.Hits > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}\t{1}", line, entry.Hits)
                : line);
        }

        return string.Join('\n', head.Concat(body)) + "\n";
    }

    /// <summary>
    /// 把一个 txt 的内容切成「要追加进密码本」的密码。密码本自己那一份按密码本规则解析，
    /// 其余一律每一行原样当一个密码。
    /// </summary>
    public static IReadOnlyList<string> ImportPasswords(string text, bool vaultLike) =>
        vaultLike ? Parse(text).Select(entry => entry.Password).ToList() : SplitLines(text);

    /// <summary>
    /// 读密码本原文。顺序是 BOM、含零字节时的单字节判定、无 BOM 的宽编码猜测、单字节回退链。
    /// 顺序本身是判据：回退链最后的 latin-1 能解开任意字节流，排在宽编码前面会让乱码被当成答案。
    /// </summary>
    public static string ReadText(string path)
    {
        var raw = File.ReadAllBytes(path);
        return DecodeText(raw);
    }

    /// <summary>供断言直接传入字节用。</summary>
    public static string DecodeText(byte[] raw)
    {
        foreach (var (bom, encoding) in BomEncodings)
        {
            if (!StartsWith(raw, bom))
            {
                continue;
            }

            try
            {
                return encoding.GetString(raw, bom.Length, raw.Length - bom.Length);
            }
            catch (DecoderFallbackException)
            {
                break;
            }
        }

        if (Array.IndexOf(raw, (byte)0) >= 0)
        {
            if (CountZeros(raw) * 3 < raw.Length)
            {
                foreach (var encoding in SingleByteChain())
                {
                    if (TryDecode(raw, encoding, out var text))
                    {
                        return text;
                    }
                }
            }

            foreach (var encoding in OrderedWideCandidates(raw))
            {
                var width = encoding.CodePage is 12000 or 12001 ? 4 : 2;
                if (raw.Length % width != 0 || !TryDecode(raw, encoding, out var text))
                {
                    continue;
                }

                if (LooksLikeText(text))
                {
                    return text;
                }
            }

            // 认不出来又确实有零字节：不是文本文件，也不许硬解成 latin-1 把用户数据读成乱码。
            return Encoding.UTF8.GetString(raw);
        }

        foreach (var encoding in SingleByteChain())
        {
            if (TryDecode(raw, encoding, out var text))
            {
                return text;
            }
        }

        return Encoding.Latin1.GetString(raw);
    }

    /// <summary>拆行，与 <c>str.splitlines()</c> 同口径：`\r\n` 只算一个断点，末尾的断点不产生空行。</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            return lines;
        }

        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                lines.Add(text[start..i]);
                i += i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                start = i;
                continue;
            }

            if (IsLineBreak(ch))
            {
                lines.Add(text[start..i]);
                i++;
                start = i;
                continue;
            }

            i++;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>
    /// 去掉首尾空白，口径按 Python 的 <c>str.strip()</c>：<c>\x1c</c> 至 <c>\x1e</c> 在 Python 里算空白，
    /// 在 .NET 的 <c>char.IsWhiteSpace</c> 里不算。差这三个字符会让「整行是一个控制字符」的行留下残渣。
    /// </summary>
    internal static string StripPy(string value)
    {
        var start = 0;
        while (start < value.Length && IsPythonSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsPythonSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsPythonSpace(char ch) => char.IsWhiteSpace(ch) || ch is '\x1c' or '\x1d' or '\x1e';

    private static readonly (byte[] Bom, Encoding Encoding)[] BomEncodings =
    [
        ([0xef, 0xbb, 0xbf], new UTF8Encoding(false, true)),
        // UTF-32LE 的 BOM 前两字节与 UTF-16LE 相同，必须排在前面。
        ([0xff, 0xfe, 0x00, 0x00], new UTF32Encoding(false, false, true)),
        ([0x00, 0x00, 0xfe, 0xff], new UTF32Encoding(true, false, true)),
        ([0xff, 0xfe], new UnicodeEncoding(false, false, true)),
        ([0xfe, 0xff], new UnicodeEncoding(true, false, true)),
    ];

    private static readonly Encoding[] SingleByte = BuildSingleByteChain();
    private static Encoding[] BuildSingleByteChain()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 两档都必须严格：默认的替换回退会把「解不开」变成「解出问号」，
        // 于是回退链永远走不到下一档，latin-1 那一级也永远不会被用到。
        return
        [
            StrictUtf8,
            Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        ];
    }

    private static IReadOnlyList<Encoding> SingleByteChain() => SingleByte;

    private static IEnumerable<Encoding> OrderedWideCandidates(byte[] raw)
    {
        var atOdd = 0;
        var atEven = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 0)
            {
                continue;
            }

            if (i % 2 == 1)
            {
                atOdd++;
            }
            else
            {
                atEven++;
            }
        }

        var phases = new int[4];
        for (var p = 0; p < 4; p++)
        {
            for (var i = p; i < raw.Length; i += 4)
            {
                if (raw[i] == 0)
                {
                    phases[p]++;
                }
            }
        }

        var sixteen = atOdd >= atEven
            ? new[] { new UnicodeEncoding(false, false, true), new UnicodeEncoding(true, false, true) }
            : new[] { new UnicodeEncoding(true, false, true), new UnicodeEncoding(false, false, true) };
        var thirtyTwo = phases[1] + phases[2] + phases[3] >= phases[0]
            ? new[] { new UTF32Encoding(false, false, true), new UTF32Encoding(true, false, true) }
            : new[] { new UTF32Encoding(true, false, true), new UTF32Encoding(false, false, true) };

        return [.. sixteen, .. thirtyTwo];
    }

    /// <summary>
    /// 解出来的东西像不像文本。零字节、除换行外的控制字符、替换字符三者任一出现都不算：
    /// 错字节序解出来往往满屏控制字符，错宽度解出来往往带零字节。
    /// </summary>
    private static bool LooksLikeText(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\0' || ch == '\ufffd')
            {
                return false;
            }

            if (ch < ' ' && ch is not ('\r' or '\n' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecode(byte[] raw, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(raw);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool StartsWith(byte[] raw, byte[] prefix) => raw.AsSpan().StartsWith(prefix);

    private static int CountZeros(byte[] raw)
    {
        var count = 0;
        foreach (var b in raw)
        {
            if (b == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsLineBreak(char ch) =>
        ch is '\n' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary>
    /// TAB 后面那一截是不是次数。判据是「全是十进制数字字符」，全角数字因此也算，按字符的数值累加。
    ///
    /// 与 Python 的 <c>isdigit()</c> 刻意不同：那个还认上标与带圈数字（`²`、`①`），
    /// 而它后面紧跟的 <c>int()</c> 对这两类会抛异常，整本密码读不出来。这里判为非数字，
    /// 整行按密码原样保留 —— 比整本读不出来轻得多。
    /// </summary>
    private static int? ParseCount(string tail)
    {
        var text = StripPy(tail);
        if (text.Length == 0)
        {
            return null;
        }

        var value = 0L;
        foreach (var ch in text)
        {
            if (!char.IsDigit(ch))
            {
                return null;
            }

            value = (value * 10) + (int)char.GetNumericValue(ch);
            if (value > int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)value;
    }
}
