namespace Bbu.Core.Configuration;

/// <summary>
/// 配置读写。业务层通过它拿配置，**不直接碰 <c>config.json</c> 的路径**
/// （路径判定归 <see cref="DataDirectory"/>，组合根装配时注入）。
/// </summary>
public interface IConfigStore
{
    string Path { get; }

    /// <summary>上一次读写出的问题；正常时为 <c>null</c>。写盘失败的原因由调用方从此处读取。</summary>
    string? LastError { get; }

    /// <summary>读配置。文件不存在或整份读不出来时返回默认配置，不抛异常。</summary>
    AppConfig Load();

    /// <summary>
    /// 写配置（整份覆盖，先写临时文件再替换）。**不抛异常**：写不进去返回 <c>false</c>，原因留在
    /// <see cref="LastError"/> —— 照 1.1.0 的 <c>save()</c>，失败通道是返回值。
    /// </summary>
    bool Save(AppConfig config);
}
