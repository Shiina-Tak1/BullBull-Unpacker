namespace Bbu.Core.Texts;

/// <summary>
/// 系统集成层的文案：右键菜单与入口的几个开关。用户能看到的字符串一律进表，
/// 命令行输出也算用户可见。
/// </summary>
public static class IntegrationText
{
    // ── 右键菜单 ──

    /// <summary>菜单项文字。三处注册键的默认值都是它。</summary>
    public const string ShellMenuText = "添加到 BBU 解压清单";

    /// <summary>打包形态下菜单由包清单提供，注册表那三处不参与。</summary>
    public const string ShellMenuFromPackage = "随安装包提供";

    // ── 入口：用法与参数表 ──

    /// <summary>用法行。占位符是可执行文件名。</summary>
    public const string CliUsageAt = "用法：{0} [开关] [路径…]";

    public const string CliArgsHeader = "参数";

    public const string CliExitCodesHeader = "退出码";

    public const string CliRowFormat = "  {0,-22}{1}";

    public const string CliPathRow = "<路径…>";

    public const string CliPathText = "把路径加入清单后开窗，与右键菜单同一条口径";

    public const string CliAutoRow = "--auto";

    public const string CliAutoText = "与路径同用，加入清单后立即开始";

    public const string CliHelpRow = "--help, -h";

    public const string CliHelpText = "显示这份说明";

    public const string CliVersionRow = "--version";

    public const string CliVersionText = "显示版本";

    public const string CliWhereRow = "--where";

    public const string CliWhereText = "用于查看程序、数据及相关文件的位置，便于排查问题";

    public const string CliInstallRow = "--install-shellmenu";

    public const string CliInstallText = "注册右键菜单的三处键，不开窗";

    public const string CliUninstallRow = "--uninstall-shellmenu";

    public const string CliUninstallText = "清除旧版右键菜单注册项";

    public const string CliMenuSwitchRow = "--menu-switch=<名称>:<on|off>";

    public const string CliMenuSwitchText = "写菜单显示开关：名称取 modern 或 classic，第二段取 on 或 off，不开窗";

    public const string CliMenuSwitchUsageAt = "菜单开关的写法是 modern:on 或 classic:off，收到「{0}」";

    public const string CliMenuSwitchAt = "菜单开关：{0} → {1}";

    public const string CliMenuSwitchFailedAt = "写开关失败：{0}";

    // ── 入口：退出码表 ──

    public const string CliCodeDoneText = "操作成功";

    public const string CliCodeUndoneText = "操作未完成";

    public const string CliCodeUsageText = "用法错误";

    // ── 入口：版权尾注 ──

    /// <summary>版本与帮助末尾的版权行。取值与关于页那一行共用一处，不另写一遍正文。</summary>
    public const string CliCopyright = Ui.AboutCopyright;

    /// <summary>版权行下面那一行：SPDX 标识符。取值与关于页共用一处。</summary>
    public const string CliLicense = Ui.AboutLicense;

    // ── 入口：结论与诊断 ──

    /// <summary>用法错误的提示。占位符是那个开关原文。</summary>
    public const string CliUnknownSwitchAt = "未知开关：{0}";

    /// <summary>装完右键菜单之后的结论。占位符是写成功的位置数。</summary>
    public const string CliShellInstalledAt = "右键菜单已注册 {0} 处";

    /// <summary>注册没写全时补的那一句。占位符：应写的处数、失败落点、错误码。</summary>
    public const string CliShellInstallFailedAt = "注册未完成：应写 {0} 处，失败在 {1}（错误码 {2}）";

    /// <summary>失败时同一台机器上 PowerShell 能写而这里不能，差异只可能在这个进程的身份上。</summary>
    public const string CliShellInstallContextAt = "进程 {0}：管理员 {1}、64 位 {2}、包身份 {3}";

    public const string CliYes = "是";

    public const string CliNo = "否";

    /// <summary>失败时补的进程完整性级别。占位符是级别名。</summary>
    public const string CliShellInstallIntegrityAt = "进程完整性级别：{0}";

    /// <summary>失败时逐层试写的结果。占位符：键路径、结果。</summary>
    public const string CliShellInstallProbeAt = "  试写 {0} → {1}";

    /// <summary>完整性级别的几个名字（<see cref="Integration.ProcessIntegrity"/> 用）。</summary>
    public const string IntegrityUntrusted = "不受信任";

    public const string IntegrityLow = "低";

    public const string IntegrityMedium = "中";

    public const string IntegrityMediumPlus = "中高";

    public const string IntegrityHigh = "高";

    public const string IntegritySystem = "系统级";

    public const string CliShellRemovedAt = "右键菜单已移除 {0} 处";

    /// <summary><c>--where</c> 的各行。占位符都是一个路径。</summary>
    public const string CliWhereProgramAt = "程序目录  {0}";

    public const string CliWhereDataDirAt = "数据目录  {0}";

    public const string CliWhereConfigAt = "配置文件  {0}";

    public const string CliWhereVaultAt = "密码本    {0}";

    public const string CliWhereLogAt = "日志文件  {0}";

    public const string CliWhereSevenZipAt = "7-Zip     {0}";

    /// <summary>随包 UnRAR 那一行。与 7-Zip 分开列：两个引擎缺哪一个的影响不同。</summary>
    public const string CliWhereUnRarAt = "UnRAR     {0}";

    public const string CliWhereBuildAt = "构建指纹  {0}";

    /// <summary>某一处没有落点时那句，只给 `--where` 用；关于页那两句「未找到随包的 …」是另外两条文案。</summary>
    public const string CliWhereMissing = Ui.EngineMissingNote;

    /// <summary>数据目录就在程序旁边。</summary>
    public const string CliWherePortable = "（与程序在同一文件夹，便携）";

    /// <summary>程序目录写不进去，数据落到了用户目录。</summary>
    public const string CliWhereRoaming = "（程序目录无法写入，已放到用户目录）";

    /// <summary>数据目录被环境变量钉住了。</summary>
    public const string CliWherePinned = "（由环境变量指定）";

    /// <summary>任务栏标识没声明成。只进日志文件，不进界面。</summary>
    public const string TaskbarIdentityFailed = "未能声明任务栏标识，任务栏可能按可执行文件分组";

    /// <summary>转发或冷启动带进来的路径不在了。占位符是那个路径。</summary>
    public const string LaunchPathMissingAt = "路径不存在，已跳过：{0}";
}
