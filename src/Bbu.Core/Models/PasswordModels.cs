using System;
using System.Collections.Generic;
using System.Text;
using Bbu.Core.Texts;

namespace Bbu.Core.Models;

/// <summary>密码来源，同时也是候选顺序。成员顺序即档位顺序，不许重排。</summary>
public enum PasswordOrigin
{
    /// <summary>文件名或文件夹名里读出来的。免费、精确，静默生效。</summary>
    FileName = 0,

    /// <summary>密码本里的，按成功次数降序。</summary>
    Book = 1,

    /// <summary>空密码，兜底。</summary>
    Empty = 2,

    /// <summary>本次由用户手动输入。只用于显示来源，不进候选。</summary>
    Manual = 3,
}

/// <summary>密码本里的一条。</summary>
public sealed record VaultEntry(string Password, int Hits = 0);

/// <summary>
/// 一个待尝试的密码。
///
/// <paramref name="SourceValue"/> 是这条候选**从哪一条原文展开出来的**：变体放大之后，
/// 命中变体时必须把成功次数记回原条目，否则密码本里会多出一条用户从没写过的密码，
/// 而他原来那条永远停在 0 次（B-2026-057）。空串表示这条本身就是原文。
/// </summary>
public sealed record PasswordCandidate(
    string Value,
    PasswordOrigin Origin,
    string Detail = "",
    string SourceValue = "")
{
    /// <summary>展开变体时记回原条目；不是变体时就是自己。</summary>
    public string RememberValue => SourceValue.Length > 0 ? SourceValue : Value;

    /// <summary>展示用的一行：来源加取值。密码明文显示，不打码。</summary>
    public string Describe()
    {
        var shown = Value.Length > 0 ? Value : VaultText.EmptyPasswordShown;
        var label = OriginLabel(Origin);
        return Detail.Length > 0
            ? string.Format("{0}：{1}（{2}）", label, shown, Detail)
            : string.Format("{0}：{1}", label, shown);
    }

    /// <summary>来源的中文名。索引与枚举成员顺序一一对应。</summary>
    public static string OriginLabel(PasswordOrigin origin)
    {
        var index = (int)origin;
        return index >= 0 && index < VaultText.OriginLabels.Count
            ? VaultText.OriginLabels[index]
            : string.Empty;
    }
}

/// <summary>试密码失败的原因，决定上层该不该再问用户要密码。</summary>
public enum UnlockProblem
{
    /// <summary>密码全试完，只可能是密码不对，值得问用户。</summary>
    Exhausted,

    /// <summary>包损坏或引擎缺失，问用户也没用。</summary>
    EngineError,

    /// <summary>分卷不全，去把分卷凑齐。</summary>
    MissingVolume,

    Timeout,

    /// <summary>用户点了停止。</summary>
    Cancelled,

    /// <summary>明确没加密。这不是失败：一个密码都不该试，也不许报密码错。</summary>
    NotEncrypted,
}

/// <summary>解锁结果，直接可以交给界面。</summary>
public sealed record UnlockResult
{
    public required bool Ok { get; init; }

    public string? Password { get; init; }

    public PasswordCandidate? Candidate { get; init; }

    public IReadOnlyList<PasswordCandidate> Tried { get; init; } = [];

    public string StoppedReason { get; init; } = string.Empty;

    /// <summary>判不了时为 <c>null</c>，此时仍按「密码问题」措辞。</summary>
    public UnlockProblem? Problem { get; init; }

    public int Tries => Tried.Count;

    /// <summary>只有「密码全试完」才值得弹窗让用户手输。缺分卷、包损坏这类输多少都没用。</summary>
    public bool WorthAskingUser => !Ok && Problem is UnlockProblem.Exhausted;

    /// <summary>
    /// 「密码不对」这句话只在真的是密码不对时才说：包损坏或分卷不全时说出来，
    /// 会把用户引到密码本里加密码，真正的原因被盖住（B-2026-027）。
    /// </summary>
    public string Summary()
    {
        if (Ok && Candidate is not null)
        {
            var shown = Candidate.Value.Length > 0 ? Candidate.Value : VaultText.EmptyPasswordShown;
            return string.Format(
                VaultText.HitSummary, PasswordCandidate.OriginLabel(Candidate.Origin), shown, Tries);
        }

        if (Problem is null or UnlockProblem.Exhausted)
        {
            // 停止理由不拼进来：这一支里它只会是「密码已全部试完」，与「均不正确」重复（裁）。
            return string.Format(VaultText.MissedSummary, Tries);
        }

        return StoppedReason.Length > 0 ? StoppedReason : VaultText.NotUnlocked;
    }
}

/// <summary>一次解锁的选项。</summary>
public sealed record UnlockOptions
{
    /// <summary>并行路数。0 表示自动，仍受便宜路径与逻辑核数夹取。</summary>
    public int Workers { get; init; }

    /// <summary>层号，只用于心跳与日志。层号文字由日志出口按它施加，调用点不自己拼「第 N 层」。</summary>
    public int Layer { get; init; }

    /// <summary>跳过调用方已经确认无效的密码，例如外层层已经试过的那一个。</summary>
    public IReadOnlySet<string>? Skip { get; init; }

    /// <summary>每试一个候选回调一次。回调抛异常不许影响搜索。</summary>
    public Action<PasswordCandidate, Services.EngineResult>? OnTry { get; init; }

    /// <summary>
    /// 「现在该暂停吗」，与 <c>ExtractOptions.Pause</c> 同形。不传表示这一趟不支持暂停。
    /// 在候选边界上问它；正在跑的那一次引擎验证靠它挂起子进程。
    /// </summary>
    public Func<bool>? Pause { get; init; }
}

/// <summary>快路的结论。只有两种取值，永远不含「正确」。</summary>
public enum FastPathVerdict
{
    /// <summary>判不出来，照原路交给引擎。</summary>
    Unknown = 0,

    /// <summary>这个密码肯定不对，不必起引擎。</summary>
    ProbablyWrong = 1,
}
