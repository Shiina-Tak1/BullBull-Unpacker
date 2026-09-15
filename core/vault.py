"""密码本：**一个文件、一个列表、按成功次数排序**。

尝试顺序（固定，不可配置）：

    1. 文件名里直接读出来的      —— 免费、精确，而且**静默**（不是给用户调的开关）
    2. 密码本                    —— 按「成功次数」从多到少试
    3. 空密码                    —— 兜底
    （全都不中 → 弹窗让用户手输；输了而且解压成功，就写回密码本、次数 +1）

密码本.txt 长这样（一行一个密码，`#` 注释）：

    # 密码本 —— 一行一个密码，成功过的会排到前面。
    123456
    abc123	3
    mypass888

* 只写密码就行，后面的次数由工具自己维护（TAB 分隔）。
* 文件里的顺序 = 尝试顺序，打开文件就能看懂会先试哪些。
* 你新加的密码从 0 次开始，所以会排在成功过的后面。

为什么只有一本：早先设计过分两段（"记住的"/"我加的"），但那只回答了
"这个密码是怎么来的"——对用户来说没有用，还多背一个概念。
真正决定顺序的是"哪个更可能管用"，也就是成功次数。来源不重要，结果才重要。

兼容：旧版那种带 `[记住的密码]` / `[我添加的密码]` 分段标记的文件会被直接读成
一个列表（标记行忽略）；旧的「临时密码本.txt」若存在，其中的密码会被并入本文件，
原文件改名成 .bak。
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from enum import Enum, IntEnum

from core import naming

DEFAULT_BOOK = "密码本.txt"
LEGACY_TEMP_BOOK = "临时密码本.txt"

# 密码本最多留多少条（纯防御，正常用不到）
BOOK_LIMIT = 500


class Origin(IntEnum):
    """密码来源，同时也是尝试顺序。"""

    FILENAME = 0     # 文件名/文件夹名里读出来的
    BOOK = 1         # 密码本里的
    EMPTY = 2        # 空密码
    MANUAL = 3       # 本次由用户手动输入（不进候选，只用于显示来源）

    @property
    def label(self) -> str:
        return {
            Origin.FILENAME: "文件名",
            Origin.BOOK: "密码本",
            Origin.EMPTY: "空密码",
            Origin.MANUAL: "手动输入",
        }[self]


@dataclass
class BookEntry:
    """密码本里的一条。"""

    password: str
    hits: int = 0          # 成功次数，决定它排多前面


@dataclass(frozen=True)
class PasswordCandidate:
    """一个待尝试的密码。"""

    value: str
    origin: Origin
    detail: str = ""

    def describe(self) -> str:
        # 本机自用工具，密码一律明文显示，不打码
        shown = self.value if self.value else "（空密码）"
        return f"{self.origin.label}：{shown}" + (f"（{self.detail}）" if self.detail else "")


class Problem(str, Enum):
    """试密码失败的原因——决定上层该不该再问用户要密码。"""

    EXHAUSTED = "exhausted"        # 密码全试完 → 只可能是密码不对，值得问用户
    ENGINE_ERROR = "engine_error"  # 包损坏 / 引擎缺失 → 问用户也没用
    MISSING_VOLUME = "missing_volume"  # 分卷不全 → 问用户也没用，去把分卷凑齐
    TIMEOUT = "timeout"
    CANCELLED = "cancelled"        # 用户点了停止

    @property
    def label(self) -> str:
        return {
            Problem.EXHAUSTED: "密码已全部试完",
            Problem.ENGINE_ERROR: "引擎报错（不是密码问题）",
            Problem.MISSING_VOLUME: "分卷不全（不是密码问题）",
            Problem.TIMEOUT: "验证超时",
            Problem.CANCELLED: "用户中止",
        }[self]


# --------------------------------------------------------------------------
# 文件读写
# --------------------------------------------------------------------------


def read_text(path: str) -> str:
    """读文本，容忍 UTF-8 / GBK / UTF-8-BOM 混用（密码本是手编的，编码很杂）。"""
    with open(path, "rb") as f:
        raw = f.read()
    for enc in ("utf-8-sig", "utf-8", "cp936", "latin-1"):
        try:
            return raw.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", errors="replace")


def parse_book(text: str) -> list[BookEntry]:
    """解析密码本 → 条目列表（保持文件里的顺序，重复的合并次数）。

    每行：`密码` 或 `密码<TAB>成功次数`。
    旧格式的分段标记（`[记住的密码]` 之类）会被忽略，所以老文件直接能用。
    """
    entries: list[BookEntry] = []
    index: dict[str, BookEntry] = {}

    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or line.startswith("["):
            continue

        hits = 0
        pw = line
        if "\t" in line:
            head, _, tail = line.partition("\t")
            tail = tail.strip()
            if tail.isdigit():
                pw, hits = head.strip(), int(tail)
            else:
                pw = head.strip()

        # 行内若含竖线/逗号/全角冒号，只取第一段（有人习惯顺手写备注）
        for sep in ("|", "，", "：", ":"):
            if sep in pw:
                pw = pw.split(sep, 1)[0].strip()

        if not pw:
            continue

        if pw in index:
            # 同一个密码写了两遍：次数取大的，位置保留先出现的
            index[pw].hits = max(index[pw].hits, hits)
            continue
        entry = BookEntry(pw, hits)
        index[pw] = entry
        entries.append(entry)

    return entries


def sort_entries(entries: list[BookEntry]) -> list[BookEntry]:
    """按成功次数降序；次数相同的保持文件里的顺序（稳定排序）。"""
    return sorted(entries, key=lambda e: -e.hits)


def render_book(entries: list[BookEntry]) -> str:
    """渲染整个密码本文件（按尝试顺序写出去，文件顺序 = 尝试顺序）。"""
    head = [
        "# 密码本 —— 一行一个密码，工具会按「成功次数」从多到少依次尝试。",
        "#",
        "#   只写密码就行，后面的次数是工具自己维护的（TAB 分隔）。",
        "#   你新加的密码从 0 次开始，所以会排在成功过的后面。",
        "#",
        "# 顺序固定为：文件名里带的密码 → 密码本 → 空密码，不需要你调。",
        "",
    ]
    body = []
    for e in sort_entries(entries):
        body.append(f"{e.password}\t{e.hits}" if e.hits else e.password)
    return "\n".join(head + body) + "\n"


# --------------------------------------------------------------------------
# 统计
# --------------------------------------------------------------------------


@dataclass
class VaultStats:
    filename_hits: int = 0
    book_hits: int = 0
    empty_hits: int = 0
    manual_hits: int = 0

    def bump(self, origin: Origin) -> None:
        attr = {
            Origin.FILENAME: "filename_hits",
            Origin.BOOK: "book_hits",
            Origin.EMPTY: "empty_hits",
            Origin.MANUAL: "manual_hits",
        }.get(origin)
        if attr:
            setattr(self, attr, getattr(self, attr) + 1)

    def summary(self) -> str:
        return (
            f"文件名 {self.filename_hits} · 密码本 {self.book_hits} · "
            f"空密码 {self.empty_hits} · 手输 {self.manual_hits}"
        )


# --------------------------------------------------------------------------
# 密码本
# --------------------------------------------------------------------------


class PasswordVault:
    """一本密码 + 候选生成 + 成功计数。"""

    def __init__(self, *, book: str | None = None, legacy_temp: str | None = None) -> None:
        self.book_path = book
        self.legacy_temp_path = legacy_temp
        self.entries: list[BookEntry] = []
        self.stats = VaultStats()
        self.last_write_error: str = ""

    # -- 载入 ----------------------------------------------------------

    @classmethod
    def from_dir(cls, base_dir: str, **kw) -> PasswordVault:
        v = cls(
            book=os.path.join(base_dir, DEFAULT_BOOK),
            legacy_temp=os.path.join(base_dir, LEGACY_TEMP_BOOK),
            **kw,
        )
        v.reload()
        return v

    def reload(self) -> None:
        """从磁盘重读（文件可能在外部被编辑过）。"""
        self.entries = []
        if self.book_path and os.path.isfile(self.book_path):
            self.entries = parse_book(read_text(self.book_path))
        self._merge_legacy_temp()

    def _merge_legacy_temp(self) -> None:
        """把旧「临时密码本」里的密码并进来，然后改名退役。"""
        p = self.legacy_temp_path
        if not p or not os.path.isfile(p):
            return
        try:
            text = read_text(p)
        except OSError:
            return

        known = {e.password for e in self.entries}
        moved = 0
        for raw in text.splitlines():
            line = raw.strip()
            if not line or line.startswith("#") or line.startswith("["):
                continue
            parts = [x for x in line.replace("|", "\t").split("\t") if x.strip()]
            pw = parts[-1].strip() if parts else ""
            if pw and pw not in known:
                self.entries.append(BookEntry(pw, 0))
                known.add(pw)
                moved += 1
        try:
            os.replace(p, p + ".bak")
        except OSError:
            pass
        if moved:
            self.save()

    # -- 保存 ----------------------------------------------------------

    def save(self) -> bool:
        if not self.book_path:
            return False
        try:
            os.makedirs(os.path.dirname(self.book_path) or ".", exist_ok=True)
            tmp = self.book_path + ".tmp"
            with open(tmp, "w", encoding="utf-8", newline="\n") as f:
                f.write(render_book(self.entries))
            os.replace(tmp, self.book_path)   # 原子替换，写一半断电也不会毁掉密码本
        except OSError as exc:
            self.last_write_error = str(exc)
            return False
        self.last_write_error = ""
        return True

    # -- 供 UI 用 ------------------------------------------------------

    def size(self) -> dict[str, int]:
        return {"total": len(self.entries)}

    def set_entries(self, passwords: list[str]) -> None:
        """直接设定内容（测试用，不落盘）。"""
        self.entries = [BookEntry(p, 0) for p in passwords if p]

    def find(self, password: str) -> BookEntry | None:
        return next((e for e in self.entries if e.password == password), None)

    def add(self, password: str) -> bool:
        """用户手动加一条，次数从 0 开始，立刻落盘。"""
        pw = (password or "").strip()
        if not pw or self.find(pw):
            return False
        self.entries.append(BookEntry(pw, 0))
        del self.entries[BOOK_LIMIT:]
        return self.save()

    def remove(self, password: str) -> bool:
        before = len(self.entries)
        self.entries = [e for e in self.entries if e.password != password]
        if len(self.entries) == before:
            return False
        return self.save()

    def remove_many(self, passwords: list[str]) -> int:
        """一次删一串（密码本页多选删除用）。返回真删掉的条数。

        整批只落盘一次：逐条 `remove` 会写 N 次文件，密码本上千条时能明显卡一下，
        而且中途失败会留下"删了一半"的状态。

        注意空密码：`password` 为空串是**合法条目**（"试试不加密"那条），
        所以判等时不能写成 `if password` 那种"空值就跳过"。
        """
        wanted = list(dict.fromkeys(passwords))          # 去重、保序
        if not wanted:
            return 0
        victims = set(wanted)
        before = len(self.entries)
        self.entries = [e for e in self.entries if e.password not in victims]
        removed = before - len(self.entries)
        if not removed:
            return 0
        if self.book_path and not self.save():
            # 落盘失败：把内存状态退回去，别让界面显示的和文件里的不一致
            self.reload()
            return 0
        return removed

    # -- 记忆（飞轮） --------------------------------------------------

    def remember(self, password: str) -> bool:
        """记一次成功：次数 +1 并落盘。没有的就新建一条。"""
        pw = (password or "").strip()
        if not pw:
            return False
        entry = self.find(pw)
        if entry is None:
            self.entries.append(BookEntry(pw, 1))
            del self.entries[BOOK_LIMIT:]
        else:
            entry.hits += 1
        return self.save()

    # -- 候选生成 ------------------------------------------------------

    def candidates_for(self, archive_path: str) -> list[PasswordCandidate]:
        """给一个压缩包生成「按顺序排好、已去重、已展开变体」的密码序列。"""
        raw: list[PasswordCandidate] = []

        # 1) 文件名 / 文件夹名（静默，界面不把它当可调项）
        guess = naming.extract_from_path(archive_path)
        if guess:
            raw.append(PasswordCandidate(guess.value, Origin.FILENAME, guess.source))

        # 2) 密码本：成功次数多的先试，次数相同的保持文件顺序
        for entry in sort_entries(self.entries):
            raw.append(
                PasswordCandidate(
                    entry.password, Origin.BOOK,
                    f"成功过 {entry.hits} 次" if entry.hits else "",
                )
            )

        # 3) 空密码（兜底）
        raw.append(PasswordCandidate("", Origin.EMPTY, ""))

        return self._dedup_expand(raw)

    @staticmethod
    def _dedup_expand(raw: list[PasswordCandidate]) -> list[PasswordCandidate]:
        """去重（保留最靠前的来源）+ 变体展开（保持顺序）。"""
        seen: set[str] = set()
        out: list[PasswordCandidate] = []
        for cand in raw:
            for v in naming.variants(cand.value) or [cand.value]:
                if v in seen:
                    continue
                seen.add(v)
                detail = cand.detail
                if v != cand.value:
                    detail = (detail + " · 变体").strip(" ·")
                out.append(PasswordCandidate(v, cand.origin, detail))
        return out


# --------------------------------------------------------------------------
# 与引擎结合：真正去试
# --------------------------------------------------------------------------


@dataclass
class UnlockResult:
    """解锁结果，直接可以喂给 UI 的详情页。"""

    ok: bool
    password: str | None = None
    candidate: PasswordCandidate | None = None
    tried: list[PasswordCandidate] = field(default_factory=list)
    stopped_reason: str = ""
    problem: Problem | None = None

    @property
    def tries(self) -> int:
        return len(self.tried)

    @property
    def worth_asking_user(self) -> bool:
        """只有「密码全试完」才值得弹窗让用户手输。

        缺分卷、包损坏这类问题，输多少个密码都没用——弹窗只会让人白忙，
        在无头/自动化场景下更会直接把流程挂死。
        """
        return not self.ok and self.problem is Problem.EXHAUSTED

    def summary(self) -> str:
        if self.ok and self.candidate:
            shown = self.candidate.value or "（空密码）"
            return f"命中 {self.candidate.origin.label}：{shown}（试了 {self.tries} 个）"
        return f"试完 {self.tries} 个密码仍未命中：{self.stopped_reason}"


def unlock(
    vault: PasswordVault,
    extractor,
    archive_path: str,
    *,
    kind=None,
    skip: set[str] | None = None,
    max_tries: int | None = None,
    entry: str | None = None,
    on_try=None,
) -> UnlockResult:
    """按固定顺序逐个验证密码，命中即停。

    这里刻意用 `test`（不是 `extract`）——密码验证通过后才值得真正解压，
    避免"密码错→解压到一半→清垃圾"的浪费。

    `entry` 传第一个条目的路径时**只验证那一个**：对 11GB 的分卷 7z，
    逐个试密码如果把整包读一遍是不可接受的。

    `skip` 用于跳过调用方已经确认无效的密码（例如外层复用的密码试过了不对）。
    """
    candidates = vault.candidates_for(archive_path)
    if skip:
        candidates = [c for c in candidates if c.value not in skip]
    if max_tries is not None:
        candidates = candidates[:max_tries]

    tried: list[PasswordCandidate] = []
    for cand in candidates:
        if extractor.needs_manual(cand.value):
            # 密码含引号，命令行无法可靠传递，别硬刚
            continue
        # 用 verify 而不是 test：文件名也加密的包只要读文件头就能判定密码，
        # 对 11GB 的分卷 7z 而言，这是「读几十 KB」和「读 11GB」的区别
        res = extractor.verify(archive_path, cand.value, kind=kind)
        tried.append(cand)
        if on_try is not None:
            try:
                on_try(cand, res)
            except Exception:
                pass
        if res.ok:
            vault.stats.bump(cand.origin)
            return UnlockResult(True, cand.value, cand, tried)
        if res.cancelled:
            return UnlockResult(False, None, None, tried, "用户中止", Problem.CANCELLED)
        if res.timed_out:
            return UnlockResult(False, None, None, tried, "验证超时，已中止", Problem.TIMEOUT)
        # 非密码错误（缺分卷 / 包损坏 / 引擎缺失）就没必要继续试了，
        # 而且要带上引擎自己的输出，否则用户只看到"失败"却不知道为什么
        if not res.wrong_password:
            detail = res.tail.splitlines()[-1] if res.tail else res.brief()
            return UnlockResult(
                False, None, None, tried,
                f"引擎报错（{res.brief()}）：{detail}",
                Problem.ENGINE_ERROR,
            )

    reason = "密码已全部试完"
    if not candidates:
        reason = "密码本是空的（也没有文件名密码可提取）"
    # 分卷不全的包：别把"缺分卷"说成"密码试完了"——那会让界面弹一个白问的密码框
    try:
        from core import probe as _probe

        missing = _probe.missing_volume_note(archive_path)
    except Exception:              # noqa: BLE001 - 判不了就当没有
        missing = None
    if missing:
        return UnlockResult(
            False, None, None, tried,
            f"缺分卷：找不到 {missing}，先把分卷凑齐再解",
            Problem.MISSING_VOLUME,
        )
    return UnlockResult(False, None, None, tried, reason, Problem.EXHAUSTED)
