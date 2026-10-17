using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace Bbu.Core.SingleInstance;

/// <summary>转发结果。后两档的处置相同（自己开窗），分开是为了让日志文件与 probe 说得出是哪一支。</summary>
public enum ForwardResult
{
    /// <summary>载荷已经落在**本份程序**的主实例手里，并且拿到了它的回执。</summary>
    Delivered,

    /// <summary>对端不在（连不上）。只有这一档值得重试。</summary>
    NoServer,

    /// <summary>连上了，但对端不是本份程序：一个字节的载荷都不发。不重试，由调用方自己开窗。</summary>
    NotOurs,

    /// <summary>对端是本份程序，但收下之后没等到回执。不重试，由调用方自己开窗。</summary>
    Unconfirmed,
}

/// <summary>
/// 把路径递给已经在跑的那个实例。三步：认对端（不是本份程序就一个字节都不发）、写载荷、等回执。
/// 写超时也不当递达：写完不等于对方收下了，宁可自己再开一个窗口，也不让用户拖进去的文件没有反应。
/// 三步各用 <see cref="SingleInstanceProtocol.ForwardTimeoutMs"/>。
/// </summary>
public static class SingleInstanceForwarder
{
    public static ForwardResult Forward(
        LaunchPayload payload,
        int timeoutMs = SingleInstanceProtocol.ForwardTimeoutMs) =>
        Forward(payload, timeoutMs, out _);

    /// <summary>另把「为什么没递达」带出来：连不上那一档给的是捕获到的异常原文，调用方拿它写失败那一行。</summary>
    public static ForwardResult Forward(
        LaunchPayload payload,
        int timeoutMs,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            using var client = new NamedPipeClientStream(
                ".", SingleInstanceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                client.Connect(timeoutMs);
            }
            catch (TimeoutException ex)
            {
                reason = ex.Message;
                return ForwardResult.NoServer;
            }

            if (!SingleInstancePeer.IsOwnProgram(SingleInstancePeer.ServerProcessId(client.SafePipeHandle)))
            {
                return ForwardResult.NotOurs;
            }

            var bytes = Encoding.UTF8.GetBytes(
                SingleInstanceProtocol.Serialize(payload) + SingleInstanceProtocol.MessageTerminator);
            var write = Task.Run(() =>
            {
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
            });

            write.Wait(timeoutMs);

            var receipt = Task.Run(() => ReadReceipt(client));
            return receipt.Wait(timeoutMs) && receipt.Result
                ? ForwardResult.Delivered
                : ForwardResult.Unconfirmed;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException
                                       or TimeoutException)
        {
            reason = ex.Message;
            return ForwardResult.NoServer;
        }
    }

    /// <summary>等主实例回执。回不来（对端已死、超时）由调用方按未递达处理。</summary>
    private static bool ReadReceipt(NamedPipeClientStream client)
    {
        var wanted = Encoding.UTF8.GetByteCount(SingleInstanceProtocol.Receipt);
        var buffer = new byte[wanted];
        var got = 0;

        while (got < wanted)
        {
            var read = client.Read(buffer, got, wanted - got);
            if (read <= 0)
            {
                return false;
            }

            got += read;
        }

        return string.Equals(
            Encoding.UTF8.GetString(buffer), SingleInstanceProtocol.Receipt, StringComparison.Ordinal);
    }
}
