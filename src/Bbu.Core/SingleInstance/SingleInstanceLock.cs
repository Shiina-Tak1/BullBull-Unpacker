using System;
using System.Threading;

namespace Bbu.Core.SingleInstance;

/// <summary>
/// 主实例身份。拿到互斥体的是主实例，拿不到的是后来者。
/// 句柄要持有到进程退出，中途不能释放（释放等于把身份让出去）。
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owns;

    public SingleInstanceLock()
    {
        _mutex = new Mutex(initiallyOwned: false, SingleInstanceProtocol.MutexName);
        try
        {
            _owns = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是崩掉的 → 互斥体被抛弃，这次由本进程接手
            _owns = true;
        }
    }

    /// <summary>本进程是不是主实例。</summary>
    public bool IsPrimary => _owns;

    public void Dispose()
    {
        if (_owns)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 没持有就释放：忽略，退出路径上不该抛
            }

            _owns = false;
        }

        _mutex.Dispose();
    }
}
