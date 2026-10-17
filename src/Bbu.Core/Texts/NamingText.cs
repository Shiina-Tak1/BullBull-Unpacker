namespace Bbu.Core.Texts;

/// <summary>
/// 输出目录命名的文案：日志句与跳过原因。
/// 本类只放用户能看到的字符串。
/// </summary>
public static class NamingText
{
    /// <summary>无扩展名的包错开成功。占位符是源包名与最终文件夹名。</summary>
    public const string NoExtSuffixLoggedAt = "无扩展名：{0} 改名为 {1}/";

    /// <summary>同名的是文件而非文件夹，改名落盘。占位符是原名与新名。</summary>
    public const string SameNameIsFileRenamedAt = "同名的是文件而非文件夹，已重命名：{0} 变为 {1}";

    /// <summary>按重名处理跳过。占位符是被挡住的那个名字。</summary>
    public const string SkippedAt = "已跳过：{0}" + PierceText.ConflictSkipSuffix;

    /// <summary>名称超过保留长度上限，保留开头截断。占位符是截前的名称与截后的文件夹名。</summary>
    public const string LongNameTrimmedAt = "名称过长，已截断：{0} 变为 {1}";
}
