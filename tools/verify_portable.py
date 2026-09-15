"""验收"打包出来的便携版"：**对着真实的 exe** 跑一遍能自动验的东西。

验什么（都是在源码版验不到、只有冻结之后才会暴露的问题）：

  1. 目录结构对不对（exe / _internal / tools\\7z / assets / 文档）；
  2. `--where` 认得自己是 exe、资源在 _internal、**数据目录就在程序旁边（便携）**；
  3. 内置 7z 真的能跑（拿一个真夹具解一遍，产物里要有文件）；
  4. 拖进来的路径能进清单（用 `SMART_UNZIP_STATE_FILE` 从外面看；顺带验冷启动）；
  5. 右键菜单注册的是 **exe 自己**（用临时注册表根，不碰用户真菜单）→ 再卸掉；
  6. 窗口能起来（标题/尺寸/DWM 圆角）。

为了不把用户数据写进要发布的 dist，脚本会先把便携目录**拷一份**到
`tests/work/portable-test/`，所有验证都在副本里做。

用法：
    .venv\\Scripts\\python.exe tools\\verify_portable.py
"""

from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import time
import winreg
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # <big>/src
BIG = os.path.dirname(ROOT)                                          # <big>
sys.path.insert(0, ROOT)

from core import appinfo  # noqa: E402

# 构建产物住在 <big>/github/release/（目录约定见 tools/sync_repo.py）
SRC = os.path.join(BIG, "github", "release", appinfo.APP_NAME)
WORK = os.path.join(ROOT, "tests", "work", "portable-test")
TEST_ROOT = r"Software\BBUTest\Portable\Classes"

user32 = ctypes.WinDLL("user32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:                                     # noqa: BLE001
    pass

ok: list[tuple[bool, str, str]] = []


def check(flag: bool, name: str, detail: str = "") -> None:
    ok.append((bool(flag), name, detail))
    print(f"  [{'PASS' if flag else 'FAIL'}] {name}" + (f"  — {detail}" if detail else ""))


def drop_tree(path: str) -> None:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            subs = []
            i = 0
            while True:
                try:
                    subs.append(winreg.EnumKey(k, i))
                except OSError:
                    break
                i += 1
        for s in subs:
            drop_tree(path + "\\" + s)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except OSError:
        pass


def read_log_lines(portable: str) -> list[str]:
    """读便携版自己写的日志（打包版没有控制台，`--where` 的输出落在这儿）。"""
    log = os.path.join(portable, "logs", "ui.log")
    if not os.path.isfile(log):
        return []
    with open(log, encoding="utf-8", errors="replace") as f:
        return f.read().splitlines()


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    r = wintypes.RECT()
    user32.GetWindowRect(wintypes.HWND(hwnd), ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def find_window(title: str, pid: int | None = None) -> int:
    """找应用的主窗口。**优先按进程号**——光靠标题会被"标题里带项目名的别的窗口"骗到。

    实测踩过：桌面上开着一个文件资源管理器窗口，标题是
    `BullBull Unpacker 和 1 个其他选项卡 - 文件资源管理器`，
    于是"窗口能起来吗"这一条量到的是 Explorer 的尺寸、DWM 圆角也是 0，
    看起来像打包版坏了。给了 pid 就只认那个进程的窗口。
    """
    found: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _lp):
        if pid is not None:
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(wintypes.HWND(hwnd), ctypes.byref(owner))
            if owner.value != pid:
                return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            ok_title = (pid is not None) or (title in buf.value)
            if ok_title and user32.IsWindowVisible(hwnd):
                r = wintypes.RECT()
                user32.GetWindowRect(wintypes.HWND(hwnd), ctypes.byref(r))
                found.append(((r.right - r.left) * (r.bottom - r.top), hwnd))
        return True

    user32.EnumWindows(cb, 0)
    return max(found)[1] if found else 0


def main() -> int:
    exe_name = appinfo.APP_NAME + ".exe"
    if not os.path.isfile(os.path.join(SRC, exe_name)):
        print(f"没找到打包产物：{SRC}\\{exe_name}（先跑 build\\build_portable.ps1）")
        return 1

    # 拷一份副本再验，别把数据写进要发布的目录
    print("== 复制便携目录到测试位置（不污染 dist）==")
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(os.path.dirname(WORK), exist_ok=True)
    shutil.copytree(SRC, WORK)
    exe = os.path.join(WORK, exe_name)
    print(f"  {SRC}\n  → {WORK}")

    print("\n== 1) 目录结构 ==")
    for rel in (exe_name, "_internal", os.path.join("tools", "7z", "7z.exe"),
                os.path.join("tools", "7z", "7z.dll"), os.path.join("assets", "bbu.ico"),
                os.path.join("docs", "README.txt"),
                os.path.join("docs", "THIRD-PARTY.md")):
        check(os.path.exists(os.path.join(WORK, rel)), f"有 {rel}")
    # 文档目录名是作者定稿的 **docs**（不是 doc、更不是中文「文档」）
    wrong = [n for n in ("文档", "doc") if os.path.isdir(os.path.join(WORK, n))]
    check(not wrong, "★ 文档目录名就是 docs（没有 doc/文档 这两个多余的）", str(wrong))
    check(not os.path.exists(os.path.join(WORK, "core")),
          "★ 没把源码（core/ui/*.py）打进便携包")
    check(not os.path.exists(os.path.join(WORK, "密码本.txt"))
          and not os.path.exists(os.path.join(WORK, "config.json")),
          "★ 初始状态没有用户的密码本/配置（首次运行才生成）")

    print("\n== 2) --where：它知不知道自己是 exe、数据该放哪 ==")
    env = dict(os.environ)
    env["SMART_UNZIP_SHELLMENU_ROOT"] = TEST_ROOT
    subprocess.run([exe, "--where"], env=env, timeout=120,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    lines = read_log_lines(WORK)
    text = "\n".join(lines)
    check("exe（打包版）" in text, "★ exe 认得自己是打包版（is_frozen）", text[-200:])
    check(os.path.normcase(WORK) in os.path.normcase(text) and "_internal" not in [
        ln for ln in lines if "资源目录" in ln][0],
        "★ 资源目录就在程序目录旁边（工具/图标各只有一份，不再塞进 _internal）",
        [ln for ln in lines if "资源目录" in ln][:1])
    check(os.path.normcase(WORK) in os.path.normcase(text),
          "★ 数据目录就在程序旁边（便携）", [x for x in lines if "数据目录" in x][:1])
    seven_line = [x for x in lines if "7-Zip" in x]
    check(seven_line and "有" in seven_line[0]
          and os.path.normcase(seven_line[0]).find(os.path.normcase(WORK)) >= 0,
          "★ 内置 7z 找得到，而且用的是程序旁边那一份（不是 _internal 里的第二份）",
          seven_line[:1])
    check(not os.path.isdir(os.path.join(WORK, "_internal", "tools")),
          "★ _internal 里没有重复的 tools\\7z（体积优化：7-Zip 只有一份）")
    check(not os.path.isfile(os.path.join(WORK, "_internal", "PySide6", "opengl32sw.dll")),
          "★ 已删掉用不到的 opengl32sw.dll（约 20MB）")

    print("\n== 3) 冷启动带路径 + 内置 7z 真能解 ==")
    fixture = os.path.join(ROOT, "tests", "demo", "嵌套.zip")
    out = os.path.join(WORK, "_verify-out")
    os.makedirs(out, exist_ok=True)
    cfg = os.path.join(WORK, "config.json")
    with open(cfg, "w", encoding="utf-8") as f:
        f.write('{"theme": "dark", "output_mode": "custom", "output_dir": %s,'
                ' "conflict": "rename", "max_depth": 3}'
                % __import__("json").dumps(out))
    # 冷启动带路径：让它把清单写进状态文件（这是从进程外面唯一能看到的证据）
    # **必须把两种形态的残留进程都清掉**：源码版（pythonw.exe run.py）和打包版共用
    # 同一个互斥体 + 命名管道，只要有一个活着，新起的 exe 就会把路径转交给它然后自己退出
    # —— 表现是"窗口没起来、状态文件没出现"，看着像打包坏了（实测就是这么误报的）。
    subprocess.run(["taskkill", "/F", "/IM", exe_name], capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "pythonw.exe"], capture_output=True)
    time.sleep(1.5)
    state = os.path.join(WORK, "_state.txt")
    if os.path.exists(state):
        os.remove(state)
    env2 = dict(env)
    env2["SMART_UNZIP_STATE_FILE"] = state
    subprocess.Popen([exe, fixture], env=env2)
    deadline = time.time() + 90
    listed = ""
    while time.time() < deadline:
        if os.path.isfile(state):
            listed = open(state, encoding="utf-8").read()
            if "嵌套.zip" in listed:
                break
        time.sleep(1)
    check("嵌套.zip" in listed,
          "★ 冷启动带路径：那个包真的进了清单（exe 版，验证的是冻结后的启动链路）",
          listed.strip().replace("\n", " | ")[:160] or "(状态文件没出现)")
    subprocess.run(["taskkill", "/F", "/IM", exe_name], capture_output=True)
    time.sleep(1.0)

    print("\n== 4) 右键菜单注册的是 exe 自己（临时注册表根）==")
    subprocess.run([exe, "--install-shellmenu"], env=env, timeout=120,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            TEST_ROOT + r"\*\shell\BBUUnpack\command") as k:
            cmd = str(winreg.QueryValueEx(k, None)[0])
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            TEST_ROOT + r"\*\shell\BBUUnpack") as k:
            icon = str(winreg.QueryValueEx(k, "Icon")[0])
            label = str(winreg.QueryValueEx(k, None)[0])
    except OSError as exc:
        cmd = icon = label = ""
        print(f"    （读注册表失败：{exc}）")
    check("run.py" not in cmd and exe_name in cmd and cmd.endswith('"%1"'),
          "★ 命令直接调 exe，不再经过 pythonw + run.py", cmd)
    check(icon.lower().startswith(os.path.normcase(exe).lower()) and icon.endswith(",0"),
          "★ 图标用 exe 自带的（路径,0）", icon)
    check(label == appinfo.SHELL_MENU_TEXT, "菜单文案正确", label)
    subprocess.run([exe, "--uninstall-shellmenu"], env=env, timeout=120,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    left = True
    try:
        winreg.OpenKey(winreg.HKEY_CURRENT_USER, TEST_ROOT + r"\*\shell\BBUUnpack")
    except OSError:
        left = False
    check(not left, "卸载后注册表项没了")
    drop_tree(r"Software\BBUTest")

    print("\n== 5) 窗口能起来（标题/尺寸/DWM 圆角）==")
    # 先清场：源码版残留（pythonw）或上一次跑剩的窗口也带着同样的标题，
    # find_window 会挑到它 → 量出来的尺寸/圆角全是错的（实测踩过：1565×845、圆角 0）
    subprocess.run(["taskkill", "/F", "/IM", exe_name], capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "pythonw.exe"], capture_output=True)
    time.sleep(1.5)
    proc = subprocess.Popen([exe], env=env)
    # **别用固定等待**：刚打出来的 exe 第一次启动要过一遍杀软/预热，8 秒可能还没出窗口
    # （实测：等 8 秒时只找到个 157×25 的辅助窗口，误判成"默认尺寸不对"）。
    hwnd = 0
    deadline = time.time() + 60
    while time.time() < deadline:
        time.sleep(1.0)
        hwnd = find_window(appinfo.APP_NAME, proc.pid)   # 只认这个进程的窗口
        if hwnd:
            l, t, r, b = window_rect(hwnd)
            if (r - l) > 400 and (b - t) > 400:      # 等到真窗口（不是辅助小窗）
                break
            hwnd = 0
    check(bool(hwnd), "窗口起来了", f"pid={proc.pid} hwnd={hwnd}")
    if hwnd:
        l, t, r, b = window_rect(hwnd)
        dpi = user32.GetDpiForWindow(wintypes.HWND(hwnd)) or 96
        dpr = dpi / 96.0
        value = ctypes.c_int(0)
        hr = dwmapi.DwmGetWindowAttribute(wintypes.HWND(hwnd), 33,
                                          ctypes.byref(value), ctypes.sizeof(value))
        check(hr == 0 and value.value == 2, "★ DWM 圆角生效", f"value={value.value}")
        check(abs((r - l) / dpr - 800) <= 40 and abs((b - t) / dpr - 900) <= 40,
              "★ 默认开窗还是 800×900 逻辑像素",
              f"{(r - l) / dpr:.0f}×{(b - t) / dpr:.0f} (dpr={dpr:g})")
    subprocess.run(["taskkill", "/F", "/IM", exe_name], capture_output=True)
    time.sleep(1.0)

    print("\n== 结果 ==")
    for flag, name, detail in ok:
        print(f"  [{'PASS' if flag else 'FAIL'}] {name}  {detail}")
    bad = [n for f, n, _ in ok if not f]
    print(f"\n  {'全部通过' if not bad else '失败：' + str(bad)}")
    print(f"  （验证在副本里做的，要发布的 {SRC} 没被写脏；"
          f"副本留在 {WORK}，不要了直接删）")
    return 0 if not bad else 1


if __name__ == "__main__":
    raise SystemExit(main())
