<div align="center">
<img src="src/Bbu.App/Assets/icon.png" alt="BullBull Unpacker" width="128">

# BullBull Unpacker

**简体中文** · [English](README.en.md)

BullBull Unpacker / 牛牛解压器是一个 Windows 上的嵌套 · 伪装压缩包解压工具

[![构建](https://img.shields.io/github/actions/workflow/status/Shiina-Tak1/BullBull-Unpacker/ci.yml?style=flat&label=构建)](https://github.com/Shiina-Tak1/BullBull-Unpacker/actions/workflows/ci.yml)
[![发布](https://img.shields.io/github/actions/workflow/status/Shiina-Tak1/BullBull-Unpacker/release.yml?style=flat&label=发布)](https://github.com/Shiina-Tak1/BullBull-Unpacker/actions/workflows/release.yml)
[![最新版本](https://img.shields.io/github/v/release/Shiina-Tak1/BullBull-Unpacker?style=flat&label=最新版本&color=36AFA0)](https://github.com/Shiina-Tak1/BullBull-Unpacker/releases/latest)
[![许可证](https://img.shields.io/badge/许可证-GPLv3-2780C2?style=flat)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Avalonia-8B44AC?style=flat)](https://avaloniaui.net/)

</div>

## 功能特点

- 支持 zip / 7z / rar / rar5 / tar / gz / bz2 / xz / zstd / lz4 / lz5 / brotli / cab 等格式
- 识别并解压伪装为 mp4 / jpg 等格式的压缩包
- 解压嵌套压缩包，自动整理外层文件夹，并删除中间压缩包
- 自动记录解压密码，支持手动添加、修改和删除
- 集成资源管理器右键菜单，可一键将文件加入解压清单

## 密码记忆

按照固定顺序尝试密码：从文件名中提取的密码、密码本中的密码（按成功次数降序）、弹窗输入的密码。弹窗输入的密码仅在实际解压成功后才会记录到密码本。

密码本为纯文本文件，可通过软件内置功能添加或删除密码，也可使用记事本等文本编辑器直接修改。每行一条记录，格式如下：

- `密码<TAB>成功次数`：记录密码及其成功使用次数。
- `密码`：仅记录密码，未指定成功次数。

当密码包含换行符、TAB 等特殊字符时，整条密码将以 `\\B64:` 为前缀，后接标准 Base64 编码。编码对象为密码的 UTF-8 字节序列。程序读取时会自动还原，无需手动处理。

## 下载

[![GitHub Releases](https://img.shields.io/badge/下载-GitHub%20Releases-181717?style=flat&logo=github)](https://github.com/Shiina-Tak1/BullBull-Unpacker/releases)
[![Microsoft Store](https://img.shields.io/badge/下载-Microsoft%20Store-0078D4?style=flat&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyMyAyMyI%2BPHBhdGggZmlsbD0iI2YzZjNmMyIgZD0iTTAgMGgyM3YyM0gweiIvPjxwYXRoIGZpbGw9IiNmMzUzMjUiIGQ9Ik0xIDFoMTB2MTBIMXoiLz48cGF0aCBmaWxsPSIjODFiYzA2IiBkPSJNMTIgMWgxMHYxMEgxMnoiLz48cGF0aCBmaWxsPSIjMDVhNmYwIiBkPSJNMSAxMmgxMHYxMEgxeiIvPjxwYXRoIGZpbGw9IiNmZmJhMDgiIGQ9Ik0xMiAxMmgxMHYxMEgxMnoiLz48L3N2Zz4%3D)](https://apps.microsoft.com/detail/9NJ532JX69VK)
[![百度网盘](https://img.shields.io/badge/下载-Baidu%20Netdisk-2932E1?style=flat&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMDAgMTAwIj48cGF0aCBmaWxsPSIjZmZmZmZmIiBkPSJNODguNiw0Ni4xYy00LjYtNC42LTEwLjgtNi43LTE2LjgtNi4zYzAuNC02LTEuNy0xMi4yLTYuMy0xNi44Yy04LjUtOC41LTIyLjQtOC41LTMwLjksMCBjLTQuNiw0LjYtNi43LDEwLjgtNi4zLDE2LjhjLTYtMC40LTEyLjIsMS43LTE2LjgsNi4zYy04LjUsOC41LTguNSwyMi40LDAsMzAuOXMyMi40LDguNSwzMC45LDBsMTQuMy0xNC4zbDEuMS0xLjFsNy43LTcuNyBjNC4zLTQuMywxMS4yLTQuMywxNS40LDBzNC4zLDExLjIsMCwxNS40Yy00LjMsNC4zLTExLjIsNC4zLTE1LjQsMGMtMi4xLTIuMS01LjYtMi4xLTcuNywwcy0yLjEsNS42LDAsNy43IGM4LjUsOC41LDIyLjQsOC41LDMwLjksMEM5Ny4xLDY4LjUsOTcuMSw1NC43LDg4LjYsNDYuMXogTTE5LjEsNjkuM2MtNC4zLTQuMy00LjMtMTEuMiwwLTE1LjRjNC4zLTQuMywxMS4yLTQuMywxNS40LDAgczQuMywxMS4yLDAsMTUuNEMzMC4zLDczLjYsMjMuNCw3My42LDE5LjEsNjkuM3ogTTQyLjMsNDYuMUMzOCw0MS45LDM4LDM1LDQyLjMsMzAuN2M0LjMtNC4zLDExLjItNC4zLDE1LjQsMHM0LjMsMTEuMiwwLDE1LjQgQzUzLjUsNTAuNCw0Ni41LDUwLjQsNDIuMyw0Ni4xeiIvPjwvc3ZnPg%3D%3D)](https://pan.baidu.com/s/1x6-2fbgSK5eEDRI1GjqoWQ?pwd=tak1)

**系统要求**：

便携版：64 位 Windows 10 版本 1607（build 14393）或更高版本。

商店版：64 位 Windows 10 版本 2004（build 19041）或更高版本。

## 隐私说明

本程序不主动联网，不包含运行时遥测、使用统计或崩溃上报功能。解压处理在本地进行，密码本、配置和日志均保存在本地，程序不会主动上传这些数据。

请注意：**密码本以明文保存**，运行日志也可能包含密码与本地文件路径，请妥善保管。

## 从源码运行与构建

开发环境需要 Windows 和 .NET 10 SDK。

克隆仓库并运行：

```powershell
git clone https://github.com/Shiina-Tak1/BullBull-Unpacker.git
cd BullBull-Unpacker
dotnet build Bbu.slnx
dotnet run --project src\Bbu.App
```

运行测试：

```powershell
dotnet test Bbu.slnx
```

构建 AOT 便携版需要 Python、MSVC 链接器与 Windows SDK：

```powershell
python packaging\pack_release.py --flavor aot
```

## 第三方组件与许可证

本项目使用或包含以下第三方组件：

- **UnRAR**：用于解压 rar / rar5 格式，以 Freeware 许可分发。
- **7-Zip ZS**：用于解压 zip / 7z / tar 等格式，以 GNU LGPL 2.1 或更高版本分发（部分代码适用其他许可证）。
- **Avalonia、SkiaSharp、HarfBuzzSharp**：用于界面框架、图形渲染与文字塑形，以 MIT 许可证分发。
- **ANGLE**：用于 Windows 下的图形渲染，以 BSD 3-Clause 许可证分发。
- **Lucide**：用于界面图标，以 ISC 许可证分发；其中部分派生自 Feather 的图标为 MIT 许可证。

完整的第三方组件、版权声明与许可信息见 [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md)。

开发过程中使用 7-Zip ZS 与 WinRAR 生成测试夹具，用于验证压缩包格式兼容性与解压行为。WinRAR 仅用于开发测试，不随软件分发。

**特别感谢 WinRAR 销售与支持团队为本项目提供了个人授权的折扣。**

本项目采用 [GNU GPLv3](https://www.gnu.org/licenses/gpl-3.0.html) 许可证。详见仓库中的 [`LICENSE`](LICENSE) 文件。

## AI 生成内容说明

本项目的大部分代码、文档与测试用例由 DeepSeek 生成。

软件图标由 OpenAI GPT-Image 生成。
