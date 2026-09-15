"""解压引擎封装：Rar.exe / 7z.exe 的统一调用层。

三条设计铁律（对应技术路线里"别自己写解压内核"）：

  1. **不做 shell**：一律 `subprocess.run([...])` 列表传参，绝不 `shell=True`，
     避免密码里的空格/引号/`&` 变成命令注入或参数错位。
  2. **显式编码**：7z 输出按控制台代码页（中文机是 GBK），WinRAR 也是 GBK。
     统一走 `_decode()` 的多级回退，而不是让 subprocess 猜——中文文件名乱码
     90% 出在这里。
  3. **验证与解压分离**：先用 `t`（test）确认密码对不对，通过了再 `x`（extract）。
     密码错时解压到一半再清垃圾，代价差一个数量级。

退出码语义（已按官方文档核对）：

    7z :  0 成功 | 1 警告 | 2 致命错误 | 7 命令行错误 | 8 内存不足 | 255 用户中断
    rar:  0 成功 | 1 警告 | 2 致命 | 3 CRC 错 | 8 内存不足 | 11 密码错误 | 255 中断

注意 rar 的"密码错误"是 11（有时表现为 3），不要只看非零就当失败。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes
import os
import re
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from enum import Enum

from core import paths

# Windows：不弹黑框
CREATE_NO_WINDOW = 0x08000000

# 7-Zip：**只用工程里自带的那份**（7-Zip ZS，见 tools/7z/）。
#
# 为什么换掉系统装的：Stock 7-Zip 没有 lz4/lz5/brotli 这些编解码器，
# 实测 `Desktop.7z`（LZ5 压缩的 7z）在它手里每个文件都报 `Unsupported Method`；
# 而 7-Zip ZS 是它的**超集**（26.02 内核 + lz4/lz5/zstd/brotli/lizard，
# 还能把裸 .lz4/.lz5 帧当单文件压缩包读），所以按用户要求不再调 C 盘那份。
_BUNDLED_SEVENZIP = paths.resource_path("tools", "7z", "7z.exe")
# 给界面用：把"自带的那份在哪"暴露出去（设置页要拿它判断"这一份是不是自带的"）
BUNDLED_SEVENZIP = _BUNDLED_SEVENZIP
_SEVENZIP_CANDIDATES = (_BUNDLED_SEVENZIP,)
# 老配置里存的是系统装的 7-Zip：按约定不再用它，见到就当没配（回落到自带那份）
_LEGACY_STOCK_SEVENZIP = frozenset(
    os.path.normcase(p) for p in (
        r"C:\Program Files\7-Zip\7z.exe",
        r"C:\Program Files (x86)\7-Zip\7z.exe",
    )
)
_WINRAR_CANDIDATES = (
    r"C:\Program Files\WinRAR\Rar.exe",
    r"C:\Program Files (x86)\WinRAR\Rar.exe",
)

# 控制台输出编码回退链：先用 UTF-8，再按中文 Windows 的常见代码页
_ENCODINGS = ("utf-8", "cp936", "mbcs", "latin-1")


class EngineKind(str, Enum):
    SEVENZIP = "7z"
    WINRAR = "winrar"


@dataclass
class Engines:
    """本机可用的引擎路径（None 表示没找到）。"""

    seven_zip: str | None = None
    winrar: str | None = None
    # 这个 7-Zip 是不是**工程自带**的那份（ZS 版，支持 lz4/lz5/zstd…）。
    # 系统装的那份没有这些编码器，遇到相关包只会报 "Unsupported Method"，
    # 所以界面上要能把"是不是自带的那份"说出来。
    sevenzip_bundled: bool = False

    def path_of(self, kind: EngineKind | str) -> str | None:
        kind = EngineKind(kind)
        return self.seven_zip if kind is EngineKind.SEVENZIP else self.winrar

    def has(self, kind: EngineKind | str) -> bool:
        return self.path_of(kind) is not None

    def describe(self) -> str:
        z = self.seven_zip or "未找到"
        if self.seven_zip and not self.sevenzip_bundled:
            z += "（系统安装的，不支持 lz4/lz5）"
        r = self.winrar or "未找到（.rar 与伪装 rar 将无法处理）"
        return f"7-Zip: {z}\nWinRAR: {r}"


def _registry_winrar() -> str | None:
    """从注册表兜底找 WinRAR（装在非默认盘时用得上）。"""
    if os.name != "nt":
        return None
    try:
        import winreg  # type: ignore

        for root, key in (
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WinRAR"),
            (winreg.HKEY_CURRENT_USER, r"SOFTWARE\WinRAR"),
        ):
            try:
                with winreg.OpenKey(root, key) as k:
                    exe, _ = winreg.QueryValueEx(k, "exe64")
                    candidate = os.path.join(os.path.dirname(exe), "Rar.exe")
                    if os.path.isfile(candidate):
                        return candidate
                    if os.path.isfile(exe):
                        return exe
            except OSError:
                continue
    except Exception:
        pass
    return None


def find_engines(
    *,
    seven_zip: str | None = None,
    winrar: str | None = None,
) -> Engines:
    """探测引擎：优先用工程自带的那份 7-Zip（ZS 版）。

    **自带那份是按"本文件所在目录"算出来的相对位置**（`tools/7z/7z.exe`），
    不是写死的绝对路径 —— 所以整个文件夹拷到别的电脑（别的盘符、别的用户名）都照样能用，
    只要 `tools\\7z` 跟着一起走。

    找不到自带那份时的兜底顺序：系统安装的 7-Zip（`C:\\Program Files\\...`）→ PATH 里的 7z。
    这**不是**"优先用 C 盘那份"：只有自带那份不在了才会走到这里，而且会打上
    `sevenzip_bundled=False`，设置页据此提醒"这份不支持 lz4/lz5"。
    （老配置里手写的 `C:\\Program Files\\7-Zip\\7z.exe` 仍然被忽略——那是"明明带了
    自带版却去用系统版"的旧行为，正是当初 lz5 报错的来源。）
    """
    if seven_zip and os.path.normcase(seven_zip) in _LEGACY_STOCK_SEVENZIP:
        seven_zip = None
    bundled = False
    sz = seven_zip if seven_zip and os.path.isfile(seven_zip) else None
    if sz is not None:
        bundled = os.path.normcase(sz) == os.path.normcase(_BUNDLED_SEVENZIP)
    if sz is None:
        for p in _SEVENZIP_CANDIDATES:           # 自带的那份
            if os.path.isfile(p):
                sz = p
                bundled = True
                break
    if sz is None:
        # 自带那份没了（被误删、或者只拷了部分文件）：退回系统安装的
        for p in _LEGACY_STOCK_SEVENZIP:
            if os.path.isfile(p):
                sz = p
                bundled = False
                break
    if sz is None:
        sz = shutil.which("7z") or shutil.which("7za")
        bundled = False

    ra = winrar if winrar and os.path.isfile(winrar) else None
    if ra is None:
        for p in _WINRAR_CANDIDATES:
            if os.path.isfile(p):
                ra = p
                break
    if ra is None:
        ra = _registry_winrar()
    if ra is None:
        ra = shutil.which("rar")

    return Engines(seven_zip=sz, winrar=ra, sevenzip_bundled=bundled)


# --------------------------------------------------------------------------
# 编码
# --------------------------------------------------------------------------


def _decode(raw: bytes) -> str:
    """把引擎输出解码成文本。

    中文环境里 7z/rar 的输出**是混合编码的**：命令行/盘符那部分走 OEM 代码页（GBK），
    而包里的文件名常常是 UTF-8。整段只挑一种编码必有一边变乱码——实测
    `[SHANA]01.名侦探柯南…mp4` 会变成 `[SHANA]01.鍚嶄睛鎺㈡煰鍗楋…`，
    而乱码的文件名拿去做"只测这个条目"的便宜验证时，7z 匹配不到却说成功（假阳性）。
    所以这里**按行**挑编码：先 UTF-8（严格），失败了再退到 GBK/mbcs。
    """
    if not raw:
        return ""
    lines = []
    for line in raw.splitlines():
        lines.append(_decode_line(line))
    return "\n".join(lines)


def _decode_line(line: bytes) -> str:
    for enc in _ENCODINGS:
        try:
            text = line.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
        if "\ufffd" not in text:
            return text
    return line.decode("utf-8", errors="replace")


# --------------------------------------------------------------------------
# 运行结果
# --------------------------------------------------------------------------


@dataclass
class RunResult:
    ok: bool
    code: int
    output: str = ""
    timed_out: bool = False
    wrong_password: bool = False
    cancelled: bool = False          # 被用户中止（进程已杀掉）
    seconds: float = 0.0
    cmd: list[str] = field(default_factory=list)

    def brief(self) -> str:
        if self.cancelled:
            return "用户中止"
        if self.timed_out:
            return "超时"
        if self.wrong_password:
            return "密码错误"
        if self.ok:
            return f"成功({self.seconds:.1f}s)"
        return f"失败(退出码 {self.code})"

    @property
    def tail(self) -> str:
        """输出的最后几行，用于日志/详情页。"""
        lines = [ln for ln in self.output.splitlines() if ln.strip()]
        return "\n".join(lines[-3:])


def _nothing_matched(output: str) -> bool:
    """`7z t <包> <条目名>` 里那个条目名根本没匹配上时，7z 仍然返回 0。

    实测（26.01，中文 Windows）输出：
        No files to process
        Everything is Ok

        Files: 0
    所以"返回 0"不能当作密码正确，必须看这几个字。
    """
    low = output.lower()
    return ("no files to process" in low) or ("files: 0" in low)


# 「缺分卷 / 文件缺失」这类错误：不是密码问题，别让上层误判成"该问用户要密码"
_MISSING_VOLUME_HINTS = (
    "cannot find volume",
    "next volume",
    "missing volume",
    "找不到指定的文件",
    "the system cannot find the file",
    "no such file",
)


def _looks_like_missing_volume(output: str) -> bool:
    low = output.lower()
    return any(h in low for h in _MISSING_VOLUME_HINTS)


# 密码错误的文字特征（引擎版本不同，措辞会变，所以做关键字兜底）
_WRONG_PW_HINTS = (
    "wrong password",
    "incorrect password",      # ← RAR 的措辞，实测为 "Incorrect password for ..."
    "password is incorrect",
    "can not open encrypted archive",
    "cannot open encrypted archive",
    "encrypted archive",
    "crc failed",
    "checksum error",
    "密码错误",
    "错误的密码",
)

# 退出码语义（实测 + 官方文档）
#   7z : 0 成功 | 1 警告 | 2 致命/密码错 | 7 命令行错误 | 8 内存不足 | 255 中断
#   rar: 0 成功 | 1 警告 | 2 致命 | 3 CRC 错 | 8 内存不足 | 11 密码错误 | 255 中断
_RAR_WRONG_PASSWORD_CODE = 11


def _looks_like_wrong_password(text: str) -> bool:
    low = text.lower()
    return any(h in low for h in _WRONG_PW_HINTS)


# --------------------------------------------------------------------------
# 进程挂起（真暂停）
# --------------------------------------------------------------------------
#
# 想让"点了暂停就真的停下、再点继续接着跑"，只能让操作系统挂起引擎进程：
# 7z/Rar 没有暂停接口，而"解压到一半重来"的代价太大（大包要重新读一遍）。
# 挂起会冻结目标进程的所有线程、保留它的文件位置，恢复后从原处继续。
#
# 实现路线：**逐线程 SuspendThread**，而不是 NtSuspendProcess。
# 实测（本机 Win11 + Python 3.14）：`OpenProcess(PROCESS_SUSPEND_RESUME)` 被拒
# （err=5，权限不够），而 `OpenThread(THREAD_SUSPEND_RESUME)` 对 7z/Rar 的每个线程
# 都能打开，SuspendThread 也生效（拿 powershell 当靶子实测：挂起 1.2s，总耗时
# 3.0s → 3.9s）。所以走线程这条路才真的能用。
#
# 已知取舍：挂起期间进程**新建**的线程不会被挂住；对 7z/Rar 这种启动时就开好
# 线程池的工具没影响（主线程被挂住，活就干不下去）。

_TH32CS_SNAPTHREAD = 0x00000004
_THREAD_SUSPEND_RESUME = 0x0002
_suspend_handles: dict[int, list[int]] = {}


class _THREADENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.wintypes.DWORD),
        ("cntUsage", ctypes.wintypes.DWORD),
        ("th32ThreadID", ctypes.wintypes.DWORD),
        ("th32OwnerProcessID", ctypes.wintypes.DWORD),
        ("tpBasePri", ctypes.wintypes.LONG),
        ("tpDeltaPri", ctypes.wintypes.LONG),
        ("dwFlags", ctypes.wintypes.DWORD),
    ]


def _thread_ids(pid: int) -> list[int]:
    """列出某个进程的所有线程 ID。"""
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.restype = ctypes.wintypes.HANDLE
    kernel32.CreateToolhelp32Snapshot.argtypes = (ctypes.wintypes.DWORD, ctypes.wintypes.DWORD)
    kernel32.Thread32First.argtypes = (ctypes.wintypes.HANDLE, ctypes.c_void_p)
    kernel32.Thread32Next.argtypes = (ctypes.wintypes.HANDLE, ctypes.c_void_p)
    kernel32.CloseHandle.argtypes = (ctypes.wintypes.HANDLE,)

    snap = kernel32.CreateToolhelp32Snapshot(_TH32CS_SNAPTHREAD, 0)
    if not snap or snap == ctypes.wintypes.HANDLE(-1).value:
        return []
    found: list[int] = []
    try:
        entry = _THREADENTRY32()
        entry.dwSize = ctypes.sizeof(_THREADENTRY32)
        ok = kernel32.Thread32First(snap, ctypes.byref(entry))
        while ok:
            if entry.th32OwnerProcessID == pid:
                found.append(int(entry.th32ThreadID))
            ok = kernel32.Thread32Next(snap, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snap)
    return found


def _suspend_process(pid: int, suspend: bool) -> bool:
    """挂起/恢复某个进程的所有线程。返回是否成功（非 Windows 恒为 False）。"""
    if os.name != "nt":
        return False
    try:
        import ctypes as _c
        from ctypes import wintypes

        kernel32 = _c.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenThread.restype = wintypes.HANDLE
        kernel32.OpenThread.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel32.SuspendThread.argtypes = (wintypes.HANDLE,)
        kernel32.SuspendThread.restype = wintypes.DWORD
        kernel32.ResumeThread.argtypes = (wintypes.HANDLE,)
        kernel32.ResumeThread.restype = wintypes.DWORD
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)

        if suspend:
            if _suspend_handles.get(pid):
                return True
            handles = []
            for tid in _thread_ids(pid):
                h = kernel32.OpenThread(_THREAD_SUSPEND_RESUME, False, tid)
                if not h:
                    continue
                if kernel32.SuspendThread(h) == 0xFFFFFFFF:      # -1 = 失败
                    kernel32.CloseHandle(h)
                    continue
                handles.append(h)
            if not handles:
                return False
            _suspend_handles[pid] = handles
            return True

        handles = _suspend_handles.pop(pid, [])
        for h in handles:
            kernel32.ResumeThread(h)
            kernel32.CloseHandle(h)
        return bool(handles)
    except Exception:
        return False


# --------------------------------------------------------------------------
# 命令行打码
# --------------------------------------------------------------------------

# 引擎的参数形式：`-p<密码>`（7-Zip 与 WinRAR 都是这个）。`-p-` 表示"空密码"，
# 不是秘密，保留原样；`--password=xxx` 也一起打掉。
_PW_ARG = re.compile(r"(?<!\S)(-p|--password=)(?!-)(\S+)")


def mask_cmd(text: str) -> str:
    """把命令行里的密码打码：`-pFLYYZ` → `-p***`。

    **为什么必须在引擎层就做**：试密码时每条命令都带 `-p<候选密码>`，
    一旦这些行进了日志（界面或文件），用户的整个密码本就等于被摊开了
    （实测踩过：界面日志里出现过 `-pFLYYZ`、`-p拱墅烧烤摊师傅`）。
    """
    return _PW_ARG.sub(lambda m: f"{m.group(1)}***", text)


def display_cmd(cmd: "list[str] | tuple[str, ...]") -> str:
    """把参数列表变成"给人看的命令行"（密码已打码）。"""
    return mask_cmd(" ".join(str(c) for c in cmd))


# --------------------------------------------------------------------------
# 引擎
# --------------------------------------------------------------------------


@dataclass
class ArchiveInfo:
    """一次列目录就能拿到的包信息（避免反复调引擎）。"""

    encrypted: bool | None = None       # True/False/None(判不了)
    header_encrypted: bool = False      # 连文件名都加密了：没密码根本列不出目录
    first_entry: str | None = None      # 第一个**文件**条目（目录条目不能用来验证密码）
    smallest_entry: str | None = None   # 最小的文件条目：便宜验证优先用它
    entry_count: int = 0                # 列出来的条目数（含目录）
    read_ok: bool = False               # 引擎能把这个文件当压缩包读出来


class Extractor:
    """按格式择引擎执行 test / extract。"""

    def __init__(
        self,
        engines: Engines | None = None,
        *,
        timeout: float = 1800.0,
        logger=None,
        debug_logger=None,
    ) -> None:
        self.engines = engines or find_engines()
        self.timeout = timeout
        # 两条日志通道：
        #   logger       —— 用户该看见的（挂起/恢复、开不了引擎这种）
        #   debug_logger —— 引擎的细节（命令行、原始输出、心跳）。**不进界面**，
        #                   只进 logs\run.log；界面里要看就打开「详细」开关。
        # 为什么分开：密码候选是一个个试的，命令行里带着 `-p密码`，
        # 一股脑写进界面既刷屏又把密码摊在屏幕上（真的发生过）。
        self.logger = logger
        self.debug_logger = debug_logger
        # 「要不要掐断当前子进程」的回调。由 Runner 在跑批期间挂上。
        # 用属性而不是层层传参，是因为它要穿过 test/extract/_run 三层签名，
        # 而这三层的语义跟"取消"无关。
        self.cancel = None
        # 「要不要让引擎先停一下」的回调。返回 True 时把子进程**挂起**（不是杀掉），
        # 返回 False 再继续跑——这样暂停/继续对用户是"真的停了、还能接着跑"。
        self.pause = None

    # -- 内部 ----------------------------------------------------------

    def _log(self, msg: str) -> None:
        """给用户看的（会进界面日志面板，也会进 run.log）。"""
        if self.logger is not None:
            try:
                self.logger(msg)
            except Exception:
                pass

    def _debug(self, msg: str) -> None:
        """引擎细节：命令行、原始输出、心跳。只进 run.log（界面上要看得开「详细」）。

        **命令行必须先打码**：试密码时 cmd 里带着 `-p<密码>`，原样写出去等于把
        密码本摊在屏幕上（19:39 那次日志就是这样泄的）。
        """
        if self.debug_logger is None:
            return
        try:
            self.debug_logger(mask_cmd(msg))
        except Exception:
            pass

    def _cancelled(self) -> bool:
        if self.cancel is None:
            return False
        try:
            return bool(self.cancel())
        except Exception:
            return False

    def _paused(self) -> bool:
        if self.pause is None:
            return False
        try:
            return bool(self.pause())
        except Exception:
            return False

    def _run(self, cmd: list[str], kind: EngineKind | None = None) -> RunResult:
        """跑一个引擎命令。

        刻意不用 `subprocess.run`——它是阻塞的，用户在界面上点「停止」时
        正在进行的解压**根本掐不断**，只能等它自己跑完（可能是半小时）。
        这里改成 Popen + 100ms 轮询，一旦要中止就 kill 掉子进程。
        """
        started = time.monotonic()
        cancelled = False
        timed_out = False
        out = ""
        paused_total = 0.0
        paused_at: float | None = None
        last_note = 0.0

        # 一条命令可能要跑几十秒（整包 4.5GB 的 `7z t` 实测 27s）。跑完才说话的话，
        # 界面在这段时间里一句都不说——用户会以为卡死了。所以：开跑报一句、之后每 5 秒补一句。
        # **这两类都是引擎细节**（而且命令行里带着密码，见 display_cmd），所以走 debug 通道：
        # 只进 logs\run.log，界面里要看得自己打开「详细」。
        self._debug(f"▶ 运行中：{display_cmd(cmd)}")
        try:
            proc = subprocess.Popen(  # noqa: S603 - 列表传参，无 shell
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
        except OSError as e:
            return RunResult(
                ok=False, code=-1, output=f"无法启动引擎：{e}",
                seconds=time.monotonic() - started, cmd=cmd,
            )

        while True:
            try:
                # communicate 内部用线程抽干管道，反复调用不会因为管道写满而死锁
                stdout, _ = proc.communicate(timeout=0.1)
                out = _decode(stdout or b"")
                break
            except subprocess.TimeoutExpired:
                # 暂停：挂起子进程（挂起期间不计入超时，否则"暂停一会儿"会把任务判死）
                if self._paused() and not self._cancelled():
                    if paused_at is None:
                        suspended = _suspend_process(proc.pid, True)
                        paused_at = time.monotonic()
                        # 挂起/恢复这两句**要进界面**：用户点了暂停，得看得见"真的挂上了"
                        self._log("⏸ 已挂起引擎进程" if suspended else "⏸ 暂停（当前引擎无法挂起，排队中生效）")
                elif paused_at is not None:
                    _suspend_process(proc.pid, False)
                    paused_total += time.monotonic() - paused_at
                    paused_at = None
                    self._log("▶ 已恢复引擎进程")

                now = time.monotonic()
                # 每 5 秒报一次"还在跑"：长命令期间不能让用户以为卡死。
                # 这一句是**用户明确要保留在界面上的**（引擎的命令行/每次试密码才藏起
                # 来只进文件）——一次解压刷十几条，但它是"还在动"的唯一证据。
                if now - last_note >= 5.0 and paused_at is None:
                    last_note = now
                    self._log(f"…仍在运行（已 {now - started:.0f}s）")
                # 正在挂起的**这一段也要先扣掉**：`paused_total` 只在"恢复"时才累加，
                # 光减它的话，"暂停得比超时还久"会在挂起状态下被判超时、直接把任务杀掉
                # （实测：1 秒的命令 + 2 秒超时 + 挂 3 秒 → 以前一定红）。
                paused_for = paused_total + (now - paused_at if paused_at is not None else 0.0)
                if self._cancelled():
                    cancelled = True
                elif now - started - paused_for > self.timeout:
                    timed_out = True
                if cancelled or timed_out:
                    if paused_at is not None:      # 挂起状态下也能 kill
                        _suspend_process(proc.pid, False)
                        paused_at = None
                    proc.kill()
                    try:
                        stdout, _ = proc.communicate(timeout=5)
                        out = _decode(stdout or b"")
                    except Exception:
                        pass
                    break

        # 进程已经跑完时也要收尾：线程句柄留在表里不清掉的话，
        # 下一个进程**复用到同一个 PID** 就会被误判成"已经挂起了"（实测踩到过）
        if paused_at is not None:
            _suspend_process(proc.pid, False)
            paused_total += time.monotonic() - paused_at

        seconds = time.monotonic() - started
        res = RunResult(
            ok=(not cancelled and not timed_out) and proc.returncode == 0,
            code=proc.returncode if proc.returncode is not None else -1,
            output=out,
            timed_out=timed_out,
            cancelled=cancelled,
            seconds=seconds,
            cmd=cmd,
        )
        if not res.ok and not res.timed_out and not res.cancelled:
            # RAR 有专用退出码，优先信它；其余靠输出关键字兜底
            if kind is EngineKind.WINRAR and res.code == _RAR_WRONG_PASSWORD_CODE:
                res.wrong_password = True
            elif _looks_like_wrong_password(res.output):
                res.wrong_password = True
        self._debug(f"$ {display_cmd(cmd)}\n-> {res.brief()}")
        return res

    @staticmethod
    def _pw_args(password: str | None) -> list[str]:
        """密码参数。注意：`-p` 与密码必须拼成一个参数，不能拆开。

        密码含双引号时命令行无法安全表达，调用方应先拦下来（见 needs_manual）。
        """
        if password is None:
            # 显式告诉引擎"不要再问密码"，否则会卡在交互提示上直到超时
            return ["-p-"]
        return [f"-p{password}"]

    @staticmethod
    def needs_manual(password: str | None) -> bool:
        """密码里含引号时，命令行无法可靠传递，应提示用户手解。"""
        return bool(password) and ('"' in password or "\n" in password)

    # -- 对外 ----------------------------------------------------------

    def inspect(self, archive: str) -> ArchiveInfo:
        """一次 `7z l -slt` 拿到：加不加密 / 是不是文件名也加密 / 能用来验证密码的条目。

        三个信息本来要跑三次引擎，合并成一次。实测它能读 zip / 7z / **rar（含 RAR5）**。

        **目录条目必须跳过**：`65546.zip` 的第一条是目录「新建文件夹」，
        而 `7z t <包> <目录>` 会把整棵子树都测一遍——实测一次 27.4s，
        本该是"便宜验证"的优化直接退化。
        """
        info = ArchiveInfo()
        exe = self.engines.seven_zip
        if not exe:
            return info

        res = self._run([exe, "l", "-slt", "-p-", "--", archive], EngineKind.SEVENZIP)
        low = res.output.lower()

        if res.wrong_password or "cannot open encrypted archive" in low or "enter password" in low:
            # 没给密码却连目录都列不出来 → 文件名也是加密的
            info.encrypted = True
            info.header_encrypted = True
            return info
        if not res.ok:
            return info                       # 判不了，三个字段保持默认
        info.read_ok = True                   # 引擎确实把它当压缩包读出来了

        info.encrypted = any(
            ln.strip().lower().startswith("encrypted = +") for ln in res.output.splitlines()
        )
        target = os.path.normcase(os.path.abspath(archive))
        # `-slt` 是"一条记录一段"：Path 开头，后面跟 Folder / Size / Encrypted …
        cur: dict[str, str] = {}
        smallest = {"size": -1}

        def flush() -> None:
            path = cur.get("path", "")
            if not path:
                return
            if os.path.normcase(os.path.abspath(path)) == target:
                return                        # 第一条是压缩包自己
            info.entry_count += 1
            if cur.get("folder") == "+":
                return                        # 目录：测它会连整棵子树一起测
            if info.first_entry is None:
                info.first_entry = path
            # **只有加密条目才能证明密码对不对**：实测那个 2.88G 的包里第一条是
            # 未加密的 mp4，拿它去"验证"任何密码都会通过（假阳性）。
            if cur.get("encrypted") != "+":
                return
            try:
                size = int(cur.get("size", "0"))
            except ValueError:
                size = 0
            if smallest["size"] < 0 or size < smallest["size"]:
                info.smallest_entry = path
                smallest["size"] = size

        for line in res.output.splitlines():
            if line.startswith("Path = "):
                flush()
                cur = {"path": line[len("Path = "):].strip()}
            elif line.startswith("Folder = "):
                cur["folder"] = line[len("Folder = "):].strip()
            elif line.startswith("Size = "):
                cur["size"] = line[len("Size = "):].strip()
            elif line.startswith("Encrypted = "):
                cur["encrypted"] = line[len("Encrypted = "):].strip()
        flush()
        return info

    def is_encrypted(self, archive: str) -> bool | None:
        """这个包到底加没加密？True / False / None（判不了）。

        **为什么必须判**：对未加密的包，引擎会忽略 `-p`，验证必然"成功"。
        不先判一下，密码本会把第一个候选密码当成"命中"——既误导用户，
        又污染成功次数。
        """
        return self.inspect(archive).encrypted

    def first_entry(self, archive: str) -> str | None:
        """包里第一个条目的路径（只读头部，很便宜）。"""
        return self.inspect(archive).first_entry

    def verify(self, archive: str, password: str | None, *, kind=None, info=None) -> RunResult:
        """**便宜地**验证密码对不对。

        关键取舍（实测驱动）：

        * 文件名也加密的包（`-mhe=on` / `-hp`）：`7z l -p<密码>` 解密文件头就能
          证明密码正确，**只读几十 KB**。用 `7z t` 会把整个包读一遍——
          对一个 11GB 的分卷 7z，那就是每个候选密码读 11GB，不可接受。
        * 文件名没加密的包：只 `t` 第一个条目，同样便宜。

        比"直接解压试密码"更稳：不会在密码错误时留下半成品目录。
        """
        info = info or self.inspect(archive)
        if info.header_encrypted:
            exe = self.engines.seven_zip
            if not exe:
                return RunResult(ok=False, code=-2, output="缺少 7-Zip，无法验证加密头")
            return self._run(
                [exe, "l", "-slt", *self._pw_args(password), "--", archive],
                EngineKind.SEVENZIP,
            )
        # 优先只测**最小的那个加密文件条目**：4.5GB 的包里有小文件时，
        # 一次验证从 27s 掉到几秒（目录条目和未加密条目都已在 inspect 里滤掉）
        entry = info.smallest_entry or info.first_entry
        res = self.test(archive, password, kind=kind, entry=entry)
        if entry and res.code == -3:
            # 条目名没匹配上（7z 在非 UTF-8 控制台下会把包里的名字写成乱码，
            # 我们再传回去就匹配不到了）→ 老老实实整包测一遍，别把正确密码判成错的
            res = self.test(archive, password, kind=kind)
        return res

    def test(
        self,
        archive: str,
        password: str | None = None,
        *,
        kind: EngineKind | str | None = None,
        entry: str | None = None,
    ) -> RunResult:
        """验证密码/完整性。

        `entry` 给了就**只验证这一个条目**——大包逐个试密码时的关键优化。
        """
        kind = EngineKind(kind) if kind is not None else self.engine_for(archive)
        # 空密码 + WinRAR：`-p` 裸写在 RAR 命令行里是**"请提示我输入密码"**，于是它跑去读
        # stdin、返回 12（找不到文件），被上层当成"引擎报错"→ 用户连输密码的弹窗都看不到
        # （4444.rar 就是这么被跳过的）。7-Zip 的裸 `-p` 才是"空密码"，而它的 Rar5 解码器
        # 足够验密码，所以这一种情况改用 7-Zip。
        if kind is EngineKind.WINRAR and password == "":
            kind = EngineKind.SEVENZIP
        exe = self.engines.path_of(kind)
        if not exe:
            return RunResult(ok=False, code=-2, output=f"缺少引擎：{kind.value}")

        tail_args = [archive] + ([entry] if entry else [])
        if kind is EngineKind.SEVENZIP:
            if entry:
                # 带条目时要**看得见输出**：7z 匹配不到条目会打印 "No files to process"
                # 却仍然返回 0，于是乱码/错名字会变成"密码通过"的假阳性（实测踩过）。
                cmd = [exe, "t", "-y", "-bsp0", *self._pw_args(password), "--", *tail_args]
            else:
                cmd = [exe, "t", "-y", "-bso0", "-bsp0", *self._pw_args(password), "--", *tail_args]
        else:
            cmd = [exe, "t", "-y", "-idq", *self._pw_args(password), "--", *tail_args]
        res = self._run(cmd, kind)
        if entry and res.ok and _nothing_matched(res.output):
            res.ok = False
            res.code = -3
            res.output = f"{res.output}\n条目名没匹配上（{entry!r}），这次验证不算通过".strip()
        return res

    def extract(
        self,
        archive: str,
        outdir: str,
        password: str | None = None,
        *,
        kind: EngineKind | str | None = None,
        overwrite: bool = True,
    ) -> RunResult:
        """解压到 outdir（目录由本函数创建）。"""
        kind = EngineKind(kind) if kind is not None else self.engine_for(archive)
        # 同 test()：空密码没法用 RAR 命令行表达（裸 -p 是"提示输入"），改用 7-Zip
        if kind is EngineKind.WINRAR and password == "":
            self._log("空密码交给 7-Zip 解（WinRAR 命令行没法表达空密码）")
            kind = EngineKind.SEVENZIP
        exe = self.engines.path_of(kind)
        if not exe:
            return RunResult(ok=False, code=-2, output=f"缺少引擎：{kind.value}")

        os.makedirs(outdir, exist_ok=True)
        if kind is EngineKind.SEVENZIP:
            cmd = [
                exe, "x",
                f"-o{outdir}",
                "-y",
                "-bso0", "-bsp0",
                *self._pw_args(password),
                "--", archive,
            ]
        else:
            # Rar.exe 的目标目录是位置参数，且 -o+ 覆盖
            cmd = [
                exe, "x",
                "-o+" if overwrite else "-o-",
                "-y",
                "-idq",
                *self._pw_args(password),
                "--", archive,
                outdir + os.sep,
            ]
        return self._run(cmd, kind)

    def engine_for(self, archive: str) -> EngineKind:
        """按真实格式选引擎：rar/rar5 必须 WinRAR，其余用 7z。"""
        from core.probe import Fmt, detect_format

        try:
            fmt = detect_format(archive)
        except Exception:
            fmt = Fmt.UNKNOWN
        if fmt.engine == "winrar":
            return EngineKind.WINRAR
        return EngineKind.SEVENZIP

    # 注：is_encrypted / first_entry / verify 见上面的 inspect()，
    # 这里不再重复定义——同一个逻辑写两遍迟早会漂移。

    # -- 便捷组合 ------------------------------------------------------

    def test_then_extract(
        self,
        archive: str,
        outdir: str,
        password: str | None = None,
        *,
        kind: EngineKind | str | None = None,
    ) -> tuple[RunResult, RunResult | None]:
        """先验证再解压。验证失败就直接返回，不做无用解压。"""
        t = self.test(archive, password, kind=kind)
        if not t.ok:
            return t, None
        return t, self.extract(archive, outdir, password, kind=kind)


def probe_capability() -> dict[str, object]:
    """给 UI 引擎状态条用：本机能力一览。"""
    eng = find_engines()
    return {
        "seven_zip": eng.seven_zip,
        "winrar": eng.winrar,
        "rar_ok": eng.winrar is not None,
        "zip_ok": eng.seven_zip is not None,
        "can_zip": eng.seven_zip is not None,
        "note": None
        if eng.winrar is not None
        else ".rar 与伪装 rar 依赖 WinRAR；当前仅能处理 .zip / .7z",
    }
