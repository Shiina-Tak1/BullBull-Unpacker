using System;
using System.IO;
using Bbu.Core.Configuration;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 数据目录的失败路径：**启动路径上的目录创建不许抛**（B-2026-151）。
///
/// 现场是 <c>BBU_DATA_DIR</c> 钉到一个不存在的盘符，<c>Bbu.App.exe</c> 未处理异常、
/// 退出码 <c>0xE0434352</c>，双击启动连窗口都没有。窗口那条路走的是同一个
/// <c>ResolveCore</c>（<c>DataDirectory.Resolve()</c>），所以窗口路径与非窗口路径同时失效。
///
/// 三档降级各一条断言，都用「路径里横着一个同名文件」造出来，不依赖某台机器上哪个盘符不存在。
/// 与  同处一个集合：两个类都改进程级的环境变量。
/// </summary>
[Collection("data-directory-env")]
public sealed class DataDirectoryFailureTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly string? _savedPin;
    private readonly string? _savedAppData;

    public DataDirectoryFailureTests()
    {
        _savedPin = Environment.GetEnvironmentVariable(DataDirectory.EnvOverride);
        _savedAppData = Environment.GetEnvironmentVariable("APPDATA");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, _savedPin);
        Environment.SetEnvironmentVariable("APPDATA", _savedAppData);

        // 把静态说明清回「没降级」：它是进程级的，留着会串到别的用例（设置页那一块要读它）。
        DataDirectory.ResolveRoot(Fresh("reset"), useCache: false);
        _dir.Dispose();
    }

    /// <summary>
    /// 第一档：钉住的目录建不出来就**忽略它**，接着按正常顺序判，程序照常起步。
    /// 撤掉那条忽略（改回裸 <c>CreateDirectory</c>），异常会从这条用例直接逃出去。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-151")]
    public void PinnedDirectoryThatCannotBeCreated_MustBeIgnoredAndExplained()
    {
        var app = Fresh("app");
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, Blocked("pin"));
        Environment.SetEnvironmentVariable("APPDATA", Fresh("unused-roaming"));

        var root = DataDirectory.ResolveRoot(app, useCache: false);

        Assert.Equal(Path.GetFullPath(app), root);
        Assert.Equal(DataDirectoryKind.Portable, DataDirectory.Classify(root, app));
        Assert.Contains("BBU_DATA_DIR", DataDirectory.LastNote, StringComparison.Ordinal);
        Assert.Contains(root, DataDirectory.LastNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第二档：程序目录写不进去（只读介质、ACL、装到 Program Files）→ 落到用户目录，
    /// 并说清数据落在哪。这一档 1.1.0 就有，缺的是那句说明。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-151")]
    public void ProgramDirectoryUnwritable_MustLandInTheUserDirectoryAndSaySo()
    {
        var roaming = Fresh("roaming");
        var app = Blocked("app");
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, null);
        Environment.SetEnvironmentVariable("APPDATA", roaming);

        var root = DataDirectory.ResolveRoot(app, useCache: false);

        Assert.Equal(Path.Combine(Path.GetFullPath(roaming), DataDirectory.AppFolderName), root);
        Assert.Equal(DataDirectoryKind.Roaming, DataDirectory.Classify(root, app));
        Assert.Contains(root, DataDirectory.LastNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第三档：用户目录也建不出来 → **退回程序目录并仍启动**。「仍启动」是这一条的要点 ——
    /// 两个候选都不成时，1.1.0 的原话是「那就只能用程序目录（至少报错位置一致）」，
    /// 不是抛出去把窗口拦在启动前。这一档要程序目录也写不进去才走得到。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-151")]
    public void BothCandidatesUnusable_MustFallBackToTheProgramDirectoryAndStillStart()
    {
        var app = Blocked("app");
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, null);
        Environment.SetEnvironmentVariable("APPDATA", Blocked("roaming"));

        var root = DataDirectory.ResolveRoot(app, useCache: false);

        Assert.Equal(Path.GetFullPath(app), root);
        Assert.Contains(root, DataDirectory.LastNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>userdata</c> 那一层同理：数据根判得出来、它下面这一层建不出来时照样把路径交回去，
    /// 不许抛。写盘那一步自己会失败，走的是 <c>IConfigStore.Save</c> 的失败通道（B-2026-152）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-151")]
    public void UserDataLayer_MustNotThrowWhenItCannotBeCreated()
    {
        var app = Fresh("app");
        File.WriteAllText(Path.Combine(app, AppInfo.UserDataFolderName), "占住这一层");
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, null);
        Environment.SetEnvironmentVariable("APPDATA", Fresh("unused-roaming"));

        var userData = DataDirectory.Resolve(app, useCache: false);

        Assert.Equal(Path.Combine(Path.GetFullPath(app), AppInfo.UserDataFolderName), userData);
    }

    /// <summary>没降级时不许留说明：该说明只服务于降级路径，正常路径上不留。</summary>
    [Fact]
    [Trait("Bug", "B-2026-151")]
    public void OrdinaryResolution_MustLeaveNoExplanation()
    {
        var app = Fresh("app");
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, null);
        Environment.SetEnvironmentVariable("APPDATA", Fresh("unused-roaming"));

        Assert.Equal(Path.GetFullPath(app), DataDirectory.ResolveRoot(app, useCache: false));
        Assert.Null(DataDirectory.LastNote);
    }

    /// <summary>一个可写的新目录。</summary>
    private string Fresh(string name)
    {
        var path = _dir.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>一个**建不出来**的路径：路径里横着一个同名文件，<c>CreateDirectory</c> 必抛。</summary>
    private string Blocked(string name)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, "挡路");
        return Path.Combine(path, "under-a-file");
    }
}
