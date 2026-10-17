namespace Bbu.Core.Texts;

/// <summary>
/// 日志文案。
/// 用户该知道的进 info（面板与日志文件都有），排错才看的进 debug（只进日志文件）。
/// </summary>
public static class LogText
{
    /// <summary>会话第一行。</summary>
    public const string SessionStarted = "启动";

    /// <summary>会话最后一行：正常退出时写。</summary>
    public const string SessionEnded = "会话结束";

    /// <summary>单条超过上限被截断，接在被截断那一行的行尾。</summary>
    public const string LineTruncated = "本条过长，已截断";

    /// <summary>未捕获异常那一行。占位符依次是钩子来源与异常原文（含堆栈）。</summary>
    public const string CrashAt = "未捕获异常（{0}）：{1}";

    /// <summary>句柄已关之后写入被拒的原因原文；它会显示在界面提示条上。</summary>
    public const string LogFileClosed = "日志文件已关闭";

    /// <summary>配置整份读不出来，这次用默认值。占位符是读失败的原因原文。</summary>
    public const string ConfigFallbackToDefaultsAt = "配置无法读取，本次使用默认值：{0}";

    /// <summary>第二个实例把自己的参数转发出去后就退出。</summary>
    public const string ForwardedToRunningInstance = "已有实例在运行，参数已转发";

    /// <summary>管道那一头不是本份程序：这条消息不进清单。载荷里自称的东西一律不看。</summary>
    public const string SingleInstanceForeignSender = "这条单实例消息不是本份程序发来的，已丢弃";

    /// <summary>要交出去的那一头不是本份程序：不交，自己开窗。排障时用来分清是哪一支。</summary>
    public const string ForwardTargetIsNotOurs = "管道另一端不是本份程序，自己开窗";

    /// <summary>对端是本份程序但没回执：按未递达处理，自己开窗。</summary>
    public const string ForwardNotConfirmed = "主实例没有回执，按未送达处理，自己开窗";

    /// <summary>空密码没法用 RAR 命令行表达（裸 `-p` 是「请提示输入密码」），改派 7-Zip。</summary>
    public const string EmptyPasswordSwitchToSevenZip = "空密码改用 7-Zip 解压";

    /// <summary>
    /// 用户点了暂停、子进程真的被挂起了。逐实例的排错证据，不进界面：
    /// 用户看得见的那一句由批级的 <see cref="BatchPaused"/> 出。
    /// </summary>
    public const string PauseSuspended = "已挂起引擎进程";

    /// <summary>暂停了，但子进程挂不住（非 Windows 或权限不足）。如实说。</summary>
    public const string PauseOnly = Ui.RowPaused;

    /// <summary>用户点了继续。</summary>
    public const string PauseResumed = "已继续";

    public const string DataDirPinIgnoredAt = "BBU_DATA_DIR 指定的数据目录创建失败，已忽略；数据目录为 {0}";

    public const string DataDirRoamingAt = "程序目录无法写入，数据目录改为用户目录 {0}";

    public const string DataDirFallbackAt = "用户目录创建失败，数据目录退回程序目录 {0}";

    /// <summary>随包组件与发布清单不符，这一份不用。占位符是那一份的相对路径（B-2026-208）。</summary>
    public const string ComponentDigestDiffersAt = "随包组件与发布清单不符，本次不使用：{0}";

    /// <summary>包内没有哈希清单：这次没核对，不当通过也不当失败（开发树与集成测试形态）。</summary>
    public const string ComponentCheckSkipped = "包内没有 SHA256SUMS，随包组件未核对";

    /// <summary>
    /// 用户点了暂停：批级只出一条。引擎那两条逐实例回执降在排错档，十几个引擎不会把面板刷满。
    /// </summary>
    public const string BatchPaused = "已暂停，不再开新任务";

    /// <summary>用户点了继续：批级只出一条，与 <see cref="BatchPaused"/> 成对。</summary>
    public const string BatchResumed = "已继续，接着往下做";
}
