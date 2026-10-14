namespace MaszynyAlert.Windows;

public sealed class SharedConfigReloadRequest : IDisposable
{
    public const string EventName = @"Local\MaszynyAlert.SharedConfigReload";

    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _registration;

    public SharedConfigReloadRequest(string eventName = EventName)
        => _signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

    public void Listen(Action reload)
    {
        if (_registration is not null) throw new InvalidOperationException("Already listening.");
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal,
            (_, _) => reload(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static bool TryRequest(string eventName = EventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var signal)) return false;
            using (signal) return signal.Set();
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _signal.Dispose();
    }
}
