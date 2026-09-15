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
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable

from core import probe
from core.config import Config
from core.engine import Extractor
from core.pierce import PierceResult, Piercer, StopReason
from core.vault import PasswordVault, UnlockResult


class ItemStatus(str, Enum):
    QUEUED = "queued"
    RUNNING = "running"
    DONE = "done"
    FAILED = "failed"
    SKIPPED = "skipped"

    @property
    def label(self) -> str:
        return {
            ItemStatus.QUEUED: "排队中",
            ItemStatus.RUNNING: "解压中",
            ItemStatus.DONE: "完成",
            ItemStatus.FAILED: "失败",
            ItemStatus.SKIPPED: "已跳过",
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
        parts.append("伪装")
    cleaned = probe.clean_delete_chars(os.path.basename(path))
    if cleaned != os.path.basename(path):
        parts.append("含「删」字")
    return " · ".join(parts)


def _carrier_note(path: str, embedded: probe.Embedded) -> str:
    ext = probe.ext_of(path) or "无扩展名"
    return (
        f"内嵌包：{ext} 文件里从 {embedded.offset_text} 处开始有一个 "
        f"{embedded.fmt.value.upper()}，解压时切出来处理，不改动原文件"
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
    `[SLG官中][4321510]…2.88G.mp4` = 36 字节假 MP4 头 + 一整个 ZIP，
    而包内偏移是**绝对**的（EOCD 里的 cd_off 直接等于中央目录的绝对位置），
    probe 的"反推起点"因此算出 base=0 被判非法；就算算出来了也**不能切包**
    ——切掉那 36 字节会让包内所有绝对偏移整体错位。

    问引擎只要 0.03s（`7z l -slt` 一次），却能把这类"头是假、身体是真包"的
    文件救回来：7z 自己会报 `Embedded Stub Size = 36` 然后照常读。
    """
    ex = _probe_extractor()
    if ex is None:
        return False
    try:
        info = ex.inspect(path)
    except Exception:              # noqa: BLE001
        return False
    return bool(info.read_ok and info.entry_count)


def _archives_in(
    directory: str, *, scan_appended: bool = True
) -> tuple[list[str], dict[str, int], dict[str, probe.Embedded], set[str]]:
    """列出一个目录里的「待解压目标」（分卷只留主卷）+ 卷数 + 内嵌包信息 + 引擎直读集。"""
    try:
        files = [e.path for e in os.scandir(directory) if e.is_file()]
    except OSError:
        return [], {}, {}, set()

    groups = probe.group_volumes(files)
    counts = {os.path.normcase(g.main): g.count for g in groups}
    targets = [g.main for g in groups]
    embedded: dict[str, probe.Embedded] = {}
    engine_read: set[str] = set()
    for p in files:
        if probe.classify_volume(p).kind is not probe.VolKind.NONE:
            continue
        if probe.detect_format(p).is_archive:
            targets.append(p)
            continue
        # 头不是压缩包：可能是"垫了真视频、后面接压缩包"的那种
        if scan_appended and probe.looks_like_carrier(p):
            # 先问引擎（0.03s）：能直读就省掉后面那次几秒的全盘扫描
            if engine_can_read(p):
                targets.append(p)
                engine_read.add(os.path.normcase(p))
                continue
            emb = probe.find_embedded(p)
            if emb is not None:
                targets.append(p)
                embedded[os.path.normcase(p)] = emb
    return targets, counts, embedded, engine_read


def scan(paths: list[str], *, scan_appended: bool = True,
         exclude: "list[str] | None" = None) -> list[ScanItem]:
    """把拖入的路径展开成清单。

    * 单个文件 → 一行（若拖进来的是 part2.rar 这种次卷，自动纠正到主卷）
    * 文件夹  → 一行汇总 + 每个压缩包一行（缩进），汇总行不参与执行
    * 文件夹里没有任何压缩包 → 仍然生成一行可执行项（交给穿透自己去找）

    **必须去重**：用户一次性拖入 part1~part4 时，每个分卷都会解析成同一个主卷，
    不去重就会生成 4 个一模一样的任务、把同一个包解 4 遍
    （实测：拖 4 个分卷 → 4 个「资源分卷.part1.rar」）。

    不可执行的行（`runnable=False`）只用来"把话说清楚"：比如拖进来一个真的
    mp4，就明确显示「不是压缩包，不会处理」，而不是让引擎去报一句"解压失败"。
    """
    items: list[ScanItem] = []
    seen_dirs: set[str] = set()
    seen_targets: set[str] = set()

    def key(p: str) -> str:
        return os.path.normcase(os.path.abspath(p))

    for raw in paths:
        path = os.path.abspath(raw)
        if os.path.isdir(path):
            if key(path) in seen_dirs:
                continue
            seen_dirs.add(key(path))

            targets, counts, embedded, engine_read = _archives_in(
                path, scan_appended=scan_appended
            )
            # 文件夹里的目标也要跟已加入的去重（同一个包既被单独拖入又在文件夹里）
            targets = [t for t in targets if key(t) not in seen_targets]

            if not targets:
                if any(i.path == path for i in items):
                    continue
                items.append(
                    ScanItem(path=path, name=os.path.basename(path) or path, kind="📁 文件夹",
                             is_dir=True, note="目录里没有可识别的压缩包")
                )
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
                    note = "假头 + 一个完整的包：直接交给 7-Zip 读原文件，不切包"
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
        skip_ext = probe.excluded_ext(main, exclude)
        if skip_ext:
            # 排除名单里的类型：列出来、说清楚、不执行（apk 就是 zip，引擎会照解）
            items.append(
                ScanItem(
                    path=main,
                    name=os.path.basename(main),
                    kind=f"🚫 {skip_ext.upper()} 已在排除列表",
                    runnable=False,
                    note=f".{skip_ext} 在「设置 → 不处理的文件类型」里，拖进来也只列不解",
                )
            )
            continue
        emb = None
        engine_read = False
        if not is_archive and scan_appended and probe.looks_like_carrier(main):
            # 先问引擎（0.03s）：能直读就不必再花几秒全盘扫（拖进来卡顿的主因）
            engine_read = engine_can_read(main)
            if not engine_read:
                emb = probe.find_embedded(main)

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
                    note="假头 + 一个完整的包：直接交给 7-Zip 读原文件，**不切包**"
                         "（包内偏移是绝对的，切了会错位）",
                )
            )
            continue

        if not is_archive and probe.ext_of(main) not in probe.ARCHIVE_EXTS:
            # 既不是压缩包、名字也不像压缩包（真视频、文档之类）：
            # 列出来但标成不可执行。以前这种会进队列，然后引擎回一句
            # "Cannot open the file as archive"，看着像工具坏了。
            items.append(
                ScanItem(
                    path=main,
                    name=os.path.basename(main),
                    kind=probe.describe(probe.detect_format(main)),
                    runnable=False,
                    note="不是压缩包，跳过",
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
                self._log("没有待处理的任务（已跑完的那些不会重复解；要重跑先清空列表或加新文件）")
            for item in leaves:
                # 暂停期间不开新任务（正在跑的那条由引擎层挂起）
                while self.should_pause() and not self.should_stop():
                    time.sleep(0.1)
                if self.should_stop():
                    item.status = ItemStatus.SKIPPED
                    item.note = "用户中止"
                    self._emit(item)
                    continue
                # 源文件在排队期间被删掉/移走（上一批可能把临时切片、内层包删了）：
                # 直接说清楚，别让引擎回一句"退出码 2：系统找不到指定的文件"
                if not os.path.exists(item.path):
                    item.status = ItemStatus.FAILED
                    item.note = "源文件不在了（可能已被上一批删掉或移动）"
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
                    item.note = f"处理这个包时出错：{exc!r}"
                    self._log(f"✘ 结束：{item.name} — {item.note}")
                    try:
                        import traceback

                        self._log("（技术细节已写进 logs/ui.log）")
                        print(f"[Runner] {item.path} 处理失败：\n"
                              + traceback.format_exc(), flush=True)
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
            exclude_exts=list(getattr(self.cfg, "exclude_exts", ()) or ()),
            output_root=(self.cfg.output_dir.strip()
                         if self.cfg.output_mode == "custom" and self.cfg.output_dir.strip()
                         else None),
            conflict=self.cfg.conflict,
            logger=self._log,
            ask_password=self._ask,
            on_layer=lambda depth, _name, _done: self._on_layer(item, depth),
        )
        res = piercer.run(item.path)
        item.elapsed = time.monotonic() - started
        self._apply(item, res)
        self._emit(item)

    def _on_layer(self, item: ScanItem, depth: int) -> None:
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
        item.layer = res.max_depth_reached
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

        if res.ok:
            item.status = ItemStatus.DONE
        elif res.stop_reason is StopReason.CANCELLED:
            item.status = ItemStatus.SKIPPED
            item.note = "用户中止"
        elif res.stop_reason is StopReason.OUTPUT_EXISTS:
            item.status = ItemStatus.SKIPPED
            item.note = res.stop_detail or res.stop_reason.label
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

        mark = {"done": "✔", "failed": "✘", "skipped": "⚠"}.get(item.status.value, "·")
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
            if any(k.status is ItemStatus.RUNNING for k in kids):
                item.status = ItemStatus.RUNNING
            elif done == len(kids):
                item.status = ItemStatus.DONE
            elif failed == len(kids):
                item.status = ItemStatus.FAILED
            else:
                item.status = ItemStatus.DONE if done else ItemStatus.FAILED
            bits = [f"{len(kids)} 个包"]
            if done:
                bits.append(f"{done} 成功")
            if failed:
                bits.append(f"{failed} 失败")
            if skipped:
                bits.append(f"{skipped} 跳过")
            item.note = " · ".join(bits)
            item.layer = max((k.layer for k in kids), default=0)
            item.elapsed = sum(k.elapsed for k in kids)
            self._emit(item)


def summarize(items: list[ScanItem]) -> dict[str, int]:
    """给计数卡用。"""
    counts = {"queued": 0, "running": 0, "done": 0, "failed": 0, "skipped": 0}
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
    """
    out: list[str] = []
    seen: set[str] = set()
    for it in items:
        if it.status is not ItemStatus.DONE:
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
