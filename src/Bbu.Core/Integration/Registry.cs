using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

// 本类与 BCL 的注册表门面同名：同命名空间里的类型优先，走 BCL 那一侧必须带别名。
// 平台可用性：产品只发 win-x64，非 Windows 上 BCL 抛的 PlatformNotSupportedException 已被折成「不成」。
using Win32Registry = Microsoft.Win32.Registry;

#pragma warning disable CA1416

namespace Bbu.Core.Integration;

/// <summary>
/// 注册表的薄封装，只包本程序用到的动作：建键写值、读值、删一棵子树。
/// 走 BCL 的 <c>Microsoft.Win32.Registry</c>（ref 包里就有，不引包），建键请求的权限由它给；
/// 删键用 <c>DeleteSubKeyTree</c>。每个方法都返回成不成，不抛：被策略挡住是正常环境。
/// </summary>
internal static class Registry
{
    /// <summary>最后一次失败的 Win32 错误码（0 表示没失败过）。写入失败是静默的，靠它带出原因（B-2026-162）。</summary>
    internal static int LastError { get; private set; }

    /// <summary>最后一次失败的落点：键路径、值名与错误码。只有错误码还不足以定位是哪一步（B-2026-162）。</summary>
    internal static string LastFailure { get; private set; } = string.Empty;

    // 这一段是**诊断数据**不是文案，所以不用中文（用户看到的那句在 IntegrationText 里）。
    private static void RememberFailure(string subKey, string? name, int code)
    {
        LastError = code;
        LastFailure = $@"{subKey} value={name ?? "(default)"} code={code}";
    }

    /// <summary>Win32 码在异常 <c>HResult</c> 的低 16 位（0x80070005 即 5）；只认 HRESULT_FROM_WIN32 段，其余记 0。</summary>
    private static int Win32Code(Exception ex) =>
        (uint)ex.HResult is >= 0x80070000u and <= 0x8007FFFFu ? ex.HResult & 0xFFFF : 0;

    /// <summary>「环境不允许改注册表」那一类，一律折成不成（本类的契约是不抛）。</summary>
    private static bool IsRegistryFailure(Exception ex) =>
        ex is SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException
            or PlatformNotSupportedException
            or NotSupportedException
            or ArgumentException;

    /// <summary>建键（已存在就打开）并写一个字符串值。</summary>
    internal static bool TryWriteString(string subKey, string? name, string value)
    {
        if (!TryCreate(subKey, out var key))
        {
            return false;
        }

        using (key)
        {
            try
            {
                key.SetValue(name, value, RegistryValueKind.String);
                return true;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                RememberFailure(subKey, name, Win32Code(ex));
                return false;
            }
        }
    }

    /// <summary>读一个字符串值。键不在、值不在、类型不是字符串都算读不到。</summary>
    internal static bool TryReadString(string subKey, string? name, out string value)
    {
        value = string.Empty;
        if (!TryOpen(subKey, out var key))
        {
            return false;
        }

        using (key)
        {
            try
            {
                if (key.GetValueKind(name) != RegistryValueKind.String)
                {
                    return false;
                }

                if (key.GetValue(name, null) is not string text)
                {
                    return false;
                }

                value = text;
                return true;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                return false;
            }
        }
    }

    /// <summary>按字面试写一个键、立刻删掉，返回 Win32 错误码（0 表示成功）。不改 LastError/LastFailure。</summary>
    internal static int Probe(string subKey)
    {
        try
        {
            using var created = Win32Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            Win32Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            return 0;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Win32Code(ex);
        }
    }

    /// <summary>建键（已存在就打开）并写一个 DWORD 值。界面那个菜单显示开关用它。</summary>
    internal static bool TryWriteDword(string subKey, string name, int value)
    {
        if (!TryCreate(subKey, out var key))
        {
            return false;
        }

        using (key)
        {
            try
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
                return true;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                return false;
            }
        }
    }

    /// <summary>读一个 DWORD 值。键不在、值不在、类型不是 DWORD 都算读不到（调用方决定缺省）。</summary>
    internal static bool TryReadDword(string subKey, string name, out int value)
    {
        value = 0;
        if (!TryOpen(subKey, out var key))
        {
            return false;
        }

        using (key)
        {
            try
            {
                if (key.GetValueKind(name) != RegistryValueKind.DWord)
                {
                    return false;
                }

                if (key.GetValue(name, null) is not int raw)
                {
                    return false;
                }

                value = raw;
                return true;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 删一棵子树。返回"**真的删掉了**"：键本来就不在时返回 <c>false</c>，与 1.1.0 同口径
    /// （把"本来就不在"算进去会打印出「已卸载 9 处」， B-2026-116）。
    /// </summary>
    internal static bool TryDeleteTree(string subKey)
    {
        try
        {
            using (var existing = Win32Registry.CurrentUser.OpenSubKey(subKey, writable: false))
            {
                if (existing is null)
                {
                    return false;
                }
            }

            Win32Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    private static bool TryCreate(string subKey, out RegistryKey key)
    {
        try
        {
            var created = Win32Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            key = created!;
            return created is not null;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            key = null!;
            RememberFailure(subKey, null, Win32Code(ex));
            return false;
        }
    }

    private static bool TryOpen(string subKey, out RegistryKey key)
    {
        try
        {
            var opened = Win32Registry.CurrentUser.OpenSubKey(subKey, writable: false);
            key = opened!;
            return opened is not null;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            key = null!;
            return false;
        }
    }
}
