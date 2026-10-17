using System;
using System.Runtime.InteropServices;
using Bbu.Core.Texts;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.Integration;

/// <summary>
/// 进程的完整性级别。同一个用户、同一段 P/Invoke、同一处键而写入结果不同时，
/// 差异只可能在进程令牌上（B-2026-159）。
/// </summary>
internal static class ProcessIntegrity
{
    private const int TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    /// <summary>完整性级别名。取不到就返回「未知」，不抛。</summary>
    internal static string Name()
    {
        var unknown = States.Unknown;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return unknown;
        }

        using (token)
        {
            var handle = token.DangerousGetHandle();
            _ = GetTokenInformation(handle, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0)
            {
                return unknown;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(handle, TokenIntegrityLevel, buffer, size, out _))
                {
                    return unknown;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                var count = GetSidSubAuthorityCount(label.Sid);
                if (count == IntPtr.Zero)
                {
                    return unknown;
                }

                var rid = Marshal.ReadInt32(GetSidSubAuthority(label.Sid, Marshal.ReadByte(count) - 1));
                return rid switch
                {
                    0x0000 => IntegrationText.IntegrityUntrusted,
                    0x1000 => IntegrationText.IntegrityLow,
                    0x2000 => IntegrationText.IntegrityMedium,
                    0x2100 => IntegrationText.IntegrityMediumPlus,
                    0x3000 => IntegrationText.IntegrityHigh,
                    0x4000 => IntegrationText.IntegritySystem,
                    _ => $"0x{rid:X}",
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int subAuthority);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
