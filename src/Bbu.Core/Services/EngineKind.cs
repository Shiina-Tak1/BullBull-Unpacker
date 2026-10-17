namespace Bbu.Core.Services;

/// <summary>
/// 本程序调用的两个外部引擎，谁处理哪种格式见 <see cref="EngineRouter"/>。成员名跟着**二进制名**走：
/// <see cref="UnRar"/> 指随包的 <c>UnRAR.exe</c>（2026-10-07 之前叫 <c>WinRar</c>，指机器上那份 <c>Rar.exe</c>）。
/// </summary>
public enum EngineKind
{
    SevenZip,

    UnRar,
}
