using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bbu.Core.Configuration;
using Bbu.Core.Integration;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 设置页：每一项对应 <see cref="AppConfig"/> 的一个字段。
/// 项长什么样、值读写在哪一处都由 <see cref="SettingsModel"/> 那份描述表给，控件由模板生成；
/// 改动即存只有 <see cref="Push"/> 一条路，回填期间不发信号。数据目录与探测函数由宿主注入。
/// </summary>
public partial class SettingsPage : UserControl
{
    /// <summary>排除名单的去抖间隔：逐字符落盘会在盘上留下半截名单。</summary>
    private const int ExcludeDebounceMs = 400;

    private readonly DispatcherTimer _excludeSave;
    private readonly SettingsModel _model;

    private AppConfig? _config;
    private IConfigStore? _store;
    private IRunLog? _log;

    public SettingsPage()
    {
        InitializeComponent();

        _model = new SettingsModel(OnFieldChanged);
        DataContext = _model;

        _excludeSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ExcludeDebounceMs) };
        _excludeSave.Tick += (_, _) =>
        {
            _excludeSave.Stop();
            Push();
        };

        // 恢复默认不再是页脚按钮，而是「系统」组里的一个动作项（见描述表）。
        SetClose.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用户点了页头的关闭。宿主负责收掉这一层。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>用系统默认程序打开一个目录。宿主接到「打开输出目录」那一份实现上。</summary>
    public Action<string>? OpenDirectory { get; set; }

    /// <summary>一句反馈。宿主接到主窗口右下角的提示条上；页脚撤掉之后反馈走这里。</summary>
    public Action<string>? Toast { get; set; }

    /// <summary>破坏性开关拨到开那一次的问句通道，由宿主接到窗口内模态确认框上（没接就不问，照密码本那条删除确认）。</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>页面上的项。断言与宿主都从这里取，不按控件名找控件。</summary>
    public SettingsModel Model => _model;

    /// <summary>
    /// 接上配置、写盘器与日志。数据目录由宿主传进来：页面自己解析会带静态缓存。
    /// <paramref name="dataNotice"/> 是数据根降级时生成的说明；未降级时为 <c>null</c>。
    /// </summary>
    public void Attach(
        AppConfig config,
        IConfigStore store,
        IRunLog log,
        string dataDirectory,
        string? alternativeDirectory,
        string? dataNotice = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _store = store;
        _log = log;

        var dataDir = (SettingsDataDirField)_model["DataDirectory"];
        dataDir.Value = dataDirectory;
        // 说明只在一侧另有一份密码本时给。
        dataDir.Notice = alternativeDirectory is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                UiText.SettingsDataDirSplitAt,
                alternativeDirectory)
            : dataNotice;
        dataDir.Open = () => OpenDirectory?.Invoke(dataDirectory);

        ((SettingsActionField)_model["RestoreDefaults"]).Invoke = RestoreDefaults;
        ((SettingsShellField)_model["Shell"]).Toggle = ToggleShellMenu;

        LoadFrom(config);
        RefreshShellMenu();
    }

    /// <summary>接上配置并回填每一项。回填期间不写盘。</summary>
    public void LoadFrom(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _model.Pull(config);
    }

    /// <summary>当前控件上的配置。测试与宿主都从这里取，不必直接读取控件。</summary>
    public AppConfig? Current => _config;

    /// <summary>把每一项当前的值收集进配置并写盘。改动即存这一条路的唯一实现；真写成了才给一句反馈。</summary>
    public void Push()
    {
        if (Save())
        {
            Note(UiText.SettingsSaved);
        }
    }

    /// <summary>收集并写盘，不带反馈：改动即存与恢复默认都走它，反馈各自给各的（失败那一句由 <see cref="SaveConfig"/> 给）。</summary>
    private bool Save()
    {
        if (_config is not { } config || _store is not { } store)
        {
            return false;
        }

        foreach (var item in _model.Groups.SelectMany(group => group.AllItems))
        {
            item.Apply(config);
        }

        return SaveConfig(store, config);
    }

    /// <summary>状态那一句反馈：同时进日志与主窗口右下角的提示条。级别由方法名承载。</summary>
    public void Failure(string template, params object?[] args)
    {
        var message = Message(template, args);
        _log?.Failure(template, args);
        Toast?.Invoke(message);
    }

    public void Problem(string template, params object?[] args)
    {
        var message = Message(template, args);
        _log?.Problem(template, args);
        Toast?.Invoke(message);
    }

    public void Note(string template, params object?[] args)
    {
        var message = Message(template, args);
        _log?.Note(template, args);
        Toast?.Invoke(message);
    }

    /// <summary>
    /// 提示条那一句在这里成形；日志那一句由通道按模板自己填。两处各渲染一次（这里用当前文化、
    /// 通道用不变文化），今天逐字相同是因为实参都是裸占位符配字符串与整数 ——
    /// 「日志与提示条同字」这条不变量现在是巧合，不是结构保证。
    /// </summary>
    private static string Message(string template, object?[] args) =>
        args.Length == 0
            ? template
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);

    /// <summary>某一项的值变了：立即落盘；排除名单那一项走去抖，停手之后再写。</summary>
    private void OnFieldChanged(SettingsField field)
    {
        _excludeSave.Stop();
        if (field.Debounced)
        {
            _excludeSave.Start();
            return;
        }

        // 删除原压缩包是永久、不可恢复的破坏性开关：从关拨到开先问一次，问完才落盘。
        if (field is SettingsToggleField toggle
            && toggle.Key == SettingsCatalog.RemoveSourceKey
            && DestructiveAsk.BeforeRemovingSource(_config?.RemoveSource ?? false, toggle.Value))
        {
            _ = ConfirmRemoveSourceAsync(toggle);
            return;
        }

        Push();
    }

    /// <summary>拨到开那一次的问与答：点否定就把开关拨回关闭，这一次一个字都不落盘（回退走回填那条路）。</summary>
    private async Task ConfirmRemoveSourceAsync(SettingsToggleField toggle)
    {
        // 没接问句通道就不问（页面只有测试会这样造），与密码本那条删除确认同一形态（LibraryPage 的 AskAndRemoveAsync）。
        if (Confirm is { } ask
            && !await ask(UiText.SettingsRemoveSource, UiText.RemoveSourceAskBody, UiText.RemoveSourceAskConfirm))
        {
            toggle.Loading = true;
            try
            {
                toggle.Value = false;
            }
            finally
            {
                toggle.Loading = false;
            }

            return;
        }

        Push();
    }

    /// <summary>按钮项（恢复默认这类）被按下。动作由宿主在 Attach 时接上来。</summary>
    private void OnFieldAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SettingsActionField field })
        {
            field.Invoke?.Invoke();
        }
    }

    private void OnShellInstall(object? sender, RoutedEventArgs e) =>
        ((SettingsShellField)_model["Shell"]).Toggle?.Invoke(true);

    private void OnShellRemove(object? sender, RoutedEventArgs e) =>
        ((SettingsShellField)_model["Shell"]).Toggle?.Invoke(false);

    private void OnOpenDataDir(object? sender, RoutedEventArgs e) =>
        ((SettingsDataDirField)_model["DataDirectory"]).Open?.Invoke();

    /// <summary>装或卸右键菜单并刷新状态；注册表读写都在集成层，页面只读结论。</summary>
    private void ToggleShellMenu(bool install)
    {
        var count = install ? ShellMenu.InstallSelf().Count : ShellMenu.Uninstall().Count;
        // 没写全时把原因也报出来：只报数量与命令行一样没法定位（B-2026-162）
        // LastFailure 里已经带了错误码，别再追一个
        if (install && count < ShellMenu.Places.Count)
        {
            Failure(UiText.SettingsShellFailedAt, ShellMenu.LastFailure);
        }
        else
        {
            if (install)
            {
                Note(UiText.SettingsShellInstalledAt, count);
            }
            else
            {
                Problem(UiText.SettingsShellRemovedAt, count);
            }
        }

        RefreshShellMenu();
    }

    /// <summary>
    /// 状态标记与按钮文案都按当前注册状态给。三处都在才算已注册；打包形态两个按钮都禁用。
    /// <paramref name="packaged"/> 与集成层同义，留空按现场包身份判（测试拿它钉打包形态那一支）。
    /// </summary>
    public void RefreshShellMenu(bool? packaged = null)
    {
        var state = ShellMenu.State(packaged: packaged);
        var field = (SettingsShellField)_model["Shell"];

        // 菜单开关只在「菜单由包清单提供」的形态下给，那一档由 ShellMenu 按包身份判（B-2026-169）；
        // 没有包身份时菜单来自注册表三处键，设置页给的是「安装／移除」按钮。先算开关再算状态。
        field.ModernOn = ShellMenu.SwitchEnabled(ShellMenu.MenuSwitchValue);
        field.SwitchesVisible = state.FromPackage;

        // 两种形态各说各的事：打包形态报开关（清单声明）开没开，绿色版报三处键装没装。
        field.State = state.FromPackage
            ? field.MenuStateText
            : state.Installed ? UiText.SettingsShellRegistered : UiText.SettingsShellUnregistered;
        field.Registered = state.Installed;
        field.FromPackage = state.FromPackage;
        field.Command = state.Command.Length > 0 ? state.Command : null;
        field.InstallText = state.Installed ? UiText.SettingsShellReinstall : UiText.SettingsShellInstall;
    }

    /// <summary>打包形态下那一个切换按钮：翻一下开关写 HKCU，组件构造菜单时按这个值给可见性。</summary>
    private void OnMenuSwitchClick(object? sender, RoutedEventArgs e)
    {
        var field = (SettingsShellField)_model["Shell"];
        ShellMenu.SetSwitch(ShellMenu.MenuSwitchValue, !field.ModernOn);
        RefreshShellMenu();
    }

    private bool SaveConfig(IConfigStore store, AppConfig config)
    {
        // 写盘失败要留在页面上并说明原因：静默失败会让用户以为改好了。
        if (store.Save(config))
        {
            return true;
        }

        Failure(UiText.SettingsSaveFailedAt, store.LastError ?? string.Empty);
        return false;
    }

    /// <summary>恢复默认：默认值回填控件之后立刻落盘（走与别的项同一条写盘路径），排着的去抖要撤掉。</summary>
    private void RestoreDefaults()
    {
        _excludeSave.Stop();
        _model.Pull(new AppConfig());
        if (Save())
        {
            Note(UiText.SettingsRestoreSaved);
        }
    }

}
