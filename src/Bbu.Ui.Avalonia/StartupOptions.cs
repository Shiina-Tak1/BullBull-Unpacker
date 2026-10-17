using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bbu.Ui.Avalonia;

/// <summary>
/// 启动开关：入口（<c>Bbu.App</c> 的 <c>Program</c>）从 argv 解析后写入，界面层只读。
///
/// 为什么放在界面层而不是入口层：这些开关只被界面用（截图、铺假数据、自动跑一遍），
///   而 <c>Bbu.Ui.Avalonia</c> 不许引用 <c>Bbu.App</c>（引用方向单向，架构断言强制）。
///   入口层负责解析，界面层负责读，方向仍是 App → Ui。
/// </summary>
public static class StartupOptions
{
    /// <summary>
    /// 本表认的开关名，以 <c>=</c> 结尾的表示要跟一个值。入口层判「未知开关」时读这一份。
    /// 新增开关要同时改这一处与 <see cref="Apply"/>，两边的同步由断言钉住。
    /// </summary>
    public static readonly IReadOnlyList<string> Known =
    [
        "--auto-run",
        "--overlay",
        "--ask-close",
        "--shot=",
        "--shot-mid=",
        "--shot-mid-delay=",
        "--shot-scale=",
        "--drop=",
        "--page=",
        "--size=",
    ];

    /// <summary><c>--shot=&lt;path&gt;</c>：起来后自己渲染一张 PNG 再退出。
    /// 自己渲染是 DIP 1:1，系统截屏在 200% 缩放下拿到的是虚拟化矩形。</summary>
    public static string? ShotPath { get; set; }

    /// <summary>
    /// <c>--drop=&lt;path&gt;</c>：启动时就把这些包放进清单，可以给多次，按给的先后排队。
    /// 收全部而不是取第一个：清单里要同时排几个任务时，只取第一个会让其余的**静默丢掉**。
    /// </summary>
    public static IReadOnlyList<string> DropPaths { get; set; } = [];

    /// <summary><c>--auto-run</c>：起来后自动跑一遍，走「开始解压」按钮同一个 <c>RunAsync</c>。</summary>
    public static bool AutoRun { get; set; }

    /// <summary><c>--overlay</c>：起来就把「关于」浮层打开。</summary>
    public static bool ShowOverlay { get; set; }

    /// <summary><c>--shot-mid=&lt;path&gt;</c>：解压过程中再截一张。</summary>
    public static string? ShotMidPath { get; set; }

    /// <summary><c>--shot-mid-delay=&lt;ms&gt;</c>：上面那张什么时候截，默认 600ms。</summary>
    public static int ShotMidDelayMs { get; set; } = 600;

    /// <summary><c>--shot-scale=&lt;n&gt;</c>：截图倍率，默认 1（即 DIP 1:1）。
    /// 布局尺寸不变、只提高像素密度：文字是矢量重绘的，放大看仍然锐利，
    /// 出商店图这类要放大的物料靠它，放大现有位图只会软（实际取值在 SaveShot 里夹到 1..4）。</summary>
    public static int ShotScale { get; set; } = 1;

    /// <summary>
    /// <c>--page=&lt;library|settings&gt;</c>：起来就把这一层浮窗打开。
    /// 只为对照截图存在：三页各一张是判据要的，没有这个开关就只能靠手点，
    /// 而手点的时机与主题切换凑不到一起。
    /// </summary>
    public static string? Page { get; set; }

    /// <summary><c>--ask-close</c>：起来就把关窗确认框打开（带一个假任务，见 <c>MainWindow</c>）。</summary>
    public static bool AskClose { get; set; }

    /// <summary><c>--size=&lt;宽x高&gt;</c>：把窗口固定为该尺寸（分档对照截图用）。</summary>
    public static (double Width, double Height)? Size { get; set; }

    /// <summary>从 argv 解析并写入本表。入口层调用一次。</summary>
    public static void Apply(string[] args)
    {
        AutoRun = Has(args, "--auto-run");
        ShowOverlay = Has(args, "--overlay");
        AskClose = Has(args, "--ask-close");
        ShotPath = Value(args, "--shot=");
        DropPaths = Values(args, "--drop=");
        ShotMidPath = Value(args, "--shot-mid=");
        Page = Value(args, "--page=");
        Size = ParseSize(Value(args, "--size="));
        if (int.TryParse(Value(args, "--shot-mid-delay="), out var delay))
        {
            ShotMidDelayMs = delay;
        }

        if (int.TryParse(Value(args, "--shot-scale="), out var shotScale) && shotScale > 0)
        {
            ShotScale = shotScale;
        }
    }

    private static (double Width, double Height)? ParseSize(string? text)
    {
        var parts = text?.Split('x', 'X');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    /// <summary>可以给多次的开关：按 argv 里的先后收全部值，一个都不丢。</summary>
    private static IReadOnlyList<string> Values(string[] args, string prefix)
    {
        var found = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(arg[prefix.Length..]);
            }
        }

        return found;
    }
}
