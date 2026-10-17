using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bbu.Core.Models;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 询问密码的那一层。它是窗口内的一层、由后台的穿透层跨线程唤起，
/// 而这条胶水断掉时是静默的（没有控制台、槽里抛异常看不见）——
/// 用户看到的就是「该弹窗的时候没弹」，而那正是他唯一的出路。
/// </summary>
public sealed class PasswordDialogTests
{
    /// <summary>
    /// 第一次问的时候不显示「不对」；第二次才显示，而用户一打字它就得收掉 ——
    /// 那行字说的是上一次的输入，留着会被当成本次输入的判词（B-2026-025）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-025")]
    public void WrongHint_MustClearAsSoonAsTheUserTypes()
    {
        var dialog = new PasswordDialog(Ask(attempt: 2));
        var wrong = dialog.FindControl<TextBlock>("TxtWrong")!;
        var input = dialog.FindControl<TextBox>("TxtInput")!;

        Assert.True(wrong.IsVisible);

        input.Text = "x";
        // 文本框把「内容变了」投递到下一轮消息，先把那些动作跑完再断言
        Dispatcher.UIThread.RunJobs();

        Assert.False(wrong.IsVisible);
    }

    /// <summary>第一次问的时候本来就没有「不对」可显示。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-025")]
    public void WrongHint_MustStayHiddenOnTheFirstAsk()
    {
        var dialog = new PasswordDialog(Ask(attempt: 1));

        Assert.False(dialog.FindControl<TextBlock>("TxtWrong")!.IsVisible);
    }

    /// <summary>
    /// 跨线程握手：用户输入的密码必须原样回填给穿透层。
    /// 首尾空格也是密码的一部分，不许 trim。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-050")]
    public async Task AskAsync_MustHandBackExactlyWhatTheUserTyped()
    {
        var (window, host, dialog) = BuildHost();
        var pending = dialog.AskAsync(host, Ask(attempt: 1), CancellationToken.None);

        await WaitForShownAsync(host);
        dialog.FindControl<TextBox>("TxtInput")!.Text = "  hunter2  ";
        dialog.FindControl<Button>("BtnVerify")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("  hunter2  ", await pending);
        window.Close();
    }

    /// <summary>用户跳过：回填 <c>null</c>。这一条与「没有询问通道」是两件事，结论也两样。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-050")]
    public async Task AskAsync_MustHandBackNullWhenTheUserSkips()
    {
        var (window, host, dialog) = BuildHost();
        var pending = dialog.AskAsync(host, Ask(attempt: 1), CancellationToken.None);

        await WaitForShownAsync(host);
        dialog.FindControl<Button>("BtnSkip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(await pending);
        window.Close();
    }

    /// <summary>已经取消时不开这一层 —— 后台收工之后还弹一层，用户只能干等它。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-050")]
    public async Task AskAsync_MustNotStayOpenWhenAlreadyCancelled()
    {
        var (window, host, dialog) = BuildHost();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var answer = await dialog.AskAsync(host, Ask(attempt: 1), cancelled.Token);

        Assert.Null(answer);
        Assert.False(host.IsVisible);
        window.Close();
    }

    /// <summary>
    /// 它是宿主里的一层，不是独立窗口：独立窗口会进任务栏，也脱离主窗口的层叠顺序。
    /// 显隐必须走那一处实现。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-050")]
    public async Task AskAsync_MustOpenInsideTheGivenLayer()
    {
        var (window, host, dialog) = BuildHost();
        var pending = dialog.AskAsync(host, Ask(attempt: 1), CancellationToken.None);

        await WaitForShownAsync(host);
        Assert.Contains(host.Children, child => child.Classes.Contains("open"));

        dialog.FindControl<Button>("BtnSkip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(await pending);
        window.Close();
    }

    /// <summary>
    /// 「跳过」这个按钮的字要与它的后果一致：点了之后<b>整个包</b>（含内部所有层）都不再解，
    /// 结果口径也写着「已跳过这个包」——按钮上写「跳过当前文件」时，用户以为只是跳过这一个文件。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-244")]
    public async Task SkipButton_MustSayItDropsTheWholePackage()
    {
        var (window, host, dialog) = BuildHost();
        var skip = dialog.FindControl<Button>("BtnSkip")!;
        Assert.Equal(UiText.AskSkip, skip.Content);
        Assert.DoesNotContain("当前文件", UiText.AskSkip, StringComparison.Ordinal);

        var pending = dialog.AskAsync(host, Ask(attempt: 1), CancellationToken.None);
        await WaitForShownAsync(host);
        skip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(await pending);
        window.Close();
    }

    /// <summary>
    /// 点「验证」之后面板转入验证中这一档：按钮的字换成「正在验证…」并禁用、输入框禁用。
    /// 这段时间里再点一次不算第二次提交 —— 第一次交出去的密码就是这一趟的答案。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-244")]
    public async Task Verify_MustShowTheVerifyingStateAndRefuseASecondSubmit()
    {
        var (window, host, dialog) = BuildHost();
        var pending = dialog.AskAsync(host, Ask(attempt: 1), CancellationToken.None);
        await WaitForShownAsync(host);

        var input = dialog.FindControl<TextBox>("TxtInput")!;
        var verify = dialog.FindControl<Button>("BtnVerify")!;
        input.Text = "hunter2";
        verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(dialog.IsVerifying, "点了「验证」之后面板没有进入验证中这一档");
        Assert.Equal(UiText.AskVerifying, verify.Content);
        Assert.False(verify.IsEnabled, "验证期间「验证」还点得动");
        Assert.False(input.IsEnabled, "验证期间输入框还能改");

        // 再点一次、或者按跳过：都不该产生第二个答案
        verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.Skip();
        Assert.Equal("hunter2", await pending);

        window.Close();
    }

    /// <summary>
    /// 交了密码之后这一层不立刻收：验证要起一次引擎，这段时间用户得看得到「正在验证…」。
    /// 结论回来之后由宿主收（验不过会带着下一次询问回来，那时面板要能重新输入）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-244")]
    public async Task Verify_MustHoldTheLayerUntilTheHostReleasesIt()
    {
        var (window, host, dialog) = BuildHost();
        var pending = dialog.AskAsync(host, Ask(attempt: 1), CancellationToken.None);
        await WaitForShownAsync(host);

        dialog.FindControl<TextBox>("TxtInput")!.Text = "hunter2";
        dialog.FindControl<Button>("BtnVerify")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("hunter2", await pending);
        Assert.True(host.IsVisible, "交了密码之后这一层就收了，验证期间用户什么都看不到");

        dialog.Release();
        Assert.False(dialog.IsVerifying);
        await WaitForHiddenAsync(host);

        // 下一次询问把它铺回可输入的样子（上一次的密码不对时走的就是这一趟）
        var again = dialog.AskAsync(host, Ask(attempt: 2), CancellationToken.None);
        await WaitForShownAsync(host);
        Assert.False(dialog.IsVerifying);
        Assert.Equal(UiText.AskVerify, dialog.FindControl<Button>("BtnVerify")!.Content);
        Assert.True(dialog.FindControl<TextBox>("TxtInput")!.IsEnabled);
        Assert.True(dialog.FindControl<TextBox>("TxtInput")!.Text!.Length == 0, "上一次输的密码留在了框里");

        dialog.FindControl<Button>("BtnSkip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(await again);
        window.Close();
    }

    private static PasswordAsk Ask(int attempt) =>
        new(@"C:\demo\a.zip", "a.zip", 5, attempt, "密码全试完了");

    /// <summary>
    /// 造一个与宿主同形的层：遮罩加一张模态卡，卡片里放这一个面板。
    /// 起一个真 Window 是让样式与布局能跑起来。
    /// </summary>
    private static (Window Window, Panel Host, PasswordDialog Dialog) BuildHost()
    {
        var dialog = new PasswordDialog();
        var card = new Border { Classes = { "modal" }, Child = dialog };
        var host = new Panel { IsVisible = false };
        host.Children.Add(new Border { Classes = { "scrim" } });
        host.Children.Add(card);

        var window = new Window { Content = host };
        window.Show();
        return (window, host, dialog);
    }

    /// <summary>
    /// 这一层是在下一轮消息里铺开的，所以要让它先跑起来。
    /// <c>await Task.Delay</c> 会让出界面线程，dispatcher 得以继续处理消息 —— 不能忙等。
    /// </summary>
    private static async Task WaitForShownAsync(Panel host)
    {
        for (var spin = 0; spin < 100; spin++)
        {
            if (host.IsVisible && host.Children.Any(child => child.Classes.Contains("open")))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("询问密码的那一层没有铺开");
    }

    /// <summary>等这一层真的收回去：淡出是两段式的，摘掉 <c>open</c> 之后还要跑完那 0.2 秒。</summary>
    private static async Task WaitForHiddenAsync(Panel host)
    {
        for (var spin = 0; spin < 100; spin++)
        {
            if (!host.IsVisible)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("询问密码的那一层没有收回去");
    }
}
