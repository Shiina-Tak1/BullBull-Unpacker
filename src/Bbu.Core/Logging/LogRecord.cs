using System;

namespace Bbu.Core.Logging;

/// <summary>一条记录：级别、正文、层号与进度结构；正文由 <see cref="RunLog.Compose"/> 排，面板与日志文件共用同一句。</summary>
/// <remarks>级别是语义级别（面板按它取色、日志文件按它打标签）；层号 0 表示不适用；
/// <see cref="Progress"/> 非空即进度行（只进活动槽与日志文件），为空的行进面板与日志文件；
/// <see cref="At"/> 由 <see cref="RunLog"/> 写入与广播前填一次，调用点不传。</remarks>
public readonly record struct LogRecord(
    LogLevel Level, string Text, int Layer = 0, LogProgress? Progress = null, DateTimeOffset At = default);
