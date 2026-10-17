using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 形态边界第 2 条：数据不出本机。
/// 判据是跑一次解压，非回环的 TCP 与 UDP 连接为 0 条。
///
/// 为什么挂真机档：成本。它要起真引擎子进程跑一次真解压，并全程采样。
/// 分界是成本，不是能不能起窗口。
/// 跑法：`dotnet test Bbu.slnx --filter Category=machine`。
///
/// 采集边界：只看**本进程**（即 BBU 进程本身）的连接。引擎子进程（`7z.exe`）的连接不在范围内，
/// 它由「引擎契约」那一组断言盯着另一件事。
/// </summary>
[Collection("engine")]
public sealed class NetworkBoundaryTests
{
    [Fact]
    [Trait("Category", "machine")]
    public async Task Unpacking_MustNotOpenNonLoopbackConnections()
    {
        var work = Path.Combine(AppContext.BaseDirectory, "network-boundary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var samples = new List<string>();
            var unpack = Task.Run(async () =>
                await TestPaths.NewEngine().ExtractAsync(
                    TestPaths.Fixture("ok-small.zip"), work, password: null));

            // 解压期间反复采样：连接的寿命可能比一次采样间隔短
            while (!unpack.IsCompleted)
            {
                samples.AddRange(NonLoopbackEndpoints(Environment.ProcessId));
                await Task.Delay(50);
            }

            await unpack;
            samples.AddRange(NonLoopbackEndpoints(Environment.ProcessId));

            Assert.Empty(samples.Distinct().OrderBy(s => s, StringComparer.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // 引擎可能还占着文件，删不掉不影响判据
            }
        }
    }

    /// <summary>本进程名下的非回环端点。监听也算：程序不该把端口开着。</summary>
    private static IEnumerable<string> NonLoopbackEndpoints(int pid)
    {
        var psi = new ProcessStartInfo("netstat", "-ano")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var netstat = Process.Start(psi) ?? throw new InvalidOperationException("起不了 netstat");
        var output = netstat.StandardOutput.ReadToEnd();
        netstat.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || (parts[0] != "TCP" && parts[0] != "UDP"))
            {
                continue;
            }

            if (!int.TryParse(parts[^1], out var owner) || owner != pid)
            {
                continue;
            }

            var local = parts[1];
            if (parts.Length >= 5 && parts[3] == "LISTENING")
            {
                yield return $"监听 {local}";
                continue;
            }

            var remote = parts[2];
            if (!IsLoopbackOrUnspecified(remote))
            {
                yield return $"{parts[0]} {local} -> {remote}";
            }
        }
    }

    private static bool IsLoopbackOrUnspecified(string endpoint)
    {
        var host = endpoint.StartsWith('[')
            ? endpoint[1..endpoint.IndexOf(']')]
            : endpoint.Split(':')[0];

        return host is "127.0.0.1" or "::1" or "0.0.0.0" or "::" or "*" or ""
            || host.StartsWith("127.", StringComparison.Ordinal);
    }
}
