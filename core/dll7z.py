# -*- coding: utf-8 -*-
"""常驻 7z.dll：进程内判 7z 的密码对不对（只做排除）。**两条路，自动选**：

**① 加密头（`-mhe=on`）** —— 判据是 `IInArchive::Open()` 的 HRESULT：`S_FALSE` = 密码不对
（背后是头块 CRC32，假阳性 2⁻³²），`S_OK` = 对；一条 `Open` 出结论。

**② 明文头（`-mhe=off`）** —— `Open()` 对**任何**密码都返回 `S_OK`、而且**不问密码**
（密码回调 0 次），所以判据只能靠真解一点数据：`Extract(testMode=1)` 只解**最小的那个加密条目**，
看 `IArchiveExtractCallback::SetOperationResult()` 的结果码 ——
**2（数据错误）/ 3（CRC 错）= 这个密码肯定不对**，0（OK）= 对（而且这时字节数 > 0）。
只有"密码正确"那一次会把最小条目所在的整个固实块解出来，而正确密码一辈子只验一次。

**为什么值得做**：试一个密码要起一次 `7z.exe` —— 进程创建 14.7ms + `7z.dll` 加载 ~5.7ms +
7z 自己的 KDF ~4ms ≈ 24ms，而这台机器**每秒只能起 ~43 个带 7z 的进程**，所以 16 路并行也只到
12.18ms/候选。常驻 dll 把前两段全免掉。实测（手册 §20.11 / §20.12）：
`-mhe=on` **4~7ms 串行**（16 路引擎 12.18ms），`-mhe=off` **约 8.2ms 串行**（同上）。

**语义**：`quick_reject(archive, password) -> True` 只表示"这个密码肯定不对"；永远不宣布"正确"，
活下来的候选照旧由引擎复核（与 `core/fastcheck.py` 的 zip/rar5 完全一致）。

**并行**：实测这条路 16 线程**零收益**（平均核数恒 1.0，dll 内部大概是那把 KDF 缓存锁；
同进程 `hashlib.pbkdf2_hmac` 对照能到 11.6 核）→ 这里用一把模块锁**自己串行**，语义等价、
也不会去踩 dll 的线程安全。想再提高吞吐只能多进程常驻 + IPC，不在本模块范围。

**踩过的坑**（都在原型 `src\\tests\\proto_7z_dll.py` / 探针 `src\\tests\\proto_7z_extract.py` 里复现过）：
  * 回调对象**每个接口各自一张以 IUnknown 开头的 vtable**，QI 要返回该接口自己的指针 ——
    两张接口拼一张表会让 handler 把 `CryptoGetTextPassword` 调到 `SetTotal` 上，
    于是"QI 要过密码接口、回调却 0 次、Open 恒 S_FALSE"，看起来像 dll 不支持，其实是用法错。
  * **IInArchive 的槽位必须以官方 `IArchive.h` 为准**：`5 GetNumberOfItems / 6 GetProperty /
    7 Extract / 8 GetArchiveProperty` —— `Extract` 在 `GetProperty` **后面**。原型当年把 7 当
    `GetArchiveProperty`，等于把 PROPID 当 `indices` 喂给 `Extract`，必然 access violation。
  * `ctypes.WINFUNCTYPE(...)()` **空参构造**出来的是"空函数指针原型"（地址 0），一调就 AV，
    报错是 `access violation writing 0x0`，看着像结构体布局错 —— 必须从 vtable 槽位取地址：
    `WINFUNCTYPE(...)(v[6])`。
  * ctypes 的 Structure 既没有 `__eq__` 也没有 `__str__`：`str(guid)` 拿到的是**带地址的 repr**，
    拿它跟 IID 常量比**永远不等** → QI 全 `E_NOINTERFACE`，症状与上面那条 vtable 坑一模一样。
    本模块所有 IID 都按 16 字节比。
  * `7z.dll` 内部按 (归档, 密码串) **缓存 KDF**：同一密码第二次只要 0.05ms。基准测试必须
    每候选换新密码，否则数字假快 ~80 倍。
  * 该 dll 的 `GetArchiveProperty` 别用（本模块不碰它）；`GetProperty` 在 archive 没有条目时
    调 `GetProperty(0,...)` 会越界崩溃，必须先问 `GetNumberOfItems`。
  * 导出函数是 **cdecl**（`CFUNCTYPE`），COM vtable 方法是 **stdcall**（`WINFUNCTYPE`）；
    `CFUNCTYPE` 实例必须自己保引用，否则被 GC 回收 → 调用时踩野指针。
"""

from __future__ import annotations

import ctypes
import os
import re
import sys
import threading
from ctypes import POINTER, Structure, Union, byref, c_int32, c_int64, c_uint32, c_uint64, c_ushort, c_void_p, c_wchar_p, sizeof

# ---- 7-Zip 接口 GUID：{23170F69-40C1-278A-0000-000<group>000<sub>0000} ----

class _GUID(Structure):
    _fields_ = [("Data1", c_uint32), ("Data2", c_ushort), ("Data3", c_ushort),
                ("Data4", ctypes.c_ubyte * 8)]


def _guid(group: int, sub: int) -> _GUID:
    return _GUID(0x23170F69, 0x40C1, 0x278A, (ctypes.c_ubyte * 8)(0, 0, 0, group, 0, sub, 0, 0))


def _iid(group: int, sub: int) -> bytes:
    """IID 一律用**16 字节**比较。

    坑：ctypes Structure 既没有 `__eq__` 也没有 `__str__`，`str(guid)` 拿到的是
    `<core.dll7z._GUID object at 0x...>`（还带地址）→ 拿它跟常量比**永远不等**，
    QI 会一律回 `E_NOINTERFACE`，症状是"Open 恒 S_FALSE、密码回调 0 次"，
    看着特别像 dll 不支持，其实是自己比错了。原型里那次折腾的就是这个。
    """
    return bytes(_guid(group, sub))     # Structure 支持 buffer 协议 → 直接 16 字节


_IID_IInStream = _iid(3, 0x03)
_IID_ISequentialOutStream = _iid(3, 0x06)
_IID_IInArchive = _guid(6, 0x60)
_IID_IArchiveOpenCallback = _iid(6, 0x10)
_IID_IArchiveExtractCallback = _iid(6, 0x20)
_IID_ICryptoGetTextPassword = _iid(5, 0x10)
_IID_IUnknown = bytes((0, 0, 0, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46))

_S_OK = 0
_S_FALSE = 1
_E_NOINTERFACE = -2147467262
_E_ABORT = -2147467260

# ★ IInArchive 的槽位**以 7-Zip 官方 `IArchive.h` 的 `INTERFACE_IInArchive` 为准**：
#     3 Open / 4 Close / 5 GetNumberOfItems / 6 GetProperty / **7 Extract** /
#     8 GetArchiveProperty / 9 GetNumberOfProperties / 10 GetPropertyInfo /
#     11 GetNumberOfArchiveProperties / 12 GetArchivePropertyInfo
#   `Extract` 在 `GetProperty` **后面**（不是最后）—— 原型当年把 7 当 GetArchiveProperty，
#   于是拿 (PROPID, PROPVARIANT*) 去调 Extract（= 把 PROPID 当 indices 指针），必然 AV，
#   还被记成"这个 dll 的 GetArchiveProperty 一调就崩"。详见手册 §20.12。
_VT_OPEN = ctypes.WINFUNCTYPE(c_int32, c_void_p, c_void_p, c_void_p, c_void_p)   # slot 3
_VT_CLOSE = ctypes.WINFUNCTYPE(c_int32, c_void_p)                                # slot 4
_VT_RELEASE = ctypes.WINFUNCTYPE(c_uint32, c_void_p)                             # slot 2
_VT_GETITEMS = ctypes.WINFUNCTYPE(c_int32, c_void_p, POINTER(c_uint32))          # slot 5
_VT_ITEMPROP = ctypes.WINFUNCTYPE(c_int32, c_void_p, c_uint32, c_uint32, c_void_p)  # slot 6
_VT_EXTRACT = ctypes.WINFUNCTYPE(c_int32, c_void_p, c_void_p, c_uint32, c_int32, c_void_p)  # 7

_SYS = ctypes.WinDLL("oleaut32", use_last_error=True)
_SYS.SysAllocString.restype = c_void_p
_SYS.SysAllocString.argtypes = [c_wchar_p]
_K32 = ctypes.WinDLL("kernel32", use_last_error=True)
_K32.LoadLibraryW.restype = c_void_p
_K32.LoadLibraryW.argtypes = [c_wchar_p]
_K32.GetProcAddress.restype = c_void_p
_K32.GetProcAddress.argtypes = [c_void_p, ctypes.c_char_p]
_K32.lstrlenW.restype = ctypes.c_int
_K32.lstrlenW.argtypes = [c_void_p]

_VT_BSTR = 8
_VT_I4, _VT_UI4, _VT_UI8, _VT_BOOL = 3, 19, 21, 11
_KNAME = 0
_KCLASSID = 1
_MAX_PW_CALLS = 2            # 密码回调最多给几次（错密码时 7-Zip 可能再问；给满就让它失败）
_MAX_ITEM_SCAN = 50_000      # 明文头那条路要扫"最小加密条目"，条目太多的包只扫前这么多（见 _pick_item）

# kpid（7-Zip `IArchive.h` 的 kpid 枚举）与解压结果码（`NArchive::NExtract::NOperationResult`）
_KPID_SIZE = 7
_KPID_ENCRYPTED = 15
_OP_OK, _OP_UNSUPPORTED, _OP_DATA_ERROR, _OP_CRC_ERROR = 0, 1, 2, 3
# **判据**：明文头那条路看 `SetOperationResult` 的结果码 —— 数据错误/CRC 错 = 这个密码肯定不对。
# 实测（§20.12）：密码错 → 2（数据错误）、写出 0 字节；密码对 → 0（OK）、写出整条目。
_OP_WRONG = (_OP_DATA_ERROR, _OP_CRC_ERROR)

_LOCK = threading.Lock()     # 这条路并行无收益 → 自己串行，也避免踩 dll 线程安全
_PICK_CACHE: dict[tuple[str, int, int], object] = {}   # 明文头那条路的"最小加密条目"缓存
# "试过了但得不出结论"的包（见 `_Archive._verdict_extract`）：记下来省掉后面每个候选的十几毫秒
_NO_VERDICT: dict[tuple[str, int, int], bool] = {}


def _no_verdict_key(archive: str):
    """(路径, 大小, mtime) —— 跟 `_pick_item` 用同一个缓存键。"""
    try:
        st = os.stat(archive)
    except OSError:
        return None
    return (os.path.normcase(os.path.abspath(archive)), st.st_size, st.st_mtime_ns)
_OBJ: "_Archive | None" = None
_DEBUG = os.environ.get("SMART_UNZIP_DEBUG_DLL7Z", "") not in ("", "0", "false")   # 打 handler 要的分卷名
_STATE = "new"               # new / ready / dead
_FAILS = 0


class _PROPUNION(Union):
    _fields_ = [("llVal", c_int64), ("lVal", c_int32), ("ulVal", c_uint32),
                ("uhVal", c_uint64), ("boolVal", c_ushort), ("pwszVal", c_wchar_p),
                ("punkVal", c_void_p)]


class _PROPVARIANT(Structure):
    _fields_ = [("vt", c_ushort), ("a", c_ushort), ("b", c_ushort), ("c", c_ushort),
                ("u", _PROPUNION), ("pad", ctypes.c_ubyte * 8)]


def _raw(p, n: int) -> bytes:
    """从原生指针取 n 字节。**必须显式 bytes() 包一层**：string_at 有时返回 c_char 数组，
    那样 `arr == b"7z"` 会是 False，但 repr/hex 看着完全正常（原型里为此浪费了一小时）。"""
    return bytes(ctypes.string_at(p, n))


def _read_guid(p) -> _GUID:
    return _GUID.from_buffer_copy(_raw(p, sizeof(_GUID)))


def _write_ptr(ppv, value) -> None:
    if ppv:
        ctypes.cast(ppv, POINTER(c_void_p))[0] = value


def _prop_int(getprop, arch, index: int, propid: int):
    """读一个整数型条目属性（VT_UI8 / VT_UI4 / VT_I4 / VT_BOOL）；读不到回 None。"""
    pv = _PROPVARIANT()
    if int(getprop(arch, index, propid, byref(pv))) < 0:
        return None
    if pv.vt == _VT_UI8:
        return int(pv.u.uhVal)
    if pv.vt == _VT_UI4:
        return int(pv.u.ulVal)
    if pv.vt == _VT_I4:
        return int(pv.u.lVal)
    if pv.vt == _VT_BOOL:
        return bool(pv.u.boolVal)
    return None


def _prop_bool(getprop, arch, index: int, propid: int) -> bool:
    """读一个布尔型条目属性（VARIANT_BOOL：-1 = 真）。"""
    pv = _PROPVARIANT()
    if int(getprop(arch, index, propid, byref(pv))) < 0:
        return False
    return pv.vt == _VT_BOOL and bool(pv.u.boolVal)


_VOL_CACHE: dict[tuple[str, int, int], list[str]] = {}
_VOL_RE = re.compile(r"^(?P<base>.+)\.(?P<num>\d{3,})$")


def _volume_paths(archive: str) -> list[str]:
    """给定 `x.7z.001`，把同组的 `.001/.002/…` 按序凑齐（不是分卷就返回它自己）。

    命名规则是"**基名 + 零填充数字**"（7-Zip 的 `-v`、以及不带内层扩展名的 `x.001`）。
    被喂进来的若不是第一卷（用户拖了 `.002`），先往下找到最小那个卷号再说。
    中间缺一卷就停在缺口处 —— handler 会因此打不开，正好回退给引擎（不猜、不合并）。
    """
    try:
        st = os.stat(archive)
    except OSError:
        return [archive]
    key = (os.path.normcase(os.path.abspath(archive)), st.st_size, st.st_mtime_ns)
    hit = _VOL_CACHE.get(key)
    if hit is not None:
        return hit
    m = _VOL_RE.match(os.path.basename(archive))
    out = [archive]
    if m:
        base = os.path.join(os.path.dirname(archive), m.group("base"))
        width = len(m.group("num"))
        n = int(m.group("num"))
        while n > 1 and os.path.isfile(f"{base}.{n - 1:0{width}d}"):
            n -= 1
        vols: list[str] = []
        while os.path.isfile(f"{base}.{n:0{width}d}"):
            vols.append(f"{base}.{n:0{width}d}")
            n += 1
        if vols:
            out = vols
    _VOL_CACHE[key] = out
    return out


class _Stream:
    """IInStream：Read / Seek，**按需读文件**（不把整包读进内存；大 7z 也扛得住）。

    **多卷**（`x.7z.001` + `.002` + …）在这里被当成**一条虚拟流**：地址空间首尾相接，
    `Seek`/`Read` 越过卷边界就自动接着下一卷。

    为什么不是 `IArchiveOpenVolumeCallback`：**实测这个 handler 连 QI 都不问那个接口**
    （只问密码接口），而把 5 卷直接拼成一个文件喂给它就完全正常 —— 也就是 7z 的多卷
    是**客户端自己拼流**、handler 根本不知道分卷这回事。那就照做，反而更简单：
    Extract（`-mhe=off` 那条路）要跨卷读数据也自动成立。
    """

    def __init__(self) -> None:
        self.parts: list[str] = []       # 各卷路径（单卷就是 1 个）
        self.sizes: list[int] = []
        self.total = 0
        self.pos = 0                     # 虚拟流里的绝对位置
        self.fp = None
        self._cur = -1                   # 当前开着的是第几卷
        self._base = 0                   # 当前卷在虚拟流里的起点
        V = ctypes.WINFUNCTYPE
        f = (V(c_int32, c_void_p, c_void_p, c_void_p)(self._qi),
             V(c_uint32, c_void_p)(self._addref),
             V(c_uint32, c_void_p)(self._release),
             V(c_int32, c_void_p, c_void_p, c_uint32, POINTER(c_uint32))(self._read),
             V(c_int32, c_void_p, c_int64, c_uint32, POINTER(c_uint64))(self._seek))
        self._fns = f                                   # 保引用，别删
        self._vt = (c_void_p * 5)(*[ctypes.cast(x, c_void_p) for x in f])
        self._vtp = (c_void_p * 1)(ctypes.cast(self._vt, c_void_p))

    def ptr(self):
        return ctypes.cast(self._vtp, c_void_p)

    def ensure(self, path: str) -> None:
        self.ensure_multi([path])

    def ensure_multi(self, paths: list[str]) -> None:
        """钉住这一组卷（换了组才重新统计大小），并把读位置归零。"""
        paths = list(paths)
        if paths != self.parts:
            self._drop()
            self.parts = paths
            self.sizes = []
            for p in paths:
                try:
                    self.sizes.append(os.path.getsize(p))
                except OSError:
                    self.sizes.append(0)
            self.total = sum(self.sizes)
        self.pos = 0

    def close(self) -> None:
        self._drop()
        self.parts = []
        self.sizes = []
        self.total = 0
        self.pos = 0

    def _drop(self) -> None:
        if self.fp is not None:
            try:
                self.fp.close()
            finally:
                self.fp = None
                self._cur = -1

    def _open_part(self, i: int) -> bool:
        if i < 0 or i >= len(self.parts):
            return False
        if self._cur == i and self.fp is not None:
            return True
        self._drop()
        self.fp = open(self.parts[i], "rb")
        self._cur = i
        self._base = sum(self.sizes[:i])
        return True

    def _locate(self, pos: int) -> tuple[int, int]:
        """虚拟位置 → (第几卷, 卷内偏移)；越界回 (len(parts), 0)。"""
        off = 0
        for i, sz in enumerate(self.sizes):
            if pos < off + sz:
                return i, pos - off
            off += sz
        return len(self.sizes), 0

    def _qi(self, this, riid, ppv):
        if _raw(riid, 16) in (_IID_IUnknown, _IID_IInStream):
            _write_ptr(ppv, self.ptr())
            return _S_OK
        _write_ptr(ppv, None)
        return _E_NOINTERFACE

    def _addref(self, this):
        return 2

    def _release(self, this):
        return 1

    def _read(self, this, data, size, processed):
        got = 0
        want = int(size)
        try:
            while want > 0:
                i, off = self._locate(self.pos)
                if not self._open_part(i):
                    break                            # 读完最后一卷
                self.fp.seek(off)
                buf = self.fp.read(want)
                if not buf:
                    break
                ctypes.memmove(data + got, buf, len(buf))
                got += len(buf)
                self.pos += len(buf)
                want -= len(buf)
        except Exception:            # noqa: BLE001 - 读失败就报已经读到的字节数
            pass
        if processed:
            processed[0] = got
        return _S_OK

    def _seek(self, this, offset, origin, newpos):
        base = (0, self.pos, self.total)[origin] if origin < 3 else 0
        new = base + int(offset)
        self.pos = new if new > 0 else 0
        if newpos:
            newpos[0] = self.pos
        return _S_OK


class _Callback:
    """`IArchiveOpenCallback` + `ICryptoGetTextPassword`。

    **两个接口各一张 vtable**（各自以 IUnknown 三方法开头），QI 返回对应那张的指针 ——
    这是原型里最关键的一处：拼成一张表会让 handler 把取密码调到 `SetTotal` 上。

    分卷**不走这里**：实测 handler 连 `IArchiveOpenVolumeCallback` 都不 QI
    （只问密码接口），7z 的多卷是客户端把分卷拼成一条流喂给它的 —— 见 `_Stream`。
    """

    def __init__(self) -> None:
        self.password = ""
        self.pw_calls = 0
        V = ctypes.WINFUNCTYPE
        f_qi = V(c_int32, c_void_p, c_void_p, c_void_p)(self._qi)
        f_add = V(c_uint32, c_void_p)(self._addref)
        f_rel = V(c_uint32, c_void_p)(self._release)
        f_tot = V(c_int32, c_void_p, c_void_p, c_void_p)(self._settotal)
        f_com = V(c_int32, c_void_p, c_void_p, c_void_p)(self._setcompleted)
        f_pw = V(c_int32, c_void_p, c_void_p)(self._getpw)
        self._fns = (f_qi, f_add, f_rel, f_tot, f_com, f_pw)
        # IArchiveOpenCallback：QI / AddRef / Release / SetTotal / SetCompleted
        self._vt_oc = (c_void_p * 5)(ctypes.cast(f_qi, c_void_p), ctypes.cast(f_add, c_void_p),
                                     ctypes.cast(f_rel, c_void_p), ctypes.cast(f_tot, c_void_p),
                                     ctypes.cast(f_com, c_void_p))
        self._vtp_oc = (c_void_p * 1)(ctypes.cast(self._vt_oc, c_void_p))
        # ICryptoGetTextPassword：QI / AddRef / Release / CryptoGetTextPassword
        self._vt_pw = (c_void_p * 4)(ctypes.cast(f_qi, c_void_p), ctypes.cast(f_add, c_void_p),
                                     ctypes.cast(f_rel, c_void_p), ctypes.cast(f_pw, c_void_p))
        self._vtp_pw = (c_void_p * 1)(ctypes.cast(self._vt_pw, c_void_p))

    def ptr_oc(self):
        return ctypes.cast(self._vtp_oc, c_void_p)

    def reset(self) -> None:
        self.pw_calls = 0

    def _qi(self, this, riid, ppv):
        g = _raw(riid, 16)
        if _DEBUG:
            print(f"[dll7z] openCallback QI {g.hex()}", flush=True)
        if g in (_IID_IUnknown, _IID_IArchiveOpenCallback):
            _write_ptr(ppv, self.ptr_oc())
            return _S_OK
        if g == _IID_ICryptoGetTextPassword:
            _write_ptr(ppv, ctypes.cast(self._vtp_pw, c_void_p))
            return _S_OK
        _write_ptr(ppv, None)
        return _E_NOINTERFACE

    def _addref(self, this):
        return 2

    def _release(self, this):
        return 1

    def _settotal(self, this, files, bytes_):
        return _S_OK

    def _setcompleted(self, this, files, bytes_):
        return _S_OK

    def _getpw(self, this, bstr_out):
        self.pw_calls += 1
        if self.pw_calls > _MAX_PW_CALLS:
            _write_ptr(bstr_out, None)
            return _E_ABORT
        _write_ptr(bstr_out, _SYS.SysAllocString(self.password))
        return _S_OK


class _DiscardStream:
    """`ISequentialOutStream`：`Write()` 把数据直接丢掉。

    走 `Extract(testMode=1)` 时 handler **仍然会要一个输出流**（实测 `GetStream` 被调 1 次），
    所以必须给一个真的能收字节的东西 —— 但我们只关心"结果码"，字节本身不要。
    """

    def __init__(self) -> None:
        self.bytes = 0
        V = ctypes.WINFUNCTYPE
        f = (V(c_int32, c_void_p, c_void_p, c_void_p)(self._qi),
             V(c_uint32, c_void_p)(self._addref),
             V(c_uint32, c_void_p)(self._release),
             V(c_int32, c_void_p, c_void_p, c_uint32, POINTER(c_uint32))(self._write))
        self._fns = f
        self._vt = (c_void_p * 4)(*[ctypes.cast(x, c_void_p) for x in f])
        self._vtp = (c_void_p * 1)(ctypes.cast(self._vt, c_void_p))

    def ptr(self):
        return ctypes.cast(self._vtp, c_void_p)

    def _qi(self, this, riid, ppv):
        g = _raw(riid, 16)
        if g in (_IID_IUnknown, _IID_ISequentialOutStream):
            _write_ptr(ppv, self.ptr())
            return _S_OK
        _write_ptr(ppv, None)
        return _E_NOINTERFACE

    def _addref(self, this):
        return 2

    def _release(self, this):
        return 1

    def _write(self, this, data, size, processed):
        self.bytes += int(size)
        if processed:
            processed[0] = int(size)
        return _S_OK


class _ExtractCallback:
    """`IArchiveExtractCallback`（= IProgress + GetStream/PrepareOperation/SetOperationResult）
    **外加 `ICryptoGetTextPassword` 再一张 vtable** —— §20.10 那条教训：每个接口一张以
    IUnknown 三方法开头的表，QI 返回该接口自己的指针。

    handler 在 `Extract` 时会问一次密码（实测 `pw_calls=1`），密码就喂给这里。
    """

    def __init__(self) -> None:
        self.password = ""
        self.pw_calls = 0
        self.ops: list[int] = []
        self.streams = 0
        self.bytes = 0
        self.discard = _DiscardStream()
        V = ctypes.WINFUNCTYPE
        qi = V(c_int32, c_void_p, c_void_p, c_void_p)(self._qi)
        add = V(c_uint32, c_void_p)(self._addref)
        rel = V(c_uint32, c_void_p)(self._release)
        tot = V(c_int32, c_void_p, c_uint64)(self._settotal)
        com = V(c_int32, c_void_p, c_void_p)(self._setcompleted)
        gst = V(c_int32, c_void_p, c_uint32, c_void_p, c_int32)(self._getstream)
        pre = V(c_int32, c_void_p, c_int32)(self._prepare)
        res = V(c_int32, c_void_p, c_int32)(self._result)
        pwd = V(c_int32, c_void_p, c_void_p)(self._getpw)
        self._fns = (qi, add, rel, tot, com, gst, pre, res, pwd)
        self._vt_x = (c_void_p * 8)(ctypes.cast(qi, c_void_p), ctypes.cast(add, c_void_p),
                                    ctypes.cast(rel, c_void_p), ctypes.cast(tot, c_void_p),
                                    ctypes.cast(com, c_void_p), ctypes.cast(gst, c_void_p),
                                    ctypes.cast(pre, c_void_p), ctypes.cast(res, c_void_p))
        self._vtp_x = (c_void_p * 1)(ctypes.cast(self._vt_x, c_void_p))
        self._vt_p = (c_void_p * 4)(ctypes.cast(qi, c_void_p), ctypes.cast(add, c_void_p),
                                    ctypes.cast(rel, c_void_p), ctypes.cast(pwd, c_void_p))
        self._vtp_p = (c_void_p * 1)(ctypes.cast(self._vt_p, c_void_p))

    def ptr(self):
        return ctypes.cast(self._vtp_x, c_void_p)

    def reset(self) -> None:
        self.pw_calls = 0
        self.ops = []
        self.streams = 0
        self.bytes = 0
        self.discard.bytes = 0

    def _qi(self, this, riid, ppv):
        g = _raw(riid, 16)
        if g in (_IID_IUnknown, _IID_IArchiveExtractCallback):
            _write_ptr(ppv, self.ptr())
            return _S_OK
        if g == _IID_ICryptoGetTextPassword:
            _write_ptr(ppv, ctypes.cast(self._vtp_p, c_void_p))
            return _S_OK
        _write_ptr(ppv, None)
        return _E_NOINTERFACE

    def _addref(self, this):
        return 2

    def _release(self, this):
        return 1

    def _settotal(self, this, total):
        return _S_OK

    def _setcompleted(self, this, complete):
        return _S_OK

    def _getstream(self, this, index, out_stream, ask_mode):
        self.streams += 1
        _write_ptr(out_stream, self.discard.ptr())
        return _S_OK

    def _prepare(self, this, ask_mode):
        return _S_OK

    def _result(self, this, op_res):
        self.ops.append(int(op_res))
        self.bytes = self.discard.bytes
        return _S_OK

    def _getpw(self, this, bstr_out):
        self.pw_calls += 1
        if self.pw_calls > _MAX_PW_CALLS:
            _write_ptr(bstr_out, None)
            return _E_ABORT
        _write_ptr(bstr_out, _SYS.SysAllocString(self.password))
        return _S_OK


class _Archive:
    """一个常驻的 `IInArchive` + 文件流 + 回调（模块锁内使用）。"""

    def __init__(self, dll_path: str) -> None:
        h = _K32.LoadLibraryW(dll_path)
        if not h:
            raise OSError(f"LoadLibraryW({dll_path}) 失败：{ctypes.get_last_error()}")
        self.h = h
        self.procs = {n: _K32.GetProcAddress(h, n.encode())
                      for n in ("CreateObject", "GetNumberOfFormats", "GetHandlerProperty2")}
        if not all(self.procs.values()):
            raise OSError("7z.dll 缺少 CreateObject/GetNumberOfFormats/GetHandlerProperty2")
        CF = ctypes.CFUNCTYPE
        self.CreateObject = CF(c_int32, POINTER(_GUID), POINTER(_GUID), POINTER(c_void_p))(
            int(self.procs["CreateObject"]))
        self.GetNumberOfFormats = CF(c_int32, POINTER(c_uint32))(
            int(self.procs["GetNumberOfFormats"]))
        self.GetHandlerProperty2 = CF(c_int32, c_uint32, c_uint32, POINTER(_PROPVARIANT))(
            int(self.procs["GetHandlerProperty2"]))
        self._fns = (self.CreateObject, self.GetNumberOfFormats, self.GetHandlerProperty2)
        self.clsid = self._find_7z_clsid()
        arch = c_void_p()
        hr = int(self.CreateObject(byref(self.clsid), byref(_IID_IInArchive), byref(arch)))
        if hr < 0 or not arch:
            raise OSError(f"CreateObject(IInArchive) 失败 hr=0x{hr & 0xFFFFFFFF:08X}")
        self.arch = arch
        self.stream = _Stream()
        self.cb = _Callback()
        self._xcb = _ExtractCallback()          # 明文头那条路的解压回调（常驻复用）

    # ---- 找 7z handler 的 CLSID（不写死，跟着 dll 走）
    def _prop(self, index: int, propid: int) -> _PROPVARIANT:
        pv = _PROPVARIANT()
        self.GetHandlerProperty2(index, propid, byref(pv))
        return pv

    def _find_7z_clsid(self) -> _GUID:
        n = c_uint32(0)
        self.GetNumberOfFormats(byref(n))
        for i in range(n.value):
            pv = self._prop(i, _KNAME)
            if pv.vt != _VT_BSTR or not pv.u.punkVal:
                continue
            strlen = _K32.lstrlenW(c_void_p(pv.u.punkVal))
            name = _raw(c_void_p(pv.u.punkVal), strlen * 2).decode("utf-16-le", "replace")
            if name == "7z":
                cl = self._prop(i, _KCLASSID)
                if cl.u.punkVal:
                    return _GUID.from_buffer_copy(_raw(c_void_p(cl.u.punkVal), sizeof(_GUID)))
        raise OSError("这个 7z.dll 里没找到 7z handler")

    # ---- 一次验证
    def verdict(self, archive: str, password: str) -> bool:
        """返回 True = **这个密码肯定不对**。两条路自动选：

        ① **加密头**（`-mhe=on`）：`Open()` 会顺手解头块并校验 CRC32 ——
           密码错 `S_FALSE`、密码对 `S_OK`（且密码回调 ≥1 次）；一条 `Open` 就出结论。
        ② **明文头**（`-mhe=off`）：`Open()` 对任何密码都是 `S_OK` 且**不问密码**（回调 0 次），
           判据只能靠真解一点数据：`Extract(testMode=1)` 只解**最小的那个加密条目**，
           看 `SetOperationResult` 的结果码（2 数据错误 / 3 CRC 错 = 密码肯定不对）。

        **分卷**（`x.7z.001`）靠**把同组各卷拼成一条虚拟流**顶过去（`_volume_paths` + `_Stream`）——
        实测 handler 根本不问 `IArchiveOpenVolumeCallback`，7z 的多卷是客户端自己拼流的。
        """
        v = ctypes.cast(self.arch, POINTER(POINTER(c_void_p)))[0]
        self.stream.ensure_multi(_volume_paths(archive))
        self.cb.password = password
        self.cb.reset()
        hr = int(_VT_OPEN(v[3])(self.arch, self.stream.ptr(), None, self.cb.ptr_oc()))
        try:
            if hr == _S_FALSE and self.cb.pw_calls >= 1:
                return True                      # ① 加密头 + 密码错
            if hr < 0 or hr == _S_FALSE:
                return False                     # 打不开（缺卷/损坏）或问不出密码 → 交给引擎
            if self.cb.pw_calls >= 1:
                return False                     # ① 加密头 + 密码对（不能排除）
            return self._verdict_extract(v, archive, password)   # ② 明文头（或不加密）
        finally:
            try:
                _VT_CLOSE(v[4])(self.arch)       # 每次 Open 之后 Close，别留状态
            except Exception:                    # noqa: BLE001
                pass
            self.stream.close()                  # 分卷句柄也别攒着

    def _verdict_extract(self, v, archive: str, password: str) -> bool:
        """明文头那条：只解最小的加密条目，看结果码。

        判不出结论的包会记进 `_NO_VERDICT`：Extract 真跑了一遍（十几毫秒），
        但结果码既不是"密码错"也不是 OK —— 实测 `-mhe=off` + ZS 编码（lz4/brotli）就是这样，
        多半报"不支持的方法"。那种包**后面的候选就别再花这笔钱了**，直接回退引擎。
        """
        key = _no_verdict_key(archive)
        if key is not None and _NO_VERDICT.get(key):
            return False
        pick = self._pick_item(v, archive)
        if pick is None:
            return False                         # 没有加密条目（或看不出来）→ 交给引擎
        x = self._xcb
        x.password = password
        x.reset()
        idx = (c_uint32 * 16)(pick)              # `const UInt32*`，给足字节防止认错槽位写穿
        try:
            _VT_EXTRACT(v[7])(self.arch, ctypes.cast(idx, c_void_p), 1, 1, x.ptr())
        except OSError:
            raise                                # 硬故障（access violation）交上去数三振
        except Exception:                        # noqa: BLE001
            return False
        if not x.ops:
            return False
        last = x.ops[-1]
        if last in _OP_WRONG:
            return True
        if last not in (0,):                     # 0 = OK（密码正确）；其余都是"判不出来"
            if key is not None:
                _NO_VERDICT[key] = True
        return False

    def _pick_item(self, v, archive: str):
        """最小的**加密**条目下标（明文头那条路用）；没有就 None。

        按 (路径, 大小, mtime) 缓存 —— 它跟密码无关，一个包 1000 个候选只该算一次。
        条目特别多的包只扫前 `_MAX_ITEM_SCAN` 个（扫一遍是 GetProperty × N，代价不能失控；
        扫不到就回退，宁可交给引擎也别把时间花在扫条目上）。
        """
        try:
            st = os.stat(archive)
        except OSError:
            return None
        key = (os.path.normcase(os.path.abspath(archive)), st.st_size, st.st_mtime_ns)
        if key in _PICK_CACHE:
            return _PICK_CACHE[key]
        pick = None
        try:
            n = c_uint32(0)
            if int(_VT_GETITEMS(v[5])(self.arch, byref(n))) < 0:
                pick = None
            else:
                # ★ 必须**从 vtable 取地址再构造**：`_VT_ITEMPROP()` 空参构造出来的是
                # "空函数指针原型"（地址 0），一调就是 access violation writing 0x0，
                # 看着像 PROPVARIANT 布局错，其实是根本没绑函数。
                getprop = _VT_ITEMPROP(v[6])
                count = min(int(n.value), _MAX_ITEM_SCAN)
                best = None
                for i in range(count):
                    if not _prop_bool(getprop, self.arch, i, _KPID_ENCRYPTED):
                        continue
                    size = _prop_int(getprop, self.arch, i, _KPID_SIZE)
                    if size is None:
                        continue
                    if best is None or size < best[1]:
                        best = (i, size)
                pick = best[0] if best else None
        except OSError:
            raise                                # 硬故障（access violation）交上去数三振
        except Exception:                        # noqa: BLE001
            pick = None
        _PICK_CACHE[key] = pick
        return pick


def _install_unraisable_hook() -> None:
    """`SMART_UNZIP_DEBUG_DLL7Z=1` 时把"ctypes 回调里抛的异常"暴露出来。

    **回调里抛异常是静默的**：ctypes 只把 traceback 丢给 `sys.unraisablehook`（默认打到
    stderr，界面程序里等于看不见），而那个回调对 handler 来说只是"返回了 0"。
    本模块为此丢过一次人：`_VT_EMPTY` 忘了定义，`_volprop` 每次都抛 NameError，
    表现却是"handler 从来不问分卷" —— 所以调试模式下必须让它响。
    """
    old = sys.unraisablehook

    def hook(args) -> None:                                  # type: ignore[no-untyped-def]
        print(f"[dll7z] ★ 回调里抛异常（被 ctypes 吞了）：{args.exc_type.__name__}: {args.exc_value}",
              flush=True)
        old(args)

    sys.unraisablehook = hook


def _init() -> "_Archive | None":
    """惰性初始化；失败就永久标记 dead（不再反复试）。"""
    global _OBJ, _STATE
    if _STATE == "dead":
        return None
    if _OBJ is not None:
        return _OBJ
    try:
        from core import paths

        dll = paths.resource_path("tools", "7z", "7z.dll")
        if not os.path.isfile(dll):
            raise OSError(f"找不到 {dll}")
        _OBJ = _Archive(dll)
        _STATE = "ready"
        if _DEBUG:
            _install_unraisable_hook()
    except Exception:            # noqa: BLE001 - 任何问题都退回引擎
        if _DEBUG:
            import traceback

            traceback.print_exc()
        _STATE = "dead"
        _OBJ = None
    return _OBJ


def _enabled() -> bool:
    """`SMART_UNZIP_NO_DLL7Z=1` 单独关掉这一段（`SMART_UNZIP_NO_FASTCHECK=1` 关整条快路）。"""
    return os.environ.get("SMART_UNZIP_NO_DLL7Z", "") not in ("1", "true", "yes")


def available() -> bool:
    if not _enabled():
        return False
    with _LOCK:
        try:
            return _init() is not None
        except Exception:        # noqa: BLE001 - 探测本身也不许抛
            return False


def quick_reject(archive: str, password: str) -> bool:
    """`True` = 这个密码**肯定不对**（别起引擎了）；`False` = 不知道，交给引擎。"""
    global _FAILS
    if not password or not _enabled():
        return False
    with _LOCK:
        try:
            obj = _init()                    # 连初始化也包在里面：这个方法对外承诺"永不抛"
            if obj is None:
                return False
            return obj.verdict(archive, password)
        except Exception:        # noqa: BLE001 - 出错就退回引擎，连着错就关掉这条路
            _FAILS += 1
            if _FAILS >= 3:
                global _STATE
                _STATE = "dead"
            return False
