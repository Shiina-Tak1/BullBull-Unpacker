using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bbu.Core.Logging;
using Bbu.Ui.Avalonia.Views;

namespace Bbu.App;

public partial class App : Application
{
    /// <summary>入口建好的那一份日志。界面层的钩子与主窗口都取它，界面不另建。</summary>
    public static IRunLog? Log { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Log is { } log)
            {
                // 第三个钩子只能在 Avalonia 起来之后装：这一刻之前 Dispatcher.UIThread 还不存在。
                Dispatcher.UIThread.UnhandledException += (_, e) =>
                    CrashHooks.Report(log, CrashHooks.SourceDispatcher, e.Exception);
                desktop.MainWindow = new MainWindow(log);
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
