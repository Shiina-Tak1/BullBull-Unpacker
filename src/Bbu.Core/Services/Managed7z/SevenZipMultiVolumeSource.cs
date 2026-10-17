using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 多卷源。分卷是一条逻辑流按字节切出来的切片，所以这一层只做「流偏移折算到某一卷」。
/// 卷表由调用方排好序传进来：命名规则与缺口判定都归分派那一条，这里不猜、不补、不按名字找。
/// </summary>
internal sealed class SevenZipMultiVolumeSource : ISevenZipSource
{
    private readonly SafeFileHandle[] _handles;
    private readonly long[] _starts;

    private SevenZipMultiVolumeSource(SafeFileHandle[] handles, long[] starts)
    {
        _handles = handles;
        _starts = starts;
    }

    /// <summary>各卷长度之和，也就是那条逻辑流的长度。</summary>
    public long Length => _starts[^1];

    /// <summary>一次开齐各卷。卷大小是包级常量，累计偏移表算一次就够，候选循环里不再访问磁盘。</summary>
    public static SevenZipMultiVolumeSource Open(IReadOnlyList<string> volumes)
    {
        var handles = new SafeFileHandle[volumes.Count];
        var starts = new long[volumes.Count + 1];
        var opened = 0;
        try
        {
            for (; opened < volumes.Count; opened++)
            {
                handles[opened] = File.OpenHandle(
                    volumes[opened], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                starts[opened + 1] = starts[opened] + RandomAccess.GetLength(handles[opened]);
            }
        }
        catch (Exception)
        {
            // 未完全打开的句柄要当场放掉：漏出去等于这一组卷一直被占着，改名与删除都会失败。
            for (var i = 0; i < opened; i++)
            {
                handles[i].Dispose();
            }

            throw;
        }

        return new SevenZipMultiVolumeSource(handles, starts);
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
            var at = offset + read;
            var index = Locate(at);
            if (index < 0)
            {
                break;
            }

            // 一段只保证读满当前卷：到卷尾就短读，下一轮从下一卷接着读，缺口因此如实变短。
            var got = RandomAccess.Read(_handles[index], buffer[read..want], at - _starts[index]);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read;
    }

    /// <summary>这个流偏移落在哪一卷。0 字节的卷不含任何偏移，因此跳过。</summary>
    private int Locate(long offset)
    {
        for (var i = 0; i < _handles.Length; i++)
        {
            if (offset < _starts[i + 1])
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }
    }
}
