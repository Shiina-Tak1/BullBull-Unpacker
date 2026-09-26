r"""隐私扫描 / 脱敏要看的**文本文件后缀** —— 全项目只此一份（`B-2026-062`）。

为什么要有它
------------
同一份"哪些后缀算文本"的名单，以前在三个地方各写一份、**三个不同的值**：

* `tools\check_repo_clean.py`：11 项（含 `.ini` / `.yml` / `.yaml`）；
* `tools\sync_repo.py`：8 项（**少** `.ini` / `.yml` / `.yaml`）；
* `build\build_portable.ps1`：7 项（还少 `.py` / `.spec`）。

三处做的是**同一条隐私闸门**（扫本机路径 / 用户名 / 真密码 / 真素材名），
所以名单漂了就是洞：`.ini` / `.yml` / `.yaml` 在"洗公开仓副本 / 洗便携包 docs"里
**根本不会被洗**，不是 UTF-8 的那份连自查都读不到（成因与后果见 `B-2026-062`）。
集合只有一处出处，三处**引用**它（§21.7：信息只有一个权威归属）。

放在 `core\` 而不是 `tools\`
---------------------------
`tools\*.py` **不进公开仓、也不进便携包**（`sync_repo.py` 的 `TOOLS` 是空表），
而 `build_portable.ps1` 在**公开仓克隆**里也要跑同一条扫描 —— 它靠 `python -c`
从这里取名单（见该脚本第 4 步的注释）。`core\` 在两种布局里都躺在仓库根，
是唯一两边都读得到的地方。
"""

from __future__ import annotations

import os

#: 会被"隐私扫描 / 脱敏"当成文本处理的后缀（**小写、带点**）。
#: 取三处旧名单的**并集**：以前短的那两处漏掉的后缀，现在都在这里。
TEXT_EXT: frozenset[str] = frozenset({
    ".py", ".md", ".txt", ".json", ".ini", ".ps1", ".bat", ".vbs", ".spec", ".yml", ".yaml",
})


def is_text_file(name: str) -> bool:
    """文件名是不是"该按文本扫"的那一类（后缀大小写不敏感）。"""
    return os.path.splitext(name)[1].lower() in TEXT_EXT
