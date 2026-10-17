using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Bbu.Ui.Avalonia;
using Xunit;

namespace Bbu.Ui.Tests;

/// <summary>
/// 界面层骨架。R1 只证明三件事：headless 平台起得来、启动开关能解析、
/// 界面层没有反向引用入口层。真正的界面断言从 R7 开始长。
/// </summary>
public sealed class UiSkeletonTests
{
    [AvaloniaFact]
    public void HeadlessPlatform_MustStart()
    {
        // global:: 是必须的：本文件 using 了 Bbu.Ui.Avalonia，裸写 Avalonia 会先解析到那个命名空间
        Assert.NotNull(global::Avalonia.Application.Current);
    }

    [Fact]
    public void StartupOptions_Apply_ReadsFlagsAndValues()
    {
        try
        {
            StartupOptions.Apply(new[]
            {
                @"D:\某个包.zip",
                "--auto-run",
                @"--shot=D:\产物\一屏.png",
                "--shot-mid-delay=250",
            });

            Assert.True(StartupOptions.AutoRun);
            Assert.Equal(@"D:\产物\一屏.png", StartupOptions.ShotPath);
            Assert.Equal(250, StartupOptions.ShotMidDelayMs);
        }
        finally
        {
            // 静态状态要还原：同一个进程里还有别的测试
            StartupOptions.Apply(Array.Empty<string>());
            StartupOptions.ShotPath = null;
            StartupOptions.ShotMidDelayMs = 600;
        }
    }

    /// <summary>
    /// <c>--drop=</c> 可以给多次，全都要收下（契约侧的形状门禁在
    /// <c>Bbu.Contract.Tests. .RepeatableSwitch_MustCollectEveryValue</c>，
    /// 这条真跑一遍解析）。只留第一个的话，一次排五个任务时后四个静默丢掉。
    /// </summary>
    [Fact]
    public void StartupOptions_Apply_CollectsEveryDrop()
    {
        try
        {
            StartupOptions.Apply(new[]
            {
                @"--drop=Z:\某个包\photos-2024.zip",
                "--auto-run",
                @"--drop=Z:\某个包\project-backup.zip",
                @"--drop=Z:\某个包\lecture-video.mp4",
            });

            Assert.Equal(
                [@"Z:\某个包\photos-2024.zip", @"Z:\某个包\project-backup.zip", @"Z:\某个包\lecture-video.mp4"],
                StartupOptions.DropPaths);
        }
        finally
        {
            StartupOptions.Apply(Array.Empty<string>());
        }
    }

    [Fact]
    public void Ui_MustNotReferenceAppAssembly()
    {
        var bad = typeof(StartupOptions).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => name.StartsWith("Bbu.App", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bad);
    }
}
