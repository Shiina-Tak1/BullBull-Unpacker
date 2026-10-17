using System.Collections.Generic;

namespace Bbu.Contract.Tests;

/// <summary>
/// 测试分层的四个档。取值就是 <c>--filter Category=&lt;档&gt;</c> 里的那个值，也是
/// <c>Trait("Category", ...)</c> 的取值。档名写错由工程侧那套检查拦，本工程不带那条。
///
/// 分档判据沿用 1.1.0：破了会让用户静默受损的性质进默认档，
/// 只是"数字变了要知道"的进边界档或账目档。
/// </summary>
public static class TestCategories
{
    /// <summary>默认档：单元、架构、文本纪律、 里的 Critical 与 High。裸 <c>dotnet test</c> 跑的就是它。</summary>
    public const string Default = "default";

    /// <summary>边界档：夹具、字符集、分卷、伪装包、引擎生命周期。改判据或改覆盖格式时跑。</summary>
    public const string Edge = "edge";

    /// <summary> 档： 里中低严重度的条目。发版前或改状态映射时跑。</summary>
    public const string Ledger = "ledger";

    /// <summary>真机档：真窗口、注册表、体积、启动。手动跑。</summary>
    public const string Machine = "machine";

    public static readonly IReadOnlyList<string> All = new[] { Default, Edge, Ledger, Machine };
}
