using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace MaszynyAlert.App.Tests;

/// <summary>
/// Jeden wątek STA z dyspozytorem WPF i zasobami <see cref="App"/> (style, pędzle, konwertery z App.xaml) dla testów,
/// które budują prawdziwe kontrolki. <see cref="System.Windows.Application"/> może być w procesie tylko jedna, więc
/// wszystkie takie testy idą przez ten sam wątek po kolei.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Wykonuje <paramref name="body"/> na wątku UI i przekazuje wynik albo wyjątek testowi.</summary>
    public static T Run<T>(Func<T> body)
    {
        // Wywołanie z samego wątku UI (ciało testu woła Run ponownie) idzie wprost — bramkę trzyma już test zewnętrzny.
        if (Ui.Value.CheckAccess()) return body();

        Gate.Wait();
        try
        {
            ExceptionDispatchInfo? failure = null;
            T result = Ui.Value.Invoke(() =>
            {
                try { return body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); return default!; }
            });
            failure?.Throw();
            return result;
        }
        finally { Gate.Release(); }
    }

    public static void Run(Action body) => Run(() => { body(); return 0; });

    /// <summary>Lewy górny róg okna testu — daleko poza każdym ekranem (także przy kilku monitorach).</summary>
    public const double OffScreenPosition = -30000;

    public static T OffScreen<T>(T window) where T : System.Windows.Window
    {
        window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        window.Left = OffScreenPosition;
        window.Top = OffScreenPosition;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        return window;
    }

    /// <summary><see cref="OffScreen{T}"/> i <c>Show()</c>.</summary>
    public static T ShowOffScreen<T>(T window) where T : System.Windows.Window
    {
        OffScreen(window).Show();
        return window;
    }

    /// <summary>Opróżnia kolejkę dyspozytora do priorytetu <paramref name="priority"/> włącznie (zagnieżdżona pętla).</summary>
    public static void Pump(DispatcherPriority priority) => Dispatcher.CurrentDispatcher.Invoke(() => { }, priority);

    /// <summary>Plik z drzewa źródeł (XAML okna czytany w teście tak, jak go skompilowano do programu).</summary>
    public static string SourceFile(params string[] relative)
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine([dir, .. relative]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Nie znaleziono pliku w drzewie źródeł: " + Path.Combine(relative));
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                _ = new App();
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ready.Set();
            if (error is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (error is not null) throw new InvalidOperationException("Nie udało się uruchomić wątku WPF dla testów.", error);
        return dispatcher!;
    }
}
