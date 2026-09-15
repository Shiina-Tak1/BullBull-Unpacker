"""BullBull Unpacker —— 界面。

页面结构：
    Workbench
      ├── 顶栏（仅主页面显示）
      ├── 内容区 QStackedWidget
      │     ├── MainPage      工作台（拖拽/清单/输出设置/日志）
      │     ├── LibraryPage   密码本
      │     └── SettingsPage  设置
      └── PasswordDialog      密码试完后的手动输入（模态，按需弹）

结算结果**不另开页面**：以前那张"完成汇总"页的列宽要靠手算，任务一多/一有失败项
就互相压字（反馈了两次）。现在改成往日志里追加一段带状态色的结果行——日志天生
就是逐行流的，不会重叠，还能顺着往下滚看历史。对应的「打开输出目录」按钮就挂在
日志卡右上角。

配色：全局 QSS 来自 theme.py；少数 inline 上色由各页面的 `_theme_hook` 登记，
换主题时统一重放（见 ThemedMixin）。
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
import ctypes
from ctypes import wintypes

from PySide6.QtCore import (
    QEvent,
    QPoint,
    QRect,
    QSize,
    Qt,
    QThread,
    QTimer,
    Signal,
)
from PySide6.QtGui import (
    QColor,
    QFont,
    QFontDatabase,
    QFontMetrics,
    QIcon,
    QKeySequence,
    QPainter,
    QShortcut,
    QTextCursor,
)
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import (
    QAbstractButton,
    QAbstractItemView,
    QApplication,
    QButtonGroup,
    QCheckBox,
    QComboBox,
    QDialog,
    QDoubleSpinBox,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMenu,
    QMessageBox,
    QPlainTextEdit,
    QProgressBar,
    QPushButton,
    QRadioButton,
    QScrollArea,
    QSpinBox,
    QStackedWidget,
    QStyledItemDelegate,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from core import shellmenu
from core import single
from core import probe
from core import appinfo
from core import paths as paths_mod
from core.launchargs import parse_launch_args
from core.config import Config
from core.engine import Extractor, find_engines
from core import engine as engine_mod
from core.pipeline import (
    ItemStatus,
    ScanItem,
    needs_run,
    output_dirs,
    output_root,
    scan,
    summarize,
)
from core.runlog import RunLog
from core.vault import PasswordVault, sort_entries

from .theme import Theme
from . import icons
from . import theme as theme_mod
from .worker import AskRequest, JobWorker

# 软件名/版本/图标只在 core/appinfo.py 写一遍（打包时 exe 的版本资源也读它）
# 这里保留模块级别的同名常量，是为了不让旧引用（含测试）失效
APP_NAME = appinfo.APP_NAME
APP_VERSION = appinfo.VERSION
ICON_FILE = appinfo.ICON_FILE

# ==========================================================================
# 行模型
# ==========================================================================

# 界面层直接复用 core 的模型，别再维护一套镜像结构——
# 两套模型迟早会漂移，而且每次加字段都要改两处。
Status = ItemStatus
Task = ScanItem

# 日志面板最多留这么多条（换主题时要整段重渲染，不设上限会越来越卡）
MAX_LOG_ENTRIES = 800

# 文件夹汇总行的状态挂在状态格的这个角色上（代理只认它，不认 Task.status）
FOLDER_ROLE = Qt.ItemDataRole.UserRole + 7

# 窗口宽度窄于这个值才把清单按钮收成纯图标。
# 默认开窗是 800 逻辑像素宽（用户按物理像素 1600 给的），所以这个阈值必须比它小：
# "默认状态下打开，元素一个都不许被折叠"是用户点名的要求。
COMPACT_WIDTH = 720

# 单实例用的本机 socket 名：第二个进程进来时把路径交给已经在跑的那个窗口
# 命名管道的名字只有一处定义（core/single.py），客户端/服务端必须一致
PIPE_NAME = single.PIPE_NAME


def fmt_seconds(seconds: float) -> str:
    """把秒数变成「1m12s」这种给人看的写法。"""
    if not seconds:
        return ""
    if seconds < 60:
        return f"{seconds:.1f}s"
    m, s = divmod(int(seconds), 60)
    return f"{m}m{s:02d}s"

# ==========================================================================
# 小工具
# ==========================================================================


def card(obj_name: str = "Card") -> QFrame:
    f = QFrame()
    f.setObjectName(obj_name)
    return f


def vline() -> QFrame:
    f = QFrame()
    f.setObjectName("VDivider")
    f.setFrameShape(QFrame.Shape.VLine)
    f.setFixedWidth(1)
    return f


def label(text: str, obj: str = "", *, wrap: bool = False) -> QLabel:
    lb = QLabel(text)
    if obj:
        lb.setObjectName(obj)
    lb.setWordWrap(wrap)
    return lb


def mono_font() -> QFont:
    for fam in ("Cascadia Mono", "Consolas", "Courier New"):
        if fam in QFontDatabase.families():
            f = QFont(fam)
            f.setPointSize(9)
            f.setHintingPreference(QFont.HintingPreference.PreferFullHinting)
            return f
    f = QFont()
    f.setStyleHint(QFont.StyleHint.Monospace)
    f.setPointSize(9)
    return f


def ui_font() -> QFont:
    """界面字体：中文优先微软雅黑 UI，显式开全量 hinting。

    用户反馈"字看起来糊"——那台机器是 200% 缩放。两个已知原因：
      1. 以前用 `⚙ ▢ ✕ ▶ ⏸` 这些**字符当图标**，它们来自符号/emoji 字体，
         高 DPI 下常被当位图缩放 → 糊且大小不一（现在全换成矢量图标，见 ui/icons.py）；
      2. 字体没显式设 hinting，DirectWrite 在某些字号下会偏软。
    这里把字体族和 hinting 都定下来，正文/标题由 QSS 只管字号。
    """
    f = QFont()
    for fam in ("Microsoft YaHei UI", "Segoe UI", "Microsoft YaHei"):
        if fam in QFontDatabase.families():
            f.setFamily(fam)
            break
    f.setPointSize(10)
    f.setHintingPreference(QFont.HintingPreference.PreferFullHinting)
    f.setStyleStrategy(QFont.StyleStrategy.PreferAntialias)
    return f


def set_btn_icon(btn: QPushButton, name: str, *, size: int = 18, role: str = "text",
                 theme: Theme | None = None) -> None:
    """给按钮装矢量图标，并把 (名字/尺寸/颜色角色) 记在按钮上。

    记住这些是为了换主题时能重画——图标的颜色是**画进位图**的，不会跟着 QSS 变。
    """
    btn._icon_spec = (name, size, role)          # type: ignore[attr-defined]
    if theme is None:
        return
    btn.setIcon(icons.icon(name, theme.color(role), size))
    btn.setIconSize(QSize(size, size))


def refresh_icons(root: QWidget, theme: Theme) -> None:
    """换主题时重画这棵树里所有矢量图标。"""
    icons.clear_cache()
    for btn in root.findChildren(QPushButton):
        spec = getattr(btn, "_icon_spec", None)
        if spec:
            name, size, role = spec
            btn.setIcon(icons.icon(name, theme.color(role), size))
            btn.setIconSize(QSize(size, size))


# ==========================================================================
# 主题切换
# ==========================================================================
#
# 配色有两个来源：全局 QSS（setStyleSheet 一换全变）+ 很多处 inline 的
# `theme.color(...)`（字号/状态色这种 QSS 表达不方便的）。后者是**构造时取值**的，
# 只换 QSS 的话，那些标签会停在旧主题的颜色上——这就是"深色浅色切换不生效"。
#
# 所以每个页面自己登记一批"重新上色的小函数"（_theme_hooks），
# Workbench 换主题时逐个调一遍。


class ThemedMixin:
    """给页面/浮层用：登记并重放"与主题有关的那些 inline 样式"。"""

    def _theme_hook(self, fn) -> None:
        hooks = getattr(self, "_theme_hooks", None)
        if hooks is None:
            hooks = self._theme_hooks = []
        hooks.append(fn)

    def apply_theme(self) -> None:
        for fn in getattr(self, "_theme_hooks", []):
            try:
                fn()
            except RuntimeError:
                pass          # 控件已经被删（页面重建过），忽略


def repolish(widget: QWidget) -> None:
    """让新 QSS 对**已存在**的控件全部生效。

    只 setStyleSheet 通常会自动重算，但带动态属性（#DropZone[dragActive]）和
    自绘代理的控件不一定跟着变，显式 unpolish/polish 一遍最省心。
    """
    style = widget.style()
    targets = [widget] + widget.findChildren(QWidget)
    for w in targets:
        style.unpolish(w)
        style.polish(w)
        w.update()


# ==========================================================================
# 状态列代理：在单元格里画进度条
# ==========================================================================


class StatusDelegate(QStyledItemDelegate):
    """状态列：排队=文字，运行中=进度条+百分比+速度，完成=✔+耗时。

    文件夹汇总行自己不跑，所以它的状态由**孩子们**汇总出来（数据挂在
    `FOLDER_ROLE` 上，见 `MainPage._folder_status`）——用户的要求是
    "文件夹在处理子任务时要显示进行中，而不是一直挂排队中"。
    """

    def __init__(self, theme: Theme, parent=None) -> None:
        super().__init__(parent)
        self.theme = theme

    def sizeHint(self, opt, index) -> QSize:
        return QSize(240, 34)

    def paint(self, painter: QPainter, opt, index) -> None:
        task: Task = index.data(Qt.ItemDataRole.UserRole)
        if task is None:
            super().paint(painter, opt, index)
            return

        c = self.theme.c
        painter.save()
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = opt.rect.adjusted(8, 0, -8, 0)
        cy = r.center().y()

        f = QFont(painter.font())
        f.setPointSize(9)

        # 文件夹行：显示汇总状态（进行中 / 完成 x/y / 排队中）
        folder = index.data(FOLDER_ROLE)
        if folder:
            text, role = folder
            painter.setPen(QColor(c.get(role, c["text_dim"])))
            painter.setFont(f)
            painter.drawText(r, int(Qt.AlignmentFlag.AlignLeft
                                    | Qt.AlignmentFlag.AlignVCenter), text)
            painter.restore()
            return

        if task.status is Status.RUNNING:
            bar_w = int(r.width() * 0.42)
            bar_h = 6
            bar = QRect(r.left(), cy - bar_h // 2, bar_w, bar_h)
            painter.setPen(Qt.PenStyle.NoPen)
            bg = QColor(c["surface_2"])
            painter.setBrush(bg)
            painter.drawRoundedRect(bar, 3, 3)
            fill_w = int(bar_w * task.progress / 100)
            if fill_w > 0:
                painter.setBrush(QColor(c["accent"]))
                painter.drawRoundedRect(QRect(bar.left(), bar.top(), fill_w, bar_h), 3, 3)

            painter.setPen(QColor(c["text"]))
            painter.setFont(f)
            txt_rect = QRect(bar.right() + 10, r.top(), r.width() - bar_w - 10, r.height())
            # 用户要的写法：**第 x 层 / 第 y 层**（以前是"第 2 层" + 远处一个"/ 5"，
            # 读起来像两个不相干的数字）
            layer = max(task.layer, 1)
            text = (f"第 {layer} 层 / 第 {task.max_layer} 层" if task.max_layer
                    else f"第 {layer} 层")
            painter.drawText(txt_rect,
                             int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                             text)

        elif task.status is Status.QUEUED:
            painter.setPen(QColor(c["text_faint"]))
            painter.setFont(f)
            painter.drawText(
                r,
                int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                "◷  排队中",
            )

        elif task.status is Status.DONE:
            painter.setPen(QColor(c["ok"]))
            painter.setFont(f)
            painter.drawText(
                r,
                int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                f"✔  完成    {task.elapsed_text}",
            )

        elif task.status is Status.FAILED:
            painter.setPen(QColor(c["err"]))
            painter.setFont(f)
            painter.drawText(
                r,
                int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                "✘  失败",
            )
        else:
            painter.setPen(QColor(c["warn"]))
            painter.setFont(f)
            painter.drawText(
                r,
                int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                "⚠  已跳过",
            )

        painter.restore()


# ==========================================================================
# 顶栏
# ==========================================================================


class ScanJob(QThread):
    """后台跑 core.pipeline.scan()。

    扫描会起 7z 问"加密没"、还会为垫片伪装整盘读文件——几 GB 的 mp4 就是好几秒，
    放 UI 线程上界面会僵住。这里只把结果带回主线程，绝不碰控件。
    """

    sig_done = Signal(list, str)          # (items, 出错信息)

    def __init__(self, paths: list[str], exclude: list[str] | None = None, parent=None) -> None:
        super().__init__(parent)
        self._paths = list(paths)
        self._exclude = list(exclude or [])

    def run(self) -> None:                # noqa: D102 - QThread 约定的入口
        try:
            self.sig_done.emit(scan(self._paths, exclude=self._exclude), "")
        except Exception as exc:          # noqa: BLE001 - 什么错都得让用户看见
            self.sig_done.emit([], repr(exc))


class TopBar(ThemedMixin, QFrame):
    """自绘标题栏（系统标题栏已经被 FramelessWindowHint 去掉了）。

    组成：[应用图标] 产品名 + 版本 …… 任务数 · 密码本 · 设置 | 最小化 最大化 关闭
    所有图标都是矢量图（见 ui/icons.py），不再用 `⚙ ▢ ✕` 这类字符——
    字符图标来自不同字体，大小/粗细对不齐，高 DPI 下还会糊。
    """

    sig_min = Signal()
    sig_max = Signal()
    sig_close = Signal()
    sig_library = Signal()

    def __init__(self, theme: Theme, icon_path: str = "", parent=None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.setObjectName("TopBar")
        # 顶栏加高：图标、标题、窗口按钮都往上加一档（用户："标题行的 icon / 文字标题 /
        # 按钮都可以再大再粗一点"）。默认窗宽只有 800 逻辑像素，但这一行元素不多，
        # 加大的同时把间距收一点仍然放得下。
        self.setFixedHeight(64)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(18, 0, 10, 0)
        lay.setSpacing(12)

        # 应用图标：用户给的那张 icon.png（打包成 assets/bbu.ico）——
        # 以前它只出现在任务栏和右键菜单里，无边框之后界面里根本看不见
        self.logo = QLabel()
        self.logo.setFixedSize(38, 38)
        self._logo_path = icon_path
        self._paint_logo()
        lay.addWidget(self.logo)

        lay.addWidget(label(APP_NAME, "Title"))
        lay.addWidget(label(f"v{APP_VERSION}", "Version"))
        lay.addStretch(1)

        self.badge = label("", "Faint")
        lay.addWidget(self.badge)

        self.btn_library = QPushButton()
        self.btn_library.setObjectName("Ghost")
        self.btn_library.setFixedSize(40, 38)
        self.btn_library.setToolTip("密码本")
        set_btn_icon(self.btn_library, "key", size=21, role="text_dim", theme=theme)
        self.btn_library.clicked.connect(self.sig_library.emit)
        lay.addWidget(self.btn_library)

        self.btn_settings = QPushButton()
        self.btn_settings.setObjectName("Ghost")
        self.btn_settings.setFixedSize(40, 38)
        self.btn_settings.setToolTip("设置")
        set_btn_icon(self.btn_settings, "settings", size=21, role="text_dim", theme=theme)
        lay.addWidget(self.btn_settings)

        lay.addSpacing(4)
        self.btn_min = QPushButton()
        self.btn_max = QPushButton()
        self.btn_close = QPushButton()
        for btn, name, tip, sig in (
            (self.btn_min, "min", "最小化", self.sig_min),
            (self.btn_max, "max", "最大化 / 还原", self.sig_max),
            (self.btn_close, "close", "关闭", self.sig_close),
        ):
            btn.setObjectName("WinBtnClose" if name == "close" else "WinBtn")
            btn.setFixedSize(44, 38)
            btn.setToolTip(tip)
            set_btn_icon(btn, name, size=20,
                         role="err" if name == "close" else "text_dim", theme=theme)
            btn.clicked.connect(sig.emit)
            lay.addWidget(btn)

        self._theme_hook(self._paint_logo)
        self._theme_hook(lambda: self.badge.setStyleSheet(""))

    def _paint_logo(self) -> None:
        """把应用图标画进标题栏（.ico 的 32 号）——换了 icon.png 重跑 make_icon.py 就变。"""
        if not self._logo_path or not os.path.isfile(self._logo_path):
            self.logo.setText("▣")
            self.logo.setStyleSheet(f"color:{self.theme.color('accent')}; font-size:24px;")
            return
        pm = QIcon(self._logo_path).pixmap(38, 38)
        self.logo.setPixmap(pm)

    def set_count(self, n: int) -> None:
        self.badge.setText(f"{n} 个任务" if n else "")

    def set_maximized(self, yes: bool) -> None:
        """最大化状态换图标（▢ ↔ 两个错开的方框）。"""
        name = "restore" if yes else "max"
        set_btn_icon(self.btn_max, name, size=20, role="text_dim", theme=self.theme)
        self.btn_max.setToolTip("还原" if yes else "最大化")


# ==========================================================================
# 主页面
# ==========================================================================


class MainPage(ThemedMixin, QWidget):
    sig_open_library = Signal()
    sig_open_settings = Signal()
    sig_paths_added = Signal()
    # 窗口按钮（系统标题栏去掉了，这三个转发给 Workbench）
    sig_window_min = Signal()
    sig_window_max = Signal()
    sig_window_close = Signal()

    def __init__(self, theme: Theme, parent=None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.tasks: list[Task] = []
        self.is_running = False
        self._paused = False            # 暂停按钮的状态（收成图标后不能再靠按钮文字判断）
        self._pause_syncing = False     # set_paused() 里改勾选时置位，避免重复下发暂停/继续
        self._compact_stage = -1        # 当前响应式档位（-1 = 还没算过）
        self._hint_cache: dict[int, int] = {}   # 按钮"带文字"时需要的宽度（收起来后 sizeHint 会变小）
        self._row_decision: dict = {}   # 最近一次档位判定的依据（测试要看）
        # 界面日志落盘（logs/run.log，带轮转）。测试传 base_dir 时也跟着走，
        # 所以不会写到用户真实日志里；写不进去就自动降级成"只显示不落盘"。
        try:
            self._runlog = RunLog(paths_mod.run_log_path())
            self._runlog.session_header(f"{appinfo.APP_NAME} {appinfo.VERSION}"
                                        + ("" if not paths_mod.is_frozen() else "（打包版）"))
        except Exception:               # noqa: BLE001 - 日志写不进去不该影响启动
            self._runlog = None
        self._log_entries: list[tuple[str, str, str]] = []
        # 日志是增量画的：_log_rendered = 已经画出来几行（换主题时整段重画）
        self._log_rendered = 0
        # 后台扫描（拖入几 GB 的 mp4 时不能卡界面，见 add_paths）
        self._scan_job = None
        self._scan_queue: list[str] = []
        # 这一批要跑的任务（id 列表）：总进度条的分母，开跑那一刻定死
        self._batch_ids: list[int] = []
        # 「不处理的文件类型」（.apk/.iso 这类"其实是 zip 但用户不想让它拆"的东西）。
        # 由 Workbench 灌进来（它读配置），这里只存一份给扫描线程用。
        self._exclude_exts: list[str] = []
        # 「打开输出目录」当前认定的目标：多个产物时 _open_root 是公共上级
        self._open_dirs: list[str] = []
        self._open_root = ""
        self.setAcceptDrops(True)

        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(12)

        # 图标是**只读资源**，跟"数据目录"是两回事：打包后它在 _internal 里，
        # 所以要经 paths.resource_path 拿（见 core/paths.py 的说明）
        icon_path = paths_mod.resource_path("assets", ICON_FILE)
        self.topbar = TopBar(theme, icon_path)
        self.topbar.btn_settings.clicked.connect(self.sig_open_settings.emit)
        self.topbar.btn_library.clicked.connect(self.sig_open_library.emit)
        self.btn_lib = self.topbar.btn_library      # 兼容旧引用（老代码/测试按这个名字找）
        self.topbar.sig_min.connect(self.sig_window_min.emit)
        self.topbar.sig_max.connect(self.sig_window_max.emit)
        self.topbar.sig_close.connect(self.sig_window_close.emit)
        root.addWidget(self.topbar)

        # ---- 计数卡 ----
        self.counter_row = QHBoxLayout()
        self.counter_row.setSpacing(12)
        self.stats: dict[str, QLabel] = {}
        for key, text, color in (
            ("queued", "待处理", "text"),
            ("running", "运行中", "info"),
            ("done", "已完成", "ok"),
            ("failed", "失败", "err"),
        ):
            f = card("Card")
            f.setFixedHeight(72)
            v = QVBoxLayout(f)
            v.setContentsMargins(16, 10, 16, 10)
            v.setSpacing(0)
            num = label("0", "StatNum")
            num.setStyleSheet(f"color:{theme.color(color)};")
            self._theme_hook(
                lambda lb=num, role=color: lb.setStyleSheet(f"color:{theme.color(role)};")
            )
            self.stats[key] = num
            v.addWidget(num)
            v.addWidget(label(text, "StatLabel"))
            self.counter_row.addWidget(f)
        root.addLayout(self.counter_row)

        self.dropzone = self._build_dropzone()

        # ---- 从上到下的顺序（用户点名要的，缩放不改顺序）----
        #   ① 文件列表（空的时候**同一个槽位**显示拖拽引导）
        #   ② 输出目录 + 重命名 一行
        #   ③ 开始/暂停/停止 + 清单操作 一行
        self.list_stack = QStackedWidget()
        self.list_stack.addWidget(self._build_table())     # 0
        self.list_stack.addWidget(self.dropzone)           # 1
        root.addWidget(self.list_stack, 1)

        root.addWidget(self._build_output_row())
        root.addWidget(self._build_action_row())

        # ---- 日志 ----
        root.addWidget(self._build_log())

        # ---- 总进度 ----
        root.addWidget(self._build_footer())

        for b in (self.btn_files_big,):
            b.clicked.connect(self.pick_files)
        for b in (self.btn_dirs_big,):
            b.clicked.connect(self.pick_dirs)

        self._refresh_stats()
        self._update_empty_state()
        self._sync_buttons()

        # 换主题时：顶栏图标/矢量图标 + 日志整段重渲染（日志颜色写在 HTML 里）
        self._theme_hook(self.topbar.apply_theme)
        self._theme_hook(lambda: refresh_icons(self, theme))
        self._theme_hook(self._render_log)

    # ------------------------------------------------------------------
    def _build_dropzone(self) -> QWidget:
        """空状态：卡片式的拖拽引导。

        它跟任务表**共用一个槽位**（`list_stack`），所以"加入任务"这件事在屏幕上
        只是这一块换了内容，上下两行按钮一动不动——用户报的"添加之后任务表格出现
        在两排按钮下面、位置变了"就是这么来的（以前它是独立一大块，加进任务后
        整块收掉，表格才冒出来）。
        """
        z = card("DropZone")

        bv = QVBoxLayout(z)
        bv.setContentsMargins(20, 16, 20, 16)
        bv.setSpacing(10)
        bv.addStretch(1)

        icon = QLabel()
        icon.setAlignment(Qt.AlignmentFlag.AlignCenter)

        def _paint_arrow() -> None:
            icon.setPixmap(
                icons.icon("arrow-down", self.theme.color("accent"), 36).pixmap(36, 36)
            )

        _paint_arrow()
        self._theme_hook(_paint_arrow)
        bv.addWidget(icon)

        t = label("把压缩包或文件夹拖到这里", "Title")
        t.setAlignment(Qt.AlignmentFlag.AlignCenter)
        bv.addWidget(t)

        orly = label("或", "Faint")
        orly.setAlignment(Qt.AlignmentFlag.AlignCenter)
        bv.addWidget(orly)

        row = QHBoxLayout()
        row.addStretch(1)
        b3 = QPushButton("选择文件")
        b4 = QPushButton("选择文件夹")
        b3.setMinimumWidth(110)
        b4.setMinimumWidth(110)
        row.addWidget(b3)
        row.addWidget(b4)
        row.addStretch(1)
        bv.addLayout(row)

        hint = label(".zip .rar .7z · 分卷 · 伪装 · 嵌套穿透", "Faint")
        hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        bv.addWidget(hint)
        bv.addStretch(1)

        self.btn_files_big, self.btn_dirs_big = b3, b4
        return z

    def _update_empty_state(self) -> None:
        """有任务就只看列表；没任务时显示拖拽引导——**同一个槽位**，位置不变。"""
        self.list_stack.setCurrentIndex(0 if self.tasks else 1)

    # ------------------------------------------------------------------
    # 主操作行 + 「输出设置 | 清单操作」行
    # ------------------------------------------------------------------

    def _build_output_row(self) -> QWidget:
        """② 输出目录 + 重命名 一行（在文件列表**下面**，位置固定不随缩放变）。

        为什么把"重名"和输出目录放一起：这两件事都属于"这次怎么解"，
        用户要的就是它们排成一行。
        """
        f = card("Card")
        f.setFixedHeight(50)
        lay = QHBoxLayout(f)
        lay.setContentsMargins(14, 0, 12, 0)
        lay.setSpacing(8)

        out_icon = QLabel()
        out_icon.setPixmap(icons.icon("folder-out", self.theme.color("text_dim"), 16).pixmap(16, 16))
        lay.addWidget(out_icon)

        self.rb_same = QRadioButton("与原文件同目录")
        self.rb_custom = QRadioButton("指定目录")
        self.rb_same.setChecked(True)
        grp = QButtonGroup(self)
        grp.addButton(self.rb_same)
        grp.addButton(self.rb_custom)
        lay.addWidget(self.rb_same)
        lay.addWidget(self.rb_custom)

        self.ed_outdir = QLineEdit()
        self.ed_outdir.setPlaceholderText("选择目录…")
        self.ed_outdir.setMinimumHeight(30)
        self.ed_outdir.setMinimumWidth(110)
        lay.addWidget(self.ed_outdir, 1)

        btn_browse = QPushButton("浏览")
        btn_browse.setMinimumHeight(30)
        btn_browse.clicked.connect(self.pick_output_dir)
        lay.addWidget(btn_browse)

        lay.addWidget(vline())
        lay.addWidget(label("重名", "Dim"))
        self.cmb_conflict = QComboBox()
        # "自动加 (1)" 太含糊：得让人一眼看出是"重名时自动加数字序号"
        for text, value in (("自动添加数字序号", "rename"), ("覆盖", "overwrite"), ("跳过", "skip")):
            self.cmb_conflict.addItem(text, value)
        self.cmb_conflict.setMinimumWidth(132)
        lay.addWidget(self.cmb_conflict)

        # 指定目录时才让路径那两件控件可用，别让人对着灰输入框发愁
        self.rb_custom.toggled.connect(
            lambda on: (self.ed_outdir.setEnabled(on), btn_browse.setEnabled(on))
        )
        self.ed_outdir.setEnabled(False)
        btn_browse.setEnabled(False)
        return f

    def _build_action_row(self) -> QWidget:
        """③ 开始/暂停/停止 + 清单操作 一行。

        分组原则：左边是"这次跑不跑"（一屏只有一个主色按钮），右边是"清单里放什么"。
        密码本按钮**删掉了**——顶栏已经有一个，重复摆一个只会让人犹豫。
        这些按钮的悬浮说明也一并去掉（用户："删掉这些按钮的悬浮说明文字"）：
        文案本身已经说清了，再弹一层提示只是噪音。
        """
        f = card("Card")
        f.setFixedHeight(56)
        lay = QHBoxLayout(f)
        lay.setContentsMargins(12, 0, 12, 0)
        lay.setSpacing(6)          # 7 个按钮，间距从 8 收到 6（配合 QSS 的 #RowBtn 内边距）

        self.btn_start = QPushButton("开始")
        self.btn_start.setObjectName("Primary")         # 主色按钮（QSS 里连带收窄内边距）
        self.btn_start.setMinimumSize(96, 38)
        self.btn_start.setToolTip("开始解压")
        set_btn_icon(self.btn_start, "play", size=16, role="accent_text", theme=self.theme)
        lay.addWidget(self.btn_start)

        self.btn_pause = QPushButton("暂停")
        self.btn_pause.setObjectName("RowBtn")
        self.btn_pause.setMinimumSize(70, 38)
        set_btn_icon(self.btn_pause, "pause", size=15, role="text", theme=self.theme)
        lay.addWidget(self.btn_pause)

        self.btn_stop = QPushButton("停止")
        self.btn_stop.setObjectName("RowBtn")
        self.btn_stop.setMinimumSize(70, 38)
        set_btn_icon(self.btn_stop, "stop", size=14, role="text", theme=self.theme)
        lay.addWidget(self.btn_stop)

        lay.addStretch(1)

        self.btn_add = QPushButton("添加文件")
        self.btn_add.setObjectName("RowBtn")
        self.btn_add.setMinimumHeight(34)
        set_btn_icon(self.btn_add, "file-plus", size=16, role="text", theme=self.theme)
        self.btn_add.clicked.connect(self.pick_files)
        lay.addWidget(self.btn_add)

        self.btn_add_dir = QPushButton("添加文件夹")
        self.btn_add_dir.setObjectName("RowBtn")
        self.btn_add_dir.setMinimumHeight(34)
        set_btn_icon(self.btn_add_dir, "folder-plus", size=16, role="text", theme=self.theme)
        self.btn_add_dir.clicked.connect(self.pick_dirs)
        lay.addWidget(self.btn_add_dir)

        self.btn_clear_sel = QPushButton("清除选中")
        self.btn_clear_sel.setObjectName("RowBtn")
        self.btn_clear_sel.setMinimumHeight(34)
        set_btn_icon(self.btn_clear_sel, "trash", size=16, role="text", theme=self.theme)
        self.btn_clear_sel.clicked.connect(self.clear_selected)
        lay.addWidget(self.btn_clear_sel)

        self.btn_clear = QPushButton("清空列表")
        self.btn_clear.setObjectName("RowBtn")
        self.btn_clear.setMinimumHeight(34)
        set_btn_icon(self.btn_clear, "clear", size=16, role="text", theme=self.theme)
        self.btn_clear.clicked.connect(self.clear_all)
        lay.addWidget(self.btn_clear)

        # 兼容旧引用：以前清单操作行里有个「密码本」按钮，现在只留顶栏那个
        self.btn_lib_row = self.topbar.btn_library

        self.btn_pause.setEnabled(False)
        self.btn_stop.setEnabled(False)
        self.btn_start.setEnabled(False)
        return f

    def pick_output_dir(self) -> None:
        d = QFileDialog.getExistingDirectory(self, "选择输出目录")
        if d:
            self.ed_outdir.setText(d)
            self.rb_custom.setChecked(True)

    def output_values(self) -> tuple[str, str, str]:
        """当前界面上的输出设置 → (mode, dir, conflict)。"""
        mode = "same" if self.rb_same.isChecked() else "custom"
        return mode, self.ed_outdir.text().strip(), self.cmb_conflict.currentData()

    def load_output(self, mode: str, directory: str, conflict: str) -> None:
        self.rb_same.setChecked(mode != "custom")
        self.rb_custom.setChecked(mode == "custom")
        self.ed_outdir.setText(directory or "")
        idx = self.cmb_conflict.findData(conflict)
        self.cmb_conflict.setCurrentIndex(idx if idx >= 0 else 0)

    # ------------------------------------------------------------------
    # 真数据入口：拖入 / 选择 → 后台 scan() → 清单
    # ------------------------------------------------------------------

    def add_paths(self, paths: list[str]) -> int:
        """把路径交给**后台线程**扫描，扫完追加到清单。返回这次提交了几个路径。

        为什么不是同步扫：扫描里有两件慢事——问 7z 这个包加没加密、以及"垫片伪装"
        要把非压缩包整盘读一遍找内嵌包。几 GB 的 mp4 就是好几秒，放在 UI 线程上
        界面会直接僵住（用户报的"拖动 mp4 进去大卡顿"就是这个）。
        真正新增了几项由扫描结果决定，完成后写日志。

        异常一律就地接住：pythonw 下没有控制台，抛出去就是"点了没反应"。
        """
        paths = [p for p in (paths or []) if p]
        if not paths:
            return 0
        self._scan_queue.extend(paths)
        if self._scan_job is None:
            self._start_scan()
        return len(paths)

    def _start_scan(self) -> None:
        batch, self._scan_queue = self._scan_queue, []
        if not batch:
            return
        self.append_log("扫描", f"正在扫描 {len(batch)} 个路径…", "text_dim")
        self.scan_badge.setText("扫描中…")
        job = ScanJob(batch, self._exclude_exts, self)
        job.sig_done.connect(self._scan_finished)
        self._scan_job = job
        job.start()

    def _scan_finished(self, items: list, error: str) -> None:
        """扫描线程回来了：合并进清单，然后看队列里还有没有新提交的。"""
        self._scan_job = None
        self.scan_badge.setText("")
        if error:
            self.append_log("错误", f"扫描失败：{error}", "err")
        items = list(items or [])
        if not items and not error:
            self.append_log("扫描", "这些路径里没有可处理的文件", "warn")
        if items:
            # ★ 跨批去重：同一个包（或它的另一个分卷）已经在清单里就不再加一行。
            #   批量扫描内部有去重，但"右键多选"是**分几批**进来的，跨批就会重复
            #   （实测：分卷被加了三次、同名文件出现两行）。
            existing = {os.path.normcase(os.path.abspath(t.path)) for t in self.tasks}
            fresh_items = []
            dups = 0
            for it in items:
                key = os.path.normcase(os.path.abspath(it.path))
                if key in existing:
                    dups += 1
                    continue
                existing.add(key)
                fresh_items.append(it)
            if dups:
                self.append_log("扫描", f"其中 {dups} 项已经在清单里了，没有重复添加", "info")
            items = fresh_items
        if items:
            self.tasks.extend(items)
            self._fill_table()
            self._update_empty_state()
            self.append_log("扫描", f"加入 {len(items)} 项（共 {len(self.tasks)} 项）", "info")
            fresh = sum(1 for it in items if it.runnable)
            if fresh and self.is_running:
                # 正在跑的时候加进来的：说清楚它们不会被这一批顺手带走，
                # 但**这批一结束就会自动接着跑**（不用再点开始）
                self.append_log(
                    "系统",
                    f"新加的 {fresh} 项排在后面，这批跑完自动接着解",
                    "info",
                )
            for it in items:
                if it.kind.startswith("📁"):
                    self.append_log("探测", f"{it.name} — {it.kind}", "text_dim")
                elif not it.runnable:
                    # 拖进来一个真视频/文档：明确说"它不会被处理"，
                    # 免得用户对着灰着的「开始」按钮猜发生了什么
                    self.append_log("跳过", f"{it.name} — {it.note or '不是压缩包'}", "warn")
        self._sync_buttons()
        self.sig_paths_added.emit()
        self._dump_state()
        if self._scan_queue:
            self._start_scan()

    def _dump_state(self) -> None:
        """给验证脚本看的"清单现状"（只有设了 SMART_UNZIP_STATE_FILE 才写）。

        为什么需要它：右键那条路的真实断言是"清单里真的多了那几行"，
        而这件事从进程外面看不到（以前只验到"没自动开跑"，所以漏掉了
        "发过来的路径其实没进清单"这种 bug）。写的是任务名，一行一个。

        另外把**当前页 + 日志尾部**也写进去：排"右键加了但界面上看不见"这类问题时，
        光看清单名不够——得知道窗口当时停在哪一页、日志里到底有没有说话。
        """
        path = os.environ.get("SMART_UNZIP_STATE_FILE")
        if not path:
            return
        try:
            with open(path, "w", encoding="utf-8") as f:
                for t in self.tasks:
                    f.write(t.name + "\n")
                # 下面都是 `#` 开头的诊断行（清单名保持"一行一个名字"的格式，
                # 验证脚本靠它断言"某某真的进清单了"）
                page = "?"
                try:
                    win = self.window()
                    stack = getattr(win, "stack", None)
                    if stack is not None:
                        cur = stack.currentWidget()
                        pairs = ((getattr(win, "main_page", None), "main"),
                                 (getattr(win, "library_page", None), "library"),
                                 (getattr(win, "settings_page", None), "settings"))
                        page = next((n for w, n in pairs if w is cur), "other")
                except Exception:        # noqa: BLE001 - 纯诊断信息，取不到就算
                    page = "?"
                f.write(f"\n# page={page} visible={self.window().isVisible()} "
                        f"minimized={self.window().isMinimized()} tasks={len(self.tasks)} "
                        f"running={self.is_running} scanning={self._scan_job is not None} "
                        f"maximized={self.window().isMaximized()} "
                        f"size={self.window().width()}x{self.window().height()}\n")
                for t in self.tasks:
                    f.write(f"# task {t.name} :: {t.status.value}"
                            + (f" :: {t.note}" if t.note else "") + "\n")
                try:
                    for tag, msg, _color in self._log_entries[-15:]:
                        f.write(f"# log {tag} {msg}\n")
                except Exception:        # noqa: BLE001
                    pass
        except OSError:
            pass

    def wait_scan(self, timeout: float = 120.0) -> bool:
        """等后台扫描做完（测试/截图脚本用；顺带把队列里的批次也跑完）。"""
        app = QApplication.instance()
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if self._scan_job is None and not self._scan_queue:
                return True
            if app is not None:
                app.processEvents()
            time.sleep(0.01)
        return False

    def _sync_buttons(self) -> None:
        """按钮可用性只在这里算，别指望某个信号一定会发到。

        （之前的 bug：只有拖拽的 dropEvent 才发 sig_paths_added，
         走「选择文件」对话框加进来的文件，开始按钮一直是灰的。）
        """
        has_runnable = any(t.runnable for t in self.tasks)
        has_selection = bool(self.table.selectionModel().hasSelection()) if self.table.selectionModel() else False
        # ★ 「开始 / 继续」和「暂停」是**互斥**的（用户要的）：
        #   没跑    → 开始可用，暂停/停止灰着
        #   跑着呢  → 开始灰着（已经在跑了），暂停可用
        #   暂停中  → 开始变成「继续」并可用，暂停灰着
        # 这样任何时刻只有一个"当前该按的按钮"，不会出现两个都能按、按哪个才对的问题。
        paused = self.is_running and self._paused
        self.btn_start.setText("继续" if paused else "开始")
        self.btn_start.setToolTip("继续解压" if paused else "开始解压")
        self.btn_start.setEnabled(paused or (has_runnable and not self.is_running))
        self.btn_add.setEnabled(not self.is_running)
        self.btn_clear.setEnabled(not self.is_running and bool(self.tasks))
        self.btn_clear_sel.setEnabled(not self.is_running and has_selection)
        self.btn_pause.setEnabled(self.is_running and not self._paused)
        self.btn_stop.setEnabled(self.is_running)

    def update_item(self, item: Task) -> None:
        """按对象原地刷新对应行，不整表重建（重建会丢选中/滚动位置）。"""
        for row, t in enumerate(self.tasks):
            if t is not item:
                continue
            pw_cell = self.table.item(row, 2)
            if pw_cell is not None:
                # 只写密码本身：来源（密码本/手动输入）是内部概念，
                # 用户要的就是"这个包用的哪把钥匙"，来源栏纯属噪音。
                pw_cell.setText(t.password or "—")
                # ★ 颜色也必须在这里重设：`_fill_table` 建行时把"有密码"的格子染成主色，
                #   而运行中/跑完的原地刷新只 setText —— 前景色不会跟着回来，于是
                #   同一列出现"绿的（建行时染的）"和"白的（刷新过的）"两种样子
                #   （用户就是拿截图来问"为啥前两行是绿的"）。
                self._paint_password_cell(pw_cell, t)
                pw_cell.setToolTip("点击复制到剪贴板" if t.password else "")
            st_cell = self.table.item(row, 3)
            if st_cell is not None:
                st_cell.setData(Qt.ItemDataRole.UserRole, t)
            kind_cell = self.table.item(row, 1)
            if kind_cell is not None and t.note:
                kind_cell.setToolTip(t.note)
            self._refresh_folder_rows()      # 子任务状态变了，文件夹那行也要跟着变
            self._update_overall()           # 总进度：一项跑完就往前走一格
            self.table.viewport().update()
            self._refresh_stats()
            return

    def set_running(self, running: bool) -> None:
        """按运行状态切换按钮可用性；开跑时给总进度条定分母、并起/停右下角计时器。"""
        self.is_running = running
        if running:
            # 总进度 = 这一批里完成了几项 / 这一批一共几项（用户要的简单口径）。
            # 分母**在开跑那一刻定死**：跑到一半又拖进来的新文件不算这一批的，
            # 否则进度条会往回跳。
            self._batch_ids = [id(t) for t in self.tasks if t.runnable and needs_run(t)]
            self._update_overall()
            self.start_clock()
        else:
            self.stop_clock()
        self._sync_buttons()

    def _update_overall(self) -> None:
        """按「已完成任务数 / 本批任务数」更新总进度条（不搞层数/字节那套）。"""
        if not self._batch_ids:
            return
        by_id = {id(t): t for t in self.tasks}
        chosen = [by_id[i] for i in self._batch_ids if i in by_id]
        total = len(chosen) or 1
        done = sum(1 for t in chosen
                   if t.status in (Status.DONE, Status.FAILED, Status.SKIPPED))
        pct = int(done / total * 100)
        self.overall.setValue(pct)
        self.overall_text.setText(f"{pct}%")

    def clear_tasks(self) -> None:
        """清空全部任务，回到空状态。"""
        self.tasks = []
        self.table.setRowCount(0)
        self._log_entries = []
        self._log_rendered = 0
        self._log_stick = True          # 清空之后重新跟着最新一行走
        self._batch_ids = []
        self.log.clear()
        self.log_badge.setText("")
        self.overall.setValue(0)
        self.overall_text.setText("0%")
        self.reset_clock()          # 清空列表 → 右下角不显示时间
        self.topbar.set_count(0)
        self.reset_open_button()
        self._refresh_stats()
        self._update_empty_state()
        self._sync_buttons()

    def clear_all(self) -> None:
        """「清空列表」：整张清单清掉（不分已完成/未完成）。

        以前叫「清空已完成」、只删跑完的那几行——但跑完的本来就不会留着"继续处理"，
        留着半张表只会让人以为还有活没干，索性整表清掉。
        """
        if self.is_running:
            self.append_log("系统", "正在跑，先「停止」再清空列表", "warn")
            return
        had = len(self.tasks)
        self.clear_tasks()
        if had:
            self.append_log("系统", f"已清空列表（{had} 项）", "info")

    def clear_selected(self) -> None:
        """「清除选中」：只把选中的那几行去掉。"""
        if self.is_running:
            self.append_log("系统", "正在跑，先「停止」再清除选中", "warn")
            return
        rows = sorted({i.row() for i in self.table.selectedIndexes()}, reverse=True)
        if not rows:
            self.append_log("系统", "先在清单里选几行（Ctrl/Shift 可以多选）", "warn")
            return
        for row in rows:
            if 0 <= row < len(self.tasks):
                self.tasks.pop(row)
        self._fill_table()
        self._update_empty_state()
        self._sync_buttons()
        self.topbar.set_count(len(self.tasks))
        self._refresh_stats()
        self.append_log("系统", f"已清除选中 {len(rows)} 项（剩 {len(self.tasks)} 项）", "info")

    def _on_selection_changed(self) -> None:
        self._sync_buttons()

    def _fill_table(self) -> None:
        """按 self.tasks 重建表格。"""
        self.table.setRowCount(len(self.tasks))
        for row, t in enumerate(self.tasks):
            name = ("    └  " if t.indent else "") + t.name
            item = QTableWidgetItem(name)
            item.setData(Qt.ItemDataRole.UserRole, t)
            self.table.setItem(row, 0, item)

            kind_cell = QTableWidgetItem(t.kind)
            if t.note:
                kind_cell.setToolTip(t.note)
            self.table.setItem(row, 1, kind_cell)

            pw = QTableWidgetItem(t.password or "—")
            self._paint_password_cell(pw, t)
            self.table.setItem(row, 2, pw)
            # 状态列靠代理自绘，但代理需要从 UserRole 里取 Task，这里必须塞进去
            st = QTableWidgetItem("")
            st.setData(Qt.ItemDataRole.UserRole, t)
            if t.is_dir:
                st.setData(FOLDER_ROLE, self._folder_status(row))
            self.table.setItem(row, 3, st)

        self.topbar.set_count(len(self.tasks))
        self._refresh_stats()

    def _folder_status(self, row: int) -> tuple[str, str] | None:
        """文件夹汇总行显示什么（它自己不跑，看孩子们的进度）。

        用户原话："如果添加了文件夹进列表，在处理文件夹下的子任务时文件夹的状态
        说明应当是进行中而不是排队中"。以前它永远显示"◷ 排队中"（因为它自己是
        `runnable=False`，没人会改它的状态），看着像卡住了。
        """
        if not (0 <= row < len(self.tasks)):
            return None
        t = self.tasks[row]
        kids = [self.tasks[i] for i in t.children if 0 <= i < len(self.tasks)]
        kids = [k for k in kids if not k.is_dir]
        if not kids:
            # 索引可能因为"清除选中"而失效：退化成按 parent 现算一遍
            kids = [x for x in self.tasks if x.parent == row and not x.is_dir]
        if not kids:
            return ("◷  排队中", "text_faint")
        if any(k.status is Status.RUNNING for k in kids):
            return ("▶  进行中", "accent")
        done = [k for k in kids if k.status in (Status.DONE, Status.FAILED, Status.SKIPPED)]
        if len(done) < len(kids):
            return ("◷  排队中", "text_faint")
        bad = [k for k in kids if k.status is Status.FAILED]
        if bad:
            return (f"⚠  完成 {len(kids) - len(bad)}/{len(kids)}（{len(bad)} 个失败）", "warn")
        return (f"✔  完成 {len(kids)}/{len(kids)}", "ok")

    def _refresh_folder_rows(self) -> None:
        """孩子们的状态变了 → 把文件夹行的汇总状态重算一遍（不整表重建）。"""
        for row, t in enumerate(self.tasks):
            if not t.is_dir:
                continue
            cell = self.table.item(row, 3)
            if cell is not None:
                cell.setData(FOLDER_ROLE, self._folder_status(row))
        self.table.viewport().update()

    def _paint_password_cell(self, cell: QTableWidgetItem, t: Task) -> None:
        """密码格的统一上色规则：**有密码 = 主色（看着能点，点了复制）**，没有 = 淡灰。

        以前这条规则只写在 `_fill_table` 里，运行中刷新走的是另一条路（只 setText），
        于是同一列的密码有的绿有的白（用户拿截图来问过）。现在两边都走这个函数。
        """
        role = "accent" if t.password else "text_faint"
        cell.setForeground(QColor(self.theme.color(role)))

    def _build_table(self) -> QWidget:
        self.table = QTableWidget(0, 4)
        # 列名里带一句引导：密码格是可点的，光靠主色不够，得把话说出来
        self.table.setHorizontalHeaderLabels(["文件", "识别类型", "密码（点击复制）", "状态"])
        self.table.verticalHeader().setVisible(False)
        # 纵向分割线走 QSS 的 border-right（见 theme.py），不用 showGrid——
        # 那样连横线也一起出来，行间会显得很碎
        self.table.setShowGrid(False)
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        # 多选：这样「清除选中」才能一次去掉几行
        self.table.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setFocusPolicy(Qt.FocusPolicy.ClickFocus)
        self.table.verticalHeader().setDefaultSectionSize(34)

        hh = self.table.horizontalHeader()
        hh.setHighlightSections(False)
        # 列宽：全部 Interactive —— 能拖边界、双击边界按内容自适应（Qt 自带）。
        # 最后一列**不**交给 setStretchLastSection：那玩意只会"最后一列独自吃满"，
        # 你把前面的列拖宽，它就把最后一列压到最小、然后甩出一根横向滚动条
        # ——也就是"最右边那列被挤到看不见"。现在总宽由一个不变量管住：
        # 永远等于可视宽度（_spread/_fit_viewport），拖谁都是从相邻列身上匀，
        # 横向滚动条直接关掉，压根不会出现。
        for col in range(4):
            hh.setSectionResizeMode(col, QHeaderView.ResizeMode.Interactive)
        hh.setStretchLastSection(False)
        hh.setMinimumSectionSize(self.MIN_COL)
        self.table.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.col_defaults = [340, 190, 200, 320]
        self.apply_col_widths(self.col_defaults)
        # 双击表头 = 按内容自适应（自己接，别指望各平台行为一致）
        hh.installEventFilter(self)
        self.table.viewport().installEventFilter(self)   # 窗口缩放 → 重新匀一遍
        hh.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        hh.customContextMenuRequested.connect(self._header_menu)
        hh.sectionResized.connect(self._on_section_resized)
        self.table.setItemDelegateForColumn(3, StatusDelegate(self.theme, self.table))
        self.table.cellClicked.connect(self._on_cell_clicked)
        self.table.itemSelectionChanged.connect(self._on_selection_changed)
        return self.table

    # -- 列宽 ----------------------------------------------------------
    #
    # 不变量：sum(列宽) == 表格可视宽度。这样既不会空出一块，也不会长横向滚动条。
    # 拖动、双击自适应、窗口缩放都走同一套"匀"的逻辑。

    MIN_COL = 56
    MAX_COL = 1600

    def col_widths(self) -> list[int]:
        return [self.table.columnWidth(i) for i in range(self.table.columnCount())]

    def _available_width(self) -> int:
        return self.table.viewport().width()

    def apply_col_widths(self, widths) -> None:
        """按给定宽度设置列（越界/脏数据一律夹到合理区间），最后掰回可视宽度。

        设的过程中要关掉 _spread：否则"设第 1 列"会立刻从还停在旧值的第 2、3 列
        身上扣宽度，一条条设下来结果就面目全非（恢复上次列宽时最容易撞上）。
        """
        self._col_guard = True
        try:
            for i, w in enumerate(widths or []):
                if i >= self.table.columnCount():
                    break
                try:
                    width = int(w)
                except (TypeError, ValueError):
                    continue
                self.table.setColumnWidth(i, max(self.MIN_COL, min(self.MAX_COL, width)))
        finally:
            self._col_guard = False
        self._fit_viewport()

    def reset_col_widths(self) -> None:
        self.apply_col_widths(self.col_defaults)

    def fit_col_widths(self) -> None:
        """按内容自适应：每列都缩到刚好装下最长的那个单元格。"""
        self._col_guard = True
        try:
            for i in range(self.table.columnCount()):
                self.table.resizeColumnToContents(i)
        finally:
            self._col_guard = False
        self._fit_viewport()

    def _spread(self, index: int, delta: int) -> None:
        """index 列宽变了 delta：从别的列身上扣/补回来，总宽保持不变。

        先动右边的列（从紧挨着的那个开始），右边全顶到极限了再动左边的；
        还是不够就把这一列自己夹回来——宁可夹住当前这列，也不长出横向滚动条。
        """
        if not delta:
            return
        n = self.table.columnCount()
        order = [i for i in range(index + 1, n)] + [i for i in range(index - 1, -1, -1)]
        self._col_guard = True
        try:
            rest = int(delta)      # >0：还要从别的列扣掉这么多；<0：还要补给别的列
            for i in order:
                if not rest:
                    break
                cur = self.table.columnWidth(i)
                if rest > 0:
                    take = min(cur - self.MIN_COL, rest)
                    if take > 0:
                        self.table.setColumnWidth(i, cur - take)
                        rest -= take
                else:
                    give = min(self.MAX_COL - cur, -rest)
                    if give > 0:
                        self.table.setColumnWidth(i, cur + give)
                        rest += give
            if rest:
                cur = self.table.columnWidth(index)
                self.table.setColumnWidth(
                    index, max(self.MIN_COL, min(self.MAX_COL, cur - rest))
                )
        finally:
            self._col_guard = False

    def _on_section_resized(self, index: int, old: int, new: int) -> None:
        if self._col_guard:        # 自己匀出来的宽度，不要再匀一遍（会递归）
            return
        self._spread(index, new - old)

    def _fit_viewport(self) -> None:
        """把总宽掰回可视宽度：整体按比例缩放（拉宽、缩窄都是同一套）。

        为什么两个方向都按比例：只"缩右边、涨左列"的话，窗口来回缩放会让宽度
        一点点往左边的列上跑（左列越来越宽、右边几列贴到最小），这是在截图里看出来的。
        按比例缩放等于始终保住用户拉出来的**比例**，缩放窗口不改变观感。
        """
        n = self.table.columnCount()
        avail = self._available_width()
        if avail < n * self.MIN_COL:
            return                 # 还没布局出来（宽度是 0/几十像素），别乱动
        widths = self.col_widths()
        total = sum(widths)
        if total == avail or total <= 0:
            return
        scale = avail / total
        scaled = [max(self.MIN_COL, min(self.MAX_COL, int(round(w * scale))))
                  for w in widths]
        # 被 MIN/MAX 夹住的列少扣/少加的那部分，从右边的列再要一次，保证总和精确
        rest = avail - sum(scaled)
        for i in range(n - 1, -1, -1):
            if rest == 0:
                break
            if rest > 0:
                add = min(self.MAX_COL - scaled[i], rest)
            else:
                add = -min(scaled[i] - self.MIN_COL, -rest)
            scaled[i] += add
            rest -= add
        self._col_guard = True
        try:
            for i, w in enumerate(scaled):
                self.table.setColumnWidth(i, w)
        finally:
            self._col_guard = False

    def _header_need_widths(self) -> list[int]:
        """每一列"表头文字放得下"至少需要多宽（含左右留白）。"""
        hh = self.table.horizontalHeader()
        fm = QFontMetrics(hh.font())
        out = []
        for i in range(self.table.columnCount()):
            item = self.table.horizontalHeaderItem(i)
            text = item.text() if item is not None else ""
            out.append(fm.horizontalAdvance(text) + 24)
        return out

    def ensure_headers_fit(self) -> None:
        """保证**表头文字不被裁**：哪列不够就从最宽的列里匀给它。

        为什么要有这一步：列宽是按比例摊的（不变量是"总宽 == 可视宽"），
        窗口一窄，"密码（点击复制）"这种长表头就会先被裁掉——用户报的
        "默认大小下表头显示不全"就是这个。这里只做一件事：把不够的补齐、从最宽
        的列身上扣，扣的时候不许把任何列压到"它自己表头需要的最小宽"以下。
        """
        need = self._header_need_widths()
        widths = self.col_widths()
        short = [i for i in range(len(widths)) if widths[i] < need[i]]
        if not short:
            return
        deficit = sum(need[i] - widths[i] for i in short)
        order = sorted(range(len(widths)), key=lambda i: widths[i], reverse=True)
        taken = 0
        for i in order:
            if taken >= deficit:
                break
            room = widths[i] - max(self.MIN_COL, need[i])
            if room <= 0:
                continue
            take = min(room, deficit - taken)
            widths[i] -= take
            taken += take
        if taken <= 0:
            return
        # 把匀出来的按需分配（还不够就按比例少给一点，至少不会更糟）
        for i in short:
            give = min(need[i] - widths[i], taken)
            widths[i] += give
            taken -= give
            if taken <= 0:
                break
        self._col_guard = True
        try:
            for i, w in enumerate(widths):
                self.table.setColumnWidth(i, w)
        finally:
            self._col_guard = False

    def eventFilter(self, obj, event):  # noqa: N802 - Qt 命名
        hh = self.table.horizontalHeader()
        if obj is hh and event.type() == QEvent.Type.MouseButtonDblClick:
            col = hh.logicalIndexAt(event.position().toPoint())
            if col >= 0:
                self.table.resizeColumnToContents(col)
                self.ensure_headers_fit()
                self._fit_viewport()
                self.ensure_headers_fit()
                return True
        if obj is self.table.viewport() and event.type() == QEvent.Type.Resize:
            if not self._col_guard:
                self._fit_viewport()
                self.ensure_headers_fit()
        return super().eventFilter(obj, event)

    def header_menu(self, pos) -> QMenu:
        """表头右键菜单（普通软件都有的那两下）。单独成函数是为了能被测。"""
        hh = self.table.horizontalHeader()
        menu = QMenu(hh)
        col = hh.logicalIndexAt(pos)
        if col >= 0:
            act = menu.addAction(f"「{self.table.horizontalHeaderItem(col).text()}」按内容自适应")
            act.triggered.connect(lambda _c=False, i=col: self.table.resizeColumnToContents(i))
            menu.addSeparator()
        menu.addAction("全部按内容自适应", self.fit_col_widths)
        menu.addAction("恢复默认列宽", self.reset_col_widths)
        return menu

    def _header_menu(self, pos) -> None:
        self.header_menu(pos).exec(self.table.horizontalHeader().mapToGlobal(pos))

    def _on_cell_clicked(self, row: int, col: int) -> None:
        """点密码格 = 复制（详情抽屉删掉以后，它是唯一的"取密码"入口）。"""
        if col != 2 or row >= len(self.tasks):
            return
        task = self.tasks[row]
        if not task.password:
            return
        QApplication.clipboard().setText(task.password)
        self.append_log("剪贴板", f"已复制密码：{task.password}", "ok")

    def resizeEvent(self, event) -> None:      # noqa: N802 - Qt 命名
        super().resizeEvent(event)
        # 响应式：窗口窄了就把「清单操作」收成纯图标（tooltip 还在），
        # 免得那一行被挤爆、或者把路径框压到看不见
        self._apply_compact()
        self._dump_state()       # 尺寸写进状态文件，验证脚本好核对（见 _dump_state）

    def _apply_compact(self) -> None:
        """按**实测宽度**决定这一行要不要收成图标（分档压缩，保证不挤、不裁字）。

        以前的写法是"内部宽度 < 720 就收"——一个拍脑袋的常数。实测：
        这一行全带文字要 **802px**，而阈值只在窗口 < 752 时才收 → **760~834 这一带
        既没收、又放不下**：文字被裁、相邻按钮还会视觉上叠在一起
        （用户报的"默认大小下开始和暂停碰撞、添加文件夹显示不全"就是这个）。
        量法见 `tests/work/probe_layout.py`；这里在运行时按同样的方式现算：

          档 0：全带文字                     需要 ≈ 800px
          档 1：清单操作四个收成图标          需要 ≈ 660px
          档 2：暂停/停止也收（开始保留文字）  需要 ≈ 540px
          档 3：全收（开始也只剩图标）        需要 ≈ 330px

        收成图标时**补 tooltip**：平时这几个按钮不带悬浮说明（用户明确要求删掉），
        但只剩图标时没有提示就没人知道是干嘛的了。开始按钮任何时候都留 tooltip。
        """
        avail = self.width() - 24 - 24          # 卡片左右边距
        needs = [self._row_need(s) for s in range(4)]
        stage = 0
        for s in (0, 1, 2):
            if avail < needs[s]:
                stage = s + 1
        # 记下这次是怎么判的（测试与排错要看；一开始没记，档位算错时只能靠猜）
        self._row_decision = {"avail": avail, "needs": needs, "stage": stage}
        if stage == getattr(self, "_compact_stage", None):
            return
        self._compact_stage = stage
        self._compact = stage > 0               # 兼容旧引用（别处按这个判断窄了）

        self._set_btn_mode(self.btn_add, "添加文件", stage >= 1)
        self._set_btn_mode(self.btn_add_dir, "添加文件夹", stage >= 1)
        self._set_btn_mode(self.btn_clear_sel, "清除选中", stage >= 1)
        self._set_btn_mode(self.btn_clear, "清空列表", stage >= 1)
        self.btn_start.setText("" if stage >= 3 else ("继续" if self._paused else "开始"))
        self.btn_start.setToolTip("继续解压" if self._paused else "开始解压")
        self._set_btn_mode(self.btn_pause, "暂停", stage >= 2)
        self._set_btn_mode(self.btn_stop, "停止", stage >= 2)

    def _full_hint(self, btn, text: str) -> int:
        """按钮"带文字"时需要的宽度。

        **必须缓存**：收成图标之后按钮的 `sizeHint()` 就变小了，再拿它去算
        "全带文字要多少"会得到偏小的值 → 档位判错（实测：720 宽时该收没收，
        按钮被挤到互相重叠）。所以第一次量到就记下来，之后只用记下来的。
        """
        cached = self._hint_cache.get(id(btn))
        if cached:
            return cached
        # 没量过（例如启动时窗口就已经很窄）：按字体宽 + 图标 + 内边距估一个；
        # 宁可估大——估小了会重叠，估大了只是提前收成图标。
        fm = QFontMetrics(btn.font())
        guess = fm.horizontalAdvance(text) + 52
        self._hint_cache[id(btn)] = guess
        return guess

    def _set_btn_mode(self, btn, text: str, icon_only: bool) -> None:
        """把一个按钮在"图标+文字"和"纯图标"之间切换（收起来时补 tooltip）。"""
        if not icon_only:
            # 有文字时顺手把真实 sizeHint 记下来（比上面的估算准）
            self._hint_cache[id(btn)] = max(self._hint_cache.get(id(btn), 0),
                                            btn.sizeHint().width())
        btn.setText("" if icon_only else text)
        btn.setToolTip(text if icon_only else "")
        if icon_only:
            btn.setFixedWidth(38)
        else:
            btn.setMinimumWidth(0)
            btn.setMaximumWidth(16777215)

    def _row_need(self, stage: int) -> int:
        """这一行在某个档位下至少要多少宽（用缓存的"带文字宽度"现算，不写死）。"""

        def need(btn, text: str, icon_only: bool) -> int:
            return 38 if icon_only else self._full_hint(btn, text)

        total = need(self.btn_start, "开始", stage >= 3)
        total += need(self.btn_pause, "暂停", stage >= 2)      # "继续"更窄，按宽的算
        total += need(self.btn_stop, "停止", stage >= 2)
        total += need(self.btn_add, "添加文件", stage >= 1)
        total += need(self.btn_add_dir, "添加文件夹", stage >= 1)
        total += need(self.btn_clear_sel, "清除选中", stage >= 1)
        total += need(self.btn_clear, "清空列表", stage >= 1)
        return total + 8 * 6                    # 6 个间距

    def set_paused(self, paused: bool) -> None:
        """记录暂停状态：按钮可用性/文案、计时器、图标都跟着它走。

        注意：窄窗口下暂停按钮会收成纯图标，所以不能直接 setText——只记状态，
        再交给 `_apply_compact()` 按当前档位决定显示文字还是图标。
        """
        self._paused = bool(paused)
        set_btn_icon(self.btn_pause, "pause", size=15, role="text", theme=self.theme)
        self._compact_stage = None          # 强制重算档位（别被缓存挡住）
        self._apply_compact()
        self._sync_buttons()                # 「开始/继续」和「暂停」的互斥在这里生效
        self._sync_clock()                  # 暂停/继续时立刻把计时器状态刷对

    # ------------------------------------------------------------------
    # 选文件 / 拖拽
    # ------------------------------------------------------------------

    def pick_files(self) -> None:
        paths, _ = QFileDialog.getOpenFileNames(
            self, "选择压缩包", "", "压缩包 (*.zip *.rar *.7z *.tar *.gz *.001 *.z01 *.mp4);;所有文件 (*)"
        )
        if paths:
            self.add_paths(paths)

    def pick_dirs(self) -> None:
        d = QFileDialog.getExistingDirectory(self, "选择文件夹")
        if d:
            self.add_paths([d])

    @staticmethod
    def _urls_to_paths(event) -> list[str]:
        mime = event.mimeData()
        if not mime.hasUrls():
            return []
        return [u.toLocalFile() for u in mime.urls() if u.isLocalFile()]

    def dragEnterEvent(self, event) -> None:      # noqa: N802 - Qt 命名
        if self._urls_to_paths(event):
            event.acceptProposedAction()
        else:
            event.ignore()

    def dragMoveEvent(self, event) -> None:       # noqa: N802
        if self._urls_to_paths(event):
            event.acceptProposedAction()

    def dropEvent(self, event) -> None:           # noqa: N802
        paths = self._urls_to_paths(event)
        if not paths:
            event.ignore()
            return
        self.add_paths(paths)
        event.acceptProposedAction()
        self.sig_paths_added.emit()

    def _build_log(self) -> QWidget:
        f = card("Card")
        v = QVBoxLayout(f)
        v.setContentsMargins(12, 10, 12, 12)
        v.setSpacing(6)

        head = QHBoxLayout()
        head.addWidget(label("实时日志", "SectionTitle"))
        head.addStretch(1)
        # 结算结果现在写在这块日志里，所以"去哪找产物"的按钮也挂这儿：
        # 结论和出口在同一个地方，用户不用再找一遍。
        self.btn_open_out = QPushButton("打开输出目录")
        self.btn_open_out.setEnabled(False)
        self.btn_open_out.setToolTip("还没跑过任务")
        set_btn_icon(self.btn_open_out, "open-folder", size=16, role="text", theme=self.theme)
        self.btn_open_out.clicked.connect(lambda: self.open_output())
        head.addWidget(self.btn_open_out)
        self.log_badge = label("", "Faint")
        head.addWidget(self.log_badge)
        # 扫描走后台线程，界面上得有个"在忙"的提示，不然用户不知道点了有没有生效
        self.scan_badge = label("", "Faint")
        head.addWidget(self.scan_badge)
        v.addLayout(head)

        self.log = QPlainTextEdit()
        self.log.setObjectName("LogView")
        self.log.setReadOnly(True)
        self.log.setFont(mono_font())
        self.log.setFixedHeight(170)
        # 汇总行里带完整输出路径，按控件宽度折行比横向截断/拉滚动条好读
        self.log.setLineWrapMode(QPlainTextEdit.LineWrapMode.WidgetWidth)
        # 跟着最新一行滚（用户手动往上翻历史时自动松开，见 _log_follow）
        self._log_stick = True
        self._log_scrolling = False        # 区分"我自己滚的"和"用户滚的"
        self._log_range_dirty = False      # 范围刚变过 → 那次 valueChanged 不算用户滚动
        bar = self.log.verticalScrollBar()
        bar.valueChanged.connect(self._on_log_scrolled)
        # 内容变长时滚动条范围是**异步**更新的：只靠 append 那一刻 setValue(max) 会
        # 落在旧的 max 上（实测 791 行时 value 还停在 0）。范围一变就再跟一次。
        bar.rangeChanged.connect(self._on_log_range_changed)
        v.addWidget(self.log)
        return f

    def _build_footer(self) -> QWidget:
        f = card("Card")
        f.setFixedHeight(52)
        lay = QHBoxLayout(f)
        lay.setContentsMargins(16, 0, 16, 0)
        lay.setSpacing(14)

        lay.addWidget(label("总进度", "Dim"))
        self.overall = QProgressBar()
        self.overall.setTextVisible(False)
        self.overall.setFixedHeight(8)
        self.overall.setValue(0)
        lay.addWidget(self.overall, 1)
        self.overall_text = label("0%", "Mono")
        self.overall_text.setFixedWidth(44)
        lay.addWidget(self.overall_text)
        # 右下角就是一个**计时器**：跑起来每秒跳一次，跑完显示总用时。
        # （以前写死"已用 0s · 预计剩余 —"：没开始时也显示、估计又永远算不出来，
        #   用户看着莫名其妙；他说"放个计时器就好"。）
        self.eta = label("", "Mono")
        self.eta.setMinimumWidth(96)
        self.eta.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        lay.addWidget(self.eta)
        # 每秒刷新的时钟（只在运行且**没暂停**时转）
        self._clock = QTimer(self)
        self._clock.setInterval(1000)
        self._clock.timeout.connect(self._tick_clock)
        self._run_started = 0.0     # 本段计时的起点（暂停时清 0）
        self._elapsed = 0.0         # 已经累计的秒数（不含暂停的时间）
        return f

    # -- 右下角计时器 --------------------------------------------------
    #
    # 计时口径：**只算真正在跑的时间**。暂停时把这一段结清、表停住
    # （用户明确要求"暂停后计时器别走了"），继续时重新开一段。

    def start_clock(self) -> None:
        """开始计时（点「开始」时调用）。

        自动接着跑下一批时**不重置**：那还是同一次"用户点了开始"的过程，
        计时器归零会让用户以为前面那批白跑了。
        """
        if self._run_started and self._clock.isActive():
            return
        self._run_started = time.monotonic()
        self.eta.setText(f"用时 {fmt_seconds(self._elapsed) or '0s'}")
        self._clock.start()

    def stop_clock(self, *, keep_total: bool = True) -> None:
        """停表。`keep_total=True` 就把总用时留在界面上（跑完/停止都该看得见）。"""
        total = self._total_seconds()
        self._clock.stop()
        self._run_started = 0.0
        self._elapsed = total
        if keep_total:
            self.eta.setText(f"总用时 {fmt_seconds(total) or '0s'}")

    def reset_clock(self) -> None:
        """清空列表 / 还没开始跑时：右下角不显示任何时间。"""
        self._clock.stop()
        self._run_started = 0.0
        self._elapsed = 0.0
        self.eta.setText("")

    def _total_seconds(self) -> float:
        """到现在为止真正跑了多少秒（含正在跑的这一段）。"""
        live = (time.monotonic() - self._run_started) if self._run_started else 0.0
        return self._elapsed + live

    def _sync_clock(self) -> None:
        """按"在跑 / 暂停"把表的状态摆正（暂停 = 结清这一段并停表）。"""
        if not self.is_running:
            return
        if self._paused:
            if self._run_started:
                self._elapsed += time.monotonic() - self._run_started
                self._run_started = 0.0
            self._clock.stop()
            self.eta.setText(f"已暂停 · 用时 {fmt_seconds(self._elapsed) or '0s'}")
        else:
            if not self._run_started:
                self._run_started = time.monotonic()
            if not self._clock.isActive():
                self._clock.start()
            self.eta.setText(f"用时 {fmt_seconds(self._total_seconds()) or '0s'}")

    def _tick_clock(self) -> None:
        """每秒把"用时 Ns"刷新一次。"""
        if not self._run_started:
            return
        self.eta.setText(f"用时 {fmt_seconds(self._total_seconds()) or '0s'}")

    # ------------------------------------------------------------------
    # 结算：写进日志
    # ------------------------------------------------------------------

    def append_summary(self, items: list[Task]) -> None:
        """把这次的结果写成几行日志——原来那张「完成汇总」页就干这个。

        为什么换掉：那一页要把 文件/类型/输出/密码/说明 排成五列，列宽得手算，
        任务一多、尤其一出现失败项，行与行之间就开始压字（反馈过两次）。
        日志天生是逐行流，压不着；而且顺着往上滚就能看历史，比翻页有用。
        """
        leaves = [t for t in items if t.runnable]
        done = [t for t in leaves if t.status is Status.DONE]
        failed = [t for t in leaves if t.status is Status.FAILED]
        skipped = [t for t in leaves if t.status is Status.SKIPPED]
        used = sum(t.elapsed for t in leaves)

        self.append_log(
            "汇总",
            f"{len(leaves)} 项：成功 {len(done)} · 失败 {len(failed)} · 跳过 {len(skipped)}"
            + (f" · 用时 {fmt_seconds(used)}" if used else ""),
            "ok" if not (failed or skipped) else "warn",
        )
        for t in leaves:
            # 明细行的左格留空：它们挂在上面那条「汇总」下面，是同一段的一部分，
            # 每行都再盖一个"汇总"反而糊成一片
            if t.status is Status.DONE:
                bits = []
                out = t.output_top or t.output
                if out:
                    bits.append(f"输出 {out}")
                if t.password:
                    bits.append(f"密码 {t.password}")
                if t.layer:
                    bits.append(f"{t.layer} 层")
                if t.elapsed:
                    bits.append(fmt_seconds(t.elapsed))
                self.append_log(
                    "",
                    f"✔  {t.name}" + ("  ·  " + " · ".join(bits) if bits else ""),
                    "ok",
                )
            else:
                mark, color = ("✘", "err") if t.status is Status.FAILED else ("⚠", "warn")
                why = t.note or t.stop_reason or "未完成"
                self.append_log("", f"{mark}  {t.name}  —  {why}", color)

    # ------------------------------------------------------------------
    # 打开输出目录
    # ------------------------------------------------------------------

    def update_open_button(self, items: list[Task]) -> None:
        """决定「打开输出目录」指向哪儿（结论由 core.pipeline 算）。

        规则很简单，三种情况：

          * 这次没产出     → 灰掉，tooltip 说清楚；
          * 只有一个产物目录 → 直接打开它；
          * 多个产物目录   → 拉菜单。来源是多个文件夹/多个包时就落在这里，
                            菜单第一项是它们的公共上级（有意义时），后面逐条列。

        特意不做"永远打开某一个"的猜测：猜错了用户会以为解压跑丢了。
        """
        dirs = output_dirs(items)
        root = output_root(dirs)
        self._open_dirs = dirs
        self._open_root = root or (dirs[0] if dirs else "")

        old = self.btn_open_out.menu()
        if old is not None:
            self.btn_open_out.setMenu(None)
            old.deleteLater()

        if not dirs:
            self.btn_open_out.setEnabled(False)
            self.btn_open_out.setToolTip("这次没有产生输出目录")
            return
        if len(dirs) == 1:
            self.btn_open_out.setEnabled(True)
            self.btn_open_out.setToolTip(dirs[0])
            return

        menu = QMenu(self.btn_open_out)
        menu.setToolTipsVisible(True)
        if root:
            act = menu.addAction(f"全部（{len(dirs)} 个目录的上级）")
            act.setToolTip(root)
            act.triggered.connect(lambda _c=False, p=root: self.open_output(p))
            menu.addSeparator()
        for d in dirs:
            act = menu.addAction(d)
            act.setToolTip(d)
            act.triggered.connect(lambda _c=False, p=d: self.open_output(p))
        self.btn_open_out.setMenu(menu)
        self.btn_open_out.setEnabled(True)
        self.btn_open_out.setToolTip(f"{len(dirs)} 个输出目录，点开选择")

    def reset_open_button(self) -> None:
        """清空清单时把按钮退回初始态（别让人点开上一批的产物目录）。"""
        self.update_open_button([])
        self.btn_open_out.setToolTip("还没跑过任务")

    def open_output(self, path: str = "") -> None:
        """在资源管理器里打开一个输出目录；不传 path 就打开当前认定的那个。"""
        target = path or self._open_root
        if not target or not os.path.isdir(target):
            self.append_log("错误", f"目录不存在：{target or '（这次没有产物）'}", "err")
            return
        try:
            os.startfile(target)  # noqa: S606 - Windows 专用，自用工具
        except OSError as exc:
            self.append_log("错误", f"打不开 {target}：{exc}", "err")

    # ------------------------------------------------------------------
    # 日志
    # ------------------------------------------------------------------

    def log_line(self, message: str) -> None:
        """给 core 的日志回调用的入口：自动打时间戳并按内容着色。"""
        msg = message.rstrip()
        if not msg:
            return
        color = "text_dim"
        low = msg
        if low.startswith("✔") or "完成" in low:
            color = "ok"
        elif low.startswith("✘") or "失败" in low or "错误" in low:
            color = "err"
        elif low.startswith("▶") or "命中" in low or "第" in low and "层" in low:
            color = "info"
        elif low.startswith("⚠") or "跳过" in low:
            color = "warn"
        self.append_log(time.strftime("%H:%M:%S"), msg, color)

    def set_log_badge(self, text: str) -> None:
        self.log_badge.setText(text)

    def append_log(self, tag: str, msg: str, color: str = "text_dim",
                   level: str = "info") -> None:
        """日志每行 = 「左边一格 + 正文」，颜色在渲染时才写进 HTML。

        第一格原来拆成 `ts` / `who` 两个参数，但实际调用几乎全是
        `append_log("系统", "开始了", "info")` 这种三元组 —— 于是**颜色名被当成正文**
        写进日志尾巴（"…用时 1.8s warn"、"…已保存 ok"）。既然那一格从来只有
        "谁在说话"（时间戳或 系统/扫描/汇总 这类标签），就只留一个参数，
        参数错位这类 bug 从此无处可生。

        渲染是**增量**的：解压穿透时会一口气来几十上百行日志，以前每行都把整个面板
        clear + 重新 appendHtml 一遍（800 行 × 每行一次 ≈ O(n²)），界面直接卡住
        ——用户看到的就是"输完密码界面无响应"。现在每行只 append 自己那一行。

        `level="debug"` 的行**只写进 logs/run.log，不进界面**（带 `[详细]` 前缀）：
        引擎的命令行、每次密码尝试、原始输出都属这一类——一次试密码能刷上百行，
        命令行里还带着 `-p<密码>`（真出过事故）。界面上保留的是"心跳、每层结果、
        密码命中、挂起/恢复"这些该知道的。
        """
        if level == "debug":
            if self._runlog is not None:
                self._runlog.write(tag, f"[详细] {msg}")
            return
        self._log_entries.append((tag, msg, color))
        self._flush_log()
        # 面板里出现过的内容同时落盘到 logs/run.log（带轮转、密码打码）：
        # 用户的理解是"日志文件应该记下日志窗口显示的东西"——ui.log 只管启动/异常。
        # 写文件失败绝不能影响界面，所以 RunLog 内部一律静默。
        if self._runlog is not None:
            self._runlog.write(tag, msg)

    def _flush_log(self) -> None:
        """把还没画出来的日志补上（只 append 新增的那几行，不重画整段）。"""
        drop = max(0, len(self._log_entries) - MAX_LOG_ENTRIES)
        if drop:
            del self._log_entries[:drop]
            self._drop_log_lines(drop)
            # 前面被砍掉几行，"画到第几行"这个游标也要跟着往前挪，
            # 否则它会一直等于 800，后面新来的行全都画不出来（实测面板反而越用越空）
            self._log_rendered = max(0, self._log_rendered - drop)
        if self._log_rendered > len(self._log_entries):
            self._log_rendered = 0           # 被 clear() 或换主题整段重画过
        if self._log_rendered == 0:
            self.log.clear()
        c = self.theme.c
        for tag, msg, color in self._log_entries[self._log_rendered:]:
            self.log.appendHtml(
                f'<span style="color:{c["text_faint"]}">{tag}</span>'
                f'&nbsp;&nbsp;<span style="color:{c.get(color, c["text_dim"])}">{msg}</span>'
            )
        self._log_rendered = len(self._log_entries)
        self._log_follow()

    def _log_at_bottom(self) -> bool:
        bar = self.log.verticalScrollBar()
        return bar.value() >= bar.maximum() - 4

    def _on_log_scrolled(self, _value: int) -> None:
        """用户自己滚回底部 → 重新开始跟随；往上翻 → 先别打扰他。

        两道过滤，缺一不可：
          * `_log_scrolling`：`_log_follow` 自己 setValue 引发的，不算用户滚动；
          * `_log_range_dirty`：**范围刚变过**的这一次也不算——内容变多、窗口变大
            都会让 Qt 挪一下 value 并发这个信号，把它当成"用户往上翻了"就会
            永久关掉跟随（实测：791 行时 value 停在 0、stick 变 False 就是这么来的）。
        """
        if self._log_scrolling or self._log_range_dirty:
            return
        self._log_stick = self._log_at_bottom()

    def _on_log_range_changed(self, _lo: int, _hi: int) -> None:
        """滚动条范围变了：这是"内容/控件尺寸变了"，不是用户在滚。"""
        self._log_range_dirty = True
        self._log_follow()
        QTimer.singleShot(0, self._clear_log_range_dirty)

    def _clear_log_range_dirty(self) -> None:
        self._log_range_dirty = False

    def _log_follow(self) -> None:
        """把日志面板滚到最新一行——**只在用户本来就待在底部时**才跟。

        以前这里只有一句 `ensureCursorVisible()`：光标从来没被挪到文末，
        所以它等于什么都没做，面板会一直停在最上面（用户报的"日志不会滚到最底部"）。
        现在显式把光标移到文末再滚，同时用"本来在不在底部"决定要不要跟：
        用户手动往回翻看历史时不会每来一行就被拽回底部。
        """
        if not self._log_stick:
            return
        bar = self.log.verticalScrollBar()
        self._log_scrolling = True         # 我自己的滚动不算"用户翻历史"
        try:
            cur = self.log.textCursor()
            cur.movePosition(cur.MoveOperation.End)
            self.log.setTextCursor(cur)
            bar.setValue(bar.maximum())
        finally:
            self._log_scrolling = False

    def _drop_log_lines(self, count: int) -> None:
        """面板最上面那 count 行跟着滚掉（超出上限时），别让控件无限长。

        选中范围要从"本行开头"到"下一行开头"（NextBlock + KeepAnchor），这样删掉的
        正好是一行 + 它的换行符；用 BlockUnderCursor 再补一个 deleteChar 会多吃掉
        下一行的第一个字符（实测：掉 1 行时面板少 2 行）。

        注意：这里会把**文本光标挪到开头**（否则删不掉最上面那几行），那一下会被
        Qt 当成"滚动位置变了"。如果不屏蔽，`_on_log_scrolled` 就会以为用户往上翻、
        把自动跟随关掉——正是"日志不会滚到最底部"的另一个成因（只在超过 800 行、
        开始滚掉旧行之后才出现，所以很容易漏掉）。
        """
        self._log_scrolling = True
        try:
            cursor = self.log.textCursor()
            cursor.movePosition(QTextCursor.MoveOperation.Start)
            for _ in range(count):
                cursor.movePosition(QTextCursor.MoveOperation.NextBlock,
                                    QTextCursor.MoveMode.KeepAnchor)
                cursor.removeSelectedText()
            self.log.setTextCursor(cursor)
        finally:
            self._log_scrolling = False
        self._log_follow()          # 砍掉最上面几行之后继续贴底

    def _render_log(self, *, scroll: bool = False, full: bool = True) -> None:
        """整段重画。换主题要调它（颜色是写进 HTML 的），补日志走 _flush_log。"""
        if not full:
            self._flush_log()
            return
        self._log_rendered = 0
        self._flush_log()
        if scroll:
            self._log_stick = True
            self._log_follow()

    # ------------------------------------------------------------------
    def _refresh_stats(self) -> None:
        counts = summarize(self.tasks)
        for key, lb in self.stats.items():
            if key == "queued":
                lb.setText(str(counts.get("queued", 0)))
            elif key == "running":
                lb.setText(str(counts.get("running", 0)))
            elif key == "done":
                lb.setText(str(counts.get("done", 0)))
            elif key == "failed":
                # 失败计数把「已跳过」也算进去，否则跳过的东西看着像凭空消失
                lb.setText(str(counts.get("failed", 0) + counts.get("skipped", 0)))



# ==========================================================================
# 手动输入密码弹窗
# ==========================================================================


def _dwm_round(hwnd: int) -> None:
    """Win11：让 DWM 把这个窗口的四个角磨圆（主窗口与密码弹窗共用）。

    **为什么不用 `WA_TranslucentBackground` + 自绘圆角**：半透明表面上 Qt 只能用
    灰度抗锯齿画字，文字会明显发糊（用户反馈的正是"字糊"）。DWM 圆角是系统画的，
    窗口依旧不透明 → 文字还是 ClearType，两全。
    """
    if os.name != "nt" or not hwnd:
        return
    try:
        value = ctypes.c_int(2)              # DWMWCP_ROUND
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            wintypes.HWND(hwnd), 33, ctypes.byref(value), ctypes.sizeof(value),
        )
    except Exception:                        # noqa: BLE001 - 老系统没这个属性，忽略
        pass


class _VerifyJob(QThread):
    """后台跑一次密码验证。

    为什么必须挪出界面线程：验证就是 `7z t`，一个 4.58 GB 的包**整包测一遍实测 27.4s**
    （而错密码只要 0.03s，所以"只有输对了才卡"）。放在界面线程上，弹窗会僵死半分钟，
    用户以为程序挂了。
    """

    sig_done = Signal(bool, str)          # (对不对, 出错信息)

    def __init__(self, verifier, password: str, parent=None) -> None:
        super().__init__(parent)
        self._verifier = verifier
        self._password = password

    def run(self) -> None:                # noqa: D102 - QThread 约定的入口
        try:
            self.sig_done.emit(bool(self._verifier(self._password)), "")
        except Exception as exc:          # noqa: BLE001 - 什么错都要让用户看见
            self.sig_done.emit(False, repr(exc))


class PasswordDialog(QDialog):
    """密码本全部试完后的手动干预：只干一件事——让用户把密码输进来。

    两个出口，语义互斥、不会让人猜：

      * **验证** —— 拿真引擎试一次。对了就继续解压（并把密码交回工作线程，
        解压成功后由管道写进密码本）；错了什么也不做，停在原地让你改。
      * **跳过当前文件** —— 这个包不弄了，继续下一个。

    以前这里有第三个按钮「继续解压」，效果和「跳过」一模一样（都是跳过当前文件），
    摆两个含义相同的按钮只会让人犹豫；说明文字也砍到只剩"这是什么包"。
    """

    def __init__(
        self,
        theme: Theme,
        task_name: str,
        *,
        verifier=None,
        parent=None,
    ) -> None:
        super().__init__(parent)
        self.setWindowTitle("输入密码")
        self.setModal(True)
        # 跟主窗口一样去掉系统标题栏：无边框 + DWM 圆角（不透明，字才不糊），
        # 自己画一个"标题 + ✕"，标题那一条还能拖着走（见 mousePressEvent）。
        self.setWindowFlag(Qt.WindowType.FramelessWindowHint, True)
        self.setFixedWidth(460)
        self.setObjectName("Dialog")
        self.theme = theme
        self.verifier = verifier
        self.accepted_password: str | None = None
        self.verified = False
        self._job: _VerifyJob | None = None
        self._t0 = 0.0
        self._closed = False
        self._drag_zone = QRect()          # 可拖动的区域（标题条），showEvent 里算

        v = QVBoxLayout(self)
        v.setContentsMargins(22, 14, 22, 18)
        v.setSpacing(12)

        # 自绘标题条：左边标题、右边关闭（跟主窗口顶栏一个调子）
        head = QHBoxLayout()
        head.setSpacing(8)
        self.lbl_title = label("需要密码", "Title")
        head.addWidget(self.lbl_title)
        head.addStretch(1)
        self.btn_close = QPushButton()
        self.btn_close.setObjectName("WinBtn")
        self.btn_close.setFixedSize(30, 26)
        self.btn_close.setToolTip("关闭")
        set_btn_icon(self.btn_close, "close", size=15, role="text_dim", theme=self.theme)
        self.btn_close.clicked.connect(self.reject)
        head.addWidget(self.btn_close)
        v.addLayout(head)

        v.addWidget(self._kv("包", task_name))

        row = QHBoxLayout()
        row.setSpacing(8)
        self.edit = QLineEdit()
        # 明文显示：这个工具从表到密码本全程不打码，输入框打点只会让人
        # 打错了自己看不见（"取消打码"那轮的漏网之鱼）
        self.edit.setEchoMode(QLineEdit.EchoMode.Normal)
        self.edit.setPlaceholderText("输入解压密码")
        self.edit.setMinimumHeight(34)
        btn_test = QPushButton("验证")
        btn_test.setObjectName("Primary")
        btn_test.setFixedHeight(34)
        btn_test.setMinimumWidth(88)
        row.addWidget(self.edit, 1)
        row.addWidget(btn_test)
        v.addLayout(row)

        self.hint = label("", "Faint")
        v.addWidget(self.hint)

        btns = QHBoxLayout()
        btns.addStretch(1)
        b_skip = QPushButton("跳过当前文件")
        btns.addWidget(b_skip)
        v.addLayout(btns)

        # 回车要落在「验证」上：以前没指定默认按钮，焦点不在输入框时回车会触发
        # **第一个** autoDefault 按钮 —— 实测落在「跳过当前文件」上，非常反直觉。
        btn_test.setDefault(True)
        btn_test.setAutoDefault(True)
        b_skip.setAutoDefault(False)
        self.btn_close.setAutoDefault(False)
        b_skip.clicked.connect(self.reject)
        btn_test.clicked.connect(self._verify)
        self.edit.returnPressed.connect(self._verify)
        self.btn_test = btn_test
        self.btn_skip = b_skip

    def _kv(self, k: str, val: str, color: str = "text") -> QWidget:
        w = QWidget()
        h = QHBoxLayout(w)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(10)
        kk = label(k, "Faint")
        kk.setFixedWidth(46)
        h.addWidget(kk)
        vv = label(val, wrap=True)
        if color != "text":
            vv.setStyleSheet(f"color:{self.theme.color(color)};")
        h.addWidget(vv, 1)
        return w

    # -- 无边框窗口：圆角 + 拖标题条移动 ----------------------------------

    def showEvent(self, event) -> None:      # noqa: N802 - Qt 命名
        super().showEvent(event)
        hwnd = int(self.winId())
        _dwm_round(hwnd)                     # 跟主窗口同一套圆角（DWM，不透明，字不糊）
        hwnd_ = self.windowHandle()
        if hwnd_ is not None:
            hwnd_.setFlag(Qt.WindowType.WindowContextHelpButtonHint, False)
        # 可拖动区域 = 标题条那条横带（✕ 按钮自己收点击，不算）
        self._drag_zone = QRect(0, 0, self.width(), self.lbl_title.height() + 16)

    def _in_drag_zone(self, pos) -> bool:
        """这一点算不算"标题条"（能拖着窗口走）。单独成函数是为了能被测。"""
        if not self._drag_zone.isValid():
            return pos.y() <= (self.lbl_title.height() + 16)
        return self._drag_zone.contains(pos)

    def mousePressEvent(self, event) -> None:      # noqa: N802
        if event.button() == Qt.MouseButton.LeftButton:
            pos = event.position().toPoint()
            child = self.childAt(pos)
            handle = self.windowHandle()
            if (self._in_drag_zone(pos) and not isinstance(child, QAbstractButton)
                    and handle is not None and handle.startSystemMove()):
                event.accept()
                return
        super().mousePressEvent(event)

    def _say(self, text: str, color: str) -> None:
        # 注：这个标签叫 hint 而不是 result——result 是 QDialog 自己的方法名，
        # 覆盖它会让 dlg.result() 变成拿一个 QLabel 来调用，迟早出事。
        self.hint.setText(text)
        self.hint.setStyleSheet(f"color:{self.theme.color(color)};")

    def _verify(self) -> None:
        """真去验证：调引擎 test，不假装成功。

        验证通过 → 直接继续解压（用户按的就是"验证"，不再要求他再点一次别的）；
        没通过 → 只留一句错话，什么都不做。

        **验证跑在后台线程**：整包 `7z t` 对大包要几十秒（4.58GB 实测 27.4s），
        以前这一步在界面线程上，弹窗直接僵死半分钟——用户以为是程序挂了。
        所以这里立刻返回、界面照常响应（「跳过当前文件」随时能点）。

        注意：**不在这里写密码本**。写进去的时机是"解压真的成功了"，
        由管道负责（见 pierce._extract_layer）。
        """
        pw = self.edit.text()
        if not pw:
            self._say("✘ 请先输入密码", "warn")
            return
        if self.verifier is None:
            self._say("（未接引擎，无法验证）", "text_dim")
            return
        if self._job is not None:
            return                       # 已经在验证了，别叠加

        self.btn_test.setEnabled(False)
        self._say("验证中…（大体积文件可能需要较长时间）", "text_dim")
        self._t0 = time.monotonic()
        job = _VerifyJob(self.verifier, pw, self)
        job.sig_done.connect(lambda ok, err, p=pw: self._verified(p, ok, err))
        self._job = job
        job.start()

    def reject(self) -> None:      # noqa: N802 - Qt 命名
        """用户点了「跳过当前文件」/按了 Esc/关了窗：记一笔，验证回来时别再动界面。"""
        self._closed = True
        super().reject()

    def _verified(self, pw: str, ok: bool, err: str) -> None:
        self._job = None
        used = time.monotonic() - self._t0
        if self._closed:
            return                       # 验证期间用户已经点了「跳过」，别再动这个弹窗
        self.btn_test.setEnabled(True)
        if err:
            self._say(f"✘ 验证出错：{err}", "err")
            return
        self.verified = bool(ok)
        if ok:
            self._say(f"✔ 密码正确（{used:.1f}s），继续解压", "ok")
            self.accepted_password = pw
            self.accept()
        else:
            self._say(f"✘ 密码不对（{used:.1f}s），改一下再试", "err")


# ==========================================================================
# 密码本页面
# ==========================================================================


class LibraryPage(ThemedMixin, QWidget):
    sig_back = Signal()

    def __init__(self, theme: Theme, parent=None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.vault: PasswordVault | None = None

        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(12)

        head = card("Card")
        head.setFixedHeight(52)
        hl = QHBoxLayout(head)
        hl.setContentsMargins(12, 0, 12, 0)
        hl.setSpacing(10)
        b = QPushButton("← 返回")
        b.setObjectName("Ghost")
        b.clicked.connect(self.sig_back.emit)
        hl.addWidget(b)
        hl.addWidget(label("密码本", "Title"))
        hl.addStretch(1)
        self.summary_label = label("", "Faint")
        hl.addWidget(self.summary_label)
        v.addWidget(head)

        body = card("Card")
        bl = QVBoxLayout(body)
        bl.setContentsMargins(16, 14, 16, 14)
        bl.setSpacing(10)

        top = QHBoxLayout()
        top.setSpacing(8)
        self.ed_search = QLineEdit()
        self.ed_search.setPlaceholderText("搜索密码…")
        self.ed_search.setMinimumHeight(32)
        self.ed_search.textChanged.connect(lambda _t: self._reload_rows())
        top.addWidget(self.ed_search, 1)
        reload_btn = QPushButton("重新载入")
        reload_btn.clicked.connect(self.reload)
        top.addWidget(reload_btn)
        bl.addLayout(top)

        add_row = QHBoxLayout()
        add_row.setSpacing(8)
        self.ed_new = QLineEdit()
        self.ed_new.setPlaceholderText("输入要添加的密码，回车即可")
        self.ed_new.setMinimumHeight(32)
        self.ed_new.returnPressed.connect(self._add_password)
        add_row.addWidget(self.ed_new, 1)
        add_btn = QPushButton("添加")
        add_btn.clicked.connect(self._add_password)
        add_row.addWidget(add_btn)
        self.btn_del = QPushButton("删除选中")
        self.btn_del.setToolTip("可以多选：Ctrl 点选、Shift 连选、鼠标框选，或按 Delete")
        self.btn_del.clicked.connect(self._remove_selected)
        add_row.addWidget(self.btn_del)
        bl.addLayout(add_row)

        self.table = QTableWidget(0, 2)
        self.table.setHorizontalHeaderLabels(["密码", "成功次数"])
        self.table.verticalHeader().setVisible(False)
        self.table.setShowGrid(False)
        self.table.setAlternatingRowColors(True)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        # 多选（Ctrl 点选 / Shift 连选 / 拖动框选），配合「删除选中」一次删一批
        self.table.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        hh = self.table.horizontalHeader()
        hh.setHighlightSections(False)
        for col in range(2):
            hh.setSectionResizeMode(col, QHeaderView.ResizeMode.Interactive)
        hh.setStretchLastSection(True)
        hh.setMinimumSectionSize(56)
        self.table.setColumnWidth(0, 420)
        self.table.setColumnWidth(1, 120)
        # 键盘 Delete 也能删（删之前照样要确认，删错了可没法撤销）
        shortcut = QShortcut(QKeySequence.StandardKey.Delete, self.table)
        shortcut.setContext(Qt.ShortcutContext.WidgetShortcut)
        shortcut.activated.connect(self._remove_selected)
        bl.addWidget(self.table, 1)

        bl.addWidget(label("尝试顺序：文件名 → 本表（成功次数多优先）→ 空密码，全不中才弹窗", "Faint"))

        self.file_label = label("", "Faint")
        bl.addWidget(self.file_label)
        v.addWidget(body, 1)

        # 命中次数那列的颜色是代码里给的（不是 QSS），换主题要重刷一遍表格
        self._theme_hook(self._reload_rows)
        self._theme_hook(self._recolor_summary)

    # ------------------------------------------------------------------
    # 真数据
    # ------------------------------------------------------------------

    def set_vault(self, vault: PasswordVault) -> None:
        self.vault = vault
        self.reload()

    def reload(self) -> None:
        """从磁盘重读（密码本可能在外部被编辑过）。"""
        if self.vault is None:
            return
        self.vault.reload()
        if self.vault.book_path:
            self.file_label.setText(f"文件：{self.vault.book_path}")
        self._reload_rows()

    def _reload_rows(self) -> None:
        if self.vault is None:
            return
        keyword = self.ed_search.text().strip()
        # 显示顺序 = 尝试顺序，所见即所试
        ordered = sort_entries(self.vault.entries)
        rows = [e for e in ordered if not keyword or keyword in e.password]

        self.table.setRowCount(len(rows))
        for r, entry in enumerate(rows):
            shown = entry.password or "（空密码）"
            cell = QTableWidgetItem(shown)
            cell.setData(Qt.ItemDataRole.UserRole, entry.password)   # 删除时用真值
            self.table.setItem(r, 0, cell)

            hits = QTableWidgetItem(f"{entry.hits} 次" if entry.hits else "—")
            if entry.hits:
                hits.setForeground(QColor(self.theme.color("ok")))
            self.table.setItem(r, 1, hits)

        self.summary_label.setText(f"共 {len(self.vault.entries)} 个密码")
        self.summary_label.setStyleSheet("")
        self._summary_role = ""

    def _recolor_summary(self) -> None:
        """把标题栏右侧那句反馈按当前主题重新上色（_flash 之后换主题的情况）。"""
        role = getattr(self, "_summary_role", "")
        self.summary_label.setStyleSheet(
            f"color:{self.theme.color(role)};" if role else ""
        )

    # -- 编辑 ----------------------------------------------------------

    def _add_password(self) -> None:
        if self.vault is None:
            return
        pw = self.ed_new.text().strip()
        if not pw:
            self._flash(False, "先在框里输入密码")
            return
        if self.vault.find(pw):
            self.ed_new.clear()
            self._flash(False, "这个密码已经在表里了")
            return
        ok = self.vault.add(pw)
        self.ed_new.clear()
        self.reload()          # 先刷新，再报话（reload 会把提示行改写成"共 N 个密码"）
        self._flash(ok, f"已添加：{pw}" if ok else f"写入失败：{self.vault.last_write_error}")

    def _selected_passwords(self) -> list[str]:
        """当前选中的行 → 真实密码值（多选顺序按行号，去重）。

        空密码那一行的 data 是空串而不是 None，所以判"有没有取到"只能看 None：
        以前写成 `data(UserRole) or cell.text()`，空密码就退化成拿显示文案
        「（空密码）」去删，永远删不掉。
        """
        model = self.table.selectionModel()
        if model is None:
            return []
        out: list[str] = []
        for index in sorted(model.selectedRows(), key=lambda i: i.row()):
            cell = self.table.item(index.row(), 0)
            if cell is None:
                continue
            raw = cell.data(Qt.ItemDataRole.UserRole)
            out.append(cell.text() if raw is None else str(raw))
        return list(dict.fromkeys(out))

    def _remove_selected(self) -> None:
        """删除选中（支持多选，一次删一批）。删除前必须确认。

        密码是**攒出来的资产**（成功次数就是它的价值），误删一条得重新靠解压碰回来，
        所以一次删几条都要先说清楚删的是哪几条。
        """
        if self.vault is None:
            return
        pws = self._selected_passwords()
        if not pws:
            self._flash(False, "先选中要删的行（Ctrl / Shift 可以多选）")
            return

        shown = [pw if pw else "（空密码）" for pw in pws]
        if len(pws) == 1:
            question = f"确定从密码本里删除「{shown[0]}」吗？"
        else:
            head = "、".join(shown[:5]) + ("…" if len(shown) > 5 else "")
            question = f"确定从密码本里删除这 {len(pws)} 条吗？\n\n{head}"
        ok = QMessageBox.question(
            self,
            f"删除 {len(pws)} 条密码" if len(pws) > 1 else "删除密码",
            question,
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if ok != QMessageBox.StandardButton.Yes:
            self._flash(True, "已取消")
            return

        removed = self.vault.remove_many(pws)
        # 先 reload 再 flash：reload 会把这行字刷成"共 N 个密码"，
        # 顺序反了就等于用户永远看不到"删了什么"（加密码那条路也一样）
        self.reload()
        if removed == len(pws):
            self._flash(True, f"已删除 {removed} 条" if removed > 1 else f"已删除：{shown[0]}")
        elif removed:
            self._flash(True, f"删了 {removed} 条，另有 {len(pws) - removed} 条没找到")
        else:
            self._flash(False, "删除失败（密码本写不进去？）")

    def _flash(self, ok: bool, text: str) -> None:
        """标题栏右侧给一句即时反馈（成功失败都说话，别静默）。"""
        self.summary_label.setText(text)
        self._summary_role = "ok" if ok else "err"
        self._recolor_summary()
        QTimer.singleShot(2500, self._reload_rows)



# ==========================================================================
# 设置页面
# ==========================================================================


class SettingsPage(ThemedMixin, QWidget):
    sig_back = Signal()
    sig_save = Signal()
    sig_shell_install = Signal()
    sig_shell_remove = Signal()

    def __init__(self, theme: Theme, parent=None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.path_edits: dict[str, QLineEdit] = {}
        self.path_marks: dict[str, QLabel] = {}
        self._mark_state: dict[str, bool] = {}
        self.checkboxes: dict[str, QCheckBox] = {}
        self.theme_radios: list[QRadioButton] = []

        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(12)

        head = card("Card")
        head.setFixedHeight(52)
        hl = QHBoxLayout(head)
        hl.setContentsMargins(12, 0, 12, 0)
        hl.setSpacing(10)
        b = QPushButton("← 返回")
        b.setObjectName("Ghost")
        b.clicked.connect(self.sig_back.emit)
        hl.addWidget(b)
        hl.addWidget(label("设置", "Title"))
        hl.addStretch(1)
        outer.addWidget(head)

        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        inner = QWidget()
        scroll.setWidget(inner)
        v = QVBoxLayout(inner)
        v.setContentsMargins(0, 0, 6, 0)
        v.setSpacing(12)

        # 引擎
        c1 = card("Card")
        a = QVBoxLayout(c1)
        a.setContentsMargins(16, 14, 16, 14)
        a.setSpacing(9)
        a.addWidget(label("引擎", "SectionTitle"))
        a.addLayout(self._path_row("WinRAR 路径", r"C:\Program Files\WinRAR\Rar.exe", True))
        a.addLayout(self._path_row("7-Zip 路径", r"C:\Program Files\7-Zip\7z.exe", True))
        # 只在**出问题**时才说话（用系统那份 / 找不到）：一切正常时不占地方
        # （用户明确要求把这类说明文字删掉）
        self.zip_note = label("", "Hint")
        self.zip_note.setWordWrap(True)
        self.zip_note.setVisible(False)
        a.addWidget(self.zip_note)
        row = QHBoxLayout()
        detect = QPushButton("重新检测")
        detect.clicked.connect(self._detect_engines)
        row.addWidget(detect)
        row.addStretch(1)
        a.addLayout(row)
        v.addWidget(c1)

        # 解压行为
        c2 = card("Card")
        b2 = QVBoxLayout(c2)
        b2.setContentsMargins(16, 14, 16, 14)
        b2.setSpacing(9)
        b2.addWidget(label("解压行为", "SectionTitle"))
        g = QHBoxLayout()
        g.setSpacing(24)
        g.addWidget(label("最大嵌套层数", "Dim"))
        sp = QSpinBox()
        sp.setRange(1, 20)
        sp.setValue(5)
        sp.setFixedWidth(80)
        g.addWidget(sp)
        self.sp_depth = sp
        g.addSpacing(16)
        g.addWidget(label("单任务超时", "Dim"))
        sp2 = QSpinBox()
        sp2.setRange(1, 600)
        sp2.setValue(30)
        sp2.setFixedWidth(90)
        g.addWidget(sp2)
        self.sp_timeout = sp2
        g.addWidget(label("分钟", "Faint"))
        g.addSpacing(16)
        g.addWidget(label("剩余空间下限", "Dim"))
        sp3 = QDoubleSpinBox()
        sp3.setRange(0.0, 1000.0)
        sp3.setValue(5.0)
        sp3.setFixedWidth(90)
        g.addWidget(sp3)
        self.sp_free = sp3
        g.addWidget(label("GB", "Faint"))
        g.addStretch(1)
        b2.addLayout(g)
        # 文案 → Config 字段名的映射，别靠顺序对齐
        for text, key, default in (
            ("解压成功后删除原压缩包", "remove_source", False),
            ("删除解出来的嵌套包（省空间）", "remove_intermediate", True),
            ("单层文件夹自动上提", "flatten", True),
            ("识别内嵌压缩包（视频等伪装，较慢）", "scan_appended", True),
            ("清理文件名里的「删」字", "clean_delete", True),
        ):
            cb = QCheckBox(text)
            cb.setChecked(default)
            b2.addWidget(cb)
            self.checkboxes[key] = cb
        v.addWidget(c2)

        # 外观
        c4 = card("Card")
        b4 = QVBoxLayout(c4)
        b4.setContentsMargins(16, 14, 16, 14)
        b4.setSpacing(9)
        b4.addWidget(label("外观", "SectionTitle"))
        r3 = QHBoxLayout()
        r3.setSpacing(16)
        r3.addWidget(label("主题", "Dim"))
        g2 = QButtonGroup(self)
        for t, on in (("深色", True), ("浅色", False), ("跟随系统", False)):
            rb = QRadioButton(t)
            rb.setChecked(on)
            g2.addButton(rb)
            r3.addWidget(rb)
            self.theme_radios.append(rb)
        r3.addStretch(1)
        b4.addLayout(r3)
        v.addWidget(c4)

        # 资源管理器右键菜单
        c5 = card("Card")
        b5 = QVBoxLayout(c5)
        b5.setContentsMargins(16, 14, 16, 14)
        b5.setSpacing(9)
        b5.addWidget(label("资源管理器右键菜单", "SectionTitle"))
        row5 = QHBoxLayout()
        row5.setSpacing(10)
        self.btn_shell_on = QPushButton("安装到右键菜单")
        self.btn_shell_on.setObjectName("Primary")
        self.btn_shell_on.clicked.connect(self.sig_shell_install.emit)
        self.btn_shell_off = QPushButton("移除")
        self.btn_shell_off.clicked.connect(self.sig_shell_remove.emit)
        self.shell_mark = label("", "")
        row5.addWidget(self.btn_shell_on)
        row5.addWidget(self.btn_shell_off)
        row5.addWidget(self.shell_mark)
        row5.addStretch(1)
        b5.addLayout(row5)
        v.addWidget(c5)

        # 不处理的文件类型
        c6 = card("Card")
        b6 = QVBoxLayout(c6)
        b6.setContentsMargins(16, 14, 16, 14)
        b6.setSpacing(9)
        b6.addWidget(label("不处理的文件类型", "SectionTitle"))
        self.ed_exclude = QLineEdit()
        self.ed_exclude.setPlaceholderText("apk iso img dmg msi deb rpm jar")
        b6.addWidget(self.ed_exclude)
        note6 = label("空格分隔，不用带点。仍会列出来，但不会解压。", "Hint")
        note6.setWordWrap(True)
        b6.addWidget(note6)
        v.addWidget(c6)

        # 数据放在哪：只留两个入口按钮，位置信息挂在 tooltip 上（用户要求删掉说明文字）
        c7 = card("Card")
        b7 = QVBoxLayout(c7)
        b7.setContentsMargins(16, 14, 16, 14)
        b7.setSpacing(9)
        self.lbl_paths = label("", "Hint")          # 只用于 tooltip / 测试断言，不显示
        self.lbl_paths.setVisible(False)
        b7.addWidget(self.lbl_paths)
        row7 = QHBoxLayout()
        row7.setSpacing(8)
        self.btn_open_data = QPushButton("打开数据目录")
        self.btn_open_data.clicked.connect(lambda: self._open(self._data_dir()))
        self.btn_open_log = QPushButton("打开日志")
        self.btn_open_log.clicked.connect(lambda: self._open(paths_mod.log_path()))
        row7.addWidget(self.btn_open_data)
        row7.addWidget(self.btn_open_log)
        row7.addStretch(1)
        b7.addLayout(row7)
        v.addWidget(c7)
        self.refresh_paths()

        v.addStretch(1)

        outer.addWidget(scroll, 1)

        # 「恢复默认 / 保存」**放在滚动区外面**：以前它在滚动内容的最底部，页面一长
        # 就滚出可视区（用户报的"保存按钮位置在界面之外"）。这两个按钮必须永远看得见。
        foot_card = card("Card")
        foot_card.setFixedHeight(56)
        foot = QHBoxLayout(foot_card)
        foot.setContentsMargins(12, 0, 12, 0)
        foot.addStretch(1)
        reset = QPushButton("恢复默认")
        reset.clicked.connect(lambda: self.load_config(Config()))
        foot.addWidget(reset)
        save = QPushButton("保存")
        save.setObjectName("Primary")
        save.setMinimumWidth(100)
        save.clicked.connect(self.sig_save.emit)
        foot.addWidget(save)
        outer.addWidget(foot_card)

        # 引擎"已找到/未找到"的标记色是 inline 给的，跟随主题重画
        self._theme_hook(self._repaint_marks)

    # -- 数据位置显示 --------------------------------------------------

    @staticmethod
    def _data_dir() -> str:
        return paths_mod.data_dir()

    def _open(self, target: str) -> None:
        """用资源管理器打开一个目录/文件（打不开就静默——这只是个方便入口）。"""
        try:
            if os.path.isdir(target):
                os.startfile(target)                       # noqa: S606 - Windows 专用
            elif os.path.isfile(target):
                os.startfile(os.path.dirname(target) or ".")   # noqa: S606
        except Exception:                                  # noqa: BLE001
            pass

    def refresh_paths(self) -> None:
        """把"配置/密码本/日志实际在哪"记到隐藏标签 + 两个按钮的 tooltip 上。

        界面上**不再铺这段文字**（用户明确要求删掉）；但位置信息还是得有个去处：
        tooltip 里能看到，测试也能断言。打包成便携版后放在 `C:\\Program Files\\` 下
        会退到 `%APPDATA%`，悬停这两个按钮就能看出来。
        """
        if not hasattr(self, "lbl_paths"):
            return
        where = paths_mod.data_dir_note()
        data = paths_mod.data_dir()
        self.lbl_paths.setText(
            f"{where}：{data}\n"
            f"配置 {os.path.basename(paths_mod.data_path('config.json'))} · "
            f"密码本 {os.path.basename(paths_mod.data_path('密码本.txt'))} · "
            f"日志 logs\\ui.log"
        )
        self.btn_open_data.setToolTip(f"{where}\n{data}")
        self.btn_open_log.setToolTip(paths_mod.log_path())

    def _repaint_marks(self) -> None:
        for name in self.path_marks:
            self._paint_mark(name)

    def _detect_engines(self) -> None:
        """真去探测一次引擎，并把结果写进输入框 + 标记。"""
        eng = find_engines(
            seven_zip=self.path_edits["7-Zip 路径"].text().strip(),
            winrar=self.path_edits["WinRAR 路径"].text().strip(),
        )
        self.set_engine_paths(eng.seven_zip or "", eng.winrar or "",
                              bundled=eng.sevenzip_bundled)

    def _path_row(self, name: str, value: str, ok: bool) -> QHBoxLayout:
        r = QHBoxLayout()
        r.setSpacing(10)
        lb = label(name, "Dim")
        lb.setFixedWidth(96)
        r.addWidget(lb)
        e = QLineEdit(value)
        e.setMinimumHeight(32)
        r.addWidget(e, 1)
        browse = QPushButton("浏览")
        browse.clicked.connect(lambda: self._pick_exe(name))
        r.addWidget(browse)
        mark = label("✔ 已找到" if ok else "✘ 未找到", "")
        mark.setFixedWidth(64)
        r.addWidget(mark)
        self.path_edits[name] = e
        self.path_marks[name] = mark
        self._mark_state[name] = bool(ok)
        self._paint_mark(name)
        return r

    def _paint_mark(self, name: str) -> None:
        mark = self.path_marks.get(name)
        if mark is None:
            return
        found = self._mark_state.get(name, False)
        mark.setText("✔ 已找到" if found else "✘ 未找到")
        mark.setStyleSheet(f"color:{self.theme.color('ok' if found else 'err')};")

    def _pick_exe(self, name: str) -> None:
        path, _ = QFileDialog.getOpenFileName(self, f"选择 {name}", "", "可执行文件 (*.exe)")
        if path:
            self.path_edits[name].setText(path)

    # ------------------------------------------------------------------
    # 与 Config 互转
    # ------------------------------------------------------------------

    def set_engine_paths(self, seven_zip: str, winrar: str,
                         bundled: bool = True) -> None:
        """把探测到的引擎路径填进去，并给出找到/没找到的标记。"""
        for name, value in (("WinRAR 路径", winrar), ("7-Zip 路径", seven_zip)):
            edit = self.path_edits.get(name)
            if edit is not None and value and not edit.text().strip():
                edit.setText(value)
            self._mark_state[name] = bool(value) or bool(
                edit and os.path.isfile(edit.text().strip())
            )
            self._paint_mark(name)
        # 只在**不正常**的时候才显示一行（正常用自带那份时保持安静）：
        # 用户明确说了不要把这类说明文字堆在设置页上。
        if hasattr(self, "zip_note"):
            if not seven_zip:
                self.zip_note.setText("没找到 7-Zip：确认软件目录下的 tools\\7z 还在")
                self.zip_note.setStyleSheet(f"color:{self.theme.color('err')};")
                self.zip_note.setVisible(True)
            elif bundled:
                self.zip_note.setText("")
                self.zip_note.setVisible(False)
            else:
                self.zip_note.setText("用的不是自带那份，不支持 lz4 / lz5 / zstd")
                self.zip_note.setStyleSheet(f"color:{self.theme.color('warn')};")
                self.zip_note.setVisible(True)

    def load_config(self, cfg: Config) -> None:
        self.path_edits["WinRAR 路径"].setText(cfg.winrar)
        self.path_edits["7-Zip 路径"].setText(cfg.seven_zip)
        self.sp_depth.setValue(int(cfg.max_depth))
        self.sp_timeout.setValue(int(cfg.timeout_min))
        self.sp_free.setValue(float(cfg.min_free_gb))
        for key, cb in self.checkboxes.items():
            cb.setChecked(bool(getattr(cfg, key)))
        for rb, name in zip(self.theme_radios, ("dark", "light", "system")):
            rb.setChecked(cfg.theme == name)
        self.ed_exclude.setText(" ".join(cfg.exclude_exts or []))

    def apply_to_config(self, cfg: Config) -> None:
        """注意：解压位置/重名策略不在这里——它们在主界面那一行（MainPage.output_values）。

        语言选项也删了：界面只有中文一套，摆个切不了的下拉比没有更糟。
        """
        cfg.winrar = self.path_edits["WinRAR 路径"].text().strip()
        # 自带那份 7-Zip 就存空串（= "用自带的"）：这样配置里不带任何绝对路径，
        # 整个文件夹拷到别的电脑/别的用户名下都照样能用
        z = self.path_edits["7-Zip 路径"].text().strip()
        cfg.seven_zip = "" if os.path.normcase(z) == os.path.normcase(
            engine_mod.BUNDLED_SEVENZIP) else z
        cfg.max_depth = int(self.sp_depth.value())
        cfg.timeout_min = float(self.sp_timeout.value())
        cfg.min_free_gb = float(self.sp_free.value())
        # 排除名单：逗号/空格/分号都当分隔符（用户怎么顺手怎么填），统一存小写不带点
        raw = self.ed_exclude.text().replace(",", " ").replace("，", " ").replace(";", " ")
        cfg.exclude_exts = sorted(probe.norm_exts(raw.split()))
        for key, cb in self.checkboxes.items():
            setattr(cfg, key, bool(cb.isChecked()))
        for rb, name in zip(self.theme_radios, ("dark", "light", "system")):
            if rb.isChecked():
                cfg.theme = name

    # -- 右键菜单状态显示 ------------------------------------------------

    def set_shell_status(self, installed: bool, command: str = "") -> None:
        """把「装没装」直接写在按钮旁边，别让用户自己猜。"""
        self.shell_mark.setText("✔ 已注册" if installed else "未注册")
        self.shell_mark.setStyleSheet(
            f"color:{self.theme.color('ok' if installed else 'text_faint')};"
        )
        self.shell_mark.setToolTip(command or "还没装到右键菜单")
        self.btn_shell_off.setEnabled(installed)
        self.btn_shell_on.setText("重新安装" if installed else "安装到右键菜单")


# ==========================================================================
# 主窗口
# ==========================================================================


class Workbench(QWidget):
    def __init__(
        self,
        base_dir: str | None = None,
        *,
        launch_paths: list[str] | None = None,
        launch_auto: bool = False,
    ) -> None:
        super().__init__()
        self.setObjectName("Workbench")
        # 去掉系统标题栏（自绘顶栏更好看，也省掉一条灰边）。去掉之后：
        #   * 拖动窗口 / 双击最大化 / Aero 贴边：交给 WM_NCHITTEST 返回 HTCAPTION，
        #     由 Windows 原生处理（比自己算鼠标位移稳，还能贴边分屏）；
        #   * 边缘缩放：同一处返回 HTLEFT/HTTOP… 交给系统；
        #   * 最小化/最大化/关闭：顶栏右侧那三个自绘按钮。
        self.setWindowFlag(Qt.WindowType.FramelessWindowHint, True)
        self.setWindowTitle(APP_NAME)
        # 尺寸要**按屏幕来**，而且要窄一点、高一点（用户按物理像素给的 1600×1800；
        # 这块屏是 200% 缩放，换算成逻辑就是 800×900）。
        # 以前写死 1340×900：在 1280×720 逻辑屏上比屏幕还高，用户根本摆不下。
        avail = None
        try:
            from PySide6.QtGui import QGuiApplication

            screen = QGuiApplication.primaryScreen()
            avail = screen.availableGeometry() if screen else None
        except Exception:            # noqa: BLE001
            avail = None
        if avail is not None:
            self.resize(max(720, min(800, int(avail.width() * 0.92))),
                        max(620, min(900, int(avail.height() * 0.92))))
        else:
            self.resize(800, 900)
        # 最小尺寸两个方向都要给：只给宽度的话，把高度拖小会让上下两块挤在一起
        # （用户报的"元素重叠"）。这个高度的算法：顶栏 48 + 计数卡 72 + 列表(最小) 120
        # + 输出行 50 + 操作行 56 + 日志 170 + 进度 36 + 间距/边距 ≈ 620。
        self.setMinimumSize(720, 620)
        self.setAcceptDrops(True)
        self.launch_paths = list(launch_paths or [])
        self.launch_auto = bool(launch_auto)

        # ---- 真实状态（先读配置，主题也归配置管）----
        # `base_dir` 现在表示**数据目录**（配置/密码本/日志放哪）：
        #   * 给了就在那儿（测试与截图脚本都这么用，绝不能碰用户真实配置）；
        #   * 没给就走 paths.data_dir()：便携版优先程序目录，写不进去才落 %APPDATA%。
        # 只读资源（assets、tools）另走 paths.resource_path()，跟数据目录解耦——
        # 打包后资源在 _internal 里，而数据必须在可写的地方。
        if base_dir:
            paths_mod.set_data_dir_override(base_dir)
        self.base_dir = paths_mod.data_dir()
        icon_path = paths_mod.resource_path("assets", ICON_FILE)
        if os.path.isfile(icon_path):
            self.setWindowIcon(QIcon(icon_path))     # 无边框后任务栏图标只能靠这个
        self.config = Config.load(os.environ.get("SMART_UNZIP_CONFIG")
                                  or paths_mod.data_path("config.json"))
        # 主题在这儿就定下来：页面是下面构造的，inline 上色会取当前主题，
        # 启动时是浅色就不该先画一遍深色再改（以前就是写死 "dark"，配置里的主题根本没生效）
        self.theme = Theme(theme_mod.resolve(self.config.theme))
        self.setStyleSheet(self.theme.qss())

        self.vault = PasswordVault(book=paths_mod.data_path("密码本.txt"))
        self.vault.reload()
        self.extractor = Extractor(
            find_engines(seven_zip=self.config.seven_zip, winrar=self.config.winrar),
            timeout=self.config.timeout_seconds,
        )
        self.worker: JobWorker | None = None
        self.pending_ask: AskRequest | None = None
        self.run_started = 0.0
        # 右键/命令行带了 --auto：等扫描把这些路径挂进清单之后再开跑
        self._auto_pending = False
        # 用户点过「停止」：这一批结束后不要再自动接着跑队列
        self._stop_requested = False

        root = QVBoxLayout(self)
        root.setContentsMargins(16, 16, 16, 16)
        root.setSpacing(0)

        self.stack = QStackedWidget()
        root.addWidget(self.stack)

        self.main_page = MainPage(self.theme)
        self.library_page = LibraryPage(self.theme)
        self.settings_page = SettingsPage(self.theme)

        for p in (self.main_page, self.library_page, self.settings_page):
            self.stack.addWidget(p)

        self.main_page.sig_open_library.connect(self._open_library)
        self.main_page.sig_open_settings.connect(self._open_settings)
        self.main_page.sig_paths_added.connect(self._on_paths_added)
        self.main_page.sig_window_min.connect(self.showMinimized)
        self.main_page.sig_window_max.connect(self.toggle_maximized)
        self.main_page.sig_window_close.connect(self.close)
        for p in (self.library_page, self.settings_page):
            p.sig_back.connect(lambda: self.stack.setCurrentWidget(self.main_page))


        # 开始按钮 = 「开始 / 继续」（跑着且暂停时它就是继续），暂停按钮只负责暂停，
        # 两者由 _sync_buttons 保证互斥：任何时刻只有一个可用。
        self.main_page.btn_start.clicked.connect(self._start_clicked)
        self.main_page.btn_pause.clicked.connect(self._pause_clicked)
        self.main_page.btn_stop.clicked.connect(self._on_stop)

        self.settings_page.sig_save.connect(self._on_save_config)
        self.settings_page.sig_shell_install.connect(self._on_shell_install)
        self.settings_page.sig_shell_remove.connect(self._on_shell_remove)
        self.library_page.set_vault(self.vault)
        self.settings_page.load_config(self.config)
        self.main_page._exclude_exts = list(self.config.exclude_exts or [])
        self.main_page.load_output(
            self.config.output_mode, self.config.output_dir, self.config.conflict
        )
        self.settings_page.set_engine_paths(
            self.extractor.engines.seven_zip or "", self.extractor.engines.winrar or "",
            bundled=self.extractor.engines.sevenzip_bundled,
        )
        # 列宽：上次拖成什么样，这次还是什么样（拖完 0.7 秒才落盘，别每像素写一次文件）
        self.main_page.apply_col_widths(self.config.table_cols)
        self._col_save_timer = QTimer(self)
        self._col_save_timer.setSingleShot(True)
        self._col_save_timer.setInterval(700)
        self._col_save_timer.timeout.connect(self._save_col_widths)
        self.main_page.table.horizontalHeader().sectionResized.connect(
            lambda *_a: self._col_save_timer.start()
        )
        self._refresh_shell_status()
        self._ipc_clients: list = []
        self.server = self._start_ipc_server()
        # 转发进来的路径先攒 220ms 再作为一批提交（多选分卷要合并成一批才认得出来）
        self._ipc_buffer: list[str] = []
        self._ipc_auto = False
        self._ipc_timer = QTimer(self)
        self._ipc_timer.setSingleShot(True)
        self._ipc_timer.setInterval(220)
        self._ipc_timer.timeout.connect(self._flush_ipc)
        self.resizeEvent_extra = None

        # 「跟随系统」要能真的跟着系统变（Windows 的浅色/深色开关一动就重画）
        try:
            from PySide6.QtGui import QGuiApplication

            QGuiApplication.styleHints().colorSchemeChanged.connect(self._on_system_scheme)
        except Exception:
            pass

        # ★ 冷启动带路径（右键菜单在"还没有窗口在跑"时就是这种）：以前 `launch_paths`
        #   只被赋值、**全工程没有任何地方读它** → 窗口开出来了、清单却是空的，
        #   用户看到的就是"右键加了但什么都没进列表"（这一步漏了整整一段时间）。
        #   等窗口画出来再挂清单：扫描是后台线程，早了界面还没准备好。
        if self.launch_paths:
            QTimer.singleShot(0, lambda: self.queue_launch(self.launch_paths, self.launch_auto))

    # ------------------------------------------------------------------
    # 主题
    # ------------------------------------------------------------------

    def toggle_maximized(self) -> None:
        """顶栏那个按钮：最大化 / 还原。"""
        if self.isMaximized():
            self.showNormal()
        else:
            self.showMaximized()
        self.main_page.topbar.set_maximized(self.isMaximized())

    def showEvent(self, event) -> None:      # noqa: N802 - Qt 命名
        super().showEvent(event)
        self._round_corners()
        self._make_resizable()

    def changeEvent(self, event) -> None:    # noqa: N802 - Qt 命名
        """窗口状态变了（最大化/最小化/还原）就刷新一次状态文件。

        为什么要在这里写：`_dump_state` 平时只在清单变动时写，而"双击标题栏
        到底有没有最大化"这种事只能从外面看——把窗口状态也写进去，
        验证脚本才能分辨"系统切了但 Qt 不知道"这种情况。
        """
        super().changeEvent(event)
        if event.type() == QEvent.Type.WindowStateChange:
            self.main_page._dump_state()

    def _make_resizable(self) -> None:
        """给无边框窗口补上 `WS_THICKFRAME`，否则拖边缘永远不会缩放。

        实测（GetWindowLongW 读出来是 0x960B0000）：Qt 的 FramelessWindowHint 建出来的
        是 WS_POPUP，**既没有 WS_CAPTION 也没有 WS_THICKFRAME**。于是我们在
        WM_NCHITTEST 里老老实实返回 HTLEFT / HTBOTTOMRIGHT 也没用——Windows 的
        "拖非客户区改大小"只对**可缩放边框**的窗口生效（用户报的"拖拽调整窗口大小
        不生效"就是这个）。补上这一位再 SWP_FRAMECHANGED 让系统重算边框，
        缩放/贴边分屏/双击最大化就全回到系统行为了。
        """
        if os.name != "nt":
            return
        try:
            gwL_style = -16
            ws_thickframe = 0x00040000
            swp = 0x20 | 0x2 | 0x1 | 0x4        # FRAMECHANGED|NOMOVE|NOSIZE|NOZORDER
            hwnd = int(self.winId())
            u = ctypes.windll.user32
            style = u.GetWindowLongW(wintypes.HWND(hwnd), gwL_style)
            if not style & ws_thickframe:
                u.SetWindowLongW(wintypes.HWND(hwnd), gwL_style, style | ws_thickframe)
                u.SetWindowPos(wintypes.HWND(hwnd), None, 0, 0, 0, 0, swp)
        except Exception:                    # noqa: BLE001 - 加不上就退化成"不能拖边缘"
            pass

    def _round_corners(self) -> None:
        """主窗口的圆角（实现见模块级 `_dwm_round`）。"""
        _dwm_round(int(self.winId()))

    # 无边框窗口：拖动、双击最大化、贴边分屏、边缘缩放全都交回给 Windows，
    # 靠 WM_NCHITTEST 告诉它"这一点是标题栏 / 左边框 / 客户区"。
    # 自己用 mouseMoveEvent 算位移的话，贴边分屏、双击最大化都得手写一遍，还容易抖。
    #
    # 但**光靠 WM_NCHITTEST 不够**：Qt 的 FramelessWindowHint 会把窗口的非客户区
    # 算成 0 厚度（WM_NCCALCSIZE），于是鼠标那一路压根不会走"非客户区"分支
    # （实测 SendInput 拖边缘完全没反应）。所以真正的缩放/移动由下面这几个
    # 事件处理里显式调 startSystemResize / startSystemMove —— Qt 会替我们发
    # `WM_NCLBUTTONDOWN(HTxxx)`，那条路绕开命中测试，实测有效（宽度 1680→3355）。
    # 另外补上 WS_THICKFRAME（见 _make_resizable），双保险。
    RESIZE_BAND = 8

    def _edges_at(self, pos: QPoint):
        """鼠标在窗口的哪几条边上（8 逻辑像素内算边）。"""
        b = self.RESIZE_BAND
        edges = Qt.Edge(0)
        if pos.x() <= b:
            edges |= Qt.Edge.LeftEdge
        if pos.x() >= self.width() - b:
            edges |= Qt.Edge.RightEdge
        if pos.y() <= b:
            edges |= Qt.Edge.TopEdge
        if pos.y() >= self.height() - b:
            edges |= Qt.Edge.BottomEdge
        return edges

    def _titlebar_at(self, pos: QPoint) -> bool:
        """这一点算不算标题栏（顶栏空白处，不含按钮）。"""
        return self._hit_test(self.mapToGlobal(pos)) == self.HTCAPTION

    def mousePressEvent(self, event) -> None:      # noqa: N802 - Qt 命名
        if event.button() == Qt.MouseButton.LeftButton and not self.isMaximized():
            pos = event.position().toPoint()
            edges = self._edges_at(pos)
            handle = self.windowHandle()
            if edges and handle is not None and handle.startSystemResize(edges):
                # 系统的缩放循环会吃掉随后的移动/松开事件，我们的 mouseMoveEvent
                # 就再也收不到"离开边缘"这一下，光标会一直卡在双箭头上
                # （用户报的"光标保持双箭头不释放"）→ 循环返回后按当前真实位置重算
                QTimer.singleShot(0, self._refresh_resize_cursor)
                event.accept()
                return
            if self._titlebar_at(pos) and handle is not None and handle.startSystemMove():
                QTimer.singleShot(0, self._refresh_resize_cursor)
                event.accept()
                return
        super().mousePressEvent(event)

    def mouseDoubleClickEvent(self, event) -> None:  # noqa: N802
        """双击顶栏 = 最大化/还原。

        **不要在 Qt 这边直接切**：顶栏在 `WM_NCHITTEST` 里返回 HTCAPTION，Windows
        自己就会把"双击标题栏"当非客户区行为处理——实测连发两次
        `WM_NCLBUTTONDBLCLK(HTCAPTION)`，窗口在 3840×2064 与 2720×1840 之间来回切，
        Qt 的 `isMaximized()` 也同步跟着变。以前这里又写了一遍 toggle_maximized()，
        两边各切一次互相抵消，用户看到的就是"能最大化，再双击不还原"。
        所以这里只做**兜底**：等一下看状态有没有真的变，没变才自己动手。
        """
        if (event.button() == Qt.MouseButton.LeftButton
                and self._titlebar_at(event.position().toPoint())):
            before = self.isMaximized()
            QTimer.singleShot(120, lambda: self._ensure_maximize_toggled(before))
            event.accept()
            return
        super().mouseDoubleClickEvent(event)

    def _ensure_maximize_toggled(self, before: bool) -> None:
        """系统没管这一下（有些环境/主题下确实可能不管）→ 自己切。"""
        if self.isMaximized() == before:
            self.toggle_maximized()

    def _refresh_resize_cursor(self) -> None:
        """按鼠标**当前所在位置**重算缩放光标（离开窗口就恢复箭头）。"""
        try:
            from PySide6.QtGui import QCursor

            local = self.mapFromGlobal(QCursor.pos())
            if not self.rect().contains(local):
                self.unsetCursor()
                return
            self._set_cursor_for(local)
        except Exception:                    # noqa: BLE001
            self.unsetCursor()

    def leaveEvent(self, event) -> None:      # noqa: N802 - Qt 命名
        self.unsetCursor()                   # 离开窗口别再留着双箭头
        super().leaveEvent(event)

    def _set_cursor_for(self, pos: QPoint) -> None:
        if self.isMaximized():
            self.unsetCursor()
            return
        edges = self._edges_at(pos)
        shape = Qt.CursorShape.ArrowCursor
        has_lr = bool(edges & (Qt.Edge.LeftEdge | Qt.Edge.RightEdge))
        has_tb = bool(edges & (Qt.Edge.TopEdge | Qt.Edge.BottomEdge))
        if has_lr and has_tb:
            shape = (Qt.CursorShape.SizeFDiagCursor
                     if bool(edges & Qt.Edge.LeftEdge) == bool(edges & Qt.Edge.TopEdge)
                     else Qt.CursorShape.SizeBDiagCursor)
        elif has_lr:
            shape = Qt.CursorShape.SizeHorCursor
        elif has_tb:
            shape = Qt.CursorShape.SizeVerCursor
        if shape == Qt.CursorShape.ArrowCursor:
            self.unsetCursor()
        else:
            self.setCursor(shape)

    def mouseMoveEvent(self, event) -> None:       # noqa: N802
        """悬停到边缘时换成缩放光标，让人看得出"这里能拖"。"""
        self._set_cursor_for(event.position().toPoint())
        super().mouseMoveEvent(event)

    HTCLIENT, HTCAPTION = 1, 2
    HTLEFT, HTRIGHT, HTTOP, HTTOPLEFT, HTTOPRIGHT, HTBOTTOM = 10, 11, 12, 13, 14, 15
    HTBOTTOMLEFT, HTBOTTOMRIGHT = 16, 17
    WM_NCHITTEST = 0x0084

    def nativeEvent(self, event_type, message):      # noqa: N802 - Qt 命名
        if os.name == "nt" and event_type == b"windows_generic_MSG":
            try:
                msg = wintypes.MSG.from_address(int(message))
            except (TypeError, ValueError):
                return super().nativeEvent(event_type, message)
            if msg.message == self.WM_NCHITTEST:
                x = ctypes.c_short(msg.lParam & 0xFFFF).value
                y = ctypes.c_short((msg.lParam >> 16) & 0xFFFF).value
                return True, self._hit_test_msg(x, y)
        return super().nativeEvent(event_type, message)

    def _hit_test_msg(self, x_phys: int, y_phys: int) -> int:
        """WM_NCHITTEST 里给的是**物理像素**的屏幕坐标，Qt 的几何是逻辑像素。

        高 DPI（本机 200%）下两者差 DPR 倍：不换算的话，点窗口中心会被当成
        "窗口右下角之外"，于是**点按钮全变成拖边框**——整个窗口既点不动也拖不动
        （实测就是这个 bug）。这里先除 DPR 再交给 _hit_test。

        实测（200% 缩放，窗口 1340×900）：物理 y=窗口顶+10 → 客户区、+20~+60 → 标题栏
        （正好是顶栏那 52px）、+84 及以下 → 客户区；左边缘 → HTLEFT、右下角 → HTBOTTOMRIGHT。
        `tools/verify_shellmenu.py` 里有一份照着这几个数字写的探针。
        """
        dpr = float(self.devicePixelRatioF() or 1.0)
        return self._hit_test(QPoint(int(round(x_phys / dpr)), int(round(y_phys / dpr))))

    def _hit_test(self, screen_pos) -> int:
        """逻辑坐标的屏幕位置 → HTxxx。单独成函数是为了能被测（不用真的发窗口消息）。"""
        local = self.mapFromGlobal(screen_pos)
        if not self.rect().adjusted(-64, -64, 64, 64).contains(local):
            # 落在窗口外面（坐标系对不上时会出现）：老老实实说自己不是标题栏，
            # 别瞎给一个"右下角"，否则用户点哪儿都在拖边框
            return self.HTCLIENT

        def in_topbar() -> bool:
            """顶栏空白处 = 标题栏（可拖动/双击最大化/贴边）。

            顶栏上的按钮要让它们自己收点击，不能当成标题栏。
            坐标系：topbar.geometry() 是相对 MainPage 的，得先换算到本窗口。
            """
            bar = self.main_page.topbar
            bar_rect = QRect(bar.mapTo(self, QPoint(0, 0)), bar.size())
            if not bar_rect.contains(local):
                return False
            return not isinstance(self.childAt(local), QAbstractButton)

        if self.isMaximized():
            # ★ 最大化时**没有可拖的边框**（不能再缩放），但顶栏依然是标题栏：
            #   双击要能还原、按住往下拖也要能还原成普通窗口（系统行为）。
            #   以前这里直接 `return HTCLIENT`，于是最大化之后 `_titlebar_at()` 永远为假
            #   → 我们自己的双击处理根本不触发，用户看到的就是
            #   "双击能最大化、再双击不恢复"（探针 probe_maximize 量出来的就是这个）。
            return self.HTCAPTION if in_topbar() else self.HTCLIENT

        # 边缘抓取带：8 逻辑像素（200% 下是 16 物理像素，够好抓；以前 6 有点窄）
        border = 8
        corner = 14
        left = local.x() <= border
        right = local.x() >= self.width() - border
        top = local.y() <= border
        bottom = local.y() >= self.height() - border
        if top and local.x() <= corner:
            return self.HTTOPLEFT
        if top and local.x() >= self.width() - corner:
            return self.HTTOPRIGHT
        if bottom and local.x() <= corner:
            return self.HTBOTTOMLEFT
        if bottom and local.x() >= self.width() - corner:
            return self.HTBOTTOMRIGHT
        if left:
            return self.HTLEFT
        if right:
            return self.HTRIGHT
        if top:
            return self.HTTOP
        if bottom:
            return self.HTBOTTOM
        if in_topbar():
            return self.HTCAPTION
        return self.HTCLIENT

    def _on_system_scheme(self, *_a) -> None:
        if self.config.theme not in ("dark", "light"):
            self._apply_theme()
            self.main_page.append_log("外观", "跟随系统：已切换配色", "text_dim")

    def apply_theme_now(self) -> None:
        """按配置重新落一遍主题（设置页点保存时调用）。"""
        self._apply_theme()

    def _apply_theme(self) -> None:
        """换配色：全局 QSS + 各页面自己登记的 inline 上色 + 重刷控件样式。"""
        self.theme.set_mode(theme_mod.resolve(self.config.theme))
        self.setStyleSheet(self.theme.qss())
        for page in (self.main_page, self.library_page, self.settings_page):
            page.apply_theme()
        refresh_icons(self, self.theme)      # 矢量图标的颜色是画进位图的，得重画
        repolish(self)
        self.main_page.table.viewport().update()
        self.library_page.table.viewport().update()

    # ------------------------------------------------------------------
    # 页面切换
    # ------------------------------------------------------------------

    def _open_library(self) -> None:
        self.library_page.reload()
        self.stack.setCurrentWidget(self.library_page)

    def _open_settings(self) -> None:
        self.stack.setCurrentWidget(self.settings_page)

    def _on_paths_added(self) -> None:
        # 按钮状态统一由 MainPage._sync_buttons 决定，这里只是拖拽后的补一次
        self.main_page._sync_buttons()
        # 扫描结束也会发这个信号 → 顺手把"右键带了 --auto"这件事办掉
        self._maybe_auto_start()

    # ------------------------------------------------------------------
    # 从右键菜单 / 命令行进来
    # ------------------------------------------------------------------

    def _start_ipc_server(self):
        """开一个本机 socket：已经有窗口在跑时，新进程把路径甩过来然后自己退出。

        为什么需要：右键一次选 10 个文件，资源管理器会起 10 个进程——没有这一段
        就是 10 个窗口。listen 失败（端口被占/受限环境）就退化成"各自开一个窗口"，
        功能不受影响，所以这里不抛异常。
        """
        server = QLocalServer(self)
        server.removeServer(PIPE_NAME)          # 上次异常退出可能留下残骸
        if not server.listen(PIPE_NAME):
            return None
        server.newConnection.connect(self._on_ipc)
        return server

    def _on_ipc(self) -> None:
        sock = self.server.nextPendingConnection() if self.server else None
        if sock is None:
            return
        buf = bytearray()
        self._ipc_clients.append((sock, buf))

        def collect() -> None:
            buf.extend(bytes(sock.readAll()))

        def finish() -> None:
            self._ipc_clients = [c for c in self._ipc_clients if c[0] is not sock]
            try:
                # **断开之前必须再排空一次**：客户端（core/single.py 那条纯 ctypes 快路径）
                # 是"写完就 CloseHandle"，Qt 有时先发 disconnected 才轮到 readyRead，
                # 只靠 readyRead 的话就会拿空 buffer 去解析 → 路径被静默丢掉
                # （用户报的"右键加了但清单里没有"就是这个）。
                buf.extend(bytes(sock.readAll()))
                payload = bytes(buf)
                if payload:
                    # "空路径"是**正常**消息：不带参数启动 = "把已有窗口顶到前面"，
                    # run.py 就是发一条 `{"paths": []}` 过来的。别把它当成解析失败报黄字
                    # （实测：用户看到过一句莫名其妙的"没解析出可用路径"）。
                    wake_only = False
                    try:
                        data = json.loads(payload.decode("utf-8"))
                        wake_only = (isinstance(data, dict)
                                     and not [p for p in (data.get("paths") or []) if p])
                    except (ValueError, UnicodeDecodeError):
                        wake_only = False
                    if not self.queue_launch_from_payload(payload) and not wake_only:
                        self.main_page.append_log(
                            "右键菜单", "这条转发消息没解析出可用路径（已忽略）", "warn")
                else:
                    self.main_page.append_log("右键菜单", "收到了一条空的转发消息（已忽略）", "warn")
            except Exception as exc:      # noqa: BLE001 - 槽里抛出去 = 用户眼里"点了没反应"
                self.main_page.append_log("错误", f"处理转发消息失败：{exc}", "err")
            finally:
                sock.deleteLater()

        sock.readyRead.connect(collect)
        # 对方写完就断，所以"断开"才是收全了的信号（边收边解析会撞上半截 JSON）
        sock.disconnected.connect(finish)

    def queue_launch_from_payload(self, payload: bytes) -> bool:
        try:
            data = json.loads(payload.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return False
        if not isinstance(data, dict):
            return False
        paths = [str(p) for p in (data.get("paths") or []) if p]
        return self.queue_launch(paths, bool(data.get("auto")))

    def queue_launch(self, paths: list[str], auto: bool = False) -> bool:
        """把一批路径挂进清单；auto=True 就顺手开始跑（右键菜单那条路）。

        **收到路径之后绝不能"静默丢弃"**：右键点了没反应是最难查的一类 bug
        （用户只看到"什么都没发生"）。所以这里对每条路径都留痕：
          * 先把资源管理器可能带上的引号/空白剥掉；
          * 不存在的路径写进日志（带上原始字符串），而不是悄悄过滤掉；
          * 真的有东西加进来了就把窗口顶到前面、并切回清单页——用户当时可能
            停在设置页/密码本页，那样"加进来了"在他眼里也等于"什么都没发生"。
        """
        cleaned: list[str] = []
        for raw in paths or []:
            p = str(raw).strip().strip('"').strip()
            if p:
                cleaned.append(p)
        wanted, missing = [], []
        for p in cleaned:
            (wanted if os.path.exists(p) else missing).append(p)
        if missing:
            self.main_page.append_log(
                "右键菜单",
                "这些路径不存在，没法加进清单：" + "、".join(missing[:3])
                + ("…" if len(missing) > 3 else ""),
                "warn",
            )
        if wanted:
            self.stack.setCurrentWidget(self.main_page)    # 让人看得见清单
            # ★ **攒一小会儿再提交**：资源管理器多选时是按 MultiSelectModel=Player
            #   一个文件起一个进程的（选 3 个分卷 = 3 个进程 = 3 条转发消息）。
            #   以前每条消息各自 scan 一次，三次都只看到一个分卷文件，
            #   于是"分卷归组"根本没机会发生 → 清单里冒出三个 .001（用户报的正是这个）。
            #   合并成一批再扫，`probe.group_volumes` 就能按文件名认出它们是一组，
            #   只留主卷一项。
            self._ipc_buffer.extend(wanted)
            self._ipc_auto = self._ipc_auto or bool(auto)
            self._ipc_timer.start()
        self._raise_window()
        if not wanted:
            return False
        return True

    def _flush_ipc(self) -> None:
        """把攒起来的转发路径作为**一批**提交（见 queue_launch）。"""
        paths, auto = self._ipc_buffer, self._ipc_auto
        self._ipc_buffer, self._ipc_auto = [], False
        if not paths:
            return
        self.main_page.add_paths(paths)
        self.main_page.append_log(
            "右键菜单",
            f"收到 {len(paths)} 个路径" + ("，开始解压" if auto else "，已挂进清单"),
            "info",
        )
        if not auto:
            return
        if self.worker is not None:
            self.main_page.append_log(
                "系统", "上一批还在跑：新加的已进清单，等这批结束再点「开始」", "warn")
            return
        self._auto_pending = True
        self._maybe_auto_start()

    def _maybe_auto_start(self) -> None:
        """把"该自动开跑"这件事挂在**扫描结束**上，而不是定时器等一会儿。

        以前这里是 `QTimer.singleShot(120, self._on_start)`：扫描是后台线程，
        120ms 往往还没扫完，`_on_start` 看到清单是空的就直接"没有可执行的任务"
        收工——右键带 `--auto` 因此时灵时不灵（本地夹具快，刚好躲过去了）。
        现在扫描一结束就检查一次，没有竞态。
        """
        if not self._auto_pending or self.worker is not None:
            return
        mp = self.main_page
        if mp._scan_job is not None or mp._scan_queue:
            return                      # 还在扫，等 _scan_finished 再喊我
        self._auto_pending = False
        self._on_start()

    def _raise_window(self) -> None:
        """把窗口顶到最前面（Explorer 拉起来的进程不一定在前台）。"""
        self.show()
        if self.isMinimized():
            self.setWindowState(self.windowState() & ~Qt.WindowState.WindowMinimized)
        self.raise_()
        self.activateWindow()

    # ------------------------------------------------------------------
    # 列宽
    # ------------------------------------------------------------------

    def _save_col_widths(self) -> None:
        widths = self.main_page.col_widths()
        if widths and widths != list(self.config.table_cols or []):
            self.config.table_cols = widths
            self.config.save(paths_mod.data_path("config.json"))

    # ------------------------------------------------------------------
    # 资源管理器右键菜单
    # ------------------------------------------------------------------

    def _shell_command(self) -> tuple[str, str]:
        """要写进注册表的 (可执行文件, 脚本)。

        打包版返回 `(exe, "")`——直接调自己，目标机不需要 Python；
        源码版返回 `(pythonw.exe, run.py)`。判断逻辑在 `shellmenu.launch_parts()`，
        命令行开关 `--install-shellmenu` 用的是同一份，保证两处不会跑偏。
        """
        return shellmenu.launch_parts()

    def _on_shell_install(self) -> None:
        if os.name != "nt":
            self.main_page.append_log("右键菜单", "只有 Windows 才支持注册右键菜单", "warn")
            return
        try:
            keys = shellmenu.install_self()
        except OSError as exc:
            self.main_page.append_log("右键菜单", f"写入注册表失败：{exc}", "err")
            self._refresh_shell_status()
            return
        self._refresh_shell_status()
        self.main_page.append_log(
            "右键菜单",
            f"已注册 {len(keys)} 处（文件 / 文件夹 / 文件夹空白处），"
            "右键后把路径加进待处理列表、不自动开始"
            "——在资源管理器里随便右键一个文件试试",
            "ok",
        )

    def _on_shell_remove(self) -> None:
        if os.name != "nt":
            return
        try:
            removed = shellmenu.uninstall_self()
        except OSError as exc:
            self.main_page.append_log("右键菜单", f"删除注册表项失败：{exc}", "err")
            self._refresh_shell_status()
            return
        self._refresh_shell_status()
        self.main_page.append_log(
            "右键菜单", f"已移除 {len(removed)} 处右键菜单项", "ok" if removed else "warn"
        )

    def _shell_stale(self, command: str) -> bool:
        """注册表里那条命令还指得着东西吗。

        以前是拿"当前算出来的 run.py 路径"去跟命令字符串比对——测试里 base_dir
        是临时目录，于是一装上就显示"路径已失效"（假警报）。改成检查命令里
        **每个带引号的路径**是否还存在：挪了工程 / 换了 venv 才是真的失效。
        """
        if not command:
            return False
        for path in re.findall(r'"([^"]+)"', command):
            if path in ("%1", "%V"):
                continue
            if not os.path.exists(path):
                return True
        return False

    def _refresh_shell_status(self) -> None:
        if os.name != "nt":
            self.settings_page.set_shell_status(False, "当前系统不支持")
            self.settings_page.btn_shell_on.setEnabled(False)
            return
        try:
            st = shellmenu.state()
        except OSError:
            st = {"installed": False, "command": ""}
        self.settings_page.set_shell_status(bool(st.get("installed")), st.get("command", ""))
        if st.get("installed") and self._shell_stale(st.get("command", "")):
            self.settings_page.shell_mark.setText("⚠ 路径已失效")
            self.settings_page.shell_mark.setStyleSheet(
                f"color:{self.theme.color('warn')};"
            )


    # ------------------------------------------------------------------
    # 配置
    # ------------------------------------------------------------------

    def _on_save_config(self) -> None:
        before = self.config.theme
        self.settings_page.apply_to_config(self.config)
        self.config.output_mode, self.config.output_dir, self.config.conflict = (
            self.main_page.output_values()
        )
        ok = self.config.save(paths_mod.data_path("config.json"))
        self.extractor.timeout = self.config.timeout_seconds
        self.extractor.engines = find_engines(
            seven_zip=self.config.seven_zip, winrar=self.config.winrar
        )
        self.settings_page.set_engine_paths(
            self.extractor.engines.seven_zip or "", self.extractor.engines.winrar or "",
            bundled=self.extractor.engines.sevenzip_bundled,
        )
        # 主题改了就立刻生效——以前只写进 config.json，界面纹丝不动，
        # 看着就像"切了没用"
        self._apply_theme()
        if before != self.config.theme:
            self.main_page.append_log(
                "外观",
                {"dark": "已切到深色", "light": "已切到浅色"}.get(self.config.theme, "已切到跟随系统"),
                "text_dim",
            )
        self.main_page.append_log(
            "设置", "配置已保存" if ok else "配置保存失败（磁盘只读？）", "ok" if ok else "err"
        )

    # ------------------------------------------------------------------
    # 运行
    # ------------------------------------------------------------------

    def _on_start(self) -> None:
        if self.worker is not None:
            return
        self._stop_requested = False
        # ★ 还有路径在扫描 → 别急着开这一批。
        #   扫描是后台线程，清单是"扫完一批加一批"地长出来的；用户完全可能刚拖完
        #   就点开始，那时清单里只有先扫完的那几项。以前照开不误 → 这一批只包含
        #   一部分，剩下的留在"排队中"，用户得再点一次开始（他报的
        #   "这个失败之后没跳过继续，变成还要手点一下开始"就是这个场景）。
        #   现在改成"等扫完自动开始"。
        if self.main_page._scan_job is not None or self.main_page._scan_queue:
            self._auto_pending = True
            self.main_page.append_log(
                "系统", "还有文件在扫描，扫完就自动开始（不用再点一次）", "info")
            return
        # 只跑"要跑的"：新加的（排队）和上次失败的（重试）。已跑完的不再重复解
        # ——重复解只会生成一堆 (1)(2)(3)，原包删过的话还会直接变一片失败。
        items = [t for t in self.main_page.tasks if t.runnable and needs_run(t)]
        if not items:
            skipped = sum(1 for t in self.main_page.tasks if t.runnable)
            self.main_page.append_log(
                "系统",
                ("没有待处理的任务（都跑完了；要重跑先清空列表）" if skipped
                 else "没有可执行的任务"),
                "warn",
            )
            return

        # 界面上的"解压到"就是这次的输出设置（放在最外层就是为了所见即所用）
        self.config.output_mode, self.config.output_dir, self.config.conflict = (
            self.main_page.output_values()
        )
        if self.config.output_mode == "custom" and not self.config.output_dir:
            self.main_page.append_log("系统", "选了「指定目录」但没填路径，改按原文件同目录", "warn")
            self.config.output_mode = "same"

        self.vault.reload()
        self.main_page.set_log_badge("准备中")
        where = (self.config.output_dir if self.config.output_mode == "custom" else "原文件同目录")
        self.main_page.append_log(
            "系统", f"开始：{len(items)} 个任务 · 最多 {self.config.max_depth} 层 · 解压到 {where}", "info"
        )
        self.run_started = time.monotonic()
        self.main_page.overall.setValue(0)
        self.main_page.overall_text.setText("0%")

        self.worker = JobWorker(
            self.main_page.tasks, self.vault, self.extractor, self.config, self
        )
        self.worker.sig_log.connect(self._on_log)
        self.worker.sig_debug.connect(self._on_debug)
        self.worker.sig_item.connect(self.main_page.update_item)
        self.worker.sig_ask.connect(self._on_ask_password)
        self.worker.sig_done.connect(self._on_run_done)
        self.main_page.set_running(True)
        self.main_page.set_paused(False)
        self.worker.start()

    def _pause_clicked(self) -> None:
        """「暂停」：只做暂停这一件事（继续由「开始 → 继续」负责，两者互斥）。"""
        try:
            if self.worker is None:
                self.main_page.set_paused(False)
                self.main_page.set_log_badge("")
                return
            if self.worker.is_paused():
                return                       # 已经在暂停状态：不重复下发
            self.worker.request_pause()
            self.main_page.set_paused(True)  # 文案/可用性/计时器一起摆正（互斥也在这里）
            self.main_page.set_log_badge("已暂停")
            self.main_page.append_log(
                "系统", "已暂停（正在跑的引擎已挂起，点「继续」接着跑）", "warn")
        except Exception as exc:             # noqa: BLE001
            # 信号槽里抛异常在打包版里是静默的（没有控制台），用户只会看到"点了没反应"
            self.main_page.append_log("错误", f"暂停失败：{exc!r}", "err")

    def _start_clicked(self) -> None:
        """「开始 / 继续」：跑着的时候它是「继续」，没跑的时候是「开始」。

        用户要的互斥：暂停时「开始」变成「继续」并可用、「暂停」灰掉；
        跑着的时候「开始」灰掉、「暂停」可用。所以这里按**当前状态**决定做什么，
        不依赖任何"上一次点的是哪个按钮"的记忆。
        """
        try:
            if self.worker is not None and self.worker.is_paused():
                self.worker.request_resume()
                self.main_page.set_paused(False)
                self.main_page.set_log_badge("已继续")
                self.main_page.append_log("系统", "已继续", "info")
                return
            if self.worker is None and self.main_page._paused:
                # 这批已经跑完了，界面却还停在"继续"上（跑完的一瞬间点到的）：
                # 复位，别让它一直挂着"继续"骗人
                self.main_page.set_paused(False)
                self.main_page.set_log_badge("")
                return
            self._on_start()
        except Exception as exc:             # noqa: BLE001
            self.main_page.append_log("错误", f"开始/继续失败：{exc!r}", "err")

    def _on_pause(self) -> None:
        """兼容旧入口（有测试/脚本直接调它）：等价于点一下「暂停」。"""
        self._pause_clicked()

    def _on_stop(self) -> None:
        if self.worker is None:
            return
        self._stop_requested = True        # 停过之后不要再自动接着跑
        self.worker.request_stop()
        self.main_page.set_paused(False)
        self.main_page.append_log("系统", "已停止（正在运行的引擎已掐断）", "err")
        self.main_page.set_log_badge("已停止")
        self.main_page.btn_stop.setEnabled(False)
        self.main_page.btn_pause.setEnabled(False)

    def _on_log(self, message: str) -> None:
        """工作线程的日志：写进面板，顺便把右上角徽标更新成当前层号。"""
        self.main_page.log_line(message)
        m = re.search(r"第(\d+)层", message)
        if m:
            self.main_page.set_log_badge(f"第 {m.group(1)} 层 · 穿透中")

    def _on_debug(self, message: str) -> None:
        """引擎的**细节**日志（命令行、每次密码尝试、心跳）。

        默认只在 `logs\\run.log` 里（带 `[详细]` 前缀），界面要看得点「详细」——
        以前这些行直接进面板：一次试密码能刷上百行，还把 `-p<密码>` 摊在屏幕上。
        `level="debug"` 让面板按开关决定画不画（命令行在引擎层已经打过码）。
        """
        self.main_page.append_log(time.strftime("%H:%M:%S"), message, "text_faint",
                                  level="debug")

    def _on_run_done(self, items: list[Task]) -> None:
        """一批跑完：结果写进日志 + 更新「打开输出目录」，**不切页面**。

        以前这里会跳到一张专门的「完成汇总」页——那张页的列对齐是手算的，
        任务一多/一有失败项就开始互相压字，索性不要了。
        """
        self.worker = None
        self.main_page.set_running(False)
        self.main_page.set_paused(False)
        self.main_page.set_log_badge("")
        self._refresh_overall(items)
        self.main_page.append_summary(items)
        self.main_page.update_open_button(items)
        self.library_page.reload()
        # 这一批跑完：把还没轮到的接着跑掉，别让用户再点一次开始
        if not self._auto_continue_queue():
            # 没有排队的了，但可能刚才"点了开始却还在扫描"——那就现在补上
            self._maybe_auto_start()

    def _auto_continue_queue(self) -> bool:
        """一批结束后，把**还没轮到的**（queued）接着跑掉；返回是否又开了一批。

        为什么要有这一步：用户一次性加了一批包，中途又加了几个，前一批跑完时
        后加的那些还在"排队中"——以前就停在那儿等用户再点一次「开始」，
        他的原话是"变成了还要手点一下开始的状态"。等待本身没有任何价值：他加进来
        就是要解的。

        只接 `queued`，**不碰 failed**：失败的不自动重试，否则一个永远解不开的包
        会变成死循环；重试是用户的决定（再点一次开始就是重试）。
        刚点过「停止」也不接：那样等于停不下来。
        """
        if self._stop_requested or self.worker is not None:
            return False
        queued = [t for t in self.main_page.tasks
                  if t.runnable and t.status is ItemStatus.QUEUED]
        if not queued:
            return False
        self.main_page.append_log("系统", f"清单里还有 {len(queued)} 项没轮到，接着跑", "info")
        self._auto_pending = False        # 这个批次本身就包含了它们
        self._on_start()
        return True


    def _refresh_overall(self, items: list[Task]) -> None:
        leaves = [i for i in items if i.runnable]
        total = len(leaves) or 1
        done = sum(1 for i in leaves if i.status in (Status.DONE, Status.FAILED, Status.SKIPPED))
        pct = int(done / total * 100)
        self.main_page.overall.setValue(pct)
        self.main_page.overall_text.setText(f"{pct}%")
        # 右下角的用时交给 MainPage 的计时器，这里**只在没在跑的时候**收个尾。
        # ★ 跑的过程中绝不能碰它：这个函数每更新一次进度就会被调一次，
        #   在里面 stop_clock() 等于"每次进度都停表重计"，界面上永远是"用时 0.0s"
        #   （实测截图里就是这么露出来的）。
        if not self.main_page.is_running:
            self.main_page.stop_clock()

    # ------------------------------------------------------------------
    # 「需要密码」弹窗
    # ------------------------------------------------------------------

    def _on_ask_password(self, req: AskRequest) -> None:
        """密码本试完之后的弹窗。

        这个槽必须自己扛住异常：pythonw 下没有控制台，槽里抛异常是**静默**的，
        用户看到的就是"该弹窗的时候没弹"（曾经真的这样：弹窗构造里用了已删掉的
        `masked` / `cb_common`，两个 AttributeError 一起把这条路堵死了）。
        """
        if os.environ.get("SMART_UNZIP_NO_PROMPT") == "1":
            self.main_page.append_log(
                "需要密码", f"{os.path.basename(req.archive)}：无头模式，自动跳过", "warn"
            )
            if self.worker is not None:
                self.worker.answer_password(None)
            return

        self.pending_ask = req
        tried = req.unlock.tried
        # 试过哪些**来源**（文件名/密码本/空密码）报在日志里就够了，
        # 弹窗里只留"输密码"这一件事。
        kinds = "、".join(dict.fromkeys(c.origin.label for c in tried))
        self.main_page.append_log(
            "需要密码",
            f"{os.path.basename(req.archive)}：{kinds or '候选'}都试过了"
            f"（{len(tried)} 个），等你输入",
            "warn",
        )
        password: str | None = None
        try:
            dlg = PasswordDialog(
                self.theme,
                os.path.basename(req.archive),
                verifier=lambda pw: self.extractor.test(req.archive, pw).ok,
                parent=self,
            )
            dlg.exec()
            password = dlg.accepted_password
        except Exception as exc:
            self.main_page.append_log("错误", f"密码弹窗打不开：{exc!r}", "err")
        finally:
            self.pending_ask = None

        # 不在这里写密码本：写进去的时机是"解压真的成功了"（由管道负责）。
        # 现在只是把用户输的密码交回给工作线程继续解。
        if self.worker is not None:
            self.worker.answer_password(password)


def forward_to_running(paths: list[str], auto: bool) -> bool:
    """已经有窗口在跑吗？有就把路径甩给它并返回 True（调用方直接退出）。

    连不上（没窗口在跑）返回 False —— 那就自己开一个窗口。
    """
    sock = QLocalSocket()
    sock.connectToServer(PIPE_NAME)
    if not sock.waitForConnected(400):
        return False
    payload = json.dumps({"paths": list(paths), "auto": bool(auto)}, ensure_ascii=False)
    sock.write(payload.encode("utf-8"))
    sock.flush()
    sock.waitForBytesWritten(600)
    sock.disconnectFromServer()
    return True


def _install_crash_log() -> str:
    """把界面的 stderr / 未捕获异常写进文件。

    **为什么必须做这件事**：右键菜单是用 `pythonw.exe` 起的，它没有控制台——
    Python 的 traceback、Qt 槽里抛的异常、`print` 全都掉进虚空。于是"点了没反应"
    这类 bug 只能靠猜（这一轮就踩了两次：`launch_paths` 没人读、`queue_launch`
    里调了个不存在的方法，两次都是"界面上什么都不说"）。

    写两个地方：`logs/ui.log`（**数据目录**下，位置见 core/paths.py）+ 原来的 stderr
    （用户从终端起时照旧）。打包版没有控制台，这个文件就是唯一的排错出口。
    """
    import io
    import threading
    import traceback

    target = paths_mod.log_path()
    try:
        fh = open(target, "a", encoding="utf-8", buffering=1)
    except OSError:
        return ""

    fh.write(f"\n===== {time.strftime('%Y-%m-%d %H:%M:%S')} 启动 "
             f"v{APP_VERSION} {'' if paths_mod.is_frozen() else '(源码)'} "
             f"argv={sys.argv[1:]!r} =====\n")

    class _Tee(io.TextIOBase):
        def __init__(self, mirror) -> None:
            self._mirror = mirror

        def write(self, s: str) -> int:          # type: ignore[override]
            try:
                fh.write(s)
            except Exception:                    # noqa: BLE001
                pass
            try:
                if self._mirror is not None:
                    self._mirror.write(s)
            except Exception:                    # noqa: BLE001
                pass
            return len(s)

        def flush(self) -> None:
            try:
                fh.flush()
            except Exception:                    # noqa: BLE001
                pass

    sys.stderr = _Tee(sys.__stderr__)
    sys.stdout = _Tee(sys.__stdout__)

    def hook(exc_type, exc, tb) -> None:
        fh.write("".join(traceback.format_exception(exc_type, exc, tb)))

    sys.excepthook = hook
    try:
        threading.excepthook = lambda a: hook(a.exc_type, a.exc_value, a.exc_traceback)
    except Exception:                            # noqa: BLE001
        pass
    return target


def _set_app_user_model_id() -> None:
    """给本进程一个显式的 AppUserModelID，让任务栏用窗口自己的图标。

    Windows 的任务栏按钮默认按"进程 exe"分组、显示 exe 的图标——用 pythonw 跑就
    是 Python 的图标，`setWindowIcon()` 改不动它（用户反馈："任务栏 icon 还是默认
    图标"）。设了 AppUserModelID 之后任务栏改认窗口图标。打包成 exe 之后这一步
    就不再必要（exe 自带图标），但现在就能对上。
    """
    if os.name != "nt":
        return
    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(
            "BullBull.Unpacker.1"
        )
    except Exception:                            # noqa: BLE001
        pass


def main(argv: list[str] | None = None, *, paths: list[str] | None = None,
         auto: bool | None = None) -> int:
    """开界面。`paths/auto` 允许由 run.py 先解析好（它在 import Qt 之前就能转交路径）。"""
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    from PySide6.QtGui import QGuiApplication
    from PySide6.QtWidgets import QApplication

    argv = list(sys.argv if argv is None else argv)
    if paths is None or auto is None:
        parsed_paths, parsed_auto = parse_launch_args(argv)
        paths = parsed_paths if paths is None else paths
        auto = parsed_auto if auto is None else auto

    _install_crash_log()      # pythonw 没有控制台：先把异常的去处准备好
    _set_app_user_model_id()  # 任务栏图标要认窗口图标，得先声明自己的 AppUserModelID

    # 高 DPI 策略要在建 QApplication **之前**定：PassThrough 保留 125%/150% 这种
    # 小数缩放（布局按真实比例走），配合显式 hinting/字体族，文字边缘最清楚。
    # 必须在 QApplication 之前调用，之后设置会被忽略（Qt 文档明确写了）。
    try:
        QGuiApplication.setHighDpiScaleFactorRoundingPolicy(
            Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
        )
    except Exception:                        # noqa: BLE001 - 老 Qt 没这个 API
        pass

    app = QApplication(argv)
    app.setApplicationName(APP_NAME)
    app.setFont(ui_font())                   # 统一字体 + 全量 hinting（治"字糊"）

    # 兜底：能走到这儿说明 Qt 已经加载了（run.py 那步没转交成功，或者直接
    # python -m ui.app 起来的）。先确认主实例身份，再试一次 Qt 版的转交
    if not single.become_primary() and paths:
        if forward_to_running(paths, auto):
            return 0

    w = Workbench(launch_paths=paths, launch_auto=bool(auto))
    w.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
