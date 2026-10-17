using Bbu.Core.Models;

namespace Bbu.Core.Services;

/// <summary>
/// 按真实格式择引擎，不看扩展名（格式由 <see cref="Probe.DetectFormat"/> 按文件头判出）。
/// rar 与 rar5 走随包的 <c>UnRAR.exe</c>，其余（含判不出）走随包的 7-Zip。
/// 随包那份的格式覆盖面由 <c>7z.exe</c> 与 <c>7z.dll</c> 两份共同决定，两份都随包。
/// </summary>
public static class EngineRouter
{
    /// <summary>分派。判不出格式时走 7-Zip：它比 <c>UnRAR.exe</c> 覆盖的格式多。</summary>
    public static EngineKind KindFor(ArchiveFormat format) =>
        format is ArchiveFormat.Rar or ArchiveFormat.Rar5 ? EngineKind.UnRar : EngineKind.SevenZip;

    /// <summary>按文件头判格式再择引擎。调用方拿不到格式时走这一条，不许按扩展名猜。</summary>
    public static EngineKind KindForPath(string archive) => KindFor(Probe.DetectFormat(archive));
}
