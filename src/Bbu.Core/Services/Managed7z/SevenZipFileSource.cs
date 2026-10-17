using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 判据要从包里读字节的那一层。单文件是它的唯一实现；分卷（<c>x.7z.001</c>）要进来时
/// 只需另写一个实现（把同组卷按序拼成一个可定位的流），判据本身一行都不用改。
/// </summary>
internal interface ISevenZipSource : IDisposable
{
    /// <summary>这段数据的总长度。</summary>
    long Length { get; }

    /// <summary>绝对偏移读。<paramref name="offset"/> 越界就是短读或 0，不抛。</summary>
    int ReadAt(long offset, Span<byte> buffer);
}

/// <summary>
/// 单文件源。<c>RandomAccess.Read</c> 走 pread 语义：**无共享游标、无锁**，
/// 所以候选循环天然可以并行 —— 这正是纯托管快路要拿回来的东西。
/// </summary>
internal sealed class SevenZipFileSource : ISevenZipSource
{
    private readonly SafeFileHandle _handle;

    private SevenZipFileSource(SafeFileHandle handle, long length)
    {
        _handle = handle;
        Length = length;
    }

    public long Length { get; }

    public static SevenZipFileSource Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return new SevenZipFileSource(handle, RandomAccess.GetLength(handle));
        }
        catch (Exception)
        {
            handle.Dispose();
            throw;
        }
    }

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        var want = (int)Math.Min(buffer.Length, Length - offset);
        var read = 0;
        while (read < want)
        {
            var got = RandomAccess.Read(_handle, buffer[read..want], offset + read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read;
    }

    public void Dispose() => _handle.Dispose();
}
