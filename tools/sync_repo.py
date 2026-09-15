"""把开发目录（src）与文档（doc）同步成"要上传 GitHub 的仓库副本"（github/）。

目录约定（整个项目就长这样）：

    BullBullUnpacker/
    ├─ src/        开发目录（代码 + 虚拟环境 + 测试；**不直接上传**）
    ├─ github/     仓库副本（= 要 push 的内容）；release/ 放构建产物（不进 git）
    ├─ doc/        所有文档（唯一来源：中文名不变，英文名全大写）
    ├─ snapshots/  打包/大改之前的快照
    └─ backup/     拿不准要不要留的东西

一条命令跑完：拷贝白名单 → 脱敏 → 清掉不该有的 → 自查。
路径全部相对本文件推导，**整个大目录可以整体搬走**。

用法（在 src 里）：
    .venv\\Scripts\\python.exe tools\\sync_repo.py
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))       # <big>/src/tools
SRC = os.path.dirname(HERE)                             # <big>/src
BIG = os.path.dirname(SRC)                              # <big>
DST = os.path.join(BIG, "github")                       # 仓库副本
DOC = os.path.join(BIG, "doc")                          # 文档唯一来源
RELEASE = os.path.join(DST, "release")                  # 构建产物（.gitignore 里忽略）

# 从 src 拷进仓库的目录 / 文件（仓库根的**布局按作者定稿的那套**）
DIRS = ["core", "ui", "assets", "build"]
FILES = ["run.py", "cli.py", "启动.bat", "启动.vbs", "启动_调试.bat"]
TOOLS = ["smoke_core.py", "smoke_pipeline.py", "smoke_ui.py", "verify_shellmenu.py",
         "verify_window.py", "verify_portable.py", "shots.py", "shots_dpi.py",
         "shots_readme.py", "make_icon.py", "check_repo_clean.py", "sync_repo.py"]
# 仓库里放这些：
#   docs\         LICENSE / REQUIREMENTS.txt / THIRD-PARTY.md（文档都收在这儿）
#   assets\       bbu.ico + icon.png（图标源图也放 assets 里）
#   shots\        README 引用的截图
REPO_DOCS = [("LICENSE", "docs/LICENSE"),
             ("requirements.txt", "docs/REQUIREMENTS.txt"),   # 从 src\ 取，改名大写
             ("THIRD-PARTY.md", "docs/THIRD-PARTY.md")]
ICON_SRC = "icon.png"
ICON_DST = "assets/icon.png"

# 不进公开仓库的东西（相对 DST）
DROP_IN_REPO = ["build/pyi", "build/version.txt", "logs", "tests",
                "config.json", "密码本.txt"]
# 内部文档：真实素材名 + 本机路径，不公开（外层 doc\ 里留着，仓库里不放）
DOC_PRIVATE = ["引擎实测笔记.md", "上传说明.md"]

# 脱敏规则（只改副本）
RULES = [
    (r"D:\incoming", r"D:\incoming"),
    (r"D:\incoming", r"D:\incoming"),
    (r"<项目目录>", r"<项目目录>"),
    (r"<大目录>", r"<大目录>"),
    (r"<项目目录>", r"<项目目录>"),
    (r"<工作区>", r"<工作区>"),
    ("<用户名>", "<用户名>"),
    ("素材目录", "素材目录"),
    ("示例包", "示例包"),
    ("样例视频.mp4", "样例视频.mp4"),
    ("样例视频", "样例视频"),
    ("教程视频", "教程视频"),
    ("BullBullUnpacker", "BullBullUnpacker"),      # 旧名字（shellmenu 里那个常量除外）
]
KEEP_OLD_NAME = {os.path.join("core", "shellmenu.py")}
TEXT_EXT = {".py", ".md", ".txt", ".json", ".ps1", ".bat", ".vbs", ".spec"}


def copy_whitelist() -> None:
    os.makedirs(DST, exist_ok=True)
    for d in DIRS:
        target = os.path.join(DST, d)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(os.path.join(SRC, d), target)
    os.makedirs(os.path.join(DST, "tools", "7z"), exist_ok=True)
    for f in TOOLS:
        s = os.path.join(SRC, "tools", f)
        if os.path.isfile(s):
            shutil.copy2(s, os.path.join(DST, "tools", f))
    for f in FILES:
        s = os.path.join(SRC, f)
        if os.path.isfile(s):
            shutil.copy2(s, os.path.join(DST, f))
    # 文档：LICENSE / REQUIREMENTS.txt / THIRD-PARTY.md 都放仓库的 docs\
    # 先清空：这样"从最外层删掉一份文档"能真的反映到仓库里（不然旧文件一直赖着）
    shutil.rmtree(os.path.join(DST, "docs"), ignore_errors=True)
    for src_name, rel in REPO_DOCS:
        base = SRC if src_name == "requirements.txt" else DOC
        s = os.path.join(base, src_name)
        if not os.path.isfile(s):
            print(f"  ★ 找不到文档源：{s}")
            continue
        dst = os.path.join(DST, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(s, dst)
    # 图标源图放 assets\（跟 bbu.ico 放一起）
    icon = os.path.join(SRC, ICON_SRC)
    if os.path.isfile(icon):
        shutil.copy2(icon, os.path.join(DST, ICON_DST.replace("/", os.sep)))
    # 截图（README 里引用的）：唯一来源是 doc\shots\，仓库里放 shots\
    shots_src = os.path.join(DOC, "shots")
    shots_dst = os.path.join(DST, "shots")
    if os.path.isdir(shots_src):
        shutil.rmtree(shots_dst, ignore_errors=True)
        os.makedirs(shots_dst, exist_ok=True)
        for f in os.listdir(shots_src):
            if os.path.isfile(os.path.join(shots_src, f)):
                shutil.copy2(os.path.join(shots_src, f), os.path.join(shots_dst, f))
    print(f"拷贝：{len(DIRS)} 个目录 + {len(TOOLS)} 个工具 + {len(FILES)} 个根文件"
          f" + docs\\ {len(REPO_DOCS)} 份 + 图标 + 截图")


def sanitize() -> None:
    changed = 0
    for root, dirs, files in os.walk(DST):
        dirs[:] = [d for d in dirs if d not in {"__pycache__", ".git", "release"}]
        for name in files:
            if os.path.splitext(name)[1].lower() not in TEXT_EXT:
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, DST)
            try:
                text = open(path, encoding="utf-8").read()
            except (OSError, UnicodeDecodeError):
                continue
            original = text
            for src, dst in RULES:
                if rel in KEEP_OLD_NAME and src == "BullBullUnpacker":
                    continue
                text = text.replace(src, dst)
            if text != original:
                open(path, "w", encoding="utf-8").write(text)
                changed += 1
    print(f"脱敏：改了 {changed} 个文件")


def drop_extras() -> None:
    dropped = []
    for rel in DROP_IN_REPO:
        p = os.path.join(DST, rel.replace("/", os.sep))
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
            dropped.append(rel)
        elif os.path.isfile(p):
            os.remove(p)
            dropped.append(rel)
    # 内部文档（实测笔记/上传说明）只在最外层 doc\ 留着，仓库里不该有
    for name in DOC_PRIVATE:
        p = os.path.join(DST, "docs", name)
        if os.path.isfile(p):
            os.remove(p)
            dropped.append(os.path.relpath(p, DST))
    for root, dirs, _files in os.walk(DST):
        for d in list(dirs):
            if d == "__pycache__":
                shutil.rmtree(os.path.join(root, d), ignore_errors=True)
                dirs.remove(d)
    print(f"清掉 {len(dropped)} 项：{', '.join(sorted(dropped)) or '（无）'}")


def main() -> int:
    if not os.path.isdir(SRC) or not os.path.isdir(DOC):
        print(f"目录不像预期：{SRC} / {DOC}")
        return 1
    os.makedirs(RELEASE, exist_ok=True)
    copy_whitelist()
    sanitize()
    drop_extras()
    print("\n== 自查 ==")
    py = os.path.join(SRC, ".venv", "Scripts", "python.exe")
    return subprocess.run([py, os.path.join(DST, "tools", "check_repo_clean.py")]).returncode


if __name__ == "__main__":
    sys.exit(main())
