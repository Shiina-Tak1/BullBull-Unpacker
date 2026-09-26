"""扫描 + 运行管道：UI 与 core 之间那一层。

为什么单独一层：

  * `scan()` 把「用户拖进来的东西」变成一张**可显示的清单**（探测结果、分卷折叠、
    文件夹里的包数），不碰任何解压；
  * `Runner` 负责逐个跑 `Piercer`，通过回调把「日志 / 单条状态变化 / 需要用户输密码」
    抛给上层，自己不认识 Qt。

这样这条管道能在没有界面的情况下完整测（`tools/smoke_pipeline.py`），
UI 崩了也不影响核心逻辑。
"""

from __future__ import annotations

import os
import sys
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable

from core import probe
from core.config import Config
from core.engine import Extractor
from core.pierce import PackLayer, PierceResult, Piercer, StopReason
from core.vault import PasswordVault, UnlockResult


class ItemStatus(str, Enum):
    QUEUED = "queued"
    RUNNING = "running"
    DONE = "done"
    FAILED = "failed"
    SKIPPED = "skipped"
    # 安全停下了，但**有内容确实没解**（重复包被指纹挡下 / 到层数上限 / 多个包不知道先解哪个）。
    # 为什么单独一档：这三种以前都算 `ok=True` → 界面显示「完成」，用户不会去查
    # （BUG-6）。它不是失败（没出错），也不是完成（有东西没解）。
    PARTIAL = "partial"

    @property
    def label(self) -> str:
        return {
            ItemStatus.QUEUED: "排队中",
            ItemStatus.RUNNING: "解压中",
            ItemStatus.DONE: "完成",
            ItemStatus.FAILED: "失败",
            ItemStatus.SKIPPED: "已跳过",
            ItemStatus.PARTIAL: "部分完成",
        }[self]


@dataclass
class ScanItem:
    """清单里的一行。"""

    path: str
    name: str
    kind: str
    status: ItemStatus = ItemStatus.QUEUED
    is_dir: bool = False
    indent: bool = False
    runnable: bool = True          # 文件夹汇总行不参与执行
    child_count: int = 0
    parent: int | None = None      # 汇总行的下标
    children: list[int] = field(default_factory=list)

    password: str = ""
    source: str = ""
    layer: int = 0
    max_layer: int = 0
    elapsed: float = 0.0
    note: str = ""
    output: str = ""                # 最深一层的产物目录
    output_top: str = ""            # 第一层的产物目录（"打开输出目录"要看的就是这里）
    timeline: list[tuple[str, str, str]] = field(default_factory=list)
    stop_reason: str = ""
    reason_detail: str = ""

    @property
    def progress(self) -> int:
        """层进度：第 N 层 / 最大层数。

        刻意不假装"字节进度"——引擎是黑盒，没有真实百分比可用，
        拿层数当进度至少是**真信息**，不会看着一直卡在 0% 或乱跳。
        """
        if self.status is not ItemStatus.RUNNING or not self.max_layer:
            return 0
        return max(1, min(99, int(self.layer / self.max_layer * 100)))

    @property
    def elapsed_text(self) -> str:
        if not self.elapsed:
            return ""
        if self.elapsed < 60:
            return f"{self.elapsed:.1f}s"
        m, s = divmod(int(self.elapsed), 60)
        return f"{m}m{s:02d}s"

    def reset(self) -> None:
        self.status = ItemStatus.QUEUED
        self.password = ""
        self.source = ""
        self.layer = 0
        self.elapsed = 0.0
        self.note = ""
        self.output = ""
        self.output_top = ""
        self.timeline = []
        self.stop_reason = ""
        self.reason_detail = ""


# --------------------------------------------------------------------------
# 扫描
# --------------------------------------------------------------------------


def describe_archive(
    path: str, *, group_is_split: bool = False, embedded: probe.Embedded | None = None
) -> str:
    """给一行生成「识别类型」文案。"""
    if embedded is not None:
        ext = probe.ext_of(path)
        head = f"{ext.upper()} " if ext else "无扩展名 "
        return f"📼 {head}内嵌 {embedded.fmt.value.upper()} · 偏移 {embedded.offset_text}"

    fmt = probe.detect_format(path)
    info = probe.classify_volume(path)
    parts = [probe.describe(fmt, info if group_is_split else None)]
    if probe.is_disguised(path):
        parts.append("伪装成视频")
    cleaned = probe.clean_delete_chars(os.path.basename(path))
    if cleaned != os.path.basename(path):
        parts.append("含「删」字")
    return " · ".join(parts)


def _carrier_note(path: str, embedded: probe.Embedded) -> str:
    ext = probe.ext_of(path) or "无扩展名"
    return (
        f"内嵌包：{ext} 文件里从 {embedded.offset_text} 处开始有一个 "
        f"{embedded.fmt.value.upper()}，解压时会先取出处理，不改动原文件"
    )


_engine_cache: list = []


def _probe_extractor():
    """scan() 偶尔要问引擎一句（"你能不能直接读它"）。懒建、整个进程复用。"""
    if not _engine_cache:
        try:
            from .engine import Extractor, find_engines

            _engine_cache.append(Extractor(find_engines()))
        except Exception:          # noqa: BLE001 - 问不了就当"读不了"，别让扫描崩
            _engine_cache.append(None)
    return _engine_cache[0]


def engine_can_read(path: str) -> bool:
    """引擎能不能把这个文件**直接**当压缩包读出来（不再自己切包）。

    用在 probe 认不出来的兜底上。实测样本
    `示例视频.mp4` = 36 字节假 MP4 头 + 一整个 ZIP，
    而包内偏移是**绝对**的（EOCD 里的 cd_off 直接等于中央目录的绝对位置），
    probe 的"反推起点"因此算出 base=0 被判非法；就算算出来了也**不能切包**
    ——切掉那 36 字节会让包内所有绝对偏移整体错位。

    问引擎只要 0.03s（`7z l -slt` 一次），却能把这类"头是假、身体是真包"的
    文件救回来：7z 自己会报 `Embedded Stub Size = 36` 然后照常读。

    ⚠ 判据本体在 `Extractor.can_read()` —— 穿透侧 `pierce.carve_source` 也用
    同一个方法（`B-2026-094`）；这里只是"借那个懒建的单例引擎问一句"的包装，
    **不要再把 `read_ok and entry_count` 抄一份**。
    """
    ex = _probe_extractor()
    return bool(ex is not None and ex.can_read(path))


def _unique_by_identity(paths: list[str]) -> list[str]:
    """按**物理身份**去重，保留首次出现的顺序（`B-2026-086`）。

    为什么不是"按路径"：硬链接 / 8.3 短名 / subst 盘符都能让同一个包有第二条路径，
    按路径去重看不出来 —— 界面会把它列成两行、各起一个 `Piercer` 各解一份，
    CLI 那条路则是"解一份 + 另一份报 already_visited/partial"（退出码 1，而那个包
    明明还在盘上）。手册 §5.3 早就要求"候选与已扫目录都按**物理身份**去重"
    （`B-2026-047`），以前只在 `probe.find_archives_below` 里实现了 —— 这里是漏实现。
    `probe._identity` 拿不到身份时自己会退回规范化路径，所以不必额外判空。
    """
    seen: set[tuple[int, int] | str] = set()
    out: list[str] = []
    for p in paths:
        key = probe._identity(p)
        if key not in seen:
            seen.add(key)
            out.append(p)
    return out


def _archives_in(
    directory: str, *, scan_appended: bool = True, recurse_unique: bool = True,
    unreadable: "list[str] | None" = None, cancel: "ShouldStop | None" = None,
    suspected: "list[str] | None" = None,
) -> tuple[list[str], dict[str, int], dict[str, probe.Embedded], set[str]]:
    """列出一个目录里的「待解压目标」（分卷只留主卷）+ 卷数 + 内嵌包信息 + 引擎直读集。

    `recurse_unique=True` 时**本层与子目录一视同仁**：本层的目标 + 子目录里找到的包
    **都列出来**（外层包自带目录是打包常态：`包裹A/包裹B/inner.zip` 相对工作目录是两级）。
    「哪个文件算一个包」与「往下找」都走 **`probe` 里那一份共享实现**（`archive_candidate`
    / `candidates_from` / `find_archives_below`）—— 扫描与穿透对"包在哪"必须给出一致的答案，
    否则界面列出来的和执行时解的不是同一批（B-2026-035：这里曾经自己写了一份 `one_level`，它按
    `classify_volume().kind is NONE` 过滤，于是**普通 `.zip`/`.rar` 被当成"分卷成员"整批漏掉**，
    而穿透侧看得见）。
    ⚠ `B-2026-060`：这里以前把"什么算一个包"自己又写了一遍（`detect_format().is_archive`），
    于是**少了**单文件分支那条"扩展名在 `ARCHIVE_EXTS` 里也算候选"的兜底 —— 同一个 `.cab`
    单独拖进来能解、放进文件夹被静默漏解。现在两边都调 `probe.archive_candidate()`。
    `unreadable` 是**出参**：读不了的文件（`CAND-001`）没法判断是不是包，收集起来由
    `scan()` 写进清单备注 —— 不许以"不是压缩包"的名义消失。
    ★ `suspected` 也是**出参**（`B-2026-076`）：子目录里**疑似藏了内嵌压缩包**的文件
    （`probe.find_archives_below` 的 `carriers`，廉价尾部定位收的）。它们**不在这里解**
    （扫不扫子目录里的伪装包是 `candidates_from()` 写明的代价取舍），但**必须报出来** ——
    一个字节没产出却给这个目录写「文件夹里没有可解压的压缩包」是谎话。
    ⚠ 2026-09-22（`B-2026-040`）之前这里还有一条"**本层一个目标都没有**才往下找"的条件 ——
    那正是"本层有包时子目录里的包被静默漏解"的另一半：穿透侧解不到、界面也列不出来。
    现在两边都不设这个条件。
    """
    try:
        files = [e.path for e in os.scandir(directory) if e.is_file()]
    except OSError:
        return [], {}, {}, set()

    groups = probe.group_volumes(files)
    counts = {os.path.normcase(g.main): g.count for g in groups}
    # 分卷只留主卷；**主卷不在场**就跳过 —— 与穿透侧同一份判据（B-2026-035）
    targets = probe.main_volumes(groups)
    embedded: dict[str, probe.Embedded] = {}
    engine_read: set[str] = set()
    for p in files:
        if probe.classify_volume(p).kind is not probe.VolKind.NONE:
            continue
        # ★ 「什么算一个包」走 `probe.archive_candidate()` 这**一份**判据（B-2026-060）：
        #   以前这里自己写的是 `detect_format(p).is_archive`，**少了单文件分支那条
        #   「扩展名在 ARCHIVE_EXTS 名单里也算候选」的兜底** —— 于是同一个 `.cab`
        #   单独拖进来能解、放进文件夹被静默漏解，还报「文件夹里没有可解压的压缩包」。
        #   顺带把 CAND-001 的"读不了 ≠ 不是压缩包"也接上（`why` 非空就记一笔）。
        ok, why = probe.archive_candidate(p)
        if why and unreadable is not None:
            unreadable.append(p)
        if ok:
            targets.append(p)
            continue
        # 头不是压缩包：可能是"垫了真视频、后面接压缩包"的那种。
        # ★ 判据走**唯一入口** `probe.inspect_carrier()`（`B-2026-097`）：与穿透侧
        #   （`pierce.carve_source` 处理目标 / `pierce._carrier_candidates` 选目标）、
        #   报备通道、`cli --probe` **同一份** —— 以前这里也是自己拼的一套组合，
        #   而且顺序是"先问引擎"，与穿透侧"先尾部定位"不一致：同一个"真视频 + 尾部
        #   追加 ZIP"的文件，界面会标成「7-Zip 可直接读」，而穿透实际会**先切包**
        #   （`carve_source` 尾部定位命中就切）。同一个入口之后两边口径一致。
        #   ⚠ 这里付得起引擎那一档（界面扫描是按**拖入的那批**跑、不是按整个目录里
        #   每个文件跑），深扫也保留（垫片伪装的整盘读由 `cancel` 之外的代价取舍管，
        #   与修复前一致）。
        if scan_appended:
            got = probe.inspect_carrier(p, engine_probe=engine_can_read, deep=True)
            if got.direct:
                # 包内偏移是绝对的 / 引擎自己会跳过假头 → 不切包，原样交给引擎
                targets.append(p)
                engine_read.add(os.path.normcase(p))
                continue
            if got.embedded is not None:
                targets.append(p)
                embedded[os.path.normcase(p)] = got.embedded

    # ★ 本层候选按**物理身份**去重（`B-2026-086`）：硬链接是同一个包的第二个名字，
    #   按路径去重看不出来 —— 界面会把它列成两行、各起一个 `Piercer` 各解一份
    #   （CLI 那条路是"解一份 + 另一份报 already_visited/partial"、退出码 1）。
    #   顺序保留首次出现的那个名字。
    targets = _unique_by_identity(targets)

    # ★ 本层与子目录**一视同仁**（2026-09-22，`B-2026-040`）：以前只在"本层一个目标都没有"
    #   时才往下找，于是**界面列出来的**和**穿透实际会解的**不是同一批 ——
    #   本层有包时子目录里的包既不在清单里、也不会被解（用户报的"显示完成、包还在原地"）。
    #   现在两边共用同一口径：找到的**都列出来**。
    # **没找完**（保险丝烧了 / 有子目录读不了）：要如实带给调用方，别丢掉（`B-2026-044`）。
    search_incomplete = False
    if recurse_unique:
        # ★ 取消钩子要**传下去**（`B-2026-081`）：穿透侧 `pierce.pick_target` 早就传了，
        #   扫描侧以前没传 —— 于是"往下找"这段遍历在界面扫描阶段打断不了（子目录多时
        #   界面就一直卡着）。`find_archives_below` 取消时只置 `cancelled`、**不置**
        #   `truncated`，所以下面那句 `search_incomplete = search.truncated` 天然是 False：
        #   取消**不是**"没找完"（`B-2026-045`），备注不会写成「子文件夹没往下找完」。
        search = probe.find_archives_below(directory, cancel=cancel)
        search_incomplete = search.truncated
        # 与子目录的候选合并时同样按**物理身份**去重（`B-2026-086`）：
        # `find_archives_below` 的 `found` 内部已经去过一次，但"本层那个"和
        # "子目录里那个"是不是同一个包，只有这里能判。
        known = {probe._identity(t) for t in targets}
        for p in search.found:
            key = probe._identity(p)
            if key not in known:
                known.add(key)
                targets.append(p)
        # ★ 子目录里**疑似藏了内嵌压缩包**的文件（`B-2026-076`）：不解，但要如实报。
        #   与"已经是候选的"（本层 + 子目录，按物理身份）分开 —— 那边会真去解。
        if suspected is not None:
            known_carrier = {probe._identity(t) for t in targets}
            for p in search.carriers:
                key = probe._identity(p)
                if key not in known_carrier:
                    known_carrier.add(key)
                    suspected.append(p)

    return targets, counts, embedded, engine_read, search_incomplete


def scan(paths: list[str], *, scan_appended: bool = True,
         exclude: "list[str] | None" = None,
         cancel: "ShouldStop | None" = None) -> list[ScanItem]:
    """把拖入的路径展开成清单。

    * 单个文件 → 一行（若拖进来的是 part2.rar 这种次卷，自动纠正到主卷）
    * 文件夹  → 一行汇总 + 每个压缩包一行（缩进），汇总行不参与执行
    * 文件夹里没有任何压缩包 → 仍然生成一行可执行项（交给穿透自己去找）

    **必须去重**：用户一次性拖入 part1~part4 时，每个分卷都会解析成同一个主卷，
    不去重就会生成 4 个一模一样的任务、把同一个包解 4 遍
    （实测：拖 4 个分卷 → 4 个「资源分卷.part1.rar」）。

    不可执行的行（`runnable=False`）只用来"把话说清楚"：比如拖进来一个真的
    mp4，就明确显示「不是压缩包，不会处理」，而不是让引擎去报一句"解压失败"。

    `cancel` 是"用户点了停止吗"的钩子（`B-2026-081`，与 `Runner` 用的是同一种
    `ShouldStop`）：命中时**不再展开后面的路径**，把已经扫出来的交回去 ——
    扫描里那两件慢事（问引擎加没加密、"垫片伪装"整盘读文件）以及 `find_archives_below`
    的整棵子树遍历都要能被它打断。⚠ 取消**不是**"没找完"：被它打断时清单备注
    不会写「子文件夹没往下找完」（`B-2026-045`）。
    """
    items: list[ScanItem] = []
    seen_dirs: set[tuple[int, int] | str] = set()
    seen_targets: set[tuple[int, int] | str] = set()

    def key(p: str) -> tuple[int, int] | str:
        """「是不是同一个东西」的键 = **物理身份**（`B-2026-086`）。

        拖进来的两条路径指向同一个目录 / 同一个包时（硬链接、8.3 短名、subst 盘符），
        按路径去重会把它当成两个 —— 清单上出现两行、各解一份。
        `probe._identity` 拿不到身份时自己会退回规范化路径。
        """
        return probe._identity(p)

    for raw in paths:
        if cancel is not None and cancel():
            # 用户点了「停止」：不再展开后面的路径（B-2026-081）。
            # ⚠ 这里 break 而不是"继续扫完再丢"：扫描的每一段都可能很慢
            #   （几 GB 的 mp4 整盘读），取消的全部意义就是**现在**停下。
            break
        path = os.path.abspath(raw)
        if os.path.isdir(path):
            if key(path) in seen_dirs:
                continue
            seen_dirs.add(key(path))

            # ★ `CAND-001`：读不了的文件没法判断是不是包 —— 收集起来写进备注，
            #   不许以"不是压缩包"的名义静默消失（以前就是这么漏的）。
            # ★ `B-2026-076`：子目录里疑似藏了内嵌压缩包的文件也要收集（不解，但要说）。
            unreadable: list[str] = []
            suspected: list[str] = []
            targets, counts, embedded, engine_read, search_incomplete = _archives_in(
                path, scan_appended=scan_appended, unreadable=unreadable,
                suspected=suspected, cancel=cancel
            )
            # 文件夹里的目标也要跟已加入的去重（同一个包既被单独拖入又在文件夹里）
            found_here = targets
            targets = [t for t in found_here if key(t) not in seen_targets]
            # ★ 「已经被前面的扫描收进清单了」要和「这里真的什么都没有」**分开**
            #   （`B-2026-091`）：拖 `[父目录, 子目录]` 时，子目录里的包已经被父项那一次
            #   递归扫描列进清单（`seen_targets`），这里因此被剔空 —— 以前**照样**给这个
            #   子目录生成一行「文件夹里没有可解压的压缩包」（与事实不符：包就在上面几行）
            #   而且 `runnable=True` → `Runner` 走目录入口再解一遍（叠上 `B-2026-082`：
            #   产物落回源目录，而 `output_root` 里已有一份 = 重复解压 + 污染源目录）。
            #   选 (a)：这种行**不生成** —— 它的信息量是零（包都在清单里），留着只会误导
            #   用户并让他白等一遍。**唯一例外**是这一行还承载着别处的扫描看不到的信息
            #   （"没往下找完" / "有文件读不了"：`_archives_in` 的 `unreadable` 只收本层
            #   文件，子目录里读不了的东西只有这里说得出）—— 那时保留一行，但
            #   `runnable=False`（不再重复执行），备注也**不许**再写那句与事实不符的话。
            swallowed_by_parent = bool(found_here) and not targets

            def unreadable_note() -> str:
                if not unreadable:
                    return ""
                names = "、".join(os.path.basename(p) for p in unreadable[:3])
                more = f" 等 {len(unreadable)} 个" if len(unreadable) > 3 else ""
                return f"{names}{more} 读不了，没能判断是不是压缩包"

            def suspected_note() -> str:
                """★ 子目录里疑似藏了内嵌压缩包的文件（`B-2026-076`）。

                以前这种情况没有任何一行会说出来，目录行照写「文件夹里没有可解压的
                压缩包」——而盘上那个文件里的内容一个字节都没解出来（报完成 = 谎话）。
                """
                if not suspected:
                    return ""
                names = "、".join(os.path.basename(p) for p in suspected[:3])
                more = f" 等 {len(suspected)} 个" if len(suspected) > 3 else ""
                return (f"子文件夹里有 {len(suspected)} 个文件疑似藏了内嵌压缩包"
                        f"（不会自动解）：{names}{more}")

            if not targets:
                if swallowed_by_parent:
                    bits = []
                    if search_incomplete:
                        bits.append("子文件夹没往下找完（可能有包没找出来）")
                    if suspected:
                        bits.append(suspected_note())
                    if unreadable:
                        bits.append(unreadable_note())
                    if bits:
                        items.append(
                            ScanItem(path=path, name=os.path.basename(path) or path,
                                     kind="📁 文件夹", is_dir=True, runnable=False,
                                     note="；".join(bits))
                        )
                    continue
                if any(i.path == path for i in items):
                    continue
                # ★ 备注三选一（不许把两件事混成一句，`R-13`）：没找完 / 有疑似内嵌包 /
                #   真的什么都没有。第三种才是唯一可以写"没有可解压的压缩包"的情形。
                bits = []
                if search_incomplete:
                    bits.append("子文件夹没往下找完（可能有包没找出来）")
                if suspected:
                    bits.append(suspected_note())
                if not bits:
                    bits.append("文件夹里没有可解压的压缩包")
                items.append(
                    ScanItem(path=path, name=os.path.basename(path) or path, kind="📁 文件夹",
                             is_dir=True,
                             note="；".join(bits))
                )
                if unreadable:
                    items[-1].note += "；" + unreadable_note()
                continue

            parent_idx = len(items)
            skipped = [t for t in targets if probe.excluded_ext(t, exclude)]
            parent = ScanItem(
                path=path,
                name=os.path.basename(path) or path,
                kind=(f"📁 文件夹 · 内 {len(targets)} 个包"
                      + (f"（{len(skipped)} 个已排除）" if skipped else "")),
                is_dir=True,
                runnable=False,
                child_count=len(targets),
                note="；".join(x for x in (suspected_note(), unreadable_note()) if x),
            )
            items.append(parent)

            for t in sorted(targets):
                seen_targets.add(key(t))
                idx = len(items)
                parent.children.append(idx)
                emb = embedded.get(os.path.normcase(t))
                direct = os.path.normcase(t) in engine_read
                split = counts.get(os.path.normcase(t), 1) > 1
                skip_ext = probe.excluded_ext(t, exclude)
                if skip_ext:
                    # 名单里的类型仍然列出来，但标明"不处理"——用户要的是看得见
                    # 为什么没解，而不是"我明明拖进去了它却没反应"
                    items.append(
                        ScanItem(
                            path=t,
                            name=os.path.basename(t),
                            kind=f"🚫 {skip_ext.upper()} 已在排除列表",
                            indent=True,
                            parent=parent_idx,
                            runnable=False,
                            note=f".{skip_ext} 在「设置 → 不处理的文件类型」里，不会解压",
                        )
                    )
                    continue
                if direct:
                    kind = f"📼 {(probe.ext_of(t) or '无扩展名').upper()} 内嵌压缩包（7-Zip 可直接读）"
                    note = "伪装的文件头 + 完整包：直接交给 7-Zip 读取原文件，不切包、不改动它"
                else:
                    kind = describe_archive(t, group_is_split=split, embedded=emb)
                    note = (_carrier_note(t, emb) if emb is not None
                            else (f"共 {counts[os.path.normcase(t)]} 卷，只处理主卷"
                                  if split else ""))
                items.append(
                    ScanItem(
                        path=t,
                        name=os.path.basename(t),
                        kind=kind,
                        indent=True,
                        parent=parent_idx,
                        note=note,
                    )
                )
            continue

        if not os.path.isfile(path):
            continue

        # 拖进来的可能是次卷：纠正到同目录的主卷
        parent_dir = os.path.dirname(path) or "."
        siblings = []
        try:
            siblings = [e.path for e in os.scandir(parent_dir) if e.is_file()]
        except OSError:
            pass
        main = probe.main_volume_of(path, siblings) or path

        if key(main) in seen_targets:
            continue          # 同一个包（或它的其他分卷）已经加过了
        seen_targets.add(key(main))

        counts = {os.path.normcase(g.main): g.count for g in probe.group_volumes(siblings)}
        is_archive = probe.detect_format(main).is_archive
        # ★ 「算不算一个包」的判据**只此一份**（`probe.archive_candidate`，B-2026-060）：
        #   它与文件夹分支共用，读不了时第二项给出一句人话（`CAND-001`）。
        candidate, why = probe.archive_candidate(main)
        skip_ext = probe.excluded_ext(main, exclude)
        if skip_ext:
            # 排除名单里的类型：列出来、说清楚、不执行（apk 就是 zip，引擎会照解）
            items.append(
                ScanItem(
                    path=main,
                    name=os.path.basename(main),
                    kind=f"🚫 {skip_ext.upper()} 已在排除列表",
                    runnable=False,
                    note=f".{skip_ext} 在「设置 → 不处理的文件类型」里，不会解压",
                )
            )
            continue
        emb = None
        engine_read = False
        if not is_archive and scan_appended:
            # ★ 判据走**唯一入口** `probe.inspect_carrier()`（`B-2026-098`）：与目录分支
            #   （`_archives_in`）、穿透侧（`carve_source` / `_carrier_candidates`）、
            #   报备通道（`suspected_carriers`）、`cli --probe` **同一份**。
            #   以前这里是**自己拼的一套**，而且**先问引擎**，两个后果：
            #     * 顺序与 `B-2026-094` 的约束相反：包离文件头 ≤8MB 时
            #       `engine_can_read` 也是 True，于是同一个「垫片 + 相对偏移包」
            #       单独拖进来判「7-Zip 可直接读 · 不切包」、放进文件夹判
            #       「内嵌 ZIP · 先切包」—— 界面文案与实际行为**相反**。`B-2026-097`
            #       修的就是这件事，但当时只改了 `_archives_in`，漏了单文件这一处
            #       （台账 ① 把两者写成一格，掩盖了这个漏网）。
            #     * 代价白付：引擎问询 0.06~0.13s/文件，而档 1「尾部目录定位」本来
            #       就能命中同一批文件。实测同一批 12 个文件：0.727s → 0.078s。
            #   ⚠ `deep=True` 保留：档 5 是"包藏在文件**中段**"的唯一兜底，不许省
            #     （档 4 只救得回"尾部追加"那一类）。
            #   唯一入口内部已经做了 `looks_like_carrier`（档 0），这里不必再问一遍。
            got = probe.inspect_carrier(main, engine_probe=engine_can_read, deep=True)
            if got.direct:
                # 包内偏移是绝对的 / 引擎自己会跳过假头 → 不切包，原样交给引擎
                engine_read = True
            else:
                emb = got.embedded

        if emb is not None:
            items.append(
                ScanItem(
                    path=main,
                    name=os.path.basename(main),
                    kind=describe_archive(main, embedded=emb),
                    note=_carrier_note(main, emb),
                )
            )
            continue

        if engine_read:
            ext = (probe.ext_of(main) or "无扩展名").upper()
            items.append(
                ScanItem(
                    path=main,
                    name=os.path.basename(main),
                    kind=f"📼 {ext} 内嵌压缩包（7-Zip 可直接读）",
                    note="伪装的文件头 + 完整包：直接交给 7-Zip 读取原文件，不切包、不改动它",
                )
            )
            continue

        if not candidate:
            # 既不是压缩包、名字也不像压缩包（真视频、文档之类）：
            # 列出来但标成不可执行。以前这种会进队列，然后引擎回一句
            # "Cannot open the file as archive"，看着像工具坏了。
            #
            # ★ 判据与文件夹分支**共用同一份**（`B-2026-060`）：以前这里是手写的一份
            #   （`is_archive` + `ext not in ARCHIVE_EXTS`），文件夹那边没有这条兜底 ——
            #   同一个 `.cab` 单独拖进来能解、放进文件夹被静默漏解。现在两边都问
            #   `probe.archive_candidate()`。
            # ★ `why` 非空 = **读不了 / 保留设备名**（`CAND-001`）：那**不是**"不是压缩包"，
            #   必须把原因原样写出来，不许用一句"不是压缩包，跳过"盖住。
            items.append(
                ScanItem(
                    path=main,
                    name=os.path.basename(main),
                    kind=probe.describe(probe.detect_format(main)),
                    runnable=False,
                    note=why or "不是压缩包，跳过",
                )
            )
            continue

        items.append(
            ScanItem(
                path=main,
                name=os.path.basename(main),
                kind=describe_archive(main, group_is_split=counts.get(os.path.normcase(main), 1) > 1),
                note=(f"共 {counts[os.path.normcase(main)]} 卷，只处理主卷"
                      if counts.get(os.path.normcase(main), 1) > 1 else ""),
            )
        )

    return items


# --------------------------------------------------------------------------
# 运行
# --------------------------------------------------------------------------

OnLog = Callable[[str], None]
OnItem = Callable[[ScanItem], None]
AskPassword = Callable[[str, UnlockResult], "str | None"]
ShouldStop = Callable[[], bool]


@dataclass
class RunnerHooks:
    on_log: OnLog | None = None
    on_item: OnItem | None = None
    ask_password: AskPassword | None = None


def needs_run(item: ScanItem) -> bool:
    """这一项要不要跑？

    * `queued`（新加的、上次没轮到）→ 要
    * `failed`（上次失败了，比如那会儿还没把密码记进来）→ 要，重试是用户点"开始"的常见意图
    * `done` / `skipped` → **不要**：跑完的重复解一遍只会生成一堆 (1)(2)(3) 目录，
      而且如果原包已经被删（remove_source），重跑就是一片"失败"（用户报的
      "前面的任务执行完之后新加的直接失败"里就有这一层）
    * `partial`（部分完成）→ **也不要**，而且这一条是**实测**出来的：重跑同一个外层包
      时，新的 `Piercer` 是干净的 `_visited`，但同一次运行里那个"先解的那份"会**先**
      登记指纹，第二份照样被挡下 —— 也就是说**重试永远解不出那第二份**
      （实测：全新 Piercer 重跑 → 仍然 `ok=True`/`already_visited`、产物里还是只剩一份）。
      给用户一个"重试"按钮却永远修不好，比不给更糟。要看那个包，得单独把它拖进来解。
    """
    return item.status in (ItemStatus.QUEUED, ItemStatus.FAILED)


class Runner:
    """顺序执行清单里的可运行项。"""

    def __init__(
        self,
        items: list[ScanItem],
        *,
        vault: PasswordVault,
        extractor: Extractor,
        config: Config | None = None,
        hooks: RunnerHooks | None = None,
        should_stop: ShouldStop | None = None,
        should_pause: ShouldStop | None = None,
    ) -> None:
        # ★ 这一批就是"点开始时清单里那些要跑的"，**快照**下来：
        #   跑的过程中用户又拖了新文件进来（MainPage.tasks 会被 extend），
        #   当前这批绝不能顺手把它们也跑了——新进来的一项在界面线程里才刚建好行，
        #   配置/输出目录都是按"这一批"算的，混进来只会失败得莫名其妙。
        #   新项留在清单里 = 排队，等这批结束用户再点开始（那时是全新的一批）。
        self.items = list(items)
        self._batch = [it for it in self.items if it.runnable and needs_run(it)]
        self.vault = vault
        self.ex = extractor
        self.cfg = config or Config()
        self.hooks = hooks or RunnerHooks()
        self.should_stop = should_stop or (lambda: False)
        self.should_pause = should_pause or (lambda: False)

    # -- 回调 ----------------------------------------------------------

    def _log(self, msg: str) -> None:
        if self.hooks.on_log:
            self.hooks.on_log(msg)

    def _emit(self, item: ScanItem) -> None:
        if self.hooks.on_item:
            self.hooks.on_item(item)

    def _ask(self, archive: str, un: UnlockResult) -> str | None:
        if self.hooks.ask_password is None:
            return None
        return self.hooks.ask_password(archive, un)

    # -- 主流程 --------------------------------------------------------

    def run(self) -> list[ScanItem]:
        # 把「要不要掐断当前子进程」挂到引擎上：这样点停止能立刻杀掉正在跑的
        # 7z/Rar，而不是等它自己跑完（可能几十分钟）
        self.ex.cancel = self.should_stop
        # 暂停也挂在引擎上：当前这一条会被真的挂起（不是"跑完再停"）
        self.ex.pause = self.should_pause
        try:
            leaves = self._batch
            if not leaves:
                self._log("没有待处理的任务（已完成的不会重复解压；如需重跑，请先清空列表或添加新文件）")
            for item in leaves:
                # 暂停期间不开新任务（正在跑的那条由引擎层挂起）
                while self.should_pause() and not self.should_stop():
                    time.sleep(0.1)
                if self.should_stop():
                    item.status = ItemStatus.SKIPPED
                    item.note = "已停止"
                    self._emit(item)
                    continue
                # 源文件在排队期间被删掉/移走（上一批可能把临时切片、内层包删了）：
                # 直接说清楚，别让引擎回一句"退出码 2：系统找不到指定的文件"
                if not os.path.exists(item.path):
                    item.status = ItemStatus.FAILED
                    item.note = "源文件不在了（可能已被上一批删除或移动）"
                    self._log(f"✘ 结束：{item.name} — {item.note}")
                    self._emit(item)
                    continue
                # ★ 一个包出意外（异常、编码、磁盘抽风…）**绝不能带走整批**：
                #   以前异常会一路冒到 JobWorker.run 的兜底 except，那一批就此结束，
                #   后面排队的任务全都停在那儿等用户再点一次开始（用户报过这个）。
                #   现在这一条标失败并写清原因，接着跑下一个。
                try:
                    self._run_one(item)
                except Exception as exc:                  # noqa: BLE001
                    item.status = ItemStatus.FAILED
                    item.note = f"处理这个包时出错：{exc}"
                    self._log(f"✘ 结束：{item.name} — {item.note}")
                    try:
                        import traceback

                        self._log("（详细错误已记录到日志文件）")
                        # ★ 走**已有的**调试通道（`Extractor.debug_logger` → `run.log` 里那条
                        #   带 `[详细]` 前缀的行），不再裸 `print` 到 stdout（2026-09-24，`TASK-060`）：
                        #   那条 print **没有开关**，在打包版里被 `_Tee` 抄进 `ui.log`、
                        #   在命令行里糊在用户脸上，界面上却看不到 —— 同一件事两条出口。
                        _text = (f"[Runner] {item.path} 处理失败：\n"
                                 + traceback.format_exc())
                        _dbg = getattr(self.ex, "debug_logger", None)
                        if callable(_dbg):
                            _dbg(_text)
                        elif sys.stderr is not None:
                            sys.stderr.write(_text)      # 没有调试通道（纯 core 调用）时兜底
                    except Exception:                     # noqa: BLE001
                        pass
                    self._emit(item)
            self._rollup()
        finally:
            self.ex.cancel = None
            self.ex.pause = None
        return self.items

    def _run_one(self, item: ScanItem) -> None:
        item.reset()
        item.max_layer = self.cfg.max_depth
        item.status = ItemStatus.RUNNING
        self._emit(item)
        self._log(f"▶ 开始：{item.name}")

        started = time.monotonic()
        piercer = Piercer(
            self.ex,
            self.vault,
            max_depth=self.cfg.max_depth,
            min_free_gb=self.cfg.min_free_gb,
            flatten_single_child=self.cfg.flatten,
            remove_source=self.cfg.remove_source,
            remove_intermediate=self.cfg.remove_intermediate,
            scan_appended=self.cfg.scan_appended,
            workers=int(getattr(self.cfg, "workers", 0) or 0),
            exclude_exts=list(getattr(self.cfg, "exclude_exts", ()) or ()),
            output_root=(self.cfg.output_dir.strip()
                         if self.cfg.output_mode == "custom" and self.cfg.output_dir.strip()
                         else None),
            conflict=self.cfg.conflict,
            logger=self._log,
            # ★ 只有**真的存在**询问通道时才把回调传下去（B-2026-015）。
            #   `Piercer` 判断"有没有问过用户"用的是 `if un.worth_asking_user and
            #   self.ask_password:`，而 `self._ask` 这个包装**永远不是 None**
            #   （没有回调时它只会返回 None）。直接传下去，Piercer 就会以为
            #   "问过了、用户没给"，于是把「密码本试完、又没有弹窗通道」
            #   说成「你跳过了这个包（没输入密码）」—— 真正的问题被这句话盖住。
            #   这里传 None，Piercer 才会如实给出 PASSWORD_EXHAUSTED。
            ask_password=(self._ask if self.hooks.ask_password is not None else None),
            on_layer=lambda depth, _name, _done: self._on_layer(item, depth),
        )
        res = piercer.run(item.path)
        item.elapsed = time.monotonic() - started
        self._apply(item, res)
        self._emit(item)

    def _on_layer(self, item: ScanItem, depth: PackLayer) -> None:
        """某层开始/结束时刷新这一行，界面就能看到真实层进度。"""
        item.max_layer = self.cfg.max_depth
        if depth > item.layer:
            item.layer = depth
        self._emit(item)

    def _apply(self, item: ScanItem, res: PierceResult) -> None:
        item.output = res.output_dir
        # 第一层的产物目录才是"这次解压的根"。只记最深那层的话，
        # 完成页的「打开输出目录」会把用户丢进最后一个嵌套小目录里。
        item.output_top = res.layers[0].outdir if res.layers else res.output_dir
        item.stop_reason = res.stop_reason.label
        item.reason_detail = res.stop_detail
        # 层深用 `deepest_layer`（已解各层里最大的 depth），**不是** `max_depth_reached`
        # —— 后者是"解压动作次数"，同层多包时会大于层深，界面就会出现
        # "第 4 层 / 共 2 层"这种自相矛盾（B-2026-016）
        # ★ 单调不减：运行中 `_on_layer` 已经报过的层号，结束后**不许退回**。
        #   正常情况两者相等 —— `_record_layer` 是 `res.layers.append` 的唯一入口，
        #   层号事件与它配对（B-2026-036 修的就是这里断过的那一处）。
        #   取 max 是把这个不变量变成**构造上不可能破**：不配对时也只是"少报"，
        #   不会出现"运行中显示第 3 层、结束后退回第 2 层"。
        item.layer = max(item.layer, res.deepest_layer)
        item.max_layer = self.cfg.max_depth
        item.timeline = [
            (
                f"第{ly.depth}层",
                os.path.basename(ly.target),
                "done" if ly.ok else "failed",
            )
            for ly in res.layers
        ]

        # 密码来源取第一个真正用上密码的层（LayerResult 直接带回密码值，
        # 不再从密码库里旁路查——那样既耦合又容易拿错）
        for ly in res.layers:
            if ly.password:
                item.source = ly.password_origin or item.source
                item.password = ly.password
                break
        if res.layers and all(ly.password_origin == "无密码" for ly in res.layers):
            item.source = "无密码"

        # ★ 判定顺序：**先判「有没有做完」，再判「为什么停」** —— 这几个分支的先后
        #   本身就是这里唯一的逻辑，三个位置都踩过坑：
        #   1) `res.partial` 必须在 `res.ok` 前面：`partial` 时 `ok` 也是 True（安全停下），
        #      先判 ok 就会一路显示"完成"（BUG-6，见 smoke_pipeline 的 partial_state 组）；
        #   2) `CANCELLED` 也必须在 `res.ok` 前面：`is_clean_stop` 把"安全停下"当成
        #      "做完了"，所以取消后 `ok=True` —— 用户点了「停止」，界面却是绿色「完成」、
        #      汇总还记一笔"成功 1"，而产物只有百分之几（B-2026-014，High；外部测试
        #      第 2 轮在真实 GUI 路径上 3/3 复现）；
        #   3) `OUTPUT_EXISTS` 同理：输出目录已存在、按设置跳过时那个包根本没解开，
        #      界面却是「完成」+ 空备注（B-2026-017）。
        #   ⚠ 不要去改 `PierceResult.ok` 的语义：它表示"安全停下"，改成"真的完成"
        #   会让 MAX_DEPTH / AMBIGUOUS 掉进下面的 `else` 变成「失败」，还会把
        #   「打开输出目录」剔掉 —— 2026-09-21 修 BUG-6 时已经验证过这条路走不通。
        if res.partial:
            item.status = ItemStatus.PARTIAL
            if res.leftover:
                n = len(res.leftover)
                item.note = (f"有 {n} 个包没解开（{res.stop_reason.label}）："
                             f"{'、'.join(res.leftover[:3])}")
            else:
                # 没找完那种（SEARCH_INCOMPLETE）可能一个候选都没找到，理由由 label 说
                item.note = res.stop_reason.label
        elif res.stop_reason is StopReason.CANCELLED:
            item.status = ItemStatus.SKIPPED
            item.note = "已停止"
        elif res.stop_reason is StopReason.OUTPUT_EXISTS:
            item.status = ItemStatus.SKIPPED
            item.note = res.stop_detail or res.stop_reason.label
        elif res.ok:
            item.status = ItemStatus.DONE
        elif res.stop_reason is StopReason.PASSWORD_EXHAUSTED:
            item.status = ItemStatus.SKIPPED
            item.note = res.stop_detail or res.stop_reason.label
        elif res.stop_reason is StopReason.PASSWORD_SKIPPED:
            # 用户自己在弹窗里点了「跳过当前文件」：这是他主动的选择，不是出错，
            # 状态列按"已跳过"显示，备注写清是"你跳过了"，别和"密码试完了"混为一谈
            item.status = ItemStatus.SKIPPED
            item.note = res.stop_detail or res.stop_reason.label
        else:
            item.status = ItemStatus.FAILED
            item.note = res.stop_detail or res.stop_reason.label

        mark = {"done": "✔", "failed": "✘", "skipped": "⚠",
                "partial": "◑"}.get(item.status.value, "·")
        self._log(f"{mark} 结束：{item.name} — {res.summary()}")

    def _rollup(self) -> None:
        """把子项状态汇总回文件夹行。"""
        for idx, item in enumerate(self.items):
            if item.runnable or not item.children:
                continue
            kids = [self.items[i] for i in item.children if i < len(self.items)]
            done = sum(1 for k in kids if k.status is ItemStatus.DONE)
            failed = sum(1 for k in kids if k.status is ItemStatus.FAILED)
            skipped = sum(1 for k in kids if k.status is ItemStatus.SKIPPED)
            partial = sum(1 for k in kids if k.status is ItemStatus.PARTIAL)
            if any(k.status is ItemStatus.RUNNING for k in kids):
                item.status = ItemStatus.RUNNING
            elif done == len(kids):
                item.status = ItemStatus.DONE
            elif failed == len(kids):
                item.status = ItemStatus.FAILED
            elif partial and not failed:
                # 子项里有"部分完成"、没有失败 → 文件夹行也标部分完成，
                # 别一路显示"完成"把没解开的包盖过去
                item.status = ItemStatus.PARTIAL
            else:
                item.status = ItemStatus.DONE if done else ItemStatus.FAILED
            bits = [f"{len(kids)} 个包"]
            if done:
                bits.append(f"{done} 成功")
            if partial:
                bits.append(f"{partial} 部分完成")
            if failed:
                bits.append(f"{failed} 失败")
            if skipped:
                bits.append(f"{skipped} 跳过")
            item.note = " · ".join(bits)
            item.layer = max((k.layer for k in kids), default=0)
            item.elapsed = sum(k.elapsed for k in kids)
            self._emit(item)


def summarize(items: list[ScanItem]) -> dict[str, int]:
    """给计数卡用。

    ⚠ `counts` 的键必须**覆盖 `ItemStatus` 的每一个成员** —— 这里是无保护的
    下标累加（`counts[it.status.value] += 1`），漏一个键就是 **KeyError 崩掉整批**，
    不是断言红。加新状态时**先改这里**。
    """
    counts = {"queued": 0, "running": 0, "done": 0, "failed": 0, "skipped": 0,
              "partial": 0}
    for it in items:
        if it.runnable:
            counts[it.status.value] += 1
    return counts


# --------------------------------------------------------------------------
# 「这次解压到哪去了」——给「打开输出目录」用
# --------------------------------------------------------------------------
#
# 这两件事必须在 core 里算完再交给界面：来源可能有多个（拖了不同目录的包，
# 或者一次拖进来一堆文件夹），产物可能散在好几个地方，界面只该负责"把结论画成
# 一个按钮"，不该自己去拼路径。


def output_dirs(items: list[ScanItem]) -> list[str]:
    """这次跑成功的任务各自的第一层产物目录（去重、按任务顺序、只留真实存在的）。

    只取**第一层**：`output` 是套娃最深那层的目录，用户要的是"这次解压的根"。

    `PARTIAL`（部分完成）也算 —— 它**确实产出了内容**，用户最需要的就是
    "打开目录看看那个没解开的包长什么样"，把它排除掉等于把唯一的线索藏起来。
    """
    out: list[str] = []
    seen: set[str] = set()
    for it in items:
        if it.status not in (ItemStatus.DONE, ItemStatus.PARTIAL):
            continue
        d = it.output_top or it.output
        if not d:
            continue
        full = os.path.abspath(d)
        if not os.path.isdir(full):
            continue
        k = os.path.normcase(full)
        if k in seen:
            continue
        seen.add(k)
        out.append(full)
    return out


def output_root(dirs: list[str]) -> str:
    """一批输出目录的公共上级；没有意义时返回 ""。

    什么时候"没有意义"：

      * 只有一个目录——它自己就是根，不用再往上叠一层；
      * 跨盘符（`commonpath` 直接抛 ValueError）；
      * 公共上级只剩盘符根（`D:\\`）——来源分散在整个盘上时会出现，
        打开它等于什么都没打开。

    返回 "" 时界面就退化成"逐条列出让你选"，不会假装有一个统一的地方。
    """
    if len(dirs) < 2:
        return ""
    try:
        common = os.path.commonpath([os.path.abspath(d) for d in dirs])
    except ValueError:
        return ""
    _drive, tail = os.path.splitdrive(common)
    if not tail.strip("\\/"):
        return ""
    return common
