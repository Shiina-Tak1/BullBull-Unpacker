using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>提示条的三档：陈述（做了什么）、需要注意（没失败但结果与预期不同）、失败（没生效）。</summary>
public enum ToastTone
{
    Normal,
    Warn,
    Bad,
}

/// <summary>
/// 主内容区底部的提示条容器。
///
/// 同一时刻只有一条：新提示进来时旧的直接撤掉，不做纵向叠加。没有关闭按钮：点提示条任意位置即撤下。
/// 文案由调用方传入：这里不持有任何用户可见字符串，措辞一律来自文案表。
/// </summary>
public partial class ToastHost : UserControl
{
    /// <summary>停留 6 秒：用户常常正看着别处，而同一时刻只有这一条。</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(6);

    /// <summary>淡出时长，与样式表里那一条过渡对齐。</summary>
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(220);

    /// <summary>排着的两次撤下（到时那一次与淡出那一次）。换新提示时把旧的连同计时一起停掉。</summary>
    private IDisposable? _hold;
    private IDisposable? _fade;

    public ToastHost()
    {
        InitializeComponent();
    }

    /// <summary>换一条提示。空串直接忽略，未布局时调用也安全。</summary>
    public void Show(string message, ToastTone? tone = null)
    {
        if (string.IsNullOrEmpty(message))
            return;

        // 不给档位就按陈述档：级别在写日志那一刻就定了，提示条不另判一次。
        var resolved = tone ?? ToastTone.Normal;

        _hold?.Dispose();
        _fade?.Dispose();
        ToastStack.Children.Clear();

        var toast = new Border
        {
            Child = new TextBlock { Text = message, VerticalAlignment = VerticalAlignment.Center },
        };
        toast.Classes.Add("toast");
        switch (resolved)
        {
            case ToastTone.Bad:
                toast.Classes.Add("bad");
                break;
            case ToastTone.Warn:
                toast.Classes.Add("warn");
                break;
        }

        ToastStack.Children.Add(toast);

        // 没有按钮：点提示条任意位置与到时走同一个出口，撤下的时机只有一处判定。
        toast.PointerPressed += (_, _) => Dismiss(toast);

        // 两段式进场：先以 0 透明度挂上，下一帧再置 1，过渡才有起点。
        Dispatcher.UIThread.Post(() => toast.Opacity = 1, DispatcherPriority.Background);

        _hold = DispatcherTimer.RunOnce(() => Dismiss(toast), Hold);
    }

    /// <summary>撤一条提示。重复调用没有副作用：不在栈上的那条直接返回。</summary>
    private void Dismiss(Border toast)
    {
        _hold?.Dispose();
        if (!ToastStack.Children.Contains(toast))
            return;

        toast.Opacity = 0;
        _fade?.Dispose();
        _fade = DispatcherTimer.RunOnce(() => ToastStack.Children.Remove(toast), Fade);
    }
}
