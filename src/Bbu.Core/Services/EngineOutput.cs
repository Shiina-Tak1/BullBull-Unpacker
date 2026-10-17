using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 引擎输出的解码与特征词判定。编码按行挑：输出里夹着包内文件名（UTF-8 居多）而命令行与盘符走 OEM 代码页，整段只挑一种必有一边乱码。
/// 乱码不只是难看：乱码的条目名贴到验证命令行上，7z 匹配不到却返回 0，于是变成「密码通过」的假阳性。
/// </summary>
public static class EngineOutput
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// 回退链：UTF-8 严格、中文代码页、系统 ANSI 代码页、latin-1。
    ///
    /// 936 与系统 ANSI 代码页不在 .NET 的默认编码集里，取之前要先注册提供程序。注册写在建链的
    /// 方法里而不是静态构造函数里：静态字段初始化器先于构造函数体执行，放构造函数里会先取 936。
    /// </summary>
    private static readonly Encoding[] Chain = BuildChain();

    /// <summary>
    /// 把整段输出解码成文本，逐行挑编码。分隔符是 CR 与 LF 两个：7z 的进度靠 CR 原地刷新，整轮可能
    /// 一个 LF 都没有，只按 LF 切会把几十个百分比连成一行。
    /// </summary>
    public static string Decode(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var start = 0;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i == raw.Length)
            {
                builder.Append(DecodeLine(raw.AsSpan(start, i - start)));
                break;
            }

            if (raw[i] != (byte)'\n' && raw[i] != (byte)'\r')
            {
                continue;
            }

            if (raw[i] == (byte)'\r' && i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
            {
                i++; // CRLF 算一个换行
            }

            builder.Append(DecodeLine(raw.AsSpan(start, i - start)));
            builder.Append('\n');
            start = i + 1;
        }

        return builder.ToString();
    }

    /// <summary>一行的回退链。latin-1 能映射任意字节，所以最后那一档不会失败。</summary>
    public static string DecodeLine(ReadOnlySpan<byte> line)
    {
        var bytes = line.ToArray();
        foreach (var encoding in Chain)
        {
            try
            {
                var text = encoding.GetString(bytes);
                if (!text.Contains('\ufffd'))
                {
                    return text.TrimEnd('\r');
                }
            }
            catch (DecoderFallbackException)
            {
                // 这一档解不了，换下一档
            }
            catch (ArgumentException)
            {
                // 本机没有这个代码页，换下一档
            }
        }

        return Encoding.UTF8.GetString(bytes).TrimEnd('\r');
    }

    private static Encoding[] BuildChain()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return
        [
            StrictUtf8,
            Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding(0, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        ];
    }

    /// <summary>
    /// 从一行引擎输出里读进度百分比。行格式只能实测（官方只定义 <c>-bs</c> 的语义，不给格式）：7z 是
    /// <c> 63% 110 T 条目名</c>，百分比在行首；UnRAR 的百分比在行内被退格原地刷新，取最后一个。
    /// </summary>
    public static bool TryReadPercent(string line, EngineKind kind, out int percent) =>
        kind is EngineKind.UnRar
            ? TryReadTrailingPercent(line, out percent)
            : TryReadLeadingPercent(line, out percent);

    private static bool TryReadLeadingPercent(string line, out int percent)
    {
        percent = 0;
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t' || line[i] == '\b'))
        {
            i++;
        }

        var value = 0;
        var digits = 0;
        while (i < line.Length && line[i] is >= '0' and <= '9')
        {
            value = (value * 10) + (line[i] - '0');
            digits++;
            i++;
        }

        if (digits is 0 or > 3 || i >= line.Length || line[i] != '%' || value > 100)
        {
            return false;
        }

        percent = value;
        return true;
    }

    private static bool TryReadTrailingPercent(string line, out int percent)
    {
        percent = 0;
        var found = false;
        var value = 0;
        var digits = 0;
        foreach (var ch in line)
        {
            if (ch is >= '0' and <= '9')
            {
                if (digits >= 3)
                {
                    // 连了四位以上：这是条目体积一类，不是百分比，重新起数
                    digits = 0;
                    value = 0;
                }

                value = (value * 10) + (ch - '0');
                digits++;
                continue;
            }

            if (ch == '%' && digits > 0 && value <= 100)
            {
                percent = value;
                found = true;
            }

            digits = 0;
            value = 0;
        }

        return found;
    }

    /// <summary>
    /// <c>7z t &lt;包&gt; &lt;条目名&gt;</c> 里那个条目名根本没匹配上时，7z 仍然返回 0，
    /// 所以「返回 0」不能当作密码正确，必须看这几个字。
    /// </summary>
    public static bool NothingMatched(string output) =>
        ContainsAny(output, EngineWording.NothingMatchedHints);

    /// <summary>输出里有没有足以单独定罪的「密码错误」证据。</summary>
    public static bool LooksLikeWrongPassword(string output) =>
        ContainsAny(output, EngineWording.StrongWrongPasswordHints);

    /// <summary>
    /// 引擎有没有明确指出这个包读不了（编码不支持、根本不是压缩包）。
    /// 只有这一档才够停止整轮搜索，其余致命错误一律当成「这个候选不对，继续试」。
    /// </summary>
    public static bool LooksLikeStructuralFailure(string output) =>
        ContainsAny(output, EngineWording.StructuralFailureHints);

    /// <summary>
    /// 同名条目挡住了这一层：引擎要把挡路的目录或文件换成包里的条目，创建不了。
    /// 只认 7z 那一句窄特征词，见 <see cref="EngineWording.SameNameBlockedHints"/>。
    /// </summary>
    public static bool LooksLikeSameNameBlocked(string output) =>
        ContainsAny(output, EngineWording.SameNameBlockedHints);

    /// <summary>输出里有没有弱证据。只说明「可能」，不构成判决。</summary>
    public static bool HasWeakWrongPasswordHint(string output) =>
        ContainsAny(output, EngineWording.WeakWrongPasswordHints);

    /// <summary>粗判：强证据或弱证据都先按「像密码问题」记下，弱证据由知道包路径的一方再确认。</summary>
    public static bool WrongPasswordHint(string output) =>
        LooksLikeWrongPassword(output) || HasWeakWrongPasswordHint(output);

    /// <summary>没给密码却连目录都列不出来。</summary>
    public static bool LooksHeaderEncrypted(string output) =>
        ContainsAny(output, EngineWording.HeaderEncryptedHints);

    /// <summary>
    /// 引擎输出里是不是「缺分卷 / 文件缺失」？判据本身只认特征词，而光靠特征词判不了：
    /// <c>找不到指定的文件</c> 对「源包被删了」同样成立。
    /// </summary>
    public static bool LooksLikeMissingVolume(string output) =>
        ContainsAny(output, EngineWording.MissingVolumeHints);

    /// <summary>
    /// 加上「源包还在吗」的前置检查，两条都成立才算缺分卷。调用方优先用这个重载：
    /// 用纯特征词那一个会把「源包被删」误诊成「分卷不全」。
    /// </summary>
    public static bool LooksLikeMissingVolume(string output, string archive)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        return File.Exists(archive) && LooksLikeMissingVolume(output);
    }

    /// <summary>引擎原话的中文归类，七个特征词域各一句；次序即优先级，都不命中时返回空串、由调用方只照抛原话。</summary>
    public static string Conclusion(string output)
    {
        if (NothingMatched(output))
        {
            return Errors.EntryNotMatched;
        }

        if (LooksLikeWrongPassword(output))
        {
            return Errors.ExitPassword;
        }

        if (LooksLikeStructuralFailure(output))
        {
            return Errors.EngineCannotReadArchive;
        }

        if (LooksLikeSameNameBlocked(output))
        {
            return Errors.SameNameBlocksTheLayer;
        }

        if (LooksHeaderEncrypted(output))
        {
            return Errors.EncryptedHeader;
        }

        if (LooksLikeMissingVolume(output))
        {
            return Errors.VolumeIncomplete;
        }

        return HasWeakWrongPasswordHint(output) ? Errors.ChecksumMismatch : string.Empty;
    }

    /// <summary>「引擎话术 + 用户路径」的行：话术保留，路径丢掉。**本仓实测**的四种形态。</summary>
    private static readonly string[] EchoedPathPrefixes =
    [
        "Listing archive: ",
        "Testing archive: ",
        "Extracting from ",
        "Extracting  ",
    ];

    /// <summary>UnRAR 的错密码那句把条目名嵌在里面：<c>Incorrect password for &lt;条目名&gt;</c>。</summary>
    private const string UnRarWrongPasswordFor = "incorrect password for ";

    /// <summary>7z 的错误行前缀。</summary>
    private const string ErrorPrefix = "ERROR: ";

    /// <summary>7z 把路径与诊断隔开的写法。</summary>
    private const string SegmentSeparator = " : ";

    /// <summary>引擎输出里回显用户数据的那一段，判特征词前先剥掉：`-slt` 必然回显条目名（外部审计第 1 轮 `F-04`）。</summary>
    private static string WithoutEchoedData(string output)
    {
        if (output.Length == 0)
        {
            return output;
        }

        var builder = new StringBuilder(output.Length);
        foreach (var raw in output.Split('\n'))
        {
            builder.Append(StripEchoed(raw.TrimEnd('\r')));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>剥掉这一行里回显的用户数据；没有回显就原样返回。</summary>
    private static string StripEchoed(string line)
    {
        var eq = line.IndexOf(" = ", StringComparison.Ordinal);
        if (eq > 0 && IsFieldName(line.AsSpan(0, eq)))
        {
            return string.Empty;
        }

        foreach (var prefix in EchoedPathPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[..prefix.Length];
            }
        }

        if (line.StartsWith(UnRarWrongPasswordFor, StringComparison.OrdinalIgnoreCase))
        {
            return line[..(UnRarWrongPasswordFor.Length - 1)];
        }

        // 7z 把路径嵌在诊断里，两种排法都有实例：`ERROR: <路径> : <诊断>`（头加密包）与
        // `ERROR: <诊断> : <名字>`。像路径的那一段丢掉，另一段留着。
        if (line.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var head = line[ErrorPrefix.Length..];
            var sep = head.IndexOf(SegmentSeparator, StringComparison.Ordinal);
            if (sep > 0)
            {
                var before = head[..sep];
                var after = head[(sep + SegmentSeparator.Length)..];
                return ErrorPrefix + (LooksLikePath(before) ? after : before);
            }
        }

        return line;
    }

    /// <summary>只有路径那一段会带目录分隔符 `\` 或 `/`；诊断句与条目名不带。</summary>
    private static bool LooksLikePath(string text) =>
        text.Contains('\\', StringComparison.Ordinal) || text.Contains('/', StringComparison.Ordinal);

    /// <summary>`-slt` 字段行的键名形状：只由字母与空格组成。引擎的诊断行不是这个形状。</summary>
    private static bool IsFieldName(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiLetter(ch) && ch != ' ')
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsAny(string output, IReadOnlyList<string> hints)
    {
        if (output.Length == 0)
        {
            return false;
        }

        // 判定一律吃剥离后的文本：回显的用户数据不许参与特征词匹配（F-04）。
        var low = WithoutEchoedData(output).ToLowerInvariant();
        foreach (var hint in hints)
        {
            if (low.Contains(hint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
