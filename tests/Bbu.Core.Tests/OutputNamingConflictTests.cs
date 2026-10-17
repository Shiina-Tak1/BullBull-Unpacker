using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 档：重名冲突三档与无扩展名包错开。
///
/// 这一层破了用户是静默受损：选了「跳过」照样解、每跑一次多一份 <c>(1) (2)</c>，
/// 或者日志里的目录名与盘上真实目录名不一致，用户照着日志去找什么也找不到。
/// </summary>
[Trait("Category", "ledger")]
public sealed class OutputNamingConflictTests
{
    /// <summary>
    /// 判据 5（B-2026-003）：无扩展名的包要加 <c>_unpack</c> 错开。
    /// 「包名恰好等于会生成的目录名」正是这一档：不错开的话 <c>skip</c> 永远挡不住，
    /// 而且每跑一次多一份。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-003")]
    public void NoExtensionPackage_MustBeOffsetByTheUnpackSuffix()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("无扩展名包");
        File.WriteAllText(archive, string.Empty);

        var naming = OutputNaming.Resolve(new OutputDirRequest(archive));
        var leaf = Path.GetFileName(naming.Directory);

        Assert.NotEqual(Path.GetFileName(archive), leaf);
        Assert.Equal("无扩展名包_unpack", leaf);
    }

    /// <summary>
    /// 判据 6（B-2026-088）：日志里写出的目录名与盘上真实目录名逐字一致。
    ///
    /// 反向对照：日志那行必须唯一指向 <c>Resolve</c> 自己发的那句（以 <c>无扩展名：</c> 开头）。
    /// 退回 <c>stem + _unpack</c> 的写法会打出 <c>X_unpack_unpack/</c>，而盘上没有那个目录。
    /// 宽松子串的写法指不到唯一一行，就是绿的也不算验过。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-088")]
    public void NoExtensionLog_MustNameTheDirectoryThatReallyExists()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("无扩展名包");
        File.WriteAllText(archive, string.Empty);

        var levels = new List<LogLevel>();
        var lines = new List<string>();
        var naming = OutputNaming.Resolve(
            new OutputDirRequest(archive),
            new ChannelLog((level, message) =>
            {
                levels.Add(level);
                lines.Add(message);
            }));
        var line = Assert.Single(lines, l => l.StartsWith("无扩展名：", StringComparison.Ordinal));

        Assert.DoesNotContain("_unpack_unpack", Text(lines), StringComparison.Ordinal);
        Assert.EndsWith("改名为 无扩展名包_unpack/", line, StringComparison.Ordinal);

        Directory.CreateDirectory(naming.Directory!);
        var made = Directory.EnumerateDirectories(dir.Path).Select(p => Path.GetFileName(p)!).ToArray();
        Assert.Equal(["无扩展名包_unpack"], made);
        Assert.EndsWith("改名为 " + made[0] + "/", line, StringComparison.Ordinal);

        // 级别由这一层的入口名承载：这一句走 Note，替身收到的那一档就得是 Info。
        Assert.Equal(LogLevel.Info, Assert.Single(levels));
    }

    /// <summary>把命名层交上来的行拼成一段文本，供"整段里不该出现什么"这类断言用。</summary>
    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines);

    /// <summary>把命名层落下来的行收起来：并通道之后本层自己按入口命名，断言改看这个收集器。</summary>
    private static (OutputDirResult Result, List<string> Lines) ResolveWithLog(OutputDirRequest request)
    {
        var lines = new List<string>();
        var result = OutputNaming.Resolve(request, new ChannelLog((_, message) => lines.Add(message)));
        return (result, lines);
    }

    /// <summary>判据 7（B-2026-003）：已有同名目录时，<c>rename</c> 出 <c>x (1)</c>、<c>overwrite</c> 照解。</summary>
    [Fact]
    [Trait("Bug", "B-2026-003")]
    public void RenameAndOverwrite_MustAnswerDifferentlyOnAnExistingDirectory()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        Directory.CreateDirectory(dir.Combine("x"));

        var renamed = OutputNaming.Resolve(new OutputDirRequest(archive, Conflict: ConflictPolicy.Rename));
        var overwritten = OutputNaming.Resolve(new OutputDirRequest(archive, Conflict: ConflictPolicy.Overwrite));

        Assert.Equal("x (1)", Path.GetFileName(renamed.Directory));
        Assert.Equal("x", Path.GetFileName(overwritten.Directory));
        Assert.False(renamed.Skipped);
        Assert.False(overwritten.Skipped);
    }

    /// <summary>判据 7 的第三档：<c>skip</c> 要返回「已跳过」，而且一个字都不落盘。</summary>
    [Fact]
    [Trait("Bug", "B-2026-003")]
    public void Skip_MustStopAndSaySoOnAnExistingDirectory()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        Directory.CreateDirectory(dir.Combine("x"));

        var (naming, lines) = ResolveWithLog(new OutputDirRequest(archive, Conflict: ConflictPolicy.Skip));

        Assert.True(naming.Skipped);
        Assert.Null(naming.Directory);
        Assert.Equal(dir.Combine("x"), naming.BlockedBy);
        Assert.Contains("已跳过", Assert.Single(lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// 判据 8（B-2026-003）：同名的是个文件而不是目录时，<c>skip</c> 也挡得住。
    ///
    /// 这条断言同时是顺序的反向对照：把「先判 skip」换成「先改名」，改名后的路径是全新的，
    /// <c>skip</c> 那一档再也进不去 —— 结果会变成解出 <c>x (1)</c>，这里三条断言全红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-003")]
    public void Skip_MustAlsoBlockWhenTheNameBelongsToAFile()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        File.WriteAllText(dir.Combine("x"), "占位文件");

        var (naming, lines) = ResolveWithLog(new OutputDirRequest(archive, Conflict: ConflictPolicy.Skip));

        Assert.True(naming.Skipped);
        Assert.Null(naming.Directory);
        Assert.DoesNotContain(lines, l => l.Contains("已重命名", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(dir.Path));
    }

    /// <summary>同名的是文件时，另外两档照解 —— 对照组：skip 那一档的处理不得牵连这两档。</summary>
    [Theory]
    [InlineData(ConflictPolicy.Rename)]
    [InlineData(ConflictPolicy.Overwrite)]
    [Trait("Bug", "B-2026-003")]
    public void RenameAndOverwrite_MustStillExtractWhenTheNameBelongsToAFile(ConflictPolicy policy)
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        File.WriteAllText(dir.Combine("x"), "占位文件");

        var (naming, lines) = ResolveWithLog(new OutputDirRequest(archive, Conflict: policy));

        Assert.Equal("x (1)", Path.GetFileName(naming.Directory));
        Assert.Contains("已重命名", Assert.Single(lines), StringComparison.Ordinal);
    }

    /// <summary>序号要一直加下去，撞上 <c>x (1)</c> 就得给 <c>x (2)</c>。</summary>
    [Fact]
    [Trait("Bug", "B-2026-003")]
    public void Rename_MustKeepCountingUntilTheNameIsFree()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        Directory.CreateDirectory(dir.Combine("x"));
        Directory.CreateDirectory(dir.Combine("x (1)"));

        var naming = OutputNaming.Resolve(new OutputDirRequest(archive, Conflict: ConflictPolicy.Rename));

        Assert.Equal("x (2)", Path.GetFileName(naming.Directory));
    }

    /// <summary>
    /// B-2026-202：同名候选堆到上界之后回落成带随机串的名字，既不撞既有、也不再无限数序号。
    ///
    /// 两条断言要一起钉：只钉「不冲突」的话，一个继续数到第 1001 个的实现照样是绿的。
    /// 对照取「同一处的另一种错法」——撤掉上界不会挂住，而是返回 <c>x (1001)</c>，第二条即红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-202")]
    public void Rename_MustFallBackInsteadOfCountingForever()
    {
        using var dir = new TestDirectory();
        var archive = dir.Combine("x.zip");
        File.WriteAllText(archive, string.Empty);
        Directory.CreateDirectory(dir.Combine("x"));
        for (var i = 1; i <= 1000; i++)
        {
            Directory.CreateDirectory(dir.Combine($"x ({i})"));
        }

        var naming = OutputNaming.Resolve(new OutputDirRequest(archive, Conflict: ConflictPolicy.Rename));
        var leaf = Path.GetFileName(naming.Directory);
        var full = naming.Directory!;

        Assert.False(Directory.Exists(full) || File.Exists(full), $"回落名撞上了既有的：「{leaf}」");
        Assert.DoesNotMatch(@"^x \(\d+\)$", leaf);
    }
}
