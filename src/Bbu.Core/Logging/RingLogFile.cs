using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bbu.Core.Texts;
using IOPath = System.IO.Path;

namespace Bbu.Core.Logging;

/// <summary>
/// 环形日志文件：长度不超过 <see cref="Capacity"/>，写满一圈回头覆盖，不轮转、不改名。
/// 写指针取时间戳最大那一行的行尾 —— 时间戳定宽全 ASCII，字节字典序即时刻先后；
/// 这条等价只在同一时区内成立，跨时区或改钟之后不加固。
/// </summary>
public sealed class RingLogFile
{
    /// <summary>文件长度的上限，也是环的容量。</summary>
    public const long Capacity = 4L * 1024 * 1024;

    /// <summary>单条上限：按上限截断在 <c>RunLog</c> 侧做，本类只兜底超过容量的那一条。</summary>
    public const int MaxLineBytes = 128 * 1024;

    /// <summary>时间戳宽度，含方括号。</summary>
    public const int StampWidth = 31;

    private const byte NewLine = (byte)'\n';

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] NewLineOnly = [NewLine];

    private readonly object _gate = new();
    private readonly long _capacity;
    private FileStream? _stream;
    private long _pos;

    /// <summary>打开或创建 path、按文件实际长度定出写指针；打不开只置 <see cref="LastError"/>。</summary>
    public RingLogFile(string path)
        : this(path, Capacity)
    {
    }

    /// <summary>环长可换的构造，只给测试用来免写满 4 MiB。</summary>
    internal RingLogFile(string path, long capacity)
    {
        Path = path;
        _capacity = capacity;
        lock (_gate)
        {
            try
            {
                Open();
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                LastError = ex.Message;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    public string Path { get; }

    /// <summary>上一次写失败的原因；正常为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>写一行，末尾补换行；失败不抛，原因留在 <see cref="LastError"/>。</summary>
    public void WriteLine(string line)
    {
        lock (_gate)
        {
            var stream = _stream;
            if (stream is null)
            {
                // 构造期就失败过的那一次原因要留住，它才是要报给用户的
                LastError ??= LogText.LogFileClosed;
                return;
            }

            try
            {
                var payload = Encode(line);
                var start = _pos;
                if (start + payload.Length > _capacity)
                {
                    // 回绕前补一个换行对齐行边界，新写入因此不产生半截行
                    if (start < _capacity)
                    {
                        WriteAt(stream, start, NewLineOnly);
                    }

                    start = 0;
                }

                WriteAt(stream, start, payload);
                _pos = start + payload.Length;
                LastError = null;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // 写指针只在整条写成功后前进：没写全的那一条下次被整条盖掉
                LastError = ex.Message;
            }
        }
    }

    /// <summary>按时间序读回全部完整行：从写指针起绕一圈；读不出来返回空串。</summary>
    public string ReadAll()
    {
        lock (_gate)
        {
            var stream = _stream;
            if (stream is null)
            {
                return string.Empty;
            }

            try
            {
                var data = ReadBytes(stream, _capacity);
                return OrderLines(data, CompleteLines(data), _pos);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // 读失败不改 LastError：那是写路的信号，界面提示条挂在它上面
                return string.Empty;
            }
        }
    }

    /// <summary>读回整个文件给测试与排障用：共享读取，避开常开写句柄；读不出来返回空串。</summary>
    public static string ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = ReadBytes(stream, Capacity);
            var lines = CompleteLines(data);
            return OrderLines(data, lines, WritePointer(data, lines, Capacity));
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return string.Empty;
        }
    }

    /// <summary>关掉句柄；之后 <see cref="WriteLine"/> 只置 <see cref="LastError"/>，不抛。</summary>
    public void Close()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Open()
    {
        var directory = IOPath.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var data = ReadBytes(_stream, _capacity);
        _pos = WritePointer(data, CompleteLines(data), _capacity);
    }

    /// <summary>按实际长度整份读回；比容量长时只认前一段，本类不截断别人的数据。</summary>
    private static byte[] ReadBytes(FileStream stream, long capacity)
    {
        var length = (int)Math.Min(stream.Length, capacity);
        var data = new byte[length];
        stream.Position = 0;
        stream.ReadExactly(data, 0, length);
        return data;
    }

    /// <summary>带时间戳且以换行收尾的段才算一行；没有换行的尾部残片与不带时间戳的段都不进结果。</summary>
    private static List<(int Start, int Length)> CompleteLines(byte[] data)
    {
        var lines = new List<(int Start, int Length)>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != NewLine)
            {
                continue;
            }

            var length = i - start;
            if (HasStamp(data.AsSpan(start, length)))
            {
                lines.Add((start, length));
            }

            start = i + 1;
        }

        return lines;
    }

    /// <summary>最大时间戳那一行的行尾就是写指针；同一毫秒并列时取文件序靠后的那一条。</summary>
    private static long WritePointer(byte[] data, List<(int Start, int Length)> lines, long capacity)
    {
        var best = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (best < 0 || data.AsSpan(lines[i].Start, StampWidth)
                    .SequenceCompareTo(data.AsSpan(lines[best].Start, StampWidth)) >= 0)
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return 0;
        }

        var end = lines[best].Start + lines[best].Length + 1;
        return end >= capacity ? 0 : end;
    }

    /// <summary>完整行按时间序拼成文本：从写指针起绕一圈，与其他两处读回共用这一份口径。</summary>
    private static string OrderLines(byte[] data, List<(int Start, int Length)> lines, long pos)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Start >= pos)
            {
                Append(text, data, line);
            }
        }

        foreach (var line in lines)
        {
            if (line.Start < pos)
            {
                Append(text, data, line);
            }
        }

        return text.ToString();
    }

    private static bool HasStamp(ReadOnlySpan<byte> line) =>
        line.Length >= StampWidth && line[0] == (byte)'[' && line[5] == (byte)'-' && line[8] == (byte)'-';

    private void WriteAt(FileStream stream, long offset, ReadOnlySpan<byte> bytes)
    {
        var first = (int)Math.Min(bytes.Length, _capacity - offset);
        stream.Position = offset;
        stream.Write(bytes[..first]);
        if (first == bytes.Length)
        {
            return;
        }

        stream.Position = 0;
        stream.Write(bytes[first..]);
    }

    private byte[] Encode(string line)
    {
        var payload = new byte[Utf8.GetByteCount(line) + 1];
        Utf8.GetBytes(line.AsSpan(), payload);
        payload[^1] = NewLine;
        return payload.Length > _capacity ? Clamp(payload) : payload;
    }

    /// <summary>超过容量那一条的兜底：顶到容量、末位留给换行，切点退回字符边界。</summary>
    private byte[] Clamp(byte[] payload)
    {
        var keep = (int)_capacity - 1;
        while (keep > 0 && (payload[keep] & 0b1100_0000) == 0b1000_0000)
        {
            keep--;
        }

        var clamped = new byte[(int)_capacity];
        payload.AsSpan(0, keep).CopyTo(clamped);
        clamped[keep] = NewLine;
        return clamped;
    }

    private static void Append(StringBuilder text, byte[] data, (int Start, int Length) line) =>
        text.Append(Utf8.GetString(data, line.Start, line.Length)).Append('\n');

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
