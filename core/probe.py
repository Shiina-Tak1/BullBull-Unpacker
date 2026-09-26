"""格式探测：不信任扩展名，只认 magic bytes。

核心职责：
  1. 判断一个文件「真实」是什么格式（含"伪装成 mp4 的 zip"）
  2. 找出"前面垫了真视频、后面接压缩包"的内嵌包（示例.mp4 那种）
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
# 这几个是"帧"格式（小端 magic）。实测样本 `示例.rar.lz4` 就是 LZ4 帧里包了一个 rar。
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


def _read_head(path: str | os.PathLike[str]) -> tuple[bytes | None, str]:
    """读文件头 512 字节 → `(head, 读不了的原因)`；读不了时 `head is None`。

    单独抽出来是为了让「**读不了**」和「**读到了、但不是压缩包**」能分开
    （`CAND-001`：以前两者都塌成 `Fmt.UNKNOWN`，于是"个别文件读不动"被静默当成
    "不是压缩包"，又是一条静默漏解）。
    """
    try:
        with open(path, "rb") as fh:
            return fh.read(512), ""
    except OSError as exc:
        return None, f"{type(exc).__name__}: {exc}"


def _format_of_head(head: bytes, path: str | os.PathLike[str]) -> Fmt:
    """文件头 → 格式（只看 magic，不碰盘）。"""
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


def detect_format(path: str | os.PathLike[str]) -> Fmt:
    """读取文件头判断真实格式。文件不可读时返回 UNKNOWN。

    ⚠ **"读不了"和"真不是压缩包"都会得到 `UNKNOWN`** —— 这个函数只回答"格式是什么"。
    要区分这两种情况的调用方用 `archive_candidate()`（它把"读不了"单独报出来，
    见 `CAND-001`）。

    ⚠ 读之前先挡保留设备名：`open("CON", "rb")` 在会做 DOS 设备映射的系统上会
    **永久阻塞**（详见 `is_device_path` 上面那段）。这里返回 UNKNOWN 就够 ——
    上层会把它当"不是压缩包"，日志里由调用方补 `describe_reserved()` 说明原因。
    """
    if is_device_path(path):
        return Fmt.UNKNOWN
    head, _why = _read_head(path)
    if head is None:
        return Fmt.UNKNOWN
    return _format_of_head(head, path)


def archive_candidate(path: str | os.PathLike[str]) -> tuple[bool, str]:
    r"""这个文件**该不该当成候选包**？→ `(是候选, 要报给用户的一句话)`。

    扫描与穿透共用这一份判据（`B-2026-060`）。以前判据是**两份**、而且不对称：

    * 单文件分支（`pipeline.scan`）有一条兜底 `ext not in ARCHIVE_EXTS` —— 名字在名单里
      就放行，所以**单独拖进来的 `.cab` 能解**；
    * 文件夹分支（`candidates_from` / `_archives_in`）只认 `detect_format().is_archive`
      —— 没有这条兜底，所以**同一个 `.cab` 放进文件夹被静默漏解**，还报「完成」。

    现在的判据（顺序有意义）：

      1. **保留设备名**（`CON` / `COM1`…）→ 不是候选。**绝不 open**（会永久阻塞）；
      2. magic 认得出压缩包 → 是候选；
      3. **读不了**（权限 / 占用 / 坏道）→ 不是候选，但**第二项返回一句原因**。
         ★ `CAND-001`：读不了 ≠ "不是压缩包"，不许静默当成"不是包"咽下去；
      4. 名字的扩展名在 `ARCHIVE_EXTS` 名单里（`cab` / `bz2` / `xz` / `iso` / `arj`…）
         → **是候选**，交给引擎去试。这就是原来只有单文件分支才有的那条兜底；
         名单本来就是"用户以为这是压缩包"的判据，猜错也只是引擎报一句错（看得见），
         比静默丢掉诚实得多。

    说明：`.zip` / `.rar` 这一类**本来**就走名字（`classify_volume` 把它们归成分卷方案，
    `group_volumes` 会收下），所以"按名字当候选"不是新发明 —— 这条只是把 zip/rar
    已有的行为推广到名单里其余后缀，消掉不对称本身。
    """
    p = str(path)
    if is_device_path(p):
        return False, f"⚠ {os.path.basename(p) or p} 是 Windows 保留设备名，已跳过"
    head, why = _read_head(p)
    if head is None:
        return False, (f"⚠ 读不了，无法判断是不是压缩包（{why}）："
                       f"{os.path.basename(p) or p}")
    if _format_of_head(head, p).is_archive:
        return True, ""
    if ext_of(p) in ARCHIVE_EXTS:
        return True, ""
    return False, ""


def is_disguised(path: str | os.PathLike[str]) -> bool:
    """内容像压缩包，但扩展名不是压缩包 —— 即"伪装"。

    例：教程视频.mp4 实际是 ZIP → True

    判据用 `ARCHIVE_EXTS`（**不在这里另抄一份名单**）。以前这里写的是
    `("zip","rar","7z","tar","gz","001","z01")` 七个，于是 `.tgz` / `.tar.zst` /
    `.tzst` / `.tar.lz4` 这些**扩展名本来就是压缩包**的文件因为"内容有 magic、名字不在那七个里"
    被标成「伪装成视频」（`B-2026-061`）。判据与"这个后缀算不算压缩包"只能有一份出处，
    否则下次加格式又要两边都改。
    """
    fmt = detect_format(path)
    if not fmt.is_archive:
        return False
    return ext_of(path) not in ARCHIVE_EXTS


def ext_of(path: str | os.PathLike[str]) -> str:
    """取扩展名（小写、不含点）。无扩展名返回空串。"""
    name = os.path.basename(str(path))
    _, dot, ext = name.rpartition(".")
    if not dot or dot == name:
        return ""
    return ext.lower()


# --------------------------------------------------------------------------
# Windows 保留设备名守卫（2026-09-21，攻击审计 BUG-1）
#
# 为什么要它：在会做 DOS 设备映射的系统上，`open("CON.zip", "rb")` 拿到的是
# **控制台设备**而不是文件，`read()` 会**永久阻塞** —— 而且阻塞在 syscall 里，
# 引擎层那个 100ms 轮询的取消机制、Runner 的任务间取消标志，**都没机会执行**。
# 在探测之前挡掉它，就是把「永久卡死、取消无效」换成「一句能看懂的失败」。
#
# 为什么还要连 `os.path.isfile` 一起判（`is_device_path`）：新系统（本机 Win11
# build 28000 实测）已经把「名字.扩展名」当普通文件了，**只看名字**会把用户完全
# 能读的 `CON.zip` / `COM1.zip` 误判成设备（实测 14/14 误伤）。反过来，真走设备时
# `stat` 给的是字符设备（实测 `st_mode == 0o20000`），`isfile` 必为 False。
# 两边都判，既不误伤也不漏。
# --------------------------------------------------------------------------

# 8.3 时代沿用到今天的设备名。依据微软《Naming Files, Paths, and Namespaces》与
# ntdll!RtlIsDosDeviceName_U 的文档列表（该函数明确列出 CONIN$ / CONOUT$）。
#   * **没有 COM0 / LPT0** —— 文档只写 1-9，本机实测 RtlIsDosDeviceName_U("COM0") == 0；
#   * 上标数字（COM¹）也算 —— 本机实测 RtlIsDosDeviceName_U("COM¹") 非 0。
_RESERVED_DEVICES = frozenset(
    ("CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$")
    + tuple(f"COM{i}" for i in range(1, 10))
    + tuple(f"LPT{i}" for i in range(1, 10))
    + tuple(f"COM{c}" for c in "¹²³")
    + tuple(f"LPT{c}" for c in "¹²³")
)


def reserved_device_of(path: str | os.PathLike[str]) -> str | None:
    """这个路径的名字是不是 Windows 保留设备？是就返回设备名（如 `"CON"`），否则 None。

    判定规则（按 8.3 的「名字.扩展名」形式，大小写不敏感）：
      * **只看 basename** —— 目录里叫 CON 与这里无关（现代 Windows 只判最后一段）；
      * 以**第一个点**为界取前缀（所以 `CON.tar.gz` 也命中）；
      * 前缀去掉**尾随空格**（`CON .zip` 命中）；
      * 整个名字去掉尾随点/空格后仍命中（`CON.` / `CON ` 命中）。

    刻意**不用** `os.path.isreserved()`：那是给"解压时防路径穿越"用的保守判据，
    它额外把「尾随点/空格」「`*?"<>|` 与控制字符」「**路径里任何一段**是设备名」
    都算命中 —— 拿它当硬拒绝会误伤 `report..txt` 和 `D:\\下载\\CON\\资料.zip`
    这类完全正常的输入（本机实测两者都能正常读）。
    """
    raw = os.fspath(path)
    if isinstance(raw, bytes):
        raw = os.fsdecode(raw)
    # ★ 先剥掉**设备命名空间前缀** `\\.\` / `\\?\`：
    #   `ntpath.basename(r"\\.\CON")` 会返回**空串**（`splitdrive` 把整个 `\\.\CON`
    #   当成"盘符"了），于是守卫会漏判 —— 而 `os.path.abspath("CON")` 产出的**正是**
    #   这个形式（本机实测 `abspath("CON") == "\\\\.\\CON"`）。漏了它的后果就是
    #   "裸名 CON 明明挡得住，走一趟 abspath 又挡不住了"（CLI 上真踩过）。
    for prefix in ("\\\\?\\", "\\\\.\\"):
        if raw.startswith(prefix):
            raw = raw[len(prefix):]
            break
    name = os.path.basename(raw) or raw
    if not name:
        return None

    stem = name.split(".", 1)[0].rstrip()
    if not stem:
        stem = name.strip()
    if not stem:
        return None
    if stem.upper() in _RESERVED_DEVICES:
        return stem.upper()

    stripped = name.rstrip(". ")
    if stripped:
        s = stripped.split(".", 1)[0].rstrip().upper()
        if s in _RESERVED_DEVICES:
            return s
    return None


def is_device_path(path: str | os.PathLike[str]) -> bool:
    """这个路径**现在**会走到设备而不是文件吗？（读文件之前用这一个守卫就够）

    名字命中保留设备**且**它不是一个真实文件 → True。
    `os.stat` 在说不清时（OSError）按"危险"处理：宁可明确失败，也不要无限等待。
    """
    if reserved_device_of(path) is None:
        return False
    try:
        return not os.path.isfile(path)
    except OSError:
        return True


def describe_reserved(name: str) -> str:
    """给界面/日志用的一句话（调用方拿去当失败原因）。"""
    return (f"{name} 是 Windows 保留设备名（DOS 设备），磁盘上读不到它——"
            f"在部分系统上读它会永久卡住，已跳过")


# --------------------------------------------------------------------------
# 内嵌压缩包：文件头不是压缩包，但**身体里**藏着一个
# --------------------------------------------------------------------------
#
# 实盘最常见的一种伪装不是"改扩展名"，而是"前面垫一段真视频，后面接压缩包"：
# 文件能正常播放（播放器读到 moov 就完事），而压缩包完整地躺在尾部。
# 实测样本 示例.mp4 = 534MB 真视频 + 从 534691227 字节处开始的完整 ZIP。
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

    实测样本（示例.mp4 里嵌的那个 RAR5）：HEAD_SIZE=33、vint 占 1 字节，
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


def zip_absolute_layout(path: str | os.PathLike[str]) -> bool:
    r"""尾部有**自洽的** ZIP 中央目录，但起点推不出来 —— 也就是"包内偏移是绝对的"那种布局。

    这是 `示例视频.mp4` 那类文件的形状（`B-2026-093` 起）：EOCD 说得出
    中央目录在哪、那条 `PK\x01\x02` 也对得上，可是 `cd_pos - cd_off` 算出来的起点落在文件外
    （`base = 0`）—— 因为包里的偏移写的是**文件绝对位置**，不是"从包起点算"。这类文件
    7-Zip 自己能直读（报 `Embedded Stub Size`），于是：

      * **不该切包**（切掉假头会让包内绝对偏移整体错位）；
      * **也不该为它全盘扫**（`_scan_magic` 只认 RAR5/7z 头部，ZIP 不在扫描范围内 ——
        扫了必然是 `None`；真样本 6.16GB 扫一遍 6.7s，纯粹白等，`B-2026-096`）。

    只读尾部 `EMBED_TAIL`（4MB），**毫秒级** —— 用来替代"起一次 7z 问引擎"（0.06~0.13s）
    或"整盘读一遍"（GB 级）那种昂贵判据。判据**只认这一种布局**：真视频、普通内容文件、
    以及起点推得出来的垫片包一律 `False`（它们分别走全盘扫与尾部定位，覆盖面不变）。
    ZIP64 的绝对偏移形态也一样认（`cd_off` 溢出、真值在 ZIP64 EOCD 记录里 —— 真样本
    `雨瀬みゆ…ver 1.10.mp4` 6.16GB 就是这一种）。
    """
    full = str(path)
    if detect_format(full).is_archive:
        return False          # 头就是压缩包：那是另一条通道（`archive_candidate`）的事
    try:
        size = os.path.getsize(full)
        if size < EMBED_MIN_SIZE:
            return False
        start = max(0, size - EMBED_TAIL)
        with open(full, "rb") as fh:
            fh.seek(start)
            tail = fh.read()
    except OSError:
        return False

    pos = len(tail)
    while True:
        i = tail.rfind(b"PK\x05\x06", 0, pos)
        if i < 0:
            return False
        pos = i
        rec = tail[i:i + 22]
        if len(rec) < 22:
            continue
        cd_size, cd_off = struct.unpack("<II", rec[12:20])
        if cd_off == 0xFFFFFFFF:
            # ZIP64：偏移真值在 ZIP64 EOCD 记录里。**绝对偏移布局在这里同样自洽** ——
            # 记录里的 `cd_off` 本身就是中央目录的**绝对位置**（实测雨瀬みゆ 6.16GB：
            # cd_off64=6618119778 处正是 `PK\x01\x02`），而反推起点 = 0（被非法性检查否掉）。
            loc = tail.rfind(b"PK\x06\x07", max(0, i - 128), i)
            z64 = tail.rfind(b"PK\x06\x06", 0, loc) if loc >= 0 else -1
            if z64 >= 0 and z64 + 56 <= len(tail):
                cd_off64 = struct.unpack("<Q", tail[z64 + 48:z64 + 56])[0]
                if _read_at(full, cd_off64, 4) == b"PK\x01\x02":
                    z64_off = struct.unpack("<Q", tail[loc + 8:loc + 16])[0]
                    base = (start + z64) - z64_off        # 与 `_zip64_base` 同一个算法
                    if not (0 < base < size):
                        return True
            continue
        if cd_size in (0, 0xFFFFFFFF):
            continue          # 空目录 / 大小另存：这里判不了，交给别的路径
        abs_eocd = start + i
        cd_pos = abs_eocd - cd_size
        if cd_pos <= 0 or _read_at(full, cd_pos, 4) != b"PK\x01\x02":
            continue          # 中央目录自己对不上 = 视频数据里偶然出现的那几个字节
        base = cd_pos - cd_off
        if 0 < base < size:
            if _read_at(full, base, 4) in ZIP_MAGICS:
                return False  # 起点处真是本地头 → 正常布局或垫片布局，不是这一类
            continue          # 起点不是头部：继续往前找（与 `_zip_base_in_tail` 同一条链）
        return True           # 中央目录自洽、起点却非法 = 绝对偏移布局


# 「内嵌包签名 → 格式 → 头部校验」的唯一一张表：`_magic_in()` 一处使用，
# `_scan_magic()`（全盘）与 `tail_magic()`（尾部窗口）两个窗口共用同一份判据。
_MAGIC2FMT = ((RAR5_MAGIC, Fmt.RAR5, _rar5_ok), (SEVENZ_MAGIC, Fmt.SEVENZ, _sevenz_ok))


def _magic_in(data: bytes, base: int, path: str) -> tuple[Fmt, int] | None:
    r"""在 `data`（起始绝对偏移 `base`）里找第一个**通过头部 CRC 校验**的内嵌包签名。

    只认 RAR5 / 7z 的**明文签名**：它们的头部 CRC 不需要密码就能校验，所以 `-hp` /
    `-mhe=on` 那种加密头的包**也认得出来**（这正是"尾部窗口扫"能覆盖加密头包的原因，
    `B-2026-097`）。返回 `(格式, 绝对偏移)`；`absolute <= 0` 的不要 —— 那种是"文件头
    本身就是包"，走 `archive_candidate()` 那条通道，不归"内嵌"管。

    抽出来是为了让两个窗口用**同一份**判据：各写一遍的话，迟早会像 `B-2026-093`
    那样只修一处（ZIP32 修了、ZIP64 漏了）。
    """
    for magic, fmt, checker in _MAGIC2FMT:
        at = data.find(magic)
        while at >= 0:
            absolute = base + at
            if absolute > 0:
                # 跨块 / 跨窗口边界时 data 里可能不够校验，重新按绝对偏移读一段
                head = data[at:at + 64]
                if len(head) < 64:
                    head = _read_at(path, absolute, 64)
                if checker(head):
                    return fmt, absolute
            at = data.find(magic, at + 1)
    return None


def _scan_magic(path: str, size: int, cancel: Callable[[], bool] | None = None):
    """全盘扫描 RAR5 / 7z 签名，返回第一个通过头部 CRC 校验的 (格式, 偏移)。

    ⚠ **代价 = O(包离文件头的距离)，不是 O(文件大小)** —— 命中即返回。实测（512MB 文件）：
    包在 36B 处 **0.008s**、包在 256MB 处 **0.261s**、**文件里没有包才是 0.519s**（整盘读完）。
    所以它真正贵的那一半是"压根没有包"，而"包紧贴文件尾"的追加式伪装也要先读完整个文件
    才找到 —— `tail_magic()` 就是为这两类加的廉价前置（`B-2026-097`，见 §14.8）。
    """
    longest = max(len(m) for m, _, _ in _MAGIC2FMT)

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
            hit = _magic_in(data, base, path)
            if hit is not None:
                return hit
            carry = data[-longest:]
            offset += len(buf)


def tail_magic(path: str | os.PathLike[str], *,
               cancel: Callable[[], bool] | None = None) -> Embedded | None:
    """只在**尾部 `EMBED_TAIL`（4MB）窗口**里找 RAR5 / 7z 签名 —— 毫秒级。

    为什么需要它（`B-2026-097` 实测，§14.8）：`_scan_magic()` 从头扫、命中即返回，
    于是"真视频 + 尾部追加一个 RAR5/7z"要**把整个文件读完**才找到；"真视频里根本没有包"
    更是纯粹白读（512MB→0.52s、1GB→1.25s）。可是追加式伪装的**包起点就在尾部附近** ——
    换个窗口去扫，代价恒定 4MB、实测 2~4ms。

    ⚠ 它**不是**"深扫的替代品"，是深扫前面的**廉价筛子**：
      * 命中 → 直接拿到切片起点，省掉整盘读（连 `-hp` 加密头那类也认得出 ——
        签名与头部 CRC 都是明文，不需要密码）；
      * 不命中 → **不能断定"里面没有包"**（包可能在文件中段，尾部窗口够不着）。
        该不该继续深扫由调用方按预算决定，所以 `deep=True` 的通道照旧保留深扫兜底。

    与 `zip_absolute_layout()` 的分工（两个都是"读尾部 4MB"的毫秒级判据，互补不重叠）：
    那个判"尾部有自洽的 ZIP 中央目录、但起点推不出来" → **不切包、直读**；
    这个判"尾部窗口里有 RAR5/7z 明文签名" → **要切包**。
    """
    full = str(path)
    if detect_format(full).is_archive:
        return None                      # 头就是包：走 `archive_candidate()` 那条通道
    try:
        size = os.path.getsize(full)
    except OSError:
        return None
    if size < EMBED_MIN_SIZE:
        return None
    if cancel is not None and cancel():
        return None
    start = max(0, size - EMBED_TAIL)
    try:
        with open(full, "rb") as fh:
            fh.seek(start)
            data = fh.read()
    except OSError:
        return None
    hit = _magic_in(data, start, full)
    if hit is None:
        return None
    return Embedded(hit[0], hit[1], "尾部签名")


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


@dataclass(frozen=True)
class CarrierProbe:
    """一个"马甲文件"值不值得处理、里面有没有包 —— **判据只此一份**（`B-2026-097`）。

    以前回答这件事的代码有 **5 处**、各用各的判据组合（界面扫描 / 子目录报备 / 选目标 /
    处理目标 / `cli --probe`），于是同一个文件换个入口就换一个答案：能解的包被判成
    「文件夹里没有可解压的压缩包」、选目标选不中而处理目标解得开。收敛之后：
    **探测只在这一个函数里做（`inspect_carrier`），调用方只声明自己愿付到哪一档代价，
    然后读字段** —— 而不是各自决定"要不要问引擎 / 要不要深扫"。

    ★ **两个不同的问题，别塞进同一个判断**（§0.4「伪装 mp4 的两种形态」）：
      * `carrier_like`：文件**本身**像不像马甲（扩展名家族 + 体积，最便宜的前置闸）。
        `False` 时下面几个字段一律无意义。
      * `embedded`：文件**里**确实有一个内嵌包、从哪开始 —— 有偏移就能切包。
      * `absolute_layout`：包内偏移是**绝对**的（起点推不出来）→ **不许切包**：
        切掉假头会让包内所有偏移整体错位，只能把原文件交给引擎直读。
      * `engine_readable`：引擎能把这个文件**当压缩包直接读**（`Extractor.can_read`）。
        注意它说的是"读得了"，**不是"解得开"** —— 加密的包照样要密码，那一步在
        `vault` / `pierce` 里，不在这里。
    """

    carrier_like: bool
    embedded: Embedded | None = None
    absolute_layout: bool = False
    engine_readable: bool = False

    @property
    def worth_handling(self) -> bool:
        """值不值得**收成候选 / 交给穿透**：三条通道任意一条命中就算。

        注意 `embedded`（切包解）与 `absolute_layout` / `engine_readable`（直读原文件）
        是两种不同的处理方式，但对"要不要管它"这个问题的答案是同一个 —— 这正是
        `B-2026-095`（选目标选不中、处理目标却解得开）的成因。
        """
        return bool(self.embedded or self.absolute_layout or self.engine_readable)

    @property
    def direct(self) -> bool:
        """**别切包、原样直读**（包内偏移是绝对的，或引擎自己会跳过假头）。"""
        return bool(self.absolute_layout or self.engine_readable)

    @property
    def how(self) -> str:
        """怎么找到的（给人看；与 `Embedded.how` 同一份口径）。"""
        if self.embedded is not None:
            return self.embedded.how
        if self.absolute_layout:
            return "尾部绝对偏移布局"
        if self.engine_readable:
            return "引擎直读"
        return ""


def inspect_carrier(
    path: str | os.PathLike[str],
    *,
    engine_probe: Callable[[str], bool] | None = None,
    deep: bool = False,
    cancel: Callable[[], bool] | None = None,
) -> CarrierProbe:
    """**唯一入口**：这个文件是不是马甲、里面有没有包、值不值得处理（`B-2026-097`）。

    判据按**代价从低到高**排，命中即返回（`CarrierProbe.how` 说明是哪一档找到的）：

    | 档 | 判据 | 代价 | 覆盖 |
    |---|---|---|---|
    | 0 | `looks_like_carrier()` | stat + 扩展名 | 前置闸：不在马甲家族就直接退出 |
    | 1 | `find_embedded(deep=False)` | 读尾部 4MB | 垫片 / 相对偏移 ZIP（尾部目录定位） |
    | 2 | `zip_absolute_layout()` | 读尾部 4MB | 假头 + 绝对偏移 ZIP（**直读**，不切包） |
    | 3 | `engine_probe()` | ~0.06~0.13s | "头是假、身体是真包"且包离文件头 ≤8MB 的一切 |
    | 4 | `tail_magic()` | 读尾部 4MB | **尾部追加**的 RAR5/7z（含 `-hp` 加密头那类） |
    | 5 | `find_embedded(deep=True)` | 1~7s/GB | 兜底：包藏在文件**中段**、尾部没有任何结构 |

    ★ **`engine_probe` 传不传，是调用方的代价决策，不是这个函数的事**：
      * 传（`pipeline._archives_in` / `pierce.carve_source` / `cli --probe`）→ 覆盖面最全，
        每个马甲文件多 0.06~0.13s；
      * 不传（`pierce._carrier_candidates` / `probe.suspected_carriers`）→ 一个进程都不起。
        ⚠ 那两个是**按目录里每个 ≥1MB 文件**跑的（§20.21 坑②）：一屋子真视频就是 N×0.1s
        的进程开销（§14.13/§14.14 记的"拖 mp4 卡顿"）。它们靠 0/1/2/4 四档**廉价**判据
        挡掉绝大多数，**不许**图省事把引擎传进来。

    ★ **`deep` 同样由调用方声明**：只在"这一层没有别的候选可解"时才该为 True
    （`pierce.pick_target` 的 `deep=not merged`）。档 4 已经把"尾部追加"那一大类从深扫里
    救出来了，所以深扫现在只剩"包在文件**中段**"这一种情形。

    ★ **顺序是有约束的，不许随手调换**：
      * `engine_probe` **必须**排在档 1、2 **之后**（`B-2026-094`，§20.21）——
        "垫片 + 相对偏移"的包（档 1 能推出切片起点）引擎**也**能直读，把引擎判据提前会让
        那些包从"切包后解"变成"直读原文件"，那是拿正确性换速度（回归里有对照断言盯着）。
      * 档 4 排在 `engine_probe` **之后**，是为了**不改变现有行为**：包离文件头 ≤8MB 时
        引擎本来就够得着（"小假头 + 一整个 7z"就是这一类），照旧按"引擎直读"处理；
        只有引擎够不着的（包在 >8MB 处，实测边界见 §14.8）才落到尾部窗口那条路。
        ⚠ 若 `engine_probe` 没传（廉价通道），档 4 自然顶上来 —— 那正是"没有引擎也要
        不漏掉尾部追加包"的兜底，不是行为漂移。
    """
    full = str(path)
    if not looks_like_carrier(full):
        return CarrierProbe(carrier_like=False)

    emb = find_embedded(full, deep=False, cancel=cancel)
    if emb is not None:
        return CarrierProbe(carrier_like=True, embedded=emb)

    if zip_absolute_layout(full):
        return CarrierProbe(carrier_like=True, absolute_layout=True)

    if engine_probe is not None and engine_probe(full):
        return CarrierProbe(carrier_like=True, engine_readable=True)

    emb = tail_magic(full, cancel=cancel)
    if emb is not None:
        return CarrierProbe(carrier_like=True, embedded=emb)

    if deep:
        emb = find_embedded(full, deep=True, cancel=cancel)
        if emb is not None:
            return CarrierProbe(carrier_like=True, embedded=emb)

    return CarrierProbe(carrier_like=True)


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
    """归一化：用于"按文件名做相似度比较"。

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
    main: str                       # **首卷**完整路径 —— 只把这个送进引擎
    others: list[str] = field(default_factory=list)
    # 首卷（`main`）**真的在场**吗？（`B-2026-079`）
    # 用户只下到一部分分卷时（`x.part2.rar` / `x.z01` / `x.r00`），这一组**没有**首卷：
    # 这时 `main` 指向的是"应该在的那个名字"（一个**不存在**的路径），`first_present=False`。
    # 以前这里没有这个状态，`group_volumes` 只能把 `main` 退化成 `infos[0]`（一个存在的
    # 次卷）—— 于是"缺主卷"在数据模型里**无法表达**，`main_volumes` 那道守卫恒为假。
    first_present: bool = True

    @property
    def count(self) -> int:
        """这一组**在场**的卷文件有几个（首卷缺席时不算它）。"""
        return len(self.others) + (1 if self.first_present else 0)

    @property
    def is_split(self) -> bool:
        """真的是分卷吗？只有 1 个成员时不算。

        `.rar` / `.zip` 会被归到 OLD_RAR / ZIP_SPLIT 命名方案里（为了能配对
        老式 `.r00` 和 `.z01`），所以**单个** .rar 或 .zip 的 kind 看着像分卷，
        实际不是——判"是否分卷"必须看这一组里有没有别的成员。

        ⚠ 它**不是**"能不能送进引擎"的判据（`B-2026-079`）：只有单个**次卷**时
        `count == 1` → 这里为假，可那一组恰恰是解不开的。该判的是 `first_present`。
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
        main_info = next((i for i in infos if i.is_first), None)
        # ★ **首卷不在场**（用户只下到 `part2` / `z01` / `r00`）时，`main` 指向"应该在的
        #   那个名字"——一个**不存在**的路径，并记下 `first_present=False`（`B-2026-079`）。
        #   以前这里退化成 `infos[0]`（一个**存在**的次卷），于是"缺主卷"在数据模型里
        #   无法表达：`main_volumes` 的 `os.path.isfile(g.main)` 恒为真，次卷被当主卷
        #   送进引擎，用户看到「引擎读不出这个包（不是密码问题）：可能不是压缩包，
        #   或者文件已损坏」—— 完全错误的方向。
        groups.append(
            VolumeGroup(
                base=base,
                kind=kind,
                # 还原主卷的真实文件名（VolumeInfo 只存了 base + index，需要回查）
                main=(_resolve_main_path(main_info, paths) if main_info is not None
                      else _volume_name(infos[0].base, kind, 1)),
                others=[i.base for i in infos if i is not main_info],
                first_present=main_info is not None,
            )
        )
    return groups


def _resolve_main_path(info: VolumeInfo, paths: Iterable[str]) -> str:
    """在原始路径列表里找出该组的主卷文件（按规则匹配）。

    ⚠ **必须连 `is_first` 一起比**（`B-2026-048`）：`x.zip` 与 `x.z01` 的
    `(base, kind, index)` 完全一样（`.z01` 的序号也是 1），只按那三个比就会返回
    **列表里先出现的那个** —— `.z01` 排在前面时"主卷"就成了次卷，
    喂给引擎必然解不开（7z 打不开非首卷）。
    """
    for p in paths:
        vi = classify_volume(p)
        if (vi.base == info.base and vi.kind == info.kind
                and vi.index == info.index and vi.is_first == info.is_first):
            return str(p)
    return info.base


def _volume_name(base: str, kind: VolKind, seq: int) -> str:
    """这一组里**第 `seq` 卷**该叫什么文件名（`seq` 从 1 起，1 = 首卷）。

    为什么要单独一条规则（`B-2026-079`）：`VolumeInfo` 只存了 `base + index`，而 index
    对 `.z01` / `.r00` 与各自的主卷是**撞号**的（`.z01` 的 index 也是 1、`.r00` 的 index
    是 1，见 `classify_volume`）—— 它们只是排序用的，不能反推"第 N 卷叫什么"。
    主卷不在场时要用它把"**缺的那一卷**"如实说出来，所以按命名方案各写一条。
    """
    if kind is VolKind.PART:
        return f"{base}.part{seq}.rar"
    if kind is VolKind.NUMERIC:
        return f"{base}.{seq:03d}"
    if kind is VolKind.ZIP_SPLIT:
        return f"{base}.zip" if seq == 1 else f"{base}.z{seq - 1:02d}"
    if kind is VolKind.OLD_RAR:
        return f"{base}.rar" if seq == 1 else f"{base}.r{seq - 2:02d}"
    return base


def _volume_seq(info: VolumeInfo) -> int:
    """这一卷在**组内**的卷序号（1 = 首卷）。

    `.z01` 是第 2 卷、`.r00` 是第 2 卷（首卷分别是 `.zip` / `.rar`）—— 而
    `classify_volume` 给它们的 `index` 与首卷撞号，所以在这里按命名方案换算一次。
    """
    if info.kind in (VolKind.ZIP_SPLIT, VolKind.OLD_RAR):
        return 1 if info.is_first else info.index + 1
    return info.index


_RAR5_SIG = b"Rar!\x1a\x07\x01\x00"
_RAR5_HEAD_FILE = 2
_RAR5_HFL_SPLITAFTER = 0x0010


def _rar5_vint(buf: bytes, i: int) -> tuple[int, int]:
    """RAR5 的可变长整数：7 位一组、**大端**，最高位是"还有后续字节"标志。"""
    val = 0
    while i < len(buf):
        b = buf[i]
        i += 1
        val = (val << 7) | (b & 0x7F)
        if not b & 0x80:
            return val, i
    return -1, i


def _rar5_has_more_volumes(path: str) -> bool:
    """这一卷的卷头声明"后面还有卷"吗（RAR5）？

    ★ 这是**唯一**能判「缺的是**最后一卷**」的凭据（`B-2026-079`）：文件名里没有
    "总共有几卷"这个信息，`part1..3` 连续到场时，光看名字看不出还差一个 `part4`
    —— 而那时引擎连包都列不出来，会走到「引擎读不出这个包（不是密码问题）」。
    RAR5 的 **FILE header** 在**非末卷**上置 `HFL_SPLITAFTER`（0x0010）、末卷不置
    （实测 WinRAR 6.x 造的 4 卷包：`part1/2/3` 的 FILE flags = 0x13/0x1b/0x1b，
    `part4` = 0x0b）。

    ⚠ **读不到 / 不是 RAR5 / 结构不对 → 一律 False**（如实不猜）：RAR4 的 `.partN.rar`
    与 7z 分卷没有这个标志，它们的"缺末尾卷"只能由引擎输出兜底
    （`engine.looks_like_missing_volume`）。**别把"判不出来"写成"没缺"以外的任何结论**。
    """
    try:
        with open(path, "rb") as f:
            buf = f.read(256)
    except OSError:
        return False
    if buf[:8] != _RAR5_SIG:
        return False
    pos = 8                              # 跳过 8 字节签名
    while pos + 4 < len(buf):
        p = pos + 4                      # 跳过 header 的 CRC32
        size, p = _rar5_vint(buf, p)     # HeaderSize（从 HeaderType 起算）
        if size <= 0:
            return False
        htype, q = _rar5_vint(buf, p)
        flags, _ = _rar5_vint(buf, q)
        if htype == _RAR5_HEAD_FILE:
            return bool(flags & _RAR5_HFL_SPLITAFTER)
        # 主头不带数据区（HFL_DATA 未置位），跳到下一个 header 即可
        pos = p + size
    return False


# --------------------------------------------------------------------------
# 「包在哪」：目录里的候选包 + 向下找
#   ★ **扫描（pipeline）与穿透（pierce）共用这一份判据** —— 以前两边各写一份，
#     已经漂移到"界面列出来的"和"实际执行时解的"不是同一批（B-2026-035）。
# --------------------------------------------------------------------------

# 往下找包的**保险丝：按目录数**，与"包层"（用户的 max_depth）正交（B-2026-033 成因①）。
# 为什么不用目录深度：一个包藏在 `d1/…/d7` 里和藏在 `d1` 里，对用户是同一件事 ——
# 目录有多深跟"要不要继续解"无关。旧版那条 `max_scan=6` 的**深度**限制会让第 7 层的
# 内层包被静默漏解，而把「最大嵌套层数」调大也救不了（它管的是包层，不是目录层）。
# 200 这个数只是"别在畸形目录树里遍历到天荒地老"，**不是预算**：烧到它时调用方必须
# 如实说"没往下找完"（`StopReason.SEARCH_INCOMPLETE`），不许报"没有可解压的压缩包"。
NESTED_SCAN_MAX_DIRS = 200


def _say(log: Callable[[str], None] | None, msg: str) -> None:
    """可选的日志通道：没有就静默（判断本身不依赖日志）。"""
    if log is None:
        return
    try:
        log(msg)
    except Exception:                       # noqa: BLE001 - 日志坏掉不能影响判断
        pass


def main_volumes(groups: Iterable[VolumeGroup], *,
                 log: Callable[[str], None] | None = None) -> list[str]:
    """从分卷分组里挑出**可以送进引擎的主卷**。

    **主卷不在场**（用户只拿到 x.part2.rar / x.z01 / x.r00）时跳过并说一声：次卷单独解不了，
    拿它去喂引擎只会得到一句"引擎报错"，把用户引向错误方向。

    这是扫描与穿透共用的**唯一**一处判据（B-2026-035）：
    以前 pipeline 是 `[g.main for g in groups]` 无条件全收，pierce 才排掉缺主卷的组。

    ★ `B-2026-079`：判据是「**这一组的首卷在不在**」（`first_present`），
    **不许**再先看 `is_split` —— "只下到一个次卷"时 `count == 1` → `is_split` 为假，
    以前连这道守卫都进不去，次卷照样被送进引擎、最后报「引擎读不出这个包」。
    首卷不在场就是解不开，与这一组有 1 个还是 N 个成员无关。
    """
    mains: list[str] = []
    for g in groups:
        if not g.first_present:
            _say(log, f"⚠ 缺少主卷 {os.path.basename(g.main)}，{g.count} 个分卷已跳过")
            continue
        mains.append(g.main)
    return mains


def candidates_from(files: Iterable[str], *,
                    log: Callable[[str], None] | None = None,
                    unreadable: list[str] | None = None) -> list[str]:
    """**只看这一层**：从一批文件里挑出可以直接送引擎的候选包（不含子目录）。

    * 分卷只留主卷（`main_volumes`），主卷不在场就跳过；
    * 其余一律走 **`archive_candidate()`** —— magic 认得出、**或者**扩展名在
      `ARCHIVE_EXTS` 名单里（`B-2026-060`：这条兜底以前只有单文件分支有，
      所以 `.cab` 单独拖进来能解、放进文件夹被静默漏解）；
    * **读不了的文件**（`CAND-001`）不算候选，但**必须报出来**：写进 `log`，
      有 `unreadable` 收集器时也记一笔。以前它静默变成 `UNKNOWN` → 被当成"不是压缩包"。

    **伪装包（垫了视频那种）不算候选**：找它要整盘扫，代价高，由调用方在本层单独处理。
    """
    files = [str(p) for p in files]
    if not files:
        return []
    plains: list[str] = []
    for p in files:
        if classify_volume(p).kind is not VolKind.NONE:
            continue
        ok, why = archive_candidate(p)
        if why:
            _say(log, why)
            if unreadable is not None:
                unreadable.append(p)
        if ok:
            plains.append(p)
    return main_volumes(group_volumes(files), log=log) + plains


@dataclass
class DirSearch:
    """一次"往下找包"的结果。

    `truncated` 是关键：它表示**没找完**（保险丝烧了 / 有子目录读不了）。
    调用方看到它必须如实说"没往下找完"，**不许**报「文件夹里已没有可解压的压缩包」
    —— 那正是 B-2026-033 成因① 与 B-2026-037 让用户看到的假象。
    """

    found: list[str] = field(default_factory=list)
    scanned_dirs: int = 0
    truncated: bool = False
    unreadable: list[str] = field(default_factory=list)
    # ★ `B-2026-076`：子目录里**疑似藏了内嵌压缩包**的文件（`suspected_carriers()`
    # 用廉价判据收上来的）。它们**不是**候选（`candidates_from()` 明写"伪装包不算候选"），
    # 但调用方**必须如实报**：一个字节都没解出来还说「文件夹里已没有可解压的压缩包」
    # 是谎话（`PierceResult.partial` / 扫描侧备注都靠这条名单）。
    carriers: list[str] = field(default_factory=list)
    # 用户点了「停止」：与 `truncated`（没找完）**必须分开** —— 取消不是"没找完"，
    # 拿去报 `SEARCH_INCOMPLETE` 会让用户以为程序自己放弃了（B-2026-045）。
    cancelled: bool = False


def _is_real_dir(entry: os.DirEntry) -> bool:
    """这个条目是**真目录**吗（不是符号链接 / junction）？

    为什么必须挡（`B-2026-047`）：遍历会顺着重解析点走 —— 同一目录上两个 junction
    指回自己就是**指数级的路径**（实测：产品默认的 200 目录保险丝下 2.5 秒、
    同一个包被报 130 次；无上限时实际挂死）。而产物目录正是解压会写到的地方。
    ⚠ `entry.is_dir()` **默认 follow**，对 junction 返回 True；`is_symlink()` 对 junction
    是 **False** —— 只有 `is_junction()` 认得出（3.12+；更老的用 `st_reparse_tag` 兜底）。
    """
    try:
        if not entry.is_dir(follow_symlinks=False):
            return False
        is_junction = getattr(entry, "is_junction", None)
        if is_junction is not None and is_junction():
            return False
        tag = getattr(entry.stat(follow_symlinks=False), "st_reparse_tag", 0)
        return tag != 0xA0000003            # IO_REPARSE_TAG_MOUNT_POINT
    except OSError:
        return False


def _is_link_dir(entry: os.DirEntry) -> bool:
    """这个条目是**指向别处的目录链接**（符号链接 / junction）吗？只用于记账。

    "是个目录（follow 成立）但 `_is_real_dir` 不认" = 链接目录（`B-2026-047`）。
    单独成函数是为了**只算一次** `_is_real_dir`，也为了让"跳过了几个"能报给用户。
    """
    try:
        return entry.is_dir() and not _is_real_dir(entry)
    except OSError:
        return False


def _identity(path: str) -> tuple[int, int] | str:
    """这个路径的**物理身份**（`st_dev`, `st_ino`）；拿不到就退回规范化路径。

    `B-2026-047`：同一个物理目录/文件可以被**两条不同路径**到达（重解析点、8.3 短名、
    只是大小写不同的写法…），只按路径去重挡不住 —— 表现就是"同一个包被报很多次"。
    """
    try:
        st = os.stat(path, follow_symlinks=False)
    except OSError:
        return os.path.normcase(os.path.abspath(path))
    return (st.st_dev, st.st_ino)


def suspected_carriers(files: Iterable[str], *,
                       cancel: Callable[[], bool] | None = None) -> list[str]:
    """这一批文件里**疑似藏了内嵌压缩包**、但候选判据看不见的那些（`B-2026-076`）。

    为什么需要它：`candidates_from()` 只认标准候选（magic / 扩展名），伪装包
    （垫片 + 尾部完整包）**不在它的判据里**（它的文档明写"找它要整盘扫，代价高，
    由调用方在本层单独处理"）。而"本层单独处理"只覆盖了**本层**：子目录里只有一个
    伪装包时两边都不管 —— 候选单是空的、往下找也说"没有包"，最终报
    「文件夹里已没有可解压的压缩包」+ `ok=True`，而那个文件里的内容一个字节都没解出来。

    判据走**唯一入口** `inspect_carrier()`（`B-2026-097`）：与选目标通道
    （`Piercer._carrier_candidates`）**同一份**，两边都不传引擎（代价决策见下）。
    ⚠ **绝不在这里做全盘扫描**（`deep=True` 会整盘读：~1GB/s，真视频上就是几秒 ——
    那正是 §14.13/§14.14 记的"拖 mp4 卡顿"成因），**也绝不问引擎**（本函数按目录里
    每个文件跑，N×0.1s 的进程开销，§20.21 坑②）。
    廉价三档（尾部目录定位 / 绝对偏移布局 / **尾部签名**）已经覆盖了现实里绝大多数
    追加式伪装；真正够不着的只剩"包在文件**中段**"那一种，它不该由报备通道花几秒去挖 ——
    本函数只负责**如实说**，不负责扩大扫描范围（要不要下放是 `B-2026-076` 的 A/B 方案）。
    """
    out: list[str] = []
    for p in files:
        if cancel is not None and cancel():
            break
        if inspect_carrier(p, cancel=cancel).worth_handling:
            out.append(p)
    return out


def find_archives_below(
    directory: str,
    *,
    max_dirs: int = NESTED_SCAN_MAX_DIRS,
    log: Callable[[str], None] | None = None,
    cancel: Callable[[], bool] | None = None,
) -> DirSearch:
    """在 `directory` 的**子目录**里找候选包（当前层自己不算），**收全**而不是只找前两个。

    为什么需要它：外层包自带一层目录是打包常态（`包裹A/包裹B/inner.zip`），
    只看一层会把内层包漏掉、还报"文件夹里已没有可解压的压缩包"（B-2026-002 / B-2026-033）。

    三条设计（都是踩过的坑）：
      * **不按目录深度设限**：深度是用户 `max_depth`（包层）的事，与"往下找"无关；
        改用**按目录数**的保险丝（`max_dirs`），两者正交（B-2026-033 成因①）；
      * **显式栈、不递归**：本机开了长路径支持，目录可以深到超过 Python 的递归上限，
        递归版深到那儿就是 RecursionError（而且是在用户的解压流程里）；
      * **读不了就记下来继续**：`os.scandir` 失败（权限 / 超长路径 / 保留设备名 /
        网络盘断开 / 目录被并发删除）不再静默 return，而是记 `truncated` + `unreadable`
        并继续找别的目录 —— 静默 return 会让"某个子目录读不了"看起来和"真的没有包"
        一模一样（B-2026-037）。**但要分清"读不动"与"不在"**（B-2026-049）：
        根目录**不存在**时 `found=[]` 已经说清"没有东西可找"，**不置 `truncated`**，
        否则"输出目录被删掉"会被报成「子文件夹没往下找完」。

    `found` 是**收全的候选**：调用方据此判"唯一才用、多个如实列全"。
    """
    out = DirSearch()
    skipped_links = 0
    try:
        top = list(os.scandir(directory))
    except OSError as exc:
        # ★ 这里要分清两件事（`B-2026-049`）：
        #   * **这个目录本身就不在**（不存在 / 被删掉 / 根本不是目录）→ 没有东西可找，
        #     `found=[]` 已经说清了，**不许**再置 `truncated`。以前两者混在一起，
        #     于是"输出目录被删掉"被报成「子文件夹没往下找完（有 1 个子文件夹读不了）」
        #     —— 用户看到的是"程序自己没找完"，而真实原因是产物目录没了；
        #     它还把 `pierce._extract_one` 的产物校验（`B-2026-030` 的 `EMPTY_OUTPUT`）
        #     挡在了后面：`pick_target` 先报 `SEARCH_INCOMPLETE`，那条校验永远轮不到。
        #   * **它在、但读不动**（权限 / 超长路径 / 网络盘断开）→ 那才叫"没找完"。
        # 日志文案两者共用一句（不为改这句话去连坐文案断言）：对"不存在"的目录，
        # 「没往下找」也是事实（那里本来就一个目录都没有）。
        exists = os.path.isdir(directory)
        out.truncated = exists
        if exists:
            out.unreadable.append(directory)
        _say(log, f"⚠ 读不了这个目录，没往下找：{os.path.basename(directory) or directory}"
                  f"（{exc.strerror or exc.__class__.__name__}）")
        return out
    roots = sorted(e.path for e in top if _is_real_dir(e))
    skipped_links += sum(1 for e in top if _is_link_dir(e))

    stack = list(reversed(roots))       # 深度优先、按名字排序 → 结果稳定
    visited: set[tuple[int, int] | str] = set()
    while stack:
        if cancel is not None and cancel():
            # 用户点了「停止」：立刻收工。**不置 `truncated`** —— 取消不是"没找完"，
            # 报 `SEARCH_INCOMPLETE` 会让用户以为程序自己放弃了（B-2026-045）。
            out.cancelled = True
            _say(log, "⏹ 已停止：不再往下找")
            break
        if out.scanned_dirs >= max_dirs:
            out.truncated = True
            _say(log, f"⚠ 子文件夹超过 {max_dirs} 个，没往下找完（这一层可能还有包没找出来）")
            break
        d = stack.pop()
        key = _identity(d)
        if key in visited:
            # 同一个物理目录**只扫一次**（B-2026-047）：路径别名（大小写 / 8.3 短名）
            # 指向同一个目录时，按路径去重挡不住，会重复扫、重复报、白烧保险丝。
            # ⚠ 它**不是** junction 的防线：junction 自身的 `st_ino` 与目标不同，
            # `follow_symlinks=False` 看不出来 —— 那件事靠 `_is_real_dir`。
            # 这里是第二道：万一哪天 `_is_real_dir` 漏了，把指数爆炸降级成"重复扫一遍"。
            continue
        visited.add(key)
        out.scanned_dirs += 1
        try:
            entries = list(os.scandir(d))
        except OSError as exc:
            out.truncated = True
            out.unreadable.append(d)
            _say(log, f"⚠ 读不了子目录，已跳过：{os.path.basename(d) or d}"
                      f"（{exc.strerror or exc.__class__.__name__}）")
            continue
        files = [e.path for e in entries if e.is_file()]
        out.found.extend(candidates_from(files, log=log))
        # ★ 伪装包（垫片 + 尾部完整包）**不在候选判据里**（`B-2026-076`）：本层有
        #   `pick_target` 单独处理，子目录里以前两边都不管。这里按**廉价**判据收一笔
        #   （尾部目录定位，不做全盘扫描），由调用方如实报"有东西没解"。
        out.carriers.extend(suspected_carriers(files, cancel=cancel))
        subdirs: list[str] = []
        for e in entries:
            # ⚠ 这里也**必须**用 `_is_real_dir`（B-2026-047）：只改入口那一处等于没修
            if _is_real_dir(e):
                subdirs.append(e.path)
            elif _is_link_dir(e):
                skipped_links += 1
        stack.extend(reversed(sorted(subdirs)))
    if skipped_links:
        # 跳过链接**不算"没找完"**（我们是主动不跟，不是找不动）——但必须说出来：
        # 用户的目录树里真有 junction 时，里面的包就是不会被解，一声不响等于静默漏解。
        _say(log, f"⚠ 跳过了 {skipped_links} 个符号链接/junction 目录（不跟进去找包）")
    # `found` 去重：同一个包被不同路径扫到时别重复报（下游会当成"有 N 个候选"）。
    # 按**物理身份**去重（`B-2026-047`）—— 硬链接到同一个包的另一个名字，
    # 按路径去重看不出来，照样会被当成第二个候选。
    seen: set[tuple[int, int] | str] = set()
    unique: list[str] = []
    for p in out.found:
        key = _identity(p)
        if key not in seen:
            seen.add(key)
            unique.append(p)
    out.found = unique
    # `carriers` 同样按**物理身份**去重，并剔掉**已经是候选**的那些（`B-2026-076`）：
    # 下游把两个名单当成"要解"与"没解"两件事，重了就会既解又报"没解"。
    # 理论上互斥（`find_embedded` 对"头就是压缩包"的文件返回 None），但硬链接 /
    # 短名这类"一个文件两条路径"只有按身份才认得出来（`B-2026-047`）。
    known = {_identity(p) for p in out.found}
    cseen: set[tuple[int, int] | str] = set()
    carriers: list[str] = []
    for p in out.carriers:
        key = _identity(p)
        if key in known or key in cseen:
            continue
        cseen.add(key)
        carriers.append(p)
    out.carriers = carriers
    return out


def missing_volume_note(path: str | os.PathLike[str]) -> str | None:
    """这一组**分卷不全**吗？不全就返回**缺的那一卷文件名**，否则 None。

    `x.part1.rar` / `x.7z.001` 这种命名本身就是"多卷集合"的标志（WinRAR / 7-Zip 只在
    多卷模式下这么命名），所以"主卷在、下一卷不在"= 分卷不全。这种包问用户要密码是白问——
    真正该做的是把分卷凑齐，至少得说清缺哪一卷（比"密码已全部试完"清楚得多）。

    ★ `B-2026-079`：以前它只认 `VolKind.PART` + `is_first`、而且只查 `index + 1`，
    于是「**缺主卷**」（用户只下到 `part2` / `z01` / `r00`）这条路**根本判不出来**：
    判据恒返回 None → `vault.unlock` 一路走到引擎那道"读不出这个包"的短路，
    报「引擎读不出这个包（不是密码问题）：可能不是压缩包，或者文件已损坏」
    —— 对"网盘漏下了一卷"这种最常见的情形，给出的方向完全是错的（`R-13`）。
    现在三种命名方案的两种缺口都认：

      * **首卷整体缺席**（组里一个 `is_first` 成员都没有）→ 说清缺的是第 1 卷；
      * 首卷在场、**中间缺卷** → 说清缺的是哪一卷（按在场卷号找第一个缺口）；
      * 首卷在场、**缺最后一卷** → 只有 RAR5 的卷头答得了（见 `_rar5_has_more_volumes`）。

    ⚠ **"连续到场"不许直接当成"还差下一卷"**：分卷头里没有"总共有几卷"这个信息，
    那样写会把**每一个完整的分卷包**都报成缺卷 —— 那种包从此一个密码都不试、永远解不开
    （比原来那个 bug 更糟）。"缺末尾卷"必须由**卷头声明**（RAR5 的 `HFL_SPLITAFTER`）
    或引擎输出来说，判不出来就如实返回 None。
    """
    info = classify_volume(path)
    if info.kind is VolKind.NONE:
        return None
    folder = os.path.dirname(str(path)) or "."
    try:
        entries = [e.path for e in os.scandir(folder) if e.is_file()]
    except OSError:
        return None
    present: dict[int, str] = {}
    first_present = False
    has_later = False          # 组里有**次卷**成员吗（`part2` / `.z01` / `.r00`…）
    for e in entries:
        vi = classify_volume(e)
        if (vi.kind is not info.kind
                or os.path.normcase(vi.base) != os.path.normcase(info.base)):
            continue
        present[_volume_seq(vi)] = e
        first_present = first_present or vi.is_first
        has_later = has_later or not vi.is_first
    if not first_present:
        # 只下到次卷（`part2` / `z01` / `r00`）—— 缺的就是**首卷**，别让它走到引擎。
        return os.path.basename(_volume_name(info.base, info.kind, 1))
    # `.zip` / `.rar` / `.NNN` **单独一个不算分卷**：`classify_volume` 把前两者归到
    # ZIP_SPLIT / OLD_RAR 只是为了能跟 `.z01` / `.r00` 配对；`.7z.001` 也可能是单卷包
    # 被改了名。这一组里连一个次卷成员都没有，就没有"下一卷"可言 —— 报下去会让普通
    # 单卷包被说成"缺 x.z01"、一个密码都不试。`.partN.rar` 不在此列：那个命名只在多卷
    # 模式下出现（WinRAR 建单卷包不会叫 `.part1.rar`），所以它单个也算"缺第 2 卷"。
    if info.kind is not VolKind.PART and not has_later:
        return None
    top = max(present)
    seq = 1
    while seq in present:
        seq += 1
    if seq <= top:
        # 1..top 里有缺口（`part1` + `part3` → 缺 `part2`）—— 这条判据只看名字就够。
        return os.path.basename(_volume_name(info.base, info.kind, seq))
    if top == 1:
        # 首卷单独在场：`.partN.rar` 这个命名只在多卷模式下出现，所以"缺第 2 卷"
        # 直接说得出口（旧行为，与卷头无关 —— RAR4 的老式分卷也走这一条）。
        return os.path.basename(_volume_name(info.base, info.kind, 2))
    # 1..top 连续到场，名字上已经没有缺口了：只剩"末卷之后还有没有卷"这一种可能，
    # 而它只有卷头答得了（RAR5 的 HFL_SPLITAFTER）。答不了就如实返回 None。
    if _rar5_has_more_volumes(present[top]):
        return os.path.basename(_volume_name(info.base, info.kind, top + 1))
    return None


def main_volume_of(path: str, siblings: Iterable[str]) -> str | None:
    """给定一个文件，若它是分卷成员，返回同组主卷路径；否则 None。

    用于 UI：用户把 part2.rar 拖进来，也能自动纠正到 part1.rar。

    ★ 首卷**不在场**时返回 None（`B-2026-079`）：纠正的目标根本不存在，
    返回它会让调用方（`pipeline.scan` 的单文件分支）把这一行列成"读不了的文件"，
    用户反而看不到"缺主卷"。返回 None → 调用方沿用原路径，由 `vault.unlock`
    在试密码之前如实报「分卷不全：缺少 x.part1.rar」。
    """
    info = classify_volume(path)
    if info.kind is VolKind.NONE:
        return None
    candidates = [str(s) for s in siblings]
    for g in group_volumes(candidates):
        if g.kind is info.kind and os.path.normcase(g.base) == os.path.normcase(info.base):
            return g.main if g.first_present else None
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
        # ★ 次卷不许被标成「分卷 2」（`B-2026-079`）：它**不是**这一组的主卷，标成一个
        #   看着正常的分卷号再送去解，等于明知故犯地把用户引到"包坏了"那个方向（`R-13`）。
        label += (f" · 分卷 {vol.index}" if vol.is_first else " · 次卷（不是主卷）")
    return label
