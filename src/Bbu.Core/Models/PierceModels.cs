using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bbu.Core.Texts;

namespace Bbu.Core.Models;

/// <summary>
/// 包层：压缩包套压缩包的层数。源包是第 1 层，包里的包是第 2 层，由用户的层数预算管。
/// 它不是「解了几个包」（动作序号），也不是目录层级。三类数各管一段，不得互换，
/// 所以层深单独一个类型：这样「这个数是包层还是动作序号」在签名上就看得出来。
/// </summary>
public readonly record struct PackLayer(int Value) : IComparable<PackLayer>
{
    /// <summary>源包那一层，也是目录入口里那些包的层。</summary>
    public static PackLayer First => new(1);

    public static PackLayer operator +(PackLayer layer, int step) => new(layer.Value + step);

    public static bool operator >(PackLayer left, PackLayer right) => left.Value > right.Value;

    public static bool operator <(PackLayer left, PackLayer right) => left.Value < right.Value;

    public static bool operator >=(PackLayer left, PackLayer right) => left.Value >= right.Value;

    public static bool operator <=(PackLayer left, PackLayer right) => left.Value <= right.Value;

    public int CompareTo(PackLayer other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 为什么停下来。十三个成员，每个都要有文案与状态分支。
/// 「安全停下」不是「做完了」—— 它的意思是「这不是异常」，由 <see cref="StopReasonTraits.IsCleanStop"/> 一处定义。
/// 把这个属性当成成功用会让命令行返回 0、界面显示成成功色；
/// </summary>
public enum StopReason
{
    /// <summary>这一层没有可解压的包，内层也没找到。</summary>
    NoArchive,

    /// <summary>这一层有多个伪装包，分不清先解哪一个。</summary>
    Ambiguous,

    /// <summary>引擎报错，或队列里的包已不在。</summary>
    ExtractFailed,

    /// <summary>密码全部试完，一个都没命中。</summary>
    PasswordExhausted,

    /// <summary>问过用户，用户选择跳过这个包。</summary>
    PasswordSkipped,

    /// <summary>层预算用尽，这一层还有包没解。</summary>
    MaxDepth,

    /// <summary>解压前按包声明的体积判定空间不够，还没起过引擎。</summary>
    NoSpace,

    /// <summary>内容与已解过的包逐字节相同，跳过。</summary>
    AlreadyVisited,

    /// <summary>用户点了停止。</summary>
    Cancelled,

    /// <summary>产物目录已存在，冲突策略是跳过。</summary>
    OutputExists,

    /// <summary>往下找内层包没找完。它是安全停下，但确实有内容没解。</summary>
    SearchIncomplete,

    /// <summary>引擎报成功，产物目录却没有新增内容。</summary>
    EmptyOutput,

    /// <summary>解压中磁盘余量跌破下限，被主动中止。</summary>
    SpaceGuard,
}

/// <summary>停止原因的两条分类。判据只有这里一处，别处不许另立名单。</summary>
public static class StopReasonTraits
{
    /// <summary>属于「安全停下」而非「出错」的停止原因。</summary>
    public static bool IsCleanStop(this StopReason reason) => reason is
        StopReason.NoArchive
        or StopReason.MaxDepth
        or StopReason.AlreadyVisited
        or StopReason.Ambiguous
        or StopReason.Cancelled
        or StopReason.OutputExists
        or StopReason.SearchIncomplete;

    /// <summary>这一档的结果能不能对外报成功。撞层数上限不算：那一刻必然还有包没解。</summary>
    public static bool CountsAsDone(this StopReason reason) =>
        reason.IsCleanStop() && reason is not StopReason.MaxDepth;

    /// <summary>取文案。出处是 <see cref="PierceText.Label"/>。</summary>
    public static string Label(this StopReason reason) => PierceText.Label(reason);
}

/// <summary>一层穿透的结果。字段与 1.1.0 的 <c>LayerResult</c> 一一对应。</summary>
public sealed record LayerResult
{
    /// <summary>这一层的包层，不是动作序号。</summary>
    public required PackLayer Depth { get; init; }

    public required string Target { get; init; }

    /// <summary>收壳之后可能被改写，所以可写。</summary>
    public required string OutDir { get; set; }

    public required bool Ok { get; init; }

    /// <summary>引擎报了警告退出码（有东西没解全，引擎没报错）。这一层算解出来了，整趟结果落「部分完成」。</summary>
    public bool Warned { get; init; }

    public double Seconds { get; init; }

    public string PasswordOrigin { get; init; } = string.Empty;

    /// <summary>这一层真正用上的密码，给界面显示与复制用。</summary>
    public string Password { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    /// <summary>上提半搬时已经搬走的条目名，只在搬移中途失败时非空；不做事务化，只如实报出来。</summary>
    public IReadOnlyList<string> PartialLift { get; init; } = [];

    /// <summary>仅在失败时给出，结构化，不靠猜字符串。</summary>
    public StopReason? Reason { get; init; }
}

/// <summary>一整趟穿透的结果。对上层只暴露这个对象，界面的状态映射与命令行的退出码都从它出。</summary>
public sealed class PierceResult
{
    /// <summary>
    /// 「这一趟不算失败」，不是「做完了」：含取消、已跳过、多解歧义，不含撞层数上限。
    /// 取值域见 <see cref="StopReasonTraits.CountsAsDone"/>，改它会连坐到界面状态与退出码。
    /// </summary>
    public bool Ok { get; set; }

    public List<LayerResult> Layers { get; } = [];

    public StopReason StopReason { get; set; } = StopReason.NoArchive;

    public string StopDetail { get; set; } = string.Empty;

    /// <summary>最深一层的产物目录。没有解开任何一层时是空串。</summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>
    /// 这一趟没解开的包，相对于 <see cref="OutputDir"/>。
    /// 单列一个字段是因为 <see cref="Ok"/> 只说明「安全停下了」，
    /// 而到上限、多解歧义、判重跳过这三种停法都是「有内容确实没解」。
    /// </summary>
    public List<string> Leftover { get; } = [];

    /// <summary>解压动作次数。不是层深，同层多包时它大于层深。</summary>
    public int MaxDepthReached => Layers.Count;

    /// <summary>已经解开的最深包层。界面的层号只能用这一个。</summary>
    public PackLayer DeepestLayer =>
        Layers.Count == 0 ? new PackLayer(0) : Layers.Max(layer => layer.Depth);

    /// <summary>引擎报过警告（退出码 1）那一层留下的那句话。空串表示这一趟没有警告。</summary>
    public string EngineWarning =>
        Layers.FirstOrDefault(layer => layer.Warned)?.Note ?? string.Empty;

    /// <summary>有内容没解、没找完，或引擎报了警告退出码。撞层数上限与「没找完」那两档不必看残留名单。</summary>
    public bool Partial =>
        EngineWarning.Length > 0
        || StopReason is StopReason.MaxDepth or StopReason.SearchIncomplete
        || (Ok && Leftover.Count > 0);

    /// <summary>给日志面板与命令行用的一段结论。两处必须同口径。</summary>
    public string Summary()
    {
        string state;
        if (Partial)
        {
            state = Leftover.Count > 0
                ? PierceText.Fill(PierceText.SummaryPartialCountAt, Leftover.Count)
                : States.Partial;
        }
        else if (StopReason is StopReason.Cancelled)
        {
            // `Ok` 是「安全停下」，取消也算，所以这里不能跟着它说「完成」。
            state = Errors.Stopped;
        }
        else if (StopReason is StopReason.OutputExists)
        {
            // 同理：按设置跳过不等于做完了。
            state = States.Skipped;
        }
        else
        {
            state = Ok ? States.Done : PierceText.StateUnfinished;
        }

        // 引擎警告那一档由警告句自己说：拿停止原因凑会写出「解压失败」，与「引擎没报错」相反。
        var label = EngineWarning.Length > 0 ? EngineWarning : StopReason.Label();
        var head = PierceText.Fill(PierceText.SummaryHeadAt, DeepestLayer.Value, state);
        if (label.Length > 0 && label != state)
        {
            head += PierceText.Fill(PierceText.SummaryReasonAt, label);
        }

        return StopDetail.Length > 0
            ? head + PierceText.Fill(PierceText.SummaryDetailAt, StopDetail)
            : head;
    }
}

/// <summary>在某个目录里挑出来的下一步目标。</summary>
public sealed class PickResult
{
    public string? Path { get; set; }

    /// <summary>多个伪装包，分不清主次。</summary>
    public List<string> Ambiguous { get; } = [];

    public string Note { get; set; } = string.Empty;

    /// <summary>同一层有多个互不相关的包时全放这里，调用方会逐个都解。</summary>
    public List<string> Targets { get; } = [];

    /// <summary>往下找时没找完。置真时调用方必须如实报，不许当成「没有可解压的压缩包」。</summary>
    public bool SearchIncomplete { get; set; }

    /// <summary>安全停下但确实没解的东西，调用方要登记进残留名单。</summary>
    public List<string> Unfinished { get; } = [];

    /// <summary>没找完的原因：保险丝烧了，还是有子目录读不了。两种不许共用一句。</summary>
    public string SearchDetail { get; set; } = string.Empty;

    public bool Found => Path is not null || Targets.Count > 0;
}
