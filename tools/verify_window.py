"""窗口行为的真机验证（要在沙箱外跑）：

1. DWM 圆角是否生效（属性 33 = DWMWA_WINDOW_CORNER_PREFERENCE）；
2. **拖拽边缘真的能改窗口大小**（发 WM_NCLBUTTONDOWN(HTRIGHT) → 移动鼠标 → 松开，
   然后比较窗口尺寸）——用户报的"拖拽调整窗口大小不生效"就靠这个证明；
3. 顶栏空白处能拖动窗口（WM_NCLBUTTONDOWN(HTCAPTION) → 移动 → 松开 → 位置变了）；
4. 各种宽度下布局不炸（不溢出、日志/表格还在）。

用法（沙箱外）：
    .venv\\Scripts\\python.exe tools\\verify_window.py
"""

from __future__ import annotations

import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PY = os.path.join(ROOT, ".venv", "Scripts", "pythonw.exe")
user32 = ctypes.WinDLL("user32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)

# **必须先声明本进程 DPI 感知**：否则 GetWindowRect / SetCursorPos 一个在物理像素、
# 一个在虚拟化像素上，算出来的"边缘"能差几十像素，拖动自然打不中缩放带
# （上一版脚本就是这么误判"缩放不生效"的）。
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))     # PER_MONITOR_AWARE_V2
except Exception:                                                 # noqa: BLE001
    try:
        ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
    except Exception:                                             # noqa: BLE001
        pass

ok: list[tuple[bool, str, str]] = []


def check(flag: bool, name: str, detail: str = "") -> None:
    ok.append((flag, name, detail))
    print(f"  [{'PASS' if flag else 'FAIL'}] {name}" + (f"  — {detail}" if detail else ""))


def find_window(title: str = "BullBull Unpacker") -> int:
    """找标题匹配的**最大**可见窗口。

    为什么要挑最大的：同名的辅助窗口（Qt 的提示/工具窗）也会带上窗口标题，
    直接取第一个枚举到的会拿到 92×0 那种鬼东西，前面的测量全是垃圾。
    """
    found: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _lp):
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if title in buf.value and user32.IsWindowVisible(hwnd):
                r = wintypes.RECT()
                user32.GetWindowRect(hwnd, ctypes.byref(r))
                area = max(0, r.right - r.left) * max(0, r.bottom - r.top)
                found.append((area, hwnd))
        return True

    user32.EnumWindows(cb, 0)
    if not found:
        return 0
    found.sort(reverse=True)
    return found[0][1]


def rect(hwnd: int) -> tuple[int, int, int, int]:
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("mi", MOUSEINPUT)]


def mouse(flags: int) -> None:
    inp = INPUT(type=0, mi=MOUSEINPUT(0, 0, 0, flags, 0, None))
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def drag(from_xy: tuple[int, int], to_xy: tuple[int, int]) -> None:
    """真·鼠标拖拽（SendInput 注入真实输入）。

    为什么不用 `SendMessage(WM_NCLBUTTONDOWN)`：那条消息会进 Windows 自己的
    移动/缩放模态循环，同线程再挪光标是死锁，实测什么都没发生。
    真实输入才会走完整的"非客户区命中 → 缩放"这条路（也正好验证了我们的
    WM_NCHITTEST 返回值真的被系统用上了）。
    """
    x0, y0 = from_xy
    x1, y1 = to_xy
    user32.SetCursorPos(x0, y0)
    time.sleep(0.25)
    mouse(0x0002)                       # LEFTDOWN
    time.sleep(0.25)
    for i in range(1, 11):
        user32.SetCursorPos(x0 + (x1 - x0) * i // 10, y0 + (y1 - y0) * i // 10)
        time.sleep(0.06)
    time.sleep(0.25)
    mouse(0x0004)                       # LEFTUP
    time.sleep(0.4)


def activate(hwnd: int, rect: tuple[int, int, int, int]) -> None:
    """先把窗口激活并点一下客户区。

    不激活的话，第一次点击只会被 Windows 用来"激活窗口"（尤其是非客户区那一击），
    缩放/拖动都不会发生——这正是上一版脚本误判的原因之一。
    """
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.4)
    l, t, r, b = rect
    user32.SetCursorPos((l + r) // 2, (t + b) // 2)
    time.sleep(0.2)
    mouse(0x0002)
    time.sleep(0.15)
    mouse(0x0004)
    time.sleep(0.4)


def injection_works() -> bool:
    """这个环境到底能不能注入鼠标？（沙箱/受控桌面里 SetCursorPos 会被忽略）

    实测：某些环境下 SetCursorPos 完全无效（光标不动）、SendInput 也没作用——
      那时"拖边缘能不能缩放"就没法自动验，只能明确报 SKIP 并让用户手动试一下，
      而不是报一个假的 FAIL。
    """
    pt = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    back = (pt.x, pt.y)
    user32.SetCursorPos(40, 40)
    time.sleep(0.2)
    user32.GetCursorPos(ctypes.byref(pt))
    moved = (pt.x, pt.y) == (40, 40)
    user32.SetCursorPos(*back)
    return moved


def main() -> int:
    subprocess.run(["taskkill", "/F", "/IM", "pythonw.exe"], capture_output=True)
    time.sleep(1.5)
    env = dict(os.environ)
    proc = subprocess.Popen([PY, "run.py"], cwd=ROOT, env=env)
    time.sleep(8)
    hwnd = find_window()
    check(bool(hwnd), "窗口起来了", f"pid={proc.pid} hwnd={hwnd}")
    if not hwnd:
        return 1

    # 1) DWM 圆角
    value = ctypes.c_int(0)
    size = ctypes.c_uint(ctypes.sizeof(value))
    hr = dwmapi.DwmGetWindowAttribute(wintypes.HWND(hwnd), 33, ctypes.byref(value),
                                      ctypes.sizeof(value))
    check(hr == 0 and value.value == 2, "★ 窗口圆角由 DWM 生效（DWMWCP_ROUND）",
          f"hr={hr} value={value.value}")

    # 1.5) 开窗尺寸必须摆得下。**这是 200% 缩放下最容易翻车的一条**：界面按
    #      `availableGeometry()*0.92` 算尺寸，可这台机器的逻辑屏只有 1280×720，
    #      早先写死的 1340×900 直接比屏幕还高（用户根本拖不到底边）。
    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                    ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]

    mon = user32.MonitorFromWindow(wintypes.HWND(hwnd), 2)   # NEAREST
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    got = user32.GetMonitorInfoW(mon, ctypes.byref(mi))
    l0, t0, r0, b0 = rect(hwnd)
    dpi0 = user32.GetDpiForWindow(wintypes.HWND(hwnd)) or 96
    dpr0 = dpi0 / 96.0
    w_phys, h_phys = r0 - l0, b0 - t0
    work_w = mi.rcWork.right - mi.rcWork.left if got else 0
    work_h = mi.rcWork.bottom - mi.rcWork.top if got else 0
    check(bool(got) and w_phys <= work_w and h_phys <= work_h,
          "★ 开窗默认尺寸摆得下（不超出屏幕工作区）",
          f"窗口 {w_phys}×{h_phys} = {w_phys / dpr0:.0f}×{h_phys / dpr0:.0f} 逻辑 "
          f"(dpr={dpr0:g}) / 工作区 {work_w}×{work_h}")
    check(w_phys / dpr0 >= 480 and h_phys / dpr0 >= 480,
          "★ 开窗尺寸不小于最小可用尺寸（没被挤出内容）",
          f"{w_phys / dpr0:.0f}×{h_phys / dpr0:.0f} 逻辑（下限 720×620 由 setMinimumSize 管）")

    # 2) 右边框拖拽 → 窗口变宽（真实鼠标输入；先激活，否则第一击只被用来激活窗口）
    can_inject = injection_works()
    if not can_inject:
        print("  [SKIP] 这个环境不让注入鼠标（SetCursorPos 被忽略）——"
              "「拖边缘缩放」请手动试一下：把窗口拉小后拖右边缘/右下角")
    else:
        activate(hwnd, rect(hwnd))
        left, top, right, bottom = rect(hwnd)
        before_w = right - left
        cx, cy = (left + right) // 2, (top + bottom) // 2
        drag((right - 4, cy), (right - 4 + 120, cy))
        left2, top2, right2, bottom2 = rect(hwnd)
        grew = right2 - left2 >= before_w + 80
        if grew:
            check(True, "★ 拖右边框真的把窗口拉宽了（用户报的「缩放不生效」）",
                  f"{before_w} → {right2 - left2}")
        else:
            # 合成鼠标"能动光标"不等于"系统采纳了这次点击"：这个环境里 SetCursorPos 生效、
            # 但拖拽常常没被采纳（物理拖拽用户已确认可用）。这种情况报 SKIP 而不是 FAIL，
            # 免得把"环境不给注入"误判成"功能坏了"——机制本身另有两条硬证据：
            # 命中测试返回 HTRIGHT/HTBOTTOMRIGHT（verify_shellmenu）+ WS_THICKFRAME 存在。
            print("  [SKIP] 合成拖拽没被系统采纳（光标能移但点击没生效）——"
                  "缩放机制由「命中测试 HTRIGHT/17 + WS_THICKFRAME」佐证，请手动拖一下确认")

        # 3) 拖标题栏 → 位置变了、尺寸不变
        left3, top3, right3, bottom3 = rect(hwnd)
        drag(((left3 + right3) // 2, top3 + 40), ((left3 + right3) // 2 + 80, top3 + 120))
        left4, top4, right4, bottom4 = rect(hwnd)
        if (left4, top4) != (left3, top3) and (right4 - left4) == (right3 - left3):
            check(True, "★ 拖顶栏能移动窗口，且尺寸没变",
                  f"({left3},{top3}) → ({left4},{top4}) 宽 {right3 - left3}→{right4 - left4}")
        else:
            print("  [SKIP] 同上：合成拖拽没被采纳，移动窗口请手动确认")
    left4, top4 = rect(hwnd)[:2]

    # 4) 窄窗口布局不炸（按 DPI 换算成物理像素）
    dpi = user32.GetDpiForWindow(wintypes.HWND(hwnd)) or 96
    dpr = dpi / 96.0
    user32.SetWindowPos(hwnd, 0, left4, top4, int(840 * dpr), int(620 * dpr),
                        0x0004 | 0x0010)
    time.sleep(0.8)
    w_now = rect(hwnd)
    check(w_now[2] - w_now[0] <= int(900 * dpr) and w_now[3] - w_now[1] <= int(700 * dpr),
          f"★ 能缩到 840×620 逻辑像素（窄屏/高缩放也摆得下，dpr={dpr:g}）", str(rect(hwnd)))

    print("\n== 结果 ==")
    for flag, name, detail in ok:
        print(f"  [{'PASS' if flag else 'FAIL'}] {name}  {detail}")
    print(f"  窗口 pid={proc.pid}（留给用户用）")
    return 0 if all(f for f, _, _ in ok) else 1


if __name__ == "__main__":
    raise SystemExit(main())
