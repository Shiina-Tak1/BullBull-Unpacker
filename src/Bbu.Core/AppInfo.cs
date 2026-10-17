using System.Reflection;

namespace Bbu.Core;

/// <summary>
/// 应用的身份信息：版本号、发布形态、任务栏标识。
///
/// 版本号与形态由构建注入，单一出处是仓根的 <c>Directory.Build.props</c>；任务栏标识的改动
/// 等于换一个任务栏身份，用户钉住的快捷方式会失联，因此它不随版本号走。
/// </summary>
public static class AppInfo
{
    /// <summary>版本号：PE 产品版本里 <c>+</c> 之前那一段。</summary>
    public static string Version { get; } = WithoutRevision(InformationalVersion());

    /// <summary>构建指纹 <c>&lt;版本&gt;+&lt;git sha&gt;</c>：发布包里没有 BUILD-ID.txt，认产物靠它。</summary>
    public static string BuildId { get; } = InformationalVersion();

    /// <summary>本次发布形态。会话头带上它，日志才说得清是哪一份。</summary>
    public static string Flavor =>
#if BBU_AOT
        "aot";
#else
        "framework-dependent";
#endif

    /// <summary>AppUserModelID。与 1.1.0 同值。</summary>
    public const string AppUserModelId = "BullBull.Unpacker.1";

    public const string ConfigFileName = "config.json";

    /// <summary>日志文件名：平铺在用户数据目录下，不再单开一层。</summary>
    public const string LogFileName = "BullBullUnpacker.log";

    /// <summary>用户数据那一层的目录名：设置、密码本与日志都收在它下面。</summary>
    public const string UserDataFolderName = "userdata";

    /// <summary>
    /// PE 产品版本，形状 <c>&lt;版本&gt;+&lt;git sha&gt;</c>。写成方法而不是属性：
    /// 静态属性按声明顺序初始化，读它的一方会拿到空值。
    /// </summary>
    private static string InformationalVersion() =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
            ?? string.Empty;

    private static string WithoutRevision(string text)
    {
        var plus = text.IndexOf('+');
        return plus < 0 ? text : text[..plus];
    }
}
