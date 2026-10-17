using System;
using Bbu.Core.Configuration;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 破坏性设置项的问句判据：删除原压缩包与切到覆盖，都只在「从别的档切过来」那一次问。
/// 问错方向都是坏结果：少问一次是用户的包被永久删掉，多问一次只是多一次打扰。
/// </summary>
public sealed class DestructiveAskTests
{
    /// <summary>从关拨到开问一次；本来就在关、本来就在开、从开拨回关，都不问。</summary>
    [Fact]
    [Trait("Bug", "B-2026-224")]
    public void RemovingTheSource_MustAskOnlyOnTheWayUp()
    {
        Assert.True(DestructiveAsk.BeforeRemovingSource(current: false, requested: true));

        Assert.False(DestructiveAsk.BeforeRemovingSource(false, false));
        Assert.False(DestructiveAsk.BeforeRemovingSource(true, true));
        Assert.False(DestructiveAsk.BeforeRemovingSource(true, false));
    }

    /// <summary>「重名时」只在对准覆盖那一次问：已经在覆盖档再保存不问第二遍，从覆盖档切走也不问。</summary>
    [Fact]
    [Trait("Bug", "B-2026-225")]
    public void Overwrite_MustAskOnlyWhenCrossingIntoIt()
    {
        Assert.True(DestructiveAsk.BeforeOverwriting(ConflictPolicy.Rename, ConflictPolicy.Overwrite));
        Assert.True(DestructiveAsk.BeforeOverwriting(ConflictPolicy.Skip, ConflictPolicy.Overwrite));

        Assert.False(DestructiveAsk.BeforeOverwriting(ConflictPolicy.Overwrite, ConflictPolicy.Overwrite));
        Assert.False(DestructiveAsk.BeforeOverwriting(ConflictPolicy.Overwrite, ConflictPolicy.Rename));
        Assert.False(DestructiveAsk.BeforeOverwriting(ConflictPolicy.Rename, ConflictPolicy.Skip));
    }

    /// <summary>两段确认正文各自要说全它那几件事：少一件，用户就不知道自己答应的是什么。</summary>
    [Fact]
    [Trait("Bug", "B-2026-224"), Trait("Bug", "B-2026-225")]
    public void AskBodies_MustCarryTheScopeAndThatItIsNotReversible()
    {
        Assert.Contains("回收站", Ui.RemoveSourceAskBody, StringComparison.Ordinal);
        Assert.Contains("分卷", Ui.RemoveSourceAskBody, StringComparison.Ordinal);
        Assert.Contains("整组", Ui.RemoveSourceAskBody, StringComparison.Ordinal);
        Assert.Contains("无法恢复", Ui.RemoveSourceAskBody, StringComparison.Ordinal);

        Assert.Contains("替换", Ui.ConflictOverwriteAskBody, StringComparison.Ordinal);
        Assert.Contains("无法恢复", Ui.ConflictOverwriteAskBody, StringComparison.Ordinal);
        Assert.Contains("保留", Ui.ConflictOverwriteAskBody, StringComparison.Ordinal);
    }
}
