using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>这一层试密码的结论。要么给出密码，要么给出一条失败与它的原因。</summary>
internal sealed record PasswordOutcome(
    string? Password,
    string Origin,
    string? RememberValue,
    string FailureNote,
    StopReason? FailureReason)
{
    internal static PasswordOutcome Hit(string password, string origin, string? rememberValue) =>
        new(password, origin, rememberValue, string.Empty, null);

    internal static PasswordOutcome Fail(string note, StopReason reason) =>
        new(null, string.Empty, null, note, reason);
}

/// <summary>
/// 每层的密码解析：先试外层那个成功的密码，再走密码层。
///
/// 两个要点。一是「明确没加密」在调用方就判掉了，这里只管真要试密码的情形。二是只有「密码全试完」才值得问用户；缺分卷、包损坏问了也没有结果。询问通道由调用方注入，没注入就照旧报「密码耗尽」。
/// </summary>
public sealed partial class Piercer
{
    private async Task<PasswordOutcome> ResolvePasswordAsync(
        string source,
        EngineKind kind,
        ArchiveInfo info,
        PackLayer depth,
        string? passwordHint,
        string outdir)
    {
        HashSet<string> skipped = [];
        string? password = null;
        var origin = string.Empty;

        // 优先复用外层成功的密码：同一个包的多层常常用同一个密码。
        if (passwordHint is not null)
        {
            var probe = await _engine.TestAsync(source, passwordHint, kind, info.FirstEntry, _cancellation, reportPercent: false)
                .ConfigureAwait(false);
            if (probe.Ok)
            {
                password = passwordHint;
                origin = PierceText.OriginReused;
                _log?.Write(new LogRecord(LogLevel.Info, PierceText.ReusedPasswordAt, depth.Value));
            }
            else
            {
                skipped.Add(passwordHint);
            }
        }

        if (password is not null)
        {
            return PasswordOutcome.Hit(password, origin, null);
        }

        var unlock = await _vault.UnlockAsync(
            _engine,
            source,
            new UnlockOptions
            {
                Workers = _options.Workers,
                Layer = depth.Value,
                Skip = skipped,
                Pause = _options.Pause,
            },
            _options.FastPath,
            _cancellation).ConfigureAwait(false);

        if (unlock.Ok)
        {
            _log?.Write(new LogRecord(LogLevel.Info, unlock.Summary(), depth.Value));
            var hitOrigin = unlock.Candidate is not null
                ? PasswordCandidate.OriginLabel(unlock.Candidate.Origin)
                : PierceText.OriginBook;
            var remember = unlock.Candidate is { SourceValue.Length: > 0 } candidate
                ? candidate.SourceValue
                : null;
            return PasswordOutcome.Hit(unlock.Password ?? string.Empty, hitOrigin, remember);
        }

        _log?.Write(new LogRecord(LogLevel.Warn, unlock.Summary(), depth.Value));

        // 只有「密码全试完」才值得问。用户跳过与「根本没有这条通道」是两件事，结论也两样。
        if (unlock.WorthAskingUser && _options.Ask is not null)
        {
            var asked = await AskUserAsync(source, kind, info, depth, unlock).ConfigureAwait(false);
            if (asked is not null)
            {
                return asked;
            }
        }

        // 提示语要说清是哪一种没解开：只有密码本全试完那种才值得建议他手动再解一次，引擎报错时说那一句是误导。
        var reason = unlock.Problem is UnlockProblem.Cancelled
            ? StopReason.Cancelled
            : unlock.WorthAskingUser ? StopReason.PasswordExhausted : StopReason.ExtractFailed;

        if (reason is StopReason.PasswordExhausted)
        {
            _log?.Problem(
                PierceText.NotUnlockedAskAgainAt, Path.GetFileName(source), outdir);
        }
        else if (reason is not StopReason.Cancelled)
        {
            _log?.Problem(
                PierceText.NotUnlockedWhyAt,
                Path.GetFileName(source),
                unlock.StoppedReason.Length > 0 ? unlock.StoppedReason : PierceText.StopExtractFailed,
                outdir);
        }

        // 取消时备注留空：原因本身已经写着「已停止」，再填一遍就是「已停止（已停止）」。
        return PasswordOutcome.Fail(
            reason is StopReason.Cancelled ? string.Empty : unlock.StoppedReason, reason);
    }

    /// <summary>手输密码最多问几次。问满之后仍不对就按密码耗尽收口，不无限追问。</summary>
    private const int ManualAskLimit = 3;

    /// <summary>
    /// 问用户要密码，每条都要用引擎验过才往下走；未验过的密码不得交出去。
    /// 返回 <c>null</c> 表示问满次数仍不对，由调用方按密码耗尽收口；通道坏掉另成一档，见下。
    /// </summary>
    private async Task<PasswordOutcome?> AskUserAsync(
        string source,
        EngineKind kind,
        ArchiveInfo info,
        PackLayer depth,
        UnlockResult unlock)
    {
        var tried = unlock.Tries;
        for (var attempt = 1; attempt <= ManualAskLimit; attempt++)
        {
            string? manual;
            try
            {
                manual = await _options.Ask!(
                    new PasswordAsk(source, Path.GetFileName(source), tried, attempt, unlock.Summary()),
                    _cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 询问是旁路：通道坏掉不许把异常带走，但也不能说成「密码不对」——那会把用户支到密码本里加密码，真正的原因反而被盖住。这一趟的取消走面板自己的出口，不算通道坏掉。
                return ex is OperationCanceledException
                    ? null
                    : PasswordOutcome.Fail(
                        PierceText.Fill(Ui.AskVerifyFailedAt, ex.Message), StopReason.ExtractFailed);
            }

            if (string.IsNullOrEmpty(manual))
            {
                return PasswordOutcome.Fail(string.Empty, StopReason.PasswordSkipped);
            }

            var probe = await _engine.TestAsync(source, manual, kind, info.FirstEntry, _cancellation, reportPercent: false)
                .ConfigureAwait(false);
            if (probe.Ok)
            {
                _log?.Write(new LogRecord(LogLevel.Info, PierceText.ManualPasswordAcceptedAt, depth.Value));
                return PasswordOutcome.Hit(manual, PierceText.OriginManual, manual);
            }

            tried++;
        }

        return null;
    }

    /// <summary>
    /// 解压真的成功之后才记住这个密码，比「验证通过」更可信。
    ///
    /// 记的是<b>用户写的那一条</b>：命中变体时记变体值会在密码本里凭空多一条用户没写过的密码，
    /// 而他原来那条永远是零次。写盘失败必须说出来，否则「越用越准」悄悄失效——解压本身照样成功，所以只报一句，不改这一层的结果。
    /// </summary>
    private void RememberPassword(PackLayer depth, string value)
    {
        if (_vault.Remember(value))
        {
            _log?.Write(new LogRecord(LogLevel.Info, PierceText.PasswordRememberedAt, depth.Value));
            return;
        }

        if (_vault.LastWriteError.Length > 0)
        {
            _log?.Write(new LogRecord(
                LogLevel.Warn,
                PierceText.Fill(VaultText.RememberWriteFailed, _vault.LastWriteError),
                depth.Value));
        }
    }
}
