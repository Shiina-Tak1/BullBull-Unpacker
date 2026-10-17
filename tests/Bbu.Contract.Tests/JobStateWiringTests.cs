using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 任务状态与界面渲染的结构门禁：状态枚举每加一个成员，界面就得有对应的图标与配色。
///
/// 破了它的后果是静默的：新状态沿用上一个状态的图标与类名，文字却已经是新的，
/// 用户看到的是一行自相矛盾的状态。加成员的人很容易只改枚举与判定、忘了渲染那一侧，
/// 所以把它写成断言，而不是靠记住。
/// </summary>
public sealed class JobStateWiringTests
{
    private static readonly Regex Member = new(
        @"^\s{4}(?<name>[A-Z][A-Za-z]*),\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void EveryJobStateMember_MustBeRenderedByTheUi()
    {
        var job = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Models", "Job.cs"));
        var window = File.ReadAllText(
            Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Views", "MainWindow.axaml.cs"));

        var members = new List<string>();
        var enumStart = job.IndexOf("enum JobState", StringComparison.Ordinal);
        Assert.True(enumStart >= 0, "在 Job.cs 里找不到 JobState 枚举");
        var enumBody = job[enumStart..job.IndexOf('}', enumStart)];
        foreach (Match match in Member.Matches(enumBody))
        {
            members.Add(match.Groups["name"].Value);
        }

        Assert.NotEmpty(members);

        var missing = new List<string>();
        foreach (var name in members)
        {
            if (!window.Contains($"case JobState.{name}:", StringComparison.Ordinal))
            {
                missing.Add($"{name} 没有 case 分支");
            }

            if (!window.Contains($"Icons.{name}", StringComparison.Ordinal))
            {
                missing.Add($"{name} 没有图标");
            }
        }

        Assert.Empty(missing);
    }
}
