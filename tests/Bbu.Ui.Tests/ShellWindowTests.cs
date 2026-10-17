using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 真窗口那一档：窗口图标、双击标题栏、窄窗与基准窗下三个系统按钮都在可视区内。
///
/// 这三条从进程外面量不出来，所以起真产物、发真输入、读窗口自己的诊断（
/// 要真窗口才验得出来的挂 <c>machine</c>，不看 severity）。默认档那几条 headless 断言看的是布局，
/// 这一档验的是真窗口的图标、双击切换与三个系统按钮的落点。
///
/// 前提：先 <c>dotnet build Bbu.slnx</c>；本程序没有别的实例在跑（同一个互斥体与命名管道会互相干扰）。
/// 本档会动鼠标并切换前台窗口，属真机档，发版前手动执行。
/// </summary>
public sealed class ShellWindowTests
{
    [Fact]
    [Trait("Category", "machine")]
    [Trait("Bug", "B-2026-121")]
    public void NarrowWindow_MustKeepTheThreeSystemButtonsInside()
    {
        using var app = Launch("--size=700x620");

        AssertButtonsInside(app, 700);
    }

    [Fact]
    [Trait("Category", "machine")]
    [Trait("Bug", "B-2026-121")]
    public void BaselineWindow_MustKeepTheThreeSystemButtonsInside()
    {
        using var app = Launch("--size=1160x660");

        AssertButtonsInside(app, 1160);
    }

    /// <summary>任务栏与 Alt+Tab 用的是窗口图标：Win32 那边问得到，就不是系统默认那张。</summary>
    [Fact]
    [Trait("Category", "machine")]
    [Trait("Bug", "B-2026-119")]
    public void Window_MustCarryAnIconOnTheRealWindow()
    {
        using var app = Launch("--size=1160x660");

        var big = SendMessage(app.Handle, WM_GETICON, ICON_BIG, IntPtr.Zero);
        var small = SendMessage(app.Handle, WM_GETICON, ICON_SMALL, IntPtr.Zero);
        var klass = GetClassLongPtr(app.Handle, GCLP_HICON);

        Assert.True(
            big != IntPtr.Zero || small != IntPtr.Zero || klass != IntPtr.Zero,
            "真窗口上没有图标：任务栏与 Alt+Tab 会是系统默认那张");

        Assert.False(app.Diagnostics()["Icon.IsNull"] == "True", "窗口自己的 Icon 是空的");
    }

    /// <summary>
    /// 双击标题栏最大化、再双击还原。发的是真鼠标事件 ——
    /// 这条判据要的正是"真窗口上双击一次就翻一次"，判定与实现方式无关（B-2026-118）。
    /// </summary>
    [Fact]
    [Trait("Category", "machine")]
    [Trait("Bug", "B-2026-118")]
    public void DoubleClickOnTitleBar_MustToggleMaximize()
    {
        using var app = Launch("--size=1160x660");

        DoubleClickTitleBar(app);
        Assert.True(IsZoomed(app.Handle), "双击标题栏之后窗口没有最大化");

        DoubleClickTitleBar(app);
        Assert.False(IsZoomed(app.Handle), "再双击一次没有还原");
    }

    /// <summary>按钮要完整落在客户区里，右边还要留得住顶栏自己的内边距。</summary>
    private static void AssertButtonsInside(LaunchedApp app, double expectedWidth)
    {
        var diag = app.Diagnostics();
        var client = SizeOf(diag, "ClientSize (DIP)");
        var pad = 12.0;

        Assert.True(
            Math.Abs(client.Width - expectedWidth) < 1.5,
            $"窗口没有按 --size 摆：客户区宽 {client.Width:0.#}，要的是 {expectedWidth:0.#}");

        var stray = new List<string>();
        foreach (var name in new[] { "BtnMin", "BtnMax", "BtnClose" })
        {
            var rect = BoxOf(diag, "TopBar." + name);
            if (rect.Right > client.Width + 0.5 || rect.Bottom > client.Height + 0.5)
            {
                stray.Add($"{name} 落在 {rect}");
                continue;
            }

            if (name == "BtnClose" && rect.Right + pad > client.Width + 0.5)
            {
                stray.Add($"BtnClose 贴到了右边界：右边只剩 {client.Width - rect.Right:0.#}");
            }
        }

        Assert.True(stray.Count == 0, $"{expectedWidth:0}px 宽的窗口里：" + string.Join("；", stray));
    }

    /// <summary>
    /// 往标题栏正中发两次左键按下与抬起。走窗口消息而不是全局鼠标事件：
    /// 全局事件要先把窗口弄到前台，跨进程设前台会被系统拒，点就落到别人身上了；
    /// 消息直接进目标窗口，遮挡与前台都不影响，也不动用户的鼠标。
    /// </summary>
    private static void DoubleClickTitleBar(LaunchedApp app)
    {
        var size = SizeOf(app.Diagnostics(), "ClientSize (DIP)");
        var x = (int)(size.Width * 0.5);
        var y = 20;

        var point = MakeLParam(x, y);
        PostMessage(app.Handle, WM_LBUTTONDOWN, MK_LBUTTON, point);
        PostMessage(app.Handle, WM_LBUTTONUP, IntPtr.Zero, point);
        Thread.Sleep(60);
        PostMessage(app.Handle, WM_LBUTTONDOWN, MK_LBUTTON, point);
        PostMessage(app.Handle, WM_LBUTTONUP, IntPtr.Zero, point);
        Thread.Sleep(600);
    }

    private static IntPtr MakeLParam(int x, int y) => (y << 16) | (x & 0xFFFF);

    /// <summary>测试程序集自己所在的那一段 <c>bin\&lt;配置&gt;\&lt;TFM&gt;\&lt;RID&gt;</c>：产物与测试同形，
    /// 从自身位置取它，改 TFM 或加 RID 时跟着走（手拼的那一份会静默指空，报出来的是「找不到产物」）。
    /// 取的是**测试程序集**的位置而不是 <c>AppContext.BaseDirectory</c>：后者是宿主入口目录，
    /// 经 <c>xunit.console</c> 跑时它落在运行器那一侧，没有 bin 段。取不到回空串，由调用方的缺件断言出声。</summary>
    private static string BuildOutputTail()
    {
        var dir = Path.GetDirectoryName(typeof(ShellWindowTests).Assembly.Location) ?? string.Empty;
        var marker = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var at = dir.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? string.Empty : dir[(at + 1)..];
    }

    /// <summary>起真产物、等窗口出现、等它把诊断写出来。</summary>
    private static LaunchedApp Launch(string args)
    {
        var exe = Path.Combine(RepoRoot(), "src", "Bbu.App", BuildOutputTail(), "Bbu.App.exe");
        Assert.True(File.Exists(exe), $"找不到真窗口产物：{exe}（先 dotnet build Bbu.slnx）");

        var diag = Path.Combine(Path.GetTempPath(), "bbu-diag.txt");
        File.Delete(diag);

        var data = Directory.CreateTempSubdirectory("bbu-shellwin-").FullName;
        var info = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        info.Environment[DataDirEnv] = data;

        var process = Process.Start(info) ?? throw new InvalidOperationException("真产物没起来");
        var handle = WaitForWindow(process);
        var parsed = WaitForDiagnostics(diag, process);

        return new LaunchedApp(process, handle, data, diag, parsed);
    }

    private static IntPtr WaitForWindow(Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                Assert.Fail($"真产物提前退出，退出码 {process.ExitCode}");
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(150);
        }

        Assert.Fail("等不到真窗口");
        return IntPtr.Zero;
    }

    private static Dictionary<string, string> WaitForDiagnostics(string path, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    return Parse(File.ReadAllLines(path));
                }
                catch (IOException)
                {
                    // 程序正在写，再等一轮
                }
            }

            if (process.HasExited)
            {
                Assert.Fail("真产物退出前没有写出诊断");
            }

            Thread.Sleep(150);
        }

        Assert.Fail("等不到诊断文件");
        return [];
    }

    private static Dictionary<string, string> Parse(string[] lines)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var at = line.IndexOf('=');
            if (at > 0)
            {
                parsed[line[..at].Trim()] = line[(at + 1)..].Trim();
            }
        }

        return parsed;
    }

    /// <summary>仓库根：往上一路找 Bbu.slnx，不写死层数（换 TFM 或 RID 会让层数变）。</summary>
    private static string RepoRoot() => TestRepo.Root();

    private const string DataDirEnv = "BBU_DATA_DIR";

    private sealed class LaunchedApp(Process process, IntPtr handle, string dataDir, string diagPath,
        Dictionary<string, string> diagnostics) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public Dictionary<string, string> Diagnostics() => diagnostics;

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // 已经退出了
            }

            process.Dispose();
            TryDelete(dataDir);
            TryDelete(diagPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 一次性目录，删不掉就留给系统
            }
        }
    }

    /// <summary>诊断里那几行 <c>x,y,w,h</c> 与 <c>宽 x 高</c> 的读法。</summary>
    private static Box BoxOf(Dictionary<string, string> diagnostics, string key) =>
        Box.Parse(diagnostics[key]);

    private static (double Width, double Height) SizeOf(Dictionary<string, string> diagnostics, string key)
    {
        var parts = diagnostics[key].Split('x', StringSplitOptions.TrimEntries);
        return (double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;

        public double Bottom => Y + Height;

        public static Box Parse(string text)
        {
            var parts = text.Split(',');
            return new Box(
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture));
        }

        public override string ToString() =>
            $"x={X:0.#} y={Y:0.#} w={Width:0.#} h={Height:0.#}";
    }

    private const uint WM_GETICON = 0x007F;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const int GCLP_HICON = -14;
    private static readonly IntPtr ICON_BIG = 1;
    private static readonly IntPtr ICON_SMALL = 0;
    private static readonly IntPtr MK_LBUTTON = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);
}
