namespace Bbu.Core.Logging;

/// <summary>
/// 进度行说的是哪一段事实。界面按它读，不解析正文；成员集合有穷举门禁
/// （<c> .ProgressStage_MustStayExhaustive</c>）：加成员会静默落进 else。
/// </summary>
public enum ProgressStage
{
    /// <summary>扫描清单：两个数分别是已扫目录数与已扫文件数，都是计数、没有分母。</summary>
    Scan,

    /// <summary>密码候选枚举，带「已试几个 / 共几个」。</summary>
    Search,

    /// <summary>解压一层，分辨率由引擎自己给。</summary>
    Extract,
}

/// <summary>
/// 一条进度的结构。界面按它渲染活动槽，不解析正文；<see cref="Tried"/> 与 <see cref="Total"/> 按阶段解释。
/// </summary>
public readonly record struct LogProgress(double Seconds, int Tried, int Total, ProgressStage Stage)
{
    /// <summary>解压一层：<paramref name="percent"/> 是引擎报的百分比。</summary>
    public static LogProgress Extracting(double seconds, int percent) =>
        new(seconds, percent, 100, ProgressStage.Extract);

    public static LogProgress Scanning(int directories, int files) =>
        new(0, directories, files, ProgressStage.Scan);
}
