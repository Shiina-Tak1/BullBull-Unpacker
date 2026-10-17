using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bbu.Core.Models;
using Bbu.Core.Texts;

namespace Bbu.Core.Services;

/// <summary>一次判定的结论：候选与它那一次的结果。</summary>
internal readonly record struct SweepOutcome(PasswordCandidate Candidate, EngineResult Result);

/// <summary>
/// 候选枚举的调度：一条快路生产线加一条引擎生产线，按候选顺序产出结论。
/// 判定在专用线程上取活、没有整窗屏障；落到引擎那一档的候选投给引擎生产线之后判定线程立刻继续。
/// 顺序语义不变：结论按候选下标推进，最早命中的赢，与串行逐项一致。
/// </summary>
internal sealed class CandidateSweep
{
    /// <summary>暂停期间两次探问之间的间隔。</summary>
    private const int PausePollMs = 20;

    /// <summary>快路划掉的候选共用这一个结果：字段全是只读的，内容与候选无关。</summary>
    private static readonly EngineResult RejectedResult = new()
    {
        Code = EngineExitCodes.FastPathRejectedCode,
        WrongPassword = true,
        Output = VaultText.FastPathRejected,
    };

    /// <summary>取消的结论：字段全是只读的，内容与候选无关，取值与产线收尾共用这一处。</summary>
    private static readonly EngineResult CancelledResult = new() { Cancelled = true };

    private readonly IReadOnlyList<PasswordCandidate> _candidates;
    private readonly IFastPathSession? _session;
    private readonly Func<PasswordCandidate, CancellationToken, Task<EngineResult>> _verify;
    private readonly TaskCompletionSource<EngineResult>[] _slots;
    private readonly ConcurrentQueue<int> _pending = new();
    private readonly SemaphoreSlim _idle = new(0);
    private readonly CancellationToken _cancellation;
    private readonly Func<bool>? _pause;
    private readonly Task[] _lanes;
    private readonly Task[] _engines;
    private int _next = -1;
    private int _stop;
    private int _aborted;
    private int _rejected;
    private int _engineTried;

    internal CandidateSweep(
        IReadOnlyList<PasswordCandidate> candidates,
        IFastPathSession? session,
        Func<PasswordCandidate, CancellationToken, Task<EngineResult>> verify,
        int workers,
        CancellationToken cancellationToken,
        Func<bool>? pause = null)
    {
        _candidates = candidates;
        _session = session;
        _verify = verify;
        _cancellation = cancellationToken;
        _pause = pause;

        // 槽先建齐再开工：消费者可能在生产线投递到它之前就走到那一个下标。
        _slots = new TaskCompletionSource<EngineResult>[candidates.Count];
        for (var i = 0; i < _slots.Length; i++)
        {
            // 续体必须异步：否则生产线线程会替消费者往下跑，把这条线程占死。
            _slots[i] = new TaskCompletionSource<EngineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // 判定是同步 CPU 工作，占满一整条线程：用 LongRunning 而不是线程池，以免线程池被判定占满之后
        // 引擎那一条路的续体排不上（线程池注水是秒级的）。
        _lanes = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            _lanes[i] = Task.Factory.StartNew(
                Lane, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        _engines = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            _engines[i] = Task.Run(EngineAsync);
        }
    }

    /// <summary>被快路划掉的候选数。</summary>
    internal int Rejected => Volatile.Read(ref _rejected);

    /// <summary>真的起过引擎的候选数。</summary>
    internal int EngineTried => Volatile.Read(ref _engineTried);

    /// <summary>按候选顺序产出结论；调用方break、抛异常、取消都会收掉两条生产线。</summary>
    internal async IAsyncEnumerable<SweepOutcome> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < _candidates.Count; i++)
            {
                var result = await ResultOfAsync(i, cancellationToken).ConfigureAwait(false);
                yield return new SweepOutcome(_candidates[i], result);
            }
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 取第 <paramref name="index"/> 个候选的结论。取消在这一步接住，不以异常逃出：
    /// 产线收尾时 <see cref="Abort"/> 已把没填的槽填成取消结论，下游照候选顺序收口成「已停止」；
    /// 已填好的槽按填好的算，取消不改写已经落定的结论。
    /// </summary>
    private async Task<EngineResult> ResultOfAsync(int index, CancellationToken cancellationToken)
    {
        try
        {
            return await _slots[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CancelledResult;
        }
    }

    /// <summary>生产线：取一个候选判定；判不了就交给引擎，自己接着取下一个。</summary>
    private void Lane()
    {
        try
        {
            while (Volatile.Read(ref _stop) == 0 && !_cancellation.IsCancellationRequested)
            {
                // 暂停停在取活之前：判定是进程内的 CPU 活，不拦住它，「暂停」就只是把引擎进程挂起来。
                while (_pause?.Invoke() == true)
                {
                    if (Volatile.Read(ref _stop) != 0 || _cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    Thread.Sleep(PausePollMs);
                }

                var index = Interlocked.Increment(ref _next);
                if (index >= _candidates.Count)
                {
                    return;
                }

                var candidate = _candidates[index];
                if (candidate.Value.Length > 0
                    && _session is not null
                    && Judge(_session, candidate.Value))
                {
                    Interlocked.Increment(ref _rejected);
                    _slots[index].TrySetResult(RejectedResult);
                    continue;
                }

                Interlocked.Increment(ref _engineTried);
                _pending.Enqueue(index);
                _idle.Release();
            }
        }
        finally
        {
            // 取消与停止这两条路都有人还在等槽：把没填的填掉，消费者与引擎线程都不再等。
            if (Volatile.Read(ref _stop) != 0 || _cancellation.IsCancellationRequested)
            {
                Abort();
            }
        }
    }

    /// <summary>引擎生产线：验证一个候选，填它的槽，再取下一个。</summary>
    private async Task EngineAsync()
    {
        while (true)
        {
            await _idle.WaitAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _stop) != 0)
            {
                return;
            }

            if (!_pending.TryDequeue(out var index))
            {
                continue;
            }

            // 这里是 async，等暂停用 await 而不是 Sleep：占着线程池线程等会拖慢判定那一条线。
            while (_pause?.Invoke() == true)
            {
                if (Volatile.Read(ref _stop) != 0)
                {
                    return;
                }

                await Task.Delay(PausePollMs).ConfigureAwait(false);
            }

            try
            {
                var result = await _verify(_candidates[index], _cancellation).ConfigureAwait(false);
                _slots[index].TrySetResult(result);
            }
            catch (Exception ex)
            {
                // 原样交给消费者：引擎那一路的异常一直是整轮搜索失败，这里不改这个语义。
                _slots[index].TrySetException(ex);
            }
        }
    }

    /// <summary>判定是优化：它自己出错就退回引擎，不许把整轮搜索停在这里。</summary>
    private static bool Judge(IFastPathSession session, string password)
    {
        try
        {
            return session.Judge(password) is FastPathVerdict.ProbablyWrong;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < _slots.Length; i++)
        {
            _slots[i].TrySetResult(CancelledResult);
        }
    }

    private async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stop, 1) == 0)
        {
            _idle.Release(_engines.Length);
        }

        await Task.WhenAll(_lanes).ConfigureAwait(false);
        await Task.WhenAll(_engines).ConfigureAwait(false);
    }
}
