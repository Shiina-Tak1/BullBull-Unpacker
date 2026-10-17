using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.SingleInstance;

/// <summary>
/// 管道那一头是不是**本份程序**：只取内核给的答案（对端进程号到映像路径），载荷里自称的字段一律不看；两侧都调这里。
/// </summary>
public static class SingleInstancePeer
{
    /// <summary>客户端问：服务端是谁。取不到回 <c>null</c>。</summary>
    public static int? ServerProcessId(SafePipeHandle pipe) => NamedPipeProcessId(pipe, server: true);

    /// <summary>服务端问：客户端是谁。取不到回 <c>null</c>。</summary>
    public static int? ClientProcessId(SafePipeHandle pipe) => NamedPipeProcessId(pipe, server: false);

    /// <summary>那个进程跑的是不是本份程序。进程不在、或读不到它的映像路径，一律按「不是」处理。</summary>
    public static bool IsOwnProgram(int? processId) =>
        SameProgram(ImagePathOf(processId), Environment.ProcessPath);

    /// <summary>
    /// 两个映像路径是不是同一个文件：取整路径后按序数不敏感比；**同一个文件名、不同目录**算两份程序
    /// （便携档与安装档各有各的数据目录与窗口，递错了会落到另一份程序那里）。
    /// </summary>
    public static bool SameProgram(string? peerImagePath, string? ownImagePath)
    {
        if (string.IsNullOrEmpty(peerImagePath) || string.IsNullOrEmpty(ownImagePath))
        {
            return false;
        }

        return string.Equals(FullPath(peerImagePath), FullPath(ownImagePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>进程的映像路径。进程已退出、或是读不了的进程（权限、位数），回 <c>null</c>。</summary>
    private static string? ImagePathOf(int? processId)
    {
        if (processId is not { } id)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static int? NamedPipeProcessId(SafePipeHandle pipe, bool server)
    {
        if (pipe is null || pipe.IsInvalid)
        {
            return null;
        }

        uint id;
        var ok = server
            ? GetNamedPipeServerProcessId(pipe, out id)
            : GetNamedPipeClientProcessId(pipe, out id);
        return ok ? (int)id : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
