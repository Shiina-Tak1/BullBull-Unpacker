using System;
using System.IO;
using System.Threading;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>切一次内嵌包的结果。</summary>
/// <remarks>
/// 不用负数当哨兵：取消与读写失败是两种要给不同文案的结局，
/// 合成一个返回值之后调用方只能靠猜。
/// </remarks>
public sealed record CarveResult(bool Ok, bool Cancelled, long Bytes, string Error)
{
    public static CarveResult Failed(string error) => new(false, false, 0, error);
}

/// <summary>
/// 把「垫了真视频、后面接压缩包」的文件从包的起点切到文件尾，写成临时文件。
/// </summary>
public static partial class Probe
{
    /// <summary>
    /// 从 <paramref name="offset"/> 切到文件尾，写到 <paramref name="destination"/>。
    ///
    /// 多切几个字节是<b>故意</b>的：尾部多几个字节引擎只会警告，而算少长度会把包切坏。
    /// 原文件一个字节都不动；临时文件由调用方用完就删，半成品由这里删（B-2026-153）。
    /// </summary>
    public static CarveResult Carve(
        string source,
        long offset,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var created = false;
        try
        {
            using var input = File.OpenRead(source);
            if (offset < 0 || offset > input.Length)
            {
                return CarveResult.Failed(PierceText.CarveOffsetOutOfRange);
            }

            input.Seek(offset, SeekOrigin.Begin);
            long total;
            // CreateNew：目标已有文件时抛 IOException，正好是「不许覆盖」（B-2026-204）。
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                created = true;
                var buffer = new byte[1 << 20];
                total = 0;
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // 顺序不能反：句柄未关闭时删除会撞共享冲突，残片因此留在父目录。
                        output.Dispose();
                        return Abandon(new CarveResult(false, true, total, string.Empty), destination);
                    }

                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    total += read;
                }

                // 不强制落盘：切出来的是中间物，调用方用完就删（见 PierceExtract 的 Discard），
                // 断电丢掉只是这一层重跑一次；而 FlushFileBuffers 在 GB 级切片上要付约 0.3 秒/GiB。
                // 密码本那种写盘不属于这一类。
                output.Flush(false);
            }

            return new CarveResult(true, false, total, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 没建出文件时不许删目标：撞名那条 IOException 也走到这里，删掉用户那个文件比截断更糟。
            return created
                ? Abandon(CarveResult.Failed(ex.Message), destination)
                : CarveResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 丢掉这一层的半成品，再把结论原样交回去（残片的扩展名像包，留着会被下一次扫描当候选包）。
    /// 原话是「半成品会被删掉，不留垃圾」（B-2026-153）。删不掉不改结论也不抛。
    /// </summary>
    private static CarveResult Abandon(CarveResult result, string destination)
    {
        try
        {
            File.Delete(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 结论本身已经是失败，这里再抛一层只会换一个更难读的报错
        }

        return result;
    }
}
