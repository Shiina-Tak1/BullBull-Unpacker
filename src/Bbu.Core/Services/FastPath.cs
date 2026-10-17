using System;
using System.IO;
using System.Threading;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Services.Rar4;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 进程内快路的入口。它只回答一个问题：这个密码是不是**肯定不对**。
/// 返回值只有两种取值，永远不含「正确」，所以活下来的候选照旧走一次真引擎。
///
/// 一个包只开一次庭：包级元数据在 <see cref="Open"/> 里解析一次，判定器各自持有它，
/// 于是 500 个候选不会把中央目录重读 500 遍。7z 那一条要常驻 <c>7z.dll</c>，
/// 它的生命周期也跟这个会话对象走，不靠静态字段。
/// </summary>
public interface IFastPath
{
    /// <summary>
    /// 开庭：解析包级元数据。返回 <c>null</c> 表示这个包判不了，整轮都不进快路 ——
    /// 那比每个候选都调一次、每次回「不知道」更省。
    /// </summary>
    IFastPathSession? Open(string archive);
}

/// <summary>
/// 一个包上的判定会话。候选循环会**并发**调用 <see cref="Judge"/>，所以实现必须线程安全；
/// <see cref="IDisposable.Dispose"/> 幂等且不许抛（候选循环可能中途取消）。
/// </summary>
public interface IFastPathSession : IDisposable
{
    /// <summary><see cref="FastPathVerdict.ProbablyWrong"/> 表示不必起引擎；判不出来一律回「不知道」。</summary>
    FastPathVerdict Judge(string password);
}

/// <summary>按文件头分派。认不出、读不了、抛异常都回 <c>null</c>（整轮不进快路）。</summary>
public sealed class CompositeFastPath : IFastPath
{
    private readonly string? _sevenZipDll;
    private readonly IRunLog? _log;

    /// <summary>
    /// <paramref name="sevenZipDll"/> 为 <c>null</c> 时按默认位置找，见 <see cref="SevenZipDll.Locate"/>；
    /// <paramref name="log"/> 是随包组件核对那两句的出口，装配处接上日志文件。
    /// </summary>
    public CompositeFastPath(string? sevenZipDll = null, IRunLog? log = null)
    {
        _sevenZipDll = sevenZipDll;
        _log = log;
    }

    public IFastPathSession? Open(string archive)
    {
        if (string.IsNullOrEmpty(archive))
        {
            return null;
        }

        try
        {
            // 挡在所有读操作之前：读设备会永久阻塞，取消与超时都没有机会执行（B-2026-001）。
            if (Probe.IsDevicePath(archive))
            {
                return null;
            }

            using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[8];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (read < 4)
            {
                return null;
            }

            if (head[..SevenZipDll.Magic.Length].SequenceEqual(SevenZipDll.Magic))
            {
                // 纯托管优先，接在 dll 前面：判得了就不开庭，也就绕开了那把进程级静态锁
                // 接反了等于白做。
                // 两个 null 语义不同：左边 = 「托管这一段判不了」（继续往下试），
                // 右边 = 「整轮不进快路」（交给调用方的引擎）。
                return (IFastPathSession?)ManagedSevenZipFastPathSession.Open(archive)
                       ?? SevenZipFastPathSession.Open(archive, _sevenZipDll, _log);
            }

            if (head[..4].SequenceEqual(ZipFastPathSession.LocalHeaderSignature))
            {
                return ZipFastPathSession.Open(archive, stream);
            }

            if (head.SequenceEqual(Rar5FastPathSession.Magic))
            {
                return Rar5FastPathSession.Open(stream);
            }

            // RAR4 与 RA签名只在第 7 字节不同（RAR5 还多一个尾字节），两者互不为前缀，两条互斥；
            // 判据与覆盖面（只有 stored 条目）
            if (head[..Rar4FastPathSession.Magic.Length].SequenceEqual(Rar4FastPathSession.Magic))
            {
                return Rar4FastPathSession.Open(stream);
            }

            // 伪装包：头 8 字节是垫片，四种分派（7z、zip、RAR5、RAR4）都不匹配。但**绝对偏移布局的 ZIP**
            // 判得了 —— 它的中央目录与本地头偏移都是文件里的绝对位置，直接拿伪装包去读就对
            // （7-Zip 也是这么直读的，所以穿透层那一档连包都不切，见 PierceExtract 的 Direct 短路）。
            // 这是毫秒级的尾部检查，不是全盘扫；深扫留给穿透层，不在开庭时付。
            if (Probe.HasZipAbsoluteLayout(archive))
            {
                return ZipFastPathSession.Open(archive, stream);
            }

            return null;
        }
        catch (Exception)
        {
            // 认不出、读不了、解析失败一律不进快路：多起几次进程的代价远小于误杀一个正确密码。
            return null;
        }
    }
}

/// <summary>
/// dll 那一段的失败计数。计数是**进程级**的：它记的是这个 dll 在这个进程里的状态，
/// 与当前是哪个包无关。「连续」按字面实现 —— 成功一次就清零。
///
/// **资源类失败**（dll 不在、加载不了、导出缺失、CreateObject 失败）根本不进这里，
/// 所以把 dll 放回来之后下一次开庭照旧能用；只有 dll 内部反复出异常才关掉这一段。
/// </summary>
internal static class FastPathFailureGuard
{
    internal const int MaxFailures = 3;

    private static int _failures;
    private static int _inputFailures;
    private static bool _disabled;

    internal static bool Disabled => Volatile.Read(ref _disabled);

    /// <summary>输入类失败的条数，只供断言与排查读：这一类的失败不消耗 dll 那一段的额度。</summary>
    internal static int InputFailures => Volatile.Read(ref _inputFailures);

    internal static void NoteFailure()
    {
        if (Interlocked.Increment(ref _failures) >= MaxFailures)
        {
            Volatile.Write(ref _disabled, true);
        }
    }

    /// <summary>输入类失败（归档名读不出卷号）：与这份 dll 无关，单独计数，不消耗它那一段的额度。</summary>
    internal static void NoteInputFailure() => Interlocked.Increment(ref _inputFailures);

    internal static void Succeeded() => Interlocked.Exchange(ref _failures, 0);

    /// <summary>复位计数与开关。生产路径不调用它，只给「关掉之后复位、这条路照旧能工作」那条断言用。</summary>
    internal static void Reset()
    {
        Interlocked.Exchange(ref _failures, 0);
        Interlocked.Exchange(ref _inputFailures, 0);
        Volatile.Write(ref _disabled, false);
    }
}
