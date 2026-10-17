using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Bbu.Ui.Tests;

/// <summary>
/// 外壳：标题栏那一行在窄窗下不许被挤出可视区（
/// 一整行，最右边那三个系统按钮是这一屏唯一的出口）。
///
/// 量的对象是窗口自身：样式表在 headless 里不预装，所以每个窗口自己挂一份令牌与控件样式，
/// 量出来的宽度才和真机一致 —— 图标按钮的可点范围来自样式表的 HHitTarget。
/// </summary>
public sealed class ShellChromeTests
{
    private const double BaselineWidth = 1160;

    private const double BaselineHeight = 660;

    /// <summary>最小宽度与基准宽度下，最小化 / 最大化 / 关闭三个按钮都要完整落在窗口客户区里。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-121")]
    public void SystemButtons_MustStayInsideTheWindowInBothWidths()
    {
        foreach (var width in new[] { 700.0, BaselineWidth })
        {
            var window = Show(width, BaselineHeight);

            var stray = new List<string>();
            foreach (var name in SystemButtons)
            {
                var button = window.FindControl<Button>(name)!;
                var at = button.TranslatePoint(new Point(0, 0), window) ?? new Point(double.NaN, double.NaN);
                if (!IsInside(at, button.Bounds.Size, window.ClientSize))
                {
                    stray.Add($"{name} 在 x={at.X:0.#} y={at.Y:0.#} " +
                              $"w={button.Bounds.Width:0.#} h={button.Bounds.Height:0.#} " +
                              $"Width={button.Width} Height={button.Height}");
                }
            }

            Assert.True(
                stray.Count == 0,
                $"{width:0}px 宽时这三个按钮落在了可视区外（客户区 {window.ClientSize.Width:0.#}）：" +
                string.Join("；", stray) + " ｜ 顶栏：" + Bar(window));

            window.Close();
        }
    }

    private static readonly string[] SystemButtons = ["BtnMin", "BtnMax", "BtnClose"];

    /// <summary>
    /// 双击标题栏翻的是最大化与还原这两态，连翻两次回到原状。
    /// 三种窗口状态的真机验证在 machine 档，本条验的是翻动的恰好是最大化与还原两态（B-2026-118）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-118")]
    public void ToggleMaximize_MustFlipBetweenNormalAndMaximized()
    {
        var window = Show(BaselineWidth, BaselineHeight);

        Assert.Equal(WindowState.Normal, window.WindowState);

        ToggleMaximize(window);
        Assert.Equal(WindowState.Maximized, window.WindowState);

        ToggleMaximize(window);
        Assert.Equal(WindowState.Normal, window.WindowState);

        window.Close();
    }

    /// <summary>
    /// 最小化按钮走到 <see cref="WindowState.Minimized"/>。
    /// 原先它在窄窗下被挤出可视区（成因是标题栏那一行的宽度， B-2026-121），
    /// 本条验的是按钮的点击接线（B-2026-118）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-118")]
    public void MinimizeButton_MustMinimizeTheWindow()
    {
        var window = Show(700, BaselineHeight);

        window.FindControl<Button>("BtnMin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowState.Minimized, window.WindowState);

        window.Close();
    }

    private static void ToggleMaximize(MainWindow window)
    {
        typeof(MainWindow)
            .GetMethod("ToggleMaximize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>
    /// 窗口图标要真的解析得出来：视图里写一个字符串而资源不在，任务栏与 Alt+Tab 上照样是空白
    /// （B-2026-119）。真机验证在 machine 档，本条验的是图标资源可解析。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-119")]
    public void WindowIcon_MustResolveToAnImage()
    {
        var window = Show(BaselineWidth, BaselineHeight);

        Assert.NotNull(window.Icon);

        window.Close();
    }

    /// <summary>
    /// 设置页的勾选框取的是主题令牌的强调色。框架默认那一档（FluentTheme 的强调色）
    /// 与产品的令牌无关，在深色底上是一块紫（B-2026-120）。
    /// 撤掉 <c>Controls.axaml</c> 里那条模板就找不到这个部件，断言随之变红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-120")]
    public void CheckBox_MustTakeTheAccentFromTheThemeToken()
    {
        var page = new SettingsPage();
        var window = Host(page);
        var box = page.Part<CheckBox>("ToggleBox")!;

        box.IsChecked = true;
        Settle(window);

        var face = box.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Name == "Box");

        Assert.True(face is not null, "勾选框的模板里没有 Box 这个部件，说明用的还是框架默认模板");
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(face!.Background);
        Assert.Equal(Token("Accent"), fill.Color);

        window.Close();
    }

    /// <summary>勾选框的勾取 accent-ink：整块底色是强调色时，勾不能再是同色。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-120")]
    public void CheckBoxTick_MustTakeTheInkFromTheThemeToken()
    {
        var page = new SettingsPage();
        var window = Host(page);
        var box = page.Part<CheckBox>("ToggleBox")!;

        box.IsChecked = true;
        Settle(window);

        var tick = box.GetVisualDescendants()
            .OfType<ShapePath>()
            .FirstOrDefault(path => path.Name == "Tick");

        Assert.True(tick is not null, "勾选框的模板里没有 Tick 这个部件");
        var stroke = Assert.IsAssignableFrom<ISolidColorBrush>(tick!.Stroke);
        Assert.Equal(Token("AccentInk"), stroke.Color);

        window.Close();
    }

    /// <summary>
    /// 把一页放进装了令牌与样式表的窗口：headless 宿主不预装这两份。
    /// 主题变体定死深色 —— 真机上窗口与 <c>App.axaml</c> 都是这么定的，
    /// 让 headless 自己取会把令牌读成浅色那一套。
    /// </summary>
    private static Window Host(Control content)
    {
        var window = new Window { Content = content, Width = 900, Height = 640 };
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        // 动画时钟要手动推：headless 不跑真实计时器，带 BrushTransition 的属性会停在起始值上。
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(20);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>从令牌表的深色那一套里取一条的颜色。判据是"取的是令牌"，所以比对的是令牌本身。</summary>
    private static Color Token(string key)
    {
        var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new System.Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));
        var dark = (ResourceDictionary)tokens.ThemeDictionaries[ThemeVariant.Dark]!;
        return ((SolidColorBrush)dark[key]!).Color;
    }

    /// <summary>
    /// 三个按钮不只是"落在窗口里"：视觉那一格（模板里的 HitFace，28×28）右边还要留得住顶栏自己的内边距。
    /// 少了这一条，把按钮推到紧贴窗口边缘也算通过 —— 那时关闭按钮已经压在窗口的缩放抓手上。
    /// 量的是视觉格而不是 44 的命中格：命中格为了补足可点范围各向两边溢出 8（负 margin 抵掉视觉间距）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-121")]
    public void SystemButtons_MustKeepTheTopBarPaddingOnTheirRight()
    {
        foreach (var width in new[] { 700.0, BaselineWidth })
        {
            var window = Show(width, BaselineHeight);
            var pad = window.FindResource("PadTopBar") as Thickness? ?? default;
            var (at, size) = Face(window, "BtnClose");
            var right = at.X + size.Width;

            Assert.True(
                right + pad.Right <= window.ClientSize.Width + 0.5,
                $"{width:0}px 宽时关闭按钮的视觉右边缘在 {right:0.#}，右边只剩 " +
                $"{window.ClientSize.Width - right:0.#}，留不住顶栏的右内边距 {pad.Right:0.#}");

            window.Close();
        }
    }

    /// <summary>按钮的视觉那一格：`.iconbtn.hit` 模板里的 `HitFace`（28×28，居中在 44 的命中格里）。</summary>
    private static (Point At, Size Size) Face(MainWindow window, string name)
    {
        var button = window.FindControl<Button>(name)!;
        Control face = button.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Name == "HitFace") is { } hit
            ? hit
            : button;

        var at = face.TranslatePoint(new Point(0, 0), window) ?? new Point(double.NaN, double.NaN);
        return (at, face.Bounds.Size);
    }

    private static string Bar(MainWindow window) =>
        string.Join("，", TopBarButtons
            .Select(name => window.FindControl<Button>(name))
            .OfType<Button>()
            .Select(button =>
            {
                var at = button.TranslatePoint(new Point(0, 0), window) ?? new Point(double.NaN, double.NaN);
                return $"{button.Name}@{at.X:0.#}+{button.Bounds.Width:0.#}";
            }));

    private static readonly string[] TopBarButtons =
        ["BtnLibrary", "BtnSettings", "BtnAbout", "BtnMin", "BtnMax", "BtnClose"];

    private static bool IsInside(Point at, Size size, Size client) =>
        !double.IsNaN(at.X)
        && at.X >= -0.5
        && at.Y >= -0.5
        && at.X + size.Width <= client.Width + 0.5
        && at.Y + size.Height <= client.Height + 0.5;

    /// <summary>尺寸要在 Show 之后设：XAML 里那份基准尺寸是 InitializeComponent 写的，先设会被它盖掉。</summary>
    private static MainWindow Show(double width, double height)
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();

        window.Width = width;
        window.Height = height;

        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return window;
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new System.Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new System.Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    /// <summary>落点用一次性临时目录：默认目录会覆盖作者的 <c>config.json</c>。</summary>
    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-chrome-").FullName;
}
