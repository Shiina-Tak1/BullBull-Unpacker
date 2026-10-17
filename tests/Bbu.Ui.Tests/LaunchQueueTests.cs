using System;
using System.Collections.Generic;
using Bbu.Core.SingleInstance;
using Bbu.Ui.Avalonia;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 界面还没建好时收到的启动载荷。
///
/// 载荷在窗口出现之前抵达时不许丢：丢一条就是用户点了五个文件只进去四个。
/// 这里用实例而不是 <c>Current</c>：那个是进程级的，测它会把别的用例接上的收货台拆掉。
/// </summary>
public sealed class LaunchQueueTests
{
    /// <summary>窗口还没出现时先排队，接上之后按来的顺序一次交出。</summary>
    [Fact]
    public void Post_BeforeTheWindow_MustWaitAndThenArriveInOrder()
    {
        var queue = new LaunchQueue();
        var seen = new List<string>();

        queue.Post(new LaunchPayload([@"C:\a.zip"], Auto: false));
        queue.Post(new LaunchPayload([@"C:\b.zip"], Auto: true));
        Assert.Empty(seen);

        queue.Attach(payload => seen.Add(payload.Paths[0]));

        Assert.Equal(new[] { @"C:\a.zip", @"C:\b.zip" }, seen);
    }

    /// <summary>窗口接上之后不必等，收到即送。</summary>
    [Fact]
    public void Post_WithAWindow_MustArriveWithoutWaiting()
    {
        var queue = new LaunchQueue();
        var seen = new List<LaunchPayload>();
        queue.Attach(seen.Add);

        queue.Post(new LaunchPayload([@"C:\d.zip"], Auto: true));

        Assert.Single(seen);
        Assert.True(seen[0].Auto);
    }

    /// <summary>窗口关掉之后又来的载荷重新排队，不会投进一个已经没人收的地方。</summary>
    [Fact]
    public void Post_AfterTheWindowIsGone_MustQueueAgain()
    {
        var queue = new LaunchQueue();
        var seen = new List<string>();
        queue.Attach(payload => seen.Add(payload.Paths[0]));
        queue.Detach();

        queue.Post(new LaunchPayload([@"C:\c.zip"], Auto: false));
        Assert.Empty(seen);

        queue.Attach(payload => seen.Add(payload.Paths[0]));
        Assert.Equal(new[] { @"C:\c.zip" }, seen);
    }
}
