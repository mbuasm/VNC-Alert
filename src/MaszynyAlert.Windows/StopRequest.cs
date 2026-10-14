using System.Diagnostics;

namespace MaszynyAlert.Windows;

/// <summary>
/// Oficjalna komenda zatrzymania programu (<c>vncalert killapp</c> → <c>MaszynyAlert.App.exe --stop</c>).
/// Działa jak <see cref="PreviewActivation"/>: sygnał w obrębie sesji, bez okna i bez uprawnień administratora.
/// Program po odebraniu sygnału zamyka się normalnie (zapis historii, zwolnienie mutexu), a proces komendy
/// czeka, aż mutex jednej instancji zniknie — dzięki temu instalator wie, kiedy pliki są wolne.
/// </summary>
public sealed class StopRequest : IDisposable
{
    public const string EventName = @"Local\MaszynyAlert.StopRequest";

    /// <summary>Mutex jednej instancji z <c>Program.Main</c>; jego zniknięcie oznacza, że proces się zakończył.</summary>
    public const string AppMutexName = @"Global\MaszynyAlert.App";

    public const string Argument = "--stop";

    public static string? GlobalNameFor(string eventName) =>
        eventName.StartsWith(@"Local\", StringComparison.Ordinal) ? @"Global\" + eventName[@"Local\".Length..] : null;

    private readonly EventWaitHandle _signal;
    private readonly EventWaitHandle? _globalSignal;
    private RegisteredWaitHandle? _registration;
    private RegisteredWaitHandle? _globalRegistration;

    // Tworzone tuż po mutexie, przed wczytaniem konfiguracji: komenda wysłana w trakcie startu nie przepada.
    public StopRequest(string eventName = EventName)
    {
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        if (GlobalNameFor(eventName) is not { } global) return;
        try
        {
            var security = new System.Security.AccessControl.EventWaitHandleSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            foreach (var account in new[] { identity.User!, new System.Security.Principal.SecurityIdentifier(
                         System.Security.Principal.WellKnownSidType.LocalSystemSid, null) })
            {
                security.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(account,
                    System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            }
            var created = System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, global, out bool createdNew, security);
            if (createdNew) _globalSignal = created;
            else created.Dispose();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            _globalSignal = null;
        }
    }

    public void Listen(Action stop)
    {
        if (_registration is not null) throw new InvalidOperationException("Already listening.");
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal,
            (_, _) => stop(), null, Timeout.Infinite, executeOnlyOnce: false);
        if (_globalSignal is not null)
        {
            _globalRegistration = ThreadPool.RegisterWaitForSingleObject(_globalSignal,
                (_, _) => stop(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
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

    /// <summary>
    /// Wykonuje komendę <c>--stop</c>: prosi działający program o zamknięcie i czeka na jego zakończenie.
    /// </summary>
    /// <returns>
    /// <c>0</c>, gdy program nie działa albo zatrzymał się w podanym czasie (komenda jest więc powtarzalna),
    /// <c>1</c>, gdy po upływie <paramref name="timeout"/> nadal działa.
    /// </returns>
    public static int RequestStop(TimeSpan timeout, string eventName = EventName, string mutexName = AppMutexName)
    {
        // Najpierw sygnał tej sesji (vncalert killapp, instalator z UAC); gdy go nie ma — globalny (instalator jako SYSTEM).
        bool requested = TryRequest(eventName) || (GlobalNameFor(eventName) is { } global && TryRequest(global));
        if (!requested && !IsRunning(mutexName)) return 0;
        return WaitUntilStopped(timeout, mutexName) ? 0 : 1;
    }

    /// <summary>Czeka, aż mutex jednej instancji przestanie istnieć (proces zakończony i handle zamknięte).</summary>
    public static bool WaitUntilStopped(TimeSpan timeout, string mutexName = AppMutexName)
    {
        var elapsed = Stopwatch.StartNew();
        while (IsRunning(mutexName))
        {
            if (elapsed.Elapsed >= timeout) return false;
            Thread.Sleep(100);
        }

        return true;
    }

    /// <summary>
    /// Czy mutex jednej instancji istnieje. Brak prawa otwarcia (mutex innego użytkownika) liczymy jako
    /// „działa”: uczciwiej zgłosić przekroczenie czasu niż udawać, że program został zatrzymany.
    /// </summary>
    public static bool IsRunning(string mutexName = AppMutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _globalRegistration?.Unregister(null);
        _signal.Dispose();
        _globalSignal?.Dispose();
    }
}
