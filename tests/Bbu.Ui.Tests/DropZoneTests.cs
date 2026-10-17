using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 拖放三态与提示条：拖着文件到窗口上方时提示条亮、移出去一个宽限后收起、放开立即收起。
///
/// 判的是**行为**，不只是 API 是否接上：走 headless 的拖放模拟（<c>HeadlessWindowExtensions.DragDrop</c>），
/// 用它的 <c>IDataTransfer</c> 重载 —— 与 <c>MainWindow</c> 里判断文件的那一处同一套 API。
/// 判据若退回 <c>DragEventArgs.Data</c> 一类旧 API，提示条不再亮，这几条会红。
/// </summary>
public sealed class DropZoneTests
{
    /// <summary>提示条只认文件：拖文本进来不许亮 —— 那说明判的是「有东西拖进来」而不是文件格式。</summary>
    [AvaloniaFact]
    public void DragOver_MustShowTheHintOnlyForFiles()
    {
        var window = Show();
        var zone = Zone(window);

        Enter(window, Text());
        Settle(window);
        Assert.False(zone.IsVisible, "拖着文本进来时提示条也亮了");

        Enter(window, Files(window, 1));
        Settle(window);
        Assert.True(zone.IsVisible, "拖着文件进来时提示条没有亮");

        window.Close();
    }

    /// <summary>
    /// 中央槽位同一时刻只有一组内容：拖着文件到窗口上方时，空状态收起、只显示拖入提示。
    /// 拖文本不算拖放态，空状态不让位。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-175")]
    public void EmptyState_MustGiveWayWhileFilesAreDraggedOver()
    {
        var window = Show();
        var empty = Empty(window);
        Assert.True(empty.IsVisible, "前置没成立：空清单时先要显示空状态");

        Enter(window, Text());
        Settle(window);
        Assert.True(empty.IsVisible, "拖文本被当成了拖放态，空状态不该让位");

        Enter(window, Files(window, 1));
        Settle(window);
        Assert.False(empty.IsVisible, "拖着文件进来时空状态没有让位，两层居中排版叠在一起");

        window.Close();
    }

    /// <summary>
    /// 移出去不是立即收起，而是等一个宽限：窗口内换拖放目标也会来一次 DragLeave，
    /// 立即收起会让提示闪一下；紧接着的 DragOver 或放开都要能取消这次收起。
    ///
    /// **量不出的那一半**：宽限到期后真的收起，靠的是 <c>DispatcherTimer</c>，而 headless
    /// 不跑真实计时器、也没有推进时钟的入口（<c>Avalonia.Headless</c> 只暴露渲染时钟）。
    /// 所以这里判的是「宽限内不收」与「取消收起」，到期那一半留待真机。
    /// </summary>
    [AvaloniaFact]
    public void DragLeave_MustNotHideTheHintWithinTheGraceAndDrop_MustHideItAtOnce()
    {
        var window = Show();
        var zone = Zone(window);

        Enter(window, Files(window, 1));
        Settle(window);
        Assert.True(zone.IsVisible, "前置没成立：拖着文件进来时提示条没亮");

        // 宽限之内还在：立即收起会让提示闪一下，这一条钉的是「不是立即收」
        window.DragDrop(Middle(window), RawDragEventType.DragLeave, Data(), DragDropEffects.None, RawInputModifiers.None);
        Assert.True(zone.IsVisible, "DragLeave 是立即收起，宽限没生效");

        // 紧接着再来一次 DragOver：这一次收起要被取消，提示条继续亮
        Enter(window, Files(window, 1));
        Settle(window);
        Assert.True(zone.IsVisible, "DragLeave 之后的 DragOver 没能取消收起");

        // 放开是立即收起，不走宽限
        window.DragDrop(Middle(window), RawDragEventType.Drop, Files(window, 1), DragDropEffects.Copy, RawInputModifiers.None);
        Settle(window);
        Assert.False(zone.IsVisible, "放开之后提示条还亮着");

        window.Close();
    }

    /// <summary>空闲时拖着文件进来，中央写的就是「松开即添加至清单」——松手真会把它加进清单。</summary>
    [AvaloniaFact, Trait("Bug", "B-2026-245")]
    public void DragOver_MustInviteWhileIdle()
    {
        var window = Show();

        Enter(window, Files(window, 1));
        Settle(window);
        Assert.Equal(UiText.DropHint, Hint(window).Text);

        window.Close();
    }

    /// <summary>
    /// 运行中拖着文件进来，提示要写清松手之后真会发生的事：文件进队列，等这一批跑完才轮到它。
    /// 照旧写「松开即加入清单」时，用户会以为马上就会处理它。
    /// </summary>
    [AvaloniaFact, Trait("Bug", "B-2026-245")]
    public void DragOver_MustSayItQueuesWhileRunning()
    {
        var window = Show();
        Running(window, true);

        Enter(window, Files(window, 1));
        Settle(window);

        Assert.NotEqual(UiText.DropHint, Hint(window).Text);
        Assert.Equal(UiText.DropHintQueued, Hint(window).Text);

        // 提示与真实后果一致：它承诺的是「当前批次结束后处理」，而跑批那一环会自动接续（见
        // .Queued_MustBePickedUpWhenTheBatchEnds）；写成「由你点开始」就是一句假承诺。
        Assert.Contains("当前批次结束", UiText.DropHintQueued, StringComparison.Ordinal);
        Assert.DoesNotContain("点开始", UiText.DropHintQueued, StringComparison.Ordinal);

        Running(window, false);
        window.Close();
    }

    /// <summary>拖着东西进入窗口：真实拖放先来 <c>DragEnter</c> 再来 <c>DragOver</c>，两条都要发。</summary>
    private static void Enter(MainWindow window, DataTransfer transfer)
    {
        var at = Middle(window);
        window.DragDrop(at, RawDragEventType.DragEnter, transfer, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, transfer, DragDropEffects.Copy, RawInputModifiers.None);
    }

    private static Border Zone(MainWindow window) =>
        window.FindControl<Border>("DropZone")
        ?? throw new InvalidOperationException("窗口里找不到提示条 DropZone");

    private static Control Empty(MainWindow window) =>
        window.FindControl<Control>("EmptyState")
        ?? throw new InvalidOperationException("窗口里找不到空状态 EmptyState");

    /// <summary>中央那一句话：它按状态换，所以取值要当场从渲染出来的控件上读。</summary>
    private static TextBlock Hint(MainWindow window) =>
        window.FindControl<TextBlock>("DropHintText")
        ?? throw new InvalidOperationException("窗口里找不到放置提示 DropHintText");

    /// <summary>
    /// 写运行中那一位。反射写进去：运行态是私有的，而开一个真的跑批要起引擎，
    /// 提示换不换字与有没有引擎无关。
    /// </summary>
    private static void Running(MainWindow window, bool running) =>
        typeof(MainWindow)
            .GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(window, running);

    private static Point Middle(MainWindow window) => new(window.Width / 2, window.Height / 2);

    /// <summary>空拖放：一条格式都没有。</summary>
    private static DataTransfer Data() => new();

    private static DataTransfer Text()
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText("不是文件"));
        return transfer;
    }

    /// <summary>带 <paramref name="count"/> 个真实文件的拖放：路径要真的存在，否则取不到存储项。</summary>
    private static DataTransfer Files(MainWindow window, int count)
    {
        var transfer = new DataTransfer();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(Path.GetTempPath(), $"bbu-drop-{Guid.NewGuid():N}.zip");
            File.WriteAllText(path, "x");
            var item = window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)).GetAwaiter().GetResult();
            Assert.True(item is not null, $"造不出存储项：{path}");
            transfer.Add(DataTransferItem.CreateFile(item!));
        }

        return transfer;
    }

    private static MainWindow Show()
    {
        var window = new MainWindow(TempDir(), TempDir());
        window.Resources.MergedDictionaries.Add(Tokens());
        window.Styles.Add(Controls());
        window.Show();
        window.Width = 900;
        window.Height = 620;
        Settle(window);
        return window;
    }

    /// <summary>
    /// 收起那一步挂在 <c>DispatcherTimer</c> 上：headless 不跑真实计时器，要等真实时间过去
    /// 再按 Background 优先级跑一次 job 队列，它才会到期（与提示条那一类测试同一个口径）。
    /// </summary>
    private static void Settle(MainWindow window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            window.UpdateLayout();
        }
    }

    private static ResourceDictionary Tokens() =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

    private static Styles Controls() =>
        (Styles)AvaloniaXamlLoader.Load(new Uri("avares://Bbu.Ui.Avalonia/Theme/Controls.axaml"));

    private static string TempDir() => Directory.CreateTempSubdirectory("bbu-drop-").FullName;
}
