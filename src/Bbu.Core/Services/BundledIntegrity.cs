using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Bbu.Core.Logging;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>随包组件的完整性结论。清单不在时给不出结论，与「对得上」不是一回事。</summary>
internal enum ComponentIntegrity
{
    NoManifest,

    Verified,

    Mismatch,
}

/// <summary>
/// 随包组件在加载前按清单核对：凭据是发布包里的 <c>SHA256SUMS</c>（包根），读取与解析只此一处。
/// </summary>
internal static class BundledIntegrity
{
    /// <summary>包内哈希清单的落点，相对程序目录。发布包与对齐后的商店包都只有这一份。</summary>
    internal const string ManifestRelative = "SHA256SUMS";

    /// <summary>7-Zip 那一组：被起进程的 exe 与本进程要加载的 dll。</summary>
    internal static readonly string[] SevenZipFiles = [@"engines\7z\7z.exe", @"engines\7z\7z.dll"];

    /// <summary>UnRAR 那一份。</summary>
    internal static readonly string[] UnRarFiles = [@"engines\unrar\UnRAR.exe"];

    /// <summary>外置引擎三份。快路加载 dll 之前按这一组核，任何一份不符就不用这一段。</summary>
    internal static readonly string[] EngineFiles = [.. SevenZipFiles, .. UnRarFiles];

    /// <summary>
    /// 核一组组件：清单不在回 <see cref="ComponentIntegrity.NoManifest"/>，对不上的第一份写进 <paramref name="offender"/>。
    /// </summary>
    internal static ComponentIntegrity Check(
        string programDirectory, IReadOnlyList<string> relativePaths, out string? offender)
    {
        offender = null;
        if (ReadManifest(programDirectory) is not { } recorded)
        {
            return ComponentIntegrity.NoManifest;
        }

        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(programDirectory, relative);
            if (!File.Exists(full)
                || !recorded.TryGetValue(relative, out var want)
                || !string.Equals(want, Digest(full), StringComparison.Ordinal))
            {
                offender = relative;
                return ComponentIntegrity.Mismatch;
            }
        }

        return ComponentIntegrity.Verified;
    }

    /// <summary>把结论交给日志出口：没接线时只留结论，调用方仍按结论处置。</summary>
    internal static void Report(ComponentIntegrity state, string? offender, IRunLog? log)
    {
        if (log is null || state == ComponentIntegrity.Verified)
        {
            return;
        }

        if (state == ComponentIntegrity.NoManifest)
        {
            log.Note(LogText.ComponentCheckSkipped);
            return;
        }

        log.Problem(LogText.ComponentDigestDiffersAt, offender);
    }

    /// <summary>
    /// 一份 dll 所在包的程序目录：只有 <c>&lt;包根&gt;\engines\7z\7z.dll</c> 这个形态才认。
    /// </summary>
    internal static string? PackageRootOf(string dllPath)
    {
        var engine = Path.GetDirectoryName(dllPath);
        var engines = engine is null ? null : Path.GetDirectoryName(engine);
        var root = engines is null ? null : Path.GetDirectoryName(engines);
        return root is not null
               && string.Equals(Path.GetFileName(engine), EngineLocator.BundledFolderName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(engines), EngineLocator.EnginesFolderName, StringComparison.OrdinalIgnoreCase)
            ? root
            : null;
    }

    /// <summary>清单：每行「SHA-256 小写十六进制 + 两个空格 + 相对包根的路径」，与 <c>sha256sum -c</c> 兼容。读不了按没有清单处置。</summary>
    private static Dictionary<string, string>? ReadManifest(string programDirectory)
    {
        var path = Path.Combine(programDirectory, ManifestRelative);
        if (!File.Exists(path))
        {
            return null;
        }

        var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var parts = line.TrimEnd('\r').Split("  ", 2, StringSplitOptions.None);
                if (parts.Length == 2 && parts[0].Length == 64)
                {
                    recorded[parts[1].Replace('/', '\\').Trim()] = parts[0];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return recorded;
    }

    /// <summary>这一份的哈希。读不了（被独占打开、权限不足）回 <c>null</c>，调用方按「对不上」处置。</summary>
    private static string? Digest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
