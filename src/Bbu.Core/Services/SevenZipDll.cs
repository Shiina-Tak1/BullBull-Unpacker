using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Bbu.Core.Services;

/// <summary>
/// 常驻 <c>7z.dll</c> 的互操作契约：签名、HRESULT、PROPVARIANT 读取，以及两侧的接口声明。
///
/// 分工是这条路的关键：**调用方**用 <c>[ComImport]</c> 接口，槽位由声明顺序决定
/// （第一个方法就是 slot 3，不必手写 vtable）；**回调方**用托管类加 <c>[ComVisible]</c>，
/// 由 CLR 生成 CCW —— 每个接口一张 vtable、QI 按 IID 匹配、BSTR 与引用计数都由 CLR 管。
/// 手写 vtable 那条路也做通了，但每一处都要自己保 delegate 引用与双层指针，所以不用它；
///
/// 下面这些回调接口与实现类都是 <c>public</c>：**CLR 的 CCW 只把 public 类型暴露给 COM**，
/// 写成 <c>internal</c> 会让 marshal 失败（症状是每次判定都回「不知道」，不抛也不报）。
/// </summary>
internal static class SevenZipDll
{
    /// <summary>7z 的签名，6 字节。</summary>
    internal static readonly byte[] Magic = [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c];

    internal const int SOk = 0;
    internal const int SFalse = 1;

    /// <summary>问不出密码时回这个码，让 handler 自己中止重试。</summary>
    internal const int EAbort = unchecked((int)0x80004004);

    /// <summary>密码回调最多给几次：错密码时 7-Zip 可能再问，给满就让它失败。</summary>
    internal const int MaxPasswordCalls = 2;

    /// <summary>明文头那条路要扫「最小加密条目」，条目太多的包只扫前这么多。</summary>
    internal const int MaxItemScan = 50_000;

    internal const uint KpidSize = 7;
    internal const uint KpidEncrypted = 15;

    internal const int OpOk = 0;
    internal const int OpDataError = 2;
    internal const int OpCrcError = 3;

    private const int VtBstr = 8;
    private const int VtI4 = 3;
    private const int VtBool = 11;
    private const int VtUi4 = 19;
    private const int VtUi8 = 21;

    /// <summary>PROPVARIANT 的整数型取值；读不到回 <c>null</c>。</summary>
    internal static long? PropInt(IntPtr value)
    {
        var type = (ushort)Marshal.ReadInt16(value);
        return type switch
        {
            VtUi8 => Marshal.ReadInt64(value, 8),
            VtUi4 => (uint)Marshal.ReadInt32(value, 8),
            VtI4 => Marshal.ReadInt32(value, 8),
            VtBool => (ushort)Marshal.ReadInt16(value, 8),
            _ => null,
        };
    }

    /// <summary>PROPVARIANT 的布尔型取值（VARIANT_BOOL：非零为真）。</summary>
    internal static bool PropBool(IntPtr value) =>
        (ushort)Marshal.ReadInt16(value) == VtBool && Marshal.ReadInt16(value, 8) != 0;

    /// <summary>
    /// 这个 PROPVARIANT 是不是 BSTR。**读指针之前必须问这一句**：
    /// union 那个偏移在别的类型下不是指针，直接当字符串读就是一次越界（进程当场结束）。
    /// </summary>
    internal static bool IsBstr(IntPtr value) => (ushort)Marshal.ReadInt16(value) == VtBstr;

    /// <summary>
    /// 读 <c>GetProperty</c> / <c>GetHandlerProperty2</c> 用的一块 PROPVARIANT 缓冲。
    /// **每次调用前必须清零**：handler 见到一个非空的 <c>vt</c> 就不往里写，
    /// 复用的缓冲会留下上一次（或堆上）的垃圾，读出来的是随机值。
    /// </summary>
    internal sealed class PropBuffer : IDisposable
    {
        /// <summary>官方 PROPVARIANT 在 x64 是 24 字节（8 字节头加 16 字节 union）。</summary>
        internal const int Size = 24;

        private readonly IntPtr _memory = Marshal.AllocHGlobal(Size);

        internal IntPtr Pointer => _memory;

        internal void Clear()
        {
            Marshal.WriteInt64(_memory, 0);
            Marshal.WriteInt64(_memory, 8, 0);
            Marshal.WriteInt64(_memory, 16, 0);
        }

        public void Dispose() => Marshal.FreeHGlobal(_memory);
    }
}

/// <summary>
/// <c>IInArchive</c>：本实现要调用的一侧。方法顺序照官方 <c>IArchive.h</c>，
/// <c>Extract</c> 在 <c>GetProperty</c> **后面**（不是最后）。
/// <c>[PreserveSig]</c> 是必需的：判据正是 <c>S_FALSE</c> 与 <c>S_OK</c> 的区别。
/// </summary>
[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, IntPtr maxCheckStart, IArchiveOpenCallback? callback);

    [PreserveSig] int Close();

    [PreserveSig] int GetNumberOfItems(out uint numItems);

    [PreserveSig] int GetProperty(uint index, uint propId, IntPtr value);

    [PreserveSig] int Extract(IntPtr indices, uint numItems, int testMode, IArchiveExtractCallback callback);
}

/// <summary><c>IInStream</c>：按需读文件，不把整包读进内存；多卷在这里拼成一条虚拟流。</summary>
[ComVisible(true), Guid("23170F69-40C1-278A-0000-000300030000"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processed);

    [PreserveSig] int Seek(long offset, uint origin, IntPtr newPosition);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000600100000"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);

    [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000500100000"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password);
}

[ComVisible(true), Guid("23170F69-40C1-278A-0000-000600200000"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveExtractCallback
{
    [PreserveSig] int SetTotal(ulong total);

    [PreserveSig] int SetCompleted(IntPtr complete);

    [PreserveSig] int GetStream(uint index, out ISequentialOutStream stream, int askMode);

    [PreserveSig] int PrepareOperation(int askMode);

    [PreserveSig] int SetOperationResult(int opResult);
}

/// <summary>写回调。IID 照 7-Zip 官方源码包 <c>DOC\Guid.txt</c> 里 <c>ISequentialOutStream</c> 那一行。</summary>
[ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processed);
}

/// <summary>
/// <c>IInStream</c> 的实现。多卷在地址空间里首尾相接，<c>Read</c> 越过卷边界自动接着下一卷。
///
/// 为什么不走 <c>IArchiveOpenVolumeCallback</c>：实测这个 handler 连 QI 都不问那个接口，
/// 7z 的多卷是客户端自己拼流、handler 根本不知道有分卷这回事。
/// </summary>
[ComVisible(true)]
public sealed class NativeStreamServer : IInStream
{
    private readonly List<string> _parts = [];
    private readonly List<long> _sizes = [];

    private readonly byte[] _chunk = new byte[1 << 16];

    private long _total;
    private long _position;
    private FileStream? _current;
    private int _currentIndex = -1;

    /// <summary>
    /// 钉住这一组卷，并把读位置归零。卷大小是包级常量，只在卷集合发生变化时才重算 ——
    /// 每候选算一次会在候选循环里破坏「包级元数据只在开庭时解析一次」这条契约。
    /// </summary>
    internal void Bind(IReadOnlyList<string> parts)
    {
        if (!SameParts(parts))
        {
            _parts.Clear();
            _parts.AddRange(parts);
            _sizes.Clear();
            _total = 0;
            foreach (var part in parts)
            {
                var size = SizeOf(part);
                _sizes.Add(size);
                _total += size;
            }
        }

        CloseHandle();
        _position = 0;
    }

    /// <summary>与上一次钉住的卷集合是否相同。不同则需重算卷大小。</summary>
    private bool SameParts(IReadOnlyList<string> parts)
    {
        if (_parts.Count != parts.Count)
        {
            return false;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (!string.Equals(_parts[i], parts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>每次 <c>Open</c> 之后收工：只放掉文件句柄与读位置，卷列表留着下次复用。</summary>
    internal void Close()
    {
        CloseHandle();
        _position = 0;
    }

    /// <summary>这条虚拟流认到的总长度。卷大小是包级常量，所以它在一次会话里不变。</summary>
    internal long Total => _total;

    public int Read(IntPtr data, uint size, IntPtr processed)
    {
        long got = 0;
        try
        {
            var want = size;
            while (want > 0)
            {
                var (index, offset) = Locate(_position);
                if (!OpenPart(index))
                {
                    break;
                }

                _current!.Position = offset;
                var take = (int)Math.Min(want, _chunk.Length);
                var read = _current.Read(_chunk, 0, take);
                if (read <= 0)
                {
                    break;
                }

                Marshal.Copy(_chunk, 0, data + (int)got, read);
                got += read;
                _position += read;
                want -= (uint)read;
            }

            if (processed != IntPtr.Zero)
            {
                Marshal.WriteInt32(processed, (int)got);
            }
        }
        catch (Exception)
        {
            // 读失败就报已经读到的字节数，由 handler 自己判「数据不完整」。
            // 回调里抛出的托管异常跨不回 native 边界，会直接终止进程。
        }

        return SevenZipDll.SOk;
    }

    public int Seek(long offset, uint origin, IntPtr newPosition)
    {
        try
        {
            var start = origin switch
            {
                0 => 0L,
                1 => _position,
                2 => _total,
                _ => 0L,
            };
            var next = start + offset;
            _position = next > 0 ? next : 0;
            if (newPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(newPosition, _position);
            }
        }
        catch (Exception)
        {
            // 算不出来就当没动过，位置保持不变。
        }

        return SevenZipDll.SOk;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void CloseHandle()
    {
        _current?.Dispose();
        _current = null;
        _currentIndex = -1;
    }

    private bool OpenPart(int index)
    {
        if (index < 0 || index >= _parts.Count)
        {
            return false;
        }

        if (_currentIndex == index && _current is not null)
        {
            return true;
        }

        CloseHandle();
        _current = new FileStream(_parts[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _currentIndex = index;
        return true;
    }

    /// <summary>虚拟位置 → (第几卷, 卷内偏移)；越界回 <c>(-1, 0)</c>。</summary>
    private (int Index, long Offset) Locate(long position)
    {
        var at = 0L;
        for (var i = 0; i < _sizes.Count; i++)
        {
            if (position < at + _sizes[i])
            {
                return (i, position - at);
            }

            at += _sizes[i];
        }

        return (-1, 0);
    }
}

/// <summary>
/// <c>IArchiveOpenCallback</c> 与 <c>ICryptoGetTextPassword</c> 的实现。
/// 两个接口都由 CLR 各自生成一张 vtable 与一次 QI —— 手写那条路在这里最容易出错。
/// </summary>
[ComVisible(true)]
public sealed class OpenCallbackServer : IArchiveOpenCallback, ICryptoGetTextPassword
{
    internal string Password { get; set; } = string.Empty;

    internal int PasswordCalls { get; private set; }

    internal void Reset() => PasswordCalls = 0;

    public int SetTotal(IntPtr files, IntPtr bytes) => SevenZipDll.SOk;

    public int SetCompleted(IntPtr files, IntPtr bytes) => SevenZipDll.SOk;

    public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
    {
        // 回调里抛出的托管异常跨不回 native 边界，所以这个方法自己兜住所有异常。
        try
        {
            PasswordCalls++;
            if (PasswordCalls > SevenZipDll.MaxPasswordCalls)
            {
                password = string.Empty;
                return SevenZipDll.EAbort;
            }

            password = Password;
            return SevenZipDll.SOk;
        }
        catch (Exception)
        {
            password = string.Empty;
            return SevenZipDll.EAbort;
        }
    }
}

/// <summary><c>ISequentialOutStream</c>：把字节直接丢掉。<c>testMode=1</c> 也照样要一个输出流。</summary>
[ComVisible(true)]
public sealed class DiscardStreamServer : ISequentialOutStream
{
    internal long Bytes { get; private set; }

    internal void Reset() => Bytes = 0;

    public int Write(IntPtr data, uint size, IntPtr processed)
    {
        try
        {
            Bytes += size;
            if (processed != IntPtr.Zero)
            {
                Marshal.WriteInt32(processed, (int)size);
            }
        }
        catch (Exception)
        {
            // 丢字节这条路不该失败；失败了也不许把异常带回 native。
        }

        return SevenZipDll.SOk;
    }
}

/// <summary>
/// <c>IArchiveExtractCallback</c> 与 <c>ICryptoGetTextPassword</c> 的实现。
/// 判据是 <c>SetOperationResult</c> 的结果码：2 数据错误 / 3 CRC 错 = 这个密码肯定不对。
/// </summary>
[ComVisible(true)]
public sealed class ExtractCallbackServer : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private readonly DiscardStreamServer _discard = new();

    internal string Password { get; set; } = string.Empty;

    internal List<int> Results { get; } = [];

    internal void Reset()
    {
        Password = string.Empty;
        Results.Clear();
        _discard.Reset();
    }

    public int SetTotal(ulong total) => SevenZipDll.SOk;

    public int SetCompleted(IntPtr complete) => SevenZipDll.SOk;

    public int GetStream(uint index, out ISequentialOutStream stream, int askMode)
    {
        stream = _discard;
        return SevenZipDll.SOk;
    }

    public int PrepareOperation(int askMode) => SevenZipDll.SOk;

    public int SetOperationResult(int opResult)
    {
        try
        {
            Results.Add(opResult);
        }
        catch (Exception)
        {
            // 记不下结果码时按「判不出来」处理：不记就等于没有结论。
        }

        return SevenZipDll.SOk;
    }

    public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
    {
        try
        {
            password = Password;
            return SevenZipDll.SOk;
        }
        catch (Exception)
        {
            password = string.Empty;
            return SevenZipDll.EAbort;
        }
    }
}
