using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bbu.Core.Texts;
using Bbu.Ui.Avalonia.Views;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Tests;

/// <summary>
/// 窗口内模态确认框。密码本页的删除与关窗都走它，这几条判据在这里一次钉住：
/// 中文按钮、与主题同一套底、肯定按钮排在否定按钮左边、正文不再是告警色块。
///
/// 两档语气（见 <see cref="AskTone"/>）各钉一遍，并钉「每层至多一个强调按钮」与「语气不残留到下一次」：
/// 破坏性档的强调色与默认项落在否定那一颗，取舍档反过来落在肯定那一颗。
/// 默认与 Esc 都归「取消」是破坏性档那一支的后果，那一条在 <see cref="DefaultAndEscape_MustBothMeanCancel"/>。
///
/// 它必须留在窗口之内：另开一个 Window 的对话框在关窗那一步会与宿主窗口抢焦点。
/// </summary>
public sealed class ConfirmPromptTests
{
    /// <summary>两个按钮都是中文，且确认那个按钮的文字由调用点给（删一条就说「删除」）。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void Buttons_MustBeChinese()
    {
        var prompt = Show();
        var pending = prompt.AskAsync(UiText.LibraryDeleteAskTitle, "正文", UiText.LibraryDelete);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(UiText.LibraryDelete, prompt.FindControl<Button>("PromptOk")!.Content);
        Assert.Equal(UiText.Cancel, prompt.FindControl<Button>("PromptCancel")!.Content);

        prompt.Cancel();
        _ = pending;
    }

    /// <summary>
    /// 默认按钮与 Esc 是同一个答案，且那个答案是「取消」。
    /// 语气由 <c>AskAsync</c> 决定（不传即破坏性档，默认与回车留在取消），所以这两条断言要排在它之后
    /// —— 构造完还没发问时，界面上没有默认项（B-2026-022 / B-2026-026）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void DefaultAndEscape_MustBothMeanCancel()
    {
        var prompt = Show();
        var cancelButton = prompt.FindControl<Button>("PromptCancel")!;

        var pending = prompt.AskAsync(UiText.CloseAskTitle, "正文", UiText.CloseAskStop);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cancelButton.IsDefault);
        Assert.True(cancelButton.IsCancel);

        // Esc / 回车走的都是「取消」那个按钮，按下它等于回答「不」
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(pending.GetAwaiter().GetResult());
    }

    /// <summary>点确认那个按钮才回真。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void Confirm_MustAnswerTrue()
    {
        var prompt = Show();
        var pending = prompt.AskAsync(UiText.CloseAskTitle, "正文", UiText.CloseAskStop);
        Dispatcher.UIThread.RunJobs();

        prompt.FindControl<Button>("PromptOk")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(pending.GetAwaiter().GetResult());
    }

    /// <summary>没答之前它是开着的，答完就关；宿主按这个判「最上面那一层是谁」。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void IsOpen_MustFollowThePendingQuestion()
    {
        var prompt = Show();

        Assert.False(prompt.IsOpen);

        var pending = prompt.AskAsync(UiText.CloseAskTitle, "正文", UiText.CloseAskStop);
        Dispatcher.UIThread.RunJobs();

        Assert.True(prompt.IsOpen);

        prompt.Cancel();
        _ = pending;

        Assert.False(prompt.IsOpen);
    }

    /// <summary>
    /// 两次问答不许叠在一起：新的进来时旧的按「取消」收掉。
    /// 叠在一起的话，用户点一下会同时答掉两个问题（关窗那一步会真关窗）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-022")]
    public void ASecondQuestion_MustCancelTheFirst()
    {
        var prompt = Show();
        var first = prompt.AskAsync(UiText.CloseAskTitle, "正文一", UiText.CloseAskStop);
        Dispatcher.UIThread.RunJobs();

        var second = prompt.AskAsync(UiText.LibraryDeleteAskTitle, "正文二", UiText.LibraryDelete);
        Dispatcher.UIThread.RunJobs();

        Assert.False(first.GetAwaiter().GetResult());
        Assert.Equal("正文二", prompt.FindControl<TextBlock>("PromptBody")!.Text);

        prompt.Cancel();
        _ = second;
    }

    /// <summary>卡片与遮罩都带类名。</summary>
    [AvaloniaFact]
    public void Card_MustCarryTheThemeClasses()
    {
        var prompt = Show();

        Assert.Contains("scrim", prompt.FindControl<Border>("PromptScrim")!.Classes);
        Assert.Contains("modal", prompt.FindControl<Border>("PromptCard")!.Classes);
    }

    /// <summary>
    /// 「与深色主题同一套底」：卡片的底色必须来自主题令牌解析，不是写死的颜色。
    /// 两半都要钉：样式表把 Background 绑到 Raised，且该令牌真在主题字典里。
    /// 原先还比了深浅两套取值不同 —— 主题只留深色之后那一半作废（
    /// 判据内核是"取的是令牌"，那一半照旧（B-2026-026）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-026")]
    public void Card_MustTakeItsBackgroundFromTheThemeToken()
    {
        var raised = ThemeTokens("Raised");

        Assert.True(raised.ContainsKey(ThemeVariant.Dark), "深色那套里没有 Raised");

        // 样式表把卡片的底色绑到这条令牌上，而不是写一个色值
        var controls = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Bbu.Ui.Avalonia", "Theme", "Controls.axaml"));
        var modal = controls[controls.IndexOf("Border.modal", StringComparison.Ordinal)..];
        var head = modal[..modal.IndexOf("</Style>", StringComparison.Ordinal)];

        Assert.Contains("{DynamicResource Raised}", head, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\"", head, StringComparison.Ordinal);
    }

    /// <summary>
    /// 否定项的字可以按这一次问的出路给：同一个壳子下「取消」在不同问句里的后果并不一样
    /// （删除确认里是「不删」、关窗里是「不关」、往下找那里是「不再往下找」）。不传时仍是通用的「取消」，
    /// 而且上一次传过的字不许留到下一次。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-247")]
    public void AskAsync_MustUseTheGivenNegativeCaptionOnlyWhenPassed()
    {
        var prompt = Show();
        var cancelButton = prompt.FindControl<Button>("PromptCancel")!;

        var named = prompt.AskAsync(UiText.ScanFuseAskTitle, "正文", UiText.ScanFuseAskContinue, UiText.ScanFuseAskCancel);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UiText.ScanFuseAskCancel, cancelButton.Content);

        prompt.Cancel();
        Assert.False(named.GetAwaiter().GetResult());

        var plain = prompt.AskAsync(UiText.CloseAskTitle, "正文", UiText.CloseAskStop);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UiText.Cancel, cancelButton.Content);

        prompt.Cancel();
        Assert.False(plain.GetAwaiter().GetResult());
    }

    /// <summary>肯定按钮排在否定按钮左边：页脚是右对齐的横排栈，源码序位就是用户看到的左右序，这里量真坐标（B-2026-316）。</summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void Ok_MustSitLeftOfCancel()
    {
        var prompt = ShowInWindow(out var window);
        var pending = prompt.AskAsync(UiText.CloseAskTitle, "正文", UiText.CloseAskStop);
        Dispatcher.UIThread.RunJobs();
        Settle(window);

        var ok = prompt.FindControl<Button>("PromptOk")!;
        var cancel = prompt.FindControl<Button>("PromptCancel")!;
        var okAt = ok.TranslatePoint(new Point(0, 0), window)!.Value;
        var cancelAt = cancel.TranslatePoint(new Point(0, 0), window)!.Value;

        Assert.True(
            okAt.X < cancelAt.X,
            $"肯定按钮在 {okAt.X:0.#}，否定按钮在 {cancelAt.X:0.#}：肯定没有排在否定左边");

        prompt.Cancel();
        _ = pending;
        window.Close();
    }

    /// <summary>
    /// 不传语气就是破坏性档：强调色给会改数据的那一颗，默认项与回车留在「取消」。
    /// 未指定语气的调用点因此自动落在安全方向（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void DestructiveTone_MustPutTheEmphasisOnConfirmAndTheDefaultOnCancel()
    {
        var prompt = Show();
        var ok = prompt.FindControl<Button>("PromptOk")!;
        var cancel = prompt.FindControl<Button>("PromptCancel")!;

        var pending = prompt.AskAsync(UiText.LibraryDeleteAskTitle, "正文", UiText.LibraryDelete);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("danger", ok.Classes);
        Assert.DoesNotContain("primary", ok.Classes);
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsCancel);

        prompt.Cancel();
        _ = pending;
    }

    /// <summary>
    /// 取舍档反过来：强调色与默认项都给肯定那一颗，「取消」只剩 Esc。
    /// 走这一档的是两条出路的取舍，不是「要不要中断」（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void ChoiceTone_MustPutTheEmphasisAndTheDefaultOnConfirm()
    {
        var prompt = Show();
        var ok = prompt.FindControl<Button>("PromptOk")!;
        var cancel = prompt.FindControl<Button>("PromptCancel")!;

        var pending = prompt.AskAsync(
            UiText.ScanFuseAskTitle, "正文", UiText.ScanFuseAskContinue, UiText.ScanFuseAskCancel, AskTone.Choice);
        Dispatcher.UIThread.RunJobs();

        Assert.True(ok.IsDefault);
        Assert.Contains("primary", ok.Classes);
        Assert.DoesNotContain("danger", ok.Classes);
        Assert.True(cancel.IsCancel);
        Assert.False(cancel.IsDefault);

        prompt.Cancel();
        _ = pending;
    }

    /// <summary>
    /// 每层至多一个强调按钮：两颗都挂强调色就没有重点可言。两档各量一遍（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void BothTones_MustLeaveAtMostOneEmphasisedButton()
    {
        var prompt = Show();
        var buttons = new[]
        {
            prompt.FindControl<Button>("PromptOk")!,
            prompt.FindControl<Button>("PromptCancel")!,
        };

        foreach (var tone in new[] { AskTone.Destructive, AskTone.Choice })
        {
            var pending = prompt.AskAsync(UiText.CloseAskTitle, "正文", UiText.CloseAskStop, tone: tone);
            Dispatcher.UIThread.RunJobs();

            var emphasis = buttons.Sum(button => button.Classes.Count(name => name is "danger" or "primary"));
            Assert.True(emphasis <= 1, $"{tone} 档上有 {emphasis} 颗强调按钮");

            prompt.Cancel();
            _ = pending;
        }
    }

    /// <summary>
    /// 正文不再是告警色块：<c>lvl-warn</c> 撤掉、改取中性档，卡片里也不再有挂 <c>reason</c> 或 <c>warn</c> 的元素。
    /// 色块留给真正需要它的地方，确认窗的正文只是问一句话（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void Body_MustNotWearTheWarningBlock()
    {
        var prompt = Show();
        var pending = prompt.AskAsync(UiText.ConflictOverwriteAskTitle, "正文", UiText.ConflictOverwrite);
        Dispatcher.UIThread.RunJobs();

        var body = prompt.FindControl<TextBlock>("PromptBody")!;
        Assert.Contains("dim", body.Classes);
        Assert.DoesNotContain("lvl-warn", body.Classes);

        var card = prompt.FindControl<Border>("PromptCard")!;
        var inside = card.GetVisualDescendants().OfType<Control>().ToList();

        // 走一遍子树的断言要先自证走得到正文，否则卡片没建成时这一条会空转。
        Assert.Contains(body, inside);
        Assert.Empty(inside
            .Where(control => control.Classes.Contains("reason") || control.Classes.Contains("warn"))
            .Select(control => control.GetType().Name + "." + string.Join('.', control.Classes)));
    }

    /// <summary>
    /// 语气不残留：先按取舍档问一次、再按破坏性档问一次，第二次的按钮类与默认项必须回到破坏性档。
    /// 与「上一次传过的字不许留到下一次」同形，撤掉按档清类即红（B-2026-316）。
    /// </summary>
    [AvaloniaFact]
    [Trait("Bug", "B-2026-316")]
    public void Tone_MustNotLingerIntoTheNextQuestion()
    {
        var prompt = Show();
        var ok = prompt.FindControl<Button>("PromptOk")!;
        var cancel = prompt.FindControl<Button>("PromptCancel")!;

        var choice = prompt.AskAsync(
            UiText.ScanFuseAskTitle, "正文", UiText.ScanFuseAskContinue, UiText.ScanFuseAskCancel, AskTone.Choice);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ok.IsDefault);

        prompt.Cancel();
        Assert.False(choice.GetAwaiter().GetResult());

        var plain = prompt.AskAsync(UiText.LibraryDeleteAskTitle, "正文", UiText.LibraryDelete);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("danger", ok.Classes);
        Assert.DoesNotContain("primary", ok.Classes);
        Assert.False(ok.IsDefault);
        Assert.True(cancel.IsDefault);

        prompt.Cancel();
        _ = plain;
    }

    /// <summary>从项目自己的令牌表里取一条令牌在两套主题下的取值。</summary>
    private static Dictionary<ThemeVariant, SolidColorBrush> ThemeTokens(string key)
    {
        var dict = (global::Avalonia.Controls.ResourceDictionary)
            global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                new Uri("avares://Bbu.Ui.Avalonia/Theme/Tokens.axaml"));

        var found = new Dictionary<ThemeVariant, SolidColorBrush>();
        foreach (var variant in dict.ThemeDictionaries.Keys)
        {
            if (dict.ThemeDictionaries[variant] is global::Avalonia.Controls.ResourceDictionary inner
                && inner[key] is SolidColorBrush brush)
            {
                found[variant] = brush;
            }
        }

        return found;
    }

    /// <summary>
    /// 仓库根：往上一路找 Bbu.slnx。不写死层数 —— 换 TFM 或 RID 都会让层数变，
    /// 写死的那种会静默指到别的目录。
    /// </summary>
    private static string RepoRoot() => TestRepo.Root();

    private static ConfirmPrompt Show()
    {
        var prompt = new ConfirmPrompt();
        var window = new Window { Content = prompt, Width = 900, Height = 620 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return prompt;
    }

    /// <summary>同一个宿主，另把窗口交出来：量坐标要拿它当参照系。</summary>
    private static ConfirmPrompt ShowInWindow(out Window window)
    {
        var prompt = new ConfirmPrompt();
        window = new Window { Content = prompt, Width = 900, Height = 620 };
        window.Show();
        Settle(window);
        return prompt;
    }

    /// <summary>把布局跑到底：浮层刚变可见时坐标还没算出来，不跑完量到的是上一次的位置。</summary>
    private static void Settle(Window window)
    {
        for (var spin = 0; spin < 10; spin++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
