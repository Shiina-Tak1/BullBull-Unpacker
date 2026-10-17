using System;
using System.IO;
using Bbu.Core.Logging;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>本机可用的引擎路径，<c>null</c> 表示没找到。</summary>
public sealed record EnginePaths(string? SevenZip, string? UnRar)
{
    public string? PathOf(EngineKind kind) =>
        kind is EngineKind.UnRar ? UnRar : SevenZip;

    public bool Has(EngineKind kind) => PathOf(kind) is not null;
}

/// <summary>
/// 引擎探测。**两个引擎都只有随包一处**：程序目录旁的 <c>engines\7z\7z.exe</c> 与
/// <c>engines\unrar\UnRAR.exe</c>，在场才算有这个引擎，缺席时如实报「缺少引擎」。
/// 两个引擎收在一个 <c>engines\</c> 下、各占一层（理由见 csproj）；不再探测用户机器的解压软件。
/// </summary>
public static class EngineLocator
{
    /// <summary>随包引擎的根目录名：两个引擎各占它下面的一层。</summary>
    public const string EnginesFolderName = "engines";

    public const string BundledFolderName = "7z";

    public const string SevenZipExeName = "7z.exe";

    /// <summary>随包 UnRAR 的目录名与文件名。照抄官方自解压包，文件名一个字母都不改。</summary>
    public const string UnRarFolderName = "unrar";

    public const string UnRarExeName = "UnRAR.exe";

    /// <summary>内置那份 7-Zip 的位置：程序目录旁的 <c>engines\7z\7z.exe</c>。</summary>
    public static string BundledSevenZipAt(string programDirectory) =>
        Path.Combine(programDirectory, EnginesFolderName, BundledFolderName, SevenZipExeName);

    /// <summary>随包 UnRAR 的位置：程序目录旁的 <c>engines\unrar\UnRAR.exe</c>。</summary>
    public static string BundledUnRarAt(string programDirectory) =>
        Path.Combine(programDirectory, EnginesFolderName, UnRarFolderName, UnRarExeName);

    /// <summary>
    /// 探测。两个引擎各判一次在场，没有第二处候选；发布包形态下还要与包内清单相符 ——
    /// 对不上的那一份按「不用它」处理，交给既有的缺引擎通道去报（B-2026-208）。
    /// </summary>
    public static EnginePaths Resolve(string programDirectory, IRunLog? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(programDirectory);

        var sevenZip = BundledSevenZipAt(programDirectory);
        var unRar = BundledUnRarAt(programDirectory);
        var sevenState = BundledIntegrity.Check(programDirectory, BundledIntegrity.SevenZipFiles, out var sevenOffender);
        var unRarState = BundledIntegrity.Check(programDirectory, BundledIntegrity.UnRarFiles, out var unRarOffender);

        if (sevenState == ComponentIntegrity.Mismatch)
        {
            BundledIntegrity.Report(sevenState, sevenOffender, log);
        }
        else if (unRarState == ComponentIntegrity.Mismatch)
        {
            BundledIntegrity.Report(unRarState, unRarOffender, log);
        }
        else if (sevenState == ComponentIntegrity.NoManifest)
        {
            BundledIntegrity.Report(sevenState, null, log);
        }

        return new EnginePaths(
            File.Exists(sevenZip) && sevenState != ComponentIntegrity.Mismatch ? sevenZip : null,
            File.Exists(unRar) && unRarState != ComponentIntegrity.Mismatch ? unRar : null);
    }
}
