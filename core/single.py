"""单实例：已经有窗口在跑时，把路径甩给它然后自己退出。

**这个模块只用标准库**（ctypes + json），绝不 import Qt —— 因为它的全部意义就是
"在启动界面之前先把活转交出去"：

    右键一次 → 资源管理器起一个 pythonw → 以前它要 import Qt（1~2 秒，还得先建
    QApplication）之后才去连管道，用户感受到的就是"右键之后卡半天"；多选几个文件
    就是好几个进程同时卡，谁都没连上就各开一个窗口。

现在这条路是：纯 ctypes 连 Windows 命名管道（Qt 的 QLocalServer 在 Windows 上
创建的就是 `\\\\.\\pipe\\<name>`），连上 → 写一段 JSON → 退出。整个进程几百毫秒，
不加载 Qt。

另一件事是**抢主实例**：用命名互斥体（CreateMutexW）选出唯一的"主窗口"，
避免多选时几个进程同时启动、互相抢管道名字（QLocalServer.removeServer 会把
别人正在用的那个删掉）。

对外三个函数：
    become_primary() -> bool                 抢到主实例了吗（抢到就该去开界面）
    forward(paths, auto, timeout) -> bool     把活交给已在跑的窗口（成功就别开界面）
    release_primary()                        退出时放掉（不调也行，进程结束会自动放）
"""

from __future__ import annotations

import ctypes
import json
import threading
import time
from ctypes import wintypes

PIPE_NAME = "bbu-unpacker"                 # Qt 用名字，ctypes 用 \\.\pipe\<名字>
MUTEX_NAME = "Local\\bbu-unpacker-single"

_ERROR_ALREADY_EXISTS = 183
_GENERIC_WRITE = 0x40000000
_OPEN_EXISTING = 3
_INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

_HANDLE = wintypes.HANDLE
_LPCWSTR = wintypes.LPCWSTR

_k32 = None
_mutex_handle = None


def _kernel32():
    global _k32
    if _k32 is None:
        _k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        _k32.CreateMutexW.restype = _HANDLE
        _k32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, _LPCWSTR]
        _k32.CreateFileW.restype = _HANDLE
        _k32.CreateFileW.argtypes = [_LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                     ctypes.c_void_p, wintypes.DWORD,
                                     wintypes.DWORD, _HANDLE]
        _k32.WriteFile.argtypes = [_HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                   ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
        _k32.CloseHandle.argtypes = [_HANDLE]
    return _k32


def become_primary() -> bool:
    """抢主实例。抢到（之前没人持有这个互斥体）返回 True。

    互斥体是"进程活着就持有"的内核对象：上一个主窗口被关掉/崩掉之后会自动释放，
    所以不会出现"残留一个名字导致永远起不来"。
    """
    global _mutex_handle
    if _mutex_handle:
        return True
    try:
        k32 = _kernel32()
        handle = k32.CreateMutexW(None, True, MUTEX_NAME)
        if not handle:
            return True                      # 建不出来就当自己主实例，别把功能卡死
        if ctypes.get_last_error() == _ERROR_ALREADY_EXISTS:
            k32.CloseHandle(handle)
            return False
        _mutex_handle = handle
        return True
    except Exception:                        # noqa: BLE001 - 意外就退化成"自己开一个"
        return True


def release_primary() -> None:
    global _mutex_handle
    if _mutex_handle:
        try:
            _kernel32().CloseHandle(_mutex_handle)
        except Exception:                    # noqa: BLE001
            pass
        _mutex_handle = None


def forward(paths: list[str], auto: bool = False, timeout: float = 4.0) -> bool:
    """把路径交给已经在跑的窗口；成功返回 True。

    `paths` 允许是空列表：那表示"我什么都不带，只是想让已经在跑的那个窗口
    顶到前面来"（`ui/app.py` 的 `queue_launch` 收到空路径也会把窗口 raise 出来）。

    timeout 是"等主窗口把管道建起来"的耐心：多选时几个进程几乎同时被拉起来，
    第一个正在 import Qt、管道还没建，这时候必须先等等它，而不是各自开窗口。
    """
    if paths is None:
        return False
    payload = json.dumps({"paths": [str(p) for p in paths], "auto": bool(auto)},
                         ensure_ascii=False).encode("utf-8")
    deadline = time.monotonic() + max(0.0, timeout)
    while True:
        state = _write_once(payload)
        if state != "no-server":
            # "ok" = 写进去了；"timeout" = 写卡住了（数据多半已经在管道缓冲里，
            # 只是对端没读）——两种情况都别再重试，免得同一批路径被加进清单两次
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.15)


def _write_once(payload: bytes, write_timeout: float = 2.0) -> str:
    """连一次管道、写完、关掉。

    返回 `"ok"` / `"no-server"`（连不上，可以重试）/ `"timeout"`（连上了但写不动，
    别再重试）。

    **为什么写要放到线程里加超时**：`WriteFile` 在命名管道上是**可能一直阻塞**的
    （对端不读、或者对端是个卡住的进程），实测把整个进程挂死在那一行。
    右键菜单的语言是"点了就该有反应"，绝不能因为对面卡住就把这个进程也拖住。
    """
    box: list[str] = []

    def work() -> None:
        box.append(_write_once_raw(payload))

    th = threading.Thread(target=work, daemon=True)
    th.start()
    th.join(write_timeout)
    if not box:
        return "timeout"
    return box[0]


def _write_once_raw(payload: bytes) -> str:
    try:
        k32 = _kernel32()
        handle = k32.CreateFileW(r"\\.\pipe\\" + PIPE_NAME, _GENERIC_WRITE, 0,
                                 None, _OPEN_EXISTING, 0, None)
        if not handle or handle == _INVALID_HANDLE_VALUE:
            return "no-server"
        try:
            # 试过把写设成 PIPE_NOWAIT（"非阻塞"）想让卡住的情况也不卡：**不行**——
            # 对 Qt 建的管道设完之后 WriteFile 直接失败/写不进去，对端什么都收不到
            # （实测正着测的那条断言从 PASS 变 FAIL）。所以老实按阻塞写，
            # 由外层的线程 + 超时兜底。
            written = wintypes.DWORD(0)
            ok = k32.WriteFile(handle, payload, len(payload),
                               ctypes.byref(written), None)
            return "ok" if (ok and written.value == len(payload)) else "no-server"
        finally:
            k32.CloseHandle(handle)
    except Exception:                        # noqa: BLE001 - 连不上就是"没有窗口在跑"
        return "no-server"


if __name__ == "__main__":                   # 手动排错用：python -m core.single "D:\x.zip"
    import sys

    print("is_primary:", become_primary())
    print("forwarded :", forward(sys.argv[1:], timeout=1.0))
