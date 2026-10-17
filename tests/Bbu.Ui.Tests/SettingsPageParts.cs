using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bbu.Ui.Avalonia.Views;

namespace Bbu.Ui.Tests;

/// <summary>
/// 设置页的项是模板生成的，按设置项名 <c>FindControl</c> 找不到 ——
/// 按模板里那个部件名，从可视树里取生成的控件。
///
/// 判断"某一项接没接对"仍然看 <see cref="SettingsPage.Model"/>，不靠控件。
/// </summary>
internal static class SettingsPageParts
{
    internal static T? Part<T>(this SettingsPage page, string name)
        where T : Control =>
        page.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);

    /// <summary>
    /// 当下看得见的按钮名。问"这一项还剩几个按钮"要看它：收起的按钮 <see cref="Part{T}"/> 取不到，
    /// 拿它当"没有这个按钮"会把收起与不存在混成一件事。
    /// </summary>
    internal static IReadOnlyList<string> VisibleButtons(this SettingsPage page) =>
        [.. page.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Select(button => button.Name ?? string.Empty)];
}
