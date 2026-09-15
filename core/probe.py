"""格式探测：不信任扩展名，只认 magic bytes。

核心职责：
  1. 判断一个文件「真实」是什么格式（含"伪装成 mp4 的 zip"）
  2. 找出"前面垫了真视频、后面接压缩包"的内嵌包（1067.mp4 那种）
  3. 识别分卷压缩包，并指出哪个是主卷（原版"重复解压"的病灶）
  4. 清理文件名中的下载站后缀 / 规避检测的"删"字

设计原则：本模块只做判断，不做任何文件移动或解压。
"""

from __future__ import annotations

import os
import re
import struct
import time
import zlib
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, Iterable


class Fmt(str, Enum):
    """探测出的真实格式。"""

    ZIP = "zip"
    RAR = "rar"
    RAR5 = "rar5"
    SEVENZ = "7z"
    TAR = "tar"
    GZIP = "gz"
    # 单文件压缩（7-Zip ZS 能直接当压缩包读，解出来就是里面那个文件）
    LZ4 = "lz4"
    LZ5 = "lz5"
    ZSTD = "zstd"
    BROTLI = "brotli"
    MP4 = "mp4"
    UNKNOWN = "unknown"

    @property
    def is_archive(self) -> bool:
        return self in (Fmt.ZIP, Fmt.RAR, Fmt.RAR5, Fmt.SEVENZ, Fmt.TAR, Fmt.GZIP,
                        Fmt.LZ4, Fmt.LZ5, Fmt.ZSTD, Fmt.BROTLI)

    @property
    def engine(self) -> str | None:
        """该格式应由哪个引擎处理。"""
        if self is Fmt.ZIP:
            return "7z"          # zip 用 7z 即可，稳定
        if self in (Fmt.RAR, Fmt.RAR5):
            return "winrar"      # 关键：rar 与伪装 rar 必须走 WinRAR
        if self in (Fmt.SEVENZ, Fmt.TAR, Fmt.GZIP, Fmt.LZ4, Fmt.LZ5, Fmt.ZSTD, Fmt.BROTLI):
            return "7z"          # lz4/lz5/zstd/brotli 只有内置的 7-Zip ZS 认
        return None


# --------------------------------------------------------------------------
# magic bytes 判定
# --------------------------------------------------------------------------

ZIP_MAGICS = (b"PK\x03\x04", b"PK\x05\x06", b"PK\x07\x08")
RAR4_MAGIC = b"Rar!\x1a\x07\x00"
RAR5_MAGIC = b"Rar!\x1a\x07\x01\x00"   # 注意：8 字节，末位 01
SEVENZ_MAGIC = b"7z\xbc\xaf\x27\x1c"
GZIP_MAGIC = b"\x1f\x8b"
# 这几个是"帧"格式（小端 magic）。实测样本 `D521.rar.lz4` 就是 LZ4 帧里包了一个 rar。
LZ4_MAGIC = b"\x04\x22\x4d\x18"
LZ5_MAGIC = b"\x05\x22\x4d\x18"
ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"
# brotli 没有 magic（裸流无法可靠识别），只能靠扩展名
BROTLI_EXTS = frozenset({"br", "brotli", "tbr"})
# tar 的 magic 在偏移 257 处，需要读 265 字节
TAR_MAGIC_OFFSET = 257
TAR_MAGICS = (b"ustar", b"ustar  \x00")

# 「这个名字看起来本来就该是压缩包」的扩展名。
# 用来说明：一个探测不出格式的文件到底是"用户拖错了"还是"包坏了但值得一试"。
ARCHIVE_EXTS = frozenset({
    "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz",
    "cab", "arj", "lzh", "lzma", "zst", "z", "iso", "001", "z01", "r00",
    # 7-Zip ZS 认的这些：裸帧 + 对应的 tar 变体
    "lz4", "tlz4", "lz5", "tlz5", "tzst", "br", "brotli", "tbr", "liz", "tliz",
})


def detect_format(path: str | os.PathLike[str]) -> Fmt:
    """读取文件头判断真实格式。文件不可读时返回 UNKNOWN。"""
    try:
        with open(path, "rb") as fh:
            head = fh.read(512)
    except OSError:
        return Fmt.UNKNOWN

    if head.startswith(ZIP_MAGICS):
        # 空包 (PK\x05\x06) 也是合法 zip
        return Fmt.ZIP
    if head.startswith(RAR5_MAGIC):
        return Fmt.RAR5
    if head.startswith(RAR4_MAGIC):
        return Fmt.RAR
    if head.startswith(SEVENZ_MAGIC):
        return Fmt.SEVENZ
    if head.startswith(GZIP_MAGIC):
        return Fmt.GZIP
    # 帧格式：靠 magic 认（小端）。brotli 没有 magic，只能靠扩展名（见下面的兜底）
    if head.startswith(LZ4_MAGIC):
        return Fmt.LZ4
    if head.startswith(LZ5_MAGIC):
        return Fmt.LZ5
    if head.startswith(ZSTD_MAGIC):
        return Fmt.ZSTD
    if len(head) > TAR_MAGIC_OFFSET + 5 and head[TAR_MAGIC_OFFSET:TAR_MAGIC_OFFSET + 5] in (
        b"ustar",
    ):
        return Fmt.TAR
    # mp4/mov: 偏移 4 处为 'ftyp'
    if len(head) >= 12 and head[4:8] == b"ftyp":
        return Fmt.MP4
    # brotli 裸流没有任何 magic，只能靠扩展名（认错的风险由"只有 .br 才这么判"来兜）
    if ext_of(path) in BROTLI_EXTS:
        return Fmt.BROTLI
    return Fmt.UNKNOWN


def is_disguised(path: str | os.PathLike[str]) -> bool:
    """内容像压缩包，但扩展名不是压缩包 —— 即"伪装"。

    例：教程视频.mp4 实际是 ZIP → True
    """
    fmt = detect_format(path)
    if not fmt.is_archive:
        return False
    ext = ext_of(path)
    return ext not in ("zip", "rar", "7z", "tar", "gz", "001", "z01")


def ext_of(path: str | os.PathLike[str]) -> str:
    """取扩展名（小写、不含点）。无扩展名返回空串。"""
    name = os.path.basename(str(path))
    _, dot, ext = name.rpartition(".")
    if not dot or dot == name:
        return ""
    return ext.lower()


# --------------------------------------------------------------------------
# 内嵌压缩包：文件头不是压缩包，但**身体里**藏着一个
# --------------------------------------------------------------------------
#
# 实盘最常见的一种伪装不是"改扩展名"，而是"前面垫一段真视频，后面接压缩包"：
# 文件能正常播放（播放器读到 moov 就完事），而压缩包完整地躺在尾部。
# 实测样本 1067.mp4 = 534MB 真视频 + 从 534691227 字节处开始的完整 ZIP。
#
# 这类文件靠头 512 字节永远认不出来，engine 只会回一句 "Cannot open the file
# as archive"。所以这里补两条发现路径：
#
#   1. **尾部目录定位**（ZIP 专用，权威且便宜）：ZIP 的中央目录/EOCD 在文件末尾，
#      从末尾几 MB 里找到 EOCD 就能**反推出**压缩包的起始偏移，不用扫描整个文件。
#   2. **全盘扫描**（RAR5 / 7z 备用）：这两个格式的头部信息量足够，
#      可以直接校验头部的 CRC32 —— 只认 magic 是不行的：
#      实测 1.79GB 的高熵视频数据里，`PK\x03\x04` 这种 4 字节签名会**自然出现**，
#      不校验就会把随机数据当成压缩包。

EMBED_MIN_SIZE = 1 << 20        # 小于 1MB 的文件不值得做"垫片"伪装
EMBED_TAIL = 4 << 20            # 找 EOCD 只看尾部 4MB（EOCD 注释上限 64KB，够宽）
EMBED_CHUNK = 8 << 20           # 全盘扫描的块大小

# 「有可能是垫了压缩包的文件」的扩展名。刻意收窄：只覆盖"改扩展名伪装"的常见家族，
# 免得给每个 .vhdx/.raw 大文件都白扫一遍（全盘扫描是要读完整文件的）。
CARRIER_EXTS = frozenset({
    # 视频
    "mp4", "mkv", "avi", "mov", "wmv", "flv", "ts", "rmvb", "rm", "m4v",
    "mpg", "mpeg", "webm", "3gp", "f4v",
    # 图片
    "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "avif",
    # 文档 / 安装包 / 裸数据，网盘绕检测的常见马甲
    "txt", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "epub",
    "apk", "exe", "dll", "bin", "dat", "msi",
})

_EMBED_EXT = {
    Fmt.ZIP: "zip",
    Fmt.RAR: "rar",
    Fmt.RAR5: "rar",
    Fmt.SEVENZ: "7z",
    Fmt.GZIP: "gz",
    Fmt.TAR: "tar",
}


@dataclass(frozen=True)
class Embedded:
    """在某个文件里发现的内嵌压缩包。"""

    fmt: Fmt
    offset: int          # 压缩包在这个文件里的起始偏移
    how: str = ""        # 怎么找到的（给人看）

    @property
    def ext(self) -> str:
        """切出来之后该给它什么扩展名 —— 引擎也会按内容判断，这里只为可读。"""
        return _EMBED_EXT.get(self.fmt, "bin")

    @property
    def offset_text(self) -> str:
        mb = self.offset / (1024 ** 2)
        if mb >= 1024:
            return f"{mb / 1024:.2f} GB"
        return f"{mb:.1f} MB"

    @property
    def label(self) -> str:
        return f"内嵌 {self.fmt.value.upper()} @ {self.offset_text}"


def human_size(n: int) -> str:
    """字节数 → 给人看的写法（GB / MB / KB）。"""
    if n >= 1024 ** 3:
        return f"{n / 1024 ** 3:.2f} GB"
    if n >= 1024 ** 2:
        return f"{n / 1024 ** 2:.1f} MB"
    if n >= 1024:
        return f"{n / 1024:.1f} KB"
    return f"{n} 字节"


def _read_at(path: str, offset: int, n: int) -> bytes:
    try:
        with open(path, "rb") as fh:
            fh.seek(offset)
            return fh.read(n)
    except OSError:
        return b""


def _vint(buf: bytes, i: int) -> tuple[int, int]:
    """RAR5 的可变长整数：低 7 位存数据，最高位表示"还有后续"。"""
    val = 0
    shift = 0
    while i < len(buf):
        b = buf[i]
        i += 1
        val |= (b & 0x7F) << shift
        if not (b & 0x80):
            return val, i
        shift += 7
    return -1, i


def _rar5_ok(buf: bytes) -> bool:
    """RAR5 主档头校验：HEAD_CRC 覆盖「HEAD_SIZE 字段本身 + 它声明的长度」。

    实测样本（1067.mp4 里嵌的那个 RAR5）：HEAD_SIZE=33、vint 占 1 字节，
    crc32(buf[12:46]) 正好等于 buf[8:12] 里的 CRC。
    """
    if not buf.startswith(RAR5_MAGIC) or len(buf) < 16:
        return False
    want = struct.unpack("<I", buf[8:12])[0]
    size, after = _vint(buf, 12)
    if size <= 0 or size > (1 << 20):
        return False
    if after + size > len(buf):
        return False
    return zlib.crc32(buf[12:after + size]) & 0xFFFFFFFF == want


def _sevenz_ok(buf: bytes) -> bool:
    """7z 起始头校验：Start Header CRC32 覆盖 buf[12:32]。"""
    if not buf.startswith(SEVENZ_MAGIC) or len(buf) < 32:
        return False
    want = struct.unpack("<I", buf[8:12])[0]
    return zlib.crc32(buf[12:32]) & 0xFFFFFFFF == want


def _zip64_base(path: str, tail: bytes, start: int, eocd_i: int, size: int) -> int | None:
    """ZIP64：经典 EOCD 里放不下 4GB 的偏移时，真值在 ZIP64 EOCD 记录里。"""
    loc = tail.rfind(b"PK\x06\x07", max(0, eocd_i - 128), eocd_i)
    if loc < 0 or loc + 20 > len(tail):
        return None
    z64_off = struct.unpack("<Q", tail[loc + 8:loc + 16])[0]
    z64 = tail.rfind(b"PK\x06\x06", 0, loc)
    if z64 < 0 or z64 + 56 > len(tail):
        return None
    cd_off = struct.unpack("<Q", tail[z64 + 48:z64 + 56])[0]
    base = (start + z64) - z64_off
    if not (0 < base < size):
        return None
    if _read_at(path, base + cd_off, 4) != b"PK\x01\x02":
        return None
    return base


def _zip_base_in_tail(path: str, size: int) -> int | None:
    """从尾部找 EOCD，反推 ZIP 在这个文件里的起始偏移。

    自洽性要求（两个都满足才认）：
        * EOCD 前 cd_size 字节处必须真的是中央目录签名 `PK\\x01\\x02`
        * 反推出的起点处必须是 ZIP 本地头 `PK\\x03\\x04`
    这样即使视频数据里恰好出现 `PK\\x05\\x06`，也不会误判。
    """
    start = max(0, size - EMBED_TAIL)
    try:
        with open(path, "rb") as fh:
            fh.seek(start)
            tail = fh.read()
    except OSError:
        return None

    pos = len(tail)
    while True:
        i = tail.rfind(b"PK\x05\x06", 0, pos)
        if i < 0:
            return None
        pos = i
        rec = tail[i:i + 22]
        if len(rec) < 22:
            continue
        cd_size, cd_off = struct.unpack("<II", rec[12:20])
        abs_eocd = start + i

        base: int | None = None
        if cd_size not in (0, 0xFFFFFFFF) and cd_off not in (0, 0xFFFFFFFF):
            cd_pos = abs_eocd - cd_size
            if cd_pos > 0 and _read_at(path, cd_pos, 4) == b"PK\x01\x02":
                candidate = cd_pos - cd_off
                if 0 < candidate < size:
                    base = candidate
        if base is None:
            base = _zip64_base(path, tail, start, i, size)
        if base is None:
            continue
        if _read_at(path, base, 4) in ZIP_MAGICS:
            return base


def _scan_magic(path: str, size: int, cancel: Callable[[], bool] | None = None):
    """全盘扫描 RAR5 / 7z 签名，返回第一个通过头部 CRC 校验的 (格式, 偏移)。"""
    magic2fmt = ((RAR5_MAGIC, Fmt.RAR5, _rar5_ok), (SEVENZ_MAGIC, Fmt.SEVENZ, _sevenz_ok))
    longest = max(len(m) for m, _, _ in magic2fmt)

    carry = b""
    offset = 0
    try:
        fh = open(path, "rb")
    except OSError:
        return None
    with fh:
        while True:
            if cancel is not None and cancel():
                return None
            buf = fh.read(EMBED_CHUNK)
            if not buf:
                return None
            data = carry + buf
            base = offset - len(carry)
            for magic, fmt, checker in magic2fmt:
                at = data.find(magic)
                while at >= 0:
                    absolute = base + at
                    if absolute > 0:
                        # 跨块边界时 data 里可能不够校验，重新按绝对偏移读一段
                        head = data[at:at + 64]
                        if len(head) < 64:
                            head = _read_at(path, absolute, 64)
                        if checker(head):
                            return fmt, absolute
                    at = data.find(magic, at + 1)
            carry = data[-longest:]
            offset += len(buf)


def find_embedded(
    path: str | os.PathLike[str],
    *,
    deep: bool = True,
    cancel: Callable[[], bool] | None = None,
) -> Embedded | None:
    """这个文件里是不是藏着一个压缩包？返回它从哪开始。

    * 文件头本身就是压缩包 → 返回 None（那是"改扩展名伪装"，另一条路径处理）
    * `deep=False` 只做便宜的尾部定位（不含全盘扫描）
    """
    full = str(path)
    if detect_format(full).is_archive:
        return None
    try:
        size = os.path.getsize(full)
    except OSError:
        return None
    if size < EMBED_MIN_SIZE:
        return None

    base = _zip_base_in_tail(full, size)
    if base is not None:
        return Embedded(Fmt.ZIP, base, "尾部目录定位")
    if not deep:
        return None
    hit = _scan_magic(full, size, cancel)
    if hit is None:
        return None
    return Embedded(hit[0], hit[1], "全盘扫描")


def norm_exts(exts) -> set[str]:
    """把用户填的扩展名列表规整成小写、不带点的集合（".APK" / "apk " 都认）。"""
    out: set[str] = set()
    for e in exts or ():
        s = str(e).strip().lower().lstrip("*")
        while s.startswith("."):
            s = s[1:]
        if s:
            out.add(s)
    return out


def excluded_ext(path: str | os.PathLike[str], exts) -> str:
    """这个文件的扩展名在"不处理"名单里吗？在就返回那个扩展名，否则返回空串。"""
    want = norm_exts(exts) if not isinstance(exts, set) else exts
    ext = ext_of(path)
    return ext if ext and ext in want else ""


def looks_like_carrier(path: str | os.PathLike[str]) -> bool:
    """值不值得为它做内嵌探测（扩展名像"马甲" + 体积够大）。"""
    try:
        if os.path.getsize(path) < EMBED_MIN_SIZE:
            return False
    except OSError:
        return False
    ext = ext_of(path)
    return not ext or ext in CARRIER_EXTS


def carve(
    path: str | os.PathLike[str],
    offset: int,
    dest: str,
    *,
    cancel: Callable[[], bool] | None = None,
    pause: Callable[[], bool] | None = None,
    chunk: int = EMBED_CHUNK,
) -> int:
    """把 [offset, 文件尾) 原样切到 dest。

    为什么是"切到文件尾"而不是"精确切到压缩包尾"：ZIP/RAR 尾部多几个字节的
    垃圾数据是**容忍**的（7-Zip 只会警告 "There are data after the end of archive"），
    而算错长度会直接切坏包。少算不如多算。

    返回写入字节数；被中止或失败返回 -1（半成品会被删掉，不留垃圾）。
    """
    written = 0
    try:
        with open(path, "rb") as fi, open(dest, "wb") as fo:
            fi.seek(offset)
            while True:
                if cancel is not None and cancel():
                    raise InterruptedError
                if pause is not None and pause():
                    time.sleep(0.1)          # 切线也要能被暂停（1GB 要切好几秒）
                    continue
                buf = fi.read(chunk)
                if not buf:
                    break
                fo.write(buf)
                written += len(buf)
    except (OSError, InterruptedError):
        try:
            os.remove(dest)
        except OSError:
            pass
        return -1
    return written


# --------------------------------------------------------------------------
# 文件名清理
# --------------------------------------------------------------------------

# 下载站/论坛常见后缀：[xxx.com]、{www.aaa.net}、【某某论坛】、(1)、_1
_SITE_BRACKET = re.compile(r"[\[\{【（(][^\]\}】）)]*(?:\.(?:com|net|org|cc|cn|me|xyz|top|info)|论坛|社区|首发|分享)[^\]\}】）)]*[\]\}】）)]", re.I)
_TRAILING_COPY = re.compile(r"(?:[_\-\s]+(?:\(\d+\)|\[\d+\]|\d{1,2}))+$")
# 规避检测用的"删"字：夹在扩展名里，如 .z删 i删 p删
_DEL_CHAR = re.compile(r"[\s]*删[\s]*")


def clean_delete_chars(name: str) -> str:
    """去掉文件名中用于规避检测的"删"字。

    '学习资料.z删 i删 p删'  ->  '学习资料.zip'
    """
    return _DEL_CHAR.sub("", name)


def strip_site_noise(name: str) -> str:
    """去掉下载站站点标识、括号广告、结尾的副本编号，避免污染密码提取。"""
    stem, ext = os.path.splitext(name)
    stem = _SITE_BRACKET.sub("", stem)
    stem = _TRAILING_COPY.sub("", stem)
    stem = stem.strip(" ._-")
    return stem + ext


def normalize_for_match(name: str) -> str:
    """归一化：用于"临时密码本按文件名匹配"和相似度比较。

    去扩展名、去站名、去"删"字、去标点空格、全角转半角、大小写折叠。
    """
    s = clean_delete_chars(name)
    stem = os.path.splitext(s)[0]
    stem = _SITE_BRACKET.sub("", stem)
    # 全角 -> 半角
    out = []
    for ch in stem:
        code = ord(ch)
        if code == 0x3000:
            out.append(" ")
        elif 0xFF01 <= code <= 0xFF5E:
            out.append(chr(code - 0xFEE0))
        else:
            out.append(ch)
    stem = "".join(out)
    stem = re.sub(r"[\s\-_.,:;!?，。：；！？、·|]+", "", stem)
    return stem.casefold()


# --------------------------------------------------------------------------
# 分卷识别
# --------------------------------------------------------------------------

class VolKind(str, Enum):
    PART = "part"        # x.part1.rar / x.part01.rar
    OLD_RAR = "oldrar"   # x.rar + x.r00 + x.r01
    ZIP_SPLIT = "zipsplit"  # x.z01 + x.zip
    NUMERIC = "numeric"  # x.001 + x.002
    NONE = "none"        # 非分卷


@dataclass(frozen=True)
class VolumeInfo:
    kind: VolKind
    base: str            # 归组用的 basename（含目录）
    index: int           # 卷号，主卷为最小
    is_first: bool       # 是否主卷

    @property
    def is_split(self) -> bool:
        return self.kind is not VolKind.NONE


_PART_RE = re.compile(r"^(?P<base>.+?)\.part(?P<num>\d{1,3})\.rar$", re.I)
_OLD_RAR_RE = re.compile(r"^(?P<base>.+?)\.r(?P<num>\d{2,3})$", re.I)
_ZSPLIT_RE = re.compile(r"^(?P<base>.+?)\.z(?P<num>\d{2})$", re.I)
_NUMERIC_RE = re.compile(r"^(?P<base>.+?)\.(?P<num>\d{3})$")
_PLAIN_RAR_RE = re.compile(r"^(?P<base>.+?)\.rar$", re.I)
_PLAIN_ZIP_RE = re.compile(r"^(?P<base>.+?)\.zip$", re.I)


def classify_volume(path: str | os.PathLike[str]) -> VolumeInfo:
    """判断单个文件属于哪种分卷形态。"""
    full = str(path)
    name = os.path.basename(full)
    d = os.path.dirname(full)
    join = lambda b: os.path.join(d, b) if d else b

    m = _PART_RE.match(name)
    if m:
        n = int(m.group("num"))
        return VolumeInfo(VolKind.PART, join(m.group("base")), n, n == 1)

    m = _ZSPLIT_RE.match(name)
    if m:
        # z01 是次卷；主卷是同目录的 .zip，由调用方配对
        return VolumeInfo(VolKind.ZIP_SPLIT, join(m.group("base")), int(m.group("num")), False)

    m = _OLD_RAR_RE.match(name)
    if m:
        # r00 是第 2 卷（第 1 卷是 x.rar）
        return VolumeInfo(VolKind.OLD_RAR, join(m.group("base")), int(m.group("num")) + 1, False)

    m = _NUMERIC_RE.match(name)
    if m:
        n = int(m.group("num"))
        # .001 是主卷；注意排除 .7z.001 这种（base 已含 .7z）
        return VolumeInfo(VolKind.NUMERIC, join(m.group("base")), n, n == 1)

    m = _PLAIN_ZIP_RE.match(name)
    if m:
        # zip 主卷总是主卷（若同目录有 .z01 则确实是分卷）
        return VolumeInfo(VolKind.ZIP_SPLIT, join(m.group("base")), 1, True)

    m = _PLAIN_RAR_RE.match(name)
    if m:
        return VolumeInfo(VolKind.OLD_RAR, join(m.group("base")), 1, True)

    return VolumeInfo(VolKind.NONE, join(os.path.splitext(name)[0]), 1, True)


@dataclass
class VolumeGroup:
    """同目录、同 basename 的一组分卷。"""

    base: str
    kind: VolKind
    main: str                       # 主卷完整路径 —— 只把这个送进引擎
    others: list[str] = field(default_factory=list)

    @property
    def count(self) -> int:
        return 1 + len(self.others)

    @property
    def is_split(self) -> bool:
        """真的是分卷吗？只有 1 个成员时不算。

        `.rar` / `.zip` 会被归到 OLD_RAR / ZIP_SPLIT 命名方案里（为了能配对
        老式 `.r00` 和 `.z01`），所以**单个** .rar 或 .zip 的 kind 看着像分卷，
        实际不是——判"是否分卷"必须看这一组里有没有别的成员。
        """
        return self.count > 1


def group_volumes(paths: Iterable[str]) -> list[VolumeGroup]:
    """把一批文件按分卷归组，每组只保留主卷。

    这是"修复了多卷压缩包重复解压"的关键：同一分卷组只会产出 1 个任务。
    """
    buckets: dict[tuple[str, VolKind], list[VolumeInfo]] = {}
    for p in paths:
        info = classify_volume(p)
        if info.kind is VolKind.NONE:
            continue
        key = (os.path.normcase(info.base), info.kind)
        buckets.setdefault(key, []).append(info)

    groups: list[VolumeGroup] = []
    for (base, kind), infos in buckets.items():
        infos.sort(key=lambda i: i.index)
        main_info = next((i for i in infos if i.is_first), infos[0])
        # 还原主卷的真实文件名（VolumeInfo 只存了 base + index，需要回查）
        groups.append(
            VolumeGroup(
                base=base,
                kind=kind,
                main=_resolve_main_path(main_info, paths),
                others=[i.base for i in infos if i is not main_info],
            )
        )
    return groups


def _resolve_main_path(info: VolumeInfo, paths: Iterable[str]) -> str:
    """在原始路径列表里找出该组的主卷文件（按规则匹配）。"""
    for p in paths:
        vi = classify_volume(p)
        if vi.base == info.base and vi.kind == info.kind and vi.index == info.index:
            return str(p)
    return info.base


def missing_volume_note(path: str | os.PathLike[str]) -> str | None:
    """这看着是分卷**主卷**、但下一卷不在场？返回缺的那一卷文件名，否则 None。

    `x.part1.rar` 这种命名本身就是"多卷集合"的标志（WinRAR 只在多卷模式下这么命名），
    所以"主卷在、第二卷不在"= 分卷不全。这种包问用户要密码是白问——
    真正该做的是把分卷凑齐，至少得说清缺哪一卷（比"密码已全部试完"清楚得多）。
    """
    info = classify_volume(path)
    if info.kind is not VolKind.PART or not info.is_first:
        return None
    folder = os.path.dirname(str(path)) or "."
    try:
        entries = [e.path for e in os.scandir(folder) if e.is_file()]
    except OSError:
        return None
    for e in entries:
        vi = classify_volume(e)
        if (vi.kind is info.kind and vi.index == info.index + 1
                and os.path.normcase(vi.base) == os.path.normcase(info.base)):
            return None
    return f"{os.path.basename(info.base)}.part{info.index + 1}.rar"


def main_volume_of(path: str, siblings: Iterable[str]) -> str | None:
    """给定一个文件，若它是分卷成员，返回同组主卷路径；否则 None。

    用于 UI：用户把 part2.rar 拖进来，也能自动纠正到 part1.rar。
    """
    info = classify_volume(path)
    if info.kind is VolKind.NONE:
        return None
    candidates = [str(s) for s in siblings]
    for g in group_volumes(candidates):
        if g.kind is info.kind and os.path.normcase(g.base) == os.path.normcase(info.base):
            return g.main
    return None


def describe(fmt: Fmt, vol: VolumeInfo | None = None) -> str:
    """给 UI 用的中文描述。"""
    label = {
        Fmt.ZIP: "ZIP 压缩包",
        Fmt.RAR: "RAR 压缩包",
        Fmt.RAR5: "RAR5 压缩包",
        Fmt.SEVENZ: "7z 压缩包",
        Fmt.TAR: "TAR 归档",
        Fmt.GZIP: "GZip 压缩",
        Fmt.LZ4: "LZ4 压缩",
        Fmt.LZ5: "LZ5 压缩",
        Fmt.ZSTD: "Zstd 压缩",
        Fmt.BROTLI: "Brotli 压缩",
        Fmt.MP4: "视频（非压缩包）",
        Fmt.UNKNOWN: "未知格式",
    }[fmt]
    if vol and vol.is_split:
        label += f" · 分卷 {vol.index}"
    return label
