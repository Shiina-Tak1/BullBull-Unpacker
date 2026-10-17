using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bbu.Contract.Tests;

/// <summary>
/// 仓库内的定位与读取。本工程读的东西只有两种：<c>src\</c> 里的源码，
/// 以及同步进公开仓的那些一般文件（两份第三方声明、随包引擎目录、.gitattributes）。
///
/// 另一个测试工程里有一份同名类，是**两份实现**，刻意不抽公共类库：
/// 那一份要读的落点只在本机，本工程一处都不许出现。
/// </summary>
internal static class Repo
{
    /// <summary>仓库根：从测试输出目录往上找到含 <c>Bbu.slnx</c> 的那一层。</summary>
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    /// <summary>枚举目录下的文件，跳过构建产物与临时目录。</summary>
    public static IEnumerable<string> Files(string directory, params string[] extensions)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(SkipDirectory))
            {
                continue;
            }

            if (extensions.Length == 0 || extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    public static bool SkipDirectory(string name) =>
        name is "bin" or "obj" or ".scratch" or ".git" or "node_modules" or "dist";

    /// <summary>读 csproj 里声明的项目引用（相对路径，正斜杠统一）。</summary>
    public static IReadOnlyList<string> ProjectReferencesOf(string projectPath)
    {
        var text = File.ReadAllText(projectPath);
        return Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToList();
    }

    /// <summary>把 csproj 路径归一成"相对仓库根的、正斜杠的"形状，用来比对依赖方向。</summary>
    public static string Normalize(string projectPath) =>
        Path.GetRelativePath(Root, Path.GetFullPath(projectPath)).Replace('\\', '/');

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bbu.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("定位不到仓库根：往上一路都没找到 Bbu.slnx");
    }
}
