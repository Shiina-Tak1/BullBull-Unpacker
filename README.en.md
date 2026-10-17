<div align="center">
<img src="src/Bbu.App/Assets/icon.png" alt="BullBull Unpacker" width="128">

# BullBull Unpacker

[简体中文](README.md) · **English**

BullBull Unpacker is a nested and disguised archive extractor for Windows

[![CI](https://img.shields.io/github/actions/workflow/status/Shiina-Tak1/BullBull-Unpacker/ci.yml?style=flat)](https://github.com/Shiina-Tak1/BullBull-Unpacker/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/actions/workflow/status/Shiina-Tak1/BullBull-Unpacker/release.yml?style=flat&label=Release)](https://github.com/Shiina-Tak1/BullBull-Unpacker/actions/workflows/release.yml)
[![Latest](https://img.shields.io/github/v/release/Shiina-Tak1/BullBull-Unpacker?style=flat&label=Latest&color=36AFA0)](https://github.com/Shiina-Tak1/BullBull-Unpacker/releases/latest)
[![License](https://img.shields.io/badge/license-GPLv3-2780C2?style=flat)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Avalonia-8B44AC?style=flat)](https://avaloniaui.net/)

</div>

## Features

- Supports zip / 7z / rar / rar5 / tar / gz / bz2 / xz / zstd / lz4 / lz5 / brotli / cab and more
- Detects and extracts archives with misleading extensions, such as mp4 and jpg
- Extracts nested archives, removes redundant outer folders, and deletes intermediate archives
- Automatically saves extraction passwords; supports manual addition, editing and deletion
- Integrates with the Windows Explorer context menu to add files to the extraction queue in one click

## Password memory

Passwords are tried in a fixed order: one taken from the file name, then the password book, and if nothing matches, a prompt asks you to type one. A password typed into the prompt is remembered only after an extraction has actually succeeded.

The password book is a plain text file. You can add or remove passwords through the app, or edit it directly in Notepad. One record per line:

- `password<TAB>hit count`: the password and how many times it has worked.
- `password`: just the password, with no hit count.

When a password contains a newline, a TAB or another special character, the whole password is stored with a `\\B64:` prefix followed by standard Base64, encoding the UTF-8 bytes of the password. The app decodes it back on read, so there is nothing to handle by hand.

## Download

[![Download](https://img.shields.io/badge/Download-GitHub%20Releases-181717?style=flat&logo=github)](https://github.com/Shiina-Tak1/BullBull-Unpacker/releases)
[![Download](https://img.shields.io/badge/Download-Microsoft%20Store-0078D4?style=flat&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyMyAyMyI%2BPHBhdGggZmlsbD0iI2YzZjNmMyIgZD0iTTAgMGgyM3YyM0gweiIvPjxwYXRoIGZpbGw9IiNmMzUzMjUiIGQ9Ik0xIDFoMTB2MTBIMXoiLz48cGF0aCBmaWxsPSIjODFiYzA2IiBkPSJNMTIgMWgxMHYxMEgxMnoiLz48cGF0aCBmaWxsPSIjMDVhNmYwIiBkPSJNMSAxMmgxMHYxMEgxeiIvPjxwYXRoIGZpbGw9IiNmZmJhMDgiIGQ9Ik0xMiAxMmgxMHYxMEgxMnoiLz48L3N2Zz4%3D)](https://apps.microsoft.com/detail/9NJ532JX69VK)
[![Baidu Netdisk](https://img.shields.io/badge/Download-Baidu%20Netdisk-2932E1?style=flat&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMDAgMTAwIj48cGF0aCBmaWxsPSIjZmZmZmZmIiBkPSJNODguNiw0Ni4xYy00LjYtNC42LTEwLjgtNi43LTE2LjgtNi4zYzAuNC02LTEuNy0xMi4yLTYuMy0xNi44Yy04LjUtOC41LTIyLjQtOC41LTMwLjksMCBjLTQuNiw0LjYtNi43LDEwLjgtNi4zLDE2LjhjLTYtMC40LTEyLjIsMS43LTE2LjgsNi4zYy04LjUsOC41LTguNSwyMi40LDAsMzAuOXMyMi40LDguNSwzMC45LDBsMTQuMy0xNC4zbDEuMS0xLjFsNy43LTcuNyBjNC4zLTQuMywxMS4yLTQuMywxNS40LDBzNC4zLDExLjIsMCwxNS40Yy00LjMsNC4zLTExLjIsNC4zLTE1LjQsMGMtMi4xLTIuMS01LjYtMi4xLTcuNywwcy0yLjEsNS42LDAsNy43IGM4LjUsOC41LDIyLjQsOC41LDMwLjksMEM5Ny4xLDY4LjUsOTcuMSw1NC43LDg4LjYsNDYuMXogTTE5LjEsNjkuM2MtNC4zLTQuMy00LjMtMTEuMiwwLTE1LjRjNC4zLTQuMywxMS4yLTQuMywxNS40LDAgczQuMywxMS4yLDAsMTUuNEMzMC4zLDczLjYsMjMuNCw3My42LDE5LjEsNjkuM3ogTTQyLjMsNDYuMUMzOCw0MS45LDM4LDM1LDQyLjMsMzAuN2M0LjMtNC4zLDExLjItNC4zLDE1LjQsMHM0LjMsMTEuMiwwLDE1LjQgQzUzLjUsNTAuNCw0Ni41LDUwLjQsNDIuMyw0Ni4xeiIvPjwvc3ZnPg%3D%3D)](https://pan.baidu.com/s/1x6-2fbgSK5eEDRI1GjqoWQ?pwd=tak1)

**System requirements**:

Portable: 64-bit Windows 10, version 1607 (build 14393) or later.

Store: 64-bit Windows 10, version 2004 (build 19041) or later.

## Privacy

This program does not initiate network connections or include runtime telemetry, usage statistics, or crash reporting. Archive extraction is performed locally. The password book, settings, and logs are stored locally, and the program does not actively upload this data.

Please note: **the password book is stored in plain text**, and the run log may also contain passwords and local file paths. Keep them safe.

## Running and building from source

Building needs **Windows and the .NET 10 SDK**; the portable-package command below also needs **Python**.

Clone the repository and run:

```powershell
git clone https://github.com/Shiina-Tak1/BullBull-Unpacker.git
cd BullBull-Unpacker
dotnet build Bbu.slnx
dotnet run --project src\Bbu.App
```

Run the tests:

```powershell
dotnet test Bbu.slnx
```

Build the AOT portable package, which needs Python, the MSVC linker and the Windows SDK:

```powershell
python packaging\pack_release.py --flavor aot
```

## Third-party components and licenses

This project uses or includes the following third-party components:

- **UnRAR**: used for extracting rar / rar5 archives, distributed as freeware.
- **7-Zip ZS**: used for extracting zip / 7z / tar and other archives, distributed under GNU LGPL 2.1 or later (some code is covered by other licenses).
- **Avalonia, SkiaSharp, HarfBuzzSharp**: used for the UI framework, graphics rendering and text shaping, distributed under the MIT License.
- **ANGLE**: used for graphics rendering on Windows, distributed under the BSD 3-Clause License.
- **Lucide**: used for UI icons, distributed under the ISC License; the icons derived from Feather are MIT-licensed.

For the full list of third-party components, copyright notices and licenses, see [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md).

7-Zip ZS and WinRAR are used during development to generate test fixtures that check archive-format compatibility and extraction behaviour. WinRAR is used for development and testing only and is not distributed with the software.

**Special thanks to the WinRAR Sales & Support Team for a discount on a personal license for this project.**

This project is licensed under [GNU GPLv3](https://www.gnu.org/licenses/gpl-3.0.html). See the [`LICENSE`](LICENSE) file in the repository.

## About AI-generated content

Most of this project's code, documentation and test cases were generated by DeepSeek.

The application icon was generated by OpenAI GPT-Image.
