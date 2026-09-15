"""把「添加到 BBU 解压列表」注册进资源管理器的右键菜单。

写在 **HKCU**（当前用户自己那份）下，所以：
  * 不需要管理员、不需要 UAC；
  * 卸载就是删掉那几个键，不碰系统里任何别的软件；
  * 换电脑/删工程目录后，菜单项会失效——重新点一次「安装」即可。

注册三个位置，覆盖日常会遇到的所有情况：

    *\\shell\\<verb>                     右键一个文件（包括 .zip/.rar/伪装 .mp4）
    Directory\\shell\\<verb>             右键一个文件夹
    Directory\\Background\\shell\\<verb> 在文件夹里的空白处右键（拿 %V 当前目录）

命令统一是 `<venv>\\Scripts\\pythonw.exe <工程目录>\\run.py "<路径>"`：
用 pythonw 是为了不闪黑框。run.py 起界面之前先试连已经在跑的那个窗口（纯 stdlib，
不 import Qt），连上就把路径甩过去自己退出——所以右键多选不会开出一堆窗口。

**只有一种模式**：右键后把路径加进待处理列表，不自动开始——解压参数（解压到哪、
重名怎么办、要不要删原包）都在界面上，先让人看一眼再点「开始」，不会一右键就照着
上次的设置闷头跑。想让命令行自动开始的话，run.py 认 `--auto`（右键菜单不写这个参数）。

`root` 参数是为了可测：测试用 `Software\\BBUTest\\Classes` 这种临时根，
绝不往真实菜单里写东西。
"""

from __future__ import annotations

import os
import sys
from typing import Iterable

from core import appinfo
from core import paths

VERB = appinfo.SHELL_VERB
MENU_TEXT = appinfo.SHELL_MENU_TEXT
# 以前叫「用智能解压解压」（VERB=SmartUnzip）：安装/卸载时顺手把老键清掉，
# 不然菜单里会并排出现两条一模一样的项
LEGACY_VERBS = ("SmartUnzip",)
ROOT = r"Software\Classes"
# 右键选中多个文件时，资源管理器最多给这么多个副本发命令（默认只发第一个）
MULTI_SELECT = "Player"


def registry_root() -> str:
    """写哪个注册表根。

    默认 `Software\\Classes`（当前用户）。测试可以把它指到 `Software\\BBUTest\\Classes`
    这种临时根上，跑完就删——**绝不**往用户真实菜单里写测试项。
    环境变量 `SMART_UNZIP_SHELLMENU_ROOT` 也能覆盖（命令行开关跑测试时用）。
    """
    return os.environ.get("SMART_UNZIP_SHELLMENU_ROOT") or ROOT

# 三个注册位置 → 用哪个占位符拿路径
PLACES: tuple[tuple[str, str], ...] = (
    (r"*", "%1"),                       # 文件
    (r"Directory", "%1"),               # 文件夹
    (r"Directory\Background", "%V"),    # 文件夹空白处（%V = 当前目录）
)


def _key(root: str, place: str, verb: str = VERB) -> str:
    return rf"{root}\{place}\shell\{verb}"


def command_line(pythonw: str, script: str, *, placeholder: str = "%1") -> str:
    """拼一条能被资源管理器当命令行执行的字符串（路径全加引号，防空格）。

    不带 `--auto`：右键只把路径送进待处理列表，不自动开始。
    """
    return f'"{pythonw}" "{script}" "{placeholder}"'


def pythonw_for(python_exe: str) -> str:
    """给一个 python.exe，尽量换成同目录的 pythonw.exe（不然每次右键都闪一个黑框）。"""
    folder = os.path.dirname(os.path.abspath(python_exe))
    for name in ("pythonw.exe", "pythonw"):
        cand = os.path.join(folder, name)
        if os.path.isfile(cand):
            return cand
    return python_exe


def launch_parts() -> tuple[str, str]:
    """右键菜单该执行什么：返回 `(可执行文件, 脚本)`，脚本为空表示"只有 exe"。

    * **冻结版（打包后的 exe）**：直接跑自己 —— `("…\\BullBull Unpacker.exe", "")`，
      目标机不需要 Python，也不会闪黑框（exe 本身是 `--windowed` 的）；
    * **源码版**：`("…\\pythonw.exe", "…\\run.py")`，同样不闪黑框。
    """
    if paths.is_frozen():
        exe = os.path.abspath(sys.executable)
        return exe, ""
    return pythonw_for(sys.executable), os.path.join(paths.app_dir(), "run.py")


def background_launcher() -> str:
    """后台启动用哪个 exe，专门为了给"停止"之类留口子时用（目前只有 exe 本身）。"""
    return launch_parts()[0]


def menu_icon() -> str:
    """右键菜单项左边那个图标。

    * 冻结版：用 exe 自带的图标（`路径,0`），换图标只换 exe；
    * 源码版：用 assets 里的 .ico。
    """
    if paths.is_frozen():
        return os.path.abspath(sys.executable) + ",0"
    ico = paths.resource_path("assets", appinfo.ICON_FILE)
    return ico if os.path.isfile(ico) else ""


def command_line(pythonw: str, script: str, *, placeholder: str = "%1") -> str:
    """拼一条能被资源管理器当命令行执行的字符串（路径全加引号，防空格）。

    `script` 为空 = 直接执行 exe（冻结版就是这种）。不带 `--auto`：
    右键只把路径送进待处理列表，不自动开始。
    """
    if not script:
        return f'"{pythonw}" "{placeholder}"'
    return f'"{pythonw}" "{script}" "{placeholder}"'


def exe_command(placeholder: str = "%1") -> str:
    """当前形态下右键菜单该用的完整命令行（测试与设置页都读它）。"""
    exe, script = launch_parts()
    return command_line(exe, script, placeholder=placeholder)


def install_self(*, root: str | None = None) -> list[str]:
    """按**当前形态**把右键菜单装好（设置页与 `--install-shellmenu` 都走这个）。

    冻结版注册 exe 自己，源码版注册 pythonw + run.py；图标同理。
    """
    exe, script = launch_parts()
    return install(exe, script, root=root or registry_root(), icon=menu_icon())


def uninstall_self(*, root: str | None = None) -> list[str]:
    return uninstall(root=root or registry_root())


def install(
    pythonw: str,
    script: str,
    *,
    root: str = ROOT,
    icon: str = "",
) -> list[str]:
    """写入右键菜单，返回真正写过的注册表键（方便卸载/排错）。"""
    import winreg  # 只在 Windows 有；这个工具本来就只跑 Windows

    purge_legacy(root=root)          # 老名字那几条先清掉，免得菜单里出现两条
    written: list[str] = []
    for place, placeholder in PLACES:
        key_path = _key(root, place)
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_SET_VALUE) as k:
            winreg.SetValueEx(k, None, 0, winreg.REG_SZ, MENU_TEXT)
            winreg.SetValueEx(k, "MultiSelectModel", 0, winreg.REG_SZ, MULTI_SELECT)
            if icon:
                winreg.SetValueEx(k, "Icon", 0, winreg.REG_SZ, icon)
        with winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER, key_path + r"\command", 0, winreg.KEY_SET_VALUE
        ) as k:
            winreg.SetValueEx(
                k, None, 0, winreg.REG_SZ,
                command_line(pythonw, script, placeholder=placeholder),
            )
        written.append(key_path)
    return written


def purge_legacy(*, root: str = ROOT) -> list[str]:
    """删掉旧菜单项（改名前的那些 VERB）。返回真正删掉的键。"""
    import winreg

    removed: list[str] = []
    for verb in LEGACY_VERBS:
        for place, _ in PLACES:
            key_path = _key(root, place, verb)
            if _delete_tree(winreg, winreg.HKEY_CURRENT_USER, key_path):
                removed.append(key_path)
    return removed


def uninstall(*, root: str = ROOT, places: Iterable[str] | None = None) -> list[str]:
    """删掉右键菜单，返回真正删掉的键。不存在的键直接跳过。"""
    import winreg

    removed: list[str] = []
    names = list(places) if places is not None else [p for p, _ in PLACES]
    for place in names:
        key_path = _key(root, place)
        if _delete_tree(winreg, winreg.HKEY_CURRENT_USER, key_path):
            removed.append(key_path)
    if places is None:
        removed.extend(purge_legacy(root=root))
    return removed


def _delete_tree(winreg, hive, path: str) -> bool:
    """递归删键（winreg 没有删整棵树的方法，:command 子键得先删）。"""
    try:
        with winreg.OpenKey(hive, path, 0, winreg.KEY_READ | winreg.KEY_WRITE) as k:
            subs = []
            i = 0
            while True:
                try:
                    subs.append(winreg.EnumKey(k, i))
                except OSError:
                    break
                i += 1
        for sub in subs:
            _delete_tree(winreg, hive, path + "\\" + sub)
        try:
            winreg.DeleteKey(hive, path)
        except OSError:
            return False
        return True
    except FileNotFoundError:
        return False
    except OSError:
        return False


def state(*, root: str | None = None) -> dict:
    """现在装没装、装的是什么命令（设置页要显示，别让用户猜）。"""
    import winreg

    root = root or registry_root()
    places: list[str] = []
    commands: list[str] = []
    for place, _ in PLACES:
        key_path = _key(root, place)
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path + r"\command") as k:
                cmd = str(winreg.QueryValueEx(k, None)[0])
        except OSError:
            continue
        places.append(place)
        commands.append(cmd)
    return {
        "installed": len(places) == len(PLACES),
        "places": places,
        "command": commands[0] if commands else "",
        "text": MENU_TEXT,
    }
