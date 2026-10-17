using System;
using System.Collections.Generic;
using Bbu.Core.Texts;

namespace Bbu.Core.Models;

/// <summary>
/// 任务状态机：排队 → 运行中 → 完成 / 失败 / 已跳过 / 部分完成；标签在 <c>Texts.States</c> 一处定义。
/// 它加成员时会连坐界面的事件分支、图标、配色、六段构成条与汇总。
/// </summary>
public enum JobState
{
    Queued,
    Running,
    Done,
    Failed,

    /// <summary>按冲突策略跳过这一项。它属于「安全停下」，不是失败。</summary>
    Skipped,

    /// <summary>
    /// 安全停下了，但有内容确实没解（重复包被指纹挡下 / 到层数上限 / 多个包不知道先解哪一个 / 没找完）。
    /// 它既不是失败（没出错），也不是完成（有东西没解）。
    /// </summary>
    Partial,
}

public static class JobStateText
{
    public static string Label(this JobState s) => s switch
    {
        JobState.Queued => States.Queued,
        JobState.Running => States.Running,
        JobState.Done => States.Done,
        JobState.Failed => States.Failed,
        JobState.Skipped => States.Skipped,
        JobState.Partial => States.Partial,
        _ => States.Unknown,
    };
}

/// <summary>清单里的一行。字段对齐 1.1.0 的 <c>ScanItem</c>，界面只负责把它画出来。</summary>
public sealed class Job
{
    public required string ArchivePath { get; init; }

    /// <summary>类型列。扫描按真实格式给，不按扩展名。</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 类型列的短标签，2 至 4 字。列里画它、长串挂 title —— 类型列放不下
    /// <see cref="Kind"/> 那种长串，截断之后信息量为零。
    /// </summary>
    public string KindShort { get; init; } = string.Empty;

    public required string DisplayName { get; init; }

    public JobState State { get; set; } = JobState.Queued;

    /// <summary>这一项花了多久。</summary>
    public double Seconds { get; set; }

    /// <summary>最深一层的产物目录。</summary>
    public string? OutDir { get; set; }

    /// <summary>第一层的产物目录。「打开输出目录」要看的就是这里。</summary>
    public string? OutputTop { get; set; }

    /// <summary>失败归因或状态备注。要说清原因，不许只写「失败（退出码 2）」。</summary>
    public string? Reason { get; set; }

    /// <summary>这一项是用户点「停止」停下的，不是程序因故跳过。状态列按它分起因。</summary>
    public bool StoppedByUser { get; set; }

    /// <summary>这一行是不是文件夹汇总行。</summary>
    public bool IsDir { get; init; }

    /// <summary>文件夹里的包这一行要不要缩进。</summary>
    public bool Indent { get; init; }

    /// <summary>汇总行不参与执行。</summary>
    public bool Runnable { get; init; } = true;

    public int ChildCount { get; init; }

    /// <summary>汇总行在清单里的下标。子项指回它。</summary>
    public int? ParentIndex { get; init; }

    /// <summary>汇总行的子项下标。</summary>
    public List<int> Children { get; } = [];

    /// <summary>真正命中并用来解压的密码。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>密码来源。</summary>
    public string PasswordSource { get; set; } = string.Empty;

    /// <summary>已解开的最深包层。不是动作序号。</summary>
    public int Layer { get; set; }

    /// <summary>用户的层数预算。界面画的是「第 N 层 / 上限 M」。</summary>
    public int MaxLayer { get; set; }

    /// <summary>逐层的（层号、目标名、成没成）。</summary>
    public List<(string Layer, string Target, string Outcome)> Timeline { get; } = [];

    /// <summary>停止原因的文案。</summary>
    public string StopReasonText { get; set; } = string.Empty;

    /// <summary>停止原因本身：写点只有结果映射那一处、读点只有可重跑判据那一处；重跑之前要清掉。</summary>
    public StopReason? StopReason { get; set; }

    /// <summary>停止原因的细节。</summary>
    public string ReasonDetail { get; set; } = string.Empty;

    /// <summary>重跑这一项之前要清掉的上一次结论。</summary>
    public void Reset()
    {
        State = JobState.Queued;
        Password = string.Empty;
        PasswordSource = string.Empty;
        Layer = 0;
        Seconds = 0;
        Reason = null;
        StoppedByUser = false;
        OutDir = null;
        OutputTop = null;
        Timeline.Clear();
        StopReasonText = string.Empty;
        StopReason = null;
        ReasonDetail = string.Empty;
    }
}
