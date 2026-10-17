using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using ShapePath = Avalonia.Controls.Shapes.Path;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>列表里的一行。命中次数已经折成给人看的写法，视图不再算第二遍。</summary>
public sealed record LibraryRow(string Password, string HitsText);

/// <summary>一次批量导入的账。三个数由密码层给，页面只把它摆出来。</summary>
public sealed record ImportResult(int Added, int Duplicate, int Empty);

/// <summary>
/// 密码本页：窗口内居中浮窗的内容层。搜索、新增、批量导入、删除、外部编辑五个入口，
/// 条目与落盘全在 <see cref="Vault"/> 那一层，这一页不自己数、不自己写盘。
///
/// 页面的显示顺序就是密码层里的顺序（也就是尝试顺序），这里不排序 ——
/// 排一次会使"先试哪个"与用户看到的顺序不一致。
///
/// 它不构造窗口、不弹对话框：确认与选文件都由宿主接进来，所以 headless 里能直接测。
/// </summary>
public partial class LibraryPage : UserControl
{
    /// <summary>搜索防抖。逐字符重建会重复遍历列表，改为停止输入后再重建视图。</summary>
    private const int DebounceMs = 250;

    private readonly DispatcherTimer _debounce;
    private readonly ObservableCollection<LibraryRow> _rows = [];
    private Vault? _vault;
    private IRunLog? _log;
    private string _keyword = string.Empty;

    public LibraryPage()
    {
        InitializeComponent();
        LibList.ItemsSource = _rows;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ApplyFilter(LibSearch.Text ?? string.Empty);
        };

        LibSearch.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        LibNew.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter)
            {
                e.Handled = true;
                AddOne(LibNew.Text ?? string.Empty);
            }
        };

        // 行内清空按钮常显：看图反馈「行内小圆 x 清空未见痕迹」——
        // 空框时藏起来，等于这个入口根本不存在。搜索框与新增框各一个。
        LibSearchClear.Click += (_, _) =>
        {
            LibSearch.Text = string.Empty;
            LibSearch.Focus();
        };

        LibNewClear.Click += (_, _) =>
        {
            LibNew.Text = string.Empty;
            LibNew.Focus();
        };

        LibAddCommit.Click += (_, _) => AddOne(LibNew.Text ?? string.Empty);
        LibDelete.Click += async (_, _) => await DeleteSelectedAsync();
        LibImport.Click += async (_, _) => await ImportAsync();
        LibEdit.Click += async (_, _) => await EditExternalAsync();
        LibReload.Click += (_, _) => ReloadFromDisk();
        LibClose.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用户点了页头的关闭。宿主负责收掉这一层，页面自己不动显隐。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>删除前的确认。宿主把它接到窗口内确认框上；不接就按取消处理，宁可删不掉。</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>选要导入的文件。宿主接到系统文件对话框上；不接就当用户没选。</summary>
    public Func<Task<string?>>? PickImportFile { get; set; }

    /// <summary>
    /// 页内那句即时反馈的落点。宿主接到任务表底部的提示条上；
    /// 不接就没有反馈。档位由文案级别推，页面不在调用点另指定。
    /// </summary>
    public Action<string, ToastTone>? Toast { get; set; }

    /// <summary>
    /// 条数达到在页内提醒（判据是 &gt;=）。
    /// </summary>
    public int SoftWarnAt { get; set; } = Vault.BookSoftWarn;

    /// <summary>当前视图里的行。宿主与断言都从这里看，不另存一份。</summary>
    public IReadOnlyList<LibraryRow> Rows() => [.. _rows];

    /// <summary>新增成功那一句。断言比对的是同一处文案，不另写一份中文字面量。</summary>
    public static string AddedText => UiText.LibraryAdded;

    /// <summary>接上密码层与日志。页面不读盘：要重读走 <see cref="ReloadFromDisk"/>。</summary>
    public void Attach(Vault vault, IRunLog log)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
        _log = log;
        Reload();
    }

    /// <summary>按内存里的密码层重建列表。</summary>
    public void Reload()
    {
        if (_vault is { } vault)
        {
            Rebuild(vault.Entries);
        }
    }

    /// <summary>
    /// 按磁盘重读。外部编辑器改完之后走它 —— 这时候内存里那一份已经过时了，
    /// 按内存重建只会把用户刚写的改动盖回去。
    /// </summary>
    public void ReloadFromDisk()
    {
        if (_vault is not { } vault)
        {
            return;
        }

        vault.Load();
        Reload();
    }

    /// <summary>按关键字重建视图。空关键字就是全量。</summary>
    public void ApplyFilter(string keyword)
    {
        _keyword = keyword ?? string.Empty;
        Reload();
    }

    /// <summary>当前选中的密码原文。删的时候拿它去数据源里删，不是拿行对象删。</summary>
    public IReadOnlyList<string> SelectedPasswords() =>
        [.. LibList.SelectedItems?.OfType<LibraryRow>().Select(row => row.Password) ?? []];

    /// <summary>
    /// 加一条。空串与重复都不加，密码原样收下、不做首尾裁剪 ——
    /// 用户输的前导或尾随空格可能真是密码的一部分。
    /// 加进去之后把行内输入行收起来：留着它会让列表少显示一行，而这时用户要看的正是列表。
    /// </summary>
    public string AddOne(string password)
    {
        if (_vault is not { } vault)
        {
            return string.Empty;
        }

        var value = password ?? string.Empty;
        if (value.Length == 0)
        {
            return Note(UiText.LibraryNothingToAdd);
        }

        if (vault.Find(value) is not null)
        {
            return Note(UiText.LibraryDuplicateAt, value);
        }

        if (!vault.Add(value))
        {
            return Failure(UiText.LibraryWriteFailedAt, vault.LastWriteError);
        }

        LibNew.Text = string.Empty;
        LibAddRow.IsVisible = false;
        return Note(UiText.LibraryAdded);
    }

    /// <summary>删掉选中的条目。删之前必须问一次，问的那一步返回假就什么都不做。</summary>
    public async Task<bool> DeleteSelectedAsync()
    {
        var picked = SelectedPasswords();
        if (picked.Count == 0)
        {
            Note(UiText.LibraryNothingSelected);
            return false;
        }

        return await AskAndRemoveAsync(picked);
    }

    /// <summary>弹一次选文件，选中就导入。</summary>
    public async Task<ImportResult?> ImportAsync()
    {
        if (PickImportFile is not { } pick || await pick() is not { Length: > 0 } path)
        {
            return null;
        }

        return ImportFrom(path);
    }

    /// <summary>
    /// 从一个文件导入：只追加、不改已有。两种文件都认 —— 密码本格式按密码本规则解，
    /// 其余每一行原样当一个密码。重复与空行分别计数，数由密码层给。
    /// </summary>
    public ImportResult? ImportFrom(string path)
    {
        if (_vault is not { } vault)
        {
            return null;
        }

        try
        {
            var text = PasswordBook.ReadText(path);
            var passwords = PasswordBook.ImportPasswords(text, PasswordBook.LooksLikeBook(text));
            var (added, duplicate, empty) = vault.AddMany(passwords);

            // 有导入与一条都没导入是两种结果，连占位符的个数都不同，级别也各是一条 ——
            // 合成一句会让其中一种结果说错语气（级别归属总表）。
            if (added > 0)
            {
                Note(UiText.LibraryImportResultAt, added, duplicate, empty);
            }
            else
            {
                Problem(UiText.LibraryImportNoneAt, duplicate, empty);
            }

            return new ImportResult(added, duplicate, empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Failure(UiText.LibraryExternalFailedAt, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 用系统程序打开密码本，等它退出再按磁盘重读。
    /// 编辑器开着的时候本程序不写入：两边同时写会互相盖掉。
    /// </summary>
    public async Task EditExternalAsync()
    {
        if (_vault?.BookPath is not { Length: > 0 } path)
        {
            Note(UiText.LibraryNoBookPath);
            return;
        }

        try
        {
            using var editor = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            if (editor is not null)
            {
                await editor.WaitForExitAsync();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Failure(UiText.LibraryExternalFailedAt, ex.Message);
            return;
        }

        ReloadFromDisk();
    }

    /// <summary>行尾复制。取的是这一行的密码原文，不是列表里被选中的那几条。</summary>
    private void OnRowCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LibraryRow row } button)
        {
            _ = CopyAsync(row, button);
        }
    }

    /// <summary>
    /// 把一个密码写进剪贴板：图标短暂换成勾、提示短暂换成「已复制」。
    /// 只换图标与提示，不换按钮的内容 —— 把 Content 换成文字会顶掉那个图标，
    /// 换回来时写的是「复制」二字，按钮从此不再是图标。
    /// 拿不到剪贴板（没有顶层窗口）时不做任何反馈：此时写「已复制」与事实不符。
    /// </summary>
    private async Task CopyAsync(LibraryRow row, Button button)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(row.Password);
        _log?.Note(UiText.PasswordCopiedAt, row.Password);

        var glyph = button.Content as ShapePath;
        if (glyph is not null)
        {
            glyph.Data = Geometry.Parse(Icons.Done);
        }

        CopiedTip(button, true);
        DispatcherTimer.RunOnce(
            () =>
            {
                if (glyph is not null)
                {
                    glyph.Data = Geometry.Parse(Icons.CopyRow);
                }

                CopiedTip(button, false);
            },
            TimeSpan.FromSeconds(1));
    }

    /// <summary>复制反馈只改提示与无障碍名：按钮本身任何时候都只有图标。</summary>
    private static void CopiedTip(Button button, bool copied)
    {
        var name = copied ? UiText.Copied : UiText.LibraryCopy;
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
    }

    /// <summary>行尾删除。行内入口不是绕过确认的通道：删之前照样问一次。</summary>
    private void OnRowDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LibraryRow row })
        {
            _ = DeleteRowAsync(row.Password);
        }
    }

    /// <summary>
    /// 删掉行尾这一个。与批量删除分成两条路：那条删的永远是选中集，
    /// 这条只碰传进来的这一条，合在一起以后就分不清一次点击到底删了谁。
    /// </summary>
    private Task<bool> DeleteRowAsync(string password) => AskAndRemoveAsync([password]);

    /// <summary>
    /// 问一次再删。<paramref name="picked"/> 是密码原文而不是行对象：
    /// 行可能来自上一次筛选，按原文回密码层删才不会删错。
    /// 正文要说清删的是哪些：只有一条时把那条原文写出来，多条时报个数（B-2026-026）。
    /// 没接确认回调时不问就删，与这条路径原有的行为一致，本轮不动它。
    /// </summary>
    private async Task<bool> AskAndRemoveAsync(IReadOnlyList<string> picked)
    {
        if (_vault is not { } vault)
        {
            return false;
        }

        var body = picked.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, UiText.LibraryDeleteOneAskAt, picked[0])
            : string.Format(CultureInfo.CurrentCulture, UiText.LibraryDeleteManyAskAt, picked.Count);

        if (Confirm is { } ask && !await ask(UiText.LibraryDeleteAskTitle, body, UiText.LibraryDelete))
        {
            return false;
        }

        // 整批交给密码层删一次（B-2026-173）。
        var removed = vault.RemoveMany(picked);

        // 「删掉了」与「一条都没删掉」是两种结果，各用一条文案，级别也随之各是各的（级别归属总表）。
        if (removed > 0)
        {
            Note(UiText.LibraryDeletedAt, removed);
            return true;
        }

        Failure(UiText.LibraryDeletedNone);
        return false;
    }

    /// <summary>
    /// 一次操作的即时反馈：同时进日志与任务表底部的提示条，日志里翻得到同一条。
    /// 页面只说用了哪条文案，级别由方法名承载。
    /// 参数是要填进模板的值，页面不再自己调 <c>string.Format</c>。
    /// </summary>
    private string Failure(string template, params object?[] args)
    {
        var message = Message(template, args);
        _log?.Failure(template, args);
        Toast?.Invoke(message, Levels.FailureTone);
        Reload();
        return message;
    }

    private string Note(string template, params object?[] args)
    {
        var message = Message(template, args);
        _log?.Note(template, args);
        Toast?.Invoke(message, Levels.NoteTone);
        Reload();
        return message;
    }

    private string Problem(string template, params object?[] args)
    {
        var message = Message(template, args);
        _log?.Problem(template, args);
        Toast?.Invoke(message, Levels.ProblemTone);
        Reload();
        return message;
    }

    /// <summary>
    /// 提示条那一句在这里成形；日志那一句由通道按模板自己填。两处各渲染一次（这里用当前文化、
    /// 通道用不变文化），今天逐字相同是因为实参都是裸占位符配字符串与整数 ——
    /// 「日志与提示条同字」这条不变量现在是巧合，不是结构保证。
    /// </summary>
    private static string Message(string template, object?[] args) =>
        args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>按当前关键字重建。筛选是大小写不敏感的包含匹配，与 1.1.0 同口径。</summary>
    private void Rebuild(IReadOnlyList<VaultEntry> entries)
    {
        _rows.Clear();
        foreach (var entry in entries)
        {
            if (_keyword.Length > 0 && !entry.Password.Contains(_keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _rows.Add(new LibraryRow(entry.Password, HitsText(entry)));
        }

        LibEmpty.IsVisible = _rows.Count == 0;
        LibEmpty.Text = entries.Count == 0 ? UiText.LibraryEmpty : UiText.LibrarySearchEmpty;

        // 两处条数同源：页头那一句回答「本里有多少条」，底部那一句是同一件事的短写法。
        LibCount.Text = string.Format(CultureInfo.CurrentCulture, UiText.LibraryCountAt, entries.Count);
        LibHeaderCount.Text = string.Format(
            CultureInfo.CurrentCulture, UiText.LibraryHeaderCountAt, entries.Count);

        // 判据是「达到提醒线」。
        LibSoftWarn.IsVisible = entries.Count >= SoftWarnAt;
        // 占位符是提醒线。
        LibSoftWarnText.Text = Message(UiText.LibrarySoftWarnAt, [SoftWarnAt]);
    }

    /// <summary>命中次数只给数值：口径由表头说，表里再写一遍「成功过 N 次」是重复。</summary>
    private static string HitsText(VaultEntry entry) =>
        entry.Hits <= 0 ? UiText.PasswordNone : entry.Hits.ToString(CultureInfo.CurrentCulture);
}
