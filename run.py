"""启动入口。

用法：
    .venv\\Scripts\\python.exe run.py                     # 打开界面
    .venv\\Scripts\\pythonw.exe run.py "D:\\某包.zip"        # 带着文件打开（挂进待处理列表）
    .venv\\Scripts\\pythonw.exe run.py --auto "D:\\某包.zip" # 带着文件并直接开始（手工用）

    run.py --install-shellmenu      # 只装右键菜单，不开界面（装完就退）
    run.py --uninstall-shellmenu    # 只卸右键菜单
    run.py --where                  # 打印"程序/资源/数据都在哪"，排错用

右键菜单（core/shellmenu.py）写的就是第二条命令：只把路径加进待处理列表，
不自动开始——解压到哪、重名怎么办这些都在界面上，先看一眼再点「开始」。

顺序很重要：

    1. 先处理**不需要 Qt** 的开关（装/卸右键菜单、--where）——打包之后这是
       "第一次运行顺手把菜单装上"和"卸载前清干净"的入口；
    2. 再用**纯标准库**解析参数、试着把路径转交给已经在跑的窗口（core/single.py）。
       连上就退出——这一路不 import Qt，几百毫秒结束，右键才不会"卡半天"；
    3. 转交不出去（没有窗口在跑 / 自己是主实例）才 import Qt 开界面。

已经在跑的窗口通过本机命名管道接手新路径，所以不会开出第二个窗口。
"""

from __future__ import annotations

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core import paths                          # noqa: E402
from core import shellmenu                      # noqa: E402
from core import single                         # noqa: E402
from core.appinfo import APP_NAME, VERSION      # noqa: E402
from core.launchargs import parse_launch_args   # noqa: E402


def _say(text: str = "") -> None:
    """打印一行；**打包成 exe 之后没有控制台**，那就同时写进日志。

    `--windowed` 的 exe 上 `sys.stdout` 是 None，直接 `print` 会抛异常——
    那样 `--where` / `--install-shellmenu` 这种"只在命令行用"的功能在打包版里
    一按就崩，用户还看不到任何原因。这里两手都做：有控制台就打，始终写日志。
    """
    try:
        print(text)
    except Exception:                            # noqa: BLE001 - 没有 stdout 很正常
        pass
    try:
        from core import paths as _p

        with open(_p.log_path(), "a", encoding="utf-8") as f:
            f.write(f"[命令行] {text}\n")
    except Exception:                            # noqa: BLE001
        pass


def handle_headless(argv: list[str]) -> "int | None":
    """处理"不需要开界面"的开关；不是这些开关就返回 None 继续正常流程。"""
    if "--install-shellmenu" in argv:
        try:
            keys = shellmenu.install_self()
        except Exception as exc:                 # noqa: BLE001 - 让调用方看到原因
            _say(f"装右键菜单失败：{exc!r}")
            return 1
        _say(f"{APP_NAME} {VERSION}：已装右键菜单（{len(keys)} 处）")
        return 0
    if "--uninstall-shellmenu" in argv:
        try:
            removed = shellmenu.uninstall_self()
        except Exception as exc:                 # noqa: BLE001
            _say(f"卸右键菜单失败：{exc!r}")
            return 1
        _say(f"{APP_NAME} {VERSION}：已移除 {len(removed)} 处右键菜单项")
        return 0
    if "--where" in argv:
        seven = os.path.join(paths.resource_dir(), "tools", "7z", "7z.exe")
        _say(f"{APP_NAME} {VERSION}")
        _say(f"  形态     : {'exe（打包版）' if paths.is_frozen() else '源码'}")
        _say(f"  程序目录 : {paths.app_dir()}")
        _say(f"  资源目录 : {paths.resource_dir()}")
        _say(f"  数据目录 : {paths.data_dir()}"
             + ("（程序目录写不进去，已落到用户目录）" if paths.is_appdata_mode()
                else "（就在程序旁边，便携）"))
        _say(f"  配置文件 : {paths.data_path('config.json')}")
        _say(f"  密码本   : {paths.data_path('密码本.txt')}")
        _say(f"  日志     : {paths.log_path()}")
        _say(f"  7-Zip    : {'有' if os.path.isfile(seven) else '缺'} {seven}")
        return 0
    return None


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv if argv is None else argv)

    rc = handle_headless(argv)
    if rc is not None:
        return rc

    launch_paths, auto = parse_launch_args(argv)

    # 先抢主实例。**没有路径时也要抢**——否则第一个窗口不持有互斥体，后面右键
    # 起来的进程会以为自己才是主实例、各自开窗口（实测就是这样开出 4 个进程的）。
    primary = single.become_primary()
    if not primary:
        if launch_paths:
            # 已经有主实例：把路径交给它。它可能正在 import Qt，所以给点耐心
            if single.forward(launch_paths, auto, timeout=8.0):
                return 0
            # 转交不出去（主实例刚崩）：退化成自己开一个，功能优先
        else:
            # 不带路径 = 用户就是想开界面，而界面已经开着 → 把那个窗口顶到前面就完事，
            # 别再开第二个（以前这种会并排开出两个一模一样的窗口）。
            # 但对方可能正好在退出（刚点过关闭）：那时"唤醒"会成功却没人接管，
            # 于是这次启动会什么都没留下——所以等一下再看互斥体还在不在。
            if single.forward([], False, timeout=1.5):
                time.sleep(0.4)
                if not single.become_primary():
                    return 0          # 对方还活着，窗口已经被顶到前面
                # 对方已经走了 → 落到下面自己开一个
    from ui.app import main as ui_main      # 到这一步才付 import Qt 的钱

    return ui_main(argv, paths=launch_paths, auto=auto)


if __name__ == "__main__":
    raise SystemExit(main())
