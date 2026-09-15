"""命令行参数解析（**只用标准库**，所以 `run.py` 能在 import Qt 之前就用它）。

单独成模块的原因：右键菜单那条路要在"决定要不要开界面"之前就知道要处理哪些路径，
而 `ui/app.py` 一 import 就会把 PySide6 拖进来（1~2 秒）。参数解析挪到这儿，
`run.py` 就能先纯 Python 地把活转交出去。

Qt 自己的单横线选项（`-style Fusion` 这类）一律忽略，其中带值的那些要连它的值
一起吃掉，不然 "Fusion" 会被当成一个文件路径。
"""

from __future__ import annotations

QT_VALUE_OPTS = {
    "-style", "-stylesheet", "-platform", "-platformpluginpath", "-plugin",
    "-session", "-graphicssystem", "-display", "-geometry", "-title", "-name",
    "-qmljsdebugger", "-fontsize",
}


def parse_launch_args(argv: list[str]) -> tuple[list[str], bool]:
    """从命令行里挑出「要处理的路径」和「要不要直接开始」。

    右键菜单会传 `--auto "<路径>"`（见 core/shellmenu.py）；
    Qt 自己的参数一律忽略，别当成文件路径。
    """
    paths: list[str] = []
    auto = False
    skip_value = False
    for arg in list(argv)[1:]:
        if skip_value:
            skip_value = False
            continue
        if arg in ("--auto", "/auto"):
            auto = True
        elif arg in QT_VALUE_OPTS:
            skip_value = True
        elif arg.startswith("-"):
            continue
        elif arg:
            paths.append(arg)
    return paths, auto
