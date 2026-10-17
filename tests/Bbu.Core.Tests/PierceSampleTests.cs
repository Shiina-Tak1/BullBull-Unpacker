using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Services;
using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 穿透层真机档：拿仓外的旗舰样本跑完整的一条链。合成夹具替代不了真字节。
///
/// 样本 `` 是外层 lz4 帧裹一个加密 RAR5，条目名是中文，口令是样本自己的。
/// 识别层与引擎层各有各的断言，
/// 这里补的是**穿透**那一半：同一份字节交给穿透跑，两层都要真的走完、内层要真的解得开。
/// 两个类的注释里写着"中文名的完整验证要等穿透层把它解出来"，这一条就是那个"等"。
///
/// 样本不在的机器上整类跳过：它是作者的下载目录，不是仓库的一部分，真机档本来就手动跑。
/// </summary>
[Trait("Category", "machine")]
[Collection("engine")]
public sealed class PierceSampleTests
{
    private const string SampleName = "S000.rar.lz4";

    /// <summary>样本口令。取值同 <see cref="TestPaths.RealSamplePassword"/>。</summary>
    private static string SamplePassword => TestPaths.RealSamplePassword;

    private static string Sample => Path.Combine(TestPaths.RealSampleRoot, SampleName);

    private static bool Available => File.Exists(Sample);

    /// <summary>
    /// 两层都要走完。第二条断言同时是"口令真的命中"的证据：内层是加密 RAR5，
    /// 口令不对时第二层解不开，`DeepestLayer` 就停在 1 —— 这正是合成夹具证明不了的那一环。
    ///
    /// 产物落临时目录、**不落源文件旁边**：样本在作者的下载目录里，而穿透默认把产物开在源文件旁边。
    /// 验证脚本把测试产物扔进了用户的桌面输出目录）。
    /// </summary>
    [Fact]
    public async Task FlagshipSample_MustPierceBothLayers()
    {
        if (!Available)
        {
            return;
        }

        var work = Path.Combine(
            AppContext.BaseDirectory, "_test-tmp", "sample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var logs = new List<string>();
        var options = new PierceOptions
        {
            MaxDepth = 3,
            MinFreeGb = 0,
            OutputRoot = work,
            RemoveIntermediate = true,
            RemoveSource = false,
            Log = new ChannelLog((_, message) => logs.Add(message)),
        };

        var vault = new Vault();
        vault.SetEntries(new[] { SamplePassword });

        // 解压走随包 `UnRAR.exe`（`TestPaths.NewRarEngine()` 就是随包装配），它随包、
        // 不需要开发机装 WinRAR —— 这一类挂 machine 档只是因为真样本 `` 不在仓内。
        var result = await new Piercer(TestPaths.NewRarEngine(), vault, options).RunAsync(Sample);

        try
        {
            Assert.True(
                result.Ok,
                "穿透没安全停下：" + result.Summary() + Environment.NewLine + string.Join(Environment.NewLine, logs));

            // 钉的是"内层那个加密 RAR5 走得通"，不钉样本的偶然深度：实测是三层
            // （lz4 帧 → `S000.rar` 加密 RAR5 → 里面还有一个包），而判据的本体只是第二层。
            // 口令不对或引擎缺席时 `DeepestLayer` 会停在 1，所以这一条同时是口令命中的证据。
            Assert.True(
                result.DeepestLayer.Value >= 2,
                "只走到第 " + result.DeepestLayer.Value + " 层：" + Environment.NewLine
                    + string.Join(Environment.NewLine, logs));

            var products = Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).ToList();
            Assert.NotEmpty(products);

            // 中文条目名不许出现替换字符：乱码的名字拿去匹配条目会假阳性（见 的注释）。
            Assert.DoesNotContain('\ufffd', string.Join(Environment.NewLine, products));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响断言结论
            }
        }
    }
}
