"""管道无界面测试：扫描 → 运行 → 回调（日志 / 状态 / 问用户要密码）。

复用 smoke_core 造好的夹具，避免两套夹具逻辑走偏。

用法：
    .venv\\Scripts\\python.exe tools\\smoke_pipeline.py
"""

from __future__ import annotations

import json
import os
import random
import subprocess
import time
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
sys.path.insert(0, HERE)

import smoke_core as sc  # noqa: E402

from core.config import Config  # noqa: E402
from core.engine import Extractor  # noqa: E402
from core.pipeline import (  # noqa: E402
    ItemStatus,
    Runner,
    RunnerHooks,
    ScanItem,
    output_dirs,
    output_root,
    scan,
    summarize,
)
from core.vault import BookEntry, PasswordVault  # noqa: E402

WORK = os.path.join(ROOT, "tests", "work")
FIX = sc.FIX


def stage(name: str, *sources: str) -> str:
    d = os.path.join(WORK, name)
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    for s in sources:
        shutil.copyfile(os.path.join(FIX, s), os.path.join(d, s))
    return d


def empty_vault() -> PasswordVault:
    """没有任何密码的库——用来逼出「问用户要密码」那条路。"""
    return PasswordVault()


def test_config() -> None:
    print("\n== config：配置持久化 ==")
    path = os.path.join(WORK, "cfg", "config.json")
    shutil.rmtree(os.path.dirname(path), ignore_errors=True)

    c = Config()
    c.max_depth = 3
    c.winrar = r"C:\Program Files\WinRAR\Rar.exe"
    c.flatten = False
    saved = c.save(path)
    sc.check(saved and os.path.isfile(path), "保存 config.json")

    back = Config.load(path)
    sc.check(back.max_depth == 3 and back.flatten is False, "读回后字段一致",
             f"depth={back.max_depth} flatten={back.flatten}")
    sc.check(back.winrar.endswith("Rar.exe"), "路径字段能往返")

    # 未知字段不该让配置读崩（新老版本混用的常见坑）
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    data["将来才有的字段"] = {"a": 1}
    data["max_depth"] = "5"          # 类型不对
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    tolerant = Config.load(path)
    sc.check(tolerant.max_depth == 5, "类型不对时回落（字符串 '5' → 5 或默认）",
             str(tolerant.max_depth))

    with open(path, "w", encoding="utf-8") as f:
        f.write("{ 这不是合法 json")
    broken = Config.load(path)
    sc.check(broken.max_depth == 5, "配置损坏时用默认值，不抛异常", str(broken.max_depth))


def test_scan() -> None:
    print("\n== scan：拖进来变成清单 ==")
    # 1) 单个文件
    items = scan([sc.fx("示例包.zip")])
    sc.check(len(items) == 1 and items[0].name == "示例包.zip", "单个文件 → 一行")
    sc.check("ZIP" in items[0].kind, "识别类型带上真实格式", items[0].kind)

    # 2) 伪装文件
    items = scan([sc.fx("教程视频.mp4")])
    sc.check("伪装" in items[0].kind, "伪装文件在清单里被标注", items[0].kind)

    # 3) 拖进来的是次卷 → 自动纠正到主卷
    parts = sorted(n for n in os.listdir(FIX) if n.startswith("资源分卷.part"))
    if len(parts) > 1:
        items = scan([sc.fx(parts[1])])
        sc.check(items[0].name == parts[0], "拖入次卷自动纠正到主卷",
                 f"{parts[1]} → {items[0].name}")
        sc.check("只处理主卷" in items[0].note, "并在备注里说明只处理主卷", items[0].note)

    # 4) 文件夹 → 汇总行 + 每个包一行
    d = stage("scan-dir", "示例包.zip", "嵌套.zip", "教程视频.mp4")
    items = scan([d])
    parents = [i for i in items if not i.runnable]
    kids = [i for i in items if i.runnable]
    sc.check(len(parents) == 1 and len(kids) == 3,
             "文件夹 → 1 个汇总行 + 3 个子项", f"汇总={len(parents)} 子项={len(kids)}")
    sc.check(parents[0].child_count == 3 and "内 3 个包" in parents[0].kind,
             "汇总行显示包数", parents[0].kind)
    sc.check(all(k.indent and k.parent == 0 for k in kids), "子项缩进且挂到汇总行")

    # 5) 分卷只列主卷（4 卷 → 1 行）
    d2 = stage("scan-vol", *parts)
    items2 = scan([d2])
    kids2 = [i for i in items2 if i.runnable]
    sc.check(len(kids2) == 1, f"{len(parts)} 个分卷文件只列成 1 个任务", f"实际 {len(kids2)}")

    # 6) 空文件夹
    d3 = stage("scan-empty")
    items3 = scan([d3])
    sc.check(len(items3) == 1 and items3[0].runnable, "空文件夹仍生成一个可执行项")
    sc.check("没有可识别" in items3[0].note, "并注明目录里没有压缩包", items3[0].note)

    # 7) ★ 去重：一次性拖入多个分卷，不该变成多个一模一样的任务
    #    （实测 bug：拖 part1~part4 → 4 个「资源分卷.part1.rar」，同一个包解 4 遍）
    split_paths = sorted(sc.fx(n) for n in os.listdir(FIX) if n.startswith("资源分卷.part"))
    if len(split_paths) > 1:
        one = scan(split_paths)
        sc.check(len(one) == 1, f"拖入 {len(split_paths)} 个分卷 → 只生成 1 个任务",
                 f"实际 {len(one)} 个：{[i.name for i in one]}")
        sc.check(one[0].name == os.path.basename(split_paths[0]), "纠正到主卷", one[0].name)
        sc.check(len(scan(split_paths + split_paths)) == 1, "同一批重复拖两遍 → 仍然 1 个")
        sc.check(len(scan([split_paths[0], split_paths[1], split_paths[0]])) == 1,
                 "主卷和次卷混着拖 → 只算一个")

    # 8) ★ 去重：文件夹里的包 + 单独拖同一个包
    d_dup = stage("scan-dup", "示例包.zip", "教程视频.mp4")
    dup = [i for i in scan([d_dup, os.path.join(d_dup, "示例包.zip")]) if i.runnable]
    sc.check(len(dup) == 2, "文件夹 + 单独再拖里面某个包 → 不重复计",
             f"{[i.name for i in dup]}")
    sc.check(len(scan([d_dup, d_dup])) == len(scan([d_dup])), "同一个文件夹拖两遍 → 不重复")


def test_runner() -> None:
    print("\n== Runner：扫描 → 运行 → 回调 ==")
    ex = Extractor()

    # 1) 文件夹里两个包：一个加密（密码在库里）、一个明文
    d = stage("pipe-run", "示例包.zip", "教程视频.mp4")
    items = scan([d])
    vault = PasswordVault()
    vault.set_entries([sc.PASS])

    logs: list[str] = []
    events: list[tuple[str, ItemStatus]] = []
    runner = Runner(
        items,
        vault=vault,
        extractor=ex,
        config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(
            on_log=logs.append,
            on_item=lambda it: events.append((it.name, it.status)),
        ),
    )
    result = runner.run()
    kids = [i for i in result if i.runnable]
    sc.check(all(k.status is ItemStatus.DONE for k in kids), "两个任务都完成",
             str([(k.name, k.status.value) for k in kids]))
    sc.check(any("命中" in m for m in logs), "日志里能看到密码命中过程")
    sc.check(any(st is ItemStatus.RUNNING for _, st in events), "状态变化回调发出了 RUNNING")

    parent = next(i for i in result if not i.runnable)
    sc.check(parent.status is ItemStatus.DONE and "2 成功" in parent.note,
             "汇总行回填成功数", f"{parent.status.value} / {parent.note}")

    counts = summarize(result)
    sc.check(counts["done"] == 2 and counts["queued"] == 0,
             "计数卡数据只算子项", str(counts))

    enc = next(k for k in kids if k.name.endswith(".zip"))
    sc.check(enc.password == sc.PASS and enc.source == "密码本",
             "任务行带回真实密码与来源", f"{enc.password} / {enc.source}")
    plain = next(k for k in kids if k.name.endswith(".mp4"))
    sc.check(plain.source == "无密码", "未加密的包来源标为「无密码」", plain.source)

    # 2) 密码库里没有 → 回调问用户 → 用户给了正确密码
    d2 = stage("pipe-ask", "示例包.zip")
    items2 = scan([d2])
    asked: list[str] = []

    def answer(path: str, un) -> str | None:
        asked.append(os.path.basename(path))
        return sc.PASS

    r2 = Runner(
        items2, vault=empty_vault(), extractor=ex, config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(ask_password=answer, on_log=lambda m: None),
    )
    res2 = r2.run()
    kid2 = next(i for i in res2 if i.runnable)
    sc.check(bool(asked), "密码库试完后确实回调了「问用户要密码」", str(asked))
    sc.check(kid2.status is ItemStatus.DONE and kid2.source == "手动输入",
             "手动输入的密码被采用并标注来源", f"{kid2.status.value} / {kid2.source}")

    # 3) 用户放弃 → 该任务标为「已跳过」，不拖垮整批
    d3 = stage("pipe-skip", "示例包.zip", "教程视频.mp4")
    items3 = scan([d3])
    r3 = Runner(
        items3, vault=empty_vault(), extractor=ex, config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(ask_password=lambda p, u: None, on_log=lambda m: None),
    )
    res3 = r3.run()
    kids3 = {i.name: i for i in res3 if i.runnable}
    enc3 = next(i for n, i in kids3.items() if n.endswith(".zip"))
    plain3 = next(i for n, i in kids3.items() if n.endswith(".mp4"))
    sc.check(enc3.status is ItemStatus.SKIPPED, "放弃输密码的任务标为 SKIPPED",
             f"{enc3.status.value} / {enc3.note}")
    sc.check(plain3.status is ItemStatus.DONE, "同批的其他任务照常完成", plain3.status.value)

    # 5) 飞轮：密码本里没有 → 弹窗手输 → **解压成功后**自动写回密码本、次数 +1
    d5 = stage("pipe-flywheel", "示例包.zip")
    book = os.path.join(WORK, "flywheel", "密码本.txt")
    shutil.rmtree(os.path.dirname(book), ignore_errors=True)
    os.makedirs(os.path.dirname(book))
    with open(book, "w", encoding="utf-8") as f:
        f.write("# 一本密码\n完全不相关的密码\n")
    vault5 = PasswordVault(book=book)
    vault5.reload()
    before = vault5.size()["total"]
    sc.check(vault5.find(sc.PASS) is None, "开头密码本里没有这个密码")

    r5 = Runner(
        scan([d5]), vault=vault5, extractor=ex, config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(ask_password=lambda p, u: sc.PASS, on_log=lambda m: None),
    )
    res5 = r5.run()
    kid5 = next(i for i in res5 if i.runnable)
    sc.check(kid5.status is ItemStatus.DONE and kid5.source == "手动输入",
             "手输的密码被采用", f"{kid5.status.value} / {kid5.source}")

    entry = vault5.find(sc.PASS)
    sc.check(vault5.size()["total"] == before + 1 and entry is not None and entry.hits == 1,
             "★ 解压成功后，手输的密码自动写进密码本、次数 1",
             f"{before} 条 → {vault5.size()['total']} 条 / hits={entry.hits if entry else None}")

    reread = PasswordVault(book=book)
    reread.reload()
    re_entry = reread.find(sc.PASS)
    sc.check(re_entry is not None and re_entry.hits == 1
             and reread.find("完全不相关的密码") is not None,
             "★ 落盘了：重读文件能读回次数",
             f"hits={re_entry.hits if re_entry else None} 共 {reread.size()['total']} 条")

    # 再跑一次同样的包：这次应该直接从密码本命中，不用再问用户
    asked_again: list[str] = []
    vault5.reload()
    r5b = Runner(
        scan([stage("pipe-flywheel2", "示例包.zip")]), vault=vault5, extractor=ex,
        config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(ask_password=lambda p, u: asked_again.append(p) or None,
                          on_log=lambda m: None),
    )
    res5b = r5b.run()
    kid5b = next(i for i in res5b if i.runnable)
    sc.check(not asked_again and kid5b.status is ItemStatus.DONE
             and "密码本" in kid5b.source,
             "★ 第二次遇到同类包：直接命中密码本，不再打扰用户",
             f"asked={asked_again} source={kid5b.source}")

    # 6) 引擎报错（不是密码问题）时**不该**弹窗问用户
    #    用一个缺分卷的 rar：只拷 part1，不拷 part2~part4
    if sc.WINRAR and os.path.isfile(sc.fx("资源分卷.part1.rar")):
        d5 = stage("pipe-engineerr", "资源分卷.part1.rar")
        items5 = scan([d5])
        asked5: list[str] = []
        r5 = Runner(
            items5, vault=PasswordVault(), extractor=ex, config=Config(min_free_gb=0.001),
            hooks=RunnerHooks(ask_password=lambda p, u: asked5.append(p) or sc.PASS,
                              on_log=lambda m: None),
        )
        res5 = r5.run()
        kid5 = next(i for i in res5 if i.runnable)
        sc.check(not asked5, "缺分卷这类引擎错误不会弹窗要密码（否则无头场景会挂死）",
                 f"asked={asked5}")
        sc.check(kid5.status is ItemStatus.FAILED, "该任务标为 FAILED 而不是 SKIPPED",
                 f"{kid5.status.value} / {kid5.note}")

    # 6) 中止开关：剩下的任务不再执行
    d4 = stage("pipe-stop", "示例包.zip", "教程视频.mp4", "嵌套.zip")
    items4 = scan([d4])
    state = {"n": 0}

    def stop() -> bool:
        state["n"] += 1
        return state["n"] > 1        # 第一个任务之后就喊停

    r4 = Runner(
        items4, vault=PasswordVault(), extractor=ex, config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(on_log=lambda m: None), should_stop=stop,
    )
    res4 = r4.run()
    kids4 = [i for i in res4 if i.runnable]
    sc.check(any(i.status is ItemStatus.SKIPPED and "中止" in i.note for i in kids4),
             "中止后剩余任务标为 SKIPPED（用户中止）",
             str([(i.name, i.status.value) for i in kids4]))


def test_cancel() -> None:
    """★ 点「停止」必须**立刻**掐断正在跑的引擎。

    原来的实现在任务之间才检查停止标志，而 `subprocess.run` 是阻塞的，
    所以点了停止界面毫无反应（可能要等几十分钟）。现在改成 Popen + 轮询 kill。
    """
    print("\n== 中止：立刻掐断正在跑的引擎 ==")
    import threading

    ex = Extractor()
    flag = {"v": False}
    ex.cancel = lambda: flag["v"]
    threading.Timer(0.4, lambda: flag.update(v=True)).start()
    t0 = time.monotonic()
    res = ex._run(["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 5"])
    dt = time.monotonic() - t0
    sc.check(res.cancelled and dt < 1.5,
             f"★ 中止能在 1.5s 内掐断长命令（实测 {dt:.2f}s，原本要等满 5s）",
             f"cancelled={res.cancelled} 耗时={dt:.2f}s")

    # 中止后：剩下的任务标为「用户中止」，不再继续
    d = stage("pipe-killstop", "示例包.zip", "教程视频.mp4", "嵌套.zip")
    seq = {"n": 0}

    def stop_after_first() -> bool:
        seq["n"] += 1
        return seq["n"] > 1

    ex2 = Extractor()
    ex2.cancel = stop_after_first
    res2 = Runner(
        scan([d]), vault=PasswordVault(), extractor=ex2,
        config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(on_log=lambda m: None),
        should_stop=stop_after_first,
    ).run()
    kids = [i for i in res2 if i.runnable]
    sc.check(any(i.status is ItemStatus.SKIPPED and "中止" in (i.note or "") for i in kids),
             "★ 中止后剩余任务标为「用户中止」",
             str([(i.name, i.status.value, i.note) for i in kids]))


def test_pause() -> None:
    """暂停 = 挂起引擎进程，不是"跑完当前任务再说"。"""
    print("\n== 暂停 / 继续 ==")
    import threading

    # 用一条 2 秒的假命令当引擎：挂起 2.5 秒的话，总耗时会明显超过 2 秒。
    # 暂停时长用**轮询次数**数出来，而不是按下计时器：引擎每 100ms 轮询一次，
    # 数次数就不受"进程刚起来那一小会儿"的影响（曾经因为第一次轮询来得晚，
    # 计时器窗口只剩 0.2 秒，测出 2.19s 的假失败）。
    ex = Extractor()
    logs: list[str] = []
    ex.logger = logs.append
    state = {"n": 0}

    def pause_25_polls() -> bool:
        state["n"] += 1
        return state["n"] <= 25

    ex.pause = pause_25_polls
    t0 = time.monotonic()
    res = ex._run(["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 1"])
    dt = time.monotonic() - t0
    sc.check(any("已挂起引擎进程" in m for m in logs),
             "★ 暂停真的把引擎进程挂起了（不是「跑完再说」）", str(logs[-2:]))
    sc.check(res.ok and dt >= 2.5,
             f"★ 暂停期间进程真的被挂起、继续后跑完（1s 的命令跑了 {dt:.2f}s）",
             f"ok={res.ok} 耗时={dt:.2f}s 轮询={state['n']} 次")
    sc.check(any("已恢复引擎进程" in m for m in logs), "恢复也记了日志", str(logs[-1:]))

    # 挂起的时间不能算进超时，否则"暂停一会儿"会把任务判死。
    # 1 秒的命令 + 2 秒超时 + ~3 秒暂停：算盘打对了 → 有效用时还是 1 秒（安全）；
    # 要是把挂起时间也算进去，墙钟 4 秒早就超了，这条用例就会红。
    ex2 = Extractor(timeout=2.0)
    logs2: list[str] = []
    ex2.logger = logs2.append
    state2 = {"n": 0}

    def pause_30_polls() -> bool:
        state2["n"] += 1
        return state2["n"] <= 30

    ex2.pause = pause_30_polls
    res2 = ex2._run(["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 1"])
    sc.check(any("已挂起引擎进程" in m for m in logs2), "第二次暂停也确实挂起了", str(logs2[-2:]))
    sc.check(res2.ok and not res2.timed_out,
             "★ 暂停时长不计入超时（否则暂停完就被判超时）",
             f"ok={res2.ok} timed_out={res2.timed_out} 耗时={res2.seconds:.2f}s")

    # 不暂停时行为不变（别把普通场景拖慢）
    ex3 = Extractor()
    t0 = time.monotonic()
    res3 = ex3._run(["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 1"])
    dt3 = time.monotonic() - t0
    sc.check(res3.ok and dt3 < 2.5, "不暂停时照常跑，没有额外开销", f"耗时={dt3:.2f}s")

    # Runner 层：暂停期间不开下一个任务，恢复后接着跑完这一批
    d = stage("pause-runner", "示例包.zip", "教程视频.mp4")
    ex4 = Extractor()
    flag3 = {"v": True}
    ex4.pause = lambda: flag3["v"]
    vault4 = PasswordVault()
    vault4.set_entries([sc.PASS])          # 那个加密包得有密码，否则它是"跳过"不是"完成"
    threading.Timer(0.9, lambda: flag3.update(v=False)).start()
    t0 = time.monotonic()
    res4 = Runner(
        scan([d]), vault=vault4, extractor=ex4, config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(on_log=lambda m: None), should_pause=lambda: flag3["v"],
    ).run()
    dt4 = time.monotonic() - t0
    kids = [i for i in res4 if i.runnable]
    # 时间只做"确实等过一会儿"的下限检查（0.5s），重点是整批照样跑完：
    # 这里按秒计时，进程启动那点抖动不该让它变成假失败
    sc.check(dt4 >= 0.5 and all(k.status is ItemStatus.DONE for k in kids),
             "★ 暂停期间不开新任务，恢复后整批跑完",
             f"耗时={dt4:.2f}s 状态={[k.status.value for k in kids]}")


def test_embedded() -> None:
    """内嵌压缩包在「扫描 → 运行」这条链上的表现。"""
    print("\n== 内嵌压缩包：扫描与运行 ==")
    ex = Extractor()

    # 1) 文件夹扫描：垫片伪装要被列出来，并且把"切包"这件事说清楚
    d = stage("scan-embed", "垫片伪装.mp4", "示例包.zip")
    items = scan([d])
    kids = [i for i in items if i.runnable]
    sc.check(len(kids) == 2, "文件夹里的垫片伪装被认出来了", str([k.name for k in kids]))
    carrier = next(i for i in kids if i.name.endswith("垫片伪装.mp4"))
    sc.check("内嵌 ZIP" in carrier.kind and "偏移" in carrier.kind,
             "识别类型写明「内嵌 ZIP + 偏移」", carrier.kind)
    sc.check("切出来" in carrier.note, "备注里说明会先切出来、不动原文件", carrier.note)

    # 2) 单独拖入也要能识别（以前这种文件会被塞进队列然后报一句"解压失败"）
    one = scan([sc.fx("垫片伪装.mp4")])
    sc.check(len(one) == 1 and one[0].runnable and "内嵌" in one[0].kind,
             "单独拖入垫片伪装也是一行可执行任务", one[0].kind)

    # 3) 关掉开关 → 不再扫内嵌包（省掉读一遍文件的代价）
    off = [i for i in scan([d], scan_appended=False) if i.runnable]
    sc.check(all("内嵌" not in i.kind for i in off), "关掉开关后不做内嵌识别",
             str([(i.name, i.kind) for i in off]))

    # 4) 真正的视频文件（没有内嵌包）：列出来但标成不可执行，不许当任务去跑
    plain = os.path.join(stage("scan-plainvideo"), "真视频.mp4")
    with open(plain, "wb") as f:
        f.write(sc._fake_mp4_head())
        f.write(random.Random(3).randbytes(2 << 20))
    pv = scan([plain])
    sc.check(len(pv) == 1 and not pv[0].runnable,
             "★ 真视频：列出但标为不可执行（不再报「解压失败」）",
             f"runnable={pv[0].runnable} kind={pv[0].kind}")
    sc.check("不是压缩包" in pv[0].note, "并说明它不是压缩包", pv[0].note)
    counts = summarize(pv)
    sc.check(counts["queued"] == 0, "不可执行的行不进计数卡", str(counts))

    # 5) 跑一遍：切包 → 解压 → 原文件不动 → output_top 指向第一层产物
    d5 = stage("pipe-embed", "垫片伪装.mp4")
    src5 = os.path.join(d5, "垫片伪装.mp4")
    size_before = os.path.getsize(src5)
    logs5: list[str] = []
    r5 = Runner(
        scan([src5]), vault=PasswordVault(), extractor=ex,
        config=Config(min_free_gb=0.001),
        hooks=RunnerHooks(on_log=logs5.append),
    )
    res5 = r5.run()
    kid5 = next(i for i in res5 if i.runnable)
    sc.check(kid5.status is ItemStatus.DONE, "★ 垫片伪装跑到底：DONE",
             f"{kid5.status.value} / {kid5.note}")
    sc.check(os.path.isfile(os.path.join(kid5.output_top, "垫片内容.txt")),
             "第一层产物目录里就有解出来的内容", kid5.output_top)
    sc.check(kid5.output_top == kid5.output or os.path.isdir(kid5.output_top),
             "output_top 是真实存在的目录（完成页「打开输出目录」用它）", kid5.output_top)
    sc.check(os.path.getsize(src5) == size_before, "★ 原视频没被动过",
             f"{size_before} → {os.path.getsize(src5)}")
    sc.check(not [n for n in os.listdir(d5) if ".carve." in n], "★ 临时切片已清理",
             str(os.listdir(d5)))


def test_output_targets() -> None:
    """★ 多个来源时产物落在哪、以及「打开输出目录」该指向哪。

    这是用户点名要测的：一次拖进来的东西可能来自好几个文件夹（甚至好几个盘），
    "目标文件夹"就可能有多个答案。这里的规则必须明确、可测：

      * 同目录模式  → 每个包解在**它自己所在目录**里，来源之间互不干扰；
      * 指定目录模式 → 都归到指定目录下，各自一个子目录，同名靠 rename 排队；
      * 打开输出目录 → 一个产物就直接开；多个产物时只认它们的公共上级，
                      跨盘/只剩盘符根时**不猜**（交给界面逐条列）。
    """
    print("\n== 多来源输出目录：产物位置 + 打开目标 ==")
    ex = Extractor()

    # 两个来源目录，各自有包；故意让 A 里有个加密包逼出"密码命中"那条路
    a = stage("out-a", "嵌套.zip", "教程视频.mp4")
    b = stage("out-b", "示例包.zip")
    vault = PasswordVault()
    vault.set_entries([sc.PASS])

    def run(paths, cfg):
        return Runner(
            scan(paths), vault=vault, extractor=ex, config=cfg,
            hooks=RunnerHooks(on_log=lambda m: None),
        ).run()

    # 1) 同目录模式：每个包的产物必须落在**它自己**的源目录里
    res = run([a, b], Config(min_free_gb=0.001, output_mode="same"))
    kids = [i for i in res if i.runnable]
    sc.check(len(kids) == 3 and all(k.status is ItemStatus.DONE for k in kids),
             "3 个来源任务全部完成",
             str([(k.name, k.status.value) for k in kids]))
    strays = [k.name for k in kids
              if os.path.normcase(os.path.dirname(k.output_top))
              != os.path.normcase(os.path.dirname(k.path))]
    sc.check(not strays, "★ 同目录模式：产物都落在各自的源目录里，没有串门", str(strays))
    dirs = output_dirs(res)
    sc.check(len(dirs) == 3, "3 个任务 → 3 个产物目录", str(dirs))
    sc.check(all(os.path.isdir(d) for d in dirs), "产出的目录真实存在")
    sc.check(output_root(dirs) == os.path.abspath(WORK),
             "★ 多个来源的公共上级 = 它们共同的父目录（打开输出目录的第一项）",
             f"{output_root(dirs)} vs {os.path.abspath(WORK)}")
    sc.check(all(d not in (os.path.abspath(k.path) for k in kids) for d in dirs),
             "产物目录不是源文件本身")

    # 2) 指定输出目录：所有来源都归到那里，各自一个子目录
    out = os.path.join(WORK, "out-custom")
    shutil.rmtree(out, ignore_errors=True)
    res2 = run([a, b], Config(min_free_gb=0.001, output_mode="custom", output_dir=out))
    kids2 = [i for i in res2 if i.runnable]
    sc.check(all(os.path.normcase(os.path.dirname(k.output_top)) == os.path.normcase(out)
                 for k in kids2),
             "★ 指定目录模式：本该散落各处的产物全归到指定目录下",
             str([(k.name, k.output_top) for k in kids2]))
    dirs2 = output_dirs(res2)
    sc.check(output_root(dirs2) == os.path.abspath(out),
             "★ 这时公共上级就是用户指定的那个目录", output_root(dirs2))
    sc.check(len(set(os.path.normcase(d) for d in dirs2)) == len(dirs2),
             "多个产物目录互不重复", str(dirs2))

    # 3) ★ 两个来源里有一模一样的包名（不同目录 → 两个任务），
    #    指定目录模式下不能互相覆盖：后者必须自己让开一个格子
    c = stage("out-c", "嵌套.zip")
    d = stage("out-d", "嵌套.zip")
    out3 = os.path.join(WORK, "out-same-stem")
    shutil.rmtree(out3, ignore_errors=True)
    res3 = run([c, d], Config(min_free_gb=0.001, output_mode="custom", output_dir=out3))
    kids3 = [i for i in res3 if i.runnable]
    sc.check(len(kids3) == 2 and all(k.status is ItemStatus.DONE for k in kids3),
             "同名不同来源 = 两个任务，都跑完",
             str([(k.name, k.status.value) for k in kids3]))
    tops3 = [k.output_top for k in kids3]
    sc.check(len(set(os.path.normcase(t) for t in tops3)) == 2
             and any(t.endswith("(1)") for t in tops3),
             "★ 同名产物不会互相覆盖（第二个自动加 (1)）", str(tops3))
    sc.check(all(os.listdir(t) for t in tops3),
             "两个产物目录里都真的有东西", str([os.listdir(t) for t in tops3]))

    # 4) 再跑一遍同一批（rename 策略）：不许覆盖上一批，也不许复用旧目录
    res4 = run([c, d], Config(min_free_gb=0.001, output_mode="custom", output_dir=out3))
    tops4 = [i.output_top for i in res4 if i.runnable and i.output_top]
    sc.check(not (set(tops4) & set(tops3)),
             "★ 同一批再跑一次：产出新目录，不动上一批", f"{tops3} → {tops4}")

    # 5) 「打开输出目录」的三种结论（纯函数，不碰磁盘）
    sc.check(output_root(["D:/a/one"]) == "", "只有一个产物目录时不叠公共上级")
    sc.check(output_root(["C:/x/a", "D:/y/b"]) == "", "跨盘符没有公共上级")
    sc.check(output_root(["D:/a/x", "D:/b/y"]) == "", "★ 公共上级只剩盘符根时也不给（打开它等于没打开）")
    fake = [
        ScanItem(path="p1", name="n1", kind="ZIP", status=ItemStatus.DONE, output_top="D:/a/x"),
        ScanItem(path="p2", name="n2", kind="ZIP", status=ItemStatus.DONE, output_top="D:/a/x"),
        ScanItem(path="p3", name="n3", kind="RAR", status=ItemStatus.FAILED, output_top="D:/a/y"),
        ScanItem(path="p4", name="n4", kind="7Z", status=ItemStatus.SKIPPED, output_top=""),
    ]
    sc.check(output_dirs(fake) == [], "目录不存在/失败/跳过的都不算（这里是假路径）",
             str(output_dirs(fake)))
    fake[0].output_top = WORK
    sc.check(output_dirs(fake) == [os.path.abspath(WORK)], "只有成功项、且去重",
             str(output_dirs(fake)))


def _drop_reg_tree(path: str) -> None:
    """整棵删掉一个注册表键（winreg 没有递归删除）。

    卸载只删 `...\\shell\\BBUUnpack`，`*` / `Directory` / `shell` 这些"骨架"键会留着
    （真实根上它们是别的软件共用的，绝不能碰）；测试根上就得自己清干净。
    """
    import winreg

    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_READ) as k:
            subs = []
            i = 0
            while True:
                try:
                    subs.append(winreg.EnumKey(k, i))
                except OSError:
                    break
                i += 1
        for sub in subs:
            _drop_reg_tree(path + "\\" + sub)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except OSError:
        pass


def test_shellmenu() -> None:
    """★ 右键菜单注册：写注册表 / 状态查询 / 卸载。

    测试**只碰临时根键**（`Software\\BBUTest\\Classes`），
    绝不往真实的资源管理器菜单里写东西；跑完把测试根也删掉。
    """
    print("\n== 右键菜单注册（临时注册表根）==")
    import winreg

    from core import shellmenu as sm

    test_root = r"Software\BBUTest\Classes"
    real = sm.state(root=test_root)
    sc.check(not real["installed"], "临时根开始是干净的", str(real))

    pw = r"D:\x\.venv\Scripts\pythonw.exe"
    script = r"D:\x\run.py"

    # 先测不碰注册表的部分（这些在哪儿都能测）
    cmd = sm.command_line(pw, script, placeholder="%1")
    sc.check(cmd == f'"{pw}" "{script}" "%1"', "命令行拼装", cmd)
    sc.check("--auto" not in cmd,
             "★ 右键菜单只有一种模式：加进待处理列表，不自动开始", cmd)
    here = os.path.dirname(sys.executable)
    got = sm.pythonw_for(sys.executable)
    if os.path.isfile(os.path.join(here, "pythonw.exe")):
        sc.check(got.endswith("pythonw.exe"), "★ 自动换成同目录的 pythonw（不闪黑框）", got)
    sc.check(isinstance(sm.state()["installed"], bool), "读真实根的状态不报错")

    try:
        keys = sm.install(pw, script, root=test_root, icon=r"D:\x\a.ico")
    except OSError as exc:
        # 受控环境下 HKCU 也可能是只读的：这时候别假装通过，明确报"跳过"，
        # 安装/卸载最终得由界面上的按钮在真实桌面会话里点一次
        print(f"  [SKIP] 这个环境不让写注册表（{exc}）——"
              "安装/卸载请由界面「设置 → 资源管理器右键菜单」按钮完成")
        sc.check(not sm.state(root=test_root)["installed"],
                 "写不进去的时候状态如实显示「未注册」")
        sc.check(sm.uninstall(root=test_root) == [], "卸载不存在的项也不报错")
        return

    sc.check(len(keys) == 3, "文件 / 文件夹 / 文件夹空白处 三处都写了", str(keys))
    st = sm.state(root=test_root)
    sc.check(st["installed"] and len(st["places"]) == 3, "状态查询说已注册", str(st))
    sc.check(pw in st["command"] and script in st["command"] and "--auto" not in st["command"],
             "命令里带 pythonw / run.py，且不带 --auto", st["command"])
    sc.check(st["text"] == sm.MENU_TEXT, "菜单文字正确", st["text"])
    sc.check('"%1"' in st["command"], "文件/文件夹用 %1 拿路径", st["command"])

    # 三处的占位符各自不同：文件夹空白处要用 %V（当前目录）
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                        test_root + r"\Directory\Background\shell\BBUUnpack\command") as k:
        bg = str(winreg.QueryValueEx(k, None)[0])
    sc.check('"%V"' in bg, "★ 文件夹空白处用 %V（否则拿到的是空路径）", bg)

    # 多选：不写 MultiSelectModel 的话资源管理器只把第一个文件给过来
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                        test_root + r"\*\shell\BBUUnpack") as k:
        multi = str(winreg.QueryValueEx(k, "MultiSelectModel")[0])
    sc.check(multi == "Player", "★ 允许多选（否则右键 5 个文件只处理 1 个）", multi)

    removed = sm.uninstall(root=test_root)
    sc.check(len(removed) == 3 and not sm.state(root=test_root)["installed"],
             "卸载后三处都没了", str(removed))
    sc.check(sm.uninstall(root=test_root) == [], "重复卸载不报错")

    # 收尾：把测试根整棵删掉，别在注册表里留垃圾
    _drop_reg_tree(r"Software\BBUTest")


def test_run_log() -> None:
    """★ 界面日志落盘 `logs/run.log`：记全部内容（含完整路径）、密码打码、超限轮转。

    用户的预期是"日志文件应该记下日志窗口显示的东西"；`logs/ui.log` 只管启动与异常，
    流水走 `run.log`。这里把它钉死，免得以后又被合并回去或忘了轮转把盘写满。
    """
    print("\n== 运行日志 run.log ==")
    from core.runlog import RunLog, mask_secrets

    d = os.path.join(WORK, "runlog")
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, "run.log")

    rl = RunLog(p)
    sc.check(rl.is_open, "能打开（可写目录）")
    rl.session_header("测试")
    rl.write("扫描", r"D:\incoming\某个包.zip 扫描完成")
    rl.write("剪贴板", "已复制密码：Secret123")
    rl.close()

    body = open(p, encoding="utf-8").read()
    sc.check("扫描完成" in body and r"D:\incoming\某个包.zip" in body,
             "★ 面板内容进了 run.log，而且是**完整路径**（排错要靠它）", body[-120:])
    sc.check("Secret123" not in body and "已复制密码：***" in body,
             "★ 密码打码后才落盘（日志经常要发出来排错）", body[-60:])
    sc.check("启动" in body and "=====" in body, "每次启动有分隔头，看得出哪几行是哪次运行")

    # 轮转：超过上限就把旧文件改名成 run.log.1，只留一份
    rl2 = RunLog(p, max_bytes=300)
    for i in range(60):
        rl2.raw(f"填充 {i} " + "x" * 20)
    rl2.close()
    sc.check(os.path.isfile(p + ".1"), "★ 超过上限会自动轮转（run.log.1）",
             str(sorted(os.listdir(d))))
    sc.check(os.path.getsize(p) < 1200 and os.path.getsize(p + ".1") >= 300,
             "★ 新文件重新开始，旧文件留一份（不会无限长）",
             f"{os.path.getsize(p)} / {os.path.getsize(p + '.1')}")

    # 写不进去也不能抛异常（只显示不落盘）：拿一个**文件**当目录用，必然失败
    blocker = os.path.join(d, "blocker")
    with open(blocker, "w", encoding="utf-8") as f:
        f.write("x")
    bad = RunLog(os.path.join(blocker, "run.log"))
    bad.write("系统", "随便写点什么")
    sc.check(not bad.is_open, "路径不可写时静默降级（不影响界面）", str(bad.is_open))
    sc.check(mask_secrets("命中 密码本：abc123") == "命中 密码本：***",
             "打码规则对「命中 密码本」也生效", mask_secrets("命中 密码本：abc123"))


def test_log_levels_and_masking() -> None:
    """★ 命令行里的密码必须打码，且细节不该往界面上灌。

    实盘事故：试密码时每条命令都带 `-p<候选>`，这些行进了日志面板之后，
    用户的整个密码本等于摊在屏幕上（`-pFLYYZ`、`-p拱墅烧烤摊师傅` …）。
    这里把"两道打码"和"空密码要保留"钉死。
    """
    print("\n== 日志打码 / 分级 ==")
    from core.engine import display_cmd, mask_cmd
    from core.runlog import mask_secrets

    got = display_cmd(["7z.exe", "l", "-slt", "-pFLYYZ", "--", r"D:\x.zip"])
    sc.check(got.endswith("-p*** -- D:\\x.zip") and "FLYYZ" not in got,
             "★ `-p<密码>` 打码后才进日志", got)
    sc.check("-p-" in display_cmd(["7z.exe", "t", "-p-", "--", "x.zip"]),
             "空密码 `-p-` 保留原样（不是秘密）",
             display_cmd(["7z.exe", "t", "-p-", "--", "x.zip"]))
    sc.check("acg18" not in display_cmd(["Rar.exe", "x", "-pacg18", "--", "y.rar"]),
             "WinRAR 的命令行一样打码")
    sc.check("sjhs003" not in display_cmd(["7z", "l", "--password=sjhs003.xyz", "--", "z"]),
             "--password=xxx 形式也打码")
    sc.check("FLYYZ" not in mask_secrets("$ 7z l -pFLYYZ -- y.zip"),
             "★ 落盘时的第二道保险同样打码（万一别处把命令行塞进日志）")
    sc.check("abc123" not in mask_cmd("7z t -pabc123 -- x"), "字符串形式的命令行也打码")

    # 两条通道各走各的：心跳进界面（info），命令行/原始输出只进文件（debug）
    # （用户明确要"心跳要看得见、试密码的过程不要"；心跳只在命令跑超过 5 秒时才发，
    #   所以这里真跑一个 6 秒的命令来验，别用 mock——mock 写过一次就漏了）
    import sys as _sys

    from core.engine import Extractor, find_engines

    info: list[str] = []
    debug: list[str] = []
    ex2 = Extractor(find_engines(), timeout=60,
                    logger=info.append, debug_logger=debug.append)
    ex2._run([_sys.executable, "-c", "import time; time.sleep(6)"])
    sc.check(any("仍在运行" in m for m in info),
             "★ 心跳走 info 通道（界面上看得见）", info[-2:])
    sc.check(not any("仍在运行" in m for m in debug), "心跳不走 debug 通道")
    sc.check(any("▶ 运行中" in m for m in debug) and not any("▶ 运行中" in m for m in info),
             "★ 命令行走 debug 通道（只进 run.log，不进界面）", debug[:1])
    sc.check(any(m.strip().startswith("$ ") for m in debug),
             "引擎的原始输出也走 debug 通道", [m for m in debug if m.startswith("$ ")][:1])


def test_paths_and_identity() -> None:
    """★ 路径解析（源码 vs 打包）与应用身份。

    打包成便携 exe 之后，`__file__` 的位置会变：资源在 `_internal` 里，用户数据要落
    可写目录。这一组断言把"两套路径不能混"钉死；`SMART_UNZIP_FORCE_FROZEN=1`
    让它在源码环境里也能验到冻结分支（右键菜单该注册 exe，而不是 pythonw+run.py）。
    """
    print("\n== 路径解析 / 打包形态 ==")
    from core import appinfo, paths as p, shellmenu as sm

    sc.check(os.path.isfile(p.resource_path("tools", "7z", "7z.exe")),
             "资源目录里能找到内置 7z（源码态）", p.resource_path("tools", "7z", "7z.exe"))
    sc.check(os.path.isfile(p.resource_path("assets", appinfo.ICON_FILE)),
             "资源目录里能找到图标", p.resource_path("assets", appinfo.ICON_FILE))
    sc.check(os.path.normcase(p.app_dir()) == os.path.normcase(ROOT),
             "源码态：程序目录就是工程根", p.app_dir())
    sc.check(not p.is_frozen(), "源码态 is_frozen() 为假")
    sc.check(os.access(p.data_dir(), os.W_OK),
             "数据目录可写（配置/密码本/日志就落在这儿）", p.data_dir())
    sc.check(p.log_path().endswith("ui.log"), "日志路径以 ui.log 结尾", p.log_path())

    # 数据目录能被"钉"到别处：测试/截图脚本靠它，绝不能动用户真实配置
    pinned = os.path.join(WORK, "paths-pinned")
    shutil.rmtree(pinned, ignore_errors=True)
    p.set_data_dir_override(pinned)
    sc.check(os.path.normcase(p.data_dir()) == os.path.normcase(os.path.abspath(pinned)),
             "★ 数据目录能被钉到指定位置（Workbench(base_dir=…) 用这条）", p.data_dir())
    sc.check(os.path.normcase(os.path.dirname(p.data_path("x"))) ==
             os.path.normcase(os.path.abspath(pinned)), "data_path 跟着走")
    p.set_data_dir_override(None)

    # 程序目录**写不进去**时要退到用户目录（典型场景：便携版被放到 C:\Program Files）
    real_appdir = p.app_dir
    try:
        p.app_dir = lambda: os.path.join("Z:\\", "definitely-not-writable")   # type: ignore[assignment]
        p.set_data_dir_override(None)
        fell_back = p.data_dir()
        sc.check(p.is_appdata_mode() and "appdata" in p.data_dir_note().lower()
                 or "用户目录" in p.data_dir_note(),
                 "★ 程序目录写不进去 → 退到用户目录（%APPDATA%）", fell_back)
        sc.check(os.path.normcase(fell_back) != os.path.normcase("Z:\\definitely-not-writable"),
                 "★ 不会硬写在不可写的位置", fell_back)
    finally:
        p.app_dir = real_appdir                       # type: ignore[assignment]
        p.set_data_dir_override(None)
    sc.check(not p.is_appdata_mode(),
             "复原后又是「就在程序旁边」（便携）", p.data_dir_note())

    # 冻结分支：右键菜单要注册 exe 自己，且命令里不能出现 run.py
    old = os.environ.get("SMART_UNZIP_FORCE_FROZEN")
    os.environ["SMART_UNZIP_FORCE_FROZEN"] = "1"
    try:
        sc.check(p.is_frozen(), "SMART_UNZIP_FORCE_FROZEN=1 时装成冻结态")
        exe, script = sm.launch_parts()
        sc.check(script == "" and os.path.normcase(exe) == os.path.normcase(sys.executable),
                 "★ 冻结态：右键直接调 exe（不经过 pythonw + run.py）", f"{exe!r} {script!r}")
        cmd = sm.exe_command("%1")
        sc.check("run.py" not in cmd and cmd.endswith('"%1"'),
                 "★ 冻结态的命令行里没有 run.py", cmd)
        sc.check(sm.menu_icon().endswith(",0"), "冻结态图标用 exe 自带的（路径,0）",
                 sm.menu_icon())
    finally:
        if old is None:
            os.environ.pop("SMART_UNZIP_FORCE_FROZEN", None)
        else:
            os.environ["SMART_UNZIP_FORCE_FROZEN"] = old
    sc.check(not p.is_frozen(), "复原后 is_frozen() 又是假")

    # 身份信息只有一处定义
    sc.check(appinfo.APP_NAME == "BullBull Unpacker" and appinfo.VERSION,
             "产品名/版本来自 core/appinfo.py", f"{appinfo.APP_NAME} {appinfo.VERSION}")
    sc.check(sm.VERB == appinfo.SHELL_VERB and sm.MENU_TEXT == appinfo.SHELL_MENU_TEXT,
             "右键菜单的 verb/文案与应用身份一致")

    # 注册表根可以用环境变量顶掉（命令行开关跑测试时靠它，别写用户的真菜单）
    keep = os.environ.get("SMART_UNZIP_SHELLMENU_ROOT")
    os.environ["SMART_UNZIP_SHELLMENU_ROOT"] = r"Software\BBUTest\EnvRoot\Classes"
    try:
        sc.check(sm.registry_root() == r"Software\BBUTest\EnvRoot\Classes",
                 "★ 注册表根能被环境变量顶掉（测试不碰真实菜单）", sm.registry_root())
    finally:
        if keep is None:
            os.environ.pop("SMART_UNZIP_SHELLMENU_ROOT", None)
        else:
            os.environ["SMART_UNZIP_SHELLMENU_ROOT"] = keep
    sc.check(sm.registry_root() == sm.ROOT, "复原后回到默认根", sm.registry_root())


def test_vault_batch_remove() -> None:
    """★ 密码本批量删除：一次删多条、只落盘一次、空密码那条也能删。"""
    print("\n== 密码本：批量删除 ==")
    book = os.path.join(WORK, "batch", "密码本.txt")
    shutil.rmtree(os.path.dirname(book), ignore_errors=True)
    os.makedirs(os.path.dirname(book))

    v = PasswordVault(book=book)
    v.set_entries(["a", "b", "c", "d"])
    v.save()
    sc.check(v.remove_many(["a", "c"]) == 2, "一次删两条")
    sc.check([e.password for e in v.entries] == ["b", "d"], "剩下的对",
             str([e.password for e in v.entries]))
    reread = PasswordVault(book=book)
    reread.reload()
    sc.check([e.password for e in reread.entries] == ["b", "d"], "★ 真的落盘了（只写一次）")

    sc.check(v.remove_many(["b", "b", "没有这条"]) == 1, "重复/不存在的条目不算数")
    sc.check(v.remove_many([]) == 0, "空列表不动文件")

    # 空密码是合法条目（界面上那行「（空密码）」），不能被当成"空值跳过"。
    # 注意它进不了密码本文件（`parse_book` 会把空行略过），只能像界面那样直接挂进内存。
    v3 = PasswordVault()
    v3.entries.append(BookEntry("", 3))
    v3.entries.append(BookEntry("keep", 1))
    sc.check(v3.remove_many([""]) == 1 and [e.password for e in v3.entries] == ["keep"],
             "★ 空密码那条也能批量删掉（它的 data 是空串，不算「没取到」）")
    sc.check(v3.remove_many(["keep"]) == 1 and not v3.entries, "普通条目照删")

    # 没有密码本文件（不落盘）时也不能报错
    v2 = PasswordVault()
    v2.set_entries(["x", "y"])
    sc.check(v2.remove_many(["x"]) == 1 and len(v2.entries) == 1,
             "没有密码本文件时（不落盘）也照常删内存")


def test_engine_details() -> None:
    """★ 引擎侧几个实测踩出来的细节：目录条目 / 未加密条目 / 条目名没匹配 / 混合编码。"""
    print("\n== engine：便宜验证挑哪个条目 + 混合编码 ==")
    from core.engine import _decode, _nothing_matched

    ex = Extractor()
    work = os.path.join(WORK, "engine-detail")
    shutil.rmtree(work, ignore_errors=True)
    sub = os.path.join(work, "新建文件夹")
    os.makedirs(sub, exist_ok=True)
    with open(os.path.join(sub, "a.txt"), "w", encoding="utf-8") as f:
        f.write("x")
    arc = os.path.join(work, "folder.zip")
    subprocess.run([sc.SEVENZIP, "a", "-tzip", arc, sub],
                   capture_output=True, creationflags=0x08000000)
    info = ex.inspect(arc)
    sc.check(info.read_ok and info.entry_count >= 2, "列得出条目", str(info.entry_count))
    sc.check(info.first_entry is not None and info.first_entry.endswith("a.txt"),
             "★ 目录条目不当「用来验证的条目」（`7z t 包 目录` 会连整棵子树一起测，实测 27s）",
             str(info.first_entry))
    sc.check(info.smallest_entry is None,
             "★ 未加密的条目也不许拿来验证（拿它验任何密码都会通过）",
             str(info.smallest_entry))

    enc = ex.inspect(sc.fx("示例包.zip"))
    sc.check(enc.encrypted is True and bool(enc.smallest_entry),
             "★ 加密包挑得出「最小的加密条目」", f"{enc.smallest_entry} / enc={enc.encrypted}")

    bad = ex.test(sc.fx("示例包.zip"), sc.PASS, entry="根本不存在的条目")
    sc.check(not bad.ok and bad.code == -3,
             "★ 条目名没匹配上 → 不算通过（7z 返回 0，其实什么都没测）",
             f"ok={bad.ok} code={bad.code}")
    sc.check(_nothing_matched("No files to process\nEverything is Ok\n\nFiles: 0"),
             "认得「没匹配上」的输出特征")

    raw = ("驱动器 D 中的卷是 数据".encode("gbk") + b"\n"
           + "名侦探柯南 计时引爆摩天楼.mp4".encode("utf-8"))
    text = _decode(raw)
    sc.check("驱动器" in text and "名侦探柯南" in text,
             "★ 混合编码按行解码（整段只挑一种必有一边变乱码）",
             text.replace("\n", " | "))


def _make_absolute_zip(zip_bytes: bytes, stub: int) -> bytes:
    """把 ZIP 里的偏移整体加上 stub，模拟"包内偏移是绝对位置"的那种伪装包。

    真样本 `[SLG…]2.88G.mp4` 就是这样：36 字节假 MP4 头 + 一整个 ZIP，而 EOCD 里的
    `cd_off` 恰好等于中央目录的**绝对**位置 → probe 反推出的 base=0 被判非法。
    这里手工改一遍：每条 CD 记录的"本地头偏移"和 EOCD 的 cd_off 都 +stub。
    """
    data = bytearray(zip_bytes)
    eocd = data.rfind(b"PK\x05\x06")
    cd_size = int.from_bytes(data[eocd + 12:eocd + 16], "little")
    cd_off = int.from_bytes(data[eocd + 16:eocd + 20], "little")
    pos = eocd - cd_size
    while pos < eocd and data[pos:pos + 4] == b"PK\x01\x02":
        off = int.from_bytes(data[pos + 42:pos + 46], "little")
        data[pos + 42:pos + 46] = (off + stub).to_bytes(4, "little")
        name_len = int.from_bytes(data[pos + 28:pos + 30], "little")
        extra_len = int.from_bytes(data[pos + 30:pos + 32], "little")
        cmt_len = int.from_bytes(data[pos + 32:pos + 34], "little")
        pos += 46 + name_len + extra_len + cmt_len
    data[eocd + 16:eocd + 20] = (cd_off + stub).to_bytes(4, "little")
    return bytes(data)


def test_engine_direct_read() -> None:
    """★ 假头 + 一整个包（包内偏移是绝对的）：probe 认不出来时靠"问引擎一句"兜底。"""
    print("\n== 引擎直读兜底（假头 + 整包）==")
    from core import probe as probe_mod

    work = os.path.join(WORK, "engine-direct")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work, exist_ok=True)
    big = os.path.join(work, "big.bin")
    with open(big, "wb") as f:
        f.write(random.Random(9).randbytes(16 << 20))
    inner = os.path.join(work, "inner.zip")
    subprocess.run([sc.SEVENZIP, "a", "-tzip", "-mx0", inner, big],
                   capture_output=True, creationflags=0x08000000)
    with open(inner, "rb") as f:
        raw = f.read()
    stub = 36
    fake = os.path.join(work, "伪装 2.88G.mp4")
    with open(fake, "wb") as f:
        f.write(b"\x00\x00\x00\x20ftypisom" + b"\x00" * (stub - 9))   # 假 MP4 头
        f.write(_make_absolute_zip(raw, stub))

    sc.check(probe_mod.detect_format(fake) is probe_mod.Fmt.MP4,
             "假头让它看着像 MP4", str(probe_mod.detect_format(fake)))
    sc.check(probe_mod.find_embedded(fake) is None,
             "★ probe 反推不出起点（base 算成 0，被合法性检查否掉）——正是真样本的形状")

    ex = Extractor()
    if ex.inspect(fake).read_ok:
        items = scan([fake])
        sc.check(len(items) == 1 and items[0].runnable,
                 "★ 还是认出来了、可执行", f"runnable={items[0].runnable} kind={items[0].kind}")
        sc.check("可直接读" in items[0].kind, "识别类型写明「7-Zip 可直接读」", items[0].kind)
        sc.check("不切包" in items[0].note, "备注说明不切包（切了会让包内绝对偏移整体错位）",
                 items[0].note)
    else:
        # 手工改偏移的合成样本 7-Zip 不一定认（它处理"前置垫片"的策略跟版本有关）；
        # 下面用真机上的真样本验同一条路，那个才是这个兜底存在的理由
        print("  [SKIP] 合成样本 7-Zip 读不出来（真样本能读，见下一条）")

    # 真机上有那个 2.88G 样本的话，顺手验一遍（只问引擎一句，0.03s）
    sample_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "素材目录")
    if os.path.isdir(sample_dir):
        hits = [n for n in os.listdir(sample_dir) if "2.88G" in n and n.lower().endswith(".mp4")]
        if hits:
            real = scan([os.path.join(sample_dir, hits[0])])
            sc.check(bool(real) and real[0].runnable,
                     "★ 真样本（2.88G）也认出来了", str([(i.kind, i.runnable) for i in real]))
            sc.check("可直接读" in real[0].kind,
                     "★ 真样本走的是「引擎直读」而不是切包", real[0].kind)

            # `--probe` 也得照实说：它以前只跑 probe 自己的探测，对这个真样本报
            # 「内嵌压缩包（扫过，没有）」——把能解的包说成不能解（用户就是这么被误导的）
            import contextlib
            import io

            from cli import print_probe

            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                print_probe(os.path.join(sample_dir, hits[0]))
            out = buf.getvalue()
            lines = [ln.strip() for ln in out.splitlines() if "内嵌" in ln]
            sc.check("引擎能直接读" in out and "扫过，没有" not in out,
                     "★ --probe 也照实说（引擎能直读，不说「扫过没有」）", str(lines))
        else:
            print("  [SKIP] 素材目录里没有 2.88G 那个样本")


def test_leftover_message() -> None:
    """★ 提前收工（层数上限）时必须说清"输出目录里还留着哪个包"。"""
    print("\n== 层数上限：残留 zip 要说清 ==")
    logs: list[str] = []
    src = sc.fx("嵌套.zip")
    r = Runner(
        scan([src]), vault=PasswordVault(), extractor=Extractor(),
        config=Config(min_free_gb=0.001, max_depth=1, output_mode="custom",
                      output_dir=os.path.join(WORK, "leftover-msg")),
        hooks=RunnerHooks(on_log=logs.append),
    )
    shutil.rmtree(os.path.join(WORK, "leftover-msg"), ignore_errors=True)
    res = r.run()
    joined = "\n".join(logs)
    sc.check("达到层数上限" in joined, "日志里点明是层数上限", joined[-200:])
    sc.check("留在输出目录" in joined,
             "★ 并说明输出目录里还留着哪个包（否则就像「显示完成却有残留 zip」的 bug）",
             joined[-200:])
    sc.check(any(it.stop_reason is not None for it in res), "任务照常有停止原因")


def test_one_bad_item_does_not_kill_batch() -> None:
    """★ 一个包处理时炸了，后面排队的**必须继续**（用户报过"整批停在那儿"）。

    以前 `Runner.run` 里没有任何 per-item 兜底：`_run_one` 抛出的任何异常都会一路
    冒到 `JobWorker.run` 的 except，那一批就此结束，剩下的停在"排队中"，
    用户得再点一次「开始」——这就是他报的现象。
    """
    print("\n== 一个包炸了不能带走整批 ==")
    d = os.path.join(WORK, "batch-survive")
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d, exist_ok=True)
    good1 = os.path.join(d, "好包A.zip")
    boom = os.path.join(d, "会炸的包.zip")
    good2 = os.path.join(d, "好包B.zip")
    for src, dst in ((sc.fx("嵌套.zip"), good1), (sc.fx("嵌套.zip"), boom),
                     (sc.fx("嵌套.zip"), good2)):
        shutil.copyfile(src, dst)

    from core import pipeline as pl

    orig_run = pl.Piercer.run

    def boom_run(self, path):                      # noqa: ANN001
        if "会炸的包" in str(path):
            raise RuntimeError("模拟引擎路径上的意外")
        return orig_run(self, path)

    pl.Piercer.run = boom_run                      # type: ignore[assignment]
    logs: list[str] = []
    try:
        items = scan([good1, boom, good2])
        Runner(
            items, vault=PasswordVault(), extractor=Extractor(),
            config=Config(min_free_gb=0.001, output_mode="custom",
                          output_dir=os.path.join(d, "out")),
            hooks=RunnerHooks(on_log=logs.append),
        ).run()
    finally:
        pl.Piercer.run = orig_run                  # type: ignore[assignment]

    statuses = {os.path.basename(i.path): i.status.value for i in items}
    sc.check(all(v != "queued" for v in statuses.values()),
             "★ 三项都被处理过（没有留在排队中等用户再点一次）", str(statuses))
    sc.check(statuses.get("会炸的包.zip") == "failed",
             "★ 炸掉的那项标失败", str(statuses))
    sc.check(any("会炸的包" in x and "出错" in x for x in logs),
             "★ 日志里写清是它出错，并且接着说继续跑后面的", "\n".join(logs[-6:]))
    sc.check(statuses.get("好包B.zip") == "done",
             "★ 排在它后面的包照样跑完了", str(statuses))


def main() -> int:
    print("== 造夹具（复用 smoke_core）==")
    if not sc.SEVENZIP:
        print("!! 没找到 7z.exe")
        return 1
    if not os.path.isdir(FIX) or not os.path.isfile(sc.fx("示例包.zip")):
        sc.build_fixtures()
    os.makedirs(WORK, exist_ok=True)

    test_config()
    test_scan()
    test_runner()
    test_embedded()
    test_engine_details()
    test_engine_direct_read()
    test_leftover_message()
    test_one_bad_item_does_not_kill_batch()
    test_output_targets()
    test_shellmenu()
    test_run_log()
    test_log_levels_and_masking()
    test_paths_and_identity()
    test_vault_batch_remove()
    test_cancel()
    test_pause()

    passed = sum(1 for ok, _, _ in sc.results if ok)
    total = len(sc.results)
    print(f"\n===== {passed}/{total} 通过 =====")
    if passed != total:
        print("失败项：")
        for ok, name, detail in sc.results:
            if not ok:
                print(f"  - {name}  {detail}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
