using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 判重的账：登记的账与「真的解出来了」的账是两件事，不许合并。
/// B-2026-311）。
/// </summary>
public sealed class PierceRegistryTests
{
    /// <summary>
    /// 「登记过」不等于「解出来了」：防死循环那本账在解压**之前**登记，它答不出内容解没解出来；
    /// 只有另记的那一本才答得出落点，而且答的是按包路径找的那一份。
    ///
    /// 这一条钉的是该缺陷的成因 —— 账里只有一个「已解过」的概念，于是跳过之后说不清内容到底解出来过没有。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-311")]
    public void SolvedAt_MustOnlyAnswerForTheSuccessfullyExtractedOne()
    {
        var registry = new PierceRegistry();
        var fingerprint = new Fingerprint(1024, "AA");

        Assert.Null(registry.SolvedAt(fingerprint, @"Z:\out\a.zip"));

        // 登记：防死循环那一位。它不许回答「解出来了」。
        registry.Register(fingerprint, @"Z:\out\a.zip");
        Assert.Null(registry.SolvedAt(fingerprint, @"Z:\out\a.zip"));

        // 解压成功、产物确定之后另记一笔，这才答得出落点。
        registry.NoteSolved(fingerprint, @"Z:\Out\A.ZIP", @"Z:\out\a");
        Assert.Equal(@"Z:\out\a", registry.SolvedAt(fingerprint, @"z:\out\a.zip"));

        // 指纹仍是收窄候选的第一道：换个指纹就查不到。
        Assert.Null(registry.SolvedAt(new Fingerprint(2048, "BB"), @"Z:\out\a.zip"));

        // 同一个指纹下的两份各答各的，而且同一份重复登记不产生第二条（后记的落点顶掉前一条）。
        registry.NoteSolved(fingerprint, @"Z:\out\b.zip", @"Z:\out\b");
        Assert.Equal(@"Z:\out\a", registry.SolvedAt(fingerprint, @"Z:\out\a.zip"));
        Assert.Equal(@"Z:\out\b", registry.SolvedAt(fingerprint, @"Z:\out\b.zip"));
        registry.NoteSolved(fingerprint, @"Z:\out\a.zip", @"Z:\out\a2");
        Assert.Equal(@"Z:\out\a2", registry.SolvedAt(fingerprint, @"Z:\out\a.zip"));
        Assert.Equal(@"Z:\out\b", registry.SolvedAt(fingerprint, @"Z:\out\b.zip"));
    }

    /// <summary>
    /// 分叉：登记表与落点两份各走各的，分叉之前登记的看得见、分叉之后互不可见；
    /// 「本进程删过哪些路径」那一份共用（它是进程的态，不是链的态）。
    /// </summary>
    [Fact]
    public void Fork_MustSplitBothAccountsAndShareTheRemovedList()
    {
        var root = new PierceRegistry();
        var earlier = new Fingerprint(11, "AA");
        var later = new Fingerprint(22, "BB");
        root.Register(earlier, @"Z:\out\earlier.zip");

        var left = root.Fork();
        var right = root.Fork();

        // 分叉拿的是此刻的账：分叉之前登记的那一份两边都看得见。
        Assert.Contains(@"Z:\out\earlier.zip", left.Registered(earlier));
        Assert.Contains(@"Z:\out\earlier.zip", right.Registered(earlier));

        // 分叉之后各记各的：兄弟不是彼此的祖先。
        left.Register(later, @"Z:\out\left.zip");
        Assert.Empty(right.Registered(later));
        Assert.Empty(root.Registered(later));

        left.NoteSolved(later, @"Z:\out\left.zip", @"Z:\out\left");
        Assert.Null(right.SolvedAt(later, @"Z:\out\left.zip"));
        Assert.Null(root.SolvedAt(later, @"Z:\out\left.zip"));

        // 删过的路径共用：对账时兄弟与根账都要认得出「是本进程删的」。
        left.NoteRemoved(@"Z:\out\gone.zip");
        Assert.True(right.WasRemovedByUs(@"Z:\out\gone.zip"));
        Assert.True(root.WasRemovedByUs(@"Z:\out\gone.zip"));
    }
}
