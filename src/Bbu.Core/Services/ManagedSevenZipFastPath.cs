using System;
using Bbu.Core.Models;
using Bbu.Core.Services.Managed7z;

namespace Bbu.Core.Services;

/// <summary>
/// 7z 快路的纯托管那一段。与 <see cref="SevenZipFastPathSession"/> 同形，
/// 但全程不碰 <c>7z.dll</c>，所以没有那把进程级静态锁。
/// </summary>
internal sealed class ManagedSevenZipFastPathSession : IFastPathSession
{
    private readonly ManagedSevenZipSession _session;

    private ManagedSevenZipFastPathSession(ManagedSevenZipSession session) => _session = session;

    /// <summary>
    /// 返回 <c>null</c> 只表示**托管这一段**判不了（未加密、链尾不是 LZMA2、没有可用的期望 CRC、
    /// 分卷缺卷或拼不齐）—— **不是**「整轮不进快路」。
    /// 调用方必须接着试 dll 那条，见 <see cref="CompositeFastPath.Open"/>。
    /// </summary>
    internal static ManagedSevenZipFastPathSession? Open(string archive)
    {
        ManagedSevenZipSession? session;
        try
        {
            // 卷表在这里算一次：dll 那条用的是同一份，两条路据此读到同一条流。
            // `Volumes()` 在卷号溢出时记一次输入类失败：同一个包经两条路会记两次，只影响计数读数。
            var volumes = SevenZipArchive.Volumes(archive);
            session = volumes.Length > 1
                ? ManagedSevenZipSession.OpenVolumes(volumes)
                : ManagedSevenZipSession.Open(archive);
        }
        catch (Exception)
        {
            // 与会话层同一条硬要求：托管这一段只许回 null，漏出去会被分派层当成整轮不进快路。
            return null;
        }

        return session is null ? null : new ManagedSevenZipFastPathSession(session);
    }

    public FastPathVerdict Judge(string password) => _session.Judge(password);

    public void Dispose() => _session.Dispose();
}
