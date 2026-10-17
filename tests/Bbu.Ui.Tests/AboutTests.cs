using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Core;
using Bbu.Core.Services;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 关于页的五个字段：软件版本、7-Zip 版本、unrar 版本、仓库地址、商店地址（p007）。
///
/// unrar 那一行与 7-Zip 同通道：从随包二进制的版本资源读；读不到写 `AboutUnrarMissing`，不编数。
/// </summary>
public sealed class AboutTests
{
    /// <summary>五个字段各有控件与文案，两个入口按钮可点。</summary>
    [AvaloniaFact]
    public void About_MustCarryTheFiveFields()
    {
        var window = Show(1160, 660);
        window.FindControl<Button>("BtnAbout")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);

        Assert.Contains(AppInfo.Version, window.FindControl<TextBlock>("AboutVersion")!.Text!);

        var engine = window.FindControl<TextBlock>("AboutEngine")!.Text ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(engine), "7-Zip 那一行没有值");
        Assert.True(char.IsDigit(engine[0]), $"7-Zip 那一行不像版本号：{engine}");

        // 判据 5：unrar 那一行也是**从随包二进制的版本资源读出来的**，不是一个写死的占位串。
        var unrar = window.FindControl<TextBlock>("AboutUnrar")!.Text ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(unrar), "unrar 那一行没有值");
        Assert.NotEqual(UiText.AboutUnrarMissing, unrar);
        Assert.True(char.IsDigit(unrar[0]), $"unrar 那一行不像版本号：{unrar}");

        // 数据目录那一行按删掉了：它与设置页里那一项重复。
        Assert.Null(window.FindControl<TextBlock>("AboutData"));

        // 两个入口都是带图标的按钮，提示一律不挂。
        var repo = window.FindControl<Button>("AboutRepo")!;
        Assert.True(repo.IsEnabled, "仓库按钮该可点");
        Assert.Equal(UiText.AboutOpenRepo, window.FindControl<TextBlock>("AboutRepoText")!.Text);
        Assert.Null(ToolTip.GetTip(repo));

        var store = window.FindControl<Button>("AboutStore")!;
        Assert.True(store.IsEnabled, "商店按钮该可点");
        Assert.Equal(UiText.AboutOpenStore, window.FindControl<TextBlock>("AboutStoreText")!.Text);
        Assert.Null(ToolTip.GetTip(store));

        // 两个图标都是品牌图形：走 brand 那一档（素材原始坐标 + 填充），不走 lucide 的描边。
        // 路径要解析得出几何；尺寸由样式档给，headless 不加载主题字典，尺寸值在这里取不到。
        foreach (var entry in new[] { repo, store })
        {
            var glyph = entry.GetVisualDescendants().OfType<ShapePath>().Single();
            Assert.Contains("brand", glyph.Classes);
            Assert.True(glyph.Data!.Bounds.Width > 0, "品牌图形解析不出几何");
        }

        // 日志那一行按作者反馈删掉了 —— 它就在数据目录里面。
        Assert.Null(window.FindControl<TextBlock>("AboutLogs"));

        // 「确定」仍是默认按钮。
        var ok = window.FindControl<Button>("BtnOverlayOk")!;
        Assert.Equal("确定", UiText.AboutClose);
        Assert.Equal(UiText.AboutClose, ok.Content);

        window.Close();
    }

    /// <summary>
    /// 版权与许可各有控件与文案，同收在一行：版权与程序集元数据、CLI 同文；
    /// 许可是 SPDX 标识符，它是一段可点的链接，落点是 GNU 官方的 GPLv3 全文页。
    /// </summary>
    [AvaloniaFact]
    public void About_MustCarryCopyrightAndLicense()
    {
        var window = Show(1160, 660);
        window.FindControl<Button>("BtnAbout")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);

        Assert.Equal(UiText.AboutCopyright, window.FindControl<TextBlock>("AboutCopyright")!.Text);

        // 许可是链接控件：文字在它里面，四态与焦点由 Button.link 那一档样式给。
        var license = window.FindControl<Button>("AboutLicense")!;
        Assert.True(license.IsEnabled, "许可那段该可点");
        var text = (TextBlock)license.Content!;
        Assert.Equal(UiText.AboutLicense, text.Text);
        Assert.Equal("https://www.gnu.org/licenses/gpl-3.0.html", UiText.AboutLicenseUrl);
        Assert.NotEmpty(text.TextDecorations!);
        Assert.Null(ToolTip.GetTip(license));

        window.Close();
    }

    /// <summary>
    /// 版面按的两轮反馈定：标题栏只写「关于」、不放图标；头部是大图标，下面一行是产品名
    /// 与版本胶囊；两个引擎在中间那张卡片里；仓库与商店并排等宽；版权与许可收成一行、确定在那一行右侧；
    /// 浮层按内容收窄成接近方形。判的是渲染出来的位置、宽度与字体，不是 XAML 里写了什么。
    /// </summary>
    [AvaloniaFact]
    public void About_Layout_MustKeepTheBoardShape()
    {
        var window = Show(1160, 660);
        window.FindControl<Button>("BtnAbout")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);

        var head = window.FindControl<Border>("OverlayHead")!;
        var title = window.FindControl<TextBlock>("OverlayTitle")!;
        var modal = window.FindControl<Border>("OverlayModal")!;
        var product = window.FindControl<TextBlock>("AboutProduct")!;
        var version = window.FindControl<TextBlock>("AboutVersion")!;
        var engine = window.FindControl<TextBlock>("AboutEngine")!;
        var unrar = window.FindControl<TextBlock>("AboutUnrar")!;
        var copyright = window.FindControl<TextBlock>("AboutCopyright")!;
        var license = window.FindControl<Button>("AboutLicense")!;
        var repo = window.FindControl<Button>("AboutRepo")!;
        var store = window.FindControl<Button>("AboutStore")!;
        var ok = window.FindControl<Button>("BtnOverlayOk")!;

        // 标题栏：写这一页的名字，图标不在这一条里。
        Assert.Equal(UiText.About, title.Text);
        Assert.Empty(head.GetVisualDescendants().OfType<Image>());

        // 头部：产品名与版本胶囊同一行、胶囊在右，整行在引擎卡片上面；产品名取粗体（这一页唯一一处）。
        // 前景那一档不在这里判：headless 里主题字典没加载，任何 DynamicResource 都取到框架默认值。
        Assert.Equal(UiText.ProductName, product.Text);
        Assert.Equal(FontWeight.SemiBold, product.FontWeight);
        // 产品名与胶囊字号不同、各自在行里居中，所以比中心并给 2px 容差。
        Assert.True(
            Math.Abs(CenterY(product, window) - CenterY(version, window)) < 2,
            "版本胶囊该与产品名同一行");
        Assert.True(Top(version, window).X > Top(product, window).X, "版本胶囊该在产品名右边");
        Assert.True(Top(product, window).Y < Top(engine, window).Y, "产品名那一行该在引擎卡片上面");
        Assert.True(Top(engine, window).Y < Top(unrar, window).Y, "两个引擎该上下排");

        // 两个入口并排：同一行，而且等宽 —— 亚像素取整会让两者差个把像素，按 2px 判。
        Assert.True(
            Math.Abs(CenterY(repo, window) - CenterY(store, window)) < 2,
            "两个入口该在同一行");
        Assert.True(
            Math.Abs(repo.Bounds.Width - store.Bounds.Width) < 2,
            "两个入口该等宽");

        // 底部那一行：版权与许可同一行，它在入口按钮下面；确定与它们同一行、排在右边。
        Assert.True(
            Math.Abs(CenterY(copyright, window) - CenterY(license, window)) < 2,
            "版权与许可该在同一行");
        Assert.True(Top(copyright, window).Y > Top(repo, window).Y, "版权那一行该在入口按钮下面");
        // 「确定」与版权那一行同高：一个固定高 32、另一个按内容高，各自在行里居中，差半个像素是常态。
        Assert.True(
            Math.Abs(CenterY(ok, window) - CenterY(copyright, window)) < 1,
            "「确定」该与版权那一行同高");
        Assert.True(Top(ok, window).X > Top(license, window).X, "确定该排在版权与许可的右边");

        // 收窄：内容少，宽度压到 400（不是样式里那个默认值）。
        Assert.Equal(400d, modal.Width);

        window.Close();
    }

    /// <summary>控件左上角在整窗坐标系里的位置：直接读 Bounds 只到父级，跨父级比位置要用它。</summary>
    private static Point Top(Visual visual, Visual root) =>
        visual.TranslatePoint(new Point(0, 0), root) ?? new Point(double.NaN, double.NaN);

    /// <summary>
    /// 垂直中心：同一行里高低不同的两个控件（一个按内容高、一个固定高 32）比"排在同一行"要看中心，
    /// 比左上角会把行内居中算成错位。
    /// </summary>
    private static double CenterY(Visual visual, Visual root) => Top(visual, root).Y + (visual.Bounds.Height / 2);

    /// <summary>
    /// 商店那一格走网页：地址只有一处、写死，不做运行时探测与降级。
    /// </summary>
    [Fact]
    public void StoreUrl_MustPointAtTheStorePage()
    {
        Assert.Equal("https://apps.microsoft.com/detail/9NJ532JX69VK", UiText.StoreUrl);
    }

    /// <summary>
    /// 仓库地址指向**公开仓**：不带语言后缀那一个。
    /// 第一版放的是 C# 仓，那是私有开发仓。
    /// </summary>
    [Fact]
    public void RepoUrl_MustPointAtThePublicRepository()
    {
        Assert.Equal("https://github.com/Shiina-Tak1/BullBull-Unpacker", UiText.RepoUrl);
        Assert.DoesNotContain("-CS", UiText.RepoUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("-PY", UiText.RepoUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// 版本探针读的是引擎二进制自己的版本资源：随包那份读得出，不存在的路径回空串。
    /// 编一个常量当版本号是这一条要拦的事。
    /// </summary>
    [Fact]
    public void EngineVersion_MustComeFromTheBinaryItself()
    {
        var bundled = Path.Combine(RepoRoot(), "src", "Bbu.App", "engines", "7z", "7z.exe");
        Assert.True(File.Exists(bundled), $"找不到随包的 7z.exe：{bundled}");

        var version = EngineVersion.Of(bundled);
        Assert.False(string.IsNullOrWhiteSpace(version), "随包的 7z.exe 读不出版本");
        Assert.True(char.IsDigit(version[0]), $"读出来的不像版本号：{version}");

        Assert.Equal(string.Empty, EngineVersion.Of(Path.Combine(RepoRoot(), "不存在的目录", "7z.exe")));
        Assert.Equal(string.Empty, EngineVersion.Of(null));
    }

    /// <summary>尺寸要在 Show 之后设：XAML 里那份基准尺寸是 InitializeComponent 写的，先设会被它盖掉。</summary>
    private static MainWindow Show(double width, double height)
    {
        // 程序目录指向仓内那一处：两个引擎都随包，指着真实程序目录才读得到版本号。
        var window = new MainWindow(TempDir(), Path.Combine(RepoRoot(), "src", "Bbu.App"));
        window.Show();
        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    /// <summary>仓库根：往上一路找 Bbu.slnx，不写死层数。</summary>
    private static string RepoRoot() => TestRepo.Root();

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-about-").FullName;
}
