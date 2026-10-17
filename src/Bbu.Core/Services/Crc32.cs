using System;

namespace Bbu.Core.Services;

/// <summary>CRC32（IEEE 多项式，反射输入输出）。老式 ZipCrypto 的密钥流用它，表只建一次。</summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;

    internal static readonly uint[] Table = BuildTable();

    /// <summary>
    /// 按给定初值算一段的 CRC32，终值由调用方决定是否取反。
    /// RAR5 头块的口径是初值与终值都取反（unrar `rawread.cpp` 的 `GetCRC50`），
    /// 与 ZipCrypto 用的这张表同一套多项式。
    /// </summary>
    internal static uint Compute(uint seed, ReadOnlySpan<byte> data)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xff];
        }

        return crc;
    }

    /// <summary>
    /// 标准 CRC32：初值与终值都取反。RAR 的 <c>FILE_CRC</c> / <c>HEAD_CRC</c>（RAR4 快路）与 7z 的
    /// folder CRC、startHeaderCRC、nextHeaderCRC 都是这一个式子。
    /// </summary>
    internal static uint Standard(ReadOnlySpan<byte> data) => Compute(0xffffffff, data) ^ 0xffffffff;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
