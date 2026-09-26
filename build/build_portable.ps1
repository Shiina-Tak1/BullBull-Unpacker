# BullBull Unpacker —— 便携版打包脚本（一把跑完）
#
# 用法（**用 Windows 自带的 PowerShell 5.1 就行**；手册与 skill 里统一写这个形式）：
#     powershell -NoProfile -ExecutionPolicy Bypass -File build\build_portable.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File build\build_portable.ps1 -SkipFreeze
#
# ⚠ **本文件必须存成 UTF-8 带 BOM**。PowerShell 5.1 只靠 BOM 判定 UTF-8，没有 BOM 就按
#   GBK 读 —— 这里满是中文注释，会直接报一串 ParserError、整个脚本起不来。
#   （2026-09-20 的 `a6d013a` 丢过一次 BOM，2026-09-23 找回；改这份文件时别用会去掉 BOM 的工具。）
#
# 做五件事：
#   1) 生成版本资源 + PyInstaller 冻结（onedir、无控制台、带 manifest）
#   2) 组装便携目录：**只拷白名单里的东西**（绝不带源码/配置/密码本/日志/测试/素材）
#   3) 瘦身（删掉用不到的 Qt 组件）
#   4) 包内自查：本机绝对路径 / 用户名 / 用户目录 + 内置 7-Zip 的 SHA256（不符就报错停下）
#      ⚠ **素材名与项目旧名字不在这里查**（2026-09-24 更正注释：以前这里写着查，实现里没有）：
#        脱敏词表是 `src\tools\private_terms.txt` + 真密码本，而 `tools\` 不进便携包、这份脚本
#        也可能在公开仓克隆里跑（克隆里根本没有词表）。那一步由 `tools\sync_repo.py` +
#        `tools\check_repo_clean.py` 在 **github 副本那一侧**做（§16.2）。
#       包内能查的是"本机信息"（用户名 / 用户目录 `%USERPROFILE%` / 本机绝对路径），
#       这三条一直在实现里（$patterns 那三条）。
#   5) 验收（verify_portable）+ 打 zip + 写 SHA256
#
# 为什么强调"白名单"：打包最怕的就是顺手把 config.json（里面有用户机器路径）、
# 密码本.txt（用户的密码！）、logs（启动参数里就是文件名）一起发出去。
# 所以这里是"一个个点名拷进去"，而不是"排除几个"。

[CmdletBinding()]
param(
    [switch]$SkipFreeze,
    [switch]$SkipVerify,
    [string]$OutDir = "",
    [string]$ZipName = ""
)

$ErrorActionPreference = "Stop"
# 子进程（python）的中文输出别乱码：PS 5.1 默认按 ANSI 解码子进程的 stdout，而 Python 写的是
# UTF-8 —— 不改也不影响产物，只是 `sync_repo.py --sanitize-dir` 那一行会显示成一堆乱码。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:PYTHONIOENCODING = "utf-8"

# ---- 两种布局，同一个脚本（2026-09-23 起公开仓克隆里也能跑）-------------------------
# 开发仓：脚本在 <大>\src\build\ → `<大>\doc\` 与 `<大>\github\` 是同级目录；
#         文档取自 doc\dist\README.txt + doc\THIRD-PARTY.md，产物落 github\release\。
# 公开仓：脚本在 <repo>\build\  → 仓库根就有 docs\（没有 doc\）；
#         文档取自 docs\README.txt + docs\THIRD-PARTY.md，产物落 <repo>\release\
#         （`.gitignore` 里已经忽略 release\）。
# 判据：**开发仓里 `<大>\doc\` 一定在**；公开仓克隆只有 `docs\`。
$Root = Split-Path -Parent $PSScriptRoot
$Parent = Split-Path -Parent $Root
$IsDevTree = Test-Path -LiteralPath (Join-Path $Parent "doc")
if ($IsDevTree) {
    $Big        = $Parent
    $ReleaseDir = Join-Path $Big "github\release"
    $DocReadme  = Join-Path $Big "doc\dist\README.txt"
    $DocThird   = Join-Path $Big "doc\THIRD-PARTY.md"
} else {
    $Big        = $Root
    $ReleaseDir = Join-Path $Root "release"
    $DocReadme  = Join-Path $Root "docs\README.txt"
    $DocThird   = Join-Path $Root "docs\THIRD-PARTY.md"
}
Set-Location $Root
# 产物目录先建出来：**全新公开仓克隆里 `<repo>\release\` 并不存在**，而下面第 1 步是
# `Move-Item … -Destination $OutDir` —— 父目录不在就报 "Could not find a part of the path"
# （2026-09-23 端到端实测踩到：开发仓里没暴露，只因为 `github\release\` 早就被 sync_repo 建过）。
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$AppName = "BullBull Unpacker"
# 解释器：开发仓用 `.venv`；公开仓克隆里没有 venv，就用 PATH 上那个
# （依赖见 docs\REQUIREMENTS-BUILD.txt —— 它 `-r` 了 docs\REQUIREMENTS.txt，打包要两份一起装）
$python = Join-Path $Root ".venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python)) { $python = "python" }
$Version = ""
try {
    $Version = (& $python -c "import sys; sys.path.insert(0,'.'); from core.appinfo import VERSION; print(VERSION)" 2>$null).Trim()
} catch {
    $Version = ""
}
if (-not $Version) {
    Write-Host "读不到版本号 —— 先看看解释器装好没有：" -ForegroundColor Red
    Write-Host "  用的是：$python"
    Write-Host "  开发仓：确认 <大>\src\.venv 在。"
    Write-Host "  公开仓克隆：先建环境再装依赖（打包依赖在 docs\REQUIREMENTS-BUILD.txt，它会带上 PySide6）："
    Write-Host "      python -m venv .venv"
    Write-Host "      .venv\Scripts\python -m pip install -r docs\REQUIREMENTS-BUILD.txt"
    throw "读不到版本号（core/appinfo.py）"
}
if (-not $OutDir)   { $OutDir = Join-Path $ReleaseDir $AppName }
if (-not $ZipName)  { $ZipName = "BullBullUnpacker-$Version-portable.zip" }
$ZipPath = Join-Path $ReleaseDir $ZipName

Write-Host "== BullBull Unpacker $Version 便携版打包 ==" -ForegroundColor Cyan
Write-Host "   布局    : $(if ($IsDevTree) { '开发仓' } else { '公开仓克隆' })（$Root）"
Write-Host "   输出目录: $OutDir"
Write-Host "   压缩包  : $ZipPath"

# ---------------------------------------------------------------- 1) 冻结
if (-not $SkipFreeze) {
    Write-Host "`n[1/5] 生成版本资源 + PyInstaller 冻结…" -ForegroundColor Cyan
    & $python "$Root\build\make_version_file.py"
    if ($LASTEXITCODE -ne 0) { throw "生成版本资源失败" }

    # 每次从干净的 dist 开始，避免上一版的残留被当成新产物
    Remove-Item -LiteralPath (Join-Path $Root "dist") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $Root "build\$AppName") -Recurse -Force -ErrorAction SilentlyContinue

    # 注意：**不用 --add-data**。资源（tools\7z、assets）由第 2 步放到程序目录旁边，
    # `core/paths.py` 的 resource_dir() 会优先用旁边那份。
    # 以前两边各放一份 → 同一个 7-Zip 有两份（5.9MB ×2）。
    #
    # ★ 用 `python -m PyInstaller` 而不是 `Scripts\pyinstaller.exe`：
    #   venv 里那些 .exe 启动器（pip/pyinstaller…）**把解释器路径写死在里面**，
    #   整个项目文件夹一搬动，它们就会一声不吭地退出（退出码 1、没有任何输出）——
    #   实测就是这么踩的。走 `-m` 永远跟着当前解释器走。
    & $python -m PyInstaller `
        --noconfirm --clean --windowed --onedir `
        --name $AppName `
        --icon "$Root\assets\bbu.ico" `
        --version-file "$Root\build\version.txt" `
        --manifest "$Root\build\app.manifest" `
        --distpath "$Root\build\frozen" --workpath "$Root\build\pyi" `
        "$Root\run.py"
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller 失败（退出码 $LASTEXITCODE）" }
    # PyInstaller 只能往一个已有/新建目录里产出，之后再搬到 release 去
    $frozen = Join-Path $Root "build\frozen\$AppName"
    if (-not (Test-Path -LiteralPath $frozen)) { throw "冻结产物不在预期位置：$frozen" }
    Remove-Item -LiteralPath $OutDir -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item -LiteralPath $frozen -Destination $OutDir -Force
    Remove-Item -LiteralPath (Join-Path $Root "build\frozen") -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "`n[1/5] 跳过冻结（-SkipFreeze）" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 2) 组装
Write-Host "`n[2/5] 组装便携目录（白名单）…" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 先清掉"上一次组装"留下的东西，这样 -SkipFreeze 重跑也是幂等的。
# （踩过：`Copy-Item 整个目录 到已存在的目录` 会嵌一层 tools\7z\7z\…，跑两次多一层）
foreach ($rel in @("tools", "assets", "docs", "doc", "文档")) {
    Remove-Item -LiteralPath (Join-Path $OutDir $rel) -Recurse -Force -ErrorAction SilentlyContinue
}

# 文档目录叫 **docs**（作者定稿的布局）；里头**只放两份**（2026-09-23 作者裁决）：
#   README.txt       —— 作者手写的"快速上手"精简版。它的内容引用了界面文案，
#                       界面文案变了要**人工核对**这份，脚本不会替你同步。
#   THIRD-PARTY.md   —— 第三方许可。
# 两份的来源按布局不同（见文件开头那段）：开发仓取 `doc\dist\README.txt` + `doc\THIRD-PARTY.md`，
# 公开仓克隆取 `docs\README.txt` + `docs\THIRD-PARTY.md`。
# **不进包**：使用手册.md（它是 README.txt 的来源，只给作者精简用）、CHANGELOG.md（开发记录）。
# 两者也不进公开仓。见开发手册 §16.7 / §16.11。
# （只做中文。英文译本已于 2026-09-21 归档到 backup\doc-en-2026-09-21\，不再维护）
$docs = Join-Path $OutDir "docs"
New-Item -ItemType Directory -Force -Path $docs | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir "tools\7z") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir "assets") | Out-Null

function Copy-FileSafe([string]$From, [string]$To) {
    if (-not (Test-Path -LiteralPath $From)) { throw "白名单里的东西不见了：$From" }
    $dir = Split-Path -Parent $To
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $From -Destination $To -Force
}

# tools/7z：放在**程序目录旁边**，这样"打开程序目录就能看到内置 7z"，也方便用户单独用。
# **不用 `--add-data`**：`core/paths.py::resource_dir()` 优先用旁边这份；加了它同一个 7-Zip
# 会在 `_internal` 里再多一份（5.9MB ×2）—— 见开发手册 §16.8 末。
foreach ($f in Get-ChildItem -LiteralPath "$Root\tools\7z" -File) {
    Copy-FileSafe $f.FullName (Join-Path $OutDir "tools\7z\$($f.Name)")
}
Copy-FileSafe "$Root\assets\bbu.ico" (Join-Path $OutDir "assets\bbu.ico")

# 文档：**点名两个**（这脚本的原则就是"一个个点名拷"，不搞"目录里有什么就发什么"）
Copy-FileSafe $DocReadme (Join-Path $docs "README.txt")
Copy-FileSafe $DocThird  (Join-Path $docs "THIRD-PARTY.md")
# 这两份是**包里唯一的文本**（其余不是 exe 就是二进制），也是整个仓库里唯一会离开本机的
# 文档 → 用同一套私有脱敏表洗一遍**包里的副本**（不动源文件）。
# 开发机上 tools\ 齐全；公开仓克隆里没有这个脚本，跳过即可（那边也没有脱敏表要洗）。
$sanitizer = Join-Path $Root "tools\sync_repo.py"
if (Test-Path -LiteralPath $sanitizer) {
    & $python $sanitizer --sanitize-dir $docs
    if ($LASTEXITCODE -ne 0) { throw "便携包文档脱敏失败（退出码 $LASTEXITCODE）" }
} else {
    Write-Host "  找不到 tools\sync_repo.py（脱敏脚本是开发工具，不随仓库发布）——跳过文档脱敏" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 3) 瘦身
# PyInstaller 会把 PySide6 的一大堆东西一起收进来，其中相当一部分我们这个
# **纯控件程序**根本用不到（实测占了 100MB 里的 45MB+）。下面按"图案"删，
# 每一项都写清为什么；删完**必须**过 tools\verify_portable.py（第 5 步），
# 那是"还跑不跑得起来"的唯一凭据 —— 别在没跑验收的情况下加新的删除项。
Write-Host "`n[3/5] 瘦身（删掉用不到的 Qt 组件）…" -ForegroundColor Cyan
$internalDir = Join-Path $OutDir "_internal"
$beforeMB = [math]::Round((Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)

$pruneRules = @(
    @{ glob = "PySide6\opengl32sw.dll";        why = "Qt 的软件 OpenGL 兜底（19.7MB）：程序里没有任何 QOpenGLWidget，用不上" },
    @{ glob = "PySide6\*Qt6Quick*.dll";        why = "Qt Quick/QML 运行时：界面是 QWidget，不用 QML" },
    @{ glob = "PySide6\*Qt6Qml*.dll";          why = "同上（Qml/QmlModels/QmlWorkerScript）" },
    @{ glob = "PySide6\QtQuick*.pyd";          why = "同上（Python 绑定）" },
    @{ glob = "PySide6\QtQml*.pyd";            why = "同上" },
    @{ glob = "PySide6\*Qt6Pdf*.dll";          why = "QtPdf：不看 PDF" },
    @{ glob = "PySide6\QtPdf*.pyd";            why = "同上" },
    @{ glob = "PySide6\*Qt6OpenGL*.dll";       why = "QtOpenGL/OpenGLWidgets：不用 OpenGL" },
    @{ glob = "PySide6\QtOpenGL*.pyd";         why = "同上" },
    @{ glob = "PySide6\*Qt6Sql*.dll";          why = "QtSql：不连数据库" },
    @{ glob = "PySide6\QtSql*.pyd";            why = "同上" },
    @{ glob = "PySide6\*Qt6Test*.dll";         why = "QtTest：运行时不需要" },
    @{ glob = "PySide6\QtTest*.pyd";           why = "同上" },
    @{ glob = "PySide6\*Qt6Designer*.dll";     why = "Designer 支持：运行时不需要" },
    @{ glob = "PySide6\QtDesigner*.pyd";       why = "同上" },
    @{ glob = "PySide6\*Qt6Multimedia*.dll";   why = "多媒体：不播音频视频" },
    @{ glob = "PySide6\QtMultimedia*.pyd";     why = "同上" },
    @{ glob = "PySide6\*Qt6WebEngine*";        why = "WebEngine：没有浏览器控件" },
    @{ glob = "PySide6\*Qt6WebChannel*";       why = "同上" },
    @{ glob = "PySide6\plugins\tls\*";         why = "QtNetwork 的 TLS 后端：只用本机命名管道（QLocalSocket），全程不联网" },
    @{ glob = "libssl-3.dll";                  why = "同上（OpenSSL）；本程序没有任何网络请求" },
    @{ glob = "libcrypto-3.dll";               why = "同上；哈希用的是 Python 自带的 blake2b，不依赖 OpenSSL" },
    @{ glob = "PySide6\plugins\platforms\qdirect2d.dll"; why = "Direct2D 平台插件：默认走 qwindows.dll，只有显式指定才用 D2D" },
    @{ glob = "PySide6\translations\*.qm";     why = "Qt 自带翻译：只留中文那几份（界面文案都是我们自己写的）" }
)
$removed = @()
foreach ($rule in $pruneRules) {
    $targets = Get-ChildItem -LiteralPath $internalDir -Recurse -File -Filter "*" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName.Substring($internalDir.Length + 1) -like $rule.glob }
    if ($rule.glob -like "*translations*") {
        # 翻译目录单独处理：保留 zh_CN 与 en，其余删
        $targets = $targets | Where-Object {
            $_.Name -notlike "*zh_CN*" -and $_.Name -notlike "*zh_TW*" -and $_.Name -notlike "qt_en*"
        }
    }
    foreach ($t in $targets) {
        $removed += [pscustomobject]@{ File = $t.FullName.Substring($OutDir.Length + 1); MB = [math]::Round($t.Length / 1MB, 2) }
        Remove-Item -LiteralPath $t.FullName -Force -ErrorAction SilentlyContinue
    }
}
$afterMB = [math]::Round((Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ("  删了 {0} 个文件，{1} MB → {2} MB（省 {3} MB）" -f $removed.Count, $beforeMB, $afterMB, ($beforeMB - $afterMB)) -ForegroundColor Green
$removed | Group-Object { $_.File.Split('\')[1] } | Sort-Object { ($_.Group | Measure-Object MB -Sum).Sum } -Descending |
    Select-Object -First 8 | ForEach-Object {
        Write-Host ("    {0,-28} {1,6:N1} MB" -f $_.Name, ($_.Group | Measure-Object MB -Sum).Sum)
    }

# ------------------------------------------------- 4) 包内自查（本机信息 + 7z 校验）
# 先清掉"用户数据"：便携版第一次运行会自己生成它们，**绝不能被我们打进包里**。
# （踩过：手动跑过一次 exe，logs\run.log 留在目录里而程序还开着 → 文件被占用、
#   Remove-Item 静默失败 → 那个日志真的被打进了 zip。所以删完必须**复查**。）
foreach ($rel in @("logs", "config.json", "密码本.txt")) {
    $p = Join-Path $OutDir $rel
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $p) {
            throw "清不掉 $rel（有程序正开着这个目录？先退出 BullBull Unpacker 再打包）"
        }
    }
}

Write-Host "`n[4/5] 包内自查（本机信息 + 内置 7-Zip 校验）…" -ForegroundColor Cyan
$bad = @()
$files = Get-ChildItem -LiteralPath $OutDir -Recurse -File -Force
# `_internal` 是 PyInstaller 自己的运行时（Python 标准库 + PySide6），里面本来就有
# base_library.zip、各种 .json/.txt；那些不属于"我们发出去的内容"，
# 也跟你确认过的一致（二进制/第三方内部字符串不管）。所以扫描只看明面上的文件。
$internalPrefix = (Join-Path $OutDir "_internal") + [System.IO.Path]::DirectorySeparatorChar
$scanFiles = $files | Where-Object {
    -not $_.FullName.StartsWith($internalPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

# 3a) 不该出现的文件名 / 目录
$forbiddenNames = @("密码本.txt", "config.json", "新建 文本文档.txt", "icon.png")
foreach ($f in $scanFiles) {
    if ($forbiddenNames -contains $f.Name) { $bad += "不该带的文件：$($f.FullName)" }
    if ($f.FullName -match "\\logs\\") { $bad += "不该带的日志：$($f.FullName)" }
    if ($f.Extension -in @(".mp4", ".zip", ".rar", ".7z", ".lz4", ".iso")) {
        $bad += "像是素材/压缩包：$($f.FullName)"
    }
}
# 3b) 不该出现的内容（只扫文本类文件；二进制里的内部常量不管）
# 注意：只查**本机信息**（用户名/用户目录/本机盘符路径）。
# 别把"密码本"这种**程序自己的数据文件名**列进来——手册里正常会写"密码本.txt 在哪"，
# 那样会把正当的文档判成问题（文件本身由上面的 forbiddenNames 兜住）。
# 文本后缀名单**只此一份**（`core\textscan.py`，B-2026-062）：以前这里 7 项、
# `tools\check_repo_clean.py` 11 项、`tools\sync_repo.py` 8 项 —— 三个值，而三处
# 做的是同一条隐私闸门（`.ini` / `.yml` / `.yaml` 于是各漏一段）。
# 从 Python 那边取：`core\` 在开发仓与公开仓克隆里都在仓库根，是两边都读得到的地方。
$textExtRaw = (& $python -c "import sys; sys.path.insert(0,'.'); from core.textscan import TEXT_EXT; print(' '.join(sorted(TEXT_EXT)))" 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $textExtRaw) {
    # 名单读不到 = 这条扫描**不知道要扫什么**。不许降级成"少扫几个后缀"继续打包
    # （那正是 B-2026-062 的形状：读不了就算过）。直接停。
    throw "读不到文本后缀名单（core\textscan.py）—— 隐私扫描不能没有它"
}
$textExt = @($textExtRaw.Trim() -split '\s+' | Where-Object { $_ })
$patterns = @(
    @{ p = "C:\Users\";    why = "本机绝对路径" }
)
# 用户名/用户目录从环境里取，不写死在脚本里（这份脚本是要进公开仓库的）
$userName = $env:USERNAME
if ($userName) { $patterns += @{ p = $userName; why = "本机用户名" } }
$userProfile = $env:USERPROFILE
if ($userProfile) { $patterns += @{ p = $userProfile; why = "本机用户目录" } }

$scanTargets = $scanFiles | Where-Object { $textExt -contains $_.Extension.ToLower() }
foreach ($f in $scanTargets) {
    # ★ 读不了**不许静默跳过**（B-2026-062 同一形状）：读不了 = 这一份没被扫描过，
    #   而它照样在包里。以前是 `-ErrorAction SilentlyContinue` + `continue`，
    #   等于"没查过"冒充"干净"。
    try {
        $content = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 -ErrorAction Stop
    } catch {
        $bad += "读不了（没有被扫描过）：$($f.FullName.Substring($OutDir.Length)) — $($_.Exception.Message)"
        continue
    }
    if (-not $content) { continue }     # 空文件：本来就没有内容可扫
    foreach ($pat in $patterns) {
        if ($content -like "*$($pat.p)*") {
            $bad += "$($pat.why)「$($pat.p)」出现在 $($f.FullName.Substring($OutDir.Length))"
        }
    }
}
# 3c) ★ 内置 7-Zip 的校验值（`tools\7z\SHA256SUMS`，2026-09-24 加）：
#   它是**跟仓的二进制依赖**（R-06），不是构建产物 —— 换过版本、拷坏、被别的程序改写，
#   都必须当场看出来，而不是等用户端解压报错。所以**打包前逐条核对**。
#   ⚠ 校验文件缺失 / 一条有效记录都没有 —— **都算失败**：不许静默跳过这一步
#   （"没查"冒充"干净"是 B-2026-062 的形状，这个项目里已经踩过一次）。
$sumFile = Join-Path $Root "tools\7z\SHA256SUMS"
if (-not (Test-Path -LiteralPath $sumFile)) {
    $bad += "内置 7-Zip 没有校验文件：$sumFile（这一步不许跳过）"
} else {
    $wantHash = @{}
    foreach ($line in (Get-Content -LiteralPath $sumFile -Encoding UTF8)) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith("#")) { continue }
        $parts = $t -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].Trim()) { $wantHash[$parts[1].Trim()] = $parts[0].Trim().ToLower() }
    }
    if (-not $wantHash.Count) { $bad += "7-Zip 校验文件里一条有效记录都没有：$sumFile" }
    foreach ($name in @($wantHash.Keys)) {
        $f = Join-Path $Root "tools\7z\$name"
        if (-not (Test-Path -LiteralPath $f)) { $bad += "校验文件点名了 $name，但文件不在：$f"; continue }
        $gotHash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
        if ($gotHash -ne $wantHash[$name]) {
            $bad += "内置 7-Zip 校验不通过：$name（期望 $($wantHash[$name])，实际 $gotHash）"
        }
    }
}

if ($bad.Count) {
    Write-Host "  命中以下问题，先处理再打包：" -ForegroundColor Red
    $bad | Select-Object -Unique | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    throw "隐私扫描没过（共 $($bad.Count) 条）"
}
Write-Host "  文本文件 $($scanTargets.Count) 个，全部干净；不该带的文件也没有" -ForegroundColor Green

# ---------------------------------------------------------------- 5) 验收 + 打包
# 瘦身删过东西之后**必须**先证明它还跑得起来，再打 zip：
# verify_portable 会拷一份副本、起真窗口、用真夹具解一遍（内置 7z 真跑）、
# 验右键菜单注册与冷启动带路径 —— 任何一项红了就停在这儿，不产出包。
if (-not $SkipVerify) {
    Write-Host "`n[5/5] 对着 exe 跑验收（瘦身后的回归）…" -ForegroundColor Cyan
    # 验收脚本是**开发工具**（2026-09-20 起不随仓库发布），所以可能不在。
    # 在开发机上它一定在；别人 clone 公开仓后跑这个脚本时，这里只提示、不中断打包。
    $verifier = "$Root\tools\verify_portable.py"
    if (-not (Test-Path -LiteralPath $verifier)) {
        Write-Host "  找不到 tools\verify_portable.py（验收脚本不随仓库发布）——跳过第 5 步" -ForegroundColor Yellow
    } else {
        & $python $verifier
        if ($LASTEXITCODE -ne 0) { throw "验收没过（退出码 $LASTEXITCODE）——不产出包，先看上面的 FAIL" }
    }
} else {
    Write-Host "`n[5/5] 跳过验收（-SkipVerify）" -ForegroundColor Yellow
}

Write-Host "`n打 zip + SHA256…" -ForegroundColor Cyan
Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -Force
$hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
$shaPath = "$ZipPath.sha256"
"$hash  $ZipName" | Set-Content -LiteralPath $shaPath -Encoding ASCII

$sizeZip = [math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 1)
$sizeDir = [math]::Round((Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "`n完成：" -ForegroundColor Green
Write-Host "  目录 $OutDir  ($sizeDir MB)"
Write-Host "  压缩 $ZipPath  ($sizeZip MB)"
Write-Host "  SHA256 $hash"
if ($IsDevTree) {
    Write-Host "`n下一步（你自己做）：干净机上解压 zip → 双击 exe → 走一遍验收清单（doc\开发手册.md §16.10）"
} else {
    Write-Host "`n下一步（你自己做）：干净机上解压 zip → 双击 exe → 按 docs\README.txt 走一遍"
}
