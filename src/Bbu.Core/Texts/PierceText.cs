using System;
using Bbu.Core.Models;

namespace Bbu.Core.Texts;

/// <summary>
/// 穿透层的用户可见文案。
/// 取值相同的句子只定义一处，复用 <see cref="Errors"/> 与 <see cref="States"/>。
/// </summary>
public static class PierceText
{
    // ── 停止原因的文案。成员与 StopReason 一一对应，缺一个编译不过 ──

    public const string StopNoArchive = "文件夹里已没有可解压的压缩包";
    public const string StopAmbiguous = "有多个压缩包，无法确定先解压哪一个，已停止";
    public const string StopExtractFailed = "解压失败";
    public const string StopPasswordExhausted = "密码已全部试完，未找到正确密码";
    public const string StopPasswordSkipped = "已跳过该压缩包（未输入密码）";
    public const string StopMaxDepth = "已达到层数上限";
    public const string StopAlreadyVisited = "该压缩包之前已解压过";
    public const string StopOutputExists = "输出目录已存在，按设置跳过";
    public const string StopSearchIncomplete = "子文件夹扫描未完成";
    public const string StopEmptyOutput = "解压后没有产出任何内容";

    /// <summary>重名跳过那一句的括号后缀。三处共用，改一处即可。</summary>
    public const string ConflictSkipSuffix = "（重名处理设为「跳过」）";

    /// <summary>单文件入口碰到已存在的输出目录时的细节。占位符是被挡住的名字。</summary>
    public const string OutputExistsDetailAt = "输出目录已存在：{0}" + ConflictSkipSuffix;

    /// <summary>循环里碰到已存在的输出目录时的细节。占位符是包名。</summary>
    public const string OutputExistsBlockedAt = "{0}" + ConflictSkipSuffix;

    /// <summary>状态词里的「未完成」。完成与已跳过那三个复用状态表，不在这里重复。</summary>
    public const string StateUnfinished = "未完成";

    /// <summary>总结的第一段：最深包层加状态。占位符是层数与状态词。</summary>
    public const string SummaryHeadAt = "{0} 层，{1}";

    /// <summary>总结里补一句没解的数量。占位符是包数。</summary>
    public const string SummaryPartialCountAt = "部分完成（{0} 个包未能解压）";

    /// <summary>总结里接上停止原因。占位符是原因文案。</summary>
    public const string SummaryReasonAt = "：{0}";

    /// <summary>总结里接上细节。占位符是细节。</summary>
    public const string SummaryDetailAt = "（{0}）";

    // ── 密码来源。三档与 1.1.0 的字符串逐字一致 ──

    /// <summary>明确没加密，一个密码都没试。</summary>
    public const string OriginNoPassword = "无密码";

    /// <summary>这一层沿用了外层试成功的密码。</summary>
    public const string OriginReused = "沿用外层密码";

    /// <summary>命中的候选没有来源时的兜底写法。</summary>
    public const string OriginBook = VaultText.OriginFromBook;

    /// <summary>密码来源在候选自己身上。取密码表的第四档，不重复写那个字面量。</summary>
    public static string OriginManual => VaultText.OriginLabels[3];

    // ── 单层的日志句 ──

    /// <summary>这一层的包与输出目录。占位符依次是包名、文件夹名。</summary>
    public const string LayerStartedAt = "{0} 解压到 {1}/";

    /// <summary>这一层跑完。占位符是秒数。</summary>
    public const string LayerDoneAt = "完成（{0:F1} 秒）";

    public const string ManualPasswordAcceptedAt = "使用了手动输入的密码";

    /// <summary>这一层失败。占位符是原因。</summary>
    public const string LayerFailedAt = "失败：{0}";

    /// <summary>活动卡时间轴上那一处层号的写法（日志抬头那处见 LayerNoteAt）。取值与界面侧共用，不重写一遍。</summary>
    public const string LayerLabelAt = Ui.LayerAt;

    /// <summary>带层号的通用一行。占位符依次是层号、正文。</summary>
    public const string LayerNoteAt = "第 {0} 层 {1}";

    /// <summary>源包在开解之前已不存在。占位符是包名。</summary>
    public const string SourceGoneAt = SourceGone + "：{0}";

    /// <summary>「可能已被删除或移动」这个括注。共用一份，改一处即可。</summary>
    public const string GoneOrMoved = "（可能已被删除或移动）";

    /// <summary>源文件已不存在这一句本身。</summary>
    public const string SourceGone = "源文件已不存在" + GoneOrMoved;

    /// <summary>一次意外异常把这一趟停下。占位符是异常原文。</summary>
    public const string UnexpectedFailureAt = "处理该压缩包时出错，已停止：{0}";

    // ── 伪装包与切包 ──

    /// <summary>探测之后无法读取伪装文件的大小。占位符依次是文件名、原因。</summary>
    public const string CarrierSizeUnreadableAt = "无法读取这个伪装文件的大小，先不切：{0}（{1}）";

    /// <summary>切出内嵌包之前的那一句。占位符依次是文件名、偏移说明、格式名、体积。</summary>
    public const string CarrierDetectedAt =
        "检测到伪装文件：{0} 的 {1} 处有一个 {2}，先切出 {3} 再解压";

    /// <summary>切出内嵌包要多占的地方，低于设定下限就拒绝。占位符依次是体积、设定下限。</summary>
    public const string CarveNeedsSpaceAt = "切出内嵌包需要额外 {0}，可用磁盘空间将低于设定下限（{1:F2} GB）";

    /// <summary>切包失败。</summary>
    public const string CarveFailed = "切出内嵌包失败";

    /// <summary>临时切片用完就删。占位符是文件名。</summary>
    public const string TempDiscardedAt = "已清理临时文件：{0}";

    /// <summary>临时切片删不掉，只能说清它留在了原处。占位符依次是文件名、原因。</summary>
    public const string TempDiscardFailedAt = "临时文件删不掉，留在原处：{0}（{1}）";

    // ── 「删」字清理 ──

    /// <summary>这个文件夹无法读取，无法清理「删」字。占位符依次是文件夹名、原因。</summary>
    public const string CleanNamesDirUnreadableAt = "这个文件夹无法读取，「删」字未能清理：{0}（{1}）";

    /// <summary>清理「删」字成功。占位符依次是旧名、新名。</summary>
    public const string DeleteCharCleanedAt = "清理「删」字：{0} 改名为 {1}";

    /// <summary>改名会撞上同名文件，放弃。占位符是文件名。</summary>
    public const string RenameTargetExistsAt = "同名文件已存在，跳过重命名：{0}";

    /// <summary>改名失败。占位符依次是旧名、新名。</summary>
    public const string RenameFailedAt = "重命名失败：{0} 变为 {1}";

    /// <summary>按不处理的类型名单跳过。占位符依次是文件名、扩展名。</summary>
    public const string ExcludedSkippedAt = "跳过 {0}：.{1} 已设置为不处理";

    // ── 挑目标与排队 ──

    /// <summary>子文件夹里只有一个包，继续解压。占位符是相对路径。</summary>
    public const string SubdirSingleTargetAt = "子文件夹里只有一个包，继续解压：{0}";

    /// <summary>子文件夹里找到的包，备注里说明它在哪。占位符是相对路径。</summary>
    public const string SubdirPackageAt = "子文件夹里的包：{0}";

    /// <summary>本层那个伪装包的备注。</summary>
    public const string CarrierTargetNote = "伪装文件里内嵌的压缩包";

    /// <summary>往下找的保险丝烧了。占位符是文件夹数上限与文件数上限。</summary>
    public const string SubdirFuseAt = "扫描范围过大（超过 {0} 个文件夹或 {1} 个文件）";

    /// <summary>往下找时有子文件夹无法读取。占位符是无法读取的个数。</summary>
    public const string SubdirUnreadableAt = "有 {0} 个子文件夹无法读取（权限 / 路径过长 / 盘断开）";

    /// <summary>子文件夹里疑似藏了内嵌压缩包，如实报但不自动解。占位符依次是个数、名字、省略说明。</summary>
    public const string SubCarriersAt =
        "子文件夹里有 {0} 个文件疑似藏了内嵌压缩包（不会自动解）：{1}{2}";

    /// <summary>名单只列前几个。占位符是没列出来的个数。</summary>
    public const string ListMoreAt = " 等 {0} 个";

    /// <summary>这一层有多个包，会依次全解。占位符依次是个数、名字。</summary>
    public const string MultipleTargetsAt = "这里有 {0} 个压缩包，会依次全部解压：{1}";

    /// <summary>这一层有多个伪装包，无法确定先解压哪一个。占位符依次是总数、未列出说明、名字。</summary>
    public const string AmbiguousAt = "这一层共 {0} 个候选（都是伪装包）{1}，无法确定先解压哪一个，已停止：{2}";

    /// <summary>多解歧义的细节行。占位符依次是总数、未列出说明、名字。</summary>
    public const string AmbiguousDetailAt = "共 {0} 个候选{1}：{2}";

    /// <summary>候选太多，只说前几个。占位符是没列出来的个数。</summary>
    public const string AmbiguousMoreAt = "，另有 {0} 个未列出";

    /// <summary>没找完但已经找到候选，先把找到的解完。占位符是原因。</summary>
    public const string SearchIncompleteFirstAt = "{0}，先解压已找到的压缩包";

    /// <summary>层数上限挡住了这一层。占位符是上限。</summary>
    public const string MaxDepthHitAt = "已达到层数上限（{0} 层），这一层的压缩包不再往下解压";

    /// <summary>对账之后点名尚未解压的包。占位符依次是个数、名字。</summary>
    public const string LeftoverReportAt = "已达到层数上限，还有 {0} 个压缩包尚未解压，仍在输出目录里：{1}";

    /// <summary>队列里的包已不存在。占位符是文件名。</summary>
    public const string QueuedTargetGoneAt = "队列里的 {0} 已不存在" + GoneOrMoved;

    /// <summary>队列里的包已不存在，这一份未能解压。占位符是文件名。</summary>
    public const string QueuedTargetGoneDetailAt =
        "{0} 已不存在" + GoneOrMoved + "，这一份未能解压";

    /// <summary>本进程刚把它当中间包删掉，队列里那条是过期记录。占位符是文件名。</summary>
    public const string AlreadyRemovedAt = "已处理过，无需重复：{0}";

    /// <summary>拖进来的是次卷，改按同组的主卷解。占位符依次是拖进来的名字、主卷名字。</summary>
    public const string VolumeMainChosenAt = "{0} 是分卷的次卷，改按主卷解压：{1}";

    // ── 内容判重 ──

    /// <summary>与哪一个相同的那半句。占位符是那个包的相对路径。**不带「相同」二字**：
    /// 它后面紧接 <see cref="SameContent"/> 那一半，两半之间没有分隔符，各写一次会读成「与「X」相同内容相同」。</summary>
    public const string SameAsAt = "与「{0}」";

    /// <summary>逐字节确认过，确实同一份内容。</summary>
    public const string SameContent = "内容相同";

    /// <summary>逐字节确认不了，只能说疑似。</summary>
    public const string SameContentUnverified = "疑似内容相同（那个包已不存在，无法逐字节确认）";

    /// <summary>与先前的包相同的那半句。与上面那条配对使用，同样不带「相同」二字。</summary>
    public const string SameAsEarlier = "与先前的包";

    /// <summary>判重跳过之后那一段。占位符依次是前半句、原因、这一份自己的全路径。</summary>
    public const string DuplicateSkippedAt = "{0}{1}，这一份尚未解压（仍在 {2}）";

    /// <summary>判重命中、且那一份的内容**确实已经解出来过**：陈述事实，并指出内容所在的全路径。
    /// 占位符依次是前半句（<see cref="SameAsAt"/>）、原因（<see cref="SameContent"/>）、产物目录的**全路径**。</summary>
    /// <remarks>与上一条的区别是「有没有条目没解」：这一档不记残留，所以不写这一份自己的路径；级别也不是警告（B-2026-311）。</remarks>
    public const string SameContentSolvedAt = "{0}{1}，内容已在 {2} 解出，这一份尚未解压";

    // ── 解压前后 ──

    /// <summary>未加密，一个密码都不试。</summary>
    public const string NotEncryptedAt = "未加密，无需密码";

    public const string ReusedPasswordAt = "沿用外层密码成功";

    /// <summary>可用磁盘空间低于设定下限，尚未解压。占位符是原因。</summary>
    public const string NoSpaceAt = "可用磁盘空间低于设定下限，未解压：{0}";

    /// <summary>按该压缩包声明的体积判定解压后可用磁盘空间将低于设定下限。占位符依次是体积、设定下限。</summary>
    public const string DeclaredSpaceAt = "该压缩包解压后约 {0}，解压后可用磁盘空间将低于设定下限（{1:F2} GB）";

    /// <summary>判不出体积，退化成绝对下限。取值与引擎侧那句共用一处。</summary>
    public static string SpaceBelowFloor(double floor) => Fill(Format.SpaceBelowFloorAt, floor);

    /// <summary>切包起点落在文件范围之外。</summary>
    public const string CarveOffsetOutOfRange = "切包起点超出文件范围";

    public const string EmptyOutputAt = "解压引擎报告成功，但输出目录中没有新增内容";

    /// <summary>密码未能解压时的提示，值得再试一次那种。占位符依次是包名、输出目录。</summary>
    public const string NotUnlockedAskAgainAt = "未能解压：{0}（原文件仍在 {1}）。输入正确密码后可重新添加该文件单独解压";

    /// <summary>密码未能解压时的提示，不必再试那种。占位符依次是包名、原因、输出目录。</summary>
    public const string NotUnlockedWhyAt = "未能解压：{0} - {1}（文件仍在 {2}）";

    /// <summary>记住这个密码了。占位符是层号。</summary>
    public const string PasswordRememberedAt = "已记住此密码，下次优先尝试";

    /// <summary>事中磁盘中止那一段。占位符是设定下限。</summary>
    public const string SpaceGuardDetailAt =
        "解压中可用磁盘空间低于设定下限（当前可用 {0:F2} GB），已停止，解压后的文件可能不完整；"
        + "请检查「设置」里的「最低剩余空间」后重试";

    // ── 上提与收壳 ──

    /// <summary>整理多余的外层文件夹完成（上提与收壳**共一条**：两处记的是同一件事，走的是不同代码路径）。
    /// 占位符依次是文件夹名、连提说明。</summary>
    public const string LiftedAt = "已整理多余的外层文件夹，内容移入 {0}/{1}";

    /// <summary>连提了多层同名空壳。占位符是层数。**自带前导空格**：
    /// 它接在 `{0}/` 之后，不带空格时说明为空串会留下尾空格。</summary>
    public const string LiftedManyAt = " （连提 {0} 层同名空壳）";

    /// <summary>同名链太深，递归合并被一票否决。占位符依次是上限、外层名、内层名。</summary>
    public const string LiftFuseBlownAt = "同名文件夹嵌套太深（超过 {0} 层），没有整块搬：{1}/ 里还套着 {2}/，需要的话请手动拖出来";

    /// <summary>同名且当前策略不许覆盖。占位符依次是外层名、内层名。</summary>
    public const string LiftBlockedConflictAt = "没有整块搬：{0}/ 与 {1}/ 里有同名条目，当前策略不许覆盖，需要的话请手动搬";

    /// <summary>读写失败，搬移中断。占位符依次是外层名、内层名。</summary>
    public const string LiftBlockedIoAt = "搬移中断（读写失败）：{0}/ 里还套着 {1}/，需要的话请手动拖出来";

    /// <summary>半搬：中断之前已经搬走了哪些条目。占位符依次是项数、条目名清单。</summary>
    public const string LiftPartialAt = "已经搬走 {0} 项（它们在新位置，没搬的还在原处）：{1}";

    // ── 删包 ──

    /// <summary>中间包删掉了。占位符是文件名。</summary>
    public const string IntermediateRemovedAt = "已删除中间包：{0}";

    /// <summary>中间包删不掉。占位符依次是文件名、原因。</summary>
    public const string IntermediateRemoveFailedAt = "无法删除中间包：{0}（{1}）";

    public const string SourceRemovedAt = "已删除原压缩包：{0}";

    public const string SourceRemoveFailedAt = "无法删除原压缩包：{0}（{1}）";

    // ── 失败与取消的收尾 ──

    /// <summary>本次解压没有解出任何文件，空输出目录已经删掉。占位符是文件夹名。</summary>
    public const string VacantOutputRemovedAt = "本次没有解出任何文件，已删除空文件夹：{0}";

    /// <summary>取停止原因的文案。成员与 <see cref="StopReason"/> 一一对应，缺一个编译不过。</summary>
    public static string Label(StopReason reason) => reason switch
    {
        StopReason.NoArchive => StopNoArchive,
        StopReason.Ambiguous => StopAmbiguous,
        StopReason.ExtractFailed => StopExtractFailed,
        StopReason.PasswordExhausted => StopPasswordExhausted,
        StopReason.PasswordSkipped => StopPasswordSkipped,
        StopReason.MaxDepth => StopMaxDepth,
        StopReason.NoSpace => Errors.SpaceNotEnough,
        StopReason.AlreadyVisited => StopAlreadyVisited,
        StopReason.Cancelled => Errors.Stopped,
        StopReason.OutputExists => StopOutputExists,
        StopReason.SearchIncomplete => StopSearchIncomplete,
        StopReason.EmptyOutput => StopEmptyOutput,
        StopReason.SpaceGuard => Errors.SpaceGuardAborted,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>补模板的占位符。调用方不必各自引 <c>string.Format</c> 与不变文化。</summary>
    public static string Fill(string template, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
}
