using System;
using System.IO;

namespace Bbu.Core.Services;

/// <summary>解压前按包的需求判磁盘余量的三种结论。</summary>
public enum DiskFloorVerdict
{
    /// <summary>够用，或者用户没设下限。</summary>
    Ok,

    /// <summary>这个包的声明体积加上余量下限会撑爆：不是「盘不够」而是「这个包太大」。</summary>
    NotEnoughForThisPack,

    /// <summary>判不出体积，退化成绝对下限：就是盘不够。</summary>
    BelowFloor,
}

/// <summary>
/// 磁盘余量的两个时刻。两条不许合并：
///
/// 事前那道按包**声明**的体积判，便宜，但声明值不可信，所以它只挡「误拖一个巨大的包」；
/// 事中那道在解压过程中看产物所在盘的**实际**剩余量，跌破同一个下限就中止，是真兜底。
///
/// 判据是「这个包要多大」：要求扣掉声明的解压体积之后仍不低于下限，因此余量够下限但不够这个包时
/// 照样拒绝。判不出体积（声明值为 0）时才退化成绝对下限。
/// </summary>
public static class DiskFloor
{
    /// <summary>
    /// 事前判定。<paramref name="minFreeBytes"/> 不大于 0 表示用户明确不管剩多少，直接放行；
    /// 盘读不到时同样放行，那种情况要由别的判据处理，不能在这里杀任务。
    /// </summary>
    public static DiskFloorVerdict Decide(long? freeBytes, long declaredBytes, long minFreeBytes)
    {
        if (minFreeBytes <= 0 || freeBytes is not long free)
        {
            return DiskFloorVerdict.Ok;
        }

        if (declaredBytes > 0)
        {
            // 先减后比：原来的 free < declaredBytes + minFreeBytes 在声明体积接近 long.MaxValue 时溢出成负、判定恒为 Ok（B-2026-203）。
            return free - minFreeBytes < declaredBytes
                ? DiskFloorVerdict.NotEnoughForThisPack
                : DiskFloorVerdict.Ok;
        }

        return free < minFreeBytes ? DiskFloorVerdict.BelowFloor : DiskFloorVerdict.Ok;
    }

    /// <summary>事中判定：产物所在盘的剩余量是不是跌破了同一条下限。</summary>
    public static bool BelowFloor(long? freeBytes, long minFreeBytes) =>
        minFreeBytes > 0 && freeBytes is long free && free < minFreeBytes;

    /// <summary>某个目录所在盘的可用余量。读不到时返回 <c>null</c>。</summary>
    public static long? FreeBytes(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
