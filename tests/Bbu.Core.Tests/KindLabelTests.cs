using Bbu.Core.Models;
using Bbu.Core.Services;
using Bbu.Core.Texts;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 清单类型列的短标签：列里画得下、全称不丢。
///
/// 类型列宽只放得下 2 至 4 字，塞长文案必然截断，截断之后信息量为零。
/// 级别那一半已随级别表退役（2026-10-15）：一句文案的档改由调用出口的名字承载，逐条登记的判据
/// 没有对象了；各出口自己的断言接了大部分，三句失败档文案仍没有。
/// </summary>
public sealed class KindLabelTests
{
    /// <summary>格式名短标签：最多四字。</summary>
    [Theory]
    [InlineData(ArchiveFormat.Zip, "ZIP")]
    [InlineData(ArchiveFormat.SevenZip, "7z")]
    [InlineData(ArchiveFormat.Rar, "RAR")]
    [InlineData(ArchiveFormat.Rar5, "RAR")]
    [InlineData(ArchiveFormat.Tar, "tar")]
    [InlineData(ArchiveFormat.GZip, "gz")]
    [InlineData(ArchiveFormat.Mp4, "视频")]
    public void ShortFormat_MustFitInsideFourCharacters(ArchiveFormat format, string expected)
    {
        var label = Probe.ShortFormat(format);

        Assert.Equal(expected, label);
        Assert.InRange(label.Length, 1, 4);
    }

    /// <summary>「不会处理」是一句独立的文案，不是状态机那六个词里的任何一个。</summary>
    [Fact]
    [Trait("Bug", "B-2026-110")]
    public void NotRunnableState_MustNotCollideWithTheStateLabels()
    {
        Assert.NotEqual(States.Queued, Ui.NotRunnableState);
        Assert.NotEqual(States.Unknown, Ui.NotRunnableState);

        foreach (var state in new[]
                 {
                     JobState.Queued, JobState.Running, JobState.Done,
                     JobState.Failed, JobState.Skipped, JobState.Partial,
                 })
        {
            Assert.NotEqual(Ui.NotRunnableState, state.Label());
        }
    }
}
