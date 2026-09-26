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

文件格式：一行一个密码（`密码` 或 `密码<TAB>成功次数`）。明文一行表达不了的写法
（`#` 开头、有首尾空格、含 TAB 或换行）写成 `\\B64:<base64>` 那一行，读回来一模一样。
"""

from __future__ import annotations

import base64
import concurrent.futures
import contextlib
import os
import secrets
import time
from dataclasses import dataclass, field
from enum import Enum, IntEnum

from core import naming

try:                                    # 密码本的写者锁是 Windows 专有的（项目也只在 Windows 上跑）
    import msvcrt
except ImportError:                     # pragma: no cover - 非 Windows 上退化成"不锁"
    msvcrt = None                       # type: ignore[assignment]

DEFAULT_BOOK = "密码本.txt"

# 密码本"条目偏多"的提醒线：
BOOK_SOFT_WARN = 10000

# 找密码时的"心跳"间隔（秒）。
# 为什么要有它：单个候选只要几十毫秒，引擎层那套心跳（`engine._run` 每 5 秒一句）永远轮不上，
# 于是"整轮搜索要几分钟"这件事在界面上一点动静都没有（用户报过：修掉假心跳之后就没有任何提示了）。
# 这里按"整轮搜索"计时：每 ≥5 秒报一句，带上**这一层的包已经找了多久**和**已试 x/y**。
SEARCH_HEARTBEAT_SECONDS = 5.0

# ── 写密码本的并发约定（`B-2026-056`）────────────────────────────────────────
# 为什么需要它：单实例互斥体是 `Local\bbu-unpacker-single`（`core/single.py`），
# **每登录会话一个** —— 两个 Windows 账号同时在线时各成主实例，会同时写同一本密码。
# 实测（两个写者各 120 次）：盘上 AAA 只剩 12 条、BBB 120 条（后写者**整本覆盖**）。
#
# 三件事一起才够：
#   ① 写者锁（`<密码本>.lock`，见 `PasswordVault._book_lock`）—— 两次 save 不互相踩；
#   ② **唯一 tmp 名**（pid + 随机串）—— 固定 `<book>.tmp` 时两个写者会同时写同一个文件
#      （实测 save 失败 AAA 66 次 / BBB 69 次，全是 WinError 5 / Errno 13）；
#   ③ 写盘前的**三方合并**（见 `PasswordVault._merge_from_disk`）—— 光有锁不够：
#      丢更新发生在"读-改-写"这一层，两个实例各持一份内存副本时，后写的会整本覆盖。
LOCK_SUFFIX = ".lock"
# 等锁的上限（秒）。密码本的 save 只要几毫秒，10 秒足够；**超时后照样写**
# （宁可留一点并发风险，也不能让一个卡住的进程把密码本变成永远写不进去）。
LOCK_WAIT_SECONDS = 10.0
# 读密码本失败时的重试次数 / 间隔：并发 `os.replace` 期间实测有 48/36623 次直接
# `Permission denied`，撞上就是未捕获异常（`reload()` 的 `read_text` 以前没有 try/except）。
READ_RETRIES = 5
READ_RETRY_SLEEP = 0.05
# 写密码本时的重试次数 / 间隔。为什么必须重试：Windows 上 `os.replace` 要拿到目标文件的
# DELETE 权限，而**任何别的进程正打开着这个密码本**（另一个实例的 reload、用户的记事本）
# 都会让它报 sharing violation（`Permission denied`）—— 实测把"反复 reload 的读者"
# 和写者放一起跑，3 个写者各 30 条里就有 6~10 次写失败、最终丢了 25/90 条。
# 那不是"密码本写不进去"，只是"此刻有人在读"。
WRITE_RETRIES = 10
WRITE_RETRY_SLEEP = 0.05


# "行格式表达不了"的密码（空、首尾空格、含换行/TAB、以 #/[ 开头、含上面的分隔符）
# 用这个前缀 + base64 存。普通密码照旧是明文一行，人还能拿记事本直接改。
RAW_PREFIX = "\\\\B64:"

# 「明文一行」写不出去的字符（B-2026-051）。
# 判据不是"看着像换行"，而是**读回来时 `str.splitlines()` 会在哪儿断行** ——
# 写侧与读侧必须是同一个字符集，否则一条密码写出去、读回来会裂成两条（不可逆）。
#   * `\n` / `\r`（含 `\r\n`）：splitlines 的经典断点；
#   * `\v \f \x1c \x1d \x1e \x85 \u2028 \u2029`：Python 的 splitlines **同样**在这 8 个
#     字符上断行（VT/FF/FS/GS/RS/NEL/LS/PS）。用户从网页或 PDF 复制密码时，
#     U+2028 与 NEL 是真会带进来的 —— QLineEdit 不过滤，粘贴即可。
#   * `\t`：splitlines 不断，但 `parse_book()` 把 TAB 当"次数"分隔符 → 照样会丢信息。
# 首尾出现这些字符时 `password != password.strip()` 已经拦住了；这里管的是**中间位置**。
_LINE_BREAK_CHARS = "\r\n\t\v\f\x1c\x1d\x1e\x85\u2028\u2029"

# 说明段与密码段的**分界行**（2026-09-21 加）。
# 这行**以上**是给人看的说明（整段忽略）；这行**以下**每一行都是密码，
# `#` 开头的也是密码 —— 否则用户手写一行 `#我的密码` 会被当注释静默丢掉。
# 老文件里没有这一行 → 解析退回"`#` 开头当注释"的老行为，不用迁移。
COMMENT_MARKER = "# ==== 密码从这里开始（这一行以下都是密码，# 开头的也算） ===="

# 「工具说明」与「用户自己的备注」的分界行（2026-09-21 加，配合上面那条）。
# 工具说明（上面那十几行）每次保存都会重写，**用户别在那儿写东西**；
# 这一行到 `COMMENT_MARKER` 之间是**用户备注区**：写什么都会被原样保留、也不会被当密码。
# 为什么要它：老密码本升级成新格式时要重写文件，而老文件里"用户手写的 `#` 行"
# 没法区分是备注还是想当密码 —— 有了备注区就能**先搬进来保住**，一条都不丢。
NOTES_MARKER = "# ==== 以上是工具说明（每次保存会重写）；以下可以写你自己的备注 ===="

# BOM → 解码器。**长的排在前面**：UTF-32LE 的 BOM 前两字节与 UTF-16LE 相同（FF FE），
# 先判 UTF-16LE 就会把 UTF-32LE 读坏。UTF-8 的 BOM 不列在这儿 —— 回退链第一条
# `utf-8-sig` 已经管了，列进来等于重复处理。
BOM_ENCODINGS: tuple[tuple[bytes, str], ...] = (
    (b"\xff\xfe\x00\x00", "utf-32"),     # UTF-32LE —— 必须排在 UTF-16LE 前面
    (b"\x00\x00\xfe\xff", "utf-32"),     # UTF-32BE
    (b"\xff\xfe", "utf-16"),             # UTF-16LE（记事本「Unicode」）
    (b"\xfe\xff", "utf-16"),             # UTF-16BE
)


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
    # ★ 这条候选**是从哪一条变体展开出来的**（`B-2026-057`）。
    #   为什么必须有它：`naming.variants()` 会把用户写的一条密码展开成好几条
    #   （去空格、全角转半角…），命中变体时如果拿**变体值**去 `remember()`，
    #   密码本里就会多出一条"用户从没写过"的条目、而他原来那条永远是 0 次
    #   （实测：本里只有 `abc 123`、真实密码 `abc123` → 盘上变成
    #   `[('abc123',1), ('abc 123',0)]`）。飞轮要转的是**原条目**。
    #   空串 = "这条就是原文"（非变体），`remember()` 时退回 `value`。
    source_value: str = ""

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
            Problem.CANCELLED: "已停止",
        }[self]


# --------------------------------------------------------------------------
# 文件读写
# --------------------------------------------------------------------------


def read_text(path: str) -> str:
    """读文本，容忍 UTF-8 / GBK / UTF-16 / UTF-32 / UTF-8-BOM 混用（密码本是手编的，编码很杂）。

    **先按 BOM 判**（记事本存"Unicode"就是 UTF-16LE 带 BOM），再走编码回退链，
    回退链**最后**才轮到"无 BOM 的 UTF-16/32 探测"（`_guess_utf16`）。
    为什么必须这样：回退链最后一个 `latin-1` 能解码**任意**字节流，所以 UTF-16 的文件
    会被它"若无其事地解成功"，永远走不到报错分支 —— 结果是密码全变乱码，还被写回文件
    （用户报的"UTF-16 密码本读出来是乱码"）。
    ⚠ **只判 BOM 是不够的**（2026-09-21 补，攻击审计 BUG-5 的残余）：没有 BOM 的
    UTF-16LE/BE、UTF-32LE/BE 读出来依旧是乱码，所以才有 `_guess_utf16` 那一步。

    ⚠ 两条容易写错的：
      * **UTF-32LE 的 BOM 是 `FF FE 00 00`，前两个字节和 UTF-16LE 一模一样** →
        `BOM_ENCODINGS` 里 UTF-32 必须排在 UTF-16 **前面**，否则 UTF-32 被误判成 UTF-16。
      * 必须用带 BOM 的解码器 `utf-16` / `utf-32`，**不能用 `utf-16-le` / `utf-32-le`**：
        后者会把 BOM 解成开头的 `\\ufeff`，于是第一行变成 `\\ufeff# 密码本 …`，
        界面判"这是不是本程序密码本"（`startswith("# 密码本")`）就判错。
        （`_guess_utf16` 返回的**正是** `-le` 变体，但那条只在"完全没有 BOM"时才走到，
        此时没有 BOM 可解，所以不冲突。）
    """
    with open(path, "rb") as f:
        raw = f.read()
    for bom, enc in BOM_ENCODINGS:
        if raw.startswith(bom):
            try:
                return raw.decode(enc)
            except UnicodeDecodeError:
                break               # BOM 在但内容坏了：别再猜，交给下面的回退链
    # ★ NUL 字节 = "这大概率是 UTF-16/32"的**结构性信号**，必须排在单字节编码**前面**。
    #   为什么：`cp936` / `latin-1` 对 UTF-16 的字节流也能"成功"解出一堆乱码
    #   （实测：UTF-16LE 被 cp936 吃下去、UTF-16 无 BOM 被 latin-1 吃下去），
    #   所以只要它们排前面，就永远走不到真正的答案 —— 用户看到的是密码全变乱码，
    #   而且会被写回文件（攻击审计 BUG-5 的本体）。
    #   ASCII 文本按 UTF-16 存，字节流里必定有 NUL；正常 UTF-8/GBK 文本没有 NUL
    #   （DBCS 的每个字节都非零），所以这条信号不会误伤它们。
    if b"\x00" in raw:
        # ★ 先排除"单字节文本里混进零星 NUL"这一种（`B-2026-054`）：
        #   整份字节流本来就是合法的 UTF-8/GBK，而 NUL 只占极少数 —— 那它就是
        #   **单字节文本**，绝不该拿它去猜宽编码。错宽度的猜测会把整本读成乱码
        #   （实测 `read_text(b'aaa\x00') -> '慡a'`：`b'aaa\x00'` 正好是 4 字节、
        #   按 utf-16-le 解出来是「合法的中日韩汉字」，`_looks_like_text` 全绿），
        #   之后任意 add/remember/remove 的 `save()` 都会把乱码按 UTF-8 重写，
        #   **原字节不可逆消失**。
        #
        #   判据为什么是"占比"而不是"有没有 NUL"：真正的宽编码文本，NUL 是**结构性**的 ——
        #   UTF-16 存 ASCII 文本时字节流里约一半是 NUL（`a\0b\0c\0`），UTF-32 约四分之三。
        #   实测的四种正确行为都必须保住：`b'a\x00b\x00' -> 'ab'`（NUL 占 1/2）、
        #   `b'\x00\x00' -> '\x00\x00'`（占 1）、`b'aaaa\x00' -> 'aaaa\x00'`（占 1/5）、
        #   以及 8 种编码的同一本密码本 read_ok 全 True。
        if raw.count(0) * 3 < len(raw):
            for enc in ("utf-8-sig", "utf-8", "cp936"):
                try:
                    return raw.decode(enc)
                except (UnicodeDecodeError, LookupError):
                    continue
        for enc in _ordered_utf_candidates(raw):
            if len(raw) % (2 if "16" in enc else 4):
                continue
            try:
                text = raw.decode(enc)
            except (UnicodeDecodeError, LookupError):
                continue
            if _looks_like_text(text):
                return text
        # 认不出来又确实有 NUL：这不是文本文件（二进制 / 没见过的编码）。
        # **不许**硬解成 latin-1 把用户数据读成乱码 —— 把替换字符摆出来让他看得见。
        return raw.decode("utf-8", errors="replace")
    for enc in ("utf-8-sig", "utf-8", "cp936"):
        try:
            return raw.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("latin-1")


# 文本里允许出现的控制字符：换行/回车/制表（密码本按行存，这三个是格式的一部分）。
_TEXT_CTRL = frozenset("\r\n\t")


def _ordered_utf_candidates(raw: bytes) -> tuple[str, ...]:
    """按"字节序有多像"排一下无 BOM 的 UTF-16/32 候选，最像的排最前。

    两层判据，都是**纯结构**的、不依赖内容语言：

    ① **字节序**：看 NUL 落在哪些字节位置上 ——
       UTF-16LE 低字节在前，ASCII 字符的 NUL 在**奇数**位；UTF-16BE 则在**偶数**位。
       为什么必须有这一条（实测踩过）：UTF-16**BE** 的字节流（`\\x00a\\x00b…`）按
       UTF-16**LE** 解会解成一串**合法的中日韩汉字**（`愀戀挀…`），
       `_looks_like_text` 全绿 —— 光靠"解出来像文本"会把错字节序当成答案。
    ② **宽度**：UTF-16 排在 UTF-32 前面。判"该多宽"没有可靠的结构判据
       （UTF-32 的 NUL 密度只是 UTF-16 的两倍，而"尾相位是否也有 NUL"两者都可能成立），
       所以按**实际概率**排：密码本是手编辑的文本文件，记事本/导出工具给的
       UTF-32 极少，UTF-16 是主流。代价是"UTF-32 文本被读成 UTF-16 的 CJK 乱码"
       仍然存在 —— 但那是**下一个**精度问题，不该为了它把主流场景排到后面。
    """
    at_odd = sum(1 for i in range(1, len(raw), 2) if raw[i] == 0)
    at_even = sum(1 for i in range(0, len(raw), 2) if raw[i] == 0)
    sixteen = ("utf-16-le", "utf-16-be") if at_odd >= at_even else ("utf-16-be", "utf-16-le")

    # UTF-32 的字节序同样按"零字节在第 1/2/3 相位还是第 0 相位"判
    phases = [sum(1 for i in range(p, len(raw), 4) if raw[i] == 0) for p in range(4)]
    thirty_two = ("utf-32-le", "utf-32-be") if sum(phases[1:]) >= phases[0] \
        else ("utf-32-be", "utf-32-le")

    return sixteen + thirty_two


def _looks_like_text(text: str) -> bool:
    """解出来的东西像不像"文本"？用于给无 BOM 的 UTF-16/32 候选当**验收闸门**。

    判据（都是"解错宽度/错字节序必然违反"的硬条件，不靠比例阈值猜）：
      * 不许有 NUL —— NUL 是解码错字节序/错宽度的**确定信号**；
      * 不许有除 CR/LF/TAB 之外的控制字符 —— 错宽度解出来的往往满屏 `\\x01`；
      * 不许有 U+FFFD（替换字符）—— 解坏了的兜底产物。

    为什么要真解一遍再验：只按"字节长度能被 4 整除"去猜 UTF-32 会把 **UTF-16 误判成
    UTF-32**（实测踩过：28 字节的 UTF-16 文本被猜成 utf-32-le，`decode` 侥幸不抛错，
    于是绕过回退链直接返回乱码）。
    """
    if "\x00" in text or "\ufffd" in text:
        return False
    return not any(ch < " " and ch not in _TEXT_CTRL for ch in text)


def needs_encode(password: str) -> bool:
    """这个密码用"明文一行"写出去会不会丢信息？会，就得走 base64 那一行。

    用户给什么我们就存什么（不替用户判断"这密码合不合规矩"），所以这里只是
    在问"文件格式表达得了吗"：表达不了就换个写法存，而不是把它丢掉/改样。
    """
    if not password:
        return True
    if password != password.strip():
        return True                     # 首尾空格：解析时会 strip
    if password.startswith(RAW_PREFIX):
        # ★ 密码**本身**就长得像标记行：不强制编码的话，它会以明文写出去，
        #   读回来时被 decode_raw_line() 当成编码行解开 → 静默变成另一个值。
        #   再套一层 base64 之后，读回来还是原来那个字面量（行格式仍只有一层）。
        return True
    if password[0] == "#":
        # `#` 开头：现在标记以下本来就能明文存，但**保留**这条是为了**向前兼容** ——
        # 写成 `\\B64:` 行之后，1.1.0 之前的旧版本程序也读得懂（旧版会把明文 `#` 行
        # 当注释跳过，等于把用户的密码丢了）。
        return True
    # ★ 判据必须与读侧 `parse_book()` 的 `text.splitlines()` **对称**（B-2026-051）：
    #   splitlines 不只在 \n / \r 断行，还在 \v \f \x1c \x1d \x1e \x85 \u2028 \u2029
    #   这 8 个字符上断行 —— 漏判任何一个，一条密码读回来就裂成两条（不可逆）。
    #   字符集只此一份：`_LINE_BREAK_CHARS`（\t 也在里面，因为 TAB 是次数分隔符）。
    return any(ch in password for ch in _LINE_BREAK_CHARS)


def encode_raw_line(password: str) -> str:
    """把密码编成 `\\\\B64:<base64>`（次数由调用方按需补在 TAB 后面）。"""
    return RAW_PREFIX + base64.b64encode(password.encode("utf-8")).decode("ascii")


def decode_raw_line(line: str) -> tuple[str, int] | None:
    """`\\\\B64:<base64>[\t次数]` → (密码, 次数)。

    不是"我们自己写的规范编码"就返回 None（照旧当普通行处理）——
    这样用户手写一行 `\\\\B64:xxxx` 当密码也不会被误读。
    """
    body = line[len(RAW_PREFIX):]
    b64, _, tail = body.partition("\t")
    try:
        pw = base64.b64decode(b64, validate=True).decode("utf-8")
    except Exception:                   # noqa: BLE001 - 解不开就当普通行
        return None
    if base64.b64encode(pw.encode("utf-8")).decode("ascii") != b64:
        return None
    return pw, int(tail) if tail.strip().isdigit() else 0


def parse_book(text: str) -> list[BookEntry]:
    """解析密码本 → 条目列表（保持文件里的顺序，重复的合并次数）。

    **注释段的边界靠 `COMMENT_MARKER` 那行划**（2026-09-21 起）：
      * 标记行**以上**是说明（整段跳过，不管写的是什么）；
      * 标记行**以下**每一行都是密码 —— **`#` 开头的也算密码**，不再当注释。
    为什么要这样：以前"`#` 开头一律当注释"，于是用户手写一行 `#我的密码`
    会被**静默丢掉**，而且下次保存连文件里那行都没了（用户报的）。
    标记把"说明"和"数据"分开之后，这种密码就能正常读回来。

    **没有标记的老文件**（1.1.0 之前写的、或者用户自己拿记事本拼的清单）：
    退回老行为 —— `#` 开头当注释跳过。所以老文件照常能读，不用迁移。

    每行格式：`密码` 或 `密码<TAB>成功次数`；`\\\\B64:` 行是 base64 存的密码。
    **TAB 后面不是数字时，整行按密码原样保留（含 TAB）** —— 手写 `mypass1<TAB>这是备注`
    不会被截成 `mypass1`（作者裁决 `J-8 = A`，`B-2026-059`）。
    """
    entries: list[BookEntry] = []
    index: dict[str, BookEntry] = {}
    lines = text.splitlines()

    # 找**第一个**标记行：它以上是注释，以下全是数据。
    # 用第一个而不是"每个标记都算"：这样密码本身如果正好是这一行，写在下面照样是密码。
    marker_at: int | None = None
    for i, raw in enumerate(lines):
        if raw.strip() == COMMENT_MARKER:
            marker_at = i
            break

    for n, raw in enumerate(lines):
        line = raw.strip()
        if not line:
            continue
        if marker_at is not None:
            if n <= marker_at:
                continue                # 标记以上：整段说明，跳过
            # 标记以下：**不跳过 `#`** —— 用户写什么就是什么
        elif line.startswith("#"):
            continue                    # 老文件（没有标记）：照旧当注释

        hits = 0
        pw = line
        decoded = decode_raw_line(line) if line.startswith(RAW_PREFIX) else None
        if decoded is not None:
            # 我们自己用 base64 写的"行格式存不住"的密码：原样取回，不做任何截断
            pw, hits = decoded
        else:
            if "\t" in line:
                head, _, tail = line.partition("\t")
                tail = tail.strip()
                if tail.isdigit():
                    pw, hits = head.strip(), int(tail)
                else:
                    # ★ TAB 后面**不是数字**时：整行按密码原样保留（**含 TAB**）。
                    #   作者裁决 `J-8 = A`（`B-2026-059`）。
                    #   以前只取 TAB 之前那一段，于是手写一行 `mypass1<TAB>这是备注`
                    #   读出来是 `[('mypass1', 0)]` —— **备注静默消失**，下次 `save`
                    #   又把该行重写成只剩密码，**不可逆**。
                    #   这里连 `head.strip()` 都不能用（那会改掉用户写的首尾空格）；
                    #   保留整行才符合"用户写什么就是什么"。
                    pw = line
            # 注意：**不再按 `| ， ： :` 截断**（以前会把"顺手写的备注"当分隔符，
            # 等于偷偷改用户的密码）；用户写什么就是什么，见 needs_encode()。
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


def book_notes(text: str) -> list[str]:
    """取出文件里**用户自己的备注**（`NOTES_MARKER` 到 `COMMENT_MARKER` 之间那几行）。

    这段是"用户的"，工具不解释、不当密码、保存时原样写回。老文件（没有这两个标记）返回空。
    """
    lines = text.splitlines()
    start = None
    for i, raw in enumerate(lines):
        if raw.strip() == NOTES_MARKER:
            start = i + 1
            break
    if start is None:
        return []
    end = len(lines)
    for i in range(start, len(lines)):
        if lines[i].strip() == COMMENT_MARKER:
            end = i
            break
    return lines[start:end]


def legacy_notes(text: str) -> list[str]:
    """老格式文件里**用户手写**的 `#` 行（工具自己那段文件头之外的）。

    判据：工具的文件头永远是**开头连续的一段** `#`/空行（`render_book` 就是这么写的），
    所以**第一个数据行之后**出现的 `#` 行就是用户手写的。

    升级成新格式时把它们搬进备注区**保住** —— 老文件里没法区分"这是备注"还是
    "这是想当密码的 `#` 行"，**保留是唯一不丢数据的做法**（想当密码就自己挪到
    `COMMENT_MARKER` 下面，那之后 `#` 行就是密码了）。
    """
    out: list[str] = []
    seen_data = False
    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("#"):
            if seen_data:
                out.append(line)
            continue
        seen_data = True
    return out


def looks_like_book(text: str) -> bool:
    """这文件是不是**本程序**的密码本（首行以 `# 密码本` 开头）。

    升级前先认一下：不是我们的密码本（用户自己拼的清单、别的 txt）**一律不动**，
    免得"读到就重写"把人家文件改了。
    """
    for raw in text.splitlines():
        line = raw.strip()
        if line:
            return line.startswith("# 密码本")
    return False


def sort_entries(entries: list[BookEntry]) -> list[BookEntry]:
    """按成功次数降序；次数相同的保持文件里的顺序（稳定排序）。"""
    return sorted(entries, key=lambda e: -e.hits)


def render_book(entries: list[BookEntry], notes: "list[str] | tuple[str, ...]" = ()) -> str:
    """渲染整个密码本文件（按尝试顺序写出去，文件顺序 = 尝试顺序）。

    结构：**工具说明**（全是 `#`，每次保存重写）→ `NOTES_MARKER` →
    **用户备注**（`notes`，原样写回）→ `COMMENT_MARKER` → **密码段**。
    分界行以下每一行都是密码，`#` 开头的也是 —— 这样用户手写 `#我的密码` 不会被丢掉。
    """
    head = [
        "# 密码本 —— 一行一个密码，工具按「成功次数」从多到少依次尝试（顺序：文件名 → 密码本 → 空密码）。",
        "# 可以手动编辑；编辑前请先读 README / 使用手册里的「密码本」一节。",
        NOTES_MARKER,
    ]
    head.extend(notes)                       # 用户备注：原样写回，不解释、不当密码
    head.append(COMMENT_MARKER)              # 密码从这里开始
    body = []
    for e in sort_entries(entries):
        if needs_encode(e.password):
            body.append(encode_raw_line(e.password) + (f"\t{e.hits}" if e.hits else ""))
        else:
            body.append(f"{e.password}\t{e.hits}" if e.hits else e.password)
    return "\n".join(head + body) + "\n"


def import_passwords(text: str, *, vault_like: bool = False) -> list[str]:
    """把一个 txt 的内容切成"要追加进密码本"的密码列表。

    * `vault_like=True`：这个文件本来就是密码本（有 `#` 注释 / TAB 次数 / `\\\\B64:` 行）
      → 按密码本的规则解析（注释跳过、base64 行解回来）；
    * 否则：**每一行原样就是一个密码**（`splitlines()` 已经去掉行尾的 `\\r\\n`，
      空行跳过）。**不 strip、不按备注截断** —— 用户文件里写什么就是什么。
    """
    if vault_like:
        return [e.password for e in parse_book(text)]
    # 空行**照收**（返回空串），由 add_many 去数"跳过空行几条"——这样界面能如实报话
    return list(text.splitlines())


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

    def __init__(self, *, book: str | None = None) -> None:
        self.book_path = book
        self.entries: list[BookEntry] = []
        self.stats = VaultStats()
        self.last_write_error: str = ""
        # 用户在备注区写的行（`NOTES_MARKER` 到 `COMMENT_MARKER` 之间），保存时原样写回
        self.notes: list[str] = []
        # 本次 reload 是否把老格式密码本**升级**成了新格式（界面想提示就用它）
        self.upgraded_from_legacy: bool = False
        # 上次读/写盘时盘上的样子（密码 → 次数）。写盘前做三方合并要靠它认出
        # "哪条是别的写者新加的 / 哪条是别的写者删掉的"（`B-2026-056`）。
        self._disk_snapshot: dict[str, int] = {}

    # -- 载入 ----------------------------------------------------------

    @classmethod
    def from_dir(cls, base_dir: str, **kw) -> PasswordVault:
        v = cls(book=os.path.join(base_dir, DEFAULT_BOOK), **kw)
        v.reload()
        return v

    def reload(self) -> None:
        """从磁盘重读（文件可能在外部被编辑过）。

        **老格式自动升级**（2026-09-21）：读到的文件如果是"本程序的密码本但还没有分界行"，
        就顺手按新格式重写一遍（`save()`），这样用户什么都不用做，格式就统一了。
        三条安全线：
          * 只认首行以 `# 密码本` 开头的文件 —— 用户自己拼的清单/别的 txt **一律不动**；
          * 升级前先把老文件里**用户手写的 `#` 行**搬进备注区（`legacy_notes`），**一条都不丢**；
          * 写失败（只读盘/被占用）只记 `last_write_error`，**不影响已经读到的条目**。

        **读不出来时保留内存里已有的条目**（`B-2026-056`）：并发 `os.replace` 期间实测
        48/36623 次读会 `Permission denied` —— 以前这里没有 `try/except`，撞上就是未捕获
        异常；而 `remove_many()` 还拿 `reload()` 当"落盘失败的回退"，一抛整条链路就断。
        """
        self.upgraded_from_legacy = False
        if not (self.book_path and os.path.isfile(self.book_path)):
            self.entries = []
            self.notes = []
            self._disk_snapshot = {}
            return
        text = self._read_book_text()
        if text is None:
            return                          # 读不出来：别把用户的密码本在内存里清空
        self.entries = parse_book(text)
        self.notes = book_notes(text)
        self._disk_snapshot = {e.password: e.hits for e in self.entries}
        if COMMENT_MARKER not in text and looks_like_book(text):
            self.notes = legacy_notes(text) + self.notes
            if self.save():
                self.upgraded_from_legacy = True

    # -- 保存 ----------------------------------------------------------

    def save(self) -> bool:
        """把整本密码本写回磁盘（写者锁 + 三方合并 + 原子替换 + fsync）。

        并发约定（`B-2026-056`）见模块级 `LOCK_SUFFIX` 那一段。这里只强调一件事：
        **写盘前会先做一次三方合并**，所以 `save()` 成功后 `self.entries` 可能
        **比调用前多出几条**（别的写者在这期间加进来的）—— 那是故意的，
        "后写者不许吞掉前写者的条目"。**失败时 `self.entries` 一个字都不动**，
        这样 `add()` / `remember()` 的精确回退仍然成立。
        """
        if not self.book_path:
            return False
        try:
            os.makedirs(os.path.dirname(self.book_path) or ".", exist_ok=True)
            entries, notes = self.entries, self.notes
            for attempt in range(WRITE_RETRIES):
                try:
                    with self._book_lock():
                        merged = self._merge_from_disk()
                        entries, notes = (self.entries, self.notes) if merged is None else merged
                        data = render_book(entries, notes)
                        # ★ tmp 名带 pid + 随机串：固定的 `<book>.tmp` 会让两个写者**同时写
                        #   同一个文件**（实测 save 失败 AAA 66 次 / BBB 69 次，
                        #   全是 WinError 5 / Errno 13）。
                        tmp = f"{self.book_path}.{os.getpid()}.{secrets.token_hex(4)}.tmp"
                        try:
                            with open(tmp, "w", encoding="utf-8", newline="\n") as f:
                                f.write(data)
                                f.flush()
                                # ★ fsync：`os.replace` 只保证"要么旧要么新"，**不保证数据
                                #   已经落盘** —— 断电时最后一次写入会连同文件一起消失
                                #   （以前全程 0 次 fsync）。
                                os.fsync(f.fileno())
                            os.replace(tmp, self.book_path)   # 原子替换，写一半断电也不毁本
                        finally:
                            if os.path.exists(tmp):           # 写失败别留半成品
                                try:
                                    os.remove(tmp)
                                except OSError:
                                    pass
                    break
                except PermissionError:
                    # 目标文件正被**别的进程**打开（另一个实例的 reload / 记事本）→ Windows 上
                    # `os.replace` 报 sharing violation。退让一下再试，别把"另一个人在读"
                    # 当成"密码本写不进去"。
                    if attempt == WRITE_RETRIES - 1:
                        raise
                    time.sleep(WRITE_RETRY_SLEEP * (attempt + 1))
            self.entries = entries
            self.notes = notes
            self._disk_snapshot = {e.password: e.hits for e in self.entries}
        except OSError as exc:
            self.last_write_error = str(exc)
            return False
        self.last_write_error = ""
        return True

    # -- 并发：读 / 锁 / 三方合并 --------------------------------------

    def _read_book_text(self) -> str | None:
        """读密码本原文；**读不出来返回 None，绝不抛**（`B-2026-056`）。

        为什么要重试：两个写者并发 `os.replace` 期间，实测 48/36623 次读会直接
        `Permission denied`（替换那一刻目标文件正被打开）。撞上就退让重试几次，
        而不是把异常抛给调用方。文件不存在返回空串（那是"盘上是空的"，不是"读不出来"）。
        """
        if not os.path.isfile(self.book_path):
            return ""
        for attempt in range(READ_RETRIES):
            try:
                return read_text(self.book_path)
            except OSError:
                time.sleep(READ_RETRY_SLEEP * (attempt + 1))
        return None

    @contextlib.contextmanager
    def _book_lock(self):
        """写密码本期间拿一把**跨会话 / 跨用户 / 跨机器共享盘**的写者锁。

        为什么不能用 `Local\\bbu-unpacker-single`（`core/single.py`）：那个互斥体
        **每登录会话一个**，两个 Windows 账号同时在线时各成主实例（`B-2026-056`）。
        也**不能**改成 `Global\\`：默认 DACL 只给创建者，第二个账号 `ACCESS_DENIED`，
        而 `single.py` 把"句柄为 NULL"当"我是主实例" → 照样开第二个窗口；更糟的是
        `Global\\` 会让第二个会话把路径投进**用户看不见的另一个会话的窗口**。

        锁文件是 `<密码本>.lock`（0 字节，不含任何密码数据）。**超时后照样往下走**
        （yield 出去的布尔值说明"到底锁上没有"）—— 宁可留一点并发风险，也不能让一个
        卡住的进程把密码本变成永远写不进去；何况 `save()` 里还有三方合并兜底。
        """
        if msvcrt is None or not self.book_path:
            yield False
            return
        fd = -1
        held = False
        try:
            try:
                fd = os.open(self.book_path + LOCK_SUFFIX, os.O_CREAT | os.O_RDWR)
            except OSError:
                yield False                  # 连锁文件都建不出来（只读目录）：不锁，照常写
                return
            deadline = time.monotonic() + LOCK_WAIT_SECONDS
            while True:
                try:
                    msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
                    held = True
                    break
                except OSError:
                    if time.monotonic() >= deadline:
                        break
                    time.sleep(0.02)
            yield held
        finally:
            if fd >= 0:
                if held:
                    try:
                        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
                    except OSError:
                        pass
                os.close(fd)

    def _merge_from_disk(self) -> "tuple[list[BookEntry], list[str]] | None":
        """写盘前把**别的写者**在这期间加进来的条目并回来（`B-2026-056`）。

        为什么光有锁不够：锁只保证"两次 save 不互相踩"，**丢更新发生在读-改-写这一层** ——
        两个实例各持一份内存副本时，后写的那个会拿自己的整本覆盖前一个写进去的条目
        （实测放大到 1500×400 轮时最终 AAA=1500、BBB=0）。

        三方合并：`base` = 上次读/写盘时盘上的样子（`_disk_snapshot`），`local` = 现在内存里的，
        `remote` = 此刻盘上的。每条规则都对应一种真实场景：
          * remote 有、local 也有          → 取次数大的（两边都成功过）；
          * remote 有、local 没有、base 有  → **local 删过它** → 尊重删除，丢掉；
          * remote 有、local 没有、base 没有 → **别的写者新加的** → 并进来（后写者不许吞）；
          * local 有、remote 没有、base 有  → 别的写者删过它 → 尊重删除，丢掉；
          * local 有、remote 没有、base 没有 → 我们自己新加的 → 保留。

        盘读不出来（并发替换 / 权限）时返回 `None` = **不合并**，按内存里的原样写 ——
        合并的前提是"知道盘上是什么"，读不到就别猜。备注区同理：我们这边是空的就沿用
        盘上的，别把用户写的备注抹掉。
        """
        text = self._read_book_text()
        if text is None:
            return None
        remote_entries = parse_book(text)
        remote = {e.password: e.hits for e in remote_entries}
        base = self._disk_snapshot
        local = {e.password: e.hits for e in self.entries}
        merged: list[BookEntry] = []
        for e in self.entries:
            if e.password in remote:
                merged.append(BookEntry(e.password, max(e.hits, remote[e.password])))
            elif e.password not in base:
                merged.append(BookEntry(e.password, e.hits))     # 我们自己新加的
            # else：别的写者删掉了它 → 尊重删除
        seen = {e.password for e in merged}
        for e in remote_entries:
            if e.password in seen or e.password in local or e.password in base:
                continue
            merged.append(BookEntry(e.password, e.hits))         # 别的写者新加的，并进来
        return merged, (self.notes or book_notes(text))

    def _write_really_failed(self) -> bool:
        """刚才那次 `save()` 返回 False，是不是**真的写盘失败**了？

        没有 `book_path` 的 vault（纯内存：测试、界面还没建文件时）本来就"不落盘"，
        `save()` 同样返回 False —— 那**不是**失败，不该把内存退回去（`remove_many()`
        一直是这么判的：`if self.book_path and not self.save()`）。这里统一成同一个判据。
        """
        return bool(self.book_path)

    # -- 供 UI 用 ------------------------------------------------------

    def size(self) -> dict[str, int]:
        return {"total": len(self.entries)}

    def set_entries(self, passwords: list[str]) -> None:
        """直接设定内容（测试用，不落盘）。

        ⚠ **不许顺手把 `_disk_snapshot` 改成这批内容**：这个 helper 是"绕过磁盘直接给内存
        塞一份"，盘上此刻是什么它并不知道。把 base 改成"内存里这批"会让下一次 `save()`
        的三方合并以为"盘上原本就有这些、现在没了 = 被别的写者删了"，于是**把自己刚设的
        条目全丢掉**（实测：`fast_book` 那条用例 701 条只剩 0 条）。
        base 只由 `reload()` / 成功的 `save()` 更新 —— 它描述的是**磁盘**，不是内存。
        """
        self.entries = [BookEntry(p, 0) for p in passwords if p]

    def find(self, password: str) -> BookEntry | None:
        return next((e for e in self.entries if e.password == password), None)

    def add(self, password: str) -> bool:
        """用户手动加一条，次数从 0 开始，立刻落盘。

        **什么都存得住、什么都不删**：文件格式表达不了的写法（空、首尾空格、
        以 `#`/`[` 开头、含 TAB 或 `| ， ： :`）会走 base64 那一行，
        所以"用户给什么就是什么"（原样进出，不 strip、不改样）。
        删除只能由用户在密码本页明确点 —— 这里没有条数上限。

        **落盘失败要把内存退回去**（`B-2026-055`）：以前只有 `remove_many` 会回退，
        于是写盘失败时内存里多了一条、盘上没有 —— 界面显示的和文件里的不一致，
        下次 reload 又"凭空少一条"。这里精确回退（不靠 `reload()`：文件读不出来时
        reload 会把内存清空，那是更大的不一致）。
        """
        pw = password if password is not None else ""
        if pw == "" or self.find(pw):
            return False
        self.entries.append(BookEntry(pw, 0))
        if self.save():
            return True
        if self._write_really_failed():
            self.entries.pop()              # 落盘失败：把刚加的那条撤回去
        return False

    def add_many(self, passwords: list[str]) -> tuple[int, int, int]:
        """批量追加到**末尾**（界面「批量导入」用）。返回 (新增, 跳过重复, 跳过空行)。

        原样收、不裁剪条数；整批只落盘一次（几千条也不会写几千次文件）。
        """
        known = {e.password for e in self.entries}
        added = dup = empty = 0
        fresh: list[BookEntry] = []
        for pw in passwords:
            if pw == "":
                empty += 1
                continue
            if pw in known:
                dup += 1
                continue
            fresh.append(BookEntry(pw, 0))
            known.add(pw)
            added += 1
        if not added:
            return added, dup, empty
        self.entries.extend(fresh)
        if not self.save():
            if self._write_really_failed():
                # 落盘失败：把这批退回去，别让内存比盘多出一批（`B-2026-055` 同一条不变量）
                del self.entries[len(self.entries) - len(fresh):]
                return 0, dup, empty
        return added, dup, empty

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
        before = list(self.entries)                      # 失败时**精确**退回去（不是 reload）
        self.entries = [e for e in self.entries if e.password not in victims]
        removed = len(before) - len(self.entries)
        if not removed:
            return 0
        if self.book_path and not self.save():
            # 落盘失败：把内存状态退回去，别让界面显示的和文件里的不一致。
            # 用**快照**而不是 `reload()`：并发替换期间盘可能读不出来，那样 reload 会把
            # 整本内存清空 —— 比"多删了几条"糟得多（`B-2026-056` 顺手改掉）。
            self.entries = before
            return 0
        return removed

    # -- 记忆（飞轮） --------------------------------------------------

    def remember(self, password: str) -> bool:
        """记一次成功：次数 +1 并落盘。没有的就新建一条。

        原样存（不 strip）、不裁剪条数：用户自己加的密码，就让他试、让他攒。
        """
        pw = password if password is not None else ""
        if pw == "":
            return False
        entry = self.find(pw)
        added = entry is None
        if added:
            self.entries.append(BookEntry(pw, 1))
        else:
            entry.hits += 1
        if self.save():
            return True
        # ★ 落盘失败：把内存退回去（`B-2026-055`），保持"内存 == 盘"。
        #   为什么以前只有 `remove_many` 有回退：`remember()` 是**跑批那条路**（穿透每成功
        #   一层就记一次），失败时内存 `hits+1`、盘上没变、界面一句提示都没有 ——
        #   "越用越准"悄悄失效，而且和 remove 的行为不一致（同样的失败，两套结果）。
        #   这里精确回退而不是 `reload()`：文件此刻可能读不出来（并发替换），
        #   那样 reload 会把整本内存清空 —— 比"多算一次"糟得多。
        #   ⚠ 纯内存 vault（没有 `book_path`）不在此列：那本来就"不落盘"，不是失败。
        if self._write_really_failed():
            if added:
                self.entries.pop()
            else:
                entry.hits -= 1
        return False

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
        """去重（保留最靠前的来源）+ 变体展开（保持顺序）。

        ★ 展开出来的每一条都记住**它出自哪一条原文**（`source_value`，`B-2026-057`）：
        命中变体时 `pierce` 靠它把成功记回**原条目**，而不是记成一条用户从没写过的
        变体值（那样原条目永远是 0 次、还凭空多一条假密码）。
        """
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
                out.append(PasswordCandidate(v, cand.origin, detail, cand.value))
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
            return f"命中{self.candidate.origin.label}：{shown}（尝试 {self.tries} 个）"
        # 「密码不对」这句话只在**真的是密码不对**时才说（`B-2026-027`）：
        # 包损坏 / 缺分卷 / 引擎读不出时，说"已尝试 N 个密码，均不正确"会把用户
        # 引到密码本里加密码、反复重试 —— 真正的原因（包本身有问题）被盖住。
        # `problem` 为 None 是老的调用方没填，仍按"密码问题"说（保守）。
        if self.problem is None or self.problem is Problem.EXHAUSTED:
            return f"已尝试 {self.tries} 个密码，均不正确：{self.stopped_reason}"
        return self.stopped_reason or "未能解开"


# 候选少于这个数就不值得并行（线程开销 + 前几个通常就命中了）
PARALLEL_MIN_CANDIDATES = 8

# **没有别的上限**：并行路数的天花板就是本机逻辑核数（`os.cpu_count()`）。
# 曾经有过一个 `PARALLEL_MAX = 32` 的硬顶，2026-09-20 按作者要求去掉了——
# 既然路数由用户在设置里自己选，机器扛得住就让他开满，不该由代码替他保守。


def pick_workers(candidate_count: int, info, want: int = 0) -> int:
    """试密码开几路并行。

    只在"便宜路径"上并行：知道包信息、而且能只测一个小条目（读几十 KB）
    或只读加密的文件头。整包回退那种 IO 密集的路子并行只会互相抢盘；
    单核机器也老实串行。

    `want` 是设置里指定的路数；0 = 自动 = 用满本机逻辑核。
    **天花板只有一个：本机逻辑核数**（选得比它大就夹到它）。
    """
    cpus = os.cpu_count() or 1
    if cpus < 2 or candidate_count < PARALLEL_MIN_CANDIDATES:
        return 1
    if info is None:
        return 1
    # 两条"便宜路径"都可以并行：
    #   ① 文件名没加密 → 只测最小的那个加密条目（读几十 KB）
    #   ② 文件名也加密 → 只 `7z l -slt -p<密码>` 读文件头（同样只读几十 KB）
    # 其余情况（不知道条目、要走"整包回退"）保持串行 —— 那是 IO 密集，多开会互相抢盘。
    cheap = (info.read_ok and (info.smallest_entry or info.first_entry)) or info.header_encrypted
    if not cheap:
        return 1
    return max(1, min(cpus, want or cpus))


def _fastpath_serial(archive_path: str) -> bool:
    """这个包在进程内判定时**只能是串行**的吗？（目前只有 7z 是这样）

    为什么要有它：7z 走常驻 `7z.dll`，那条路被一把模块锁串起来（实测核数恒 1.0，§20.10），
    所以日志里写"并行 16 路"会让人以为设置没生效 —— 那句话本身对（确实开了 16 路），
    但 **7z 这一段用不上**，得说明白。
    """
    try:
        import os as _os

        with open(archive_path, "rb") as f:
            if f.read(6) != b"7z\xbc\xaf\x27\x1c":
                return False
        from core import dll7z

        return dll7z.available()
    except Exception:      # noqa: BLE001 - 认不出来就当它不串行，别影响日志
        return False


def _fastpath_decides(archive_path: str) -> bool:
    """**真拿一个错密码问一次**：这条快路对这个包到底判不判得出结论？

    为什么要试：同样是 7z，"加密头"看 `Open()` 的 HRESULT 必定能判；
    而"明文头 + lz4/brotli"那种，`Extract` 跑完了也得不出一致结论（只能回退引擎）。
    前者退到 1 路很划算，后者退到 1 路就会把"能靠多进程并行"的引擎路拖慢。
    试探只花一次（十几毫秒），错密码被划掉就是"判得了"。
    """
    try:
        from core import fastcheck

        return bool(fastcheck.quick_reject(archive_path, "__bbu_probe__"))
    except Exception:      # noqa: BLE001 - 试不出来就当它判不了（保守：保持多路）
        return False


def worker_choices() -> list[tuple[int, str]]:
    """设置页「密码尝试线程数」下拉的选项：(值, 文案)。

    1 / 2 / 4 / 8 / 16 … 一路列到本机逻辑核数（不是 2 的幂时末尾补一个"满"）。
    逻辑核数就是上限：这里是唯一的夹取点，`pick_workers` 再夹一次。
    """
    cpus = os.cpu_count() or 1
    top = max(1, cpus)
    out: list[tuple[int, str]] = []
    value = 1
    while value <= top:
        out.append((value, f"{value} 线程"))
        value *= 2
    if top not in {v for v, _ in out}:
        out.append((top, f"{top} 路"))
    return out


def _missing_volume_note(archive_path) -> str | None:
    """这个包是"缺分卷"吗？缺哪一卷？（判据在 `probe.missing_volume_note`）

    只做一件事：把导入失败兜住 —— 判不出来就当没有，绝不影响找密码。
    """
    try:
        from core import probe as _probe

        return _probe.missing_volume_note(archive_path)
    except Exception:              # noqa: BLE001 - 判不了就当没有
        return None


def _engine_says_missing_volume(res) -> bool:
    """引擎输出里说"缺分卷 / 文件缺失"吗？（判据在 `engine.looks_like_missing_volume`）"""
    try:
        from core import engine as _engine

        return _engine.looks_like_missing_volume(res.tail or "")
    except Exception:              # noqa: BLE001 - 判不了就当没有
        return False


def _engine_cannot_read(extractor, info) -> bool:
    """引擎连包都**列不出来**吗？（不是压缩包 / 内容损坏 / 结构不完整）

    `B-2026-027`：这种包**没有密码可试** —— 以前会把整本密码挨个试一遍，最后报
    「已尝试 N 个密码，均不正确：引擎报错…（Is not archive）」，把用户引到密码本里
    加密码、反复重试。判据只看 `inspect()` 的结论：

      * `read_ok=False` —— 引擎没把它当压缩包读出来；
      * `encrypted is None` —— 连"加没加密"都判不出来。

    文件名加密（`header_encrypted`）**不属于**这一类：那个正常需要密码，
    `inspect()` 会给 `encrypted=True`。

    ⚠ 只有**真的跑过 `7z l`** 才算数：没有 7-Zip 时 `inspect()` 返回的是一份
    什么都没测的默认值（`read_ok=False` / `encrypted=None`），拿它当"读不出"会把
    "只装了 WinRAR"的机器上所有包都误判成损坏。
    """
    if info is None or info.read_ok or info.encrypted is not None:
        return False
    engines = getattr(extractor, "engines", None)
    return bool(getattr(engines, "seven_zip", None))


def apply_result(vault: PasswordVault, cand: PasswordCandidate, res,
                 tried: list[PasswordCandidate],
                 may_mangle: bool = False,
                 archive_path: str = "") -> "UnlockResult | None":
    """按候选顺序判定一个结果：返回 UnlockResult = 到此为止，None = 继续试。

    `may_mangle`：这个密码可能"命令行传不过去"（含引号/换行，见 `needs_manual`）。
    这时引擎报的"非密码错误"不能当成"包坏了"——那只是这一个候选传不过去，
    跳过它继续试别的，别把整轮搜索掐了。

    `archive_path`：只用于把"缺分卷"从"引擎报错"里**分出来**（`B-2026-038`）。
    """
    tried.append(cand)
    if res.ok:
        vault.stats.bump(cand.origin)
        return UnlockResult(True, cand.value, cand, tried)
    if res.cancelled:
        return UnlockResult(False, None, None, tried, "已停止", Problem.CANCELLED)
    if res.timed_out:
        return UnlockResult(False, None, None, tried, "验证超时，已中止", Problem.TIMEOUT)
    # 非密码错误（缺分卷 / 包损坏 / 引擎缺失）就没必要继续试了，
    # 而且要带上引擎自己的输出，否则用户只看到"失败"却不知道为什么
    if not res.wrong_password:
        if may_mangle:
            return None
        detail = res.tail.splitlines()[-1] if res.tail else res.brief()
        # 「缺分卷」要跟"包坏了"分开说（`B-2026-038`）：引擎那组特征词以前是**死代码**。
        # ⚠ `R-13`：`找不到指定的文件` 对"源包被删"同样成立 —— 所以**先核源包还在**，
        #    否则会把"源包没了"（`B-2026-031` 的场景）误诊成"分卷不全"，
        #    让用户去凑一份根本不需要的分卷。
        if archive_path and os.path.exists(str(archive_path)) and _engine_says_missing_volume(res):
            return UnlockResult(
                False, None, None, tried,
                f"分卷不全（不是密码问题）：{detail}",
                Problem.MISSING_VOLUME,
            )
        return UnlockResult(
            False, None, None, tried,
            f"引擎报错（{res.brief()}）：{detail}",
            Problem.ENGINE_ERROR,
        )
    return None


def unlock(
    vault: PasswordVault,
    extractor,
    archive_path: str,
    *,
    kind=None,
    skip: set[str] | None = None,
    max_tries: int | None = None,
    on_try=None,
    workers: int | None = None,
    label: str = "",
) -> UnlockResult:
    """按固定顺序逐个验证密码，命中即停。

    这里刻意用 `test`（不是 `extract`）——密码验证通过后才值得真正解压，
    避免"密码错→解压到一半→清垃圾"的浪费。

    `skip` 用于跳过调用方已经确认无效的密码（例如外层复用的密码试过了不对）。

    `on_try(cand, res)` 每试一个回调一次（界面拿它做"已试 x/y"的进度）。

    `label` 只用于心跳文案（`pierce` 传 `"第2层"`），没有它也能跑。

    `workers` 是并行路数：不给就自动（用满本机逻辑核，也就是上限；设置页可调），
    传 1 强制串行。并行只影响快慢，不影响"谁先命中"——判定仍按候选顺序走。
    """
    candidates = vault.candidates_for(archive_path)
    if skip:
        candidates = [c for c in candidates if c.value not in skip]
    if max_tries is not None:
        candidates = candidates[:max_tries]

    # 包信息（加没加密、最小条目是哪个）**跟密码无关**，取一次就够。
    # 以前每个候选都让 verify() 自己去 inspect —— 等于每个密码多起一个 7z 进程，
    # 实测单位成本 71.7ms 里有一半（≈35ms）花在这上面。取不到就退回旧行为。
    info = None
    if candidates:
        try:
            info = extractor.inspect(archive_path)
        except Exception:      # noqa: BLE001 - 判不了就让 verify 自己去问
            info = None

    n_workers = pick_workers(len(candidates), info, want=int(workers or 0))
    # 7z：进程内判定那一段是**串行**的（常驻 dll 一把锁，§20.10），开 N 路只会让 N-1 个线程
    # 排在锁上空转 → **在"这一段真判得了"时**退回 1 路（作者要求：别白开那么多线程）。
    # 判不了的那种 7z（比如 -mhe=off 配 lz4/brotli）每个候选都要起引擎，多进程并行是有效的，
    # 所以那种保持 N 路不变。
    serial_reason = ""
    if n_workers > 1 and _fastpath_serial(archive_path):
        if _fastpath_decides(archive_path):
            n_workers = 1
            serial_reason = "7z 的校验可在程序内部完成，已改用单线程"
        else:
            serial_reason = "7z 无法在程序内部判定，改用多线程并行"
    tried: list[PasswordCandidate] = []

    # 分卷不全：**先判再试**（`B-2026-038`）。判据早就写好了，以前却只在末尾兜底 ——
    # 结果是"一个根本解不开的包"照样把整本密码试一遍、还可能弹窗问用户要密码
    # （`worth_asking_user` 只对 `EXHAUSTED` 为真，所以末尾兜底时**确实会弹**）。
    missing = _missing_volume_note(archive_path)
    if missing:
        return UnlockResult(
            False, None, None, [],
            f"分卷不全：缺少 {missing}。请将这一组分卷放入同一个文件夹后重试",
            Problem.MISSING_VOLUME,
        )

    # 引擎根本读不出这个包（不是压缩包 / 内容损坏）：**这不是密码问题**（`B-2026-027`）。
    # 必须在"缺分卷"那道之后判 —— 缺分卷的包同样列不出来，先判会被抢成"引擎读不出"。
    # 判早了会白试整本密码，还会说一句「已尝试 N 个密码，均不正确」把人引错方向。
    if _engine_cannot_read(extractor, info):
        return UnlockResult(
            False, None, None, [],
            "引擎读不出这个包（不是密码问题）：可能不是压缩包，或者文件已损坏",
            Problem.ENGINE_ERROR,
        )

    def note(cand: PasswordCandidate, res) -> None:
        if on_try is not None:
            try:
                on_try(cand, res)
            except Exception:
                pass

    # ── 整轮搜索的心跳 ────────────────────────────────────────────────
    # 串行时每个候选只要几十毫秒，所以"还在动"不能靠引擎层那条心跳（它只对超过 5 秒的
    # **单条命令**说话）。这里以"这一层的搜索"为计时单位：每 ≥5 秒给一句
    # `[第2层] 仍在尝试密码：示例包.zip 已用时 12 秒，进度 340/1000`，走界面日志通道。
    started = time.monotonic()
    last_beat = started
    total = len(candidates)

    def beat() -> None:
        nonlocal last_beat
        now = time.monotonic()
        if now - last_beat < SEARCH_HEARTBEAT_SECONDS:
            return
        last_beat = now
        log = getattr(extractor, "log", None)
        if log is None:                 # 测试里传的假引擎：没这条通道就静默
            return
        head = f"[{label}] " if label else ""
        try:
            log(f"{head}仍在尝试密码：{os.path.basename(str(archive_path))} "
                f"已用时 {now - started:.0f} 秒，进度 {len(tried)}/{total}")
        except Exception:               # noqa: BLE001 - 报进度失败绝不能影响找密码
            pass

    # 开局报一句"这次几路"：用户从日志里就该看得出设置有没有生效，
    # 而不是靠数 `▶ 运行中` 的行数（并行路数会被候选数/逻辑核/便宜路径三道条件夹取）。
    _log = getattr(extractor, "log", None)
    if _log is not None and total:
        head = f"[{label}] " if label else ""
        try:
            if n_workers > 1:
                # 7z 那一段在程序内部是**串行**的（§20.10）：要么已经退回 1 路（判得了），
                # 要么补一句说明"为什么还开着 N 路"（判不了结论时靠引擎多进程并行）
                suffix = f"（{serial_reason}）" if serial_reason else ""
                _log(f"{head}并行 {n_workers} 线程尝试密码，共 {total} 个候选{suffix}")
            elif total >= PARALLEL_MIN_CANDIDATES:
                why = (serial_reason or ("设置里选了 1 路" if int(workers or 0) == 1
                                         else "拿不到包信息 / 只能整包回退 / 单核"))
                _log(f"{head}…{total} 个密码改为依次尝试（{why}）")
        except Exception:               # noqa: BLE001
            pass

    # ── 进程内快路：先划掉"肯定不对"的候选，再给引擎 ──────────────────
    # 试一个密码要起一次引擎进程（7z ≈30ms，而这台机器每秒只能起 ~43 个带 7z 的进程），
    # 所以瓶颈是"起进程"本身。`quick_reject()` 在进程内只看 KDF 派生的校验值就能判掉绝大多数
    # 错密码（zip 的 AES/ZipCrypto、rar5 的加密头；见 core/fastcheck.py）——它**只会说"不对"**，
    # 活下来的候选照旧走 `verify()` 真跑一次，语义完全不变（假阴性=0 是硬要求）。
    from core.engine import RunResult          # 延迟导入，避免 import 顺序上的弯弯绕

    rejected = 0
    engaged = 0          # **真的起了引擎**的候选数（不是"没被快路划掉"的数）
    finished = False     # 整轮候选是否走完（提前命中/中止/报错时是 False）

    def attempt(cand: PasswordCandidate) -> RunResult:
        nonlocal rejected, engaged
        value = cand.value or ""
        try:
            if value and extractor.quick_reject(archive_path, value):
                rejected += 1
                return RunResult(ok=False, code=-9, wrong_password=True,
                                 output="进程内快路：密码肯定不对（没有起引擎）")
        except Exception:      # noqa: BLE001 - 快路出任何问题都退回引擎
            pass
        engaged += 1
        return extractor.verify(archive_path, cand.value, kind=kind, info=info)

    def report_fastpath() -> None:
        """报一句"快路省了多少"——**只进 `run.log`（debug 通道），不上界面**。

        ★ 为什么不进界面（2026-09-25 作者裁决）：界面上已经有开局那句
        「并行 16 线程尝试密码，共 9 个候选」和命中时的「（尝试 N 个）」，再加一句
        "排除 6 / 验证 1" 就是**第三套计数口径**——并行窗口里"在飞的"候选算进了"尝试"、
        被快路划掉的不算"验证"，三个数谁也对不上，用户看着像程序算错了。
        数字对排错仍然有用，所以降级到 `extractor._debug`（`logs\run.log`，带 `[详细]`）。

        ★ 这里报的必须是**真实起过的引擎次数**。以前写的是 `total - rejected`
        （= "没被划掉的候选数"），在"密码提前命中 / 用户停止 / 引擎报错提前收工"时
        会把**根本没试到的候选**也算成"起了引擎"——作者的 10 万级实测里就撞上了：
        第 1 层密码在第 4 个候选命中、整层 0.5 秒跑完，却打出一句
        "排除了 6/101034 个候选（只给 101028 个起了引擎）"。现在两个数都报，
        并且把"整轮一共多少候选 / 实际试了几个"写清楚 —— 光看
        "排除了 0 个候选，1 个真的起了引擎（共 50001 个候选）"会让人以为漏试了 5 万条，
        其实那是**第一个候选就命中、整轮到此为止**。
        """
        dbg = getattr(extractor, "_debug", None)
        if not (rejected or engaged) or dbg is None:
            return
        try:
            did = len(tried)
            tail = (f"（整轮 {total} 个候选，只试了 {did} 个就结束）" if not finished
                    else f"（整轮 {total} 个候选）")
            dbg(f"{head}…已排除 {rejected} 个不正确的密码，"
                f"实际验证 {engaged} 个{tail}")
        except Exception:  # noqa: BLE001
            pass

    def stopped() -> "UnlockResult | None":
        """用户点了停止吗？—— 快路让候选不必经过引擎，所以这里得自己定期问一次，
        不然几千个候选的进程内扫描会一直跑到底、停止键按了没反应（每 64 个问一次就够）。"""
        nonlocal since_check
        since_check += 1
        if since_check < 64:
            return None
        since_check = 0
        try:
            if extractor.cancelled():
                report_fastpath()
                return UnlockResult(False, None, None, tried, "已停止", Problem.CANCELLED)
        except Exception:      # noqa: BLE001 - 问不出来就继续跑
            pass
        return None

    def wait_if_paused() -> "UnlockResult | None":
        """暂停时**在候选边界上停住**。

        引擎进程能被挂起（`engine._suspend_process`），但**进程内判定挂不住**
        （rar5/zip 是在 Python 线程里算 PBKDF2、7z 是常驻 dll 调用）——
        不在这里等的话，界面上"进度 x/y"会继续涨，看着像"暂停只停了一半"。
        等的时候顺便把"暂停中点了停止"接住。
        """
        while True:
            try:
                if not extractor.paused():
                    return None
            except Exception:      # noqa: BLE001 - 问不出来就别停
                return None
            try:
                if extractor.cancelled():
                    report_fastpath()
                    return UnlockResult(False, None, None, tried, "已停止", Problem.CANCELLED)
            except Exception:      # noqa: BLE001
                pass
            time.sleep(0.05)

    since_check = 0

    if n_workers <= 1:
        for cand in candidates:
            beat()
            gone = stopped()
            if gone is not None:
                return gone
            gone = wait_if_paused()
            if gone is not None:
                return gone
            # 用户给什么就往引擎里丢什么：含引号/换行的也照试一次（传不过去就跳过，
            # 见 apply_result 的 may_mangle），不再"静默不试"。
            mangle = extractor.needs_manual(cand.value)
            # 用 verify 而不是 test：文件名也加密的包只要读文件头就能判定密码，
            # 对 11GB 的分卷 7z 而言，这是「读几十 KB」和「读 11GB」的区别
            res = attempt(cand)
            note(cand, res)
            done = apply_result(vault, cand, res, tried, may_mangle=mangle,
                                archive_path=str(archive_path))
            if done is not None:
                report_fastpath()
                return done
    else:
        # 并行：窗口按 1 → 2 → 4 … 放大。前几个（最可能命中）照样是串行试的，
        # 只有"一直不中"的长尾才真正并发起来 —— 所以常见情况一点没变慢。
        # 窗口里同时起 N 个引擎进程（`_run` 用的是局部 proc，互不干扰）；
        # 判定仍按候选顺序走，保证"最早命中的那个赢"，结论和串行完全一致。
        pos, width = 0, 1
        with concurrent.futures.ThreadPoolExecutor(max_workers=n_workers) as pool:
            while pos < len(candidates):
                gone = wait_if_paused()      # 暂停时不再往里提交（已在飞的那一窗跑完就停住）
                if gone is not None:
                    return gone
                window = candidates[pos:pos + width]
                if window:
                    futures = [pool.submit(attempt, c) for c in window]
                    for cand, fut in zip(window, futures):
                        res = fut.result()
                        note(cand, res)
                        done = apply_result(vault, cand, res, tried,
                                            may_mangle=extractor.needs_manual(cand.value),
                                            archive_path=str(archive_path))
                        if done is not None:
                            report_fastpath()
                            return done
                        beat()
                        gone = stopped()
                        if gone is not None:
                            return gone
                pos += width
                width = min(width * 2, n_workers)

    finished = True
    reason = "密码已全部试完"
    report_fastpath()
    if not candidates:
        reason = "密码本为空，文件名中也未包含密码"
    # 分卷不全的包：别把"缺分卷"说成"密码试完了"——那会让界面弹一个白问的密码框
    # （这一处是**兜底**：命名的判据在试密码之前已经判过一次，见上面的 `_missing_volume_note`）
    missing = _missing_volume_note(archive_path)
    if missing:
        return UnlockResult(
            False, None, None, tried,
            f"分卷不全：缺少 {missing}。请将这一组分卷放入同一个文件夹后重试",
            Problem.MISSING_VOLUME,
        )
    return UnlockResult(False, None, None, tried, reason, Problem.EXHAUSTED)
