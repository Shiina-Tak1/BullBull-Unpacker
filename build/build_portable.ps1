# BullBull Unpacker —— 便携版打包脚本（一把跑完）
#
# 用法：
#     pwsh -File build\build_portable.ps1                 # 完整流程
#     pwsh -File build\build_portable.ps1 -SkipFreeze     # 跳过 PyInstaller（只重组目录/扫描/打包）
#
# 做四件事：
#   1) 生成版本资源 + PyInstaller 冻结（onedir、无控制台、带 manifest）
#   2) 组装便携目录：**只拷白名单里的东西**（绝不带源码/配置/密码本/日志/测试/素材）
#   3) 隐私扫描：机器路径、用户名、素材名、旧名字（命中就报错停下）
#   4) 打 zip + 写 SHA256
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
$Root = Split-Path -Parent $PSScriptRoot         # <big>\src
$Big  = Split-Path -Parent $Root                 # <big>（doc/ 与 github/ 都在这一层）
Set-Location $Root

$AppName  = "BullBull Unpacker"
$Version  = (& "$Root\.venv\Scripts\python.exe" -c "import sys; sys.path.insert(0,'.'); from core.appinfo import VERSION; print(VERSION)").Trim()
if (-not $Version) { throw "读不到版本号（core/appinfo.py）" }
# 产物统一放 <big>\github\release（那儿不进 git；发布时把 zip 传到 GitHub Releases）
$ReleaseDir = Join-Path $Big "github\release"
if (-not $OutDir)   { $OutDir = Join-Path $ReleaseDir $AppName }
if (-not $ZipName)  { $ZipName = "BullBullUnpacker-$Version-portable.zip" }
$ZipPath = Join-Path $ReleaseDir $ZipName

Write-Host "== BullBull Unpacker $Version 便携版打包 ==" -ForegroundColor Cyan
Write-Host "   输出目录: $OutDir"
Write-Host "   压缩包  : $ZipPath"

# ---------------------------------------------------------------- 1) 冻结
if (-not $SkipFreeze) {
    Write-Host "`n[1/5] 生成版本资源 + PyInstaller 冻结…" -ForegroundColor Cyan
    & "$Root\.venv\Scripts\python.exe" "$Root\build\make_version_file.py"
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
    & "$Root\.venv\Scripts\python.exe" -m PyInstaller `
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

# 文档目录叫 **docs**（作者定稿的布局）；里头放 doc\dist\ 里的那几份
# （要英文版就把 doc\en\ 的三份覆盖过来，见那份说明）
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

# tools/7z：_internal 里已经有一份（--add-data），明面上再放一份，
# 这样"打开程序目录就能看到内置 7z"，也方便用户单独用
foreach ($f in Get-ChildItem -LiteralPath "$Root\tools\7z" -File) {
    Copy-FileSafe $f.FullName (Join-Path $OutDir "tools\7z\$($f.Name)")
}
Copy-FileSafe "$Root\assets\bbu.ico" (Join-Path $OutDir "assets\bbu.ico")

# 文档：中文四份，来源 <大>\doc\dist\（英文译本在 <大>\doc\en\，要换自己覆盖）
$pkgDocs = Join-Path $Big "doc\dist"   # 要打进包里的文档全在这儿（放几份就打几份）
foreach ($f in Get-ChildItem -LiteralPath $pkgDocs -File) {
    Copy-FileSafe $f.FullName (Join-Path $docs $f.Name)
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

# ---------------------------------------------------------------- 4) 隐私扫描
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

Write-Host "`n[4/5] 隐私扫描…" -ForegroundColor Cyan
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
$textExt = @(".md", ".txt", ".json", ".ini", ".ps1", ".bat", ".vbs")
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
    $content = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    foreach ($pat in $patterns) {
        if ($content -like "*$($pat.p)*") {
            $bad += "$($pat.why)「$($pat.p)」出现在 $($f.FullName.Substring($OutDir.Length))"
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
    & "$Root\.venv\Scripts\python.exe" "$Root\tools\verify_portable.py"
    if ($LASTEXITCODE -ne 0) { throw "验收没过（退出码 $LASTEXITCODE）——不产出包，先看上面的 FAIL" }
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
Write-Host "`n下一步（你自己做）：干净机上解压 zip → 双击 exe → 走一遍验收清单（docs\打包发布清单.md 第 5 节）"
