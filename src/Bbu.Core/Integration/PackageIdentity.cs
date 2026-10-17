using System;
using System.Runtime.InteropServices;

namespace Bbu.Core.Integration;

/// <summary>包身份探测：有包标识时（MSIX 与稀疏包）身份由包清单声明，任务栏分组与数据目录判定各走另一条路。</summary>
public static class PackageIdentity
{
    public const int NoPackage = 15700;

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

    public static bool IsPackaged
    {
        get
        {
            try
            {
                var length = 0u;
                return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
