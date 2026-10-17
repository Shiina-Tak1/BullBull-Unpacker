#!/usr/bin/env python3
"""pack_release.py —— 出两个便携包：AOT 与框架依赖，各带 SHA256SUMS。

契约：
  输入：--flavor（aot / fd / both，默认 both）、--out（默认 .scratch\\release）、
        --skip-publish（只重组已有的 publish 产物）、--keep-stage（保留中间 publish 目录）、
        --suffix（预发布后缀，如 beta.1：只转成 -p:VersionSuffix=，不传 -p:Version）、--copy-to（复制到指定目录）
  输出落点：<out>\\BullBullUnpacker-<版本>-win-x64-portable.zip 与同名 .sha256（AOT 那一份，
            是对外发布的那份形态；fd 只作本地自测，名里保留 flavor 以示区别）；
            解包目录 <out>\\<flavor>\\BullBullUnpacker-<版本>-<flavor>\\（内部中间产物，不对外）；
            包内顶层目录名取对外名，包内哈希清单在它的包根 SHA256SUMS（行内路径相对包根）；
            体积与结论写 stdout；--copy-to 给了就只在检查全过之后复制那一对
  退出码：0 全部成功；1 有一项检查没过（哈希、黑名单、隐私扫描、结构）；2 前置不成立（--copy-to 的目标写不进去也是 2：出包前试写拦下、出包后复制失败同回 2）
  幂等：重复运行覆盖同一批产物；中间 publish 目录每次重建

版本号与产品身份的唯一出处是仓根的 Directory.Build.props，本脚本只取值不改值。
便携包内容按白名单逐项点名，不按目录整体拷。
密码本、config.json、logs、源码、测试与 *.pdb 一律不进包。
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP_PROJ = os.path.join(REPO, "src", "Bbu.App", "Bbu.App.csproj")
APP_EXE = "Bbu.App.exe"
PACKED_EXE = "BullBull Unpacker.exe"
SUMS = "SHA256SUMS"

# 对外物料的源区：开发仓 docs\public\，公开仓 docs\ —— 两级都试，同一个脚本要在两个仓里都能出包。
PACKED_SRC_CANDIDATES = (("docs", "public"), ("docs",))

# 摊在包根的白名单文件（程序本体与清单之外）：源在物料区，包内名与源名同；随包文本的内容已内联进它。
PACKED_ROOT = ("README.html",)


def packed_source(name: str) -> str:
    """随包文件在仓里的路径。两级都不在就抛 —— 缺件必须立即失败，不得跳过。"""
    for parts in PACKED_SRC_CANDIDATES:
        path = os.path.join(REPO, *parts, name)
        if os.path.exists(path):
            return path
    tried = "、".join(os.path.join(REPO, *parts, name) for parts in PACKED_SRC_CANDIDATES)
    raise RuntimeError(f"随包文件不在：{tried}")

# 禁止进包项（命中即失败）
FORBIDDEN_NAMES = ("密码本.txt", "config.json", "BUILD-ID.txt", "BullBullUnpacker.log")
FORBIDDEN_DIRS = ("logs", "tests", "tools", ".scratch", "obj", "bin")
FORBIDDEN_EXT = (".cs", ".csproj", ".slnx", ".pdb", ".py", ".ps1")

FLAVORS = {
    "aot": ["-p:PublishAot=true", "-p:StripSymbols=true"],
    # single-file：托管 dll 与 native 一起内嵌进那一个 exe，顶层只剩程序本体与 engines\。
    # 代价是首次启动多一次自解压，且自解压行为会抬高杀软误报概率。
    "fd": ["--self-contained", "false",
           "-p:PublishSingleFile=true",
           "-p:IncludeNativeLibrariesForSelfExtract=true"],
}

# 包内顶层只许有程序本体与这些目录；两个引擎收在一个 engines\ 下，各留一层子目录（理由见 csproj）。
TOP_LEVEL_DIRS = ("engines",)

# 引擎子树：engines\<这一层的名字>\。仓库里各自带一份 SHA256SUMS（防篡改凭据，跟仓库走、不进包），
# 打包时按它逐条核对包内文件，check_layout 反向拿它查「多出来的文件」。
ENGINES_DIR = "engines"
ENGINE_DIRS = ("7z", "unrar")


def engine_dir(name: str) -> str:
    """包内（与仓库内）某个引擎那一层的相对路径。"""
    return os.path.join(ENGINES_DIR, name)

# AOT 版那三个 native 与 exe 同留在包根（不再另收一层）；框架依赖版转 single-file 后已内嵌。
NATIVE_DLLS = ("av_libglesv2.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll")


def sh(cmd: list[str], cwd: str | None = None, env: dict[str, str] | None = None) -> int:
    print("  $ " + " ".join(cmd), flush=True)
    return subprocess.call(cmd, cwd=cwd or REPO, env=env)


def dotnet_env() -> dict[str, str]:
    """沙箱里 dotnet 的状态目录必须落在工作区，否则被拒。"""
    env = dict(os.environ)
    work = os.path.dirname(REPO)
    for key, leaf in (
        ("DOTNET_CLI_HOME", ".dotnet-home"),
        ("NUGET_PACKAGES", ".nuget-packages"),
        ("NUGET_HTTP_CACHE_PATH", ".nuget-http-cache"),
        ("NUGET_SCRATCH", ".nuget-scratch"),
        ("DOTNET_BUNDLE_EXTRACT_BASE_DIR", ".dotnet-bundle"),
    ):
        path = os.path.join(work, leaf)
        os.makedirs(path, exist_ok=True)
        env[key] = path
    env["AVALONIA_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def msbuild_props(names: list[str], suffix: str | None = None) -> dict[str, str]:
    """从 MSBuild 取属性取值。单属性输出是裸值、多属性是 JSON，两种都要收。

    版本只许从 Directory.Build.props 取；传 -p:Version 会让那个文件降级成「被覆盖的那个」，
    所以这里唯一允许传的是后缀。
    """
    cmd = ["dotnet", "msbuild", APP_PROJ, "-getProperty:" + ",".join(names), "-nologo"]
    if suffix:
        cmd.append("-p:VersionSuffix=" + suffix)
    done = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, env=dotnet_env())
    if done.returncode != 0:
        raise RuntimeError(f"dotnet msbuild 退出码 {done.returncode}：{done.stderr.strip()}")
    text = done.stdout.strip()
    if text.startswith("{"):
        return json.loads(text)["Properties"]
    return {names[0]: text}


def version(suffix: str | None = None) -> str:
    return msbuild_props(["Version"], suffix)["Version"]


def publish(flavor: str, stage: str, suffix: str | None) -> int:
    if os.path.exists(stage):
        shutil.rmtree(stage)
    os.makedirs(stage, exist_ok=True)
    cmd = [
        "dotnet", "publish", APP_PROJ,
        "-c", "Release", "-r", "win-x64",
        # ★ -m:1 不能省：沙箱拒命名管道，多节点会让 restore 报出与代码无关的假错误
        "-m:1",
        *FLAVORS[flavor],
        "-o", stage,
    ]
    if suffix:
        cmd.append("-p:VersionSuffix=" + suffix)
    return sh(cmd, env=dotnet_env())


def remove_tree(path: str) -> None:
    """删掉上一次的产物目录。被占用时给一句能照做的说明，不把 Python 的调用栈丢给出包者。

    实际遇到的是上一份产物还开着：`rmtree` 抛 `WinError 32`，而出包者看到的是 traceback。
    """
    if not os.path.exists(path):
        return
    try:
        shutil.rmtree(path)
    except OSError as exc:
        raise RuntimeError(
            f"删不掉上一次的产物目录：{path}（{exc.strerror or exc}）。"
            "多半是有程序正开着它 —— 先关掉那一份再重跑。") from exc


def assemble(stage: str, dest: str) -> None:
    """白名单组装：publish 产物全部保留（除 pdb），包根再补上 PACKED_ROOT 那几份。

    AOT 版那三个 native 与 exe 同留在包根：同目录时靠 DLL 搜索路径就够，不必另收一层。
    """
    remove_tree(dest)
    os.makedirs(dest, exist_ok=True)
    for root, _dirs, files in os.walk(stage):
        for name in files:
            if name.lower().endswith(".pdb"):
                continue
            src = os.path.join(root, name)
            rel = os.path.relpath(src, stage)
            # 引擎子树各自那份清单不进包：包根那份已经是全量清单，
            # 仓库里那两份是「这几份二进制有没有被换过」的凭据，跟仓库走，用户不需要。
            if name == SUMS and rel.split(os.sep)[0] == ENGINES_DIR:
                continue
            out = os.path.join(dest, rel)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            shutil.copy2(src, out)
    for name in PACKED_ROOT:
        src = packed_source(name)
        out = os.path.join(dest, name)
        if name.lower().endswith((".html", ".htm")):
            write_packed_text(src, out)
        else:
            shutil.copy2(src, out)
    # exe 改名成用户可见的那个名字；fd 的 apphost 按内嵌名找 dll，改名不影响它
    exe = os.path.join(dest, APP_EXE)
    if not os.path.exists(exe):
        raise RuntimeError(f"publish 产物里没有 {APP_EXE}：{stage}")
    shutil.move(exe, os.path.join(dest, PACKED_EXE))


# 包根那几份文本进包前要剥注释：`README.html` 的排版注释（某条 CSS 为什么这么写、页尾脚本
# 为什么这么算）是写给改这份 HTML 的人看的，拿到包的人只需要页面本身。剥离用 `publishing\`
# 那支按语法定位的实现 —— `spans_html` 只认 HTML 注释与 `<style>`／`<script>` 段内的块注释，
# 许可视图里那些第三方许可原文（含 16 处 `/*` 与 314 行 `//`）是正文，不碰。
# 公开仓没有 `publishing\`（那个目录不进公开仓），而它的 `docs\README.html` 本来就是同步时
# 剥过的：拿不到剥离器就直拷，两种情况都由 `check_text_comments` 兜底。
def write_packed_text(src: str, dst: str) -> None:
    try:
        sys.path.insert(0, os.path.join(REPO, "publishing"))
        import clean_public
    except ImportError:
        shutil.copy2(src, dst)
        return
    # 读与写都带 newline=""：默认的通用换行在**读**的时候就把 CRLF 折成 LF 了，写回时补不回来。
    # 这份文件在 `.gitattributes` 里是 `-text`，源与 `clean_public` 那条路都保留 CRLF。
    with open(src, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    stripped, _ = clean_public.rewrite(text, clean_public.spans_html(text), strip=True)
    # 不压连续空行：许可正文里本来就有，压了等于改正文（`clean_public` 那一档对 HTML 同样不压）。
    with open(dst, "w", encoding="utf-8", newline="") as fh:
        fh.write(stripped)


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sums_path(dest: str) -> str:
    """包内哈希清单的落点。行内路径相对包根，用户在包根跑 sha256sum -c SHA256SUMS。"""
    return os.path.join(dest, SUMS)


def write_sums(dest: str) -> int:
    """包内哈希清单：每行「哈希 + 两个空格 + 相对包根的路径」，与 sha256sum -c 兼容。"""
    rows = []
    for root, _dirs, files in os.walk(dest):
        for name in sorted(files):
            if name == SUMS:
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, dest).replace(os.sep, "/")
            rows.append(f"{sha256(full)}  {rel}\n")
    rows.sort(key=lambda r: r.split("  ", 1)[1])
    with open(sums_path(dest), "w", encoding="utf-8", newline="\n") as fh:
        fh.writelines(rows)
    return len(rows)


def verify_sums(dest: str) -> list[str]:
    bad = []
    with open(sums_path(dest), encoding="utf-8") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if not line:
                continue
            want, rel = line.split("  ", 1)
            full = os.path.join(dest, rel.replace("/", os.sep))
            if not os.path.exists(full):
                bad.append(f"缺文件 {rel}")
            elif sha256(full) != want:
                bad.append(f"哈希不符 {rel}")
    return bad


def check_blacklist(dest: str) -> list[str]:
    bad = []
    for root, dirs, files in os.walk(dest):
        for name in list(dirs):
            if name in FORBIDDEN_DIRS:
                bad.append(f"目录进了包 {os.path.relpath(os.path.join(root, name), dest)}")
        for name in files:
            rel = os.path.relpath(os.path.join(root, name), dest)
            if name in FORBIDDEN_NAMES:
                bad.append(f"文件进了包 {rel}")
            if name.lower().endswith(FORBIDDEN_EXT):
                bad.append(f"源码类文件进了包 {rel}")
    return bad


# 随包的那几份文本（隐私扫描只扫它们）：包内其余东西是二进制，按 UTF-8 试读必然失败。
TEXT_GLOBS = ("README.html", "engines/7z/*.txt", "engines/unrar/*.txt")

# 按扩展名认「这是文本」：能按 UTF-8 读、参与隐私与禁词扫描。
# **权威在这一份**（它进公开仓、内容是中性的）—— 文本判定只能有一处定义：
# 三处各写一份必然漏一段，而「读不了」与「干净」在结果上不可区分。
TEXT_EXTS = (
    ".txt", ".md", ".json", ".yml", ".yaml", ".xml", ".props", ".targets",
    ".cs", ".csproj", ".slnx", ".axaml", ".xaml", ".manifest", ".vcxproj",
    ".h", ".cpp", ".def", ".py", ".ps1", ".cmd", ".bat",
    ".editorconfig", ".gitattributes", ".gitignore", ".template",
)


def is_text(path: str) -> bool:
    """按扩展名判文本。`.editorconfig` 这类无扩展名的用全名比对。"""
    name = os.path.basename(path).lower()
    if name in TEXT_EXTS:
        return True
    return os.path.splitext(name)[1] in TEXT_EXTS


def privacy_needles() -> list[str]:
    """当前环境里那几个「只属于本机」的串。取不到就返回空 —— 空表由调用方当失败处理。"""
    return [n for n in (os.environ.get("USERNAME", ""), os.environ.get("USERPROFILE", "")) if n]


def check_privacy(dest: str) -> list[str]:
    """隐私扫描：本机用户名、用户目录与盘符路径出现在随包文本里即失败。

    读不了也失败 —— 读不了与干净在结果上不可区分，那是 B-2026-062 的形态。
    """
    needles = [*privacy_needles(), ":\\Users\\"]
    hits = []
    for pattern in TEXT_GLOBS:
        for full in sorted(glob.glob(os.path.join(dest, *pattern.split("/")))):
            rel = os.path.relpath(full, dest)
            try:
                with open(full, "r", encoding="utf-8", errors="strict") as fh:
                    text = fh.read()
            except (OSError, UnicodeDecodeError) as exc:
                hits.append(f"{rel} 读不了：{type(exc).__name__}")
                continue

            for needle in needles:
                if needle in text:
                    hits.append(f"{rel} 出现 {needle}")
    return hits


def check_structure(dest: str, flavor: str) -> list[str]:
    bad = []
    required = [
        os.path.join(engine_dir("7z"), "7z.exe"),
        os.path.join(engine_dir("7z"), "7z.dll"),
        os.path.join(engine_dir("7z"), "License.txt"),
        os.path.join(engine_dir("unrar"), "UnRAR.exe"),
        os.path.join(engine_dir("unrar"), "license.txt"),
        SUMS,
        PACKED_EXE,
        # 包根那几份的名单只有 PACKED_ROOT 一处：加了随包文件却忘了同步这份必需清单，在这里报。
        *PACKED_ROOT,
    ]
    for rel in required:
        if not os.path.exists(os.path.join(dest, rel)):
            bad.append(f"缺 {rel}")
    for engine in ENGINE_DIRS:
        if os.path.exists(os.path.join(dest, engine_dir(engine), SUMS)):
            bad.append(f"{engine_dir(engine)}\\{SUMS} 不该进包：包内的全量清单只有包根那份 {SUMS}")
    bad.extend(check_layout(dest, flavor))
    bad.extend(check_text_comments(dest))
    return bad


def check_text_comments(dest: str) -> list[str]:
    """随包文本里不许有注释：HTML 注释，以及 `<style>`／`<script>` 段内的块注释。

    判据独立于装配时那个剥离器（公开仓没有 `publishing\\` 那个目录，本脚本两仓都要能跑）：撤掉剥离，
    这里立刻红。**只在两个段内找 `/*`** —— 许可原文里就有它，全文找会把正文当注释报出来；
    `<script>` 里的 `//` 也不判，它与字符串里的 `//` 不可区分（剥离器同样不删它）。
    """
    bad = []
    for name in PACKED_ROOT:
        if not name.lower().endswith((".html", ".htm")):
            continue
        full = os.path.join(dest, name)
        if not os.path.exists(full):
            continue                      # 缺件由 check_structure 那份必需清单报
        with open(full, "r", encoding="utf-8", errors="strict") as fh:
            text = fh.read()
        if "<!--" in text:
            bad.append(f"{name} 里还有 HTML 注释")
        for tag in ("style", "script"):
            # 每一段都查：只查第一段的话，第二段起带块注释的包会被漏过。
            for open_tag in re.finditer(rf"<{tag}\b", text, re.I):
                close_tag = re.search(rf"</{tag}\s*>", text[open_tag.end():], re.I)
                if not close_tag:
                    continue
                inner = text[open_tag.end():open_tag.end() + close_tag.start()]
                if "/*" in inner:
                    bad.append(f"{name} 的 <{tag}> 段里还有块注释")
                    break
    return bad


def check_layout(dest: str, flavor: str) -> list[str]:
    """顶层只许有程序本体、白名单文件与白名单目录；**子目录内部多出来的东西也在这里点名**。

    顶层那一层已有检查；这里补的是子目录内部：只查缺件时，多放一个文件不会被任何检查发现。
    """
    bad = []
    allowed_dirs = set(TOP_LEVEL_DIRS)
    allowed_files = {PACKED_EXE, SUMS, *PACKED_ROOT}
    if flavor == "aot":
        allowed_files.update(NATIVE_DLLS)
    for name in sorted(os.listdir(dest)):
        full = os.path.join(dest, name)
        if os.path.isdir(full):
            if name not in allowed_dirs:
                bad.append(f"顶层多出目录 {name}")
        elif name not in allowed_files:
            bad.append(f"顶层多出文件 {name}")

    for name in NATIVE_DLLS:
        if flavor == "aot":
            if not os.path.exists(os.path.join(dest, name)):
                bad.append(f"根下缺 native {name}")
        elif os.path.exists(os.path.join(dest, name)):
            bad.append(f"native 没内嵌进 exe：{name}")

    bad.extend(check_layout_engines(dest))
    return bad


def engine_manifest(name: str) -> tuple[str, ...]:
    """仓库里那份 SHA256SUMS 点名的文件名。读不了就回空表 —— 缺件与哈希不符归 check_bundles 点名。"""
    sums = os.path.join(REPO, "src", "Bbu.App", ENGINES_DIR, name, SUMS)
    names = []
    try:
        with open(sums, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split()
                if len(parts) == 2:
                    names.append(parts[1].lstrip("*"))
    except OSError:
        return ()
    return tuple(names)


def check_layout_engines(dest: str) -> list[str]:
    """引擎子树内部：`engines\\` 下只许那两个子目录，各自内部只许清单点名的那些文件。

    「该有哪些」的依据是仓库里同目录的那份 SHA256SUMS（与 check_bundles 同一份凭据），
    所以这里只报多出来的东西。
    """
    bad = []
    root = os.path.join(dest, ENGINES_DIR)
    if not os.path.isdir(root):
        return bad
    for name in sorted(os.listdir(root)):
        if os.path.isdir(os.path.join(root, name)):
            if name not in ENGINE_DIRS:
                bad.append(f"{ENGINES_DIR}\\ 下多出目录 {name}")
        else:
            bad.append(f"{ENGINES_DIR}\\ 下多出文件 {name}")

    for engine in ENGINE_DIRS:
        folder = os.path.join(root, engine)
        if not os.path.isdir(folder):
            continue
        allowed = set(engine_manifest(engine))
        for entry in sorted(os.listdir(folder)):
            if entry not in allowed:
                bad.append(f"{ENGINES_DIR}\\{engine}\\ 下多出 {entry}")
    return bad


def check_bundles(dest: str) -> list[str]:
    """引擎子树的哈希逐条核对：清单取自仓库（跟着仓库走），核的是包内文件。

    对不上或缺失一律失败，不许跳过。清单本身不进包 —— 包内的全量清单是包根那份 SHA256SUMS，
    所以这里读的是 src\\Bbu.App\\engines\\<引擎>\\ 那一份。
    """
    bad = []
    for engine in ENGINE_DIRS:
        sums = os.path.join(REPO, "src", "Bbu.App", ENGINES_DIR, engine, SUMS)
        if not os.path.exists(sums):
            bad.append(f"仓库里缺 src\\Bbu.App\\{ENGINES_DIR}\\{engine}\\{SUMS}")
            continue
        with open(sums, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split()
                if len(parts) != 2:
                    continue
                want, name = parts
                full = os.path.join(dest, engine_dir(engine), name.lstrip("*"))
                if not os.path.exists(full):
                    bad.append(f"{engine_dir(engine)} 缺文件 {name}")
                elif sha256(full) != want.lower():
                    bad.append(f"{engine_dir(engine)} 哈希不符 {name}")
    return bad


def zip_dir(dest: str, zip_path: str, root_name: str) -> None:
    """把 dest 打成一个 zip；包内顶层目录名取 root_name。

    包内顶层名与解包目录名**不是**同一个：解包目录落在
    `.scratch\\release\\<flavor>\\` 下，是内部中间产物；用户解压 zip 看到的那一层
    要能看出平台与形态，所以取对外名。
    """
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for root, dirs, files in os.walk(dest):
            dirs.sort()
            for name in sorted(files):
                full = os.path.join(root, name)
                zf.write(full, os.path.join(root_name, os.path.relpath(full, dest)))


def zip_basename(ver: str, flavor: str) -> str:
    """对外 zip 的名字（不带扩展名）。

    那个位置该放**平台与形态**，不是构建技术名（`-aot` / `-fd` 是 Ahead-Of-Time /
    framework-dependent，用户拿到文件看不出是哪个平台、是不是免安装）。
    fd 只作本地自测、不对外，所以它沿用 flavor 以示区别。
    版本号不带 `v`：与公开仓既有的 tag `1.0.0` / `1.1.0` 同口径；
    界面上那个 `v` 是另一处惯例（文件名里版本是被连字符分出来的并列字段）。
    """
    if flavor == "aot":
        return f"BullBullUnpacker-{ver}-win-x64-portable"
    return f"BullBullUnpacker-{ver}-{flavor}"


def copy_artifacts(zip_path: str, target: str) -> None:
    """把 zip 与同名 .sha256 成对复制到 target（--copy-to 用），覆盖同名旧文件：分开落两地会让哈希对不上号。"""
    for src in (zip_path, zip_path + ".sha256"):
        shutil.copy2(src, os.path.join(target, os.path.basename(src)))


def mb(path: str) -> float:
    if os.path.isdir(path):
        total = 0
        for root, _dirs, files in os.walk(path):
            total += sum(os.path.getsize(os.path.join(root, f)) for f in files)
        return total / (1 << 20)
    return os.path.getsize(path) / (1 << 20)


def one(flavor: str, out: str, ver: str, suffix: str | None,
        skip_publish: bool, keep_stage: bool, copy_to: str | None) -> tuple[int, dict]:
    # 三个名字各管一件事：解包目录名是内部中间产物的落点，
    # 对外 zip 名说清平台与形态，包内顶层名取对外名（用户解压后看到的就是它）。
    dir_name = f"BullBullUnpacker-{ver}-{flavor}"
    zip_name = zip_basename(ver, flavor)
    stage = os.path.join(out, "_stage-" + flavor)
    dest = os.path.join(out, flavor, dir_name)
    zip_path = os.path.join(out, zip_name + ".zip")

    print(f"\n===== {flavor} =====")
    if not skip_publish:
        code = publish(flavor, stage, suffix)
        if code != 0:
            print(f"  publish 失败，退出码 {code}")
            return 2, {}
    if not os.path.isdir(stage):
        print(f"  没有 publish 产物：{stage}")
        return 2, {}

    assemble(stage, dest)
    files = write_sums(dest)

    problems = (check_blacklist(dest) + check_structure(dest, flavor)
                + check_bundles(dest) + verify_sums(dest) + check_privacy(dest))
    zip_dir(dest, zip_path, zip_name)
    zip_sum = sha256(zip_path)
    with open(zip_path + ".sha256", "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"{zip_sum}  {os.path.basename(zip_path)}\n")

    unpacked, packed = mb(dest), mb(zip_path)
    print(f"  包内文件 {files} 个，解包 {unpacked:.2f} MB，zip {packed:.2f} MB")
    print(f"  zip SHA256 {zip_sum}")
    print(f"  落点 {zip_path}")
    if problems:
        print("  检查没过：")
        for p in problems:
            print(f"    - {p}")
        return 1, {"flavor": flavor, "unpacked": unpacked, "packed": packed,
                   "problems": problems, "zip": zip_path}
    if not skip_publish and not keep_stage:
        shutil.rmtree(stage, ignore_errors=True)
    if copy_to:
        try:
            copy_artifacts(zip_path, copy_to)
        except OSError as exc:
            print(f"  复制到 {copy_to} 失败：{exc}")
            return 2, {}
        print(f"  已复制到 {os.path.join(copy_to, os.path.basename(zip_path))}")
    return 0, {"flavor": flavor, "unpacked": unpacked, "packed": packed,
               "problems": [], "zip": zip_path}


def main() -> int:
    ap = argparse.ArgumentParser(description="出便携包：AOT 与框架依赖各一份")
    ap.add_argument("--flavor", choices=("aot", "fd", "both"), default="both")
    ap.add_argument("--out", default=os.path.join(REPO, ".scratch", "release"))
    ap.add_argument("--skip-publish", action="store_true", help="只重组已有的 _stage-* 产物")
    ap.add_argument("--keep-stage", action="store_true", help="保留中间 publish 目录")
    ap.add_argument("--suffix", default=None,
                    help="预发布后缀（点分数字，如 beta.1）：只转成 -p:VersionSuffix=")
    ap.add_argument("--copy-to", default=None,
                    help="出包后把 zip 与同名 .sha256 复制到这个目录（覆盖同名旧文件）")
    args = ap.parse_args()

    if shutil.which("dotnet") is None and not args.skip_publish:
        print("找不到 dotnet", file=sys.stderr)
        return 2

    try:
        ver = version(args.suffix)
    except RuntimeError as exc:
        print(f"取版本号失败：{exc}", file=sys.stderr)
        return 2

    os.makedirs(args.out, exist_ok=True)
    # 先建目录并试写一次：出完包（数分钟）才发现写不进去，那一趟就白跑了；沙箱里靠这一步提前拒掉。
    if args.copy_to:
        try:
            os.makedirs(args.copy_to, exist_ok=True)
            probe = os.path.join(args.copy_to, ".bbu-copy-probe")
            with open(probe, "w", encoding="utf-8") as fh:
                fh.write("")
            os.remove(probe)
        except OSError as exc:
            print(f"复制目标写不进去：{exc}", file=sys.stderr)
            return 2
    flavors = ("aot", "fd") if args.flavor == "both" else (args.flavor,)

    results, worst = [], 0
    for flavor in flavors:
        try:
            code, info = one(flavor, args.out, ver, args.suffix, args.skip_publish, args.keep_stage, args.copy_to)
        except RuntimeError as exc:
            # 组装阶段可读的失败（产物目录被占用、随包文档不在）走这里，退出码与「前置不成立」同档
            print(f"  {flavor} 出包失败：{exc}", file=sys.stderr)
            worst = max(worst, 2)
            continue
        worst = max(worst, code)
        if info:
            results.append(info)

    print("\n===== 汇总 =====")
    for info in results:
        flag = "通过" if not info["problems"] else f"没过（{len(info['problems'])} 项）"
        print(f"  {info['flavor']}: 解包 {info['unpacked']:.2f} MB，"
              f"zip {info['packed']:.2f} MB，{flag}")
    return worst


if __name__ == "__main__":
    raise SystemExit(main())
