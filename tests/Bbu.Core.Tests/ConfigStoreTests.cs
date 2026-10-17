using System;
using System.IO;
using System.Text;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// <c>config.json</c> 的读写契约。
/// 缺字段回落默认；类型不对只回落那一个字段；枚举值不认识回落默认。
/// </summary>
public sealed class ConfigStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        using var dir = new TestDirectory();
        var store = new JsonConfigStore(dir.Combine("config.json"));
        var config = new AppConfig
        {
            Version = AppConfig.CurrentVersion,
            TimeoutMinutes = 12.5,
            MaxDepth = 7,
            MinFreeGb = 2.5,
            FlattenSingleChild = false,
            RemoveSource = true,
            RemoveIntermediate = false,
            CleanDelete = false,
            ScanAppended = false,
            FolderScan = FolderScan.Deep,
            PasswordWorkers = 4,
            OutputMode = OutputMode.Custom,
            OutputDirectory = @"D:\产物",
            Conflict = ConflictPolicy.Skip,
            ExcludedExtensions = new[] { "apk", "iso" },
            Language = "zh-CN",
            TableColumnWidths = new[] { 120.0, 240.5 },
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(config.TimeoutMinutes, loaded.TimeoutMinutes);
        Assert.Equal(config.MaxDepth, loaded.MaxDepth);
        Assert.Equal(config.MinFreeGb, loaded.MinFreeGb);
        Assert.False(loaded.FlattenSingleChild);
        Assert.True(loaded.RemoveSource);
        Assert.False(loaded.RemoveIntermediate);
        Assert.False(loaded.CleanDelete);
        Assert.False(loaded.ScanAppended);
        Assert.Equal(FolderScan.Deep, loaded.FolderScan);
        Assert.Equal(4, loaded.PasswordWorkers);
        Assert.Equal(OutputMode.Custom, loaded.OutputMode);
        Assert.Equal(@"D:\产物", loaded.OutputDirectory);
        Assert.Equal(ConflictPolicy.Skip, loaded.Conflict);
        Assert.Equal(new[] { "apk", "iso" }, loaded.ExcludedExtensions);
        Assert.Equal(new[] { 120.0, 240.5 }, loaded.TableColumnWidths);
    }

    [Fact]
    public void Save_WritesChineseWithoutEscaping()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        var store = new JsonConfigStore(path);

        store.Save(new AppConfig { OutputDirectory = @"D:\产物" });

        // 用户可能用记事本直接改这份文件：中文必须是中文，不许转成 \uXXXX
        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("产物", text);
        Assert.DoesNotContain("\\u4EA7", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var dir = new TestDirectory();
        var store = new JsonConfigStore(dir.Combine("no-such-config.json"));

        var config = store.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Equal(30.0, config.TimeoutMinutes);
        Assert.Equal(5, config.MaxDepth);
        Assert.Equal(1.0, config.MinFreeGb);
        Assert.True(config.FlattenSingleChild);
        Assert.False(config.RemoveSource);
        Assert.True(config.RemoveIntermediate);
        Assert.True(config.CleanDelete);
        Assert.True(config.ScanAppended);
        Assert.Equal(0, config.PasswordWorkers);
        Assert.Equal(OutputMode.Same, config.OutputMode);
        Assert.Equal(ConflictPolicy.Rename, config.Conflict);
        Assert.Equal(AppConfig.DefaultExcludedExtensions, config.ExcludedExtensions);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Load_MissingField_FallsBackToDefaultOnlyForThatField()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        File.WriteAllText(path, """{"maxDepth": 9}""", Encoding.UTF8);

        var config = new JsonConfigStore(path).Load();

        Assert.Equal(9, config.MaxDepth);
        Assert.Equal(30.0, config.TimeoutMinutes);
    }

    [Fact]
    public void Load_WrongTypeOnOneField_KeepsTheOtherFields()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        File.WriteAllText(path, """{"maxDepth": "很深", "minFreeGb": 3.5}""", Encoding.UTF8);

        var config = new JsonConfigStore(path).Load();

        // 类型不对的那个回落默认，旁边那个照读 —— 整份丢弃会静默抹掉用户调过的所有值
        Assert.Equal(5, config.MaxDepth);
        Assert.Equal(3.5, config.MinFreeGb);
    }

    [Fact]
    public void Load_UnknownEnumValue_FallsBackToDefault()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        File.WriteAllText(path, """{"conflict": "whatever", "outputMode": "别处"}""", Encoding.UTF8);

        var config = new JsonConfigStore(path).Load();

        Assert.Equal(ConflictPolicy.Rename, config.Conflict);
        Assert.Equal(OutputMode.Same, config.OutputMode);
    }

    [Fact]
    public void Load_UnknownKey_IsIgnoredSilently()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        // makeHtml 是 1.1.0 的占位字段；theme 是 0.1.0 的主题项 —— 只留深色之后这个字段没有了。
        // 两份旧配置都要读得动：不报错，且不再写回。
        File.WriteAllText(path, """{"makeHtml": true, "theme": "light", "maxDepth": 8}""", Encoding.UTF8);

        var store = new JsonConfigStore(path);
        var config = store.Load();
        store.Save(config);

        var written = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal(8, config.MaxDepth);
        Assert.DoesNotContain("makeHtml", written);
        Assert.DoesNotContain("theme", written);
    }

    [Fact]
    public void Load_BrokenJson_FallsBackToDefaultsAndReportsWhy()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        File.WriteAllText(path, "{ 这不是 JSON", Encoding.UTF8);

        var store = new JsonConfigStore(path);

        Assert.Equal(5, store.Load().MaxDepth);
        Assert.NotNull(store.LastError);
    }

    /// <summary>
    /// 整份读不出来回落到默认值时，要在用户看得见的那条通道上说一声：不说这一句，
    /// 用户分不清「这次用的是默认值」与「我的设置丢了」，于是会去重新设一遍。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-236")]
    public void Load_BrokenJson_SaysItFellBackToDefaultsAndWhy()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        File.WriteAllText(path, "{ 这不是 JSON", Encoding.UTF8);
        var log = new RecordingLog();
        var store = new JsonConfigStore(path, log);

        Assert.Equal(5, store.Load().MaxDepth);

        var line = Assert.Single(log.Infos);
        Assert.Equal(string.Format(LogText.ConfigFallbackToDefaultsAt, store.LastError), line);
        Assert.Equal(LogLevel.Warn, log.InfoLevels[0]);
        Assert.Empty(log.Debugs);
    }

    /// <summary>读得动时不写这一条，首次启动（还没有配置文件）也不写：每次都喊「这次用默认值」，用户会以为设置一直在丢。</summary>
    [Fact]
    [Trait("Bug", "B-2026-236")]
    public void Load_ReadableConfig_StaysQuietAboutDefaults()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        var log = new RecordingLog();
        var store = new JsonConfigStore(path, log);

        // 还没有配置文件：这是首次启动的正常路径，不是「读不出来」。
        Assert.Equal(5, store.Load().MaxDepth);
        Assert.Empty(log.Infos);

        store.Save(new AppConfig { MaxDepth = 8 });
        Assert.Equal(8, store.Load().MaxDepth);
        Assert.Empty(log.Infos);
    }

    /// <summary>
    /// 写不进去时交回结论、**不抛**，原因留在 <c>LastError</c>（B-2026-152）。
    ///
    /// 1.1.0 的 <c>save()</c> 就是 <c>except OSError: return False</c>，移植时签名变成了
    /// <c>void</c> 且不包 catch —— 失败通道整个消失，三个调用点只能各自补一层 catch。
    /// 现场来源是 sharing violation、目录只读与磁盘满；这里用「目标位置上横着一个目录」造出来。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-152")]
    public void Save_TargetUnwritable_ReportsFailureInsteadOfThrowing()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        Directory.CreateDirectory(path);

        var store = new JsonConfigStore(path);

        Assert.False(store.Save(new AppConfig()));
        Assert.NotNull(store.LastError);
    }

    /// <summary>写得进去时要交回成功，而且不许留下上一次那条失败原因。</summary>
    [Fact]
    [Trait("Bug", "B-2026-152")]
    public void Save_ReportsSuccessAndClearsThePreviousReason()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        Directory.CreateDirectory(path);
        var store = new JsonConfigStore(path);
        Assert.False(store.Save(new AppConfig()));

        Directory.Delete(path);
        Assert.True(store.Save(new AppConfig()));
        Assert.Null(store.LastError);
    }

    /// <summary>
    /// 写盘失败不许把临时文件留在配置目录里：它的名字与 <c>config.json</c> 只差后缀，被当成配置文件读会读空。
    /// 撤掉那段 <c>finally</c> 清理，这一条变红（B-2026-207）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-207")]
    public void Save_Failure_MustNotLeaveATempFileBehind()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        Directory.CreateDirectory(path);

        var store = new JsonConfigStore(path);

        Assert.False(store.Save(new AppConfig()));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    /// <summary>
    /// 临时名不可预测：把老的可预测名（<c>&lt;盘&gt;.&lt;进程号&gt;.tmp</c>）占成一个目录，
    /// 这次保存照样要成功 —— 同 <c>Vault</c> 的 B-2026-056 那条（B-2026-207）。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-207")]
    public void Save_MustNotBeBlockedByAnOccupiedPredictableTempName()
    {
        using var dir = new TestDirectory();
        var path = dir.Combine("config.json");
        Directory.CreateDirectory($"{path}.{Environment.ProcessId}.tmp");
        var store = new JsonConfigStore(path);

        Assert.True(store.Save(new AppConfig { MaxDepth = 8 }));
        Assert.Equal(8, store.Load().MaxDepth);
    }
}
