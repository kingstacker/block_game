namespace BlockGame.Core.Services;

/// <summary>
/// 基于命名互斥体的跨进程锁，用于串行化 App、Guard、卸载器对共享文件的读写。
/// 拿不到锁（超时或权限差异）时降级为无锁继续，避免锁问题反过来阻断保护功能。
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _acquired;

    private CrossProcessLock(Mutex? mutex, bool acquired)
    {
        _mutex = mutex;
        _acquired = acquired;
    }

    public static CrossProcessLock Acquire(string name, TimeSpan timeout)
    {
        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // 持有进程异常退出；锁已转移到当前线程，文件本身通过临时文件+替换保持一致。
                acquired = true;
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or WaitHandleCannotBeOpenedException)
        {
            mutex?.Dispose();
            mutex = null;
        }

        return new CrossProcessLock(mutex, acquired);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 已在别处释放；忽略。
            }
        }

        _mutex.Dispose();
    }
}
