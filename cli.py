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
    --min-free GB      剩余空间下限（默认 5）
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
from core.pierce import Piercer, describe_pierce_result  # noqa: E402
from core.vault import DEFAULT_BOOK, PasswordVault  # noqa: E402

ROOT_DIR = BASE


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
                   help="不识别「垫了真视频、后面接压缩包」的伪装（1067.mp4 那种）")
    p.add_argument("--delete-source", action="store_true", help="解压成功后删除原压缩包")
    p.add_argument("--min-free", type=float, default=5.0, help="剩余空间下限 GB（默认 5）")
    p.add_argument("--book", default=paths_mod.data_path(DEFAULT_BOOK), help="密码本文件")
    p.add_argument("--quiet", action="store_true", help="只打结论，不打过程日志")
    return p


def print_probe(path: str) -> None:
    """探测模式：把「它认出了什么」摊开给人看。"""
    name = os.path.basename(path)
    print(f"\n■ {name}")
    if os.path.isdir(path):
        print("  类型        文件夹")
        try:
            entries = [e.path for e in os.scandir(path) if e.is_file()]
        except OSError as e:
            print(f"  读取失败    {e}")
            return
        groups = probe.group_volumes(entries)
        if groups:
            print(f"  分卷组      {len(groups)} 组")
            for g in groups:
                print(f"    · {os.path.basename(g.main)}  共 {g.count} 卷（只处理主卷）")
        for p in entries:
            if probe.classify_volume(p).kind is not probe.VolKind.NONE:
                continue
            fmt = probe.detect_format(p)
            if fmt.is_archive:
                tag = "（伪装）" if probe.is_disguised(p) else ""
                print(f"    · {os.path.basename(p)}  →  {fmt.value}{tag}")
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

    print(f"  真实格式    {probe.describe(fmt, info if real_split else None)}")
    print(f"  扩展名      {os.path.splitext(name)[1] or '（无）'}")
    print(f"  是否伪装    {'是' if probe.is_disguised(path) else '否'}")
    if real_split and my_group:
        print(f"  分卷        {info.kind.value}，共 {my_group.count} 卷，"
              f"{'这是主卷' if info.is_first else '不是主卷（要喂主卷）'}")
    cleaned = probe.clean_delete_chars(name)
    if cleaned != name:
        print(f"  清理「删」字 → {cleaned}")

    # 「垫了真视频、后面接压缩包」那种：头不是包，但身体里是。
    # **先问引擎**（0.03s）再自己扫：`[SLG官中]…2.88G.mp4` 是"36 字节假 MP4 头 +
    # 一整个 ZIP"，包内偏移是绝对的，7-Zip 报 `Embedded Stub Size = 36` 后能直接读，
    # 而 probe 的反推起点会算出 base=0、判非法 → 只报"扫过，没有"，把能解的说成不能解。
    if not fmt.is_archive and probe.looks_like_carrier(path):
        from core.pipeline import engine_can_read

        if engine_can_read(path):
            print("  内嵌压缩包  引擎能直接读（假头 + 一个完整的包）")
            print("              → 不切包，直接把原文件交给 7-Zip（切了包内绝对偏移会错位）")
        else:
            emb = probe.find_embedded(path)
            if emb is not None:
                tail = probe.human_size(os.path.getsize(path) - emb.offset)
                print(f"  内嵌压缩包  {emb.label}（{emb.how}）")
                print(f"              → 解压时会先切出 {tail} 再解，不动原文件")
            else:
                print("  内嵌压缩包  （引擎读不了、扫过也没有）")

    from core import naming

    guess = naming.extract_from_path(path)
    print(f"  文件名密码  {guess.value if guess else '（未找到）'}"
          + (f"  ← 来源：{guess.source}" if guess else ""))


def main() -> int:
    args = build_parser().parse_args()

    cap = probe_capability()
    print("== 引擎 ==")
    for line in cap["note"].splitlines() if cap["note"] else []:
        print("  " + line)
    eng = find_engines()
    print(f"  7-Zip  : {eng.seven_zip or '未找到'}")
    print(f"  WinRAR : {eng.winrar or '未找到'}")

    if args.probe:
        for path in args.paths:
            print_probe(path)
        return 0

    vault = PasswordVault(book=args.book)
    vault.reload()
    print(f"== 密码本 ==  共 {vault.size()['total']} 个密码")

    extractor = Extractor()
    failed = 0

    for path in args.paths:
        if not os.path.exists(path):
            print(f"\n■ {path}\n  ✘ 路径不存在")
            failed += 1
            continue

        print(f"\n■ {os.path.basename(path)}")
        logger = None if args.quiet else (lambda m: print("  " + m))
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
        print(describe_pierce_result(res))
        if res.output_dir:
            print(f"  输出目录：{res.output_dir}")
        if not res.ok:
            failed += 1

    print(f"\n== 汇总 ==  {len(args.paths) - failed}/{len(args.paths)} 成功")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
