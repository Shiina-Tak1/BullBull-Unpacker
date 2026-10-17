using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bbu.Core.Models;
// 裸写 Ui 会落到外层命名空间。
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 询问密码的模态面板。
///
/// 它是窗口内的一层，不是独立窗口——独立窗口会进任务栏，也脱离主窗口的层叠顺序。
/// 答案就是用户的选择：密码原文表示采用；<c>null</c> 表示跳过这个包（它内部所有层一起不弄），
/// 关闭按钮、Esc、跳过按钮三条路都归到 <c>null</c>。
/// 交回密码之后这一层<b>不立刻收</b>：密码要由穿透层拿引擎验一遍，验不过还会带着下一次询问回来，
/// 所以面板先停在验证中，结论由宿主的收口点决定（见 <see cref="Release"/>）。
/// </summary>
public sealed partial class PasswordDialog : UserControl
{
    /// <summary>这一次询问的答案。同一个面板会被问很多次，每次重新开一个。</summary>
    private TaskCompletionSource<string?>? _answer;

    private Panel? _host;

    /// <summary>已经交回密码、正等穿透层验完；下一次询问会把它清掉，那时面板要能重新输入。</summary>
    private bool _verifying;

    /// <summary>XAML 加载器要的公有无参构造。</summary>
    public PasswordDialog()
    {
        InitializeComponent();

        BtnClose.Click += (_, _) => Finish(null);
        BtnSkip.Click += (_, _) => Finish(null);
        BtnVerify.Click += (_, _) => Verify();
        // 输入变化即清除上一次的错误提示
        TxtInput.TextChanged += (_, _) => TxtWrong.IsVisible = false;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>按一次询问的内容先把界面铺好。正常路径走 <see cref="AskAsync"/>。</summary>
    public PasswordDialog(PasswordAsk ask) : this() => Fill(ask);

    public bool IsVerifying => _verifying;

    /// <summary>
    /// 在 <paramref name="host"/> 这一层上铺开并等答案。可以从任何线程调；
    /// 取消时按「跳过」收口，拿到答案之后把这一层收回去。
    /// </summary>
    public async Task<string?> AskAsync(Panel host, PasswordAsk ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        return await Dispatcher.UIThread.InvokeAsync(() => AskCoreAsync(host, ask, cancellationToken));
    }

    private async Task<string?> AskCoreAsync(Panel host, PasswordAsk ask, CancellationToken cancellationToken)
    {
        Fill(ask);
        _host = host;
        _answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 关掉这一层的动作投递到下一轮消息：取消可能立刻回调，那时面板还没显示出来。
        using var registration = cancellationToken.Register(
            () => Dispatcher.UIThread.Post(() => Finish(null)));

        Layer.Show(host);
        TxtInput.Focus();
        var result = await _answer.Task;
        _answer = null;
        if (result is null)
        {
            // 跳过与取消没有下文要等，当场收；交了密码的这一支停在验证中，由宿主按结论收回。
            await Layer.HideAsync(host);
        }

        return result;
    }

    /// <summary>把停在验证中的这一层收回去。宿主在「已经在解压了」与「这一批收尾」两处调它。</summary>
    public void Release()
    {
        if (_host is not { } host)
        {
            return;
        }

        _verifying = false;
        _ = Layer.HideAsync(host);
    }

    /// <summary>
    /// 按这一次询问的内容铺界面。输入框每次都清空；验证中这一档也一起复位——会再问一次就说明上一次的密码不对，面板这时要能重新输入。压缩包名给的是**完整路径**：同名包很常见，
    /// 只写文件名时用户分不清问的是哪一个。
    /// </summary>
    private void Fill(PasswordAsk ask)
    {
        _verifying = false;
        TxtInput.Text = string.Empty;
        TxtInput.IsEnabled = true;
        BtnVerify.Content = UiText.AskVerify;
        BtnVerify.IsEnabled = true;
        TxtArchive.Text = ask.ArchivePath;
        ToolTip.SetTip(TxtArchive, ask.ArchivePath);
        TxtTried.Text = string.Format(UiText.AskTriedAt, ask.Tried);
        TxtSummary.Text = ask.Summary;
        TxtWrong.IsVisible = ask.Attempt > 1;
    }

    /// <summary>跳过这个包。Esc 落在宿主上时走这一条，与面板自己的 Esc 同一个出口。</summary>
    public void Skip() => Finish(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Verify();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
    }

    /// <summary>
    /// 空框不提交；否则原样交出输入内容，首尾空格也是密码的一部分。交出去之后面板转验证中这一档：验一次要起一次引擎，这段时间里再点一次不算第二次提交。
    /// </summary>
    private void Verify()
    {
        var password = TxtInput.Text;
        if (string.IsNullOrEmpty(password) || _verifying)
        {
            return;
        }

        _verifying = true;
        BtnVerify.Content = UiText.AskVerifying;
        BtnVerify.IsEnabled = false;
        TxtInput.IsEnabled = false;
        Finish(password);
    }

    private void Finish(string? result)
    {
        _answer?.TrySetResult(result);
    }
}
