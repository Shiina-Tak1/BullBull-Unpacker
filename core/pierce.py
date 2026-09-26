"""递归穿透：把「解压出来的压缩包」继续解下去，直到没有可解的为止。

原版的流程（唯一文件→补 .zip、循环扫压缩包、失败即停）思路是对的，
但**缺少保险丝**，实盘上会变成磁盘炸弹或死循环。这里补了三个：

    1. 最大层数      —— 防 a.zip 里放 a.zip 的死循环
    2. 已访问指纹    —— 对每个目标记 (路径, 大小, mtime)，防同一个包反复解
    3. 最低剩余空间（设置页项名）—— 解压前检查磁盘余量，防把盘撑爆

停止条件写全，并且**每一层都给出明确的停止原因**——"没反应"是原版最劝退的地方。

单层流程：

    清理含「删」字文件名 → 挑主卷（分卷只挑主卷）→ 用外层成功密码优先试 →
    解到新目录 → 若目录里只剩一个子文件夹则把内容上提 → 进入下一层
"""

from __future__ import annotations

import hashlib
import os
import shutil
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, NewType

from core import probe
from core.engine import Extractor
from core.vault import PasswordVault, Problem, UnlockResult, unlock

Logger = Callable[[str], None]
# 密码全部试完后，向上层（UI）要一个手动密码；返回 None 表示用户放弃这个任务
AskPassword = Callable[[str, "UnlockResult"], "str | None"]

# ★「包层」= 压缩包套压缩包的层数（源包 = 1 层，包里的包 = 2 层），由用户的 `max_depth` 管。
#   **它不是**"解了几个包"（动作序号），**也不是**目录层级 —— 这两件事历史上都和它混用过：
#   `max_depth_reached`（= len(layers)，动作序号）一度被当成层深喂给界面，于是出现
#   「第 4 层 / 共 2 层」（B-2026-016）；同层第 2 个包被当成"更深一层"记账，于是先报
#   「达到层数上限」、紧接着又把那个包解了（B-2026-033 成因②）。
#   单独给它一个类型，是为了让"这个数是包层还是动作序号"在签名上就能看出来。
PackLayer = NewType("PackLayer", int)
# 逐层上报：(包层, 目标文件名, 这一层是否成功)。给界面做实时进度用。
OnLayer = Callable[[PackLayer, str, bool], None]

# 无扩展名的包：产物目录名加这个后缀，免得 `outdir` 恰好等于源文件自己的路径（见 `_outdir_for`）
NOEXT_SUFFIX = "_unpack"

# `_same_content` 的分块大小：它只在**指纹碰撞**时才跑，而且遇到第一处不同立刻返回，
# 所以这个数只决定"发现差异要读几次"，不影响正确性（1MB 是"读得不碎、又不至于
# 为一个字节读完整个 11GB 分卷"的折中）。
CONTENT_CMP_CHUNK = 1 << 20

# `_can_lift` 的递归合并深度上限：防"深到把耗时/栈拖爆"的保险丝，
# **不是**用户的层数预算（跟 `max_depth` 无关，见 §5.3）。
# ⚠ 撞上它的后果是"这一层**整个不提**"（全有或全无，不是少提几层），而且再跑一次也不动 ——
#   所以必须有一句日志说清，别让用户对着十几层同名目录发呆（B-2026-050）。
CAN_LIFT_MAX_DEPTH = 16


class StopReason(str, Enum):
    NO_ARCHIVE = "no_archive"
    AMBIGUOUS = "ambiguous"
    EXTRACT_FAILED = "extract_failed"
    PASSWORD_EXHAUSTED = "password_exhausted"
    PASSWORD_SKIPPED = "password_skipped"
    MAX_DEPTH = "max_depth"
    NO_SPACE = "no_space"
    ALREADY_VISITED = "already_visited"
    CANCELLED = "cancelled"
    OUTPUT_EXISTS = "output_exists"
    # 往下找内层包时**没找完**（子目录太多烧了保险丝 / 有子目录读不了）。
    # 为什么单独一条：这种情况以前会被说成 NO_ARCHIVE（=「文件夹里已没有可解压的
    # 压缩包」）→ ok=True → 界面绿色「完成」，而盘上其实还躺着包（B-2026-033 成因①、
    # B-2026-037）。它**不是出错**（所以算 clean stop），但**确实有内容没解**，
    # 所以要进 `partial`，界面显示「部分完成」。
    SEARCH_INCOMPLETE = "search_incomplete"
    # 引擎**报成功**、但输出目录里一个字节都没新增（`B-2026-030`）。最典型的成因是
    # "输出目录在解压途中被删 / 被清理"，而 7z 有时仍然返回 0 —— 以前这种会被当成
    # 「完成」，用户打开输出目录才发现是空的（实测 20/20 稳定复现）。
    # 它**不是** clean stop（确实什么都没解出来），所以界面如实显示「失败」+ 原因。
    EMPTY_OUTPUT = "empty_output"
    # 解压**过程中**磁盘余量跌破下限，被主动掐断（`B-2026-043`）。
    # 与 `NO_SPACE`（解压前按"包声明的体积"判、还没开始就拒绝）**必须分开**（`R-13`）：
    # 那个是"没开始"，这个是"写到一半发现盘要被写满"——用户要采取的动作不一样。
    SPACE_GUARD = "space_guard"

    @property
    def label(self) -> str:
        return {
            StopReason.NO_ARCHIVE: "文件夹里已没有可解压的压缩包",
            StopReason.AMBIGUOUS: "有多个压缩包，无法确定先解压哪一个，已停止",
            StopReason.EXTRACT_FAILED: "解压失败",
            StopReason.PASSWORD_EXHAUSTED: "密码已全部试完，未找到正确密码",
            StopReason.PASSWORD_SKIPPED: "已跳过这个包（未输入密码）",
            StopReason.MAX_DEPTH: "已达到层数上限",
            StopReason.NO_SPACE: "磁盘剩余空间不足，已停止",
            StopReason.ALREADY_VISITED: "这个包之前已经解压过",
            StopReason.CANCELLED: "已停止",
            StopReason.OUTPUT_EXISTS: "输出目录已存在，按设置跳过",
            # 不预设原因（是"保险丝烧了"还是"有目录读不了"，由 `stop_detail` 说）——B-2026-044
            StopReason.SEARCH_INCOMPLETE: "子文件夹没往下找完",
            StopReason.EMPTY_OUTPUT: "解压后没有产出任何内容",
            StopReason.SPACE_GUARD: "解压中磁盘空间不足，已中止",
        }[self]

    @property
    def is_clean_stop(self) -> bool:
        """属于「安全停下」而非「出错」的停止原因。"""
        return self in (
            StopReason.NO_ARCHIVE,
            StopReason.MAX_DEPTH,
            StopReason.ALREADY_VISITED,
            StopReason.AMBIGUOUS,
            StopReason.CANCELLED,
            StopReason.OUTPUT_EXISTS,
            StopReason.SEARCH_INCOMPLETE,
        )


@dataclass
class LayerResult:
    """一层穿透的结果。"""

    depth: PackLayer                 # 这一层的**包层**（不是动作序号，见 `PackLayer`）
    target: str
    outdir: str
    ok: bool
    seconds: float = 0.0
    password_origin: str = ""
    password: str = ""                 # 这一层真正用上的密码（给 UI 显示/复制用）
    note: str = ""
    reason: StopReason | None = None   # 仅在失败时给出，结构化，不靠猜字符串


@dataclass
class PierceResult:
    ok: bool
    layers: list[LayerResult] = field(default_factory=list)
    stop_reason: StopReason = StopReason.NO_ARCHIVE
    stop_detail: str = ""
    output_dir: str = ""
    # 这一趟**没解开**的包（相对 `output_dir` 的路径）。
    # 为什么单列一个字段：`ok=True` 只说明"安全停下了"，而 `ALREADY_VISITED` /
    # `MAX_DEPTH` / `AMBIGUOUS` 这三种停法其实是"**有内容确实没解**"——
    # 界面显示"完成"会让用户以为全解完了（BUG-6 的核心）。
    # ⚠ 绝大多数条目确实还留在盘上（B-2026-013：不许点名幽灵），**唯一例外**是
    #   "排队排到它时已经被**外部**删掉/移走"那一种（`B-2026-084`）：它必须留在账上
    #   （那意思是"我们没解"，不是"它还在那里"），判据与对账规则见 §5.3 / `_reconcile_leftover`。
    leftover: list[str] = field(default_factory=list)

    @property
    def max_depth_reached(self) -> int:
        """**解压动作次数**（= `len(self.layers)`）—— 不是层深。

        同层有多个互不相关的包时它会大于真实层深（解了 4 个包但只到了第 2 层）。
        历史：它一度被当成"层深"喂给界面，于是出现"第 4 层 / 共 2 层"（B-2026-016）。
        真正的层深看 `deepest_layer`；这个属性**保留原义**，别处是按"解了几个包"在用。
        """
        return len(self.layers)

    @property
    def deepest_layer(self) -> PackLayer:
        """**已经解开的最深包层** = 已解各层里最大的 `depth`（B-2026-016）。

        `LayerResult.depth` 是**包层**（源包 1 层），不是"解了几个包" —— 后者看
        `max_depth_reached`。同层有多个包时这两个数不相等，界面必须用这一个
        （用错就会出现「第 4 层 / 共 2 层」）。
        """
        return max((ly.depth for ly in self.layers), default=0)

    @property
    def partial(self) -> bool:
        """安全停下了，但**有内容没解**（或者没找完）—— 既不是失败，也不是真的完成。"""
        if not self.ok:
            return False
        return bool(self.leftover) or self.stop_reason is StopReason.SEARCH_INCOMPLETE

    def summary(self) -> str:
        if self.partial:
            # 没找完那种 leftover 可能是空的（一个候选都没找到，但搜索被保险丝截断）——
            # 这时不能写"0 个包没解"，理由由 label 说
            state = (f"部分完成（{len(self.leftover)} 个包没解）" if self.leftover
                     else "部分完成")
        elif self.stop_reason is StopReason.CANCELLED:
            # `ok` 是"安全停下"，取消也算，所以这里不能跟着 `ok` 说"完成"——
            # 日志里那句"完成：用户中止"自相矛盾（B-2026-014 的表现之一）
            state = "已停止"
        elif self.stop_reason is StopReason.OUTPUT_EXISTS:
            # 同理：按设置跳过不等于做完了（B-2026-017）
            state = "已跳过"
        else:
            state = "完成" if self.ok else "未完成"
        # 状态词与 label 相同时不重复拼（例如 CANCELLED：state 与 label 都是「已停止」）
        label = self.stop_reason.label
        head = f"{self.deepest_layer} 层，{state}"
        if label and label != state:
            head += f"：{label}"
        return head + (f"（{self.stop_detail}）" if self.stop_detail else "")


@dataclass
class PickResult:
    """在某个目录里挑出来的下一步目标。"""

    path: str | None = None
    ambiguous: list[str] = field(default_factory=list)
    note: str = ""
    # 同一层有**多个互不相关的包**时全放这里：调用方会逐个都解。
    # 旧行为是直接判 ambiguous 收工（"无法确定主包"），但用户的预期是"都解出来"
    # ——实测见过一个外层包里躺着五个互不相关的内层包（tar / 7z / zip 混着），
    # 旧逻辑解到这儿就停了。
    targets: list[str] = field(default_factory=list)
    # 往下找时**没找完**（子目录太多烧了保险丝 / 有子目录读不了）。
    # 置 True 时调用方必须如实报 `SEARCH_INCOMPLETE`，**不许**当成"没有可解压的压缩包"
    # —— 那正是 B-2026-033 成因① 与 B-2026-037 的假象。
    search_incomplete: bool = False
    # 「安全停下、但确实没解」的东西 —— 调用方要登记进 `leftover`，界面才知道有东西没解：
    #   * 没找完（子目录太多烧了保险丝 / 有子目录读不了）时**已经找到**的候选；
    #   * ★ 子目录里**疑似藏了内嵌压缩包**的文件（`B-2026-076`）：A 方案只**如实报**、
    #     不去解它 —— 名单来自 `probe.suspected_carriers()`（廉价尾部定位）。
    unfinished: list[str] = field(default_factory=list)
    # 没找完的**原因**（保险丝烧了 / 有子目录读不了）。以前两种情况共用一句
    # 「子文件夹太多」，于是"读不了"被说成"太多"（B-2026-044，R-13）。
    search_detail: str = ""

    @property
    def found(self) -> bool:
        return self.path is not None or bool(self.targets)


def _prefer_failure(cur: tuple[StopReason, str] | None,
                    reason: StopReason,
                    detail: str) -> tuple[StopReason, str]:
    """两个候选停止原因里，哪个该报给用户：**真失败优先于安全停下**（`B-2026-073`/`B-2026-074`）。

    以前是一个槽位 + `if failure is None:`（先到先得），于是这些**写在解压之前、之后还会
    继续解压**的安全停下会把后面真正发生的失败顶掉：

    * `SEARCH_INCOMPLETE`：`pick_target` 说「没往下找完」，但已经找到的候选照解；
    * `ALREADY_VISITED`：指纹重复的那个跳过，同层其它包照解；
    * `OUTPUT_EXISTS`：按设置跳过这个包，同层其它包照解；
    * `AMBIGUOUS` / `MAX_DEPTH`：同理，写完槽位还会去处理排队里的兄弟包。

    后果是 `_run` 那句 `res.ok = reason.is_clean_stop` 把结果定成 `ok=True`（"一切正常"），
    而 `res.layers` 里躺着 `ok=False` —— 结果与层自相矛盾，`cli.is_done()` 还会判成功。
    实测两条路都不需要"扫描被截断"就能撞上（`B-2026-074`）。

    规则两条，别只记一条：

    * **真失败可以盖掉已记下的安全停下**（真原因优先；这是唯一允许覆盖的方向）；
    * **安全停下之间仍然先到先得**（第一个才是"这条路为什么停"，后面的只是顺带遇到）。

    分界线直接用 `reason.is_clean_stop`，不另立一张名单：`StopReason` 加新成员时两边不会
    走偏（`R-13`：判据只有一份）。
    """
    if cur is None:
        return (reason, detail)
    if reason.is_clean_stop:
        return cur                      # 安全停下不许顶掉已经记下的任何原因
    if not cur[0].is_clean_stop:
        return cur                      # 真失败之间也先到先得：第一个才是主因
    return (reason, detail)             # ★ 真失败盖掉安全停下


class Piercer:
    """递归穿透状态机。"""

    def __init__(
        self,
        extractor: Extractor,
        vault: PasswordVault,
        *,
        max_depth: int = 5,
        min_free_gb: float = 1.0,
        flatten_single_child: bool = True,
        remove_source: bool = False,
        remove_intermediate: bool = True,
        scan_appended: bool = True,
        workers: int = 0,
        exclude_exts: "list[str] | None" = None,
        output_root: str | None = None,
        conflict: str = "rename",
        logger: Logger | None = None,
        ask_password: AskPassword | None = None,
        on_layer: OnLayer | None = None,
    ) -> None:
        self.ex = extractor
        self.vault = vault
        self.max_depth = max_depth
        self.min_free_gb = min_free_gb
        self.flatten = flatten_single_child
        self.remove_source = remove_source
        # 解压完一层后，把**嵌在里面的**那个压缩包删掉（省磁盘）。
        # 只删 depth>1：第一层是用户自己拖进来的原文件，不归这里管
        self.remove_intermediate = remove_intermediate
        # 认不认「前面垫了真视频、后面接压缩包」的那种伪装（示例.mp4）
        self.scan_appended = scan_appended
        # 试密码的并行路数（0 = 自动，用满本机逻辑核；见 vault.pick_workers）
        self.workers = workers
        # 不处理的扩展名（apk/iso…）：穿透到嵌套层时同样跳过，别把用户的安装包拆了
        self.exclude_exts = list(exclude_exts or [])
        self.output_root = output_root
        self.conflict = conflict
        self._log_fn = logger
        self.ask_password = ask_password
        self.on_layer = on_layer
        # 指纹 → **已确认解过的那些包的路径**（用 dict 不用 set：报"和谁相同"时要说得出对象）。
        # ⚠ 值是**列表**不是单个路径（`B-2026-080`）：指纹只是预筛，两个内容不同的包
        #   可以撞上同一个指纹；撞了之后要逐个逐字节确认。只留"第一个"的话，第二个包
        #   解完之后盘上就没有它的记录了 —— 同一个包再遇到时又会被判成"没解过"，
        #   防死循环的机制反而失效。
        self._visited: dict[tuple, list[str]] = {}
        self._last_password: str | None = None
        # ★ **本进程自己删掉的**路径（键 = `normcase(abspath(...))`，见 `_remove_group`）。
        #   用途只有一个：分清"队列里那个包为什么不在了"（`B-2026-084`）——
        #   自己刚作为中间包删掉的是**队列过期记录**（静默跳过），
        #   别人删的 / 没删但读不到的必须如实报（见 `_pierce` 里那个分支）。
        #   以前这两种共用一个 `os.path.exists()` 为假的分支，于是外部删包也写
        #   「已处理过，无需重复」、`1/1 成功`、退出码 0，与"没注入"的对照组一模一样。
        self._removed: set[str] = set()

    # -- 工具 ----------------------------------------------------------

    def _layer_event(self, depth: PackLayer, target: str, done: bool) -> None:
        if self.on_layer:
            try:
                self.on_layer(depth, os.path.basename(target), done)
            except Exception:
                pass

    def _record_layer(self, res: PierceResult, layer: LayerResult) -> None:
        """把一层记进结果 + 发层号事件 —— **这两件事必须在同一处**（B-2026-036）。

        以前 `res.layers.append` 在调用方、`_layer_event(…, done=True)` 在 `_extract_one`
        里，于是"切出内嵌包失败"那条分支只有 append、没有 event：运行中界面的层号停在
        上一层，而结束后 `deepest_layer` 把这一层算进去了 —— 同一次解压两个层号对不上。
        现在只有这一个入口，配对不可能再断。
        """
        res.layers.append(layer)
        self._layer_event(layer.depth, layer.target, layer.ok)

    def _log(self, msg: str) -> None:
        if self._log_fn:
            try:
                self._log_fn(msg)
            except Exception:
                pass

    @staticmethod
    def _rel_to(path: str, base: str) -> str:
        """算 `path` 相对 `base` 的路径；跨盘符或跑到 `base` 外面就退回文件名。

        为什么要退：`..\\..\\别处\\x.zip` 对用户没有意义 —— 他只想知道"东西还在
        输出目录的哪个位置"，跑到外面就说明它不在他看的那棵树里。
        """
        try:
            rel = os.path.relpath(path, base)
        except ValueError:                      # 跨盘符，算不出相对路径
            return os.path.basename(path)
        return os.path.basename(path) if rel.startswith("..") else rel

    @staticmethod
    def _fingerprint(path: str) -> tuple:
        """内容指纹**预筛**：大小 + 首尾各 64KB 的哈希。

        刻意**不含路径与 mtime**——嵌套解压出来的内层包每次落在新路径、mtime 也是新的，
        按路径做指纹等于形同虚设。同一份内容出现在哪里都该被认出来，
        这才是防「同一个包反复解」的有效判据。

        ⚠ **它只是预筛，不是"相同"的判据**（`B-2026-080`）：只看首尾各 64KB 时，
        两个 215040 字节的 `tar` 只要差异落在**中间**就得到同一个指纹 —— 于是第二个包
        被判成"已解过"、内容没落盘，日志还写「内容相同」（假话）。所以碰撞之后
        必须再用 `_same_content()` 逐字节确认（或确认不了时如实降级文案）。

        ⚠ 开头先挡保留设备名：`open(path, "rb")`（下面那行）在会做 DOS 设备映射的
        系统上会**永久阻塞**，而这条路径在 `run()` 里是**解压之前**就走的
        （`self._visited[…].append(start)`）——不挡的话连第一层都进不去。
        """
        if probe.is_device_path(path):
            return (-1, os.path.normcase(path))
        try:
            st = os.stat(path)
            h = hashlib.blake2b(digest_size=16)
            with open(path, "rb") as f:
                h.update(f.read(65536))
                if st.st_size > 131072:
                    f.seek(-65536, os.SEEK_END)
                    h.update(f.read(65536))
            return (st.st_size, h.hexdigest())
        except OSError:
            return (-1, os.path.normcase(path))

    @staticmethod
    def _same_content(a: str, b: str) -> bool:
        """`a` 与 `b` **逐字节**相同吗？只在指纹碰撞时才调（`B-2026-080`）。

        **为什么不能改成"每层都全文哈希"**：实测 64MB 全文 blake2b ≈0.116s
        （≈550MB/s），11GB 分卷每层会白花约 20s。这里做的是**逐字节比较**，
        遇到第一处不同立刻返回 —— 代价只落在"指纹真的碰撞"那一刻
        （同一个包再次遇到，或首尾恰好相同的两个包），正常套娃（内外层大小不同）
        根本走不到这里。

        ⚠ **读不了 / 半路出错一律返回 False**（"不确认相同"）：宁可多解一份，
        也不能把"读不动"当成"内容相同"咽下去（`R-13`：判据用词要有区分力）。
        """
        try:
            if os.path.getsize(a) != os.path.getsize(b):
                return False
            with open(a, "rb") as fa, open(b, "rb") as fb:
                while True:
                    ba = fa.read(CONTENT_CMP_CHUNK)
                    bb = fb.read(CONTENT_CMP_CHUNK)
                    if ba != bb:
                        return False
                    if not ba:
                        return True
        except OSError:
            return False

    def _has_space(self, directory: str, need_bytes: int = 0) -> bool:
        """磁盘余量够不够。`need_bytes` 用于"要先切出一个同样大的临时文件"的场景。"""
        try:
            free = shutil.disk_usage(directory).free
        except OSError:
            return True
        return free - need_bytes >= self.min_free_gb * (1024 ** 3)

    # -- 内嵌压缩包：切出来再解 ----------------------------------------

    def _cancel_flag(self):
        """引擎上挂着 Runner 设的取消钩子，这里借来给大文件切片用。"""
        return getattr(self.ex, "cancel", None)

    def _pause_flag(self):
        return getattr(self.ex, "pause", None)

    def carve_source(self, archive: str, outdir: str) -> tuple[str, str | None, str, StopReason | None]:
        """如果 archive 是"垫了真视频、后面接压缩包"的伪装文件，先切出压缩包。

        返回 `(喂给引擎的路径, 需要事后删掉的临时文件, 出错说明, 停止原因)`。

        为什么必须先切出来：引擎（7z/Rar）不会去文件中间找压缩包，
        `7z l 示例.mp4` 只会回 "Cannot open the file as archive"。
        切出来的临时文件用完就删，用户的原视频一个字节都不动。

        判据走**唯一入口** `probe.inspect_carrier()`（`B-2026-097`）；与
        `_carrier_candidates` 的区别只有两个参数 —— **传引擎**（走到这里已经确定是目标，
        0.06~0.13s 换"覆盖所有引擎能直读的形态"值得，§20.21）、**开深扫**（是最后手段）。

        ★ **这几段的顺序不许调换**（`B-2026-094`，§20.21）：尾部目录定位 → 绝对偏移布局
        → 问引擎 → **尾部签名** → 深扫。把引擎判据提到前面会让"垫片 + 相对偏移"那类包
        从"切包后解"变成"直读原文件"（实测它 `can_read` 也是 True）—— 那是拿正确性换速度。
        尾部签名排在引擎之后，是为了让"包离文件头 ≤8MB"的（引擎够得着）照旧走直读，
        只有引擎够不着的追加式伪装才落到它手上（实测边界见 §14.8）。
        """
        if not self.scan_appended:
            return archive, None, "", None

        # 与 `_cancel_flag` / `_pause_flag` 同一种防御：拿不到这个能力（老引擎对象 /
        # 测试里的桩）就**当它读不了** —— `inspect_carrier` 会跳过引擎那一档，
        # 行为与修复前一致。
        _can_read = getattr(self.ex, "can_read", None)
        got = probe.inspect_carrier(
            archive, engine_probe=_can_read, deep=True, cancel=self._cancel_flag(),
        )
        # 「包内偏移是绝对的」或「引擎自己会跳过假头」→ 原样直读：不切包、不改动用户文件。
        # ⚠ 这两条都只许排在"尾部推不出切片起点"之后（`inspect_carrier` 里已经保证）。
        if got.direct:
            return archive, None, "", None
        emb = got.embedded
        if emb is None:
            return archive, None, "", None

        try:
            total = os.path.getsize(archive) - emb.offset
        except OSError as exc:
            # 读不到大小（探测之后文件被删 / 被占用）：说一句再继续。
            # 以前这里是静默 `return`，于是"这个伪装包为什么没解出来"在日志里毫无线索
            # （B-2026-037 的剩余部分；真原因最后由引擎那条路说，见 B-2026-031）。
            self._log(
                f"⚠ 读不到这个伪装文件的大小，先不切：{os.path.basename(archive)}"
                f"（{exc.strerror or exc.__class__.__name__}）"
            )
            return archive, None, "", None

        base_dir = os.path.dirname(outdir) or os.path.dirname(archive) or "."
        temp = os.path.join(base_dir, f".{os.path.basename(archive)}.carve.{emb.ext}")
        self._log(
            f"检测到伪装文件：{os.path.basename(archive)} 的 {emb.offset_text} 处有一个 "
            f"{emb.fmt.value.upper()}，先切出 {probe.human_size(total)} 再解压"
        )
        # 切出来的临时文件跟正式产物一样占地方，先看余量再动手
        if not self._has_space(base_dir, need_bytes=total):
            return archive, None, (
                f"切出内嵌包需要额外 {probe.human_size(total)}，"
                f"低于最低剩余空间 {self.min_free_gb:.2f} GB"
            ), StopReason.NO_SPACE

        if probe.carve(archive, emb.offset, temp, cancel=self._cancel_flag(),
                       pause=self._pause_flag()) < 0:
            reason = StopReason.CANCELLED if self._cancelled() else StopReason.EXTRACT_FAILED
            return archive, None, "切出内嵌包失败", reason
        return temp, temp, "", None

    def _cancelled(self) -> bool:
        flag = self._cancel_flag()
        try:
            return bool(flag and flag())
        except Exception:
            return False

    def _discard(self, temp: str | None) -> None:
        if not temp:
            return
        try:
            os.remove(temp)
            self._log(f"已清理临时文件：{os.path.basename(temp)}")
        except OSError:
            pass

    # -- 单层第 1 步：清理「删」字 --------------------------------------

    def clean_delete_names(self, directory: str) -> list[tuple[str, str]]:
        """把含「删」字的文件名还原（规避网盘检测的常见手法）。返回 [(旧, 新)]。"""
        renamed: list[tuple[str, str]] = []
        try:
            entries = list(os.scandir(directory))
        except OSError as exc:
            # 以前静默返回空名单：用户只看到"这个包没解出来"，没有任何线索
            # （B-2026-037 的剩余部分）。清理不了「删」字不是致命的，但必须说出来。
            self._log(
                f"⚠ 读不了这个目录，没法清理「删」字："
                f"{os.path.basename(directory) or directory}"
                f"（{exc.strerror or exc.__class__.__name__}）"
            )
            return renamed

        for e in entries:
            if not e.is_file():
                continue
            cleaned = probe.clean_delete_chars(e.name)
            if cleaned == e.name:
                continue
            dst = os.path.join(directory, cleaned)
            if os.path.exists(dst):
                self._log(f"同名文件已存在，跳过重命名：{e.name}")
                continue
            try:
                os.rename(e.path, dst)
            except OSError as ex:
                self._log(f"重命名失败：{e.name} → {cleaned}")
                continue
            renamed.append((e.name, cleaned))
            self._log(f"清理「删」字：{e.name} → {cleaned}")
        return renamed

    # -- 单层第 2 步：挑目标 -------------------------------------------

    def _carrier_candidates(self, files: "list[str]", *, deep: bool) -> "list[str]":
        """这一层里"垫了马甲、身体里藏着压缩包"的文件（候选单的第三个通道）。

        判据走**唯一入口** `probe.inspect_carrier()`（`B-2026-097`）：与界面扫描
        （`pipeline._archives_in`）、子目录报备（`probe.suspected_carriers`）、处理目标
        （`carve_source`）、`cli --probe` **同一份**。各处的差异只剩两个参数：

        * **不传 `engine_probe`**（代价决策）：本函数是**按这一层每个 ≥1MB 文件**跑的，
          问一次引擎就是 0.06~0.13s 的进程开销，一屋子真视频 = N×0.1s（§20.21 坑②；
          §14.13/§14.14 记的"拖 mp4 卡顿"）。所以这里只靠**毫秒级**的三档廉价判据：
          尾部目录定位 → 绝对偏移布局 → **尾部签名**。
        * `deep` 由调用方给（`pick_target` 传 `not merged`）：全盘扫（~1GB/s）只在
          "这一层没有别的候选可解"时才有意义。

        ⚠ **尾部签名**那一档（`B-2026-097`，`probe.tail_magic`）是这次补上的：RAR5 / 7z
        的追加式伪装（包紧贴文件尾）以前**只有全盘扫**能发现，于是"这一层有普通包时
        不深扫"就把它整个漏掉 —— 现在读尾部 4MB 就能拿到切片起点，连 `-hp` 加密头
        那类也认得出（签名与头部 CRC 都是明文，不需要密码）。代价恒定 4MB、实测 2~4ms。
        """
        out: list[str] = []
        for p in files:
            got = probe.inspect_carrier(p, deep=deep, cancel=self._cancel_flag())
            if got.worth_handling:
                out.append(p)
        return out

    def pick_target(self, directory: str, *, skip: "set[str] | None" = None) -> PickResult:
        """按优先级挑下一步要解的包。

        判据只有一条：**`probe.candidates_from()` 挑出来的候选包**（不信任扩展名、按 magic
        认格式；分卷只留主卷，主卷不在场就跳过并说明）。所以这里**不再**单独处理"目录里只有
        一个没有扩展名的文件"—— 那种文件在候选里本来就是候选。

        `skip` 是**已经在排队**的包（同层兄弟，见 `_pierce` 的 `todo`）：它们各自的包层
        由队列带着，这里不能再捡一次 —— 捡了就会按"更深一层"记账，于是同层的第 2 个包
        白白吃掉一层预算（B-2026-033 成因②：日志先报「达到层数上限」，紧接着又把那个包
        解了）。所以候选与"往下找"的结果都要过一遍这个名单。

        **多个互不相关的候选 = 全都解**（返回 `targets`），不再判 ambiguous 收工：
        套娃里经常是"好几个独立的包躺在一起"，用户要的就是都解出来。
        真正需要停的只有"多个候选但一个都认不出主次"那种伪装文件场景。

        **这一层扫不到时向下找候选包**（`probe.find_archives_below`，与扫描侧**同一份实现**）：
        外层包自带目录（`包裹A/包裹B/inner.zip`）是打包常态，只看一层会把内层包漏掉还报"完成"。
        找到恰好一个就继续解；找到多个就**如实列全**；**没找完**（子目录太多 / 读不了）就报
        `search_incomplete`，不许说成"没有可解压的压缩包"。

        历史（B-2026-034）：这里曾经还有一条「情况 A：目录里只有一个文件、头是压缩包 →
        直接交给引擎」的分支，用的是**没过滤排除名单**的 `files`。于是「不处理的文件类型」
        在本层只剩一个文件时失效 —— 日志说「跳过 x.apk」、程序照样解。
        那条分支在正常路径上本就是死代码（候选用的就是同一个 `probe.detect_format`），
        唯一的"活路"恰好是绕过排除名单，所以直接删掉。
        """
        # ★ 「是不是同一个包」的判据是**物理身份**，不是路径（`B-2026-086`）：
        #   硬链接 / 8.3 短名 / subst 盘符都能让同一个包有第二条路径，按路径去重看不出来
        #   —— CLI 会"解一份 + 另一份报 already_visited/partial"（退出码 1，而那个包
        #   明明还在盘上），界面则各解一份。手册 §5.3 早就要求"候选与已扫目录都按
        #   **物理身份**去重"（`B-2026-047`），以前只在 `find_archives_below` 里实现了
        #   —— 这里是漏实现，不是取舍。`probe._identity` 拿不到身份时退回规范化路径，
        #   所以一个键就够。
        blocked = {probe._identity(p) for p in (skip or ())}

        def allowed(paths: "list[str]") -> "list[str]":
            return [p for p in paths if probe._identity(p) not in blocked]

        def not_excluded(paths: "list[str]") -> "list[str]":
            """过一遍「不处理的文件类型」（apk/iso…）。

            **本层与子目录必须共用这一份判据**（B-2026-041）：以前只过滤本层候选，
            子目录里找到的包绕过名单被照样解开 —— 用户明确说了这类文件不要动。
            """
            if not self.exclude_exts:
                return list(paths)
            kept: list[str] = []
            for cand in paths:
                ext = probe.excluded_ext(cand, self.exclude_exts)
                if ext:
                    self._log(f"跳过 {os.path.basename(cand)}：.{ext} 已设置为不处理")
                else:
                    kept.append(cand)
            return kept

        try:
            files = [e.path for e in os.scandir(directory) if e.is_file()]
        except OSError:
            files = []
        candidates: list[str] = not_excluded(
            allowed(probe.candidates_from(files, log=self._log))
        )

        # ★ 往下找**总是做**（不再是"本层一个候选都没有才做"）——
        #   本层有候选时子目录里的包以前会被静默漏解（B-2026-040），而"包解出来自带
        #   一层目录"是打包常态。**本层与子目录一视同仁**：合并成一张候选单，
        #   多个就依次全解（与"本层多候选全解"同一口径，B-2026-042）。
        #   每次调用只扫一次、由 `_pierce` 把候选全部入队，所以不是"每个包扫一遍"。
        search = probe.find_archives_below(directory, log=self._log,
                                           cancel=self._cancel_flag())
        if search.cancelled:
            # 用户点了「停止」：**不是**"没找完"，别让上层报 SEARCH_INCOMPLETE
            # （那会让用户以为程序自己放弃了）。返回"没有候选"，由 `_pierce` 判 CANCELLED。
            return PickResult()
        nested = not_excluded(allowed(search.found))

        # ★ `B-2026-076`：子目录里**疑似藏了内嵌压缩包**的文件。它们不是候选
        #   （`candidates_from()` 明写"伪装包不算候选"），这里也**不去解** ——
        #   但**必须如实报**：一个字节都没产出却说「文件夹里已没有可解压的压缩包」
        #   是谎话。判据由 `probe.find_archives_below` 用廉价尾部定位收上来
        #   （**不做全盘扫描** —— 真视频整盘读就是 "拖 mp4 卡顿" 的成因）。
        sub_carriers = not_excluded(allowed(search.carriers))

        # 本层在前、子目录在后，按**物理身份**去重（同一个包可能两边都出现；
        # 硬链接 / 短名这类"两条路径一个文件"也在这里被识破，`B-2026-086`）
        merged: list[str] = []
        seen: set[tuple[int, int] | str] = set()
        for p in list(candidates) + list(nested):
            key = probe._identity(p)
            if key not in seen:
                seen.add(key)
                merged.append(p)
        standard_count = len(merged)          # 正常候选（本层 + 子目录）有几个

        # ★ 伪装包（垫了一段假视频、尾部接一个完整压缩包）与标准候选**走同一张候选单**
        #   （B-2026-075）。以前这段收集排在上面那几个 `return` **之后** ——
        #   只要候选单非空就永远走不到：本层既有正常包、又有伪装包时，伪装包被静默漏解，
        #   还报「完成」、CLI 退出码 0。**遮蔽条件不是"本层有候选"而是"候选单非空"**：
        #   本层伪装包 + **子目录**里的 `sub\inner.zip` 同样漏解（自 `B-2026-040` 起
        #   本层与子目录已合并成一张候选单）。受害面是 CLI 与直接调 `Piercer.run(目录)`
        #   的调用方；界面走 `pipeline._archives_in`，它**无条件**查伪装包，不受影响。
        #   它同样要过「不处理的文件类型」（B-2026-077）—— 名单必须对所有候选通道
        #   一视同仁（界面那条路本来就有这道闸，`pipeline.py` 把 carrier 标成
        #   「🚫 已在排除列表」）。
        #   ⚠ `deep` 只在"这一层没有别的候选可解"时才放开全盘扫描（代价取舍见
        #   `_carrier_candidates`）：尾部定位这一段**与候选单是否为空无关**，
        #   所以"有正常包就不查伪装包"那个毛病不会回来。
        carriers = (not_excluded(allowed(self._carrier_candidates(files, deep=not merged)))
                    if self.scan_appended else [])
        if standard_count:
            # 有正常候选时：伪装包就是"这一层里另一个互不相关的包"，与它们一起**全都解**
            # （与 B-2026-040「本层多候选全解」同一口径）。**顺序必须显式保住**：
            # 正常候选在前、伪装包在后 —— 不能再用 `sorted(merged)` 一把排（按路径排
            # 会让 `video.mp4` 排到 `normal.zip` 前面，与"优先解正常压缩包"冲突）。
            for p in carriers:
                key = probe._identity(p)
                if key not in seen:
                    seen.add(key)
                    merged.append(p)

        # 「没找完」的原因要分开说：保险丝烧了 ≠ 有目录读不了（B-2026-044 / R-13）
        bits: list[str] = []
        if search.truncated:
            if search.unreadable:
                bits.append(f"有 {len(search.unreadable)} 个子文件夹读不了"
                            f"（权限 / 路径过长 / 盘断开）")
            else:
                bits.append(f"子文件夹超过 {probe.NESTED_SCAN_MAX_DIRS} 个")
        if sub_carriers:
            # ★ 不是"没找完"，而是"**按现在的做法不会去找**"（代价取舍，见 find_archives_below
            #   的 docstring）：如实说清楚是哪几个文件，别让用户以为程序在偷懒或者真没有。
            names = "、".join(os.path.basename(p) for p in sub_carriers[:3])
            more = f" 等 {len(sub_carriers)} 个" if len(sub_carriers) > 3 else ""
            bits.append(f"子文件夹里有 {len(sub_carriers)} 个文件疑似藏了内嵌压缩包"
                        f"（不会自动解）：{names}{more}")
        detail = "；".join(bits)
        # 「没找完」与「有疑似伪装包没解」都算**有东西没解** → 结果必须是"部分完成"
        incomplete = search.truncated or bool(sub_carriers)

        if len(merged) > 1:
            # **多个 = 全都解**（本层与子目录同一口径）。日志由 `_pierce` 打
            # （它带 `[第N层]` 前缀，界面正则认那个格式）。
            # 顺序：正常候选（保持原来的排序，稳定可复现）在前，伪装包按发现顺序在后。
            return PickResult(targets=sorted(merged[:standard_count]) + merged[standard_count:],
                              search_incomplete=incomplete,
                              unfinished=list(sub_carriers),
                              search_detail=detail)

        if len(merged) == 1:
            only = merged[0]
            note = ""
            same_dir = (os.path.dirname(os.path.normcase(os.path.abspath(only)))
                        == os.path.normcase(os.path.abspath(directory)))
            if same_dir:
                # 只有真有多卷时才算分卷（单个 .rar/.zip 的 kind 看着像分卷，其实不是）
                info = probe.classify_volume(only)
                try:
                    groups = probe.group_volumes([only])
                except Exception:                # noqa: BLE001 - 归组失败不影响主流程
                    groups = []
                group = next(
                    (g for g in groups if os.path.normcase(g.main) == os.path.normcase(only)),
                    None,
                )
                if group is not None and group.is_split:
                    note = probe.describe(probe.detect_format(only), info)
            else:
                rel = os.path.relpath(only, directory)
                self._log(f"子文件夹里只有一个包，继续解压：{rel}")
                note = f"子目录里的包：{rel}"
            return PickResult(path=only, note=note,
                              search_incomplete=incomplete,
                              unfinished=list(sub_carriers),
                              search_detail=detail)

        # 情况 A2：一个正常候选都没有，只剩"垫了真视频的伪装文件"这条路
        # （候选已经在上面无条件算好了，这里直接用，不再重复探测一遍）。
        # ⚠ 多个伪装包之间仍然**如实报 AMBIGUOUS**：那才是真分不清主次、且探测代价高的
        #   场景（§5.3；正面断言在 `tools\probe_hunt_bugs.py::ambiguous_carriers`）。
        #   一旦有正常候选在场，伪装包就与它一起"全都解"（见上面那段），不再判 ambiguous
        #   —— 分不清主次的前提是"没有正常包当主包"。
        # ⚠ 这两支必须排在下面那个 `incomplete` 分支**前面**：本层那个伪装包是**确认过的**
        #   候选（尾部目录定位命中），不能因为"子目录里还有疑似伪装包"就把它放着不解
        #   （否则 B-2026-075 的对照会坏：本层只有伪装包时照样要解出来）。
        if len(carriers) == 1:
            return PickResult(path=carriers[0], note="伪装文件里内嵌的压缩包",
                              search_incomplete=incomplete,
                              unfinished=list(sub_carriers),
                              search_detail=detail)
        if len(carriers) > 1:
            return PickResult(ambiguous=carriers,
                              search_incomplete=incomplete,
                              unfinished=list(sub_carriers),
                              search_detail=detail)

        # 一个候选都没有：**没找完**（或有疑似伪装包没解）就如实说
        # （别报"文件夹里已没有可解压的压缩包"）
        if incomplete:
            return PickResult(search_incomplete=True, unfinished=list(sub_carriers),
                              search_detail=detail)

        return PickResult()

    # -- 单层第 3 步：内容上提 -----------------------------------------

    @staticmethod
    def _snapshot(directory: str) -> set[str]:
        """记下目录里现在有哪些条目（用来区分"这次解出来的"和"原来就在的"）。"""
        try:
            return {e.name for e in os.scandir(directory)}
        except OSError:
            return set()

    def _has_payload(self, outdir: str, before: set[str]) -> bool:
        """这一层真的往 `outdir` 里写出东西了吗？（`B-2026-030` 的判据）

        只看**新增**的条目；覆盖模式（`conflict=overwrite`）下旧路径被重写、条目名集合
        没变，所以那种情况退化成"目录里还有没有东西"。
        """
        now = self._snapshot(outdir)
        if now - before:
            return True
        return self.conflict == "overwrite" and bool(now)

    @staticmethod
    def flatten_single_child(directory: str, born_after: "set[str] | None" = None,
                             overwrite: bool = False, merge_dirs: bool = False) -> str | None:
        """这一层**解出来**只有一个文件夹时，把它的内容提到上一层，避免 A/A/A 套娃。

        关键在"这一层解出来的"：重名=覆盖时目录里本来就有旧东西（上一次解出来的文件），
        以前按"整个目录只有一个子目录、且没有别的文件"来判断 → 只要目录非空就永远不上提，
        于是覆盖之后变成 `示例/内层/内容.txt` 这种套娃（用户报的就是这个）。
        现在用 `born_after`（解压**之前**的目录快照）把"新出现的条目"挑出来判断。

        `overwrite=True` 时允许覆盖同名条目（覆盖模式下这是用户的明确意图）；
        否则遇到同名就放弃上提——宁可不动，也不覆盖用户的东西。
        `merge_dirs=True` 时遇到"两边都是目录"的同名项**递归合并**（不覆盖文件）——
        `套娃/套娃/套娃` 这种同名链只能这样拆，否则内层名字和外层撞上，非覆盖模式下会直接放弃。
        """
        inner = Piercer._sole_child(directory, born_after)
        if inner is None:
            return None
        if Piercer._lift_child(directory, inner, overwrite=overwrite, merge_dirs=merge_dirs):
            return os.path.basename(inner)
        return None

    @staticmethod
    def _sole_child(directory: str, born_after: "set[str] | None" = None) -> str | None:
        """这一层是不是"只有一个**新出现**的子目录、且没有新文件"？是就返回它的路径。

        单独抽出来是因为有**两个**调用方要用同一份判据：`flatten_single_child`（真去提）
        和 `flatten_same_name_shells`（提不动时要判断"是不是同名套娃太深"）。
        判据只有一份，别各写一遍。
        """
        try:
            entries = list(os.scandir(directory))
        except OSError:
            return None
        known = born_after or set()
        fresh = [e for e in entries if e.name not in known]
        dirs = [e for e in fresh if e.is_dir()]
        files = [e for e in fresh if e.is_file()]
        if len(dirs) != 1 or files:
            return None
        return dirs[0].path

    def flatten_same_name_shells(self, directory: str, born_after: "set[str] | None" = None,
                                 overwrite: bool = False, limit: int = 8) -> list[str]:
        """把**同名空壳**一路提平（`示例/示例/示例/内容` → `示例/内容`）。

        为什么还要这个：`flatten_single_child` 一次只提一层，而三层套娃（示例.mp4 = zip→zip→rar）
        每层解出来的目录都叫 示例 —— 只提一次的话，用户拿到的还是 `示例/示例/内容`。
        界面日志上以前只写一句"内容上提"，用户看不懂为什么还剩一层。

        **只提同名的**是指"**循环继续**"的条件（子目录名 == 当前目录名，忽略大小写）：
        `mods/MyMod/data/…` 这种有意义的层级不会**继续**往下提。
        `limit` 兜底防死循环；同名链要能拆开，必须允许"目录合并不覆盖文件"（`merge_dirs=True`）。

        ⚠ **第一次提根本不看名字**（B-2026-051 的如实描述）：`flatten_single_child` 是
        "这一层只剩一个子目录就提"，**搬完之后**才轮到"是否同名"决定要不要继续循环。
        所以子目录不同名时只提这一层就停；返回值里**包含第一次那一层**（它确实被提了，
        日志必须如实说 —— 以前不同名就不记账，于是"盘上已经提平、日志却说没提"）。

        撞上 `CAN_LIFT_MAX_DEPTH` 时 `_can_lift` 会**整体否决**（一层都不提，再跑也不动）——
        那种情况必须说一句，否则用户对着十几层同名目录不知道发生了什么（B-2026-050）。
        """
        lifted: list[str] = []
        born = born_after
        for _ in range(max(1, limit)):
            name = os.path.basename(directory.rstrip("\\/"))
            got = self.flatten_single_child(directory, born, overwrite=overwrite,
                                            merge_dirs=True)
            if not got:
                inner = self._sole_child(directory, born)
                if inner and os.path.normcase(os.path.basename(inner)) == os.path.normcase(name):
                    # 唯一子目录与当前目录**同名**、却提不动 = 撞上了递归合并的保险丝
                    self._log(
                        f"⚠ 同名文件夹嵌套太深（超过 {CAN_LIFT_MAX_DEPTH} 层），没有整块搬："
                        f"{name}/ 里还套着 {os.path.basename(inner)}/，需要的话请手动拖出来"
                    )
                break
            # 这一层确实被提了（不管同不同名）——先记账，再说要不要继续
            lifted.append(got)
            if os.path.normcase(got) != os.path.normcase(name):
                break
            born = None                 # 提过一次之后就不再看快照（目录内容已经换了）
        return lifted

    @staticmethod
    def _can_lift(directory: str, inner: str, overwrite: bool, merge_dirs: bool,
                  depth: int = 0) -> bool:
        """干跑一遍：`_lift_child` 到底能不能把 inner 整个搬进 directory。

        **为什么必须干跑**：`_lift_child` 是一边遍历一边搬的，遇到"搬不动"的项就返回 False，
        但**前面已经搬走的东西不会回来** —— 调用方看到 False 以为"没动过"，继续在
        **已经被搬空的目录**里找下一层，多层包就从这里断掉（作者 5 万级素材包实测：
        只解了 2 层就不动了，盘上还剩个半空的目录）。所以先干跑，再真搬。
        """
        if depth > CAN_LIFT_MAX_DEPTH:
            return False
        try:
            entries = list(os.scandir(inner))
        except OSError:
            return False
        for e in entries:
            dst = os.path.join(directory, e.name)
            if not os.path.exists(dst):
                continue
            if e.is_dir() and os.path.isdir(dst):
                if overwrite or merge_dirs:
                    if not Piercer._can_lift(dst, e.path, overwrite, merge_dirs, depth + 1):
                        return False
                    continue
                return False
            if e.is_file() and os.path.isfile(dst) and overwrite:
                continue
            if overwrite:                    # 类型不一致：覆盖模式下删掉挡路的再搬
                continue
            return False
        return True

    @staticmethod
    def _lift_child(directory: str, inner: str, overwrite: bool = False,
                    merge_dirs: bool = False) -> bool:
        """把 inner 里的东西全部移到 directory，然后删掉空了的 inner。

        同名时：`overwrite=True` 且都是文件 → 直接替换；都是目录 → 递归合并；
        `merge_dirs=True` 且都是目录 → 也递归合并，但**文件同名就放弃**（不覆盖用户的东西）；
        类型不一致（文件 vs 目录）→ 覆盖模式下删掉挡路的那个再搬；
        非覆盖模式一律放弃（返回 False，调用方就保持原样）。

        **开头先干跑**（`_can_lift`）：搬不动就一个字节都不动，绝不留下"半个目录"。
        """
        if not Piercer._can_lift(directory, inner, overwrite, merge_dirs):
            return False
        moved = 0
        worked = False          # 递归合并里搬过东西也算动过（不然 moved 还是 0，会误报"没提动"）
        try:
            for e in list(os.scandir(inner)):
                dst = os.path.join(directory, e.name)
                if os.path.exists(dst):
                    if e.is_dir() and os.path.isdir(dst) and (overwrite or merge_dirs):
                        if not Piercer._lift_child(dst, e.path, overwrite=overwrite,
                                                   merge_dirs=merge_dirs):
                            return False
                        worked = True
                        continue
                    if not overwrite:
                        return False
                    if e.is_file() and os.path.isfile(dst):
                        os.replace(e.path, dst)
                        moved += 1
                        continue
                    # 类型不一样：把挡路的删掉再搬（覆盖模式）
                    if os.path.isdir(dst):
                        try:
                            os.rmdir(dst)
                        except OSError:
                            return False
                    else:
                        try:
                            os.remove(dst)
                        except OSError:
                            return False
                os.rename(e.path, dst)
                moved += 1
        except OSError:
            return False
        try:
            os.rmdir(inner)
        except OSError:
            pass
        return bool(moved) or worked

    def collapse_shell(self, workdir: str, outdir: str,
                       born_after: "set[str] | None" = None) -> str:
        """把「中间包删掉后剩下的空壳层」拆平，返回下一层该用的工作目录。

        实盘例子：示例.mp4 = zip → zip → rar 三层套娃，每层解出来的目录都叫 示例。
        中间包按设置删掉之后，`示例/` 里就只剩一个刚解出来的 `示例/` 子目录——
        不收掉的话，用户拿到的是 `示例/示例/示例/内容`，前面两层还是空的。

        只在**中间包确实被删掉了**（target 不存在了）且"这一层只解出来一个子目录"时动手，
        所以「保留中间包」和「第一层原文件」这两种情况都不会被误伤。
        `born_after` 是解压前父目录的快照：重名=覆盖时父目录里本来就有旧文件，
        不看快照就会永远收不了壳（套娃就是这么来的）。
        """
        if not os.path.isdir(workdir) or not os.path.isdir(outdir):
            return outdir
        if os.path.dirname(os.path.normcase(outdir)) != os.path.normcase(workdir):
            return outdir            # 产物不在这一层目录下（指定输出目录那种），不动
        if self.flatten_single_child(workdir, born_after,
                                     overwrite=(self.conflict == "overwrite")) is None:
            return outdir
        # 内容已经提到 workdir 了，下一层就从这里继续
        return workdir

    # -- 主流程 --------------------------------------------------------

    def run(self, start: str) -> PierceResult:
        r"""从压缩包或文件夹开始，逐层穿透。

        ★ **这个函数不许把异常抛给调用方**（`B-2026-072`）：界面里它由
        `Runner._run_one` 直接调用，一个漏出去的异常会让**整批**中断、界面状态停在
        「运行中」；CLI 那边则是 traceback + 非 0 退出。已知的失败（引擎报错、输出目录
        建不出来、取消、空间不足…）都已经在各自的分支里转成 `LayerResult` / `StopReason`，
        所以这里是**最后一道网**，只兜"没预料到"的异常，见 `_unexpected_failure`。
        """
        res = PierceResult(ok=False, output_dir="")
        try:
            return self._run(start, res)
        except Exception as exc:                   # noqa: BLE001 - 最后一道网，见 docstring
            return self._unexpected_failure(start, res, exc)

    def _unexpected_failure(self, start: str, res: PierceResult, exc: Exception) -> PierceResult:
        r"""把**未预期**的异常转成一条如实的失败结果（`run()` 的最后一道网）。

        兜底 ≠ 把问题吞掉（`B-2026-072` 的交付要求），所以这里三件事都要做到：
        ① 记一句用户看得见的日志；② 把异常类型与原文写进 `stop_detail`（日志与详情页
        都看得到，用户反馈时能说清是什么）；③ 结果 `ok=False`（界面显示「失败」，
        **不许**停在「运行中」）。

        为什么需要它：`run()` 里那些分支只覆盖**已知**的失败，而它跑在用户机器上
        （奇怪的路径、只读盘、被杀软锁住的文件…），任何一处漏掉的异常都不该升级成
        "整批中断"。⚠ **只兜 `Exception`**：`KeyboardInterrupt` / `SystemExit` 继承
        `BaseException`，照样穿过去 —— 用户按 Ctrl+C 不该被伪装成"解压失败"。

        ★ 收的是 `_run` **正在用的那个 `res`**（不是新造一个）：异常发生时已经解开的层
        仍在 `res.layers` / `res.output_dir` 里，账上不许消失（`R-12`：账目要在结果之后算）。
        """
        detail = f"{type(exc).__name__}: {exc}"
        self._log(f"⚠ 处理这个包时发生意外错误，已停止：{detail}")
        if not res.output_dir:
            # 从目录开始时就是它自己；从单个包开始时还没解出任何东西就留空
            # （与原 `run()` 的失败分支一致，别让"打开输出目录"指错地方）。
            res.output_dir = start if os.path.isdir(start) else ""
        res.ok = False
        res.stop_reason = StopReason.EXTRACT_FAILED
        res.stop_detail = detail
        self._reconcile_leftover(res)      # 盘上已经不在的包不再点名（`B-2026-013`）
        return res

    def _run(self, start: str, res: PierceResult) -> PierceResult:
        """`run()` 的实现（公开入口只负责兜底，见 `run` / `_unexpected_failure`）。"""

        if os.path.isdir(start):
            workdir = start
            res.output_dir = start
            next_layer = PackLayer(1)      # 从目录开始：目录里的包就是第 1 层
        else:
            # ★ 单文件入口以前**完全不看取消**（`B-2026-081`）：拖进来的就是一个包时，
            #   用户点了「停止」照样把它解完。口径与 `_pierce` 的取消分支一致，而且 `ok`
            #   仍然是「这不是异常」（§19.1 `R-11`）：CANCELLED 属于安全停下 → `ok=True`，
            #   所以这里必须是 True（照 `_final_state` 的算法：`reason.is_clean_stop`）。
            if self._cancelled():
                res.stop_reason = StopReason.CANCELLED
                res.stop_detail = ""
                res.ok = True
                return res
            self._visited.setdefault(self._fingerprint(start), []).append(start)
            if not self._has_space(os.path.dirname(start) or "."):
                res.stop_reason = StopReason.NO_SPACE
                res.stop_detail = f"剩余空间低于 {self.min_free_gb:.2f} GB"
                return res
            outdir = self._outdir_for(start)
            if outdir is None:
                res.stop_reason = StopReason.OUTPUT_EXISTS
                # ★ `ok` 的语义是「**这不是异常**」（§19.1 `R-11`），`OUTPUT_EXISTS` 属于
                #   "安全停下" → 这一支必须和目录入口给出**同一个答案**。目录入口走
                #   `_final_state`（`reason.is_clean_stop` → True），而这里以前漏了赋值、
                #   保持 `res.ok=False`：同一个场景（输出目录已存在 + `conflict=skip`）
                #   单文件入口 `cli.is_done=False` → 退出码 1，目录入口 `True` → 退出码 0
                #   （`B-2026-083`）。**不是**改 `ok` 的语义，是让这条早返回分支跟上既有语义。
                res.ok = StopReason.OUTPUT_EXISTS.is_clean_stop
                blocked = getattr(self, "_skip_outdir", "") or ""
                res.stop_detail = (
                    f"输出目录已存在：{os.path.basename(blocked)}（冲突处理设为「跳过」）"
                    if blocked else "输出目录已存在（冲突处理设为「跳过」）"
                )
                return res
            layer = self._extract_layer(start, outdir, depth=PackLayer(1), password_hint=None)
            self._record_layer(res, layer)
            if not layer.ok:
                res.stop_reason = layer.reason or StopReason.EXTRACT_FAILED
                res.stop_detail = layer.note
                res.ok = False
                return res
            workdir = outdir
            res.output_dir = outdir
            # 源包是第 1 层；它的产物目录里再找到的包就是第 2 层
            next_layer = PackLayer(2)

        reason, detail = self._pierce(workdir, res, start_layer=next_layer)
        res.stop_reason, res.stop_detail, res.ok = self._final_state(res, reason, detail)
        self._reconcile_leftover(res)
        self._report_leftover(res)
        return res

    @staticmethod
    def _final_state(res: PierceResult, reason: StopReason,
                     detail: str) -> tuple[StopReason, str, bool]:
        """把状态机给出的结论与**层里记录的事实**对齐，再定 `ok`（`B-2026-073`/`B-2026-074`）。

        不变量（本卡钉死的就是这一条）：**只要有一层 `ok=False`，最终结果就不许是
        "一切正常"** —— 既不许 `ok=True`，也不许 `stop_reason` 落在"安全停下"那一类。

        正常情况下 `_prefer_failure` 已经保证了它（真失败不会被安全停下顶掉）；这里再统一
        收口一次，是为了让"以后有人新加一条提前 return 的 clean stop"也破不了这条不变量。
        破了它的直接后果不是文案难看，而是 `cli.is_done()` 在**存在失败层**时判成功
        （`B-2026-074` 变体②：`conflict=skip` + 输出目录已存在 + 同层坏包）。

        `ok` 的语义照旧是「这不是异常」（§19.1 `R-11`），所以这里只把它**改小**、
        从不改大：`ok` 仍然等于"最终原因属于安全停下"，只是不再允许"有失败层还说安全"。
        """
        failed = next((ly for ly in res.layers if not ly.ok), None)
        if failed is not None and reason.is_clean_stop:
            reason = (failed.reason if failed.reason and not failed.reason.is_clean_stop
                      else StopReason.EXTRACT_FAILED)
            detail = failed.note or detail
        return reason, detail, reason.is_clean_stop

    def _report_leftover(self, res: PierceResult) -> None:
        """撞层数上限时，把**最终确实还留在盘上**的包点名一次（B-2026-013）。

        为什么不在撞上限那一刻点名：那一刻的名单只是快照，后面 `todo` 队列还会把其中
        几个解开并删掉，于是日志会点出事后并不存在的包。**日志和界面必须说同一件事**，
        所以两边都取对账之后的最终态。

        其它停止原因各自在**自己的分支**里已经点过名了：`ALREADY_VISITED` 有那句
        「和「X」内容相同，已跳过（仍在 …）」、`AMBIGUOUS` 有那句「这一层共 N 个候选…」
        （`B-2026-085` 补上的）、"队列里的包已不在"也有自己那句警告（`B-2026-084`）——
        所以这里不重复报。⚠ 但别把这句话读成"所有 leftover 都会被点名"：这里只管
        `MAX_DEPTH` 那一种，其余原因的名单要靠 `leftover` 计数（界面备注与
        `describe_pierce_result`）才看得见。
        """
        if not res.leftover or res.stop_reason is not StopReason.MAX_DEPTH:
            return
        names = "、".join(os.path.basename(p) for p in res.leftover[:4])
        more = "…" if len(res.leftover) > 4 else ""
        self._log(
            f"⚠ 已达到层数上限，还有 {len(res.leftover)} 个压缩包未解压，仍在输出目录里：{names}{more}"
        )

    def _reconcile_leftover(self, res: PierceResult) -> None:
        """对账：把 `leftover` 里**本进程自己解完并删掉**的条目剔掉（B-2026-013）。

        为什么需要：`_pierce` 撞层数上限（或判 ambiguous）那一刻，是把 workdir 里
        **当时看得见**的包整个记下来的；随后 `todo` 队列又把其中几个正常解开、并按
        默认设置删掉 —— 账记了没销。于是界面会说"有 3 个包没解开：E1.zip、L1.zip、
        L2.zip"，用户照着去输出目录找只找得到 1 个；日志里那几行"还留在输出目录里"
        同样点名了盘上没有的包，"有东西没解开"这个警告因此不可信。

        **在 `run()` 返回之前统一重算一次**，而不是在每个 if 分支里就地销账 ——
        那些分支根本不知道后面还会解掉谁（记账时刻早于结果时刻，中间还会删文件）。

        ★ 判据是"**是不是本进程删的**"（`self._removed`），不是"还在不在盘上"
        （`B-2026-084`）：以前用存在性当代理，于是**外部**删掉 / 移走的那一条也被
        当成"我们解完删了"而静默销账 —— 与 `_pierce` 里那句「已处理过，无需重复」
        是同一个谎的两半。现在三种结局分开：还在盘上 → 留；不在了但我们没删过
        → 留（那是没解释的消失，必须报）；不在了且本进程删过 → 才是真销账。
        """
        if not res.leftover:
            return
        base = res.output_dir
        kept: list[str] = []
        for rel in res.leftover:
            if os.path.isabs(rel) or not base:
                full = rel
            else:
                full = os.path.join(base, rel)
            if os.path.exists(full):
                kept.append(rel)
                continue
            if os.path.normcase(os.path.abspath(full)) not in self._removed:
                kept.append(rel)
        res.leftover = kept

    @staticmethod
    def _note_leftover(res: PierceResult, base: str, *paths: str) -> None:
        """登记"这个东西没解"（去重，按 `base` 算相对路径）。

        为什么要它：`ALREADY_VISITED` / `MAX_DEPTH` / `AMBIGUOUS` 都会 `ok=True`，
        光看 `ok` 分不出"真的解完了"和"还剩东西没解"。登记下来之后
        `PierceResult.partial` 才成立，界面才能显示"部分完成"。
        账目在 `run()` 返回前由 `_reconcile_leftover` 对账 —— 只销**本进程删过的**那些
        （`B-2026-084`：外部删掉/移走的那一条也要留在账上，见 §5.3）。
        """
        for p in paths:
            if not p:
                continue
            try:
                rel = os.path.relpath(p, base) if base else p
            except ValueError:                      # 跨盘符
                rel = os.path.basename(p)
            if rel not in res.leftover:
                res.leftover.append(rel)

    def _pierce(self, workdir: str, res: PierceResult, *,
                start_layer: PackLayer) -> tuple[StopReason, str]:
        """从 workdir 开始继续往下穿透，返回 (停止原因, 细节)。

        `start_layer` 是**这个工作目录里那些包的包层**（源包解出来的产物目录 → 2；
        用户直接拖进来的目录 → 1）。调用方显式给出来，不再用 `len(res.layers) + 1`
        现算 —— 那是"动作序号"，两者只在单链场景下碰巧相等。

        **同一层有多个互不相关的包时逐个都解**：当前这条链先走，其余的进 `todo`
        排队（记着"哪个目录、第几层、只解哪个包"），这条链走完了再回来接着解。
        失败也不是立刻收工——先记下**最该报的那个**原因（真失败优先于安全停下，
        见 `_prefer_failure`），把同层其它包解完，最后再报。

        **`depth` 的含义是"当前这个工作项的包层"**：入队时连同包层一起存下来，
        `next_task()` 换回来 —— 所以同层的第 2 个包**复用同一个包层**，
        不消耗层数预算（B-2026-033 成因②）。
        """
        depth: PackLayer = start_layer
        todo: list[tuple[str, PackLayer, str | None]] = []
        only: str | None = None
        failure: tuple[StopReason, str] | None = None
        # ★ 「入口层」= **用户直接拖进来的那个目录里的那些包**（只有目录入口才有：
        #   `_run` 里目录入口给 `start_layer=1`，单文件入口从 2 开始）。
        #   它们的落点要跟**单文件入口**同一口径（`output_root` 优先，`B-2026-082`），
        #   见下面调 `_outdir_for` 的那一处。判据 `depth == entry_layer` 是充分的：
        #   `todo` 里排队的同层兄弟带着**当时的** `depth`，而 `depth` 只会在解完一层后 +1
        #   —— 所以 `depth` 回到 1 时 `workdir` 必定还是那个入口目录。
        entry_layer: PackLayer | None = PackLayer(1) if start_layer == PackLayer(1) else None

        def next_task() -> bool:
            """还有排队的包就切过去，返回 True。"""
            nonlocal workdir, depth, only
            if not todo:
                return False
            workdir, depth, only = todo.pop(0)
            return True

        while True:
            self.clean_delete_names(workdir)
            # 已经在排队的包（同层兄弟）：它们各自的包层由队列带着，pick 时**要跳过** ——
            # 再捡一次就会按"更深一层"记账，同层第 2 个包白白吃掉一层预算（B-2026-033 成因②）
            queued = {t for _wd, _d, t in todo if t}
            if only is not None:
                pick = PickResult(path=only)   # 队列里指定的那个包
                only = None
            else:
                pick = self.pick_target(workdir, skip=queued)

            if not pick.found:
                if self._cancelled():
                    # 用户点了「停止」（可能是在"往下找"的过程中点的）：必须如实报
                    # CANCELLED，**不能**顺着下面报"没有可解压的压缩包" —— 那就成了
                    # "取消被显示成完成"（B-2026-014 的老病）。
                    # ★ 但**已发生的真失败优先**（B-2026-073/074 的不变量：有失败层就不许
                    #   报"一切都好"）：只报 CANCELLED 会得到 ok=True，而 `res.layers` 里
                    #   躺着 ok=False —— 那种"用户主动停止"并不该把失败一起盖掉。
                    #   安全停下（含 CANCELLED 自己）之间仍然先到先得。
                    if failure is not None and not failure[0].is_clean_stop:
                        return failure
                    return (StopReason.CANCELLED, "")
                if pick.ambiguous:
                    total = len(pick.ambiguous)
                    names = "、".join(os.path.basename(p) for p in pick.ambiguous[:4])
                    rest = total - len(pick.ambiguous[:4])
                    more = f"，另有 {rest} 个未列出" if rest > 0 else ""
                    # ★ 这一支以前**一句日志都不打**，`stop_detail` 又只说前 4 个
                    #   （`B-2026-085`）：leftover 里躺着 8 条、日志里 0 条、原因里 4 个 ——
                    #   用户与脚本都看不出"一共几个、还有几个没说出来"。
                    #   总数与"未列出"个数必须出现在**日志**（用户看得到的那条）与
                    #   `stop_detail`（界面备注 / CLI 的原因行）里，且两处同值。
                    self._log(f"⚠ 这一层共 {total} 个候选（都是伪装包）{more}，"
                              f"分不清先解哪一个，已停止：{names}")
                    self._note_leftover(res, res.output_dir or workdir, *pick.ambiguous)
                    # 子目录里还有疑似伪装包没解时一并点名（B-2026-076）：这一支报的是
                    # AMBIGUOUS，但"没解的东西"不止本层那几个候选。
                    if pick.unfinished:
                        self._note_leftover(res, res.output_dir or workdir, *pick.unfinished)
                    failure = _prefer_failure(failure, StopReason.AMBIGUOUS,
                                              f"共 {total} 个候选{more}：{names}")
                    if next_task():
                        continue
                    return failure
                if pick.search_incomplete:
                    # **没找完**（子目录太多 / 读不了）：以前这种情况和"真的没有包"
                    # 长得一模一样（都报 NO_ARCHIVE → 界面绿色「完成」），盘上却还躺着包
                    # （B-2026-033 成因① / B-2026-037）。如实说，并把已经找到的登记下来。
                    self._note_leftover(res, res.output_dir or workdir, *pick.unfinished)
                    failure = _prefer_failure(failure, StopReason.SEARCH_INCOMPLETE,
                                              pick.search_detail)
                    if next_task():
                        continue
                    return failure
                if next_task():
                    continue
                return failure or (StopReason.NO_ARCHIVE, "")

            # ★ 取到候选之后、**真正开解之前**再独立查一次取消（`B-2026-081`）。
            #   上面那个取消检查长在 `not pick.found` 分支里 —— 只有"这一层没候选"才轮到它；
            #   而 `pick_target` 借来的取消钩子长在 `probe.find_archives_below` 的
            #   `while stack:` **内部**，`stack` 来自子目录列表：目录里**一个子目录都没有**时
            #   那个钩子一次都不会被调用（实测：无子目录 + cancel 恒真 → 第 1 层照样解、
            #   产物照落盘；只加一个**空**子目录就立刻停）。于是"当前这条套娃链"会一路解到底
            #   （几 GB 级），而默认 `flatten_single_child=True` 会主动拆掉包裹目录 ——
            #   "当前目录里没有子目录"是套娃的**常态**，触发面比"特例"宽。
            #   ⚠ 口径与上面那个分支一致：**已发生的真失败优先**（`B-2026-073/074` 的不变量：
            #     只报 CANCELLED 会得到 ok=True，而 `res.layers` 里躺着 ok=False）。
            if self._cancelled():
                if failure is not None and not failure[0].is_clean_stop:
                    return failure
                return (StopReason.CANCELLED, "")

            # ★ 层数预算按**包层**判，而且只在"这一层真有包要解"时才判。
            #   以前这个判断在循环开头、拿"上一轮解完 +1"的 `depth` 去比，于是同层
            #   第 2 个包解完之后会假报一次「达到层数上限」，而 leftover 对账后是空的
            #   —— 日志里字面自相矛盾（B-2026-033 成因②）。
            #   现在：没有包要解 = 不是"撞上限"，而是正常到底（NO_ARCHIVE）。
            if depth > self.max_depth:
                # 层数上限是"提前收工"，输出目录里会**留下这一层的包**没解。
                # 不明说的话，用户看到的就是"显示完成、目录里却躺着个 zip"（报过这个）。
                # 点名的就是**这一刻真要解、但被预算挡住**的那些包；最终还剩下谁，
                # 由 `run()` 结尾 `_reconcile_leftover` 对账之后统一点名（B-2026-013）。
                targets = pick.targets or ([pick.path] if pick.path else [])
                self._log(f"⚠ 达到层数上限（{self.max_depth} 层），"
                          f"这一层的压缩包不再往下解压")
                self._note_leftover(res, res.output_dir or workdir, *targets)
                failure = _prefer_failure(failure, StopReason.MAX_DEPTH,
                                          f"已达上限 {self.max_depth} 层")
                if next_task():
                    continue
                return failure

            # 多个独立候选：第一个现在就走，其余的排队（同目录、同层号）
            if len(pick.targets) > 1:
                names = "、".join(os.path.basename(p) for p in pick.targets[:6])
                self._log(
                    f"[第{depth}层] 这里有 {len(pick.targets)} 个压缩包，会依次全部解压：{names}"
                )
                for extra in pick.targets[1:]:
                    todo.append((workdir, depth, extra))

            # ★ 「没找完」但**已经找到了候选**：先把找到的解完，最后如实报
            #   （以前这种情况一个都不解、让用户去整理摆放 —— B-2026-040/042）。
            #   注意别覆盖更早的 failure：第一个失败原因才是要报给用户的那个
            #   （`_prefer_failure`：真失败优先，安全停下先到先得 —— B-2026-073/074）。
            if pick.search_incomplete:
                # 「有东西没解」要登记进 leftover（B-2026-076：子目录里疑似藏了内嵌压缩包
                # 的文件也走这条 —— `pick.unfinished` 非空时结果必须是"部分完成"，
                # 界面的备注才会点名它们，而不是报"完成"）。
                if pick.unfinished:
                    self._note_leftover(res, res.output_dir or workdir, *pick.unfinished)
                was_empty = failure is None
                failure = _prefer_failure(failure, StopReason.SEARCH_INCOMPLETE,
                                          pick.search_detail)
                if was_empty:
                    self._log(f"⚠ {pick.search_detail}，先把已经找到的解完")

            target = pick.targets[0] if pick.targets else pick.path
            assert target is not None

            if not os.path.exists(target):
                # 队列里排着的包已经不在了 —— 这里有**三种语义完全不同**的情形，
                # 以前无条件合并成一句「已处理过，无需重复」（`B-2026-084`）：
                #   (a) 本进程刚作为**中间包**解完并删掉的 → 队列里的一条过期记录；
                #   (b) **外部**删掉 / 移动的（杀软隔离、同步盘、用户自己动的）；
                #   (c) 暂时不可达的（网络盘抖动）。
                # 实测（CLI 端到端）：同层两个包，解第一个的窗口里删掉第二个 →
                # 日志照样写「已处理过，无需重复：B.zip」、`1/1 成功`、退出码 0，
                # 而 `B` 的产物根本不存在；**不注入的对照组也是 0** —— 两者无法区分，
                # 脚本化调用者拿到的是"全部成功"这个错误结论。
                # 所以只有 (a)（下面这一支）允许静默：(b)(c) 要记账 + 说清是哪一种
                # （`R-13`：判据要有区分力），而且**不许**变成"全部成功"—— 按
                # `_extract_one` 那条既有的「源文件不在了」口径报 `EXTRACT_FAILED`
                # → `ok=False` → 界面「失败」/ CLI 退出码 1。
                #
                # 作者 2026-09-20 的 10 万级实测（(a) 的来历）：第 3 层那份列表里的
                # 02-…zip 早已解完并删掉，稍后又被扫到 → 引擎报「系统找不到指定的文件」
                # → 界面还提示"没解开，请手动再解一次"。那不是失败，是队列里的一条过期记录。
                if os.path.normcase(os.path.abspath(target)) in self._removed:
                    self._log(f"已处理过，无需重复：{os.path.basename(target)}")
                    if next_task():
                        continue
                    return failure or (StopReason.NO_ARCHIVE, "")

                base = res.output_dir or workdir
                self._note_leftover(res, base, target)
                gone = (f"{os.path.basename(target)} 已不在（可能被其它程序移动或删除），"
                        f"这一份没能解压")
                self._log(f"⚠ 队列里的 {os.path.basename(target)} 已不在"
                          f"（可能被其它程序移动或删除）")
                failure = _prefer_failure(failure, StopReason.EXTRACT_FAILED, gone)
                if next_task():
                    continue
                return failure

            fp = self._fingerprint(target)
            known = self._visited.get(fp)
            if known:
                # ★ 指纹只是**预筛**，碰撞之后必须逐字节确认（`B-2026-080`）：
                #   只看首尾各 64KB 时，两个 215040 字节的 `tar` 只要差异落在**中间**
                #   就得到同一个指纹 —— 第二个包被判成"已解过"、内容没落盘，
                #   日志还写「内容相同」（假话）。三种结局分开：
                #     ① 有任何一个已登记的包与它**逐字节相同** → 真的解过，跳过；
                #     ② 那些包**已经不在盘上**（被当中间包删了）→ 确认不了，仍然跳过
                #        （防死循环的机制不许失效），但文案降级成「疑似」—— 不许断言
                #        一件自己没验过的事（`R-13`：判据用词要有区分力）；
                #     ③ 全都还在、且逐字节**都不同** → 根本不是同一个包，照解。
                #   同一物理文件走快速路：`probe._identity` 相等就不必读一个字节
                #   （8.3 短名 / subst 盘符这类"两条路径一个文件"，全文比较会白读
                #   一遍 11GB 分卷）。
                # 文案注意：界面日志按**行首标记**上色（`✔/✘/◑/⚠/▶`，见
                #   `ui/app.py::log_color`），所以这里必须 ⚠ 开头 —— 这是唯一的硬约束。
                #   正文用词**不再**影响颜色（B-2026-087 之前是"按文本子串上色"，
                #   正文里一旦出现「完成」就把整行染绿，那时还得规避那几个词）。
                base = res.output_dir or workdir
                rel = self._rel_to(target, base)
                my_id = probe._identity(target)
                same_as: str | None = None
                unverifiable = False
                for prev in known:
                    if not os.path.exists(prev):
                        unverifiable = True
                        continue
                    if probe._identity(prev) == my_id or self._same_content(target, prev):
                        same_as = prev
                        break
                if same_as is not None or unverifiable:
                    # 说清「和哪一个相同」+「它现在在哪」：但**确实有内容没解**，
                    # 所以必须给出用户找得到的相对路径 —— 只报一个裸文件名，
                    # 用户不知道去哪儿看。
                    if same_as is not None:
                        where = f"和「{self._rel_to(same_as, base)}」"
                        why = "内容相同"
                    else:
                        where = "和先前那个包"
                        why = "疑似内容相同（那个包已不在，没法逐字节确认）"
                    # 补一句用户视角的话：他不是"重复了"，他可能就是想要两份
                    self._log(f"⚠ {where}{why}，已跳过（仍在 {rel}）——要两份请单独解压它")
                    self._note_leftover(res, base, target)
                    failure = _prefer_failure(failure, StopReason.ALREADY_VISITED,
                                              f"{where}{why}，已跳过，仍在 {rel}")
                    if next_task():
                        continue
                    return failure
            self._visited.setdefault(fp, []).append(target)

            # ★ 入口层的落点跟**单文件入口**同一口径：`output_root` 优先（`B-2026-082`）。
            #   以前这里一律传 `parent=workdir`，而 `_outdir_for` 里 `parent` **优先于**
            #   `output_root` → 用户设的输出目录被**整层**忽略：实测目录入口 `output_root`
            #   里空空、产物落回源目录，而同一个包走单文件入口却正常落 `output_root`。
            #   手册 §5.3 对"入口那个源包"明写的是「走 `_outdir_for(start)`（解在包自己旁边，
            #   或用户指定的 `output_root`）」—— 目录入口的入口层就是那个"源包"。
            #   ⚠ 只有**入口层**放开这一条：更深层仍跟 `parent`（§5.3 定死的设计），
            #     否则每一层都往 `output_root` 里挤，同名目录只能靠 `(1)(2)` 区分
            #     （实测过 `示例 / 示例 (1) / 示例 (2)`）。
            #   ⚠ **没设 `output_root` 时照旧传 `parent=workdir`**：`_outdir_for(target)`
            #     不带 `parent` 会落到"包自己所在的那个子目录"，而 §5.3 定的基点是
            #     "当前工作目录"——子目录里找到的包要落在 `workdir` 下，不是落回那个子目录。
            entry_now = entry_layer is not None and depth == entry_layer
            outdir = self._outdir_for(
                target, parent=(None if (entry_now and self.output_root) else workdir))
            if outdir is None:
                failure = _prefer_failure(
                    failure, StopReason.OUTPUT_EXISTS,
                    f"{os.path.basename(target)}（冲突处理设为「跳过」）")
                if next_task():
                    continue
                return failure
            # 解压**之前**先记下父目录里有什么：重名=覆盖时父目录本来就有旧东西，
            # 不然"这一层解出来只有一个子目录"永远判不出来（套娃就是这么留下的）
            parent_before = self._snapshot(os.path.dirname(outdir) or ".")
            layer = self._extract_layer(target, outdir, depth=depth, password_hint=self._last_password)
            self._record_layer(res, layer)

            if not layer.ok:
                reason = layer.reason or StopReason.EXTRACT_FAILED
                detail = f"{os.path.basename(target)}：{layer.note}" if layer.note else os.path.basename(target)
                # ★ 真失败在这里入槽：它必须能盖掉此前那些"安全停下"（B-2026-073/074）
                failure = _prefer_failure(failure, reason, detail)
                if next_task():
                    continue
                return failure

            workdir = outdir
            # 中间包删掉了、上一层目录因此只剩这一个子目录 → 拆平它
            # （示例.mp4 这种三层套娃，不收壳就会留下 示例/示例/示例）
            if not os.path.exists(target):
                collapsed = self.collapse_shell(os.path.dirname(target) or ".", outdir,
                                                born_after=parent_before)
                if collapsed != outdir:
                    layer.outdir = collapsed
                    workdir = collapsed
                    if os.path.normcase(res.output_dir) == os.path.normcase(outdir):
                        res.output_dir = collapsed
                    self._log(f"[第{depth}层] 已整理多余的外层文件夹，内容移入 {os.path.basename(collapsed)}/")
            # 往下走了一层：这个包的**内容**（含里面可能有的包）是更深一个包层。
            # ⚠ 即使上面收了壳（工作目录又回到上一层），这一层也必须 +1 —— 收壳只是把
            #   内容搬到浅一点的目录里，包层没变浅（包里的包仍然是 +1 层）。
            depth = PackLayer(depth + 1)

    # -- 输出目录 ------------------------------------------------------

    def _outdir_for(self, archive: str, *, parent: str | None = None) -> str | None:
        """算出这一层的输出目录。

        * 设了 `output_root` 就统一解到那里（用户指定输出目录的场景）；
          否则解到压缩包同级目录。
        * **更深层跟着上一层走**：否则每一层都往 output_root 里挤，同名目录只能
          靠 `(1) (2)` 区分（实测 示例.mp4 三层套娃就这样产出 示例 / 示例 (1) / 示例 (2)）。
        * 冲突处理按 `conflict`：
            rename    → 自动加 (1)(2)…（默认）
            overwrite → 直接用已有目录
            skip      → 返回 None，调用方标为「已跳过」
        * **无扩展名的包**（`outdir` 会等于源文件自己的路径）→ 目录名加 `NOEXT_SUFFIX`
          错开，这样 `skip` 才挡得住（见 §5.4）。判据是"路径是否自撞"，不是"有没有扩展名"。
        """
        info = probe.classify_volume(archive)
        if info.kind is not probe.VolKind.NONE and info.is_split:
            stem = os.path.basename(info.base)
        else:
            stem = os.path.splitext(os.path.basename(archive))[0]
        # 清掉残留的「删」字，并把 .mp4 之类伪装扩展名去掉
        stem = probe.clean_delete_chars(stem)
        stem = os.path.splitext(stem)[0] or stem

        if parent:
            base = parent
        elif self.output_root:
            base = self.output_root
            try:
                os.makedirs(base, exist_ok=True)
            except OSError:
                base = os.path.dirname(archive) or "."
        else:
            base = os.path.dirname(archive) or "."
        outdir = os.path.join(base, stem)

        # ★ 无扩展名的包：`splitext` 剥不掉东西 → `stem` 就是全名 → **`outdir` 正好等于源文件
        #   自己的路径**。先把名字错开，否则下面「同名的是个文件」会把它改叫 `名字 (1)` 照解不误，
        #   `conflict=skip` 就永远挡不住（用户报的这条：选「跳过」还是解，且每跑一次多一份）。
        #   判据用「算出来的 `outdir` 和源包是不是同一个路径」，**不是**「有没有扩展名」：
        #   `stem` 被分卷判断、`clean_delete_chars`、两次 `splitext` 改过，而 `.zip`（点开头）、
        #   `a.`（点结尾）这类名字用 `probe.ext_of()` 判会漏判或误判；`abspath` 走的是系统
        #   自己的路径规范化，和下面 `os.path.exists()` 看到的是同一个身份。
        if os.path.normcase(os.path.abspath(outdir)) == os.path.normcase(os.path.abspath(archive)):
            stem = f"{stem}{NOEXT_SUFFIX}"
            outdir = os.path.join(base, stem)
            # ⚠ `stem` 这里**已经**带上 `NOEXT_SUFFIX` 了，别再拼一次（`B-2026-088`）：
            #   以前写的是 `{stem}_unpack/`，日志里就成了 `X_unpack_unpack/`，而盘上
            #   真实目录是 `X_unpack` —— 用户照着日志去目录里找会找不到（内容是对的，
            #   纯文案错误）。顺带：`smoke_core` 里那条断言还用「日志某处含这个子串」
            #   的宽松写法，被 `_extract_one` 那条**正确**的日志顶包 → 恒绿。
            self._log(f"无扩展名：{os.path.basename(archive)} → {stem}/")

        # ★ 同名的是个**文件**（不是已存在的输出目录）：正常情况换名字，**但 skip 要挡住**。
        #   典型就是 `x.zip` 旁边真有个叫 `x` 的**文件**（同名文件和文件夹在同一个目录里
        #   不能共存）。
        #   ⚠ 这里必须**先判 skip 再改名**（2026-09-21 修，攻击审计 BUG-3 的残余）：
        #   改名改的是 `outdir` 这个局部变量，改完它就是一条全新路径，下面
        #   `if os.path.isdir(outdir)` 的 skip 分支**再也进不去** —— 表现是用户选了
        #   「跳过」，程序照样解出一份 `x (1)/`，而且每跑一次多一份。
        #   `conflict=rename` / `overwrite` 两档用户要的就是"照解"，所以只有 skip 变行为。
        #   注意：**无扩展名的包不走这里** —— 它已经在上面用 `_unpack` 把名字错开了。
        if os.path.exists(outdir) and not os.path.isdir(outdir):
            if self.conflict == "skip":
                self._skip_outdir = outdir
                return None
            renamed = outdir
            i = 1
            while os.path.exists(renamed):
                renamed = f"{outdir} ({i})"
                i += 1
            self._log(
                f"同名的是文件而非文件夹，已重命名：{os.path.basename(outdir)} → "
                f"{os.path.basename(renamed)}"
            )
            outdir = renamed

        if os.path.isdir(outdir):
            if self.conflict == "skip":
                # 记下是哪个目录挡住了，好让日志说清楚（不然用户只看到一句"已存在"）
                self._skip_outdir = outdir
                return None
            if self.conflict != "overwrite":
                i = 1
                while os.path.exists(f"{outdir} ({i})"):
                    i += 1
                outdir = f"{outdir} ({i})"
        return outdir

    # -- 解压一层 ------------------------------------------------------

    def _extract_layer(
        self,
        archive: str,
        outdir: str,
        *,
        depth: PackLayer,
        password_hint: str | None,
    ) -> LayerResult:
        """解压一层。目标是"伪装视频"时，先切出内嵌的压缩包再解。"""
        started = time.monotonic()
        source, temp, error, reason = self.carve_source(archive, outdir)
        if error:
            self._log(f"[第{depth}层] {error}")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=time.monotonic() - started, note=error,
                reason=reason or StopReason.EXTRACT_FAILED,
            )
        try:
            return self._extract_one(
                source, archive, outdir, depth=depth, password_hint=password_hint, started=started
            )
        except OSError as exc:
            # ★ 一层里的文件系统错误 = **这一层**失败，不是整批崩掉（`B-2026-072`）。
            #   为什么这里还要再兜一道：`engine.extract()` 里那句 `os.makedirs` 已经改成
            #   返回失败（见 `engine.py::Extractor.extract`），但 `_extract_one` 里还有别的
            #   文件系统调用（产物快照 `_snapshot`、内容上提 `flatten_same_name_shells`、
            #   删中间包 `_remove_group`…），它们抛 `OSError` 时以前会一路冒到 `run()` ——
            #   而 `run()` 是**整批**的入口：一个包把批打断，同目录的其它包连试都不试。
            #   转成 `EXTRACT_FAILED` 之后，`_pierce` 的失败分支会照常 `next_task()` 接着解，
            #   与"引擎报错"走同一条出口（同一个原因、同一句备注格式）。
            #   ⚠ 只兜 `OSError`，**不是** `except Exception`：逻辑 bug 不许伪装成
            #   "这个包解不开"，那由 `run()` 顶层那一道如实报出（见 `_unexpected_failure`）。
            detail = f"处理这个包时出错：{exc}"
            self._log(f"[第{depth}层] {detail}")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=time.monotonic() - started,
                note=detail, reason=StopReason.EXTRACT_FAILED,
            )
        finally:
            # 临时的切片文件无论成败都要清掉，别在用户目录里留一个 1GB 的垃圾
            self._discard(temp)

    def _extract_one(
        self,
        source: str,
        archive: str,
        outdir: str,
        *,
        depth: PackLayer,
        password_hint: str | None,
        started: float,
    ) -> LayerResult:
        display = os.path.basename(archive)
        self._log(f"[第{depth}层] {display} → {os.path.basename(outdir)}/")
        self._layer_event(depth, archive, False)

        # ★ 喂引擎之前先核一眼源包还在不在（`B-2026-031`）：解压**中途**源包被删时，
        #   引擎只会回一句"系统找不到指定的文件"（退出码 2）—— 自己先判一次，
        #   备注就能直接说清是"源文件不在了"，而不是把引擎黑话丢给用户。
        if not os.path.exists(source):
            detail = "源文件不在了（可能已被删掉或移走）"
            self._log(f"[第{depth}层] {detail}：{display}")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=time.monotonic() - started,
                note=detail, reason=StopReason.EXTRACT_FAILED,
            )

        kind = self.ex.engine_for(source)
        # 一次 `7z l -slt` 同时拿到"第一个条目"与"加不加密"。
        # 以前是 `first_entry()` + `is_encrypted()` 各调一次，**每层白起一个引擎进程**
        # （两者内部都是 `Extractor.inspect`，而 `inspect` 没有缓存）。
        # 只验证第一个条目：大包（比如 11GB 的分卷 7z）逐个试密码时，
        # 整包 `t` 一遍会把每个候选密码都变成一次全盘读，代价不可接受
        info = self.ex.inspect(source)
        probe_entry = info.first_entry
        password: str | None = None
        # ★ 命中"变体密码"时要记回**用户写的那条**（`B-2026-057`）：`unlock()` 返回的
        #   `candidate.value` 可能是变体（去空格 / 全角转半角），而 `source_value` 是它
        #   出自的那条原文。只有"密码来自密码库的变体"这一条路要换 —— 沿用外层密码、
        #   用户手输的那两种，用户写的就是 `password` 本身，保持 None 即"照原样记"。
        remember_value: str | None = None
        origin = ""
        skipped: set[str] = set()

        # 空间：按**这个包自己声明的解压体积**判，而不是只看"还剩多少"（B-2026-043）。
        # `need == 0`（判不出体积：加密头、inspect 失败…）时它**退化成原来的绝对下限检查**，
        # 所以这一条同时覆盖两种情形 —— `_pierce` 里那道同义检查已经删掉
        # （同一件事只有一份实现）。放在试密码之前：余量不够就别白试几十万个密码。
        # ⚠ 声明值可以撒谎（体积炸弹的中央目录写小值），它只挡"误拖一个巨大的包"；
        #   防炸弹要靠解压过程中看**实际写出**的量（尚未实现，见 §4.4）。
        need = info.total_size
        if not self._has_space(os.path.dirname(outdir) or ".", need_bytes=need):
            detail = (f"这个包解出来约 {probe.human_size(need)}，"
                      f"解完会低于最低剩余空间 {self.min_free_gb:.2f} GB"
                      if need else f"剩余空间低于 {self.min_free_gb:.2f} GB")
            self._log(f"[第{depth}层] 空间不足，先不解：{detail}")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=time.monotonic() - started,
                note=detail, reason=StopReason.NO_SPACE,
            )

        # 0) 先判加密。未加密的包，引擎会忽略 -p，验证必然"成功"，
        #    不判就会把第一个候选密码报成"命中"，来源列全是假的。
        encrypted = info.encrypted
        if encrypted is False:
            origin = "无密码"
            self._log(f"[第{depth}层] 未加密，无需密码")
        else:
            # 1) 优先复用外层成功的密码——同一个包的多层常常用同一个密码
            if password_hint is not None:
                res = self.ex.test(source, password_hint, kind=kind, entry=probe_entry)
                if res.ok:
                    password, origin = password_hint, "沿用外层密码"
                    self._log(f"[第{depth}层] 沿用外层密码成功")
                else:
                    skipped.add(password_hint)

            # 2) 否则走密码库（跳过刚试过的那个）
            if password is None:
                un = unlock(self.vault, self.ex, source, kind=kind, skip=skipped,
                            workers=self.workers or None,
                            label=f"第{depth}层")   # 只给"找密码"的心跳当抬头用
                if not un.ok:
                    self._log(f"[第{depth}层] {un.summary()}")
                    # 3) 只有「密码全试完」才值得问用户；缺分卷/包损坏问也白问，
                    #    而且在无头场景下会把流程挂死。
                    #    （示例.rar 那种"该问却没问"的根因不在这里：那是**空密码候选**
                    #     在 WinRAR 里变成"请提示输入密码"、返回 12 被当成引擎报错，
                    #     现在空密码改走 7-Zip，unlock 会正常给出 EXHAUSTED。）
                    manual = None
                    asked = False
                    if un.worth_asking_user and self.ask_password:
                        asked = True
                        manual = self.ask_password(source, un)
                    if manual:
                        password, origin = manual, "手动输入"
                        self._log(f"[第{depth}层] 使用手动输入的密码")
                    else:
                        if un.problem is Problem.CANCELLED:
                            reason = StopReason.CANCELLED
                        elif asked:
                            # 问过了、用户没给（点了「跳过当前文件」或关掉弹窗）：
                            # 这不是"工具没辙"，是他自己选择跳过。状态里必须说清楚，
                            # 否则和"密码本试完了"混在一起，看着像软件坏了
                            # （用户报"这个文件不能正常处理"很可能就是这一幕）。
                            reason = StopReason.PASSWORD_SKIPPED
                        elif un.worth_asking_user:
                            reason = StopReason.PASSWORD_EXHAUSTED
                        else:
                            reason = StopReason.EXTRACT_FAILED
                        # 提示语要**说清是哪种没解开**：只有"密码本全试完"那种才值得建议他手动再解一次；
                        # 引擎报错（文件不见了 / 包损坏）说这句是误导 —— 作者实测里就撞到过：
                        # 中间包已经解完删掉、队列里那条过期记录又去解，于是提示他"拖进来单独解一次"。
                        # 两个分支**只在"为什么没解开"上不同**，文件在哪那句话写法统一成一句
                        # （以前这里是「仍在 …」、下面那句是「文件仍在 …」，同一件事两种措辞）；
                        # 下面那条以前还把「文件还在 …）」又拼了一遍 —— 括号不配对、信息重复，
                        # 而它是**直接进界面日志面板**的（pierce 里 `_log` 40 处、`_debug` 0 处，`B-2026-063`）。
                        if reason is StopReason.PASSWORD_EXHAUSTED:
                            self._log(
                                f"⚠ 未解开：{os.path.basename(archive)}"
                                f"（文件仍在 {outdir}）。输入正确密码后，可以重新添加它单独解压"
                            )
                        else:
                            self._log(
                                f"⚠ 未解开：{os.path.basename(archive)}——"
                                f"{un.stopped_reason or '解压失败'}（文件仍在 {outdir}）"
                            )
                        return LayerResult(
                            depth=depth, target=archive, outdir=outdir, ok=False,
                            seconds=time.monotonic() - started,
                            # 用户跳过时 note 留空：reason.label 已经写着"你跳过了这个包"，
                            # 再填一遍会在日志里变成"…（你跳过了这个包（没输入密码））"。
                            # 取消同理（`B-2026-070`）：reason.label 与 `summary()` 里的
                            # 状态词都是「已停止」，再填一遍就是"1 层，已停止（已停止）"。
                            note=("" if asked or reason is StopReason.CANCELLED
                                  else un.stopped_reason),
                            reason=reason,
                        )
                else:
                    password = un.password
                    origin = un.candidate.origin.label if un.candidate else "密码库"
                    if un.candidate is not None:
                        remember_value = un.candidate.source_value or None
                    self._log(f"[第{depth}层] {un.summary()}")

        outdir_before = self._snapshot(outdir)      # 覆盖模式下可能是非空目录，得先记下来
        # ★ 解压**中**监控磁盘余量（`B-2026-043`）：事前那道只能按"包**声明**的体积"拦，
        #   而炸弹包的中央目录写小值 —— 真兜底只能在写的过程中看**实际**掉下去多少。
        #   阈值沿用同一个 `min_free_gb`（同一条不变量的两个时刻，**不引入第二个概念**）；
        #   设成 0 / 负数 = 用户明确表示"我不管剩多少" → 关掉事中监控（没有触发点）。
        #   ⚠ 这是挂在 `Extractor` 上的可变状态，所以**只能串行解压**（Runner 就是串行的）；
        #     真要并行解压，得改成按进程/按调用传参。
        guard_need = int(self.min_free_gb * (1024 ** 3))
        self.ex.space_guard = (
            (os.path.dirname(outdir) or ".", guard_need) if guard_need > 0 else None
        )
        try:
            res = self.ex.extract(source, outdir, password, kind=kind)
        finally:
            self.ex.space_guard = None
        seconds = time.monotonic() - started
        if res.cancelled:
            # 用户点了停止：子进程已经被杀掉，产物可能不完整，如实说明
            self._log(f"[第{depth}层] 已停止")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=seconds, password_origin=origin, password=password or "",
                # note **留空**（`B-2026-070`）：原因由 `reason` 说，它的 label 与
                # `summary()` 里的状态词都是「已停止」—— 这里再填一遍，界面日志会变成
                # 「1 层，已停止（已停止）」。与上面「用户跳过时 note 留空」同一个道理。
                note="", reason=StopReason.CANCELLED,
            )
        if res.space_guard:
            # 我们**主动**拦下的（`B-2026-043`）：绝不显示成「完成」，也不与事前的
            # `NO_SPACE` 共用一句话（`R-13`）—— 那个是"没开始"，这个是"写到一半"。
            detail = (f"解压中磁盘可用空间低于设定的下限 {self.min_free_gb:.2f} GB，已中止，"
                      f"产物可能不完整；请检查「设置」里的「最低剩余空间」后重试")
            self._log(f"[第{depth}层] {detail}")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=seconds, password_origin=origin, password=password or "",
                note=detail, reason=StopReason.SPACE_GUARD,
            )
        if res.ok:
            # ★ 引擎说成功 ≠ 真解出东西了（`B-2026-030`）：输出目录在解压途中被删 / 被清理时，
            #   7z 有时仍报退出码 0，而这里以前只信返回码 —— 界面于是显示绿色「完成」，
            #   用户打开输出目录却是空的（实测稳定复现）。判据是"这一层**新增了东西**"，
            #   三个边界都要避开：
            #     * **合法的 0 条目空包**：`entry_count == 0` 时本来就没东西，不判；
            #       （加密头的 7z 也读不出条目，一并放过 —— 宁可漏报，不可误报）
            #     * **覆盖模式**：目录里本来就有旧文件、条目名集合没变，改看"目录里有没有东西"；
            #     * **内容上提**：所以校验必须在上提**之前**做（此刻 `outdir` 里就是这一层的产物）。
            if info.entry_count and not self._has_payload(outdir, outdir_before):
                detail = "引擎报成功，但输出目录里没有新增任何内容（可能被删掉或清理了）"
                self._log(f"[第{depth}层] {detail}")
                return LayerResult(
                    depth=depth, target=archive, outdir=outdir, ok=False,
                    seconds=seconds, password_origin=origin, password=password or "",
                    note=detail, reason=StopReason.EMPTY_OUTPUT,
                )
            # 只有真的用了密码才更新"外层密码"，否则未加密的中间层会把
            # 上一层的密码清成 None，后面加密的层就丢了提示
            if password is not None:
                self._last_password = password
                # 解压**真的成功**了才记住这个密码——比"验证通过"更可信，
                # 也是那个"越用越准"的飞轮真正转起来的地方。
                # ★ 记的是**用户写的那条**（`remember_value`，`B-2026-057`）：命中变体时
                #   记变体值会在密码本里凭空多一条用户没写过的密码，而他原来那条永远是 0 次。
                if self.vault.remember(remember_value or password):
                    self._log(f"[第{depth}层] 已记住此密码，下次优先尝试")
                elif self.vault.last_write_error:
                    # ★ 写盘失败必须**说出来**（`B-2026-055`）：这条是**跑批**那条路，
                    #   以前只记 `last_write_error`、而全仓的消费点只有界面那三处
                    #   （手动加 / 批量导入 / 记事本建空本）—— 跑批这条路一个都没有，
                    #   于是内存 `hits+1`、盘上没变、界面无提示，"越用越准"悄悄失效，
                    #   这次学到的新密码也一起丢。解压本身**照样是成功的**，所以只报一句、
                    #   不改 `res.ok`（用户不该因为密码本写不进去而以为包没解开）。
                    self._log(
                        f"[第{depth}层] 密码成功次数写入失败：{self.vault.last_write_error}"
                        f"（解压结果不受影响）"
                    )
            # 内容上提放在这里（而不是调用方），保证「第一层」和后续层行为一致——
            # 只放在 _pierce 里会让首次解压漏掉上提，留下 A/A 套娃。
            # `outdir_before` 让它只看"这一层解出来的东西"：重名=覆盖时目录里
            # 本来就有旧文件，以前那样判会导致永远不上提（用户报的套娃）。
            if self.flatten:
                lifted = self.flatten_same_name_shells(
                    outdir, outdir_before, overwrite=(self.conflict == "overwrite"))
                if lifted:
                    extra = f"（连提 {len(lifted)} 层同名空壳）" if len(lifted) > 1 else ""
                    self._log(f"[第{depth}层] 已整理多余的外层文件夹：内容移入：{lifted[0]}/ {extra}")
            self._log(f"[第{depth}层] 完成（{seconds:.1f}s）")
            # 清掉已经解开的包，省磁盘：
            #   第一层是用户自己拖进来的原文件 → 只有显式开了 remove_source 才删
            #   更深层是解压过程中冒出来的嵌套包（里面/里层.zip 之类）→ 默认就删
            if depth > 1:
                if self.remove_intermediate:
                    self._remove_group(archive)
            elif self.remove_source:
                self._remove_group(archive)
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=True,
                seconds=seconds, password_origin=origin, password=password or "",
            )

        note = "密码错误" if res.wrong_password else res.brief()
        if not res.wrong_password:
            # 引擎的**原话**比"退出码 2"有用得多（`B-2026-031`）：源包在解压途中被删时，
            # 它能给出"系统找不到指定的文件"，而 `brief()` 只有退出码 —— 用户既不知道
            # 是什么原因、也不知道该往哪儿查。
            tail = (res.tail or "").strip()
            if tail:
                note = tail.splitlines()[-1].strip() or note
        self._log(f"[第{depth}层] 失败：{note}")
        return LayerResult(
            depth=depth, target=archive, outdir=outdir, ok=False,
            seconds=seconds, password_origin=origin, password=password or "",
            note=note, reason=StopReason.EXTRACT_FAILED,
        )

    def _remove_group(self, archive: str) -> None:
        """删除原压缩包（分卷要整组删，否则留下孤儿分卷）。"""
        info = probe.classify_volume(archive)
        targets = [archive]
        if info.kind is not probe.VolKind.NONE and info.is_split:
            d = os.path.dirname(archive) or "."
            try:
                names = os.listdir(d)
            except OSError:
                names = []
            for name in names:
                other = os.path.join(d, name)
                vi = probe.classify_volume(other)
                if vi.kind is info.kind and os.path.normcase(vi.base) == os.path.normcase(info.base):
                    targets.append(other)
        for t in set(targets):
            try:
                os.remove(t)
                # 记下"这一笔是我们自己销的"：`_pierce` 判"队列里的包为什么不在了"
                # 与 `_reconcile_leftover` 销账都只认这个名单（`B-2026-084`）。
                self._removed.add(os.path.normcase(os.path.abspath(t)))
                self._log(f"已删除中间包：{os.path.basename(t)}")
            except OSError as exc:
                # 以前这里静默吞掉：用户看到目录里躺着个"本该删掉"的中间包，只能猜为什么。
                # 删不掉多半是被占用（杀软扫描 / 还在被别的程序打开）—— 说一句，别让他猜。
                self._log(f"⚠ 无法删除中间包：{os.path.basename(t)}（{exc.strerror or exc}）")


def describe_pierce_result(res: PierceResult) -> str:
    """给日志面板用的一段总结。"""
    lines = [f"穿透结束：{res.stop_reason.label}"]
    for layer in res.layers:
        mark = "✔" if layer.ok else "✘"
        lines.append(
            f"  {mark} 第{layer.depth}层 {os.path.basename(layer.target)}"
            + (f"  密码来自 {layer.password_origin}" if layer.password_origin else "")
            + (f"  {layer.note}" if layer.note else "")
        )
    if res.stop_detail:
        lines.append(f"  原因：{res.stop_detail}")
    # ★ leftover 计数（`B-2026-085`）：界面备注一直有那句「有 N 个包没解开（原因）：名字」，
    #   而 CLI 以前只打 `stop_detail`（只说第一条原因、一个数都不给）—— 同一个结果
    #   两条出口说的不是一件事。这里与 `pipeline._apply` 的备注**同一口径**（计数 + 前 3 个
    #   名字），这样"CLI 与 GUI 同值"是可以被断言钉住的。
    if res.leftover:
        lines.append(f"  有 {len(res.leftover)} 个包没解开（{res.stop_reason.label}）："
                     f"{'、'.join(res.leftover[:3])}")
    return "\n".join(lines)
