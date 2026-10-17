using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Bbu.Core.Configuration;
using Bbu.Core.Services;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 设置页的一项：标签、控件形态与一个值。
/// 页面不再逐字段写"回填"与"收集"两张表：值从配置的哪一处读、写回哪一处，都由
/// <see cref="SettingsCatalog"/> 的那一行给，控件由模板按项的类型生成。
/// 回填期间 <see cref="Loading"/> 为真，值的变化不触发落盘。
/// </summary>
public abstract class SettingsField : INotifyPropertyChanged
{
    /// <summary>字段键。断言与宿主按它取项，不按控件名找控件。</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>值变了要落盘。页面在装配时注入；回填期间不会被调。</summary>
    public Action<SettingsField>? OnChanged { get; set; }

    public bool Loading { get; set; }

    /// <summary>这一项落盘要走去抖（排除名单逐字符落盘会在盘上留下半截名单）。</summary>
    public bool Debounced { get; init; }

    /// <summary>这一项跨整行，不参与两列排布（排除名单那种多行框）。</summary>
    public bool FullRow { get; init; }

    /// <summary>项名的一句提示，鼠标停在标题上时给；没有提示的项留空（排除名单的写法提示也走它）。</summary>
    public string? Hint { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>把配置里的值搬进这一项。</summary>
    internal abstract void Pull(AppConfig config);

    /// <summary>把这一项当前的值写回配置。</summary>
    internal abstract void Apply(AppConfig config);

    protected void Raise(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>值真的变了：先通知界面，不是回填就再喊一声"该落盘了"。</summary>
    protected void Toggled(string name)
    {
        Raise(name);
        if (!Loading)
        {
            OnChanged?.Invoke(this);
        }
    }
}

/// <summary>数值：上下限由描述表给，回填时超范围的旧值夹到最近的一端。</summary>
public sealed class SettingsNumberField : SettingsField
{
    private decimal? _value;

    public required Func<AppConfig, double> Read { get; init; }

    public required Action<AppConfig, double> Write { get; init; }

    public required decimal Minimum { get; init; }

    public required decimal Maximum { get; init; }

    public required decimal Increment { get; init; }

    public required string Format { get; init; }

    public decimal? Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            Toggled(nameof(Value));
        }
    }

    internal override void Pull(AppConfig config) =>
        Value = Math.Clamp((decimal)Read(config), Minimum, Maximum);

    internal override void Apply(AppConfig config) =>
        Write(config, (double)(Value ?? Minimum));
}

/// <summary>开关：右列只放一个勾选框，标题在左列，同一句话不出现两遍。</summary>
public sealed class SettingsToggleField : SettingsField
{
    private bool _value;

    public required Func<AppConfig, bool> Read { get; init; }

    public required Action<AppConfig, bool> Write { get; init; }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            Toggled(nameof(Value));
        }
    }

    internal override void Pull(AppConfig config) => Value = Read(config);

    internal override void Apply(AppConfig config) => Write(config, Value);
}

/// <summary>下拉：选项由描述表给，选中的下标就是配置里那个数。</summary>
public sealed class SettingsChoiceField : SettingsField
{
    private int _index;

    public required Func<AppConfig, int> Read { get; init; }

    public required Action<AppConfig, int> Write { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value)
            {
                return;
            }

            _index = value;
            Toggled(nameof(Index));
        }
    }

    internal override void Pull(AppConfig config) =>
        Index = Math.Clamp(Read(config), 0, Math.Max(0, Options.Count - 1));

    internal override void Apply(AppConfig config) => Write(config, Index);
}

/// <summary>多行文本（排除名单）：写法提示走 Hint，不占纵向空间。</summary>
public sealed class SettingsTextField : SettingsField
{
    private string _value = string.Empty;

    public required Func<AppConfig, string> Read { get; init; }

    public required Action<AppConfig, string> Write { get; init; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            Toggled(nameof(Value));
        }
    }

    internal override void Pull(AppConfig config) => Value = Read(config);

    internal override void Apply(AppConfig config) => Write(config, Value);
}

/// <summary>按钮项（重新检测这类）。它不落盘，动作由页面注入。</summary>
public sealed class SettingsActionField : SettingsField
{
    public required string ButtonText { get; init; }

    public Action? Invoke { get; set; }

    internal override void Pull(AppConfig config)
    {
    }

    internal override void Apply(AppConfig config)
    {
    }
}

/// <summary>Windows 集成：状态写在按钮旁边，装与卸是页面接上来的动作。</summary>
public sealed class SettingsShellField : SettingsField
{
    private string _state = string.Empty;
    private string _installText = string.Empty;
    private bool _registered;
    private bool _fromPackage;
    private bool _modernOn = true;
    private bool _modernVisible;
    private string? _command;

    public string State
    {
        get => _state;
        set
        {
            _state = value;
            Raise();
        }
    }

    public string InstallText
    {
        get => _installText;
        set
        {
            _installText = value;
            Raise();
        }
    }

    public bool Registered
    {
        get => _registered;
        set
        {
            _registered = value;
            Raise();
            Raise(nameof(CanRemove));
        }
    }

    public bool FromPackage
    {
        get => _fromPackage;
        set
        {
            _fromPackage = value;
            Raise();
            Raise(nameof(CanRemove));
        }
    }

    /// <summary>三处键存在即可删，打包形态也一样 —— 绿色版留下的键不会自己消失，留着会让「显示更多选项」出现两个同名项（B-2026-160）。</summary>
    public bool CanRemove => _registered;

    public string? Command
    {
        get => _command;
        set
        {
            _command = value;
            Raise();
        }
    }

    /// <summary>装或卸。参数为真表示装。</summary>
    public Action<bool>? Toggle { get; set; }

    /// <summary>菜单开关是否显示：只有菜单由包清单提供的形态才给 —— 那个值写给包里的组件读（B-2026-163、B-2026-169）。</summary>
    public bool SwitchesVisible
    {
        get => _modernVisible;
        set
        {
            _modernVisible = value;
            Raise();
        }
    }

    /// <summary>菜单开关的当下态。便携形态没有那份声明，这一项由 <see cref="SwitchesVisible"/> 收起。</summary>
    public bool ModernOn
    {
        get => _modernOn;
        set
        {
            _modernOn = value;
            Raise();
            Raise(nameof(MenuStateText));
            Raise(nameof(MenuSwitchText));
        }
    }

    /// <summary>打包形态下右上那一句状态：说的是开关开没开，不是注册表三处键装没装。</summary>
    public string MenuStateText => _modernOn ? UiText.SettingsMenuOn : UiText.SettingsMenuOff;

    /// <summary>打包形态下那一个切换按钮上的字：按当下的开关态给动作，与绿色版两个按钮同一位置。</summary>
    public string MenuSwitchText => _modernOn ? UiText.SettingsMenuHide : UiText.SettingsMenuShow;

    internal override void Pull(AppConfig config)
    {
    }

    internal override void Apply(AppConfig config)
    {
    }
}

/// <summary>数据目录：只读路径加一个「打开」，旁边一行提示。它没有可改的值。</summary>
public sealed class SettingsDataDirField : SettingsField
{
    private string _value = string.Empty;
    private string? _notice;

    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            Raise();
        }
    }

    public string? Notice
    {
        get => _notice;
        set
        {
            _notice = value;
            Raise();
        }
    }

    public Action? Open { get; set; }

    internal override void Pull(AppConfig config)
    {
    }

    internal override void Apply(AppConfig config)
    {
    }
}

/// <summary>一组设置项：组标题、组图标与组内各项。分组是这一页唯一的分类手段。</summary>
public sealed class SettingsGroup
{
    public required string Title { get; init; }

    public required Geometry Icon { get; init; }

    /// <summary>组内排几列：引擎与解压两列，系统那一组单列（看图反馈）。</summary>
    public int Columns { get; init; } = 1;

    /// <summary>跨整行的项：不参与两列排布，排在普通项下面（排除名单那种多行框）。</summary>
    public IReadOnlyList<SettingsField> WideItems { get; init; } = [];

    /// <summary>全部项：普通项加跨整行的项。遍历一律走它，只走 <see cref="Items"/> 会漏掉排除名单。</summary>
    public IReadOnlyList<SettingsField> AllItems => [.. Items.Concat(WideItems)];

    /// <summary>这一组走两列面板。模板按它二选一，不用绑定到面板里（那会落到反射绑定）。</summary>
    public bool TwoColumn => Columns > 1;

    public required IReadOnlyList<SettingsField> Items { get; init; }
}

/// <summary>设置页的全部行。控件由模板按项的类型生成，值与配置之间的搬运只走这里。</summary>
public sealed class SettingsModel
{
    public SettingsModel(Action<SettingsField> onChanged) => Groups = SettingsCatalog.Build(onChanged);

    public IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>按字段键取一项。断言与宿主都从这里找；跨整行的项也算在内。</summary>
    public SettingsField this[string key] =>
        Groups.SelectMany(group => group.AllItems).First(item => item.Key == key);

    /// <summary>把配置搬进每一项。回填期间不落盘，用户落笔之后才生效。</summary>
    public void Pull(AppConfig config)
    {
        foreach (var item in Groups.SelectMany(group => group.AllItems))
        {
            item.Loading = true;
            try
            {
                item.Pull(config);
            }
            finally
            {
                item.Loading = false;
            }
        }
    }
}

/// <summary>
/// 设置页的字段描述表：每一行写清一项的键、标签、控件形态与它在配置里的位置。
/// 加一个配置字段就加一行 —— 控件的生成、回填与落盘收集都由这一行推出来。
/// </summary>
internal static class SettingsCatalog
{
    /// <summary>「解压成功后删除原压缩包」那一项的键。它拨到开时要过一次破坏性闸门，调用点按它认项。</summary>
    internal const string RemoveSourceKey = "RemoveSource";

    internal static IReadOnlyList<SettingsGroup> Build(Action<SettingsField> onChanged) =>
    [
        Group(UiText.SettingsGroupExtract, Icons.GroupExtract, 2, onChanged, [
            Number(
                "MaxDepth", UiText.SettingsMaxDepth,
                config => config.MaxDepth, (config, value) => config.MaxDepth = (int)value,
                1, 20, 1, "0"),
            Number(
                "TimeoutMinutes", UiText.SettingsTimeout,
                config => config.TimeoutMinutes, (config, value) => config.TimeoutMinutes = value,
                1, 600, 5, "0", hint: UiText.SettingsTimeoutHint),
            Number(
                "MinFreeGb", UiText.SettingsMinFree,
                config => config.MinFreeGb, (config, value) => config.MinFreeGb = value,
                0, 1000000000, 0.5m, "0.0"),
            Choice(
                "PasswordWorkers", UiText.SettingsWorkers,
                config => WorkerIndex(config.PasswordWorkers),
                (config, value) => config.PasswordWorkers = WorkerCounts[value],
                WorkerOptions()),
            Toggle(
                SettingsCatalog.RemoveSourceKey, UiText.SettingsRemoveSource,
                config => config.RemoveSource, (config, value) => config.RemoveSource = value),
            Toggle(
                "RemoveIntermediate", UiText.SettingsRemoveIntermediate,
                config => config.RemoveIntermediate, (config, value) => config.RemoveIntermediate = value),
            Toggle(
                "FlattenSingleChild", UiText.SettingsFlatten,
                config => config.FlattenSingleChild, (config, value) => config.FlattenSingleChild = value,
                hint: UiText.SettingsFlattenHint),
            Toggle(
                "CleanDelete", UiText.SettingsCleanDelete,
                config => config.CleanDelete, (config, value) => config.CleanDelete = value),
            Toggle(
                "ScanAppended", UiText.SettingsScanAppended,
                config => config.ScanAppended, (config, value) => config.ScanAppended = value,
                hint: UiText.SettingsScanAppendedHint),
            Choice(
                "FolderScan", UiText.SettingsFolderScan,
                config => (int)config.FolderScan,
                (config, value) => config.FolderScan = (FolderScan)value,
                [
                    UiText.SettingsFolderScanTopOnly,
                    UiText.SettingsFolderScanBelow,
                    UiText.SettingsFolderScanDeep,
                ],
                hint: UiText.SettingsFolderScanHint),
            Text(
                "ExcludedExtensions", UiText.SettingsExcluded,
                config => string.Join(" ", config.ExcludedExtensions),
                (config, value) => config.ExcludedExtensions = [.. Probe.NormalizeExts(Split(value))],
                UiText.SettingsExcludedHint,
                debounced: true,
                fullRow: true),
        ]),
        Group(UiText.SettingsGroupShell, Icons.GroupShell, 1, onChanged, [
            new SettingsShellField
            {
                Key = "Shell",
                Label = UiText.SettingsShell,
            },
            new SettingsDataDirField
            {
                Key = "DataDirectory",
                Label = UiText.AboutDataDirLabel,
            },
            Action("RestoreDefaults", UiText.SettingsRestoreDefaults, UiText.SettingsRestoreDefaults),
        ]),
    ];

    /// <summary>组装一组：接上回调，并把跨整行的项挑出来（模板里排在普通项下面）。</summary>
    private static SettingsGroup Group(
        string title,
        string icon,
        int columns,
        Action<SettingsField> onChanged,
        SettingsField[] items)
    {
        foreach (var item in items)
        {
            item.OnChanged = onChanged;
        }

        return new SettingsGroup
        {
            Title = title,
            Icon = Geometry.Parse(icon),
            Columns = columns,
            Items = [.. items.Where(item => !item.FullRow)],
            WideItems = [.. items.Where(item => item.FullRow)],
        };
    }

    private static SettingsNumberField Number(
        string key,
        string label,
        Func<AppConfig, double> read,
        Action<AppConfig, double> write,
        decimal min,
        decimal max,
        decimal step,
        string format,
        string? hint = null) => new()
        {
            Key = key,
            Label = label,
            Read = read,
            Write = write,
            Minimum = min,
            Maximum = max,
            Increment = step,
            Format = format,
            Hint = hint,
        };

    private static SettingsToggleField Toggle(
        string key,
        string label,
        Func<AppConfig, bool> read,
        Action<AppConfig, bool> write,
        string? hint = null) => new()
        {
            Key = key,
            Label = label,
            Read = read,
            Write = write,
            Hint = hint,
        };

    private static SettingsChoiceField Choice(
        string key,
        string label,
        Func<AppConfig, int> read,
        Action<AppConfig, int> write,
        IReadOnlyList<string> options,
        string? hint = null) => new()
        {
            Key = key,
            Label = label,
            Read = read,
            Write = write,
            Options = options,
            Hint = hint,
        };

    private static SettingsTextField Text(
        string key,
        string label,
        Func<AppConfig, string> read,
        Action<AppConfig, string> write,
        string? hint = null,
        bool debounced = false,
        bool fullRow = false) => new()
        {
            Key = key,
            Label = label,
            Read = read,
            Write = write,
            Hint = hint,
            Debounced = debounced,
            FullRow = fullRow,
        };

    private static SettingsActionField Action(
        string key,
        string label,
        string buttonText) => new()
        {
            Key = key,
            Label = label,
            ButtonText = buttonText,
        };

    /// <summary>线程数档位：自动加 2 的幂，到本机逻辑核数为止（与 1.1.0 的选项同口径）。</summary>
    private static readonly IReadOnlyList<int> WorkerCounts =
        [.. new[] { 0, 1, 2, 4, 8, 16 }.Where(count => count == 0 || count <= Environment.ProcessorCount)];

    /// <summary>档位的显示文案：0 写「自动」，其余写「N 个线程」。</summary>
    private static IReadOnlyList<string> WorkerOptions() =>
        [.. WorkerCounts.Select(count => count == 0
            ? UiText.SettingsWorkersAuto
            : string.Format(CultureInfo.CurrentCulture, UiText.SettingsWorkersAt, count))];

    /// <summary>配置里那个数落在第几档：取不超过它的最大档，比最小档还小就落「自动」。</summary>
    private static int WorkerIndex(int workers)
    {
        var index = 0;
        for (var i = 1; i < WorkerCounts.Count; i++)
        {
            if (WorkerCounts[i] <= workers)
            {
                index = i;
            }
        }

        return index;
    }

    /// <summary>排除名单的分隔：逗号、全角逗号、空格、分号都当分隔，与 1.1.0 同口径。</summary>
    private static IEnumerable<string> Split(string? text) =>
        (text ?? string.Empty).Split(
            [',', '，', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
}
