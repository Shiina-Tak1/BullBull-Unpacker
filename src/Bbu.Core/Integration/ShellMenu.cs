using System;
using System.Collections.Generic;
using System.IO;
using Bbu.Core.Texts;

namespace Bbu.Core.Integration;

/// <summary>一处注册位置：右键文件、右键文件夹、文件夹空白处。</summary>
/// <param name="Key">注册表里的位置段，拼在根与 <c>shell\&lt;verb&gt;</c> 之间。</param>
/// <param name="Placeholder">资源管理器替换成选中项的占位符。空白处用 <c>%V</c>，取当前目录。</param>
public sealed record ShellPlace(string Key, string Placeholder);

/// <summary>右键菜单现在的样子。设置页只读这个结论，不自己查注册表。</summary>
/// <param name="Installed">三处都注册了才算已注册；打包形态由包清单提供，同样算已注册。</param>
/// <param name="Places">真的读到命令行的那几处。</param>
/// <param name="Command">第一处读到的命令行，供设置页显示与排错。</param>
/// <param name="FromPackage">菜单由包清单提供，注册表那三处不参与。</param>
public sealed record ShellMenuState(
    bool Installed,
    IReadOnlyList<string> Places,
    string Command,
    bool FromPackage = false);

/// <summary>
/// 右键菜单的注册、卸载与状态查询。写 HKCU 的 <c>Software\Classes</c>，不需要管理员，
/// 卸载就是删掉那三处键。
///
/// 启动命令只有 <see cref="CommandFor"/> 一处拼装（B-2026-029）。
/// </summary>
public static class ShellMenu
{
    /// <summary>现行动词，注册表键名的那一段。</summary>
    public const string CurrentVerb = "BBUnpacker";

    /// <summary>最后一次注册表写入失败的 Win32 错误码（0 表示没失败过），注册没写全时带出来给用户看。</summary>
    public static int LastError => Registry.LastError;

    /// <summary>最后一次失败落在哪个键的哪个值上。只有错误码还不足以定位（B-2026-162）。</summary>
    public static string LastFailure => Registry.LastFailure;

    /// <summary>给诊断用：按字面试写一个键并立刻删掉，返回 Win32 错误码（0 表示成功）。</summary>
    public static int Probe(string subKey) => Registry.Probe(subKey);

    /// <summary>给诊断用：当前进程的完整性级别。</summary>
    public static string Integrity => ProcessIntegrity.Name();

    /// <summary>
    /// 给诊断用：从近到远的三层试写路径（自有键、注册表根直下、三处形状的第一处）。
    /// 一律从本类的常量拼出来，不在这里另写键段字面量。
    /// </summary>
    public static IReadOnlyList<string> ProbePaths() =>
    [
        $@"Software\BullBullUnpacker\{ProbeName}",
        $@"{DefaultRoot}\{ProbeName}",
        KeyPath(Places[0], ProbeName),
    ];

    /// <summary>试写用的键名，用完即删。</summary>
    public const string ProbeName = "reg-probe";

    /// <summary>菜单显示开关的落点：与三处注册键同键、不同值，卸载删键不会连它一起删。</summary>
    public const string SwitchKey = @"Software\BullBullUnpacker\ShellExt";

    /// <summary>菜单显示开关的值名，与 C++ 侧的 <c>BBU_SWITCH_MENU</c> 同源（断言盯着两处）。</summary>
    public const string MenuSwitchValue = "MenuEnabled";

    /// <summary>
    /// 读菜单显示开关。缺值按开 —— 菜单默认显示（方案 C 已裁），可见性由组件在运行时给。
    /// </summary>
    public static bool SwitchEnabled(string value) =>
        !Registry.TryReadDword(SwitchKey, value, out var raw) || raw != 0;

    /// <summary>写菜单显示开关。1 = 显示、0 = 隐藏。</summary>
    public static bool SetSwitch(string value, bool enabled) =>
        Registry.TryWriteDword(SwitchKey, value, enabled ? 1 : 0);

    /// <summary>注册表根。默认写当前用户那一份。</summary>
    public const string DefaultRoot = @"Software\Classes";

    /// <summary>把注册表根顶到别处的环境变量。测试只用临时根，绝不写用户的真菜单。</summary>
    public const string EnvOverride = "BBU_SHELLMENU_ROOT";

    /// <summary>不写它时，一次选中多个文件资源管理器只把第一个发过来。</summary>
    public const string MultiSelectModel = "Player";

    /// <summary>
    /// 改名前的那些动词。安装与卸载都先按这份名单清一遍，否则用户升级之后
    /// 菜单里会并排出现两条一模一样的项。改一次动词就往这里补一条。
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyVerbs = ["SmartUnzip", "BBUUnpack"];

    /// <summary>三处注册位置，覆盖日常会遇到的全部情况。</summary>
    public static readonly IReadOnlyList<ShellPlace> Places =
    [
        new(@"*", "%1"),
        new(@"Directory", "%1"),
        new(@"Directory\Background", "%V"),
    ];

    /// <summary>菜单文案。</summary>
    public static string MenuText => IntegrationText.ShellMenuText;

    /// <summary>这次写哪个注册表根。</summary>
    public static string Root(string? root = null)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        var over = Environment.GetEnvironmentVariable(EnvOverride);
        return string.IsNullOrWhiteSpace(over) ? DefaultRoot : over;
    }

    /// <summary>一处置的键路径。<paramref name="verb"/> 省略时用现行动词。</summary>
    public static string KeyPath(ShellPlace place, string? verb = null, string? root = null) =>
        $@"{Root(root)}\{place.Key}\shell\{verb ?? CurrentVerb}";

    /// <summary>拼一条资源管理器能当命令行执行的字符串：两段各自加引号防路径带空格，不带多余参数与自动开跑开关。</summary>
    public static string CommandFor(string executable, string placeholder) =>
        $"\"{executable}\" \"{placeholder}\"";

    /// <summary>菜单项左边的图标。用 exe 内嵌的那一份。</summary>
    public static string IconFor(string executable) => executable + ",0";

    /// <summary>右键菜单该注册哪个 exe。就是当前这个进程自己的可执行文件。</summary>
    public static string ExecutablePath() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Bbu.App.exe");

    /// <summary>执行别名的文件名，与完整包清单里 <c>desktop:ExecutionAlias/@Alias</c> 同值（断言盯着两处）。</summary>
    public const string AliasFileName = "BullBullUnpacker.exe";

    /// <summary>菜单项的命令该指向谁：包形态下用执行别名，其余用本体。</summary>
    public static string CommandTarget() => PackagedAlias() ?? ExecutablePath();

    /// <summary>执行别名的全路径；没有这一份时返回空（没声明别名，或用户在「应用执行别名」里把它关了）。</summary>
    public static string? PackagedAlias()
    {
        if (!PackageIdentity.IsPackaged)
        {
            return null;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps",
            AliasFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>打包形态下菜单由包清单提供，注册表写入会被虚拟化，资源管理器看不到。</summary>
    public static string FromPackageCommand => IntegrationText.ShellMenuFromPackage;

    /// <summary>装三处键，返回真正写过的键。命令走 <see cref="CommandTarget"/>，图标取本体那一份。</summary>
    public static IReadOnlyList<string> InstallSelf(string? root = null, bool? packaged = null) =>
        Install(CommandTarget(), root, IconFor(ExecutablePath()), packaged);

    /// <summary>装三处键，返回真正写过的键。菜单由包清单提供时一处也不写。</summary>
    public static IReadOnlyList<string> Install(
        string executable, string? root = null, string? icon = null, bool? packaged = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (MenuFromPackage(packaged))
        {
            return [];
        }

        PurgeLegacy(root);

        var written = new List<string>();
        var iconPath = icon ?? IconFor(executable);
        foreach (var place in Places)
        {
            var key = KeyPath(place, root: root);
            var marked = Registry.TryWriteString(key, null, MenuText)
                && Registry.TryWriteString(key, "MultiSelectModel", MultiSelectModel)
                && Registry.TryWriteString(key, "Icon", iconPath)
                && Registry.TryWriteString(key + @"\command", null, CommandFor(executable, place.Placeholder));
            if (marked)
            {
                written.Add(key);
            }
        }

        return written;
    }

    /// <summary>卸掉三处键并清掉旧名字，返回真正删掉的键。菜单由包清单提供时一处也不删。</summary>
    public static IReadOnlyList<string> Uninstall(string? root = null, bool? packaged = null)
    {
        if (MenuFromPackage(packaged))
        {
            return [];
        }

        var removed = new List<string>();
        foreach (var place in Places)
        {
            var key = KeyPath(place, root: root);
            if (Registry.TryDeleteTree(key))
            {
                removed.Add(key);
            }
        }

        removed.AddRange(PurgeLegacy(root));
        return removed;
    }

    /// <summary>
    /// 清掉改名前的动词留下的键。旧名等于现行名时跳过：否则万一改名被回退，
    /// 这里会把正在用的那条删掉。
    /// </summary>
    public static IReadOnlyList<string> PurgeLegacy(string? root = null)
    {
        var removed = new List<string>();
        foreach (var verb in LegacyVerbs)
        {
            if (string.Equals(verb, CurrentVerb, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var place in Places)
            {
                var key = KeyPath(place, verb, root);
                if (Registry.TryDeleteTree(key))
                {
                    removed.Add(key);
                }
            }
        }

        return removed;
    }

    /// <summary>现在装没装。只读注册表，不写；菜单由包清单提供时读的是清单给的结论。</summary>
    public static ShellMenuState State(string? root = null, bool? packaged = null)
    {
        if (MenuFromPackage(packaged))
        {
            var declared = new List<string>();
            foreach (var place in Places)
            {
                declared.Add(place.Key);
            }

            return new ShellMenuState(true, declared, FromPackageCommand, true);
        }

        var found = new List<string>();
        var command = string.Empty;
        foreach (var place in Places)
        {
            if (!Registry.TryReadString(KeyPath(place, root: root) + @"\command", null, out var line))
            {
                continue;
            }

            found.Add(place.Key);
            if (command.Length == 0)
            {
                command = line;
            }
        }

        return new ShellMenuState(found.Count == Places.Count, found, command);
    }

    /// <summary>已注册过就重写一次三处键：目录或包更新后，键里的图标还指着旧位置（B-2026-171）。</summary>
    public static void RefreshIfInstalled(string? root = null)
    {
        if (MenuFromPackage(null) || !State(root).Installed)
        {
            return;
        }

        InstallSelf(root);
    }

    /// <summary>
    /// 菜单是不是由包清单提供：只看包身份，不看系统版本。Win10 与 Win11 的包形态都由清单给菜单，
    /// 注册表三处键只服务绿色版（B-2026-169、B-2026-171）。
    /// </summary>
    private static bool MenuFromPackage(bool? packaged) =>
        packaged ?? PackageIdentity.IsPackaged;
}
