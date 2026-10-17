using System.Linq;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 退出码语义。唯一出处是两张模块级表。
///
/// 两条容易做错的：rar 的密码错误是 11（有时表现为 3），而 7z 的密码错也走 2，
/// 不得按「非零即失败」处理；负数退出码归本程序，引擎自己一律不小于 0。
///
/// UnRAR 那张表现行 17 项（0 至 15 加 255）。2026-10-07 按 7.23 补到 15 项，
/// 2026-10-08 按 7.30 再补 14 与 15 —— 官方表的码位数**随版本长**。
/// 补之前只登记了七项，缺分卷落在 6、掩码不匹配落在 10，界面上读作「未知退出码 N」。
/// </summary>
public sealed class EngineExitCodeTests
{
    [Fact]
    public void EngineTables_MustCarryEveryCodeWithMeaning()
    {
        // 「5 个码」与「16 个码」指非零的那些：0 是成功，两边的语义相同。
        Assert.Equal(new[] { 1, 2, 7, 8, 255 }, EngineExitCodes.SevenZip.Keys.Where(code => code != 0).OrderBy(code => code));
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 255 },
            EngineExitCodes.UnRar.Keys.Where(code => code != 0).OrderBy(code => code));
        Assert.All(EngineExitCodes.SevenZip.Values, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.All(EngineExitCodes.UnRar.Values, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(EngineExitCodes.SevenZip[0], EngineExitCodes.UnRar[0]);
    }

    /// <summary>
    /// 官方那 16 个非零码逐条在场，且都给出本仓的释义。删掉补全的码，这一条即红 ——
    /// 表少一个码时 <c>Describe</c> 会落到 <c>Format.UnknownExitCode</c>，界面读作「未知退出码 N」。
    /// </summary>
    [Fact]
    public void UnRarTable_MustCarryEveryCodeFromTheOfficialTable()
    {
        int[] official = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 255];
        Assert.Equal(official, EngineExitCodes.UnRar.Keys.OrderBy(code => code));

        foreach (var code in official.Where(code => code != 0))
        {
            var text = EngineExitCodes.Describe(code, EngineKind.UnRar);
            Assert.DoesNotContain("未知", text, System.StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }

        // 两档真实撞得到的：缺分卷是 6，条目掩码没匹配上是 10；7.30 新增的两档也不能是「未知」。
        Assert.Equal(Errors.ExitOpenFile, EngineExitCodes.Describe(6, EngineKind.UnRar));
        Assert.Equal(Errors.ExitNoMatch, EngineExitCodes.Describe(10, EngineKind.UnRar));
        Assert.Equal(Errors.ExitDelete, EngineExitCodes.Describe(14, EngineKind.UnRar));
        Assert.Equal(Errors.ExitLimit, EngineExitCodes.Describe(15, EngineKind.UnRar));
    }

    [Fact]
    public void PasswordAndCrcCodes_MustNotBeScrambled()
    {
        Assert.Equal(11, EngineExitCodes.RarWrongPasswordCode);
        Assert.Equal(Errors.ExitPassword, EngineExitCodes.UnRar[EngineExitCodes.RarWrongPasswordCode]);
        Assert.Equal(Errors.ExitCrc, EngineExitCodes.UnRar[3]);
        Assert.DoesNotContain(11, EngineExitCodes.SevenZip.Keys);
        Assert.DoesNotContain(3, EngineExitCodes.SevenZip.Keys);
        Assert.Equal(Errors.ExitFatalPassword, EngineExitCodes.SevenZip[2]);
    }

    [Fact]
    public void OwnNegativeCodes_MustBeDistinguishable()
    {
        var own = new[] { -1, -2, -3, -4 };
        Assert.All(own, code => Assert.DoesNotContain(code, EngineExitCodes.SevenZip.Keys));
        Assert.Equal(Errors.CannotLaunchEngine, EngineExitCodes.Describe(-1, EngineKind.SevenZip));
        Assert.Equal(Errors.EngineMissing, EngineExitCodes.Describe(-2, EngineKind.UnRar));
        Assert.Equal(Errors.EntryNotMatched, EngineExitCodes.Describe(-3, EngineKind.SevenZip));
        Assert.Equal(Errors.OutputDirFailed, EngineExitCodes.Describe(-4, EngineKind.UnRar));
        Assert.Equal(string.Format(Format.UnknownExitCode, 42), EngineExitCodes.Describe(42, EngineKind.SevenZip));
        Assert.True(EngineExitCodes.IsSuccess(0));
        Assert.True(EngineExitCodes.IsWarning(1));
        Assert.False(EngineExitCodes.IsSuccess(1));
    }

    [Fact]
    public void Result_MustSeparateTheFourWaysToStop()
    {
        Assert.True(new EngineResult { Code = 0 }.Ok);
        Assert.False(new EngineResult { Code = 1 }.Ok);
        Assert.False(new EngineResult { Code = 0, Cancelled = true }.Ok);
        Assert.False(new EngineResult { Code = 0, TimedOut = true }.Ok);
        Assert.False(new EngineResult { Code = 0, SpaceGuardHit = true }.Ok);
        Assert.False(new EngineResult { Code = 0, OutOfSpace = true }.Ok);
        Assert.False(new EngineResult { Code = 0, LaunchFailed = true }.Ok);
        Assert.True(new EngineResult { Code = EngineExitCodes.EngineMissingCode }.EngineMissing);
        Assert.True(new EngineResult { Code = EngineExitCodes.EntryNotMatchedCode }.EntryNotMatched);
        Assert.True(new EngineResult { Code = EngineExitCodes.OutDirFailedCode }.OutDirFailed);
    }

    [Fact]
    public void Brief_MustUseTheOneSetOfWording()
    {
        Assert.Equal(Errors.Stopped, new EngineResult { Code = 1, Cancelled = true }.Brief);
        Assert.Equal(Errors.Timeout, new EngineResult { Code = 1, TimedOut = true }.Brief);
        Assert.Equal(Errors.SpaceGuardAborted, new EngineResult { Code = 1, SpaceGuardHit = true }.Brief);
        Assert.Equal(Errors.SpaceNotEnough, new EngineResult { Code = 0, OutOfSpace = true }.Brief);
        Assert.Equal(Errors.ExitPassword, new EngineResult { Code = 2, WrongPassword = true }.Brief);
        Assert.Equal(string.Format(Format.EngineFailedExitCode, 2), new EngineResult { Code = 2 }.Brief);
    }

    /// <summary>
    /// 退出码 1 是警告不是失败：结语走警告那一句，并且落进生产路径读的那个判据（<c>Warned</c>），
    /// 这一趟才可能按「部分完成」收口。被中止的那几档优先，它们的结语不许被警告码盖掉。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-228")]
    public void WarningCode_MustNotBeWordedAsFailure()
    {
        var warned = new EngineResult { Code = 1 };

        Assert.True(warned.Warned);
        Assert.Equal(string.Format(Format.EngineWarningExitCodeAt, 1), warned.Brief);
        Assert.DoesNotContain("失败", warned.Brief, System.StringComparison.Ordinal);

        Assert.False(new EngineResult { Code = 1, Cancelled = true }.Warned);
        Assert.Equal(Errors.Stopped, new EngineResult { Code = 1, Cancelled = true }.Brief);
        Assert.False(new EngineResult { Code = 1, TimedOut = true }.Warned);
        Assert.Equal(Errors.Timeout, new EngineResult { Code = 1, TimedOut = true }.Brief);

        // 对照：2 是真失败，另一句话，也不走警告那一档。
        Assert.False(new EngineResult { Code = 2 }.Warned);
        Assert.Equal(string.Format(Format.EngineFailedExitCode, 2), new EngineResult { Code = 2 }.Brief);
    }

    /// <summary>
    /// 失败那一档的结论是「中文归类 ＋ 引擎原话」两半：归类说清是哪一类失败，原话照抛不改。
    /// 只剩原话时用户看不出这是哪一类；把原话吞掉他又没有复核的依据。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-233")]
    public void FailureBrief_MustCarryBothTheKindAndTheEngineWords()
    {
        var structural = new EngineResult
        {
            Code = 2,
            Output = "a\nERROR: Cannot open the file as archive\n",
        };

        Assert.StartsWith(Errors.EngineCannotReadArchive, structural.Brief, System.StringComparison.Ordinal);
        Assert.Equal(
            string.Format(Format.ConclusionWithOutputAt, Errors.EngineCannotReadArchive, structural.Tail),
            structural.Brief);
        Assert.Contains("Cannot open the file as archive", structural.Brief, System.StringComparison.Ordinal);

        // 七个特征词域各归各的类：给的是哪一类原话，结论就说哪一类。
        Assert.StartsWith(Errors.ExitPassword, Words("Wrong password").Brief, System.StringComparison.Ordinal);
        Assert.StartsWith(Errors.EntryNotMatched, Words("No files to process").Brief, System.StringComparison.Ordinal);
        Assert.StartsWith(Errors.VolumeIncomplete, Words("Cannot find volume x.002").Brief, System.StringComparison.Ordinal);
        Assert.StartsWith(Errors.EncryptedHeader, Words("Enter password").Brief, System.StringComparison.Ordinal);
        Assert.StartsWith(Errors.ChecksumMismatch, Words("CRC Failed").Brief, System.StringComparison.Ordinal);
        Assert.StartsWith(
            Errors.SameNameBlocksTheLayer,
            Words("Cannot delete output folder").Brief,
            System.StringComparison.Ordinal);

        // 边界：特征词一个都不命中时只有原文，不许加一句编出来的结论。
        Assert.Equal("some unrelated diagnostic", Words("some unrelated diagnostic").Brief);

        // 连可读的原话都没有时才回到码那一档。
        Assert.Equal(string.Format(Format.EngineFailedExitCode, 2), new EngineResult { Code = 2 }.Brief);
    }

    /// <summary>
    /// 本程序自己记的负数码不许当成引擎的退出码显示：用户会拿它去查引擎的码表，而表里没有这些数。
    /// 引擎自己给的码（含警告码 1）照旧，那是查得到的。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-234")]
    public void OwnNegativeCode_MustNotReadAsAnEngineExitCode()
    {
        var own = new EngineResult { Code = EngineExitCodes.OutDirFailedCode };

        Assert.Equal(string.Format(Format.LocalCodeNotExitCodeAt, EngineExitCodes.OutDirFailedCode), own.Brief);
        Assert.DoesNotContain("退出码 -4", own.Brief, System.StringComparison.Ordinal);

        Assert.Equal(string.Format(Format.EngineFailedExitCode, 2), new EngineResult { Code = 2 }.Brief);
        Assert.Equal(string.Format(Format.EngineWarningExitCodeAt, 1), new EngineResult { Code = 1 }.Brief);
    }

    /// <summary>一份带原话的失败结果：特征词就在这段原话里。</summary>
    private static EngineResult Words(string output) => new() { Code = 2, Output = output };

    /// <summary>最后几行要留全，但连成一行：正文里带换行会落出没有时间戳的续行。</summary>
    [Fact]
    public void Tail_MustKeepTheLastMeaningfulLinesOnOneLine()
    {
        var result = new EngineResult { Code = 2, Output = "a\nb \nc\nd\n" };
        Assert.Equal("b；c；d", result.Tail);
    }
}
