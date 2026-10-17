using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 文案落点类断言：每一句用户可见文案都要有引用点，界面标记里不许留看得见的字面量，
/// 拼接出来的整行必须走共用的组装入口。
///
/// **落点类**断言（「这句话有没有人用」
/// 「这个字面量从哪来」）归 Contract 面；**仓库结构类**断言（目录、表区、清单的双向一致）归 Arch 面。
///  B-2026-220 至 223。
/// </summary>
public sealed class TextPlacementTests
{
    /// <summary>
    /// 白名单：本表已空 —— 两条待接线的死文案都已接上（询问密码浮层的「正在验证…」与「验证不了：{0}」）。
    /// 留着这个断言与这张空表，是为了让下一条「定义了却没人用」的文案在**定义它的那一刻**就被抓住：
    /// 删掉断言等于把这条闸门撤掉，问题会推迟到用户看不见它的时候才暴露。
    /// </summary>
    private static readonly string[] WiringPending = [];

    /// <summary>本仓根：从测试输出目录往上找到含 <c>Bbu.slnx</c> 的那一层。</summary>
    private static string Root { get; } = FindRoot();

    /// <summary>文案表的域文件目录。</summary>
    private static string TextsDirectory => Path.Combine(Root, "src", "Bbu.Core", "Texts");

    /// <summary>定义文案成员的写法：域文件里的 <c>public const string</c>。</summary>
    private static readonly Regex MemberDecl =
        new(@"public\s+const\s+string\s+(\w+)\s*=", RegexOptions.Compiled);

    /// <summary>界面标记里直接写死可见文本的写法：<c>Text=</c> / <c>Content=</c> 的值不以 <c>{</c> 开头。</summary>
    private static readonly Regex VisibleLiteral =
        new(@"(?:^|\s)(Text|Content)\s*=\s*""([^""{][^""]*)""", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// 文案成员必须有引用点：文案表里定义之后，<c>src</c> 与 <c>tests</c> 全量里至少还要出现一次
    /// （只出现定义那一次就是死文案：用户在界面上永远看不到它，而它还会被当成"已有说法"误导后来的改动）。
    ///
    /// 口径是外部审计 T1 扫描的 **const 子集**：语料是 <c>src</c> 与 <c>tests</c> 拼成的一份，
    /// 用词边界匹配、命中数 ≤ 1（仅定义处）即死文案。只判 <c>public const string</c>：
    /// <c>Texts\</c> 域里的静态组装入口（如 <c>ScanText.Fill</c>、<c>Ui.SettingsSaveFailed</c>）
    /// 不在覆盖面内，扩大口径要同时改这条断言。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-221")]
    public void EveryTextMember_MustHaveAReferencePoint()
    {
        var corpus = Corpus();

        var dead = new List<string>();
        foreach (var file in Directory.EnumerateFiles(TextsDirectory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (Match match in MemberDecl.Matches(File.ReadAllText(file)))
            {
                var member = $"{Path.GetFileNameWithoutExtension(file)}.{match.Groups[1].Value}";
                if (WiringPending.Contains(member, StringComparer.Ordinal))
                {
                    continue;
                }

                var name = match.Groups[1].Value;
                if (corpus.Sum(text => Regex.Matches(text, $@"\b{name}\b").Count) <= 1)
                {
                    dead.Add(member);
                }
            }
        }

        // 自带完整清单，且条数写在最前：Assert.Empty 的失败消息会被 xunit 打印器截断
        // （控制台运行器只放前 5 行或约 200 字），先报条数才看得出「一共几条、还差几条没显示」。
        var report = dead.OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.True(report.Count == 0, $"没有引用点的文案 {report.Count} 条：{string.Join("、", report)}");
    }

    /// <summary>
    /// 界面标记里不许留看得见的字面量：<c>src</c> 下所有 <c>.axaml</c> 的 <c>Text=</c> 与 <c>Content=</c>
    /// 取值必须来自文案表（<c>{x:Static}</c> 或绑定，即以 <c>{</c> 开头）。写死一处就有两处后果 ——
    /// 它躲开文案表（门禁看不到它），而全角与半角、空格这些差别只在这一处存在，与别处的同一句话对不上。
    ///
    /// 只判 <c>Text=</c> 与 <c>Content=</c> 两个属性名：<c>Watermark</c>、<c>ToolTip.Tip</c>、
    /// <c>AutomationProperties.Name</c> 那几类比它们多一层外壳，不在覆盖面内。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-222")]
    public void ViewMarkup_MustNotCarryVisibleLiterals()
    {
        var offenders = new List<string>();
        foreach (var file in Files(Path.Combine(Root, "src"), ".axaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in VisibleLiteral.Matches(text))
            {
                var line = text.Take(match.Index).Count(ch => ch == '\n') + 1;
                offenders.Add(
                    $"{Relative(file)}:{line} 的 {match.Groups[1].Value}=\"{match.Groups[2].Value}\"");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 输出设置保存失败那条提示必须走共用句 <c>Ui.SettingsSaveFailed</c>：界面层里引用存储层
    /// <c>LastError</c> 的每一行都得落在它上面。裸塞存储层错误消息的话，用户拿到的是一条没有中文归类、
    /// 可能夹英文的错误原文（B-2026-220）。
    ///
    /// 与 <c>Bbu.Core.Tests\ </c> 那条单测是一对：单测钉那句话长什么样（前缀与原文），
    /// 这条钉调用点没有绕过它 —— 只留单测的话，把调用点改回裸原文仍然全绿。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-220")]
    public void OutSettingsSaveFailure_MustGoThroughTheSharedLine()
    {
        var module = Path.Combine(Root, "src", "Bbu.Ui.Avalonia");
        var lines = Files(module, ".cs").SelectMany(file => NumberedLines(file, "LastError")).ToList();

        Assert.Contains(lines, line => line.Contains("SettingsSaveFailed", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => !line.Contains("SettingsSaveFailed", StringComparison.Ordinal));
    }

    /// <summary>
    /// 跑批顶层异常那一行必须走共用句 <c>ScanText.ItemFailed</c>：进日志面板的那一行要带中文归类与
    /// 异常原文、末尾给日志去向，不许把裸 <c>ex.Message</c> 当结论（B-2026-223）。
    /// 同样与  的单测成对。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-223")]
    public void BatchFatalLine_MustGoThroughTheSharedLine()
    {
        const string file = "src/Bbu.Ui.Avalonia/Views/MainWindow.axaml.cs";
        var text = File.ReadAllText(RepoPath(file));

        Assert.DoesNotContain(
            NumberedLines(RepoPath(file), "ex.Message"),
            line => line.Contains("OnLine(new LogRecord", StringComparison.Ordinal)
                && !line.Contains("ScanText.ItemFailed", StringComparison.Ordinal));
        Assert.Contains("ScanText.ItemFailed(", text, StringComparison.Ordinal);
    }

    /// <summary>仓库相对路径（正斜杠）转成本机绝对路径。</summary>
    private static string RepoPath(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>某个源文件里含指定字样的行，带相对路径与行号（报出来的红要能直接定位）。</summary>
    private static IEnumerable<string> NumberedLines(string file, string needle)
    {
        var lines = File.ReadAllLines(file);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains(needle, StringComparison.Ordinal))
            {
                yield return $"{Relative(file)}:{index + 1} {lines[index].Trim()}";
            }
        }
    }

    /// <summary>
    /// 语料：<c>src</c> 与 <c>tests</c> 下参与文案检索的那几种文件，逐文件整份读进来。
    /// **本文件自己不算**：白名单为空之后它仍不算 —— 这条断言的语料一旦含进本文件，
    /// 白名单里写下的常量名就凭空多出一个「引用点」，那份名单的口径随之失真。
    /// </summary>
    private static List<string> Corpus() =>
        Files(Path.Combine(Root, "src"), ".cs", ".axaml", ".cpp", ".h")
            .Concat(Files(Path.Combine(Root, "tests"), ".cs", ".axaml", ".cpp", ".h"))
            .Where(file => Path.GetFileName(file) != "TextPlacementTests.cs")
            .Select(File.ReadAllText)
            .ToList();

    /// <summary>按扩展名枚举仓库内的文件，跳过构建产物与临时目录。</summary>
    private static IEnumerable<string> Files(string directory, params string[] extensions) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .Where(file => !Path.GetRelativePath(directory, file)
                    .Split(Path.DirectorySeparatorChar)
                    .Any(name => name is "bin" or "obj" or ".scratch" or ".git" or "node_modules"))
            : [];

    private static string Relative(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bbu.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("定位不到仓库根：往上一路都没找到 Bbu.slnx");
    }
}
