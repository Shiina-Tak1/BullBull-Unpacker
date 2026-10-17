namespace Bbu.Core.Texts;

/// <summary>带占位符的模板。占位符用 <c>{0}</c> 形式，配合 <c>string.Format</c>。</summary>
public static class Format
{
    public const string OutputDirFailedAt = "输出目录创建失败：{0}（{1}）";

    public const string CannotLaunchEngineAt = "无法启动引擎：{0}";

    /// <summary>缺引擎。占位符是引擎名字：`7-Zip` 或 `UnRAR`。</summary>
    public const string MissingEngineAt = "找不到引擎：{0}";

    /// <summary>一次引擎调用的成功结语，占位符是秒数。</summary>
    public const string EngineOkSeconds = "成功（{0:F1} 秒）";

    /// <summary>一次引擎调用的失败结语，占位符是退出码。</summary>
    public const string EngineFailedExitCode = "失败（退出码 {0}）";

    /// <summary>引擎报了警告退出码时的结语，占位符是退出码。它与上一条是两档，不许并成一句。</summary>
    public const string EngineWarningExitCodeAt = "解压不完整（引擎警告，退出码 {0}）";

    /// <summary>失败结论的组装：前半是中文归类，后半是引擎原话，两半都要在。</summary>
    public const string ConclusionWithOutputAt = "{0}：{1}";

    /// <summary>失败那一档、这个数又是本程序自己记的（负数段）时的写法：不许当成引擎的退出码让用户去查表。</summary>
    public const string LocalCodeNotExitCodeAt = "本程序自记的编号 {0}，不是引擎的退出码";

    public const string EnginePercentAt = "解压中（{0}%）";

    public const string SpaceGuardHitAt = "解压中可用磁盘空间低于设定下限（当前可用 {0:F2} GB），已停止";

    public const string SpaceWillFallBelowAt = "解压后可用磁盘空间将低于设定下限（{0:F2} GB）";

    public const string SpaceBelowFloorAt = "可用磁盘空间低于设定下限（{0:F2} GB）";

    /// <summary>弱证据的结论。占位符是归属（没加密 / 读不出来）。</summary>
    public const string WeakEvidenceIsCorruption = "{0}：CRC/校验和失败是数据损坏，不是密码问题";

    public const string UnknownExitCode = "未知退出码 {0}";

    public const string LogFileUnavailableAt = "日志文件无法打开：{0}（{1}）";

    public const string ForwardFailedAt = "转发给已有实例失败：{0}";

    /// <summary>单实例管道建不起来之后的退避。占位符是等待秒数与原因。</summary>
    public const string SingleInstanceRetryAt = "单实例管道创建失败，{0:F1} 秒后重试：{1}";
}
