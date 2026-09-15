"""文件名密码提取。

对应需求里那 9 种写法，但用「关键词定位 + 截断」而不是一条贪婪正则——
贪婪正则会把手气差的情况吃成 '123.rar'，这是原版的典型 bug。
"""

from __future__ import annotations

import difflib
import os
import re
from dataclasses import dataclass

# 按长度降序：保证「解压密码」优先于「密码」，「解压码」优先于「码」
KEYWORDS: tuple[str, ...] = (
    "解压密码", "解压码", "压缩密码", "压缩码", "提取码", "提取密码",
    "密码", "暗号", "口令",
    "password", "passwd", "pass", "pwd", "pw",
)

# 关键词与密码之间的分隔符
_SEP_CHARS = " \t:：=＝-—_"
# 遇到这些字符说明密码到此为止
_STOP = set(" \t\r\n/\\")

# 关键词后面跟的密码 token：不吃点号（防止把 .rar 吃进来）
_TOKEN_RE = re.compile(r"[^\s/\\\.]+")

# 无关键词的写法：「示例包.zip123」——密码直接贴在扩展名后面。
# 规格里明确这种写法**只对文件生效，文件夹不认**，所以单独处理并受开关控制。
_ARCHIVE_EXTS = ("zip", "rar", "7z", "tar", "gz", "bz2", "xz", "001", "z01")
_EXT_THEN_PW_RE = re.compile(
    r"\.(?P<ext>" + "|".join(_ARCHIVE_EXTS) + r")(?P<pw>[^\s/\\\.]+)$",
    re.I,
)
# 贴出来的"密码"本身就是扩展名时不算（如 x.zipmp4）
_EXT_LIKE = set(_ARCHIVE_EXTS) | {"mp4", "avi", "mkv", "wmv", "flv", "rmvb", "ts"}


@dataclass(frozen=True)
class PasswordGuess:
    value: str
    keyword: str      # 命中的关键词
    source: str       # 来源描述，给 UI 的"密码来源"列用


def extract_from_name(name: str, *, allow_ext_digits: bool = True) -> PasswordGuess | None:
    """从单个文件名 / 文件夹名里提取密码。

    策略：取「最靠后」的关键词（最靠后的通常才是真密码位），
    再向后截断到下一个关键词、扩展名或字符串结束。

    支持的写法（以 示例包.zip 为例）：
        示例包.zip密码123        示例包.zip密码:123
        示例包.zip123            示例包.zip解压密码123
        示例包.zip解压密码:123    示例包.zippw123
        示例包.zippw:123         示例包.zip解压码123
        示例包.zip解压码:123

    `allow_ext_digits=False` 时关闭最后那种「扩展名后直接贴密码」的写法——
    文件夹名不认这种格式（规格明确要求），只有文件名才认。
    """
    if not name:
        return None
    base = os.path.basename(name.rstrip("/\\"))

    best: tuple[int, str] | None = None
    for kw in KEYWORDS:
        # 大小写不敏感找最后一个出现位置（密码/Password 混排也认）
        idx = base.casefold().rfind(kw.casefold())
        if idx == -1:
            continue
        # 取最靠后的关键词；同位置取更长的关键词
        if best is None or idx > best[0] or (idx == best[0] and len(kw) > len(best[1])):
            best = (idx, base[idx:idx + len(kw)])

    if best is None:
        # 没有关键词：只剩「扩展名后直接贴密码」这一种可能。
        if not allow_ext_digits:
            return None
        m = _EXT_THEN_PW_RE.search(base)
        if not m:
            return None
        pw = m.group("pw").strip(_SEP_CHARS)
        if not pw or pw.casefold() in _EXT_LIKE:
            return None
        return PasswordGuess(value=pw, keyword=m.group("ext").lower() + "后缀", source="文件名（扩展名后缀）")

    pos = best[0] + len(best[1])
    tail = base[pos:]

    # 跳过分隔符
    i = 0
    while i < len(tail) and tail[i] in _SEP_CHARS:
        i += 1
    tail = tail[i:]
    if not tail:
        return None

    m = _TOKEN_RE.match(tail)
    if not m:
        return None
    token = m.group(0).strip(_SEP_CHARS)
    if not token:
        return None

    # 截断：token 内部若恰好又开始一个关键词，说明密码在其之前
    for kw in KEYWORDS:
        p = token.casefold().find(kw.casefold())
        if p > 0:
            token = token[:p].strip(_SEP_CHARS)
            break
    if not token:
        return None

    # 排除把纯扩展名当密码的情况（如 "示例包.zip" 本身）
    if token.casefold() in ("zip", "rar", "7z", "tar", "gz", "001", "z01"):
        return None

    return PasswordGuess(value=token, keyword=best[1], source=f"文件名（{best[1]}）")


def extract_from_path(path: str) -> PasswordGuess | None:
    """先看文件名，再看父文件夹名。

    文件夹拖入场景：「示例包合集解压密码:123\\示例包.zip」
    密码在文件夹名里，文件名里没有 —— 必须能向上找。
    """
    p = os.path.normpath(path)
    parts = [os.path.basename(p)]
    parent = os.path.basename(os.path.dirname(p))
    if parent:
        parts.append(parent)
    # 再往上一层也有意义（「合集/示例包合集解压密码123/xxx.zip」）
    grand = os.path.basename(os.path.dirname(os.path.dirname(p)))
    if grand:
        parts.append(grand)

    for i, candidate in enumerate(parts):
        # 只有文件名的这一档允许「扩展名后贴密码」；文件夹名不认这种写法
        guess = extract_from_name(candidate, allow_ext_digits=(i == 0))
        if guess:
            where = "文件名" if candidate == parts[0] else "文件夹名"
            return PasswordGuess(guess.value, guess.keyword, f"{where}（{guess.keyword}）")
    return None


# --------------------------------------------------------------------------
# 变体：中文站点常见的全角/半角、空格混排
# --------------------------------------------------------------------------

def to_halfwidth(s: str) -> str:
    out = []
    for ch in s:
        code = ord(ch)
        if code == 0x3000:
            out.append(" ")
        elif 0xFF01 <= code <= 0xFF5E:
            out.append(chr(code - 0xFEE0))
        else:
            out.append(ch)
    return "".join(out)


def variants(value: str) -> list[str]:
    """一个候选密码 → 若干个等价写法，按尝试顺序返回。

    很多"密码明明对却解不开"就是全角字符或空格导致的。顺序：

        1. 原样                 ← 永远先试原样，别让变体抢在前面
        2. 去首尾空格
        3. 去掉全部空白         ← 仅当含空白时才生成，避免无谓多试
        4. 全角转半角
        5. 全角转半角 + 去首尾空格
    """
    seen: list[str] = []
    candidates = [value, value.strip()]
    if any(ch.isspace() for ch in value):
        candidates.append("".join(ch for ch in value if not ch.isspace()))
    half = to_halfwidth(value)
    candidates.extend([half, half.strip()])

    for v in candidates:
        if v and v not in seen:
            seen.append(v)
    return seen


# --------------------------------------------------------------------------
# 注：原「临时密码本」（`关键字<TAB>密码` 的映射表）已废弃。
#
# 它想解决的是"同一来源的文件常共用同一个密码，我手工把对应关系写下来"，
# 而「记住解压成功过的密码」就是**自动积累的同一份知识**，零维护。
# 原来那套等于让用户手工做机器该做的事，还多背两个概念。
# 相关代码（TempEntry / parse_temp_book / match_entries）已删除。
# --------------------------------------------------------------------------
