using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Logging;
using Bbu.Core.SingleInstance;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 单实例协议：载荷的序列化形状，以及"递进去能收到"这条往返。
/// 跨进程的互斥由 <c>--single-instance-probe</c> 在真机上核对。
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void Payload_RoundTripsThroughJson()
    {
        // 包名是虚构的，盘符取约定的虚构测试盘 Z:\ —— 它同时是「不存在的盘」那条断言的用场。
        var payload = new LaunchPayload(new[] { @"Z:\一个包.zip", @"Z:\空的 名字.7z" }, Auto: true);

        var json = SingleInstanceProtocol.Serialize(payload);
        Assert.True(SingleInstanceProtocol.TryDeserialize(json, out var parsed));

        // 键名沿用 1.1.0 的形状：paths / auto
        Assert.Contains("\"paths\"", json, StringComparison.Ordinal);
        Assert.Contains("\"auto\"", json, StringComparison.Ordinal);
        Assert.Equal(payload.Paths, parsed.Paths);
        Assert.True(parsed.Auto);
    }

    [Fact]
    public void Payload_BrokenJson_IsRejectedWithoutThrowing()
    {
        Assert.False(SingleInstanceProtocol.TryDeserialize("{ 这不是 JSON", out _));
        Assert.False(SingleInstanceProtocol.TryDeserialize("", out _));
    }

    /// <summary>
    /// 转发往返。挂在真机档而不是默认档，原因是环境而不是逻辑：
    /// agent 沙箱拒绝命名管道**客户端连接**（<c>TryConnect</c> 报拒绝访问），
    /// 连同一个进程内起服务端与客户端也连不上，这条断言会恒红。
    /// 正常终端与作者机器上它在默认档也过；
    /// </summary>
    [Fact]
    [Trait("Category", "machine")]
    public async Task Forward_MustReachTheListener()
    {
        using var listener = new SingleInstanceListener();
        var received = new TaskCompletionSource<LaunchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.PayloadReceived += payload => received.TrySetResult(payload);
        listener.Start();

        // 管道建不起来就直接红：那说明这台机器的环境不允许单实例转发
        Assert.True(listener.IsListening, listener.LastError);

        var result = SingleInstanceForwarder.Forward(
            new LaunchPayload(new[] { @"D:\某个包.zip" }, Auto: false));

        Assert.Equal(ForwardResult.Delivered, result);

        // 用 Task.WhenAny 超时，不用 Task.Wait：阻塞式等待在测试里可能死锁（xUnit1031）
        var finished = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(finished == received.Task, "监听端没收到载荷");
        Assert.Equal(@"D:\某个包.zip", (await received.Task).Paths[0]);
    }

    /// <summary>
    /// 后来者把路径交出去之后必须**自己退出**，否则会开出第二个窗口 ——
    /// 右键多选 N 个文件就是 N 个 BBU 窗口（B-2026-115）。
    ///
    /// 这里起的是**真进程**：主实例用 `--single-instance-probe` 占住互斥体与管道（它不开窗），
    /// 第二个进程走正常的开窗路径、带一个路径进来。它不退出就说明窗口也开了。
    ///
    /// 两个进程都要在收尾时掐掉：这条断言失败时（或环境不许它转发时）后来者会一直开着窗口活着，
    /// 它占着互斥体与管道，后面所有单实例断言都会拿到假绿。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-115")]
    [Trait("Category", "machine")]
    public async Task SecondaryProcess_MustHandOverAndExitWithoutOpeningAWindow()
    {
        var executable = AppExecutable();
        Assert.True(File.Exists(executable), $"找不到被测 exe：{executable}（真机档按 Debug 产物定位）");

        using var primary = StartApp(executable, "--single-instance-probe", "--hold=6000");
        Process? secondary = null;
        try
        {
            // 管道是在后台线程里建的，给它一点时间
            await Task.Delay(1500);

            secondary = StartApp(executable, @"D:\某个包.zip");
            Assert.True(
                await ExitsWithinAsync(secondary, TimeSpan.FromSeconds(6)),
                "后来者没有自己退出：转发成功之后仍往下开窗了");
        }
        finally
        {
            KillIfAlive(secondary);
            KillIfAlive(primary);
        }

        var output = await primary.StandardOutput.ReadToEndAsync();
        Assert.Contains("role=primary", output, StringComparison.Ordinal);
        Assert.Contains("received=1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 载荷的形状是契约：只有 <c>paths</c> 与 <c>auto</c> 两个字段。
    /// 防的是以后为了「鉴权」往载荷里塞自称的身份、口令或 PID —— 身份判据只取内核给的答案（<see cref="SingleInstancePeer"/>）。
    /// </summary>
    [Fact]
    public void PayloadKeys_MustStayPathsAndAuto()
    {
        var json = SingleInstanceProtocol.Serialize(new LaunchPayload(new[] { @"Z:\一个包.zip" }, Auto: true));

        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "paths", "auto" }, keys);
    }

    /// <summary>对端身份判据：同一个文件名、不同目录算两份程序（便携档与安装档各有各的窗口）。</summary>
    [Fact]
    public void PeerIdentity_MustCompareWholeImagePaths()
    {
        const string Own = @"Z:\portable\Bbu.App.exe";

        Assert.True(SingleInstancePeer.SameProgram(Own, Own));
        Assert.True(SingleInstancePeer.SameProgram(Own.ToUpperInvariant(), Own));
        Assert.False(SingleInstancePeer.SameProgram(@"Z:\installed\Bbu.App.exe", Own));
        Assert.False(SingleInstancePeer.SameProgram(null, Own));
        Assert.False(SingleInstancePeer.SameProgram(Own, null));
        Assert.False(SingleInstancePeer.IsOwnProgram(null));
    }

    /// <summary>
    /// 名字作用域：管道名按登录会话派生（第二个会话里的主实例才建得起自己的管道），
    /// 互斥体名照旧一个、仍是 <c>Local\</c>（跨会话各成主实例是刻意的）。
    /// </summary>
    [Fact]
    public void PipeName_MustDifferPerSessionAndTheMutexMustNot()
    {
        Assert.NotEqual(SingleInstanceProtocol.PipeNameFor(1), SingleInstanceProtocol.PipeNameFor(2));
        Assert.StartsWith(SingleInstanceProtocol.PipeNamePrefix, SingleInstanceProtocol.PipeName, StringComparison.Ordinal);
        // 这一条只是属性初始化式的镜像：真正判红的是上一行「两个会话号算出两个名字」
        Assert.Equal(
            SingleInstanceProtocol.PipeNameFor(SingleInstanceProtocol.CurrentSessionId()),
            SingleInstanceProtocol.PipeName);
        Assert.Equal(@"Local\bbu-unpacker-single", SingleInstanceProtocol.MutexName);
    }

    /// <summary>
    /// 有界读两段一起钉：超限的流在上限处停手并回「太长」，而正常载荷照旧读得进来。
    /// 只钉前一段会漏掉「正常载荷被误伤」这种更坏的错。
    /// </summary>
    [Fact]
    public async Task BoundedRead_MustStopAtTheLimitAndStillAcceptANormalPayload()
    {
        const int Max = 1024;

        var flooding = new FloodStream();
        var tooLong = await SingleInstanceListener.ReadBoundedAsync(flooding, Max, CancellationToken.None);

        Assert.Null(tooLong);
        // 多读的那一个字节就是「还没读完」的信号：交出去的字节数不该超过上限加一
        Assert.InRange(flooding.HandedOut, 1, Max + 1);

        using var normal = new MemoryStream(
            Encoding.UTF8.GetBytes("""{"paths":["Z:\\一个包.zip"],"auto":true}""" + "\n"));
        var json = await SingleInstanceListener.ReadBoundedAsync(normal, Max, CancellationToken.None);

        Assert.NotNull(json);
        Assert.True(SingleInstanceProtocol.TryDeserialize(json!, out var payload));
        Assert.Equal(@"Z:\一个包.zip", payload.Paths[0]);
    }

    /// <summary>
    /// 一条消息的结尾到了就得返回，**不许**等到对端关闭：写完还要等回执，两边互等即死锁。
    /// 撤掉「读到换行就返回」那一句：这条会卡在等待里，由超时判红。
    /// </summary>
    [Fact]
    public async Task BoundedRead_MustFinishAtTheMessageTerminatorWithoutThePeerClosing()
    {
        var payload = """{"paths":["Z:\\一个包.zip"],"auto":false}""" + SingleInstanceProtocol.MessageTerminator;
        var stream = new HangingAfterContentStream(Encoding.UTF8.GetBytes(payload));

        var reading = SingleInstanceListener.ReadBoundedAsync(stream, 1024, CancellationToken.None);
        var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(finished == reading, "读到换行之后还在等：对端没关连接就永远收不到载荷");
        Assert.Equal(
            """{"paths":["Z:\\一个包.zip"],"auto":false}""",
            await reading);
    }

    /// <summary>退避：连着失败逐次翻倍、封顶两秒；成功投递一次就从头算（不然正常路径会被越拖越慢）。</summary>
    [Fact]
    public void Backoff_MustGrowToTheCapAndStartOverAfterADelivery()
    {
        var backoff = new SingleInstanceBackoff();

        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.OnPipeCreateFailed());
        Assert.Equal(TimeSpan.FromMilliseconds(200), backoff.OnPipeCreateFailed());
        Assert.Equal(TimeSpan.FromMilliseconds(400), backoff.OnPipeCreateFailed());

        for (var i = 0; i < 20; i++)
        {
            backoff.OnPipeCreateFailed();
        }

        Assert.Equal(TimeSpan.FromMilliseconds(2000), backoff.OnPipeCreateFailed());

        backoff.OnDelivered();

        Assert.Equal(0, backoff.Failures);
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.OnPipeCreateFailed());
    }

    /// <summary>对端断开不算建管道失败：正常收尾也走那一条路，算进去会让每次转发都多等一档退避。</summary>
    [Fact]
    public void PeerDisconnect_MustNotEarnABackoff()
    {
        var backoff = new SingleInstanceBackoff();
        backoff.OnPipeCreateFailed();

        // 这一条只是实现的镜像：真正判红的是反向对照那一侧（把它接到建管道失败那一档）
        Assert.Equal(TimeSpan.Zero, backoff.OnPeerDisconnected());
        Assert.Equal(1, backoff.Failures);
    }

    /// <summary>
    /// `F-02` 的另一半：假主实例（占着互斥体、起着同名管道，但不是本份程序）拿不到用户路径，
    /// 而真程序**不许**悄悄退出 —— 它得自己开窗把这份归档接下来。
    /// 撤掉客户端的对端身份校验：这条连接上会出现那份路径，本断言变红。
    /// </summary>
#pragma warning disable CA1416 // 管道安全描述符与 SID 是 Windows 专属，下面这一对只在真机档与 Windows 上说得通
    [Fact]
    [Trait("Bug", "B-2026-190")]
    [Trait("Category", "machine")]
    public async Task ForeignPrimary_MustNotGetThePathAndTheRealOneMustKeepRunning()
    {
        var executable = AppExecutable();
        Assert.True(File.Exists(executable), $"找不到被测 exe：{executable}（真机档按 Debug 产物定位）");

        // 互斥体要由一个**自己起的线程**握着：Mutex 是线程亲和的，而测试在同一个线程上 await 之后，
        // 那个线程会回线程池、可能被退休 —— 退休就等于把互斥体抛掉，后来者于是变成主实例，
        // 这条断言就测不到东西（它表现为「一直没等到连接」）。
        using var release = new ManualResetEventSlim(false);
        using var holding = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, SingleInstanceProtocol.MutexName);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                return;
            }

            holding.Set();
            release.Wait(TimeSpan.FromSeconds(90));
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "bbu-single-instance-holder",
        };
        holder.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)), "互斥体被另一个实例占着（需先关掉正在跑的 BBU）");

        // 假管道必须带**低完整性标签**：工作区根带那条标签，从那里启动的产物继承低完整性，
        // 而测试宿主（`dotnet.exe` 在 Program Files 下）是中完整性：后来者在 Connect 那一步就被系统拒掉，
        // 这条断言于是测不到东西（表现为「一直没等到连接」）。
        // 标签本身由默认档的 `FakePipe_MustAcceptTheLowIntegrityLabel` 先钉住。
        await using var fake = FakePipe(SingleInstanceProtocol.PipeName);

        using var victim = StartApp(executable, ExistingFile());
        try
        {
            // 给它够长的时间：真机档排在整套末尾，机器忙时冷启动一个 Debug 产物要几秒
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await fake.WaitForConnectionAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(await DescribeVictimAsync(victim, executable, "假主实例一直没等到连接"));
            }

            // 一个字节都不该来：它连上之后认出「这不是本份程序」，就地返回
            var buffer = new byte[512];
            using var readBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var read = 0;
            try
            {
                read = await fake.ReadAsync(buffer, readBudget.Token);
            }
            catch (IOException)
            {
                // 对端把连接断开而不是正常关：对这条断言来说与「读到 0 个字节」同义，两者都没有把路径交出去
            }

            Assert.Equal(0, read);

            // 也不许悄悄退出（退出码 0、不开窗、什么都不做）：那正是这条缺陷的用户可见形态
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(victim.HasExited, "后来者悄悄退出了：用户拖进去的文件没有任何反应");
        }
        finally
        {
            KillIfAlive(victim);
            release.Set();
        }
    }

    /// <summary>
    /// `F-03`：外部进程（本测试自己，映像不是本份程序）往主实例的管道里灌一份「路径 + 自动开跑」，
    /// 主实例**不收**这份载荷 —— 界面上不该冒出用户没拖过的任务。
    /// 撤掉服务端的对端身份校验：`probe received=1` 会出现，本断言变红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-191")]
    [Trait("Category", "machine")]
    public async Task ForeignClient_MustNotGetItsPayloadAccepted()
    {
        var executable = AppExecutable();
        Assert.True(File.Exists(executable), $"找不到被测 exe：{executable}");

        // 上一条断言刚杀掉的进程可能还没释放互斥体：探针若以「后来者」身份起来就不会建监听，
        // 断言会以 role=secondary 失败 —— 那是时序，不是缺陷，所以先等它空出来。
        Assert.True(WaitForNoInstance(TimeSpan.FromSeconds(10)), "互斥体一直被另一个实例占着（需先关掉正在跑的 BBU）");

        using var primary = StartApp(executable, "--single-instance-probe", "--hold=6000");
        await Task.Delay(TimeSpan.FromSeconds(2));

        var injected = Encoding.UTF8.GetBytes(
            $$"""{"paths":["{{ExistingFile().Replace("\\", "\\\\", StringComparison.Ordinal)}}"],"auto":true}"""
            + SingleInstanceProtocol.MessageTerminator);

        using var client = new NamedPipeClientStream(
            ".", SingleInstanceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // 连不上就直接判红：管道名派生回归、主实例没起来这类情形下载荷一个字节都发不出去，
        // 而「输出里没有 received=」照样成立 —— 那条路是假绿，不能与写入被中止混在一个 catch 里。
        client.Connect(2000);
        Assert.True(client.IsConnected, "没连上主实例的管道：这条断言测不到注入");

        try
        {
            await client.WriteAsync(injected);
            await client.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // 主实例认出对端之后可以直接掐掉这条连接：写入失败是预期形态之一
        }

        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await primary.WaitForExitAsync(budget.Token);
        }

        var output = await primary.StandardOutput.ReadToEndAsync();
        Assert.Contains("role=primary", output, StringComparison.Ordinal);
        Assert.DoesNotContain("received=", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// `F-19` 的真管道版本：同一份程序送来的超限载荷不算一次转发（读在上限处返回），
    /// 而同样这条管道上正常大小的载荷照旧收得进来。
    /// 撤掉读上限：超限那份会解析成功并进清单，前半段变红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-192")]
    [Trait("Category", "machine")]
    public async Task OversizedMessage_MustNotBeAcceptedOverARealPipe()
    {
        using var listener = new SingleInstanceListener();
        var received = new List<LaunchPayload>();
        listener.PayloadReceived += payload =>
        {
            lock (received)
            {
                received.Add(payload);
            }
        };
        listener.Start();
        Assert.True(listener.IsListening, listener.LastError);

        var oversized = Encoding.UTF8.GetBytes(
            $$"""{"paths":["{{new string('x', SingleInstanceProtocol.MaxPayloadBytes + 4096)}}"],"auto":true}""");
        await SendRawAsync(oversized);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(received);

        await SendRawAsync(Encoding.UTF8.GetBytes(
            SingleInstanceProtocol.Serialize(new LaunchPayload(new[] { ExistingFile() }, Auto: false))));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(received);
    }

    /// <summary>
    /// `F-20`：管道名被占住时监听循环要退避重试，**不许**拿一颗核空转，也不许一走了之。
    /// 撤掉退避：三秒里会试上千次、CPU 也贴满，两条断言一起红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-193")]
    [Trait("Category", "machine")]
    public async Task OccupiedPipe_MustNotSpinTheListener()
    {
        await using var holder = new NamedPipeServerStream(
            SingleInstanceProtocol.PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using var listener = new SingleInstanceListener();
        listener.Start();
        await Task.Delay(TimeSpan.FromSeconds(1));

        var before = Process.GetCurrentProcess().TotalProcessorTime;
        await Task.Delay(TimeSpan.FromSeconds(3));
        var burned = Process.GetCurrentProcess().TotalProcessorTime - before;

        Assert.True(listener.IsListening, "监听退出了：它该一直退避重试，等名字空出来");
        Assert.NotNull(listener.LastError);
        Assert.True(burned < TimeSpan.FromSeconds(0.5), $"三秒里占用了 {burned.TotalSeconds:F2} 秒 CPU：退避没生效");

        // 退避还该在次数上看得出来：这几秒里是次数级，不是千次级
        // 反射读私有字段：字段改名会让这条红在反射上、而不是判据上，动它时一并看
        var backoff = (SingleInstanceBackoff)typeof(SingleInstanceListener)
            .GetField("_backoff", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(listener)!;
        Assert.InRange(backoff.Failures, 1, 8);
    }

    /// <summary>被测 exe 的位置。真机档跑的是 Debug 产物。</summary>
    private static string AppExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bbu.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "src", "Bbu.App", BuildOutputTail(), "Bbu.App.exe");
    }

    /// <summary>测试程序集自己所在的那一段 <c>bin\&lt;配置&gt;\&lt;TFM&gt;\&lt;RID&gt;</c>：产物与测试同形，
    /// 从自身位置取它，改 TFM 或加 RID 时跟着走（手拼的那一份会静默指空，报出来的是「找不到产物」）。
    /// 取的是**测试程序集**的位置而不是 <c>AppContext.BaseDirectory</c>：后者是宿主入口目录，
    /// 经 <c>xunit.console</c> 跑时它落在运行器那一侧，没有 bin 段。取不到回空串，由调用方的缺件断言出声。</summary>
    private static string BuildOutputTail()
    {
        var dir = Path.GetDirectoryName(typeof(SingleInstanceTests).Assembly.Location) ?? string.Empty;
        var marker = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var at = dir.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? string.Empty : dir[(at + 1)..];
    }

    /// <summary>
    /// 起一份真产物，数据目录按便携口径落在 `&lt;exe 目录&gt;\userdata\`（开发仓库下那一份按沙箱对待）。
    /// 本方法不得加重载：加了 `(string executable, string dataDir, params string[] arguments)` 之后，
    /// `StartApp(exe, "--probe", "--hold=6000")` 会绑到它上面，`--probe` 被当成数据目录、探针模式随之丢失 ——
    /// 应用改开普通窗口、占住互斥体与管道，真机档的断言随之连锁失败。
    /// </summary>
    private static Process StartApp(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private static async Task<bool> ExitsWithinAsync(Process process, TimeSpan limit)
    {
        using var cancel = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 等到互斥体空出来。上一条断言刚杀掉的进程可能还没释放它，而探针若以「后来者」身份起来，
    /// 就不会建监听、断言会以 <c>role=secondary</c> 失败 —— 那是时序，不是缺陷。
    /// </summary>
    private static bool WaitForNoInstance(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var probe = new Mutex(initiallyOwned: false, SingleInstanceProtocol.MutexName);
                if (probe.WaitOne(0))
                {
                    probe.ReleaseMutex();
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者崩了：互斥体被抛弃，下一个等的人接手，对本断言来说就是空出来了
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>
    /// 还没退出的进程直接掐掉。留着它比测试变红更坏：它占着互斥体与管道，
    /// 后面每一条单实例断言都会「通过」—— 那是假绿。
    /// </summary>
    private static void KillIfAlive(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 恰好在两次判断之间退了
        }
    }

    /// <summary>
    /// 后来者到底怎么了：退出状态、退出码、两条输出流，加它自己的日志末尾。
    /// 真机档上一条失败的断言只报一句「一直没等到连接」是没法往下查的。
    /// </summary>
    private static async Task<string> DescribeVictimAsync(Process victim, string executable, string what)
    {
        var note = new StringBuilder();
        note.AppendLine(what + '。');
        note.AppendLine($"后来者：已退出={victim.HasExited}，退出码={(victim.HasExited ? victim.ExitCode.ToString(CultureInfo.InvariantCulture) : "还没退")}");
        note.AppendLine(DescribeHolding());

        KillIfAlive(victim);
        note.AppendLine("stdout：" + (await victim.StandardOutput.ReadToEndAsync()).Trim());
        note.AppendLine("stderr：" + (await victim.StandardError.ReadToEndAsync()).Trim());

        try
        {
            var log = Path.Combine(Path.GetDirectoryName(executable)!, "userdata", AppInfo.LogFileName);
            if (File.Exists(log))
            {
                // 单实例探针那个进程自己常开着写句柄：读回走共享读取那一处，末尾几条按时间序。
                var tail = RingLogFile.ReadText(log)
                    .Split('\n')
                    .Where(one => one.Length > 0)
                    .TakeLast(12);
                note.AppendLine("日志末尾：" + string.Join(" ⏎ ", tail));
            }
            else
            {
                note.AppendLine($"没有日志文件：{log}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note.AppendLine("日志读不出来：" + ex.Message);
        }

        return note.ToString();
    }

    /// <summary>
    /// 互斥体是否仍被持有：抢得到即说明持有它的那个线程已被退休、互斥体被抛弃 ——
    /// 那样后来者会变成主实例，这条断言就测不到东西。
    /// </summary>
    private static string DescribeHolding()
    {
        try
        {
            if (!Mutex.TryOpenExisting(SingleInstanceProtocol.MutexName, out var probe))
            {
                return "互斥体：已经不在了（连对象都没了）";
            }

            using (probe)
            {
                if (probe.WaitOne(0))
                {
                    probe.ReleaseMutex();
                    return "互斥体：抢得到 —— 持有线程被退休了，后来者是主实例";
                }
            }

            return "互斥体：还在持有（正常）";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return "互斥体查不出来：" + ex.Message;
        }
    }

#pragma warning disable CA1416 // 管道安全描述符与内核对象安全是 Windows 专属，下面这一对只在真机档与 Windows 上说得通
    private const uint LabelSecurityInformation = 0x00000010;

    private const int SeKernelObject = 6;

    /// <summary>
    /// 假管道：本用户可读写、并把完整性标签压到「低」。低完整性的后来者才连得上它
    /// （工作区里启动的产物是低完整性，而测试宿主是中完整性，低连不上中）。
    /// 标签走 `SetSecurityInfo` ＋ `LABEL_SECURITY_INFORMATION`：这条路能**当场**判成败（返回值），
    /// 而把它塞进 `PipeSecurity` 的 SDDL 里读不回来 —— 标签在 SACL 里，读 SACL 要特权。
    /// </summary>
    private static NamedPipeServerStream FakePipe(string name)
    {
        var pipe = NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: null);

        var result = LowerToLowIntegrity(pipe.SafePipeHandle);
        if (result == 0)
        {
            return pipe;
        }

        pipe.Dispose();
        throw new InvalidOperationException("压标签失败，返回码 " + result.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>把对象的完整性标签压到「低」，返回系统给的返回码（0 即成功）。压标签不需要特权，抬高才需要。</summary>
    private static uint LowerToLowIntegrity(SafeHandle handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;LW)", 1, out var descriptor, out _))
        {
            throw new InvalidOperationException("标签描述符没造出来：" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
            {
                throw new InvalidOperationException("描述符里没有 SACL：" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }

            return SetSecurityInfo(handle, SeKernelObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr descriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// `ForwardResult` 的成员集合穷举：加第五个成员时这条变红，两个消费点须跟着看一遍
    /// （`Program.cs` 的处置分支与 probe 输出）—— 那两处是开放式分支，漏了不会自己出声。
    /// </summary>
    [Fact]
    public void ForwardResult_MustStayExhaustive()
    {
        Assert.Equal(
            new[] { "Delivered", "NoServer", "NotOurs", "Unconfirmed" },
            Enum.GetNames<ForwardResult>());
    }

    /// <summary>
    /// 先钉住假管道的前提：**系统接受了这次压标签**（返回码为 0）。
    /// 标签真的生效只有真机那条判得了（低完整性的后来者连得上才算数）。
    /// 撤掉压标签那一句、或把标签描述符写坏：这条变红。
    /// </summary>
    [Fact]
    public void FakePipe_MustAcceptTheLowIntegrityLabel()
    {
        using var pipe = NamedPipeServerStreamAcl.Create(
            "bbu-15-3-标签自检",
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: null);

        Assert.Equal(0u, LowerToLowIntegrity(pipe.SafePipeHandle));
    }
#pragma warning restore CA1416

    /// <summary>盘上真实存在的一个文件：载荷里放它，就当是用户拖进来的那份。</summary>
    private static string ExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "bbu-15-3-目标.zip");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "不是压缩包，只要求它在盘上");
        }

        return path;
    }

    /// <summary>用一条真连接把一条消息递进去（绕开转发器：这两条要灌超限载荷与检查消息结尾）。</summary>
    private static async Task SendRawAsync(byte[] bytes)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", SingleInstanceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(2000);
            await client.WriteAsync(bytes);
            await client.WriteAsync(new[] { (byte)SingleInstanceProtocol.MessageTerminator });
            await client.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // 对端在读满上限之后直接关掉连接：写入失败是预期形态
        }
    }

    /// <summary>写不完的流：每次交出请求的那些字节，用来钉读上限。</summary>
    private sealed class FloodStream : Stream
    {
        public int HandedOut { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Hand(count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Hand(buffer.Length));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Hand(int count)
        {
            HandedOut += count;
            return count;
        }
    }

    /// <summary>
    /// 交完内容就**一直吊着**的流：对端不关连接的样子。用来钉「读到消息结尾就返回」——
    /// 没有这一条，读侧会一直等下去。
    /// </summary>
    private sealed class HangingAfterContentStream : Stream
    {
        private readonly byte[] _content;
        private int _at;

        public HangingAfterContentStream(byte[] content) => _content = content;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var taken = Take(buffer.Span);
            if (taken > 0)
            {
                return taken;
            }

            // 内容交完了但对端没关：一直等，直到调用方取消
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(Span<byte> buffer)
        {
            var room = Math.Min(buffer.Length, _content.Length - _at);
            if (room <= 0)
            {
                return 0;
            }

            _content.AsSpan(_at, room).CopyTo(buffer);
            _at += room;
            return room;
        }
    }
}
