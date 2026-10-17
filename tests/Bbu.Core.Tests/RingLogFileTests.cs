using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bbu.Core.Logging;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 环形落盘层的判据：按需增长、回绕、写指针、残片、共享读回与两条错误路径。
/// 绕圈那一档靠 capacity 覆盖构造压到 4 KiB，不必真写 4 MiB。
/// </summary>
public sealed class RingLogFileTests
{
    private const string Name = "BullBullUnpacker.log";
    private const string First = "2026-10-15 10:00:00.000+08:00";
    private const long Small = 4096;

    /// <summary>新建的文件不预分配：长度是 0，写完一段才长到那一段的长度。</summary>
    /// <remarks>反向对照：构造里若 SetLength 到容量，头两条断言立刻红。</remarks>
    [Fact]
    public void FreshFile_MustStartEmptyWithoutPreallocation()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);

        var file = new RingLogFile(path);

        Assert.Null(file.LastError);
        Assert.True(File.Exists(path));
        Assert.Equal(0L, new FileInfo(path).Length);
        Assert.Equal(string.Empty, file.ReadAll());

        var line = Line(First, "第一行");
        file.WriteLine(line);

        Assert.Equal(Size(line) + 1, new FileInfo(path).Length);
        Assert.Single(Directory.GetFiles(directory.Path));
        file.Close();
    }

    /// <summary>写指针取时间戳最大的那一行之后，不是最后写进去的那一行之后：更旧的那条被盖掉。</summary>
    /// <remarks>反向对照：判据换成「跟着最后写的那条」时，新行会落在更旧的那条之后。</remarks>
    [Fact]
    public void WritePointer_MustFollowTheLatestStamp()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var newest = Line(First, "第一条");
        var older = Line("2026-10-15 09:00:00.000+08:00", "更旧的一条");
        var added = Line("2026-10-15 11:00:00.000+08:00", "新的一条");

        var file = new RingLogFile(path, Small);
        file.WriteLine(newest);
        file.WriteLine(older);
        file.Close();

        var again = new RingLogFile(path, Small);
        again.WriteLine(added);
        Assert.Equal(new[] { newest, added }, Lines(again.ReadAll()));
        again.Close();

        var data = RawBytes(path);
        Assert.Equal(Size(newest) + 1, OffsetOf(data, added));
        Assert.Equal(-1, OffsetOf(data, older));
    }

    /// <summary>同一毫秒并列时取写序上最后的那一条：并列的前一条不许被新写入盖掉。</summary>
    /// <remarks>反向对照：并列时若取靠前那条，新行会落在并列的第二条上，末条断言立刻红。</remarks>
    [Fact]
    public void TiedStamps_MustTakeTheLastOfTheRun()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var earlier = Line(First, "并列的第一条");
        var later = Line(First, "并列的第二条");
        var added = Line("2026-10-15 10:00:00.001+08:00", "稍后的一条");

        var file = new RingLogFile(path, Small);
        file.WriteLine(earlier);
        file.WriteLine(later);
        file.Close();

        var again = new RingLogFile(path, Small);
        again.WriteLine(added);
        again.Close();

        var data = RawBytes(path);
        Assert.Equal(Size(earlier) + Size(later) + 2, OffsetOf(data, added));
        Assert.NotEqual(-1, OffsetOf(data, earlier));
    }

    /// <summary>不带换行的尾部残片、不带时间戳的段都不算一行：写指针不跟着它们走。</summary>
    /// <remarks>反向对照：残片若也算一行，写指针会跟着 23:00 那条走。</remarks>
    [Fact]
    public void Fragments_MustNotMoveTheWritePointer()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var complete = Line(First, "完整行");
        var tail = Line("2026-10-15 23:00:00.000+08:00", "写到一半");
        var added = Line("2026-10-15 11:00:00.000+08:00", "接在后面的一条");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"{complete}\n{new string('.', 200)}\n{tail}"));

        var file = new RingLogFile(path, Small);
        file.WriteLine(added);
        var data = RawBytes(path);
        Assert.Equal(Size(complete) + 1, OffsetOf(data, added));
        // 残片还在盘上，只是不算一行：写指针越过它，读回也不带它
        Assert.NotEqual(-1, OffsetOf(data, tail));
        file.Close();

        var again = new RingLogFile(path, Small);
        Assert.Equal(new[] { complete, added }, Lines(again.ReadAll()));
        again.Close();
    }

    /// <summary>
    /// 写满两圈：长度先涨到容量、此后不再涨，早期那几行被盖掉、最后一条在，读回按时间序。
    /// 反向对照就是这两圈：实现若变成追加，长度会越过容量，早期行也不会消失。
    /// </summary>
    [Fact]
    public void Wrap_MustStopGrowingAtCapacityInTimeOrder()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var at = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(8));
        var width = 64 - 1 - Size(Line(First, string.Empty));
        var file = new RingLogFile(path, Small);

        for (var i = 0; i < 64; i++)
        {
            file.WriteLine(Line(Stamp(at.AddMilliseconds(1 + i)), Pad($"line-{i:0000}", width)));
        }

        Assert.Equal(Small, new FileInfo(path).Length);

        for (var i = 64; i < 130; i++)
        {
            file.WriteLine(Line(Stamp(at.AddMilliseconds(1 + i)), Pad($"line-{i:0000}", width)));
        }

        file.Close();

        Assert.Equal(Small, new FileInfo(path).Length);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.False(File.Exists(path + ".1"));

        var again = new RingLogFile(path, Small);
        var text = again.ReadAll();
        again.Close();

        Assert.DoesNotContain("line-0000", text);
        Assert.Contains("line-0129", text);
        Assert.False(text.Contains('\0'));
        var stamps = Lines(text).Select(line => line[..RingLogFile.StampWidth]).ToList();
        Assert.Equal(stamps.OrderBy(stamp => stamp, StringComparer.Ordinal), stamps);
    }

    /// <summary>回绕先在写指针处补一个换行再回头写：补的那个字节就是文件的最后一位。</summary>
    /// <remarks>反向对照：不补那个换行时长度是 4000 而不是 4001，头两条断言立刻红。</remarks>
    [Fact]
    public void Wrap_MustAlignTheLineBoundary()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var at = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(8));
        var width = 100 - 1 - Size(Line(First, string.Empty));
        var newest = Line(Stamp(at.AddMilliseconds(41)), Pad("line-0040", width));
        var file = new RingLogFile(path, Small);

        for (var i = 0; i < 41; i++)
        {
            file.WriteLine(Line(Stamp(at.AddMilliseconds(1 + i)), Pad($"line-{i:0000}", width)));
        }

        file.Close();

        var data = RawBytes(path);
        Assert.Equal(4001L, data.Length);
        Assert.Equal((byte)'\n', data[4000]);
        Assert.Equal(0, OffsetOf(data, newest));

        var again = new RingLogFile(path, Small);
        var text = again.ReadAll();
        again.Close();

        Assert.DoesNotContain("line-0000", text);
        Assert.Contains("line-0040", text);
    }

    /// <summary>默认容量那一档也按需增长：一行 64 KiB 写满 4 MiB 正好填平，再写不涨。</summary>
    /// <remarks>反向对照：容量常量若被改成别的数，长度那条断言就不会落在容量上。</remarks>
    [Fact]
    public void DefaultCapacity_MustFillExactlyAndStopGrowing()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var at = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(8));
        var width = (int)(RingLogFile.Capacity / 64) - 1 - Size(Line(First, string.Empty));
        var file = new RingLogFile(path);

        for (var i = 0; i < 66; i++)
        {
            file.WriteLine(Line(Stamp(at.AddMilliseconds(1 + i)), Pad($"line-{i:0000}", width)));
        }

        file.Close();

        Assert.Equal(RingLogFile.Capacity, new FileInfo(path).Length);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.False(File.Exists(path + ".1"));

        var data = RawBytes(path);
        Assert.Equal(-1, OffsetOf(data, "line-0000"));
        Assert.NotEqual(-1, OffsetOf(data, "line-0065"));
    }

    /// <summary>超过容量那一条顶到容量就不再长：写失败不发生、长度照旧、开头的时间戳还在。</summary>
    /// <remarks>反向对照：不截断时这一条写进容量之外，长度与 LastError 两条立刻红。</remarks>
    [Fact]
    public void OverlongLine_MustBeClampedInsteadOfFailing()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var huge = Line(First, new string('x', (int)Small + 904));

        var file = new RingLogFile(path, Small);
        file.WriteLine(huge);

        Assert.Null(file.LastError);
        Assert.Equal((int)Small, file.ReadAll().Length);
        file.Close();

        Assert.Equal(Small, new FileInfo(path).Length);
        var again = new RingLogFile(path, Small);
        Assert.Null(again.LastError);
        Assert.StartsWith($"[{First}]", again.ReadAll());
        again.Close();
    }

    /// <summary>落点建不出来时只置 LastError：构造与写入都不抛，读回是空串。</summary>
    /// <remarks>反向对照：构造若把异常放出来，这一条是错误而不是断言。</remarks>
    [Fact]
    public void UnusableLocation_MustReportInsteadOfThrowing()
    {
        using var directory = new TestDirectory();
        var blocker = directory.Combine("blocker");
        File.WriteAllText(blocker, "x");

        var file = new RingLogFile(Path.Combine(blocker, Name));

        Assert.NotNull(file.LastError);
        Assert.Equal(string.Empty, file.ReadAll());

        file.WriteLine(Line(First, "写不进去的一句"));

        Assert.NotNull(file.LastError);
        file.Close();
    }

    /// <summary>关掉之后写入只置 LastError、不抛，读回是空串；再关一次也不抛。</summary>
    /// <remarks>反向对照：Close 之后若照写不误，LastError 会是 null。</remarks>
    [Fact]
    public void Closed_MustReportInsteadOfThrowing()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var file = new RingLogFile(path, Small);
        file.WriteLine(Line(First, "关之前的一句"));
        file.Close();

        file.WriteLine(Line("2026-10-15 10:00:01.000+08:00", "关之后的一句"));

        Assert.NotNull(file.LastError);
        Assert.Equal(string.Empty, file.ReadAll());
        file.Close();
    }

    /// <summary>读回整个文件给测试与排障用：常开写句柄在场也读得到，口径与 ReadAll 一致。</summary>
    /// <remarks>反向对照：共享位少给一个（读/写/删任一）时这条当场抛，断言到不了。</remarks>
    [Fact]
    public void ReadText_MustReadWhileTheHandleStaysOpen()
    {
        using var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var first = Line(First, "第一行");
        var second = Line("2026-10-15 10:00:01.000+08:00", "第二行");
        var file = new RingLogFile(path, Small);
        file.WriteLine(first);
        file.WriteLine(second);

        Assert.Equal(file.ReadAll(), RingLogFile.ReadText(path));

        file.Close();

        Assert.Equal($"{first}\n{second}\n", RingLogFile.ReadText(path));
        Assert.Equal(string.Empty, RingLogFile.ReadText(path + ".缺失"));
    }

    /// <summary>常开句柄不挡清理：句柄还开着时它所在的那个目录删得掉。</summary>
    /// <remarks>反向对照：共享位不加删，删目录当场抛 IOException。</remarks>
    [Fact]
    public void OpenHandle_MustNotBlockDirectoryCleanup()
    {
        var directory = new TestDirectory();
        var path = directory.Combine(Name);
        var file = new RingLogFile(path, Small);
        file.WriteLine(Line(First, "留着句柄的一句"));

        Directory.Delete(directory.Path, recursive: true);

        Assert.False(Directory.Exists(directory.Path));
        file.Close();
        directory.Dispose();
    }

    /// <summary>三个常量是跨成员契约：改一个，按同一份签名并行编码的那两侧就对不上。</summary>
    [Fact]
    public void Constants_MustBeTheFrozenValues()
    {
        Assert.Equal(4L * 1024 * 1024, RingLogFile.Capacity);
        Assert.Equal(128 * 1024, RingLogFile.MaxLineBytes);
        Assert.Equal(31, RingLogFile.StampWidth);
    }

    /// <summary>行的形状只有一处：时间戳、级别、正文，与落盘格式逐字一致。</summary>
    private static string Line(string stamp, string body) => $"[{stamp}] [INFO] {body}";

    private static string Stamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static string Pad(string head, int width) => head.PadRight(width, '.');

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static int Size(string line) => Encoding.UTF8.GetByteCount(line);

    /// <summary>原始字节：常开写句柄在场也读得到，偏移断言靠它。</summary>
    private static byte[] RawBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static int OffsetOf(byte[] data, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i + needle.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
