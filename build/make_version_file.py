"""生成 PyInstaller 的版本资源文件（`build/version.txt`）。

为什么要生成而不是手写：版本号只在 `core/appinfo.py` 写一遍，
exe 的"属性 → 详细信息"必须跟它一致——手写迟早对不上。
打包脚本里跑一次即可：

    .venv\\Scripts\\python build\\make_version_file.py

产物 `build/version.txt` 给 PyInstaller 的 `--version-file` 用。
"""

from __future__ import annotations

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from core.appinfo import APP_NAME, VERSION  # noqa: E402

TEMPLATE = """VSVersionInfo(
  ffi=FixedFileInfo(
    filevers={nums},
    prodvers={nums},
    mask=0x3f,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
  ),
  kids=[
    StringFileInfo([
      StringTable(
        '080404B0',
        [StringStruct('CompanyName', {company!r}),
         StringStruct('FileDescription', {desc!r}),
         StringStruct('FileVersion', {ver!r}),
         StringStruct('InternalName', {internal!r}),
         StringStruct('LegalCopyright', {copyright!r}),
         StringStruct('OriginalFilename', {filename!r}),
         StringStruct('ProductName', {product!r}),
         StringStruct('ProductVersion', {ver!r})])
    ]),
    VarFileInfo([VarStruct('Translation', [2052, 1200])])
  ]
)
"""


def main() -> int:
    parts = VERSION.split(".")
    while len(parts) < 4:
        parts.append("0")
    nums = "(" + ", ".join(str(int(p)) for p in parts[:4]) + ")"
    text = TEMPLATE.format(
        nums=nums,
        company="BullBull",
        desc=APP_NAME,
        ver=VERSION,
        internal=APP_NAME.replace(" ", ""),
        copyright="",
        filename=APP_NAME + ".exe",
        product=APP_NAME,
        # 0804 = 简体中文, 04B0 = Unicode
    )
    out = os.path.join(ROOT, "build", "version.txt")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"已生成 {out}（{APP_NAME} {VERSION}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
