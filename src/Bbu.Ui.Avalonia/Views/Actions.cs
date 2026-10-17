using System.Collections.Generic;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Services;

namespace Bbu.Ui.Avalonia.Views;

/// <summary>
/// 主窗口动作的可用性，以及这一趟要跑的那一批项。
///
/// 两件事必须同源：「按钮能不能点亮」与「点下去跑哪一批」都从这里取。
/// 各写一份时会出现按钮亮着却点不动（或反过来）——每加一个入口就多长一处。
/// 开跑的执行入口只有一个，见 <c>MainWindow.RunAsync</c>，它取的也是这里的批。
/// </summary>
internal static class Actions
{
    /// <summary>这一趟要跑的项。可执行判据与可用性共用这一处。</summary>
    public static List<Job> Batch(IReadOnlyList<Job> jobs) =>
        [.. jobs.Where(item => item.Runnable && Runner.NeedsRun(item))];

    /// <summary>开始：不在跑，且这一趟真有可跑的项。</summary>
    public static bool CanStart(bool running, IReadOnlyList<Job> jobs) =>
        !running && Batch(jobs).Count > 0;

    /// <summary>暂停：正在跑且还没暂停。它与「继续」互斥 —— 同一时刻只有一个可点（docx 26）。</summary>
    public static bool CanPause(bool running, bool paused) => running && !paused;

    public static bool CanResume(bool running, bool paused) => running && paused;

    /// <summary>清空列表：清单里有东西，且不在跑。不看项可不可执行 —— 一行不可执行的项同样要清得掉。</summary>
    public static bool CanClearList(bool running, IReadOnlyList<Job> jobs) =>
        !running && jobs.Count > 0;

    /// <summary>移除选中：至少选了一项，且不在跑。选中数由视图数，可跑判据仍是这一处。</summary>
    public static bool CanRemoveSelected(bool running, int selected) => !running && selected > 0;

    /// <summary>打开产物目录：至少有一项产出过目录。</summary>
    public static bool CanOpenOutput(IReadOnlyList<Job> jobs) => Runner.OutputDirs(jobs).Count > 0;
}
