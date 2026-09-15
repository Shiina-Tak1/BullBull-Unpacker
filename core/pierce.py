"""递归穿透：把「解压出来的压缩包」继续解下去，直到没有可解的为止。

原版的流程（唯一文件→补 .zip、循环扫压缩包、失败即停）思路是对的，
但**缺少保险丝**，实盘上会变成磁盘炸弹或死循环。这里补了三个：

    1. 最大层数      —— 防 a.zip 里放 a.zip 的死循环
    2. 已访问指纹    —— 对每个目标记 (路径, 大小, mtime)，防同一个包反复解
    3. 剩余空间下限  —— 解压前检查磁盘余量，防把盘撑爆

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
from typing import Callable

from core import probe
from core.engine import Extractor
from core.vault import PasswordVault, Problem, UnlockResult, unlock

Logger = Callable[[str], None]
# 密码全部试完后，向上层（UI）要一个手动密码；返回 None 表示用户放弃这个任务
AskPassword = Callable[[str, "UnlockResult"], "str | None"]
# 逐层上报：(层号, 目标文件名, 是否已完成)。给界面做实时进度用。
OnLayer = Callable[[int, str, bool], None]


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

    @property
    def label(self) -> str:
        return {
            StopReason.NO_ARCHIVE: "目录里已没有可解压的压缩包",
            StopReason.AMBIGUOUS: "有多个压缩包且无法确定主包，交给你手动处理",
            StopReason.EXTRACT_FAILED: "解压失败",
            StopReason.PASSWORD_EXHAUSTED: "候选密码全部试完仍未命中",
            StopReason.PASSWORD_SKIPPED: "你跳过了这个包（没输入密码）",
            StopReason.MAX_DEPTH: "达到最大嵌套层数上限",
            StopReason.NO_SPACE: "磁盘剩余空间不足，主动中止",
            StopReason.ALREADY_VISITED: "这个包之前已经解过（防止死循环）",
            StopReason.CANCELLED: "用户中止",
            StopReason.OUTPUT_EXISTS: "输出目录已存在（按设置跳过）",
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
        )


@dataclass
class LayerResult:
    """一层穿透的结果。"""

    depth: int
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

    @property
    def max_depth_reached(self) -> int:
        return len(self.layers)

    def summary(self) -> str:
        return (
            f"{self.max_depth_reached} 层，{'完成' if self.ok else '未完成'}："
            f"{self.stop_reason.label}" + (f"（{self.stop_detail}）" if self.stop_detail else "")
        )


@dataclass
class PickResult:
    """在某个目录里挑出来的下一步目标。"""

    path: str | None = None
    ambiguous: list[str] = field(default_factory=list)
    note: str = ""
    # 同一层有**多个互不相关的包**时全放这里：调用方会逐个都解。
    # 旧行为是直接判 ambiguous 收工（"无法确定主包"），但用户的预期是"都解出来"
    # ——实测 shell2.zip 里躺着 1.tar/1111.7z/2222.zip/3333.zip/4444.rar 五个，
    # 旧逻辑解到这儿就停了。
    targets: list[str] = field(default_factory=list)

    @property
    def found(self) -> bool:
        return self.path is not None or bool(self.targets)


class Piercer:
    """递归穿透状态机。"""

    def __init__(
        self,
        extractor: Extractor,
        vault: PasswordVault,
        *,
        max_depth: int = 5,
        min_free_gb: float = 5.0,
        flatten_single_child: bool = True,
        remove_source: bool = False,
        remove_intermediate: bool = True,
        scan_appended: bool = True,
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
        # 认不认「前面垫了真视频、后面接压缩包」的那种伪装（1067.mp4）
        self.scan_appended = scan_appended
        # 不处理的扩展名（apk/iso…）：穿透到嵌套层时同样跳过，别把用户的安装包拆了
        self.exclude_exts = list(exclude_exts or [])
        self.output_root = output_root
        self.conflict = conflict
        self._log_fn = logger
        self.ask_password = ask_password
        self.on_layer = on_layer
        self._visited: set[tuple] = set()
        self._last_password: str | None = None

    # -- 工具 ----------------------------------------------------------

    def _layer_event(self, depth: int, target: str, done: bool) -> None:
        if self.on_layer:
            try:
                self.on_layer(depth, os.path.basename(target), done)
            except Exception:
                pass

    def _log(self, msg: str) -> None:
        if self._log_fn:
            try:
                self._log_fn(msg)
            except Exception:
                pass

    @staticmethod
    def _fingerprint(path: str) -> tuple:
        """内容指纹：大小 + 首尾各 64KB 的哈希。

        刻意**不含路径与 mtime**——嵌套解压出来的内层包每次落在新路径、mtime 也是新的，
        按路径做指纹等于形同虚设。同一份内容出现在哪里都该被认出来，
        这才是防「同一个包反复解」的有效判据。
        """
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
        `7z l 1067.mp4` 只会回 "Cannot open the file as archive"。
        切出来的临时文件用完就删，用户的原视频一个字节都不动。
        """
        if not self.scan_appended or not probe.looks_like_carrier(archive):
            return archive, None, "", None

        emb = probe.find_embedded(archive, cancel=self._cancel_flag())
        if emb is None:
            return archive, None, "", None

        try:
            total = os.path.getsize(archive) - emb.offset
        except OSError:
            return archive, None, "", None

        base_dir = os.path.dirname(outdir) or os.path.dirname(archive) or "."
        temp = os.path.join(base_dir, f".{os.path.basename(archive)}.carve.{emb.ext}")
        self._log(
            f"识别为伪装文件：{os.path.basename(archive)} 的 {emb.offset_text} 处有一个 "
            f"{emb.fmt.value.upper()}（{emb.how}），先切出 {probe.human_size(total)} 再解"
        )
        # 切出来的临时文件跟正式产物一样占地方，先看余量再动手
        if not self._has_space(base_dir, need_bytes=total):
            return archive, None, (
                f"切出内嵌包需要额外 {probe.human_size(total)}，"
                f"低于剩余空间下限 {self.min_free_gb} GB"
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

    @staticmethod
    def _leftover_names(directory: str, limit: int = 3) -> str:
        """目录里还剩哪些压缩包（层数上限/重复包这些"提前收工"的场合要说清楚）。"""
        try:
            names = [
                e.name for e in os.scandir(directory)
                if e.is_file()
                and (probe.detect_format(e.path).is_archive
                     or (probe.looks_like_carrier(e.path) and probe.find_embedded(e.path) is not None))
            ]
        except OSError:
            return ""
        return "、".join(names[:limit]) + ("…" if len(names) > limit else "")

    def _discard(self, temp: str | None) -> None:
        if not temp:
            return
        try:
            os.remove(temp)
            self._log(f"已清理临时切片：{os.path.basename(temp)}")
        except OSError:
            pass

    # -- 单层第 1 步：清理「删」字 --------------------------------------

    def clean_delete_names(self, directory: str) -> list[tuple[str, str]]:
        """把含「删」字的文件名还原（规避网盘检测的常见手法）。返回 [(旧, 新)]。"""
        renamed: list[tuple[str, str]] = []
        try:
            entries = list(os.scandir(directory))
        except OSError:
            return renamed

        for e in entries:
            if not e.is_file():
                continue
            cleaned = probe.clean_delete_chars(e.name)
            if cleaned == e.name:
                continue
            dst = os.path.join(directory, cleaned)
            if os.path.exists(dst):
                self._log(f"跳过重命名（目标已存在）：{e.name}")
                continue
            try:
                os.rename(e.path, dst)
            except OSError as ex:
                self._log(f"重命名失败：{e.name} → {cleaned}（{ex}）")
                continue
            renamed.append((e.name, cleaned))
            self._log(f"清理「删」字：{e.name} → {cleaned}")
        return renamed

    # -- 单层第 2 步：挑目标 -------------------------------------------

    def pick_target(self, directory: str) -> PickResult:
        """按优先级挑下一步要解的包。

        优先级（对应原说明里的四种情况）：
            B 标准压缩包 → C 分卷主卷（.part1/.001/.z01+zip）→ A 唯一无扩展名文件

        **多个互不相关的候选 = 全都解**（返回 `targets`），不再判 ambiguous 收工：
        套娃里经常是"好几个独立的包躺在一起"，用户要的就是都解出来。
        真正需要停的只有"多个候选但一个都认不出主次"那种伪装文件场景。

        分卷仍然按**文件名**归组（`probe.group_volumes`：.partN / .001 / .z01 / .r00），
        只把主卷送进引擎；**主卷不在场**（用户只拿到 part2）时跳过并说明，别去解次卷。
        """
        try:
            files = [e.path for e in os.scandir(directory) if e.is_file()]
        except OSError:
            return PickResult()

        if not files:
            return PickResult()

        groups = probe.group_volumes(files)   # zip / rar / 数字分卷（已按组分好，只留主卷）
        mains: list[str] = []
        for g in groups:
            if g.is_split and not os.path.isfile(g.main):
                # 主卷缺失：次卷单独解不了，明确说一声然后跳过（别再报"引擎报错"）
                self._log(
                    f"⚠ 缺主卷：{os.path.basename(g.main)} 不在，"
                    f"这一组 {g.count} 个分卷跳过"
                )
                continue
            mains.append(g.main)

        plains: list[str] = []                 # 7z / tar / gz / lz4 / 伪装包
        for p in files:
            if probe.classify_volume(p).kind is not probe.VolKind.NONE:
                continue
            if probe.detect_format(p).is_archive:
                plains.append(p)

        candidates: list[str] = mains + plains
        # 排除名单（apk/iso…）：穿透时也别去解它们。它们是 zip，引擎会照解不误，
        # 但用户的意图是"别动我的安装包/镜像"（设置里那一条同样管嵌套层）。
        if self.exclude_exts:
            kept: list[str] = []
            for cand in candidates:
                ext = probe.excluded_ext(cand, self.exclude_exts)
                if ext:
                    self._log(f"跳过 {os.path.basename(cand)}：.{ext} 在「不处理的文件类型」里")
                else:
                    kept.append(cand)
            candidates = kept

        if len(candidates) == 1:
            only = candidates[0]
            info = probe.classify_volume(only)
            note = ""
            # 只有真有多卷时才算分卷（单个 .rar/.zip 的 kind 看着像分卷，其实不是）
            group = next(
                (g for g in groups if os.path.normcase(g.main) == os.path.normcase(only)), None
            )
            if group is not None and group.is_split:
                note = probe.describe(probe.detect_format(only), info)
            return PickResult(path=only, note=note)

        if len(candidates) > 1:
            return PickResult(targets=sorted(candidates))

        # 情况 A2：目录里只有一个文件、头不是压缩包，但**身体里**藏着一个
        # （垫了真视频的伪装文件）。放在标准候选之后：目录里既有正常压缩包
        # 又有这种视频时，优先解正常压缩包，不能因为旁边躺着个视频就判"无法确定主包"。
        if not candidates and self.scan_appended:
            carriers = [
                p for p in files
                if probe.looks_like_carrier(p) and probe.find_embedded(p) is not None
            ]
            if len(carriers) == 1:
                return PickResult(path=carriers[0], note="伪装文件里内嵌的压缩包")
            if len(carriers) > 1:
                return PickResult(ambiguous=carriers)

        # 情况 A：目录里只有一个文件、且没有能识别的扩展名 → 靠 magic 判断
        if len(files) == 1:
            only = files[0]
            fmt = probe.detect_format(only)
            if fmt.is_archive:
                return PickResult(path=only, note=f"无扩展名但头部是 {fmt.value}，直接交给引擎")
        return PickResult()

    # -- 单层第 3 步：内容上提 -----------------------------------------

    @staticmethod
    def _snapshot(directory: str) -> set[str]:
        """记下目录里现在有哪些条目（用来区分"这次解出来的"和"原来就在的"）。"""
        try:
            return {e.name for e in os.scandir(directory)}
        except OSError:
            return set()

    @staticmethod
    def flatten_single_child(directory: str, born_after: "set[str] | None" = None,
                             overwrite: bool = False) -> str | None:
        """这一层**解出来**只有一个文件夹时，把它的内容提到上一层，避免 A/A/A 套娃。

        关键在"这一层解出来的"：重名=覆盖时目录里本来就有旧东西（上一次解出来的文件），
        以前按"整个目录只有一个子目录、且没有别的文件"来判断 → 只要目录非空就永远不上提，
        于是覆盖之后变成 `1067/内层/内容.txt` 这种套娃（用户报的就是这个）。
        现在用 `born_after`（解压**之前**的目录快照）把"新出现的条目"挑出来判断。

        `overwrite=True` 时允许覆盖同名条目（覆盖模式下这是用户的明确意图）；
        否则遇到同名就放弃上提——宁可不动，也不覆盖用户的东西。
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
        if Piercer._lift_child(directory, dirs[0].path, overwrite=overwrite):
            return dirs[0].name
        return None

    @staticmethod
    def _lift_child(directory: str, inner: str, overwrite: bool = False) -> bool:
        """把 inner 里的东西全部移到 directory，然后删掉空了的 inner。

        同名时：`overwrite=True` 且都是文件 → 直接替换；都是目录 → 递归合并；
        类型不一致（文件 vs 目录）→ 覆盖模式下删掉挡路的那个再搬；
        非覆盖模式一律放弃（返回 False，调用方就保持原样）。
        """
        moved = 0
        try:
            for e in list(os.scandir(inner)):
                dst = os.path.join(directory, e.name)
                if os.path.exists(dst):
                    if not overwrite:
                        return False
                    if e.is_dir() and os.path.isdir(dst):
                        if not Piercer._lift_child(dst, e.path, overwrite=True):
                            return False
                        continue
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
        return bool(moved)

    def collapse_shell(self, workdir: str, outdir: str,
                       born_after: "set[str] | None" = None) -> str:
        """把「中间包删掉后剩下的空壳层」拆平，返回下一层该用的工作目录。

        实盘例子：1067.mp4 = zip → zip → rar 三层套娃，每层解出来的目录都叫 1067。
        中间包按设置删掉之后，`1067/` 里就只剩一个刚解出来的 `1067/` 子目录——
        不收掉的话，用户拿到的是 `1067/1067/1067/内容`，前面两层还是空的。

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
        """从压缩包或文件夹开始，逐层穿透。"""
        res = PierceResult(ok=False, output_dir="")

        if os.path.isdir(start):
            workdir = start
            res.output_dir = start
        else:
            self._visited.add(self._fingerprint(start))
            if not self._has_space(os.path.dirname(start) or "."):
                res.stop_reason = StopReason.NO_SPACE
                res.stop_detail = f"剩余空间低于 {self.min_free_gb} GB"
                return res
            outdir = self._outdir_for(start)
            if outdir is None:
                res.stop_reason = StopReason.OUTPUT_EXISTS
                blocked = getattr(self, "_skip_outdir", "") or ""
                res.stop_detail = (
                    f"输出目录已存在：{os.path.basename(blocked)}（冲突处理设为「跳过」）"
                    if blocked else "输出目录已存在（冲突处理设为「跳过」）"
                )
                return res
            layer = self._extract_layer(start, outdir, depth=1, password_hint=None)
            res.layers.append(layer)
            if not layer.ok:
                res.stop_reason = layer.reason or StopReason.EXTRACT_FAILED
                res.stop_detail = layer.note
                res.ok = False
                return res
            workdir = outdir
            res.output_dir = outdir

        reason, detail = self._pierce(workdir, res)
        res.stop_reason = reason
        res.stop_detail = detail
        res.ok = reason.is_clean_stop
        return res

    def _pierce(self, workdir: str, res: PierceResult) -> tuple[StopReason, str]:
        """从 workdir 开始继续往下穿透，返回 (停止原因, 细节)。

        **同一层有多个互不相关的包时逐个都解**：当前这条链先走，其余的进 `todo`
        排队（记着"哪个目录、第几层、只解哪个包"），这条链走完了再回来接着解。
        失败也不是立刻收工——先记下第一个失败原因，把同层其它包解完，最后再报。
        """
        depth = len(res.layers) + 1
        todo: list[tuple[str, int, str | None]] = []
        only: str | None = None
        failure: tuple[StopReason, str] | None = None

        def next_task() -> bool:
            """还有排队的包就切过去，返回 True。"""
            nonlocal workdir, depth, only
            if not todo:
                return False
            workdir, depth, only = todo.pop(0)
            return True

        while True:
            if depth > self.max_depth:
                # 层数上限是"提前收工"，输出目录里会**留下这一层的包**没解。
                # 不明说的话，用户看到的就是"显示完成、目录里却躺着个 zip"（报过这个）。
                left = self._leftover_names(workdir)
                if left:
                    self._log(
                        f"⚠ 达到层数上限（{self.max_depth} 层）：{left} 还留在输出目录里——"
                        f"想继续解就把「最大嵌套层数」调大，再把它拖进来解一次"
                    )
                if failure is None:
                    failure = (StopReason.MAX_DEPTH, f"已达上限 {self.max_depth} 层")
                if next_task():
                    continue
                return failure

            self.clean_delete_names(workdir)
            if only is not None:
                pick = PickResult(path=only)   # 队列里指定的那个包
                only = None
            else:
                pick = self.pick_target(workdir)

            if not pick.found:
                if pick.ambiguous:
                    names = "、".join(os.path.basename(p) for p in pick.ambiguous[:4])
                    if failure is None:
                        failure = (StopReason.AMBIGUOUS, f"候选：{names}")
                    if next_task():
                        continue
                    return failure
                if next_task():
                    continue
                return failure or (StopReason.NO_ARCHIVE, "")

            # 多个独立候选：第一个现在就走，其余的排队（同目录、同层号）
            if len(pick.targets) > 1:
                names = "、".join(os.path.basename(p) for p in pick.targets[:6])
                self._log(
                    f"[第{depth}层] 这一层有 {len(pick.targets)} 个互不相关的包，逐个都解：{names}"
                )
                for extra in pick.targets[1:]:
                    todo.append((workdir, depth, extra))

            target = pick.targets[0] if pick.targets else pick.path
            assert target is not None

            fp = self._fingerprint(target)
            if fp in self._visited:
                self._log(
                    f"⚠ 这个包之前已经解过（防死循环），{os.path.basename(target)} 留在原处没动"
                )
                if failure is None:
                    failure = (StopReason.ALREADY_VISITED, os.path.basename(target))
                if next_task():
                    continue
                return failure
            self._visited.add(fp)

            if not self._has_space(workdir):
                if failure is None:
                    failure = (StopReason.NO_SPACE, f"剩余空间低于 {self.min_free_gb} GB")
                if next_task():
                    continue
                return failure

            outdir = self._outdir_for(target, parent=workdir)
            if outdir is None:
                if failure is None:
                    failure = (StopReason.OUTPUT_EXISTS,
                               f"{os.path.basename(target)}（冲突处理设为「跳过」）")
                if next_task():
                    continue
                return failure
            # 解压**之前**先记下父目录里有什么：重名=覆盖时父目录本来就有旧东西，
            # 不然"这一层解出来只有一个子目录"永远判不出来（套娃就是这么留下的）
            parent_before = self._snapshot(os.path.dirname(outdir) or ".")
            layer = self._extract_layer(target, outdir, depth=depth, password_hint=self._last_password)
            res.layers.append(layer)

            if not layer.ok:
                reason = layer.reason or StopReason.EXTRACT_FAILED
                detail = f"{os.path.basename(target)}：{layer.note}" if layer.note else os.path.basename(target)
                if failure is None:
                    failure = (reason, detail)
                if next_task():
                    continue
                return failure

            workdir = outdir
            # 中间包删掉了、上一层目录因此只剩这一个子目录 → 拆平它
            # （1067.mp4 这种三层套娃，不收壳就会留下 1067/1067/1067）
            if not os.path.exists(target):
                collapsed = self.collapse_shell(os.path.dirname(target) or ".", outdir,
                                                born_after=parent_before)
                if collapsed != outdir:
                    layer.outdir = collapsed
                    workdir = collapsed
                    if os.path.normcase(res.output_dir) == os.path.normcase(outdir):
                        res.output_dir = collapsed
                    self._log(f"[第{depth}层] 收掉空壳目录，产物上提到 {os.path.basename(collapsed)}/")
            depth += 1

    # -- 输出目录 ------------------------------------------------------

    def _outdir_for(self, archive: str, *, parent: str | None = None) -> str | None:
        """算出这一层的输出目录。

        * 设了 `output_root` 就统一解到那里（用户指定输出目录的场景）；
          否则解到压缩包同级目录。
        * **更深层跟着上一层走**：否则每一层都往 output_root 里挤，同名目录只能
          靠 `(1) (2)` 区分（实测 1067.mp4 三层套娃就这样产出 1067 / 1067 (1) / 1067 (2)）。
        * 冲突处理按 `conflict`：
            rename    → 自动加 (1)(2)…（默认）
            overwrite → 直接用已有目录
            skip      → 返回 None，调用方标为「已跳过」
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

        # ★ 同名的是个**文件**（不是已存在的输出目录）：换名字，不能算"已存在"。
        #   最容易踩的就是**无扩展名的压缩包**：`splitext` 剥不掉东西，stem 就是全名，
        #   于是 outdir 正好等于源文件自己的路径 →
        #     · conflict=skip     ：被当成"输出目录已存在"直接跳过（用户报的这条）
        #     · conflict=overwrite：往一个文件上 makedirs → FileExistsError 崩掉
        #   另外 `x.zip` 旁边真有个叫 `x` 的**文件**时也是同一类问题（同名文件和文件夹
        #   在同一个目录里不能共存），所以这里统一按"换名字"处理。
        if os.path.exists(outdir) and not os.path.isdir(outdir):
            renamed = outdir
            i = 1
            while os.path.exists(renamed):
                renamed = f"{outdir} ({i})"
                i += 1
            self._log(
                f"同名的是个文件（无扩展名的包常这样）：{os.path.basename(outdir)} → "
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
        depth: int,
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
        finally:
            # 临时的切片文件无论成败都要清掉，别在用户目录里留一个 1GB 的垃圾
            self._discard(temp)

    def _extract_one(
        self,
        source: str,
        archive: str,
        outdir: str,
        *,
        depth: int,
        password_hint: str | None,
        started: float,
    ) -> LayerResult:
        display = os.path.basename(archive)
        self._log(f"[第{depth}层] {display} → {os.path.basename(outdir)}/")
        self._layer_event(depth, archive, False)

        kind = self.ex.engine_for(source)
        # 只验证第一个条目：大包（比如 11GB 的分卷 7z）逐个试密码时，
        # 整包 `t` 一遍会把每个候选密码都变成一次全盘读，代价不可接受
        probe_entry = self.ex.first_entry(source)
        password: str | None = None
        origin = ""
        skipped: set[str] = set()

        # 0) 先判加密。未加密的包，引擎会忽略 -p，验证必然"成功"，
        #    不判就会把第一个候选密码报成"命中"，来源列全是假的。
        encrypted = self.ex.is_encrypted(source)
        if encrypted is False:
            origin = "无密码"
            self._log(f"[第{depth}层] 未加密，跳过密码库")
        else:
            # 1) 优先复用外层成功的密码——同一个包的多层常常用同一个密码
            if password_hint is not None:
                res = self.ex.test(source, password_hint, kind=kind, entry=probe_entry)
                if res.ok:
                    password, origin = password_hint, "沿用外层密码"
                    self._log(f"[第{depth}层] 沿用外层密码通过")
                else:
                    skipped.add(password_hint)

            # 2) 否则走密码库（跳过刚试过的那个）
            if password is None:
                un = unlock(self.vault, self.ex, source, kind=kind, skip=skipped, entry=probe_entry)
                if not un.ok:
                    self._log(f"[第{depth}层] {un.summary()}")
                    # 3) 只有「密码全试完」才值得问用户；缺分卷/包损坏问也白问，
                    #    而且在无头场景下会把流程挂死。
                    #    （4444.rar 那种"该问却没问"的根因不在这里：那是**空密码候选**
                    #     在 WinRAR 里变成"请提示输入密码"、返回 12 被当成引擎报错，
                    #     现在空密码改走 7-Zip，unlock 会正常给出 EXHAUSTED。）
                    manual = None
                    asked = False
                    if un.worth_asking_user and self.ask_password:
                        asked = True
                        manual = self.ask_password(source, un)
                    if manual:
                        password, origin = manual, "手动输入"
                        self._log(f"[第{depth}层] 采用手动输入的密码")
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
                        self._log(
                            f"⚠ 就此收工：{os.path.basename(archive)} 还在 "
                            f"{outdir} 里没解开（拿到密码后把它拖进来单独解一次即可）"
                        )
                        return LayerResult(
                            depth=depth, target=archive, outdir=outdir, ok=False,
                            seconds=time.monotonic() - started,
                            # 用户跳过时 note 留空：reason.label 已经写着"你跳过了这个包"，
                            # 再填一遍会在日志里变成"…（你跳过了这个包（没输入密码））"
                            note=("" if asked else un.stopped_reason),
                            reason=reason,
                        )
                else:
                    password = un.password
                    origin = un.candidate.origin.label if un.candidate else "密码库"
                    self._log(f"[第{depth}层] {un.summary()}")

        outdir_before = self._snapshot(outdir)      # 覆盖模式下可能是非空目录，得先记下来
        res = self.ex.extract(source, outdir, password, kind=kind)
        seconds = time.monotonic() - started
        if res.cancelled:
            # 用户点了停止：子进程已经被杀掉，产物可能不完整，如实说明
            self._log(f"[第{depth}层] 用户中止（已掐断引擎进程）")
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=False,
                seconds=seconds, password_origin=origin, password=password or "",
                note="用户中止", reason=StopReason.CANCELLED,
            )
        if res.ok:
            # 只有真的用了密码才更新"外层密码"，否则未加密的中间层会把
            # 上一层的密码清成 None，后面加密的层就丢了提示
            if password is not None:
                self._last_password = password
                # 解压**真的成功**了才记住这个密码——比"验证通过"更可信，
                # 也是那个"越用越准"的飞轮真正转起来的地方
                if self.vault.remember(password):
                    self._log(f"[第{depth}层] 已记住这个密码，下次会优先试")
            # 内容上提放在这里（而不是调用方），保证「第一层」和后续层行为一致——
            # 只放在 _pierce 里会让首次解压漏掉上提，留下 A/A 套娃。
            # `outdir_before` 让它只看"这一层解出来的东西"：重名=覆盖时目录里
            # 本来就有旧文件，以前那样判会导致永远不上提（用户报的套娃）。
            if self.flatten:
                lifted = self.flatten_single_child(
                    outdir, outdir_before, overwrite=(self.conflict == "overwrite"))
                if lifted:
                    self._log(f"[第{depth}层] 内容上提：{lifted}/ → 上一层")
            self._log(f"[第{depth}层] 完成（{seconds:.1f}s）")
            # 清掉已经解开的包，省磁盘：
            #   第一层是用户自己拖进来的原文件 → 只有显式开了 remove_source 才删
            #   更深层是解压过程中冒出来的嵌套包（里面/里层.zip 之类）→ 默认就删
            if depth > 1:
                if self.remove_intermediate:
                    self._remove_group(archive)
            elif self.remove_source:
                self._remove_group(archive)
            self._layer_event(depth, archive, True)
            return LayerResult(
                depth=depth, target=archive, outdir=outdir, ok=True,
                seconds=seconds, password_origin=origin, password=password or "",
            )

        note = "密码错误" if res.wrong_password else res.brief()
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
                self._log(f"已删除原文件：{os.path.basename(t)}")
            except OSError:
                pass


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
    return "\n".join(lines)
