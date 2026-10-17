using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 产物目录命名的结构门禁：算产物目录与处理重名只有一处结论。
///
/// 界面、CLI、流水线各拼一套，必然对同一个包给出三个答案 —— 用户看到的是「跳过」，
/// 盘上却多出一份 <c>(1)</c>。所以调用点要逐个钉住，并且不许别处再长出一份判据。
/// </summary>
public sealed class OutputNamingEntryTests
{
    /// <summary>已经改走唯一入口的调用点。新增调用点时在这里加一行，断言漏钉会让它红。</summary>
    /// <remarks>
    /// 只剩穿透层这一处：算一层的产物目录。它由 <c>ResolveTarget</c> 包一层，
    /// 所以断言钉的是那个包装函数里的调用。
    /// 界面只读编排层给的 <c>Job.OutDir</c>，自己不算产物目录。
    /// </remarks>
    private static readonly (string Project, string File)[] CallSites =
    [
        ("Bbu.Core", Path.Combine("Services", "PierceLoop.cs")),
    ];

    /// <summary>判据 9：所有调用点都走同一个函数。</summary>
    [Fact]
    public void CallSites_MustGoThroughTheSingleEntry()
    {
        foreach (var (project, file) in CallSites)
        {
            var path = Path.Combine(Repo.Src, project, file);
            Assert.True(File.Exists(path), "找不到调用点：" + path);
            Assert.Contains("OutputNaming.Resolve", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 界面不许自己算产物目录，只读编排层给的结论。
    /// 界面里一旦又出现命名规则，同一个包就会有两个答案：界面说跳过，盘上却多一份 (1)。
    /// </summary>
    [Fact]
    public void Ui_MustNotResolveOutputDirectoriesItself()
    {
        var strays = Repo.Files(Path.Combine(Repo.Src, "Bbu.Ui.Avalonia"), ".cs")
            .Where(file => File.ReadAllText(file).Contains("OutputNaming", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Repo.Root, file))
            .ToArray();

        Assert.Empty(strays);
    }

    /// <summary>
    /// 判据 9 的另一半：结论只许在一处产生。<c>_unpack</c> 这个字面量出现第二次，就等于又开了一个入口 ——
    /// 别处按这个字面量拼目录名会绕过唯一入口。
    /// </summary>
    [Fact]
    public void OutputDirectoryDecision_MustLiveInOutputNamingOnly()
    {
        var strays = new List<string>();
        foreach (var file in Repo.Files(Repo.Src, ".cs"))
        {
            if (Path.GetFileName(file) == "OutputNaming.cs")
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("_unpack", StringComparison.Ordinal))
            {
                strays.Add($"{Path.GetRelativePath(Repo.Root, file)} 里出现了 _unpack");
            }
        }

        Assert.Empty(strays);
    }

    /// <summary>唯一入口本身要在场：改名与冲突三档都由它一处判定。</summary>
    [Fact]
    public void OutputNaming_MustOwnTheNamingRules()
    {
        var text = File.ReadAllText(Path.Combine(Repo.Src, "Bbu.Core", "Services", "OutputNaming.cs"));

        Assert.Contains("public static OutputDirResult Resolve", text, StringComparison.Ordinal);
        Assert.Contains("CleanDeleteChars", text, StringComparison.Ordinal);
        Assert.Contains("StripSiteNoise", text, StringComparison.Ordinal);
        Assert.Contains("NormalizeForMatch", text, StringComparison.Ordinal);
    }
}
