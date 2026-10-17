using Bbu.Core.Logging;
using Bbu.Core.Models;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 级别与状态到视觉的唯一一处映射。类名由枚举成员名换算（前缀加小写成员名），
/// 所以加一个级别只需要在 <c>Controls.axaml</c> 里加一条同名样式，视图代码不用改。
/// </summary>
internal static class Levels
{
    /// <summary>
    /// 面板的级别阈值：低于它的正文只进日志文件。
    /// 默认值放在这一份里，是因为**它本身也是一次级别指派** —— 页面里出现级别字面量会被
    /// `UiLayer_MustNotAssignTextLevels` 判违规（这条断言不只管一行日志的档，也管这种阈值）。
    /// </summary>
    public static LogLevel PanelMinimum => LogLevel.Info;

    /// <summary>提示条的色调：按入口取，页面里就不必出现级别字面量。</summary>
    public static ToastTone NoteTone => ToastToneFor(LogLevel.Info);

    public static ToastTone FailureTone => ToastToneFor(LogLevel.Error);

    public static ToastTone ProblemTone => ToastToneFor(LogLevel.Warn);

    /// <summary>一句文案的级别类。四个级别在样式表里各有一条同名规则。</summary>
    public static string Class(LogLevel level) => "lvl-" + level.ToString().ToLowerInvariant();

    /// <summary>
    /// 清单行的级别类。运行中那一个按状态取强调色，其余取状态自己的档 —— 强调不是级别，是「正在动」本身的表示。
    /// </summary>
    public static string StateClass(JobState state) =>
        state is JobState.Running ? "lvl-accent" : Class(RunLog.StateLevel(state));

    /// <summary>
    /// 没有状态可依的那两格的级别类（不可运行的行、空密码那一格）：按陈述档。
    /// 放在这里而不是让调用点写 <c>Class(LogLevel.Info)</c> —— 视图层写级别字面量会被
    /// <c> .UiLayer_MustNotAssignTextLevels</c> 拦下，级别的选择只有这一处。
    /// </summary>
    public static string MutedClass => Class(LogLevel.Info);

    /// <summary>「仅错误」筛选留下哪几级：失败与提示两级（跳过与部分完成也是提示档）。</summary>
    public static bool ShownWhenErrorsOnly(LogLevel level) => level is LogLevel.Error or LogLevel.Warn;

    /// <summary>
    /// 拼出来的整行（两句正文夹着异常原文）的级别：它查不到级别表，按失败档取。
    /// 视图层不许出现级别字面量，所以这一档也只能由这里给。
    /// </summary>
    public static LogLevel BatchFatalLevel => LogLevel.Error;

    public static string SegmentToken(JobState state) =>
        state is JobState.Running ? "Accent" : TokenOf(RunLog.StateLevel(state));

    /// <summary>
    /// 构成条的段令牌。Info 在这里取 <c>Ok</c>（绿），面板与清单状态格取 <c>Dim</c>（灰）：六段条要靠颜色
    /// 分出「完成」那一份，面板那边没有成功档 —— 一个级别在两个出口取两种色是有意的，不是漏改。
    /// </summary>
    private static string TokenOf(LogLevel level) => level switch
    {
        LogLevel.Info => "Ok",
        LogLevel.Warn => "Warn",
        LogLevel.Error => "Err",
        _ => "Dim",
    };

    /// <summary>级别到提示条档位的映射。视图不自己比枚举，映射只有这一处。</summary>
    public static ToastTone ToastToneFor(LogLevel level) => level switch
    {
        LogLevel.Error => ToastTone.Bad,
        LogLevel.Warn => ToastTone.Warn,
        _ => ToastTone.Normal,
    };
}
