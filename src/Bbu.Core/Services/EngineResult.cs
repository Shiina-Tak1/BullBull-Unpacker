using System;
using System.Collections.Generic;
using System.Linq;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 一次引擎调用的结果。四路终止各有独立标志（取消、超时、事中中止、事前拒绝），不许只用
/// 一个 <c>Ok</c> 表达：用户动作完全不同，一句「失败」说不清该补空间还是该改密码；<c>Code</c> 在没起过引擎时不是引擎给的数。
/// </summary>
public sealed record EngineResult
{
    /// <summary>引擎退出码，或本程序的负数码，见 <see cref="EngineExitCodes"/>。</summary>
    public int Code { get; init; }

    public string Output { get; init; } = string.Empty;

    /// <summary>只保留标准输出：判「引擎列得出条目吗」必须用它，<see cref="Output"/> 里并着 stderr。</summary>
    public string StdOut { get; init; } = string.Empty;

    public double Seconds { get; init; }

    /// <summary>实际下发的命令行，原样保留（含 <c>-p&lt;密码&gt;</c>），失败时用来复核。</summary>
    public IReadOnlyList<string> Command { get; init; } = [];
    /// <summary>进程都没起来。</summary>
    public bool LaunchFailed { get; init; }

    /// <summary>用户要求停止，进程已被杀掉。</summary>
    public bool Cancelled { get; init; }

    /// <summary>超过时限，进程已被杀掉。</summary>
    public bool TimedOut { get; init; }

    /// <summary>解压中磁盘余量跌破下限，被主动中止。</summary>
    public bool SpaceGuardHit { get; init; }

    /// <summary>解压前按包的声明体积判定空间不够，**没起过引擎**。</summary>
    public bool OutOfSpace { get; init; }

    /// <summary>两条输出流在上限内收尾了没有；假表示没等到收尾，输出因此是空的（外部审计第 1 轮 `D-5`）。</summary>
    public bool OutputDrained { get; init; } = true;

    /// <summary>
    /// 失败原因是不是「密码错误」。由引擎退出码与输出特征词判定，
    /// 弱证据（CRC / 校验和字样）还要再确认这个包真的加密了才算数。
    /// </summary>
    public bool WrongPassword { get; init; }

    /// <summary>引擎退出码 0 且没有任何终止标志，才算成功。</summary>
    public bool Ok =>
        !LaunchFailed
        && !Cancelled
        && !TimedOut
        && !SpaceGuardHit
        && !OutOfSpace
        && EngineExitCodes.IsSuccess(Code);

    /// <summary>引擎报了警告退出码：有东西没解全，但引擎没报错、也没被本实现中止。这一档按「部分完成」收口。</summary>
    public bool Warned =>
        EngineExitCodes.IsWarning(Code)
        && !Cancelled
        && !TimedOut
        && !SpaceGuardHit
        && !OutOfSpace
        && !WrongPassword;

    public bool EngineMissing => Code == EngineExitCodes.EngineMissingCode;

    public bool EntryNotMatched => Code == EngineExitCodes.EntryNotMatchedCode;

    public bool OutDirFailed => Code == EngineExitCodes.OutDirFailedCode;

    /// <summary>输出的最后几行，用于日志与详情页。多行之间用中文分号连成**一行**：日志的判据是每一行都带时间戳与级别标签，正文里不许出现换行。</summary>
    public string Tail
    {
        get
        {
            var lines = Output.Split('\n')
                .Select(line => line.TrimEnd('\r').Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            return string.Join("；", lines.TakeLast(3));
        }
    }

    /// <summary>结论。失败那一档是「中文归类 ＋ 引擎原话」两半，界面与日志都取这里的词，不各自编一套。</summary>
    public string Brief
    {
        get
        {
            if (Cancelled)
            {
                return Errors.Stopped;
            }

            if (TimedOut)
            {
                return Errors.Timeout;
            }

            if (SpaceGuardHit)
            {
                return Errors.SpaceGuardAborted;
            }

            if (OutOfSpace)
            {
                return Errors.SpaceNotEnough;
            }

            if (WrongPassword)
            {
                return Errors.ExitPassword;
            }

            if (Warned)
            {
                return string.Format(Format.EngineWarningExitCodeAt, Code);
            }

            if (Ok)
            {
                return string.Format(Format.EngineOkSeconds, Seconds);
            }

            // 失败：中文归类在前、原话照抛在后，两半都要在；判不出归类时只留原话，不编结论出来。
            var headline = EngineOutput.Conclusion(Output);
            var said = Tail;
            if (headline.Length > 0)
            {
                return said.Length > 0
                    ? string.Format(Format.ConclusionWithOutputAt, headline, said)
                    : headline;
            }

            if (said.Length > 0)
            {
                return said;
            }

            // 兜底：已经没有可读的原话了。负数段那些是本程序自己记的数，写成「退出码 N」用户查不到。
            return Code < 0
                ? string.Format(Format.LocalCodeNotExitCodeAt, Code)
                : string.Format(Format.EngineFailedExitCode, Code);
        }
    }
}
