using System;
using System.Collections.Generic;
using System.Linq;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>清单四列的宽度算法，只放纯函数：给定上一次的宽度与每列下限，算出这一次的宽度。</summary>
/// <remarks>
/// 不变量是「四列之和恒等于清单区内容盒的宽」——不留空、也不长横向滚动条。
/// 每列下限由 <see cref="Floors"/> 一处给出，启动回读、拖动、缩放与表头右键重置四条路径共用。
/// </remarks>
public static class ColumnLayout
{
    /// <summary>列数：类型、文件、密码、状态。</summary>
    public const int Count = 4;

    /// <summary>单列下限，四列通用。比这更窄，表头就先被裁掉了。</summary>
    public const double Min = 56;

    /// <summary>单列上限。</summary>
    public const double Max = 1600;

    /// <summary>状态列的内容下限 132：图标 16 ＋ 间距 6 ＋ 最长状态词 40 ＋「999 个子项」。</summary>
    /// <remarks>量的是这一列最坏会出现多宽，不是当前清单那几行的现量 —— 现量随清单变，不构成下限。该数是 2026-10-05 headless 实测。</remarks>
    public const double StatusContentFloor = 132;

    /// <summary>清单行两侧的内边距：列宽之和要放进内容盒，也就是行外框减去这两侧。</summary>
    /// <remarks>取值必须等于主题令牌 <c>PadRow</c>，两者是否还相等由 `RowPadding_MustMatchTheRowToken` 校验。</remarks>
    public const double RowPad = 12;

    /// <summary>
    /// 四列的默认宽度，按各列可能出现的最长文本确定：类型列最长「zstd」40px、
    /// 状态列最长「不会处理」40px（连图标与子项数共 132px，即 <see cref="StatusContentFloor"/>）。
    /// 1.1.0 那组 340/190/200/320 合计 1050，在基准窗口下会把状态列整个挤出可视区。
    /// </summary>
    public static double[] Default() => [56, 308, 170, 160];

    /// <summary>与 <see cref="Default"/> 同一组数的字面量，供建行处使用。</summary>
    public const string DefaultSpec = "56,308,170,160";

    /// <summary>每列的内容下限：只有状态列有实测依据，其余三列取 0，只由通用下限与表头需要宽兜住。</summary>
    /// <remarks>那三列的最长文本没有上界，设了下限列就再也收不回去；逐行现量只服务两个「适配」动作。</remarks>
    public static double[] ContentFloors() => [0, 0, 0, StatusContentFloor];

    /// <summary>每列下限：通用下限、该列内容下限、表头需要宽三者取大，是「每一列最小宽度」的唯一结论处。</summary>
    /// <remarks>表头需要宽量不到或量小了都不要紧：两项取大时会盖过它，写盘回读因此不依赖测量。</remarks>
    public static double[] Floors(IReadOnlyList<double>? headerNeed = null)
    {
        var floors = ContentFloors();
        for (var i = 0; i < Count; i++)
        {
            floors[i] = Math.Max(Math.Max(Min, floors[i]), Need(headerNeed, i));
        }

        return floors;
    }

    /// <summary>补齐条数、把脏数据换成默认值、按每列下限与上限夹回区间。</summary>
    public static double[] Normalize(IReadOnlyList<double>? widths, IReadOnlyList<double> floors)
    {
        var w = Sanitize(widths);
        for (var i = 0; i < Count; i++)
        {
            // 落到默认值那一支也要再过一次下限：下限更大时以下限为准，否则坏数据就是绕过下限的一种途径。
            w[i] = Clamp(w[i], floors[i]);
        }

        return w;
    }

    /// <summary>补齐条数、把脏数据换成默认值、夹到上限之内；下限留给调用方各自施加。</summary>
    private static double[] Sanitize(IReadOnlyList<double>? widths)
    {
        var fallback = Default();
        var w = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            var v = widths is not null && i < widths.Count ? widths[i] : fallback[i];
            w[i] = double.IsFinite(v) && v > 0 ? Math.Min(Max, v) : fallback[i];
        }

        return w;
    }

    /// <summary>把给定宽度按比例摊到内容盒宽；每列夹在自己的下限与上限之间，连下限之和都放不下时原样退回。</summary>
    /// <remarks>两个方向都按比例：只缩一侧会让宽度逐渐偏移到左侧几列，反复缩放窗口即可复现（1.1.0 的取值如此）。</remarks>
    public static double[] Fit(IReadOnlyList<double>? widths, double viewport, IReadOnlyList<double> floors)
    {
        var w = Normalize(widths, floors);
        var total = w.Sum();
        if (viewport < floors.Sum() || total <= 0 || Math.Abs(total - viewport) < 0.5)
        {
            return w;
        }

        var scale = viewport / total;
        var scaled = w.Select((v, i) => Clamp(v * scale, floors[i])).ToArray();

        // 被夹住的差额按从右往左的顺序补足，让总和精确落到内容盒宽。
        var rest = viewport - scaled.Sum();
        for (var i = Count - 1; i >= 0 && Math.Abs(rest) > 0.5; i--)
        {
            var add = rest > 0 ? Math.Min(Max - scaled[i], rest) : -Math.Min(scaled[i] - floors[i], -rest);
            scaled[i] += add;
            rest -= add;
        }

        return scaled;
    }

    /// <summary>第 index 列宽变化 delta：只与相邻列分摊（存在右邻则取右邻，否则取左邻），总宽不变。</summary>
    /// <remarks>邻列到达它自己的下限即夹住当前列；拖动只作用于这条线两边的两列，不波及其余列。</remarks>
    public static double[] Spread(IReadOnlyList<double> widths, int index, double delta, IReadOnlyList<double> floors)
    {
        var w = Normalize(widths, floors);
        if (index < 0 || index >= Count || Math.Abs(delta) < 0.5)
        {
            return w;
        }

        var before = w[index];
        w[index] = Clamp(before + delta, floors[index]);
        var rest = w[index] - before;

        var neighbour = index + 1 < Count ? index + 1 : index - 1;
        if (rest > 0)
        {
            var take = Math.Min(w[neighbour] - floors[neighbour], rest);
            w[neighbour] -= take;
            rest -= take;
        }
        else if (neighbour >= 0)
        {
            var give = Math.Min(Max - w[neighbour], -rest);
            w[neighbour] += give;
            rest += give;
        }

        if (Math.Abs(rest) >= 0.5)
        {
            w[index] = Clamp(w[index] - rest, floors[index]);
        }

        return w;
    }

    /// <summary>每列都不许低于自己的下限：不够的列从最宽的那一列划出。</summary>
    /// <remarks>表头需要宽与状态列的内容下限都已在 <see cref="Floors"/> 里合成一份，这里不再各自判定。</remarks>
    public static double[] EnsureHeaders(IReadOnlyList<double> widths, IReadOnlyList<double> floors)
    {
        var w = Sanitize(widths);
        var deficit = 0.0;
        for (var i = 0; i < Count; i++)
        {
            deficit += Math.Max(0, floors[i] - w[i]);
        }

        if (deficit <= 0.5)
        {
            return w;
        }

        var taken = 0.0;
        foreach (var i in Enumerable.Range(0, Count).OrderByDescending(i => w[i]))
        {
            if (taken >= deficit)
            {
                break;
            }

            var room = w[i] - floors[i];
            if (room <= 0)
            {
                continue;
            }

            var take = Math.Min(room, deficit - taken);
            w[i] -= take;
            taken += take;
        }

        if (taken <= 0.5)
        {
            return w;
        }

        for (var i = 0; i < Count && taken > 0.5; i++)
        {
            if (w[i] >= floors[i])
            {
                continue;
            }

            var give = Math.Min(floors[i] - w[i], taken);
            w[i] += give;
            taken -= give;
        }

        return w;
    }

    private static double Need(IReadOnlyList<double>? need, int i) =>
        need is not null && i < need.Count ? need[i] : 0;

    private static double Clamp(double v, double floor) => Math.Max(floor, Math.Min(Max, v));
}
