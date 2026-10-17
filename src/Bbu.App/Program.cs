using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Integration;
using Bbu.Core.Logging;
using Bbu.Core.SingleInstance;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia;

// 简单名 Ui 会先绑到命名空间 Bbu.Ui。
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.App;

internal static class Program
{
    // NativeAOT 需要显式 STAThread
    [STAThread]
    public static int Main(string[] args)
    {
        // 界面侧开关由界面层自己认，入口层只把它们放行，不判未知。
        var options = Cli.Parse(args, StartupOptions.Known);
        if (options.Error is { Length: > 0 } error)
        {
            return Usage(error);
        }

        var executable = Path.GetFileName(ShellMenu.ExecutablePath());
        switch (options.Action)
        {
            case CliAction.Help:
                Console.WriteLine(Cli.HelpText(executable));
                return Cli.ExitDone;

            case CliAction.Version:
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, UiText.VersionAt, AppInfo.Version));
                Console.WriteLine(IntegrationText.CliCopyright);
                Console.WriteLine(IntegrationText.CliLicense);
                return Cli.ExitDone;

            case CliAction.Where:
                Console.WriteLine(Cli.WhereText(
                    DataDirectory.ResolveRoot(), AppContext.BaseDirectory, BuildId(), DataDirectory.LastNote));
                return Cli.ExitDone;

            case CliAction.InstallShellMenu:
                return InstallShellMenu();

            case CliAction.UninstallShellMenu:
                return UninstallShellMenu();

            case CliAction.MenuSwitch:
                return SetMenuSwitch(options.MenuSwitch);
        }

        // 已注册过就重写一次三处键：目录或包更新后，键里的图标还指着旧位置（B-2026-171）。
        ShellMenu.RefreshIfInstalled();

        return RunWindow(args, options);
    }

    /// <summary>用法错误：诊断走 stderr，退出码 2。</summary>
    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            IntegrationText.CliUsageAt,
            Path.GetFileName(ShellMenu.ExecutablePath())));
        return Cli.ExitUsage;
    }

    /// <summary>写菜单显示开关。<c>modern:on</c> 这样的一段由门面的 <c>menu show|hide</c> 翻译过来。</summary>
    private static int SetMenuSwitch(string spec)
    {
        var parts = spec.Split(':', 2);
        var name = parts.Length == 2 ? parts[0].ToLowerInvariant() : string.Empty;
        var value = name switch
        {
            "modern" => ShellMenu.MenuSwitchValue,
            "classic" => ShellMenu.MenuSwitchValue,
            _ => null,
        };
        if (value is null || parts.Length != 2 || (parts[1] != "on" && parts[1] != "off"))
        {
            return Usage(string.Format(CultureInfo.InvariantCulture, IntegrationText.CliMenuSwitchUsageAt, spec));
        }

        var enabled = parts[1] == "on";
        if (!ShellMenu.SetSwitch(value, enabled))
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture, IntegrationText.CliMenuSwitchFailedAt, name));
            return Cli.ExitUndone;
        }

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            IntegrationText.CliMenuSwitchAt,
            name,
            enabled ? IntegrationText.CliYes : IntegrationText.CliNo));
        return Cli.ExitDone;
    }

    private static int InstallShellMenu()
    {
        if (PackageIdentity.IsPackaged)
        {
            Console.WriteLine(IntegrationText.ShellMenuFromPackage);
            return Cli.ExitDone;
        }

        var written = ShellMenu.InstallSelf();
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture, IntegrationText.CliShellInstalledAt, written.Count));
        if (written.Count < ShellMenu.Places.Count)
        {
            // 失败必须带原因：只报数量无法定位是哪一步（B-2026-162）
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                IntegrationText.CliShellInstallFailedAt,
                ShellMenu.Places.Count,
                ShellMenu.LastFailure,
                ShellMenu.LastError));

            // 同一台机器上 PowerShell 能写而这里不能时，差异只可能在进程令牌上：
            // 权限位与调用写法都已用 命令行诊断 排除（B-2026-159）。
            // 身份与管理员判定是 Windows 专属 API，包一层平台检查才不会被 CA1416 判红；
            // 非 Windows 上这一行不打（那里注册本来也不会成功，少这一行不影响判读）。
            if (OperatingSystem.IsWindows())
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    IntegrationText.CliShellInstallContextAt,
                    identity.Name,
                    principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                        ? IntegrationText.CliYes
                        : IntegrationText.CliNo,
                    Environment.Is64BitProcess ? IntegrationText.CliYes : IntegrationText.CliNo,
                    PackageIdentity.IsPackaged ? IntegrationText.CliYes : IntegrationText.CliNo));
            }

            // 逐层试写：同一台机器上 PowerShell 的 .NET 与同款 P/Invoke 都成功，所以「能写到哪一层」
            // 就能分出是进程的问题还是位置的问题（B-2026-159）。
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture, IntegrationText.CliShellInstallIntegrityAt, ShellMenu.Integrity));
            foreach (var probe in ShellMenu.ProbePaths())
            {
                var code = ShellMenu.Probe(probe);
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    IntegrationText.CliShellInstallProbeAt,
                    probe,
                    code == 0 ? IntegrationText.CliYes : $"0x{code:X}"));
            }
        }

        return written.Count == ShellMenu.Places.Count ? Cli.ExitDone : Cli.ExitUndone;
    }

    private static int UninstallShellMenu()
    {
        if (PackageIdentity.IsPackaged)
        {
            Console.WriteLine(IntegrationText.ShellMenuFromPackage);
            return Cli.ExitDone;
        }

        var removed = ShellMenu.Uninstall();
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture, IntegrationText.CliShellRemovedAt, removed.Count));
        return Cli.ExitDone;
    }

    private static int RunWindow(string[] args, CliOptions options)
    {
        StartupOptions.Apply(args);

        // 入口只建这一份日志：界面、引擎与三个异常钩子共用它（判据：src 下自建 logger 只剩这一处）。
        var log = new RunLog(DataDirectory.LogPath());
        CrashHooks.Install(log);
        App.Log = log;
        log.SessionHeader(AppInfo.Version, AppInfo.Flavor, BuildId(), args);

        // 判定跑在上面的 RunLog 之前，降级说明只能等到这里补写（B-2026-151）。
        if (DataDirectory.LastNoteTemplate is { Length: > 0 } template)
        {
            log.Problem(template, DataDirectory.ResolveRoot());
        }

        if (!PackageIdentity.IsPackaged && !AppIdentity.Declare())
        {
            log.Trace(IntegrationText.TaskbarIdentityFailed);
        }

        var payload = new LaunchPayload(options.Paths, options.Auto);

        try
        {
            if (options.SingleInstanceProbe)
            {
                return RunSingleInstanceProbe(payload, options.HoldMs);
            }

            using var instanceLock = new SingleInstanceLock();
            if (!instanceLock.IsPrimary)
            {
                if (HandOverToRunningInstance(payload, log, out var why))
                {
                    // 交出去了就返回：少这一处返回，多选几个文件就会开出同样多的窗口（B-2026-115）。
                    return Cli.ExitDone;
                }

                // 两次都连不上 = 主实例刚死：自己接手开窗，不把用户挡在门外；没转出去这件事要留痕、并带上原因。
                log.Failure(Format.ForwardFailedAt, why);
            }

            using var listener = new SingleInstanceListener();
            listener.PayloadReceived += LaunchQueue.Current.Post;
            // 监听器交回来的是拼好的整句、不是模板，按一条实参传。
            listener.Note = m => log.Trace("{0}", m);
            listener.Start();

            // 监听与冷启动两条来源都进同一个收货台，不留第二条加清单的路。
            if (payload.Paths.Count > 0)
            {
                LaunchQueue.Current.Post(payload);
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return Cli.ExitDone;
        }
        finally
        {
            // 收尾那一句要在关文件之前写；异常退出时是否写它不做判定（判据只认正常退出）。
            log.Note(LogText.SessionEnded);
            log.Close();
        }
    }

    /// <summary>
    /// 把这次的参数交给已经在跑的那个实例；<c>true</c> 表示已经交出去了，本进程应当退出。
    /// 只有「连不上」才重试（主实例可能恰好在两次之间退出）；对端在但不是本份程序、或收下之后没回执，
    /// 再试也是一样的结果 —— 那两种情况都自己开窗：点开没反应比多开一个窗口更糟。
    /// </summary>
    private static bool HandOverToRunningInstance(LaunchPayload payload, RunLog log, out string reason)
    {
        reason = Errors.ForwardFailed;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = SingleInstanceForwarder.Forward(
                payload, SingleInstanceProtocol.ForwardTimeoutMs, out var why);
            if (result == ForwardResult.Delivered)
            {
                log.Note(LogText.ForwardedToRunningInstance);
                return true;
            }

            if (result != ForwardResult.NoServer)
            {
                log.Trace("{0}", result == ForwardResult.NotOurs ? LogText.ForwardTargetIsNotOurs : LogText.ForwardNotConfirmed);
                return false;
            }

            if (why.Length > 0)
            {
                reason = why;
            }
        }

        return false;
    }

    /// <summary>单实例自检：把互斥体判定与管道是否起得来打到 stdout 就退出，不开窗（真窗口跑不起来的场合用它核对）。</summary>
    private static int RunSingleInstanceProbe(LaunchPayload payload, int holdMs)
    {
        using var instanceLock = new SingleInstanceLock();
        if (!instanceLock.IsPrimary)
        {
            var result = SingleInstanceForwarder.Forward(payload);
            Console.WriteLine($"probe role=secondary forward={result}");
            Console.Out.Flush();
            return Cli.ExitDone;
        }

        using var listener = new SingleInstanceListener();
        listener.PayloadReceived += received =>
            Console.WriteLine($"probe received={received.Paths.Count}");
        listener.Start();

        // 管道是在后台线程里建的，给它一点时间再问结果
        Thread.Sleep(300);
        Console.WriteLine($"probe role=primary listening={listener.IsListening} error={listener.LastError ?? "-"}");
        Console.Out.Flush();

        if (holdMs > 0)
        {
            Thread.Sleep(holdMs);
        }

        return Cli.ExitDone;
    }

    /// <summary>构建指纹：先读可执行文件旁的 BUILD-ID.txt，读不到就退到程序集自带的产品版本。</summary>
    private static string BuildId()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BUILD-ID.txt");
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了与不存在都按同一个兜底处理，落到程序集那一档
        }

        return AppInfo.BuildId;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
