using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Ui.Avalonia.Theme;
using Bbu.Ui.Avalonia.Views;
using Xunit;
// 裸写 Ui 会落到外层命名空间 Bbu.Ui。
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 一屏的形态：基准窗口、任务表列宽、浮窗与运行信息那一格。
/// 这些结论直接可判，钉成数字比逐张比对截图快得多。
/// </summary>
public sealed class ShellFormTests
{
    /// <summary>基准窗口下日志栏的宽度。任务表四列的默认宽度要在这个前提之下算。</summary>
    private static readonly double LogColumn = Breakpoints.SideWidth(BaseWidth);

    private const double BaseWidth = 1160;

    private const double BaseHeight = 660;

    /// <summary>
    /// 任务表四列的默认宽度合计不许超过基准窗口减去日志栏之后的宽度 ——
    /// 1.1.0 那组默认值合计 1050，在基准窗口下会把状态列整个挤出可视区。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-109")]
    public void DefaultColumns_MustFitBesideTheLogColumn()
    {
        var total = ColumnLayout.Default().Sum();

        Assert.True(
            total <= BaseWidth - LogColumn,
            $"四列默认合计 {total:0.#}px，任务区只有 {BaseWidth - LogColumn:0.#}px");
    }

    /// <summary>
    /// 最小宽度取 700：判档只有两档，700 落在中档。
    /// </summary>
    [AvaloniaFact]
    public void Window_MustUseTheBaselineSize()
    {
        var window = Show();

        Assert.Equal(BaseWidth, window.Width);
        Assert.Equal(BaseHeight, window.Height);
        Assert.Equal(700, window.MinWidth);
        Assert.Equal(620, window.MinHeight);
        Assert.Equal(ShellTier.Mid, Breakpoints.Of(window.MinWidth));

        window.Close();
    }

    /// <summary>
    /// 两个浮窗共用一张居中的卡：不是整页，也不是右侧抽屉。
    /// 居中与默认宽度写在样式与资源里，那两处由架构断言读样式表核对；
    /// 这里钉的是它在树里、类挂对了、尺寸上限真算得出来。
    /// </summary>
    [AvaloniaFact]
    public void PopupCard_MustBeACenteredCardInsideTheWindow()
    {
        var window = Show();
        var card = window.FindControl<Border>("PopupCard")!;

        Assert.Contains("popup", card.Classes);
        Assert.True(card.MaxWidth > 0 && card.MaxHeight > 0, "浮窗的尺寸上限没算出来");
        Assert.True(card.MaxWidth <= window.Bounds.Width * 0.79, "浮窗上限超过了窗口的 78%");

        window.Close();
    }

    /// <summary>运行信息那一格初始就是当前活动，不是空白，也不是上一批结果。</summary>
    [AvaloniaFact]
    public void ActivitySlot_MustStartAsCurrentActivity()
    {
        var window = Show();

        Assert.Equal(UiText.ActivityTitle, window.FindControl<TextBlock>("ActTitle")!.Text);
        Assert.Equal(UiText.ActivityIdle, window.FindControl<TextBlock>("ActSummary")!.Text);

        window.Close();
    }

    /// <summary>
    /// 顶栏中独立放置的图标按钮走「命中区补足」那一类：类名正确是必要条件，
    /// 补足后的宽度要真落在按钮上 —— 样式表里定义在后面的 <c>.cap</c> 曾把三个窗口按钮压回 28，
    /// 类名照旧挂着，可点范围却缩了（B-2026-121）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-121")]
    public void TopBarIconButtons_MustCarryTheWideHitTarget()
    {
        var window = Show();
        var expected = (double)window.FindResource("HHitTarget")!;

        foreach (var name in new[] { "BtnLibrary", "BtnSettings", "BtnAbout", "BtnMin", "BtnMax", "BtnClose" })
        {
            var button = window.FindControl<Button>(name)!;

            Assert.Contains("hit", button.Classes);
            Assert.Equal(expected, button.Bounds.Width);
            Assert.Equal(expected, button.Bounds.Height);
        }

        window.Close();
    }

    /// <summary>
    /// 列宽把手走同一类命中区补足：可点范围取 Breakpoints.SplitHit，视觉线宽仍为 7px；
    /// 加宽可点范围不得同时加粗线条，两者分别计算（B-2026-122）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-122")]
    public void ColumnSplitters_MustCarryTheWideHitTarget()
    {
        var window = Show();
        var expected = Breakpoints.SplitHit;

        foreach (var name in new[] { "Split0", "Split1", "Split2" })
        {
            var thumb = window.FindControl<Thumb>(name)!;
            Assert.Equal(expected, thumb.Bounds.Width);

            var bar = thumb.GetVisualDescendants().OfType<Border>().First(item => item.Name == "ColBar");
            Assert.Equal(7, bar.Bounds.Width);
        }

        window.Close();
    }

    /// <summary>
    /// 点开设置之后弹层开了：挂 <c>open</c> 类这一步排在 <c>DispatcherPriority.Render</c> 上，
    /// <c>RaiseEvent</c> 之后不抽一次 <c>RunJobs()</c> 就读不到它。这一条只钉挂类；
    /// 淡入的关键帧在样式表侧量，见 <c> .Overlay_MustDeclareItsFadeInKeyFrames</c>。
    /// </summary>
    [AvaloniaFact]
    public void Popup_MustMountTheOpenClass()
    {
        var window = Show();
        var card = window.FindControl<Border>("PopupCard")!;

        window.FindControl<Button>("BtnSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 挂类这一步排在 Dispatcher 上，先抽一次再判「弹层开了没有」。
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("open", card.Classes);

        window.Close();
    }

    /// <summary>
    /// 行两侧的内边距只有一处取值：代码侧的 <see cref="ColumnLayout.RowPad"/> 必须等于令牌 <c>PadRow</c>。
    /// 两者一分家，列宽之和就会越出内容盒，最右一列开始被裁（B-2026-310）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-310")]
    public void RowPadding_MustMatchTheRowToken()
    {
        var token = Assert.IsType<global::Avalonia.Thickness>(Tokens()["PadRow"]);

        Assert.Equal(ColumnLayout.RowPad, token.Left);
        Assert.Equal(ColumnLayout.RowPad, token.Right);
    }

    private static MainWindow Show()
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>令牌与样式表：headless 宿主不预装这两份，量宽度要靠它们。</summary>
    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new System.Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new System.Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    /// <summary>落点用一次性临时目录：默认目录会覆盖作者的 <c>config.json</c>。</summary>
    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-form-").FullName;
}
