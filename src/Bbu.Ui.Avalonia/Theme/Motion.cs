using System;
using Avalonia.Animation.Easings;

namespace Bbu.Ui.Avalonia.Theme;

/// <summary>
/// 动效曲线。
///
/// 为什么不放 XAML 的 ResourceDictionary：<c>&lt;KeySpline&gt;</c> 元素存进去会变成字符串，
/// 赋给 <c>Animation.Easing</c> 时抛 <c>InvalidCastException</c>。
/// 所以曲线在 C# 里解析，XAML 用 <c>{x:Static theme:Motion.EaseOut}</c> 引用。
///
/// 数值来自 app.css：<c>--ease-out:cubic-bezier(.22,.61,.36,1)</c> /
/// <c>--ease-in:cubic-bezier(.55,.06,.68,.19)</c>。
/// 逐级回退，任何一档不吃都不会让程序起不来；实际用了哪一档记在 <see cref="How"/> 里，
/// 启动时打进日志区，不靠猜。
/// </summary>
public static class Motion
{
    private static readonly (Easing Ease, string How) SplineOut =
        Resolve("0.22,0.61,0.36,1", "CubicEaseOut");
    private static readonly (Easing Ease, string How) SplineIn =
        Resolve("0.55,0.06,0.68,0.19", "CubicEaseIn");

    // 浮层的进与出各一条：共用一条会让打开与关掉是同一种速度感。
    private static readonly (Easing Ease, string How) SplineSpring =
        Resolve("0.16,1,0.3,1", "CubicEaseOut");
    private static readonly (Easing Ease, string How) SplineExit =
        Resolve("0.4,0,1,1", "CubicEaseIn");

    public static Easing EaseOut => SplineOut.Ease;
    public static Easing EaseIn => SplineIn.Ease;

    /// <summary>浮层入场：起步快、末尾柔和收住。</summary>
    public static Easing EaseSpring => SplineSpring.Ease;

    /// <summary>浮层退场：立刻加速消失，不拖尾。</summary>
    public static Easing EaseExit => SplineExit.Ease;

    /// <summary>四条曲线实际是用哪个方式拿到的（给日志用）。</summary>
    public static string How =>
        Describe("ease-out", SplineOut, ".22,.61,.36,1") +
        Describe(" · ease-in", SplineIn, ".55,.06,.68,.19") +
        Describe(" · spring", SplineSpring, ".16,1,.3,1") +
        Describe(" · exit", SplineExit, ".4,0,1,1");

    private static string Describe(string name, (Easing Ease, string How) entry, string spline) =>
        $"{name} = {entry.How}{(entry.How == "spline" ? $"（cubic-bezier({spline})）" : "")}";

    private static (Easing, string) Resolve(string splineSpec, string fallbackName)
    {
        // ① 让 Avalonia 自己解析样条串（含逗号时就是 cubic-bezier）
        try
        { return (Easing.Parse(splineSpec), "spline"); }
        catch { /* 换下一档 */ }

        // ② 退回内置具名曲线（名字必须是 Avalonia.Animation.Easings 里的类型名）
        try
        { return (Easing.Parse(fallbackName), fallbackName); }
        catch { /* 换下一档 */ }

        return (new LinearEasing(), "LinearEasing");
    }
}
