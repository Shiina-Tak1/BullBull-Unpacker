namespace Bbu.Core.Services;

/// <summary>「这一轮是否按暂停处理」的唯一判据：挂起没成功就不算暂停（B-2026-197）。</summary>
internal static class EnginePausePolicy
{
    /// <summary>暂停请求与挂起结果一起决定这一档是否按「已暂停」处理。真机上造不出挂起失败档，判据下沉成纯函数才可判。</summary>
    internal static bool CountsAsPaused(bool requested, bool suspended) => requested && suspended;
}
