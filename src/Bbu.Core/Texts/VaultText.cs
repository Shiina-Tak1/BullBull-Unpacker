using System.Collections.Generic;

namespace Bbu.Core.Texts;

/// <summary>
/// 密码层的用户可见文案。
///
/// 「【密码】」这一类占位符由调用方填，模板本身不进别的域：改措辞只改这一个文件。
/// </summary>
public static class VaultText
{
    /// <summary>密码来源，同时也是候选顺序。索引与 <c>PasswordOrigin</c> 的成员顺序一一对应。</summary>
    public static readonly IReadOnlyList<string> OriginLabels =
    [
        NameFromFile,
        OriginFromBook,
        OriginEmptyPassword,
        "手动输入",
    ];

    /// <summary>来源是密码本。界面上的密码本入口也用它，同一个词只有一处定义。</summary>
    public const string OriginFromBook = "密码本";

    /// <summary>空密码那一档的来源标签。密码快路那边也复用它，不再另写一份。</summary>
    public const string OriginEmptyPassword = "空密码";

    /// <summary>密码全试完，只可能是密码不对，值得问用户。</summary>
    public const string ProblemExhausted = "密码已全部试完";

    public const string ProblemTimeout = "验证超时，已停止";

    /// <summary>候选为空时的停止原因。</summary>
    public const string NoCandidates = "密码本为空，文件名中也未包含密码";

    /// <summary>空密码在展示时的写法。引擎那一路传的是空串。</summary>
    public const string EmptyPasswordShown = "（空密码）";

    /// <summary>命中结语。占位符依次是来源、密码、尝试次数。</summary>
    public const string HitSummary = "命中{0}：{1}（尝试 {2} 个）";

    /// <summary>密码不对时的结语。只在真的是密码不对时才用它。**只到「均不正确」为止**：
    /// 原先后面还接一句停止理由，而这一支里那句理由就是 <see cref="ProblemExhausted"/>（「密码已全部试完」），
    /// 与前半句是同一件事的两遍（裁「去掉重复的那半句」）。</summary>
    public const string MissedSummary = "已尝试 {0} 个密码，均不正确";

    /// <summary>不是密码问题时的兜底结语。</summary>
    public const string NotUnlocked = "未能解压";

    /// <summary>候选是从别的写法展开出来的。</summary>
    public const string VariantDetailSuffix = "，变体";

    /// <summary>密码本里的条目，成功过 n 次。占位符是次数。</summary>
    public const string BookHitDetail = "命中 {0} 次";

    /// <summary>心跳：这一层的包找了多久、进度多少。占位符依次是包名、秒数、已试数、总数。</summary>
    public const string SearchHeartbeat = "仍在尝试密码：{0} 已耗时 {1:F0} 秒，进度 {2}/{3}";

    /// <summary>开局报一句本次的候选数与线程数。占位符依次是候选数、线程数、原因后缀。</summary>
    public const string ParallelStarted = "正在尝试密码（{0} 个候选，{1} 线程）{2}";

    /// <summary>候选太少，改为依次尝试。占位符依次是候选数、原因。</summary>
    public const string SerialFallback = "{0} 个密码改为依次尝试（{1}）";

    public const string SerialBecauseConfigured = "设置里选了 1 路";

    public const string SerialBecauseNotCheap = "拿不到包信息 / 只能整包回退 / 单核";

    /// <summary>快路省了多少。只进日志文件，不上界面。</summary>
    public const string FastPathReport = "已排除 {0} 个不正确的密码，实际验证 {1} 个";

    /// <summary>整轮没走完就结束时补一句，免得数字看着像漏试。占位符依次是总数、实试数。</summary>
    public const string FastPathReportPartial = "{0}（整轮 {1} 个候选，只试了 {2} 个就结束）";

    /// <summary>整轮走完时的那半句。占位符是总数。</summary>
    public const string FastPathReportWhole = "{0}（整轮 {1} 个候选）";

    /// <summary>引擎报错时的结语。占位符依次是简述、最后一行输出。</summary>
    public const string EngineErrorSummary = "引擎报错（{0}）：{1}";

    /// <summary>缺分卷时的结语。占位符依次是那半句原因、最后一行输出。</summary>
    public const string MissingVolumeSummary = "分卷不全（不是密码问题）：{0}";

    /// <summary>候选密码命令行传不过去、这一轮划掉它时给出的交代。</summary>
    public const string NeedsManualSkipped = "部分候选密码无法通过命令行传入，本次跳过：该压缩包需要手动解压";

    /// <summary>整轮没试完时的结语：其中有候选传不过去。占位符是候选个数。</summary>
    public const string NeedsManualSummary = "密码没试完：其中 {0} 个候选传不过去，需要手动解压";

    /// <summary>分卷不全要凑齐再试。占位符是缺的那个文件名。</summary>
    public const string MissingVolumeAsk = "分卷不全：缺少 {0}。请将这一组分卷放入同一个文件夹后重试";

    /// <summary>引擎连包都列不出来，试密码没有意义。</summary>
    public const string EngineCannotRead = "引擎无法读取该压缩包（不是密码问题）：可能不是压缩包，或者文件已损坏";

    /// <summary>明确没加密，一个密码都不试。</summary>
    public const string NoPasswordNeeded = "无密码（" + Errors.ArchiveNotEncrypted + "）";

    /// <summary>密码成功次数写不进密码本。占位符是原因。</summary>
    public const string RememberWriteFailed = "密码命中次数写入失败：{0}（解压结果不受影响）";

    /// <summary>写者锁等满、锁文件建不出来、合并前读不出盘：三种「本次没保存」的原因文案。</summary>
    public const string LockBusy = "密码本正被占用，本次没保存";

    public const string LockUnusable = "密码本的锁文件创建失败（数据目录只读或权限不足），本次没保存";

    public const string BookUnreadableForMerge = "盘上的密码本无法读取，本次没保存（不覆盖整本）";

    /// <summary>密码本首行。界面靠这四个字认文件类型，不许改。</summary>
    public const string BookHeaderFirst = "# 密码本 —— 一行一个密码，工具按「成功次数」从多到少依次尝试（顺序：文件名 → 密码本 → 空密码）。";

    public const string BookHeaderSecond = "# 可以手动编辑；说明见随包的 README.html。";

    /// <summary>工具说明与用户备注的分界行。</summary>
    public const string NotesMarker = "# ==== 以上是工具说明（每次保存会重写）；以下可以写你自己的备注 ====";

    /// <summary>备注区与密码段的分界行。这一行以下每一行都是密码，`#` 开头的也是。</summary>
    public const string CommentMarker = "# ==== 密码从这里开始（这一行以下都是密码，# 开头的也算） ====";

    /// <summary>认「这是不是本程序密码本」的前缀。</summary>
    public const string BookHeaderPrefix = "# 密码本";

    /// <summary>密码本在数据目录里的文件名。用户拿记事本打开的就是它。</summary>
    public const string BookFileName = "密码本.txt";

    /// <summary>密码来源在文件名里。后面那个占位符是命中的关键词。</summary>
    public const string NameDetailAt = "文件名（{0}）";

    /// <summary>密码来源在文件名或文件夹名里，两个占位符是位置与关键词。</summary>
    public const string NamePlaceAt = "{0}（{1}）";

    /// <summary>密码来源在文件自己的名字里。</summary>
    public const string NameFromFile = "文件名";

    /// <summary>密码来源在文件自己的文件夹名里。</summary>
    public const string NameFromFolder = "文件夹名";

    /// <summary>扩展名后直接贴密码时，关键词那一栏的写法。占位符是扩展名。</summary>
    public const string NameExtensionSuffixTag = "后缀";

    /// <summary>扩展名后直接贴密码时，来源那一栏的写法。</summary>
    public const string NameFromExtensionSuffix = "文件名（扩展名后缀）";

    /// <summary>快路划掉一个候选时写进结果输出的那句话。</summary>
    public const string FastPathRejected = "进程内快路：密码肯定不对（没有起引擎）";
}
