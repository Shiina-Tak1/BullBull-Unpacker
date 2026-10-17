using System.Linq;
using Bbu.Ui.Avalonia.Views;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 列宽算法：拖动只作用于分界线两侧的两列，且每一列都不窄于自己的下限。
/// 旧实现会在邻列耗尽后继续压缩更远的列，表现即「拖一下，前面几列的宽度一起变」（看图反馈）。
/// </summary>
public sealed class ColumnLayoutTests
{
    /// <summary>拖第 1 列的边界：只有第 1、2 列变化，另外两列不动，总宽不变（B-2026-128）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-128")]
    public void Spread_MustOnlyTouchTheNeighbouringColumn()
    {
        var before = ColumnLayout.Default();
        var after = ColumnLayout.Spread(before, 1, 40, ColumnLayout.Floors());

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[3], after[3]);
        Assert.True(after[1] > before[1], "被拖的那一列没变宽");
        Assert.True(after[2] < before[2], "相邻列没让位");
        Assert.Equal(before.Sum(), after.Sum(), 3);
    }

    /// <summary>邻列到达自己的下限即夹住当前列，不侵占更远的列（B-2026-128）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-128")]
    public void Spread_MustStopAtTheNeighbourFloor()
    {
        var floors = ColumnLayout.Floors();
        var before = ColumnLayout.Default();
        var after = ColumnLayout.Spread(before, 1, 1000, floors);

        Assert.Equal(floors[2], after[2]);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[3], after[3]);
        Assert.Equal(before.Sum(), after.Sum(), 3);
    }

    /// <summary>
    /// 往右拖最右那条分界线就是压状态列：它停在自己的内容下限上，不会被压到通用下限（B-2026-310）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-310")]
    public void Spread_MustStopAtTheStatusFloor()
    {
        var floors = ColumnLayout.Floors();
        var after = ColumnLayout.Spread(ColumnLayout.Default(), 2, 10_000, floors);

        Assert.Equal(ColumnLayout.StatusContentFloor, floors[3]);
        Assert.Equal(floors[3], after[3]);
        Assert.True(after[3] > ColumnLayout.Min, "状态列被压到通用下限，内容下限没生效");
        Assert.Equal(ColumnLayout.Default().Sum(), after.Sum(), 3);
    }

    /// <summary>
    /// 写盘那份宽度读回时就要抬到下限上：窄值与脏值两条路都不许绕过它（B-2026-310）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-310")]
    public void Normalize_MustRaiseStoredWidthsToTheFloor()
    {
        var floors = ColumnLayout.Floors();

        var narrow = ColumnLayout.Normalize([10, 10, 10, 10], floors);
        for (var i = 0; i < ColumnLayout.Count; i++)
        {
            Assert.Equal(floors[i], narrow[i], 3);
        }

        var dirty = ColumnLayout.Normalize([0, double.NaN, -5, 0], floors);
        for (var i = 0; i < ColumnLayout.Count; i++)
        {
            Assert.True(dirty[i] >= floors[i], $"第 {i + 1} 列是 {dirty[i]:0.#}px，低于下限 {floors[i]:0.#}px");
        }

        Assert.True(dirty[3] >= ColumnLayout.StatusContentFloor, "状态列的脏数据支落到了内容下限以下");
    }

    /// <summary>写盘、读回、再拖：下限照样生效，不靠任何一次现量补上（B-2026-310）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-310")]
    public void NormalizeThenSpread_MustStillHoldTheStatusFloor()
    {
        var floors = ColumnLayout.Floors();
        var stored = ColumnLayout.Normalize([56, 56, 56, 56], floors);
        var after = ColumnLayout.Spread(stored, 2, 10_000, floors);

        Assert.Equal(ColumnLayout.StatusContentFloor, after[3]);
    }

    /// <summary>默认值本身不许低于下限：回读落到默认值那条路也不许绕过它。</summary>
    [Fact]
    public void Default_MustNotViolateTheFloors()
    {
        var floors = ColumnLayout.Floors();
        var defaults = ColumnLayout.Default();
        for (var i = 0; i < ColumnLayout.Count; i++)
        {
            Assert.True(
                defaults[i] >= floors[i],
                $"第 {i + 1} 列默认 {defaults[i]:0.#}px，低于下限 {floors[i]:0.#}px");
        }
    }

    /// <summary>下限取三者之大：表头需要宽比通用下限大时，下限跟着表头走。</summary>
    [Fact]
    public void Floors_MustNotBeSmallerThanTheHeaderNeed()
    {
        var need = new[] { 60.0, 300.0, 70.0, 80.0 };
        var floors = ColumnLayout.Floors(need);

        for (var i = 0; i < ColumnLayout.Count; i++)
        {
            Assert.True(
                floors[i] >= need[i],
                $"第 {i + 1} 列下限 {floors[i]:0.#}px 小于表头需要的 {need[i]:0.#}px");
            Assert.True(floors[i] >= ColumnLayout.Min, $"第 {i + 1} 列下限低于通用下限");
        }

        Assert.Equal(ColumnLayout.StatusContentFloor, floors[3], 3);
    }

    /// <summary>某列低于自己的下限时，从最宽的那一列划出给它，总宽不变。</summary>
    [Fact]
    public void EnsureHeaders_MustRaiseTheColumnBelowItsFloor()
    {
        var floors = ColumnLayout.Floors();
        var before = new double[] { 200, 100, 100, 60 };
        var after = ColumnLayout.EnsureHeaders(before, floors);

        Assert.True(
            after[3] >= floors[3],
            $"状态列 {after[3]:0.#}px 仍低于下限 {floors[3]:0.#}px");
        Assert.Equal(before.Sum(), after.Sum(), 3);
        Assert.True(after[0] < before[0], "最宽的那一列没让位");
    }

    /// <summary>一条都量不到时下限仍成立：通用下限加状态列内容下限这两条静态下限。</summary>
    [Fact]
    public void Floors_MustHoldWithoutAnyMeasurement()
    {
        var floors = ColumnLayout.Floors();

        Assert.Equal(ColumnLayout.Min, floors[0], 3);
        Assert.Equal(ColumnLayout.StatusContentFloor, floors[3], 3);
    }
}
