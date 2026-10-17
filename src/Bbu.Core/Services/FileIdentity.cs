using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.Services;

/// <summary>
/// 文件的物理身份：卷序列号加文件索引，等价于 Python 版 <c>st_dev</c> 加 <c>st_ino</c> 的组合。
///
/// 硬链接的两个名字指向同一个文件，按路径去重认不出来，表现是同一个包被报很多次、被解两份。
/// 拿不到索引（非 Windows、被占用、没权限）时退回规范化路径，与 Python 版的兜底一致。
///
/// 这里用 <c>DllImport</c> 而不是 <c>LibraryImport</c>：后者要整项目开 <c>AllowUnsafeBlocks</c>，
/// 为一个查询属性把业务层的不安全代码权限打开不值得。
/// </summary>
internal static class FileIdentity
{
    public static string Of(string path)
    {
        if (Directory.Exists(path))
        {
            return "d:" + Normalize(path);
        }

        try
        {
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (TryGet(handle, out var id))
            {
                return id;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return "p:" + Normalize(path);
    }

    private static bool TryGet(SafeFileHandle handle, out string id)
    {
        id = string.Empty;
        if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(handle, out var info))
        {
            return false;
        }

        // 卷序列号可能为 0（某些内存盘、网络重定向），那种时候索引本身仍然能区分同卷内的文件。
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        id = string.Create(
            CultureInfo.InvariantCulture,
            $"f:{info.VolumeSerialNumber:x8}:{index:x16}");
        return true;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).ToLowerInvariant();

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>字段顺序与 <c>BY_HANDLE_FILE_INFORMATION</c> 逐字段对应，全是 4 字节标量，无填充。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>两个 32 位字段，与 Win32 的 FILETIME 逐字节一致。写成 <c>long</c> 会被 CLR 插 4 字节填充。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }
}
