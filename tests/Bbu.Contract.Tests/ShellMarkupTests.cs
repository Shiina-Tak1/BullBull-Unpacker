using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 只由样式表与视图的一段结构决定的形态：日志行分成时间戳与正文两列、时间戳右对齐、
/// 浮窗用卡片类而不是整页类。走文本断言而不是起窗口去量，是因为这几条没有可量的中间态 ——
/// 渲染图上它们也只是几行字的对齐差别。
/// </summary>
public sealed class ShellMarkupTests
{
    /// <summary>时间戳独立成列并右对齐；位宽不同的时间戳会把正文推得左右不齐。</summary>
    [Fact]
    [Trait("Bug", "B-2026-111")]
    public void LogTimestamp_MustBeItsOwnRightAlignedColumn()
    {
        var styles = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));
        var block = StyleBlock(styles, "TextBlock.lg-ts");

        Assert.Contains("TextAlignment", block, StringComparison.Ordinal);
        Assert.Contains("Right", block, StringComparison.Ordinal);
    }

    /// <summary>日志一行是两列：左时间戳、右正文，正文折行而不是横向滚动。</summary>
    [Fact]
    [Trait("Bug", "B-2026-111")]
    public void LogRow_MustSplitTheTimestampFromTheBody()
    {
        var view = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("new ColumnDefinitions(\"58,*\")", view, StringComparison.Ordinal);
    }

    /// <summary>浮窗走卡片类：它在窗口内居中，而且整页那一套已经不在样式表里了。</summary>
    [Fact]
    public void Popup_MustUseTheCardStyle()
    {
        var styles = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));
        var block = StyleBlock(styles, "Border.popup");

        Assert.Contains("CornerRadius", block, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment", block, StringComparison.Ordinal);
        Assert.Contains("Center", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Selector=\"Border.page\"", styles, StringComparison.Ordinal);
    }

    /// <summary>图标按钮的命中区补足：视觉仍是 28，可点范围取那个 44 的令牌。</summary>
    [Fact]
    public void IconButtons_MustReserveTheWideHitTarget()
    {
        var styles = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));
        var block = StyleBlock(styles, "Button.iconbtn.hit");

        Assert.Contains("HHitTarget", block, StringComparison.Ordinal);
    }

    /// <summary>行首不再有展开箭头：清单行最左即为类型格（docx 18）。</summary>
    [Fact]
    public void ListRow_MustNotCarryTheExpandArrow()
    {
        var code = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        Assert.DoesNotContain("ToggleReason", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandReason", code, StringComparison.Ordinal);
        Assert.Contains("root.Child = grid;", code, StringComparison.Ordinal);
    }

    /// <summary>表头四列居中、列间分隔线取强边框色（看图反馈：分隔线几乎不可见、表头文字过亮）。</summary>
    [Fact]
    public void ListHead_MustBeCenteredWithVisibleSeparators()
    {
        var window = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml"));
        var head = window[window.IndexOf("Name=\"HeadCols\"", StringComparison.Ordinal)..];
        head = head[..head.IndexOf("</Grid>", StringComparison.Ordinal)];

        Assert.Equal(4, Count(head, "HorizontalAlignment=\"Center\""));
        Assert.Equal(3, Count(head, "BorderThickness=\"0,0,1,0\""));
        Assert.Contains("Border2", head, StringComparison.Ordinal);
    }

    /// <summary>清单的原因标记已不在视图中（B-2026-125）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-125")]
    public void ListRow_MustNotCarryTheCornerMark()
    {
        var code = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        Assert.DoesNotContain("NameMark", code, StringComparison.Ordinal);
    }

    /// <summary>列宽把手的命中区：可点范围取令牌，视觉线宽仍固定为 7px（B-2026-122）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-122")]
    public void ColumnSplit_MustReserveTheWideHitTarget()
    {
        var styles = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));
        var block = StyleBlock(styles, "Thumb.colsplit");

        Assert.Contains("Breakpoints.SplitHit", block, StringComparison.Ordinal);
        Assert.Contains("Width=\"7\"", block, StringComparison.Ordinal);
    }

    // 设置页「四组十四行」那条搬到界面测试里去了：项与控件现在由字段描述表与模板生成，
    // XAML 里数不出行数。判据内核（组数与每一项的标题齐备）改由
    // .SettingsPage_MustKeepTheGroupsAndRows 在运行时钉住（T-10）。

    /// <summary>
    /// 主题只留深色：浅色令牌、主题切换路径、主题按钮与设置项、命令行开关都不许留。
    /// 入口唯一性是这条的核心 —— 留一个就还能切回去（docx 3）。
    /// </summary>
    [Fact]
    public void Theme_MustBeDarkOnly()
    {
        var tokens = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Tokens.axaml"));
        Assert.DoesNotContain("x:Key=\"Light\"", tokens, StringComparison.Ordinal);

        var window = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml"));
        Assert.DoesNotContain("BtnTheme", window, StringComparison.Ordinal);

        var code = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));
        Assert.DoesNotContain("ApplyTheme", code, StringComparison.Ordinal);

        var page = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "SettingsPage.axaml"));
        Assert.DoesNotContain("SetTheme", page, StringComparison.Ordinal);

        var options = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "StartupOptions.cs"));
        Assert.DoesNotContain("\"--light\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--dark\"", options, StringComparison.Ordinal);

        var texts = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Texts", "Ui.cs"));
        Assert.DoesNotContain("ThemeToLight", texts, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsTheme", texts, StringComparison.Ordinal);
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var at = text.IndexOf(token, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// 不会处理的行取「不会处理」这一句，不取状态机那六个词 —— 写「排队中」会让用户一直等它。
    /// 状态词只有 <c>Runner.StatusLabel</c> 一处结论（它还认得「用户点停止」与「因故跳过」是两回事），
    /// 视图自己拼一份就会漏掉其中一支。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-110")]
    public void NotRunnableRow_MustNotBorrowAStateLabel()
    {
        var view = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("row.Status.Text = Runner.StatusLabel(job);", view, StringComparison.Ordinal);
        Assert.DoesNotContain("job.State.Label()", view, StringComparison.Ordinal);
    }

    /// <summary>浮层关掉之后焦点回到打开它的那个控件：键盘用户不会掉回窗口开头。</summary>
    [Fact]
    [Trait("Bug", "B-2026-112")]
    public void Layer_MustRestoreFocusToTheTrigger()
    {
        var layer = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "Layer.cs"));

        Assert.Contains("Triggers", layer, StringComparison.Ordinal);
        Assert.Contains(".Focus()", layer, StringComparison.Ordinal);
    }

    /// <summary>
    /// 框架自带的强调色在深色底上是一块紫，颜色与产品的令牌无关。
    /// 设置页与两个浮层里会出现的那几类输入控件都要按令牌取色（B-2026-120）。
    /// 撤掉任意一条覆盖，这条断言就少一个选择器。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-120")]
    public void FrameworkAccent_MustBeOverriddenOnEveryInputControl()
    {
        var styles = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));

        var expected = new (string Selector, string Token)[]
        {
            ("CheckBox:checked /template/ Border#Box", "Accent"),
            ("RadioButton:checked /template/ Border#Ring", "Accent"),
            ("TextBox:focus /template/ Border#PART_BorderElement", "Accent"),
            ("ComboBox:focus /template/ Border#Background", "Accent"),
            ("ListBoxItem:selected /template/ ContentPresenter", "AccentSoft"),
        };

        var missing = expected
            .Where(item => !StyleBlock(styles, item.Selector)
                .Contains($"{{DynamicResource {item.Token}}}", StringComparison.Ordinal))
            .Select(item => item.Selector)
            .ToList();

        Assert.True(missing.Count == 0, "这些控件没有按主题令牌取色：" + string.Join("、", missing));
    }

    /// <summary>
    /// 窗口要带自己的图标。原先只设了 exe 的 ApplicationIcon，窗口本身没有 Icon，
    /// 任务栏与 Alt+Tab 上是系统默认图标（B-2026-119）。这里钉两半：视图声明了它，
    /// 且它指的确实是仓内那一份资源 —— 只写字符串而文件不在，运行期照样是空白。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-119")]
    public void Window_MustDeclareItsIconFromTheRepository()
    {
        var view = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml"));

        Assert.Contains("Icon=\"/Assets/bbu.ico\"", view, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Assets", "bbu.ico")),
            "视图声明的图标不在仓内");
    }

    /// <summary>
    /// 窗口图标与 exe 图标必须是同一份字节。UI 层不得引用入口层，所以两处各留一份；
    /// 但两份内容必须逐字节一致：任务栏与 Alt+Tab 取窗口图标，资源管理器与三处注册键取 exe 图标。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-119")]
    public void WindowIcon_MustMatchTheExecutableIcon()
    {
        var app = File.ReadAllBytes(Path.Combine(Repo.Src, "Bbu.App", "Assets", "bbu.ico"));
        var ui = File.ReadAllBytes(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Assets", "bbu.ico"));

        Assert.Equal(app.Length, ui.Length);
        Assert.True(app.SequenceEqual(ui), "窗口图标与 exe 图标不是同一份字节");
    }

    /// <summary>
    /// 品牌区那一格显示的是软件自己的图标：`Assets\bbu-logo.png`，由 `bbu.ico` 的最大帧导出。
    /// 只写一个字符串而资源不在，运行期那里就是一片空白（docx 7）。
    /// </summary>
    [Fact]
    public void BrandMark_MustShowTheProductLogo()
    {
        var view = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml"));
        var brand = view[view.IndexOf("Grid.Column=\"0\" Orientation=\"Horizontal\"", StringComparison.Ordinal)..];
        var head = brand[..brand.IndexOf("</StackPanel>", StringComparison.Ordinal)];

        Assert.Contains("Source=\"/Assets/bbu-logo.png\"", head, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Assets", "bbu-logo.png")),
            "品牌标识不在仓内");
    }

    /// <summary>
    /// 双击标题栏只许有一处实现。原先 <c>PointerPressed</c> 与 <c>DoubleTapped</c> 各接了一次，
    /// 一次双击翻两次状态，用户看到的是"有时没反应"；而同一次按下又立刻进了拖动循环，
    /// 与第二次按下抢时序（B-2026-118）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-118")]
    public void TitleBarDoubleClick_MustBeHandledInOnePlace()
    {
        var code = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("e.ClickCount == 2", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TopBar.DoubleTapped", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleTappedEvent", code, StringComparison.Ordinal);

        // 拖动不在按下那一刻起手：那会进系统移动循环，双击的第二下进不来
        var pressed = code[code.IndexOf("private void OnTitleBarPressed", StringComparison.Ordinal)..];
        var head = pressed[..pressed.IndexOf("private void OnTitleBarMoved", StringComparison.Ordinal)];
        Assert.DoesNotContain("BeginMoveDrag", head, StringComparison.Ordinal);
    }

    private static string StyleBlock(string xaml, string selector)
    {
        var start = xaml.IndexOf($"Selector=\"{selector}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"样式表里找不到 {selector}");

        var open = xaml.IndexOf('>', start);
        var close = xaml.IndexOf("</Style>", open, StringComparison.Ordinal);
        return xaml[open..close];
    }
}
