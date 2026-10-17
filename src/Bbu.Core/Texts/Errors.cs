namespace Bbu.Core.Texts;

/// <summary>
/// 失败归因文案。界面与命令行都从这里取词，不各自编一套。
/// </summary>
public static class Errors
{
    public const string CannotLaunchEngine = "无法启动引擎（进程创建失败）";

    /// <summary>找不到引擎文件。文案里不点名哪一个：两个引擎都随包，缺的可能是任意一份。</summary>
    public const string EngineMissing = "找不到引擎（随包的那一份不在预期位置）";

    public const string OutputDirFailed = "输出目录创建失败";

    /// <summary>条目名没匹配上。7z 在这种情况下仍返回 0，所以不能当密码通过。</summary>
    public const string EntryNotMatched = "条目名称不匹配";

    public const string EntryNotMatchedNotVerified = "条目名称不匹配，本次验证未通过";

    public const string Timeout = "解压超时";

    /// <summary>解压中磁盘余量跌破下限，被主动中止。与下面那句「还没开始就拒绝」必须分开。</summary>
    public const string SpaceGuardAborted = "解压中可用磁盘空间低于设定下限，已停止";

    /// <summary>解压前按包的声明体积判定空间不够，还没起过引擎。</summary>
    public const string SpaceNotEnough = "可用磁盘空间低于设定下限，已停止";

    /// <summary>弱证据（CRC / 校验和字样）的归属：这个包没加密。</summary>
    public const string ArchiveNotEncrypted = "该压缩包未加密";

    /// <summary>弱证据的归属：连目录都读不出来，判不了加不加密。</summary>
    public const string ArchiveUnreadable = "无法列出该压缩包的内容";

    public const string EngineCannotReadArchive = "引擎无法读取该压缩包";

    public const string EncryptedHeader = "该压缩包连文件名也加密了";

    /// <summary>特征词只说明「找不到文件」，源包被删时同样成立，所以措辞不断定是哪一种。</summary>
    public const string VolumeIncomplete = "分卷不全（也可能是找不到文件）";

    public const string ChecksumMismatch = "校验和不符";

    /// <summary>同名条目挡住了这一层：引擎要把挡路的目录换成包里的文件（或反过来）时创建不了。</summary>
    public const string SameNameBlocksTheLayer = "同名条目挡住了这一层，内容未能全部解出";

    public const string ExitSuccess = "成功";
    public const string ExitWarning = "警告";
    public const string ExitFatal = "致命错误";
    public const string ExitFatalPassword = "致命错误（密码错也走这个码）";
    public const string ExitCommandLine = "命令行错误";
    public const string ExitOutOfMemory = "内存不足";
    public const string ExitCrc = "CRC 错";
    public const string ExitPassword = "密码错误";
    public const string ExitInterrupted = "用户中断";

    /// <summary>
    /// 本表只收下列七条：官方表共 15 个码（出处见
    /// <c>EngineExitCodes.UnRar</c>）。缺分卷落在 6、掩码不匹配落在 10，两档真实会撞到。
    /// </summary>
    public const string ExitLocked = "压缩包被锁定";

    public const string ExitWrite = "写错误";

    public const string ExitOpenFile = "文件打开错误";

    public const string ExitCreateFile = "文件创建错误";

    public const string ExitNoMatch = "没有匹配的文件";

    public const string ExitRead = "读取错误";

    public const string ExitBadArchive = "坏归档";

    public const string ExitDelete = "文件删除错误";

    public const string ExitLimit = "超出用户设置的上限";

    public const string Stopped = "已停止";

    public const string ForwardFailed = "转发给已有实例失败";
}
