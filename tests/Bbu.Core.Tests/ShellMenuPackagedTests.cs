using System;
using Bbu.Core.Integration;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 打包形态下菜单由包清单提供。打包进程的注册表写入会被虚拟化，
/// 写进去的项资源管理器看不到，所以这两条路径一处都不能动。
/// </summary>
public sealed class ShellMenuPackagedTests
{
    /// <summary>包形态（菜单由包清单提供）时装与卸都不碰注册表，返回的处数都是零。</summary>
    [Fact]
    [Trait("Bug", "B-2026-117")]
    public void Packaged_MustNotTouchTheRegistry()
    {
        var root = @"Software\BullBullUnpacker\Tests\" + Guid.NewGuid().ToString("N");

        // 判据只看包身份，不看系统版本：显式传 packaged 才不受真机是否装了包的影响（B-2026-169、B-2026-171）
        Assert.Empty(ShellMenu.Install(@"C:\fake\BullBullUnpacker.exe", root, packaged: true));
        Assert.Empty(ShellMenu.Uninstall(root, packaged: true));
    }

    /// <summary>包形态下状态读作「随安装包提供」，三处位置照报，供设置页显示。</summary>
    [Fact]
    [Trait("Bug", "B-2026-117")]
    public void PackagedState_MustComeFromThePackage()
    {
        var state = ShellMenu.State(packaged: true);

        Assert.True(state.FromPackage);
        Assert.True(state.Installed);
        Assert.Equal(ShellMenu.Places.Count, state.Places.Count);
        Assert.Equal(IntegrationText.ShellMenuFromPackage, state.Command);
    }
}
