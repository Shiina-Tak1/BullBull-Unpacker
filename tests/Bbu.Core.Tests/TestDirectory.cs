using System;
using System.IO;

namespace Bbu.Core.Tests;

/// <summary>
/// 测试用的临时目录。
/// 建在测试输出目录下而不是 <c>%TEMP%</c>：沙箱里临时目录不一定可写，输出目录一定可写。
/// </summary>
public sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "_test-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言结果
        }
    }
}
