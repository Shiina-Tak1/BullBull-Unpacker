using System;
using System.Collections.Generic;
using System.IO;
using Bbu.Core.Configuration;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 留在盘上的文件必须说出来：切出来的临时切片删不掉时，日志里要有一句指出它。
/// B-2026-312）。
/// </summary>
[Collection("engine")]
public sealed class PierceTempTests
{
    /// <summary>
    /// 临时切片被占用而删不掉时，如实记一句并说出原因；不许静默忽略 ——
    /// 静默忽略的结果是用户目录里永远留着一个几 GB 的临时件，而日志里一个字都没有。
    /// 撤掉那一句即红（那时这条断言收不到任何带文件名的行）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-312")]
    public void UndiscardableTemp_MustBeReportedInsteadOfSwallowed()
    {
        using var temp = new TestDirectory();
        var slice = temp.Combine("x.carve.zip");
        File.WriteAllText(slice, "slice");

        var logs = new List<string>();
        var vault = new Vault();
        vault.SetEntries(Array.Empty<string>());
        var piercer = new Piercer(
            TestPaths.NewEngine(),
            vault,
            new PierceOptions { MinFreeGb = 0, Log = new ChannelLog((_, message) => logs.Add(message)) });

        // 独占打开：Windows 上这一句会让 File.Delete 抛 IOException，正是「删不掉」那一档。
        using (new FileStream(slice, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            piercer.Discard(slice);
        }

        Assert.Contains(logs, line => line.Contains("x.carve.zip", StringComparison.Ordinal));
        Assert.True(File.Exists(slice), "删不掉时它仍然在盘上，这正是要说出来的那件事");
    }
}
