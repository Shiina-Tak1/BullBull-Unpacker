using System;
using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 子进程起始位置的两条判据（外部审计第 1 轮 `EP-10`、`D-5` 的另一半）。
///
/// `EP-10` 是显式化而不是修缺陷：`ProcessStartInfo.WorkingDirectory` 不设时，子进程拿的是
/// **父进程**的当前目录，而引擎该落在自己那一份旁边。两条分工如下 ——
/// 纯函数那一条钉「该钉哪个目录」，接线那一条钉「调用点真用上了它」；
/// 真机那一条在 <see cref="FinishedProcess_MustHaveItsOutputDrained"/> 里顺带过一遍起进程这条路。
/// 本类真起随包引擎，所以与别的引擎类一起串行。
/// </summary>
[Collection("engine")]
public sealed class EngineProcessScopeTests
{
    /// <summary>
    /// `EP-10` 的判据本体：给可执行文件路径，取它所在的目录。撤掉它即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-214")]
    public void EngineRunDirectory_MustTakeTheEngineFolder()
    {
        Assert.Equal(
            Path.Combine("C:", "engines", "7z"),
            SevenZipEngine.EngineRunDirectory(Path.Combine("C:", "engines", "7z", "7z.exe")));

        // 命令里只给了文件名时退回当前目录：`ProcessStartInfo.WorkingDirectory` 不许空串。
        Assert.Equal(
            Environment.CurrentDirectory,
            SevenZipEngine.EngineRunDirectory("7z.exe"));
    }

    /// <summary>
    /// `EP-10` 的接线：起进程那一处必须把上面那个结论交给 `ProcessStartInfo`。
    /// 这条按源码结构判，理由与 ` ` 的接线断言同：纯函数判对了、调用点没接上，
    /// 判据在真机上一样不生效。撤掉那一行即红。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-214")]
    public void ProcessStart_MustPinTheEngineDirectory()
    {
        var engine = ReadSource("SevenZipEngine.cs");

        Assert.Contains("WorkingDirectory = EngineRunDirectory(command[0])", engine, StringComparison.Ordinal);
    }

    /// <summary>
    /// `D-5` 的另一半：正常跑完的一趟，两条输出流都在上限内收尾 —— 结果里的输出因此是完整的。
    /// 这一条同时钉住 `OutputDrained` 真的接在结果上，而不是一个恒为真的默认值。
    /// </summary>
    [Fact, Trait("Bug", "B-2026-213")]
    public async Task FinishedProcess_MustHaveItsOutputDrained()
    {
        var result = await TestPaths.NewEngine().RunAsync(
            EngineCommand.List(TestPaths.BundledSevenZip, Path.Combine(TestPaths.Fixtures, "ok-small.zip")),
            EngineKind.SevenZip);

        Assert.True(result.Ok, result.Output);
        Assert.True(result.OutputDrained);
        Assert.Contains("ok-small.zip", result.Output, StringComparison.Ordinal);
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Bbu.Core", "Services", name));
}
