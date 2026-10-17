using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bbu.Core.Configuration;
using Bbu.Core.Services;
using Bbu.Core.Texts;

// 简单名 Ui 会先绑到命名空间 Bbu.Ui。
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Core.Integration;

/// <summary>入口的动作。同一时刻只做一个，后一个覆盖前一个。</summary>
public enum CliAction
{
    /// <summary>开窗。不给动作时的默认值。</summary>
    OpenWindow,

    Help,
    Version,
    Where,
    InstallShellMenu,
    UninstallShellMenu,
    MenuSwitch,

    // 下面是修饰与自检，不是主动作。
    Auto,
    SingleInstanceProbe,
    Hold,
}

/// <summary>参数表里的一行。这张表同时是解析的输入与帮助的输出，两处不可能不一致。</summary>
/// <param name="Name">开关原文。<c>=</c> 结尾表示要跟一个值；不以横线开头的是位置参数。</param>
/// <param name="Alias">短写法，没有就是 <c>null</c>。</param>
/// <param name="Action">它落到哪个动作。</param>
/// <param name="HelpRow">帮助表左栏。</param>
/// <param name="HelpText">帮助表右栏。</param>
/// <param name="Hidden">自检开关不进帮助。</param>
public sealed record CliOption(
    string Name,
    string? Alias,
    CliAction Action,
    string HelpRow,
    string HelpText,
    bool Hidden = false);

/// <summary>解析结果。</summary>
public sealed record CliOptions
{
    public CliAction Action { get; init; } = CliAction.OpenWindow;

    public bool Auto { get; init; }

    public bool SingleInstanceProbe { get; init; }

    public int HoldMs { get; init; }

    public string MenuSwitch { get; init; } = string.Empty;

    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>用法错误的原因。为 <c>null</c> 表示参数没问题。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 入口认的开关、解析与退出码。
///
/// 这里只收程序自身要用的那几个：开窗、右键菜单的装卸、<c>--where</c> 与自检。
/// 产品不提供面向终端用户的解压命令行，解压只有界面一条路；批量与调试走测试档，
/// 不走这个入口。
///
/// 解析由 <see cref="Options"/> 表驱动，因此「帮助里的每一条都有实现」是结构性的：
/// 帮助文本与解析读的是同一张表。
/// </summary>
public static class Cli
{
    /// <summary>动作成功。</summary>
    public const int ExitDone = 0;

    /// <summary>动作没完成。</summary>
    public const int ExitUndone = 1;

    /// <summary>用法错误。</summary>
    public const int ExitUsage = 2;

    /// <summary>参数表。顺序就是帮助里的顺序。</summary>
    public static readonly IReadOnlyList<CliOption> Options =
    [
        new(IntegrationText.CliPathRow, null, CliAction.OpenWindow,
            IntegrationText.CliPathRow, IntegrationText.CliPathText),
        new("--auto", null, CliAction.Auto,
            IntegrationText.CliAutoRow, IntegrationText.CliAutoText),
        new("--help", "-h", CliAction.Help,
            IntegrationText.CliHelpRow, IntegrationText.CliHelpText),
        new("--version", null, CliAction.Version,
            IntegrationText.CliVersionRow, IntegrationText.CliVersionText),
        new("--where", null, CliAction.Where,
            IntegrationText.CliWhereRow, IntegrationText.CliWhereText),
        new("--install-shellmenu", null, CliAction.InstallShellMenu,
            IntegrationText.CliInstallRow, IntegrationText.CliInstallText),
        new("--uninstall-shellmenu", null, CliAction.UninstallShellMenu,
            IntegrationText.CliUninstallRow, IntegrationText.CliUninstallText),
        new("--menu-switch=", null, CliAction.MenuSwitch,
            IntegrationText.CliMenuSwitchRow, IntegrationText.CliMenuSwitchText),
        new("--single-instance-probe", null, CliAction.SingleInstanceProbe, "", "", Hidden: true),
        new("--hold=", null, CliAction.Hold, "", "", Hidden: true),
    ];

    /// <summary>帮助表里的参数行，自检开关不列。</summary>
    public static IEnumerable<CliOption> Documented => Options.Where(option => !option.Hidden);

    /// <summary>
    /// 解析。界面层自己的开关由 <paramref name="passthrough"/> 给（以 <c>=</c> 结尾表示要跟值），
    /// 它们既不进本表也不判未知：界面层自己解析那一批。
    /// </summary>
    public static CliOptions Parse(IReadOnlyList<string> args, IReadOnlyCollection<string>? passthrough = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new CliOptions();
        var paths = new List<string>();
        string? error = null;

        foreach (var raw in args)
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw[0] != '-')
            {
                var path = Unquote(raw);
                if (path.Length > 0)
                {
                    paths.Add(path);
                }

                continue;
            }

            if (IsPassthrough(raw, passthrough))
            {
                continue;
            }

            var matched = Options.FirstOrDefault(option =>
                option.Name.StartsWith('-')
                && (string.Equals(option.Name, raw, StringComparison.OrdinalIgnoreCase)
                    || (option.Alias is { Length: > 0 } alias
                        && string.Equals(alias, raw, StringComparison.OrdinalIgnoreCase))
                    || (option.Name.EndsWith('=')
                        && raw.StartsWith(option.Name, StringComparison.OrdinalIgnoreCase))));

            if (matched is null)
            {
                error ??= string.Format(CultureInfo.InvariantCulture, IntegrationText.CliUnknownSwitchAt, raw);
                continue;
            }

            options = Apply(matched, raw, options);
        }

        return options with { Paths = paths, Error = error };
    }

    /// <summary>帮助文本。参数表与退出码表都从这个方法出，别处不再写一遍。</summary>
    public static string HelpText(string executable)
    {
        var lines = new List<string>
        {
            $"{UiText.ProductName} {string.Format(CultureInfo.InvariantCulture, UiText.VersionAt, AppInfo.Version)}",
            string.Format(CultureInfo.InvariantCulture, IntegrationText.CliUsageAt, executable),
            string.Empty,
            IntegrationText.CliArgsHeader,
        };

        lines.AddRange(Documented.Select(option => Row(option.HelpRow, option.HelpText)));
        lines.Add(string.Empty);
        lines.Add(IntegrationText.CliExitCodesHeader);
        lines.Add(Row(ExitDone.ToString(CultureInfo.InvariantCulture), IntegrationText.CliCodeDoneText));
        lines.Add(Row(ExitUndone.ToString(CultureInfo.InvariantCulture), IntegrationText.CliCodeUndoneText));
        lines.Add(Row(ExitUsage.ToString(CultureInfo.InvariantCulture), IntegrationText.CliCodeUsageText));
        lines.Add(string.Empty);
        lines.Add(IntegrationText.CliCopyright);
        lines.Add(IntegrationText.CliLicense);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary><c>--where</c> 的输出：这次用哪个数据根，各文件分别在哪。参数是数据根，<paramref name="note"/> 是降级说明。</summary>
    public static string WhereText(string root, string programDirectory, string buildId, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var userData = DataDirectory.UserDataOf(root);
        var kind = DataDirectory.Classify(root, programDirectory);
        var suffix = kind switch
        {
            DataDirectoryKind.Portable => IntegrationText.CliWherePortable,
            DataDirectoryKind.Pinned => IntegrationText.CliWherePinned,
            _ => IntegrationText.CliWhereRoaming,
        };

        var engines = EngineLocator.Resolve(programDirectory);

        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, IntegrationText.CliWhereProgramAt, programDirectory),
            string.Format(CultureInfo.InvariantCulture, IntegrationText.CliWhereDataDirAt, userData + suffix),
            string.Format(
                CultureInfo.InvariantCulture,
                IntegrationText.CliWhereConfigAt,
                Path.Combine(userData, AppInfo.ConfigFileName)),
            string.Format(
                CultureInfo.InvariantCulture,
                IntegrationText.CliWhereVaultAt,
                Path.Combine(userData, VaultText.BookFileName)),
            string.Format(
                CultureInfo.InvariantCulture,
                IntegrationText.CliWhereLogAt,
                Path.Combine(userData, AppInfo.LogFileName)),
            string.Format(
                CultureInfo.InvariantCulture,
                IntegrationText.CliWhereSevenZipAt,
                engines.SevenZip ?? IntegrationText.CliWhereMissing),
            string.Format(
                CultureInfo.InvariantCulture,
                IntegrationText.CliWhereUnRarAt,
                engines.UnRar ?? IntegrationText.CliWhereMissing),
            string.Format(CultureInfo.InvariantCulture, IntegrationText.CliWhereBuildAt, buildId),
        };

        if (note is { Length: > 0 })
        {
            lines.Add(note);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Row(string left, string right) =>
        string.Format(CultureInfo.InvariantCulture, IntegrationText.CliRowFormat, left, right);

    private static CliOptions Apply(CliOption option, string raw, CliOptions options) => option.Action switch
    {
        CliAction.Help => options with { Action = CliAction.Help },
        CliAction.Version => options with { Action = CliAction.Version },
        CliAction.Where => options with { Action = CliAction.Where },
        CliAction.InstallShellMenu => options with { Action = CliAction.InstallShellMenu },
        CliAction.UninstallShellMenu => options with { Action = CliAction.UninstallShellMenu },
        CliAction.MenuSwitch => options with { Action = CliAction.MenuSwitch, MenuSwitch = TextValue(raw) },
        CliAction.Auto => options with { Auto = true },
        CliAction.SingleInstanceProbe => options with { SingleInstanceProbe = true },
        CliAction.Hold => options with { HoldMs = HoldValue(raw) },
        _ => options,
    };

    /// <summary>取 <c>=</c> 之后那一段原文；怎么解释它交给用这个值的动作。</summary>
    private static string TextValue(string raw) => raw[(raw.IndexOf('=', StringComparison.Ordinal) + 1)..];

    private static int HoldValue(string raw) =>
        int.TryParse(TextValue(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    private static bool IsPassthrough(string raw, IReadOnlyCollection<string>? passthrough) =>
        passthrough is not null
        && passthrough.Any(name => name.EndsWith('=')
            ? raw.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, raw, StringComparison.OrdinalIgnoreCase));

    private static string Unquote(string raw) => raw.Trim().Trim('"').Trim();
}
