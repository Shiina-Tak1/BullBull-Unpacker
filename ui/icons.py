"""矢量图标：一套手画的线框图标，颜色/大小跟着主题走。

**为什么不用 `⚙ ◻ ✕ ▶ ⏸ ⏹ 📂` 这些字符当图标**：它们来自系统符号字体或 emoji 字体，
字号、粗细、基线、留白都不一致（同一个按钮行里"齿轮"比"叉"胖一圈），
高 DPI 下还常常是位图缩放 → 既糊又风格不统一（用户两条反馈都指这个）。

这里全部用 `QPainter` 按同一套规则画：
  * 画布 size×size，实际笔迹留 12% 内边距；
  * 线宽 = size/8（圆头圆角），填充类（播放/停止）用实心；
  * 颜色由调用方给（一般传当前主题的前景色或主色）；
  * 结果按 (名字, 颜色, 尺寸, dpr) 缓存 —— 每次重画没必要。
"""

from __future__ import annotations

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QIcon, QPainter, QPainterPath, QPen, QPixmap

# 统一的绘制规则
_PAD_RATIO = 0.12          # 四周留白
_WIDTH_RATIO = 1 / 8       # 线宽相对画布
_CACHE: dict[tuple, QIcon] = {}


def _pen(color: QColor, size: int, scale: float = 1.0) -> QPen:
    pen = QPen(color, max(1.0, size * _WIDTH_RATIO * scale))
    pen.setCapStyle(Qt.PenCapStyle.RoundCap)
    pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
    return pen


def _draw(p: QPainter, name: str, size: int, color: QColor) -> None:
    """在 size×size 的画布上画一个图标（坐标都按 0..1 比例写，好缩放）。"""
    pad = size * _PAD_RATIO
    box = size - 2 * pad

    def pt(x: float, y: float) -> QPointF:
        return QPointF(pad + box * x, pad + box * y)

    def rect(x: float, y: float, w: float, h: float) -> QRectF:
        return QRectF(pad + box * x, pad + box * y, box * w, box * h)

    p.setPen(_pen(color, size))
    p.setBrush(Qt.BrushStyle.NoBrush)

    if name == "settings":                     # 齿轮：一个圆 + 八根齿
        p.drawEllipse(rect(0.28, 0.28, 0.44, 0.44))
        for i in range(8):
            import math

            a = math.radians(i * 45)
            r0, r1 = 0.34, 0.5
            cx, cy = 0.5, 0.5
            p.drawLine(pt(cx + r0 * math.cos(a), cy + r0 * math.sin(a)),
                       pt(cx + r1 * math.cos(a), cy + r1 * math.sin(a)))

    elif name == "min":                         # 最小化：一条横线
        p.drawLine(pt(0.18, 0.5), pt(0.82, 0.5))

    elif name == "max":                         # 最大化：一个方框
        p.drawRect(rect(0.18, 0.18, 0.64, 0.64))

    elif name == "restore":                     # 还原：两个错开的方框
        p.drawRect(rect(0.14, 0.28, 0.56, 0.56))
        path = QPainterPath(pt(0.34, 0.28))
        path.lineTo(pt(0.34, 0.14))
        path.lineTo(pt(0.86, 0.14))
        path.lineTo(pt(0.86, 0.66))
        path.lineTo(pt(0.72, 0.66))
        p.drawPath(path)

    elif name == "close":                       # 关闭：叉
        p.drawLine(pt(0.22, 0.22), pt(0.78, 0.78))
        p.drawLine(pt(0.78, 0.22), pt(0.22, 0.78))

    elif name == "play":                        # 开始：实心三角
        p.setBrush(color)
        path = QPainterPath(pt(0.24, 0.14))
        path.lineTo(pt(0.86, 0.5))
        path.lineTo(pt(0.24, 0.86))
        path.closeSubpath()
        p.drawPath(path)

    elif name == "pause":                       # 暂停：两根竖条
        p.setBrush(color)
        p.drawRoundedRect(rect(0.26, 0.16, 0.16, 0.68), box * 0.05, box * 0.05)
        p.drawRoundedRect(rect(0.58, 0.16, 0.16, 0.68), box * 0.05, box * 0.05)

    elif name == "stop":                        # 停止：实心圆角方块
        p.setBrush(color)
        p.drawRoundedRect(rect(0.22, 0.22, 0.56, 0.56), box * 0.08, box * 0.08)

    elif name == "file-plus":                   # 添加文件：纸 + 加号
        path = QPainterPath(pt(0.16, 0.10))
        path.lineTo(pt(0.58, 0.10))
        path.lineTo(pt(0.80, 0.32))
        path.lineTo(pt(0.80, 0.90))
        path.lineTo(pt(0.16, 0.90))
        path.closeSubpath()
        p.drawPath(path)
        p.drawLine(pt(0.58, 0.10), pt(0.58, 0.32))
        p.drawLine(pt(0.58, 0.32), pt(0.80, 0.32))

    elif name == "folder-plus":                 # 添加文件夹：文件夹 + 加号
        path = QPainterPath(pt(0.10, 0.24))
        path.lineTo(pt(0.40, 0.24))
        path.lineTo(pt(0.48, 0.36))
        path.lineTo(pt(0.90, 0.36))
        path.lineTo(pt(0.90, 0.82))
        path.lineTo(pt(0.10, 0.82))
        path.closeSubpath()
        p.drawPath(path)

    elif name == "trash":                       # 清除选中：垃圾桶
        p.drawLine(pt(0.14, 0.24), pt(0.86, 0.24))
        p.drawLine(pt(0.38, 0.24), pt(0.38, 0.12))
        p.drawLine(pt(0.38, 0.12), pt(0.62, 0.12))
        p.drawLine(pt(0.62, 0.12), pt(0.62, 0.24))
        path = QPainterPath(pt(0.22, 0.24))
        path.lineTo(pt(0.28, 0.90))
        path.lineTo(pt(0.72, 0.90))
        path.lineTo(pt(0.78, 0.24))
        p.drawPath(path)
        p.drawLine(pt(0.42, 0.40), pt(0.42, 0.76))
        p.drawLine(pt(0.58, 0.40), pt(0.58, 0.76))

    elif name == "clear":                       # 清空列表：橡皮/斜杠扫帚
        path = QPainterPath(pt(0.14, 0.72))
        path.lineTo(pt(0.62, 0.16))
        path.lineTo(pt(0.86, 0.34))
        path.lineTo(pt(0.38, 0.90))
        path.closeSubpath()
        p.drawPath(path)
        p.drawLine(pt(0.30, 0.82), pt(0.52, 0.60))

    elif name == "key":                         # 密码本：钥匙
        p.drawEllipse(rect(0.10, 0.34, 0.40, 0.40))
        p.drawLine(pt(0.46, 0.54), pt(0.92, 0.54))
        p.drawLine(pt(0.80, 0.54), pt(0.80, 0.72))
        p.drawLine(pt(0.64, 0.54), pt(0.64, 0.68))

    elif name == "open-folder":                 # 打开输出目录：文件夹 + 箭头
        path = QPainterPath(pt(0.08, 0.28))
        path.lineTo(pt(0.38, 0.28))
        path.lineTo(pt(0.46, 0.40))
        path.lineTo(pt(0.92, 0.40))
        path.lineTo(pt(0.92, 0.84))
        path.lineTo(pt(0.08, 0.84))
        path.closeSubpath()
        p.drawPath(path)

    elif name == "folder-out":                  # 解压到：文件夹 + 向下箭头
        path = QPainterPath(pt(0.08, 0.24))
        path.lineTo(pt(0.38, 0.24))
        path.lineTo(pt(0.46, 0.36))
        path.lineTo(pt(0.92, 0.36))
        path.lineTo(pt(0.92, 0.86))
        path.lineTo(pt(0.08, 0.86))
        path.closeSubpath()
        p.drawPath(path)
        p.drawLine(pt(0.50, 0.50), pt(0.50, 0.74))

    elif name == "arrow-down":                  # 拖拽区那个大箭头
        p.drawLine(pt(0.50, 0.12), pt(0.50, 0.72))
        p.drawLine(pt(0.24, 0.48), pt(0.50, 0.74))
        p.drawLine(pt(0.76, 0.48), pt(0.50, 0.74))
        p.drawLine(pt(0.16, 0.88), pt(0.84, 0.88))

    elif name == "check":                       # 状态：对勾
        p.drawLine(pt(0.18, 0.54), pt(0.40, 0.76))
        p.drawLine(pt(0.40, 0.76), pt(0.84, 0.24))

    elif name == "warn":                        # 状态：感叹号三角
        path = QPainterPath(pt(0.50, 0.10))
        path.lineTo(pt(0.94, 0.88))
        path.lineTo(pt(0.06, 0.88))
        path.closeSubpath()
        p.drawPath(path)
        p.drawLine(pt(0.50, 0.38), pt(0.50, 0.60))
        p.drawPoint(pt(0.50, 0.74))

    else:                                       # 兜底：一个圆点，别画不出来
        p.drawEllipse(rect(0.34, 0.34, 0.32, 0.32))


def icon(name: str, color: str | QColor, size: int = 18, dpr: float = 1.0) -> QIcon:
    """取一个图标（带缓存）。`color` 传主题色名字符串或 QColor 都行。"""
    key_color = color.name() if isinstance(color, QColor) else str(color)
    key = (name, key_color, size, round(float(dpr), 2))
    hit = _CACHE.get(key)
    if hit is not None:
        return hit

    qc = QColor(key_color)
    pm = QPixmap(int(size * dpr), int(size * dpr))
    pm.setDevicePixelRatio(dpr)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing, True)
    _draw(p, name, size, qc)
    p.end()
    ic = QIcon(pm)
    _CACHE[key] = ic
    return ic


def clear_cache() -> None:
    """换主题时清缓存（颜色变了）。"""
    _CACHE.clear()


def icon_names() -> tuple[str, ...]:
    """所有可用名字（测试用：保证每个名字都画得出来）。"""
    return ("settings", "min", "max", "restore", "close", "play", "pause", "stop",
            "file-plus", "folder-plus", "trash", "clear", "key", "open-folder",
            "folder-out", "arrow-down", "check", "warn")
