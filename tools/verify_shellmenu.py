"""右键菜单 / 单实例转发的手工验证脚本（会动系统，所以不塞进 smoke_* 自动跑）。

验证四件事：
  1. 第一个窗口起来后，第二个进程**被转交**（自己退出，不再开一个窗口）；
  2. 一次"多选"起的多个进程都被同一个窗口接住（资源管理器就是这样起进程的）；
  3. 转交进来的路径**只进待处理列表、不自己开跑**（菜单只有这一种模式），
     而手工加 `--auto` 时该跑还是会跑（产物真的出现在盘上）；
  4. 把「添加到BBU解压列表」装进当前用户的右键菜单（HKCU）。

**注意**：它会先杀掉所有 pythonw 窗口（清场），跑完会留下真实的右键菜单项
——不想要就在「设置 → 资源管理器右键菜单」点一下「移除」。

**要在沙箱外跑**：受控环境里 Windows 命名管道是"拒绝访问"的，
单实例转发会静默退化成"各开一个窗口"（功能不受影响）。

用法：
    .venv\\Scripts\\python.exe tools\\verify_shellmenu.py
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PY = os.path.join(ROOT, ".venv", "Scripts", "pythonw.exe")
DEMO = os.path.join(ROOT, "tests", "demo")
FILES = ["嵌套.zip", "教程视频.mp4", "示例包.zip"]
OUTS = ["嵌套", "教程视频", "示例包"]
ok: list[tuple[bool, str, str]] = []


def check(flag: bool, name: str, detail: str = "") -> None:
    ok.append((flag, name, detail))
    print(f"  [{'PASS' if flag else 'FAIL'}] {name}" + (f"  — {detail}" if detail else ""))


def alive() -> list[int]:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq pythonw.exe", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    return [int(ln.split()[1]) for ln in out.splitlines() if "pythonw" in ln.lower()]


def hittest_probe(title: str = "BullBull Unpacker") -> list[tuple[str, int, int]]:
    """问窗口一句"这一点算哪儿"（发 WM_NCHITTEST，看返回的 HT 码）。

    这个探针抓过一个真 bug：WM_NCHITTEST 给的是**物理像素**坐标、Qt 的几何是逻辑像素，
    200% 缩放下不换算 → 点窗口中心被判成"右下角之外" → 整个窗口点不动也拖不动。
    期望值：客户区 1 / 顶栏 2 / 左边缘 10 / 右下角 17。
    """
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    found: list[int] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _lparam):
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if title in buf.value and user32.IsWindowVisible(hwnd):
                found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    if not found:
        return []
    hwnd = found[0]
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    points = [
        ("客户区中心", rect.left + w // 2, rect.top + h // 2, 1),
        ("顶栏空白", rect.left + w // 2, rect.top + 40, 2),
        ("左边缘", rect.left + 3, rect.top + h // 2, 10),
        ("右下角", rect.right - 3, rect.bottom - 3, 17),
        ("关闭按钮附近", rect.right - 30, rect.top + 40, 1),
    ]
    out: list[tuple[str, int, int]] = []
    for name, x, y, expect in points:
        res = user32.SendMessageW(hwnd, 0x0084, 0, (y << 16) | (x & 0xFFFF))
        out.append((name, int(res), expect))
    return out


def main() -> int:
    print("== 清场：杀掉旧窗口、清掉旧的产物目录 ==")
    subprocess.run(["taskkill", "/F", "/IM", "pythonw.exe"],
                   capture_output=True)
    time.sleep(1.5)
    for name in OUTS:
        for suffix in ("", " (1)", " (2)", " (3)"):
            shutil.rmtree(os.path.join(DEMO, name + suffix), ignore_errors=True)

    print("\n== 1) 起第一个窗口 ==")
    state_file = os.path.join(ROOT, "tests", "work", "shell-state.txt")
    if os.path.exists(state_file):
        os.remove(state_file)
    # ★ 验证跑的是**真窗口**，它会读真 config.json——用户配置里可能是"指定目录"
    #   （比如解压到桌面），那样这个脚本的 --auto 那步会把测试产物扔进用户的目录里，
    #   而且"产物在 tests/demo"这种断言会莫名其妙地失败。所以给它一份临时配置。
    cfg_path = os.path.join(ROOT, "tests", "work", "verify-config.json")
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump({"theme": "dark", "output_mode": "same", "conflict": "rename",
                   "max_depth": 5, "scan_appended": True, "remove_intermediate": True},
                  f, ensure_ascii=False)
    env = dict(os.environ, SMART_UNZIP_STATE_FILE=state_file,
               SMART_UNZIP_CONFIG=cfg_path)
    a = subprocess.Popen([PY, "run.py"], cwd=ROOT, env=env)
    time.sleep(5)
    check(a.poll() is None, "第一个窗口活着", f"pid={a.pid}")

    print("\n== 1b) 窗口能不能点（WM_NCHITTEST 命中测试）==")
    probe = hittest_probe()
    if not probe:
        print("  [SKIP] 没找到窗口（可能被别的窗口挡住/标题变了）")
    else:
        bad = [(n, got, want) for n, got, want in probe if got != want]
        check(not bad, "★ 命中测试对得上（客户区 1 / 顶栏 2 / 左边缘 10 / 右下角 17）",
              str(probe) if not bad else f"错的：{bad} 全部：{probe}")

    # ★ 最大化之后顶栏**仍然**要算标题栏：双击还原就靠这一条。
    #   以前 `_hit_test` 在最大化时直接返回 HTCLIENT → `_titlebar_at()` 永远为假 →
    #   我们自己的双击处理根本不触发，用户看到"能最大化、再双击不恢复"。
    print("\n== 1c) 最大化状态下顶栏还是标题栏吗（双击还原那一条）==")
    import ctypes
    from ctypes import wintypes

    u = ctypes.WinDLL("user32", use_last_error=True)

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def _find(hwnd, _lp):
        n = u.GetWindowTextLengthW(hwnd)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(hwnd, b, n + 1)
            if "BullBull Unpacker" in b.value and u.IsWindowVisible(hwnd):
                _find.hwnd = hwnd
        return True

    _find.hwnd = 0                                        # type: ignore[attr-defined]
    u.EnumWindows(_find, 0)
    hwnd = int(_find.hwnd)                                # type: ignore[attr-defined]
    if not hwnd:
        print("  [SKIP] 没找到窗口")
    else:
        u.ShowWindow(wintypes.HWND(hwnd), 3)              # SW_MAXIMIZE
        time.sleep(1.0)
        r = wintypes.RECT()
        u.GetWindowRect(wintypes.HWND(hwnd), ctypes.byref(r))
        x, y = (r.left + r.right) // 2, r.top + 48
        res = int(u.SendMessageW(wintypes.HWND(hwnd), 0x0084, 0,
                                 (y << 16) | (x & 0xFFFF)))
        check(res == 2, "★ 最大化后顶栏仍算标题栏（HTCAPTION=2，双击才能还原）",
              f"got={res} 窗口={r.right - r.left}×{r.bottom - r.top}")
        u.ShowWindow(wintypes.HWND(hwnd), 9)              # SW_RESTORE
        time.sleep(0.6)

    print("\n== 2) 模拟右键多选：同时起 3 个进程，每个带一个文件（菜单就是这么起的）==")
    starters = []
    t0 = time.monotonic()
    for name in FILES:
        path = os.path.join(DEMO, name)
        starters.append((name, subprocess.Popen([PY, "run.py", path], cwd=ROOT, env=env)))
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        if all(p.poll() is not None for _, p in starters):
            break
        time.sleep(0.05)
    elapsed = time.monotonic() - t0
    forwarded = [(n, p.returncode) for n, p in starters if p.poll() is not None]
    check(len(forwarded) == 3,
          "★ 3 个后起的进程都被转交并自己退出（没有开出 3 个新窗口）",
          str(forwarded))
    check(len(alive()) <= 2, "★ 进程数没爆炸（一个界面 + 一个启动器）", str(alive()))
    # 快路径的意义：不 import Qt，几百毫秒就结束——"右键卡半天"就是这么治的
    check(elapsed < 4.0, "★ 3 个进程全部转交完用时很短（快路径没加载 Qt）", f"{elapsed:.2f}s")

    print("\n== 2b) 只加进待处理列表，不许自己开跑 ==")
    time.sleep(4)
    early = [n for n in OUTS if os.path.isdir(os.path.join(DEMO, n))]
    check(not early, "★ 右键进来只挂清单、不自动开始（还没人点「开始」）", str(early))

    # ★ 真正的断言：那三个路径**真的进清单了**（以前只验"没自动开跑"，漏掉过
    #   "转发消息到了但清单里没有"这种 bug；窗口把清单写到 state_file 里给这里看）
    listed: list[str] = []
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if os.path.isfile(state_file):
            listed = [ln.strip() for ln in open(state_file, encoding="utf-8") if ln.strip()]
            if len(listed) >= 3:
                break
        time.sleep(0.3)
    check(all(n in listed for n in FILES),
          "★ 三个路径都真的进了待处理列表（不只是「转发成功」）", f"{listed}")

    print("\n== 3) 手工再起一个带 --auto 的：该跑还是要跑 ==")
    subprocess.Popen([PY, "run.py", "--auto", os.path.join(DEMO, FILES[0])], cwd=ROOT, env=env)
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        done = [n for n in OUTS if os.path.isdir(os.path.join(DEMO, n))]
        if len(done) == len(OUTS):
            break
        time.sleep(0.5)
    missing = [n for n in OUTS if not os.path.isdir(os.path.join(DEMO, n))]
    check(not missing, "★ 三个文件的产物都出来了（挂进清单的 + --auto 起来的都跑了）",
          str(missing))
    for n in OUTS:
        d = os.path.join(DEMO, n)
        inner = os.listdir(d) if os.path.isdir(d) else []
        check(bool(inner), f"「{n}」产物目录里有东西", str(inner[:4]))
        break

    print("\n== 4) 注册真实右键菜单（HKCU，设置页一键可移除）==")
    sys.path.insert(0, ROOT)
    from core import shellmenu as sm  # noqa: E402

    keys = sm.install(
        sm.pythonw_for(sys.executable),
        os.path.join(ROOT, "run.py"),
        icon=os.path.join(ROOT, "assets", "bbu.ico"),
    )
    st = sm.state()
    check(st["installed"], "★ 已写进当前用户的右键菜单", str(keys))
    check("pythonw.exe" in st["command"], "命令用的是 pythonw（不闪黑框）", st["command"])
    check("--auto" not in st["command"], "★ 菜单命令不带 --auto（只挂清单）", st["command"])

    # ★ 冷启动带路径：这是用户报"右键加了但清单里没有"的那个场景
    #   （窗口还没起来 → 进程自己是主实例 → 走 Workbench(launch_paths=...) 那条路。
    #    以前 launch_paths 全工程没人读，窗口开出来是空的。）
    print("\n== 5) 冷启动：还没有窗口时右键一个文件（用注册表里那条真命令）==")
    subprocess.run(["taskkill", "/F", "/IM", "pythonw.exe"], capture_output=True)
    time.sleep(1.5)
    if os.path.exists(state_file):
        os.remove(state_file)
    import winreg

    def menu_command(place: str) -> str:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            rf"Software\Classes\{place}\shell\{sm.VERB}\command") as k:
            return str(winreg.QueryValueEx(k, None)[0])

    cold_file = os.path.join(DEMO, FILES[0])
    cmd = menu_command(r"*").replace("%1", cold_file)
    subprocess.Popen(cmd, cwd=ROOT, shell=False, env=env)
    listed = []
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        time.sleep(0.5)
        if os.path.isfile(state_file):
            listed = [ln.strip() for ln in open(state_file, encoding="utf-8")
                      if ln.strip() and not ln.startswith("#")]
            if FILES[0] in listed:
                break
    check(FILES[0] in listed,
          "★ 冷启动（还没窗口时右键）路径也真的进了清单", f"{listed}")

    print("\n== 6) 冷启动：文件夹空白处右键（%V → 目录）==")
    subprocess.run(["taskkill", "/F", "/IM", "pythonw.exe"], capture_output=True)
    time.sleep(1.5)
    if os.path.exists(state_file):
        os.remove(state_file)
    cmd = menu_command(r"Directory\Background").replace("%V", DEMO).replace("%1", DEMO)
    subprocess.Popen(cmd, cwd=ROOT, shell=False, env=env)
    listed2: list[str] = []
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        time.sleep(0.5)
        if os.path.isfile(state_file):
            listed2 = [ln.strip() for ln in open(state_file, encoding="utf-8")
                       if ln.strip() and not ln.startswith("#")]
            if os.path.basename(DEMO) in listed2:
                break
    check(os.path.basename(DEMO) in listed2,
          "★ 冷启动（还没窗口时）空白处右键也真的进了清单", f"{listed2}")

    print("\n== 结果 ==")
    for flag, name, detail in ok:
        print(f"  [{'PASS' if flag else 'FAIL'}] {name}  {detail}")
    print(f"  第一个窗口 pid={a.pid}（留给用户用）")
    return 0 if all(f for f, _, _ in ok) else 1


if __name__ == "__main__":
    raise SystemExit(main())
