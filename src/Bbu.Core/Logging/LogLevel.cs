namespace Bbu.Core.Logging;

/// <summary>
/// 日志级别。四档按严重程度升序 —— 界面过滤与落盘标签都按这个顺序判，成员次序是契约，不许重排。
/// <see cref="Debug"/> 只进日志文件，界面默认不显示（面板的最小级别可配）。
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}
