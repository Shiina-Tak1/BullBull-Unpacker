namespace Bbu.Core.Texts;

/// <summary>
/// 扫描与任务编排的文案：清单里每一行的类型与备注、跑批时的日志句、文件夹行的汇总。
/// </summary>
public static class ScanText
{
    // ── 清单行的类型列 ──

    /// <summary>排除名单里的类型仍然列出来，标明不处理。取值与识别层那句共用一处。</summary>
    public static string KindExcluded(string ext) => Fill(ProbeText.ExcludedAt, ext);

    /// <summary>文件夹汇总行带包数。占位符是包数。</summary>
    public const string KindFolderWithCountAt = "文件夹，内 {0} 个包";

    /// <summary>文件夹里有多少个在排除名单里。占位符是个数。</summary>
    public const string KindExcludedCountAt = "（{0} 个已排除）";

    /// <summary>伪装的文件头加完整包，引擎能直读。占位符是扩展名。</summary>
    public const string KindEngineDirectAt = "{0} 内嵌压缩包（引擎可直接读）";

    /// <summary>伪装包里有一个内嵌包。占位符依次是扩展名、格式名、偏移。</summary>
    public const string KindEmbeddedAt = "{0}内嵌 {1}，偏移 {2}";

    /// <summary>没有扩展名时的写法。</summary>
    public const string NoExtension = "无扩展名";

    // ── 清单类型列的短标签：不是格式的那几档 ──

    public const string ShortFolder = "文件夹";

    public const string ShortExcluded = "已排除";

    public const string ShortSplit = "分卷";

    public const string ShortEmbedded = "内嵌";

    public const string ShortDisguised = "伪装";

    public const string DisguisedAsVideo = "伪装成视频";

    public const string ContainsDeleteChar = "含「删」字";

    // ── 清单行的备注列 ──

    /// <summary>排除名单里的类型。占位符是扩展名。</summary>
    public const string NoteExcludedAt = ".{0} 在「设置」的「不处理的文件类型」里，不会解压";

    /// <summary>真的一个包都没有。</summary>
    public const string NoteNoArchive = "文件夹里没有可解压的压缩包";

    /// <summary>对子文件夹的扫描尚未完成。</summary>
    public const string NoteSearchIncomplete = "子文件夹扫描未完成（可能有压缩包未列出）";

    /// <summary>无法读取的文件未能判断是否为压缩包。占位符依次是名字、省略说明。</summary>
    public const string NoteUnreadableAt = "{0}{1}无法读取，未能判断是否为压缩包";

    /// <summary>子文件夹里疑似藏了内嵌压缩包。占位符依次是个数、名字、省略说明。取值与穿透层那句共用一处。</summary>
    public const string NoteSuspectedAt = PierceText.SubCarriersAt;

    /// <summary>既不是压缩包、名字也不像压缩包。取值与识别层那句共用一处。</summary>
    public static string NoteNotArchive => ProbeText.NotArchive;

    /// <summary>引擎能直读的伪装包。它不切包、不改动原文件。不点名引擎：两个引擎都可能答这一档。</summary>
    public const string NoteEngineDirect = "伪装的文件头 + 完整包：直接交给引擎读取，不切包、不改动原文件";

    /// <summary>内嵌压缩包的位置说明。占位符依次是扩展名、偏移说明、格式名。</summary>
    public const string NoteCarrierAt =
        "内嵌压缩包：在文件 {0} 的偏移 {1} 处发现一个 {2} 压缩包，解压时会先取出处理，不改动原文件";

    /// <summary>分卷只处理主卷。占位符是卷数。</summary>
    public const string NoteVolumeCountAt = "共 {0} 卷，只处理主卷";

    // ── 扫描日志 ──

    /// <summary>
    /// 清单里不可执行的项在日志里的那一句。占位符依次是项名、原因。
    /// 粒度与 1.1.0 一致：只对进清单的项说，不按被探测的每个文件说。
    /// </summary>
    public const string LogSkippedAt = "{0} - {1}";

    // ── 跑批日志 ──

    /// <summary>没有待处理的任务。</summary>
    public const string NothingToDo =
        "没有待处理的任务（已完成的项目不会重复解压；如需重新运行，请先清空清单或添加新文件）";

    /// <summary>一项开始。</summary>
    public const string ItemStartedAt = "开始：{0}";

    /// <summary>一项结束。占位符依次是项名、结论；级别只由颜色承载。</summary>
    public const string ItemFinishedAt = "结束：{0} - {1}";

    /// <summary>源文件在排队期间被删或移走。</summary>
    public const string SourceGoneWhileQueued = PierceText.SourceGone;

    /// <summary>一个包出意外，这一条标失败并接着跑下一个。</summary>
    public const string ItemFailedAt = "处理该压缩包时出错：{0}";

    /// <summary>意外错误的详情走日志文件。</summary>
    public const string DetailsInLogFile = "（详细错误已记录到日志文件）";

    /// <summary>跑批顶层异常那一行的组装入口（给调用点用、不是文案行）：中文归类接异常原文与日志去向。</summary>
    public static string ItemFailed(string detail) => Fill(ItemFailedAt, detail) + DetailsInLogFile;

    // ── 结果映射 ──

    /// <summary>有内容没解时的备注。占位符依次是个数、停止原因、前三个名字。</summary>
    public const string PartialNoteAt = "有 {0} 个包未能解压（{1}）：{2}";

    /// <summary>半搬时的备注：外层文件夹只整理了一半。占位符依次是项数、前三个名字。</summary>
    public const string PartialLiftNoteAt = "外层文件夹只整理了一半：已经搬走 {0} 项（{1}）";

    /// <summary>时间轴里一层的标签。取值与穿透层的层号抬头共用一处。</summary>
    public static string TimelineLayer(int depth) => Fill(PierceText.LayerLabelAt, depth);

    /// <summary>文件夹行的汇总。占位符依次是包数、各部分。</summary>
    public const string RollupPackCountAt = "{0} 个包";

    public const string RollupDoneAt = "{0} 成功";

    public const string RollupPartialAt = "{0} 部分完成";

    public const string RollupFailedAt = "{0} 失败";

    public const string RollupSkippedAt = "{0} 已跳过";

    public const string RollupStoppedAt = "{0} 已停止";

    /// <summary>逐层时间轴里那一层的结局标记。不复用穿透层的 ✔／✘：它是时间轴模型内部的结局值。</summary>
    public const string OutcomeDone = "done";

    public const string OutcomeFailed = "failed";

    /// <summary>补模板的占位符。调用方不必各自引 <c>string.Format</c> 与不变文化。</summary>
    /// <remarks>零实参＝成品正文直传：它可能带花括号（用户文件名、异常原文），原样返回，不再当模板解析一遍。</remarks>
    public static string Fill(string template, params object?[] args) =>
        args.Length == 0
            ? template
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
}
