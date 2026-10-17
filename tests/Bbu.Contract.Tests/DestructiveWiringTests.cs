using System;
using System.IO;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 破坏性操作的接线形态。判据本体在 <c>DestructiveAsk</c>（单测见 <c>Bbu.Core.Tests. </c>），
/// 这一份钉「调用点没有绕过它」：只留单测的话，把闸门从界面上撤掉仍然全绿，
/// 而开关一拨就永久删包（B-2026-224 / 225）。
///
/// 真窗口那一层（确认框弹出来、点否定之后开关的视觉回退）归 <c>machine</c> 档，不在本类覆盖面内。
/// </summary>
public sealed class DestructiveWiringTests
{
    /// <summary>
    /// 设置页那个开关拨到开必须过闸门：闸门那一支只把改动交给问句、就地收口、一个字都不落盘；
    /// 否定分支要把开关拨回关闭，而且排在落盘那条路之前。撤掉闸门（直接写值）即红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-224")]
    public void RemoveSourceToggle_MustGoThroughTheGate()
    {
        var page = Source("Bbu.Ui.Avalonia/Views/SettingsPage.axaml.cs");
        var gate = page.IndexOf("DestructiveAsk.BeforeRemovingSource(", StringComparison.Ordinal);
        Assert.True(gate >= 0, "设置页没有拨到开那一次的闸门：开关一勾就直接写值");

        // 窗口只取闸门那一支，到它后面第一次落盘为止。
        var end = page.IndexOf("Push();", gate, StringComparison.Ordinal);
        var branch = end > gate ? page[gate..end] : page[gate..];
        Assert.Contains("ConfirmRemoveSourceAsync(toggle)", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("Push();", branch, StringComparison.Ordinal);

        var answer = Method(page, "private async Task ConfirmRemoveSourceAsync(");
        var revert = answer.IndexOf("toggle.Value = false;", StringComparison.Ordinal);
        Assert.True(revert >= 0, "问句的否定分支没有把开关拨回关闭");
        Assert.True(answer.IndexOf("Push();", StringComparison.Ordinal) > revert, "否定分支排在了落盘之后");
    }

    /// <summary>闸门要能被问到：设置页把问句交给宿主接上来的那一条通道，所以主窗口那一条接线不能缺。</summary>
    [Fact]
    [Trait("Bug", "B-2026-224")]
    public void SettingsConfirmChannel_MustBeWiredByTheHost()
    {
        var window = Source("Bbu.Ui.Avalonia/Views/MainWindow.axaml.cs");
        var wiring = window.IndexOf("SetPage.Confirm =", StringComparison.Ordinal);
        Assert.True(wiring >= 0, "主窗口没有把设置页的问句通道接到确认框上");

        // 只看这一条赋值语句本身：文件里别处的 Ask.AskAsync 调用与它无关。
        Assert.Contains("Ask.AskAsync", window[wiring..window.IndexOf(';', wiring)], StringComparison.Ordinal);
    }

    /// <summary>
    /// 底栏那个「重名时」切到覆盖必须过闸门，而且问在写之前：先写进配置再问，用户点否定时那一次改动已经在盘上了。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-225")]
    public void OverwriteChoice_MustAskBeforeItWrites()
    {
        var body = Method(Source("Bbu.Ui.Avalonia/Views/MainWindow.axaml.cs"), "private async Task ApplyOutSettingsAsync(");
        var ask = body.IndexOf("DestructiveAsk.BeforeOverwriting(", StringComparison.Ordinal);
        Assert.True(ask >= 0, "输出设置保存那条路上没有覆盖闸门：切到覆盖并保存是零确认的");

        var write = body.IndexOf("_config.Conflict =", StringComparison.Ordinal);
        Assert.True(write > ask, "闸门排在了写配置之后：问之前那一次改动就已经落到盘上了");
    }

    /// <summary>取一个方法的正文：从它的声明到下一个 <c>private</c> 成员为止。</summary>
    private static string Method(string text, string signature)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"源码里找不到 {signature}");

        var next = text.IndexOf("\n    private ", at, StringComparison.Ordinal);
        return next > at ? text[at..next] : text[at..];
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(Repo.Src, relative.Replace('/', Path.DirectorySeparatorChar)));
}
