using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Bbu.Ui.Avalonia.Interop;

/// <summary>
/// 无边框窗口的系统集成。
///
/// 圆角走 DWM 的 <c>DWMWA_WINDOW_CORNER_PREFERENCE = 2</c>，
///   换成"半透明背景 + 自绘圆角"会让字发糊：半透明表面上只能灰度抗锯齿。
///   Avalonia 与 Qt 在这一点上同理。
/// </summary>
public static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;

    private enum CornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>给窗口加 Win11 的 DWM 圆角。取不到句柄 / 老系统上静默跳过（不是错误）。</summary>
    public static bool TryRoundCorners(Window window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return false;
        var pref = (int)CornerPreference.Round;
        try
        {
            return DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int)) == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
