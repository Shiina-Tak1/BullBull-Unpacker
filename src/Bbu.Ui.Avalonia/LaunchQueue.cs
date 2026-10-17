using System;
using System.Collections.Generic;
using Bbu.Core.SingleInstance;

namespace Bbu.Ui.Avalonia;

/// <summary>
/// 界面还没建好时收到的启动载荷，先在这里排队。
///
/// 需要它的理由：资源管理器多选时一个文件起一个进程，头几条会在窗口出现之前抵达，
/// 那时候还没有能收下它们的界面。窗口接上之后一次性交出去，界面层因此不必知道管道。
/// 冷启动自带的路径也走这一条，落地口径与转发进来的完全相同。
/// </summary>
public sealed class LaunchQueue
{
    private readonly object _gate = new();
    private readonly List<LaunchPayload> _pending = [];
    private Action<LaunchPayload>? _sink;

    /// <summary>本次进程的收货台。一个进程只有一个。</summary>
    public static LaunchQueue Current { get; } = new();

    /// <summary>收到一份载荷。还没有接受方时先排队。</summary>
    public void Post(LaunchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Action<LaunchPayload>? sink;
        lock (_gate)
        {
            if (_sink is null)
            {
                _pending.Add(payload);
                return;
            }

            sink = _sink;
        }

        sink(payload);
    }

    /// <summary>界面接上收货台。此前排队的载荷按顺序一次交出。</summary>
    public void Attach(Action<LaunchPayload> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        List<LaunchPayload> drained;
        lock (_gate)
        {
            _sink = sink;
            drained = [.. _pending];
            _pending.Clear();
        }

        foreach (var payload in drained)
        {
            sink(payload);
        }
    }

    /// <summary>窗口不再收：接上的那个回调作废，之后的载荷重新排队。</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _sink = null;
        }
    }
}
