using Bbu.Core.Configuration;

namespace Bbu.Core.Services;

/// <summary>破坏性设置项的闸门：**该不该问**只有这一处结论，界面只把「问」接到确认框上。</summary>
/// <remarks>两处都只在「从别的档切到它」那一次成立，已经在这一档上再落笔不问第二遍。</remarks>
public static class DestructiveAsk
{
    /// <summary>「解压成功后删除原压缩包」：只在从关拨到开那一次问。</summary>
    public static bool BeforeRemovingSource(bool current, bool requested) => !current && requested;

    /// <summary>「重名时」：只在从别的档切到覆盖那一次问。</summary>
    public static bool BeforeOverwriting(ConflictPolicy current, ConflictPolicy requested) =>
        current is not ConflictPolicy.Overwrite && requested is ConflictPolicy.Overwrite;
}
