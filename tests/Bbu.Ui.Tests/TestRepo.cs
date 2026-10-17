using System;
using System.IO;

namespace Bbu.Ui.Tests;

/// <summary>
/// 从测试进程定位仓库根：往上一路找 <c>Bbu.slnx</c>，不写死层数 —— 换 TFM 或 RID 都会让层数变。
/// 读仓库文本的断言都从这里取根，各文件自己再写一份就会各自漂移。
/// </summary>
internal static class TestRepo
{
    /// <summary>仓库根。找不到即抛，不返回空串：静默的空根会让下游断言对着别的目录比较。</summary>
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bbu.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("定位不到仓库根：往上一路都没找到 Bbu.slnx");
    }
}
