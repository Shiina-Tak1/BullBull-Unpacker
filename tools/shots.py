"""UI 集成测试 + 截图：不弹窗口，喂真实文件、跑真实解压，然后 grab 成 PNG。

这既是截图工具，也是 M3 的验收测试——假数据版已被真实管道取代，
所以这里必须真的走一遍：扫描 → 解压 → 汇总。

用法：
    .venv\\Scripts\\python.exe tools\\shots.py
    SHOT_BACKEND=offscreen ...  shots.py    # 兜底：纯离屏（字体需手工注册）

输出：
    shots\\*.png
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import time

BACKEND = os.environ.get("SHOT_BACKEND", "native")
if BACKEND == "offscreen":
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
# 自动化跑批时绝不能弹模态窗：没人点按钮，流程会永久挂住
os.environ.setdefault("SMART_UNZIP_NO_PROMPT", "1")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tools"))

import smoke_core as sc  # noqa: E402

from PySide6.QtCore import Qt  # noqa: E402
from PySide6.QtGui import QFont, QFontDatabase  # noqa: E402
from PySide6.QtWidgets import QApplication, QTableWidgetSelectionRange  # noqa: E402

from ui.app import PasswordDialog, Workbench  # noqa: E402

OUT = os.path.join(ROOT, os.environ.get("SHOT_OUT", "shots"))
DEMO = os.path.join(ROOT, "tests", "demo")
os.makedirs(OUT, exist_ok=True)

# 只截"版式"那几张，不跑真实解压：给多 DPI 巡检用（100/125/150/200% 各跑一遍）
DPI_ONLY = os.environ.get("SHOT_DPI_ONLY") == "1"

# 仅 offscreen 后端需要：该平台 families() 为空，中文会全渲染成方块
FONT_FILES = (
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\msyhbd.ttc",
    r"C:\Windows\Fonts\consola.ttf",
    r"C:\Windows\Fonts\segoeui.ttf",
    r"C:\Windows\Fonts\seguiemj.ttf",
    r"C:\Windows\Fonts\seguisym.ttf",
)


def register_fonts() -> None:
    loaded = []
    for p in FONT_FILES:
        if os.path.exists(p):
            fid = QFontDatabase.addApplicationFont(p)
            if fid != -1:
                loaded.extend(QFontDatabase.applicationFontFamilies(fid))
    print("fonts loaded:", loaded)
    for want in ("Microsoft YaHei", "Consolas"):
        if want in QFontDatabase.families():
            f = QFont(want)
            f.setPointSize(9)
            QApplication.setFont(f)
            break


def grab(widget, name: str) -> None:
    app = QApplication.instance()
    for _ in range(4):
        app.processEvents()
    path = os.path.join(OUT, name)
    widget.grab().save(path)
    print(f"saved {path}  {widget.width()}x{widget.height()}")


def stage_base() -> str:
    """给界面测试准备一份独立的运行目录：跑真实解压时飞轮会写回密码本，
    直接指向工程根目录会把用户的密码本改脏。顺带保证测试密码在册。

    配置文件每次重写成"只有主题"：界面用例会拖列宽并把宽度存进 config.json，
    不重置的话截图里就是上一轮拖出来的怪比例。
    """
    base = os.path.join(ROOT, "tests", "ui-base")
    os.makedirs(base, exist_ok=True)
    sc.ensure_book(os.path.join(base, "密码本.txt"))
    with open(os.path.join(base, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"theme": "dark"}, f)
    return base

def stage_demo() -> str:
    """准备一批真实文件：加密的、伪装的、嵌套的、含「删」字的。"""
    shutil.rmtree(DEMO, ignore_errors=True)
    os.makedirs(DEMO)
    names = ["示例包.zip", "教程视频.mp4", "嵌套.zip", "学习资料.z删i删p删"]
    if sc.WINRAR:
        names += ["示例包.rar", "资源分卷.part1.rar", "资源分卷.part2.rar"]
    for name in names:
        src = sc.fx(name)
        if os.path.isfile(src):
            shutil.copyfile(src, os.path.join(DEMO, name))
    return DEMO


def pump(app, seconds: float) -> None:
    """跑一段事件循环——QThread 的信号必须有事件循环才会送达界面线程。"""
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        app.processEvents()
        time.sleep(0.01)


def wait_for_job(app, w, timeout: float = 180.0) -> bool:
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        app.processEvents()
        time.sleep(0.01)
        if w.worker is None:
            pump(app, 0.4)
            return True
    return False


def main() -> int:
    app = QApplication(sys.argv)
    if BACKEND == "offscreen":
        register_fonts()

    if not os.path.isfile(sc.fx("示例包.zip")):
        print("== 夹具不存在，先造 ==")
        sc.build_fixtures()

    demo = stage_demo()

    w = Workbench(base_dir=stage_base())
    # 不 resize：就用界面自己按屏幕算出来的默认尺寸（现在是 800×900 逻辑像素）。
    # 截图要能代表"用户双击打开时看到的样子"，改布局时才看得出版式有没有被挤坏。
    w.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
    w.show()
    pump(app, 0.5)
    print(f"dpr={w.devicePixelRatioF()} logical={w.width()}x{w.height()} "
          f"physical={int(w.width() * w.devicePixelRatioF())}x"
          f"{int(w.height() * w.devicePixelRatioF())}")

    # 1) 空状态
    grab(w, "01-empty.png")

    # 2) 工作态：真的扫描一遍
    targets = ["示例包.zip", "教程视频.mp4", "嵌套.zip", "学习资料.z删i删p删"]
    if sc.WINRAR:
        targets += ["示例包.rar", "资源分卷.part1.rar"]
    w.main_page.add_paths([os.path.join(demo, n) for n in targets if os.path.isfile(os.path.join(demo, n))])
    w.main_page.wait_scan()
    pump(app, 0.3)
    grab(w, "02-work.png")

    # 3) 输出设置那一行（主界面最外层，截图里要看清楚）：换成"指定目录"的样子
    w.main_page.rb_custom.setChecked(True)
    w.main_page.ed_outdir.setText(os.path.join(ROOT, "tests", "ui-out"))
    w.main_page.cmb_conflict.setCurrentIndex(1)      # 覆盖
    pump(app, 0.2)
    grab(w, "02b-output.png")
    w.main_page.rb_same.setChecked(True)
    pump(app, 0.2)

    # 最小尺寸也要能看（用户报过"只有最小宽度没有最小高度 → 元素重叠"）
    if os.environ.get("SHOT_MIN") == "1":
        w.resize(w.minimumWidth(), w.minimumHeight())
        pump(app, 0.5)
        grab(w, "08-min-size.png")
        w.resize(800, 900)
        pump(app, 0.3)

    # 4) 密码库（真实 vault）：顺手选中几行，展示多选批量删除
    w.library_page.reload()
    w.stack.setCurrentWidget(w.library_page)
    if w.library_page.table.rowCount() >= 3:
        w.library_page.table.setRangeSelected(QTableWidgetSelectionRange(0, 0, 2, 1), True)
    pump(app, 0.2)
    grab(w, "04-library.png")

    # 5) 设置（真实引擎探测结果）
    w.stack.setCurrentWidget(w.settings_page)
    grab(w, "05-settings.png")

    if DPI_ONLY:
        # 版式巡检到此为止：上面这几页已经覆盖顶部按钮、清单、表头那两行、
        # 密码本表格、设置表单——排版会不会挤坏全看得出来，不用真解压
        print(f"\n[DPI_ONLY] 完成，输出目录 {OUT}")
        return 0

    # 6) 真跑一遍：开始 → 等结束（结果写进日志，不再跳结算页）
    w.stack.setCurrentWidget(w.main_page)
    w._on_start()
    ok = wait_for_job(app, w, timeout=240)
    print(f"job finished: {ok}")
    pump(app, 0.6)
    grab(w, "06-result.png")

    # 7) 手动输密码弹窗（真验证器：示例包.zip 的密码是 abc123）
    #    故意输错一个：密码对的时候弹窗会立刻关掉继续解压，截不到画面
    dlg = PasswordDialog(
        w.theme,
        "示例包.zip",
        verifier=lambda pw: w.extractor.test(sc.fx("示例包.zip"), pw).ok,
    )
    dlg.edit.setText("abc1234")
    dlg.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
    dlg.show()
    pump(app, 0.2)
    dlg._verify()
    pump(app, 0.3)
    grab(dlg, "07-dialog.png")

    print("\n== 任务结果 ==")
    for it in w.main_page.tasks:
        print(f"  {it.status.value:<8} {it.name:<22} {it.source:<12} {it.note or it.stop_reason}")
    print(f"\n打开输出目录 → {w.main_page._open_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
