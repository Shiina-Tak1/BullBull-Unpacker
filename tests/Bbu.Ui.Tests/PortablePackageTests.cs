using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 冷启动红线：便携包解压到干净目录，双击起得来。挂真机档的理由是前置与成本：先要有一个能起窗口的桌面会话，并先打出一份便携包。
/// 分界是成本，不是能不能起窗口。
///
/// 便携包的位置由环境变量 BBU_PORTABLE_ZIP 给，不写死在仓库里：打包产物落在 `.scratch\release\`，
/// 它既不在版本库里，也不该由测试假定存在。环境变量没给时这条直接失败而不是跳过：
/// 「没验」与「验过」必须分得开。
///
/// 跑法：
/// </summary>
public sealed class PortablePackageTests
{
    [Fact]
    [Trait("Category", "machine")]
    public async Task PortablePackage_MustStartFromACleanDirectory()
    {
        var zip = Environment.GetEnvironmentVariable("BBU_PORTABLE_ZIP");
        Assert.False(string.IsNullOrWhiteSpace(zip), "BBU_PORTABLE_ZIP 未指向便携包 zip");
        Assert.True(File.Exists(zip), $"便携包不在：{zip}");

        var root = Path.Combine(AppContext.BaseDirectory, "portable-start", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? process = null;
        try
        {
            ZipFile.ExtractToDirectory(zip!, root);
            var exe = Directory
                .EnumerateFiles(root, "BullBull Unpacker.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            Assert.False(exe is null, "包里没有 BullBull Unpacker.exe");

            process = Process.Start(new ProcessStartInfo(exe!)
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
            })!;

            // 刚解压出来的 exe 首次启动要过一遍杀软，等窗口要留够时间
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.HasExited || process.MainWindowHandle != IntPtr.Zero)
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.False(process.HasExited, "进程起来之后自己退了");
            process.Refresh();
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
        }
        finally
        {
            // 必须杀进程树：否则包里的 dll 一直被占，临时目录删不掉，机器上还留窗口
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch (InvalidOperationException)
                {
                    // 已经退了
                }
            }

            process?.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 句柄释放要一点时间；删不掉不影响判据
            }
        }
    }
}
