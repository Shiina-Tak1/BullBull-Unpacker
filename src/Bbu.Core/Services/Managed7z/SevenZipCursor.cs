using System;
using System.Buffers.Binary;
using Bbu.Core.Texts;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 7z 头的字节游标。<c>ReadNumber</c> 照 <c>7zIn.cpp:178-229 ReadNumberSpec</c>：
/// 首字节 &lt; 0x80 就是它自己；否则首字节里从 bit6 往下第一个 0 位决定还有几个字节。
/// </summary>
internal ref struct SevenZipCursor
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public SevenZipCursor(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public readonly int Position => _position;

    public readonly int Remaining => _buffer.Length - _position;

    public byte ReadByte()
    {
        if (_position >= _buffer.Length)
        {
            throw new SevenZipFormatException(ManagedFastPathText.CursorAtEnd);
        }

        return _buffer[_position++];
    }

    public ReadOnlySpan<byte> ReadSpan(int length)
    {
        if (length < 0 || length > Remaining)
        {
            throw new SevenZipFormatException(ManagedFastPathText.CursorNeedMore);
        }

        var span = _buffer.Slice(_position, length);
        _position += length;
        return span;
    }

    public uint ReadUInt32()
    {
        var span = ReadSpan(4);
        return BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    public void Skip(int length)
    {
        if (length < 0 || length > Remaining)
        {
            throw new SevenZipFormatException(ManagedFastPathText.CursorSkipTooFar);
        }

        _position += length;
    }

    public ulong ReadNumber()
    {
        var first = ReadByte();
        if ((first & 0x80) == 0)
        {
            return first;
        }

        ulong value = ReadByte();
        for (var i = 1; i < 8; i++)
        {
            var mask = 0x80u >> i;
            if ((first & mask) == 0)
            {
                value |= (ulong)(first & (mask - 1)) << (i * 8);
                return value;
            }

            value |= (ulong)ReadByte() << (i * 8);
        }

        return value;
    }

    public ulong ReadId() => ReadNumber();

    /// <summary>跳过一个属性：长度由紧接着的 vint 给出。</summary>
    public void SkipData() => Skip(checked((int)ReadNumber()));
}
