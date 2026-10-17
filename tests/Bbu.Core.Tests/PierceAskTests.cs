using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Core.Tests;

/// <summary>
/// 询问通道的边界：没有通道时报「密码耗尽」，用户跳过时报「已跳过这个包」。
/// 两者不许合并 —— 把「没问过」写成「你跳过了」，是在替用户表态（B-2026-015）。
/// </summary>
[Collection("engine")]
public sealed class PierceAskTests
{
    /// <summary>没有询问通道（无头运行、或弹窗起不来）时，结论仍是密码耗尽。</summary>
    [Fact]
    [Trait("Bug", "B-2026-015")]
    public async Task WithoutChannel_MustReportPasswordExhausted()
    {
        using var sample = new EncryptedSample();
        var result = await sample.RunAsync(ask: null);

        Assert.Equal(StopReason.PasswordExhausted, result.StopReason);
    }

    /// <summary>用户跳过就报「已跳过这个包」，而且备注留空 —— 原因本身已经写着跳过了。</summary>
    [Fact]
    [Trait("Bug", "B-2026-015")]
    public async Task UserSkip_MustReportSkippedWithEmptyNote()
    {
        using var sample = new EncryptedSample();
        var asked = 0;
        var result = await sample.RunAsync((_, _) =>
        {
            asked++;
            return Task.FromResult<string?>(null);
        });

        Assert.Equal(1, asked);
        Assert.Equal(StopReason.PasswordSkipped, result.StopReason);
        Assert.True(
            string.IsNullOrEmpty(result.StopDetail),
            "跳过时备注必须留空，实际是「" + result.StopDetail + "」");

        // 用户看到的那一句与按钮同口径：跳掉的是整个包，不是「这一个文件」。
        Assert.Equal(PierceText.StopPasswordSkipped, result.StopReason.Label());
    }

    /// <summary>
    /// 询问通道自己坏掉（面板起不来一类）不许按「密码不对／密码已全部试完」收口：
    /// 那会把用户支到密码本里加密码，而真正的原因一个字都不会出现。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-244")]
    public async Task ChannelFailure_MustNotBeToldAsAWrongPassword()
    {
        using var sample = new EncryptedSample();
        const string reason = "面板起不来";
        var result = await sample.RunAsync((_, _) => throw new InvalidOperationException(reason));

        Assert.NotEqual(StopReason.PasswordExhausted, result.StopReason);
        Assert.Equal(PierceText.Fill(UiText.AskVerifyFailedAt, reason), result.StopDetail);
        Assert.NotEqual(PierceText.Label(StopReason.PasswordExhausted), result.StopDetail);
    }

    /// <summary>手输的密码不对时最多问三次，之后按密码耗尽收口，不无限追问。</summary>
    [Fact]
    [Trait("Bug", "B-2026-015")]
    public async Task WrongManualPassword_MustAskAgainThenGiveUp()
    {
        using var sample = new EncryptedSample();
        var attempts = new List<int>();
        var result = await sample.RunAsync((ask, _) =>
        {
            attempts.Add(ask.Attempt);
            return Task.FromResult<string?>("definitely-not-the-password");
        });

        // 在回调外断言：通道内部的异常会被当成「问不出来」吞掉，写在里面就查不出问题
        Assert.Equal([1, 2, 3], attempts);
        Assert.Equal(StopReason.PasswordExhausted, result.StopReason);
    }

    /// <summary>手里那条密码对得上时，同一个包必须能解开 —— 询问通道不是摆设。</summary>
    [Fact]
    [Trait("Bug", "B-2026-015")]
    public async Task CorrectManualPassword_MustUnlock()
    {
        using var sample = new EncryptedSample();
        var result = await sample.RunAsync((_, _) => Task.FromResult<string?>(EncryptedSample.Password));

        // 层里记着真正用上的那个密码：手输的那条必须一路走到解压，不能只是弹了个窗
        Assert.Contains(result.Layers, layer => layer.Password == EncryptedSample.Password);
        Assert.NotEqual(StopReason.PasswordExhausted, result.StopReason);
    }

    /// <summary>
    /// 只有「密码全试完」才值得问。把这一条改成恒真，下面整条立刻变红。
    /// </summary>
    [Fact]
    [Trait("Bug", "B-2026-015")]
    public void WorthsAskingUser_MustBeFalseForEveryOtherProblem()
    {
        foreach (var problem in new[]
                 {
                     UnlockProblem.EngineError,
                     UnlockProblem.MissingVolume,
                     UnlockProblem.Timeout,
                     UnlockProblem.Cancelled,
                     UnlockProblem.NotEncrypted,
                 })
        {
            Assert.False(
                new UnlockResult { Ok = false, Problem = problem }.WorthAskingUser,
                problem.ToString());
        }

        Assert.True(new UnlockResult { Ok = false, Problem = UnlockProblem.Exhausted }.WorthAskingUser);
        Assert.False(new UnlockResult { Ok = true }.WorthAskingUser);
    }

    /// <summary>
    /// 一份加密样本的临时副本。夹具目录是只读的：穿透默认把产物开在源文件旁边，
    /// 原地跑一次就把夹具污染了。
    /// </summary>
    private sealed class EncryptedSample : IDisposable
    {
        /// <summary>与  用的是同一份夹具，密码也就同一个。</summary>
        public const string Password = "BbuPw2026";

        private readonly string _directory;
        private readonly string _archive;

        public EncryptedSample()
        {
            _directory = Path.Combine(
                AppContext.BaseDirectory, "_test-tmp", "ask", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _archive = Path.Combine(_directory, "zip-aes256.zip");
            File.Copy(VaultUnlockTests.Vault1("zip-aes256.zip"), _archive);
        }

        public async Task<PierceResult> RunAsync(AskPasswordAsync? ask)
        {
            var vault = new Vault();
            vault.SetEntries(Array.Empty<string>());
            var options = new PierceOptions
            {
                MaxDepth = 1,
                MinFreeGb = 0,
                RemoveIntermediate = false,
                OutputRoot = _directory,
                Ask = ask,
                Log = new ChannelLog((_, _) => { }),
            };

            var piercer = new Piercer(TestPaths.NewEngine(), vault, options);
            return await piercer.RunAsync(_archive).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响断言结果
            }
        }
    }
}
