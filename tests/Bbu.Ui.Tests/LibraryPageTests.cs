using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Core;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 密码本页。它直接改盘上的密码本，所以每条断言都在临时目录里跑：
/// 仓库根下那份是作者自己的密码本，碰它等于毁数据。
///
/// 页面不依赖 MainWindow：宿主交进来的东西只有 Vault、日志与两个回调，
/// 于是确认框那一步在 headless 里可以换成任意答案。
/// </summary>
public sealed class LibraryPageTests
{
    /// <summary>
    /// 行尾两个操作任何时候都只有图标：复制之后的反馈只换图形与提示，不换按钮的内容。
    /// 旧实现把 Content 换成「已复制」四个字，换回来写的又是「复制」二字 ——
    /// 按钮从此变成文字按钮，再也回不到图标。
    /// 剪贴板拿不到时那条反馈不会触发，本断言防的是它把图标顶掉。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-134")]
    public async Task CopyFeedback_MustKeepTheGlyph()
    {
        using var temp = new TempBook();
        var page = Show(temp);
        page.AddOne("hunter2");
        Dispatcher.UIThread.RunJobs();
        page.UpdateLayout();

        var item = page.FindControl<ListBox>("LibList")!
            .GetVisualDescendants().OfType<ListBoxItem>().First();
        var copy = item.GetVisualDescendants().OfType<Button>().First(button =>
            AutomationProperties.GetName(button) == UiText.LibraryCopy);

        Assert.IsType<ShapePath>(copy.Content);

        var method = typeof(LibraryPage).GetMethod(
            "CopyAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)method.Invoke(page, [page.Rows()[0], copy])!;

        Assert.IsType<ShapePath>(copy.Content);
    }

    /// <summary>空本打开时给引导，不给一张空表。</summary>
    [AvaloniaFact]
    public void EmptyBook_MustShowTheGuide()
    {
        using var temp = new TempBook();
        var page = Show(temp);

        Assert.True(page.FindControl<TextBlock>("LibEmpty")!.IsVisible);
        Assert.Equal(UiText.LibraryEmpty, page.FindControl<TextBlock>("LibEmpty")!.Text);
    }

    /// <summary>加一条之后视图与盘上都得有，且计数跟着走。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void Add_MustLandInTheBookAndOnDisk()
    {
        using var temp = new TempBook();
        var page = Show(temp, out var toasts);

        page.AddOne("hunter2");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("hunter2", temp.Vault.Entries.Select(entry => entry.Password));
        Assert.Contains("hunter2", temp.ReadBook());
        Assert.Single(page.Rows());
        Assert.False(page.FindControl<TextBlock>("LibEmpty")!.IsVisible);
        Assert.Equal(LibraryPage.AddedText, SaidOf(toasts));
    }

    /// <summary>已经在本里的密码不许再加一条，并回一句说明（不是静默失败）。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void Add_MustRefuseADuplicate()
    {
        using var temp = new TempBook();
        temp.Vault.Add("hunter2");
        var page = Show(temp, out var toasts);

        page.AddOne("hunter2");

        Assert.Single(temp.Vault.Entries);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, UiText.LibraryDuplicateAt, "hunter2"),
            SaidOf(toasts));
    }

    /// <summary>
    /// 删除要先问一次，而正文里写得出删的是哪一条。
    /// 这是自绘的确认框，不是系统对话框 —— 于是中文按钮与默认项都由本仓定
    /// （B-2026-026）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void Delete_MustAskFirstAndNameTheEntry()
    {
        using var temp = new TempBook();
        temp.Vault.Add("hunter2");
        var page = Show(temp);

        string? title = null;
        string? body = null;
        page.Confirm = (t, b, _) =>
        {
            title = t;
            body = b;
            return Task.FromResult(true);
        };

        SelectAll(page);
        Assert.True(page.DeleteSelectedAsync().GetAwaiter().GetResult());

        Assert.Equal(UiText.LibraryDeleteAskTitle, title);
        Assert.Contains("hunter2", body);
        Assert.Empty(temp.Vault.Entries);
        Assert.DoesNotContain("hunter2", temp.ReadBook());
    }

    /// <summary>在确认框上选「取消」时不删任何一条，这是默认出路。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void Delete_MustKeepTheEntryWhenTheUserCancels()
    {
        using var temp = new TempBook();
        temp.Vault.Add("hunter2");
        var page = Show(temp);
        page.Confirm = (_, _, _) => Task.FromResult(false);

        SelectAll(page);

        Assert.False(page.DeleteSelectedAsync().GetAwaiter().GetResult());
        Assert.Single(temp.Vault.Entries);
    }

    /// <summary>一条都没选时不问也不删，只说一句。</summary>
    [AvaloniaFact]
    public void Delete_MustSaySomethingWhenNothingIsSelected()
    {
        using var temp = new TempBook();
        temp.Vault.Add("hunter2");
        var page = Show(temp, out var toasts);
        var asked = false;
        page.Confirm = (_, _, _) =>
        {
            asked = true;
            return Task.FromResult(true);
        };

        Assert.False(page.DeleteSelectedAsync().GetAwaiter().GetResult());
        Assert.False(asked);
        Assert.Equal(UiText.LibraryNothingSelected, SaidOf(toasts));
    }

    /// <summary>筛选按子串走，命中为空时说的是「筛没了」而不是「本里没有」。</summary>
    [AvaloniaFact]
    public void Filter_MustNarrowTheListAndSayWhyItIsEmpty()
    {
        using var temp = new TempBook();
        foreach (var password in new[] { "alpha", "beta", "gamma" })
        {
            temp.Vault.Add(password);
        }

        var page = Show(temp);

        page.ApplyFilter("bet");

        Assert.Single(page.Rows());
        Assert.Equal("beta", page.Rows()[0].Password);

        page.ApplyFilter("zzz");

        Assert.Empty(page.Rows());
        Assert.Equal(UiText.LibrarySearchEmpty, page.FindControl<TextBlock>("LibEmpty")!.Text);
    }

    /// <summary>
    /// 超过软上限只在页内提醒，绝不自动删。
    /// 提醒位做成可调的，测的时候不必真塞一万条进去。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void SoftWarn_MustWarnWithoutDeletingAnything()
    {
        using var temp = new TempBook();
        for (var i = 0; i < 5; i++)
        {
            temp.Vault.Add("pw" + i);
        }

        var page = Show(temp);
        page.SoftWarnAt = 3;
        page.Reload();

        Assert.True(page.FindControl<Border>("LibSoftWarn")!.IsVisible);
        Assert.Equal(5, temp.Vault.Entries.Count);
    }

    /// <summary>没到软上限时那一行不出现。</summary>
    [AvaloniaFact]
    public void SoftWarn_MustStayHiddenBelowTheLimit()
    {
        using var temp = new TempBook();
        temp.Vault.Add("pw");

        var page = Show(temp);

        Assert.False(page.FindControl<Border>("LibSoftWarn")!.IsVisible);
    }

    /// <summary>
    /// 大于等于提醒线时提醒。
    /// </summary>
    [AvaloniaFact]
    public void SoftWarn_MustWarnAtTheLimitItself()
    {
        using var temp = new TempBook();
        for (var i = 0; i < 3; i++)
        {
            temp.Vault.Add("pw" + i);
        }

        var page = Show(temp);
        page.SoftWarnAt = 3;
        page.Reload();

        Assert.True(page.FindControl<Border>("LibSoftWarn")!.IsVisible);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, UiText.LibrarySoftWarnAt, 3),
            page.FindControl<TextBlock>("LibSoftWarnText")!.Text);
    }

    /// <summary>页脚计数。</summary>
    [AvaloniaFact]
    public void FooterCount_MustReadTheSameNumberAsTheHeader()
    {
        using var temp = new TempBook();
        temp.Vault.Add("pw0");
        temp.Vault.Add("pw1");

        var page = Show(temp);

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, UiText.LibraryCountAt, 2),
            page.FindControl<TextBlock>("LibCount")!.Text);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, UiText.LibraryHeaderCountAt, 2),
            page.FindControl<TextBlock>("LibHeaderCount")!.Text);
    }

    /// <summary>导入只追加：已有的不动。</summary>
    [AvaloniaFact]
    public void Import_MustAppendAndCountDuplicates()
    {
        using var temp = new TempBook();
        temp.Vault.Add("alpha");
        var page = Show(temp);

        var file = temp.WriteFile("import.txt", "alpha\nbeta\n\nbeta\n");
        var result = page.ImportFrom(file);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Added);
        Assert.Equal(2, result.Duplicate);
        Assert.Equal(1, result.Empty);
        Assert.Equal(2, temp.Vault.Entries.Count);
    }

    /// <summary>
    /// 两个「添加」合一（p025 的第二方案）：输入行常显，工具栏上不再有单独那个展开按钮；
    /// 输入框右端挂一个行内清空，有字才露面，点它把框清空。
    /// </summary>
    [AvaloniaFact]
    public void AddRow_MustStayVisibleAndCarryAnInlineClear()
    {
        using var temp = new TempBook();
        var page = Show(temp);

        Assert.True(page.FindControl<Grid>("LibAddRow")!.IsVisible, "输入行默认该可见");
        Assert.Null(page.FindControl<Button>("LibAdd"));

        var input = page.FindControl<TextBox>("LibNew")!;
        var clear = page.FindControl<Button>("LibNewClear")!;

        // 清空按钮常显：空框时藏起来等于这个入口不存在（看图反馈）。
        Assert.True(clear.IsVisible, "清空按钮该常显");

        input.Text = "hunter2";
        Dispatcher.UIThread.RunJobs();
        Assert.True(clear.IsVisible, "有字之后清空按钮仍在");

        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(string.Empty, input.Text);
    }

    /// <summary>
    /// 打开密码本浮窗时按内存重建视图：命中次数是跑批写进内存那一份的，浮窗还挂着上一次打开时的
    /// 快照就等于一次都不刷新。撤掉 TogglePage 里那次 Reload 即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-301")]
    public void OpeningThePopup_MustShowTheHitsInMemoryNow()
    {
        var window = ShowWindow();
        var library = window.FindControl<LibraryPage>("LibPage")!;
        var trigger = window.FindControl<Button>("BtnLibrary")!;
        var vault = (Vault)Field("_vault").GetValue(window)!;

        // 先打开一次（空本），再在页内加一条：页面这一份快照里它的命中次数是 0。
        ClickLibrary(window, trigger);
        library.AddOne("hunter2");
        Assert.Equal(UiText.PasswordNone, Assert.Single(library.Rows()).HitsText);

        // 关掉浮窗，空本跑完一批：次数加一落在内存那一份上。
        ClickLibrary(window, trigger);
        Assert.True(vault.Remember("hunter2"));
        var hits = vault.Find("hunter2")!.Hits;
        Assert.Equal(1, hits);

        // 再打开：页面上那一列必须等于内存里那一份，不是上一次打开时的快照。
        ClickLibrary(window, trigger);

        Assert.Equal(
            hits.ToString(CultureInfo.CurrentCulture),
            Assert.Single(library.Rows()).HitsText);
        window.Close();
    }

    /// <summary>
    /// 批量导入的结果走任务表底部的提示条，页脚那一句静态文本撤掉 —— 它挂上没有清除也没有超时
    /// （与设置页同形）。结果句交给提示条时已经格式化，档位由页面在调用点取好（Levels.NoteTone），
    /// 因此照那一档停留。把落点改回只写页脚即红。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-302")]
    public void Import_MustReportThroughTheToast()
    {
        using var temp = new TempBook();
        temp.Vault.Add("alpha");
        var page = Show(temp, out var toasts);

        Assert.Null(page.FindControl<TextBlock>("LibStatus"));

        var file = temp.WriteFile("import.txt", "alpha\nbeta\n\nbeta\n");
        var result = page.ImportFrom(file);

        Assert.Equal(1, result!.Added);
        var said = Assert.Single(toasts);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, UiText.LibraryImportResultAt, 1, 2, 1),
            said.Message);
        Assert.Equal(ToastTone.Normal, said.Tone);
    }

    /// <summary>
    /// 提示条的档位逐句定：页面那三个薄方法（Note／Problem／Failure）在调用点就把档取好，
    /// 提示条那一端不查表、也不按整句推档 —— 整句交给它会一律落陈述档。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-302")]
    public void Toast_MustGiveTheFailedToneToTheThreeFailureSentences()
    {
        // 没能写回密码本：锁文件被占成一个目录，密码层拒绝写盘并把原因交回来。
        using (var temp = new TempBook())
        {
            temp.Vault.Add("base");
            var page = Show(temp, out var toasts);
            File.Delete(temp.BookPath + ".lock");
            Directory.CreateDirectory(temp.BookPath + ".lock");

            page.AddOne("fresh");

            Assert.Null(temp.Vault.Find("fresh"));
            Assert.NotEqual(string.Empty, temp.Vault.LastWriteError);
            Assert.Equal(
                new Said(
                    string.Format(
                        CultureInfo.CurrentCulture, UiText.LibraryWriteFailedAt, temp.Vault.LastWriteError),
                    ToastTone.Bad),
                Assert.Single(toasts));
        }

        // 打不开密码本：读盘那一步的异常原文照实写进句子里。
        using (var temp = new TempBook())
        {
            var page = Show(temp, out var toasts);
            var missing = Path.Combine(Path.GetDirectoryName(temp.BookPath)!, "没有这个文件.txt");

            Assert.Null(page.ImportFrom(missing));

            Assert.Equal(
                new Said(
                    string.Format(
                        CultureInfo.CurrentCulture, UiText.LibraryExternalFailedAt, SystemMessageOf(missing)),
                    ToastTone.Bad),
                Assert.Single(toasts));
        }

        // 一条都没删掉：选中之后从密码层背后删掉，这时页面上还列着它，批量删除一条也删不掉。
        using (var temp = new TempBook())
        {
            temp.Vault.Add("hunter2");
            var page = Show(temp, out var toasts);
            page.Confirm = (_, _, _) => Task.FromResult(true);

            SelectAll(page);
            temp.Vault.Remove("hunter2");

            Assert.False(page.DeleteSelectedAsync().GetAwaiter().GetResult());
            Assert.Equal(new Said(UiText.LibraryDeletedNone, ToastTone.Bad), Assert.Single(toasts));
        }
    }

    /// <summary>「一条都没导入」登记的是「需要注意」：没出错，但结果与预期不同，所以不是陈述档。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-302")]
    public void Toast_MustGiveTheAttentionToneToNothingImported()
    {
        using var temp = new TempBook();
        temp.Vault.Add("alpha");
        var page = Show(temp, out var toasts);

        Assert.NotNull(page.ImportFrom(temp.WriteFile("import.txt", "alpha")));

        Assert.Equal(
            new Said(
                string.Format(CultureInfo.CurrentCulture, UiText.LibraryImportNoneAt, 1, 0),
                ToastTone.Warn),
            Assert.Single(toasts));
    }

    /// <summary>
    /// 余下那几句都是陈述档：做成了什么，或者用户还没给出可执行的动作（先填一条、先选中）。
    /// 逐句走一遍，免得哪一句悄悄落进失败档或注意档。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-302")]
    public void Toast_MustKeepThePlainToneForTheRest()
    {
        // 已加入密码本
        using (var temp = new TempBook())
        {
            var page = Show(temp, out var toasts);
            page.AddOne("hunter2");
            Assert.Equal(new Said(UiText.LibraryAdded, ToastTone.Normal), Assert.Single(toasts));
        }

        // 已经在密码本里：{0}
        using (var temp = new TempBook())
        {
            temp.Vault.Add("hunter2");
            var page = Show(temp, out var toasts);
            page.AddOne("hunter2");
            Assert.Equal(
                new Said(
                    string.Format(CultureInfo.CurrentCulture, UiText.LibraryDuplicateAt, "hunter2"),
                    ToastTone.Normal),
                Assert.Single(toasts));
        }

        // 先填一条密码
        using (var temp = new TempBook())
        {
            var page = Show(temp, out var toasts);
            page.AddOne(string.Empty);
            Assert.Equal(new Said(UiText.LibraryNothingToAdd, ToastTone.Normal), Assert.Single(toasts));
        }

        // 先选中要删的条目
        using (var temp = new TempBook())
        {
            temp.Vault.Add("hunter2");
            var page = Show(temp, out var toasts);
            Assert.False(page.DeleteSelectedAsync().GetAwaiter().GetResult());
            Assert.Equal(new Said(UiText.LibraryNothingSelected, ToastTone.Normal), Assert.Single(toasts));
        }

        // 这一份密码本还没有落点：纯内存的一本没有可打开的文件
        using (var temp = new TempBook())
        {
            var page = Show(temp, out var toasts);
            page.Attach(new Vault(), temp.Log);
            page.EditExternalAsync().GetAwaiter().GetResult();
            Assert.Equal(new Said(UiText.LibraryNoBookPath, ToastTone.Normal), Assert.Single(toasts));
        }

        // 导入 {0} 条 · 重复 {1} 条 · 空行 {2} 行
        using (var temp = new TempBook())
        {
            var page = Show(temp, out var toasts);
            page.ImportFrom(temp.WriteFile("import.txt", "beta"));
            Assert.Equal(
                new Said(
                    string.Format(CultureInfo.CurrentCulture, UiText.LibraryImportResultAt, 1, 0, 0),
                    ToastTone.Normal),
                Assert.Single(toasts));
        }

        // 已删除 {0} 条
        using (var temp = new TempBook())
        {
            temp.Vault.Add("hunter2");
            var page = Show(temp, out var toasts);
            page.Confirm = (_, _, _) => Task.FromResult(true);
            SelectAll(page);
            Assert.True(page.DeleteSelectedAsync().GetAwaiter().GetResult());
            Assert.Equal(
                new Said(
                    string.Format(CultureInfo.CurrentCulture, UiText.LibraryDeletedAt, 1),
                    ToastTone.Normal),
                Assert.Single(toasts));
        }
    }

    private static string SaidOf(IReadOnlyList<Said> toasts) =>
        toasts.Count == 0 ? string.Empty : toasts[^1].Message;

    private static void SelectAll(LibraryPage page)
    {
        var list = page.FindControl<ListBox>("LibList")!;
        list.SelectedItems = page.Rows().Cast<object>().ToList();
    }

    private static LibraryPage Show(TempBook temp) => Show(temp, out _);

    /// <summary>断言看提示条收到了什么：页内反馈的落点在这里。</summary>
    private static LibraryPage Show(TempBook temp, out List<Said> toasts)
    {
        var collected = new List<Said>();
        toasts = collected;
        var page = new LibraryPage();
        page.Attach(temp.Vault, temp.Log);
        page.Toast = (message, tone) => collected.Add(new Said(message, tone));
        var window = new Window { Content = page, Width = 900, Height = 620 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return page;
    }

    /// <summary>提示条收到的一条：正文与档位。档位由页面在调用点取好，提示条不另判一次。</summary>
    private sealed record Said(string Message, ToastTone Tone);

    /// <summary>照页面那一条路取系统原文：同一个调用同一处异常消息，不在断言里另写一份字面量。</summary>
    private static string SystemMessageOf(string path)
    {
        try
        {
            return PasswordBook.ReadText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>点一次顶栏的「密码本」：它就挂在 TogglePage 那一条路上，开着的时候点一下即关。</summary>
    private static void ClickLibrary(MainWindow window, Button trigger)
    {
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
    }

    /// <summary>打开浮窗那一条路只在宿主里，页面自己不动显隐，所以这一条要真窗口。</summary>
    private static MainWindow ShowWindow()
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        Settle(window);
        return window;
    }

    private static FieldInfo Field(string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    private static global::Avalonia.Controls.ResourceDictionary Tokens() =>
        (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static global::Avalonia.Styling.Styles Controls() =>
        (global::Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-lib-").FullName;

    /// <summary>一次性临时目录加一份密码本。离开作用域即删，绝不碰仓库里的正式密码本。</summary>
    private sealed class TempBook : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "bbu-lib-" + Guid.NewGuid().ToString("N"));

        public TempBook()
        {
            Directory.CreateDirectory(_dir);
            Vault = Vault.FromDirectory(_dir);
            Vault.Load();
            Log = new RunLog(Path.Combine(_dir, AppInfo.LogFileName));
        }

        public Vault Vault { get; }

        public RunLog Log { get; }

        public string BookPath => Path.Combine(_dir, "密码本.txt");

        public string ReadBook() => File.Exists(BookPath) ? File.ReadAllText(BookPath) : string.Empty;

        public string WriteFile(string name, string text)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响断言结论
            }
        }
    }
}
