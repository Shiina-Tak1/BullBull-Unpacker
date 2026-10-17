using System;
using System.IO;
using System.Threading;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 密码本的写者锁。锁文件是 <c>&lt;密码本&gt;.lock</c>，零字节，不含任何密码数据。
///
/// 为什么不用单实例互斥体：那个互斥体每登录会话一个，两个账号同时在线时各成主实例，
/// 会同时写同一本密码（B-2026-056）。锁文件覆盖跨会话、跨用户、跨机器共享盘三种情形。
///
/// 拿不到锁不再无声往下写：三方合并兜不住「这中间别人写了什么」，到点后把结果与原因交回调用方去拒绝写盘（B-2026-206）。
/// </summary>
internal sealed class BookLock : IDisposable
{
    private readonly FileStream? _stream;

    private BookLock(FileStream? stream, string failure)
    {
        _stream = stream;
        Failure = failure;
    }

    /// <summary>是否拿到了锁；拿不到时 <see cref="Failure"/> 是原因，拿到时是空串。</summary>
    internal bool Held => _stream is not null;

    internal string Failure { get; }

    internal static BookLock Acquire(string bookPath, double waitSeconds)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                bookPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new BookLock(stream, string.Empty);
        }
        catch (IOException)
        {
            // 拿不到就退让重试，直到超时。
        }
        catch (UnauthorizedAccessException)
        {
            return new BookLock(null, VaultText.LockUnusable);
        }

        stream?.Dispose();
        var deadline = Environment.TickCount64 + (long)(waitSeconds * 1000);
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
            try
            {
                return new BookLock(new FileStream(
                    bookPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), string.Empty);
            }
            catch (IOException)
            {
                // 还在别人手里，继续等。
            }
            catch (UnauthorizedAccessException)
            {
                return new BookLock(null, VaultText.LockUnusable);
            }
        }

        // 等满了：别人一直占着这本密码本。原因说出来，写不写由调用方定。
        return new BookLock(null, VaultText.LockBusy);
    }

    public void Dispose() => _stream?.Dispose();
}
