using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 浮层显隐的<b>唯一</b>实现：关于、输出设置、设置浮窗、密码本浮窗、需要密码与两个确认框都走这里。
/// "没有第二份遮罩的显隐逻辑"，所以 <c>open</c> 与 <c>closing</c> 只在本文件里增删。
///
/// 进入：先让宿主可见，再在渲染优先级上挂 <c>open</c>；起始值必须真渲染一帧，否则淡入被跳过。
/// 退出：摘 <c>open</c> 的同时挂 <c>closing</c>，退场动画挂在这个类上 ——
/// 只摘 <c>open</c> 交给 <c>Transitions</c> 的话，动画值一撤就落回样式值，那一趟不是可过渡的起点，
/// 表现是"只有入场没有出场"。这一段时间里用户可能又打开它，所以到期时以"有没有 <c>open</c>"为准；
/// 淡出跑完之后把焦点还给打开它的那个控件。
/// </summary>
internal static class Layer
{
    /// <summary>退场时长，与样式表里 closing 那一组的 0.200 秒对齐。</summary>
    public static TimeSpan Fade => TimeSpan.FromMilliseconds(200);

    /// <summary>每个浮层是被哪个控件打开的。关掉之后焦点回到它，键盘用户不会掉到窗口开头。</summary>
    private static readonly Dictionary<Panel, Control> Triggers = [];

    public static void Show(Panel host, Control? trigger = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (trigger is not null)
        {
            Triggers[host] = trigger;
        }

        host.IsVisible = true;
        Mark(host, "closing", false);
        Dispatcher.UIThread.Post(() => Mark(host, "open", true), DispatcherPriority.Render);
    }

    public static async Task HideAsync(Panel host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Mark(host, "open", false);
        Mark(host, "closing", true);
        await Task.Delay(Fade);
        Mark(host, "closing", false);
        if (!IsOpen(host))
        {
            host.IsVisible = false;
        }

        Restore(host);
    }

    /// <summary>这一层当前是否处于打开状态。Esc 关最上面那一层时按它判。</summary>
    public static bool IsOpen(Panel host) =>
        host.IsVisible && host.Children.Any(child => child.Classes.Contains("open"));

    private static void Restore(Panel host)
    {
        if (Triggers.Remove(host, out var trigger))
        {
            trigger.Focus();
        }
    }

    /// <summary>给这一层的每个子元素挂上或摘掉一个类。显隐的两个开关都只走这里。</summary>
    private static void Mark(Panel host, string name, bool on)
    {
        foreach (var child in host.Children)
        {
            if (on)
            {
                if (!child.Classes.Contains(name))
                {
                    child.Classes.Add(name);
                }
            }
            else
            {
                child.Classes.Remove(name);
            }
        }
    }
}
