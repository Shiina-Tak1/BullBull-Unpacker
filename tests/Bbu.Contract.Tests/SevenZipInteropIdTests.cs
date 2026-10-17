using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Bbu.Contract.Tests;

/// <summary>
/// 7z.dll 那几个 COM 接口的 IID 必须与官方表一致。
///
/// 为什么值得一条断言：写错时行为是**静默**的 —— handler 从
/// <c>IArchiveExtractCallback.GetStream</c> 拿到 out 参数后只按 vtable 调 <c>Write</c>、不做 QI，
/// 所以错值照样跑得通；而它确实会对回调对象做 QI，于是错值只在某条少见路径上拿不到接口，
/// 没有任何一处会报错（B-2026-139）。
///
/// 出处：7-Zip 26.02 官方源码包 <c>CPP/7zip/Guid.txt</c>，模板是
/// <c>{23170F69-40C1-278A-0000-00yy00xx0000}</c>：<c>yy</c> 是头文件那一行的两位号、
/// <c>xx</c> 是接口号。本仓用到的六个接口逐一登记在下面那张表里。
/// </summary>
public sealed class SevenZipInteropIdTests
{
    /// <summary>接口名 →（头文件号, 接口号）。两个号都是官方 <c>Guid.txt</c> 里的原文。</summary>
    private static readonly (string Name, string Group, string Iface)[] Expected =
    [
        ("IInArchive", "06", "60"),
        ("IInStream", "03", "03"),
        ("IArchiveOpenCallback", "06", "10"),
        ("ICryptoGetTextPassword", "05", "10"),
        ("IArchiveExtractCallback", "06", "20"),
        ("ISequentialOutStream", "03", "02"),
    ];

    [Fact]
    [Trait("Bug", "B-2026-139")]
    public void InteropIds_MustMatchTheOfficialTable()
    {
        var path = Path.Combine(Repo.Src, "Bbu.Core", "Services", "SevenZipDll.cs");
        var source = File.ReadAllText(path);

        // 每个 Guid 属性之后紧跟的那个 interface 声明，就是它标的接口。
        // 反向对照：把 ISequentialOutStream 那一行改回 …-000300060000（那是 IStreamGetSize），
        // 这条立刻红 —— 而它在运行时是静默的，别指望别处能发现。
        var pattern = new Regex(
            @"Guid\(""(?<id>[0-9A-Fa-f-]{36})""\)[\s\S]{0,300}?\binterface\s+(?<name>\w+)",
            RegexOptions.Compiled);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in pattern.Matches(source))
        {
            found[match.Groups["name"].Value] = match.Groups["id"].Value.ToUpperInvariant();
        }

        var problems = new List<string>();
        foreach (var (name, group, iface) in Expected)
        {
            var want = $"23170F69-40C1-278A-0000-00{group}00{iface}0000";
            if (!found.TryGetValue(name, out var got))
            {
                problems.Add($"{name}：源码里找不到它标的 IID");
            }
            else if (!string.Equals(got, want, StringComparison.Ordinal))
            {
                problems.Add($"{name}：源码写 {got}，官方是 {want}（{group}/{iface}）");
            }
        }

        Assert.Empty(problems);
    }
}
