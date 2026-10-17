using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

// 7z.dll 只存在于 Windows，这个文件的 COM 互操作整体就是 Windows 实现；
// 平台可用性在调用侧判（见 CompositeFastPath），所以这里抑制 CA1416。
#pragma warning disable CA1416

namespace Bbu.Core.Services;

/// <summary>
/// 常驻的 <c>7z.dll</c>：加载一次、找 7z handler 的 CLSID、按需 CreateObject。
/// 加载与探测失败都回 <c>null</c>，而且不计数（失败计数的归属见 <see cref="FastPathFailureGuard"/>）。
/// </summary>
internal sealed class SevenZipHost
{
    private const int KName = 0;
    private const int KClassId = 1;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, SevenZipHost> Cache = new(StringComparer.OrdinalIgnoreCase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateObjectFn(IntPtr classId, IntPtr iid, IntPtr outObject);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetNumberOfFormatsFn(IntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetHandlerProperty2Fn(uint index, uint propId, IntPtr value);

    private readonly CreateObjectFn _createObject;
    private readonly GetNumberOfFormatsFn _countFormats;
    private readonly GetHandlerProperty2Fn _handlerProperty;
    private readonly Guid _classId;

    private SevenZipHost(
        CreateObjectFn createObject,
        GetNumberOfFormatsFn countFormats,
        GetHandlerProperty2Fn handlerProperty)
    {
        _createObject = createObject;
        _countFormats = countFormats;
        _handlerProperty = handlerProperty;
        _classId = FindClassId();
    }

    /// <summary>dll 里有没有 7z 这个 handler。找不到就整条路都不走。</summary>
    private bool Ready => _classId != Guid.Empty;

    /// <summary>取一个已加载的 dll。失败不缓存，下次开庭重新探测。</summary>
    internal static SevenZipHost? Acquire(string? explicitPath, IRunLog? log = null)
    {
        var path = explicitPath ?? Locate();
        if (path is null)
        {
            return null;
        }

        lock (CacheGate)
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                return hit;
            }

            var loaded = Load(path, log);
            if (loaded is not null)
            {
                Cache[path] = loaded;
            }

            return loaded;
        }
    }

    /// <summary>
    /// dll 的位置：显式路径 → 程序目录旁的 <c>engines\7z\7z.dll</c> → 仓库根下随包那份（开发与测试环境）。
    /// </summary>
    internal static string? Locate()
    {
        var configured = Environment.GetEnvironmentVariable("BBU_SEVENZIP_DLL");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "engines", "7z", "7z.dll");
        if (File.Exists(beside))
        {
            return beside;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Bbu.slnx")))
            {
                continue;
            }

            var bundled = Path.Combine(directory.FullName, "src", "Bbu.App", "engines", "7z", "7z.dll");
            return File.Exists(bundled) ? bundled : null;
        }

        return null;
    }

    /// <summary>加载之前按清单核对：包内清单不在就跳过（开发树），不符就不加载这一份（B-2026-208）。</summary>
    private static bool IntegrityAllows(string path, IRunLog? log)
    {
        if (BundledIntegrity.PackageRootOf(path) is not { } root)
        {
            return true;
        }

        var state = BundledIntegrity.Check(root, BundledIntegrity.EngineFiles, out var offender);
        BundledIntegrity.Report(state, offender, log);
        return state != ComponentIntegrity.Mismatch;
    }

    private static SevenZipHost? Load(string path, IRunLog? log)
    {
        if (!File.Exists(path) || !IntegrityAllows(path, log))
        {
            return null;
        }

        IntPtr module;
        try
        {
            module = NativeLibrary.Load(path);
        }
        catch (Exception)
        {
            // 加载不了就是「这一段用不上」：不算异常，把 dll 放回来之后下一次开庭再来。
            return null;
        }

        if (!NativeLibrary.TryGetExport(module, "CreateObject", out var create)
            || !NativeLibrary.TryGetExport(module, "GetNumberOfFormats", out var count)
            || !NativeLibrary.TryGetExport(module, "GetHandlerProperty2", out var property))
        {
            return null;
        }

        var host = new SevenZipHost(
            Marshal.GetDelegateForFunctionPointer<CreateObjectFn>(create),
            Marshal.GetDelegateForFunctionPointer<GetNumberOfFormatsFn>(count),
            Marshal.GetDelegateForFunctionPointer<GetHandlerProperty2Fn>(property));
        return host.Ready ? host : null;
    }

    /// <summary>遍历 handler 表找名字为 <c>7z</c> 的那一条，取它的 CLSID（跟着 dll 走，不写死）。</summary>
    private Guid FindClassId()
    {
        using var buffer = new SevenZipDll.PropBuffer();
        if (_countFormats(buffer.Pointer) < 0)
        {
            return Guid.Empty;
        }

        var formats = Marshal.ReadInt32(buffer.Pointer);
        for (var i = 0; i < formats; i++)
        {
            buffer.Clear();
            if (_handlerProperty((uint)i, KName, buffer.Pointer) < 0
                || !SevenZipDll.IsBstr(buffer.Pointer)
                || ReadBstr(buffer.Pointer) != "7z")
            {
                continue;
            }

            buffer.Clear();
            if (_handlerProperty((uint)i, KClassId, buffer.Pointer) < 0
                || !SevenZipDll.IsBstr(buffer.Pointer))
            {
                return Guid.Empty;
            }

            var classId = Marshal.ReadIntPtr(buffer.Pointer, 8);
            if (classId == IntPtr.Zero)
            {
                return Guid.Empty;
            }

            var bytes = new byte[16];
            Marshal.Copy(classId, bytes, 0, 16);
            return new Guid(bytes);
        }

        return Guid.Empty;
    }

    private static string ReadBstr(IntPtr value)
    {
        var pointer = Marshal.ReadIntPtr(value, 8);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;
    }

    /// <summary>造一个 <c>IInArchive</c> 的原始指针。失败回 <see cref="IntPtr.Zero"/>。</summary>
    internal IntPtr CreateArchive()
    {
        var classId = Marshal.AllocHGlobal(16);
        var iid = Marshal.AllocHGlobal(16);
        var result = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.Copy(_classId.ToByteArray(), 0, classId, 16);
            Marshal.Copy(typeof(IInArchive).GUID.ToByteArray(), 0, iid, 16);
            Marshal.WriteIntPtr(result, IntPtr.Zero);
            return _createObject(classId, iid, result) < 0 ? IntPtr.Zero : Marshal.ReadIntPtr(result);
        }
        finally
        {
            Marshal.FreeHGlobal(classId);
            Marshal.FreeHGlobal(iid);
            Marshal.FreeHGlobal(result);
        }
    }
}

/// <summary>
/// 一个包上的 <c>IInArchive</c> 加它要的两条流与两个回调。判据两条：
/// 加密头看 <c>Open()</c> 的 HRESULT，明文头看 <c>Extract(testMode=1)</c> 的结果码。
/// 每次 <c>Open</c> 之后必 <c>Close</c>：遗留状态会被下一个候选读到。
/// </summary>
internal sealed class SevenZipArchive : IDisposable
{
    private static readonly Regex VolumePattern = new(@"^(?<stem>.+)\.(?<num>\d{3,})$", RegexOptions.Compiled);

    /// <summary>
    /// <c>Extract</c> 回调一条结果都收不到，连续几次就认定这个包判不了。
    /// 取 2 而不是 1：handler 首次解压前的惰性初始化不该被当成结论。
    /// </summary>
    private const int MaxEmptyExtracts = 2;

    private readonly IInArchive _archive;
    private readonly NativeStreamServer _stream = new();
    private readonly OpenCallbackServer _openCallback = new();
    private readonly ExtractCallbackServer _extractCallback = new();
    private readonly SevenZipDll.PropBuffer _prop = new();
    private readonly IntPtr _indices = Marshal.AllocHGlobal(sizeof(uint) * 16);
    private readonly string[] _volumes;

    private int? _pick;
    private bool _pickDone;
    private bool _noVerdict;
    private bool _opened;
    private bool _disposed;
    private int _emptyExtracts;

    private SevenZipArchive(IInArchive archive, string[] volumes)
    {
        _archive = archive;
        _volumes = volumes;
    }

    /// <summary>造一个会话级档案对象。卷组由调用方一次算好：名字解析属输入类，不进这里。<c>null</c> 表示这个 dll 给不出对象。</summary>
    internal static SevenZipArchive? Create(SevenZipHost host, string[] volumes)
    {
        var raw = host.CreateArchive();
        if (raw == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return new SevenZipArchive((IInArchive)Marshal.GetObjectForIUnknown(raw), volumes);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    /// <summary>这个包扫描最小加密条目的次数。会话把它转给断言读。</summary>
    internal int MetadataScans { get; private set; }

    /// <summary>快路已经认定这个包给不出结论。会话把它转给断言读。</summary>
    internal bool NoVerdict => _noVerdict;

    /// <summary>虚拟流认到的总长度。会话把它转给断言读。</summary>
    internal long StreamTotal => _stream.Total;

    /// <summary><c>true</c> = 这个密码肯定不对。判不出来、打不开、出错一律 <c>false</c>（交给引擎）。</summary>
    internal bool Verdict(string password)
    {
        // 已经证明这个包判不了：连 Open 都不必付，直接交给引擎。
        if (_noVerdict)
        {
            return false;
        }

        _stream.Bind(_volumes);
        _openCallback.Password = password;
        _openCallback.Reset();
        var result = _archive.Open(_stream, IntPtr.Zero, _openCallback);
        _opened = result >= 0;
        try
        {
            if (result == SevenZipDll.SFalse && _openCallback.PasswordCalls >= 1)
            {
                return true;
            }

            if (result < 0 || result == SevenZipDll.SFalse)
            {
                return false;
            }

            return _openCallback.PasswordCalls < 1 && VerdictByExtract(password);
        }
        finally
        {
            Close();
            _stream.Close();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        _prop.Dispose();
        Marshal.FreeHGlobal(_indices);
        try
        {
            Marshal.ReleaseComObject(_archive);
        }
        catch (Exception)
        {
            // RCW 已经不在了：没有别的补救，也不该由它影响结论。
        }
    }

    /// <summary>
    /// 明文头那条路：只解**最小的加密条目**，看 <c>SetOperationResult</c> 的结果码。
    /// <c>Extract</c> 的返回值对错都一样是 <c>S_OK</c>，所以判据不在返回值里。
    /// </summary>
    private bool VerdictByExtract(string password)
    {
        if (_noVerdict || PickItem() is not { } index)
        {
            return false;
        }

        _extractCallback.Reset();
        _extractCallback.Password = password;
        Marshal.WriteInt32(_indices, index);
        _archive.Extract(_indices, 1, 1, _extractCallback);
        if (_extractCallback.Results.Count == 0)
        {
            // 一条结果都没收到（实测 -mhe=off 配 zstd / brotli / lz4 / lz5 / lizard 会这样）：
            // 这条路对这个包连「判错」都做不到，而结果条数由编码决定、与密码无关，
            // 所以连续两次相同就是包级结论，整条关断。
            if (++_emptyExtracts >= MaxEmptyExtracts)
            {
                _noVerdict = true;
            }

            return false;
        }

        _emptyExtracts = 0;
        var last = _extractCallback.Results[^1];
        if (last is SevenZipDll.OpDataError or SevenZipDll.OpCrcError)
        {
            return true;
        }

        // 既不是「密码错」也不是 OK：这个包的这条路给不出结论，后续候选不必再付这笔开销。
        if (last != SevenZipDll.OpOk)
        {
            _noVerdict = true;
        }

        return false;
    }

    /// <summary>
    /// 最小的加密条目下标。它跟密码无关，一个包只算一次。
    /// 必须先问 <c>GetNumberOfItems</c>：没有条目时 <c>GetProperty(0)</c> 会越界。
    /// </summary>
    private int? PickItem()
    {
        if (_pickDone)
        {
            return _pick;
        }

        _pickDone = true;
        MetadataScans++;
        if (_archive.GetNumberOfItems(out var count) < 0)
        {
            return null;
        }

        var limit = Math.Min(count, (uint)SevenZipDll.MaxItemScan);
        var best = long.MaxValue;
        for (uint i = 0; i < limit; i++)
        {
            _prop.Clear();
            if (_archive.GetProperty(i, SevenZipDll.KpidEncrypted, _prop.Pointer) < 0
                || !SevenZipDll.PropBool(_prop.Pointer))
            {
                continue;
            }

            _prop.Clear();
            if (_archive.GetProperty(i, SevenZipDll.KpidSize, _prop.Pointer) < 0)
            {
                continue;
            }

            if (SevenZipDll.PropInt(_prop.Pointer) is not { } size || size >= best)
            {
                continue;
            }

            best = size;
            _pick = (int)i;
        }

        return _pick;
    }

    private void Close()
    {
        if (!_opened)
        {
            return;
        }

        _opened = false;
        try
        {
            _archive.Close();
        }
        catch (Exception)
        {
            // 关不上不算判据：这个对象马上就不再用了。
        }
    }

    /// <summary>
    /// 给定一卷，把同组的卷按序凑齐（不是分卷就回它自己）。命名是「基名 + 零填充数字」；
    /// 被传入的不是第一卷时，先往下找到最小那个卷号。中间缺一卷就停在缺口 ——
    /// handler 因此打不开，正好回退给引擎（不猜、不合并）。
    /// </summary>
    internal static string[] Volumes(string archive)
    {
        var match = VolumePattern.Match(Path.GetFileName(archive));
        if (!match.Success)
        {
            return [archive];
        }

        var directory = Path.GetDirectoryName(archive) ?? string.Empty;
        var stem = Path.Combine(directory, match.Groups["stem"].Value);
        var width = match.Groups["num"].Value.Length;
        if (!int.TryParse(match.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            // 数字段超出 int 上限（时间戳那种名字）按「不是分卷号」处理：不抛，也不消耗 dll 那一段的额度。
            FastPathFailureGuard.NoteInputFailure();
            return [archive];
        }
        while (number > 1 && File.Exists(Part(stem, number - 1, width)))
        {
            number--;
        }

        var parts = new List<string>();
        while (File.Exists(Part(stem, number, width)))
        {
            parts.Add(Part(stem, number, width));
            number++;
        }

        return parts.Count > 0 ? [.. parts] : [archive];
    }

    private static string Part(string stem, int number, int width) =>
        stem + "." + number.ToString("D" + width, CultureInfo.InvariantCulture);
}

/// <summary>
/// 7z 的快路会话。一个包一个：常驻 <c>IInArchive</c>、按包算一次「最小加密条目」、
/// 连续异常计数、以及那条路的串行约定。
/// </summary>
internal sealed class SevenZipFastPathSession : IFastPathSession
{
    /// <summary>
    /// dll 那条路实测没有并行收益：**每线程各持一个 <c>IInArchive</c> 也一样串行**
    /// （16 路平均核数 1.12，而 zip 那条 7.48）。锁是进程级的，所以跨包也串行。
    /// </summary>
    private static readonly object Gate = new();

    private readonly SevenZipArchive? _archive;

    private SevenZipFastPathSession(SevenZipArchive archive) => _archive = archive;

    /// <summary>
    /// 开庭：加载 dll、造一个常驻档案对象。资源类失败（找不到 dll、导出缺失、CreateObject 失败）
    /// 都回 <c>null</c> 且不计数，所以那几种失败不消耗失败额度。
    /// </summary>
    internal static SevenZipFastPathSession? Open(
        string archive, string? dllPath = null, IRunLog? log = null)
    {
        if (FastPathFailureGuard.Disabled)
        {
            return null;
        }

        try
        {
            var host = SevenZipHost.Acquire(dllPath, log);
            var created = host is null ? null : SevenZipArchive.Create(host, SevenZipArchive.Volumes(archive));
            return created is null ? null : new SevenZipFastPathSession(created);
        }
        catch (Exception)
        {
            FastPathFailureGuard.NoteFailure();
            return null;
        }
    }

    /// <summary>扫描次数，转发自档案对象。</summary>
    internal int MetadataScans => _archive?.MetadataScans ?? 0;

    /// <summary>这个包是否已被认定判不了，转发自档案对象。</summary>
    internal bool NoVerdict => _archive?.NoVerdict ?? false;

    /// <summary>虚拟流认到的总长度，转发自档案对象。</summary>
    internal long StreamTotal => _archive?.StreamTotal ?? 0;

    public FastPathVerdict Judge(string password)
    {
        if (string.IsNullOrEmpty(password) || FastPathFailureGuard.Disabled || _archive is null)
        {
            return FastPathVerdict.Unknown;
        }

        lock (Gate)
        {
            try
            {
                var wrong = _archive.Verdict(password);
                FastPathFailureGuard.Succeeded();
                return wrong ? FastPathVerdict.ProbablyWrong : FastPathVerdict.Unknown;
            }
            catch (Exception)
            {
                FastPathFailureGuard.NoteFailure();
                return FastPathVerdict.Unknown;
            }
        }
    }

    public void Dispose() => _archive?.Dispose();
}
