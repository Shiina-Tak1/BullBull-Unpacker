using System;
using Bbu.Core.Texts;

namespace Bbu.Core.Configuration;

/// <summary>
/// 开跑前的配置自检。
///
/// 只放「不许开跑」这一类：配置不完整时要在动手之前拦下，而不是跑起来再说。
/// 它返回的就是要写进日志的那一句话，界面与将来的命令行共用同一句 ——
/// 各写一句，必然有一处说错。
/// </summary>
public static class ConfigChecks
{
    /// <summary>
    /// 选了「指定目录」却没填路径：不许开跑，也不许悄悄退回原目录。
    ///
    /// 说了用指定目录、文件却散到包旁边（可能在桌面或下载文件夹），
    /// 界面上还写着「指定目录」被选中 —— 用户要过几天才发现（B-2026-024）。
    /// 参数是只读的，所以「不许把配置偷偷改成原目录」这一条由签名自己保证。
    /// </summary>
    public static string? BlockingReason(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.OutputMode is OutputMode.Custom && config.OutputDirectory.Trim().Length == 0
            ? Ui.OutputDirMissing
            : null;
    }
}
