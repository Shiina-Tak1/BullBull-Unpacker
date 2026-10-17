using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 视觉分档与动效的样式侧。
///
/// 这几条只由样式表与视图的文本决定：起窗口量不出"前景色取了表外的那一个"，
/// 也看不出"弹窗进出是不是同一条曲线"。它们正是本次复核里逐处定档的那份结论，
/// 所以按结论的形状钉住，撤掉任何一处就变红。
/// </summary>
public sealed partial class VisualDisciplineTests
{
    /// <summary>窗口根设默认字体：少了这一行，没挂等宽类的地方走框架默认，与其余各处不是同一份。</summary>
    [Fact]
    public void WindowRoot_MustCarryTheSansToken()
    {
        var xaml = View("MainWindow.axaml");

        Assert.Contains("FontFamily=\"{DynamicResource Sans}\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 前景色只许在这八个令牌里选：正文与标题、次级、弱化、强调底上的字，加四个语义色。
    /// 表外的取值会让"哪一档用在哪"重新散掉。
    /// </summary>
    [Fact]
    public void Foreground_MustStayInsideTheForegroundTable()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text", "Ink", "Dim", "Faint", "AccentInk", "Accent", "Ok", "Warn", "Err",
        };

        foreach (var file in Sources())
        {
            var name = Path.GetFileName(file);
            foreach (var key in ForegroundKeys(File.ReadAllText(file)))
            {
                Assert.True(allowed.Contains(key), $"{name}：前景色取了对照表之外的 {key}");
            }
        }
    }

    /// <summary>
    /// 等宽域只剩日志面板两处：时间戳与正文。视图里不再有任何地方直接挂等宽类 ——
    /// 一挂，那一处就与界面其余文字不是同一份字体，数字看起来就成了两种。
    /// </summary>
    [Fact]
    public void MonoDomain_MustBeOnlyTheLogPanel()
    {
        var styles = Styles();

        foreach (var selector in new[] { "TextBlock.lg-ts", "TextBlock.logline" })
        {
            Assert.Contains("DynamicResource Mono", StyleBlock(styles, selector), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Selector=\"TextBlock.mono\"", styles, StringComparison.Ordinal);

        foreach (var file in ViewFiles())
        {
            Assert.DoesNotContain("mono", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 字号只有五档且全是整数：页标题 14、浮层主名 16、正文与值 13、按钮与说明 12、日志等宽 12。
    /// 半号档会在一行里凑出两种几乎一样的大小，看起来像没对齐。
    /// </summary>
    [Fact]
    public void FontSizes_MustStayOnTheIntegerScale()
    {
        var tokens = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Tokens.axaml"));
        var keys = FontSizePattern().Matches(tokens).Select(match => match.Groups[1].Value).ToArray();

        Assert.Equal(["FsBody", "FsSmall", "FsTitle", "FsBrand", "FsDisplay", "FsMono"], keys);

        foreach (Match match in FontSizePattern().Matches(tokens))
        {
            Assert.DoesNotContain(".", match.Groups[2].Value, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 悬停：区域加深一档，文字往上提一档 —— 但只提到 `Ink`，不许跳到最亮的 `Text`。
    /// 另外按钮的常态底必须取 `Clear`（#00000000）而不是 `Transparent`（#00FFFFFF）：
    /// 白透插值到实色会经过半透明白，看着像闪一下白。
    /// </summary>
    [Fact]
    public void Hover_MustDarkenTheAreaAndLiftTheInkByOneStep()
    {
        var styles = Styles();

        foreach (var selector in new[]
        {
            "Button.tbtn:pointerover", "Button.iconbtn:pointerover",
            "ToggleButton.seg:pointerover", "Button.outsum:pointerover",
        })
        {
            var block = StyleBlock(styles, selector);
            Assert.Contains("DynamicResource Ink", block, StringComparison.Ordinal);
            Assert.DoesNotContain("DynamicResource Text", block, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Value=\"Transparent\"", styles, StringComparison.Ordinal);

        // 只禁 Transparent 是反向判据：常态底换成别的半透明色仍会经过中间值；正向钉住 Button.bb 取 Clear。
        Assert.Contains("DynamicResource Clear", StyleBlock(styles, "Button.bb"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 命中区补足的图标按钮不许写 Background：它的特异性与 `.iconbtn:pointerover` 相同，
    /// 写在这里会盖住悬停底色，顶栏靠左那三个图标因此一直没有反馈（B-2026-135）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-135")]
    public void HitTargetIcons_MustNotShadowTheirHover()
    {
        Assert.DoesNotContain(
            "Setter Property=\"Background\"",
            StyleBlock(Styles(), "Button.iconbtn.hit"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 白档只许出现在标题与输入值：表头、只读的值、字段名、密码列、活动卡主名一律取中档。
    /// 这几处原先没写前景色，继承窗口根那一份，于是它们比标题还亮
    /// —— 2026-10-06 第二次梳理收窄白档时逐处补上的，撤掉任一处即红。
    /// </summary>
    [Fact]
    public void WhiteInk_MustStayOnTitlesAndInputs()
    {
        foreach (var (file, name) in new[]
        {
            ("MainWindow.axaml", "ActName"),
            ("SettingsPage.axaml", "ShellState"),
            ("SettingsPage.axaml", "DataDirValue"),
            ("PasswordDialog.axaml", "TxtArchive"),
        })
        {
            Assert.Contains("dim", Element(file, name), StringComparison.OrdinalIgnoreCase);
        }

        var library = View("LibraryPage.axaml");
        Assert.Contains("Classes=\"dim\" Text=\"{x:Static t:Ui.ColumnPassword}\"", library, StringComparison.Ordinal);
        Assert.Contains("Classes=\"dim\" Text=\"{Binding Password}\"", library, StringComparison.Ordinal);
    }

    /// <summary>
    /// 不吃窗口根继承的那几类控件各自显式取 Sans。
    /// 它们的 ControlTheme 由 FluentTheme 给，而 ControlTheme 的 Setter 优先于继承值。
    /// </summary>
    [Fact]
    public void ControlsWithOwnTheme_MustCarryTheSansToken()
    {
        var styles = Styles();

        foreach (var selector in new[]
        {
            "TextBox", "ComboBox", "ComboBoxItem", "ListBox", "ListBoxItem",
            "NumericUpDown", "MenuItem", "ToolTip",
        })
        {
            Assert.Contains("DynamicResource Sans", StyleBlock(styles, selector), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 浮层的进与出各一条曲线、各挂一个类：挂 `open` 时 260ms 走弹簧曲线，挂 `closing` 时 200ms 走退场曲线。
    /// 出场的位移只有入场的一半（9 与 18、8 与 16）：退场比进场短，观感不拖尾。
    /// </summary>
    [Fact]
    public void Overlay_MustUseOneCurveForEnteringAndAnotherForLeaving()
    {
        var styles = Styles();

        foreach (var card in new[] { "Border.modal", "Border.popup" })
        {
            var entering = StyleBlock(styles, card + ".open");
            var leaving = StyleBlock(styles, card + ".closing");

            Assert.Contains("EaseSpring", entering, StringComparison.Ordinal);
            Assert.Contains("0.260", entering, StringComparison.Ordinal);
            Assert.Contains("EaseExit", leaving, StringComparison.Ordinal);
            Assert.Contains("0.200", leaving, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 浮层的淡入写在样式表的动画定义里：`open` 类既带 `Style.Animations`，`0%` 帧与 `100%` 帧之间
    /// 又是 0 到 1。这两端不在运行时量，动画进度按真实时间累加、不按帧。
    ///
    /// 陷阱：`Border.popup.open` 那一块的 `Value="1"` 有两处 —— 类上的 Setter 与 `100%` 帧。
    /// 只搜文本会把 Setter 当成交代了 `100%` 帧，动画改成从 1 到 1 也不会红，所以先切到关键帧再判。
    /// </summary>
    [Fact]
    public void Overlay_MustDeclareItsFadeInKeyFrames()
    {
        var styles = Styles();
        var atRest = StyleBlock(styles, "Border.popup");
        var entering = StyleBlock(styles, "Border.popup.open");

        Assert.Contains("<Style.Animations>", entering, StringComparison.Ordinal);
        Assert.Contains("Property=\"Opacity\" Value=\"0\"", atRest, StringComparison.Ordinal);

        var head = entering[..entering.IndexOf("<Style.Animations>", StringComparison.Ordinal)];
        Assert.Contains("Property=\"Opacity\" Value=\"1\"", head, StringComparison.Ordinal);

        Assert.Contains("Property=\"Opacity\" Value=\"0\"", KeyFrame(entering, "0%"), StringComparison.Ordinal);
        Assert.Contains("Property=\"Opacity\" Value=\"1\"", KeyFrame(entering, "100%"), StringComparison.Ordinal);
    }

    /// <summary>按下缩到 98%；只去掉过渡会在按下瞬间跳变。</summary>
    [Fact]
    public void PressedButton_MustShrink()
    {
        var styles = Styles();

        Assert.Contains("scale(0.98)", StyleBlock(styles, "Button.btn:pressed"), StringComparison.Ordinal);
    }

    /// <summary>日志面板的高度上限带动画，数值仍只有 Breakpoints 一处来源（样式表里不写第二份）。</summary>
    [Fact]
    public void FoldedLogPanel_MustAnimateItsHeightCap()
    {
        var styles = Styles();

        Assert.Contains("MaxHeight", StyleBlock(styles, "Grid.logpanel"), StringComparison.Ordinal);

        var code = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("LogPanel.MaxHeight", code, StringComparison.Ordinal);
        Assert.Contains("Breakpoints.FoldOpen", code, StringComparison.Ordinal);
    }

    /// <summary>确认框的层叠顺序压过四个宿主：新增浮层时不能把它挤下去。</summary>
    [Fact]
    public void ConfirmPrompt_MustStayOnTopOfEveryOverlay()
    {
        var xaml = View("MainWindow.axaml");
        var hosts = new[]
        {
            "Name=\"OverlayHost\"", "Name=\"OutPanel\"", "Name=\"PopupHost\"", "Name=\"PwHost\"", "Name=\"Ask\"",
        };

        var previous = -1;
        foreach (var host in hosts)
        {
            var at = xaml.IndexOf(host, StringComparison.Ordinal);
            Assert.True(at >= 0, $"MainWindow.axaml 里找不到 {host}");
            Assert.True(at > previous, $"{host} 的层叠顺序变了：确认框必须排在四个宿主之后");
            previous = at;
        }
    }

    /// <summary>确认框限高且正文可滚：内容一长不能把标题与按钮顶出窗口。</summary>
    [Fact]
    public void ConfirmPrompt_MustCapItsHeightAndScrollTheBody()
    {
        var xaml = View("ConfirmPrompt.axaml");

        Assert.Contains("MaxHeight=\"{DynamicResource HPopupFixed}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 三个模态里肯定按钮的源码序位恒小于否定按钮，判据是「肯定在左、取消在右」这条行为本身：
    /// 右侧横排栈与两列栅格的可视左右序都由源码序位定，所以钉序位就是钉那条判据，
    /// 不去钉某一行的类名写法 —— 类名随按钮角色换（B-2026-316）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-316")]
    public void ModalButtons_MustPutTheAffirmativeOneBeforeTheNegative()
    {
        foreach (var (file, affirmative, negative) in new[]
        {
            ("ConfirmPrompt.axaml", "PromptOk", "PromptCancel"),
            ("PasswordDialog.axaml", "BtnVerify", "BtnSkip"),
            ("MainWindow.axaml", "OutSave", "OutCancel"),
        })
        {
            var xaml = View(file);
            var left = xaml.IndexOf($"Name=\"{affirmative}\"", StringComparison.Ordinal);
            var right = xaml.IndexOf($"Name=\"{negative}\"", StringComparison.Ordinal);

            Assert.True(left >= 0, $"{file} 里找不到肯定按钮 {affirmative}");
            Assert.True(right >= 0, $"{file} 里找不到否定按钮 {negative}");
            Assert.True(left < right, $"{file}：否定按钮 {negative} 排到了肯定按钮 {affirmative} 之前");
        }
    }

    /// <summary>
    /// 柔和色按族判据只作底色：凡名字含 <c>Soft</c> 的令牌都不许出现在任何 <c>Foreground</c> 位置。
    /// 成员表从令牌表现读，不写死名单 —— 新增一条柔和色自动进判据（B-2026-316）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-316")]
    public void SoftTokens_MustNeverBeUsedAsForeground()
    {
        var soft = SoftTokenKeys();

        // 家族为空说明命名口径变了，这条判据就成了空转。
        Assert.NotEmpty(soft);

        foreach (var file in Sources())
        {
            var name = Path.GetFileName(file);
            foreach (var key in ForegroundKeys(File.ReadAllText(file)))
            {
                Assert.False(soft.Contains(key), $"{name}：柔和色 {key} 用在了前景位置");
            }
        }
    }

    private static IEnumerable<string> Sources()
    {
        yield return Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml");

        foreach (var file in ViewFiles())
        {
            yield return file;
        }
    }

    private static IEnumerable<string> ViewFiles() =>
        Directory.EnumerateFiles(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views"), "*.axaml");

    private static IEnumerable<string> ForegroundKeys(string xaml)
    {
        foreach (Match match in DirectPattern().Matches(xaml))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in SetterPattern().Matches(xaml))
        {
            yield return match.Groups[1].Value;
        }
    }

    private static string View(string fileName) =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", fileName));

    /// <summary>
    /// 品牌图形的两处取值：边长取按钮那一档字号（与文字等高），缩放交给 <c>Stretch</c>。
    /// 这里读样式文本而不是量渲染尺寸 —— headless 档不加载主题字典，<c>DynamicResource</c> 取不到值。
    /// </summary>
    [Fact]
    public void BrandGlyphs_MustTakeTheirSizeFromTheButtonScale()
    {
        var block = StyleBlock(Styles(), "Path.brand");

        Assert.Contains("Width\" Value=\"{DynamicResource FsSmall}", block, StringComparison.Ordinal);
        Assert.Contains("Height\" Value=\"{DynamicResource FsSmall}", block, StringComparison.Ordinal);
        Assert.Contains("Stretch\" Value=\"Uniform", block, StringComparison.Ordinal);
    }

    /// <summary>令牌表里名字含 <c>Soft</c> 的那一族：族判据的成员表，不另抄一份名单。</summary>
    private static HashSet<string> SoftTokenKeys()
    {
        var tokens = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Tokens.axaml"));

        return new HashSet<string>(
            SoftKeyPattern().Matches(tokens).Select(match => match.Groups[1].Value),
            StringComparer.Ordinal);
    }

    /// <summary>取某个 Name 的元素整块文本：从名字起到该元素自己的结束尖括号。</summary>
    private static string Element(string fileName, string name)
    {
        var xaml = View(fileName);
        var at = xaml.IndexOf($"Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{fileName} 里找不到 {name}");

        return xaml[at..xaml.IndexOf('>', at)];
    }

    private static string Styles() =>
        File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));

    private static string StyleBlock(string xaml, string selector)
    {
        var start = xaml.IndexOf($"Selector=\"{selector}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"样式表里找不到 {selector}");

        var open = xaml.IndexOf('>', start);
        var close = xaml.IndexOf("</Style>", open, StringComparison.Ordinal);
        return xaml[open..close];
    }

    /// <summary>取某个关键帧的整块文本：从它的 Cue 起到该帧自己的结束标签。</summary>
    private static string KeyFrame(string block, string cue)
    {
        var at = block.IndexOf($"KeyFrame Cue=\"{cue}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"动画里找不到 {cue} 关键帧");

        var open = block.IndexOf('>', at);
        var close = block.IndexOf("</KeyFrame>", open, StringComparison.Ordinal);
        return block[open..close];
    }

    [GeneratedRegex("x:Key=\"(Fs[A-Za-z]+)\">([0-9.]+)<")]
    private static partial Regex FontSizePattern();

    [GeneratedRegex("Foreground=\"\\{DynamicResource ([A-Za-z0-9]+)\\}")]
    private static partial Regex DirectPattern();

    /// <summary>令牌表里的键名，取名字含 <c>Soft</c> 的那一族。</summary>
    [GeneratedRegex("x:Key=\"([A-Za-z0-9]*Soft[A-Za-z0-9]*)\"")]
    private static partial Regex SoftKeyPattern();

    [GeneratedRegex("Property=\"Foreground\" Value=\"\\{DynamicResource ([A-Za-z0-9]+)\\}")]
    private static partial Regex SetterPattern();
}
