# 第三方组件与许可

BullBull Unpacker 的便携版分发包中包含以下第三方组件。本文件说明各组件的内容、版权归属与许可条款。

---

## 1. 7-Zip ZS

- **说明**：7-Zip 的社区增强分支（由 Tino Reichardt 维护），在 7-Zip 内核基础上增加了
  `lz4 / lz5 / lizard / zstd / brotli` 等编解码器，并支持将裸 `.lz4` / `.lz5` / `.zst`
  帧作为单文件压缩包读取。本程序将其作为主解压引擎。
- **版本**：7-Zip 26.02 ZS v1.5.7 R1 (x64)
- **版权**：Copyright (c) 1999- Igor Pavlov, 2016- Tino Reichardt, 2022- Sergey G. Brester
- **上游项目**：
  - 7-Zip：<https://www.7-zip.org/>
  - ZS 分支：<https://github.com/mcmilk/7-Zip-zstd>
- **许可**：
  - `7z.dll` / `7z.exe` 主体遵循 **GNU LGPL**，部分代码为 LGPL + **unRAR license restriction**，
    另有部分为 BSD 3-clause / BSD 2-clause。
  - 完整许可与文件清单见随包分发的 `tools/7z/License.txt`。
  - 变更历史见 `tools/7z/History-7zip-zs.txt`。
- **使用范围**：本程序仅在读取 RAR 时调用 7-Zip ZS，不涉及 RAR 压缩功能的实现。

## 2. 编解码器（随 7-Zip ZS 分发）

`lz4 / lz5 / lizard / zstd / brotli` 等编解码器由各自上游项目提供，许可为 MIT / BSD 类（具体条款各有不同）。其许可声明随 7-Zip ZS 发行包一并提供，详情请参阅各上游项目仓库。

## 3. PySide6 / Qt

- **说明**：Qt for Python，本程序的界面框架。
- **版本**：PySide6 6.11.2（Qt 6）
- **上游项目**：<https://www.qt.io/qt-for-python>
- **许可**：**LGPLv3**（另有 GPLv3 与商业授权可选）。
- **分发要求**：
  - 随包提供 LGPLv3 许可文本及「使用了 Qt/PySide6」的声明。
  - 使用者需能够替换 Qt/PySide6 库。本程序采用 PyInstaller 的 onedir 形式分发，`_internal\`
    目录下的 Qt DLL 为独立文件，可直接替换，满足 LGPL 的相关要求。
  - 本程序未修改 Qt/PySide6 源码。

## 4. WinRAR

加密的 RAR5 压缩包需要系统中安装 WinRAR（`Rar.exe`）。WinRAR 为商业软件，本项目不分发、不修改、不打包该组件，仅在运行时调用用户自行安装的版本。用户需自行获取合法授权并遵守其许可条款。

## 5. 其他

- 图标 `assets\bbu.ico` 由本项目自带的 `icon.png` 生成（`tools\make_icon.py`），为本项目资产。
- 本程序不包含任何网络通信组件，也不包含任何统计或上报 SDK。

---

## 本项目许可

本项目采用 **GNU General Public License v3.0**，全文见仓库根目录 `LICENSE`。

- 你可以自由使用、修改和再分发本程序。再分发（包括修改版）必须同样以 GPL-3.0 授权，
  并提供对应源代码。本程序不提供任何担保，详见 `LICENSE` 第 15、16 节。
- 分发二进制包（便携版）时，请将本文件与 `tools/7z/License.txt` 一并包含在分发包中，
  并在下载页面提供源码仓库地址，以满足 GPL-3.0 第 6 节的要求。

### 关于 unRAR 许可的兼容性说明

7-Zip ZS 中包含的 unRAR 代码遵循「LGPL + 不得用于实现 RAR 压缩」的许可条款。自由软件基金会认为该限制与 GPL 不兼容。本项目对此的处理如下：

- 仅在读取 RAR 时调用 7-Zip ZS，不实现 RAR 压缩功能；
- 不修改 7-Zip ZS 的任何代码，仅作为独立可执行程序调用；
- 如对合规性有严格要求，可考虑将 `tools\7z\` 替换为不含 unRAR 的 7-Zip 构建版本，  或由用户自行安装 7-Zip。