namespace Bbu.Core.Texts;

/// <summary>
/// 界面标签。用户能看到的字符串一律进本表，界面代码只引用常量；
/// XAML 用 <c>{x:Static}</c> 取，不写字面量——写了两处就会同时踩上「游离」与「重复」两条门禁。
/// </summary>
public static class Ui
{
    /// <summary>用户可见产品名。</summary>
    public const string ProductName = "BullBull Unpacker";

    /// <summary>版本号本身。占位符是版本号。关于页把它放在头部那一枚胶囊里，不另给「版本」二字。</summary>
    public const string VersionAt = "v{0}";

    // ── 窗口按钮 ──

    public const string Minimize = "最小化";

    public const string Maximize = "最大化";

    public const string Restore = "还原";

    public const string Close = "关闭";

    public const string About = "关于";

    // ── 顶部：进度构成条 ──

    /// <summary>清单还是空的。</summary>
    public const string ProgressWaiting = "等待加入文件";

    /// <summary>清单里有项，但没有一项可运行。它说的是「这些不用解」，不是「还没加文件」。</summary>
    public const string ProgressNoRunnable = "没有可解压的任务";

    /// <summary>构成条的读数。占位符依次是已完成项数与本批项数。</summary>
    public const string ProgressCountAt = "{0} / {1} 项";

    public const string LegendUnfinishedAt = "未完成 {0}";

    public const string LegendDoneAt = "完成 {0}";

    public const string LegendPartialAt = "部分 {0}";

    public const string LegendSkippedAt = "已跳过 {0}";

    /// <summary>图例里被用户点「停止」停下的那一档。它与「已跳过」分开，与状态列的词一个口径。</summary>
    public const string LegendStoppedAt = "已停止 {0}";

    public const string LegendFailedAt = "失败 {0}";

    // ── 工具条 ──

    public const string TaskCountAt = "{0} 个任务";

    public const string TaskCountMixedAt = "{0} 个任务，另有 {1} 项不会处理";

    public const string FilterAll = "全部";

    public const string FilterFailedOnly = "仅失败";

    public const string AddFiles = "添加文件";

    public const string AddFolder = "添加文件夹";

    public const string ClearList = "清空清单";

    public const string SelectAll = "全选";

    /// <summary>清单已全选时那个按钮换成它，再点一次即取消。</summary>
    public const string DeselectAll = "取消全选";

    public const string RemoveSelected = "移除选中";

    /// <summary>没选任何项就点了「移除选中」。</summary>
    public const string NothingSelected = "先选中要移除的项";

    // ── 任务表 ──

    public const string ColumnFile = "文件";

    public const string ColumnKind = "类型";

    public const string ColumnPassword = "密码";

    public const string ColumnStatus = "状态";

    /// <summary>表头右键菜单第一项：只把右键点中的那一列按内容自动调整宽度。</summary>
    public const string ColumnFitOne = "按内容自动调整本列宽度";

    public const string ColumnFitAll = "按内容自动调整全部列宽";

    /// <summary>列宽跑偏之后的出口：回到四列的默认宽度。</summary>
    public const string ColumnReset = "恢复默认列宽";

    public const string PasswordNone = "—";

    /// <summary>密码是空串。它和「没有密码」是两件事。同一句话在密码候选那里已经有一处定义。</summary>
    public const string PasswordEmpty = VaultText.EmptyPasswordShown;

    public const string CopyPasswordTip = "点击复制到剪贴板";

    /// <summary>复制成功后的日志行。占位符是密码原文。</summary>
    public const string PasswordCopiedAt = "已复制密码：{0}";

    /// <summary>子行在名字前的缩进记号，与 1.1.0 的清单一致。</summary>
    public const string ChildPrefix = "    └  ";

    public const string StatusWithSecondsAt = "{0}，{1}";

    public const string SecondsAt = "{0:F1} 秒";

    public const string MinutesAt = "{0} 分 {1:00} 秒";

    /// <summary>上限为零时不写上限，只写当前层。</summary>
    public const string LayerAt = "第 {0} 层";

    /// <summary>
    /// 层号与上限一起写。占位符依次是当前层与上限。
    /// 写法照 1.1.0 的 <c>layer_text()</c>：上限就是用户的层数预算，不是"这个包有几层"，
    /// 写成"共 N 层"会让用户以为分母是探明出来的。
    /// </summary>
    public const string LayerWithLimitAt = "第 {0} 层（上限 {1}）";

    /// <summary>
    /// 不会被执行的那些行的状态词：非压缩包、排除名单命中、文件夹汇总行。它们永远轮不到，写「排队中」会让用户一直等。
    /// </summary>
    public const string NotRunnableState = "不会处理";

    public const string ChildCountAt = "{0} 个子项";

    /// <summary>密码 chip 复制成功后的短暂替换，密码本的行内复制也用它。</summary>
    public const string Copied = "已复制";

    public const string PasswordSourceAt = "密码来源：{0}";

    // ── 日志栏 ──

    public const string LogTitle = "实时日志";

    public const string LogFollow = "跟随";

    public const string LogErrorsOnly = "仅错误";

    public const string LogClear = "清空日志";

    /// <summary>中档底部日志面板的展开入口。</summary>
    public const string LogExpand = "展开日志";

    /// <summary>同一块的收起入口。它与展开那一句成对出现，收起后只剩一行摘要。</summary>
    public const string LogCollapse = "收起日志";

    public const string LogBadgeIdle = "待开始";

    public const string LogBadgeRunning = "运行中";

    public const string LogBadgeDone = "已结束";

    public const string PreviousBatchNone = "尚未运行任何任务";

    /// <summary>运行信息格的标题；该格恒用此标题，不随内容切换为「上一批结果」（docx 21）。</summary>
    public const string ActivityTitle = "当前活动";

    /// <summary>清单为空时当前活动那一格的说明。</summary>
    public const string ActivityIdle = "加入文件后点「开始解压」";

    /// <summary>运行时还没收到第一条心跳，活动槽那一行先占着。</summary>
    public const string ActivityStarting = "准备中";

    /// <summary>活动槽里扫描清单那一句。占位符依次是已扫文件夹数、已扫文件数；扫描是枚举，没有分母可算。</summary>
    public const string ActivityScanningAt = "扫描中：文件夹 {0}、文件 {1}";

    /// <summary>活动槽里真实解压那一句。占位符是引擎报的百分比。</summary>
    public const string ActivityExtractAt = "解压中 {0}%";

    /// <summary>活动槽里枚举那一句的耗时前缀。</summary>
    public const string ActivityElapsedAt = "已耗时 {0}";

    public const string ActivityPasswordAt = "密码枚举 {0}/{1}";

    /// <summary>活动槽那一行的两段。占位符依次是枚举进度与耗时。</summary>
    public const string ActivityProgressAt = "{0}，{1}";

    public const string SecondsShortAt = SecondsAt;

    /// <summary>过了一分钟仍写秒，只是不带小数。</summary>
    public const string SecondsLongAt = "{0:F0} 秒";

    // ── 底栏 ──

    public const string Start = "开始解压";

    public const string Pause = "暂停";

    public const string Resume = "继续";

    public const string Stop = "停止";

    public const string OpenOutputDir = "打开输出目录";

    public const string OutputLabel = "输出";

    public const string OutputSameDir = "与原文件同文件夹";

    public const string OutputCustomDir = "指定文件夹";

    public const string ConflictLabel = "重名时";

    public const string ConflictRenumber = "自动添加数字序号";

    public const string ConflictOverwrite = "覆盖";

    /// <summary>从别的档切到「覆盖」并保存时那一次询问的标题。确认项沿用 <see cref="ConflictOverwrite"/>。</summary>
    public const string ConflictOverwriteAskTitle = "改成覆盖同名文件";

    public const string ConflictOverwriteAskBody =
        "输出目录已存在时改为直接解压到该目录：同名文件会被替换，替换后无法恢复；其它文件保留。";

    public const string ConflictSkip = "跳过";

    // ── 空状态 ──

    public const string EmptyTitle = "把压缩包或文件夹拖到这里";

    public const string EmptyOr = "或";

    public const string EmptyHint = "支持多种压缩格式、分卷压缩、视频伪装压缩包和多层嵌套解压";

    /// <summary>拖进窗口时盖在清单上的放置提示。</summary>
    public const string DropHint = "松开即加入清单";

    // ── 提示条 ──

    public const string ToastClearedList = "清单已清空";

    public const string ToastClearedLog = "日志已清空";

    public const string ToastNothingAdded = "没有可加入的项";

    /// <summary>加入清单的结果：加进来的都是文件。占位符是加进来的项数。</summary>
    public const string ToastAddedAt = "加入清单 {0} 项";

    /// <summary>加入清单的结果：加进来的是文件夹。两个占位符依次是文件夹个数与清单新增的行数。</summary>
    public const string ToastAddedFolderAt = "加入 {0} 个文件夹，清单新增 {1} 项";

    /// <summary>加入清单的结果：文件夹与文件混着加。三个占位符依次是加入项数、其中文件夹的个数与清单新增的行数。</summary>
    public const string ToastAddedMixedAt = "加入 {0} 项（其中 {1} 个文件夹），清单新增 {2} 项";

    /// <summary>已经在清单里的那一项。占位符是文件名。</summary>
    public const string ToastAlreadyInListAt = "已在清单里：{0}";

    public const string ToastNoOutputDir = "还没有输出目录";

    /// <summary>选了「指定文件夹」却没填路径时的那一句。两条出路都要写出来，否则用户只知道不给跑。</summary>
    public const string OutputDirMissing = "未填写输出目录，无法开始。请选择输出目录，或改回「与原文件同文件夹」";

    /// <summary>关窗时这一批没能在等待窗口内收尾。窗口留着，用户自己决定再点一次还是等下去。</summary>
    public const string CloseStopTimeout = "仍有任务未停止，窗口暂不关闭；请等它收尾，或再次确认停止";

    /// <summary>打开输出目录失败。占位符是原因。</summary>
    public const string ToastOpenOutputFailedAt = "无法打开输出目录：{0}";

    /// <summary>打开数据目录失败。占位符是原因。</summary>
    public const string ToastOpenDirFailedAt = "无法打开目录：{0}";

    /// <summary>打开仓库页失败。占位符是原因。</summary>
    public const string ToastOpenLinkFailedAt = "无法打开链接：{0}";

    /// <summary>日志文件写不进去。占位符是失败原因原文。</summary>
    public const string ToastLogUnavailableAt = "日志文件无法写入：{0}";

    /// <summary>运行中不接受追加与清空。</summary>
    public const string ToastBusy = "正在处理当前批次，请等它结束";

    /// <summary>扫描期点了「开始」：排队等这一趟扫完，按钮不禁用（裁定）。</summary>
    public const string ToastQueuedUntilScanDone = "已排队：扫描结束后自动开始";

    /// <summary>扫描被停掉时那一句。清单只可能是半份，漏项必须让用户看见。</summary>
    public const string ScanStoppedIncomplete = "扫描已停止，清单可能不全";

    /// <summary>「往下找」撞上保险丝时的询问标题。措辞与日志里那一条一致，同一件事不出现两种说法。</summary>
    public const string ScanFuseAskTitle = "扫描范围过大";

    /// <summary>保险丝询问的正文。占位符依次是已扫文件夹数与已扫文件数。</summary>
    public const string ScanFuseAskBodyAt = "已扫描 {0} 个文件夹、{1} 个文件，继续扫描可能需要较长时间。是否继续？";

    /// <summary>保险丝询问里「继续」那一个按钮。与主界面那颗同措辞，不另起一个字面量。</summary>
    public const string ScanFuseAskContinue = Resume;

    /// <summary>保险丝询问里否定那一个按钮：它不停这一趟，只不再往深处找，已找到的照解。取舍由正文说清，按钮与主界面同措辞。</summary>
    public const string ScanFuseAskCancel = Stop;

    /// <summary>空闲时暂停与停止不可用，原因写在这句里。</summary>
    public const string DisabledNotRunning = "还没有开始解压";

    /// <summary>已暂停时「暂停」不可用：那一刻可点的是最左的「继续」（docx 26）。</summary>
    public const string DisabledPaused = "已暂停，请点左侧的「继续」";

    public const string DisabledNothingAdded = "先加入压缩包或文件夹";

    /// <summary>运行中把文件拖到窗口上方时的放置提示：松手真的进清单，本批跑完接着处理。</summary>
    public const string DropHintQueued = "松开即加入清单，当前批次结束后接着处理";

    /// <summary>一批跑完、清单里还有排队的项，于是接着开下一批。占位符是待处理的项数。</summary>
    public const string QueueContinuesAt = "清单中还有 {0} 项待处理，接着处理下一批";

    /// <summary>跑完一批之后的落点提示。占位符依次是完成数、失败数、已跳过数与用户停止数。</summary>
    /// <remarks>
    /// 第一格是 <c>Count(summary, JobState.Done)</c>，与图例「完成 {0}」数的是同一个集合，
    /// 所以这里必须写「完成」—— 状态列与图例都用这个词，写成「成功」就是同一个数两个名。
    /// </remarks>
    public const string BatchFinishedAt = "本批结束：完成 {0} · 失败 {1} · 已跳过 {2} · 已停止 {3}";

    // ── 文件与文件夹选择 ──

    public const string PickFilesTitle = "选择压缩包";

    /// <summary>文件对话框里「所有文件」那一项。</summary>
    public const string PickFilesFilterAll = "所有文件";

    public const string PickFolderTitle = "选择文件夹";

    // ── 询问密码 ──

    public const string AskTitle = "需要密码";

    public const string AskArchiveLabel = "包：";

    public const string AskTriedLabel = "已尝试：";

    public const string AskInputPlaceholder = "输入解压密码";

    public const string AskVerify = "验证";

    public const string AskSkip = "跳过该压缩包（本次不再解压）";

    public const string AskVerifying = "正在验证…";

    public const string AskWrongPassword = "密码不正确，请重新输入";

    public const string AskVerifyFailedAt = "验证不了：{0}";

    public const string AskRememberNote = "验证成功后这一条会记进密码本";

    /// <summary>已经试过多少个。占位符是试过的个数。</summary>
    public const string AskTriedAt = "{0} 个";

    // ── 通用按钮 ──

    public const string Cancel = "取消";

    public const string Save = "保存";

    public const string Browse = "浏览…";

    public const string Open = "打开";

    public const string Clear = "清空";

    // ── 关于 ──

    /// <summary>关于页给 7-Zip 的那一行。</summary>
    public const string AboutSevenZipLabel = "7-Zip";

    public const string AboutUnrarLabel = "UnRAR";

    public const string AboutDataDirLabel = "数据目录";

    public const string AboutClose = "确定";

    public const string AboutEngineMissing = "未找到随包的 7-Zip（压缩包将无法解压，请重新安装完整版）";

    /// <summary>读不到版本资源时写这一句，不以推测值代替。</summary>
    public const string AboutVersionUnknown = "无法获取";

    /// <summary>随包 UnRAR 读不出文件版本资源时写这一句，口径与 7-Zip 那一行一致。</summary>
    public const string AboutUnrarMissing = "未找到随包的 UnRAR";

    /// <summary>关于页那两个入口按钮的文字。</summary>
    public const string AboutOpenStore = "微软商店";

    public const string AboutOpenRepo = "GitHub 仓库";

    public const string AboutCopyright = "Copyright (c) 2026 Tak1";

    /// <summary>版权与许可并成一行时中间那一枚分隔符（底部收成一行，不写中文标签）。</summary>
    public const string AboutMetaSeparator = "·";

    /// <summary>只写 SPDX 标识符（作者裁决：关于页不用中文）；它是一段可点的超链接，指向下面那条地址。</summary>
    public const string AboutLicense = "GPL-3.0-or-later";

    /// <summary>许可那一段的落点：GNU 官方的 GPLv3 全文页。</summary>
    public const string AboutLicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    /// <summary>公开仓地址：不带语言后缀那一个。视图与断言都读这一份。</summary>
    public const string RepoUrl = "https://github.com/Shiina-Tak1/BullBull-Unpacker";

    /// <summary>商店页的网页地址：商店那一格点开的就是它。</summary>
    public const string StoreUrl = "https://apps.microsoft.com/detail/9NJ532JX69VK";

    // ── 顶栏：整页入口 ──

    public const string PasswordBook = VaultText.OriginFromBook;

    public const string Settings = "设置";

    // ── 密码本页 ──

    public const string LibrarySearchPlaceholder = "搜索密码";

    public const string LibraryNewPlaceholder = "填一条新密码";

    /// <summary>页脚计数。占位符是当前条目数。</summary>
    public const string LibraryCountAt = "共 {0} 条";

    public const string LibraryAdd = "添加";

    public const string LibraryDelete = "删除";

    public const string LibraryImport = "批量导入";

    public const string LibraryEditExternal = "用系统默认程序打开";

    /// <summary>命中次数那一列的表头。表里只放数值，单位与口径由表头说。</summary>
    public const string LibraryColumnHits = "命中次数";

    /// <summary>行尾那个复制入口的提示与无障碍名，按钮本身只画图标。</summary>
    public const string LibraryCopy = "复制";

    /// <summary>底部的重新载入入口：在别的程序里改过密码本之后用它。</summary>
    public const string LibraryReload = "重新载入";

    /// <summary>标题行上的条目计数。占位符是条目数。</summary>
    public const string LibraryHeaderCountAt = "共 {0} 条密码";

    public const string LibraryEmpty = "密码本为空：解压时验证成功的密码会自动记到这里";

    public const string LibrarySearchEmpty = "没有匹配的密码";

    /// <summary>软上限提醒。</summary>
    public const string LibrarySoftWarnAt = "已超过 {0} 条，可能影响密码尝试速度";

    public const string LibraryAdded = "已加入密码本";

    /// <summary>这一条已经在密码本里。占位符是密码原文。</summary>
    public const string LibraryDuplicateAt = "已在密码本中：{0}";

    public const string LibraryNothingToAdd = "先填一条新密码";

    public const string LibraryNothingSelected = "先选中要删的项";

    /// <summary>写回密码本失败。占位符是原因。</summary>
    public const string LibraryWriteFailedAt = "写回密码本失败：{0}";

    /// <summary>批量导入的结果。占位符依次是导入数、重复数、空行数。</summary>
    public const string LibraryImportResultAt = "导入 {0} 条、重复 {1} 条、空行 {2} 行";

    /// <summary>
    /// 未导入任何条目。它与上面那句是两件事：一句文案只能有一个级别，而"未导入任何条目"该是提示色（级别归属总表第 4.1 节）。
    /// </summary>
    public const string LibraryImportNoneAt = "未导入任何条目：重复 {0} 条、空行 {1} 行";

    /// <summary>删除确认的正文，多条。占位符是条数。</summary>
    public const string LibraryDeleteManyAskAt = "要从密码本中删除这 {0} 条吗？删除后会写回磁盘上的密码本";

    /// <summary>删除确认的正文，单条。占位符是这条密码。</summary>
    public const string LibraryDeleteOneAskAt = "要从密码本中删除这一条吗：{0}";

    public const string LibraryDeleteAskTitle = "删除密码";

    /// <summary>删完的回报。占位符是删掉的条数。</summary>
    public const string LibraryDeletedAt = "已删除 {0} 条";

    /// <summary>
    /// 未删除任何条目。它与上面那句是两件事：一句文案只能有一个级别，而"未删除任何条目"该是失败色（级别归属总表第 4.1 节）。
    /// </summary>
    public const string LibraryDeletedNone = "未删除任何条目";

    public const string LibraryExternalFailedAt = "无法读取此密码本：{0}";

    public const string LibraryNoBookPath = "密码本尚未创建，请先添加一条密码";

    public const string LibraryPickImportTitle = "选择要导入的密码文件";

    public const string LibraryPickImportFilter = "文本文件";

    // ── 设置页 ──

    // 分组是这一页唯一的分类手段（没有左导航）；设置项也不带说明副标题，每一项只有标题与控件。

    public const string SettingsGroupExtract = "解压";

    /// <summary>右键菜单与数据目录那一组的标题（「系统」）。</summary>
    public const string SettingsGroupShell = "系统";

    public const string SettingsShell = "右键菜单";

    /// <summary>三处都注册了才算已注册。</summary>
    public const string SettingsShellRegistered = "已注册";

    public const string SettingsShellUnregistered = "未注册";

    public const string SettingsShellInstall = "安装到右键菜单";

    public const string SettingsShellReinstall = "重新安装";

    public const string SettingsShellRemove = "移除";

    /// <summary>打包形态下菜单开关的状态说明。绿色版说的是注册表三处键（已注册 / 未注册），两者不是一件事。</summary>
    public const string SettingsMenuOn = "已开启";

    public const string SettingsMenuOff = "未开启";

    /// <summary>打包形态下那一个切换按钮：开着时点它是「隐藏菜单」，关着时点它是「显示菜单」。</summary>
    public const string SettingsMenuHide = "隐藏菜单";

    public const string SettingsMenuShow = "显示菜单";

    /// <summary>装完的那一句反馈。占位符是写成功的位置数。</summary>
    public const string SettingsShellInstalledAt = "已注册到 {0} 处右键菜单";

    /// <summary>卸载完的那一句反馈。占位符是删掉的键数。</summary>
    public const string SettingsShellRemovedAt = "已从右键菜单移除 {0} 处";

    /// <summary>没写全时的反馈。占位符是失败的那一处（`LastFailure` 里已带错误码）。</summary>
    public const string SettingsShellFailedAt = "注册未完成：失败在 {0}";

    public const string SettingsRestoreDefaults = "恢复默认";

    public const string SettingsSaveFailedAt = "保存失败：{0}";

    /// <summary>保存失败那一句的组装入口（给调用点用、不是文案行）：中文归类接存储层原文，原文保留。</summary>
    public static string SettingsSaveFailed(string? lastError) => string.Format(
        System.Globalization.CultureInfo.CurrentCulture, SettingsSaveFailedAt, lastError ?? string.Empty);

    public const string SettingsSaved = "设置已保存";

    /// <summary>恢复默认那一句。它和别的项一样立刻落盘，所以范围要写明：本页以外的项不在这一项里。</summary>
    public const string SettingsRestoreSaved = "已恢复默认并保存（本页以外的「输出」「重名时」与列宽不变）";

    /// <summary>「未找到」。只给 <c>--where</c> 用：引擎全随包，设置页不再有检测标记。</summary>
    public const string EngineMissingNote = "未找到";

    /// <summary>线程数下拉的自动档。</summary>
    public const string SettingsWorkersAuto = "自动";

    /// <summary>线程数下拉的一项：只写数字，不重复「个线程」。</summary>
    public const string SettingsWorkersAt = "{0}";

    /// <summary>排除名单输入框的写法提示。它同时挂在框内的 Watermark 与标签提示上：框里永远有默认名单，Watermark 只在不填时露出来。</summary>
    public const string SettingsExcludedHint = "使用逗号、空格或分号分隔；只填写扩展名，无需星号。";

    /// <summary>数据目录分裂时那一句。占位符是另一侧的用户数据目录（本程序用的是上面那一处）。</summary>
    public const string SettingsDataDirSplitAt = "另一处还有一份密码本：{0}（当前使用上面这一份）";

    public const string SettingsMaxDepth = "最大嵌套层数";

    public const string SettingsTimeout = "单次解压超时（分钟）";

    public const string SettingsTimeoutHint = "每调用一次解压引擎算一次，不是整个任务的总时限；最小 1 分钟。";

    public const string SettingsMinFree = "最低剩余空间（GB）";

    public const string SettingsWorkers = "密码尝试线程数";

    public const string SettingsRemoveSource = "解压成功后删除原压缩包";

    public const string RemoveSourceAskBody =
        "开启后：每一层解压成功后即永久删除对应的压缩包，不进入回收站 —— 拖入文件夹时，该文件夹里（含子文件夹）找到的压缩包一并删除；"
        + "分卷压缩包整组删除。删除后无法恢复。";

    /// <summary>拨到开那一次询问的确认项。否定项照确认框壳子的「取消」。</summary>
    public const string RemoveSourceAskConfirm = "开启";

    public const string SettingsRemoveIntermediate = "删除解压产生的中间压缩包";

    public const string SettingsFlatten = "自动移除多余的文件夹层级";

    public const string SettingsFlattenHint = "解出来只有一个子文件夹时，把里面的内容移上来，避免一层套一层；同名文件夹会被合并。";

    public const string SettingsScanAppended = "识别并解压伪装的内嵌压缩包";

    public const string SettingsScanAppendedHint = "关掉后这类包不会被识别，也不会解压。";

    public const string SettingsFolderScan = "扫描文件夹时列出哪些包";

    public const string SettingsFolderScanHint = "只影响清单列什么；解压时仍会按包的内容往里找。";

    /// <summary>三档按「列出哪些包」作答，用文件夹不用「层」（「层」是包内嵌套的术语）。</summary>
    public const string SettingsFolderScanTopOnly = "当前文件夹";

    public const string SettingsFolderScanBelow = "含子文件夹（推荐）";

    public const string SettingsFolderScanDeep = "含子文件夹并识别内嵌压缩包（较慢）";

    public const string SettingsCleanDelete = "清理文件名中的「删」字";

    public const string SettingsExcluded = "不处理的文件类型";

    // ── 关窗确认 ──

    public const string CloseAskTitle = "还有任务在运行";

    /// <summary>关窗确认的正文。占位符是收尾宽限的秒数：点了之后真要等这么久，承诺与它同源。</summary>
    public const string CloseAskBodyAt =
        "现在关闭会中断正在解压的任务，写了一半的输出目录会留在磁盘上；最多等 {0} 秒收尾，收不完则窗口不会关闭。停止所有任务并退出吗？";

    public const string CloseAskStop = "停止所有任务并退出";

    public const string CloseCancelled = "已取消关闭，任务继续";

    /// <summary>整批暂停时，正在跑的那一行把状态词换成这句：状态机仍把它算「解压中」，但它这一刻停着。</summary>
    public const string RowPaused = "已暂停";
}
