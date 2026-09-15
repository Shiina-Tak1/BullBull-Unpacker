"""应用的身份信息：名字、版本、图标文件名。

**只在这里写一遍**——界面标题、exe 的版本资源（打包时生成）、文档里的版本号都读它。
以前版本号散在 UI 文案里（手写 "v1.0"），改一处漏一处；打包之后
"文件属性里显示的版本"又是第三个地方，更容易对不上。
"""

from __future__ import annotations

APP_NAME = "BullBull Unpacker"
# exe/任务栏用的版本号（打包时写进版本资源；界面显示也用它）
VERSION = "1.0.0"
# 图标文件名（在 assets/ 下；换图标要重跑 tools/make_icon.py）
ICON_FILE = "bbu.ico"
# 任务栏/AppUserModelID 用；保持稳定，别随便改（改了任务栏图标会重新分组）
APP_ID = "BullBull.Unpacker.1"
# 右键菜单的 verb 名（注册表键名）
SHELL_VERB = "BBUUnpack"
# 右键菜单文案
SHELL_MENU_TEXT = "添加到BBU解压列表"
