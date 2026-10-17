using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Bbu.Core.Configuration;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 引用方向：<c>Bbu.App</c> → <c>Bbu.Ui.Avalonia</c> → <c>Bbu.Core</c>，单向无环。
/// 这一条不是自觉，是断言：业务层一旦 <c>using Avalonia</c>，换 UI 的成本从 30% 变成 100%。
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly CoreAssembly = typeof(AppConfig).Assembly;

    [Fact]
    public void Core_MustNotReferenceUiAssemblies()
    {
        var bad = CoreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Bbu.Ui", StringComparison.Ordinal)
                        || name.StartsWith("Bbu.App", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bad);
    }

    [Fact]
    public void Core_MustNotReferenceAnySiblingProject()
    {
        var bad = CoreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => name.StartsWith("Bbu.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bad);
    }

    [Fact]
    public void ProjectReferences_MustPointOneWay()
    {
        var app = References(Path.Combine(Repo.Src, "Bbu.App", "Bbu.App.csproj"));
        var ui = References(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia", "Bbu.Ui.Avalonia.csproj"));
        var core = References(Path.Combine(Repo.Src, "Bbu.Core", "Bbu.Core.csproj"));

        Assert.Contains("src/Bbu.Ui.Avalonia/Bbu.Ui.Avalonia.csproj", app);
        Assert.Contains("src/Bbu.Core/Bbu.Core.csproj", app);
        Assert.Contains("src/Bbu.Core/Bbu.Core.csproj", ui);
        Assert.DoesNotContain("src/Bbu.App/Bbu.App.csproj", ui);
        Assert.Empty(core);
    }

    [Fact]
    public void ProjectReferenceGraph_MustBeAcyclic()
    {
        var projects = Repo.Files(Repo.Src, ".csproj").ToList();
        Assert.NotEmpty(projects);

        var graph = projects.ToDictionary(
            p => Repo.Normalize(p),
            References,
            StringComparer.OrdinalIgnoreCase);

        foreach (var start in graph.Keys)
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.False(Walk(start), $"引用成环：{start}");
            continue;

            bool Walk(string node)
            {
                if (!graph.ContainsKey(node))
                {
                    return false;
                }

                if (done.Contains(node))
                {
                    return false;
                }

                if (!visiting.Add(node))
                {
                    return true;
                }

                foreach (var next in graph[node])
                {
                    if (Walk(next))
                    {
                        return true;
                    }
                }

                visiting.Remove(node);
                done.Add(node);
                return false;
            }
        }
    }

    private static IReadOnlyList<string> References(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        return Repo.ProjectReferencesOf(projectPath)
            .Select(reference => Repo.Normalize(Path.GetFullPath(Path.Combine(projectDir, reference))))
            .ToList();
    }
}
