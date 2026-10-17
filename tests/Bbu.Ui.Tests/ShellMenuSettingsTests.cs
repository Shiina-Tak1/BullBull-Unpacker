using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Integration;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 设置页最后那一组「Windows 集成」。
///
/// 状态、按钮文案与可用性都取自注册表当下的样子，页面不自己拼键路径，也不自己判级。
/// 注册表根顶到一个临时名字上，所以既不会读到作者自己的菜单，也不会把它写坏。
/// </summary>
public sealed class ShellMenuSettingsTests
{
    /// <summary>没注册时只该给「安装」，而且状态是警示色 —— 这一条默认档就能跑，读不写。</summary>
    [AvaloniaFact]
    public void Unregistered_MustOfferInstallOnly()
    {
        using var root = new ShellRoot();
        using var temp = new TempConfig();
        var page = Show(temp);

        var state = page.Part<TextBlock>("ShellState")!;
        Assert.Equal(UiText.SettingsShellUnregistered, state.Text);
        Assert.Contains("lvl-warn", state.Classes);
        Assert.Null(ToolTip.GetTip(state));

        Assert.Equal(UiText.SettingsShellInstall, page.Part<Button>("ShellInstall")!.Content);
        Assert.False(page.Part<Button>("ShellRemove")!.IsEnabled);
    }

    /// <summary>
    /// 注册之后翻成「已注册」，安装键改成「重新安装」，移除键才可点；命令行挂进提示，
    /// 排错时不用自己去翻注册表。这条要写注册表，挂在真机档（沙箱拒绝 HKCU 写入）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Category", "machine")]
    public void Registered_MustSaySoAndOfferRemove()
    {
        const string exe = @"C:\7z\Bbu.App.exe";
        using var root = new ShellRoot();
        using var temp = new TempConfig();
        var page = Show(temp);

        var written = ShellMenu.Install(exe, root.Root);
        Assert.Equal(ShellMenu.Places.Count, written.Count);

        page.RefreshShellMenu();

        var state = page.Part<TextBlock>("ShellState")!;
        Assert.Equal(UiText.SettingsShellRegistered, state.Text);
        Assert.Contains("lvl-info", state.Classes);
        Assert.Equal(ShellMenu.CommandFor(exe, "%1"), ToolTip.GetTip(state) as string);

        Assert.Equal(UiText.SettingsShellReinstall, page.Part<Button>("ShellInstall")!.Content);
        Assert.True(page.Part<Button>("ShellRemove")!.IsEnabled);
    }

    /// <summary>
    /// 打包形态那一项：菜单由包清单给，两个「安装／移除」按钮收起，
    /// 只留一个切换按钮；右上那句状态说的是开关（声明）开没开，不再是注册表三处键的「已注册」。
    /// 包身份由参数注入 —— 沙箱里造不出真包，现场判定在 sandbox 里恒为绿色版。
    /// </summary>
    [AvaloniaFact]
    public void Packaged_MustOfferASingleSwitch()
    {
        using var root = new ShellRoot();
        using var temp = new TempConfig();
        var page = Show(temp);

        page.RefreshShellMenu(packaged: true);

        var state = page.Part<TextBlock>("ShellState")!;
        Assert.Contains(state.Text, new[] { UiText.SettingsMenuOn, UiText.SettingsMenuOff });
        Assert.NotEqual(UiText.SettingsShellRegistered, state.Text);

        var toggle = page.Part<Button>("MenuSwitch")!;
        Assert.True(toggle.IsVisible, "打包形态该给那一个切换按钮");
        Assert.Contains(toggle.Content as string, new[] { UiText.SettingsMenuHide, UiText.SettingsMenuShow });

        // 装与卸是包的安装卸载：这一项在打包形态只剩上面那一个按钮，另两个收起了。
        var shown = page.VisibleButtons();
        Assert.Contains("MenuSwitch", shown);
        Assert.DoesNotContain("ShellInstall", shown);
        Assert.DoesNotContain("ShellRemove", shown);
    }

    /// <summary>
    /// 开关那一句状态与按钮上的字都由当下的开关态派生：开着时说「已开启」、按钮写「隐藏菜单」，
    /// 关着时反过来。文案只有这一处映射，界面不另判一次。
    /// </summary>
    [Fact]
    public void MenuSwitch_MustWordItselfAfterTheCurrentState()
    {
        var field = new SettingsShellField { Key = "Shell", Label = "右键菜单" };

        field.ModernOn = true;
        Assert.Equal(UiText.SettingsMenuOn, field.MenuStateText);
        Assert.Equal(UiText.SettingsMenuHide, field.MenuSwitchText);

        field.ModernOn = false;
        Assert.Equal(UiText.SettingsMenuOff, field.MenuStateText);
        Assert.Equal(UiText.SettingsMenuShow, field.MenuSwitchText);
    }

    private static SettingsPage Show(TempConfig temp)
    {
        var page = new SettingsPage();
        page.Attach(new AppConfig(), temp.Store, temp.Log, temp.Dir, null);

        var window = new Window { Content = page, Width = 900, Height = 640 };
        window.Show();

        // 项与控件由模板生成，要跑过布局才在可视树里。
        for (var spin = 0; spin < 5; spin++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return page;
    }

    /// <summary>
    /// 把注册表根顶到一个临时名字上，用完还原并清掉自己写的键。
    /// 名字带随机段：并行的用例不会互相看到对方装的菜单。
    /// </summary>
    private sealed class ShellRoot : IDisposable
    {
        private readonly string? _previous;

        public ShellRoot()
        {
            _previous = Environment.GetEnvironmentVariable(ShellMenu.EnvOverride);
            Environment.SetEnvironmentVariable(ShellMenu.EnvOverride, Root);
        }

        public string Root { get; } = @"Software\BBUTest-Ui-" + Guid.NewGuid().ToString("N")[..8];

        public void Dispose()
        {
            try
            {
                ShellMenu.Uninstall(Root);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                // 临时键清不掉不影响断言结论；沙箱里本来就是拒绝写入的
            }

            Environment.SetEnvironmentVariable(ShellMenu.EnvOverride, _previous);
        }
    }

    /// <summary>一次性临时目录加一份配置，离开作用域即删。</summary>
    private sealed class TempConfig : IDisposable
    {
        public TempConfig()
        {
            Directory.CreateDirectory(Dir);
            Store = new JsonConfigStore(Path.Combine(Dir, "config.json"));
            Log = new RunLog(Path.Combine(Dir, AppInfo.LogFileName));
        }

        public string Dir { get; } = Path.Combine(
            Path.GetTempPath(), "bbu-shell-page-" + Guid.NewGuid().ToString("N"));

        public JsonConfigStore Store { get; }

        public RunLog Log { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响断言结论
            }
        }
    }
}
