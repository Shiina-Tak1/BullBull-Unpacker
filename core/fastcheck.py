# -*- coding: utf-8 -*-
"""进程内"这个密码肯定不对"的快速判定（**只排除、不宣布正确**）。

为什么要有它：试一个密码现在要起一次引擎进程（7z ≈30ms、还要排队），而实测这台机器
**每秒最多起 ~150 个进程、带 7z 的只有 ~43 个** —— 瓶颈是"起进程"本身，不是 CPU。
只要能在进程内把绝大多数错密码划掉，就不用为它们起进程了。

支持的加密（都是"不需要解密、只看 KDF 派生的校验值"就能判的）：
  * zip / WinZip AES（AE-1/AE-2，128/192/256）：salt 后 2 字节
      == PBKDF2-HMAC-SHA1(密码UTF8, salt, 1000, 2*key_len+2) 的最后两字节
  * zip / 老式 ZipCrypto：解密数据开头 12 字节，最后一字节 == CRC 高字节
      （flag bit3 时用 DOS 时间高字节）
  * rar5 / `-hp`（加密头）与 `-p`（文件级加密记录）：头里 8 字节 PswCheck
      == PBKDF2-HMAC-SHA256(密码UTF8, salt, (1<<KDFCount)+32, 32) 按 8 字节 XOR 折叠
      （公式来自 UnRAR `crypt5.cpp`，已用 WinRAR 现造样本逐条核对）
  * 7z / `-mhe=on`（加密头）：交给**常驻 `7z.dll`**（`core/dll7z.py`）——
    判据是 `IInArchive::Open()` 的 HRESULT（`S_FALSE` = 密码不对，背后是头块 CRC32）。
    `-mhe=off`（明文头）**判不了**：`Open()` 对任何密码都给 `S_OK`，那种包照旧由引擎
    的 `7z t <最小条目>` 负责。

**绝不宣布"密码正确"**：校验值都有假阳性（AES 1/65536、ZipCrypto 1/256、rar5 ≈0、7z 头 CRC 2^-32），
所以活下来的候选必须交给引擎真跑一次。不认识的格式（rar4、明文、加密的中央目录…）
一律返回 False = "不知道"，走原路。

`SMART_UNZIP_NO_FASTCHECK=1` 可以把整条快路关掉（A/B 对照与排错用）；
`SMART_UNZIP_NO_DLL7Z=1` 只关 7z 那一段（`core/dll7z.py` 自己认这个开关）。
"""

from __future__ import annotations

import hashlib
import os
import struct
import zipfile

from core import probe        # 只为保留设备名守卫（probe 不反向依赖 fastcheck，不成环）

# rar5 的 KDF 轮数 = 1<<KDFCount；超过这个就不再"进程内验"（会比直接起引擎还慢）
RAR5_MAX_LG2 = 18

# RAR5 的密码长度上限：**127 个 UTF-16 码元**（不是字节，也不是码点）。
# `Rar.exe` 建包时把密码截断到这个长度（stderr 原话：
# `Password exceeds the maximum allowed length of 127 characters and will be truncated.`），
# 7-Zip 读包时同样截断。
# ⚠ 为什么这条常量必须存在（`B-2026-053`）：`_rar5_ok` 拿**完整密码**算 PBKDF2 时，
#   一旦密码超过这个长度，算出来的值必然和包里的 PswCheck 不匹配 → `quick_reject=True`
#   → `core/vault.py::unlock` 直接合成 `ok=False`、**根本不起引擎** —— 那是**假阴性**，
#   违反 `vault.py`「假阴性 = 0 是硬要求」，也证伪手册 §20.4 那句"假阴性在机制上不可能"。
#   实测：128 个 `Z` 建出来的 rar5，快路开时 `unlock` 报"密码已全部试完"，
#   关掉快路（`SMART_UNZIP_NO_FASTCHECK=1`）就能命中。
# ⚠ 判据的单位必须是**码元**（`len(pw.encode("utf-16-le")) // 2`），**不能**用 `len(pw)`
#   （码点）或 `len(pw.encode("utf-8"))`（字节）：
#     * 按码点截/判对含代理对的密码（emoji）不等价 —— 64 个 emoji 是 64 个码点、
#       却是 128 个 UTF-16 码元；
#     * 按码点截还可能停在**半个代理对**上（孤立 surrogate），
#       `encode("utf-8")` 会直接抛错。
RAR5_PW_MAX_UNITS = 127

RAR5_MAGIC = b"Rar!\x1a\x07\x01\x00"
SEVENZ_MAGIC = b"7z\xbc\xaf\x27\x1c"
# 本地文件头签名 "PK\x03\x04"（按小端读成一个 uint32）。`_parse_zip_entry` 用它确认
# "CD 指过去的地方真的是一个本地头"，对不上就回退引擎（理由与实测见那个函数的 docstring）。
ZIP_LOCAL_SIG = 0x04034B50
SALT_LEN = {1: 8, 2: 12, 3: 16}      # WinZip AES 强度 1/2/3 → salt 长度
KEY_LEN = {1: 16, 2: 24, 3: 32}      # → 密钥长度

_CRC_TAB: list[int] = []
for _n in range(256):
    _c = _n
    for _ in range(8):
        _c = (0xEDB88320 ^ (_c >> 1)) if (_c & 1) else (_c >> 1)
    _CRC_TAB.append(_c)

# 解析结果按 (路径, 大小, mtime) 缓存：一个包里 1000 个候选只该解析一次头
_CACHE: dict[tuple[str, int, int], object] = {}


def enabled() -> bool:
    return os.environ.get("SMART_UNZIP_NO_FASTCHECK", "") not in ("1", "true", "yes")


def quick_reject(archive: str, password: str | None) -> bool:
    """`True` = 这个密码**肯定不对**（别起引擎了）；`False` = 不知道，照原路走。"""
    if not enabled() or not password:
        return False
    try:
        item = _parse(archive)
    except Exception:            # noqa: BLE001 - 任何解析异常都退回引擎
        return False
    try:
        if item is None:
            return False
        kind, meta = item                              # type: ignore[misc]
        if kind == "zip":
            return not _zip_ok(password, meta)
        if kind == "rar5":
            return not _rar5_ok(password, meta)
        if kind == "7z":
            from core import dll7z                     # 惰性：只有真碰上 7z 才加载常驻 dll

            return dll7z.quick_reject(archive, password)
    except Exception:            # noqa: BLE001
        return False
    return False


def _parse(archive: str):
    """按文件内容判断能不能进程内判，能就返回 ("zip"|"rar5", 元数据)；否则 None。

    ⚠ 开头先挡保留设备名：试密码这条路也会走到 `open(archive, "rb")`（下面第 95 行），
    在会做 DOS 设备映射的系统上会**永久阻塞**。**这一处最容易漏** —— 只看
    `probe.detect_format` / `pierce._fingerprint` 会以为已经覆盖了，
    实测（打桩统计调用点）`fastcheck._parse` 确实会被 `vault.unlock` 的
    `quick_reject` 调到，漏了它照样卡。返回 None = "判不了"，退回引擎原路。
    """
    if probe.is_device_path(archive):
        return None
    try:
        st = os.stat(archive)
    except OSError:
        return None
    key = (os.path.normcase(os.path.abspath(archive)), st.st_size, st.st_mtime_ns)
    if key in _CACHE:
        return _CACHE[key]
    item = None
    try:
        with open(archive, "rb") as f:
            head = f.read(8)
        if head.startswith(b"PK\x03\x04"):
            meta = _parse_zip_entry(archive)
            item = ("zip", meta) if meta else None
        elif head.startswith(RAR5_MAGIC):
            cps = _parse_rar5(archive)
            item = ("rar5", cps[0]) if cps else None
        elif head.startswith(SEVENZ_MAGIC):
            item = ("7z", None)                        # 能不能判由常驻 dll 现场决定
        else:
            # ★ 兜底：**假头 + 一整个 ZIP**（`B-2026-093`，2026-09-25）。
            #
            # 真事：`[示例]…与魔性的她…2.88G.mp4` —— 前 36 字节是假 MP4 头
            # （`\x00\x00\x00\x1cftypisom…`），真实内容是一个 ZIP：内置 7-Zip 报
            # `Type = zip` / `Embedded Stub Size = 36`，包内偏移**全是绝对偏移**，
            # 所以 7-Zip 能直读（`pipeline` 那边因此走"引擎直读"、连包都不切）。
            # 但头 8 字节既不是 PK 也不是 Rar5/7z → 以前这里直接判"判不了"，
            # 于是**每个候选都起一次 7z.exe**：实测 38.6 个/秒（自动 16 路），
            # 10 万条密码本要 ~43 分钟；而它其实**完全能进程内判** —— 改完实测
            # 832~932 个/秒（×22~24），端到端 `unlock` 试 201 条：5.21s → 0.22s。
            #
            # 为什么敢让 `zipfile` 再试一次：它找中央目录靠**文件尾的 EOCD**，
            # 与文件头是什么无关；而且**位置由它自己修好**，两种内嵌形态都可用：
            #   * 绝对偏移（上面那个包）：EOCD 里的偏移本来就相对文件开头；
            #   * 相对偏移（把现成的 zip 追加到一段垫片后面）：`zipfile` 按 EOCD 反算出
            #     `concat` 并把它加进每个条目的 `header_offset`（实测 8MB 垫片 +
            #     加密 zip → `header_offset=8388608`，已经是绝对位置），对它
            #     `quick_reject` 同样**不会划掉正确密码**。
            #
            # 代价：只有"原本判不了"的文件多一次 zipfile 尝试（seek 到文件尾找 EOCD，
            # 读 64KB 量级；实测 `教程视频.mp4` / `垫片伪装.mp4` 都是 0.5~1.1 ms）。
            # `_parse` 每个包只跑一次且有缓存，整轮只多这一次。
            meta = _parse_zip_entry(archive)
            item = ("zip", meta) if meta else None
    except Exception:            # noqa: BLE001
        item = None
    _CACHE[key] = item
    return item


# --------------------------------------------------------------------------
# zip
# --------------------------------------------------------------------------

def _parse_zip_entry(path: str):
    """找**第一个加密条目**，取出判密码需要的东西（AES 的 salt/校验值，或 ZipCrypto 的 12 字节头）。

    读到的 30 字节本地头**必须**以 `PK\\x03\\x04` 开头，对不上就返回 None（= 回退引擎）。
    这道确认**不是**"路能不能走通"的前提 —— 正常包的定位实测就是准的（绝对偏移的包偏移
    原样可用；相对偏移的包 `zipfile` 按 EOCD 反算 `concat` 修好了；`示例包.zip` 和
    `pipeline` 认得的各种内嵌包都过）。它是**假阴性**的保险，而且实测确实挡得住：
    把 CD 里第一条的 header_offset 改坏（模拟"CD 与本地头基准对不上"的畸形包 ——
    7-Zip 遇到这种会退回扫描本地头、照样解得开，而 `zipfile` 只信 CD），在 204 字节的
    `示例包.zip` 上扫遍 175 个位置 —— **没有这道确认时 34 个位置**会解出垃圾 meta
    （`zipcrypto`，例如 header_offset=59）：垃圾 salt/verify 跟谁都不匹配，`quick_reject`
    会把**包括正确密码在内**的候选全划掉 = 假阴性。`B-2026-093` 的断言就是这么抓出来的
    （那个包的头正是 `PK\\x03\\x04`，走普通分支 —— 所以确认对**两条路**都生效，别只给
    兜底分支加）。`vault` 里"假阴性 = 0"是硬要求，认不准只是回退引擎慢一点 —— 别赌。
    """
    with zipfile.ZipFile(path) as zf:
        for info in zf.infolist():
            if not (info.flag_bits & 0x01):
                continue
            strength = None
            extra, i = info.extra, 0
            while i + 4 <= len(extra):
                hid, hsz = struct.unpack_from("<HH", extra, i)
                if hid == 0x9901:                      # WinZip AES extra field
                    _v, _vendor, strength, _actual = struct.unpack_from("<H2sBH", extra, i + 4)
                i += 4 + hsz
            zf.fp.seek(info.header_offset)
            raw = zf.fp.read(30)
            if len(raw) < 30:
                return None
            _sig, _ver, flags, method, mtime, _md, crc, _cs, _us, nlen, elen = \
                struct.unpack("<IHHHHHIIIHH", raw)
            if _sig != ZIP_LOCAL_SIG:
                return None
            zf.fp.seek(info.header_offset + 30 + nlen + elen)
            head = zf.fp.read(32)
            if strength and strength in SALT_LEN and len(head) >= SALT_LEN[strength] + 2:
                sl, kl = SALT_LEN[strength], KEY_LEN[strength]
                return {"kind": "aes", "salt": head[:sl], "verify": head[sl:sl + 2],
                        "salt_len": sl, "key_len": kl}
            if len(head) >= 12:                        # 老式 ZipCrypto
                return {"kind": "zipcrypto", "flags": flags, "mtime": mtime,
                        "crc": crc, "head": head[:12]}
            return None
    return None


def _zip_ok(pw: str, meta: dict) -> bool:
    if meta["kind"] == "aes":
        dk = hashlib.pbkdf2_hmac("sha1", pw.encode("utf-8"), meta["salt"], 1000,
                                 2 * meta["key_len"] + 2)
        return dk[-2:] == meta["verify"]
    keys = [0x12345678, 0x23456789, 0x34567890]

    def upd(c: int) -> None:
        keys[0] = (keys[0] >> 8) ^ _CRC_TAB[(keys[0] ^ c) & 0xFF]
        keys[1] = (keys[1] + (keys[0] & 0xFF)) & 0xFFFFFFFF
        keys[1] = (keys[1] * 134775813 + 1) & 0xFFFFFFFF
        keys[2] = (keys[2] >> 8) ^ _CRC_TAB[(keys[2] ^ (keys[1] >> 24)) & 0xFF]

    def dec(c: int) -> int:
        t = (keys[2] | 2) & 0xFFFF
        k = ((t * (t ^ 1)) >> 8) & 0xFF
        upd(c ^ k)
        return c ^ k

    for ch in pw.encode("utf-8"):
        upd(ch)
    head = bytes(dec(b) for b in meta["head"][:12])   # 必须按顺序解完 12 字节
    check = ((meta["mtime"] >> 8) & 0xFF) if (meta["flags"] & 0x08) else ((meta["crc"] >> 24) & 0xFF)
    return head[-1] == check


# --------------------------------------------------------------------------
# rar5
# --------------------------------------------------------------------------

def _vint(buf: bytes, i: int) -> tuple[int, int]:
    val = shift = 0
    while i < len(buf):
        b = buf[i]
        i += 1
        val |= (b & 0x7F) << shift
        if not (b & 0x80):
            return val, i
        shift += 7
    return -1, i


def _parse_rar5(path: str) -> list[dict]:
    with open(path, "rb") as f:
        data = f.read(1 << 20)                     # 头总在开头，1MB 够
    if not data.startswith(RAR5_MAGIC):
        return []
    out: list[dict] = []
    i = 8
    while i + 4 < len(data):
        i += 4                                     # Header CRC32
        hsize, i = _vint(data, i)
        hstart = i
        htype, i = _vint(data, i)
        flags, i = _vint(data, i)
        extra = dsize = 0
        if flags & 0x0001:
            extra, i = _vint(data, i)
        if flags & 0x0002:
            dsize, i = _vint(data, i)
        if hsize <= 0 or hsize > 2_000_000:
            break
        body = data[i:hstart + hsize]
        if htype == 4:                             # 档案加密头（-hp）
            j = 0
            _ver, j = _vint(body, j)
            eflags, j = _vint(body, j)
            if j >= len(body):
                break
            kdf = body[j]; j += 1
            salt = body[j:j + 16]; j += 16
            if (eflags & 0x0001) and kdf <= RAR5_MAX_LG2 and j + 8 <= len(body):
                out.append({"kdf_count": kdf, "salt": salt, "check8": body[j:j + 8]})
            break                                  # 之后都是密文头，别再解析
        if htype in (2, 3):                        # 文件/服务头（-p：头是明文）
            try:
                ff, j = _vint(body, 0)
                _us, j = _vint(body, j)
                _at, j = _vint(body, j)
                if ff & 0x0002:
                    j += 4
                if ff & 0x0004:
                    j += 4
                _ci, j = _vint(body, j)
                _os_, j = _vint(body, j)
                nlen, j = _vint(body, j)
                j += nlen
            except Exception:                      # noqa: BLE001
                pass
            else:
                ex = body[len(body) - extra:] if extra and extra <= len(body) else b""
                k = 0
                while k < len(ex):
                    rsize, k = _vint(ex, k)
                    type_start = k
                    if rsize <= 0:
                        break
                    rtype, k = _vint(ex, k)
                    data_len = max(0, rsize - (k - type_start))
                    rdata = ex[k:k + data_len]
                    k = type_start + rsize
                    if rtype == 1:                 # File encryption record
                        m = 0
                        _rv, m = _vint(rdata, m)
                        rflags, m = _vint(rdata, m)
                        if m >= len(rdata):
                            continue
                        rkdf = rdata[m]; m += 1
                        rsalt = rdata[m:m + 16]; m += 16
                        m += 16                    # IV
                        if (rflags & 0x0001) and rkdf <= RAR5_MAX_LG2 and m + 8 <= len(rdata):
                            out.append({"kdf_count": rkdf, "salt": rsalt,
                                        "check8": rdata[m:m + 8]})
        i = hstart + hsize + dsize                 # 注意：extra 已含在 hsize 里
        if htype == 5:
            break
    return out


def _rar5_ok(pw: str, cp: dict) -> bool:
    # ★ 超过 127 个 **UTF-16 码元** → 直接返回"不划掉"，把判定交回引擎（`B-2026-053`）。
    #   为什么不能在这里算：包里的 PswCheck 是拿**被截断到 127 码元**的密码算的，
    #   拿完整密码算必然不等 —— 那不是"密码不对"，是**我们算错了对象**。
    #   为什么不在这里自己截断（`pw[:127]`）：截断单位是 UTF-16 码元而不是码点，
    #   按码点截对含代理对的密码不等价、还可能停在半个代理对上。
    #   这是"保守回退"（作者裁决 `J-7 = A`）：宁可少划掉几个候选，也不能假阴性。
    try:
        units = len(pw.encode("utf-16-le")) // 2
    except UnicodeEncodeError:
        # 编不出来（孤立代理对）→ 同样交给引擎，别在这里宣布"肯定不对"
        return True
    if units > RAR5_PW_MAX_UNITS:
        return True
    v2 = hashlib.pbkdf2_hmac("sha256", pw.encode("utf-8"), cp["salt"],
                             (1 << cp["kdf_count"]) + 32, 32)
    fold = bytes(v2[i] ^ v2[i + 8] ^ v2[i + 16] ^ v2[i + 24] for i in range(8))
    return fold == cp["check8"]
