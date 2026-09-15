"""把界面日志面板的内容落到 `logs/run.log`（带轮转）。

为什么和 `ui.log` 分开：

    logs/ui.log    启动参数 + 未捕获异常（**小而关键**，出问题时先看它）
    logs/run.log   日志面板里显示过的**全部内容**（扫描、解压流水、汇总…）

两者的用途不一样：前者是"程序有没有崩"，后者是"这一趟到底干了什么"。
混在一起的话，几百行解压流水会把真正重要的异常淹掉。

轮转：超过 `MAX_BYTES` 就把 `run.log` 改名为 `run.log.1` 重新开一个
（只留两份，最多 2×2MB，不会无限长）。**不写密码**：面板里"已复制密码：xxx"
这类行落盘时会被打码（日志经常要被发出来排错）。

只用标准库，不依赖 Qt —— 这样能单独测。
"""

from __future__ import annotations

import os
import re
import time

MAX_BYTES = 2 * 1024 * 1024          # 单个文件上限（约 2MB）
KEEP = 1                             # 轮转保留的旧文件份数（run.log.1）

# 面板/日志里可能出现密码的行：落盘前打码
_PASSWORD_LINE = re.compile(r"(已复制密码：|手动输入密码|命中 密码本：)(\S+)")
# 命令行里的 `-p<密码>`（引擎层已经打过一次码，这里是第二道保险：
# 万一以后有人直接从别处把命令行塞进日志，也不至于把密码写进文件）
_PW_ARG = re.compile(r"(?<!\S)(-p|--password=)(?!-)(\S+)")


def mask_secrets(line: str) -> str:
    """把日志行里可能出现的密码打码（日志是要发出来排错的，别带密码）。"""
    out = _PASSWORD_LINE.sub(lambda m: m.group(1) + "***", line)
    return _PW_ARG.sub(lambda m: f"{m.group(1)}***", out)


class RunLog:
    """一个极简的追加写入器：只干"写入 + 轮转 + 打码"三件事。"""

    def __init__(self, path: str, *, max_bytes: int = MAX_BYTES, keep: int = KEEP) -> None:
        self.path = path
        self.max_bytes = max_bytes
        self.keep = keep
        self._fh = None
        self._open()

    # -- 内部 --------------------------------------------------------

    def _open(self) -> None:
        try:
            os.makedirs(os.path.dirname(self.path) or ".", exist_ok=True)
            self._fh = open(self.path, "a", encoding="utf-8", buffering=1)
        except OSError:
            self._fh = None

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
        """写一行（会自动加时间戳、打码密码；写不进去就静默放弃）。"""
        line = mask_secrets(str(msg))
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
