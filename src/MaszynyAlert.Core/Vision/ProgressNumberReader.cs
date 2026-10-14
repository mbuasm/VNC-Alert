using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Vision;

public sealed class ProgressNumberReader
{
    internal const long RefreshIntervalMs = 60000;
    private readonly object _cacheLock = new();
    private int _generation;
    private readonly IdleWatchProfile _profile;
    private readonly bool _invert;
    private readonly IOcrEngine _ocr;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private long _lastReadMs;

    private byte[]? _lastPixels;
    private int _lastWidth = -1;
    private int _lastHeight = -1;
    private ProgressDetectionResult _lastResult = Unreadable();

    /// <summary>Kiedy ostatnio zalogowano ostrzeżenie o awarii silnika OCR — do throttlingu (patrz <see cref="LogFailureThrottled"/>).</summary>
    private DateTimeOffset? _lastOcrFailureLoggedAt;

    public ProgressNumberReader(IdleWatchProfile profile, bool darkTheme, IOcrEngine ocr, ILogger? logger = null, IClock? clock = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? SystemClock.Instance;

        // InvertForOcr=null (domyślnie) → zgodnie z motywem profilu ekranu: na ciemnym tle liczba jest
        // jasna, więc trzeba ją odwrócić przed OCR (który oczekuje ciemnego tekstu na jasnym tle) — patrz
        // ImageOps.PrepareTextForOcr. Jawna wartość w konfiguracji pozwala nadpisać to per-sekcja "idle",
        // gdyby akurat obszar liczby procent miał inny motyw niż reszta ekranu.
        _invert = profile.InvertForOcr ?? darkTheme;
    }

    /// <summary>Ile razy w tej instancji faktycznie wywołano <see cref="IOcrEngine.RecognizeAsync"/> (diagnostyka/testy) — patrz cache w dokumentacji klasy.</summary>
    public int OcrCalls { get; private set; }

    /// <summary>
    /// Analizuje jeden cykl: <paramref name="progressRoiImage"/> to wycinek z liczbą procent (już wycięty
    /// do <c>IdleWatchProfile.ProgressRoi</c>). Gdy bajty wycinka są identyczne jak przy ostatnim wywołaniu
    /// — zwraca zapamiętany wynik do czasu okresowego odświeżenia (patrz dokumentacja klasy).
    /// </summary>
    public async Task<ProgressDetectionResult> ReadAsync(RgbaImage progressRoiImage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progressRoiImage);
        if (!_ocr.IsAvailable)
        {
            Reset();
            return Unreadable() with { OcrUnavailable = true, RawText = "OCR chwilowo niedostępny" };
        }

        int generation;
        long startedAt = _clock.MonotonicMs;
        lock (_cacheLock)
        {
            if (SameAsLastImage(progressRoiImage) && startedAt - _lastReadMs < (_lastResult.Percent.HasValue ? RefreshIntervalMs : 5000))
                return _lastResult;
            generation = _generation;
        }

        // Own the pixels across the await; a source may reuse its framebuffer.
        progressRoiImage = progressRoiImage.Clone();
        ProgressDetectionResult result = await RecognizeAsync(progressRoiImage, ct).ConfigureAwait(false);

        lock (_cacheLock)
        {
            // Reset during OCR must not let the old continuation repopulate the cache.
            if (generation == _generation)
            {
                _lastPixels = progressRoiImage.Pixels;
                _lastWidth = progressRoiImage.Width;
                _lastHeight = progressRoiImage.Height;
                _lastResult = result;
                _lastReadMs = startedAt;
            }
        }

        return result;
    }

    public void Reset()
    {
        lock (_cacheLock)
        {
            _generation++;
            _lastPixels = null;
            _lastResult = Unreadable();
        }
    }

    // Only the monitor's bounded pending-read path may extend use beyond the refresh interval.
    // Reading this cache does not renew its age. Completed failures replace it with unreadable.
    internal ProgressDetectionResult? GetResultWhileRefreshing(RgbaImage image, long graceMs)
    {
        lock (_cacheLock)
        {
            return _ocr.IsAvailable && _lastResult.Percent.HasValue && SameAsLastImage(image)
                && _clock.MonotonicMs - _lastReadMs < RefreshIntervalMs + graceMs ? _lastResult : null;
        }
    }

    private bool SameAsLastImage(RgbaImage img)
    {
        if (_lastPixels is null || img.Width != _lastWidth || img.Height != _lastHeight)
        {
            return false;
        }

        return _lastPixels.AsSpan().SequenceEqual(img.Pixels);
    }

    internal static readonly int[] FallbackScales = [1, 2];

    private async Task<ProgressDetectionResult> RecognizeAsync(RgbaImage progressRoiImage, CancellationToken ct)
    {
        if (!_ocr.IsAvailable)
        {
            // Nie powinno się zdarzyć w normalnej pracy (MachineMonitor w ogóle nie tworzy obserwacji
            // postoju w trybie number, gdy silnik OCR jest niedostępny — patrz MachineMonitor), ale ta
            // klasa ma być bezpieczna użyta samodzielnie: bez silnika po prostu nic nie odczytamy.
            return Unreadable() with { OcrUnavailable = true, RawText = "OCR chwilowo niedostępny" };
        }

        // Pierwsza próba: skala z profilu. Kolejne (FallbackScales) tylko wtedy,
        // gdy pierwsza nie dała liczby — i wynik kolejnej próby przyjmujemy WYŁĄCZNIE, gdy liczba stoi przed
        // znakiem '%' (także 'z' odczytanym zamiast '%', patrz PercentParser). Dodatkowa próba to dodatkowa
        // szansa na śmieciowy odczyt, więc goła cyfra bez '%' z próby zapasowej zostaje „nie wiem”.
        string? firstText = null;
        var scales = new List<int> { _profile.OcrScale };
        scales.AddRange(FallbackScales.Where(f => f != _profile.OcrScale));
        for (int i = 0; i < scales.Count; i++)
        {
            bool fallback = i > 0;
            string text;
            try
            {
                Gray8Image forOcr = ImageOps.PrepareTextForOcr(progressRoiImage, _profile.OcrMarginPx, scales[i], _invert);
                OcrResult ocrResult = await _ocr.RecognizeAsync(forOcr, ct).ConfigureAwait(false);
                if (ocrResult.Unavailable) return Unreadable() with { OcrUnavailable = true, RawText = "OCR chwilowo niedostępny" };
                text = ocrResult.Text;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Awaria silnika OCR (np. WinRT) NIE MOŻE ubić pętli obserwatora — łapiemy ją tutaj, logujemy
                // (nie częściej niż raz na minutę na maszynę, żeby zepsuty silnik nie zasypał logu co cykl)
                // i zwracamy wynik "nieczytelny" — patrz Unreadable(). Bez kolejnych prób: zepsuty silnik
                // nie naprawi się przy innej skali.
                LogFailureThrottled(ex);
                return Unreadable();
            }
            finally
            {
                OcrCalls++;
            }

            firstText ??= text;
            if (!PercentParser.TryParse(text, out int percent, out bool hasNonZeroFraction, out bool hasPercentSign)
                || (fallback && !hasPercentSign))
            {
                continue;
            }

            bool isFull = percent >= _profile.FullAtPercent;
            return new ProgressDetectionResult(percent / 100.0, isFull)
            {
                Percent = percent, HasNonZeroFraction = hasNonZeroFraction, RawText = text,
            };
        }

        // Nieczytelne (pusty tekst albo bez rozpoznawalnej liczby w żadnej próbie) — ZACHOWAWCZO traktujemy jak
        // brak postępu (FilledFraction=0, IsFull=false): brak alertu w tej sytuacji jest nieszkodliwy (najwyżej
        // ktoś sprawdzi maszynę osobiście), a fałszywy alert "stoi bez pracy" budziłby ludzi w nocy bez powodu —
        // dokładnie odwrotne ryzyko. RawText = odczyt pierwszej próby (skala z profilu).
        return new ProgressDetectionResult(0.0, false) { Percent = null, RawText = firstText };
    }

    private void LogFailureThrottled(Exception ex)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_lastOcrFailureLoggedAt is { } last && now - last < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastOcrFailureLoggedAt = now;
        _logger.LogWarning(ex, "Błąd silnika OCR ('{Engine}') przy odczycie postępu (tryb number): {Error}", _ocr.Name, ex.Message);
    }

    private static ProgressDetectionResult Unreadable() => new(0.0, false) { Percent = null, RawText = null };
}
