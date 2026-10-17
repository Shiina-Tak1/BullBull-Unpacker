using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;
using Xunit.Abstractions;

namespace Bbu.Core.Tests;

/// <summary>
/// 真机档：按条删除密码本的耗时。跑一次把曲线打出来，用来定位「条数越多单条越慢」那条路径 ——
/// 改动前后的两份曲线才是依据，结论不在这里给。
///
/// 两条口径并排：逐条（每删一条各自落一次盘）与批量（整批只落一次盘）。
///
/// 它**不写断言**：测量不是判据。
/// 两个报告落点见 <see cref="CurvePath"/> 与 <see cref="WholePath"/>，各有环境变量可改。
/// </summary>
[Trait("Category", "machine")]
public sealed class PasswordBookMeasureTests
{
    /// <summary>曲线看的条数。</summary>
    private static readonly int[] Sizes = [1_000, 10_000, 30_000, 100_000];

    /// <summary>每个条数上的取样条数：够小，10 万那一点也跑得动。</summary>
    private const int Sample = 20;

    /// <summary>验收点看的条数。</summary>
    private const int Acceptance = 100_000;

    private readonly ITestOutputHelper _output;

    public PasswordBookMeasureTests(ITestOutputHelper output) => _output = output;

    /// <summary>① ② 两张曲线的落点。</summary>
    internal static string CurvePath =>
        Environment.GetEnvironmentVariable("BBU_BOOK_DELETE_CURVE_OUT")
        ?? Path.Combine(TestPaths.Root, ".scratch", "perf", "book-delete-curve.txt");

    /// <summary>验收点（整本删空）的落点。</summary>
    internal static string WholePath =>
        Environment.GetEnvironmentVariable("BBU_BOOK_DELETE_WHOLE_OUT")
        ?? Path.Combine(TestPaths.Root, ".scratch", "perf", "book-delete-whole.txt");

    [Fact]
    public void DeleteCost_MustBeMeasuredByBookSizeAndBatch()
    {
        var report = new StringBuilder();

        void Say(string text = "")
        {
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        Say("==============================================================================");
        Say($"密码本按条删除耗时   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"机器 {Environment.ProcessorCount} 逻辑核   账号列宽 pw-000000   条数一路到 {Acceptance:N0}");
        Say("==============================================================================");

        // ① 单条成本随条数怎么走。逐条那条路每删一条都要整本落盘，这个数随条数线性涨；
        //    批量那条路整批只落一次盘，所以它随条数几乎不动。
        Say();
        Say($"== 1. 每本删 {Sample} 条（条数递增；逐条与批量各用一本新本）==");
        Say("  条数            逐条共 ms  逐条单条 ms    批量共 ms  批量单条 ms");
        foreach (var size in Sizes)
        {
            var (byOne, oneDirectory) = MakeBook(size);
            var (atOnce, onceDirectory) = MakeBook(size);
            try
            {
                var (seqTotal, seqPer) = DeleteOneByOne(byOne, size, Sample);
                var (allTotal, allPer) = DeleteAtOnce(atOnce, size, Sample);
                Say($"  {size,-12:N0} {seqTotal,9:F1} {seqPer,12:F2} {allTotal,10:F1} {allPer,12:F2}");
            }
            finally
            {
                Clean(oneDirectory);
                Clean(onceDirectory);
            }
        }

        // ② 同一本书里删得越多：逐条那条路的单条成本恒定，批量那条路随批大小下降（一次落盘分摊到更多条上）。
        Say();
        Say($"== 2. {Acceptance:N0} 条的本书里删 1 / 5 / 20 / 100 条（每格一本新本）==");
        Say("  删几条        逐条共 ms  逐条单条 ms    批量共 ms  批量单条 ms");
        foreach (var batch in new[] { 1, 5, 20, 100 })
        {
            var (byOne, oneDirectory) = MakeBook(Acceptance);
            var (atOnce, onceDirectory) = MakeBook(Acceptance);
            try
            {
                var (seqTotal, seqPer) = DeleteOneByOne(byOne, Acceptance, batch);
                var (allTotal, allPer) = DeleteAtOnce(atOnce, Acceptance, batch);
                Say($"  {batch,-10} {seqTotal,9:F1} {seqPer,12:F2} {allTotal,10:F1} {allPer,12:F2}");
            }
            finally
            {
                Clean(oneDirectory);
                Clean(onceDirectory);
            }
        }

        Write(CurvePath, report);
        _output.WriteLine($"报告落 {CurvePath}");
    }

    [Fact]
    public void WholeBook_MustBeDeletedAndTimed()
    {
        var report = new StringBuilder();

        void Say(string text = "")
        {
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        var (vault, directory) = MakeBook(Acceptance);
        try
        {
            Say("==============================================================================");
            Say($"密码本整本删空（一次批量）   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Say($"机器 {Environment.ProcessorCount} 逻辑核   {Acceptance:N0} 条");
            Say("==============================================================================");

            var saves = vault.Saves;
            var watch = Stopwatch.StartNew();
            var removed = vault.RemoveMany(Passwords(Acceptance));
            watch.Stop();

            var seconds = watch.Elapsed.TotalSeconds;
            Say($"删掉 {removed:N0} 条，共 {seconds:N2} 秒"
                + $"（单条 {watch.Elapsed.TotalMilliseconds / Math.Max(removed, 1):F3} ms）");
            Say($"这一轮落盘 {vault.Saves - saves} 次；剩下的条数 {vault.Entries.Count:N0}");
        }
        finally
        {
            Clean(directory);
        }

        Write(WholePath, report);
        _output.WriteLine($"报告落 {WholePath}");
    }

    /// <summary>造一本 <paramref name="count"/> 条的本；只落一次盘，造本的开销不进曲线。</summary>
    private static (Vault Vault, string Directory) MakeBook(int count)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bbu-book-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vault = new Vault { BookPath = Path.Combine(directory, VaultText.BookFileName) };
        vault.AddMany(Passwords(count));
        return (vault, directory);
    }

    private static IEnumerable<string> Passwords(int count) =>
        Enumerable.Range(0, count).Select(i => $"pw-{i:000000}");

    /// <summary>逐条删：每删一条各自落一次盘。返回总耗时与单条平均。</summary>
    private static (double Total, double Per) DeleteOneByOne(Vault vault, int size, int batch)
    {
        var watch = Stopwatch.StartNew();
        foreach (var password in Passwords(size).Take(batch))
        {
            vault.Remove(password);
        }

        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds / Math.Max(batch, 1));
    }

    /// <summary>一次交给密码层整批删：整批只落一次盘。返回总耗时与单条平均。</summary>
    private static (double Total, double Per) DeleteAtOnce(Vault vault, int size, int batch)
    {
        var watch = Stopwatch.StartNew();
        vault.RemoveMany(Passwords(size).Take(batch));
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds / Math.Max(batch, 1));
    }

    private static void Write(string path, StringBuilder report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
    }

    private static void Clean(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测量结论。
        }
    }
}
