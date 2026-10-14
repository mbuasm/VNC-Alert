using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Ocr;

/// <summary>
/// Dekorator serializujący wywołania OCR do jednego na raz (jeden silnik Windows.Media.Ocr na CAŁĄ
/// aplikację, a monitorów maszyn jest siedem i każdy woła OCR ze swojej pętli). Testy są odporne na
/// wolny komputer: synchronizacja idzie przez <see cref="TaskCompletionSource"/>/<see cref="SemaphoreSlim"/>,
/// nigdzie nie ma „poczekaj X ms i załóż, że zdążyło”.
/// </summary>
public sealed class SerializingOcrEngineTests
{
    /// <summary>Obraz-atrapa — dekorator go nie ogląda, przekazuje wprost do opakowanego silnika.</summary>
    private static Gray8Image Image() => new(4, 4);

    /// <summary>Ile najwyżej czekamy na zdarzenie, które w poprawnym kodzie następuje natychmiast (zabezpiecza test przed zawieszeniem).</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Silnik OCR liczący, ile wywołań jest W ŚRODKU naraz (i maksimum tej liczby w całym teście).
    /// Gdy ustawiono <see cref="Gate"/>, każde wywołanie czeka w środku, aż test je zwolni — dzięki temu
    /// „silnik zajęty” jest stanem PEWNYM, a nie wynikiem odczekania jakiejś liczby milisekund.
    /// </summary>
    private sealed class CountingOcrEngine : IOcrEngine
    {
        private readonly SemaphoreSlim _entered = new(0);
        private int _callCount;
        private int _inside;
        private int _maxInside;

        public string Name { get; set; } = "silnik-testowy";

        public bool IsAvailable { get; set; } = true;

        public string? LanguageTag { get; set; } = "pl-PL";

        /// <summary>Gdy ustawione — wywołanie czeka w środku silnika, aż ten <see cref="TaskCompletionSource"/> zostanie ukończony.</summary>
        public TaskCompletionSource Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gdy prawda — <see cref="Gate"/> jest ignorowana, a wywołanie oddaje sterowanie (asynchroniczny punkt) i wraca.</summary>
        public bool GateOpen { get; set; }

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>Największa liczba wywołań, które JEDNOCZEŚNIE były w środku silnika.</summary>
        public int MaxInside => Volatile.Read(ref _maxInside);

        /// <summary>Czeka, aż kolejne wywołanie wejdzie do środka silnika (bez odliczania czasu „na oko”).</summary>
        public Task WaitForEntryAsync() => _entered.WaitAsync(Patience);

        public async Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            int inside = Interlocked.Increment(ref _inside);
            UpdateMax(inside);
            _entered.Release();

            try
            {
                if (GateOpen)
                {
                    await Task.Yield();
                }
                else
                {
                    await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                return new OcrResult("No.705 [N]Axis 6 servo alarm", ["No.705 [N]Axis 6 servo alarm"]);
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }

        private void UpdateMax(int inside)
        {
            int seen = Volatile.Read(ref _maxInside);
            while (inside > seen)
            {
                int previous = Interlocked.CompareExchange(ref _maxInside, inside, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }

    [Fact]
    public async Task RecognizeAsync_SiedmiuWywolanNaraz_WpuszczaDoSilnikaPoJednym()
    {
        var inner = new CountingOcrEngine { GateOpen = true };
        var engine = new SerializingOcrEngine(inner, TimeSpan.FromSeconds(30), executionTimeout: Patience);

        Task<OcrResult>[] calls = Enumerable
            .Range(0, 7)
            .Select(_ => engine.RecognizeAsync(Image(), CancellationToken.None))
            .ToArray();
        OcrResult[] results = await Task.WhenAll(calls);

        Assert.Equal(7, results.Length);
        Assert.All(results, r => Assert.Equal("No.705 [N]Axis 6 servo alarm", r.Text));
        Assert.Equal(1, inner.MaxInside);
        Assert.Equal(7, inner.CallCount);
    }

    [Fact]
    public async Task RecognizeAsync_GdySilnikZajetyDluzejNizLimitOczekiwania_ZwracaPustyWynikIOstrzegaRaz()
    {
        // Dlaczego: pętla monitora nie może utknąć na kłódce OCR — maszyna, która czeka na cudzy odczyt,
        // przestałaby sprawdzać czerwień na ekranie i alert by się spóźnił. Pusty wynik jest bezpieczny:
        // czerwień i tak jest wykryta niezależnie od OCR, więc alert („nie odczytano treści”) się nie gubi.
        var logs = new RecordingLoggerFactory();
        var inner = new CountingOcrEngine();
        var engine = new SerializingOcrEngine(inner, TimeSpan.FromMilliseconds(100), logs.CreateLogger("Ocr"), Patience);

        Task<OcrResult> holding = engine.RecognizeAsync(Image(), CancellationToken.None);
        await inner.WaitForEntryAsync();

        OcrResult skipped = await engine.RecognizeAsync(Image(), CancellationToken.None);

        Assert.Same(OcrResult.NotAvailable, skipped);
        Assert.Equal(1, inner.CallCount); // drugie wywołanie NIE dotarło do silnika

        LogEntry warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith(
            "Silnik OCR ('silnik-testowy') zajęty lub przekroczył czas wykonania — pominięto odczyt",
            warning.Message,
            StringComparison.Ordinal);

        inner.Gate.SetResult();
        await holding;
    }

    [Fact]
    public async Task RecognizeAsync_DwaPominieciaPodRzad_LogujaTylkoJednoOstrzezenie()
    {
        // Dlaczego: zator na jednym silniku OCR dotyczy od razu WSZYSTKICH maszyn i powtarza się co cykl
        // (co sekundę) — bez dławienia log zapełniłby się w minutę identycznymi wpisami i nie dałoby się
        // z niego wyczytać niczego innego. Pominięty odczyt ma jednak ZAWSZE wrócić jako pusty wynik.
        var logs = new RecordingLoggerFactory();
        var inner = new CountingOcrEngine();
        var engine = new SerializingOcrEngine(inner, TimeSpan.FromMilliseconds(100), logs.CreateLogger("Ocr"), Patience);

        Task<OcrResult> holding = engine.RecognizeAsync(Image(), CancellationToken.None);
        await inner.WaitForEntryAsync();

        OcrResult first = await engine.RecognizeAsync(Image(), CancellationToken.None);
        OcrResult second = await engine.RecognizeAsync(Image(), CancellationToken.None);

        Assert.Same(OcrResult.NotAvailable, first);
        Assert.Same(OcrResult.NotAvailable, second);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);

        inner.Gate.SetResult();
        await holding;
    }

    [Fact]
    public async Task RecognizeAsync_GdyTokenAnulowanyWTrakcieOczekiwania_RzucaOperationCanceled()
    {
        // Dlaczego: anulowanie to zatrzymywanie programu (MonitorSupervisor.StopAsync) — pętla ma się
        // wtedy zakończyć, a nie „udać”, że OCR nic nie odczytał i zrobić jeszcze jeden obieg z alertem.
        // Dekorator musi więc zachować się dokładnie jak silnik bez dekoratora.
        var inner = new CountingOcrEngine();
        // executionTimeout JAWNIE długi: domyślne 3 s dotyczą wywołania TRZYMAJĄCEGO bramkę, więc na
        // obciążonym runnerze CI dekorator porzucał je w połowie testu, zwalniał bramkę i wpuszczał do
        // silnika wywołanie, które miało zostać anulowane (CallCount 2 zamiast 1). Test sprawdza
        // anulowanie OCZEKIWANIA, nie limit wykonania — ten limit ma tu po prostu nie wypaść.
        var engine = new SerializingOcrEngine(inner, TimeSpan.FromSeconds(30), executionTimeout: Patience);
        using var cts = new CancellationTokenSource();

        Task<OcrResult> holding = engine.RecognizeAsync(Image(), CancellationToken.None);
        await inner.WaitForEntryAsync();

        Task<OcrResult> waiting = engine.RecognizeAsync(Image(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, inner.CallCount);

        inner.Gate.SetResult();
        await holding;
    }

    [Fact]
    public void Wlasciwosci_DeleguujaDoOpakowanegoSilnika()
    {
        // Dlaczego: okno Status i logi pokazują nazwę i język silnika, a bramka „czy OCR jest dostępny”
        // decyduje, czy obserwacja postoju w ogóle działa — po opakowaniu dekoratorem wszystko to musi
        // pokazywać PRAWDZIWY silnik, nie atrapę dekoratora.
        var inner = new CountingOcrEngine { Name = "Windows.Media.Ocr", IsAvailable = false, LanguageTag = "en-US" };

        var engine = new SerializingOcrEngine(inner);

        Assert.Same(inner, engine.Inner);
        Assert.Equal("Windows.Media.Ocr", engine.Name);
        Assert.False(engine.IsAvailable);
        Assert.Equal("en-US", engine.LanguageTag);

        inner.IsAvailable = true;
        Assert.True(engine.IsAvailable);
    }

    [Fact]
    public void Konstruktor_BezOpakowanegoSilnika_RzucaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SerializingOcrEngine(null!));
    }
}
