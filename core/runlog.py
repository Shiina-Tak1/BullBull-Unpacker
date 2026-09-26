"""把界面日志面板的内容落到 `logs/run.log`（带轮转）。

为什么和 `ui.log` 分开：

    logs/ui.log    启动参数 + 未捕获异常（**小而关键**，出问题时先看它）
    logs/run.log   日志面板里显示过的**全部内容**（扫描、解压流水、汇总…）

两者的用途不一样：前者是"程序有没有崩"，后者是"这一趟到底干了什么"。
混在一起的话，几百行解压流水会把真正重要的异常淹掉。

轮转：超过 `MAX_BYTES` 就把 `run.log` 改名为 `run.log.1` 重新开一个
（只留两份，最多 2×2MB，不会无限长）。**原样落盘、不做任何替换**：面板里
"已复制密码：xxx"、引擎的命令行（`-p<密码>`）都按原文写进去 —— 密码在本工具里
从界面到文件**全程明文**（作者裁决：整个软件都不需要出现打码，`B-2026-032`）。
所以 `run.log` 里有真密码，**发出去之前自己看着办**。

★ `ui.log` 也换成这个类（2026-09-24，`TASK-060`）：它以前是纯 `open(target, "a")`，
而 `ui.log` 的 `_Tee` 会把**全部** stdout/stderr 镜像进去（含整段 traceback）——
无上限增长。现在同一个 `RunLog`、同一套 2MB ×2 的轮转（`ui.log` / `ui.log.1`）。

只用标准库，不依赖 Qt —— 这样能单独测。
"""

from __future__ import annotations

import os
import sys
import time

MAX_BYTES = 2 * 1024 * 1024          # 单个文件上限（约 2MB）
KEEP = 1                             # 轮转保留的旧文件份数（run.log.1）


class RunLog:
    """一个极简的追加写入器：只干"写入 + 轮转"两件事。"""

    def __init__(self, path: str, *, max_bytes: int = MAX_BYTES, keep: int = KEEP) -> None:
        self.path = path
        self.max_bytes = max_bytes
        self.keep = keep
        self._fh = None
        self._warned = False         # "打不开"只吼一次（见 `_open`）
        self._open()

    # -- 内部 --------------------------------------------------------

    def _open(self) -> None:
        try:
            os.makedirs(os.path.dirname(self.path) or ".", exist_ok=True)
            self._fh = open(self.path, "a", encoding="utf-8", buffering=1)
        except OSError as exc:
            self._fh = None
            self._warn(exc)

    def _warn(self, exc: OSError) -> None:
        r"""打不开日志文件时**说一句**（2026-09-24，`TASK-060`）。

        以前这里是彻底的静默（`except OSError: self._fh = None`）：用户看到的是
        "日志面板有内容、`run.log` 里却没有"，一个**静默丢日志**的洞 —— 而这恰恰是
        出问题时唯一能查的东西。现在写一句到 stderr（`ui.log` 的 `_Tee` 会把它抄进
        `ui.log`，终端里也看得见）。

        ⚠ 只吼一次：`_Tee` 的每一次 `write()` 都可能触发 `_open()` 重试，如果每次都打印，
        就会"打印 → 进 `_Tee` → 再打印"绕圈。`_warned` 是那道闸。
        """
        if self._warned:
            return
        self._warned = True
        try:
            print(f"[runlog] 打不开日志文件，这一轮不落盘：{self.path}（{exc}）",
                  file=sys.__stderr__ or sys.stderr)
        except Exception:                              # noqa: BLE001 - 连 stderr 都没有就算了
            pass

    def _rotate_if_needed(self) -> None:
        if self.max_bytes <= 0:
            return
        try:
            if not os.path.isfile(self.path) or os.path.getsize(self.path) < self.max_bytes:
                return
        except OSError:
            return
        if self._fh is not None:
            try:
                self._fh.close()
            except OSError:
                pass
            self._fh = None
        try:
            for i in range(self.keep, 0, -1):
                src = self.path if i == 1 else f"{self.path}.{i - 1}"
                dst = f"{self.path}.{i}"
                if os.path.isfile(src):
                    os.replace(src, dst)
        except OSError:
            pass
        self._open()

    # -- 对外 --------------------------------------------------------

    def write(self, tag: str, msg: str) -> None:
        """写一行（自动加时间戳；**原样写，密码也照写**；写不进去就静默放弃）。"""
        line = str(msg)
        stamp = time.strftime("%H:%M:%S")
        self.raw(f"{stamp}  [{tag}] {line}")

    def raw(self, line: str) -> None:
        if self._fh is None:
            self._open()
            if self._fh is None:
                return
        try:
            self._fh.write(line.rstrip("\n") + "\n")
        except OSError:
            pass
        self._rotate_if_needed()

    def chunk(self, text: str) -> None:
        """写**一段**（不加时间戳、不加换行、不拆行）—— 给 `ui.log` 的 stdout/stderr 镜像用。

        为什么需要它：`raw()` 的语义是"写一行"，而 `ui.log` 那边拿到的是一段段被 `_Tee`
        转发过来的原始输出（可能一次只有半行、也可能是整段 traceback）。轮转判据与
        `raw()` 一致：写完看一眼大小（`ui.log` 以前是无轮转的纯 append）。
        """
        if not text:
            return
        if self._fh is None:
            self._open()
            if self._fh is None:
                return
        try:
            self._fh.write(text)
        except OSError:
            pass
        self._rotate_if_needed()

    def flush(self) -> None:
        """把缓冲区刷下去（`_Tee.flush()` 用）；打不开就静默。"""
        if self._fh is not None:
            try:
                self._fh.flush()
            except OSError:
                pass

    def session_header(self, note: str = "") -> None:
        """每次启动写一段分隔（这样一份日志里能看出"哪几行是哪次运行"）。"""
        bar = "=" * 60
        self.raw(f"\n{bar}\n{time.strftime('%Y-%m-%d %H:%M:%S')} 启动{('：' + note) if note else ''}\n{bar}")

    def close(self) -> None:
        if self._fh is not None:
            try:
                self._fh.close()
            except OSError:
                pass
            self._fh = None

    @property
    def is_open(self) -> bool:
        return self._fh is not None
