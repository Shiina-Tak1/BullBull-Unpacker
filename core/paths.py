"""路径解析：源码版与冻结版（PyInstaller 打包）都要能找到资源、找到可写的数据目录。

为什么单独成模块：**打包之后 `__file__` 的位置会变**——
  * 源码版：资源就在工程目录下（`<工程>/assets`、`<工程>/tools/7z`）；
  * onedir 冻结版：资源被 `--add-data` 放进 `_internal`（`sys._MEIPASS` 指的就是它），
    而**程序目录**（exe 所在处）才是"便携"的落点，用户数据要尽量写在它旁边。

两类路径必须分开：

    资源（只读）  resource_dir() / resource_path(...)      → assets/、tools/
    用户数据（可写） data_dir() / data_path(...)            → config.json、密码本.txt、logs/

数据目录的选择顺序：显式覆盖（测试用 `base_dir`）→ 程序目录（便携，能写就写这儿）
→ `%APPDATA%\\BullBullUnpacker`（程序目录写不进去时，比如装在 Program Files 下）。

测试钩子：
  * `SMART_UNZIP_DATA_DIR`  ：直接指定数据目录（比 base_dir 更底层，进程级）
  * `SMART_UNZIP_FORCE_FROZEN=1`：把"冻结态"装出来，这样在源码环境里也能测冻结分支
"""

from __future__ import annotations

import os
import sys

# 便携数据目录的固定名字（落到 %APPDATA% 时用它）
APP_DIR_NAME = "BullBullUnpacker"

_override_dir: "str | None" = None
_cached_data_dir: "str | None" = None
# 数据目录是"为什么"选在那儿的：override(被指定) / portable(就在程序旁边) / appdata(退到用户目录)
_cached_reason: str = ""


def is_frozen() -> bool:
    """现在是不是打包后的 exe 在跑。

    `SMART_UNZIP_FORCE_FROZEN=1` 是给测试用的：源码环境里也能验"冻结分支"
    （比如右键菜单该注册 exe 而不是 pythonw+run.py）。
    """
    if os.environ.get("SMART_UNZIP_FORCE_FROZEN") == "1":
        return True
    return bool(getattr(sys, "frozen", False))


def resource_dir() -> str:
    """只读资源（`assets/`、`tools/`）的根。

    **冻结取哪一份**：打包后优先用**程序目录旁边**的那份（`<exe目录>\\tools\\7z\\7z.exe`），
    因为那是我们能看见、也好解释的位置；只有它不在时才回落到 `sys._MEIPASS`
    （PyInstaller 的 `_internal`）。

    为什么要这么判：以前 `tools\\7z` 被 `--add-data` 塞进 `_internal`，同时为了"用户能看见"
    又在程序根目录放了一份 —— 同一个 7-Zip 有两份（约 5.9 MB ×2）。现在打包时**不再 add-data**，
    只留程序目录旁边那一份，这里的判断就是让程序去用它。
    """
    if is_frozen():
        beside = os.path.join(os.path.dirname(os.path.abspath(sys.executable)),
                              "tools", "7z", "7z.exe")
        if os.path.isfile(beside):
            return os.path.dirname(os.path.abspath(sys.executable))
        # onedir：PyInstaller 把 --add-data 的东西放进 _internal，_MEIPASS 指它
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            return str(meipass)
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def resource_path(*parts: str) -> str:
    return os.path.join(resource_dir(), *parts)


def app_dir() -> str:
    """程序自己的目录（便携版就是它；"数据就地存放"优先考虑这里）。"""
    if is_frozen():
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _writable(directory: str) -> bool:
    """真去写一个临时文件试试（只判 `os.access` 在 Windows 上不可靠）。"""
    probe = os.path.join(directory, ".bbu-write-probe")
    try:
        os.makedirs(directory, exist_ok=True)
        with open(probe, "w", encoding="utf-8") as f:
            f.write("ok")
        os.remove(probe)
        return True
    except OSError:
        return False


def set_data_dir_override(directory: "str | None") -> None:
    """把数据目录钉到指定位置（`Workbench(base_dir=...)` 走这里）。

    传 None 表示恢复默认。测试与截图脚本都靠它把配置/密码本指到临时目录里，
    **绝不能**让测试去动用户真实的 config.json / 密码本.txt。
    """
    global _override_dir, _cached_data_dir, _cached_reason
    _override_dir = os.path.abspath(directory) if directory else None
    _cached_data_dir = None
    _cached_reason = ""


def data_dir() -> str:
    """配置 / 密码本 / 日志放哪。"""
    global _cached_data_dir, _cached_reason
    if _cached_data_dir:
        return _cached_data_dir

    env = os.environ.get("SMART_UNZIP_DATA_DIR")
    if env:
        os.makedirs(env, exist_ok=True)
        _cached_data_dir = os.path.abspath(env)
        _cached_reason = "override"
        return _cached_data_dir

    if _override_dir:
        os.makedirs(_override_dir, exist_ok=True)
        _cached_data_dir = _override_dir
        _cached_reason = "override"
        return _cached_data_dir

    local = app_dir()
    if _writable(local):
        _cached_data_dir = local
        _cached_reason = "portable"
        return local

    # 程序目录写不进去（典型：装在 Program Files 下）→ 退到用户目录
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    fallback = os.path.join(base, APP_DIR_NAME)
    try:
        os.makedirs(fallback, exist_ok=True)
    except OSError:
        fallback = local          # 连用户目录都不行，那就只能用程序目录（至少报错位置一致）
    _cached_data_dir = fallback
    _cached_reason = "appdata"
    return fallback


def data_dir_reason() -> str:
    """数据目录为什么在那儿：`override` / `portable` / `appdata`。

    界面上要说清楚是哪一种——"数据在程序旁边"和"被系统逼到用户目录"对用户
    是完全不同的两件事（后者会让人以为密码本丢了）。
    """
    data_dir()
    return _cached_reason or "portable"


def data_dir_note() -> str:
    """给界面用的一句话说明。"""
    return {
        "override": "已指定目录（测试或自定义）",
        "portable": "就在程序旁边（便携，整个文件夹拷走数据一起走）",
        "appdata": "程序目录写不进去，已放到用户目录（%APPDATA%）",
    }.get(data_dir_reason(), "")


def data_path(name: str) -> str:
    return os.path.join(data_dir(), name)


def log_path() -> str:
    """异常/启动日志（`logs/ui.log`）的路径，目录不存在会建。"""
    return _log_file("ui.log")


def run_log_path() -> str:
    """界面日志面板的完整流水（`logs/run.log`）的路径。"""
    return _log_file("run.log")


def _log_file(name: str) -> str:
    d = os.path.join(data_dir(), "logs")
    try:
        os.makedirs(d, exist_ok=True)
    except OSError:
        return os.path.join(data_dir(), name)
    return os.path.join(d, name)


def is_appdata_mode() -> bool:
    """数据是不是被迫放在用户目录（而不是程序旁边）——设置页要如实告诉用户。"""
    return data_dir_reason() == "appdata"
