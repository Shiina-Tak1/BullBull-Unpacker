using System;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 两处拼出来的整行：输出设置保存失败的提示条、跑批顶层异常那一行。断的是用户看到的那一行 ——
/// 有中文归类、原文不吞、末尾带日志去向，不是非空（放宽成非空的话，裸异常消息与裸存储错误都能过）。
/// 两处调用点都在真窗口里（machine 档跑不动），所以把拼装抽成纯函数钉在这里。
/// </summary>
public sealed class TextLineTests
{
    /// <summary>保存失败提示条：以「保存失败：」开头，存储层原文一字不动地跟在后面。</summary>
    [Fact]
    [Trait("Bug", "B-2026-220")]
    public void SettingsSaveFailure_MustCarryThePrefixAndTheStoreReason()
    {
        const string reason = "对路径 Z:\\bbu\\config.json 的访问被拒绝";

        var line = Ui.SettingsSaveFailed(reason);

        Assert.StartsWith("保存失败：", line, StringComparison.Ordinal);
        Assert.EndsWith(reason, line, StringComparison.Ordinal);
    }

    /// <summary>存储层没给出原因时前缀也在：提示条不许是一条空消息。</summary>
    [Fact]
    [Trait("Bug", "B-2026-220")]
    public void SettingsSaveFailure_WithoutAStoredReason_MustKeepThePrefix()
    {
        Assert.StartsWith("保存失败：", Ui.SettingsSaveFailed(null), StringComparison.Ordinal);
    }

    /// <summary>跑批顶层异常那一行：以「处理这个包时出错：」开头、异常原文夹在中间、以日志去向结尾。</summary>
    [Fact]
    [Trait("Bug", "B-2026-223")]
    public void BatchFatalLine_MustCarryThePrefixTheDetailAndTheLogPointer()
    {
        const string detail = "System.IO.IOException: 磁盘空间不足";

        var line = ScanText.ItemFailed(detail);

        Assert.StartsWith("处理该压缩包时出错：", line, StringComparison.Ordinal);
        Assert.Contains(detail, line, StringComparison.Ordinal);
        Assert.EndsWith("（详细错误已记录到日志文件）", line, StringComparison.Ordinal);
    }
}
