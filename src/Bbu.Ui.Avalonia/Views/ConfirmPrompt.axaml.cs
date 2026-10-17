using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
// 裸写 Ui 会落到外层命名空间。
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 窗口内模态确认框：删除密码与关窗都用它，不另开窗口。中文按钮、底色走主题令牌，
/// （B-2026-026、B-2026-022）
///
/// 强调色、默认项与初始焦点按 <see cref="AskTone"/> 分两档：破坏性档的确认按钮带 <c>danger</c>、默认与回车都落在"取消"；
/// 取舍档的确认按钮带 <c>primary</c>，默认与回车跟着它，"取消"只剩 Esc。
///
/// 它只管"问一句并等一个布尔"，不判该不该问：那个判据在调用点，因为两处的条件不一样。
/// </summary>
public partial class ConfirmPrompt : UserControl
{
    private TaskCompletionSource<bool>? _pending;

    private Button? _defaultButton;

    public ConfirmPrompt()
    {
        InitializeComponent();
        PromptOk.Click += (_, _) => Answer(true);
        PromptCancel.Click += (_, _) => Answer(false);
        PromptScrim.PointerPressed += (_, _) => Answer(false);
    }

    /// <summary>这一层还开着没有。Esc 只关最上面那一层，宿主按它判。</summary>
    public bool IsOpen => _pending is not null;

    /// <summary>
    /// 弹一个窗口内模态并等用户的选择。返回真表示选了确认那个按钮。同一时刻只留一个待办：新的问答进来时旧的按取消收掉，免得两次关闭叠在一起。
    /// <paramref name="cancelText"/> 是否定按钮的字，不传就是通用的「取消」。
    /// <paramref name="tone"/> 不传就是破坏性档：强调色落在会改数据的那个按钮上，默认与回车留在"取消"。
    /// </summary>
    public Task<bool> AskAsync(
        string title,
        string body,
        string confirmText,
        string? cancelText = null,
        AskTone tone = AskTone.Destructive)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(confirmText);

        _pending?.TrySetResult(false);

        PromptTitle.Text = title;
        PromptBody.Text = body;
        PromptOk.Content = confirmText;
        AutomationProperties.SetName(PromptOk, confirmText);
        PromptCancel.Content = cancelText ?? UiText.Cancel;
        AutomationProperties.SetName(PromptCancel, cancelText ?? UiText.Cancel);

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;

        Layer.Show(PromptHost);
        ApplyTone(tone);
        return pending.Task;
    }

    /// <summary>Esc 等于取消。宿主处理 Esc 时先问最上面那一层，走到这里就是"不关"。</summary>
    public void Cancel() => Answer(false);

    /// <summary>回车等于默认项那一个答案，与 <see cref="Cancel"/> 成对：宿主在焦点不落于这一层时走它（B-2026-316）。</summary>
    public void AcceptDefault() => (_defaultButton ?? PromptCancel)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// 两档都要把另一档的类与 <c>IsDefault</c> 显式清掉 —— 只加不清的话，上一次传过的语气会留到下一次。
    /// </summary>
    private void ApplyTone(AskTone tone)
    {
        var choice = tone == AskTone.Choice;

        // 同一颗按钮身上不许同时挂着 danger 与 primary。
        PromptOk.Classes.Set("danger", !choice);
        PromptOk.Classes.Set("primary", choice);
        PromptOk.IsDefault = choice;
        PromptCancel.IsDefault = !choice;

        _defaultButton = choice ? PromptOk : PromptCancel;

        // 初始焦点跟着默认项走：除回车与 Esc 之外，Tab 与鼠标两条路也要能就地作答。
        Dispatcher.UIThread.Post(
            () => (choice ? PromptOk : PromptCancel).Focus(),
            DispatcherPriority.Loaded);
    }

    private void Answer(bool confirmed)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null)
        {
            return;
        }

        _ = Layer.HideAsync(PromptHost);
        pending.TrySetResult(confirmed);
    }
}
