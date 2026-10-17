using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>从名字里读出来的一条密码，以及它出自哪个关键词。</summary>
public sealed record PasswordGuess(string Value, string Keyword, string Source);

/// <summary>
/// 文件名密码提取。
///
/// 策略是「关键词定位加截断」，不是一条贪婪正则：贪婪写法会把 `.rar` 吃进密码里。
/// </summary>
public static class NamePassword
{
    /// <summary>按长度降序，保证「解压密码」优先于「密码」，「解压码」优先于「码」。</summary>
    private static readonly string[] Keywords =
    [
        "解压密码", "解压码", "压缩密码", "压缩码", "提取码", "提取密码",
        "密码", "暗号", "口令",
        "password", "passwd", "pass", "pwd", "pw",
    ];

    private const string Separators = " \t:：=＝-—_";

    /// <summary>关键词后那一段的边界：空白、斜杠、反斜杠、点号。</summary>
    private static bool IsTokenStop(char ch) =>
        char.IsWhiteSpace(ch) || ch is '/' or '\\' or '.';

    /// <summary>无关键词时，扩展名后面直接贴的那一段。只认文件名，文件夹名不认。</summary>
    private static readonly string[] ArchiveExtensions =
        ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "001", "z01"];

    /// <summary>关键词那一档认定的纯扩展名，比下面的媒体名单短。</summary>
    private static readonly HashSet<string> BareExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "tar", "gz", "001", "z01",
    };

    /// <summary>贴出来的词本身就是扩展名或媒体后缀时不算密码。</summary>
    private static readonly HashSet<string> ExtensionLike = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "001", "z01",
        "mp4", "avi", "mkv", "wmv", "flv", "rmvb", "ts",
    };

    /// <summary>
    /// 从一个文件名或文件夹名里提取密码。<paramref name="allowExtensionDigits"/> 为假时关闭
    /// 「扩展名后直接贴密码」那一种写法，文件夹名按规格不认它。
    /// </summary>
    public static PasswordGuess? ExtractFromName(string name, bool allowExtensionDigits = true)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var text = name.TrimEnd('/', '\\');
        var slash = text.LastIndexOfAny(['/', '\\']);
        var baseName = slash >= 0 ? text[(slash + 1)..] : text;

        var bestIndex = -1;
        var bestKeyword = string.Empty;
        foreach (var keyword in Keywords)
        {
            var index = baseName.LastIndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            if (index > bestIndex || (index == bestIndex && keyword.Length > bestKeyword.Length))
            {
                bestIndex = index;
                bestKeyword = baseName.Substring(index, keyword.Length);
            }
        }

        if (bestIndex < 0)
        {
            return allowExtensionDigits ? FromExtensionSuffix(baseName) : null;
        }

        var tail = baseName[(bestIndex + bestKeyword.Length)..].TrimStart(Separators.ToCharArray());
        if (tail.Length == 0)
        {
            return null;
        }

        var token = TokenOf(tail);
        if (token.Length == 0)
        {
            return null;
        }

        // token 内部若又开始一个关键词，密码在它之前。
        foreach (var keyword in Keywords)
        {
            var at = token.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                token = token[..at].Trim(Separators.ToCharArray());
                break;
            }
        }

        if (token.Length == 0 || IsBareExtension(token))
        {
            return null;
        }

        return new PasswordGuess(token, bestKeyword, string.Format(VaultText.NameDetailAt, bestKeyword));
    }

    /// <summary>
    /// 先看文件名，再看父目录，最后看祖父目录：文件夹拖入时密码常常在文件夹名里。
    /// 只有文件名那一档允许「扩展名后贴密码」。
    /// </summary>
    public static PasswordGuess? ExtractFromPath(string path)
    {
        var full = Path.GetFullPath(path);
        var parts = new List<(string Name, bool IsFile)>
        {
            (Path.GetFileName(full), true),
        };

        var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? string.Empty);
        if (parent.Length > 0)
        {
            parts.Add((parent, false));
        }

        var grand = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(full) ?? string.Empty) ?? string.Empty);
        if (grand.Length > 0)
        {
            parts.Add((grand, false));
        }

        foreach (var (name, isFile) in parts)
        {
            var guess = ExtractFromName(name, isFile);
            if (guess is null)
            {
                continue;
            }

            var where = isFile ? VaultText.NameFromFile : VaultText.NameFromFolder;
            return guess with { Source = string.Format(VaultText.NamePlaceAt, where, guess.Keyword) };
        }

        return null;
    }

    /// <summary>
    /// 一条候选密码展开成若干等价写法。顺序固定：原样 → 去首尾空格 → 去掉全部空白（仅当含空白）
    /// → 全角转半角 → 全角转半角再去首尾空格。原样永远第一，变体不许抢在它前面。
    /// </summary>
    public static IReadOnlyList<string> Variants(string value)
    {
        var candidates = new List<string> { value, value.Trim() };
        if (value.Any(char.IsWhiteSpace))
        {
            candidates.Add(new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray()));
        }

        var half = TextNormalize.ToHalfWidth(value);
        candidates.Add(half);
        candidates.Add(half.Trim());

        var seen = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.Length > 0 && !seen.Contains(candidate, StringComparer.Ordinal))
            {
                seen.Add(candidate);
            }
        }

        return seen;
    }

    private static PasswordGuess? FromExtensionSuffix(string baseName)
    {
        var dot = baseName.LastIndexOf('.');
        if (dot < 0 || dot == baseName.Length - 1)
        {
            return null;
        }

        var remainder = baseName[(dot + 1)..];
        foreach (var extension in ArchiveExtensions)
        {
            if (!remainder.StartsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var password = remainder[extension.Length..].Trim(Separators.ToCharArray());
            if (password.Length == 0 || !IsTokenOnly(password))
            {
                continue;
            }

            return ExtensionLike.Contains(password)
                ? null
                : new PasswordGuess(
                    password,
                    extension.ToLowerInvariant() + VaultText.NameExtensionSuffixTag,
                    VaultText.NameFromExtensionSuffix);
        }

        return null;
    }

    /// <summary>扩展名后面那一段必须是一个完整的 token：夹着点号或分隔符就不算。</summary>
    private static bool IsTokenOnly(string value) => value.All(ch => !IsTokenStop(ch));

    private static string TokenOf(string tail)
    {
        var end = 0;
        while (end < tail.Length && !IsTokenStop(tail[end]))
        {
            end++;
        }

        return tail[..end].Trim(Separators.ToCharArray());
    }

    private static bool IsBareExtension(string token) => BareExtensions.Contains(token);
}
