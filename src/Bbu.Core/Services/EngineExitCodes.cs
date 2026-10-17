using System.Collections.Generic;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 退出码语义的**唯一出处**。UnRAR 那份按官方表对齐，
/// 现行 17 项（0 至 15 加 255）。**码位数随版本长**：7.23 的表到 13 为止，7.30 多出 14 与 15。
/// 7z 与 rar 的码位不同，不得按「非零即失败」处理。逐码语义由码表驱动断言，改表忘改调用点会当场红。
/// </summary>
public static class EngineExitCodes
{
    /// <summary>引擎起不来（进程创建失败）。</summary>
    public const int CannotLaunchCode = -1;

    /// <summary>缺引擎文件。判据与「引擎起不来」分开：一个要装，一个要查权限。</summary>
    public const int EngineMissingCode = -2;

    /// <summary>条目名没匹配上：7z 在非 UTF-8 控制台下把包里的名字写成乱码，传回去匹配不到。</summary>
    public const int EntryNotMatchedCode = -3;

    /// <summary>输出目录建不出来。这一层解不了，不是进程崩掉。</summary>
    public const int OutDirFailedCode = -4;

    /// <summary>
    /// 被进程内快路划掉的候选：这一条没有起过引擎，所以拿不到引擎的退出码。
    /// 取一个引擎不会给的负数，与上面四个一起构成完整的负数码段。
    /// </summary>
    public const int FastPathRejectedCode = -9;

    /// <summary>RAR 的「密码错误」退出码，<c>RunAsync</c> 靠它判 <c>WrongPassword</c>。</summary>
    public const int RarWrongPasswordCode = 11;

    public static readonly IReadOnlyDictionary<int, string> SevenZip = new Dictionary<int, string>
    {
        [0] = Errors.ExitSuccess,
        [1] = Errors.ExitWarning,
        [2] = Errors.ExitFatalPassword,
        [7] = Errors.ExitCommandLine,
        [8] = Errors.ExitOutOfMemory,
        [255] = Errors.ExitInterrupted,
    };

    public static readonly IReadOnlyDictionary<int, string> UnRar = new Dictionary<int, string>
    {
        [0] = Errors.ExitSuccess,
        [1] = Errors.ExitWarning,
        [2] = Errors.ExitFatal,
        [3] = Errors.ExitCrc,
        [4] = Errors.ExitLocked,
        [5] = Errors.ExitWrite,
        [6] = Errors.ExitOpenFile,
        [7] = Errors.ExitCommandLine,
        [8] = Errors.ExitOutOfMemory,
        [9] = Errors.ExitCreateFile,
        [10] = Errors.ExitNoMatch,
        [11] = Errors.ExitPassword,
        [12] = Errors.ExitRead,
        [13] = Errors.ExitBadArchive,
        [14] = Errors.ExitDelete,
        [15] = Errors.ExitLimit,
        [255] = Errors.ExitInterrupted,
    };

    /// <summary>退出码 → 一句话释义。界面的失败归因从这里取词，不自己编。</summary>
    public static string Describe(int code, EngineKind kind)
    {
        switch (code)
        {
            case CannotLaunchCode:
                return Errors.CannotLaunchEngine;
            case EngineMissingCode:
                return Errors.EngineMissing;
            case EntryNotMatchedCode:
                return Errors.EntryNotMatched;
            case OutDirFailedCode:
                return Errors.OutputDirFailed;
            case FastPathRejectedCode:
                return Errors.ExitPassword;
            default:
                var table = kind is EngineKind.UnRar ? UnRar : SevenZip;
                return table.TryGetValue(code, out var text)
                    ? text
                    : string.Format(Format.UnknownExitCode, code);
        }
    }

    /// <summary>退出码 0 才算成功；1 是警告，既不算成功也不算失败。</summary>
    public static bool IsSuccess(int code) => code == 0;

    /// <summary>警告码：有东西没解全，但引擎没报错。这一档按「部分完成」收口，见 <see cref="EngineResult.Warned"/>。</summary>
    public static bool IsWarning(int code) => code == 1;
}
