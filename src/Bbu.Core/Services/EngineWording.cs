namespace Bbu.Core.Services;

/// <summary>
/// 引擎输出里的特征词。
/// 这一层官方文档零覆盖，而退出码 2 分不开密码错、头加密、非压缩包与不支持的编码，所以词表只能靠实测维护。
/// 每组出处写在那一组上。
/// </summary>
public static class EngineWording
{
    public const string CannotFindVolume = "cannot find volume";
    public const string NextVolume = "next volume";
    public const string MissingVolume = "missing volume";
    public const string SystemCannotFindFileCn = "找不到指定的文件";
    public const string SystemCannotFindFile = "the system cannot find the file";
    public const string NoSuchFile = "no such file";

    public const string UnsupportedMethod = "unsupported method";
    public const string IsNotArchive = "is not archive";

    // 下面三条是实测原话，各管一类，归类见 StructuralFailureHints。
    public const string CannotOpenAsArchive = "cannot open the file as archive";
    public const string IsNotRarArchive = "is not rar archive";
    public const string UnexpectedEndOfArchive = "unexpected end of archive";

    public const string WrongPassword = "wrong password";
    public const string IncorrectPassword = "incorrect password";
    public const string PasswordIsIncorrect = "password is incorrect";
    public const string CanNotOpenEncrypted = "can not open encrypted archive";
    public const string CannotOpenEncrypted = "cannot open encrypted archive";
    public const string EncryptedArchive = "encrypted archive";
    public const string WrongPasswordCn = "密码错误";
    public const string IncorrectPasswordCn = "错误的密码";

    public const string CrcFailed = "crc failed";
    public const string ChecksumError = "checksum error";

    public const string EnterPassword = "enter password";

    public const string NoFilesToProcess = "no files to process";
    public const string FilesZero = "files: 0";

    /// <summary>缺分卷一类，采信前要确认源包仍在。**本仓实测**：UnRAR 报 <c>Cannot find volume</c>。</summary>
    public static readonly string[] MissingVolumeHints =
    [
        CannotFindVolume,
        NextVolume,
        MissingVolume,
        SystemCannotFindFileCn,
        SystemCannotFindFile,
        NoSuchFile,
    ];

    /// <summary>
    /// 结构性失败：引擎明确说这个包读不了。采信它等于停整轮搜索，所以只收明确列出的那几句，泛化错误不在其内。
    /// **本仓实测**，官方零覆盖：7z 认格式读不动报 <c>Is not archive</c>、判不出格式报
    /// <c>Cannot open the file as archive</c>、编码不认得报 <c>Unsupported Method</c>、截断报
    /// <c>Unexpected end of archive</c>；UnRAR 对非 rar 文件报 <c>is not RAR archive</c>。
    /// </summary>
    public static readonly string[] StructuralFailureHints =
    [
        UnsupportedMethod,
        IsNotArchive,
        CannotOpenAsArchive,
        IsNotRarArchive,
        UnexpectedEndOfArchive,
    ];

    /// <summary>
    /// 同名条目挡住这一层：7z 要把挡路的现有目录换成包里的文件（或反过来）时创建不了，报这句并返回 2。
    /// **本仓实测**（随包 26.02 ZS）。
    /// </summary>
    public const string CannotDeleteOutputFolder = "cannot delete output folder";

    /// <summary>
    /// 同名条目挡住了这一层。只收 7z 这一句：UnRAR 的 <c>Cannot create</c> 对磁盘满、权限、路径非法同样成立，
    /// 收进来会把那几种误说成同名冲突。
    /// </summary>
    public static readonly string[] SameNameBlockedHints =
    [
        CannotDeleteOutputFolder,
    ];

    /// <summary>强证据：只会出现在「真加密加密码不对」的输出里，可单独定性。**本仓实测**，官方零覆盖。</summary>
    public static readonly string[] StrongWrongPasswordHints =
    [
        WrongPassword,
        IncorrectPassword,
        PasswordIsIncorrect,
        CanNotOpenEncrypted,
        CannotOpenEncrypted,
        EncryptedArchive,
        WrongPasswordCn,
        IncorrectPasswordCn,
    ];

    /// <summary>弱证据：单独出现只是「可能」，要再确认这个包确实加密了。**本仓实测**，官方零覆盖。</summary>
    public static readonly string[] WeakWrongPasswordHints =
    [
        CrcFailed,
        ChecksumError,
    ];

    /// <summary>没给密码却连目录都列不出来，说明文件名也是加密的。**本仓实测**，官方零覆盖。</summary>
    public static readonly string[] HeaderEncryptedHints =
    [
        CannotOpenEncrypted,
        EnterPassword,
    ];

    /// <summary>
    /// 条目名没匹配上的标志，7z 在这一档仍返回 0，只能看输出。**本仓实测**；官方 <c>t</c> 命令页
    /// 只给了对策（<c>-spd</c> 禁掉文件名通配）。UnRAR 的 <c>No files to extract</c> 刻意不收：
    /// 它的 <c>t</c> 在这一档不返回 0，收进来只会放宽判定面。
    /// </summary>
    public static readonly string[] NothingMatchedHints =
    [
        NoFilesToProcess,
        FilesZero,
    ];
}
