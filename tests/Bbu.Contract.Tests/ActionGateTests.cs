using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 「能不能开始」只有一处判据。它落在 <c>Views\Actions.cs</c>：按钮可用性与开跑取的那一批同源。
/// 各写一份时会出现按钮亮着却点不动（或反过来），每加一个入口多长一处。
/// </summary>
public sealed class ActionGateTests
{
    /// <summary>可跑判据不许出现在别处：需要它的入口都从 <c>Actions</c> 取。</summary>
    [Fact]
    public void StartEligibility_MustHaveASingleOwner()
    {
        var offenders = Repo.Files(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia"), ".cs")
            .Where(file => Path.GetFileName(file) != "Actions.cs")
            .Where(file => File.ReadAllText(file).Contains("Runner.NeedsRun", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Repo.Root, file))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>那一处出口本身要在场：它被删掉时上一条会空转通过。</summary>
    [Fact]
    public void SingleOwner_MustExistAndCoverStart()
    {
        var actions = Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "Actions.cs");

        Assert.True(File.Exists(actions), "找不到动作可用性的那一处出口：" + actions);
        var text = File.ReadAllText(actions);
        Assert.Contains("CanStart", text, StringComparison.Ordinal);
        Assert.Contains("Batch", text, StringComparison.Ordinal);
    }
}
