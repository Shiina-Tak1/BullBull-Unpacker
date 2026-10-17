using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 产物上提与收壳：一条判据一条断言。
///
/// 这些断言直接驱动上提与收壳那三个函数，不经过解压 —— 它们的判据与「有没有解出包」无关，
/// 混在一次真解压里的话，红的时候看不出是哪一条坏了。
/// </summary>
[Collection("engine")]
public sealed class PierceFoldTests
{
    /// <summary>这一层解出来只有一个子目录时，把内容提到上一层并删掉空壳。</summary>
    [Fact]
    public void SingleFreshChild_MustBeLifted()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "content.txt"), "payload");

        var lifted = Piercer.FlattenSingleChild(temp.Combine("shell"), bornAfter: null, overwrite: false, mergeDirs: false);

        Assert.Equal("inner", lifted.Lifted);
        Assert.True(File.Exists(Path.Combine(shell, "content.txt")));
        Assert.False(Directory.Exists(inner));
    }

    /// <summary>
    /// 待搬的条目里只要有子目录，就得按类型分派着搬：<c>File.Move</c> 搬不了目录，
    /// 直译过来会让整层上提放弃。撤掉分派（改回裸 <c>File.Move</c>）这一条即红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-154")]
    public void SingleChildHoldingADirectory_MustBeLifted()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(Path.Combine(inner, "sub"));
        File.WriteAllText(Path.Combine(inner, "sub", "deep.txt"), "payload");

        var lifted = Piercer.FlattenSingleChild(shell, bornAfter: null, overwrite: false, mergeDirs: false);

        Assert.Equal("inner", lifted.Lifted);
        AssertEqualPayload(shell, "sub", "deep.txt");
        Assert.False(Directory.Exists(inner));
    }

    /// <summary>同名链里的每一层都含子目录时照样能一路提平（同一处分派，两条路径共用）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-154")]
    public void SameNameChainHoldingADirectory_MustCollapse()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var deep = Path.Combine(shell, "shell", "shell");
        Directory.CreateDirectory(Path.Combine(deep, "sub"));
        File.WriteAllText(Path.Combine(deep, "sub", "deep.txt"), "payload");

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MinFreeGb = 0 });

        var folded = piercer.FlattenSameNameShells(shell, bornAfter: null, overwrite: false);

        Assert.NotEmpty(folded.Lifted);
        Assert.True(File.Exists(Path.Combine(shell, "deep.txt")));
        Assert.False(Directory.Exists(Path.Combine(shell, "shell")));
    }

    /// <summary>收壳时待搬的是子目录，同样要能整块搬（收壳与上提共用那一处分派）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-154")]
    public void CollapseShell_MustLiftADirectory()
    {
        using var temp = new TestDirectory();
        var workdir = temp.Combine("work");
        var outdir = temp.Combine("work", "product");
        Directory.CreateDirectory(Path.Combine(outdir, "sub"));
        File.WriteAllText(Path.Combine(outdir, "sub", "deep.txt"), "payload");

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MinFreeGb = 0 });

        Assert.Equal(workdir, piercer.CollapseShell(workdir, outdir, bornAfter: null));
        AssertEqualPayload(workdir, "sub", "deep.txt");
        Assert.False(Directory.Exists(outdir));
    }

    /// <summary>有新文件时不上提：新解出来的东西不只有一个子目录。</summary>
    [Fact]
    public void FreshFileAlongsideTheChild_MustBlockTheLift()
    {
        using var temp = new TestDirectory();
        Directory.CreateDirectory(temp.Combine("shell", "inner"));
        File.WriteAllText(temp.Combine("shell", "extra.txt"), "x");

        Assert.Null(Piercer.FlattenSingleChild(
            temp.Combine("shell"), bornAfter: null, overwrite: false, mergeDirs: false).Lifted);
        Assert.True(Directory.Exists(temp.Combine("shell", "inner")));
    }

    /// <summary>
    /// 判据是「这一层解出来的东西」，不是「目录里现在有什么」：
    /// 解压前就在的旧目录不算，否则覆盖模式下永远收不了壳。
    /// </summary>
    [Fact]
    public void PreexistingChild_MustNotCountAsTheFreshOne()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        Directory.CreateDirectory(temp.Combine("shell", "old"));
        Directory.CreateDirectory(temp.Combine("shell", "fresh"));
        // 新目录里要有东西，否则「搬了零个条目」会被判成没搬动。
        File.WriteAllText(temp.Combine("shell", "fresh", "content.txt"), "payload");
        var before = new HashSet<string>(StringComparer.Ordinal) { "old" };

        var lifted = Piercer.FlattenSingleChild(shell, before, overwrite: false, mergeDirs: false);

        Assert.Equal("fresh", lifted.Lifted);
        Assert.True(Directory.Exists(temp.Combine("shell", "old")));
    }

    /// <summary>非覆盖模式遇到同名文件一律放弃上提：宁可不动，也不覆盖用户的东西。</summary>
    [Fact]
    public void SameNameWithoutOverwrite_MustGiveUp()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "same.txt"), "来自内层");
        File.WriteAllText(Path.Combine(shell, "same.txt"), "用户原有的");
        var before = new HashSet<string>(StringComparer.Ordinal) { "same.txt" };

        var lifted = Piercer.FlattenSingleChild(shell, before, overwrite: false, mergeDirs: false);

        Assert.Null(lifted.Lifted);
        Assert.Equal("用户原有的", File.ReadAllText(Path.Combine(shell, "same.txt")));
        Assert.Equal("来自内层", File.ReadAllText(Path.Combine(inner, "same.txt")));
    }

    /// <summary>覆盖模式允许替换同名文件。</summary>
    [Fact]
    public void SameNameWithOverwrite_MustReplace()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "same.txt"), "来自内层");
        File.WriteAllText(Path.Combine(shell, "same.txt"), "用户原有的");
        var before = new HashSet<string>(StringComparer.Ordinal) { "same.txt" };

        var lifted = Piercer.FlattenSingleChild(shell, before, overwrite: true, mergeDirs: false);

        Assert.Equal("inner", lifted.Lifted);
        Assert.Equal("来自内层", File.ReadAllText(Path.Combine(shell, "same.txt")));
    }

    /// <summary>
    /// 搬不动时一个字节都不动：先干跑一遍，绝不留下「半个目录」。
    /// 干跑这一条是必须的 —— 真搬是一边遍历一边搬的，失败时前面搬走的东西不会回来。
    /// </summary>
    [Fact]
    public void BlockedChild_MustLeaveEverythingUntouched()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "a.txt"), "a");
        File.WriteAllText(Path.Combine(inner, "blocked.txt"), "b");
        File.WriteAllText(Path.Combine(shell, "blocked.txt"), "用户原有的");

        Assert.False(Piercer.LiftChild(shell, inner, overwrite: false, mergeDirs: false).Ok);

        // 已经搬走的那一半必须还在原处：a.txt 不许因为 blocked.txt 而留在外面。
        Assert.True(File.Exists(Path.Combine(inner, "a.txt")), "干跑没挡住，留下了半个目录");
        Assert.False(File.Exists(Path.Combine(shell, "a.txt")));
    }

    /// <summary>两边都是目录时允许递归合并；只允许目录合并那一档遇到同名文件就放弃。</summary>
    [Fact]
    public void MergeDirs_MustRecurseButGiveUpOnSameNameFiles()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(Path.Combine(shell, "both"));
        Directory.CreateDirectory(Path.Combine(inner, "both"));
        File.WriteAllText(Path.Combine(shell, "both", "x.txt"), "外");
        File.WriteAllText(Path.Combine(inner, "both", "y.txt"), "内");

        Assert.True(Piercer.LiftChild(shell, inner, overwrite: false, mergeDirs: true).Ok);
        Assert.True(File.Exists(Path.Combine(shell, "both", "x.txt")));
        Assert.True(File.Exists(Path.Combine(shell, "both", "y.txt")));

        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(Path.Combine(inner, "both"));
        File.WriteAllText(Path.Combine(inner, "both", "x.txt"), "撞名");
        Assert.False(Piercer.LiftChild(shell, inner, overwrite: false, mergeDirs: true).Ok);
        Assert.Equal("外", File.ReadAllText(Path.Combine(shell, "both", "x.txt")));
    }

    /// <summary>
    /// 递归合并的深度上限是一票否决：撞上时整层不搬，而不是少搬几层。
    /// 撞上之后必须有一句日志，否则用户对着十几层同名目录不知道发生了什么。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-046"), Trait("Category", "ledger")]
    public void LiftDepthFuse_MustRefuseTheWholeLiftAndSaySo()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "shell");
        Directory.CreateDirectory(shell);
        Directory.CreateDirectory(inner);

        // 造一条比上限更深的同名链：每一层都只有一个同名子目录，且都与外层撞名。
        var cursor = inner;
        for (var index = 0; index < 20; index++)
        {
            cursor = Path.Combine(cursor, "shell");
            Directory.CreateDirectory(cursor);
        }

        File.WriteAllText(Path.Combine(cursor, "bottom.txt"), "最深处的载荷");

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var logs = new List<string>();
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) });

        var lifted = piercer.FlattenSameNameShells(shell, bornAfter: null, overwrite: false);

        Assert.Empty(lifted.Lifted);
        Assert.Equal(Piercer.LiftBlock.Depth, lifted.Block);
        Assert.True(File.Exists(Path.Combine(cursor, "bottom.txt")));
        Assert.Contains(logs, line => line.Contains("嵌套太深", StringComparison.Ordinal));
    }

    /// <summary>
    /// 挡住上提的是「同名且策略不许覆盖」时，告警要说这一件，不许一律报成「嵌套太深」（B-2026-155）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-155")]
    public void BlockedByConflict_MustNotBlameTheDepthFuse()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "shell");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "same.txt"), "内层");
        File.WriteAllText(Path.Combine(shell, "same.txt"), "用户原有的");
        var before = new HashSet<string>(StringComparer.Ordinal) { "same.txt" };

        var logs = new List<string>();
        var piercer = NewPiercer(logs);

        var folded = piercer.FlattenSameNameShells(shell, before, overwrite: false);

        Assert.Equal(Piercer.LiftBlock.Conflict, folded.Block);
        Assert.Contains(logs, line => line.Contains("不许覆盖", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("嵌套太深", StringComparison.Ordinal));
        Assert.Equal("用户原有的", File.ReadAllText(Path.Combine(shell, "same.txt")));
    }

    /// <summary>IO 失败同样各说各的，并且不许报成深度（B-2026-155）。</summary>
    [Fact]
    [Trait("Bug", "B-2026-155")]
    public void BlockedByIo_MustNotBlameTheDepthFuse()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "a.txt"), "a");
        var locked = Path.Combine(inner, "z_locked.txt");
        File.WriteAllText(locked, "b");
        var logs = new List<string>();
        var piercer = NewPiercer(logs);

        // 占用句柄不给「删除」共享：干跑看得见它，真搬时才会失败。
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var folded = piercer.FlattenSameNameShells(shell, bornAfter: null, overwrite: false);

            Assert.Equal(Piercer.LiftBlock.Io, folded.Block);
            Assert.Contains(logs, line => line.Contains("读写失败", StringComparison.Ordinal));

            // 告警里的内层名必须是真内层：半搬之后目录里已经多了刚搬走的条目，不能再靠 SoleChild 反推。
            Assert.Contains(logs, line => line.Contains("inner", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("嵌套太深", StringComparison.Ordinal));

            // 半搬可观测：被挡住时把已经搬走的条目如实带出来。
            Assert.Contains("a.txt", folded.Moved);
            Assert.True(File.Exists(Path.Combine(shell, "a.txt")), "已经搬走的那一项必须真的在新位置");
            Assert.True(File.Exists(locked), "搬不动的那一项必须还在原处");
        }
    }

    /// <summary>同名链一路提平：三层同名空壳拆成一层。</summary>
    [Fact]
    public void SameNameChain_MustCollapseToASingleLevel()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var deep = Path.Combine(shell, "shell", "shell");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "content.txt"), "payload");

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MinFreeGb = 0 });

        var lifted = piercer.FlattenSameNameShells(shell, bornAfter: null, overwrite: false);

        Assert.NotEmpty(lifted.Lifted);
        Assert.True(File.Exists(Path.Combine(shell, "content.txt")));
        Assert.False(Directory.Exists(Path.Combine(shell, "shell")));
    }

    /// <summary>第一次提不看名字：子目录不同名时只提这一层就停，但返回值里要如实包含它。</summary>
    /// <remarks>
    /// 这一情形是正常收口、不是被挡住：「不同名就停」不许报「嵌套太深」，那一句只留给跑满上限（B-2026-315）。
    /// </remarks>
    [Fact]
    [Trait("Bug", "B-2026-315")]
    public void FirstLiftIgnoresTheName_ButIsStillReported()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var inner = temp.Combine("shell", "other");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "content.txt"), "payload");

        var logs = new List<string>();
        var lifted = NewPiercer(logs).FlattenSameNameShells(shell, bornAfter: null, overwrite: false);

        Assert.Equal(["other"], lifted.Lifted);
        Assert.True(File.Exists(Path.Combine(shell, "content.txt")));
        Assert.Equal(Piercer.LiftBlock.None, lifted.Block);
        Assert.DoesNotContain(logs, line => line.Contains("嵌套太深", StringComparison.Ordinal));
    }

    /// <summary>
    /// 中间包收壳只在「产物就在这一层目录下」时动手：指定了输出目录那种不许动。
    /// </summary>
    [Fact]
    public void CollapseShell_MustNotTouchAProductOutsideTheWorkDirectory()
    {
        using var temp = new TestDirectory();
        var workdir = temp.Combine("work");
        var outdir = temp.Combine("elsewhere", "product");
        Directory.CreateDirectory(workdir);
        Directory.CreateDirectory(Path.Combine(outdir, "inner"));

        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(), vault, new PierceOptions { MinFreeGb = 0 });

        // 私有入口只做同一条判定，所以这里直接问「它会不会把 workdir 当成下一层」。
        Assert.Equal(outdir, piercer.CollapseShell(workdir, outdir, bornAfter: null));
        Assert.True(Directory.Exists(Path.Combine(outdir, "inner")));
    }

    /// <summary>
    /// 连提跑满上限也属「上提未完成」，必须如实报出来：报「没被挡住」会让这一层看起来什么事都没发生，
    /// 而用户面对的是十几层同名目录，且这一项状态是「完成」、再跑一次也不动（B-2026-312）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-312")]
    public void LiftLimitReached_MustBeReportedAsBlocked()
    {
        using var temp = new TestDirectory();
        var shell = temp.Combine("shell");
        var deep = shell;
        for (var round = 0; round < 10; round++)
        {
            deep = Path.Combine(deep, "shell");
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "content.txt"), "payload");

        var logs = new List<string>();
        var folded = NewPiercer(logs).FlattenSameNameShells(shell, bornAfter: null, overwrite: false);

        Assert.Equal(Piercer.LiftBlock.Depth, folded.Block);
        Assert.NotEmpty(folded.Lifted);
        Assert.Contains(
            logs,
            line => line.Contains(PierceText.Fill(PierceText.LiftFuseBlownAt, 8, "shell", "shell"), StringComparison.Ordinal));
    }

    private static Piercer NewPiercer(List<string> logs) => new(
        TestPaths.NewEngine(),
        SetUpVault(),
        new PierceOptions { MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) });

    private static Vault SetUpVault()
    {
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        return vault;
    }

    private static void AssertEqualPayload(string root, params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Assert.True(File.Exists(path), $"没提上来：{path}");
    }
}
