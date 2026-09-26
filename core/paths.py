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
from dataclasses import dataclass, field

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


def build_id() -> str:
    r"""构建指纹（`BUILD-ID.txt`，放在资源目录旁边）；读不到就返回空串。

    为什么要它：`--where` 与**两份日志的开头**（`ui.log` 的启动行、`run.log` 的会话头）
    都写它 —— 用户交回来的日志才能对应到"到底是哪一份代码"（§9.10 的"构建指纹可还原"）。

    读不到**不算错**：便携版里本来就没有这个文件（指纹由 `tools\export_for_vm.py`
    发包时写进源码树），少显示一段就是少一段。

    ⚠ 这里只是"读一个文件"、**不 import `tools\build_id.py`**：`tools\` 既不进公开仓也不进
    便携包（`sync_repo.py` 的 `TOOLS = []`），真依赖它，打包版一按 `--where` 就是 ImportError。
    生成/校验那份（要 git）留在开发机的 `tools\build_id.py`。
    （2026-09-24：从 `run.py` 提到这里 —— 界面日志头也要用同一份，见 `TASK-060`。）
    """
    try:
        with open(os.path.join(resource_dir(), "BUILD-ID.txt"), encoding="utf-8") as f:
            return f.readline().strip()
    except OSError:
        return ""


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
        try:
            os.makedirs(env, exist_ok=True)
        except OSError:
            # 建不出来（盘符不存在 / 权限 / 只读介质）→ **不许抛**：`data_dir()` 在启动路径上，
            # 抛出去就是起不来。退回下面那套正常选择，最后 `data_dir_reason()` 会如实说
            # 数据落到哪了（这跟"程序目录写不进去就退到 %APPDATA%"是同一条思路）。
            pass
        else:
            _cached_data_dir = os.path.abspath(env)
            _cached_reason = "override"
            return _cached_data_dir

    if _override_dir:
        try:
            os.makedirs(_override_dir, exist_ok=True)
        except OSError:
            pass                   # 同上：指定目录建不出来就退回正常选择，别把窗口拦在启动前
        else:
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


# --------------------------------------------------------------------------
# 「密码本被分成两本」的现场（B-2026-058）
#
# 为什么需要这一段：`data_dir()` 用**真写探针**决定数据目录、进程内只判一次，所以
# 结论**只会在两次运行之间**翻转 —— 程序目录"这一刻能不能真写"变了（装在 Program Files /
# 只读介质 / ACL 变化 / 磁盘满 / 杀软或 UAC 虚拟化 / 网络盘断开 / 整个文件夹拷到另一台机器），
# 于是两次运行各指一个目录、**各一本密码本**，而界面上一句提示都没有。
# 用户看到的是「密码本空了」（其实是另一本），解压时密码漏试、还去弹窗问密码。
#
# **作者裁决 `J-9 = A`（2026-09-23）：只做提示，不做自动合并、不做引导合并。**
# 所以这里只回答两件事：本次用的是哪个目录、**另一个目录里是不是也躺着一本**。
# 两本都原封不动留在盘上，用哪本由用户自己决定。
# --------------------------------------------------------------------------


def data_dir_candidates() -> list[tuple[str, str]]:
    """这台机器上**可能**被当成数据目录的位置（顺序 = `data_dir()` 的优先级）。

    只有"路径与本次判定无关"的两个：
      1. 程序目录（便携：整个文件夹拷走，数据跟着走）；
      2. `%APPDATA%\\BullBullUnpacker`（程序目录写不进去时程序退到这儿）。

    显式指定数据目录时（`SMART_UNZIP_DATA_DIR` / `base_dir`）返回**空** —— 那是测试与
    自定义，不该去翻用户真实的那两个目录（`R-01` 的同一条精神）。
    """
    if os.environ.get("SMART_UNZIP_DATA_DIR") or _override_dir:
        return []
    local = app_dir()
    out = [(local, "portable")]
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    fallback = os.path.join(base, APP_DIR_NAME)
    if os.path.normcase(os.path.abspath(fallback)) != os.path.normcase(os.path.abspath(local)):
        out.append((fallback, "appdata"))
    return out


@dataclass(frozen=True)
class DataDirSplit:
    """本次的数据目录 + **另一个也放着密码本**的目录（只报告，不合并）。

    `others` 是 `[(目录, 条数)]`，条数 `-1` 表示**那本读不了**（不许当成 0 条 ——
    0 条会被读成"那本是空的"，把"读不了"说成"空"正是这个项目反复踩的坑）。
    """

    current: str
    current_reason: str
    others: list[tuple[str, int]] = field(default_factory=list)

    @property
    def split(self) -> bool:
        return bool(self.others)

    def message(self) -> str:
        """给用户看的一段话；没有分裂时返回空串（调用方据此决定显不显示）。

        ⚠ **纯文本、不带 markdown**：这段直接进 `QLabel`（纯文本模式），
        `**强调**` 会原样显示成星号（2026-09-23 截图核对时抓到并去掉）。
        """
        if not self.others:
            return ""
        where = {"portable": "程序旁边", "appdata": "用户目录（%APPDATA%）",
                 "override": "指定目录"}.get(self.current_reason, self.current_reason)
        lines = [f"⚠ 这次用的数据目录：{self.current}（{where}）"]
        for d, n in self.others:
            count = "读不了" if n < 0 else f"{n} 条"
            lines.append(f"⚠ 另一个目录里也有一本密码本：{d}（{count}）")
        lines.append("程序不会自动合并这两本，也不会删掉任何一本 —— 两份都还在盘上，"
                     "当前只以上面那本为准。")
        return "\n".join(lines)


def _count_book(path: str) -> int:
    """数一本密码本里有多少条；读不了返回 `-1`。

    **解析一律走 `core.vault`**（§4.2：解析只有一份，别处不许再抄）——
    延后导入，`core.vault` 只依赖 `core.naming`，不会成环。
    """
    try:
        from core.vault import parse_book, read_text          # noqa: PLC0415
        return len(parse_book(read_text(path)))
    except Exception:                                         # noqa: BLE001
        return -1


def data_dir_split() -> DataDirSplit:
    """本次用的数据目录，以及**另一个目录里那本密码本**（`B-2026-058`）。

    判据只有一条：另一个候选目录里**存在 `密码本.txt`**。存在就说明这台机器上真的有
    两本（不管哪本更新）—— 那就必须让用户看见，而不是让他自己发现"密码本空了"。
    """
    from core.vault import DEFAULT_BOOK                        # noqa: PLC0415

    current = data_dir()
    others: list[tuple[str, int]] = []
    for d, _why in data_dir_candidates():
        if os.path.normcase(os.path.abspath(d)) == os.path.normcase(os.path.abspath(current)):
            continue
        book = os.path.join(d, DEFAULT_BOOK)
        if os.path.isfile(book):
            others.append((d, _count_book(book)))
    return DataDirSplit(current, data_dir_reason(), others)
