namespace MaszynyAlert.Windows;

/// <summary>Local-session signal: a second launch can only request the operator preview.</summary>
public sealed class PreviewActivation : IDisposable
{
    public const string EventName = @"Local\MaszynyAlert.ShowPreview";
    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _registration;

    // Create before loading configuration so a click during startup is retained.
    public PreviewActivation(string eventName = EventName)
        => _signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

    public void Listen(Action showPreview)
    {
        if (_registration is not null) throw new InvalidOperationException("Already listening.");
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal,
            (_, _) => showPreview(), null, Timeout.Infinite, executeOnlyOnce: false);
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
