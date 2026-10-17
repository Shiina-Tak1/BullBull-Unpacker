using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 文案级别的接线门禁：四档样式齐备，且级别只在出口被指派。
///
/// 破了它的后果是静默的：样式少一条，那一档的正文就取不到色；视图自己判级别，同一句就会
/// 在扫描期与穿透期显示成两种颜色（B-2026-107）。所以把它写成断言，而不是靠记住。
/// </summary>
public sealed class TextLevelWiringTests
{
    /// <summary>文案域＝<c>Bbu.Core.Texts</c> 里带 <c>public const string</c> 的公开静态类。</summary>
    private static IEnumerable<Type> TextDomains() =>
        typeof(ScanText).Assembly
            .GetTypes()
            .Where(type => type.IsAbstract && type.IsSealed && type.IsPublic)
            .Where(type => type.Namespace == typeof(ScanText).Namespace)
            .Where(type => Constants(type).Any());

    private static IEnumerable<FieldInfo> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string));

    /// <summary>
    /// 级别到颜色落在样式表里：四个级别各一条，外加运行中那一格的强调色。
    /// 视图只说「这是哪一级」，改配色就只动 <c>Tokens.axaml</c>。
    /// </summary>
    [Fact]
    public void FourLevelClasses_MustExistInTheTheme()
    {
        var controls = File.ReadAllText(
            Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));

        foreach (var level in Enum.GetValues<LogLevel>())
        {
            var name = "lvl-" + level.ToString().ToLowerInvariant();
            Assert.Contains($"Selector=\"TextBlock.{name}\"", controls, StringComparison.Ordinal);
        }

        Assert.Contains("Selector=\"TextBlock.lvl-accent\"", controls, StringComparison.Ordinal);

        var tokens = File.ReadAllText(
            Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Tokens.axaml"));

        // 主题字典里每一套都要有这四个语义色：缺一个就会出现「这一句取不到色」。
        // 套数不再写死为 2 —— 只留深色，判据是主题字典里每一套都带齐这四个语义色。
        var variants = tokens.Split("x:Key=\"Dark\"").Length - 1
            + tokens.Split("x:Key=\"Light\"").Length - 1;
        Assert.True(variants >= 1, "主题字典是空的");

        foreach (var key in new[] { "Ok", "Warn", "Err", "Dim" })
        {
            var hits = tokens.Split($"x:Key=\"{key}\"").Length - 1;
            Assert.True(hits >= variants, $"token {key} 只有 {hits} 份，{variants} 套主题各要一份");
        }
    }

    /// <summary>
    /// 级别只在出口被指派：视图层不许写 <c>LogLevel.X</c> 字面量，唯一的例外是
    /// <c>Views\Levels.cs</c>（级别到视觉的映射本身）。**没有第二处放宽**。
    ///
    /// 破了它的后果是同一条现象两种颜色：同一句在扫描期是警示色、经另一个入口变中性色
    /// （B-2026-107）。这一条只判"谁在指派"，级别取值合不合理是写日志那一处的裁决。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-107")]
    public void UiLayer_MustNotAssignTextLevels()
    {
        var literal = new Regex(@"LogLevel\s*\.\s*(Debug|Info|Warn|Error)\b");

        // 反向对照：正则必须认得出每一个现行成员。改名之后这里先红，
        // 门禁不许静默变成「一条都命不中」—— 换名那次把三支打空过，只剩 Warn 还能命中。
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            Assert.Matches(literal, $"LogLevel.{level}");
        }

        var offenders = new List<string>();
        foreach (var file in Repo.Files(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia"), ".cs"))
        {
            // Levels.cs 就是级别到视觉的映射本身。
            if (Path.GetFileName(file) == "Levels.cs")
            {
                continue;
            }

            var source = File.ReadAllText(file);
            var lines = source.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!literal.IsMatch(lines[index]))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(Repo.Root, file)}:{index + 1}");
            }
        }

        Assert.Empty(offenders);
    }


    /// <summary>
    /// 识别层不自己判级：那句文案的级别只由写它的出口给。
    /// 它同时是"扫一个普通目录整片染色"的根因（B-2026-107）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-107")]
    public void RecognitionLayer_MustNotOwnASecondLevelTable()
    {
        var probe = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Probe.cs"));

        Assert.DoesNotContain("NoteLevel", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// 穿透那条日志通道要带级别：识别层的诊断行从它过，丢一次级别就出现"同一现象两种颜色"。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-107")]
    public void PierceChannel_MustCarryTheLevel()
    {
        var pierce = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "Pierce.cs"));

        Assert.DoesNotContain("_ = level;", pierce, StringComparison.Ordinal);
        Assert.DoesNotContain("Action<string>? Log", pierce, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两种被取代的写法不许回潮：无空格的层号（应为「第 {0} 层」）、拉丁秒单位（应为「秒」）。
    ///
    /// 不能用禁用说法表（R21）兜：它的归一化会砍掉空白，错形态 <c>第{0}层</c> 与唯一说法 <c>第 {0} 层</c>
    /// 归一后同串 —— 登记错形态会把要保留的正确形态自己判红。故按**原始串**匹配，两个模式各带负向边界。
    /// 撤掉任意一处改写即红。原本还有第三个模式（<c>(?&lt;!尚)未解压</c>），2026-10-15 撤掉：「未解压」与
    /// 「尚未解压」两处都合法（`NoSpaceAt` 是已拒绝、`LeftoverReportAt` 是没轮到），裸子串分不开这两者，
    /// 留着会误判 `NoSpaceAt`。这一对的区分只能靠人（见 `90-` 的遗留风险）。
    /// </summary>
    [Fact]
    public void SourceTexts_MustNotUseTheSupersededForms()
    {
        var checks = new (string What, Regex Pattern)[]
        {
            ("无空格的层号形态（应为「第 {0} 层」）", new Regex(@"第\{[0-9]+\}层")),
            ("拉丁秒单位（应为「秒」）", new Regex(@"\{[0-9]+:(?:F1|[0-9]+)\}s")),
        };

        var offenders = checks
            .SelectMany(check => TextDomains()
                .SelectMany(domain => Constants(domain)
                    .Select(field => (Domain: domain.Name, Name: field.Name,
                                      Text: (string)field.GetRawConstantValue()!))
                    .Where(item => check.Pattern.IsMatch(item.Text))
                    .Select(item => $"{check.What} —— {item.Domain}.{item.Name} = {item.Text}")))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 0 实参的站点，模板里不许有花括号：四个语义入口都过同一处 <c>ScanText.Fill</c>，
    /// 0 实参传一个带占位符的模板会当场抛。⚠ 真身与替身走的是**同一个** <c>Fill</c>
    /// （零实参时原样返回模板），所以这条判据在测试侧与生产侧等价，没有"替身更宽容"的缺口。
    ///
    /// 判据只覆盖"能解到常量"的站点，所以解不到的也要报出来（**今天 1 处**，即下面那条已确认的例外）——
    /// 否则它会随模板写法悄悄少覆盖一块，而少覆盖的那块正是它要防的失守。
    /// </summary>
    [Fact]
    public void ZeroArgSites_MustNotUseTemplatesWithPlaceholders()
    {
        var templates = TextDomains()
            .SelectMany(domain => Constants(domain)
                .Select(field => (Key: $"{domain.Name}.{field.Name}", Text: (string)field.GetRawConstantValue()!)))
            .ToDictionary(item => item.Key, item => item.Text, StringComparer.Ordinal);

        var offenders = new List<string>();
        var unresolved = new List<string>();
        var sites = 0;
        foreach (var file in Repo.Files(Repo.Src, ".cs"))
        {
            var (bad, unknown, seen) = ZeroArgSites(File.ReadAllText(file), templates);
            var name = Path.GetRelativePath(Repo.Root, file);
            offenders.AddRange(bad.Select(site => $"{name}{site}"));
            unresolved.AddRange(unknown.Select(site => $"{name}{site}"));
            sites += seen;
        }

        Assert.True(offenders.Count == 0,
            $"0 实参 × 带占位符的模板：运行时当场抛 —— {string.Join("；", offenders)}");
        // 已确认的例外恰好 1 处：MainWindow 的 blocked（BlockingReason 的返回，无占位符）。
        // 不许改成站点点名常量：那样 BlockingReason 加一支时日志会失真。数量变即红。
        Assert.True(unresolved.Count == 1,
            $"解不到模板常量的 0 实参站点只许是那一处已确认的例外：{string.Join("；", unresolved)}");

        // 覆盖下限：枚举自己坏掉时（`Repo.Files` 取空、扩展名写错），上面两条会**静默绿** ——
        // 「一个文件都没扫到」与「扫过且都合规」必须分得开。今天全仓 6 处；真的少了就改这个下限。
        Assert.True(sites >= 6, $"只扫到 {sites} 个 0 实参站点 —— 少于 6 个说明枚举坏了，不是「今天没有站点」");
    }

    /// <summary>
    /// 反向对照：同一条判法传入人造样本 —— 带花括号的必须报、不带的不许报。
    /// 检测器自己坏掉时（常量表读空、正则写错）它会先红，而不是安静地放过每一个站点。
    /// </summary>
    [Fact]
    public void ZeroArgSites_MustFlagASyntheticBadSample()
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Fake.Braced"] = "开始 {0} 结束",
            ["Fake.Plain"] = "没有占位符",
        };

        var (bad, unknown, sites) = ZeroArgSites("_log.Note(Fake.Braced);\n_log.Note(Fake.Plain);", templates);

        Assert.Equal(2, sites);        // 两条都被看见 —— 覆盖下限那一半的自检
        Assert.Single(bad);
        Assert.Empty(unknown);
    }

    /// <summary>
    /// 扫 0 实参的日志站点：返回带花括号的（违规）、解不到模板的（没覆盖到）、以及**看见的站点数**。
    /// 只认顶层无逗号的调用 —— 有实参就一定会走 <c>Fill</c>，不在本判据里。
    /// </summary>
    private static (List<string> Offenders, List<string> Unresolved, int Sites) ZeroArgSites(
        string source, IReadOnlyDictionary<string, string> templates)
    {
        var aliases = Regex.Matches(source, @"using\s+(\w+)\s*=\s*Bbu\.Core\.Texts\.(\w+)\s*;")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        var offenders = new List<string>();
        var unresolved = new List<string>();
        var sites = 0;

        foreach (Match call in Regex.Matches(source, @"\.(?:Trace|Note|Problem|Failure)\s*\("))
        {
            var depth = 1;
            var commas = 0;
            var argument = new System.Text.StringBuilder();
            for (var index = call.Index + call.Length; index < source.Length; index++)
            {
                var ch = source[index];
                if (ch == '"' || ch == '\'')
                {
                    var quote = ch;
                    argument.Append(ch);
                    for (index++; index < source.Length && source[index] != quote; index++)
                    {
                        if (source[index] == '\\')
                        {
                            argument.Append(source[index]);
                            index++;
                        }

                        if (index < source.Length)
                        {
                            argument.Append(source[index]);
                        }
                    }

                    if (index < source.Length)
                    {
                        argument.Append(source[index]);
                    }

                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
                else if (ch == ',' && depth == 1)
                {
                    commas++;
                }

                argument.Append(ch);
            }

            if (commas > 0)
            {
                continue;                                   // 有实参：一定走 Fill
            }

            var text = string.Join(" ", argument.ToString().Split(' ', '\t', '\r', '\n')).Trim();
            if (text.Length == 0)
            {
                continue;                                   // 空实参表：四个入口都编译不过，不是本判据的对象
            }

            var line = source.Take(call.Index).Count(ch => ch == '\n') + 1;
            sites++;
            var literal = Resolve(text, aliases, templates);
            if (literal is null)
            {
                unresolved.Add($":{line} {text}");
            }
            else if (literal.Contains('{', StringComparison.Ordinal) || literal.Contains('}', StringComparison.Ordinal))
            {
                offenders.Add($":{line} {text} = {literal}");
            }
        }

        return (offenders, unresolved, sites);
    }

    /// <summary>站点的模板实参解到常量字面量：域别名先还原（<c>UiText</c> → <c>Ui</c>）；解不到返回 null。</summary>
    private static string? Resolve(string argument, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> templates)
    {
        if (argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"')
        {
            return argument[1..^1];
        }

        var dot = argument.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }

        var domain = argument[..dot];
        if (aliases.TryGetValue(domain, out var real))
        {
            domain = real;
        }

        return templates.TryGetValue($"{domain}.{argument[(dot + 1)..]}", out var literal) ? literal : null;
    }
}
