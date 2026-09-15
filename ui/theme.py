"""主题与配色。

只用一个主色（青绿），状态色语义固定：
    绿=成功/就绪   琥珀=排队/需注意   红=失败/缺失   蓝=进行中
全屏统一，不重复定义，这是"不乱"的关键。
"""

from __future__ import annotations

# 字体栈：中文优先微软雅黑 UI，英文/数字回落 Segoe UI。两边 hinting 都成熟，
# 高 DPI 下比"随便一个 system-ui"清晰得多（用户反馈过字糊）。
FONT_STACK = '"Microsoft YaHei UI", "Segoe UI", "Microsoft YaHei", sans-serif'
MONO_STACK = '"Cascadia Mono", "Consolas", "Microsoft YaHei UI", monospace'

# --------------------------------------------------------------------------
# 深色（默认）
# --------------------------------------------------------------------------
DARK = {
    "bg": "#1B1D21",
    "surface": "#25282E",
    "surface_2": "#2D3138",
    "border": "#34383F",
    "border_strong": "#41464F",
    "text": "#E6E8EB",
    "text_dim": "#9AA1AC",
    "text_faint": "#6B727E",
    "accent": "#3DDC97",
    "accent_hover": "#55E6AC",
    "accent_text": "#12261D",
    "ok": "#3DDC97",
    "warn": "#FFB020",
    "err": "#FF5C5C",
    "info": "#4EA1FF",
    "header_bg": "#20232900",
    "row_hover": "#2A2E35",
    "row_selected": "#31414A",
    "scroll": "#3A3F47",
}

# --------------------------------------------------------------------------
# 浅色
# --------------------------------------------------------------------------
LIGHT = {
    "bg": "#F4F5F7",
    "surface": "#FFFFFF",
    "surface_2": "#ECEEF1",
    "border": "#DDE0E5",
    "border_strong": "#C7CCD4",
    "text": "#1E2126",
    "text_dim": "#5C636E",
    "text_faint": "#8A929D",
    "accent": "#12B981",
    "accent_hover": "#0FA472",
    "accent_text": "#FFFFFF",
    "ok": "#12B981",
    "warn": "#C77700",
    "err": "#E5484D",
    "info": "#2F7DE1",
    "header_bg": "#00000000",
    "row_hover": "#F0F2F5",
    "row_selected": "#DFF5EC",
    "scroll": "#C7CCD4",
}

_KEYS = list(DARK.keys())


def resolve(mode: str) -> str:
    """把配置里的 theme 值（dark / light / system）落成实际要用的那一套。

    "跟随系统"在 Qt 6.5+ 可以问 QStyleHints；问不到就退回深色——
    深色是这个工具的默认脸，退回它比退回浅色更不突兀。
    """
    if mode in ("dark", "light"):
        return mode
    try:
        from PySide6.QtCore import Qt
        from PySide6.QtGui import QGuiApplication

        scheme = QGuiApplication.styleHints().colorScheme()
        if scheme == Qt.ColorScheme.Light:
            return "light"
        if scheme == Qt.ColorScheme.Dark:
            return "dark"
    except Exception:
        pass
    return "dark"


class Theme:
    """当前主题；切换时重建 QSS。"""

    def __init__(self, mode: str = "dark") -> None:
        self.mode = mode
        self._c = DARK if mode == "dark" else LIGHT

    @property
    def c(self) -> dict[str, str]:
        return self._c

    def color(self, role: str) -> str:
        return self._c[role]

    def set_mode(self, mode: str) -> None:
        self.mode = mode if mode in ("dark", "light") else "dark"
        self._c = DARK if self.mode == "dark" else LIGHT

    # ----------------------------------------------------------------
    def qss(self) -> str:
        c = self._c
        return f"""
/* ============ 基础 ============ */
QWidget {{
    color: {c['text']};
    font-family: {FONT_STACK};
    font-size: 13px;
}}
#Workbench {{ background: {c['bg']}; }}

/* ============ 卡片 ============ */
/* 圆角统一：外层窗口用 DWM 原生圆角（见 ui/app.py 的 _round_window_corners），
   里面的卡片统一 10px，边框色也统一，视觉上才是一套 */
#Card, #TopBar, #Sidebar {{
    background: {c['surface']};
    border: 1px solid {c['border']};
    border-radius: 10px;
}}
#InnerCard {{
    background: {c['surface_2']};
    border: 1px solid {c['border']};
    border-radius: 8px;
}}
#DropZone {{
    background: transparent;
    border: 1px dashed {c['border_strong']};
    border-radius: 10px;
}}
#DropZone[dragActive="true"] {{
    border: 1px dashed {c['accent']};
    background: {c['surface_2']};
}}

/* ============ 文字层级 ============ */
#Title      {{ font-size: 21px; font-weight: 800; letter-spacing: 0.4px; }}
#Version    {{ color: {c['text_faint']}; font-size: 12px; }}
#SectionTitle {{ color: {c['text_dim']}; font-size: 12px; font-weight: 600; letter-spacing: 1px; }}
#Dim        {{ color: {c['text_dim']}; }}
#Faint      {{ color: {c['text_faint']}; font-size: 12px; }}
#Mono       {{ font-family: {MONO_STACK}; }}
#StatNum    {{ font-size: 24px; font-weight: 600; }}
#StatLabel  {{ color: {c['text_dim']}; font-size: 12px; }}
#Hint       {{ color: {c['text_faint']}; font-size: 11px; }}

/* ============ 窗口按钮 / 图标按钮 ============ */
#WinBtn {{
    background: transparent;
    border: 1px solid transparent;
    border-radius: 7px;
    padding: 0;
}}
#WinBtn:hover {{ background: {c['row_hover']}; border-color: {c['border']}; }}
#WinBtn:pressed {{ background: {c['surface_2']}; }}
#WinBtnClose:hover {{ background: {c['err']}; border-color: {c['err']}; }}
#ToolIcon {{
    background: transparent;
    border: 1px solid transparent;
    border-radius: 8px;
    padding: 0;
}}
#ToolIcon:hover {{ background: {c['row_hover']}; border-color: {c['border']}; }}

/* ============ 按钮 ============ */
QPushButton {{
    background: {c['surface_2']};
    color: {c['text']};
    border: 1px solid {c['border_strong']};
    border-radius: 6px;
    padding: 6px 14px;
    min-height: 18px;
}}
QPushButton:hover   {{ background: {c['row_hover']}; border-color: {c['accent']}; }}
/* 主界面那一排操作按钮（开始/暂停/停止 + 清单操作）：内边距收窄一点。
   为什么单独给一条规则：这一行有 7 个按钮，默认 padding 下"全带文字"要 802px，
   而默认开窗只有约 720px 可用 —— 差一点就会挤在一起/裁字（用户报过）。
   收窄后约 700px，默认尺寸下文字全放得下；再窄就由 `_apply_compact()` 分档收成图标。 */
#RowBtn, #Primary {{ padding: 6px 10px; }}
QPushButton:pressed {{ background: {c['surface']}; }}
QPushButton:disabled {{
    color: {c['text_faint']};
    background: {c['surface']};
    border-color: {c['border']};
}}
#Primary {{
    background: {c['accent']};
    color: {c['accent_text']};
    border: 1px solid {c['accent']};
    font-weight: 600;
}}
#Primary:hover   {{ background: {c['accent_hover']}; border-color: {c['accent_hover']}; }}
#Primary:disabled {{ background: {c['surface_2']}; color: {c['text_faint']}; border-color: {c['border']}; }}
#Danger {{ color: {c['err']}; }}
#Ghost {{
    background: transparent;
    border: 1px solid transparent;
    color: {c['text_dim']};
    padding: 4px 8px;
}}
#Ghost:hover {{ background: {c['surface_2']}; color: {c['text']}; border-color: {c['border']}; }}
#Chip {{
    background: {c['surface_2']};
    color: {c['text_dim']};
    border: 1px solid {c['border']};
    border-radius: 10px;
    padding: 2px 9px;
    font-size: 11px;
}}
#Link {{ background: transparent; border: none; color: {c['accent']}; padding: 2px 4px; }}
#Link:hover {{ color: {c['accent_hover']}; text-decoration: underline; }}

/* ============ 输入 ============ */
QLineEdit, QPlainTextEdit, QSpinBox, QDoubleSpinBox, QComboBox {{
    background: {c['bg']};
    color: {c['text']};
    border: 1px solid {c['border_strong']};
    border-radius: 6px;
    padding: 6px 9px;
    selection-background-color: {c['accent']};
    selection-color: {c['accent_text']};
}}
QLineEdit:focus, QPlainTextEdit:focus, QSpinBox:focus, QComboBox:focus {{
    border-color: {c['accent']};
}}
QLineEdit:disabled, QSpinBox:disabled {{ color: {c['text_faint']}; }}
QSpinBox::up-button, QSpinBox::down-button,
QDoubleSpinBox::up-button, QDoubleSpinBox::down-button {{ width: 16px; }}
QComboBox::drop-down {{ border: none; width: 22px; }}
QComboBox QAbstractItemView {{
    background: {c['surface']};
    border: 1px solid {c['border_strong']};
    selection-background-color: {c['surface_2']};
    selection-color: {c['text']};
    outline: none;
}}

/* ============ 表格 ============ */
QTableWidget {{
    background: {c['surface']};
    alternate-background-color: {c['bg']};
    border: 1px solid {c['border']};
    border-radius: 8px;
    gridline-color: transparent;
    outline: none;
}}
QTableWidget::item {{
    padding: 6px 8px;
    border-left: none;
    border-top: none;
    border-bottom: none;
    border-right: 1px solid {c['border']};
}}
QTableWidget::item:selected {{
    background: {c['row_selected']};
    color: {c['text']};
}}
QHeaderView {{ background: transparent; border: none; }}
QHeaderView::section {{
    background: {c['surface']};
    color: {c['text_faint']};
    padding: 8px;
    border-left: none;
    border-top: none;
    border-bottom: 1px solid {c['border']};
    /* 纵向分割线：表头跟表体对齐，拖列宽时能看清边界 */
    border-right: 1px solid {c['border']};
    font-size: 12px;
    font-weight: 600;
}}
QTableCornerButton::section {{ background: {c['surface']}; border: none; }}

/* ============ 日志 ============ */
#LogView {{
    background: {c['bg']};
    border: 1px solid {c['border']};
    border-radius: 8px;
    color: {c['text_dim']};
    font-family: "Cascadia Mono", "Consolas", monospace;
    font-size: 12px;
    padding: 6px;
}}

/* ============ 进度条 ============ */
QProgressBar {{
    background: {c['surface_2']};
    border: none;
    border-radius: 4px;
    height: 8px;
    text-align: center;
    color: transparent;
}}
QProgressBar::chunk {{
    background: {c['accent']};
    border-radius: 4px;
}}

/* ============ 复选框 / 单选 ============ */
QCheckBox, QRadioButton {{ spacing: 7px; }}
QCheckBox::indicator, QRadioButton::indicator {{
    width: 15px; height: 15px;
    border: 1px solid {c['border_strong']};
    background: {c['bg']};
}}
QCheckBox::indicator    {{ border-radius: 4px; }}
QRadioButton::indicator {{ border-radius: 8px; }}
QCheckBox::indicator:checked, QRadioButton::indicator:checked {{
    background: {c['accent']};
    border-color: {c['accent']};
}}
QCheckBox::indicator:hover, QRadioButton::indicator:hover {{ border-color: {c['accent']}; }}

/* ============ 列表 / 树 ============ */
QListWidget, QTreeWidget {{
    background: {c['surface']};
    border: 1px solid {c['border']};
    border-radius: 8px;
    outline: none;
    padding: 4px;
}}
QListWidget::item {{ padding: 7px 9px; border-radius: 6px; }}
QListWidget::item:hover {{ background: {c['row_hover']}; }}
QListWidget::item:selected {{ background: {c['row_selected']}; color: {c['text']}; }}

/* ============ 滚动区（不写这两条，设置页右侧会漏出白色 viewport）============ */
QScrollArea {{ background: transparent; border: none; }}
QScrollArea > QWidget > QWidget {{ background: transparent; }}

/* ============ 滚动条 ============ */
QScrollBar:vertical {{
    background: transparent; width: 10px; margin: 2px;
}}
QScrollBar::handle:vertical {{
    background: {c['scroll']}; border-radius: 5px; min-height: 28px;
}}
QScrollBar::handle:vertical:hover {{ background: {c['border_strong']}; }}
QScrollBar:horizontal {{ background: transparent; height: 10px; margin: 2px; }}
QScrollBar::handle:horizontal {{
    background: {c['scroll']}; border-radius: 5px; min-width: 28px;
}}
QScrollBar::add-line, QScrollBar::sub-line {{ width: 0; height: 0; }}
QScrollBar::add-page, QScrollBar::sub-page {{ background: transparent; }}

/* ============ 抽屉 / 弹窗 ============ */
#Drawer {{
    background: {c['surface']};
    border-left: 1px solid {c['border_strong']};
}}
#Dialog {{ background: {c['surface']}; border: 1px solid {c['border_strong']}; border-radius: 10px; }}
#Separator {{ background: {c['border']}; max-height: 1px; min-height: 1px; border: none; }}
#VDivider {{ background: {c['border']}; max-width: 1px; min-width: 1px; border: none; }}

/* ============ 工具提示 ============ */
QToolTip {{
    background: {c['surface_2']};
    color: {c['text']};
    border: 1px solid {c['border_strong']};
    padding: 5px 8px;
    border-radius: 6px;
}}
"""
