using System.Threading;
using System.Threading.Tasks;

namespace Bbu.Core.Models;

/// <summary>
/// 一次询问的输入：哪一个包、已经试过几个、这是第几次问，以及给用户看的那一句。
/// </summary>
/// <param name="ArchivePath">要密码的那个包。</param>
/// <param name="DisplayName">显示用的名字。</param>
/// <param name="Tried">自动试过的候选个数。</param>
/// <param name="Attempt">第几次问，从 1 起。大于 1 表示上一次输入的密码不对。</param>
/// <param name="Summary">这一次为什么问，由密码层给出。</param>
public sealed record PasswordAsk(
    string ArchivePath,
    string DisplayName,
    int Tried,
    int Attempt,
    string Summary);

/// <summary>
/// 问用户要一个密码。返回 <c>null</c> 或空串表示用户跳过这个包。
///
/// 通道不在时不问，照旧按「密码耗尽」报：把「没通道」说成「用户跳过」是在替用户表态。
/// </summary>
public delegate Task<string?> AskPasswordAsync(PasswordAsk ask, CancellationToken cancellationToken);
