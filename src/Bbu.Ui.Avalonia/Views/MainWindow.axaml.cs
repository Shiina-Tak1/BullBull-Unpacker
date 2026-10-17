using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.SingleInstance;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Interop;
using BbuMotion = Bbu.Ui.Avalonia.Theme.Motion;
using ShapePath = Avalonia.Controls.Shapes.Path;

// 简单名 `Ui` 在这个命名空间里会先绑到外层命名空间 `Bbu.Ui`，早于 using 引入的文案域，
// 于是 `Ui.VersionAt` 被当成「命名空间 Bbu.Ui 的成员」。所以文案域一律走这个别名。
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 主窗口：一屏之内把清单、日志、进度与四个动作都摆下；状态、计数与备注取 <see cref="Runner"/> 的结论，
/// 文案取 <c>Texts</c> 表——视图里再判一次就会出现两处口径。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>日志面板同时保留的行数：只留最近 N 行，防长时间运行时行数无限增长。取值无实测依据，仅作上限。</summary>
    private const int MaxLogLines = 500;

    /// <summary>关窗时等这一批收尾的秒数。到点仍没收工就不关窗，见 <see cref="OnWindowClosing"/>。</summary>
    private const int CloseStopSeconds = 10;

    /// <summary>这一趟实际的收尾宽限。是字段不是常量：断言要把它压到毫秒级才等得起「收不干净」那一支。</summary>
    private TimeSpan _closeGrace = TimeSpan.FromSeconds(CloseStopSeconds);

    /// <summary>行入场动画只给前这么多行：100 个任务不该产生 100 个动画对象。</summary>
    private const int EnterRows = 12;

    /// <summary>转发进来的路径攒多久再提交。见 <see cref="QueueLaunch"/>。</summary>
    private const int LaunchBatchDelayMs = 220;

    /// <summary>层级刻度最多画几格。上限与设置页的取值范围一致。</summary>
    private const int MaxTickCount = 20;

    /// <summary>基准窗口。</summary>
    private const double BaseWidth = 1160;
    private const double BaseHeight = 660;

    /// <summary>标题栏拖动的起手阈值（DIP）：取间距刻度最小档，用于排除指针抖动。</summary>
    private const double TitleBarDragThreshold = 4;

    private readonly List<string> _roots = [];
    private readonly List<Job> _jobs = [];
    private readonly List<JobRow> _rows = [];

    /// <summary>每一行的列网格。列宽只有一份（<see cref="_cols"/>），变化时同步到每一行。</summary>
    private readonly List<Grid> _rowCols = [];
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _batchClock = new();
    private readonly IRunLog _log;

    /// <summary>面板的级别阈值：低于它的正文只进日志文件。</summary>
    private readonly IConfigStore _configStore;
    private readonly EnginePaths _engines;
    private readonly SevenZipEngine _engine;
    private readonly Vault _vault;
    private readonly ToastHost _toasts;

    private AppConfig _config;

    /// <summary>清单四列的宽度。行与表头共用这一份，算法在 <see cref="ColumnLayout"/>。</summary>
    private double[] _cols = ColumnLayout.Default();

    /// <summary>当前档位。按内容区宽度算，判据与断点在 <see cref="Breakpoints"/>。</summary>
    private ShellTier _tier = ShellTier.Wide;

    /// <summary>底部日志面板是否展开。默认收起，头部跑任务时与宽档同形，其余时候只有一行摘要。</summary>
    private bool _foldOpen;

    /// <summary>工具条当前是否为紧凑形：次要动作只留图标。该状态按其自身阈值判定，与档位无关。</summary>
    private bool _compact;

    /// <summary>列宽落盘的合并计时器：拖完 0.7 秒才写一次盘，不按像素写文件。</summary>
    private DispatcherTimer? _colSave;

    /// <summary>转发与冷启动传入的路径，等攒批计时器到期后一起提交。</summary>
    private readonly List<string> _launchPending = [];

    /// <summary>攒批计时器。第一次收到载荷时才建。</summary>
    private DispatcherTimer? _launchTimer;

    /// <summary>攒批里有没有要求自动开跑的载荷。</summary>
    private bool _launchAuto;

    /// <summary>表头右键点中的那一列（菜单第一项用）。</summary>
    private int _menuColumn;

    /// <summary>扫描这一趟的取消令。它与 <see cref="_runCts"/> 分家：运行中还能再拖入，两趟会同时活着，先收尾的那趟会把共用的字段置空。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>正在跑的那一批的取消令。</summary>
    private CancellationTokenSource? _runCts;

    /// <summary>这一批跑的是哪些项。分母取它：跑到一半新加的项不算进分母，进度不会往回跳。</summary>
    private List<Job> _batch = [];

    private Task? _runTask;

    private Task _scanChain = Task.CompletedTask;
    private bool _closing;
    private bool _forceClose;
    private bool _running;

    private bool _scanning;
    private bool _queued;
    private bool _paused;
    private bool _stop;
    private bool _failedOnly;

    /// <summary>日志写不进去那条提示只出一次：同一条坏消息反复冒出来，看不出哪条是新的。</summary>
    private bool _logWriteFailureToasted;
    private PageKind _page;

    /// <summary>标题栏按下了、还没到拖动阈值；按下那一刻的位置与事件参数都要留着。见 <see cref="OnTitleBarMoved"/>。</summary>
    private bool _movingTitleBar;

    private Point _titleBarOrigin;

    private PointerPressedEventArgs? _titleBarPress;

    private LogProgress? _progress;

    private DateTime _progressAt = DateTime.UtcNow;

    private IDisposable? _dropHide;
    private bool _dropActive;

    /// <summary>上一批的收尾摘要。空闲时那一格显示它，而不是空白。</summary>
    private string _lastBatchText = string.Empty;

    private TimeSpan _pausedTotal;
    private DateTime? _pausedAt;

    /// <summary>整页里开着哪一个。整页同一时刻只开一个。</summary>
    private enum PageKind
    {
        None,
        Library,
        Settings,
    }

    /// <summary>行的 UI 句柄。不绑数据：直接持有控件引用，状态切换就是改属性 + 换 class。</summary>
    private sealed class JobRow
    {
        public Job Job = null!;
        public Border Root = null!;
        public Grid Cols = null!;
        public TextBlock Name = null!;
        public TextBlock Kind = null!;
        public TextBlock Password = null!;
        public ShapePath Icon = null!;
        public TextBlock Status = null!;
        public TextBlock ChildCount = null!;
        public StackPanel StatusCell = null!;
        public Border StatusBox = null!;
    }

    public MainWindow()
        : this(DataDirectory.Resolve(), AppContext.BaseDirectory, null)
    {
    }

    /// <summary>装配入口：日志由入口建好后传进来，窗口不自建第二份。</summary>
    public MainWindow(IRunLog log)
        : this(DataDirectory.Resolve(), AppContext.BaseDirectory, log)
    {
    }

    /// <summary>带落点的构造：两个目录由调用方传入，正常启动走无参或注入那一支；断言用它换成临时目录，因此不会读到作者的配置。</summary>
    public MainWindow(string dataDirectory, string programDirectory, IRunLog? log = null)
    {
        InitializeComponent();

        // 没注入日志的那一支只给不落盘的场合用，兜底替身不构造 RunLog。
        _log = log ?? SilentRunLog.Instance;
        _configStore = new JsonConfigStore(Path.Combine(dataDirectory, AppInfo.ConfigFileName), _log);
        _config = _configStore.Load();
        _engines = EngineLocator.Resolve(programDirectory.Length > 0 ? programDirectory : dataDirectory);
        _engine = new SevenZipEngine(_engines, _log, Math.Max(1.0, _config.TimeoutMinutes) * 60.0);
        _vault = Vault.FromDirectory(dataDirectory);
        _vault.Load();
        _toasts = Toasts;

        // 引擎与密码层的心跳走同一条通道：进度行只进进度位，结果行才进面板。
        _log.LineWritten += line => Dispatcher.UIThread.Post(() => OnLine(line));

        // 写失败可能来自后台线程，提示要在界面线程上出。
        _log.WriteFailed += reason => Dispatcher.UIThread.Post(() => OnLogWriteFailed(reason));

        // 无边框窗口的拖动 / 缩放 / 圆角；标题栏：拖动与双击共用这一处判断。双击不另接 DoubleTapped，两处都接会互相抵消（B-2026-118）
        TopBar.PointerPressed += OnTitleBarPressed;
        TopBar.PointerMoved += OnTitleBarMoved;
        TopBar.PointerReleased += OnTitleBarReleased;
        HookGrip(GripN, WindowEdge.North);
        HookGrip(GripS, WindowEdge.South);
        HookGrip(GripW, WindowEdge.West);
        HookGrip(GripE, WindowEdge.East);
        HookGrip(GripNW, WindowEdge.NorthWest);
        HookGrip(GripNE, WindowEdge.NorthEast);
        HookGrip(GripSW, WindowEdge.SouthWest);
        HookGrip(GripSE, WindowEdge.SouthEast);
        HookColumns();

        Opened += async (_, _) =>
        {
            WindowChrome.TryRoundCorners(this);
            UpdateMaxGlyph();
            FitToScreen();
            ApplyTier();
            FitPopup();
            // 诊断要等平台把 --size 与基准尺寸应用下去：Opened 当场量到的还是 XAML 里那份 1160×660。
            DispatcherTimer.RunOnce(DumpDiagnostics, TimeSpan.FromMilliseconds(400));
            RefitColumns();
            FillAbout();
            RefreshActivity();
            await AutoDriveAsync();

            // 收货台接上之后，窗口出现之前收到的转发路径一次性交出来。
            LaunchQueue.Current.Attach(payload =>
                Dispatcher.UIThread.Post(() => QueueLaunch(payload)));
        };

        // 档位跟内容区宽度走；浮窗的上限跟窗口走，缩窗口时它要跟着收，四周始终留住留白。
        SizeChanged += (_, _) =>
        {
            ApplyTier();
            FitPopup();
        };

        // 键盘可达：空格 = 开始解压，Esc = 关掉当前浮层
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Space && Actions.CanStart(_running, _jobs))
            {
                e.Handled = true;
                await StartRunAsync();
            }
            else if (e.Key == Key.Escape)
            {
                CloseTopLayer();
            }
            else if (e.Key == Key.Delete && !_running)
            {
                e.Handled = true;
                await ClearListAsync();
            }
        };

        // 确认框开着时回车归它的默认项（B-2026-316），与上面那一段同属窗口的键盘处理：焦点不在框里时，
        // 根上的默认按钮回退先注册者先吃，回车会落到底层的「保存」上 —— 只能在隧道段接管，否则焦点那一颗先点自己。
        AddHandler(KeyDownEvent, OnPromptEnter, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        BtnAdd.Click += async (_, _) => await PickFilesAsync();
        BtnAdd2.Click += async (_, _) => await PickFilesAsync();
        BtnAddDir.Click += async (_, _) => await PickFolderAsync();
        BtnAddDir2.Click += async (_, _) => await PickFolderAsync();
        BtnSelectAll.Click += (_, _) => ToggleSelectAll();
        BtnRemoveSelected.Click += (_, _) => RemoveSelectedRows();
        BtnClearList.Click += async (_, _) => await ClearListAsync();
        BtnClearLog.Click += (_, _) => ClearLog();
        BtnStart.Click += (_, _) => StartOrResume();
        BtnPause.Click += (_, _) => TogglePause();
        BtnStop.Click += (_, _) => StopRun();
        BtnOpenOut.Click += (_, _) => OpenOutput();
        BtnOutSum.Click += (_, _) => ShowOutPanel();
        BtnAbout.Click += (_, _) => ToggleOverlay(true);
        AboutRepo.Click += (_, _) => OpenTarget(UiText.RepoUrl, UiText.ToastOpenLinkFailedAt);
        AboutStore.Click += (_, _) => OpenTarget(UiText.StoreUrl, UiText.ToastOpenLinkFailedAt);
        AboutLicense.Click += (_, _) => OpenTarget(UiText.AboutLicenseUrl, UiText.ToastOpenLinkFailedAt);
        BtnOverlayClose.Click += (_, _) => ToggleOverlay(false);
        BtnOverlayOk.Click += (_, _) => ToggleOverlay(false);
        OverlayScrim.PointerPressed += (_, _) => ToggleOverlay(false);
        BtnMin.Click += (_, _) => WindowState = WindowState.Minimized;
        BtnMax.Click += (_, _) => ToggleMaximize();
        BtnClose.Click += (_, _) => Close();

        BtnErrOnly.Click += (_, _) => ApplyLogFilter();
        SegAll.Click += (_, _) => SetFilter(false);
        SegFail.Click += (_, _) => SetFilter(true);
        LogToggle.Click += (_, _) => ToggleFold();

        OutClose.Click += (_, _) => HideLayer(OutPanel);
        OutCancel.Click += (_, _) => HideLayer(OutPanel);
        OutScrim.PointerPressed += (_, _) => HideLayer(OutPanel);
        OutSave.Click += async (_, _) => await ApplyOutSettingsAsync();
        OutBrowse.Click += async (_, _) => await PickOutputDirectoryAsync();

        // 浮窗与确认框：两个浮窗共用一个宿主，所以"同时只开一个"是结构性的。点遮罩不关——这两页都是改完即存，误关会丢掉尚未落盘的改动。
        BtnLibrary.Click += (_, _) => TogglePage(PageKind.Library, BtnLibrary);
        BtnSettings.Click += (_, _) => TogglePage(PageKind.Settings, BtnSettings);
        LibPage.CloseRequested += (_, _) => HidePage();
        SetPage.CloseRequested += (_, _) => HidePage();
        LibPage.Confirm = (title, body, confirmText) => Ask.AskAsync(title, body, confirmText);
        LibPage.PickImportFile = PickImportFileAsync;
        LibPage.Toast = (message, tone) => Toast(message, tone);
        SetPage.OpenDirectory = path => OpenTarget(path, UiText.ToastOpenDirFailedAt);
        SetPage.Toast = message => Toast(message);
        // 设置页里那一个破坏性开关的问句走同一个确认框壳子。
        SetPage.Confirm = (title, body, confirmText) => Ask.AskAsync(title, body, confirmText);
        // 主题只有深色一套，窗口与 App 都定死它（见 Theme\Tokens.axaml），这里不再有主题入口。
        LibPage.Attach(_vault, _log);
        SetPage.Attach(
            _config,
            _configStore,
            _log,
            dataDirectory,
            DataDirectory.AlternativeCandidate(DataDirectory.ResolveRoot()),
            DataDirectory.LastNote);

        // 用时每 100ms 原地刷新；间隔到一秒以上时，不足一秒的任务小数位不再变化。
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => RefreshTick();

        // 关窗：有任务在跑时不许一关就走人，后台还在写盘是 B-2026-022
        Closing += OnWindowClosing;
        Closed += (_, _) => LaunchQueue.Current.Detach();

        VersionChip.Text = string.Format(CultureInfo.InvariantCulture, UiText.VersionAt, AppInfo.Version);
        RefreshOutSummary();
        RefreshChrome();
        ClearProgress();

        // 起步就把构成条算一次：否则六段保持 XAML 里的初值，条左端会留下一串残色。
        RefreshProgress();
    }

    /// <summary>转发与冷启动传入的路径。提交口径与拖入、两个选择器完全相同。</summary>
    private void QueueLaunch(LaunchPayload payload)
    {
        if (payload.Paths.Count == 0)
        {
            Activate();
            return;
        }

        foreach (var path in payload.Paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                _launchPending.Add(path);
                continue;
            }

            _log.Problem(IntegrationText.LaunchPathMissingAt, path);
        }

        _launchAuto |= payload.Auto;
        if (_launchPending.Count == 0)
        {
            return;
        }

        _launchTimer ??= CreateLaunchTimer();
        _launchTimer.Stop();
        _launchTimer.Start();
    }

    private DispatcherTimer CreateLaunchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LaunchBatchDelayMs) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var batch = _launchPending.ToList();
            _launchPending.Clear();
            var auto = _launchAuto;
            _launchAuto = false;

            await AddPathsAsync(batch);
            if (auto)
            {
                await StartRunAsync();
            }
        };
        return timer;
    }

    /// <summary>清单的唯一来源，一次只扫一遍全量：父子下标是相对这张表的，追加会错位。</summary>
    private static string Norm(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }

    /// <summary><c>--drop=</c> 与拖入、两个选择器三个入口共用的一条口径；运行中照收，进来的项排到清单里等这一批跑完接着处理。</summary>
    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            Toast(UiText.ToastNothingAdded, Levels.FailureTone);
            return;
        }

        var known = new HashSet<string>(_roots.Select(Norm), StringComparer.Ordinal);
        var fresh = paths.Where(path => known.Add(Norm(path))).ToList();
        if (fresh.Count == 0)
        {
            // 这不是失败：目标已在清单里，没有任何一步出错。按"需要注意"报，用户不必去查原因。
            Toast(
                string.Format(CultureInfo.CurrentCulture, UiText.ToastAlreadyInListAt, Path.GetFileName(paths[0])),
                ToastTone.Warn);
            return;
        }

        _roots.AddRange(fresh);
        // 清单是整表重扫的：先记住扫描前的那些行，扫完再数这一次真加进来的行数（按包路径比）。
        var before = _jobs.Select(job => job.ArchivePath).ToHashSet(StringComparer.Ordinal);
        await RebuildAsync();
        Toast(AddedToastText(
            fresh.Count,
            fresh.Count(Directory.Exists),
            _jobs.Count(job => !before.Contains(job.ArchivePath))));
    }

    /// <summary>加入清单那一句提示：全是文件时沿用既有那一句，带文件夹时把文件夹个数与清单新增的行数都报出来。</summary>
    public static string AddedToastText(int roots, int folders, int addedRows) => folders switch
    {
        0 => string.Format(CultureInfo.CurrentCulture, UiText.ToastAddedAt, roots),
        var all when all == roots => string.Format(CultureInfo.CurrentCulture, UiText.ToastAddedFolderAt, all, addedRows),
        var some => string.Format(CultureInfo.CurrentCulture, UiText.ToastAddedMixedAt, roots, some, addedRows),
    };

    /// <summary>整表重扫。扫描在后台线程上跑；同一时刻只有一趟，两趟并排会互相顶掉（见 <see cref="_scanChain"/>）。</summary>
    private async Task RebuildAsync()
    {
        var previous = _scanChain;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanChain = completion.Task;
        await previous;
        try
        {
            await ScanOnceAsync();
        }
        finally
        {
            completion.SetResult();
        }
    }

    /// <summary>一趟整表重扫：扫描期占着活动槽与「停止」，扫完按结果重建清单。</summary>
    private async Task ScanOnceAsync()
    {
        var options = new ScanOptions
        {
            ScanAppended = _config.ScanAppended,
            FolderScan = _config.FolderScan,
            ExcludeExts = _config.ExcludedExtensions,

            Log = _log,

            AskFuseContinue = AskScanFuseContinue,
        };

        var roots = _roots.ToList();
        _cts = new CancellationTokenSource();
        _scanning = true;
        RefreshChrome();
        RefreshActivity();

        IReadOnlyList<Job> scanned = [];
        bool stopped;
        try
        {
            var token = _cts.Token;
            scanned = await Task.Run(() => Scan.Run(roots, options with { Cancellation = token }));
        }
        finally
        {
            stopped = _cts.IsCancellationRequested;
            _scanning = false;
            _cts.Dispose();
            _cts = null;
            ClearProgress();
        }

        if (_running)
        {
            // 跑着的那一批项被 Runner 与行表同时持有：整表换掉等于把它们的进度与状态一起丢掉，所以只把这次扫描里新出现的项追加进来（按包路径去重），跑着的那几项原样留着。
            var known = _jobs.Select(item => item.ArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _jobs.AddRange(scanned.Where(item => known.Add(item.ArchivePath)));
        }
        else
        {
            _jobs.Clear();
            _jobs.AddRange(scanned);
        }

        RebuildRows();

        if (stopped)
        {
            _log.Problem(UiText.ScanStoppedIncomplete);
            Toast(UiText.ScanStoppedIncomplete, ToastTone.Warn);
        }

        RefreshChrome();
        RefreshProgress();
        RefreshActivity();

        if (_queued)
        {
            _queued = false;
            await RunAsync();
        }
    }

    private async Task PickFilesAsync()
    {
        var everything = new FilePickerFileType(UiText.PickFilesFilterAll) { Patterns = ["*.*"] };

        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.PickFilesTitle,
            AllowMultiple = true,
            FileTypeFilter = [everything],
        });

        var paths = picked.Select(item => item.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        await AddPathsAsync(paths!);
    }

    private async Task PickFolderAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiText.PickFolderTitle,
            AllowMultiple = true,
        });

        var paths = picked.Select(item => item.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        await AddPathsAsync(paths!);
    }

    private async Task PickOutputDirectoryAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiText.OutputCustomDir,
            AllowMultiple = false,
        });

        var path = picked.Select(item => item.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (path is not null)
        {
            OutDirBox.Text = path;
        }
    }

    /// <summary>选一个密码文件来导入。三种分隔与解析都在密码层，这里只要一个路径。</summary>
    private async Task<string?> PickImportFileAsync() =>
        await PickLocalFileAsync(UiText.LibraryPickImportTitle, UiText.LibraryPickImportFilter, "*.txt");

    private async Task<string?> PickLocalFileAsync(string title, string filterName, string pattern)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = [pattern] }],
        });

        return picked.Select(item => item.TryGetLocalPath()).FirstOrDefault(path => !string.IsNullOrEmpty(path));
    }

    /// <summary>中央那一块只有一个槽位：拖放态显示拖放提示；扫描期与清单非空时都不显示空状态。</summary>
    private void ShowCentreSlot()
    {
        DropZone.IsVisible = _dropActive;
        DropHintText.Text = _running ? UiText.DropHintQueued : UiText.DropHint;
        EmptyState.IsVisible = !_dropActive && !_scanning && _jobs.Count == 0;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;

        // 拖着文件在窗口上方时给一个明确的放置提示，放开或移出即收起。
        _dropHide?.Dispose();
        _dropHide = null;
        _dropActive = files;
        ShowCentreSlot();
        e.Handled = true;
    }

    /// <summary>收起留 120ms 宽限：指针在窗口内移动时拖放目标切换也会来一次 DragLeave，
    /// 立即收起会让提示闪一下；紧接着的 DragOver 或 Drop 会取消这次收起。</summary>
    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        _dropHide?.Dispose();
        _dropHide = DispatcherTimer.RunOnce(
            () =>
            {
                _dropActive = false;
                ShowCentreSlot();
            },
            TimeSpan.FromMilliseconds(120));
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropHide?.Dispose();
        _dropHide = null;
        _dropActive = false;
        ShowCentreSlot();

        var paths = e.DataTransfer.TryGetFiles()
            ?.Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .ToList();

        if (paths is { Count: > 0 })
        {
            _ = AddPathsAsync(paths!);
        }

        e.Handled = true;
    }

    private async Task ClearListAsync()
    {
        // 扫描期也不收：这一趟扫描正在往 _jobs 与 _roots 上写。
        if (_running || _scanning)
        {
            Toast(UiText.ToastBusy, Levels.FailureTone);
            return;
        }

        _roots.Clear();
        _jobs.Clear();

        // 清空回到「未添加」：上一批的收尾摘要留着会让右栏写着「本批结束：成功 0 · 失败 0」。
        _lastBatchText = string.Empty;
        RebuildRows();
        RefreshChrome();
        RefreshProgress();
        RefreshActivity();
        Toast(UiText.ToastClearedList);
        await Task.CompletedTask;
    }

    // ══════════════════ 列宽：拖动、双击自适应、缩放与落盘 ══════════════════

    private void HookColumns()
    {
        _cols = ColumnLayout.Normalize(_config.TableColumnWidths, ColumnFloors());
        _colSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _colSave.Tick += (_, _) =>
        {
            _colSave.Stop();
            SaveColumns();
        };

        var thumbs = new[] { Split0, Split1, Split2 };
        for (var i = 0; i < thumbs.Length; i++)
        {
            var index = i;
            thumbs[i].DragDelta += (_, e) => DragColumn(index, e.Vector.X);
            thumbs[i].DragCompleted += (_, _) => QueueColumnSave();
            thumbs[i].DoubleTapped += (_, _) => FitColumnToContent(index);
        }

        // 隧道阶段收：把手压在文字上面，冒泡阶段会被它吞掉。
        HeadArea.AddHandler(PointerPressedEvent, OnHeadPressed, RoutingStrategies.Tunnel, true);
        MenuFitOne.Click += (_, _) => FitColumnToContent(_menuColumn);
        MenuFitAll.Click += (_, _) => FitAllColumns();
        MenuReset.Click += (_, _) =>
        {
            _cols = ColumnLayout.Fit(ColumnLayout.Default(), AvailableWidth(), ColumnFloors());
            ApplyColumns();
        };
        ListScroll.SizeChanged += (_, _) => RefitColumns();

        // 表头与行同源：开窗那一帧起两边都按 _cols 写宽。这一处不写盘。
        PaintColumns();
    }

    /// <summary>表头按下：右键时记下点的是哪一列，菜单第一项要用。</summary>
    private void OnHeadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HeadArea).Properties.IsRightButtonPressed)
        {
            return;
        }

        _menuColumn = ColumnLayout.Count - 1;
        var x = e.GetPosition(HeadCols).X;
        var right = 0.0;
        for (var i = 0; i < ColumnLayout.Count; i++)
        {
            right += _cols[i];
            if (x < right)
            {
                _menuColumn = i;
                break;
            }
        }
    }

    /// <summary>清单区的内容盒宽：视口宽与整盒宽都扣两侧行内边距，表头那侧量的已经是内容盒，不再扣。</summary>
    private double AvailableWidth()
    {
        var outer = ListScroll.Viewport.Width;
        if (outer <= 0)
        {
            outer = ListScroll.Bounds.Width;
        }

        if (outer > 0)
        {
            return Math.Max(0, outer - 2 * ColumnLayout.RowPad);
        }

        return HeadArea.Bounds.Width;
    }

    /// <summary>把 _cols 写到表头与每一行上，不写盘。启动同源与拖动都走这里。</summary>
    private void PaintColumns()
    {
        WriteColumns(HeadCols);
        foreach (var grid in _rowCols)
        {
            WriteColumns(grid);
        }

        PlaceSplitters();
    }

    /// <summary>画上去之后延后写盘：拖动暂停 700ms 才写一次配置。</summary>
    private void ApplyColumns()
    {
        PaintColumns();
        QueueColumnSave();
    }

    private void WriteColumns(Grid grid)
    {
        for (var i = 0; i < ColumnLayout.Count && i < grid.ColumnDefinitions.Count; i++)
        {
            grid.ColumnDefinitions[i].Width = new GridLength(_cols[i], GridUnitType.Pixel);
        }
    }

    /// <summary>把手压在列边界上，随宽度一起移动。</summary>
    private void PlaceSplitters()
    {
        var thumbs = new[] { Split0, Split1, Split2 };
        var half = Breakpoints.SplitHit / 2;
        var left = 0.0;
        for (var i = 0; i < thumbs.Length; i++)
        {
            left += _cols[i];
            Canvas.SetLeft(thumbs[i], left - half);
        }
    }

    private void DragColumn(int index, double delta)
    {
        _cols = ColumnLayout.Spread(_cols, index, delta, ColumnFloors());
        PaintColumns();
    }

    /// <summary>窗口或日志栏宽度变了：按比例摊回内容盒宽，每列不足自己的下限时优先补足。</summary>
    private void RefitColumns()
    {
        var width = AvailableWidth();
        if (width <= 0)
        {
            return;
        }

        var floors = ColumnFloors();
        var next = ColumnLayout.EnsureHeaders(ColumnLayout.Fit(_cols, width, floors), floors);
        if (next.SequenceEqual(_cols))
        {
            return;
        }

        _cols = next;
        ApplyColumns();
    }

    /// <summary>清单内容变了：滚动条出现只改视口宽、SizeChanged 收不到，排到布局之后再重算。</summary>
    private void RefitAfterListChange() => Dispatcher.UIThread.Post(RefitColumns, DispatcherPriority.Loaded);

    /// <summary>每列下限的唯一结论处：表头需要宽现量，量不到时由 <see cref="ColumnLayout.Floors"/> 的静态下限盖住。</summary>
    private double[] ColumnFloors() => ColumnLayout.Floors(HeaderNeed());

    /// <summary>每一列表头文字放得下至少要多宽，含左右留白。</summary>
    private double[] HeaderNeed()
    {
        var heads = new[] { HeadKind, HeadFile, HeadPassword, HeadStatus };
        var need = new double[ColumnLayout.Count];
        for (var i = 0; i < heads.Length; i++)
        {
            heads[i].Measure(Size.Infinity);
            need[i] = heads[i].DesiredSize.Width + 24;
        }

        return need;
    }

    /// <summary>这一列的内容放得下至少要多宽；只服务两个「适配」动作，列的下限另有 <see cref="ColumnFloors"/>。</summary>
    private double ContentNeed(int index)
    {
        var widest = 0.0;
        foreach (var row in _rows)
        {
            Control cell = index switch
            {
                0 => row.Kind,
                1 => row.Name,
                2 => row.Password,
                _ => row.StatusCell,
            };
            cell.Measure(Size.Infinity);
            var width = cell.DesiredSize.Width + (index == 3 ? 8 : 0);
            widest = Math.Max(widest, width);
        }

        return widest;
    }

    private void FitColumnToContent(int index)
    {
        if (index < 0 || index >= ColumnLayout.Count)
        {
            return;
        }

        var need = HeaderNeed();
        var floors = ColumnLayout.Floors(need);
        var want = Math.Max(ContentNeed(index), floors[index]);
        _cols = ColumnLayout.EnsureHeaders(
            ColumnLayout.Spread(_cols, index, want - _cols[index], floors), floors);
        ApplyColumns();
    }

    private void FitAllColumns()
    {
        var need = HeaderNeed();
        var floors = ColumnLayout.Floors(need);
        var want = new double[ColumnLayout.Count];
        for (var i = 0; i < ColumnLayout.Count; i++)
        {
            want[i] = Math.Max(ContentNeed(i), floors[i]);
        }

        _cols = ColumnLayout.EnsureHeaders(ColumnLayout.Fit(want, AvailableWidth(), floors), floors);
        ApplyColumns();
    }

    private void QueueColumnSave()
    {
        _colSave?.Stop();
        _colSave?.Start();
    }

    private void SaveColumns()
    {
        if (_cols.Length == _config.TableColumnWidths.Count && _cols.SequenceEqual(_config.TableColumnWidths))
        {
            return;
        }

        // 就地改字段，不换实例：宿主与设置页共用同一份配置，换实例会让页面手里那份没有列宽，下一次保存就把盘上的列宽写回旧值。
        _config.TableColumnWidths = _cols.ToArray();
        if (!_configStore.Save(_config))
        {
            // 上游是 HookColumns 里那个 700ms 去抖定时器，这一档失败没有第二处兜底（B-2026-152）。
            _log.Failure(UiText.SettingsSaveFailedAt, _configStore.LastError);
        }
    }

    // ══════════════════ 两档自适应 ══════════════════

    /// <summary>按内容区宽度切档并判工具条形，幂等。</summary>
    private void ApplyTier()
    {
        var width = ContainerWidth();
        if (width <= 0)
        {
            return;
        }

        var tier = Breakpoints.Of(width);
        var compact = Breakpoints.Compact(width);
        var tierChanged = tier != _tier;
        var chromeChanged = compact != _compact;
        _tier = tier;
        _compact = compact;

        ApplyBodyLayout(tier, width);
        ApplyTierChrome();
        ApplyFoldState();
        if (tierChanged)
        {
            ApplyColumns();
        }

        if (tierChanged || chromeChanged)
        {
            // 提示文字随工具条形换：收成图标之后，提示是这些按钮唯一的名字来源。
            RefreshChrome();
        }
    }

    /// <summary>内容区宽度。布局尚未生效时退回客户区尺寸，以免把 0 判成中档。</summary>
    private double ContainerWidth()
    {
        var width = Root.Bounds.Width;
        return width > 0 ? width : ClientSize.Width;
    }

    /// <summary>主体的一行一列关系：宽档两列一行，中档一列两行。</summary>
    private void ApplyBodyLayout(ShellTier tier, double width)
    {
        if (tier == ShellTier.Wide)
        {
            Body.ColumnDefinitions = new ColumnDefinitions($"*,{Breakpoints.SideWidth(width):0.##}");
            Body.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(TaskArea, 0);
            Grid.SetRow(TaskArea, 0);
            Grid.SetColumn(Side, 1);
            Grid.SetRow(Side, 0);
            return;
        }

        Body.ColumnDefinitions = new ColumnDefinitions("*");
        Body.RowDefinitions = new RowDefinitions("*,Auto");
        Grid.SetColumn(TaskArea, 0);
        Grid.SetRow(TaskArea, 0);
        Grid.SetColumn(Side, 0);
        Grid.SetRow(Side, 1);
    }

    /// <summary>日志面板在这一档下是否显示：宽档恒显，折叠那两档看展开态。</summary>
    private bool LogPanelShown => _tier == ShellTier.Wide || _foldOpen;

    /// <summary>折叠态：收起时跑任务那一格与宽档同形、其余时候只有一行摘要，展开给面板 286 高。</summary>
    private void ApplyFoldState()
    {
        var folded = _tier != ShellTier.Wide;
        var open = folded && _foldOpen;

        LogToggle.IsVisible = folded;
        LogHead.IsVisible = !folded || open;
        LogScroll.IsVisible = !folded || open;

        // 中档收起时把面板高度上限压到 0、展开时放到目标值，过渡挂在 Grid.logpanel 上；宽档不限高。
        LogPanel.MaxHeight = folded ? (open ? Breakpoints.FoldOpen : 0) : double.PositiveInfinity;

        Side.Height = open ? Breakpoints.FoldOpen : double.NaN;

        var caption = open ? UiText.LogCollapse : UiText.LogExpand;
        TxtLogToggle.Text = caption;
        ToolTip.SetTip(LogToggle, caption);
        AutomationProperties.SetName(LogToggle, caption);
        LogToggleGlyph.Data = Geometry.Parse(open ? Icons.ChevronDown : Icons.ChevronRight);

        RefreshActivity();
    }

    /// <summary>折叠开关只翻状态，布局由 <see cref="ApplyFoldState"/> 一处改。</summary>
    private void ToggleFold()
    {
        if (_tier == ShellTier.Wide)
        {
            return;
        }

        _foldOpen = !_foldOpen;
        ApplyFoldState();
    }

    private void ApplyTierChrome()
    {
        var iconsOnly = _compact;

        TxtSelectAll.IsVisible = !iconsOnly;
        TxtRemoveSelected.IsVisible = !iconsOnly;
        TxtClearList.IsVisible = !iconsOnly;
        TxtAdd.IsVisible = !iconsOnly;
        TxtAddDir.IsVisible = !iconsOnly;
        TxtSegAll.IsVisible = !iconsOnly;
        TxtSegFail.IsVisible = !iconsOnly;
        TxtOpenOut.IsVisible = !iconsOnly;

        // 输出摘要那一段在紧凑形只留「输出」两个字与箭头：两个取值让位给任务区。
        TxtOutMode.IsVisible = !iconsOnly;
        OutSumSep.IsVisible = !iconsOnly;
        OutConflictLabel.IsVisible = !iconsOnly;
        TxtOutConflict.IsVisible = !iconsOnly;
    }

    // ══════════════════════════ 清单行 ══════════════════════════

    private void RebuildRows()
    {
        RowHost.Children.Clear();
        _rows.Clear();
        _rowCols.Clear();
        for (var i = 0; i < _jobs.Count; i++)
        {
            AddRow(_jobs[i], i);
        }

        ApplyFilter();
        // 清单重建之后按当前的拖放态重算中央那两层：清单空且没在拖放时才显示空状态。
        ShowCentreSlot();
    }

    /// <summary>建一行。index 只决定要不要跑入场动画：只给前 <see cref="EnterRows"/> 行挂那个类。</summary>
    private void AddRow(Job job, int index)
    {
        var row = new JobRow { Job = job };

        // 尺寸与线宽取自 Path.lucide 样式：此处重复书写会让 16 网格的几何溢出，图标与文字的中心随之偏移。
        var icon = new ShapePath
        {
            Classes = { "ic", "lucide" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var kind = new TextBlock
        {
            Text = job.KindShort,
            Classes = { "faint" },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 列里只放 2 至 4 字的短标签，全称挂在这格的提示上；提示挂整格、不挂文字，挂在文字上时指向同格空白处不显示。
        var kindCell = new Border { Child = kind };
        ToolTip.SetTip(kindCell, job.Kind);

        var name = new TextBlock
        {
            Text = job.Indent ? UiText.ChildPrefix + job.DisplayName : job.DisplayName,
            Classes = { "dim" },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var password = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        password.PointerPressed += async (_, _) => await CopyPasswordAsync(row, password);
        ToolTip.SetTip(password, PasswordTip(job));

        var status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 行内四列同号：字号取窗口根那一档，层次靠颜色分。
        var childCount = new TextBlock
        {
            Classes = { "faint" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        // 状态列只留状态词，外加文件夹行末尾的子项数：灯条、耗时与层号已在右栏「当前活动」中呈现，此处重复既冗余，又迫使该列裁字。
        var statusCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { icon, status, childCount },
        };

        // 提示同样挂在整格上，内容由 RefreshRow 按状态给（跳过、部分、失败才解释原因）。
        var statusBox = new Border { Child = statusCell };
        row.StatusBox = statusBox;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnLayout.DefaultSpec) };
        WriteColumns(grid);
        grid.Children.Add(kindCell);
        Grid.SetColumn(kindCell, 0);
        grid.Children.Add(name);
        Grid.SetColumn(name, 1);
        grid.Children.Add(password);
        Grid.SetColumn(password, 2);
        grid.Children.Add(statusBox);
        Grid.SetColumn(statusBox, 3);

        var root = new Border { Classes = { "row" } };
        root.Child = grid;

        // 不会处理的行整行转次要色。
        root.Classes.Set("dim", !job.Runnable);

        row.Root = root;
        row.Cols = grid;
        row.Name = name;
        row.Kind = kind;
        row.Password = password;
        row.Icon = icon;
        row.Status = status;
        row.ChildCount = childCount;
        row.StatusCell = statusCell;

        root.PointerPressed += (_, _) => ToggleSelect(row);

        // 行入场只给前 12 行，超出的行不建动画对象。
        if (index >= 0 && index < EnterRows)
        {
            root.Classes.Add("enter");
        }

        _rows.Add(row);
        _rowCols.Add(grid);
        RowHost.Children.Add(root);
        RefreshRow(job);
    }

    /// <summary>点一行就是切换它的选中状态。多选是为了工具条那个「移除选中」。</summary>
    private void ToggleSelect(JobRow row)
    {
        row.Root.Classes.Set("sel", !row.Root.Classes.Contains("sel"));
        RefreshChrome();
    }

    /// <summary>全选与取消全选是同一个按钮的两个态：已经全选时再点一次就是取消。</summary>
    private void ToggleSelectAll()
    {
        var deselect = AllSelected();
        foreach (var row in _rows)
        {
            row.Root.Classes.Set("sel", !deselect);
        }

        RefreshChrome();
    }

    /// <summary>清单是否已经全选。按钮文字与这一趟点下去的结果取同一个判据。</summary>
    private bool AllSelected() =>
        _rows.Count > 0 && _rows.All(row => row.Root.Classes.Contains("sel"));

    /// <summary>把选中的项从清单里去掉。加入项跟着收缩；不收缩的话，被移掉的项会在下一次重扫时重新出现。</summary>
    private void RemoveSelectedRows()
    {
        // 与清空同理：扫描正在写 _jobs 与 _roots。
        if (_running || _scanning)
        {
            Toast(UiText.ToastBusy, Levels.FailureTone);
            return;
        }

        var selected = _rows
            .Where(row => row.Root.Classes.Contains("sel"))
            .Select(row => row.Job)
            .ToHashSet();
        if (selected.Count == 0)
        {
            return;
        }

        _jobs.RemoveAll(selected.Contains);
        _roots.Clear();
        _roots.AddRange(_jobs.Where(item => item.ParentIndex is null).Select(item => item.ArchivePath));

        RebuildRows();
        RefreshChrome();
        RefreshProgress();
    }

    private int SelectedCount() => _rows.Count(row => row.Root.Classes.Contains("sel"));

    /// <summary>密码 chip 的提示：有来源就报来源——用户看得到密码，却不知道它是猜出来的还是密码本命中的。</summary>
    private static string PasswordTip(Job job) => job.PasswordSource.Length > 0
        ? string.Format(CultureInfo.CurrentCulture, UiText.PasswordSourceAt, job.PasswordSource)
        : UiText.CopyPasswordTip;

    private async Task CopyPasswordAsync(JobRow row, TextBlock chip)
    {
        var job = row.Job;
        if (job.Password.Length == 0)
        {
            return;
        }

        var clipboard = Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(job.Password);
        _log.Note(UiText.PasswordCopiedAt, job.Password);

        // chip 短暂换成「已复制」，一秒之后回到密码本身。
        chip.Text = UiText.Copied;
        DispatcherTimer.RunOnce(() => chip.Text = PasswordText(job), TimeSpan.FromSeconds(1));
    }

    /// <summary>就地刷新一行，不重建整表：重建会丢选中与滚动位置，而运行中每 100ms 都在刷。</summary>
    private void RefreshRow(Job job)
    {
        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Job, job));
        if (row is null)
        {
            return;
        }

        // 整批暂停而这一行还在跑：转圈要摘掉 —— 转圈读作「正在干活」，与整批停着相反。
        // 状态词在这一档之上换成「已暂停」：状态机里没有暂停这个态，暂停只是整批的属性。
        var frozen = _paused && job.State is JobState.Running;

        row.Icon.Data = Geometry.Parse(frozen ? Icons.Pause : StateIcon(job.State));

        // 文件夹汇总行不挂状态图标：它不参与跑批，旁边配一个「排队中」的时钟会读成"马上轮到它"。
        row.Icon.IsVisible = !job.IsDir;
        row.Icon.Classes.Set("ic-ok", job.State is JobState.Done);
        row.Icon.Classes.Set("ic-err", job.State is JobState.Failed);
        row.Icon.Classes.Set("ic-warn", job.State is JobState.Skipped or JobState.Partial);
        row.Icon.Classes.Set("ic-run", job.State is JobState.Running);
        row.Icon.Classes.Set("ic-wait", job.State is JobState.Queued);

        row.Icon.Classes.Set("spin", job.State is JobState.Running && !frozen);

        // 不可运行的行不写状态机的六个词；文件夹汇总行连状态词都不写，只留子项数。
        row.Status.Text = Runner.StatusLabel(job);
        if (frozen)
        {
            row.Status.Text = UiText.RowPaused;
        }

        // 级别类名按枚举换算，不写死：级别加一档或换名时，这里不会漏掉一条。
        foreach (var value in Enum.GetValues<LogLevel>())
        {
            row.Status.Classes.Set(Levels.Class(value), false);
        }

        row.Status.Classes.Set("lvl-accent", false);
        row.Status.Classes.Set(job.Runnable ? Levels.StateClass(job.State) : Levels.MutedClass, true);

        // 文件夹汇总行在行尾报子项数。
        var showChildren = job.IsDir && job.ChildCount > 0;
        row.ChildCount.IsVisible = showChildren;
        row.ChildCount.Text = showChildren
            ? string.Format(CultureInfo.CurrentCulture, UiText.ChildCountAt, job.ChildCount)
            : string.Empty;

        row.Password.Text = PasswordText(job);
        row.Password.Classes.Set("lvl-accent", job.Password.Length > 0);
        row.Password.Classes.Set(Levels.MutedClass, job.Password.Length == 0);

        // 原因与说明只挂在状态格上，而且只在跳过、部分、失败这三种状态下给。
        var reason = job.Reason ?? string.Empty;
        var detail = job.ReasonDetail ?? string.Empty;
        var explained = job.State is JobState.Skipped or JobState.Partial or JobState.Failed;
        ToolTip.SetTip(row.StatusBox, explained && (reason.Length > 0 || detail.Length > 0)
            ? string.IsNullOrEmpty(detail)
                ? reason
                : string.Format(CultureInfo.CurrentCulture, UiText.StatusWithSecondsAt, reason, detail)
            : null);
    }

    private static void SyncTicks(StackPanel host, int layer, int max)
    {
        var count = Math.Clamp(max, 1, MaxTickCount);
        while (host.Children.Count > count)
        {
            host.Children.RemoveAt(host.Children.Count - 1);
        }

        while (host.Children.Count < count)
        {
            host.Children.Add(new Border { Classes = { "tick" } });
        }

        for (var index = 0; index < count; index++)
        {
            if (host.Children[index] is not Border tick)
            {
                continue;
            }

            tick.Classes.Set("on", index + 1 < layer);
            tick.Classes.Set("cur", index + 1 == layer);
        }
    }

    /// <summary>层号只认包层与上限，不认动作序号。上限为零时只写当前层。</summary>
    private static string LayerText(Job job)
    {
        var layer = Math.Max(job.Layer, 1);
        return job.MaxLayer > 0
            ? string.Format(CultureInfo.CurrentCulture, UiText.LayerWithLimitAt, layer, job.MaxLayer)
            : string.Format(CultureInfo.CurrentCulture, UiText.LayerAt, layer);
    }

    /// <summary>密码格三态：有密码写密码，空密码取占位文案，没用上才留空——空密码画成「没有密码」会让用户以为没解过。</summary>
    private static string PasswordText(Job job)
    {
        if (job.Password.Length > 0)
        {
            return job.Password;
        }

        return job.PasswordSource.Length > 0 && job.PasswordSource != PierceText.OriginNoPassword
            ? UiText.PasswordEmpty
            : UiText.PasswordNone;
    }

    /// <summary>每 100ms 一次的原地刷新：进度计数与活动槽那一行的耗时。行内耗时已移出清单。</summary>
    private void RefreshTick()
    {
        if (!_running)
        {
            _tick.Stop();
            return;
        }

        ProgCount.Text = string.Format(
            CultureInfo.CurrentCulture,
            UiText.ProgressCountAt,
            Runner.SettledCount(_jobs),
            Runner.RunnableCount(_jobs));

        if (_progress is not null)
        {
            ActDoing.Text = ActivityProgressText();
        }
    }

    /// <summary>只算真正在跑的时间：暂停期间把表停掉，继续时重开一段。</summary>
    private double ElapsedSeconds()
    {
        var paused = _pausedTotal;
        if (_pausedAt is { } at)
        {
            paused += DateTime.UtcNow - at;
        }

        return Math.Max(0, _batchClock.Elapsed.TotalSeconds - paused.TotalSeconds);
    }

    /// <summary>时长格式照 1.1.0：不足一分钟写秒并留一位小数，超过一分钟写分加两位秒。</summary>
    private static string FormatSeconds(double seconds)
    {
        if (seconds <= 0)
        {
            return string.Empty;
        }

        return seconds < 60
            ? string.Format(CultureInfo.InvariantCulture, UiText.SecondsAt, seconds)
            : string.Format(
                CultureInfo.InvariantCulture,
                UiText.MinutesAt,
                (int)(seconds / 60),
                (int)(seconds % 60));
    }

    private void SetFilter(bool failedOnly)
    {
        _failedOnly = failedOnly;
        SegAll.IsChecked = !failedOnly;
        SegFail.IsChecked = failedOnly;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var row in _rows)
        {
            row.Root.IsVisible = !_failedOnly
                || row.Job.State is JobState.Failed or JobState.Partial;
        }

        // 可见行数变了，滚动条随之出现或消失，内容盒的宽跟着变。
        RefitAfterListChange();
    }

    private void ApplyLogFilter()
    {
        foreach (var child in LogHost.Children)
        {
            if (child is Grid { Tag: LogLevel level } row)
            {
                row.IsVisible = LogRowVisible(level);
            }
        }
    }

    private bool LogRowVisible(LogLevel level) =>
        BtnErrOnly.IsChecked != true || Levels.ShownWhenErrorsOnly(level);

    // ══════════════════════════ 跑批 ══════════════════════════

    /// <summary>开跑的唯一起点。它跑一批；这一批跑完清单里还有排队的项就接着开下一批（见 <see cref="ContinuesQueue"/>）。</summary>
    private async Task RunAsync()
    {
        if (_running)
        {
            return;
        }

        // 扫描期点「开始」是排队，不是直接起跑：扫描还在往 _jobs 里填，此刻开跑会让 Runner 手里的清单被顶掉。
        if (_scanning)
        {
            _queued = true;
            Toast(UiText.ToastQueuedUntilScanDone);
            return;
        }

        // 配置不完整时在动手之前拦下，判据见 ConfigChecks.BlockingReason（B-2026-024）。
        if (ConfigChecks.BlockingReason(_config) is { } blocked)
        {
            _log.Failure(blocked);
            Toast(blocked, Levels.FailureTone);
            return;
        }

        do
        {
            await RunBatchAsync();
        }
        while (ContinuesQueue());
    }

    /// <summary>这一批跑完还接不接着开下一批：清单里还有排队的项、且不是点过「停止」停下的（接着开等于停不下来）。</summary>
    private bool ContinuesQueue()
    {
        if (_stop || !Runner.HasQueued(_jobs))
        {
            return false;
        }

        var waiting = _jobs.Count(item => item.Runnable && item.State is JobState.Queued);
        _log.Note(UiText.QueueContinuesAt, waiting);
        return true;
    }

    private async Task RunBatchAsync()
    {
        var batch = Actions.Batch(_jobs);
        if (batch.Count == 0)
        {
            _log.Note(ScanText.NothingToDo);
            return;
        }

        foreach (var item in batch)
        {
            item.Reset();
        }

        _batch = batch;
        _running = true;
        _paused = false;
        _stop = false;
        _pausedTotal = TimeSpan.Zero;
        _pausedAt = null;
        _batchClock.Restart();
        ClearProgress();
        RefreshChrome();
        RefreshProgress();
        _tick.Start();

        _runCts = new CancellationTokenSource();
        var hooks = new RunnerHooks
        {
            Log = _log,
            OnItem = job => Dispatcher.UIThread.Post(() =>
            {
                RefreshRow(job);
                RefreshProgress();
            }),
            AskPassword = AskPasswordAsync,
            AskFuseContinue = AskScanFuseContinue,
        };

        // 配置在开跑这一刻现取：设置页刚保存的新名单要立刻生效，不许用启动时那份。
        var runner = new Runner(_jobs, _engine, _vault, _config, hooks, () => _stop, () => _paused, _runCts.Token);
        try
        {
            await Task.Run(() => runner.RunAsync());
        }
        catch (Exception ex)
        {
            // 这一行是两句文案表正文拼成、夹着异常原文的整行，查不到级别 ⇒ 档由 Levels 那一处给。
            OnLine(new LogRecord(Levels.BatchFatalLevel, ScanText.ItemFailed(ex.Message)));
        }
        finally
        {
            _running = false;
            _paused = false;
            _batchClock.Stop();
            _runCts?.Dispose();
            _runCts = null;
            _tick.Stop();
            ClearProgress();
            // 这一批不会再问密码了：还停在验证中的那一层跟着收掉，免得它挡着下一批。
            ReleasePasswordLayer();
            RefreshChrome();
            RefreshProgress();
            RefreshAllRows();

            // 收尾摘要留给"上一批结果"那一格：空闲时它显示这个，而不是空白。数的是**这一批**：清单里可能还排着没轮到的项，按整份清单数会把它们一起算进来。
            var summary = Runner.Summarize(_batch);
            _lastBatchText = string.Format(
                CultureInfo.CurrentCulture,
                UiText.BatchFinishedAt,
                Count(summary, JobState.Done),
                Count(summary, JobState.Failed),
                Runner.SkippedCount(_batch),
                Runner.StoppedCount(_batch));
            RefreshActivity();
        }
    }

    /// <summary>询问通道。只有密码层说「值得问」时才走到这里；它跑在后台线程上，弹窗必须回到界面线程。</summary>
    private async Task<string?> AskPasswordAsync(PasswordAsk ask, CancellationToken cancellationToken)
    {
        return await AskPassword.AskAsync(PwHost, ask, cancellationToken);
    }

    /// <summary>
    /// 收回停在验证中的密码层。验证的结论只回给穿透层，界面这一侧看不到，所以按两处收：这一层已经在解压了（密码验过了）、这一批收尾了（问满次数或整批停下，都不会再有下一次询问）。
    /// </summary>
    private void ReleasePasswordLayer()
    {
        if (AskPassword.IsVerifying)
        {
            AskPassword.Release();
        }
    }

    /// <summary>撞上保险丝时问一句要不要继续。它跑在后台线程上，等界面线程上的回答，照 AskPasswordAsync 的先例。</summary>
    /// <remarks>否定那一个按钮要写明它实际做的事：它不停这一趟，只不再往深处找，已找到的照解。</remarks>
    private bool AskScanFuseContinue(int directories, int files)
    {
        var body = string.Format(CultureInfo.CurrentCulture, UiText.ScanFuseAskBodyAt, directories, files);
        var pending = Dispatcher.UIThread.InvokeAsync(
            () => Ask.AskAsync(
                UiText.ScanFuseAskTitle, body, UiText.ScanFuseAskContinue, UiText.ScanFuseAskCancel,
                tone: AskTone.Choice));

        return pending.GetAwaiter().GetResult();
    }

    private void TogglePause()
    {
        if (!_running)
        {
            return;
        }

        // 这两个回执只在批级出：一次按下有十几个引擎实例，逐实例那两条走排错档。
        _paused = !_paused;
        if (_paused)
        {
            _pausedAt = DateTime.UtcNow;
            _log.Note(LogText.BatchPaused);
        }
        else if (_pausedAt is { } at)
        {
            _pausedTotal += DateTime.UtcNow - at;
            _progressAt += DateTime.UtcNow - at;
            _pausedAt = null;
            _log.Note(LogText.BatchResumed);
        }

        RefreshChrome();

        // 暂停沿与恢复沿都要重画清单：那一行的图标与文案跟着整批状态走，只刷底栏就留着一行假的「解压中」。
        RefreshAllRows();
    }

    private void StopRun()
    {
        if (_running)
        {
            _stop = true;
            _paused = false;
            _pausedAt = null;
            _runCts?.Cancel();
            RefreshChrome();
            return;
        }

        if (_scanning)
        {
            _cts?.Cancel();
            RefreshChrome();
        }
    }

    private void RefreshAllRows()
    {
        foreach (var row in _rows)
        {
            RefreshRow(row.Job);
        }
    }

    // ══════════════════════════ 日志与进度 ══════════════════════════

    /// <summary>去处由记录本身推：带结构的进活动槽，其余按级别进面板；结果行进面板时进度位清空（它描述的那件事已有结论）。</summary>
    private void OnLine(LogRecord record)
    {
        if (record.Progress is not null)
        {
            ShowProgress(record);
            return;
        }

        if (record.Level < Levels.PanelMinimum)
        {
            return;
        }

        ClearProgress();
        AppendLog(record);
    }

    private void AppendLog(LogRecord record)
    {
        // 时间戳独立成列、右对齐；正文折行，长行不再被横向裁掉。
        // 58 是**时间戳列的固定宽**：`HH:mm:ss` 在 `FsMono`（12）下量出来正好 58 像素，取固定宽而不是 Auto，
        // 是为了正文列的起点不随每行时刻的字符宽抖动。
        // ⚠ `lg-ts` 自带 8 像素右外边距，实测那一格 DesiredSize 66 像素 ⇒ 文本与正文列相压；按作者裁决 8 不动列结构，差值登记备查。
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("58,*") };
        var stamp = new TextBlock
        {
            Classes = { "lg-ts" },
            // 时刻取记录自带的那一个：面板与日志文件里那一行因此是同一个时刻，不各取一次。
            Text = record.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        };
        var body = new TextBlock
        {
            Classes = { "logline", Levels.Class(record.Level) },
            Text = RunLog.Compose(record),
        };
        row.Children.Add(stamp);
        Grid.SetColumn(stamp, 0);
        row.Children.Add(body);
        Grid.SetColumn(body, 1);

        // 级别挂在行上，筛选按它判：新来的行也要立刻服从当前的筛选。
        row.Tag = record.Level;
        row.IsVisible = LogRowVisible(record.Level);

        LogHost.Children.Add(row);
        while (LogHost.Children.Count > MaxLogLines)
        {
            LogHost.Children.RemoveAt(0);
        }

        RefreshActivity();

        if (ChkFollow.IsChecked == true)
        {
            // 布局未跑就直接滚动，落点用的是加行之前的高度，末行会被漏掉（docx 11）。
            UpdateLayout();
            LogScroll.ScrollToEnd();
        }
    }

    private void ClearLog()
    {
        LogHost.Children.Clear();
        Toast(UiText.ToastClearedLog);
    }

    /// <summary>
    /// 活动槽收的是带结构的进度（扫描计数、解压百分比、密码枚举）。
    /// 「不带结构的记录进这一格」由 <see cref="OnLine"/> 一处挡掉（它按 <c>Progress</c> 是否为空分流）⇒ 这里不再挡第二道。
    /// </summary>
    private void ShowProgress(LogRecord record)
    {
        _progress = record.Progress;
        _progressAt = DateTime.UtcNow;
        SetLevelClass(ActDoing, record.Level);
        RefreshActivity();

        // 进度已经走到解压那一档，说明这一趟的密码交出去了：停在验证中的那一层该收了。
        if (record.Progress!.Value.Stage is not ProgressStage.Search)
        {
            ReleasePasswordLayer();
        }
    }

    private void ClearProgress()
    {
        _progress = null;
        _progressAt = DateTime.UtcNow;
    }

    private static void SetLevelClass(Control control, LogLevel level)
    {
        foreach (var value in Enum.GetValues<LogLevel>())
        {
            control.Classes.Set(Levels.Class(value), false);
        }

        control.Classes.Set(Levels.Class(level), true);
    }

    /// <summary>运行信息那一格：运行中写当前任务名与层级刻度，空闲时写上一批的收尾摘要。</summary>
    private void RefreshActivity()
    {
        // 标题恒为「当前活动」：该标题描述这一格的用途，不随格内当前显示哪一批而变（docx 21）。
        ActTitle.Text = UiText.ActivityTitle;

        var current = _rows.FirstOrDefault(row => row.Job.State is JobState.Running);
        if (_running && current is not null)
        {
            var job = current.Job;
            ActName.Text = job.DisplayName;
            ActName.IsVisible = true;
            ActLive.IsVisible = true;
            ActDoing.IsVisible = true;
            ActSummary.IsVisible = false;

            var hasTicks = job.MaxLayer > 0;
            ActTicks.IsVisible = hasTicks;
            if (hasTicks)
            {
                SyncTicks(ActTicks, job.Layer, job.MaxLayer);
            }

            ActLayer.Text = LayerText(job);
            ActDoing.Text = ActivityProgressText();

            // 折叠收起时那一行摘要填的是包名，但运行中它不让位（可见性归一在 ApplyFoldSummaryOnly）。
            FoldSummary.Text = job.DisplayName;
            ApplyFoldSummaryOnly(running: true);
            return;
        }

        if (_scanning)
        {
            // 扫描期也占这一格：不新增主状态，只把上一批摘要让位给扫描进度。
            ActName.IsVisible = false;
            ActLive.IsVisible = true;
            ActDoing.IsVisible = true;
            ActSummary.IsVisible = false;
            ActTicks.IsVisible = false;
            ActLayer.Text = string.Empty;

            var scanning = ActivityProgressText();
            ActDoing.Text = scanning;
            FoldSummary.Text = scanning;
            ApplyFoldSummaryOnly(running: true);
            return;
        }

        ActName.IsVisible = false;
        ActLive.IsVisible = false;
        ActDoing.IsVisible = false;
        ActSummary.IsVisible = true;

        if (_jobs.Count == 0)
        {
            ActSummary.Text = UiText.ActivityIdle;
            FoldSummary.Text = UiText.ActivityIdle;
            ApplyFoldSummaryOnly(running: false);
            return;
        }

        ActSummary.Text = _lastBatchText.Length > 0 ? _lastBatchText : UiText.PreviousBatchNone;
        FoldSummary.Text = ActSummary.Text;
        ApplyFoldSummaryOnly(running: false);
    }

    private string ActivityProgressText()
    {
        if (_progress is not { } progress)
        {
            return UiText.ActivityStarting;
        }

        // 秒数以 core 给的为准，两次进度之间按本地时钟把它接着走；暂停时冻结在暂停那一刻。
        var now = _pausedAt ?? DateTime.UtcNow;
        var seconds = progress.Seconds + Math.Max(0, (now - _progressAt).TotalSeconds);

        if (progress.Stage is ProgressStage.Scan)
        {
            // 扫描是枚举、没有总数：两个数是已扫目录数与已扫文件数，不给百分比。
            return string.Format(
                CultureInfo.CurrentCulture, UiText.ActivityScanningAt, progress.Tried, progress.Total);
        }

        if (progress.Stage is ProgressStage.Extract && progress.Total > 0)
        {
            // 真实解压进度这一行只留百分比：层号与耗时由活动卡上方那一行承担，不在这一行重复。
            return string.Format(CultureInfo.CurrentCulture, UiText.ActivityExtractAt, progress.Tried);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            UiText.ActivityProgressAt,
            string.Format(CultureInfo.CurrentCulture, UiText.ActivityPasswordAt, progress.Tried, progress.Total),
            string.Format(CultureInfo.CurrentCulture, UiText.ActivityElapsedAt, ActivitySeconds(seconds)));
    }

    private static string ActivitySeconds(double seconds) => string.Format(
        CultureInfo.CurrentCulture,
        seconds < 60 ? UiText.SecondsShortAt : UiText.SecondsLongAt,
        seconds);

    /// <summary>
    /// 折叠收起时活动条的形态：跑任务时与宽档同形（包名、层级灯与层号、进度行都在），只有空闲与上一批才收成一行摘要。摘要本身在别的分支里填过，这里只决定谁可见。
    /// </summary>
    private void ApplyFoldSummaryOnly(bool running)
    {
        var only = _tier != ShellTier.Wide && !_foldOpen;
        FoldSummary.IsVisible = only && !running;
        if (!only || running)
        {
            return;
        }

        ActName.IsVisible = false;
        ActLive.IsVisible = false;
        ActDoing.IsVisible = false;
        ActSummary.IsVisible = false;
    }

    // ══════════════════════════ 界面状态 ══════════════════════════

    private void RefreshChrome()
    {
        var canStart = Actions.CanStart(_running, _jobs);
        var canResume = Actions.CanResume(_running, _paused);
        var canPause = Actions.CanPause(_running, _paused);
        // 扫描期与运行期一样不许清空与移除：扫描正在写 _jobs 与 _roots。
        var canClear = Actions.CanClearList(_running, _jobs) && !_scanning;
        var canRemove = Actions.CanRemoveSelected(_running, SelectedCount()) && !_scanning;
        var canOpenOut = Actions.CanOpenOutput(_jobs);

        BtnStart.IsEnabled = canStart || canResume;
        BtnPause.IsEnabled = canPause;
        BtnStop.IsEnabled = _running || _scanning;
        // 四个添加按钮一直可点：运行中收进来的项排到清单里，等这一批跑完接着处理（与拖入同一个后果）；它们没有禁用原因，提示只按紧凑形补功能名。
        BtnAdd.IsEnabled = true;
        BtnAddDir.IsEnabled = true;
        BtnAdd2.IsEnabled = true;
        BtnAddDir2.IsEnabled = true;
        BtnSelectAll.IsEnabled = _jobs.Count > 0;
        BtnRemoveSelected.IsEnabled = canRemove;
        BtnClearList.IsEnabled = canClear;
        BtnOpenOut.IsEnabled = canOpenOut;
        BtnOutSum.IsEnabled = !_running;

        // 禁用时把原因挂到提示上。只把按钮变灰而不给原因时，用户无从判断；紧凑形下这些按钮收成了图标，可用时提示要补上功能名，否则图标没有名字。
        // 最左那个承担「开始 / 继续」，第二个恒为「暂停」，两者互斥（docx 26）；换的只是按钮里那段文字（B-2026-113）。
        TxtStart.Text = canResume ? UiText.Resume : UiText.Start;
        AutomationProperties.SetName(BtnStart, TxtStart.Text);
        TxtPause.Text = UiText.Pause;
        AutomationProperties.SetName(BtnPause, UiText.Pause);

        DisabledReason(BtnStart, canStart || canResume, StartDisabledReason(), TxtStart.Text);
        DisabledReason(BtnPause, canPause, _paused ? UiText.DisabledPaused : UiText.DisabledNotRunning, UiText.Pause);
        DisabledReason(BtnStop, _running || _scanning, UiText.DisabledNotRunning, UiText.Stop);
        ToolTip.SetTip(BtnAdd, _compact ? UiText.AddFiles : null);
        ToolTip.SetTip(BtnAddDir, _compact ? UiText.AddFolder : null);
        ToolTip.SetTip(BtnAdd2, _compact ? UiText.AddFiles : null);
        ToolTip.SetTip(BtnAddDir2, _compact ? UiText.AddFolder : null);
        // 全选那个按钮的文字随选中状态换：已经全选时它写「取消全选」，再点一次就是取消。
        var selectCaption = AllSelected() ? UiText.DeselectAll : UiText.SelectAll;
        TxtSelectAll.Text = selectCaption;
        AutomationProperties.SetName(BtnSelectAll, selectCaption);

        DisabledReason(BtnSelectAll, _jobs.Count > 0, ScanText.NothingToDo, selectCaption);
        DisabledReason(BtnClearList, canClear, _running || _scanning ? UiText.ToastBusy : ScanText.NothingToDo, UiText.ClearList);
        DisabledReason(BtnRemoveSelected, canRemove, _running || _scanning ? UiText.ToastBusy : UiText.NothingSelected, UiText.RemoveSelected);
        DisabledReason(BtnOpenOut, canOpenOut, UiText.ToastNoOutputDir, UiText.OpenOutputDir);
        DisabledReason(BtnOutSum, !_running, UiText.ToastBusy, UiText.OutputLabel);

        // 暂停与继续换的是按钮里那一段文字，不是整个内容——整块换掉会把图标一起顶掉（B-2026-113）。
        // 工具条的计数与顶栏那个「x / y 项」取同一个汇总函数：可运行项从这里数，不会处理的行另计一句。
        var runnable = Runner.RunnableCount(_jobs);
        var idle = _jobs.Count - runnable;
        TxtCount.Text = idle > 0
            ? string.Format(CultureInfo.CurrentCulture, UiText.TaskCountMixedAt, runnable, idle)
            : string.Format(CultureInfo.CurrentCulture, UiText.TaskCountAt, runnable);

        // 徽标按「跑过没跑过」：刚加入、一次没跑就写「已结束」会与同屏的「还没有跑过任务」自相矛盾。
        LogStatus.Text = _running || _scanning
            ? UiText.LogBadgeRunning
            : _lastBatchText.Length > 0 ? UiText.LogBadgeDone : UiText.LogBadgeIdle;

        ShowCentreSlot();
    }

    /// <summary>
    /// 「开始解压」不可用时提示上写的原因，按场景分档：清单还空着时说「先加入压缩包或文件夹」，已经跑过一批时才说那一句讲重跑的（对着空列表讲重跑，说的不是用户眼前这件事）。
    /// </summary>
    private string StartDisabledReason() =>
        _jobs.Count == 0 ? UiText.DisabledNothingAdded : ScanText.NothingToDo;

    /// <summary>禁用时把原因写在提示上；紧凑形按钮只留图标，可用时把功能名补进提示里。</summary>
    private void DisabledReason(Control control, bool usable, string reason, string name) =>
        ToolTip.SetTip(control, usable ? (_compact ? name : null) : reason);

    /// <summary>进度与图例数哪一份清单：跑批时是这一批（分母在开跑那一刻定死，跑到一半新加的项不算进来），空闲时是整份清单。</summary>
    private IReadOnlyList<Job> ProgressItems() => _running && _batch.Count > 0 ? _batch : _jobs;

    /// <summary>计数与构成条都取 <see cref="Runner.Summarize"/>，与状态列、汇总同源；分母是这一批。</summary>
    private void RefreshProgress()
    {
        var summary = Runner.Summarize(ProgressItems());
        var total = summary.Values.Sum();
        if (total == 0)
        {
            // 有项但一项都跑不动时不许写「等待加入文件」：文件早就加进来了，是它们不用解。
            ProgCount.Text = _jobs.Count > 0 ? UiText.ProgressNoRunnable : UiText.ProgressWaiting;
            SetLegend(0, 0, 0, 0, 0, 0, 0);
            SetSegments(0, 0, 0, 0, 0, 0);
            return;
        }

        var queued = Count(summary, JobState.Queued);
        var running = Count(summary, JobState.Running);
        var done = Count(summary, JobState.Done);
        var partial = Count(summary, JobState.Partial);
        var skipped = Count(summary, JobState.Skipped);
        var stopped = Runner.StoppedCount(ProgressItems());
        var failed = Count(summary, JobState.Failed);

        ProgCount.Text = string.Format(
            CultureInfo.CurrentCulture, UiText.ProgressCountAt, done + partial + skipped + failed, total);
        SetLegend(queued, running, done, partial, skipped - stopped, stopped, failed);
        SetSegments(queued, running, done, partial, skipped, failed);
    }

    private static int Count(IReadOnlyDictionary<JobState, int> summary, JobState state) =>
        summary.TryGetValue(state, out var value) ? value : 0;

    /// <summary>构成条的提示给归并后的口径：待处理与进行中并成「未完成」，计数为零的档位不出现。</summary>
    /// <remarks>「跳过」那一格按起因分开——被用户停下的另记一格，与状态列的词一个口径。</remarks>
    private void SetLegend(int queued, int running, int done, int partial, int skipped, int stopped, int failed)
    {
        var bits = new List<string>();
        AddLegend(bits, UiText.LegendUnfinishedAt, queued + running);
        AddLegend(bits, UiText.LegendDoneAt, done);
        AddLegend(bits, UiText.LegendPartialAt, partial);
        AddLegend(bits, UiText.LegendSkippedAt, skipped);
        AddLegend(bits, UiText.LegendStoppedAt, stopped);
        AddLegend(bits, UiText.LegendFailedAt, failed);

        ToolTip.SetTip(ProgTrack, bits.Count > 0 ? string.Join('\n', bits) : null);
    }

    private static void AddLegend(List<string> bits, string template, int count)
    {
        if (count > 0)
        {
            bits.Add(string.Format(CultureInfo.CurrentCulture, template, count));
        }
    }

    /// <summary>构成条按状态分六段，段宽按计数占比；顺序是成功、部分、跳过、失败、进行中、待处理。</summary>
    private void SetSegments(int queued, int running, int done, int partial, int skipped, int failed)
    {
        var counts = new[] { done, partial, skipped, failed, running, queued };
        var total = counts.Sum();

        // 一条都没有时整段收起：零宽不足以消除残影，Border 仍会画出像素。
        ProgSegs.IsVisible = total > 0;
        for (var index = 0; index < counts.Length; index++)
        {
            // 一条都没有时用绝对零宽：全是 0* 的话 Grid 按等分铺满，清空列表后留下六段残色。
            ProgSegs.ColumnDefinitions[index].Width = total > 0
                ? new GridLength(counts[index], GridUnitType.Star)
                : new GridLength(0);
        }
    }

    private void RefreshOutSummary()
    {
        TxtOutMode.Text = _config.OutputMode is OutputMode.Custom ? UiText.OutputCustomDir : UiText.OutputSameDir;
        TxtOutConflict.Text = _config.Conflict switch
        {
            ConflictPolicy.Overwrite => UiText.ConflictOverwrite,
            ConflictPolicy.Skip => UiText.ConflictSkip,
            _ => UiText.ConflictRenumber,
        };
    }

    private void OpenOutput()
    {
        var directories = Runner.OutputDirs(_jobs);
        if (directories.Count == 0)
        {
            Toast(UiText.ToastNoOutputDir, Levels.FailureTone);
            return;
        }

        var target = Runner.OutputRootOf(directories);
        if (target.Length == 0)
        {
            target = directories[0];
        }

        OpenTarget(target, UiText.ToastOpenOutputFailedAt);
    }

    /// <summary>用系统默认程序打开一个目录或一个链接。三处入口（输出目录、数据目录、仓库页）共用这一份实现。</summary>
    private void OpenTarget(string path, string failTemplate)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, failTemplate, ex.Message), ToastTone.Bad);
        }
    }

    /// <summary>输出设置的开合。它比浮窗低一层，浮窗打开时位于浮窗之下。</summary>
    private void ShowOutPanel()
    {
        OutSame.IsChecked = _config.OutputMode is not OutputMode.Custom;
        OutCustom.IsChecked = _config.OutputMode is OutputMode.Custom;
        OutDirBox.Text = _config.OutputDirectory;
        OutConflict.SelectedIndex = _config.Conflict switch
        {
            ConflictPolicy.Overwrite => 1,
            ConflictPolicy.Skip => 2,
            _ => 0,
        };
        Layer.Show(OutPanel, BtnOutSum);
    }

    /// <summary>收掉一层浮层：摘 open 等淡出跑完再隐藏，所有浮层共用这一条路。</summary>
    private void HideLayer(Panel host) => _ = Layer.HideAsync(host);

    /// <summary>浮窗的开关。两个浮窗共用一个宿主；宽度随页面换。</summary>
    private void TogglePage(PageKind kind, Control trigger)
    {
        if (_page == kind)
        {
            HidePage();
            return;
        }

        _page = kind;
        LibPage.IsVisible = kind is PageKind.Library;
        SetPage.IsVisible = kind is PageKind.Settings;
        if (kind is PageKind.Library)
        {
            // 打开时按内存重建视图：跑批把命中次数写进了内存，不重建看到的是上一次打开时的快照。
            // 按盘重读是「重新载入」那一个入口的事，两条路各守各的。
            LibPage.Reload();
        }

        PopupCard.Width = kind is PageKind.Library ? PopupWidth(680) : PopupWidth(720);
        Layer.Show(PopupHost, trigger);
    }

    private void HidePage()
    {
        _page = PageKind.None;
        HideLayer(PopupHost);
    }

    /// <summary>浮窗宽度：取默认值与窗口 78% 里小的那个，四周留住留白。</summary>
    private double PopupWidth(double want) => Math.Min(want, Bounds.Width * 0.78);

    /// <summary>浮窗的尺寸上限：窗口的 78%×82%，缩窗口时跟着收。</summary>
    private void FitPopup()
    {
        PopupCard.MaxWidth = Math.Max(320, Bounds.Width * 0.78);
        PopupCard.MaxHeight = Math.Max(240, Bounds.Height * 0.82);
    }

    /// <summary>
    /// Esc 关最上面那一层。自下而上：关于、输出设置、设置浮窗或密码本浮窗、需要密码、确认框——顺序写错会使一次 Esc 关闭两层。
    /// </summary>
    private void CloseTopLayer()
    {
        if (Ask.IsOpen)
        {
            // 确认框上的 Esc 等于取消：它是"要不要中断"的那个问题，默认项是不中断。
            Ask.Cancel();
            return;
        }

        if (PwHost.IsVisible)
        {
            // 密码浮层上的 Esc 等于跳过这个包。
            AskPassword.Skip();
            return;
        }

        if (_page is not PageKind.None)
        {
            HidePage();
            return;
        }

        if (OutPanel.IsVisible)
        {
            HideLayer(OutPanel);
            return;
        }

        if (OverlayHost.IsVisible)
        {
            ToggleOverlay(false);
        }
    }

    private void OnPromptEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Ask.IsOpen)
        {
            return;
        }

        e.Handled = true;
        Ask.AcceptDefault();
    }

    /// <summary>保存输出设置。切到「覆盖」那一次先问一句：点否定就不落盘，下拉拨回原来的档。</summary>
    private async Task ApplyOutSettingsAsync()
    {
        var index = OutConflict.SelectedIndex;
        var conflict = index switch
        {
            1 => ConflictPolicy.Overwrite,
            2 => ConflictPolicy.Skip,
            _ => ConflictPolicy.Rename,
        };

        if (DestructiveAsk.BeforeOverwriting(_config.Conflict, conflict))
        {
            var confirmed = await Ask.AskAsync(
                UiText.ConflictOverwriteAskTitle,
                UiText.ConflictOverwriteAskBody,
                UiText.ConflictOverwrite);
            if (!confirmed)
            {
                OutConflict.SelectedIndex = _config.Conflict switch
                {
                    ConflictPolicy.Overwrite => 1,
                    ConflictPolicy.Skip => 2,
                    _ => 0,
                };
                return;
            }
        }

        _config.OutputMode = OutCustom.IsChecked == true ? OutputMode.Custom : OutputMode.Same;
        _config.OutputDirectory = OutDirBox.Text ?? string.Empty;
        _config.Conflict = conflict;

        if (!_configStore.Save(_config))
        {
            Toast(UiText.SettingsSaveFailed(_configStore.LastError), ToastTone.Bad);
        }

        RefreshOutSummary();
        HideLayer(OutPanel);
    }

    private void Toast(string message) => _toasts.Show(message);

    /// <summary>按三档给一条反馈。措辞一律来自文案表，这里只说它是哪一档。</summary>
    private void Toast(string message, ToastTone tone) => _toasts.Show(message, tone);

    /// <summary>日志写不进磁盘：界面提示条只出一条，同一次运行内不再重复。</summary>
    private void OnLogWriteFailed(string reason)
    {
        if (_logWriteFailureToasted)
        {
            return;
        }

        _logWriteFailureToasted = true;
        Toast(string.Format(CultureInfo.CurrentCulture, UiText.ToastLogUnavailableAt, reason), Levels.FailureTone);
    }

    // ══════════════════════════ 浮层 ══════════════════════════

    private void ToggleOverlay(bool open)
    {
        if (open)
        {
            FillAbout();
            Layer.Show(OverlayHost, BtnAbout);
            return;
        }

        HideLayer(OverlayHost);
    }

    private void FillAbout()
    {
        AboutVersion.Text = string.Format(CultureInfo.InvariantCulture, UiText.VersionAt, AppInfo.Version);

        // 引擎那两行都给版本号、路径挂提示：两个引擎都随包，提示里写全路径便于核对跑的是哪一份。
        var sevenZip = _engines.SevenZip;
        var sevenZipVersion = EngineVersion.Of(sevenZip);
        AboutEngine.Text = (sevenZip, sevenZipVersion) switch
        {
            (null, _) => UiText.AboutEngineMissing,
            (_, { Length: > 0 }) => sevenZipVersion,
            _ => UiText.AboutVersionUnknown,
        };
        ToolTip.SetTip(AboutEngine, sevenZip);

        var unRar = _engines.UnRar;
        var unRarVersion = EngineVersion.Of(unRar);
        AboutUnrar.Text = (unRar, unRarVersion) switch
        {
            (null, _) => UiText.AboutUnrarMissing,
            (_, { Length: > 0 }) => unRarVersion,
            _ => UiText.AboutVersionUnknown,
        };
        ToolTip.SetTip(AboutUnrar, unRar);
    }

    // ══════════════════════════ 无边框窗口 ══════════════════════════

    /// <summary>按屏幕可用区定窗口大小。默认取基准窗口 1160×660，屏小就取可用区的 94%。</summary>
    private void FitToScreen()
    {
        if (StartupOptions.Size is { } size)
        {
            Width = size.Width;
            Height = size.Height;
            return;
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = screen.WorkingArea.Width / scaling;
        var height = screen.WorkingArea.Height / scaling;

        Width = Math.Min(BaseWidth, Math.Max(MinWidth, width * 0.94));
        Height = Math.Min(BaseHeight, Math.Max(MinHeight, height * 0.94));
    }

    private void HookGrip(Control grip, WindowEdge edge) =>
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginResizeDrag(edge, e);
            }
        };

    /// <summary>
    /// 标题栏按下。双击在这一处翻最大化状态；拖动不在这里起手——第一次按下就进系统移动循环的话，第二次按下与它抢时序，双击因此间歇失效（B-2026-118）。
    /// </summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _movingTitleBar = false;
            _titleBarPress = null;
            ToggleMaximize();
            return;
        }

        _movingTitleBar = true;
        _titleBarOrigin = e.GetPosition(this);
        _titleBarPress = e;
    }

    /// <summary>按下之后真移动了才拖动。阈值取间距刻度最小档，用于排除指针抖动。</summary>
    private void OnTitleBarMoved(object? sender, PointerEventArgs e)
    {
        if (!_movingTitleBar || _titleBarPress is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _movingTitleBar = false;
            return;
        }

        var moved = e.GetPosition(this) - _titleBarOrigin;
        if (Math.Abs(moved.X) < TitleBarDragThreshold && Math.Abs(moved.Y) < TitleBarDragThreshold)
        {
            return;
        }

        // 拖动要用按下那一刻的事件参数：Avalonia 的起手 API 收的是 PointerPressedEventArgs。
        var press = _titleBarPress;
        _movingTitleBar = false;
        _titleBarPress = null;
        BeginMoveDrag(press);
    }

    private void OnTitleBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        _movingTitleBar = false;
        _titleBarPress = null;
    }

    private static T? FindAncestor<T>(Control? control)
        where T : Control
    {
        while (control is not null)
        {
            if (control is T match)
            {
                return match;
            }

            control = control.Parent as Control;
        }

        return null;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        Dispatcher.UIThread.Post(UpdateMaxGlyph, DispatcherPriority.Background);
    }

    private void UpdateMaxGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxGlyph.Data = Geometry.Parse(maximized ? Icons.Restore : Icons.Maximize);
        ToolTip.SetTip(BtnMax, maximized ? UiText.Restore : UiText.Maximize);
    }

    /// <summary>
    /// 还有任务在跑时不许直接关窗：后台引擎还在写盘，否则下一次打开会看到半成品目录（B-2026-022）。
    /// 确认框是窗口内模态，选停止才等这一批收尾最多 <see cref="CloseStopSeconds"/> 秒；没有"点第二次强制退出"这一通道。
    /// 方法名不叫 `OnClosing`：<see cref="Window"/> 自己有一个同名的受保护虚方法。
    /// </summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _log.Trace("closing running={0} paths={1}", _running, _jobs.Count);

        if (_forceClose)
        {
            return;
        }

        if (_runTask is not { IsCompleted: false } run)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            // 确认框仍开启时再次点击关闭：不叠加第二个确认框，待当前这一次答复完成后再处理。
            return;
        }

        _closing = true;
        _ = ConfirmCloseAsync(run);
    }

    private async Task ConfirmCloseAsync(Task run)
    {
        var body = string.Format(CultureInfo.CurrentCulture, UiText.CloseAskBodyAt, CloseStopSeconds);
        var stop = await Ask.AskAsync(UiText.CloseAskTitle, body, UiText.CloseAskStop);
        if (!stop)
        {
            _closing = false;
            _log.Note(UiText.CloseCancelled);
            return;
        }

        _stop = true;
        _runCts?.Cancel();
        await CloseAfterRunAsync(run);
    }

    private async Task CloseAfterRunAsync(Task run)
    {
        try
        {
            await run.WaitAsync(_closeGrace);
        }
        catch (TimeoutException)
        {
            // 收工不了就不关窗：关掉之后引擎还在写盘，用户会去删那个半成品目录。这一句必须走提示条：日志面板在中档窗口下是折起来的，只进日志等于什么都没说。
            // 放开 _closing，用户还能再问一次，判据是"每次点关闭都问一次"。
            _closing = false;
            _log.Failure(UiText.CloseStopTimeout);
            Toast(UiText.CloseStopTimeout, Levels.FailureTone);
            return;
        }
        catch (OperationCanceledException)
        {
            // 这里的取消由关窗流程自己发出，按收工处理
        }

        _forceClose = true;
        Close();
    }

    /// <summary>开始按钮。攒下这一趟的 Task，关窗时要等它收尾。</summary>
    private void StartRun() => _ = StartRunAsync();

    /// <summary>最左那个按钮：空闲时开跑，暂停时续跑（docx 26）。</summary>
    private void StartOrResume()
    {
        if (Actions.CanResume(_running, _paused))
        {
            TogglePause();
            return;
        }

        StartRun();
    }

    /// <summary>
    /// 开跑的唯一起点。按钮、空格键与 <c>--auto-run</c> 三条路都走这里，
    /// 否则 <see cref="_runTask"/> 会漏掉几条路径，关窗时拦不住（B-2026-022）。
    /// </summary>
    private Task StartRunAsync()
    {
        if (_runTask is { IsCompleted: false } running)
        {
            return running;
        }

        _runTask = RunAsync();
        return _runTask;
    }

    // ══════════════════════════ 自检与截图 ══════════════════════════

    /// <summary>自动化入口只供验证：<c>--auto-run</c> 与「开始解压」按钮同走 <see cref="RunAsync"/>，证的是真实路径。</summary>
    private async Task AutoDriveAsync()
    {
        if (StartupOptions.DropPaths is { Count: > 0 } drops)
        {
            await AddPathsAsync(drops);
        }

        if (StartupOptions.ShowOverlay)
        {
            await Task.Delay(250);
            ToggleOverlay(true);
            await Task.Delay(900);
            if (StartupOptions.AutoRun)
            {
                ToggleOverlay(false);
                await Task.Delay(450);
            }
        }

        if (StartupOptions.AutoRun)
        {
            await Task.Delay(400);
            var run = StartRunAsync();
            if (StartupOptions.ShotMidPath is { } mid)
            {
                _ = Task.Delay(StartupOptions.ShotMidDelayMs).ContinueWith(
                    _ => Dispatcher.UIThread.Post(() => SaveShot(mid)));
            }

            await run;
            await Task.Delay(350);
        }

        // 对照截图用的两个开关：把页面或确认框摆好再截，否则截到的只有主界面。
        if (StartupOptions.Page is { Length: > 0 } page)
        {
            var settings = page.Equals("settings", StringComparison.OrdinalIgnoreCase);
            TogglePage(
                settings ? PageKind.Settings : PageKind.Library,
                settings ? BtnSettings : BtnLibrary);
            await Task.Delay(500);
        }

        TaskCompletionSource? planted = null;
        if (StartupOptions.AskClose)
        {
            // 关窗确认框只在有任务运行时弹出。构造一个不会结束的任务以触发它。
            planted = new TaskCompletionSource();
            _runTask = planted.Task;
            // 故意不等这个 Task：它要等用户点按钮才完成，而这里要的是让框留在屏幕上好截图。
            _ = Ask.AskAsync(
                UiText.CloseAskTitle,
                string.Format(CultureInfo.CurrentCulture, UiText.CloseAskBodyAt, CloseStopSeconds),
                UiText.CloseAskStop);
            await Task.Delay(500);
        }

        if (StartupOptions.ShotPath is { } shot)
        {
            if (!StartupOptions.AutoRun)
            {
                await Task.Delay(1200);
            }

            // 先完成上一步构造的假任务：关窗守卫判定仍有任务运行时会拦截 Close()，进程因此无法退出，直到超时被终止。
            planted?.TrySetResult();
            SaveShot(shot);
            Close();
        }
    }

    /// <summary>把窗口内容自己渲染成 PNG（DIP 1:1，不受系统缩放影响）。
    /// <c>--shot-scale</c> 给几倍就出几倍像素：布局尺寸不动、只抬像素密度，
    /// 文字走矢量重绘，所以放大看仍然锐利 —— 出商店图这类要放大的物料用它。</summary>
    private void SaveShot(string path)
    {
        try
        {
            var scale = Math.Clamp(StartupOptions.ShotScale, 1, 4);
            var width = Math.Max(1, (int)Math.Ceiling(ClientSize.Width));
            var height = Math.Max(1, (int)Math.Ceiling(ClientSize.Height));

            // 非 96 dpi 的位图上，Avalonia 会给带 BoxShadow 的元素建一张离屏图层，而那一层的
            // dpi 会被重复计入缩放：浮层内容于是正好放大 scale 倍（scale=1 时与原来一致，
            // 加倍数才能观察到）。截图不需要阴影 —— 对外物料那层阴影是后处理加的，
            // 渲染前临时摘掉、截完按原值放回。
            var shadows = new List<(Border Box, BoxShadows Value)>();
            foreach (var box in this.GetVisualDescendants().OfType<Border>())
            {
                if (box.BoxShadow != default)
                {
                    shadows.Add((box, box.BoxShadow));
                    box.BoxShadow = default;
                }
            }

            try
            {
                using var bitmap = new RenderTargetBitmap(
                    new PixelSize(width * scale, height * scale), new Vector(96 * scale, 96 * scale));

                bitmap.Render(this);

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bitmap.Save(path);
            }
            finally
            {
                foreach (var (box, value) in shadows)
                {
                    box.BoxShadow = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Trace("{0}", ex.Message);
        }
    }

    /// <summary>
    /// 把渲染缩放、窗口尺寸与顶栏那三个系统按钮的位置落成数字，真机档 读的就是这段输出：窗口窄到下限时按钮是否仍在客户区内，这一点从窗口外面量不出来。
    /// </summary>
    private void DumpDiagnostics()
    {
        try
        {
            var lines = new List<string>
            {
                $"RenderScaling      = {RenderScaling}",
                $"ClientSize (DIP)   = {ClientSize.Width} x {ClientSize.Height}",
                $"Bounds (DIP)       = {Bounds.Width} x {Bounds.Height}",
                $"Width/Height (DIP) = {Width} x {Height}",
                $"Screens.Count      = {Screens.All.Count}",
                $"Motion             = {BbuMotion.How}",
                $"Theme (window)     = {ActualThemeVariant}",
                $"Theme (app)        = {Application.Current?.RequestedThemeVariant?.ToString() ?? "-"}",
                $"Root.Background    = {(Root.Background as global::Avalonia.Media.ISolidColorBrush)?.Color.ToString() ?? "-"}",
                $"Tier               = {_tier} (content {ContainerWidth():0.#} DIP)",
                $"Engine.SevenZip    = {_engines.SevenZip ?? "-"}",
                $"Engine.UnRar       = {_engines.UnRar ?? "-"}",
                $"TopBar.BtnMin      = {RectOf(BtnMin)}",
                $"TopBar.BtnMax      = {RectOf(BtnMax)}",
                $"TopBar.BtnClose    = {RectOf(BtnClose)}",
                $"Icon.IsNull        = {Icon is null}",
            };
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "bbu-diag.txt"), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Trace("{0}", ex.Message);
        }
    }

    /// <summary>控件相对本窗口的矩形：图标按钮量的是视觉那一格（模板里的 `HitFace`），44 的命中格会溢出 8。</summary>
    private string RectOf(Control control)
    {
        var target = control is Button button
            ? button.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "HitFace")
              ?? control
            : control;

        return target.TranslatePoint(new Point(0, 0), this) is { } at
            ? $"{at.X:0.#},{at.Y:0.#},{target.Bounds.Width:0.#},{target.Bounds.Height:0.#}"
            : "-";
    }

    /// <summary>状态图标。门禁盯着这一段：枚举加成员必须在这里补一条。</summary>
    private static string StateIcon(JobState state)
    {
        switch (state)
        {
            case JobState.Queued:
                return Icons.Queued;
            case JobState.Running:
                return Icons.Running;
            case JobState.Done:
                return Icons.Done;
            case JobState.Failed:
                return Icons.Failed;
            case JobState.Skipped:
                return Icons.Skipped;
            case JobState.Partial:
                return Icons.Partial;
            default:
                return Icons.Dot;
        }
    }

    /// <summary>
    /// 没注入日志实现时的空档：不落盘、不广播，只服务未注入日志的旧构造（无参与两参）。
    /// 「接口不给默认实现」那条判据不许这类替身静默存在，这一处是**显式例外**：
    /// 入口是唯一自建 logger 的地方，界面层不再自建，所以这一处只实现七个成员、不承担落盘。
    /// </summary>
    private sealed class SilentRunLog : IRunLog
    {
        /// <summary>无状态，一份就够。</summary>
        public static readonly SilentRunLog Instance = new();

        public event Action<LogRecord>? LineWritten
        {
            add { }
            remove { }
        }

        public event Action<string>? WriteFailed
        {
            add { }
            remove { }
        }

        public void Trace(string template, params object?[] args)
        {
        }

        public void Note(string template, params object?[] args)
        {
        }

        public void Problem(string template, params object?[] args)
        {
        }

        public void Failure(string template, params object?[] args)
        {
        }

        public void Progress(LogProgress progress, string message)
        {
        }

        public void Result(JobState state, StopReason reason, string subject, string summary)
        {
        }

        public void Write(LogRecord record)
        {
        }
    }
}
