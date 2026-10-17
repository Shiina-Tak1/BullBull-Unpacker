namespace Bbu.Core.Texts;

/// <summary>清单里一行的状态标签。状态列只留简短级别。</summary>
public static class States
{
    public const string Queued = "排队中";
    public const string Running = "解压中";
    public const string Done = "完成";
    public const string Failed = "失败";
    public const string Skipped = "已跳过";
    public const string Partial = "部分";
    public const string Unknown = "未知";
}
