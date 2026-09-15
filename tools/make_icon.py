"""把工程根目录的 `icon.png` 转成窗口/右键菜单用的 `assets/bbu.ico`。

为什么不直接让 Qt 存 ICO：Qt 的 ICO 后端不一定带写入支持。所以这里自己拼 ICO 容器
——Vista 以后的 ICO 允许直接内嵌 PNG，一个头 + 若干张 PNG 就完事。
（`icon.png` 是用户给的原图，这里按需缩放出多档尺寸。）

用法（换了 icon.png 之后重跑一次）：
    .venv\\Scripts\\python.exe tools\\make_icon.py
"""

from __future__ import annotations

import os
import struct
import sys

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from PySide6.QtCore import QBuffer, QByteArray, Qt  # noqa: E402
from PySide6.QtGui import QImage, QPainter  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

SRC = os.path.join(ROOT, "icon.png")
OUT = os.path.join(ROOT, "assets", "bbu.ico")
# 16/32 是右键菜单和任务栏小图标，256 是资源管理器大图标
SIZES = (16, 24, 32, 48, 64, 128, 256)


def png_bytes(img: QImage) -> bytes:
    """QImage → PNG 字节。

    注意：QBuffer **不持有** QByteArray，传临时对象进去会立刻悬空 → 0xC0000005（踩过）。
    """
    store = QByteArray()
    buf = QBuffer(store)
    buf.open(QBuffer.OpenModeFlag.WriteOnly)
    img.save(buf, "PNG")
    buf.close()
    return bytes(store)


def build_ico(pngs: dict[int, bytes]) -> bytes:
    """按 ICO 格式拼容器：目录头 + 每张图的目录项 + 各自的 PNG 数据。"""
    count = len(pngs)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries, blobs = b"", b""
    for size, data in sorted(pngs.items()):
        dim = 0 if size >= 256 else size      # 256 在 ICO 里记作 0
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


def scaled_png(src: QImage, size: int) -> bytes:
    """等比缩放到 size×size 的透明画布正中（原图不是正方形也不会变形）。"""
    inner = src.scaled(size, size, Qt.AspectRatioMode.KeepAspectRatio,
                       Qt.TransformationMode.SmoothTransformation)
    canvas = QImage(size, size, QImage.Format.Format_ARGB32)
    canvas.fill(Qt.GlobalColor.transparent)
    p = QPainter(canvas)
    p.drawImage((size - inner.width()) // 2, (size - inner.height()) // 2, inner)
    p.end()
    return png_bytes(canvas)


def main() -> int:
    if not os.path.isfile(SRC):
        print(f"没有 {SRC}：把图标 PNG 放到工程根目录再跑")
        return 2
    QApplication([])
    src = QImage(SRC)
    if src.isNull():
        print(f"读不出 {SRC}")
        return 2
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    ico = build_ico({size: scaled_png(src, size) for size in SIZES})
    with open(OUT, "wb") as f:
        f.write(ico)
    print(f"saved {OUT}  ({src.width()}x{src.height()} → {len(ico)} bytes, "
          f"{', '.join(str(s) for s in SIZES)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
