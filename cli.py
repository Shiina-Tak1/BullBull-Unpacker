"""命令行入口：不启动界面也能真正干活（也方便做端到端验证）。

用法：

    .venv\\Scripts\\python.exe cli.py <压缩包或文件夹> [更多路径...] [选项]

常用选项：

    --probe            只看探测结果，不解压
    --depth N          最大嵌套层数（默认 5）
    --no-pierce        只解第一层，不往里穿透
    --no-flatten       不做「内容上提」
    --no-appended      不识别「垫了真视频、后面接压缩包」的伪装文件
    --delete-source    解压成功后删除原压缩包（分卷整组删）
    --min-free GB      最低剩余空间（默认 5，与设置页「最低剩余空间」同一项）
    --book PATH        指定密码本（默认用脚本同目录的「密码本.txt」）
    --quiet            不打日志，只打结论

例子：

    # 先看看它认不认得出来
    python cli.py "D:\\下载\\示例包.zip" --probe

    # 真解，最多穿透 3 层
    python cli.py "D:\\下载\\某合集" --depth 3
"""

from __future__ import annotations

import argparse
import os
import sys

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)

from core import probe  # noqa: E402
from core import paths as paths_mod  # noqa: E402
from core.config import Config  # noqa: E402
from core.engine import Extractor, find_engines, probe_capability  # noqa: E402
from core.pierce import Piercer, StopReason, describe_pierce_result  # noqa: E402
from core.vault import DEFAULT_BOOK, PasswordVault  # noqa: E402

ROOT_DIR = BASE


def out(text: str = "") -> None:
    r"""命令行输出**只走这一个口**（2026-09-24，`TASK-060`）。

    以前这个文件里有 29 处裸 `print`，散在每条分支里：想统一改一次输出
    （加时间戳、按 `--quiet` 收声、输出改成 JSON、顺手写进日志…）就得改 29 处，
    漏一处就是"半新半旧"的输出。收敛成一个函数之后，出口只有这一处。

    ⚠ **没有**顺手让 CLI 也往 `run.log` 里写：命令行是一次性、可重跑的，
    而 `run.log` 是"界面上这一趟干了什么"的流水（跟着数据目录走），
    把 CLI 输出灌进去会让用户看到的流水混进脚本调用 —— 这是**刻意不做**，不是漏了。
    """
    print(text)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="bbu",
        description="BullBull Unpacker · 命令行入口",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("paths", nargs="+", help="压缩包或文件夹（可多个）")
    p.add_argument("--probe", action="store_true", help="只看探测结果，不解压")
    p.add_argument("--depth", type=int, default=5, help="最大嵌套层数（默认 5）")
    p.add_argument("--no-pierce", action="store_true", help="只解第一层，不往里穿透")
    p.add_argument("--no-flatten", action="store_true", help="不做内容上提")
    p.add_argument("--no-appended", action="store_true",
                   help="不识别「垫了真视频、后面接压缩包」的伪装")
    p.add_argument("--delete-source", action="store_true", help="解压成功后删除原压缩包")
    p.add_argument("--min-free", type=float, default=5.0,
                   help="最低剩余空间 GB（默认 5，对应设置页「最低剩余空间」）")
    p.add_argument("--book", default=paths_mod.data_path(DEFAULT_BOOK), help="密码本文件")
    p.add_argument("--quiet", action="store_true", help="只打结论，不打过程日志")
    return p


def carrier_report(path: str) -> list[str]:
    r"""这个文件里是不是藏着一个压缩包？—— `--probe` 两条分支**共用这一份判据**。

    返回要打印的行（**不含缩进**；空列表 = 不是"马甲"文件、或身体里没有包）。

    为什么抽成函数（`B-2026-078`）：`--probe` 的两条分支（单文件 / 目录）以前各写各的，
    于是单文件分支练出来的本事（先问引擎、再自己扫）目录分支一直没跟上 —— 同一个
    `video.mp4` 单独 `--probe` 报「内嵌压缩包 引擎能直接读」，放进目录里却一个字都不说。
    "同一件事两条分支各写一份判据"在这个项目里已经漂移过三次（扫描侧 vs 穿透侧、
    单文件 vs 目录…），所以这里不是把那段代码抄一份，而是**只有一份**、两边都调它。

    `B-2026-097` 起"只有一份"做得更彻底：**判据本体搬进了唯一入口
    `probe.inspect_carrier()`**，本函数只剩"把结果翻译成人话" —— 五种形态
    （尾部目录定位 / 绝对偏移布局 / 引擎直读 / 尾部签名 / 全盘扫描）各怎么说都在这里，
    判据不再在五个调用点各拼一套组合。

    ⚠ 代价：目录分支现在会对目录里每个"马甲扩展名 + ≥1MB"的文件走完这条链（问引擎
    0.06~0.13s；引擎读不了就做尾部定位，再不行**尾部窗口扫**，最后才**全盘扫描**）——
    一个塞满真视频的目录会明显变慢。这是**有意**的：探测是用户主动调的辅助功能，
    "照实说"优先于"快"；别为了快在这里另写一份"目录专用"的弱判据，那正是漂移的起点。

    先问引擎的**位置**照旧（§14.14 的实测）：`示例视频.mp4` 是"36 字节假 MP4 头
    + 一整个 ZIP"，包内偏移是**绝对**的，7-Zip 报 `Embedded Stub Size = 36` 后能直接读，
    而 probe 的反推起点会算出 base=0、判非法 → 只报"扫过，没有"，把能解的说成不能解。
    顺序由 `inspect_carrier()` 统一保证（绝对偏移 / 引擎直读都排在"能推出切片起点"之后）。
    """
    if probe.detect_format(path).is_archive:
        return []                       # 它自己就是包 —— "内嵌"是另一条路径的事

    from core.pipeline import engine_can_read   # 延迟 import：别让 `--help` 也拖上 pipeline

    got = probe.inspect_carrier(path, engine_probe=engine_can_read, deep=True)
    if not got.carrier_like:
        return []                       # 不在马甲家族 / 体积不够：一句话都不说
    if got.direct:
        why = ("包内偏移是绝对的，切了会让包内偏移整体错位"
               if got.absolute_layout else "假头 + 一个完整的包")
        return [
            f"内嵌压缩包  引擎能直接读（{why}）",
            "            → 不改动原文件，直接交给 7-Zip",
        ]
    emb = got.embedded
    if emb is not None:
        tail = probe.human_size(os.path.getsize(path) - emb.offset)
        return [
            f"内嵌压缩包  {emb.label}（{emb.how}）",
            f"            → 解压时会先切出 {tail} 再解，不动原文件",
        ]
    return ["内嵌压缩包（引擎和扫描都读不出来）"]


def print_probe(path: str) -> None:
    """探测模式：把「它认出了什么」摊开给人看。"""
    name = os.path.basename(path)
    out(f"\n■ {name}")
    if os.path.isdir(path):
        out("  类型        文件夹")
        try:
            entries = [e.path for e in os.scandir(path) if e.is_file()]
        except OSError as e:
            out(f"  读取失败    {e}")
            return
        groups = probe.group_volumes(entries)
        if groups:
            out(f"  分卷组      {len(groups)} 组")
            for g in groups:
                out(f"    · {os.path.basename(g.main)}  共 {g.count} 卷（只处理主卷）")
        for p in entries:
            if probe.classify_volume(p).kind is not probe.VolKind.NONE:
                continue
            fmt = probe.detect_format(p)
            if fmt.is_archive:
                tag = "（伪装）" if probe.is_disguised(p) else ""
                out(f"    · {os.path.basename(p)}  →  {fmt.value}{tag}")
                continue
            # 不是包 ≠ 里面没有包（`B-2026-078`）：以前目录分支到这儿就什么都不说了 ——
            # 同一个 `video.mp4` 单独 `--probe` 报「内嵌压缩包 引擎能直接读」，
            # 放进目录里只列 `normal.zip`。判据与单文件分支**同一份**（`carrier_report`）。
            lines = carrier_report(p)
            if lines:
                out(f"    · {os.path.basename(p)}")
                for line in lines:
                    out("      " + line)
        return

    fmt = probe.detect_format(path)
    info = probe.classify_volume(path)
    # 单个 .rar/.zip 的名字看着像分卷（为了配对 .r00/.z01），但只有真的有多卷才算
    siblings = []
    try:
        parent = os.path.dirname(path) or "."
        siblings = [e.path for e in os.scandir(parent) if e.is_file()]
    except OSError:
        pass
    my_group = next(
        (
            g
            for g in probe.group_volumes(siblings)
            if os.path.normcase(g.main) == os.path.normcase(path)
        ),
        None,
    )
    real_split = bool(my_group and my_group.is_split)

    out(f"  真实格式    {probe.describe(fmt, info if real_split else None)}")
    out(f"  扩展名      {os.path.splitext(name)[1] or '（无）'}")
    out(f"  是否伪装    {'是' if probe.is_disguised(path) else '否'}")
    if real_split and my_group:
        out(f"  分卷        {info.kind.value}，共 {my_group.count} 卷，"
              f"{'这是主卷' if info.is_first else '不是主卷，请改用主卷'}")
    cleaned = probe.clean_delete_chars(name)
    if cleaned != name:
        out(f"  清理「删」字 → {cleaned}")

    # 「垫了真视频、后面接压缩包」那种：头不是包，但身体里是。
    # 判据与目录分支**同一份**（`carrier_report` 的 docstring 写了为什么、以及为什么要先问引擎）。
    for line in carrier_report(path):
        out("  " + line)

    from core import naming

    guess = naming.extract_from_path(path)
    out(f"  文件名密码  {guess.value if guess else '（未找到）'}"
          + (f"  ← 来源：{guess.source}" if guess else ""))


def is_done(res) -> bool:
    """这一项算不算「成功」？（`B-2026-039` 定下的口径）

    `PierceResult.ok` 的语义是「**这不是异常**」（§19.1 的 `R-11`），它把好几种
    「安全停下、但确实有内容没解」也算成 ok：
      * `CANCELLED`（用户点了停止）
      * `OUTPUT_EXISTS`（输出目录已存在、按设置跳过）
      * `MAX_DEPTH`（到层数上限，还有包没解）
      * `ALREADY_VISITED`（内容相同的重复包被跳过）
      * `AMBIGUOUS`（多个伪装包分不清主次）
      * `SEARCH_INCOMPLETE`（没往下找完，可能还有包）

    命令行以前只判 `not res.ok`，于是上面这些全被报成「成功」、退出码 0 ——
    脚本化调用者（CI / 批处理 / 别的工具）拿到的是**错误结论**。
    所以口径是三个条件同时成立：
    `ok` **且** `not partial`（leftover 非空 / 没往下找完，现成的判据）
    **且** 不是 `CANCELLED`（用户喊停时 leftover 可能还没来得及记账，单独列出来）。
    """
    return bool(res.ok and not res.partial
                and res.stop_reason is not StopReason.CANCELLED)


def main() -> int:
    args = build_parser().parse_args()

    # ★ 路径先 `abspath`，跟界面那条路（`pipeline.scan`）对齐。
    #   为什么必须做（2026-09-21 攻击审计 BUG-1）：裸名 `CON` 是个**相对的** DOS 设备名，
    #   `open("CON", "rb")` 会永久阻塞；而 `abspath` 走 `GetFullPathNameW`，会把裸名
    #   规范化成 `\\.\CON`，于是后面那句 `os.path.isfile()` 检查就能把它挡掉。
    #   界面侧一直安全正是因为它先 `abspath`；CLI 以前没有，所以 `cli.py CON`
    #   （甚至磁盘上根本没这个文件时）会直接挂住。
    args.paths = [os.path.abspath(p) for p in args.paths]

    cap = probe_capability()
    out("== 引擎 ==")
    for line in cap["note"].splitlines() if cap["note"] else []:
        out("  " + line)
    eng = find_engines()
    out(f"  7-Zip  : {eng.seven_zip or '未找到'}")
    out(f"  WinRAR : {eng.winrar or '未找到'}")

    if args.probe:
        for path in args.paths:
            print_probe(path)
        return 0

    vault = PasswordVault(book=args.book)
    vault.reload()
    out(f"== 密码本 ==  共 {vault.size()['total']} 个密码")

    extractor = Extractor()
    failed = 0

    for path in args.paths:
        if not os.path.exists(path):
            out(f"\n■ {path}\n  ✘ 路径不存在")
            failed += 1
            continue

        out(f"\n■ {os.path.basename(path)}")
        logger = None if args.quiet else (lambda m: out("  " + m))
        piercer = Piercer(
            extractor,
            vault,
            max_depth=1 if args.no_pierce else args.depth,
            min_free_gb=args.min_free,
            flatten_single_child=not args.no_flatten,
            remove_source=args.delete_source,
            scan_appended=not args.no_appended,
            exclude_exts=list(Config.load(paths_mod.data_path("config.json")).exclude_exts or ()),
            logger=logger,
        )
        res = piercer.run(path)
        out(describe_pierce_result(res))
        if res.output_dir:
            out(f"  输出目录：{res.output_dir}")
        if not is_done(res):
            failed += 1

    ok_n = len(args.paths) - failed
    out(f"\n== 汇总 ==  {ok_n}/{len(args.paths)} 成功")
    if failed:
        out(f"  （{failed} 项没解完：取消 / 跳过 / 到层数上限 / 没往下找完 / 解压失败都算）")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
