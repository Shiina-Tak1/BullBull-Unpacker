using System;
using System.IO;
using Bbu.Core;
using Bbu.Core.Configuration;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 数据目录分裂的提示（B-2026-058）。
///
/// 那一条的现场是：程序目录在两次运行之间从"能写"变成"写不进去"（只读介质、ACL 变化、
/// 整个文件夹搬到另一台机器），数据目录因此翻转，两本密码本各在一处，而程序不报任何提示 ——
/// 用户看到的是「密码本空了」，解压时密码漏试。
///
/// C# 侧判定已收敛进 <see cref="DataDirectory"/> 一处，所以这里钉的是提示那一半：
/// **只在另一侧真有密码本时**才把那个目录报出来，而且两本都不许动。
/// 与  同处一个集合：两个类都改进程级的环境变量。
/// </summary>
[Collection("data-directory-env")]
public sealed class DataDirectorySplitTests : IDisposable
{
    private readonly string _root;
    private readonly string _app;
    private readonly string _data;
    private readonly string? _savedOverride;

    public DataDirectorySplitTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "data-dir-split", Guid.NewGuid().ToString("N"));
        _app = Path.Combine(_root, "app");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(_data);

        // 环境变量钉住时判定不走探针，那会改变本条测试的判据
        _savedOverride = Environment.GetEnvironmentVariable(DataDirectory.EnvOverride);
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, _savedOverride);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响判据
        }
    }

    [Fact]
    [Trait("Bug", "B-2026-058")]
    public void AlternativeCandidate_MustBeSilentWhenTheOtherSideHasNoBook()
    {
        Assert.Null(DataDirectory.AlternativeCandidate(_data, _app));
    }

    [Fact]
    [Trait("Bug", "B-2026-058")]
    public void AlternativeCandidate_MustReportTheOtherSideWhenItHasABook()
    {
        PutBook(_app, "只有这一台机器上写过的那条");

        var other = DataDirectory.AlternativeCandidate(_data, _app);

        // 返回的是那一侧的用户数据目录，不是数据根本身
        Assert.Equal(Path.GetFullPath(DataDirectory.UserDataOf(_app)), other);
    }

    [Fact]
    [Trait("Bug", "B-2026-058")]
    public void AlternativeCandidate_MustLookAtTheAppDirectoryWhenDataIsElsewhere()
    {
        // 当前在系统目录那一档时，另一侧就是程序目录
        PutBook(_app, "程序目录旁边那本");

        Assert.Equal(
            Path.GetFullPath(DataDirectory.UserDataOf(_app)),
            DataDirectory.AlternativeCandidate(_data, _app));
    }

    [Fact]
    [Trait("Bug", "B-2026-058")]
    public void AlternativeCandidate_MustBeSilentWhenPinned()
    {
        PutBook(_app, "程序目录旁边那本");
        Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, _data);
        try
        {
            // 钉住了就不去翻用户真实的另两个候选目录
            Assert.Null(DataDirectory.AlternativeCandidate(_data, _app));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataDirectory.EnvOverride, null);
        }
    }

    [Fact]
    [Trait("Bug", "B-2026-058")]
    public void AlternativeCandidate_MustLeaveBothBooksUntouched()
    {
        var appText = "程序目录那本的内容";
        var dataText = "系统目录那本的内容";
        PutBook(_app, appText);
        PutBook(_data, dataText);
        var appBefore = File.ReadAllBytes(BookOf(_app));
        var dataBefore = File.ReadAllBytes(BookOf(_data));

        DataDirectory.AlternativeCandidate(_data, _app);

        // 不合并、不迁移、不删：两本都要一字未动
        Assert.Equal(appBefore, File.ReadAllBytes(BookOf(_app)));
        Assert.Equal(dataBefore, File.ReadAllBytes(BookOf(_data)));
    }

    [Fact]
    public void UserData_MustHoldConfigVaultAndLogsInOneFolder()
    {
        var root = Path.Combine(_root, "root-with-userdata");
        Directory.CreateDirectory(root);

        var userData = DataDirectory.Resolve(root, useCache: false);

        // 设置、密码本与日志都收在 <数据根>\userdata\ 下，便携包的根目录因此只剩程序本体与几个目录
        Assert.Equal(root, DataDirectory.ResolveRoot(root, useCache: false));
        Assert.Equal(Path.Combine(root, AppInfo.UserDataFolderName), userData);
        Assert.True(Directory.Exists(userData), "userdata 目录要自己建出来");
    }

    /// <summary></summary>
    private static string BookOf(string root) =>
        Path.Combine(DataDirectory.UserDataOf(root), VaultText.BookFileName);

    [Fact]
    public void PackagedProcess_MustNotTrustTheWriteProbe()
    {
        // 打包形态下程序目录看起来可写，那是容器重定向报的假成功。
        // 这一档直接落 %APPDATA%，不跑真写探针，分类名仍是 Roaming。
        var root = DataDirectory.ResolveRoot(_app, useCache: false, packaged: true);

        Assert.Equal(Path.GetFullPath(RoamingRoot()), root);
        Assert.Equal(DataDirectoryKind.Roaming, DataDirectory.Classify(root, _app));
    }

    private static string RoamingRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            DataDirectory.AppFolderName);

    private static void PutBook(string root, string content)
    {
        var book = BookOf(root);
        Directory.CreateDirectory(Path.GetDirectoryName(book)!);
        File.WriteAllText(book, content);
    }
}
