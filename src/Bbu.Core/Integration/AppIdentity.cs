using System;
using System.Runtime.InteropServices;

namespace Bbu.Core.Integration;

/// <summary>
/// 任务栏身份。不显式声明 AppUserModelID 时，任务栏按进程的可执行文件分组，
/// 同一个程序换个启动方式就会分成两个图标。声明要在窗口创建之前完成。
/// </summary>
public static class AppIdentity
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>声明任务栏身份。失败不阻止启动，返回成不成由调用方记一行日志。</summary>
    public static bool Declare(string? appId = null)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(appId ?? AppInfo.AppUserModelId);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
