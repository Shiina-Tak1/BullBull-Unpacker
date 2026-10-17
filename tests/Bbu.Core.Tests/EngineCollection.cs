using Xunit;

namespace Bbu.Core.Tests;

/// <summary>
/// 会启动 7z 的测试共用一个集合，因此彼此之间串行。
///
/// 为什么必须串行：有一条断言是「取消之后不许有残留的 7z 进程」，它按进程名快照全机的 7z。
/// 别的测试同时在跑 7z 时，快照里会混进不属于它的进程，那条断言会假红。
/// 判据「取消要真杀进程树」本身没变，变的是它看到的世界。
/// </summary>
[CollectionDefinition("engine")]
public sealed class EngineCollection
{
}
