namespace Bbu.Ui.Avalonia.Views;

/// <summary>确认窗的语气：决定强调按钮落在哪一侧、默认与回车落在哪一颗。</summary>
public enum AskTone
{
    /// <summary>破坏性：确认按钮走 danger 描边红，默认与回车留在"取消"。</summary>
    Destructive,

    /// <summary>非破坏性的取舍：强调色与默认给肯定那一颗，"取消"只留 Esc。</summary>
    Choice,
}
