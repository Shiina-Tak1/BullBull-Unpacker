using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.Services;

/// <summary>
/// 挂起与恢复一个进程的所有线程。
///
/// 7z 与 UnRAR 都没有暂停接口，而「解压到一半重来」的代价太大（大包要重新读一遍）；挂起目标进程会冻结
/// 它的所有线程、保留文件位置，恢复后从原处继续，所以「点了暂停就真的停下、再点继续接着跑」走这条路。
///
/// 路线是<b>逐线程</b>挂起，不用那个一次挂起整进程的接口：实测打开进程要暂停权限会被拒，
/// 而按线程打开对 7z 与 Rar 的每个线程都成功（这条实测量的是 `Rar.exe`，换随包 UnRAR 之后未复测）。
/// 已知取舍是挂起期间新建的线程不会被挂住，而这两个工具启动时就开好了线程池，主线程挂住后无法继续工作。
/// </summary>
internal static class ProcessSuspend
{
    private const uint ThreadSuspendResume = 0x0002;
    private const uint SnapshotThreads = 0x00000004;
    private const uint InvalidHandleValue = 0xFFFFFFFF;
    private const uint SuspendFailed = 0xFFFFFFFF;

    private static readonly Dictionary<int, (List<SafeThreadHandle> Handles, DateTime Start)> Held = [];
    private static readonly Lock Gate = new();

    /// <summary>挂起或恢复。返回是否成功：身份不符与恢复失败都算失败。非 Windows 恒为假。</summary>
    public static bool Set(int pid, bool suspend) => Set(pid, suspend, StartTimeOf);

    /// <summary>测试用的重载：替换启动时刻的来源，用来造「进程号被复用」那一档。</summary>
    internal static bool Set(int pid, bool suspend, Func<int, DateTime?> startOf)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (Gate)
        {
            try
            {
                var known = Held.TryGetValue(pid, out var already);
                var current = startOf(pid);

                // 表里有键时先复检身份：启动时刻对不上说明进程号已被复用，旧键作废、句柄丢掉，
                // 既不许把「没挂」回报成「已挂」，也不许拿这个进程号去挂无关进程。
                if (known && !IdentityHolds(already.Start, current))
                {
                    foreach (var handle in already.Handles)
                    {
                        handle.Dispose();
                    }

                    Held.Remove(pid);
                    return false;
                }

                if (suspend)
                {
                    // 已经挂过的不重复挂：重复挂起会让恢复次数对不上，进程无法恢复。
                    if (known)
                    {
                        return true;
                    }

                    // 这个进程号现在读不出启动时刻，不许动手。
                    if (current is null)
                    {
                        return false;
                    }

                    var handles = new List<SafeThreadHandle>();
                    foreach (var threadId in ThreadIds(pid))
                    {
                        var handle = OpenThread(ThreadSuspendResume, false, threadId);
                        if (handle.IsInvalid)
                        {
                            handle.Dispose();
                            continue;
                        }

                        if (SuspendThread(handle) == SuspendFailed)
                        {
                            handle.Dispose();
                            continue;
                        }

                        handles.Add(handle);
                    }

                    if (handles.Count == 0)
                    {
                        return false;
                    }

                    Held[pid] = (handles, current.Value);
                    return true;
                }

                if (!Held.Remove(pid, out var resume))
                {
                    return false;
                }

                var resumed = true;
                foreach (var handle in resume.Handles)
                {
                    resumed &= ResumeThread(handle) != SuspendFailed;
                    handle.Dispose();
                }

                return resumed;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 挂起前的身份复检：进程号与启动时刻这一对两边都读到、且逐刻相等，才算同一个进程。
    /// 取不到启动时刻（进程已退出、拒绝访问）或没记过的那一侧都算身份不符 ——
    /// 进程号会被复用，只按进程号认会把无关进程的线程挂住。
    /// </summary>
    internal static bool IdentityHolds(DateTime? recorded, DateTime? current) =>
        recorded is { } was && current is { } now && was == now;

    /// <summary>这个进程号的启动时刻。读不出来（已退出、拒绝访问）回 <c>null</c>。</summary>
    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<uint> ThreadIds(int pid)
    {
        var found = new List<uint>();
        using var snapshot = CreateToolhelp32Snapshot(SnapshotThreads, 0);
        if (snapshot.IsInvalid)
        {
            return found;
        }

        var entry = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
        if (!Thread32First(snapshot, ref entry))
        {
            return found;
        }

        do
        {
            if (entry.OwnerProcessId == (uint)pid)
            {
                found.Add(entry.ThreadId);
            }

            entry.Size = (uint)Marshal.SizeOf<ThreadEntry32>();
        }
        while (Thread32Next(snapshot, ref entry));

        return found;
    }

    /// <summary>进程号加线程号。字段顺序与 Win32 的 THREADENTRY32 逐字段对应。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeThreadHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeThreadHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(SafeThreadHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeThreadHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(SafeSnapshotHandle snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(SafeSnapshotHandle snapshot, ref ThreadEntry32 entry);

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeSnapshotHandle()
            : base(ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(InvalidHandleValue);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
