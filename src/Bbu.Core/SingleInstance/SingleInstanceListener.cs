using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Texts;
using Microsoft.Win32.SafeHandles;

namespace Bbu.Core.SingleInstance;

/// <summary>
/// 主实例这一侧的管道监听。收下一批路径就回调，回调里落到清单（攒批与去重见 1.1.0 的转发规程）。
/// 一次连接一条消息。只收**本份程序**发来的消息：连上之后先认对端（内核给的进程号到映像路径），
/// 认不出来就不读也不回，记一条日志后关掉这条连接 —— 拒收的是载荷，不是连接本身。
/// 读有字节上限；载荷进收货台之后回一句话，转发方等它才算递达。
/// 建管道失败退避重试，对端断开不退避。
/// </summary>
public sealed class SingleInstanceListener : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SingleInstanceBackoff _backoff = new();
    private Task? _loop;

    /// <summary>收到转发时触发。参数是解析出来的载荷。</summary>
    public event Action<LaunchPayload>? PayloadReceived;

    /// <summary>退避与拒收的原因（进日志文件）。没人订阅 Note 时不记录。</summary>
    public Action<string>? Note { get; set; }

    /// <summary>监听是否还在跑。管道建不起来时为 <c>false</c>。</summary>
    public bool IsListening => _loop is { IsCompleted: false };

    /// <summary>监听退出的原因；正常监听时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过
        }

        _cts.Dispose();
    }

    /// <summary>
    /// 有界读：读到**一条消息的结尾**（一个换行）或者对端关掉为止；到上限还没读完的那条连接不算一次载荷。
    /// 上限在**读**这一处生效：读完再判长度，字节早就进了内存。超限那一档会多读一个字节 —— 那是「还没读完」的信号。
    /// </summary>
    public static async Task<string?> ReadBoundedAsync(Stream source, int maxBytes, CancellationToken ct)
    {
        using var received = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var room = maxBytes + 1 - (int)received.Length;
            if (room <= 0)
            {
                return null;
            }

            var read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, room)), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            received.Write(chunk, 0, read);
            if (received.Length > maxBytes)
            {
                return null;
            }

            var at = received.GetBuffer().AsSpan(0, (int)received.Length).IndexOf((byte)'\n');
            if (at >= 0)
            {
                return Encoding.UTF8.GetString(received.GetBuffer(), 0, at);
            }
        }

        return Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 名字被占住：另一个会话的实例、或别的进程占了同名的管道。退避之后重试。
                LastError = ex.Message;
                var wait = _backoff.OnPipeCreateFailed();
                Note?.Invoke(string.Format(
                    CultureInfo.InvariantCulture, Format.SingleInstanceRetryAt, wait.TotalSeconds, ex.Message));
                await DelayAsync(wait, ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
            {
                // 这条路上重试没有意义。记下原因后退出，主实例照常开窗，只是收不到转发。
                LastError = ex.Message;
                return;
            }

            try
            {
                await using (server)
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    if (!IsOwnProgram(server.SafePipeHandle))
                    {
                        Note?.Invoke(LogText.SingleInstanceForeignSender);
                        continue;
                    }

                    var json = await ReadBoundedAsync(server, SingleInstanceProtocol.MaxPayloadBytes, ct).ConfigureAwait(false);
                    if (json is null || !SingleInstanceProtocol.TryDeserialize(json, out var payload))
                    {
                        continue;
                    }

                    PayloadReceived?.Invoke(payload);

                    // 回执在载荷交给收货台之后才发：对方等到它才算递达（见 ForwardResult.Delivered）。
                    var receipt = Encoding.UTF8.GetBytes(SingleInstanceProtocol.Receipt);
                    await server.WriteAsync(receipt, ct).ConfigureAwait(false);
                    await server.FlushAsync(ct).ConfigureAwait(false);
                    _backoff.OnDelivered();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // 对端断开或半途写坏：继续等下一个连接，不退避。
                await DelayAsync(_backoff.OnPeerDisconnected(), ct).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateServer() => new(
        SingleInstanceProtocol.PipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static bool IsOwnProgram(SafePipeHandle pipe) =>
        SingleInstancePeer.IsOwnProgram(SingleInstancePeer.ClientProcessId(pipe));

    private static async Task DelayAsync(TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退避期间被叫停：交给循环条件收尾
        }
    }
}

/// <summary>
/// 建管道失败之后的退避：首档 <see cref="SingleInstanceProtocol.RetryFirstMs"/> 毫秒，逐次翻倍，封顶
/// <see cref="SingleInstanceProtocol.RetryCapMs"/> 毫秒，**不设次数上限** —— 名字被占住是暂时的，退避要能自己恢复。
/// 只有建管道失败计数，成功投递一次就清零；对端断开不算失败。
/// </summary>
public sealed class SingleInstanceBackoff
{
    /// <summary>连着建管道失败的次数。</summary>
    public int Failures { get; private set; }

    /// <summary>这一次该等多久。每次调用记一次失败。</summary>
    public TimeSpan OnPipeCreateFailed()
    {
        var wait = DelayFor(Failures);
        Failures++;
        return wait;
    }

    /// <summary>载荷递到了：重新从头算。</summary>
    public void OnDelivered() => Failures = 0;

    /// <summary>对端断开：不算失败，也不退避。</summary>
    public TimeSpan OnPeerDisconnected() => TimeSpan.Zero;

    /// <summary>第 n 次连着失败该等多久（纯函数，从 0 起算）。</summary>
    public static TimeSpan DelayFor(int consecutiveFailures)
    {
        var shift = Math.Clamp(consecutiveFailures, 0, 16);
        var ms = Math.Min((long)SingleInstanceProtocol.RetryCapMs, (long)SingleInstanceProtocol.RetryFirstMs << shift);
        return TimeSpan.FromMilliseconds(ms);
    }
}
