using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Bbu.Core.Configuration;
using Bbu.Core.Logging;
using Bbu.Core.Models;
using Bbu.Core.Texts;
using UiText = Bbu.Core.Texts.Ui;

namespace Bbu.Core.Services;

/// <summary>扫一次要付的代价与要过滤的东西。</summary>
public sealed record ScanOptions
{
    /// <summary>认不认「前面垫了真视频、后面接压缩包」那种伪装。</summary>
    public bool ScanAppended { get; init; } = true;

    /// <summary>往下找多深，见 <see cref="FolderScan"/>。</summary>
    public FolderScan FolderScan { get; init; } = FolderScan.Below;

    public IReadOnlyList<string> ExcludeExts { get; init; } = [];

    /// <summary>问引擎「能不能把这个文件当包直读」。不传表示一个进程都不起。</summary>
    public Func<string, bool>? EngineProbe { get; init; }

    /// <summary>第二个引擎的同一问，排在尾部签名定位之后（真兜底）。不传表示不问。</summary>
    public Func<string, bool>? FallbackEngineProbe { get; init; }

    public CancellationToken Cancellation { get; init; }

    /// <summary>「往下找」撞上保险丝时的询问，一路传到识别层。不传表示没人可问。</summary>
    public Func<int, int, bool>? AskFuseContinue { get; init; }

    /// <summary>扫描期每一行的回传。级别由产出点给，界面不解析文案。</summary>
    public IRunLog? Log { get; init; }

}

/// <summary>
/// 扫描：把拖进来的路径展开成一张可显示的清单。
///
/// 它只探测、不碰任何解压。单个文件是一行；文件夹是汇总行加每个包一行；
/// 文件夹里没有任何压缩包时仍然生成一行，交给穿透自己去找。
///
/// 「哪个文件算一个包」与「往下找」都走识别层那一份共享实现 ——
/// 扫描与穿透对「包在哪」必须给出一致的答案，否则界面列出来的和执行时解的不是同一批。
/// </summary>
public static class Scan
{
    public static IReadOnlyList<Job> Run(IReadOnlyList<string> paths, ScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var given = options ?? new ScanOptions();

        // 只问第一次：同一次加入动作里后面的根不再打扰，取舍由那一次决定。
        var opts = given with { AskFuseContinue = Probe.AskOncePerBatch(given.AskFuseContinue) };
        var items = new List<Job>();
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        var tally = new ScanTally();
        tally.Report(opts, 0, 0);

        foreach (var raw in paths)
        {
            if (opts.Cancellation.IsCancellationRequested)
            {
                // 用户点了停止：不再展开后面的路径。扫描的每一段都可能很慢，
                // 取消的全部意义就是现在就停下。
                break;
            }

            var path = Path.GetFullPath(raw);
            if (Directory.Exists(path))
            {
                if (!seenDirs.Add(FileIdentity.Of(path)))
                {
                    continue;
                }

                ScanFolder(path, opts, items, seenTargets, tally);
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            ScanFile(path, opts, items, seenTargets);
        }

        Announce(items, opts);
        return items;
    }

    /// <summary>
    /// 把这一次进清单的项写进日志。粒度与 1.1.0 一致：只对不可执行的项逐条说一句，
    /// 名单里的类型不写（清单上已标明原因），文件夹行不写（它没有要解释的事）。
    /// 判据是日志条数与清单项数同阶 —— 按被探测的每个文件写，普通目录会刷出上万条。
    /// </summary>
    private static void Announce(List<Job> items, ScanOptions opts)
    {
        if (opts.Log is not { } log)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.Runnable || item.IsDir || Probe.ExcludedExt(item.ArchivePath, opts.ExcludeExts).Length > 0)
            {
                continue;
            }

            var note = item.Reason is { Length: > 0 } reason ? reason : ScanText.NoteNotArchive;
            log.Note(ScanText.LogSkippedAt, item.DisplayName, note);
        }
    }

    private static void ScanFolder(
        string path, ScanOptions opts, List<Job> items, HashSet<string> seenTargets, ScanTally tally)
    {
        // 读不了的文件没法判断是不是包：收集起来写进备注，不许以「不是压缩包」的名义静默消失。
        // 子目录里疑似藏了内嵌压缩包的文件同样只如实报、不去解。
        var unreadable = new List<string>();
        var suspected = new List<string>();
        var found = ArchivesIn(path, opts, tally, unreadable, suspected, out var counts, out var embedded, out var notes, out var engineRead, out var searchIncomplete);

        var targets = found.Where(target => !seenTargets.Contains(FileIdentity.Of(target))).ToList();

        // 「已经被前面的扫描收进清单了」要和「这里真的什么都没有」分开：
        // 拖父目录与子目录时，子目录里的包已经被父项那一次递归扫描列进清单，
        // 那种行不该生成 —— 它的信息量是零，留着只会误导用户并让他徒劳扫一遍。
        // 唯一例外是这一行还承载着别处的扫描看不到的信息。
        var swallowedByParent = found.Count > 0 && targets.Count == 0;
        if (targets.Count == 0)
        {
            if (swallowedByParent)
            {
                var bits = FolderNoteBits(searchIncomplete, suspected, unreadable);
                if (bits.Count > 0)
                {
                    items.Add(FolderRow(path, runnable: false, string.Join('；', bits)));
                }

                return;
            }

            if (items.Any(item => item.ArchivePath == path))
            {
                return;
            }

            // 备注三选一，不许把两件事混成一句：没找完 / 有疑似内嵌包 / 真的什么都没有。
            // 第三种才是唯一可以写「没有可解压的压缩包」的情形。
            var own = FolderNoteBits(searchIncomplete, suspected, unreadable: []);
            if (own.Count == 0)
            {
                own.Add(ScanText.NoteNoArchive);
            }

            var row = FolderRow(path, runnable: true, string.Join('；', own));
            var extra = UnreadableNote(unreadable);
            if (extra.Length > 0)
            {
                row.Reason = (row.Reason is { Length: > 0 } existing ? existing + "；" : string.Empty) + extra;
            }

            items.Add(row);
            return;
        }

        var parentIndex = items.Count;
        var skipped = targets.Count(target => Probe.ExcludedExt(target, opts.ExcludeExts).Length > 0);
        var parentKind = ScanText.Fill(ScanText.KindFolderWithCountAt, targets.Count)
            + (skipped > 0 ? ScanText.Fill(ScanText.KindExcludedCountAt, skipped) : string.Empty);
        var parent = new Job
        {
            ArchivePath = path,
            DisplayName = Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            Kind = parentKind,
            KindShort = ScanText.ShortFolder,
            IsDir = true,
            Runnable = false,
            ChildCount = targets.Count,
            Reason = string.Join('；', FolderNoteBits(searchIncomplete, suspected, unreadable)),
        };
        items.Add(parent);

        foreach (var target in targets.OrderBy(target => target, StringComparer.Ordinal))
        {
            seenTargets.Add(FileIdentity.Of(target));
            var index = items.Count;
            parent.Children.Add(index);

            var skipExt = Probe.ExcludedExt(target, opts.ExcludeExts);
            if (skipExt.Length > 0)
            {
                // 名单里的类型仍然列出来，但标明不处理 —— 用户要的是看得见为什么没解。
                items.Add(new Job
                {
                    ArchivePath = target,
                    DisplayName = Path.GetFileName(target),
                    Kind = ScanText.KindExcluded(skipExt.ToUpperInvariant()),
                    KindShort = ScanText.ShortExcluded,
                    Indent = true,
                    ParentIndex = parentIndex,
                    Runnable = false,
                    Reason = ScanText.Fill(ScanText.NoteExcludedAt, skipExt),
                });
                continue;
            }

            var key = PierceRegistry.Normalize(target);
            var direct = engineRead.Contains(key);
            var carrier = embedded.GetValueOrDefault(key);
            var count = counts.GetValueOrDefault(key, 1);
            items.Add(new Job
            {
                ArchivePath = target,
                DisplayName = Path.GetFileName(target),
                Kind = direct
                    ? ScanText.Fill(
                        ScanText.KindEngineDirectAt,
                        (Probe.ExtOf(target) is { Length: > 0 } ext ? ext : ScanText.NoExtension).ToUpperInvariant())
                    : DescribeArchive(target, count > 1, carrier),
                KindShort = direct ? ScanText.ShortEmbedded : ShortArchive(target, count > 1, carrier),
                Indent = true,
                ParentIndex = parentIndex,
                Reason = direct
                    ? ScanText.NoteEngineDirect
                    : carrier is not null
                        ? CarrierNote(target, carrier)
                        : JoinReasons(
                            count > 1 ? ScanText.Fill(ScanText.NoteVolumeCountAt, count) : string.Empty,
                            notes.GetValueOrDefault(key, string.Empty)),
            });
        }
    }

    private static void ScanFile(
        string path, ScanOptions opts, List<Job> items, HashSet<string> seenTargets)
    {
        // 拖进来的可能是次卷：纠正到同目录的主卷；弱命名的独立包不是次卷，照它自己走。
        var parentDir = Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : ".";
        var siblings = FilesIn(parentDir);
        var main = Probe.IsStandaloneVolume(path)
            ? path
            : Probe.MainVolumeOf(path, siblings) ?? path;

        // 同一个包（或它的其他分卷）已经加过了。
        if (!seenTargets.Add(FileIdentity.Of(main)))
        {
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in Probe.GroupVolumes(siblings))
        {
            counts[PierceRegistry.Normalize(group.Main)] = group.Count;
        }

        var skipExt = Probe.ExcludedExt(main, opts.ExcludeExts);
        if (skipExt.Length > 0)
        {
            items.Add(new Job
            {
                ArchivePath = main,
                DisplayName = Path.GetFileName(main),
                Kind = ScanText.KindExcluded(skipExt.ToUpperInvariant()),
                KindShort = ScanText.ShortExcluded,
                Runnable = false,
                Reason = ScanText.Fill(ScanText.NoteExcludedAt, skipExt),
            });
            return;
        }

        var isArchive = Probe.DetectFormat(main).IsArchive();
        Embedded? embedded = null;
        var engineRead = false;
        if (!isArchive && opts.ScanAppended)
        {
            var got = Probe.InspectCarrier(main, new ProbeOptions
            {
                Deep = true,
                EngineProbe = opts.EngineProbe,
                FallbackEngineProbe = opts.FallbackEngineProbe,
                Cancellation = opts.Cancellation,
                Log = opts.Log,
            });
            if (got.Direct)
            {
                engineRead = true;
            }
            else
            {
                embedded = got.Embedded;
            }
        }

        if (embedded is not null)
        {
            items.Add(new Job
            {
                ArchivePath = main,
                DisplayName = Path.GetFileName(main),
                Kind = DescribeArchive(main, groupIsSplit: false, embedded),
                KindShort = ScanText.ShortEmbedded,
                Reason = CarrierNote(main, embedded),
            });
            return;
        }

        if (engineRead)
        {
            var ext = (Probe.ExtOf(main) is { Length: > 0 } token ? token : ScanText.NoExtension).ToUpperInvariant();
            items.Add(new Job
            {
                ArchivePath = main,
                DisplayName = Path.GetFileName(main),
                Kind = ScanText.Fill(ScanText.KindEngineDirectAt, ext),
                KindShort = ScanText.ShortEmbedded,
                Reason = ScanText.NoteEngineDirect,
            });
            return;
        }

        var classified = Probe.Classify(main, new ProbeOptions
        {
            Cancellation = opts.Cancellation,
            Log = opts.Log,
        });
        if (!classified.IsCandidate)
        {
            // 既不是压缩包、名字也不像压缩包：列出来但标成不可执行。
            // 读不了与保留设备名的原因原样写出来，不许用一句「不是压缩包，跳过」盖住。
            items.Add(new Job
            {
                ArchivePath = main,
                DisplayName = Path.GetFileName(main),
                Kind = Probe.Describe(classified.Format),
                KindShort = Probe.ShortFormat(classified.Format),
                Runnable = false,
                Reason = classified.Note.Length > 0 ? classified.Note : ScanText.NoteNotArchive,
            });
            return;
        }

        var volumeCount = counts.GetValueOrDefault(PierceRegistry.Normalize(main), 1);
        items.Add(new Job
        {
            ArchivePath = main,
            DisplayName = Path.GetFileName(main),
            Kind = DescribeArchive(main, volumeCount > 1, embedded: null),
            KindShort = ShortArchive(main, volumeCount > 1, embedded: null),
            // 按扩展名兜底送的候选把那一句也写出来：结论里要说清它不是按 magic 判出来的。
            Reason = JoinReasons(
                volumeCount > 1 ? ScanText.Fill(ScanText.NoteVolumeCountAt, volumeCount) : string.Empty,
                classified.Note),
        });
    }

    /// <summary>两段短结论拼成一行备注，空串不占位。</summary>
    private static string JoinReasons(string first, string second) => (first, second) switch
    {
        ("", "") => string.Empty,
        ("", _) => second,
        (_, "") => first,
        _ => first + "；" + second,
    };

    /// <summary>列出一个目录里的待解压目标，本层与子目录一视同仁。</summary>
    /// <remarks>
    /// 「本层一个目标都没有才往下找」会让子目录里的包既不在清单里、也不会被解。
    /// 没找完要如实带给调用方，不得丢弃。取消不是没找完，所以它不会落到那个标志上。
    /// </remarks>
    private static List<string> ArchivesIn(
        string directory,
        ScanOptions opts,
        ScanTally tally,
        List<string> unreadable,
        List<string> suspected,
        out Dictionary<string, int> counts,
        out Dictionary<string, Embedded> embedded,
        out Dictionary<string, string> notes,
        out HashSet<string> engineRead,
        out bool searchIncomplete)
    {
        counts = new Dictionary<string, int>(StringComparer.Ordinal);
        embedded = new Dictionary<string, Embedded>(StringComparer.Ordinal);
        notes = new Dictionary<string, string>(StringComparer.Ordinal);
        engineRead = new HashSet<string>(StringComparer.Ordinal);
        searchIncomplete = false;

        var files = FilesIn(directory);
        tally.EnterFolder();

        var groups = Probe.GroupVolumes(files);
        foreach (var group in groups)
        {
            counts[PierceRegistry.Normalize(group.Main)] = group.Count;
        }

        // 分卷只留主卷；主卷不在场就跳过 —— 与穿透侧同一份判据。
        var targets = new List<string>(Probe.MainVolumes(groups, opts.Log));
        var probeOptions = new ProbeOptions
        {
            Deep = true,
            EngineProbe = opts.EngineProbe,
            FallbackEngineProbe = opts.FallbackEngineProbe,
            Cancellation = opts.Cancellation,
            Log = opts.Log,
            AskFuseContinue = opts.AskFuseContinue,
            OnScanProgress = (dirs, scanned) => tally.Report(opts, dirs, scanned),
        };

        foreach (var file in files)
        {
            if (opts.Cancellation.IsCancellationRequested)
            {
                // 用户点了停止：本层这一圈也要立刻停 —— 一个几千文件的目录能拖着几十秒不响应。
                break;
            }

            tally.Advance(opts);

            if (Probe.ClassifyVolume(file).Kind is not VolKind.None && !Probe.IsStandaloneVolume(file))
            {
                continue;
            }

            var classified = Probe.Classify(file, probeOptions);
            if (classified.Status is ProbeStatus.Unreadable or ProbeStatus.ReservedDevice)
            {
                unreadable.Add(file);
            }

            if (classified.IsCandidate)
            {
                targets.Add(file);
                if (classified.Note.Length > 0)
                {
                    // 结论里带说明的候选（按扩展名兜底送的那一类）把那一句带到清单行上。
                    notes[PierceRegistry.Normalize(file)] = classified.Note;
                }

                continue;
            }

            // 头不是压缩包：可能是「垫了真视频、后面接压缩包」的那种。
            if (!opts.ScanAppended)
            {
                continue;
            }

            // 复用上面那次 Classify 已经算好的载体结论：同一个文件的同一份判据不再跑第二遍
            
            var got = classified.Carrier;
            if (got.Direct)
            {
                // 包内偏移是绝对的、引擎自己会跳过假头：不切包，原样交给引擎。
                targets.Add(file);
                engineRead.Add(PierceRegistry.Normalize(file));
                continue;
            }

            if (got.Embedded is { } hit)
            {
                targets.Add(file);
                embedded[PierceRegistry.Normalize(file)] = hit;
            }
        }

        // 本层与子目录合并时同样按物理身份去重。
        var unique = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (known.Add(FileIdentity.Of(target)))
            {
                unique.Add(target);
            }
        }

        // 三档：只扫这一层（不进子目录，也就不写日志）/ 往下找只找候选（默认）/ 往下找并深挖内嵌。
        // 中间那一档的依据：子目录那批付了成百上千次尾部探测，报备产出是零。
        if (opts.FolderScan is not FolderScan.TopOnly)
        {
            var belowOptions = opts.FolderScan is FolderScan.Deep
                ? probeOptions
                : probeOptions with { Deep = false, Carriers = false };
            var search = Probe.FindArchivesBelow(directory, belowOptions);
            tally.Absorb(search.ScannedDirs, search.ScannedFiles);
            searchIncomplete = search.Truncated;
            foreach (var nested in search.Found)
            {
                if (known.Add(FileIdentity.Of(nested)))
                {
                    unique.Add(nested);
                }
            }

            // 子目录里疑似藏了内嵌压缩包的文件：不解，但要如实报。
            foreach (var carrier in search.Carriers)
            {
                if (known.Add(FileIdentity.Of(carrier)))
                {
                    suspected.Add(carrier);
                }
            }
        }

        return unique;
    }

    private sealed class ScanTally
    {
        private const int ReportEveryFiles = 64;

        private const long ReportEveryMs = 200;

        private int _dirs;
        private int _files;
        private long _lastReport = Environment.TickCount64;

        public void EnterFolder() => _dirs++;

        /// <summary>
        /// 本层又看完一个文件。它按节流上报 —— 本层每个文件都要读一次头、大的还要全盘扫，
        /// 几千个文件期间界面不能一直停在第一个数上。
        /// </summary>
        public void Advance(ScanOptions opts)
        {
            _files++;
            if (_files % ReportEveryFiles == 0 || Environment.TickCount64 - _lastReport >= ReportEveryMs)
            {
                Report(opts, 0, 0);
            }
        }

        /// <summary>把「往下找」那一段的计数并进本层：换下一个根时已扫的那些数不许回退。</summary>
        public void Absorb(int dirsBelow, int filesBelow)
        {
            _dirs += dirsBelow;
            _files += filesBelow;
        }

        /// <summary>把「往下找」那一段的计数叠到本层之上再回传；正文在这里成形。</summary>
        public void Report(ScanOptions opts, int dirsBelow, int filesBelow)
        {
            _lastReport = Environment.TickCount64;
            var dirs = _dirs + dirsBelow;
            var files = _files + filesBelow;
            opts.Log?.Progress(
                LogProgress.Scanning(dirs, files),
                ScanText.Fill(UiText.ActivityScanningAt, dirs, files));
        }
    }

    private static List<string> FilesIn(string directory)
    {
        try
        {
            return [.. new DirectoryInfo(directory).EnumerateFiles().Select(entry => entry.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Job FolderRow(string path, bool runnable, string note) => new()
    {
        ArchivePath = path,
        DisplayName = Path.GetFileName(path) is { Length: > 0 } name ? name : path,
        Kind = ScanText.ShortFolder,
        KindShort = ScanText.ShortFolder,
        IsDir = true,
        Runnable = runnable,
        Reason = note,
    };

    private static List<string> FolderNoteBits(
        bool searchIncomplete, List<string> suspected, List<string> unreadable)
    {
        var bits = new List<string>();
        if (searchIncomplete)
        {
            bits.Add(ScanText.NoteSearchIncomplete);
        }

        var suspectNote = SuspectedNote(suspected);
        if (suspectNote.Length > 0)
        {
            bits.Add(suspectNote);
        }

        var unreadableNote = UnreadableNote(unreadable);
        if (unreadableNote.Length > 0)
        {
            bits.Add(unreadableNote);
        }

        return bits;
    }

    private static string SuspectedNote(List<string> suspected) => suspected.Count == 0
        ? string.Empty
        : ScanText.Fill(
            ScanText.NoteSuspectedAt,
            suspected.Count,
            string.Join('、', suspected.Take(3).Select(Path.GetFileName)),
            suspected.Count > 3 ? PierceText.Fill(PierceText.ListMoreAt, suspected.Count) : string.Empty);

    private static string UnreadableNote(List<string> unreadable) => unreadable.Count == 0
        ? string.Empty
        : ScanText.Fill(
            ScanText.NoteUnreadableAt,
            string.Join('、', unreadable.Take(3).Select(Path.GetFileName)),
            unreadable.Count > 3 ? PierceText.Fill(PierceText.ListMoreAt, unreadable.Count) : string.Empty);

    /// <summary>类型列的一行描述。内嵌包与普通包各一种形状。</summary>
    private static string DescribeArchive(string path, bool groupIsSplit, Embedded? embedded)
    {
        if (embedded is not null)
        {
            var ext = Probe.ExtOf(path);
            var head = ext.Length > 0 ? ext.ToUpperInvariant() + " " : ScanText.NoExtension + " ";
            return ScanText.Fill(
                ScanText.KindEmbeddedAt, head, Probe.ShortFormat(embedded.Format), Probe.HumanSize(embedded.Offset));
        }

        var parts = new List<string>
        {
            Probe.Describe(Probe.DetectFormat(path), groupIsSplit ? Probe.ClassifyVolume(path) : null),
        };
        if (Probe.IsDisguised(path))
        {
            parts.Add(ScanText.DisguisedAsVideo);
        }

        if (!string.Equals(
                OutputNaming.CleanDeleteChars(Path.GetFileName(path)),
                Path.GetFileName(path),
                StringComparison.Ordinal))
        {
            parts.Add(ScanText.ContainsDeleteChar);
        }

        return string.Join("；", parts);
    }

    /// <summary>内嵌包的位置说明。</summary>
    private static string CarrierNote(string path, Embedded embedded) => ScanText.Fill(
        ScanText.NoteCarrierAt,
        Probe.ExtOf(path) is { Length: > 0 } ext ? ext : ScanText.NoExtension,
        Probe.HumanSize(embedded.Offset),
        Probe.ShortFormat(embedded.Format));

    /// <summary>
    /// 类型列的短标签。列宽只放得下 2 至 4 字，全称挂在 title 上。
    /// 分卷、内嵌与伪装各占一档：它们比"是哪种格式"更该被直接看到。
    /// </summary>
    private static string ShortArchive(string path, bool groupIsSplit, Embedded? embedded)
    {
        if (embedded is not null)
        {
            return ScanText.ShortEmbedded;
        }

        if (groupIsSplit)
        {
            return ScanText.ShortSplit;
        }

        return Probe.IsDisguised(path)
            ? ScanText.ShortDisguised
            : Probe.ShortFormat(Probe.DetectFormat(path));
    }
}
