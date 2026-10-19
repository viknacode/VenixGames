namespace Venix.Core;

/// <summary>
/// One Venix per user session: the first process owns a named mutex; a second one just signals a
/// named event (which the first turns into "restore the window") and exits.
/// </summary>
public static class SingleInstance
{
    const string MutexName = @"Local\Venix.Instance.2f8c1a";
    const string EventName = @"Local\Venix.Activate.2f8c1a";

    static Mutex? _mutex;
    static EventWaitHandle? _event;
    static RegisteredWaitHandle? _wait;

    /// <summary>True when this process is the first instance; false after the running one was asked to show itself.</summary>
    public static bool TryAcquire(Action activate)
    {
        // the event is created before the mutex is taken so the other side can never miss it
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            try { _event.Set(); } catch { }
            _event.Dispose();
            _mutex.Dispose();
            _event = null;
            _mutex = null;
            return false;
        }
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => activate(), null, -1, false);
        return true;
    }

    public static void Release()
    {
        try { _wait?.Unregister(null); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _event?.Dispose();
        _wait = null;
        _mutex = null;
        _event = null;
    }
}
