using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 随包物料的自查（B-2026-062）。
///
/// 那一条的形态是「读不了与干净在结果上不可区分」：三处脱敏工具都是
/// <c>except (OSError, UnicodeDecodeError): continue</c>，于是非 UTF-8 的文本被整份放行，
/// 自查还报「干净」并退出 0 —— 本机用户名与本机路径可以原样离开本机。
///
/// C# 仓不发布脱敏副本，所以这里守的是另一半：**会随包离开本机的文本**
/// 必须能按 UTF-8 读出来，且不含本机隐私。读不了即失败，不许跳过。
/// </summary>
public sealed class PublishMaterialTests
{
    /// <summary>随包文本的子集：便携包白名单里那几份人工可读的文本。
    /// 说明页按「先看源区、退回落点」两级找，与 <c>pack_release.py</c> 的 <c>packed_source</c> 同一口径；
    /// 那几份第三方声明与 GPL 全文已内联进它，包内不再单列，所以这里也不单列。</summary>
    private static IEnumerable<string> PackedTexts()
    {
        yield return Material("README.html");
        yield return Path.Combine(Repo.Root, "src", "Bbu.App", "engines", "7z", "License.txt");
        yield return Path.Combine(Repo.Root, "src", "Bbu.App", "engines", "7z", "History-7zip-zs.txt");
        yield return Path.Combine(Repo.Root, "src", "Bbu.App", "engines", "unrar", "license.txt");
    }

    /// <summary>
    /// 对外物料在仓里的路径：开发仓是 <c>docs\public\</c>，同步进公开仓之后落成 <c>docs\</c>。
    /// 同一个断言要在两个仓里都成立，所以两级都看 —— 这不是两处定义，是同一份物料在两个仓里的两种位置。
    /// </summary>
    private static string Material(string name)
    {
        var staged = Path.Combine(Repo.Root, "docs", "public", name);
        return File.Exists(staged) ? staged : Path.Combine(Repo.Root, "docs", name);
    }

    /// <summary>
    /// 随包二进制的行尾纪律（判据 3 的前提）：<c>.gitattributes</c> 必须把随包目录排除在行尾转换之外。
    ///
    /// 它们是上游给的文件（7z 那几份是 CRLF，UnRAR 那一份是二进制加许可原文），哈希记在各自的
    /// <c>SHA256SUMS</c> 里。少了这条规则，新克隆出来的那份字节与上游不一致，逐条核对必然失败 ——
    /// 而失败信息只说"哈希不符"，排查会落在文件内容上，看不出是 git 换了行尾。
    ///
    /// 两个引擎同在一个 <c>engines\</c> 下，所以规则也收成一条。
    /// </summary>
    [Fact]
    public void PackedBinaries_MustBeExcludedFromLineEndingConversion()
    {
        var attributes = File.ReadAllLines(Path.Combine(Repo.Root, ".gitattributes"));

        var rule = attributes.FirstOrDefault(line =>
            line.StartsWith("src/Bbu.App/engines/**", StringComparison.Ordinal));

        Assert.False(rule is null, ".gitattributes 里没有 src/Bbu.App/engines/** 这条规则");
        Assert.Contains("-text", rule!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Bug", "B-2026-062")]
    public void PackedTexts_MustBeReadableAndFreeOfLocalPrivacy()
    {
        var problems = PrivacyProblems(PackedTexts(), Repo.Root);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait("Bug", "B-2026-062")]
    public void PrivacyProblems_MustReportUnreadableText()
    {
        // 非 UTF-8 的字节序列：旧写法会在这里 continue，把整份文件当成干净
        var problems = WithProbe([0xff, 0xfe, 0x41, 0x42], out _);
        Assert.Contains(problems, p => p.Contains("读不了", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Bug", "B-2026-062")]
    public void PrivacyProblems_MustReportLocalPathsInReadableText()
    {
        var text = Encoding.UTF8.GetBytes("路径 " + @"C:\Users\ZZQQ_FAKE_USER\Documents" + " 与 " + @"D:\私有目录");
        var problems = WithProbe(text, out _);
        Assert.NotEmpty(problems);
    }

    [Fact]
    [Trait("Bug", "B-2026-062")]
    public void PrivacyProblems_MustStayQuietOnCleanText()
    {
        var problems = WithProbe(Encoding.UTF8.GetBytes("这一段没有任何本机痕迹"), out _);
        Assert.Empty(problems);
    }

    /// <summary>
    /// 严格读：非法字节抛异常，且**不做 BOM 检测**。
    /// <c>File.ReadAllText</c> 认 BOM 并按 BOM 指示的编码读，于是 `FF FE` 开头的坏文件
    /// 会被当成 UTF-16 顺利读出来 —— 那又是一次「读不了看起来像读到了」。
    /// </summary>
    private static string ReadStrict(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>在工作区里造一份探针文本（沙箱拒系统临时目录），跑完删掉。</summary>
    private static IReadOnlyList<string> WithProbe(byte[] content, out string path)
    {
        var dir = Path.Combine(Repo.Root, ".scratch", "publish-material-probe");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "probe.txt");
        try
        {
            File.WriteAllBytes(path, content);
            return PrivacyProblems([path], dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 严格的 UTF-8：非法字节**抛异常**。
    /// <c>Encoding.UTF8</c> 默认把读不了的字节替换成 U+FFFD 而不报错，
    /// 于是「读不了」被读成 U+FFFD 替换串 —— 那正是 B-2026-062 要堵的形态。
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 逐份读出来的问题；读不了也记一条。
    /// 这一句是本条断言的全部要点：读不了与干净必须分得开。
    /// </summary>
    internal static IReadOnlyList<string> PrivacyProblems(IEnumerable<string> files, string root)
    {
        var needles = new[]
        {
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"C:\Users\",
        }.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();

        var problems = new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                problems.Add($"随包文本不在：{file}");
                continue;
            }

            string text;
            try
            {
                text = ReadStrict(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                problems.Add($"{Path.GetFileName(file)} 读不了：{ex.GetType().Name}");
                continue;
            }

            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{Path.GetFileName(file)} 出现本机痕迹：{needle}");
                }
            }
        }

        return problems;
    }
}
