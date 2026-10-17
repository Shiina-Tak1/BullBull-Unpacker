#pragma once

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>

// 右键菜单的原生 COM 组件：新式菜单走 IExplorerCommand，经典菜单走 IContextMenu。
// 两者由同一个 CLSID 交给系统，清单里两处扩展都指向它。
// 

// 命令的 CLSID。四处同值：本行的文本、cpp 里的 GUID 结构体、两份清单模板里的 com:Class/@Id，一起改。
// {4F2C9E31-7B5D-4A18-9C6E-3D8A1F5B7E20}
#define BBU_CLSID_TEXT L"{4F2C9E31-7B5D-4A18-9C6E-3D8A1F5B7E20}"
extern const CLSID CLSID_BbuShellExt;

// 菜单文案。与 Bbu.Core.Texts.IntegrationText.ShellMenuText 同值：
// 架构断言 解析本文件并比对，两处不一致即门禁红。
#define BBU_MENU_TEXT L"添加到 BBU 解压清单"

// 动词名，与注册表三处键的 <verb> 同一段文字（GCS_VERBW 用）。
#define BBU_VERB_NAME L"BBUnpacker"

// DLL 启动谁按「指针 → 执行别名 → 邻接实体文件」三档找（见 ShellExt.cpp 的 ResolveExecutable）；
// 稀疏包形态下程序本体在包外，靠安装脚本写进 %LOCALAPPDATA% 的那份 target.txt。
// 指针落点：1 处。注册表里那个 TargetExecutable 值全仓零生产者，撤掉了（B-2026-209）。
#define BBU_TARGET_KEY L"Software\\BullBullUnpacker\\ShellExt"
#define BBU_TARGET_RELATIVE L"BullBullUnpacker\\ShellExt\\target.txt"

// 执行别名的文件名。打包形态下程序本体在 WindowsApps 里，那个路径受 ACL 保护，直接
// CreateProcessW 会被拒（真机上报「Windows 无法访问指定设备、路径或文件」）；执行别名经
// AppModel 激活，既带包身份、又能起。与 Bbu.Core 的 ShellMenu.AliasFileName、
// 完整包清单的 desktop:ExecutionAlias/@Alias 同值，三处不一致即 红。
#define BBU_ALIAS_FILE L"BullBullUnpacker.exe"

// 菜单显示开关（「显示 / 隐藏」）。DWORD，1 = 显示、0 = 隐藏，缺值按 1（菜单默认显示）。
// 与上面那三个同键、不同值：三处注册键在 Software\Classes 下，所以卸载删键不会连它一起删。
// 方案 C 说可见性由组件运行时给，而清单里没有开关属性：一个值管新式与经典两侧，
// GetState 与 QueryContextMenu 两处都读它。
// 与 Bbu.Core 的 ShellMenu.MenuSwitchValue 同源，两处不一致即 红。
#define BBU_SWITCH_MENU L"MenuEnabled"
