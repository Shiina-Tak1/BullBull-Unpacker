"""上传前跑一遍：确认要提交的内容里没有本机隐私。

检查三件事：

  1. **不该存在的文件**：密码本 / 配置 / 日志 / 构建产物 / 虚拟环境 / 素材；
  2. **不该出现的内容**：本机用户名、用户目录、盘符私有路径、内网机器名；
  3. **体积**：找出 > 5MB 的文件（二进制应当走 Release，不进仓库）。

用法（在仓库根目录）：

    python tools/check_repo_clean.py

退出码 0 = 干净。有命中会逐条列出来，处理完再提交。
"""

from __future__ import annotations

import fnmatch
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

TEXT_EXT = {".py", ".md", ".txt", ".json", ".ini", ".ps1", ".bat", ".vbs", ".spec", ".yml", ".yaml"}
SKIP_DIRS = {".git", "__pycache__", ".venv", "dist", "release", "build/pyi", "shots",
             "tests/work", "tests/ui-base", "tests/ui-out"}

# 允许出现的绝对路径（文档里正常的示例）
ALLOW = {
    r"C:\Program Files\7-Zip",
    r"C:\Program Files\WinRAR",
    r"C:\Users\<用户名>",
}

BAD_FILES = {
    "密码本.txt": "用户的密码本",
    "config.json": "本机配置",
    "tree.txt": "整盘目录清单",
    "BullBull Unpacker.spec": "PyInstaller 产物",
}
BAD_FILE_PATTERNS = [
    (re.compile(r"\.log$"), "日志"),
    (re.compile(r"\.(mp4|mkv|zip|rar|7z|lz4|iso|apk)$", re.I), "素材/压缩包"),
    (re.compile(r"\.(exe|dll)$", re.I), "二进制（走 Release）"),
]
BAD_CONTENT = [
    (re.compile(r"C:\\Users\\[^\\\s\"'<>]+", re.I), "本机用户目录"),
    (re.compile(r"[A-Z]:\\(?:下载|通用工作区|资料|游戏)"), "作者私人盘符路径"),
]


def gitignore_patterns() -> list[str]:
    """读仓库根的 .gitignore：**被忽略的文件不算要提交的东西**。

    为什么必须认它：本地为了能跑起来，`tools/7z/` 下会放着 7z.exe/7z.dll（它们确实
    gitignore 了，不会进仓库）。不认 .gitignore 的话自查会把它们当成"该提交的二进制"
    报一堆假红——自查要回答的是"git add 会拿到什么"，而不是"磁盘上有什么"。
    """
    path = os.path.join(ROOT, ".gitignore")
    if not os.path.isfile(path):
        return []
    out = []
    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.strip()
        if line and not line.startswith("#"):
            out.append(line)
    return out


def ignored(rel_posix: str, name: str, patterns: list[str]) -> bool:
    """够用的 .gitignore 匹配：目录名、通配符、`dir/` 前缀、后缀大小写不敏感。"""
    parts = rel_posix.split("/")
    for pat in patterns:
        p = pat.rstrip("/")
        if not p:
            continue
        # `tools/7z/*.exe` 这种带目录的：整条路径匹配
        if "/" in p:
            if fnmatch.fnmatch(rel_posix.lower(), p.lower()):
                return True
            continue
        # 目录名 / 文件名 / 通配符
        if p.lower() in {x.lower() for x in parts}:
            return True
        if fnmatch.fnmatch(name.lower(), p.lower()):
            return True
    return False


def main() -> int:
    me = os.path.basename(__file__)
    problems: list[str] = []
    big: list[tuple[float, str]] = []
    scanned = 0
    ignores = gitignore_patterns()

    for dirpath, dirnames, filenames in os.walk(ROOT):
        # 目录名要按"相对仓库根的 posix 路径"判断。
        # 踩过：先 join 再 lstrip("./") 会因为 Windows 分隔符留下来（`.\release`.lstrip
        # 得到 `\release`），于是 release/ 没被跳过、把整个构建产物都当成了该提交的东西。
        def rel_posix(p: str) -> str:
            return os.path.relpath(p, ROOT).replace("\\", "/")

        dirnames[:] = [d for d in dirnames
                       if rel_posix(os.path.join(dirpath, d)) not in SKIP_DIRS and d != ".git"]
        for name in filenames:
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, ROOT)
            rel_p = rel.replace("\\", "/")
            if ignored(rel_p, name, ignores):
                continue                     # .gitignore 里的东西不会被提交，跳过
            size = os.path.getsize(path)
            if size > 5 * 1024 * 1024:
                big.append((size / 1024 / 1024, rel))
            if name in BAD_FILES:
                problems.append(f"不该提交的文件：{rel}（{BAD_FILES[name]}）")
                continue
            for rx, why in BAD_FILE_PATTERNS:
                if rx.search(name):
                    problems.append(f"不该提交的文件：{rel}（{why}）")
                    break
            if os.path.splitext(name)[1].lower() not in TEXT_EXT or name == me:
                continue
            try:
                with open(path, encoding="utf-8") as f:
                    text = f.read()
            except (OSError, UnicodeDecodeError):
                continue
            scanned += 1
            for rx, why in BAD_CONTENT:
                for m in rx.finditer(text):
                    hit = m.group(0)
                    if any(hit.lower().startswith(a.lower()) for a in ALLOW):
                        continue
                    line = text[:m.start()].count("\n") + 1
                    problems.append(f"{rel}:{line} 出现{why}：{hit}")

    print(f"扫了 {scanned} 个文本文件（跳过 .git / 构建产物 / 虚拟环境）")
    if big:
        print("\n大于 5MB 的文件（二进制请走 Release，不要提交）：")
        for size, rel in sorted(big, reverse=True):
            print(f"  {size:8.1f} MB  {rel}")
    if problems:
        print(f"\n★ 有问题 {len(problems)} 条：")
        for p in problems:
            print("  ·", p)
        return 1
    print("没有发现本机隐私 / 不该提交的文件 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
