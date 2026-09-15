"""按 100% / 125% / 150% / 200% 四种缩放各截一遍版式图，检查排版不炸。

这台机器的物理屏是 2560x1440 且系统缩放 200%（Qt 报 dpr=2、逻辑 1280x720）。
所以用 `QT_SCALE_FACTOR` 反过来乘就能得到"等效缩放"：

    等效 100% → 0.5    等效 125% → 0.625
    等效 150% → 0.75   等效 200% → 1.0（就是本机现状）

每个缩放都必须**单独起一个进程**：`QT_SCALE_FACTOR` 只在 `QApplication` 建之前读，
进程内改不掉。子进程里跑 `tools/shots.py`（`SHOT_DPI_ONLY=1` → 只截版式那几张，
不跑真实解压），输出落在 `shots/dpi-<百分比>/`。

用法：
    .venv\\Scripts\\python.exe tools\\shots_dpi.py
"""

from __future__ import annotations

import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# (QT_SCALE_FACTOR, 目录后缀, 说明)——按等效缩放从低到高
STEPS = (
    ("0.5", "100", "等效 100%"),
    ("0.625", "125", "等效 125%"),
    ("0.75", "150", "等效 150%"),
    ("1.0", "200", "等效 200%（本机系统缩放）"),
)


def main() -> int:
    py = os.path.join(ROOT, ".venv", "Scripts", "python.exe")
    shots = os.path.join(ROOT, "tools", "shots.py")
    bad = 0
    for factor, tag, note in STEPS:
        env = dict(os.environ)
        env["QT_SCALE_FACTOR"] = factor
        env["SHOT_DPI_ONLY"] = "1"
        env["SHOT_OUT"] = os.path.join("shots", f"dpi-{tag}")
        env["PYTHONIOENCODING"] = "utf-8"
        print(f"\n===== {note} (QT_SCALE_FACTOR={factor}) → shots/dpi-{tag}/ =====",
              flush=True)
        rc = subprocess.call([py, shots], cwd=ROOT, env=env)
        if rc != 0:
            bad += 1
            print(f"  !! 这个缩放下 shots.py 退出码 {rc}", flush=True)
    print(f"\n完成：{len(STEPS) - bad}/{len(STEPS)} 个缩放跑通")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
