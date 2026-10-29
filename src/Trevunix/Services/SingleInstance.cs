namespace Trevunix.Services;

/// <summary>
///     Keeps one Trevunix per signed-in user. A second launch, for example from the Start menu
///     while Trevunix sits in the notification area, asks the first one to show its window and
///     then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Trevunix.SingleInstance";
    private const string ShowEventName = @"Local\Trevunix.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>Null when another Trevunix already runs.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(
            mutex,
            new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName)
        );
    }

    /// <summary>Asks the running Trevunix to show its window.</summary>
    public static void ShowExisting()
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
        {
            using (show)
                show.Set();
        }
    }

    /// <summary>Calls <paramref name="onShow"/> on a pool thread whenever another launch asks.</summary>
    public void Listen(Action onShow) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _show,
            (_, _) => onShow(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false
        );

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
