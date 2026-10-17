using System;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>主屏的两档。取值顺序与宽度顺序一致，比较大小即可。</summary>
public enum ShellTier
{
    Wide,
    Mid,
}

/// <summary>两档断点、工具条紧凑阈值与右栏宽度，只放纯函数；视图与断言都取这一份结论。</summary>
public static class Breakpoints
{
    /// <summary>宽档的下限。低于它即中档，窗口下限 700 也落在中档。</summary>
    public const double WideMin = 1000;

    /// <summary>
    /// 工具条收起文字的下限。与档位无关，取的是观感：带文字时工具条最坏需求 578px，
    /// 窗口下限 700 下可用 676px，压到 602px 才会遮挡。
    /// </summary>
    public const double CompactMin = 800;

    /// <summary>右栏宽度的下限。</summary>
    public const double SideMin = 380;

    /// <summary>右栏宽度的上限。</summary>
    public const double SideMax = 460;

    /// <summary>右栏占内容区的比例。基准窗口 1160 下算出 418，不撞上下限。</summary>
    public const double SideRatio = 0.36;

    /// <summary>中档的底部面板展开高度，取自线框 :214。</summary>
    public const double FoldOpen = 286;

    /// <summary>列宽把手的可点范围。视觉线宽由样式表定为 7px，两者分别计算。</summary>
    public const double SplitHit = 16;

    /// <summary>按内容区宽度判档。1000 及以上是宽档，以下均为中档。</summary>
    public static ShellTier Of(double width) =>
        width >= WideMin ? ShellTier.Wide : ShellTier.Mid;

    /// <summary>工具条是否收成图标。与档位各自独立判定。</summary>
    public static bool Compact(double width) => width < CompactMin;

    /// <summary>宽档右栏宽度：按比例算再夹到 380 至 460。死值 420 在窄屏上会压窄任务区。</summary>
    public static double SideWidth(double width) =>
        Math.Round(Math.Clamp(width * SideRatio, SideMin, SideMax));
}
