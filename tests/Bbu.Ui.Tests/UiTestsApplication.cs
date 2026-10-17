using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Bbu.Ui.Tests.UiTestsApplicationBuilder))]

namespace Bbu.Ui.Tests;

/// <summary>
/// 界面测试的 headless 宿主：只装 Fluent 主题。
/// 项目自己的样式表（Tokens / Controls）由被测窗口按需引用，这里不预装 ——
/// 预装会让"窗口能不能自己把样式拉起来"这件事测不出来。
/// </summary>
public static class UiTestsApplicationBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UiTestsApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UiTestsApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
