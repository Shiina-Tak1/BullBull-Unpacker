using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 跨语言对拍：拿同一批嵌套夹具跑 C# 版，把产物树留在固定落点，交给外部的对拍脚本
/// 与 Python 版 1.1.0 的产物逐路径、逐字节比。
///
/// 为什么产物要落到仓外的 <c>.scratch</c>：脚本读的是真实字节，不经过测试运行器的输出目录。
/// 为什么单独一条档：整批跑一遍要几十秒，而它只在改判据或发版前跑。
///
/// </summary>
[Trait("Category", "machine")]
[Collection("engine")]
public sealed class PierceParityTests
{
    /// <summary>对拍两侧共用的参数：层数用默认值，磁盘下限关掉（两台机器余量不同会造成假差异）。</summary>
    private const int ParityMaxDepth = 5;

    /// <summary>每一份样本跑一遍，把产物树留下；再核一次「命令行口径」的结论。</summary>
    [Theory]
    [InlineData("chain-1", true)]
    [InlineData("chain-2", true)]
    [InlineData("chain-3", true)]
    [InlineData("deep-50", false)]
    [InlineData("disguised", true)]
    [InlineData("fingerprint", true)]
    [InlineData("fold-single", true)]
    [InlineData("same-level", false)]
    [InlineData("traversal", true)]
    public async Task Sample_MustProduceTheSameTreeAs_ThePythonVersion(string sample, bool expectedDone)
    {
        var output = ParityRoot();
        var work = Path.Combine(output, "work", sample);
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        CopyTree(Path.Combine(TestPaths.PierceRoot, sample), work);
        var entry = EntryOf(work);

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions
            {
                MaxDepth = ParityMaxDepth,
                MinFreeGb = 0,
                Log = new ChannelLog((_, message) => logs.Add(message)),
            });
        var result = await piercer.RunAsync(entry);

        Assert.Equal(expectedDone, Runner.IsDone(result));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(work));
        WriteReport(sample, result, Runner.IsDone(result));
    }

    private static string ParityRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Bbu.slnx")))
        {
            root = root.Parent;
        }

        var output = Path.Combine(root!.FullName, ".scratch", "parity", "cs-out");
        Directory.CreateDirectory(Path.Combine(output, "work"));
        return output;
    }

    /// <summary>样本的入口：里面恰好一个文件或一个目录时用它，否则用目录本身。</summary>
    private static string EntryOf(string directory)
    {
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        return entries.Count == 1 ? entries[0].FullName : directory;
    }

    /// <summary>把 C# 侧每一份样本的结论补进报告，供对拍脚本一并展示。</summary>
    private static void WriteReport(string sample, PierceResult result, bool isDone)
    {
        var path = Path.Combine(ParityRoot(), "report.cs.json");
        var table = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, ParityEntry>>(File.ReadAllText(path)) ?? []
            : [];
        table[sample] = new ParityEntry(
            isDone ? 0 : 1,
            result.StopReason.ToString(),
            result.Ok,
            result.Partial,
            result.MaxDepthReached,
            Path.GetFileName(result.OutputDir));
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ParityEntry(
        int Exit, string StopReason, bool Ok, bool Partial, int Layers, string OutputDir);

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
