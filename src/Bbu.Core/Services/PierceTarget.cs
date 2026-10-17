using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>
/// 单层的准备动作与选目标：清理「删」字、从这一层与子目录里挑出下一步要解的包。
///
/// 候选与子目录一视同仁：合并成一张候选单，多个就依次全解。
/// 「本层有候选就不往下找」会让子目录里的包既不在清单里、也不会被解，
/// 而包解出来自带一层目录是打包常态。
/// </summary>
public sealed partial class Piercer
{
    /// <summary>把含「删」字的文件名还原，返回改过的那些对。</summary>
    /// <remarks>
    /// 读不了目录时不得静默返回空名单：用户只看到这个包没解出来，没有任何线索。
    /// 清理不了不是致命的，但必须说出来。
    /// </remarks>
    internal List<(string Old, string New)> CleanDeleteNames(string directory)
    {
        var renamed = new List<(string Old, string New)>();
        List<FileInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory).EnumerateFiles()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Problem(
                PierceText.CleanNamesDirUnreadableAt,
                Path.GetFileName(directory) is { Length: > 0 } name ? name : directory,
                ex.Message);
            return renamed;
        }

        foreach (var entry in entries)
        {
            var cleaned = OutputNaming.CleanDeleteChars(entry.Name);
            if (string.Equals(cleaned, entry.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var destination = Path.Combine(directory, cleaned);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                _log?.Problem(PierceText.RenameTargetExistsAt, entry.Name);
                continue;
            }

            try
            {
                File.Move(entry.FullName, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Problem(PierceText.RenameFailedAt, entry.Name, cleaned);
                continue;
            }

            renamed.Add((entry.Name, cleaned));
            _log?.Note(PierceText.DeleteCharCleanedAt, entry.Name, cleaned);
        }

        return renamed;
    }

    /// <summary>
    /// 按优先级挑下一步要解的包。
    ///
    /// 判据只有一条：识别层挑出来的候选包。所以这里不再单独处理「目录里只有一个没有扩展名的文件」——
    /// 那种文件在候选里本来就是候选。
    ///
    /// 多个互不相关的候选等于全都解，不再判多解歧义收工：同一层并存几个独立的包是常态。
    /// 真正需要停的只有「多个候选但一个都认不出主次」那种伪装文件场景。
    /// </summary>
    /// <param name="skip">已经在排队的同层兄弟。它们的包层由队列带着，这里不能再捡一次。</param>
    internal PickResult PickTarget(string directory, IReadOnlyCollection<string> skip)
    {
        var blocked = new HashSet<string>(skip.Select(FileIdentity.Of), StringComparer.Ordinal);

        List<string> Allowed(IEnumerable<string> paths) =>
            [.. paths.Where(path => !blocked.Contains(FileIdentity.Of(path)))];

        var probeOptions = new ProbeOptions
        {
            Deep = false,
            Cancellation = _cancellation,
            Log = _log,
            AskFuseContinue = _options.AskFuseContinue,
        };

        List<string> files;
        try
        {
            files = [.. new DirectoryInfo(directory).EnumerateFiles().Select(entry => entry.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        var candidates = NotExcluded(Allowed(Probe.CandidatesFrom(files, probeOptions)));

        var search = Probe.FindArchivesBelow(directory, probeOptions);
        if (search.Cancelled)
        {
            // 用户点了停止：这不是「没找完」，不得让上层报找不完。
            return new PickResult();
        }

        var nested = NotExcluded(Allowed(search.Found));

        // 子目录里疑似藏了内嵌压缩包的文件：不是候选，这里也不去解，但必须如实报。
        var subCarriers = NotExcluded(Allowed(search.Carriers));

        // 本层在前、子目录在后，按物理身份去重：同一个包可能两边都出现。
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in candidates.Concat(nested))
        {
            if (seen.Add(FileIdentity.Of(path)))
            {
                merged.Add(path);
            }
        }

        var standardCount = merged.Count;

        // 伪装包与标准候选走同一张候选单。收集那一段排在几个提前返回之后就永远走不到：
        // 本层既有正常包、又有伪装包时，伪装包会被静默漏解还报完成。
        // 遮蔽条件不是「本层有候选」，而是「候选单非空」。
        var carriers = _options.ScanAppended
            ? NotExcluded(Allowed(CarrierCandidates(files, deep: merged.Count == 0)))
            : [];

        if (standardCount > 0)
        {
            // 顺序必须显式保住：正常候选在前、伪装包在后。按路径一把排会让视频排到包前面。
            foreach (var path in carriers)
            {
                if (seen.Add(FileIdentity.Of(path)))
                {
                    merged.Add(path);
                }
            }
        }

        var detail = SearchDetail(search, subCarriers);
        var incomplete = search.Truncated || subCarriers.Count > 0;

        if (merged.Count > 1)
        {
            // 顺序：正常候选按路径排（稳定可复现），伪装包按发现顺序接在后面。
            var ordered = new List<string>(merged.Take(standardCount).OrderBy(path => path, StringComparer.Ordinal));
            ordered.AddRange(merged.Skip(standardCount));
            var pick = new PickResult { SearchIncomplete = incomplete, SearchDetail = detail };
            pick.Targets.AddRange(ordered);
            pick.Unfinished.AddRange(subCarriers);
            return pick;
        }

        if (merged.Count == 1)
        {
            var only = merged[0];
            var pick = new PickResult
            {
                Path = only,
                Note = SingleTargetNote(only, directory),
                SearchIncomplete = incomplete,
                SearchDetail = detail,
            };
            pick.Unfinished.AddRange(subCarriers);
            return pick;
        }

        // 一个正常候选都没有，只剩「垫了真视频的伪装文件」这条路。
        // 多个伪装包之间仍然如实报多解歧义：那才是真分不清主次、且探测代价高的场景。
        if (carriers.Count == 1)
        {
            var pick = new PickResult
            {
                Path = carriers[0],
                Note = PierceText.CarrierTargetNote,
                SearchIncomplete = incomplete,
                SearchDetail = detail,
            };
            pick.Unfinished.AddRange(subCarriers);
            return pick;
        }

        if (carriers.Count > 1)
        {
            var pick = new PickResult { SearchIncomplete = incomplete, SearchDetail = detail };
            pick.Ambiguous.AddRange(carriers);
            pick.Unfinished.AddRange(subCarriers);
            return pick;
        }

        // 一个候选都没有：没找完或有疑似伪装包没解就如实说，不得报「文件夹里已没有可解压的压缩包」。
        if (!incomplete)
        {
            return new PickResult();
        }

        var tail = new PickResult { SearchIncomplete = true, SearchDetail = detail };
        tail.Unfinished.AddRange(subCarriers);
        return tail;
    }

    /// <summary>过一遍「不处理的文件类型」。本层与子目录必须共用这一份判据。</summary>
    internal List<string> NotExcluded(IEnumerable<string> paths)
    {
        var kept = new List<string>();
        foreach (var candidate in paths)
        {
            var ext = Probe.ExcludedExt(candidate, _options.ExcludeExts);
            if (ext.Length > 0)
            {
                _log?.Note(PierceText.ExcludedSkippedAt, Path.GetFileName(candidate), ext);
            }
            else
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    /// <summary>组装「没找完」的原因。保险丝烧了与有目录读不了必须分开说。</summary>
    private static string SearchDetail(DirSearch search, List<string> subCarriers)
    {
        var bits = new List<string>();
        if (search.Truncated)
        {
            bits.Add(search.Unreadable.Count > 0
                ? PierceText.Fill(PierceText.SubdirUnreadableAt, search.Unreadable.Count)
                : PierceText.Fill(PierceText.SubdirFuseAt, Probe.NestedScanMaxDirs, Probe.NestedScanMaxFiles));
        }

        if (subCarriers.Count > 0)
        {
            // 不是「没找完」，而是「按现在的做法不会去找」。如实说清是哪几个文件。
            bits.Add(PierceText.Fill(
                PierceText.SubCarriersAt,
                subCarriers.Count,
                string.Join('、', subCarriers.Take(3).Select(Path.GetFileName)),
                subCarriers.Count > 3
                    ? PierceText.Fill(PierceText.ListMoreAt, subCarriers.Count)
                    : string.Empty));
        }

        return string.Join('；', bits);
    }

    /// <summary>只有一个候选时的备注：分卷说清是哪一组，子目录里找到的说清它在哪。</summary>
    private string SingleTargetNote(string only, string directory)
    {
        try
        {
            var sameDir = string.Equals(
                Path.GetDirectoryName(PierceRegistry.Normalize(only)),
                PierceRegistry.Normalize(directory),
                StringComparison.Ordinal);
            if (sameDir)
            {
                var info = Probe.ClassifyVolume(only);
                var group = Probe.GroupVolumes([only])
                    .FirstOrDefault(candidate => string.Equals(
                        PierceRegistry.Normalize(candidate.Main),
                        PierceRegistry.Normalize(only),
                        StringComparison.Ordinal));
                return group is not null && group.IsSplit
                    ? Probe.Describe(Probe.DetectFormat(only), info)
                    : string.Empty;
            }

            var relative = RelativeTo(only, directory);
            _log?.Note(PierceText.SubdirSingleTargetAt, relative);
            return PierceText.Fill(PierceText.SubdirPackageAt, relative);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 这一层里「垫了马甲、内含压缩包」的文件，候选单的第三个通道。
    ///
    /// 判据走识别层的唯一入口，与界面扫描、子目录报备、处理目标同一份。
    /// 差异只在两个参数：这里<b>不传引擎</b>（按这一层每个大文件跑，问一次引擎就是一次进程开销），
    /// 深扫由调用方给 —— 全盘扫只在「这一层没有别的候选可解」时才有意义。
    /// </summary>
    internal List<string> CarrierCandidates(IReadOnlyList<string> files, bool deep)
    {
        var options = new ProbeOptions
        {
            Deep = deep,
            // 两问都不传，理由相同：这一层每个大文件都会跑一次，问一次引擎就是一次进程开销
            // （UnRAR 起进程还比 7-Zip 贵），而这一档只负责把候选挑出来。
            EngineProbe = null,
            FallbackEngineProbe = null,
            Cancellation = _cancellation,
            Log = _log,
        };

        var hits = new List<string>();
        foreach (var path in files)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (size < Probe.EmbedMinSize)
            {
                continue;
            }

            if (Probe.InspectCarrier(path, options).WorthHandling)
            {
                hits.Add(path);
            }
        }

        return hits;
    }
}
