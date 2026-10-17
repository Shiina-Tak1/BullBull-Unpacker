using System;
using System.Security.Cryptography;
using Bbu.Core.Texts;

namespace Bbu.Core.Services.Managed7z;

/// <summary>
/// 7z 的 7zAES coder properties：NumCyclesPower + salt + IV，布局照 <c>7zAes.cpp:209-280</c>。
///
/// <code>
/// props[0] = NumCyclesPower | (有 salt ? 0x80 : 0) | (有 iv ? 0x40 : 0)
/// 若 0x80 或 0x40：props[1] = ((saltSize-1) &lt;&lt; 4) | (ivSize-1)，
/// 接着 saltSize 字节 salt、ivSize 字节 iv
/// NumCyclesPower == 0x3F 是特殊情形：不做 KDF，key 由 salt+password 直接拼出
/// </code>
///
/// 这里**只做解密那一侧**：快路只判密码对不对，不写包。
/// </summary>
internal sealed class SevenZipAesKey
{
    private const int KeySize = 32;
    private const int IvSizeMax = 16;
    private const int SaltSizeMax = 16;

    /// <summary>源码允许的 NumCyclesPower 上限（<c>k_NumCyclesPower_Supported_MAX</c>）。</summary>
    private const int NumCyclesPowerMax = 24;

    private const int DirectKeyPower = 0x3F;

    /// <summary>
    /// 每线程一份的 SHA-256 与 AES 实例。候选循环是并发的，所以不能共享；
    /// 但同一线程上反复判不同密码时，省掉每个候选一次对象创建 —— 快路的单价就是这里抠出来的。
    /// </summary>
    [ThreadStatic]
    private static IncrementalHash? _sha;

    [ThreadStatic]
    private static Aes? _aes;

    [ThreadStatic]
    private static byte[]? _kdfBuffer;

    private SevenZipAesKey(int numCyclesPower, byte[] salt, byte[] iv)
    {
        NumCyclesPower = numCyclesPower;
        Salt = salt;
        IV = iv;
    }

    public int NumCyclesPower { get; }

    public byte[] Salt { get; }

    public byte[] IV { get; }

    /// <summary>这个 properties 长什么样，给探针/日志看。</summary>
    public string Describe() =>
        $"7zAES:N{NumCyclesPower} salt={Salt.Length} iv={IV.Length}" +
        (NumCyclesPower == DirectKeyPower ? ManagedFastPathText.DirectKey : string.Empty);

    /// <summary>解 coder properties。失败就是把包判不了的信号，绝不等于「密码错」。</summary>
    public static bool TryParse(ReadOnlySpan<byte> properties, out SevenZipAesKey key)
    {
        key = null!;
        if (properties.Length == 0)
        {
            // 源码：size == 0 视为「全默认」。实际不会出现在加密包里。
            key = new SevenZipAesKey(0, [], new byte[IvSizeMax]);
            return true;
        }

        var b0 = properties[0];
        var numCyclesPower = b0 & 0x3F;

        if ((b0 & 0xC0) == 0)
        {
            if (properties.Length != 1)
            {
                return false;
            }

            key = new SevenZipAesKey(numCyclesPower, [], new byte[IvSizeMax]);
            return true;
        }

        if (properties.Length <= 1)
        {
            return false;
        }

        var b1 = properties[1];
        var saltSize = ((b0 >> 7) & 1) + (b1 >> 4);
        var ivSize = ((b0 >> 6) & 1) + (b1 & 0x0F);

        if (saltSize > SaltSizeMax || ivSize > IvSizeMax || properties.Length != 2 + saltSize + ivSize)
        {
            return false;
        }

        if (numCyclesPower > NumCyclesPowerMax && numCyclesPower != DirectKeyPower)
        {
            // 源码在这里回 E_NOTIMPL：不认识的参数就别判。
            return false;
        }

        var salt = properties.Slice(2, saltSize).ToArray();
        var iv = new byte[IvSizeMax];
        properties.Slice(2 + saltSize, ivSize).CopyTo(iv);
        key = new SevenZipAesKey(numCyclesPower, salt, iv);
        return true;
    }

    /// <summary>这个 coder 的 key。调用方传候选密码的字节（UTF-16 的 7z 直接按 UTF-16LE 字节传入）。</summary>
    public byte[] DeriveKey(ReadOnlySpan<byte> password)
    {
        var key = new byte[KeySize];
        DeriveKey(password, Salt, NumCyclesPower, key);
        return key;
    }

    /// <summary>
    /// 照 <c>7zAes.cpp:39-114 CKeyInfo::CalcKey</c> 的真实构造。
    ///
    /// 关键点：输入是 <c>salt + password + counter</c>，
    /// **不是**「上一轮的摘要」；64 轮打包成一次 <c>Update</c>，counter 每轮重写尾 8 字节。
    /// </summary>
    public static void DeriveKey(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        int numCyclesPower,
        Span<byte> key)
    {
        key.Clear();

        if (numCyclesPower == DirectKeyPower)
        {
            var pos = 0;
            foreach (var b in salt)
            {
                if (pos >= key.Length)
                {
                    return;
                }

                key[pos++] = b;
            }

            foreach (var b in password)
            {
                if (pos >= key.Length)
                {
                    return;
                }

                key[pos++] = b;
            }

            return;
        }

        if (numCyclesPower < 0 || numCyclesPower > NumCyclesPowerMax)
        {
            throw new SevenZipFormatException(ManagedFastPathText.CyclesOutOfRange(numCyclesPower));
        }

        // numUnroll = 1 << min(NumCyclesPower, 6)：最多 64 个槽，一次 Update 全传进去。
        var numUnroll = 1 << Math.Min(numCyclesPower, 6);
        var bufSize = 8 + salt.Length + password.Length;
        var unrollSize = bufSize * numUnroll;

        var scratch = _kdfBuffer;
        if (scratch is null || scratch.Length < unrollSize)
        {
            scratch = new byte[Math.Max(unrollSize, 4096)];
            _kdfBuffer = scratch;
        }

        try
        {
            var buf = scratch.AsSpan(0, unrollSize);
            buf.Clear();
            salt.CopyTo(buf);
            password.CopyTo(buf[salt.Length..]);

            // 第一个槽之后的每个槽都是第一个槽的副本（counter 区先全 0）。
            for (var i = 1; i < numUnroll; i++)
            {
                buf.Slice(0, bufSize).CopyTo(buf[(i * bufSize)..]);
            }

            var numRounds = 1u << numCyclesPower;

            // 每线程一份的 IncrementalHash：省掉每个候选一次 CreateHash。
            // 中途抛异常会让它的状态变脏，所以异常时直接丢弃这个实例。
            var sha = _sha;
            if (sha is null)
            {
                sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _sha = sha;
            }

            try
            {
                var r = 0u;
                var dest = bufSize - 8;
                do
                {
                    var i = r;
                    r += (uint)numUnroll;
                    do
                    {
                        // SetUi32：只写低 4 字节小端；高 4 字节是初始 memset 的 0。
                        buf[dest] = (byte)i;
                        buf[dest + 1] = (byte)(i >> 8);
                        buf[dest + 2] = (byte)(i >> 16);
                        buf[dest + 3] = (byte)(i >> 24);
                        i++;
                        dest += bufSize;
                    }
                    while (i < r);

                    dest = bufSize - 8;
                    sha.AppendData(buf);
                }
                while (r < numRounds);

                Span<byte> digest = stackalloc byte[32];
                sha.GetHashAndReset(digest);
                digest[..Math.Min(32, key.Length)].CopyTo(key);
            }
            catch
            {
                _sha = null;
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scratch.AsSpan(0, unrollSize));
        }
    }

    /// <summary>AES-256-CBC 解密。密文必须是 16 的整数倍。</summary>
    public static bool TryDecrypt(
        byte[] key,
        byte[] iv,
        ReadOnlySpan<byte> cipher,
        Span<byte> plain,
        out int written)
    {
        written = 0;
        if (key.Length != KeySize)
        {
            return false;
        }

        if (cipher.Length == 0 || (cipher.Length & 0xF) != 0)
        {
            return false;
        }

        if (plain.Length < cipher.Length)
        {
            return false;
        }

        try
        {
            // 每线程一份的 Aes：key 每候选都变，transform 必须重建，但实例不用。
            var aes = _aes;
            if (aes is null)
            {
                aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                _aes = aes;
            }

            aes.Key = key;
            written = aes.DecryptCbc(cipher, iv, plain, PaddingMode.None);
            return true;
        }
        catch (CryptographicException)
        {
            _aes = null;
            return false;
        }
    }
}
