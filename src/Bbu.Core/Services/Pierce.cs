using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>穿透一整趟要的输入。字段与 1.1.0 的 <c>Piercer</c> 构造参数一一对应。</summary>
public sealed record PierceOptions
{
    public int MaxDepth { get; init; } = 5;

    /// <summary>层数预算：0 与 1 同义（配置里的 0 不写回），两种入口的层数上限因此一致。</summary>
    public static int Budget(int maxDepth) => maxDepth > 0 ? maxDepth : 1;

    public double MinFreeGb { get; init; } = 1.0;

    public bool FlattenSingleChild { get; init; } = true;

    /// <summary>解压成功后删掉用户拖进来的那个原包。默认不删。</summary>
    public bool RemoveSource { get; init; }

    /// <summary>解压完一层后删掉嵌在里面的那个包。只删第二层起。</summary>
    public bool RemoveIntermediate { get; init; } = true;

    /// <summary>认不认「前面垫了真视频、后面接压缩包」那种伪装。</summary>
    public bool ScanAppended { get; init; } = true;

    /// <summary>解压前清掉文件名里夹的「删」字。它改的是用户目录里的真文件，关掉即一个字不动。</summary>
    public bool CleanDelete { get; init; } = true;

    /// <summary>试密码的并行路数。0 表示自动。</summary>
    public int Workers { get; init; }

    public IReadOnlyList<string> ExcludeExts { get; init; } = [];

    public string? OutputRoot { get; init; }

    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Rename;

    /// <summary>判重的账（一条解压链一本）。不传就每趟各建一份，见 <see cref="PierceRegistry"/>。</summary>
    public PierceRegistry? Registry { get; init; }

    /// <summary>快路由调用方注入。不传表示整轮都走引擎。</summary>
    public IFastPath? FastPath { get; init; }

    /// <summary>「现在该暂停吗」。传下去之后正在跑的那一条会被真的挂起，暂停时长不计入超时。</summary>
    /// <remarks>调用方负责「暂停期间不开新任务」。</remarks>
    public Func<bool>? Pause { get; init; }

    /// <summary>穿透层的日志出口。级别随每一句一起给出，由产出那一层的入口承载。</summary>
    /// <remarks>
    /// 消费方照着级别取色，不解析正文。调用点不替这条通道兜异常 —— 有意不再吞；写盘那条由 sink 自己兜。
    /// </remarks>
    public IRunLog? Log { get; init; }

    /// <summary>问用户要密码的通道。只有「密码全试完」才调用它；用户跳过时这一层报「已跳过这个包」。</summary>
    /// <remarks>不传等于没有这条通道，照旧报「密码耗尽」——两者是不同的结论，不许合并。</remarks>
    public AskPasswordAsync? Ask { get; init; }

    /// <summary>撞上保险丝时的询问：参数是已扫目录数与文件数，返回真表示继续。不传表示没人可问。</summary>
    public Func<int, int, bool>? AskFuseContinue { get; init; }

    /// <summary>逐层上报：包层、目标文件名、这一层有没有结束。层开始传假、层结束传对账结果，界面做实时进度用。</summary>
    public Action<PackLayer, string, bool>? OnLayer { get; init; }
}

/// <summary>
/// 递归穿透状态机。一次 <see cref="RunAsync"/> 处理整棵嵌套树，不是一层。
///
/// 它不认出格式、不算产物目录、不生成候选密码：那三件事各由识别层、命名层、密码层负责。
/// 三个保险丝（层数上限、内容判重、磁盘余量）
/// </summary>
public sealed partial class Piercer
{
    /// <summary>无扩展名的包：产物目录名加这个后缀，免得它与源文件自己的路径撞上。</summary>
    private const string NoExtSuffix = OutputNaming.NoExtSuffix;

    /// <summary>同名链「连提」的单次上限。它不是层数预算。</summary>
    private const int LiftLimit = 8;

    private readonly SevenZipEngine _engine;
    private readonly Vault _vault;
    private readonly PierceOptions _options;
    private readonly CancellationToken _cancellation;
    private readonly PierceRegistry _registry;
    private readonly IRunLog? _log;
    private readonly Action<PackLayer, string, bool>? _onLayer;
    private readonly long _minFreeBytes;

    private string? _lastPassword;

    /// <summary>用户拖进来的目录，只有目录入口才有；收壳不许把内容提到这里（外部审计第 1 轮 `F-01`）。</summary>
    private string? _entryRoot;

    private string? _lastOutdir;

    public Piercer(
        SevenZipEngine engine,
        Vault vault,
        PierceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(vault);
        _engine = engine;
        _vault = vault;
        _options = options ?? new PierceOptions();
        _cancellation = cancellationToken;
        _registry = _options.Registry ?? new PierceRegistry();
        _log = _options.Log;
        _onLayer = _options.OnLayer;
        _minFreeBytes = _options.MinFreeGb > 0 ? (long)(_options.MinFreeGb * 1024 * 1024 * 1024) : 0;
    }

    /// <summary>
    /// 从压缩包或文件夹开始，逐层穿透。
    ///
    /// 它<b>不把异常抛给调用方</b>：界面里它由批处理直接调，一个漏出去的异常会让整批中断、
    /// 状态停在「运行中」。已知的失败都已在各自分支里转成层结果与停止原因，
    /// 这里是最后一道网，只兜没预料到的异常 —— 且异常发生时<b>已经解开的层不许消失</b>。
    /// </summary>
    public async Task<PierceResult> RunAsync(string start)
    {
        var result = new PierceResult { Ok = false };
        PierceResult done;
        try
        {
            done = await RunCoreAsync(start, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            done = UnexpectedFailure(start, result, ex);
        }

        // 取消或失败也要指出半成品在哪（外部审计第 1 轮 D-6）；出口只此一处，统一在这里收口。
        NotePartialOutdir(done, _lastOutdir);
        return done;
    }

    /// <summary>取消或失败时把真的建出来的产物目录记进结果（外部审计第 1 轮 `D-6`）。</summary>
    private static void NotePartialOutdir(PierceResult result, string? outdir)
    {
        if (result.OutputDir.Length == 0 && outdir is not null && Directory.Exists(outdir))
        {
            result.OutputDir = outdir;
        }
    }

    /// <summary>单文件入口的分卷归组（`D-7`）：主卷在旁就改用它，缺主卷则返回假。</summary>
    private bool TryResolveEntryVolume(string start, out string target)
    {
        target = start;
        if (Probe.ClassifyVolume(start).Kind is VolKind.None)
        {
            return true;
        }

        var folder = Path.GetDirectoryName(start) is { Length: > 0 } parent ? parent : ".";

        // 名单是「这一层枚举出来的整份」而不是「除自己以外的那些」：主卷自己也在名单里，
        // 源文件已被删掉时它更得靠这一条进组，否则一个正常的单卷包会被判成缺主卷。
        var siblings = new List<string> { start };
        try
        {
            siblings.AddRange(Directory.EnumerateFiles(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了同目录时只按它自己判，不得据此报缺主卷。
            _log?.Problem(PierceText.CleanNamesDirUnreadableAt, Path.GetFileName(folder), ex.Message);
        }

        siblings = [.. siblings.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (Probe.IsStandaloneVolume(start))
        {
            return true;
        }

        var main = Probe.MainVolumeOf(start, siblings);
        if (main is null)
        {
            // 缺主卷的报法由识别层那一处给，与目录入口同一条。
            Probe.MainVolumes(Probe.GroupVolumes(siblings), _log);
            return false;
        }

        if (!string.Equals(main, start, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Note(PierceText.VolumeMainChosenAt, Path.GetFileName(start), Path.GetFileName(main));
        }

        target = main;
        return true;
    }

    /// <summary>把未预期的异常转成一条如实的失败结果，三件事都要做到。</summary>
    /// <remarks>
    /// 记一句用户看得见的日志、把异常原文写进细节、结果判失败。界面因此不会停在运行中。
    /// 只兜 <c>Exception</c> 的派生类：取消那个异常不该被伪装成解压失败。
    /// </remarks>
    private PierceResult UnexpectedFailure(string start, PierceResult result, Exception ex)
    {
        var detail = ex.GetType().Name + ": " + ex.Message;
        _log?.Failure(PierceText.UnexpectedFailureAt, detail);
        if (result.OutputDir.Length == 0)
        {
            result.OutputDir = Directory.Exists(start) ? start : string.Empty;
        }

        result.Ok = false;
        result.StopReason = StopReason.ExtractFailed;
        result.StopDetail = detail;
        ReconcileLeftover(result);
        return result;
    }

    private async Task<PierceResult> RunCoreAsync(string start, PierceResult result)
    {
        PackLayer nextLayer;
        string workdir;
        if (Directory.Exists(start))
        {
            workdir = start;
            result.OutputDir = start;
            _entryRoot = PierceRegistry.Normalize(start);
            // 从目录开始：目录里的包就是第 1 层。
            nextLayer = PackLayer.First;
        }
        else
        {
            _entryRoot = null;

            // 单文件入口同样要看取消：只取到候选才查的话，拖进来一个包时点停止不起作用。
            if (Cancelled)
            {
                result.StopReason = StopReason.Cancelled;
                result.StopDetail = string.Empty;
                result.Ok = true;
                return result;
            }

            // 弱命名的那一档先问识别层，与目录入口同一判据（D-7）。
            if (!TryResolveEntryVolume(start, out var resolvedEntry))
            {
                result.StopReason = StopReason.NoArchive;
                result.StopDetail = string.Empty;
                result.Ok = true;
                return result;
            }

            start = resolvedEntry;

            // 入口包先登记再解：链上有了它，这一份里再出现「自己的副本」才拦得住（防死循环靠它）。
            // 入口层不查判重 —— 链的根上只有它自己，没有祖先可撞；跨清单项的同内容各解一份。
            var fingerprint = ContentIdentity.Of(start);
            _registry.Register(fingerprint, start);
            if (!HasSpace(Path.GetDirectoryName(start) ?? "."))
            {
                result.StopReason = StopReason.NoSpace;
                result.StopDetail = PierceText.SpaceBelowFloor(_options.MinFreeGb);
                return result;
            }

            var blocked = ResolveTarget(start, parent: null);
            if (blocked.Directory is null)
            {
                // 输出目录已存在且策略是跳过：它属于安全停下，所以两个入口必须给同一个答案。
                result.StopReason = StopReason.OutputExists;
                result.Ok = true;
                result.StopDetail = PierceText.Fill(
                    PierceText.OutputExistsDetailAt,
                    Path.GetFileName(blocked.BlockedBy ?? start));
                return result;
            }

            var entryOutdir = blocked.Directory;

            var first = await ExtractLayerAsync(result, start, entryOutdir, PackLayer.First, passwordHint: null)
                .ConfigureAwait(false);
            RecordLayer(result, first);
            if (!first.Ok)
            {
                result.StopReason = first.Reason ?? StopReason.ExtractFailed;
                result.StopDetail = first.Note;
                result.Ok = false;
                return result;
            }

            workdir = entryOutdir;
            result.OutputDir = entryOutdir;

            // 这一份真的解出来了、产物确定 —— 另记一笔「内容在哪儿」，它与解压前那本防死循环的账是两件事。
            // 引擎报了警告退出码时不记：那一层解压并不完整，不能当成「内容已经在别处了」（B-2026-311）。
            if (!first.Warned)
            {
                _registry.NoteSolved(fingerprint, start, entryOutdir);
            }

            // 源包是第 1 层；它的产物目录里再找到的包就是第 2 层。
            nextLayer = PackLayer.First + 1;
        }

        var (reason, detail) = await PierceAsync(workdir, result, nextLayer).ConfigureAwait(false);
        var final = FinalState(result, reason, detail);
        result.StopReason = final.Reason;
        result.StopDetail = final.Detail;
        result.Ok = final.Ok;
        ReconcileLeftover(result);
        ReportLeftover(result);
        return result;
    }

    /// <summary>状态机给出的结论与层里记录的事实对齐，再定安全停下那一位。</summary>
    /// <remarks>
    /// 不变量：<b>只要有一层真失败，最终结果就不许是「一切正常」</b>；用户取消不算真失败，它本来就不 CountsAsDone。
    /// 正常情况下分类函数已经保证了它，这里再收口一次，是为了让以后新加的提前返回也破不了它。
    /// 破了它的直接后果不是文案难看，而是命令行在存在失败层时判成功。
    /// 「能不能报成功」这一位同样只在这里赋值：撞层数上限不算做完了，见 <see cref="StopReasonTraits.CountsAsDone"/>。
    /// </remarks>
    internal static (StopReason Reason, string Detail, bool Ok) FinalState(
        PierceResult result, StopReason reason, string detail)
    {
        var failed = result.Layers.FirstOrDefault(layer => !layer.Ok);
        if (failed is not null && reason.IsCleanStop() && failed.Reason?.IsCleanStop() != true)
        {
            reason = failed.Reason is { } own && !own.IsCleanStop() ? own : StopReason.ExtractFailed;
            detail = failed.Note.Length > 0 ? failed.Note : detail;
        }

        return (reason, detail, reason.CountsAsDone());
    }

    /// <summary>
    /// 两个候选停止原因里，哪个该报给用户：真失败优先于安全停下。
    ///
    /// 规则两条：真失败可以盖掉已记下的安全停下，这是唯一允许覆盖的方向；
    /// 安全停下之间、真失败之间都仍然先到先得，第一个才是这条路为什么停。
    /// 分界线直接取停止原因自己的属性，不另立名单，加成员时两边不会走偏。
    /// </summary>
    internal static (StopReason Reason, string Detail) PreferFailure(
        (StopReason Reason, string Detail)? current, StopReason reason, string detail)
    {
        if (current is null)
        {
            return (reason, detail);
        }

        if (reason.IsCleanStop())
        {
            return current.Value;
        }

        return current.Value.Reason.IsCleanStop() ? (reason, detail) : current.Value;
    }

    /// <summary>把一层记进结果并逐层上报。<b>这两件事必须在同一处</b>。</summary>
    /// <remarks>
    /// 分在两处时，「切出内嵌包失败」那条分支会只有记账、没有上报：运行中界面的层号停在上一层，
    /// 而结束后的最深包层把它算进去了 —— 同一次解压两个层号对不上。
    /// 只有一个入口，配对就不可能再断。
    /// </remarks>
    private void RecordLayer(PierceResult result, LayerResult layer)
    {
        result.Layers.Add(layer);
        LayerEvent(layer.Depth, layer.Target, layer.Ok);
    }

    private void LayerEvent(PackLayer depth, string target, bool done)
    {
        if (_onLayer is null)
        {
            return;
        }

        try
        {
            _onLayer(depth, Path.GetFileName(target), done);
        }
        catch (Exception)
        {
            // 上报是界面的旁路，它出问题不许影响这一趟的账。
        }
    }

    private bool Cancelled => _cancellation.IsCancellationRequested;

    /// <summary>磁盘余量够不够。事前按包声明的体积判，判不出体积时退化成绝对下限。</summary>
    private bool HasSpace(string directory, long needBytes = 0) =>
        DiskFloor.Decide(DiskFloor.FreeBytes(directory), needBytes, _minFreeBytes) is DiskFloorVerdict.Ok;

    /// <summary>判重文案里的定位一律给全路径：跨出产物树时相对形式退化成裸文件名，无法定位（B-2026-311）。</summary>
    /// <remarks>现场里「同一份内容」常落在另一个产物目录，只给文件名时用户无法确认是哪一份。
    /// 残留名单不走这里：它的基准由 <see cref="PierceResult.OutputDir"/> 定义，</remarks>
    private static string Locate(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>算一层相对产物目录的路径；跨盘符或跑到目录外面就退回文件名。</summary>
    private static string RelativeTo(string path, string baseDir)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(baseDir);
            var relative = Path.GetRelativePath(root, full);
            return relative.StartsWith("..", StringComparison.Ordinal)
                ? Path.GetFileName(path)
                : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.GetFileName(path);
        }
    }

    /// <summary>登记「这个东西没解」。账目在 <see cref="RunAsync"/> 返回前由对账重新算一次。</summary>
    private static void NoteLeftover(PierceResult result, string baseDir, params IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var relative = baseDir.Length > 0 ? RelativeTo(path, baseDir) : path;
            if (!result.Leftover.Contains(relative, StringComparer.Ordinal))
            {
                result.Leftover.Add(relative);
            }
        }
    }
}
