"""UI 无界面单元测试：把「点按钮加文件 → 开始按钮该亮」这类问题钉死。

之前踩的坑：只有拖拽才会发 sig_paths_added，走「选择文件」加进来的任务，
开始按钮一直是灰的，界面看着像"没反应"。这个测试就是为它写的。

用法：
    .venv\\Scripts\\python.exe tools\\smoke_ui.py
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import threading
import time

BACKEND = os.environ.get("SHOT_BACKEND", "native")
if BACKEND == "offscreen":
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
os.environ.setdefault("SMART_UNZIP_NO_PROMPT", "1")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tools"))

import smoke_core as sc  # noqa: E402

from PySide6.QtCore import QPoint, Qt  # noqa: E402
from PySide6.QtWidgets import (  # noqa: E402
    QAbstractItemView,
    QApplication,
    QDialog,
    QHeaderView,
    QLabel,
    QMessageBox,
    QPushButton,
    QTableWidgetSelectionRange,
)

from core.pipeline import ItemStatus  # noqa: E402
from ui.app import Workbench  # noqa: E402

DEMO = os.path.join(ROOT, "tests", "ui-demo")
# 测试必须用**自己的** base_dir：跑真实解压时飞轮会把命中写回密码本，
# 直接指向工程根目录会把用户的密码本改脏
BASE = os.path.join(ROOT, "tests", "ui-base")


def stage_base() -> str:
    """给界面测试准备一份独立的运行目录。

    两件事必须每次做，否则用例之间会互相污染：

      * config.json **重写**成深色：主题用例会把配置存成浅色 / 存上列宽与冲突策略，
        不重置的话第二次跑就带着上一次的状态启动；
      * 密码本 **重新拷**一份，并保证里面有测试用的那把密码（示例包.zip 的 abc123）——
        以前只在"文件不存在"时才拷，被别的用例写坏之后就一直是坏的，
        表现出来是"3 个任务全部成功"莫名其妙变成 2 个成功 1 个跳过。
    """
    os.makedirs(BASE, exist_ok=True)
    sc.ensure_book(os.path.join(BASE, "密码本.txt"))
    import json

    with open(os.path.join(BASE, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"theme": "dark"}, f)
    return BASE


def pump(app, seconds: float) -> None:
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        app.processEvents()
        time.sleep(0.01)


def wait_job(app, w, timeout: float = 120.0) -> bool:
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        app.processEvents()
        time.sleep(0.01)
        if w.worker is None:
            pump(app, 0.3)
            return True
    return False


def wait_cond(app, cond, timeout: float = 10.0) -> bool:
    """等某个条件成立（后台线程干完活回来改界面），期间照常跑事件循环。"""
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        app.processEvents()
        time.sleep(0.01)
        if cond():
            return True
    return False


def stage() -> str:
    shutil.rmtree(DEMO, ignore_errors=True)
    os.makedirs(DEMO)
    for name in ("示例包.zip", "教程视频.mp4", "嵌套.zip"):
        src = sc.fx(name)
        if os.path.isfile(src):
            shutil.copyfile(src, os.path.join(DEMO, name))
    return DEMO


def main() -> int:
    app = QApplication(sys.argv)
    if not os.path.isfile(sc.fx("示例包.zip")):
        sc.build_fixtures()
    demo = stage()

    w = Workbench(base_dir=stage_base())
    w.resize(1340, 900)
    w.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
    w.show()
    pump(app, 0.4)
    mp = w.main_page

    # --- 启动初始态 ---
    sc.check(not mp.btn_start.isEnabled(), "刚打开时「开始」是灰的（没有任务）")
    sc.check(not mp.table.isVisible(), "刚打开时显示拖拽区、不显示表格")

    # --- 关键回归：走「选择文件」那条路（不是拖拽）加文件 ---
    added = mp.add_paths([os.path.join(demo, n) for n in os.listdir(demo)])
    mp.wait_scan()
    sc.check(added == 3 and mp.table.rowCount() == 3,
             "★ add_paths 提交 3 个路径，后台扫描完表格里就是 3 行",
             f"提交 {added} / 行数 {mp.table.rowCount()}")
    sc.check(mp.btn_start.isEnabled(),
             "★ 用按钮加文件后「开始」必须变亮（曾经的 bug）",
             f"enabled={mp.btn_start.isEnabled()}")
    sc.check(mp.table.isVisible() and mp.table.rowCount() == 3,
             "表格显示出来且有 3 行", f"rowCount={mp.table.rowCount()}")
    sc.check(len(mp.log.toPlainText().strip()) > 0,
             "日志面板有输出（不是「什么都没发生」）",
             mp.log.toPlainText().strip().splitlines()[-1][:60] if mp.log.toPlainText().strip() else "")

    # --- ★ 扫描在后台线程：add_paths 立刻返回，界面不卡 ---
    mp.clear_tasks()
    t0 = time.monotonic()
    mp.add_paths([os.path.join(demo, n) for n in os.listdir(demo)])
    elapsed = time.monotonic() - t0
    sc.check(elapsed < 0.5, "★ add_paths 立刻返回（扫描不在 UI 线程上做）",
             f"{elapsed * 1000:.0f} ms")
    sc.check(mp._scan_job is not None, "扫描任务真的起来了", str(mp._scan_job))
    sc.check(mp.scan_badge.text() != "", "★ 日志区挂着「扫描中…」，用户知道点了有反应",
             mp.scan_badge.text())
    mp.wait_scan()
    sc.check(mp._scan_job is None and mp.scan_badge.text() == "" and mp.table.rowCount() == 3,
             "扫完：任务清掉、提示收掉、3 行都在",
             f"rows={mp.table.rowCount()} badge={mp.scan_badge.text()!r}")
    # 扫描期间又拖进来一批 → 排队，不能丢。
    #   注意要用**清单里还没有的**文件：同名同路径会被"跨批去重"挡掉（那是另一条规则，
    #   下面那个用例专门验它）。
    late_dir = os.path.join(ROOT, "tests", "work", "late-add")
    shutil.rmtree(late_dir, ignore_errors=True)
    os.makedirs(late_dir, exist_ok=True)
    late_a = os.path.join(late_dir, "后加的A.zip")
    late_b = os.path.join(late_dir, "后加的B.zip")
    shutil.copyfile(os.path.join(demo, "嵌套.zip"), late_a)
    shutil.copyfile(os.path.join(demo, "示例包.zip"), late_b)
    mp.add_paths([late_a])
    mp.add_paths([late_b])
    mp.wait_scan()
    sc.check(len(mp.tasks) == 5, "★ 扫描中再拖进来的路径会排队，不会丢",
             f"{len(mp.tasks)} 项")
    # ★ 跨批去重：同一个包再加一次不该冒第二行（右键多选分卷就靠这条兜底）
    mp.add_paths([late_a])
    mp.wait_scan()
    sc.check(len(mp.tasks) == 5, "★ 同一个包重复添加不会出现两行（跨批去重）",
             f"{len(mp.tasks)} 项")
    mp.clear_tasks()
    mp.add_paths([os.path.join(demo, n) for n in os.listdir(demo)])
    mp.wait_scan()

    # --- ★ 清空列表 / 清除选中 ---
    sc.check(mp.btn_clear.text() == "清空列表" and mp.btn_clear_sel.text() == "清除选中",
             "★ 按钮文案是「清空列表」+「清除选中」",
             f"{mp.btn_clear.text()} / {mp.btn_clear_sel.text()}")
    sc.check(not mp.btn_clear_sel.isEnabled(), "没选中任何行时「清除选中」是灰的")
    mp.table.setRangeSelected(QTableWidgetSelectionRange(0, 0, 1, 3), True)
    pump(app, 0.1)
    sc.check(mp.btn_clear_sel.isEnabled(), "选中两行之后「清除选中」可点",
             str([i.row() for i in mp.table.selectedIndexes()]))
    before = len(mp.tasks)
    mp.btn_clear_sel.click()
    sc.check(len(mp.tasks) == before - 2 and mp.table.rowCount() == before - 2,
             "★ 清除选中：只把那两行删掉", f"{before} → {len(mp.tasks)}")
    sc.check("已清除选中 2 项" in mp.log.toPlainText().splitlines()[-1],
             "日志说清删了几项", mp.log.toPlainText().splitlines()[-1][:60])
    mp.btn_clear.click()
    sc.check(not mp.tasks and mp.table.rowCount() == 0, "★ 清空列表：整张表清掉")
    sc.check("已清空列表" in mp.log.toPlainText().splitlines()[-1],
             "日志说清了几项", mp.log.toPlainText().splitlines()[-1][:60])
    mp.add_paths([os.path.join(demo, n) for n in os.listdir(demo)])
    mp.wait_scan()

    # --- ★ 日志是增量渲染的（以前每行都重画整段，几百行就把界面卡死）---
    mp.clear_tasks()
    t0 = time.monotonic()
    for i in range(600):
        mp.append_log("测试", f"第 {i} 行", "text_dim")
    dt = time.monotonic() - t0
    sc.check(dt < 1.0, "★ 连打 600 行日志不卡（增量渲染，不是每行重画整段）",
             f"{dt * 1000:.0f} ms")
    lines = mp.log.toPlainText().splitlines()
    sc.check(len(lines) == 600 and "第 599 行" in lines[-1],
             "600 行都在面板里、最后一行是最新的", f"{len(lines)} 行")
    for i in range(400):
        mp.append_log("测试", f"追加 {i}", "text_dim")
    lines = mp.log.toPlainText().splitlines()
    sc.check(len(lines) <= 800 and "追加 399" in lines[-1],
             "★ 超过 800 行后最老的滚掉，控件不会无限长", f"{len(lines)} 行")
    # ★ 日志要自己滚到最底部（用户报的"任务日志不会正确滚动到最底部"）。
    #   以前的实现只有一句 ensureCursorVisible()，而光标从没被移到文末 → 等于没滚。
    pump(app, 0.2)
    bar = mp.log.verticalScrollBar()
    sc.check(bar.value() == bar.maximum() and bar.maximum() > 0,
             "★ 日志跟随最新一行滚到底（不是停在最上面）",
             f"value={bar.value()} max={bar.maximum()} stick={mp._log_stick} "
             f"scrolling={mp._log_scrolling}")
    bar.setValue(0)                      # 用户手动翻到顶 → 应该暂停跟随
    pump(app, 0.1)
    mp.append_log("测试", "手动翻历史时不该被拽回去", "text_dim")
    pump(app, 0.1)
    sc.check(mp.log.verticalScrollBar().value() == 0,
             "★ 用户往上翻看历史时不会被强行拽回底部",
             f"value={mp.log.verticalScrollBar().value()}")
    bar.setValue(bar.maximum())          # 再滚回底部 → 恢复跟随
    pump(app, 0.1)
    mp.append_log("测试", "回到底部后继续跟随", "text_dim")
    pump(app, 0.1)
    sc.check(mp.log.verticalScrollBar().value() == mp.log.verticalScrollBar().maximum(),
             "★ 滚回底部后恢复自动跟随")
    mp.clear_tasks()

    # --- 加文件夹：汇总行 + 子项 ---
    mp2_rows = mp.table.rowCount()
    mp.add_paths([demo])
    mp.wait_scan()
    sc.check(mp.table.rowCount() > mp2_rows, "再拖入文件夹会新增汇总行+子项",
             f"{mp2_rows} → {mp.table.rowCount()}")

    # --- ★ 文件夹汇总行的状态要看孩子（用户："处理子任务时应该显示进行中，而不是排队中"）---
    from ui.app import FOLDER_ROLE

    folder_row = next((i for i, t in enumerate(mp.tasks) if t.is_dir), None)
    if folder_row is None:
        sc.check(False, "★ 文件夹汇总行存在（下面的状态断言需要它）")
    else:
        cell = mp.table.item(folder_row, 3)
        sc.check(cell is not None and bool(cell.data(FOLDER_ROLE)),
                 "★ 文件夹行有「汇总状态」（不是拿它自己的 status 显示）",
                 str(cell.data(FOLDER_ROLE) if cell else None))
        kids = [i for i, t in enumerate(mp.tasks)
                if t.parent == folder_row and not t.is_dir and t.runnable]
        sc.check(bool(kids), "文件夹下面有可执行的子项", f"{len(kids)} 个")
        if kids:
            # 全部排队 → 显示排队中；有一个在跑 → 显示进行中
            for i in kids:
                mp.tasks[i].status = ItemStatus.QUEUED
            sc.check("排队中" in mp._folder_status(folder_row)[0],
                     "子项都在排队 → 文件夹显示排队中", mp._folder_status(folder_row)[0])
            mp.tasks[kids[0]].status = ItemStatus.RUNNING
            text = mp._folder_status(folder_row)[0]
            sc.check("进行中" in text, "★ 有子项在跑 → 文件夹显示「进行中」", text)
            for i in kids:
                mp.tasks[i].status = ItemStatus.DONE
            text = mp._folder_status(folder_row)[0]
            sc.check("完成" in text and "进行中" not in text,
                     "★ 子项都跑完 → 文件夹显示完成 x/y", text)
            for i in kids:
                mp.tasks[i].status = ItemStatus.QUEUED
    mp.clear_tasks()

    # --- 点「开始」真跑 ---
    mp.clear_tasks()
    mp.add_paths([os.path.join(demo, n) for n in os.listdir(demo)])
    mp.wait_scan()
    sc.check(mp.btn_start.isEnabled(), "清空后重新加文件，开始按钮仍可用")
    mp.btn_start.click()
    pump(app, 0.2)
    sc.check(not mp.btn_start.isEnabled() and mp.btn_stop.isEnabled(),
             "点开始后：开始按钮禁用、停止按钮可用")
    ok = wait_job(app, w)
    sc.check(ok, "任务批跑完成")
    leaves = [t for t in mp.tasks if t.runnable]
    done = [t for t in leaves if t.status is ItemStatus.DONE]
    sc.check(len(done) == len(leaves), f"{len(leaves)} 个任务全部成功",
             str([(t.name, t.status.value) for t in leaves]))
    sc.check(mp.btn_start.isEnabled() and not mp.btn_stop.isEnabled(),
             "跑完后按钮状态复位")
    sc.check(len(mp.log.toPlainText().strip()) > 50, "日志累积了运行过程")
    # ★ 引擎的命令行/原始输出这类细节**不进界面**，但一定进 logs\run.log（带打码）
    panel_text = mp.log.toPlainText()
    sc.check("▶ 运行中" not in panel_text,
             "★ 引擎命令行等细节不出现在界面日志里（不再刷屏、也不再露密码）",
             [ln for ln in panel_text.splitlines() if "运行中" in ln][:1] or "面板里没有 ✓")
    # 心跳走的是 info 通道（跟引擎实际发的同一行），界面上必须看得见
    mp.append_log("引擎", "…仍在运行（已 30s）", "text_faint")
    pump(app, 0.15)
    sc.check("仍在运行" in mp.log.toPlainText(),
             "★ 「…仍在运行（已 Ns）」心跳**要**显示（用户指定：怕看着像卡死）",
             [ln for ln in mp.log.toPlainText().splitlines() if "仍在运行" in ln][-1:])
    # 而 debug 级（命令行/每次试密码/原始输出）只落盘，不进界面
    mp.append_log("引擎", "▶ 运行中：7z.exe l -p*** -- 某个包.zip", "text_faint",
                  level="debug")
    pump(app, 0.15)
    sc.check("某个包.zip" not in mp.log.toPlainText(),
             "★ debug 级的行永远不进界面（只写 run.log）")
    import ui.app as _app_for_debug

    run_log_file = _app_for_debug.paths_mod.run_log_path()
    log_body = open(run_log_file, encoding="utf-8", errors="replace").read() \
        if os.path.isfile(run_log_file) else ""
    sc.check("▶ 运行中" in log_body and "[详细]" in log_body,
             "★ 这些细节落在 run.log 里（带 [详细] 前缀，排错全靠它）",
             [ln for ln in log_body.splitlines() if "运行中" in ln][-1:])
    sc.check("$ " in log_body,
             "★ 引擎的原始输出也落在 run.log 里（界面上不显示）",
             [ln for ln in log_body.splitlines() if ln.strip().startswith("$ ")][-1:])
    # 界面上没有「详细」这种开关（用户明确不要）：细节永远只在文件里
    sc.check(not hasattr(mp, "btn_verbose"),
             "★ 界面没有「详细」开关（细节只落盘，界面保持干净）")
    # --- ★ 「开始/继续」与「暂停」互斥（用户要的：开始按钮承担继续）---
    class _StubWorker:
        def __init__(self) -> None:
            self.calls: list[str] = []
            self.value = False

        def is_paused(self) -> bool:
            return self.value

        def request_pause(self) -> None:
            self.calls.append("pause")
            self.value = True

        def request_resume(self) -> None:
            self.calls.append("resume")
            self.value = False

    real_worker = w.worker
    stub = _StubWorker()
    w.worker = stub
    mp.set_running(True)        # 跑起来：暂停可用、开始灰着
    try:
        mp.set_paused(False)
        sc.check(mp.btn_start.text() == "开始" and not mp.btn_start.isEnabled()
                 and mp.btn_pause.isEnabled() and mp.btn_stop.isEnabled(),
                 "★ 跑起来时：开始灰着、暂停/停止可用",
                 f"开始={mp.btn_start.text()}({mp.btn_start.isEnabled()}) "
                 f"暂停={mp.btn_pause.isEnabled()}")
        mp.btn_pause.click()
        sc.check(stub.calls == ["pause"] and mp._paused,
                 "★ 点「暂停」：只下发暂停", str(stub.calls))
        sc.check(mp.btn_start.text() == "继续" and mp.btn_start.isEnabled()
                 and not mp.btn_pause.isEnabled(),
                 "★ 暂停后：「开始」变成可用的「继续」，而「暂停」灰掉（互斥）",
                 f"开始={mp.btn_start.text()}({mp.btn_start.isEnabled()}) "
                 f"暂停={mp.btn_pause.isEnabled()}")
        mp.btn_start.click()
        sc.check(stub.calls == ["pause", "resume"] and not mp._paused,
                 "★ 点「继续」：下发继续（开始按钮承担的）", str(stub.calls))
        sc.check(mp.btn_start.text() == "开始" and not mp.btn_start.isEnabled()
                 and mp.btn_pause.isEnabled(),
                 "★ 继续之后又回到「开始灰着、暂停可用」",
                 f"开始={mp.btn_start.text()}({mp.btn_start.isEnabled()})")
        # 已经在暂停状态时重复点暂停：不重复下发
        stub.value = True                      # 假装引擎那边已经暂停了
        mp.set_paused(True)
        mp.btn_pause.setEnabled(True)          # 强行点一下（正常情况下它是灰的）
        mp.btn_pause.click()
        sc.check(stub.calls == ["pause", "resume"],
                 "暂停状态下再点暂停不会重复下发", str(stub.calls))
        stub.value = False
        mp.set_paused(False)
        # 这批已经跑完（worker=None）时点「继续」：不能崩，界面要复位
        w.worker = None
        mp.set_paused(True)
        mp.btn_start.click()
        sc.check(not mp._paused and mp.btn_start.text() == "开始",
                 "★ 这批跑完后点「继续」：界面复位，不会卡在暂停",
                 f"paused={mp._paused} 开始={mp.btn_start.text()}")
    finally:
        w.worker = real_worker
        mp.set_running(False)
        mp.set_paused(False)
    sc.check(mp.btn_start.text() == "开始" and mp.btn_start.isEnabled()
             and not mp.btn_pause.isEnabled() and not mp.btn_stop.isEnabled(),
             "★ 没跑的时候：开始可用、暂停/停止灰着",
             f"开始={mp.btn_start.isEnabled()} 暂停={mp.btn_pause.isEnabled()}")

    # --- ★ 暂停时计时器要停住（用户明确要求）---
    mp.set_running(True)
    pump(app, 0.2)
    mp.set_paused(True)
    pump(app, 0.2)
    frozen = mp.eta.text()
    pump(app, 1.4)
    sc.check(mp.eta.text() == frozen and "已暂停" in frozen,
             "★ 暂停时计时器停住不走了（只是显示已暂停 · 用时）",
             f"{frozen!r} → {mp.eta.text()!r}")
    mp.set_paused(False)
    pump(app, 1.3)
    sc.check(mp.eta.text() != frozen and "已暂停" not in mp.eta.text(),
             "★ 继续之后计时接着走（暂停那段不算进去）", mp.eta.text())
    mp.set_running(False)
    pump(app, 0.2)

    # ★ 跑的过程中刷新进度**不能把计时器停掉**
    #   （踩过：`_refresh_overall` 里每更新一次进度就 stop_clock 一次，
    #     结果不管跑多久界面都显示"用时 0.0s"——截图里一眼看出来的）
    mp.set_running(True)
    pump(app, 1.3)
    before = mp.eta.text()
    w._refresh_overall(mp.tasks)          # 模拟"跑着的时候来了一次进度更新"
    pump(app, 0.2)
    sc.check(mp._clock.isActive() and mp.eta.text().startswith("用时")
             and mp.eta.text() != "用时 0.0s",
             "★ 跑动中刷新进度不会把计时器停掉/归零", f"{before!r} → {mp.eta.text()!r}")
    mp.set_running(False)
    pump(app, 0.2)

    # --- ★ 结算不跳页，结果写进日志（原来那张汇总页列宽手算，会压字）---
    sc.check(w.stack.currentWidget() is w.main_page, "★ 跑完留在工作台，不跳结算页")
    sc.check(not hasattr(w, "summary_page"), "★ 完成汇总页已删除")
    log_text = mp.log.toPlainText()
    sc.check("项：成功 3 · 失败 0 · 跳过 0" in log_text, "★ 日志里有结算汇总行",
             [ln for ln in log_text.splitlines() if "项：" in ln][-1:])
    sc.check(log_text.count("✔") >= 3, "★ 每个成功任务都有一行结果",
             f"✔ 出现 {log_text.count('✔')} 次")
    sc.check(all(t.name in log_text for t in done), "结果行里带上了文件名")
    # append_log 曾经是 (ts, who, msg, color) 四参，而调用处几乎都按
    # (标签, 正文, 颜色) 传 → 颜色名被当成正文写进行尾（"…用时 1.8s warn"）。
    sum_lines = [ln for ln in log_text.splitlines() if "汇总" in ln]
    leak = [ln for ln in log_text.splitlines()
            if ln.rstrip().endswith(("ok", "warn", "err", "info", "text_dim"))]
    sc.check(not leak, "★ 日志行尾不会漏出「颜色名」（参数错位那类 bug）", str(leak[:2]))
    sc.check(sum_lines and sum_lines[0].lstrip().startswith("汇总")
             and "项：成功" in sum_lines[0],
             "汇总行的第一格是标签、后面是正文", repr(sum_lines[0][:60]))

    # --- 输出目录确实有东西 ---
    out_dirs = [d for d in os.listdir(demo) if os.path.isdir(os.path.join(demo, d))]
    sc.check(len(out_dirs) >= 3, "真的解压出了目录", str(out_dirs))

    # --- ★「打开输出目录」：多来源时给出结论 + 菜单，且真的打开对应目录 ---
    from core.pipeline import ScanItem as _SI
    from core.pipeline import output_dirs, output_root

    mp_dirs = output_dirs(mp.tasks)
    sc.check(len(mp_dirs) == 3, "3 个任务 → 3 个产物目录", str(mp_dirs))
    sc.check(output_root(mp_dirs) == demo, "它们的公共上级 = 素材目录", str(output_root(mp_dirs)))
    sc.check(mp._open_root == demo and mp._open_dirs == mp_dirs,
             "★ 按钮指向公共上级（不是某个嵌套小目录）", mp._open_root)
    sc.check(mp.btn_open_out.isEnabled() and mp.btn_open_out.menu() is not None,
             "多个产物目录时按钮带菜单（让人自己挑）")
    acts = [a for a in mp.btn_open_out.menu().actions() if not a.isSeparator()]
    sc.check(len(acts) == 4 and acts[0].text().startswith("全部"),
             "菜单第一项是公共上级，后面逐条列出",
             str([a.text() for a in acts]))

    opened: list[str] = []
    orig_startfile = getattr(os, "startfile", None)
    os.startfile = lambda p: opened.append(p)      # type: ignore[attr-defined]
    try:
        acts[0].trigger()
        sc.check(opened[-1] == demo, "点第一项 → 打开公共上级", str(opened))
        acts[-1].trigger()
        sc.check(opened[-1] == mp_dirs[-1], "★ 点具体某一条 → 打开那个来源自己的目录",
                 f"{opened[-1]} vs {mp_dirs[-1]}")

        # 只有一个产物目录时应该是"点了就开"，不该再套一层菜单
        one = _SI(path="D:/x/单包.zip", name="单包.zip", kind="ZIP", status=ItemStatus.DONE)
        one.output_top = mp_dirs[0]
        mp.update_open_button([one])
        sc.check(mp.btn_open_out.menu() is None and mp._open_root == mp_dirs[0],
                 "只有一个产物目录 → 直接指向它（不套菜单）", mp._open_root)
        mp.btn_open_out.click()
        sc.check(opened[-1] == mp_dirs[0], "★ 只有一个目录时点按钮就直接打开", str(opened))

        # 多个来源（两个目录）→ 菜单里除了「全部」还要逐条列
        # （下标用 -2/-1：产物数量变了也不会 IndexError 把整轮测试带崩）
        two = []
        for p in (mp_dirs[-2], mp_dirs[-1]):
            it = _SI(path="D:/x/" + p, name=os.path.basename(p), kind="ZIP",
                     status=ItemStatus.DONE)
            it.output_top = p
            two.append(it)
        mp.update_open_button(two)
        acts2 = [a for a in mp.btn_open_out.menu().actions() if not a.isSeparator()]
        sc.check(len(acts2) == 3 and acts2[0].text().startswith("全部"),
                 "两个来源同属一个父目录 → 菜单仍有「全部」项",
                 str([a.text() for a in acts2]))
        acts2[-1].trigger()
        sc.check(opened[-1] == mp_dirs[-1], "★ 各自打开各自的目录")

        # 没产物（全失败）→ 灰掉，别让人点开上一批的产物
        bad = _SI(path="D:/x/坏.rar", name="坏.rar", kind="RAR", status=ItemStatus.FAILED)
        mp.update_open_button([bad])
        sc.check(not mp.btn_open_out.isEnabled() and mp._open_root == "",
                 "★ 这次没产出 → 按钮变灰", mp.btn_open_out.toolTip())
    finally:
        if orig_startfile is not None:
            os.startfile = orig_startfile          # type: ignore[attr-defined]
    mp.update_open_button(mp.tasks)

    # --- ★ 主界面右上角：全矢量图标（设置/密码本/最小化/最大化/关闭）---
    tops = [b.text() for b in mp.topbar.findChildren(QPushButton)]
    sc.check(all(t == "" for t in tops) and len(tops) == 5,
             "★ 顶栏按钮不再用 ⚙ ▢ ✕ 字符，全是矢量图标", str(tops))
    tips = {b.toolTip() for b in mp.topbar.findChildren(QPushButton)}
    sc.check({"设置", "密码本", "最小化", "关闭"} <= tips,
             "★ 纯图标按钮都有 tooltip（不然没人知道是啥）", str(sorted(tips)))
    sc.check(not mp.topbar.btn_settings.icon().isNull()
             and not mp.topbar.btn_close.icon().isNull()
             and not mp.topbar.btn_library.icon().isNull(),
             "图标真的画出来了（QIcon 非空）")
    sc.check(mp.topbar.btn_max.toolTip() in ("最大化", "最大化 / 还原", "还原")
             and not mp.topbar.btn_max.icon().isNull(), "最大化按钮保留（用户要的）",
             mp.topbar.btn_max.toolTip())
    # 标题栏要有应用图标 + 加大加粗的标题
    sc.check(mp.topbar.logo.pixmap() is not None and not mp.topbar.logo.pixmap().isNull(),
             "★ 标题栏左边就是用户给的 icon（以前界面里根本看不到）")
    # 版本号只有 core/appinfo.py 一处定义（打包时 exe 的版本资源也读它）
    import ui.app as app_mod

    from PySide6.QtWidgets import QLabel as _QLabel

    _texts = [lb.text() for lb in mp.topbar.findChildren(_QLabel)]
    sc.check(f"v{app_mod.APP_VERSION}" in _texts and app_mod.APP_NAME in _texts,
             "★ 顶栏标题/版本来自 core/appinfo.py（不再是手写的 v1.0）",
             f"{app_mod.APP_NAME} v{app_mod.APP_VERSION} / {_texts[:5]}")
    # 无边框：拖动/缩放靠 WM_NCHITTEST 交回给 Windows 处理
    sc.check(bool(w.windowFlags() & Qt.WindowType.FramelessWindowHint),
             "★ 窗口是无边框的（默认标题栏去掉了）")

    # --- ★ 操作行的响应式：任何宽度下都不许"文字被裁 / 按钮互相叠" ---
    # 用户报过：默认大小下「开始」和「暂停」挤在一起、右侧按钮还没折叠的宽度下
    # 「添加文件夹」显示不全。这里把"每个按钮宽度 ≥ 它文字的宽度"和"相邻按钮不重叠"
    # 直接钉死，扫一遍常用宽度。
    btns = [("开始", mp.btn_start), ("暂停", mp.btn_pause), ("停止", mp.btn_stop),
            ("添加文件", mp.btn_add), ("添加文件夹", mp.btn_add_dir),
            ("清除选中", mp.btn_clear_sel), ("清空列表", mp.btn_clear)]
    bad_widths: list[str] = []
    for width in (720, 760, 800, 860, 900, 1000, 1200, 1400):
        w.resize(width, 700)
        pump(app, 0.25)
        spans = []
        for name, b in btns:
            if not b.isVisible():
                continue
            if b.text() and b.width() < b.sizeHint().width():
                bad_widths.append(f"{width}:{name} {b.width()}<{b.sizeHint().width()}")
            spans.append((name, b.mapTo(mp, QPoint(0, 0)).x(), b.width()))
        for (n1, x1, w1), (n2, x2, _w2) in zip(spans, spans[1:]):
            if x1 + w1 > x2:
                bad_widths.append(f"{width}:{n1}×{n2} 重叠")
    sc.check(not bad_widths,
             "★ 720~1400 任何宽度下按钮文字都不被裁、也不会互相重叠",
             "; ".join(bad_widths[:6]) or "全部正常")
    w.resize(800, 900)
    pump(app, 0.3)
    sc.check(mp._compact_stage == 0 and mp.btn_add_dir.text() == "添加文件夹"
             and mp.btn_start.text() == "开始",
             "★ 默认 800 宽：一个都不折叠（开始/暂停/停止 + 清单操作都带文字）",
             f"stage={mp._compact_stage} 开始={mp.btn_start.text()!r} "
             f"添加文件夹={mp.btn_add_dir.text()!r}")
    dec = mp._row_decision
    sc.check(dec and dec["needs"][0] > dec["needs"][1] > dec["needs"][2] >= dec["needs"][3],
             "★ 档位需求是单调收窄的（全带文字 > 收清单操作 > 再收暂停停止 ≥ 全收）",
             str(dec and dec["needs"]))
    # 窄窗口下把暂停/停止也收成图标：**窗口最小值挡着，得先临时放开**
    # （真实用户拉不到这么窄，这里是把分档逻辑本身钉死；顺带验 tooltip 补上了）
    old_min_w = w.minimumWidth()
    try:
        w.setMinimumWidth(300)
        w.resize(440, 620)
        pump(app, 0.35)
        d2 = mp._row_decision
        sc.check(mp._compact_stage == 2 and mp.btn_add_dir.text() == ""
                 and mp.btn_pause.text() == "" and mp.btn_start.text() == "开始",
                 "★ 再窄一档：暂停/停止也收成图标（开始还留着文字）",
                 f"stage={mp._compact_stage} avail={d2.get('avail')} needs={d2.get('needs')}")
        sc.check(mp.btn_pause.toolTip() != "" and mp.btn_add_dir.toolTip() != "",
                 "★ 收成图标时补上 tooltip（否则没人知道是干嘛的）",
                 f"{mp.btn_pause.toolTip()!r}/{mp.btn_add_dir.toolTip()!r}")
        w.resize(360, 620)
        pump(app, 0.35)
        sc.check(mp._compact_stage == 3 and mp.btn_start.text() == "",
                 "★ 最窄一档：连「开始」也只剩图标 —— 但依然不重叠、有 tooltip",
                 f"stage={mp._compact_stage} avail={mp._row_decision.get('avail')}")
        sc.check(mp.btn_start.toolTip() == "开始解压", "开始按钮任何档位都留 tooltip",
                 mp.btn_start.toolTip())
    finally:
        w.setMinimumWidth(old_min_w)
        w.resize(800, 900)
        pump(app, 0.3)

    hdr_bad = []
    for width in (720, 800, 1000):
        w.resize(width, 700)
        pump(app, 0.3)
        mp.ensure_headers_fit()
        pump(app, 0.1)
        need = mp._header_need_widths()
        for i, (got, nd) in enumerate(zip(mp.col_widths(), need)):
            if got < nd:
                hdr_bad.append(f"{width}:第{i + 1}列 {got}<{nd}")
    sc.check(not hdr_bad, "★ 表头文字在常用宽度下都不会被裁（列宽自己会匀）",
             "; ".join(hdr_bad[:6]) or "全部放得下")
    w.resize(800, 900)
    pump(app, 0.3)
    sc.check(w._hit_test(w.mapToGlobal(QPoint(3, 3))) == w.HTTOPLEFT,
             "★ 左上角命中测试 → 缩放用（HTTOPLEFT）")
    sc.check(w._hit_test(w.mapToGlobal(QPoint(w.width() - 3, w.height() // 2))) == w.HTRIGHT,
             "右边缘 → HTRIGHT")
    bar = mp.topbar
    sc.check(w._hit_test(w.mapToGlobal(bar.mapTo(w, bar.rect().center()))) == w.HTCAPTION,
             "★ 顶栏空白处 → HTCAPTION（原生拖动 / 双击最大化 / 贴边分屏）")
    sc.check(w._hit_test(w.mapToGlobal(
                 bar.mapTo(w, bar.btn_settings.geometry().center()))) == w.HTCLIENT,
             "★ 顶栏上的按钮不算标题栏（否则点不到）")
    sc.check(w._hit_test(w.mapToGlobal(QPoint(w.width() // 2, w.height() // 2))) == w.HTCLIENT,
             "中间是客户区")
    # ★ 高 DPI：WM_NCHITTEST 给的是**物理像素**，Qt 几何是逻辑像素（本机 200% 缩放）
    #   不换算的话点窗口中心会被判成"右下角之外"，整个窗口点不动也拖不动（真机上踩过）
    sc.check(w.devicePixelRatioF() >= 1.0, "拿到设备像素比", str(w.devicePixelRatioF()))
    real_dpr = w.devicePixelRatioF
    try:
        w.devicePixelRatioF = lambda: 2.0            # type: ignore[assignment]
        logical = w.mapToGlobal(QPoint(w.width() // 2, w.height() // 2))
        sc.check(w._hit_test_msg(logical.x() * 2, logical.y() * 2) == w.HTCLIENT,
                 "★ 物理像素坐标要先除 DPR：点客户区中心仍是客户区", 
                 f"dpr=2 logical={logical}")
        bar_center = mp.topbar.mapTo(w, mp.topbar.rect().center())
        bar_global = w.mapToGlobal(bar_center)
        sc.check(w._hit_test_msg(bar_global.x() * 2, bar_global.y() * 2) == w.HTCAPTION,
                 "★ 物理像素下顶栏仍判成标题栏")
        sc.check(w._hit_test_msg(10 ** 6, 10 ** 6) == w.HTCLIENT,
                 "★ 坐标离谱（落在窗口外）时不瞎给「右下角」，否则点哪儿都在拖边框")
    finally:
        w.devicePixelRatioF = real_dpr              # type: ignore[assignment]

    # --- ★ 主题：以前只写进 config.json，界面纹丝不动 ---
    from ui.theme import DARK, LIGHT

    st = w.settings_page
    dark_qss = w.styleSheet()
    dark_stat = mp.stats["done"].styleSheet()
    log_before = mp.log.toPlainText()
    st.theme_radios[1].setChecked(True)          # 浅色
    st.sig_save.emit()
    pump(app, 0.3)
    sc.check(w.theme.mode == "light", "★ 点保存后主题真的切到浅色", w.theme.mode)
    sc.check(w.styleSheet() != dark_qss and LIGHT["bg"] in w.styleSheet(),
             "★ 全局 QSS 换成浅色", f"{len(dark_qss)} → {len(w.styleSheet())} 字符")
    sc.check(DARK["ok"] in dark_stat and LIGHT["ok"] in mp.stats["done"].styleSheet(),
             "★ 计数卡那种 inline 上色也跟着换（只换 QSS 是不够的）",
             f"{dark_stat} → {mp.stats['done'].styleSheet()}")
    sc.check(LIGHT["ok"] in w.settings_page.path_marks["7-Zip 路径"].styleSheet(),
             "★ 设置页「已找到」的标记色也跟着换",
             w.settings_page.path_marks["7-Zip 路径"].styleSheet())
    sc.check(len(mp.log.toPlainText().strip()) > 0
             and mp.log.toPlainText().count("\n") >= log_before.count("\n"),
             "换主题后日志没被清空（HTML 颜色是重渲染的）",
             f"{log_before.count(chr(10))} → {mp.log.toPlainText().count(chr(10))} 行")
    import json as _json

    saved_cfg = _json.load(open(os.path.join(BASE, "config.json"), encoding="utf-8"))
    sc.check(saved_cfg.get("theme") == "light", "主题落盘了", str(saved_cfg.get("theme")))

    # 跟随系统：存的是 system，实际用的是解析出来的那一套
    st.theme_radios[2].setChecked(True)
    st.sig_save.emit()
    pump(app, 0.2)
    sc.check(w.config.theme == "system" and w.theme.mode in ("dark", "light"),
             "★ 「跟随系统」存 system、实际落到具体的一套",
             f"{w.config.theme} → {w.theme.mode}")

    st.theme_radios[0].setChecked(True)          # 切回深色，别影响后面的用例
    st.sig_save.emit()
    pump(app, 0.2)
    sc.check(w.theme.mode == "dark" and DARK["bg"] in w.styleSheet(),
             "再切回深色也正常", w.theme.mode)

    # --- ★ 右下角计时器：所有状态都要显示得对（用户："放个计时器就好"）---
    mp.clear_tasks()
    sc.check(mp.eta.text() == "", "★ 没任务/清空列表时右下角不显示时间", repr(mp.eta.text()))
    mp.add_paths([os.path.join(demo, "嵌套.zip")])
    mp.wait_scan()
    pump(app, 0.2)
    mp.set_running(True)
    pump(app, 0.2)
    sc.check(mp.eta.text().startswith("用时") and mp._clock.isActive(),
             "★ 开始跑：显示「用时 Ns」并每秒刷新", f"{mp.eta.text()!r}")
    mp.set_paused(True)
    pump(app, 0.2)
    sc.check("已暂停" in mp.eta.text() and "用时" in mp.eta.text(),
             "★ 暂停时标明「已暂停」但时间还在（用户要知道停了多久）", mp.eta.text())
    mp.set_paused(False)
    pump(app, 0.2)
    sc.check("已暂停" not in mp.eta.text(), "★ 继续之后标记消失", mp.eta.text())
    mp.set_running(False)
    pump(app, 0.2)
    sc.check(mp.eta.text().startswith("总用时") and not mp._clock.isActive(),
             "★ 跑完：换成「总用时」且不再跳", mp.eta.text())
    mp.set_running(True)          # 自动接着跑下一批：不该归零
    pump(app, 0.1)
    mp.set_running(False)
    pump(app, 0.1)
    sc.check(mp.eta.text().startswith("总用时"), "再跑一批也还是「总用时」", mp.eta.text())
    mp.clear_tasks()
    sc.check(mp.eta.text() == "", "清空列表后计时器归零（不留上次的时间）", repr(mp.eta.text()))

    # --- ★ 日志面板的内容要落到 logs/run.log（用户的预期：文件记下窗口显示的东西）---
    import ui.app as _app_for_log

    marker = "只此一行-用于验证落盘-12345"
    mp.append_log("测试", f"D:\\下载\\UC\\{marker}.zip", "info")
    pump(app, 0.2)
    run_log = _app_for_log.paths_mod.run_log_path()
    sc.check(os.path.isfile(run_log), "★ 日志面板的内容写进了 logs/run.log", run_log)
    body = open(run_log, encoding="utf-8", errors="replace").read() if os.path.isfile(run_log) else ""
    sc.check(marker in body and "D:\\下载\\UC" in body,
             "★ run.log 里是全路径（排错要按路径定位）", body[-120:])
    mp.append_log("剪贴板", "已复制密码：SuperSecret", "ok")
    pump(app, 0.1)
    body = open(run_log, encoding="utf-8", errors="replace").read()
    sc.check("SuperSecret" not in body and "已复制密码：***" in body,
             "★ 密码不会写进日志文件（打码）", body[-80:])
    sc.check(_app_for_log.paths_mod.log_path().endswith("ui.log")
             and run_log.endswith("run.log"),
             "两个日志各司其职：ui.log（启动/异常）+ run.log（面板流水）",
             f"{_app_for_log.paths_mod.log_path()} / {run_log}")

    # --- ★ 密码列：只显示密码，点一下就进剪贴板（详情抽屉已删） ---
    from PySide6.QtWidgets import QApplication as _QApp

    # 先探一下剪贴板能不能用：某些会话（远程/受控桌面/没有窗口站）里
    # QClipboard 写进去读不回来，这时那两条断言只能 SKIP——否则就是**假 FAIL**，
    # 会让人以为"点密码格复制"坏了（实测本机某个时段就是这样；offscreen 反倒能用）。
    clip_ok = True
    try:
        _QApp.clipboard().setText("bbu-clip-probe")
        clip_ok = _QApp.clipboard().text() == "bbu-clip-probe"
    except Exception:                            # noqa: BLE001
        clip_ok = False
    if not clip_ok:
        print("  [SKIP] 这个会话的剪贴板不可用（写进去读不回来），"
              "「点密码格=复制」这两条跳过（真机上是好的）")

    mp.clear_tasks()
    mp.add_paths([os.path.join(demo, n) for n in os.listdir(demo)])
    mp.wait_scan()
    mp.table.cellClicked.emit(0, 2)              # 还没跑，密码是空的 → 不该写剪贴板
    _QApp.clipboard().setText("哨兵")
    mp.table.cellClicked.emit(0, 2)
    if clip_ok:
        sc.check(_QApp.clipboard().text() == "哨兵", "没密码的行点了不写剪贴板")
    mp.tasks[0].password = "pw-123"
    mp.tasks[0].status = ItemStatus.DONE
    mp._fill_table()
    cell = mp.table.item(0, 2)
    sc.check(cell.text() == "pw-123" and "来源" not in cell.text(),
             "★ 密码列只写密码，不再缀「密码来源」", repr(cell.text()))
    # 离屏平台的剪贴板时好时坏：**点击之前**先探一次，写不进去就跳过，
    # 别把它当成产品 bug 报红（真机上这条一直是好的）。
    # （踩过：把探测放在点击之后，探针把刚复制进去的值盖掉了 → 假红）
    _QApp.clipboard().setText("bbu-clip-probe2")
    clip_ok = clip_ok and _QApp.clipboard().text() == "bbu-clip-probe2"
    if not clip_ok:
        print("  [SKIP] 剪贴板这时候写不进去，「点密码格=复制」跳过")
    mp.table.cellClicked.emit(0, 2)
    if clip_ok:
        sc.check(_QApp.clipboard().text() == "pw-123", "★ 点密码格 = 复制",
                 _QApp.clipboard().text())
    sc.check(mp.table.horizontalHeaderItem(2).text() == "密码（点击复制）",
             "★ 列名带一句「点击复制」的引导（光靠主色不够）",
             mp.table.horizontalHeaderItem(2).text())
    sc.check(not hasattr(w, "drawer"), "★ 详情抽屉已删除")

    # 跑完后的原地刷新也要只写密码（以前这里是 `密码 + 来源` 拼接）
    mp.tasks[0].source = "手动输入"
    mp.update_item(mp.tasks[0])
    sc.check(mp.table.item(0, 2).text() == "pw-123",
             "★ 运行中/跑完原地刷新也不带来源",
             repr(mp.table.item(0, 2).text()))
    sc.check(mp.table.item(0, 2).toolTip() == "点击复制到剪贴板", "密码格悬停有提示")

    # --- ★ 密码弹窗：只让用户输密码；两个按钮（验证 / 跳过当前文件） ---
    from core.vault import PasswordCandidate, Origin, UnlockResult
    from ui.app import PasswordDialog
    from ui.worker import AskRequest

    un = UnlockResult(
        ok=False,
        tried=[PasswordCandidate("abc123", Origin.BOOK, "成功过 2 次"),
               PasswordCandidate("", Origin.EMPTY, "")],
        stopped_reason="没有更多候选",
    )
    req = AskRequest(archive="D:/x/示例包.zip", unlock=un)
    sc.check(len(req.unlock.tried) == 2, "AskRequest 带上试过的候选（日志里报来源）")

    dlg = PasswordDialog(w.theme, "示例包.zip", verifier=lambda pw: pw == "ok")
    labels = [lb.text() for lb in dlg.findChildren(QLabel)]
    sc.check(not any(("试过" in t or "密码本" in t or "原因" in t or "候选" in t)
                     for t in labels),
             "★ 弹窗里不再铺说明文字（只留「这是个什么包」和输入框）", str(labels))
    # 只数"有文字"的按钮：标题条上那个 ✕ 是纯图标
    btns = sorted(b.text() for b in dlg.findChildren(QPushButton) if b.text())
    sc.check(btns == ["跳过当前文件", "验证"],
             "★ 只留两个按钮：验证 / 跳过当前文件（「继续解压」已删）", str(btns))

    # ★ 弹窗也去掉系统标题栏（跟主窗口一致）：无边框 + 自绘标题条 + ✕ + 可拖
    sc.check(bool(dlg.windowFlags() & Qt.WindowType.FramelessWindowHint),
             "★ 密码弹窗也是无边框的（系统标题栏去掉了）")
    dlg.show()
    pump(app, 0.2)
    sc.check(dlg.btn_close is not None and dlg.btn_close.text() == ""
             and dlg.btn_close.toolTip() == "关闭",
             "★ 自带一个纯图标 ✕（无边框之后总得能关）",
             f"{dlg.btn_close.text()!r}/{dlg.btn_close.toolTip()!r}")
    sc.check(dlg._in_drag_zone(QPoint(20, 8)) and not dlg._in_drag_zone(QPoint(20, dlg.height() - 8)),
             "★ 标题条能拖动、正文不能（只认标题那一条）")
    sc.check(dlg._in_drag_zone(QPoint(dlg.btn_close.x() + 2, 8)),
             "✕ 所在位置也算拖区，但按下时会被按钮自己吃掉（不冲突）")

    dlg._verify()
    sc.check(not dlg.verified and "输入" in dlg.hint.text(), "空密码不给过", dlg.hint.text())

    # ★ 回车必须落在「验证」上（用户报过：焦点不在输入框时回车触发的是"跳过"）
    sc.check(dlg.btn_test.isDefault() and not dlg.btn_skip.autoDefault()
             and not dlg.btn_close.autoDefault(),
             "★ 回车 = 验证（默认按钮是「验证」，「跳过/✕」明确不抢回车）",
             f"default={dlg.btn_test.isDefault()} skip_auto={dlg.btn_skip.autoDefault()} "
             f"close_auto={dlg.btn_close.autoDefault()}")
    dlg.edit.setText("nope")
    dlg._verify()
    sc.check("验证中" in dlg.hint.text() and not dlg.btn_test.isEnabled(),
             "★ 点了验证之后立刻返回（不再冻住界面），按钮先禁用",
             dlg.hint.text())
    sc.check(wait_cond(app, lambda: dlg._job is None, 10.0), "验证在后台线程里跑完")
    sc.check(not dlg.verified and dlg.accepted_password is None,
             "★ 验证没通过：什么都不做（不写密码、不跳过）",
             f"verified={dlg.verified} pw={dlg.accepted_password} hint={dlg.hint.text()}")
    sc.check("不对" in dlg.hint.text(), "并给出「密码不对」的反馈", dlg.hint.text())
    dlg.edit.setText("ok")
    dlg._verify()
    sc.check(wait_cond(app, lambda: dlg._job is None and dlg.result() != 0, 10.0),
             "验证通过后自动关窗")
    sc.check(dlg.verified and dlg.accepted_password == "ok" and dlg.result() == QDialog.DialogCode.Accepted,
             "★ 验证通过：直接继续解压（不用再点第二个按钮）",
             f"verified={dlg.verified} pw={dlg.accepted_password} code={dlg.result()}")
    sc.check(not any(hasattr(dlg, a) for a in ("cb_common", "cb_temp")),
             "弹窗里没有残留的「记住到哪本」复选框引用")
    dlg.deleteLater()

    # --- ★ 慢验证：真机上试一个 4GB 的包要好几秒，那几秒里界面必须还能转 ---
    # 用户原话是「输入密码后有一个界面无响应的过程」——所以这里用一个真的会
    # 睡 1.5s 的验证器，量两件事：① `_verify()` 本身立刻返回；② 验证期间
    # 主线程还能跑事件循环（能跑 = 用户拖动/点别的按钮不会被冻住）
    def _slow_verify(pw: str) -> bool:
        time.sleep(1.5)
        return pw == "ok"

    slow = PasswordDialog(w.theme, "样例视频.mp4", verifier=_slow_verify)
    slow.edit.setText("ok")
    t0 = time.monotonic()
    slow._verify()
    cost = time.monotonic() - t0
    sc.check(cost < 0.3, "★ 慢验证时 _verify() 也立刻返回（不冻界面）", f"{cost:.2f}s")
    ticks = 0
    while slow._job is not None and time.monotonic() - t0 < 8.0:
        app.processEvents()
        ticks += 1
        time.sleep(0.01)
    sc.check(ticks >= 30, "★ 验证那几秒里主线程照样在转（界面没被冻住）",
             f"事件循环转了 {ticks} 次")
    sc.check(slow.verified, "慢验证通过后结果照样生效")
    slow.deleteLater()

    dlg2 = PasswordDialog(w.theme, "示例包.zip", verifier=lambda pw: True)
    dlg2.btn_skip.click()
    sc.check(dlg2.accepted_password is None and dlg2.result() == QDialog.DialogCode.Rejected,
             "★ 跳过当前文件 = 这个包不弄了、继续下一个")
    dlg2.deleteLater()

    # --- ★ 密码本：删除要弹窗确认，空密码那一行也得删得掉；
    #     顺带多选批量删除 ---
    # 这一段**必须用一次性的密码本**：用的是真 Workbench，直接改它的 vault
    # 就会把 ui-base/密码本.txt 写坏（踩过：里面变成 p1..p5，下一轮
    # 示例包.zip 找不到密码 → 整批用例连锁失败）
    from core.vault import BookEntry, PasswordVault

    lp = w.library_page
    orig_vault = lp.vault
    tmp_book = os.path.join(ROOT, "tests", "work", "lp-book", "密码本.txt")
    shutil.rmtree(os.path.dirname(tmp_book), ignore_errors=True)
    os.makedirs(os.path.dirname(tmp_book))
    tmp_vault = PasswordVault(book=tmp_book)
    tmp_vault.entries.append(BookEntry("", 3))         # 造一条空密码
    tmp_vault.entries.append(BookEntry("p1", 1))
    tmp_vault.entries.append(BookEntry("p2", 0))
    lp.vault = tmp_vault
    lp._reload_rows()

    row = next(r for r in range(lp.table.rowCount())
               if lp.table.item(r, 0).text() == "（空密码）")
    lp.table.selectRow(row)
    orig_question = QMessageBox.question
    state: dict[str, bool] = {}
    try:
        QMessageBox.question = staticmethod(lambda *a, **k: QMessageBox.StandardButton.No)
        lp._remove_selected()
        state["取消后还在"] = any(e.password == "" for e in lp.vault.entries)
        QMessageBox.question = staticmethod(lambda *a, **k: QMessageBox.StandardButton.Yes)
        lp.table.selectRow(row)
        lp._remove_selected()
        state["确认后已删"] = not any(e.password == "" for e in lp.vault.entries)
    finally:
        QMessageBox.question = orig_question
    sc.check(all(state.values()), "★ 删密码先弹窗确认；空密码那一行也能删掉", str(state))
    sc.check("已删除" in lp.summary_label.text(),
             "★ 删完立刻就能看到「已删除…」（以前被 reload 刷掉了）",
             lp.summary_label.text())

    # --- ★ 密码本：多选批量删除 ---
    tmp_vault.set_entries(["p1", "p2", "p3", "p4", "p5"])
    tmp_vault.save()
    lp._reload_rows()
    sc.check(lp.table.selectionMode() == QAbstractItemView.SelectionMode.ExtendedSelection,
             "★ 密码本表格是多选模式（Ctrl / Shift / 框选）")
    lp.table.clearSelection()
    lp.table.setRangeSelected(QTableWidgetSelectionRange(0, 0, 2, 1), True)   # 前 3 行
    picked = lp._selected_passwords()
    sc.check(len(picked) == 3, "选中 3 行 → 能读出 3 个密码值", str(picked))
    state2: dict[str, object] = {}
    try:
        QMessageBox.question = staticmethod(lambda *a, **k: QMessageBox.StandardButton.No)
        lp._remove_selected()
        state2["取消不动"] = len(lp.vault.entries) == 5
        QMessageBox.question = staticmethod(lambda *a, **k: QMessageBox.StandardButton.Yes)
        lp._remove_selected()
        state2["一次删三条"] = [e.password for e in lp.vault.entries] == ["p4", "p5"]
        reread = PasswordVault(book=tmp_book)
        reread.reload()
        state2["落盘了"] = [e.password for e in reread.entries] == ["p4", "p5"]
    finally:
        QMessageBox.question = orig_question
    sc.check(all(state2.values()), "★ 多选批量删除：确认一次删掉一批，取消则不动",
             str(state2))
    sc.check("已删除 3 条" in lp.summary_label.text(), "并报出删了几条",
             lp.summary_label.text())
    sc.check("多选" in lp.btn_del.toolTip(), "「删除选中」提到了可以多选", lp.btn_del.toolTip())

    # 换回真密码本（后面的用例、以及退出时的状态都该是它）
    lp.set_vault(orig_vault)
    sc.check(all(e.password != "p1" for e in lp.vault.entries),
             "★ 测试用的是临时密码本，没污染 ui-base 的那本",
             lp.vault.book_path)

    # --- ★ 表格列宽：能拖、能双击自适应、总宽恒定、不出横向滚动条 ---
    hh = mp.table.horizontalHeader()
    modes = [hh.sectionResizeMode(i) for i in range(mp.table.columnCount())]
    sc.check(all(m == QHeaderView.ResizeMode.Interactive for m in modes),
             "★ 每一列都是 Interactive（能拖边界、双击按内容自适应）", str(modes))
    sc.check(not hh.stretchLastSection(),
             "★ 不再用 stretchLastSection（它只会把最后一列压到最小，然后甩出一根横向滚动条）")
    sc.check(mp.table.horizontalScrollBarPolicy() == Qt.ScrollBarPolicy.ScrollBarAlwaysOff,
             "★ 横向滚动条直接关掉（列宽由一个不变量保证，不需要它）")
    avail = mp._available_width()
    sc.check(avail >= 4 * mp.MIN_COL, "窗口真的布局出来了（可视宽度够摆 4 列）",
             f"可视宽={avail}")
    sc.check(sum(mp.col_widths()) == avail,
             "★ 初始总宽 = 可视宽度（右边不留空、也不溢出）",
             f"{mp.col_widths()} vs {avail}")

    # 把第一列拖宽：右边的列必须自己让位，最后一列不能被挤出可视区
    # （注意：能拖到多宽受"总宽 = 可视宽"这个不变量限制——其它列最多缩到 MIN_COL，
    #   所以窄窗口下第一列拿不到 700，只能拿到 avail - 3*MIN_COL。按实际能力断言。）
    want = min(700, avail - 3 * mp.MIN_COL)
    mp.table.setColumnWidth(0, 700)
    sc.check(mp.col_widths()[0] == want, "拖到哪儿就是哪儿（受总宽不变量限制）",
             f"{mp.col_widths()} 期望第一列 {want}")
    sc.check(sum(mp.col_widths()) == avail,
             "★ 拖宽第一列后总宽依旧 = 可视宽度（不用拖进度条）",
             f"{mp.col_widths()} vs {avail}")
    sc.check(mp.col_widths()[3] >= mp.MIN_COL,
             "★ 最后一列没被挤出去（拖前面的列，后面自己缩到最小为止）",
             str(mp.col_widths()))

    # 拖到远超可视宽度：夹住，而不是溢出
    mp.table.setColumnWidth(0, 5000)
    sc.check(sum(mp.col_widths()) <= avail,
             "★ 拖过头会被夹住，总宽不会超过可视宽度", f"{mp.col_widths()} vs {avail}")

    # 收窄第一列：多出来的宽度分给别的列
    mp.table.setColumnWidth(0, 120)
    sc.check(sum(mp.col_widths()) == avail, "收窄后总宽还是 = 可视宽度", str(mp.col_widths()))

    mp.apply_col_widths(["坏值", 5, 99999])
    sc.check(mp.col_widths()[1] >= 56 and mp.col_widths()[2] <= 1600,
             "★ 脏/越界宽度会被夹到合理区间（不会把列压没或拉到天边）",
             str(mp.col_widths()))
    mp.fit_col_widths()
    sc.check(all(w > 0 for w in mp.col_widths()), "「按内容自适应」不会把列缩成 0",
             str(mp.col_widths()))
    sc.check(sum(mp.col_widths()) == avail, "「按内容自适应」之后总宽也 = 可视宽度",
             str(mp.col_widths()))
    mp.reset_col_widths()
    sc.check(sum(mp.col_widths()) == avail and all(w >= mp.MIN_COL for w in mp.col_widths()),
             "恢复默认列宽后总宽 = 可视宽度、没有列被压到最小以下", str(mp.col_widths()))
    # 余量按比例摊给每一列（不是一股脑塞最后一列）：等宽起步的话前三列该一样宽
    mp.apply_col_widths([100, 100, 100, 100])
    sc.check(sum(mp.col_widths()) == avail, "余量摊完后总宽还是 = 可视宽度",
             str(mp.col_widths()))
    sc.check(len(set(mp.col_widths()[:3])) == 1,
             "★ 余量是按比例摊的（不会全塞给最后一列）", str(mp.col_widths()))
    # 窗口来回缩放不该让宽度往左边的列上跑（比例要守住）
    before = mp.col_widths()
    for width in (900, 1340, 1000, 1340):
        w.resize(width, 900)
        pump(app, 0.1)
    after = mp.col_widths()
    ratios_before = [b / sum(before) for b in before]
    ratios_after = [a / sum(after) for a in after]
    sc.check(max(abs(a - b) for a, b in zip(ratios_before, ratios_after)) < 0.1,
             "★ 窗口拉大缩小几轮后，列宽比例没跑偏（不会越来越偏左）",
             f"{before} → {after}")
    menu = mp.header_menu(QPoint(30, 10))
    texts = [a.text() for a in menu.actions() if not a.isSeparator()]
    sc.check(any("按内容自适应" in t for t in texts) and any("默认" in t for t in texts),
             "表头右键菜单有「自适应 / 恢复默认」", str(texts))
    menu.deleteLater()
    # 纵向分割线：QSS 里 item 和表头都要有 border-right
    qss = w.theme.qss()
    sc.check(qss.count("border-right: 1px solid") >= 2,
             "★ 表格有纵向分割线（表体 + 表头各一条）",
             str(qss.count("border-right: 1px solid")))
    # 列宽记忆：拖完 0.7 秒落盘，重新构造窗口能读回来
    mp.table.setColumnWidth(0, 333)
    w._save_col_widths()
    cfg_on_disk = json.load(open(os.path.join(BASE, "config.json"), encoding="utf-8"))
    sc.check(cfg_on_disk.get("table_cols", [])[:1] == [333],
             "★ 列宽写进了 config.json", str(cfg_on_disk.get("table_cols")))
    w2 = Workbench(base_dir=BASE)
    got = w2.main_page.col_widths()
    want = list(w2.config.table_cols)
    same_ratio = all(abs(a / sum(got) - b / sum(want)) < 0.03 for a, b in zip(got, want))
    sc.check(want[:1] == [333] and same_ratio,
             "★ 重开窗口后列宽还是拖过的样子（窗口大小不同就按比例缩放）",
             f"{got} vs {want}")
    w2.deleteLater()
    # 收尾：把列宽恢复成默认再存一次，免得后面截图/别的用例看到拖出来的怪比例
    mp.reset_col_widths()
    w._save_col_widths()

    # --- ★ 右键菜单：设置页有入口，装/卸不崩，状态如实显示 ---
    import ui.app as app_mod

    sc.check(not hasattr(st, "cb_shell_auto"),
             "★ 「右键后自动开始解压」那个开关撤掉了（菜单只有一种模式）")
    # 这台机器上可能真的装着（验证脚本装的），所以先显式摆成"未装"再断言外观
    st.set_shell_status(False)
    sc.check(st.btn_shell_on.text() == "安装到右键菜单", "没装时按钮是「安装到右键菜单」",
             st.btn_shell_on.text())
    st.set_shell_status(True, '"py" "run.py" "%1"')
    sc.check("已注册" in st.shell_mark.text() and st.btn_shell_off.isEnabled()
             and st.btn_shell_on.text() == "重新安装",
             "★ 装过之后按钮变成「重新安装」、移除可用", st.shell_mark.text())
    st.set_shell_status(False)
    sc.check("未注册" in st.shell_mark.text() and not st.btn_shell_off.isEnabled(),
             "没装时移除是灰的", st.shell_mark.text())
    w._refresh_shell_status()
    sc.check(st.shell_mark.text() in ("✔ 已注册", "未注册", "⚠ 路径已失效"),
             "读真实注册表状态不崩", st.shell_mark.text())

    # --- ★ 数据放在哪：界面只留两个入口按钮，位置信息挂 tooltip（用户要求删掉说明文字）---
    st.refresh_paths()
    shown = st.lbl_paths.text()
    sc.check(shown.strip() != "" and app_mod.paths_mod.data_dir() in shown,
             "数据目录信息仍在（隐藏标签里，供 tooltip/断言用）",
             shown.replace("\n", " | ")[:80])
    sc.check(not st.lbl_paths.isVisible(),
             "★ 设置页不再铺「你的数据放在哪」那段说明文字")
    sc.check(st.btn_open_data.text() == "打开数据目录"
             and st.btn_open_log.text() == "打开日志",
             "★ 只留「打开数据目录 / 打开日志」两个按钮（打包后没控制台，这是排错出口）",
             f"{st.btn_open_data.text()} / {st.btn_open_log.text()}")
    sc.check(app_mod.paths_mod.data_dir() in st.btn_open_data.toolTip(),
             "★ 数据目录位置挂在「打开数据目录」的 tooltip 上（想看还是看得到）",
             st.btn_open_data.toolTip().replace("\n", " | ")[:60])
    sc.check(st.btn_open_log.toolTip().endswith("ui.log"),
             "「打开日志」的 tooltip 指向日志文件", st.btn_open_log.toolTip())
    # 失效判断只看命令里的路径还在不在（base_dir 换成临时目录不该误报）
    pw_path, _script_path = w._shell_command()
    sc.check(not w._shell_stale(f'"{pw_path}" "{os.path.join(ROOT, "run.py")}" "%1"'),
             "★ 命令里的路径都在 → 不算失效（base_dir 不同也不误报）")
    sc.check(w._shell_stale('"D:/不存在的解释器/pythonw.exe" "D:/x/run.py" "%1"'),
             "★ 注册表里的路径没了 → 提醒重新安装")

    pythonw, script = w._shell_command()
    sc.check(pythonw.lower().endswith(("pythonw.exe", "python.exe")) and script.endswith("run.py"),
             "要写进注册表的命令指向本工程的 run.py", f"{pythonw} / {script}")

    # 安装失败（权限/策略）时必须留日志，不能静默失败——pythonw 下看不见栈
    orig_install = app_mod.shellmenu.install
    calls: list[dict] = []
    try:
        def boom(*a, **k):
            raise PermissionError("测试模拟：注册表不让写")

        app_mod.shellmenu.install = boom
        w._on_shell_install()
        failed_line = mp.log.toPlainText().splitlines()[-1]
        sc.check("写入注册表失败" in failed_line, "★ 装不上时日志里说清楚（不静默）",
                 failed_line)
        app_mod.shellmenu.install = lambda *a, **k: calls.append(k) or ["k1", "k2", "k3"]
        w._on_shell_install()
        ok_line = mp.log.toPlainText().splitlines()[-1]
        sc.check("已注册 3 处" in ok_line and calls and "auto" not in calls[0],
                 "★ 装成功会报几处、且不再传「自动开始」", ok_line)
        sc.check("不自动开始" in ok_line, "★ 日志里说明「只加进待处理列表」", ok_line)
    finally:
        app_mod.shellmenu.install = orig_install

    # --- ★ 命令行 / 右键菜单进来的路径 ---
    paths, auto = app_mod.parse_launch_args(["run.py", "--auto", "D:/a.zip"])
    sc.check(paths == ["D:/a.zip"] and auto is True, "解析 --auto 参数", f"{paths} {auto}")
    paths2, auto2 = app_mod.parse_launch_args(
        ["run.py", "-style", "Fusion", "D:/a b.zip", "D:/c.zip"])
    sc.check(paths2 == ["D:/a b.zip", "D:/c.zip"] and auto2 is False,
             "★ Qt 自己的参数不会被当成文件路径", f"{paths2} {auto2}")

    mp.clear_tasks()
    real_file = os.path.join(demo, "教程视频.mp4")
    payload = json.dumps({"paths": [real_file], "auto": False}).encode("utf-8")
    w.queue_launch_from_payload(payload)
    # 转发进来的路径会先攒 220ms 再作为一批提交（多选分卷要合并成一批才认得出来），
    # 所以断言前得让那个定时器走到
    pump(app, 0.5)
    mp.wait_scan()
    sc.check(len(mp.tasks) == 1,
             "★ 收到别的进程甩过来的路径 → 挂进清单", f"{len(mp.tasks)} 项")
    sc.check(not w.queue_launch_from_payload(b"{not json at all"), "坏数据不崩")
    sc.check(not w.queue_launch_from_payload(json.dumps({"paths": []}).encode()),
             "空路径列表返回 False")

    started: list[bool] = []
    orig_start = w._on_start
    try:
        w._on_start = lambda: started.append(True)      # type: ignore[assignment]
        w.queue_launch([real_file], auto=False)
        pump(app, 0.5)
        mp.wait_scan()
        pump(app, 0.3)
        sc.check(not started, "auto=False 只挂清单、不自己开跑")
        w.queue_launch([real_file], auto=True)
        pump(app, 0.6)
        mp.wait_scan()
        pump(app, 0.6)
        sc.check(started == [True], "★ auto=True 会自己开始解压（右键菜单就是这条）")
        sc.check(len(mp.tasks) >= 1, "路径确实进了清单")
    finally:
        w._on_start = orig_start                              # type: ignore[assignment]
    sc.check(w.server is None or w.server.isListening(),
             "单实例 socket 要么在监听、要么明确没起来（起不来只是退化，不影响功能）",
             f"server={w.server!r}")
    mp.clear_tasks()

    # --- ★ 扫描还没完就点「开始」：不许只开一部分，等扫完自己开 ---
    # 用户报过"这个跑完之后没接着处理后面的，变成还要手点一下开始"：成因就是
    # 点开始那一刻清单里只有先扫完的那几项，后面扫完的只能排队。
    late2 = os.path.join(ROOT, "tests", "work", "late-2")
    shutil.rmtree(late2, ignore_errors=True)
    os.makedirs(late2, exist_ok=True)
    late_c = os.path.join(late2, "排队A.zip")
    late_d = os.path.join(late2, "排队B.zip")
    shutil.copyfile(os.path.join(demo, "嵌套.zip"), late_c)
    shutil.copyfile(os.path.join(demo, "示例包.zip"), late_d)

    started2: list[bool] = []
    mp.clear_tasks()
    mp.add_paths([late_c, late_d])
    sc.check(mp._scan_job is not None, "扫描确实在跑（这时清单里还没有它们）")
    # 注意：扫描期间「开始」按钮本身是灰的（清单还是空的），所以这里直接调
    # `_on_start()`——右键带 --auto 那条路就是这么进来的（_maybe_auto_start）。
    # **不能**把 `_on_start` 打桩掉：要验的正是它自己的判断。
    w._on_start()
    # 立刻检查（不 pump）：这两个夹具几百字节，扫描几十毫秒就完了，pump 一下
    # 就已经"扫完并自动开跑"了，那反而看不出它有没有先等一等
    sc.check(w.worker is None and w._auto_pending,
             "★ 扫描没完就不开这一批（先记下来，等扫完再开）",
             f"worker={w.worker!r} pending={w._auto_pending}")
    mp.wait_scan()
    pump(app, 1.5)
    # 这两个夹具解起来只要零点几秒，可能"起来又跑完了"，所以看**结果**而不是看 worker
    sc.check(w.worker is not None
             or any(t.status is not ItemStatus.QUEUED for t in mp.tasks),
             "★ 扫完自动开跑（不用再点一次开始）",
             str([t.status.value for t in mp.tasks]))
    wait_job(app, w)
    mp.clear_tasks()

    # --- ★ 一批跑完，队列里还有没轮到的 → 自动接着跑 ---
    mp.clear_tasks()
    mp.add_paths([late_c, late_d])
    mp.wait_scan()
    for t in mp.tasks:
        t.status = ItemStatus.QUEUED
    calls: list[bool] = []
    orig_start3 = w._on_start
    try:
        w._on_start = lambda: calls.append(True)              # type: ignore[assignment]
        sc.check(w._auto_continue_queue() is True and calls == [True],
                 "★ 队列里还有没轮到的 → 自动接着跑（不必再点一次开始）")
        # 失败的不自动重试：否则一个解不开的包会变成死循环
        for t in mp.tasks:
            t.status = ItemStatus.FAILED
        calls.clear()
        sc.check(w._auto_continue_queue() is False and not calls,
                 "★ 失败的不自动重试（重试是用户的决定）")
        # 点过「停止」之后不许自动接着跑，否则等于停不下来
        for t in mp.tasks:
            t.status = ItemStatus.QUEUED
        w._stop_requested = True
        calls.clear()
        sc.check(w._auto_continue_queue() is False and not calls,
                 "★ 点过停止之后不再自动接着跑")
        w._stop_requested = False
    finally:
        w._on_start = orig_start3                             # type: ignore[assignment]
    mp.clear_tasks()

    # --- ★ 快路径转交：纯 ctypes 写命名管道（不 import Qt 那条路）---
    from PySide6.QtNetwork import QLocalServer as _QLS

    from core import single as _single

    probe_server = _QLS()
    probe_server.removeServer("bbu-test-pipe")
    ok_listen = probe_server.listen("bbu-test-pipe")
    if not ok_listen:
        print("  [SKIP] 这个环境不让开命名管道，快路径转交只能真机上验（tools/verify_shellmenu.py）")
    else:
        got = bytearray()
        socks: list = []

        def _grab() -> None:
            # 不能连上就 readAll：那一刻数据可能还没到（客户端是先连、后写），
            # 读到空 buffer 就会把路径当成"没收到"。要挂 readyRead，收工前再排空一次。
            s = probe_server.nextPendingConnection()
            socks.append(s)
            s.readyRead.connect(lambda s=s: got.extend(bytes(s.readAll())))

        probe_server.newConnection.connect(_grab)
        old_name = _single.PIPE_NAME
        _single.PIPE_NAME = "bbu-test-pipe"
        try:
            # 这里的服务端和客户端在同一个进程、同一个线程里：`WriteFile` 要等对端
            # 把数据读走才返回，而对端要跑事件循环才会去读 → 所以转发必须放到工作
            # 线程里，主线程同时把事件循环转起来（真机上服务端是另一个进程，不需要这样）
            box: list[bool] = []
            th = threading.Thread(
                target=lambda: box.append(
                    _single.forward([r"D:\a.zip", r"D:\b c.zip"], auto=True, timeout=2.0)))
            th.start()
            end = time.monotonic() + 4.0
            while th.is_alive() and time.monotonic() < end:
                pump(app, 0.05)
            th.join(1.0)
            sent = bool(box and box[0])
            pump(app, 0.2)
            for s in socks:                       # 收工前把管道里剩下的读干净
                got.extend(bytes(s.readAll()))
        finally:
            _single.PIPE_NAME = old_name
            for s in socks:                       # 连接不关掉的话，管道实例还在，
                s.close()                         # 下面那条"没有窗口在跑"就测不准了
            probe_server.close()
        payload = json.loads(bytes(got).decode("utf-8")) if got else {}
        sc.check(sent and payload.get("paths") == [r"D:\a.zip", r"D:\b c.zip"],
                 "★ 快路径把路径写进命名管道（右键那条路不再需要 Qt）", str(payload))
        sc.check(payload.get("auto") is True, "auto 一起带过去", str(payload))
    # "没有窗口在跑"必须指向一个**没人建过**的管道名：这个测试里主窗口对象 `w`
    # 自己就在监听 PIPE_NAME（bbu-unpacker），拿它去测只会测到"转发给了 w"。
    import uuid as _uuid

    old_name = _single.PIPE_NAME
    _single.PIPE_NAME = "bbu-nobody-" + _uuid.uuid4().hex[:8]
    try:
        t0 = time.monotonic()
        nobody = _single.forward([r"D:\x.zip"], timeout=0.2)
        spent = time.monotonic() - t0
    finally:
        _single.PIPE_NAME = old_name
    sc.check(not nobody, "★ 没有窗口在跑时转交失败（调用方就会自己开界面）",
             f"forward={nobody} 用时 {spent:.2f}s")
    sc.check(spent < 1.5, "★ 没人接的时候也是秒回（不拖住右键）", f"{spent:.2f}s")
    # 主实例互斥体：同一个进程里重复调用结果必须一致（幂等），
    # 至于是不是"第一个"取决于本机此刻有没有界面在跑，所以只断言幂等和类型
    first = _single.become_primary()
    sc.check(isinstance(first, bool), "become_primary() 返回布尔", str(first))
    sc.check(_single.become_primary() is first,
             "★ 同一进程里重复抢主实例结果一致（幂等，不会自己把自己判成第二个）")
    sc.check("bbu" in _single.PIPE_NAME.lower() and _single.MUTEX_NAME,
             "管道名/互斥体名都带 bbu 前缀", f"{_single.PIPE_NAME} / {_single.MUTEX_NAME}")
    sc.check(_single.forward([]) in (True, False),
             "空路径转发不抛异常（会被当成「把窗口顶到前面」）")

    # ★ 已经有窗口在跑、又没带路径：不能再开第二个窗口，只能把那个窗口顶到前面
    import run as run_mod

    calls: list[tuple] = []
    orig_primary = _single.become_primary
    orig_forward = _single.forward
    try:
        _single.become_primary = lambda: False          # type: ignore[assignment]
        _single.forward = lambda *a, **k: calls.append((a, k)) or True  # type: ignore[assignment]
        rc = run_mod.main(["run.py"])
        sc.check(rc == 0 and calls and calls[0][0][0] == [],
                 "★ 第二个实例（不带路径）→ 只唤醒已有窗口、自己退出（不再开第二个窗口）",
                 f"rc={rc} calls={calls}")
        calls.clear()
        rc2 = run_mod.main(["run.py", r"D:\x.zip"])
        sc.check(rc2 == 0 and calls and calls[0][0][0] == [r"D:\x.zip"],
                 "带路径的第二个实例：路径照样转交", f"rc={rc2} calls={calls}")
    finally:
        _single.become_primary = orig_primary           # type: ignore[assignment]
        _single.forward = orig_forward                  # type: ignore[assignment]

    # --- ★ 输出设置搬到了主界面最外层，并且真的写进配置 ---
    mp.set_running(True)
    mp.set_paused(True)
    sc.check(mp.btn_start.text() == "继续",
             "★ 暂停时「开始」承担继续（暂停按钮自己不变文字）",
             f"开始={mp.btn_start.text()} 暂停={mp.btn_pause.text()}")
    mp.set_paused(False)
    sc.check(mp.btn_start.text() == "开始", "取消暂停后「继续」变回「开始」",
             mp.btn_start.text())
    mp.set_running(False)

    out_dir = os.path.join(ROOT, "tests", "ui-out")
    mp.rb_custom.setChecked(True)
    mp.ed_outdir.setText(out_dir)
    mp.cmb_conflict.setCurrentIndex(mp.cmb_conflict.findData("skip"))
    sc.check(mp.output_values() == ("custom", out_dir, "skip"),
             "主界面这一行能读出设置", str(mp.output_values()))
    st.sig_save.emit()
    pump(app, 0.2)
    sc.check(w.config.output_mode == "custom" and w.config.output_dir == out_dir
             and w.config.conflict == "skip",
             "★ 保存后写进配置（以前这两项只在设置页里才算数）",
             f"{w.config.output_mode} / {w.config.output_dir} / {w.config.conflict}")
    w.config.output_mode = "same"                 # 还原，别影响别的用例
    mp.rb_same.setChecked(True)
    st.sig_save.emit()
    pump(app, 0.2)
    sc.check(w.config.output_mode == "same" and w.config.conflict == "skip",
             "改回同目录也立刻生效", w.config.output_mode)

    passed = sum(1 for okk, _, _ in sc.results if okk)
    total = len(sc.results)
    print(f"\n===== {passed}/{total} 通过 =====")
    if passed != total:
        print("失败项：")
        for okk, name, detail in sc.results:
            if not okk:
                print(f"  - {name}  {detail}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
