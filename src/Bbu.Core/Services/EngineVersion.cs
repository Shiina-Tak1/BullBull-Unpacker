using System;
using System.Diagnostics;
using System.IO;

namespace Bbu.Core.Services;

/// <summary>
/// 从引擎二进制的版本资源里读版本号，供「关于」页回答"这一份 7-Zip 是哪一版"。
///
/// 不跑引擎进程、也不另维护一份随包清单：前者每次开关于页都要起一个进程，后者多一份要同步的名单，
/// 而版本资源里写的就是同一个答案。读不到就回空串，界面照实写「取不到」，不编一个数。
/// </summary>
public static class EngineVersion
{
    public static string Of(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var text = info.ProductVersion ?? info.FileVersion;
            return text?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }
}
