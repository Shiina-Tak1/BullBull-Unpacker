"""生成 README 用的三张图：**全部中性数据**（不出现作者的任何真实文件名/密码）。

产出（默认写到 <大目录>\\github\\shots\\）：

    01-extracting.png      主界面正在解压：普通 zip / 加密 7z / 伪装 mp4 / 分卷 一起跑
    02-password-vault.png  密码本页（合成示例密码）
    03-context-menu.png    资源管理器右键菜单（用作者自己截的那张，放这儿统一命名）

临时数据放 `D:\\Demo\\BullBullUnpacker-shot\\`（路径短、看起来中性），
跑完自动清掉；**绝不动用户真实的 `密码本.txt`/`config.json`**（base_dir 被钉在临时目录）。

用法：
    .venv\\Scripts\\python.exe tools\\shots_readme.py
"""

from __future__ import annotations

import json
import os
import random
import shutil
import struct
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # <big>/src
BIG = os.path.dirname(ROOT)                                          # <big>
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tools"))

os.environ.setdefault("QT_QPA_PLATFORM", "windows")                  # 要真窗口才能截得好看
os.environ.setdefault("SMART_UNZIP_NO_PROMPT", "1")                  # 缺密码就跳过，别弹窗卡住

from PySide6.QtWidgets import QApplication  # noqa: E402

from core.engine import find_engines  # noqa: E402
from ui.app import Workbench  # noqa: E402

STAGE = os.environ.get("SHOT_STAGE", r"D:\Demo\BullBullUnpacker-shot")
FILES = os.path.join(STAGE, "files")
OUT = os.environ.get("SHOT_OUT", os.path.join(BIG, "doc", "shots"))
SEVEN = find_engines().seven_zip
DEMO_PW = "demo123"
BIG_BYTES = 150 << 20          # 让解压跑得久一点，好截到"进行中"


def run_7z(args: list[str]) -> int:
    proc = subprocess.run([SEVEN, *args], stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT,
                          creationflags=0x08000000 if os.name == "nt" else 0)
    return proc.returncode


def fake_mp4_head() -> bytes:
    """一小段合法的 mp4 头（ftyp + free + mdat），让"伪装"这个词名副其实。"""
    box = b"ftyp" + b"isom" + struct.pack(">I", 0x200) + b"isomiso2avc1mp41"
    ftyp = struct.pack(">I", len(box) + 4) + box
    free = struct.pack(">I", 8) + b"free"
    return ftyp + free + struct.pack(">I", 8) + b"mdat"


def build_fixtures() -> list[str]:
    """造一批中性夹具，返回要加进清单的文件（顺序有讲究：大的放最前）。"""
    shutil.rmtree(STAGE, ignore_errors=True)
    os.makedirs(FILES)
    tmp = os.path.join(STAGE, "tmp")
    rnd = random.Random(20260915)

    # 1) project-backup.zip：**放在最后**，靠"文件数量"把解压拖到几秒
    #    （一堆小文件比一个大文件慢得多，占地却很小）——这样才截得到"进行中"
    src = os.path.join(tmp, "project-backup")
    os.makedirs(os.path.join(src, "docs"))
    with open(os.path.join(src, "docs", "readme.txt"), "w", encoding="utf-8") as f:
        f.write("Sample project files for the screenshot.\n")
    for i in range(6000):
        sub = os.path.join(src, "chunks", f"{i // 500:02d}")
        os.makedirs(sub, exist_ok=True)
        with open(os.path.join(sub, f"part-{i:05d}.bin"), "wb") as f:
            f.write(rnd.randbytes(512))
    run_7z(["a", "-tzip", "-bso0", "-bsp0",
            os.path.join(FILES, "project-backup.zip"), src])

    # 2) photos-2024.zip：普通小 zip（一层文件夹）
    src2 = os.path.join(tmp, "photos-2024")
    os.makedirs(src2)
    for i in range(3):
        with open(os.path.join(src2, f"photo-{i + 1:02d}.txt"), "w", encoding="utf-8") as f:
            f.write(f"pretend this is photo {i + 1}\n")
    run_7z(["a", "-tzip", "-bso0", "-bsp0",
            os.path.join(FILES, "photos-2024.zip"), src2])

    # 3) design-assets.7z：**加密**（密码 = 合成密码本里的 demo123，界面上会显示绿色密码）
    src3 = os.path.join(tmp, "design-assets")
    os.makedirs(src3)
    with open(os.path.join(src3, "palette.txt"), "w", encoding="utf-8") as f:
        f.write("#1a1a1a #2d2d2d #4ade80\n")
    run_7z(["a", "-t7z", "-bso0", "-bsp0", f"-p{DEMO_PW}",
            os.path.join(FILES, "design-assets.7z"), src3])

    # 4) lecture-video.mp4：前面垫真 mp4 头 + 随机数据，后面接一个 zip（识别成"伪装"）
    inner = os.path.join(tmp, "lecture-inner.zip")
    run_7z(["a", "-tzip", "-bso0", "-bsp0", inner, os.path.join(src2, "*")])
    head = fake_mp4_head()
    with open(os.path.join(FILES, "lecture-video.mp4"), "wb") as out:
        out.write(head + rnd.randbytes((2 << 20) - len(head)))
        with open(inner, "rb") as fi:
            shutil.copyfileobj(fi, out)

    # 5) 分卷：archive-2024.7z.001 / .002（每卷 1MB）
    src4 = os.path.join(tmp, "archive-2024")
    os.makedirs(src4)
    with open(os.path.join(src4, "log.txt"), "wb") as f:
        f.write(rnd.randbytes(3 << 20))
    run_7z(["a", "-t7z", "-v1m", "-bso0", "-bsp0",
            os.path.join(FILES, "archive-2024.7z"), src4])

    shutil.rmtree(tmp, ignore_errors=True)
    # 顺序：小的先跑（截图时它们已经"完成"了），大的那个放最后 → 截到"进行中"
    order = ["photos-2024.zip", "design-assets.7z", "lecture-video.mp4",
             "archive-2024.7z.001", "project-backup.zip"]
    got = [os.path.join(FILES, n) for n in order if os.path.isfile(os.path.join(FILES, n))]
    print("夹具：" + "\n      ".join(os.path.basename(p) for p in got))
    return got


def stage_base() -> str:
    """钉住数据目录：配置 + **合成**密码本（绝不碰用户真实的那份）。"""
    with open(os.path.join(STAGE, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"theme": "dark", "output_mode": "same", "conflict": "rename",
                   "max_depth": 4,
                   # 列宽预设：让"识别类型/密码"都完整显示（默认比例下会被省略号截掉）
                   "table_cols": [275, 195, 155, 195]}, f)
    with open(os.path.join(STAGE, "密码本.txt"), "w", encoding="utf-8") as f:
        f.write("# 示例密码本（截图用，全是编的）\n")
        f.write(f"{DEMO_PW}\t7\n")
        f.write("archive-key\t3\n")
        f.write("sample-pass\t1\n")
    return STAGE


def pump(app, seconds: float) -> None:
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        app.processEvents()
        time.sleep(0.01)


def grab(widget, name: str) -> None:
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    widget.grab().save(path)
    print(f"saved {path}  {widget.width()}x{widget.height()}")


def main() -> int:
    files = build_fixtures()
    app = QApplication(sys.argv)
    w = Workbench(base_dir=stage_base())
    w.show()
    w.resize(800, 900)
    pump(app, 1.0)

    mp = w.main_page
    mp.add_paths(files)
    mp.wait_scan()
    pump(app, 0.6)

    # 开跑，然后**在跑的过程中**截图：让清单里同时有"完成""进行中""排队中"
    mp.btn_start.click()
    deadline = time.monotonic() + 60
    grabbed = False
    while time.monotonic() < deadline:
        pump(app, 0.2)
        done = sum(1 for t in mp.tasks if getattr(t.status, "name", "") == "DONE")
        running = any(getattr(t.status, "name", "") == "RUNNING" for t in mp.tasks)
        if done >= 4 and running:
            # 多等一会儿再截：让计时器跳过 1~2 秒、进度条走一截、日志里出现心跳行，
            # 否则截到的是"刚开始 0.0s"那种看着像卡住的状态
            pump(app, 2.6)
            grab(w, "01-extracting.png")
            grabbed = True
            break
    if not grabbed:
        print("★ 没截到「进行中」那一瞬间（跑太快了？），退而截当前状态")
        grab(w, "01-extracting.png")

    # 密码本页（合成密码）
    mp.topbar.btn_library.click()
    pump(app, 1.2)
    grab(w, "02-password-vault.png")

    # 收工：**先把还在跑的任务停掉再退出**，否则解释器带着活着的 QThread 退出会崩
    # （退出码 0xC0000409），还会留下一个 7z.exe 进程
    mp.btn_stop.click()
    deadline = time.monotonic() + 20
    while w.worker is not None and time.monotonic() < deadline:
        pump(app, 0.2)
    pump(app, 0.5)
    print("已停表：", mp.eta.text())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
