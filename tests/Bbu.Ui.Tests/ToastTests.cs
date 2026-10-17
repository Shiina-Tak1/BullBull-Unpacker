using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 提示条：落在任务表底部、压在所有浮层之上、同一时刻只有一条、点任意位置撤下、层外不吞点击。
///
/// 这几条都是结构与命中，headless 下量得到，所以进默认档（B-2026-130）。
/// </summary>
[Trait("Bug", "B-2026-130")]
public sealed class ToastTests
{
    /// <summary>
    /// 提示条落在任务表底部：居中于任务表而不是整个窗口，底边在输出栏上方。
    /// 两个中心在宽档下不重合，所以这一条能分辨居中基准是否换过。
    /// </summary>
    [AvaloniaFact]
    public void Toast_MustSitAtTheBottomOfTheTaskTable()
    {
        var window = Show(1160, 660);
        var host = window.FindControl<ToastHost>("Toasts")!;
        var layer = window.FindControl<Panel>("ToastLayer")!;
        var table = window.FindControl<Grid>("TaskArea")!;

        // 宽度钉在任务表那一格上；宿主本身不挪进任务表，否则它会掉到浮层之下（见下一条）。
        Assert.Equal(table.Bounds.Width, layer.Bounds.Width, 1);
        Assert.Equal(2, Grid.GetRow(layer));

        host.Show("测试提示");
        Settle(window);

        var toast = ToastOf(host);
        var at = toast.TranslatePoint(new Point(0, 0), window)!.Value;
        var client = window.ClientSize;

        var centre = at.X + toast.Bounds.Width / 2;
        Assert.True(
            Math.Abs(centre - table.Bounds.Width / 2) <= 1,
            $"提示条没在任务表里居中：中心 {centre:0.#}，任务表中心 {table.Bounds.Width / 2:0.#}");

        // 两个中心在宽档下不重合：右栏占着右端 420，任务表中心比窗口中心靠左 200 上下。
        Assert.True(
            Math.Abs(client.Width / 2 - table.Bounds.Width / 2) > 1,
            $"窗口 {client.Width:0.#} 下右栏不占宽度，这一条分不出居中基准");

        // 输出栏是根 Grid 的第 4 行，高 56 上下；底端离下缘不足 40 就是压上去了。
        var bottom = at.Y + toast.Bounds.Height;
        Assert.True(
            client.Height - bottom > 40,
            $"提示条压在输出栏上了：底端 {bottom:0.#}，窗口高 {client.Height:0.#}");

        window.Close();
    }

    /// <summary>
    /// 提示条压在所有浮层之上：设置页与密码本页内部也会触发提示，被遮罩盖住就等于没提示。
    /// 把宿主挪进主体那一格（浮层都在它之后声明）即红。
    /// </summary>
    [AvaloniaFact]
    public void Toast_MustStayAboveTheOverlays()
    {
        var window = Show(1160, 660);
        var layer = window.FindControl<Panel>("ToastLayer")!;
        var root = (Grid)layer.Parent!;

        var popup = root.Children.IndexOf(window.FindControl<Panel>("PopupHost")!);
        Assert.True(
            popup >= 0 && root.Children.IndexOf(layer) > popup,
            "提示条排在了浮层之前，会被遮罩盖住");

        window.Close();
    }

    /// <summary>
    /// 加入清单那一句提示要把两个数都报出来：加进来的那一项（文件夹或文件）与清单里展开出来的行数。
    /// 只报前者时，右键加一个目录会看到「加入清单 1 项」而清单里是一大串。
    /// </summary>
    [Fact]
    public void AddedToastText_MustReportBothTheRootsAndTheRows()
    {
        Assert.Equal("加入清单 1 项", MainWindow.AddedToastText(1, 0, 1));
        Assert.Equal("加入清单 3 项", MainWindow.AddedToastText(3, 0, 3));
        Assert.Equal("加入 1 个文件夹，清单新增 23 项", MainWindow.AddedToastText(1, 1, 23));
        Assert.Equal("加入 3 项（其中 1 个文件夹），清单新增 25 项", MainWindow.AddedToastText(3, 1, 25));
    }

    /// <summary>新提示进来时旧的被撤掉，不是纵向叠加。</summary>
    [AvaloniaFact]
    public void Toast_MustKeepOnlyTheNewestOne()
    {
        var window = Show(1160, 660);
        var host = window.FindControl<ToastHost>("Toasts")!;

        host.Show("第一条");
        Settle(window);
        host.Show("第二条");
        Settle(window);

        var stack = ToastStackOf(host);
        Assert.Single(stack.Children);
        var text = ToastOf(host).GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal("第二条", text.Text);

        window.Close();
    }

    /// <summary>
    /// 没有关闭按钮，点提示条任意位置就撤下。
    ///
    /// 这一条要真发鼠标事件 —— 只看 <c>IsHitTestVisible</c> 证明不了没被挡住。
    /// </summary>
    [AvaloniaFact]
    public void Toast_MustDismissOnAnyClick()
    {
        var window = Show(1160, 660);
        var host = window.FindControl<ToastHost>("Toasts")!;
        host.Show("测试提示");
        Settle(window);

        var toast = ToastOf(host);
        Assert.Empty(toast.GetVisualDescendants().OfType<Button>());

        Assert.True(toast.Bounds.Width > 0 && toast.Bounds.Height > 0, $"提示条没排版：{toast.Bounds}");

        // 直接派发到提示条上：headless 的鼠标投递对 Border 这类非按钮元素不稳
        // （旧的关闭按钮那一条能过，换成整条 Border 就不行），而这一条要钉的是
        // 「提示条自己接住了 PointerPressed」，命中面由 ToastFace 承担。
        toast.RaiseEvent(new PointerPressedEventArgs(
            toast,
            new Pointer(1, PointerType.Mouse, true),
            window,
            new Point(4, 4),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

        // 撤下走 0.2 秒淡出：等它跑完，元素才真的从栈里摘掉。
        Thread.Sleep(400);
        Settle(window);

        Assert.Empty(ToastStackOf(host).Children);

        window.Close();
    }

    /// <summary>
    /// 提示条之外的区域仍不吞点击：点顶栏的「关于」照样能把关于浮层打开。
    ///
    /// 提示条在内容区底部居中、关于按钮在顶栏，两者不重叠；能打开就说明提示层没有铺开挡在中间。
    /// </summary>
    [AvaloniaFact]
    public void Toast_MustNotSwallowClicksOutsideTheStrip()
    {
        var window = Show(1160, 660);
        var host = window.FindControl<ToastHost>("Toasts")!;
        host.Show("测试提示");
        Settle(window);

        var client = window.ClientSize;
        Assert.True(
            host.Bounds.Width < client.Width / 2,
            $"提示层铺开了 {host.Bounds.Width:0.#}px，会挡住主界面");
        Assert.True(
            host.Bounds.Height < client.Height / 2,
            $"提示层高到了 {host.Bounds.Height:0.#}px，会挡住主界面");

        var about = window.FindControl<Button>("BtnAbout")!;
        var center = about.TranslatePoint(
            new Point(about.Bounds.Width / 2, about.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle(window);

        Assert.True(
            window.FindControl<Panel>("OverlayHost")!.IsVisible,
            "点顶栏的「关于」被提示层吞掉了");

        window.Close();
    }

    /// <summary>
    /// 停留 6 秒：用户常常正看着别处，2.6 秒闪一下就等于永久错过。
    /// 同一时刻只有一条提示，所以报成没成的那几句与其余各句停同一档。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-248")]
    public void Hold_MustGiveEveryToastSixSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), ToastHost.Hold);
    }

    private static StackPanel ToastStackOf(ToastHost host) =>
        host.FindControl<StackPanel>("ToastStack")!;

    private static Border ToastOf(ToastHost host) => (Border)ToastStackOf(host).Children[0];

    /// <summary>尺寸要在 Show 之后设：XAML 里那份基准尺寸是 InitializeComponent 写的，先设会被它盖掉。</summary>
    private static MainWindow Show(double width, double height)
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();

        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    /// <summary>
    /// 两件事都要做：置 1 那一步挂在 Background 优先级上（默认的 <c>RunJobs()</c> 跑不到），
    /// 而它触发的是 <c>DoubleTransition</c> 动画，headless 下还得推渲染时钟才会从 0 走到 1 ——
    /// 停在 0 的那一层会被命中测试整片跳过。
    /// </summary>
    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            window.UpdateLayout();
        }
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-toast-").FullName;
}
