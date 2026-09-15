"""core 冒烟测试：用真实 7-Zip / WinRAR 造夹具包，验证 probe / naming / engine。

覆盖：伪装文件、分卷主卷、文件名密码九种写法、中文密码、中文文件名、
      「验证与解压分离」、rar 专用退出码 11。

用法：
    .venv\\Scripts\\python.exe tools\\smoke_core.py
"""

from __future__ import annotations

import os
import random
import shutil
import struct
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from core import naming, probe  # noqa: E402
from core.engine import EngineKind, Extractor, find_engines, probe_capability  # noqa: E402
from core.pierce import Piercer, StopReason  # noqa: E402
from core.vault import PasswordVault, Problem, unlock  # noqa: E402

FIX = os.path.join(ROOT, "tests", "fixtures")
WORK = os.path.join(ROOT, "tests", "work")
ENGINES = find_engines()
SEVENZIP = ENGINES.seven_zip
WINRAR = ENGINES.winrar

PASS = "abc123"
# 「垫片伪装」夹具前面的假视频长度。
# 为什么必须是 16MB 而不是 1MB：**7-Zip 自己能在一定范围内扫到内嵌的 ZIP**
# （实测：未知后缀 .mp4 + 前缀 ≤8MB → 认；≥16MB → 不认；Rar.exe 更严，≥4MB 就不认）。
# 用 1MB 做夹具的话，"不切包也能解"会让这个测试失去意义。
SHIM = 16 << 20

# 中文密码用 .7z 和 .rar 承载：7-Zip 的 zip 处理器**建包**时无法接收非 ASCII 密码
# （`7z a -tzip -p中文` 报 "System ERROR: 参数错误"）。
# 注意这是 zip 建包的限制，**解压/验证（t/x）能正常接收中文密码**；RAR 建包则完全支持。
PASS_CJK = "密码A123"
results: list[tuple[bool, str, str]] = []


def check(ok: bool, name: str, detail: str = "") -> None:
    results.append((ok, name, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  — {detail}" if detail else ""))


def fx(name: str) -> str:
    """夹具文件的完整路径。"""
    return os.path.join(FIX, name)


def ensure_book(dst: str, *, extra: tuple[str, ...] = (PASS,)) -> str:
    """按用户的真实密码本铺一份测试副本，并保证 extra 里的密码一定在里面。

    界面/截图测试用的是**真 Workbench**，密码本必须是"有那把密码"的状态：
    以前只在文件不存在时才拷，被别的用例写坏之后就一直是坏的，
    表现出来是"3 个任务全部成功"莫名变成 2 个成功 1 个跳过。
    """
    src = os.path.join(ROOT, "密码本.txt")
    text = ""
    if os.path.isfile(src):
        with open(src, "r", encoding="utf-8", errors="replace") as f:
            text = f.read()
    lines = [ln for ln in text.splitlines() if ln.strip()]
    have = {ln.split("\t")[0].strip() for ln in lines}
    for pw in extra:
        if pw not in have:
            lines.append(pw)
    os.makedirs(os.path.dirname(dst) or ".", exist_ok=True)
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    return dst


def run_7z(args: list[str]) -> int:
    proc = subprocess.run(
        [SEVENZIP, *args], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        creationflags=0x08000000,
    )
    return proc.returncode


def run_rar(args: list[str]) -> int:
    proc = subprocess.run(
        [WINRAR, *args], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        creationflags=0x08000000,
    )
    return proc.returncode


def _fake_mp4_head() -> bytes:
    """一小段**真实合法**的 mp4 头（ftyp + free + mdat 三个 box）。

    这样夹具的头部探测结果就是"视频"，跟真实的 1067.mp4 一模一样——
    伪装的重点从来不是"文件名骗人"，而是"文件头真的像视频"。
    """
    box = b"ftyp" + b"isom" + struct.pack(">I", 0x200) + b"isomiso2avc1mp41"
    ftyp = struct.pack(">I", len(box) + 4) + box
    free = struct.pack(">I", 8) + b"free"
    return ftyp + free + struct.pack(">I", 8) + b"mdat"


def build_fixtures() -> None:
    """造夹具：明文/加密 zip、伪装 mp4、含"删"字名、嵌套、数字分卷。"""
    if os.path.isdir(FIX):
        shutil.rmtree(FIX)
    os.makedirs(FIX)

    payload = os.path.join(FIX, "payload")
    os.makedirs(payload)
    with open(os.path.join(payload, "第一层内容.txt"), "w", encoding="utf-8") as f:
        f.write("hello 中文内容\n")

    inner = os.path.join(FIX, "inner.zip")
    run_7z(["a", "-tzip", "-bso0", "-bsp0", inner, os.path.join(payload, "*")])

    # 1) 加密 zip（名字里带密码，密码是真的）
    run_7z([
        "a", "-tzip", "-bso0", "-bsp0", f"-p{PASS}",
        os.path.join(FIX, "示例包.zip"), os.path.join(payload, "*"),
    ])

    # 1b) 中文密码：改用 .7z（见文件头的说明）
    run_7z([
        "a", "-t7z", "-bso0", "-bsp0", f"-p{PASS_CJK}",
        os.path.join(FIX, "中文密码.7z"), os.path.join(payload, "*"),
    ])

    # 2) 伪装成视频：把明文 zip 复制成 .mp4
    shutil.copyfile(inner, os.path.join(FIX, "教程视频.mp4"))

    # 3) 文件名含"删"字（规避检测）
    shutil.copyfile(inner, os.path.join(FIX, "学习资料.z删i删p删"))

    # 4) 嵌套：外层 zip 里装 inner.zip
    nest_src = os.path.join(FIX, "nest_src")
    os.makedirs(nest_src)
    shutil.copyfile(inner, os.path.join(nest_src, "内层.zip"))
    run_7z(["a", "-tzip", "-bso0", "-bsp0", os.path.join(FIX, "嵌套.zip"), os.path.join(nest_src, "*")])

    # 5) 数字分卷（7z 会把 data.001 切成 data.001 / data.002 …）
    big = os.path.join(FIX, "big.bin")
    with open(big, "wb") as f:
        f.write(os.urandom(6000))
    run_7z(["a", "-tzip", "-v2k", "-bso0", "-bsp0", os.path.join(FIX, "data.001"), big])

    # 5b) 加密嵌套：外层和内层同一个密码 → 验证「沿用外层密码」
    enc_inner = os.path.join(FIX, "内层加密.zip")
    run_7z(["a", "-tzip", "-bso0", "-bsp0", f"-p{PASS}", enc_inner, os.path.join(payload, "*")])
    enc_nest = os.path.join(FIX, "enc_nest_src")
    os.makedirs(enc_nest)
    shutil.copyfile(enc_inner, os.path.join(enc_nest, "内层加密.zip"))
    run_7z(["a", "-tzip", "-bso0", "-bsp0", f"-p{PASS}",
            os.path.join(FIX, "加密嵌套.zip"), os.path.join(enc_nest, "*")])
    shutil.rmtree(enc_nest, ignore_errors=True)

    # 5c) 内容只有一个文件夹的包 → 验证「内容上提」
    flat_inner = os.path.join(FIX, "flat_src", "资料")
    os.makedirs(flat_inner)
    with open(os.path.join(flat_inner, "说明.txt"), "w", encoding="utf-8") as f:
        f.write("flatten me\n")
    run_7z(["a", "-tzip", "-bso0", "-bsp0",
            os.path.join(FIX, "单文件夹.zip"), os.path.join(FIX, "flat_src", "*")])
    shutil.rmtree(os.path.join(FIX, "flat_src"), ignore_errors=True)

    # ---- 以下为 rar 夹具（需要 WinRAR）----
    if WINRAR:
        # 注意 -ep1：RAR 默认会把完整路径（含 <工作区>\...）一起存进包里，
        # 7-Zip 不会。不加这个开关，解压出来会多出一串没人要的目录层级。
        # 6) 加密 rar（ASCII 密码）
        run_rar(["a", "-idq", "-y", "-ep1", f"-p{PASS}",
                 os.path.join(FIX, "示例包.rar"), os.path.join(payload, "*")])

        # 7) 中文密码 rar —— RAR 建包支持非 ASCII 密码（7z 的 zip 不行）
        run_rar(["a", "-idq", "-y", "-ep1", f"-p{PASS_CJK}",
                 os.path.join(FIX, "中文密码.rar"), os.path.join(payload, "*")])

        # 8) 伪装成视频的 rar
        src_rar = os.path.join(FIX, "示例包.rar")
        if os.path.isfile(src_rar):
            shutil.copyfile(src_rar, os.path.join(FIX, "伪装视频.mp4"))

        # 9) 新版分卷 rar：资源分卷.part1.rar / part2…
        run_rar(["a", "-idq", "-y", "-ep1", "-v2k", f"-p{PASS}",
                 os.path.join(FIX, "资源分卷.rar"), big])

        # 10) 外层 zip 里套一个 rar（给递归穿透用）
        rar_nest = os.path.join(FIX, "rar_nest_src")
        os.makedirs(rar_nest)
        shutil.copyfile(src_rar, os.path.join(rar_nest, "内层.rar"))
        run_7z(["a", "-tzip", "-bso0", "-bsp0",
                os.path.join(FIX, "外套zip.zip"), os.path.join(rar_nest, "*")])
        shutil.rmtree(rar_nest, ignore_errors=True)

    os.remove(big)
    shutil.rmtree(payload, ignore_errors=True)
    shutil.rmtree(nest_src, ignore_errors=True)

    # 11) ★「垫片伪装」：前面垫一段真视频数据，后面接一个完整压缩包。
    #     这种文件能正常播放，但靠头 512 字节永远认不出来（1067.mp4 就是这类）。
    shim_src = os.path.join(FIX, "shim_src")
    os.makedirs(shim_src)
    with open(os.path.join(shim_src, "垫片内容.txt"), "w", encoding="utf-8") as f:
        f.write("shim payload 垫片里的内容\n")
    run_7z(["a", "-tzip", "-bso0", "-bsp0",
            os.path.join(FIX, "垫片内层.zip"), os.path.join(shim_src, "*")])
    shutil.rmtree(shim_src, ignore_errors=True)

    # 高熵但**可复现**的填充：随机内容才像真视频；固定种子才能让测试稳定
    rnd = random.Random(20260905)
    head = _fake_mp4_head()
    shim_prefix = head + rnd.randbytes(SHIM - len(head))

    with open(fx("垫片伪装.mp4"), "wb") as fo:
        fo.write(shim_prefix)
        with open(fx("垫片内层.zip"), "rb") as fi:
            shutil.copyfileobj(fi, fo)

    # rar 版：ZIP 能靠尾部目录反推起点，RAR 只能全盘扫描 + 头部 CRC 校验
    if WINRAR and os.path.isfile(fx("示例包.rar")):
        with open(fx("垫片伪装rar.mp4"), "wb") as fo:
            fo.write(shim_prefix)
            with open(fx("示例包.rar"), "rb") as fi:
                shutil.copyfileobj(fi, fo)

    print("fixtures:", sorted(os.listdir(FIX)))


def test_probe() -> None:
    print("\n== probe：格式探测 ==")

    check(probe.detect_format(fx("示例包.zip")) is probe.Fmt.ZIP, "普通 zip 识别为 ZIP")
    check(probe.detect_format(fx("教程视频.mp4")) is probe.Fmt.ZIP,
          "伪装 mp4 识别为 ZIP（真实头 50 4B 03 04）", probe.detect_format(fx("教程视频.mp4")).value)
    check(probe.is_disguised(fx("教程视频.mp4")), "伪装文件被标记为 disguised")
    check(not probe.is_disguised(fx("示例包.zip")), "普通 zip 不算伪装")

    cleaned = probe.clean_delete_chars("学习资料.z删i删p删")
    check("删" not in cleaned and cleaned == "学习资料.zip", '清理"删"字', cleaned)

    vols = sorted(p for p in os.listdir(FIX) if p.startswith("data."))
    infos = [probe.classify_volume(fx(n)) for n in vols]
    mains = [i for i in infos if i.is_first]
    print(f"     分卷文件 {vols}；主卷 {[os.path.basename(m.base) for m in mains]}")
    check(any(i.is_split for i in infos), "数字分卷被识别为 split")
    check(len(mains) == 1, "分卷组只挑出一个主卷（原版「重复解压」的病灶）")

    # 单个 .rar/.zip 的 kind 看着像分卷（为了配对 .r00/.z01），但只有一卷就不算分卷
    if os.path.isfile(fx("示例包.rar")):
        lone = probe.group_volumes([fx("示例包.rar")])
        check(bool(lone) and not lone[0].is_split,
              "单个 .rar 不算分卷（kind 像 OLD_RAR，但只有一卷）",
              f"count={lone[0].count if lone else '?'} is_split={lone[0].is_split if lone else '?'}")
        parts = [fx(n) for n in os.listdir(FIX) if n.startswith("资源分卷.part")]
        gs = probe.group_volumes(parts)
        check(bool(gs) and gs[0].is_split and gs[0].count == len(parts),
              f"多卷 rar 判为分卷且卷数正确（{len(parts)} 卷）",
              f"count={gs[0].count if gs else '?'}")


def test_naming() -> None:
    print("\n== naming：文件名密码提取（说明里列的 9 种写法）==")
    cases = {
        "示例包.zip密码123": "123",
        "示例包.zip密码:123": "123",
        "示例包.zip123": "123",
        "示例包.zip解压密码123": "123",
        "示例包.zip解压密码:123": "123",
        "示例包.zippw123": "123",
        "示例包.zippw:123": "123",
        "示例包.zip解压码123": "123",
        "示例包.zip解压码:123": "123",
    }
    for name, want in cases.items():
        got = naming.extract_from_name(name)
        value = got.value if got else None
        check(value == want, f"{name}", f"-> {value!r}（期望 {want!r}）")

    # 贪婪匹配陷阱：不能把扩展名后面的 123.rar 吃进去
    got = naming.extract_from_name("示例包.zip密码123.rar")
    check(got is not None and got.value == "123", "不贪婪：'示例包.zip密码123.rar'", repr(got.value if got else None))

    # 无密码的文件名不该瞎猜
    none_case = naming.extract_from_name("教程视频.mp4")
    check(none_case is None, "无密码文件名不误报", repr(none_case))

    # 「扩展名后直接贴密码」只对文件生效，文件夹不认（规格明确要求）
    folder = os.path.join("D:\\下载", "示例包.zip123", "示例包.zip")
    check(naming.extract_from_path(folder) is None, "文件夹名不认「示例包.zip123」写法",
          repr(naming.extract_from_path(folder)))
    file_case = os.path.join("D:\\下载", "示例包合集解压密码:123", "示例包.zip")
    got_dir = naming.extract_from_path(file_case)
    check(got_dir is not None and got_dir.value == "123", "密码在文件夹名里也能找到",
          repr(got_dir.value if got_dir else None))

    # 全角冒号
    got = naming.extract_from_name("资源.zip解压密码：abc888")
    check(got is not None and got.value in {"abc888", "：abc888"}, "全角冒号", repr(got.value if got else None))


def test_engine() -> None:
    print("\n== engine：验证/解压分离 ==")
    # 自带的 7-Zip：用户机器上没装 7-Zip 也要能用（rar 是商业软件，不内置）
    import core.engine as engine_mod

    bundled = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                           "tools", "7z", "7z.exe")
    check(os.path.isfile(bundled), "★ 工程里自带 7-Zip（tools/7z/7z.exe）", bundled)
    orig_candidates = engine_mod._SEVENZIP_CANDIDATES
    try:
        engine_mod._SEVENZIP_CANDIDATES = (bundled,)      # 假装系统里没装
        picked = engine_mod.find_engines()
        check(picked.seven_zip == bundled,
              "★ 系统没装 7-Zip 时会用自带的那份", str(picked.seven_zip))
        if os.path.isfile(bundled):
            proc = subprocess.run([bundled], capture_output=True, creationflags=0x08000000)
            banner = (proc.stdout + proc.stderr).decode("utf-8", "replace")
            check("7-Zip" in banner, "自带的 7z.exe 真的能跑起来",
                  banner.strip().splitlines()[0] if banner.strip() else "")
    finally:
        engine_mod._SEVENZIP_CANDIDATES = orig_candidates
    check(engine_mod._SEVENZIP_CANDIDATES[0] == bundled,
          "★ 候选顺序：自带的排第一，系统装的排后面", str(engine_mod._SEVENZIP_CANDIDATES[:2]))

    if not SEVENZIP:
        check(False, "本机没有 7-Zip，无法测试 engine")
        return

    ex = Extractor()
    target = os.path.join(FIX, "示例包.zip")

    good = ex.test(target, PASS)
    check(good.ok, f"正确密码 {PASS!r} test 通过", good.brief())

    bad = ex.test(target, "wrong-password")
    check(not bad.ok and bad.wrong_password, "错误密码被判定为密码错误",
          f"code={bad.code} wrong_password={bad.wrong_password} tail={bad.tail.splitlines()[-1] if bad.tail else ''}")

    nopw = ex.test(target, None)
    check(not nopw.ok and not nopw.timed_out, "不传密码不会卡住（-p- 生效）", nopw.brief())

    # 中文密码全链路（这是中文站点的真实场景，必须单独验）
    cjk = os.path.join(FIX, "中文密码.7z")
    if os.path.isfile(cjk):
        c1 = ex.test(cjk, PASS_CJK)
        check(c1.ok, f"中文密码 {PASS_CJK!r} test 通过（命令行能传中文密码）", c1.brief())
        c2 = ex.test(cjk, "完全不对的密码")
        check(not c2.ok and c2.wrong_password, "中文错误密码被判为密码错误",
              f"code={c2.code} wrong_password={c2.wrong_password}")
        outdir5 = os.path.join(WORK, "out-cjk")
        shutil.rmtree(outdir5, ignore_errors=True)
        _, x5 = ex.test_then_extract(cjk, outdir5, PASS_CJK)
        listing5 = os.listdir(outdir5) if os.path.isdir(outdir5) else []
        check(bool(x5 and x5.ok) and "第一层内容.txt" in listing5,
              "中文密码解压成功且文件名不乱码", str(listing5))

    plain = ex.test(os.path.join(FIX, "教程视频.mp4"), None)
    check(plain.ok, "伪装 zip 无密码 test 通过（7z 靠文件头识别）", plain.brief())

    # 加密判定：不判的话，未加密的包会把第一个候选密码误报成"命中"
    check(ex.is_encrypted(fx("示例包.zip")) is True, "加密判定：加密 zip → True")
    check(ex.is_encrypted(fx("教程视频.mp4")) is False, "加密判定：未加密的伪装 zip → False")
    check(ex.is_encrypted(fx("中文密码.7z")) is True, "加密判定：加密 7z → True")
    if WINRAR:
        check(ex.is_encrypted(fx("示例包.rar")) is True, "加密判定：加密 rar（7z 也能读）→ True")

    # 验证通过后再解压：中文文件名必须完好
    outdir = os.path.join(WORK, "out-single")
    shutil.rmtree(outdir, ignore_errors=True)
    t, x = ex.test_then_extract(target, outdir, PASS)
    ok = bool(x and x.ok)
    listing = os.listdir(outdir) if os.path.isdir(outdir) else []
    check(ok, "test 通过后解压成功", x.brief() if x else "未执行解压")
    check("第一层内容.txt" in listing, "解压出的中文文件名没有乱码", str(listing))

    # 错误密码不该产生垃圾解压
    outdir2 = os.path.join(WORK, "out-bad")
    shutil.rmtree(outdir2, ignore_errors=True)
    t2, x2 = ex.test_then_extract(target, outdir2, "wrong-password")
    check(x2 is None, "密码错时直接短路，不做无用解压")

    # 嵌套穿透的原料：外层解开后应有内层.zip
    outdir3 = os.path.join(WORK, "out-nest")
    shutil.rmtree(outdir3, ignore_errors=True)
    t3, x3 = ex.test_then_extract(os.path.join(FIX, "嵌套.zip"), outdir3, None)
    found = []
    for base, _dirs, files in os.walk(outdir3):
        found.extend(files)
    check(any(f.endswith(".zip") for f in found), "外层解开后能发现内层压缩包（穿透前提）", str(found))

    # 分卷：只喂主卷
    vols = sorted(p for p in os.listdir(FIX) if p.startswith("data."))
    main = os.path.join(FIX, vols[0])
    outdir4 = os.path.join(WORK, "out-vol")
    shutil.rmtree(outdir4, ignore_errors=True)
    tv = ex.test(main, None)
    check(tv.ok, f"只喂主卷 {vols[0]} 即可 test（其余分卷自动带上）", tv.brief())


def test_rar() -> None:
    """rar 专项：格式探测、伪装、中文密码、专用退出码 11、分卷主卷、嵌套穿透。"""
    print("\n== rar 链路（WinRAR）==")
    if not WINRAR:
        check(False, "本机没找到 Rar.exe，rar 全链路无法验证")
        return

    ex = Extractor()

    # 格式探测：RAR 7 默认写 RAR5，magic 末位是 01
    fmt = probe.detect_format(fx("示例包.rar"))
    check(fmt in (probe.Fmt.RAR, probe.Fmt.RAR5), "rar 识别为 RAR/RAR5", fmt.value)
    check(fmt is probe.Fmt.RAR5, "RAR 7 建的是 RAR5（magic 末位 01）", fmt.value)

    fmt_d = probe.detect_format(fx("伪装视频.mp4"))
    check(fmt_d in (probe.Fmt.RAR, probe.Fmt.RAR5), "伪装成 mp4 的 rar 仍被识别为 RAR", fmt_d.value)
    check(probe.is_disguised(fx("伪装视频.mp4")), "伪装的 rar 被标记为 disguised")

    # 引擎选择：.rar 必须走 WinRAR，不能给 7z
    pick = ex.engine_for(fx("示例包.rar"))
    check(pick is EngineKind.WINRAR, "rar 自动选择 WinRAR 引擎", pick.value)
    pick_z = ex.engine_for(fx("示例包.zip"))
    check(pick_z is EngineKind.SEVENZIP, "zip 自动选择 7-Zip 引擎", pick_z.value)

    # 退出码 11 = 密码错误（这是 RAR 专用语义，不能靠"非零即失败"）
    good = ex.test(fx("示例包.rar"), PASS)
    check(good.ok, f"rar 正确密码 {PASS!r} test 通过", good.brief())
    bad = ex.test(fx("示例包.rar"), "wrong-password")
    check(not bad.ok and bad.code == 11 and bad.wrong_password,
          "rar 错误密码 → 退出码 11 且标记为密码错误",
          f"code={bad.code} wrong_password={bad.wrong_password}")
    nopw = ex.test(fx("示例包.rar"), None)
    check(not nopw.ok and not nopw.timed_out, "rar -p- 不传密码不会卡住", nopw.brief())

    # 中文密码 rar：建包 + 验证 + 解压 全链路
    cjk = fx("中文密码.rar")
    if os.path.isfile(cjk):
        c1 = ex.test(cjk, PASS_CJK)
        check(c1.ok, f"rar 中文密码 {PASS_CJK!r} test 通过", c1.brief())
        c2 = ex.test(cjk, "错误的密码")
        check(not c2.ok and c2.wrong_password, "rar 中文错误密码被判为密码错误",
              f"code={c2.code} wrong_password={c2.wrong_password}")
        out = os.path.join(WORK, "rar-out-cjk")
        shutil.rmtree(out, ignore_errors=True)
        _, x = ex.test_then_extract(cjk, out, PASS_CJK)
        listing = os.listdir(out) if os.path.isdir(out) else []
        check(bool(x and x.ok) and "第一层内容.txt" in listing,
              "rar 中文密码解压成功且文件名不乱码", str(listing))

    # 伪装 rar：直接把 .mp4 路径喂给引擎（RAR 靠内容识别）
    disguised = ex.test(fx("伪装视频.mp4"), PASS)
    check(disguised.ok, "伪装 rar 验证通过（无需先改名）", disguised.brief())

    # 分卷 rar：只喂 part1，其余分卷自动带上
    parts = sorted(p for p in os.listdir(FIX) if p.startswith("资源分卷.part"))
    if parts:
        info = probe.classify_volume(fx(parts[0]))
        check(info.kind is probe.VolKind.PART and info.is_first,
              f"分卷识别：{parts[0]} 是 PART 组的主卷", f"kind={info.kind.value} first={info.is_first}")
        check(len(parts) > 1 and not probe.classify_volume(fx(parts[1])).is_first,
              "同组其余分卷不被当作主卷", str(parts))
        tv = ex.test(fx(parts[0]), PASS)
        check(tv.ok, f"只喂 {parts[0]} 即可验证（分卷自动带上）", tv.brief())
        out = os.path.join(WORK, "rar-out-vol")
        shutil.rmtree(out, ignore_errors=True)
        _, xv = ex.test_then_extract(fx(parts[0]), out, PASS)
        flat = os.listdir(out) if os.path.isdir(out) else []
        check(bool(xv and xv.ok) and "big.bin" in flat,
              "分卷 rar 解压成功且目录结构是平的（没有多余层级）", str(flat))

    # 嵌套穿透原料：外套 zip → 内层 rar
    out = os.path.join(WORK, "rar-out-nest")
    shutil.rmtree(out, ignore_errors=True)
    _, xn = ex.test_then_extract(fx("外套zip.zip"), out, PASS)
    found = []
    for base, _d, files in os.walk(out):
        found.extend(files)
    check(bool(xn and xn.ok) and any(x.endswith(".rar") for x in found),
          "zip 里套 rar 能解出内层（穿透前提）", str(found))


def test_vault() -> None:
    """密码本：四级固定顺序、成功次数排序、老格式兼容、去重、变体展开、飞轮、真实 unlock。"""
    print("\n== vault：密码本（一本、按成功次数排序）==")

    # 1) 顺序：文件名 → 密码本（次数多的先） → 空密码
    v = PasswordVault()
    v.entries = []
    v.set_entries(["mine_pw"])
    v.remember("learned_pw")
    v.remember("learned_pw")            # 成功两次
    cands = v.candidates_for(os.path.join("D:\\下载", "示例包.zip密码123"))
    values = [c.value for c in cands]
    check(values[0] == "123", "① 文件名提取的密码排第一", str(values[:4]))
    check(
        values.index("learned_pw") < values.index("mine_pw"),
        "② 成功次数多的排在前面（learned_pw 成功 2 次 → 先试）",
        str(values),
    )
    check(values[-1] == "", "③ 空密码排最后", repr(values[-1]))
    check(len(values) == len(set(values)), "候选密码已去重", str(values))
    check(v.find("learned_pw").hits == 2, "成功次数累计正确", str(v.find("learned_pw")))

    # 2) 变体展开
    v3 = PasswordVault()
    v3.set_entries(["abc 123"])
    c3 = [c.value for c in v3.candidates_for("x.zip")]
    check("abc 123" in c3 and "abc123" in c3, "变体展开：带空格的密码额外生成去空格写法", str(c3))
    v4 = PasswordVault()
    v4.set_entries(["ａｂｃ１２３"])
    c4 = [c.value for c in v4.candidates_for("x.zip")]
    check("abc123" in c4, "变体展开：全角转半角", str(c4))

    # 3) 落盘：一个文件、一行一个密码、成功次数写在 TAB 后面
    book = os.path.join(WORK, "book-test", "密码本.txt")
    shutil.rmtree(os.path.dirname(book), ignore_errors=True)
    os.makedirs(os.path.dirname(book))
    with open(book, "w", encoding="utf-8") as f:
        f.write("# 老格式：一行一个密码，没有次数列\nold1\nold2\n")
    v5 = PasswordVault(book=book)
    v5.reload()
    check(v5.size() == {"total": 2}, "老格式密码本直接能用", str(v5.size()))
    check(v5.find("old1").hits == 0, "老条目次数按 0 起算")

    check(v5.add("new1"), "手动加一条并落盘")
    check(v5.remember("old2"), "记住一次成功并落盘")
    check(v5.remember("old2"), "再成功一次")
    v6 = PasswordVault(book=book)
    v6.reload()
    check(v6.size() == {"total": 3}, "重新读盘条目数正确", str(v6.size()))
    check(v6.find("old2").hits == 2, "次数落盘且能读回", str(v6.find("old2")))
    text = open(book, encoding="utf-8").read()
    check("old2\t2" in text, "次数以 TAB 写在密码后面", repr(text.strip().splitlines()[-3:]))
    lines = [ln for ln in text.splitlines() if ln and not ln.startswith("#")]
    check(lines[0].startswith("old2"), "文件里的第一行就是会最先试的那个（文件顺序 = 尝试顺序）",
          str(lines[:3]))

    # 4) 旧的两段式文件能被读成一本
    two = os.path.join(WORK, "book-two", "密码本.txt")
    shutil.rmtree(os.path.dirname(two), ignore_errors=True)
    os.makedirs(os.path.dirname(two))
    with open(two, "w", encoding="utf-8") as f:
        f.write("[记住的密码]\nrem1\n[我添加的密码]\nmine1\n")
    v7 = PasswordVault(book=two)
    v7.reload()
    check({e.password for e in v7.entries} == {"rem1", "mine1"},
          "旧的两段式文件读成一本（分段标记忽略）", str([e.password for e in v7.entries]))

    # 5) 旧「临时密码本.txt」自动并入并退役
    d = os.path.join(WORK, "book-migrate")
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    with open(os.path.join(d, "密码本.txt"), "w", encoding="utf-8") as f:
        f.write("mine1\n")
    with open(os.path.join(d, "临时密码本.txt"), "w", encoding="utf-8") as f:
        f.write("# 关键字<TAB>密码\n示例包\tlegacy_pw\n")
    v8 = PasswordVault.from_dir(d)
    check(v8.find("legacy_pw") is not None, "旧临时密码本里的密码被并入",
          str([e.password for e in v8.entries]))
    check(not os.path.isfile(os.path.join(d, "临时密码本.txt"))
          and os.path.isfile(os.path.join(d, "临时密码本.txt.bak")),
          "旧文件已退役成 .bak（不丢数据）")

    # 6) 真实 unlock（走引擎，验证与解压分离）
    ex = Extractor()
    v9 = PasswordVault()
    v9.set_entries(["nope1", PASS, "nope2"])
    un = unlock(v9, ex, fx("示例包.zip"))
    check(un.ok and un.password == PASS, "unlock 从密码本里试出正确密码", un.summary())

    v10 = PasswordVault()
    v10.set_entries(["nope1", "nope2"])
    un2 = unlock(v10, ex, fx("示例包.zip"))
    check(not un2.ok and un2.problem is Problem.EXHAUSTED,
          "全试完 → 判定为「密码问题」，值得弹窗问用户", un2.summary())
    check(un2.worth_asking_user, "worth_asking_user 为真")

    if WINRAR and os.path.isfile(fx("中文密码.rar")):
        v11 = PasswordVault()
        v11.set_entries([PASS_CJK])
        un3 = unlock(v11, ex, fx("中文密码.rar"))
        check(un3.ok and un3.password == PASS_CJK, "unlock 在 rar 上试出中文密码", un3.summary())

    # 7) 缺分卷（只拷 part1、不拷 part2）→ 判成"分卷不全"，不该问用户要密码；
    #    而"包完整、只是密码本没有"→ 值得弹窗问
    if os.path.isfile(fx("资源分卷.part1.rar")):
        lone = os.path.join(WORK, "unlock-missing-vol")
        shutil.rmtree(lone, ignore_errors=True)
        os.makedirs(lone)
        shutil.copyfile(fx("资源分卷.part1.rar"), os.path.join(lone, "资源分卷.part1.rar"))
        un4 = unlock(PasswordVault(), ex, os.path.join(lone, "资源分卷.part1.rar"))
        check(un4.problem is Problem.MISSING_VOLUME and not un4.worth_asking_user,
              "★ 缺分卷 → 判成「分卷不全」，不问用户要密码", f"{un4.problem} {un4.summary()}")
    un5 = unlock(PasswordVault(), ex, fx("示例包.zip"))
    check(un5.worth_asking_user, "密码本没有、包是完好的 → 值得弹窗问用户", un5.summary())


def test_embedded() -> None:
    """内嵌压缩包：垫了真视频的那种伪装（1067.mp4 属于这一类）。"""
    print("\n== probe：内嵌压缩包（头不是包、身体里是包）==")
    ex = Extractor()
    p = fx("垫片伪装.mp4")

    check(probe.detect_format(p) is probe.Fmt.MP4, "头 512 字节看过去就是 mp4（认不出压缩包）")
    check(not probe.detect_format(p).is_archive, "只认 magic 的话，它会被当成「不是压缩包」")
    # 这一条是整个功能的"为什么"：引擎自己吃原文件是失败的。
    # （顺带钉住一个实测事实：7-Zip 能打开**小前缀**+ZIP，前缀一大就不行了，
    #   所以夹具特意垫了 16MB；RAR 反而自己会扫内嵌的 RAR。）
    raw = ex.extract(p, os.path.join(WORK, "carve", "raw-out"))
    check(not raw.ok, "★ 直接把原文件喂给引擎：打不开（必须靠切包）", raw.brief())
    check(probe.looks_like_carrier(p), "mp4 被列为「值得做内嵌探测」的候选")
    check(not probe.looks_like_carrier(fx("示例包.zip")), "本来就是压缩包的，不做内嵌探测")

    emb = probe.find_embedded(p)
    check(emb is not None and emb.fmt is probe.Fmt.ZIP, "尾部目录定位：发现内嵌 ZIP",
          emb.label if emb else "没找到")
    check(bool(emb) and emb.offset == SHIM, f"起始偏移精确到 {SHIM}（垫片长度）",
          f"offset={emb.offset if emb else '?'}")
    check(bool(emb) and emb.how == "尾部目录定位", "便宜的路径就够了（不用全盘扫描）",
          emb.how if emb else "")

    # 切出来必须是个能用的压缩包
    carved = os.path.join(WORK, "carve", "cut.zip")
    os.makedirs(os.path.dirname(carved), exist_ok=True)
    if os.path.exists(carved):
        os.remove(carved)
    n = probe.carve(p, emb.offset, carved)
    check(n > 0 and probe.detect_format(carved) is probe.Fmt.ZIP, "切出来的就是一个合法 ZIP",
          f"{n} 字节 → {probe.detect_format(carved).value}")
    check(ex.test(carved, None).ok, "切出来的包引擎能正常打开")
    check(os.path.getsize(carved) != os.path.getsize(p), "切片比原文件小（只切了后半段）")

    # 纯随机大文件不许误报（实测 1.79GB 视频数据里 `PK\x03\x04` 会自然出现）
    noise = os.path.join(WORK, "carve", "noise.mp4")
    with open(noise, "wb") as f:
        f.write(random.Random(7).randbytes(3 << 20))
    check(probe.find_embedded(noise) is None, "3MB 随机数据：不误报内嵌包")

    # RAR 没有尾部目录，只能全盘扫描 → 但头部 CRC32 校验必须过得去
    if os.path.isfile(fx("垫片伪装rar.mp4")):
        p2 = fx("垫片伪装rar.mp4")
        e2 = probe.find_embedded(p2)
        check(e2 is not None and e2.fmt is probe.Fmt.RAR5, "全盘扫描：发现内嵌 RAR5",
              e2.label if e2 else "没找到")
        check(bool(e2) and e2.offset == SHIM and e2.how == "全盘扫描",
              "RAR 只能靠全盘扫描定位（ZIP 那种尾部目录它没有）",
              f"offset={e2.offset if e2 else '?'} how={e2.how if e2 else ''}")
        check(probe.find_embedded(p2, deep=False) is None,
              "关掉全盘扫描后，RAR 这种就没有便宜办法可用了")

        # 负样本：只把头部 CRC 破坏掉，签名还在 —— 校验器必须挡住它，
        # 否则高熵视频数据里随便撞上一个 "Rar!" 就会被当成压缩包
        broken = os.path.join(WORK, "carve", "broken.mp4")
        with open(p2, "rb") as fi, open(broken, "wb") as fo:
            data = bytearray(fi.read())
        data[SHIM + 9] ^= 0xFF
        with open(broken, "wb") as fo:
            fo.write(bytes(data))
        check(probe.find_embedded(broken) is None,
              "★ 签名在、CRC 不对 → 判为误报，不当成内嵌包")


def test_pierce() -> None:
    """递归穿透：多层级联、密码沿用、内容上提、三个保险丝、停止原因。"""
    print("\n== pierce：递归穿透 ==")
    ex = Extractor()

    def fresh_vault() -> PasswordVault:
        v = PasswordVault()
        v.set_entries([PASS, PASS_CJK])
        return v

    def stage(name: str, *sources: str) -> str:
        d = os.path.join(WORK, name)
        shutil.rmtree(d, ignore_errors=True)
        os.makedirs(d)
        for s in sources:
            shutil.copyfile(os.path.join(FIX, s), os.path.join(d, s))
        return d

    # 1) 两层嵌套自动穿透
    d1 = stage("pierce-nest", "嵌套.zip")
    logs: list[str] = []
    res = Piercer(ex, fresh_vault(), min_free_gb=0.001, logger=logs.append).run(
        os.path.join(d1, "嵌套.zip")
    )
    check(res.ok and len(res.layers) == 2, "两层嵌套自动穿透",
          f"{res.summary()}｜层级={[(l.depth, os.path.basename(l.target)) for l in res.layers]}")
    check(res.stop_reason is StopReason.NO_ARCHIVE, "停止原因：解到底了（目录里没有压缩包）",
          res.stop_reason.label)
    check(any("第2层" in m for m in logs), "日志里有「第N层」层级信息")

    # 2) 加密嵌套：第二层沿用外层密码
    d2 = stage("pierce-enc", "加密嵌套.zip")
    res2 = Piercer(ex, fresh_vault(), min_free_gb=0.001).run(os.path.join(d2, "加密嵌套.zip"))
    check(res2.ok and len(res2.layers) == 2, "加密嵌套两层穿透", res2.summary())
    check(any(l.password_origin == "沿用外层密码" for l in res2.layers[1:]),
          "第二层优先沿用外层成功的密码",
          str([(l.depth, l.password_origin) for l in res2.layers]))

    # 3) 内容上提：包内只有一个文件夹时不该出现 A/A 套娃
    d3 = stage("pierce-flat", "单文件夹.zip")
    res3 = Piercer(ex, fresh_vault(), min_free_gb=0.001).run(os.path.join(d3, "单文件夹.zip"))
    out3 = res3.output_dir
    listing3 = os.listdir(out3) if os.path.isdir(out3) else []
    check("说明.txt" in listing3 and "资料" not in listing3,
          "内容上提：单文件夹被拆掉，文件直接落在输出目录", str(listing3))

    # 3b) 未加密的包不该报"命中密码"（否则详情页的来源列是假的）
    unenc = Piercer(ex, fresh_vault(), min_free_gb=0.001).run(
        os.path.join(stage("pierce-unenc", "教程视频.mp4"), "教程视频.mp4")
    )
    check(unenc.ok and unenc.layers and unenc.layers[0].password_origin == "无密码",
          "未加密的包标记为「无密码」，不误报密码来源",
          str([(l.depth, l.password_origin) for l in unenc.layers]))

    # 4) 保险丝一：最大层数
    d4 = stage("pierce-depth", "嵌套.zip")
    res4 = Piercer(ex, fresh_vault(), max_depth=1, min_free_gb=0.001).run(os.path.join(d4, "嵌套.zip"))
    check(res4.stop_reason is StopReason.MAX_DEPTH and len(res4.layers) == 1,
          "保险丝：最大层数 1 层时主动停在 MAX_DEPTH", f"{res4.summary()} 层数={len(res4.layers)}")

    # 5) 保险丝二：已访问指纹（同一个包不重复解，防死循环）
    d5 = stage("pierce-visited", "嵌套.zip")
    p5 = Piercer(ex, fresh_vault(), min_free_gb=0.001)
    p5.run(os.path.join(d5, "嵌套.zip"))
    res5 = p5.run(os.path.join(d5, "嵌套.zip"))
    check(res5.stop_reason is StopReason.ALREADY_VISITED,
          "保险丝：已解过的包第二次遇到就停（防死循环）", res5.stop_reason.label)

    # 6) 保险丝三：磁盘剩余空间
    d6 = stage("pierce-space", "嵌套.zip")
    res6 = Piercer(ex, fresh_vault(), min_free_gb=10_000_000.0).run(os.path.join(d6, "嵌套.zip"))
    check(res6.stop_reason is StopReason.NO_SPACE, "保险丝：剩余空间不足时主动中止",
          res6.stop_reason.label)

    # 7) 多候选目录 → **全都解**（不再是"无法确定主包"就收工）
    #    实测 shell2.zip 里躺着 1.tar/1111.7z/2222.zip/3333.zip/4444.rar 五个互不相关的包，
    #    旧行为解到这儿就停了，用户看到的是"套娃没解完"。
    d7 = stage("pierce-multi", "嵌套.zip", "示例包.zip")
    res7 = Piercer(ex, fresh_vault(), min_free_gb=0.001).run(d7)
    check(res7.stop_reason is not StopReason.AMBIGUOUS,
          "★ 多个互不相关的包不再判 AMBIGUOUS 收工", f"{res7.summary()}")
    outs7 = sorted(
        e.name for e in os.scandir(d7) if e.is_dir()
    )
    check(len([n for n in outs7 if n not in ("嵌套", "示例包")]) == 0 and len(res7.layers) >= 2,
          "★ 两个包都解了（各自一个产物目录）", f"{outs7} layers={len(res7.layers)}")

    # 8) 空目录 / 没有压缩包
    d8 = stage("pierce-empty")
    res8 = Piercer(ex, fresh_vault(), min_free_gb=0.001).run(d8)
    check(res8.stop_reason is StopReason.NO_ARCHIVE, "空目录：NO_ARCHIVE", res8.stop_reason.label)

    # 9) 清理「删」字文件名
    d9 = stage("pierce-delete", "学习资料.z删i删p删")
    p9 = Piercer(ex, fresh_vault(), min_free_gb=0.001)
    renamed = p9.clean_delete_names(d9)
    listing9 = os.listdir(d9)
    check(bool(renamed) and "学习资料.zip" in listing9,
          "穿透前自动清理「删」字文件名", f"重命名={renamed} 目录={listing9}")

    # 10) 从「文件夹」直接开始穿透（拖文件夹的场景）
    d10 = stage("pierce-from-dir", "嵌套.zip")
    res10 = Piercer(ex, fresh_vault(), min_free_gb=0.001).run(d10)
    check(res10.ok and len(res10.layers) == 2, "入口是文件夹时也能跑通", res10.summary())

    # 11) ★ 删除中间嵌套包：只删解压过程中冒出来的，不动用户原文件
    d11 = stage("pierce-clean", "嵌套.zip")
    res11 = Piercer(ex, fresh_vault(), min_free_gb=0.001,
                    remove_intermediate=True).run(os.path.join(d11, "嵌套.zip"))
    outer = os.path.join(d11, "嵌套.zip")
    inner = os.path.join(d11, "嵌套", "内层.zip")
    check(res11.ok and len(res11.layers) == 2, "带删除中间包也能跑通", res11.summary())
    check(not os.path.exists(inner), "★ 嵌套在里面的压缩包被删掉了",
          f"内层.zip 还在={os.path.exists(inner)}")
    check(os.path.exists(outer), "★ 用户自己拖进来的原压缩包不动",
          f"嵌套.zip 还在={os.path.exists(outer)}")
    check(os.path.exists(os.path.join(d11, "嵌套", "第一层内容.txt")),
          "解出来的内容还在（中间包删掉后，空壳目录被收掉，内容上提一层）",
          str(os.listdir(os.path.join(d11, "嵌套"))))
    check(not os.path.exists(os.path.join(d11, "嵌套", "内层")),
          "★ 不留 嵌套/内层/ 这种空壳（以前会留一串同名空目录）",
          str(os.listdir(os.path.join(d11, "嵌套"))))

    # 12) 关掉删除中间包 → 内层包应该留着
    d12 = stage("pierce-keep", "嵌套.zip")
    Piercer(ex, fresh_vault(), min_free_gb=0.001, remove_intermediate=False).run(
        os.path.join(d12, "嵌套.zip")
    )
    inner12 = os.path.join(d12, "嵌套", "内层.zip")
    check(os.path.exists(inner12), "关掉选项后，嵌套包保留",
          f"内层.zip 还在={os.path.exists(inner12)}")

    # 13) ★ 指定输出目录：产物统一落到那里
    d13 = stage("pierce-outroot", "示例包.zip")
    out_root = os.path.join(WORK, "out-root")
    shutil.rmtree(out_root, ignore_errors=True)
    res13 = Piercer(ex, fresh_vault(), min_free_gb=0.001, output_root=out_root).run(
        os.path.join(d13, "示例包.zip")
    )
    check(res13.ok and os.path.normcase(res13.output_dir).startswith(os.path.normcase(out_root)),
          "★ 指定输出目录后产物落在指定位置", res13.output_dir)
    check(os.path.isfile(os.path.join(out_root, "示例包", "第一层内容.txt")),
          "指定目录里确实有解出来的文件")

    # 14) 冲突处理 = 跳过
    d14 = stage("pierce-skip", "示例包.zip")
    os.makedirs(os.path.join(d14, "示例包"), exist_ok=True)
    res14 = Piercer(ex, fresh_vault(), min_free_gb=0.001, conflict="skip").run(
        os.path.join(d14, "示例包.zip")
    )
    check(res14.stop_reason is StopReason.OUTPUT_EXISTS and not res14.layers,
          "★ 冲突处理=跳过时，输出目录已存在就直接跳过",
          f"{res14.stop_reason.label} 层数={len(res14.layers)}")

    # 15) 冲突处理 = 自动加后缀（默认）：另建目录，不覆盖已有内容
    d15 = stage("pierce-rename", "示例包.zip")
    os.makedirs(os.path.join(d15, "示例包"), exist_ok=True)
    res15 = Piercer(ex, fresh_vault(), min_free_gb=0.001, conflict="rename").run(
        os.path.join(d15, "示例包.zip")
    )
    check(res15.ok and os.path.basename(res15.output_dir).startswith("示例包 ("),
          "★ 冲突处理=加后缀时另建目录，不覆盖已有内容",
          os.path.basename(res15.output_dir))

    # 15b) ★ 无扩展名的包：输出目录名 == 源文件自己的路径，三种冲突处理都不许出问题
    #      实测过的两个坑：skip 被当成"输出目录已存在"直接跳过（用户报的），
    #      overwrite 往一个文件上 makedirs → FileExistsError 崩掉。
    d15b = stage("pierce-noext", "示例包.zip")
    noext = os.path.join(d15b, "无扩展名包")          # 不带后缀
    shutil.copyfile(os.path.join(d15b, "示例包.zip"), noext)
    for conflict, want_ok in (("skip", True), ("overwrite", True), ("rename", True)):
        logs_b: list[str] = []
        res_b = Piercer(ex, fresh_vault(), min_free_gb=0.001, conflict=conflict,
                        logger=logs_b.append).run(noext)
        made = [n for n in os.listdir(d15b) if os.path.isdir(os.path.join(d15b, n))]
        check(res_b.ok and made and any("第一层内容" in str(os.listdir(os.path.join(d15b, n)))
                                        for n in made),
              f"★ 无扩展名的包 conflict={conflict} 也能正常解出来（不被当成「已存在」跳过）",
              f"ok={res_b.ok} 目录={made} {res_b.summary()}")
        check(any("同名的是个文件" in m for m in logs_b),
              f"conflict={conflict} 时日志说清了为什么换了目录名", "\n".join(logs_b[-3:]))
        check(os.path.isfile(noext), "源文件一个字节都没动（名字没被目录顶掉）")
        for n in made:
            shutil.rmtree(os.path.join(d15b, n), ignore_errors=True)

    # 15c) ★ 覆盖 + 目录里已有旧文件时，**内容照样要上提**（不许套娃）
    #      用户报的：重名=覆盖 没真正生效，需要上提的包（1067.mp4 那种）会解成
    #      `1067/内层/内容.txt`。根因是上提要求"目录里只有一个子目录、且没有别的文件"，
    #      而覆盖模式下目录里本来就有上一次留下的东西 → 永远判不出来。
    #
    #      夹具必须是**真·套娃形状**：包里自带一层同名文件夹（国产重打包的常见样子）。
    #      （第一版我拿的是平铺的包，压根不需要上提，测试等于没测。）
    d15c = stage("pierce-cover-hoist")
    payload_c = os.path.join(d15c, "pay", "外壳", "里层")
    os.makedirs(payload_c, exist_ok=True)
    with open(os.path.join(payload_c, "内容.txt"), "w", encoding="utf-8") as fh:
        fh.write("deep\n")
    archive_c = os.path.join(d15c, "外壳.zip")
    # 从 pay/ 里打包，包里就是 `外壳/里层/内容.txt`
    run_7z(["a", "-tzip", "-bso0", "-bsp0", archive_c, os.path.join(d15c, "pay", "外壳")])
    out_c = os.path.join(d15c, "外壳")
    os.makedirs(out_c, exist_ok=True)
    with open(os.path.join(out_c, "上次留下的.txt"), "w", encoding="utf-8") as fh:
        fh.write("old\n")
    logs_c: list[str] = []
    res_c = Piercer(ex, fresh_vault(), min_free_gb=0.001, conflict="overwrite",
                    output_root=d15c, logger=logs_c.append).run(archive_c)
    got_c = sorted(os.listdir(out_c))
    check(res_c.ok and os.path.isfile(os.path.join(out_c, "里层", "内容.txt"))
          and "外壳" not in got_c,
          "★ 覆盖模式下内容照样上提（不是 外壳/外壳/里层/内容.txt）",
          f"ok={res_c.ok} 树={got_c}")
    check(os.path.isfile(os.path.join(out_c, "上次留下的.txt")),
          "★ 上提不会把原来就在目录里的文件弄丢", str(got_c))
    check(any("上提" in m or "收掉空壳" in m for m in logs_c),
          "日志里说了做过上提/收壳", "\n".join(logs_c[-4:]))

    # 16) ★ 垫片伪装：切出内嵌包解压，原文件一个字节都不动、临时切片不留
    d16 = stage("pierce-embed", "垫片伪装.mp4")
    src16 = os.path.join(d16, "垫片伪装.mp4")
    size_before = os.path.getsize(src16)
    logs16: list[str] = []
    res16 = Piercer(ex, fresh_vault(), min_free_gb=0.001, logger=logs16.append).run(src16)
    check(res16.ok and res16.layers and res16.layers[0].ok,
          "★ 垫片伪装（头是 mp4）能一路解出来", res16.summary())
    check(os.path.isfile(os.path.join(d16, "垫片伪装", "垫片内容.txt")),
          "解出来的内容正确", str(os.listdir(os.path.join(d16, "垫片伪装"))
                                  if os.path.isdir(os.path.join(d16, "垫片伪装")) else []))
    check(os.path.getsize(src16) == size_before, "★ 原视频一个字节都没动",
          f"{size_before} → {os.path.getsize(src16)}")
    leftovers = [n for n in os.listdir(d16) if ".carve." in n]
    check(not leftovers, "★ 临时切片文件用完就清掉了", str(leftovers or "无"))
    check(any("切出" in m for m in logs16), "日志里说清楚了「切出内嵌包」这件事",
          next((m for m in logs16 if "切出" in m), ""))

    # 17) 关掉这个开关 → 老实报错，而不是偷偷去解（自用工具里"我关掉的东西别自己动"）
    d17 = stage("pierce-embed-off", "垫片伪装.mp4")
    res17 = Piercer(ex, fresh_vault(), min_free_gb=0.001, scan_appended=False).run(
        os.path.join(d17, "垫片伪装.mp4")
    )
    check(not res17.ok, "关掉「识别内嵌包」后不再切包（引擎如实报打不开）",
          f"{res17.stop_reason.label}")

    # 18) ★ 三层套娃 + 指定输出目录：不该产出 1067 / 1067 (1) / 1067 (2) 那种一地空壳，
    #     也不要 1067/1067/1067 一路套下去 —— 最终内容就落在 output_root/1067 一层里
    d18 = stage("pierce-embed-root", "垫片伪装.mp4")
    root18 = os.path.join(WORK, "embed-root")
    shutil.rmtree(root18, ignore_errors=True)
    res18 = Piercer(ex, fresh_vault(), min_free_gb=0.001, output_root=root18).run(
        os.path.join(d18, "垫片伪装.mp4")
    )
    top18 = os.listdir(root18) if os.path.isdir(root18) else []
    check(res18.ok and top18 == ["垫片伪装"],
          "★ 指定输出目录时：只有一层有意义的目录，没有 (1)(2) 分身",
          str(top18))
    check(os.path.isfile(os.path.join(root18, "垫片伪装", "垫片内容.txt")),
          "★ 最终内容直接在 output_root/垫片伪装/ 下（中间空壳被收掉）",
          str(os.listdir(os.path.join(root18, "垫片伪装"))))


def main() -> int:
    print("== 环境 ==")
    cap = probe_capability()
    for k, v in cap.items():
        print(f"  {k}: {v}")
    if not SEVENZIP:
        print("!! 没找到 7z.exe，夹具造不出来")
        return 1

    print("\n== 造夹具 ==")
    build_fixtures()

    test_probe()
    test_embedded()
    test_naming()
    test_engine()
    test_rar()
    test_vault()
    test_pierce()

    passed = sum(1 for ok, _, _ in results if ok)
    total = len(results)
    print(f"\n===== {passed}/{total} 通过 =====")
    if passed != total:
        print("失败项：")
        for ok, name, detail in results:
            if not ok:
                print(f"  - {name}  {detail}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
