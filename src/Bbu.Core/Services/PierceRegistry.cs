using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Bbu.Core.Models;

namespace Bbu.Core.Services;

/// <summary>内容指纹：大小加首尾各 64KB 的哈希。刻意不含路径与时间。</summary>
/// <remarks>
/// 嵌套解压出来的内层包每次落在新路径、修改时间也是新的，按路径做指纹等于形同虚设。
/// 它<b>只是预筛</b>：碰撞之后必须逐字节确认，见 <see cref="PierceRegistry.SameContent"/>。
/// </remarks>
public readonly record struct Fingerprint(long Size, string Hash);

/// <summary>
/// 一条解压链上的两份账：内容指纹登记表、「真的解出来了」的落点。
/// 参照集是<b>链</b>不是批次：被拦的只有「解压链绕回自己」——自己那份又出现在自己的产物里，
/// 跨链的同内容各解一份。同层有多个候选包时每个候选各分叉一条链。
/// 「登记」与「解出来了」是两件事：登记在解压之前（防死循环靠它），落点在解压成功、收壳之后。
/// </summary>
public sealed class PierceRegistry
{
    /// <summary>指纹到已登记的包路径。值是列表而不是单个：</summary>
    /// <remarks>
    /// 只留第一个的话，第二个包解完之后盘上就没有它的记录，同一个包再遇到时
    /// 防死循环的机制反而失效。
    /// </remarks>
    private readonly Dictionary<Fingerprint, List<string>> _visited = [];

    /// <summary>指纹到「真的解出来了」的记录：包路径加产物目录。</summary>
    /// <remarks>与 <see cref="_visited"/> 是两件事，不许合并：那一份在解压之前登记、为防死循环；这一份解压成功后才记，
    /// 只回答「这一份的内容此刻在哪儿」。值是列表：指纹碰撞时同一指纹下会有两份照解，只留一个会定位到错误的对象。</remarks>
    private readonly Dictionary<Fingerprint, List<(string Package, string OutDir)>> _solved = [];

    private readonly RemovedPaths _removed;

    private readonly Lock _gate = new();

    /// <summary>一本空账：一个入口包（或一个清单项）一条链的根。</summary>
    public PierceRegistry()
        : this(new RemovedPaths())
    {
    }

    private PierceRegistry(RemovedPaths removed)
    {
        _removed = removed;
    }

    /// <summary>分叉：同一层有多个候选包时，每个候选各走一条链，各拿一份此刻的账。</summary>
    /// <remarks>登记表与落点两份复制，删过的路径共用 —— 那一份是进程的态，不是链的态。</remarks>
    public PierceRegistry Fork()
    {
        var fork = new PierceRegistry(_removed);
        lock (_gate)
        {
            foreach (var (fingerprint, paths) in _visited)
            {
                fork._visited[fingerprint] = [.. paths];
            }

            foreach (var (fingerprint, solved) in _solved)
            {
                fork._solved[fingerprint] = [.. solved];
            }
        }

        return fork;
    }

    /// <summary>已登记的包路径。报「和哪一个相同」时要说得出对象。</summary>
    public IReadOnlyList<string> Registered(Fingerprint fingerprint)
    {
        lock (_gate)
        {
            return _visited.TryGetValue(fingerprint, out var paths) ? [.. paths] : [];
        }
    }

    /// <summary>把这次要解的包登记进去。</summary>
    public void Register(Fingerprint fingerprint, string path)
    {
        lock (_gate)
        {
            if (!_visited.TryGetValue(fingerprint, out var paths))
            {
                paths = [];
                _visited[fingerprint] = paths;
            }

            paths.Add(path);
        }
    }

    /// <summary>记下「这一份真的解出来了，产物落在 outDir」。解压成功、产物确定之后才调。</summary>
    /// <remarks>引擎报了警告退出码的那一层不记：那一层解压并不完整，拿它当「内容已经在别处了」不成立。</remarks>
    public void NoteSolved(Fingerprint fingerprint, string package, string outDir)
    {
        lock (_gate)
        {
            if (!_solved.TryGetValue(fingerprint, out var solved))
            {
                solved = [];
                _solved[fingerprint] = solved;
            }

            var key = Normalize(package);
            solved.RemoveAll(entry => string.Equals(Normalize(entry.Package), key, StringComparison.Ordinal));
            solved.Add((package, outDir));
        }
    }

    /// <summary>这一份解出来过吗，落在哪个产物目录。只是登记过、或没解成功，都返回空。</summary>
    public string? SolvedAt(Fingerprint fingerprint, string package)
    {
        lock (_gate)
        {
            if (!_solved.TryGetValue(fingerprint, out var solved))
            {
                return null;
            }

            var key = Normalize(package);
            foreach (var (entry, outDir) in solved)
            {
                if (string.Equals(Normalize(entry), key, StringComparison.Ordinal))
                {
                    return outDir;
                }
            }

            return null;
        }
    }

    /// <summary>记下「这一笔是本进程销的」。对账与队列过期判定都只认这个名单。</summary>
    public void NoteRemoved(string path) => _removed.Mark(path);

    /// <summary>这个路径是本进程删掉的吗。判据是它，不是「文件在不在」。</summary>
    public bool WasRemovedByUs(string path) => _removed.Contains(path);

    /// <summary>路径的规范化键：全路径、小写。</summary>
    public static string Normalize(string path) => Path.GetFullPath(path).ToLowerInvariant();
}

/// <summary>本进程删掉过哪些路径。它是进程的态，不是链的态：分叉出来的链账共用同一份。</summary>
/// <remarks>对账与队列过期判定都只认它，不认「文件在不在」。</remarks>
internal sealed class RemovedPaths
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    public void Mark(string path)
    {
        lock (_gate)
        {
            _paths.Add(PierceRegistry.Normalize(path));
        }
    }

    public bool Contains(string path)
    {
        lock (_gate)
        {
            return _paths.Contains(PierceRegistry.Normalize(path));
        }
    }
}

/// <summary>
/// 内容指纹与逐字节确认。
/// </summary>
public static class ContentIdentity
{
    /// <summary>指纹只看首尾各这么多字节。</summary>
    private const int FingerprintChunk = 64 * 1024;

    /// <summary>逐字节确认的分块大小。它只在指纹碰撞时才跑，遇到第一处不同立刻返回。</summary>
    private const int CompareChunk = 1 << 20;

    /// <summary>
    /// 内容指纹预筛。读不了时退回「大小 -1 加规范化路径」，与 1.1.0 的兜底一致。
    ///
    /// 开头先挡保留设备名：读设备会永久阻塞，而这条路径在解压之前就走。
    /// </summary>
    public static Fingerprint Of(string path)
    {
        if (Probe.IsDevicePath(path))
        {
            return new Fingerprint(-1, PierceRegistry.Normalize(path));
        }

        try
        {
            var info = new FileInfo(path);
            var size = info.Length;
            using var stream = File.OpenRead(path);
            var head = new byte[Math.Min(FingerprintChunk, size)];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var hash = System.Security.Cryptography.SHA256.HashData(head.AsSpan(0, read));

            if (size > 2L * FingerprintChunk)
            {
                stream.Seek(-FingerprintChunk, SeekOrigin.End);
                var tail = new byte[FingerprintChunk];
                var tailRead = stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false);
                hash = [.. hash, .. System.Security.Cryptography.SHA256.HashData(tail.AsSpan(0, tailRead))];
            }

            return new Fingerprint(size, Convert.ToHexString(hash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new Fingerprint(-1, PierceRegistry.Normalize(path));
        }
    }

    /// <summary>
    /// 两个文件逐字节相同吗。只在指纹碰撞时才调。
    ///
    /// 不做全文哈希：实测 64MB 全文哈希约 0.116 秒，11GB 分卷每层会浪费约 20 秒。
    /// 逐字节比较遇到第一处不同立刻返回，代价只落在指纹真的碰撞那一刻。
    /// 读不了或半路出错一律判「不确认相同」：宁可多解一份，也不能把读不动当成内容相同。
    /// </summary>
    public static bool SameContent(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var a = File.OpenRead(left);
            using var b = File.OpenRead(right);
            var bufferA = new byte[CompareChunk];
            var bufferB = new byte[CompareChunk];
            while (true)
            {
                var readA = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                var readB = b.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
                if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
