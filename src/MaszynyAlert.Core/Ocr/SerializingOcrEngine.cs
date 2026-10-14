using MaszynyAlert.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Ocr;

/// <summary>
/// Dekorator <see cref="IOcrEngine"/>, który SERIALIZUJE wywołania <see cref="RecognizeAsync"/> do
/// jednego na raz — <c>Windows.Media.Ocr</c> (<c>WindowsOcrEngine</c>) nie jest gwarantowane jako
/// bezpieczne przy współbieżnych wywołaniach, a <c>MachineMonitor</c> każdej z monitorowanych maszyn
/// woła OCR RÓWNOLEGLE (osobna pętla na maszynę). Rejestrowany jako JEDEN singleton w DI
/// (<c>MaszynyAlert.App.Program</c>) i współdzielony przez wszystkie <c>MachineMonitor</c>, więc kolejka
/// dotyczy CAŁEJ aplikacji, nie jednej maszyny.
/// </summary>
/// <remarks>
/// Osobny limit kolejki i wykonania (domyślnie po 3 s). Porzucony odczyt nadal
/// zajmuje bramkę, aż natywny silnik faktycznie zakończy pracę. Opisy błędów
/// mają pierwszeństwo przed oczekującym OCR ekranu i procentów.
/// </remarks>
public sealed class SerializingOcrEngine : IOcrEngine
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(3);
    private const long WarnThrottleMs = 60000;

    private IOcrEngine _inner;
    private readonly TimeSpan _waitTimeout;
    private readonly TimeSpan _executionTimeout;
    private int _timedOut;
    private int _pendingErrors;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Kiedy (po <see cref="Environment.TickCount64"/>) zalogowano ostatnie ostrzeżenie o pominiętym odczycie.
    /// Start od <c>-WarnThrottleMs</c> (NIE <see cref="long.MinValue"/> — odejmowanie od niego przepełniałoby
    /// się i pierwsze ostrzeżenie nigdy by się nie zalogowało), żeby pierwsze pominięcie zalogowało się od razu.
    /// </summary>
    private long _lastWarnTickMs = -WarnThrottleMs;

    private int _skippedSinceLastWarn;

    public SerializingOcrEngine(IOcrEngine inner, TimeSpan? waitTimeout = null, ILogger? logger = null, TimeSpan? executionTimeout = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
        _executionTimeout = executionTimeout ?? TimeSpan.FromSeconds(3);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Opakowany silnik (np. do logowania/diagnostyki) — samo <see cref="SerializingOcrEngine"/> niczego mu nie zmienia poza kolejkowaniem.</summary>
    public IOcrEngine Inner => _inner;
    public async Task ReplaceInnerAsync(Func<IOcrEngine> create, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(_waitTimeout, ct).ConfigureAwait(false))
            throw new InvalidOperationException("OCR nadal pracuje; nie można teraz zastosować innego silnika.");
        try { _inner = create(); Interlocked.Exchange(ref _timedOut, 0); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public bool IsAvailable => _inner.IsAvailable && Volatile.Read(ref _timedOut) == 0;

    /// <inheritdoc />
    public string? LanguageTag => _inner.LanguageTag;

    public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct) => RecognizeCoreAsync(image, ct, false);

    public Task<OcrResult> RecognizeErrorAsync(Gray8Image image, CancellationToken ct) => RecognizeCoreAsync(image, ct, true);

    private async Task<bool> EnterAsync(bool error, CancellationToken ct)
    {
        long started = Environment.TickCount64;
        if (error) Interlocked.Increment(ref _pendingErrors);
        try
        {
            while (true)
            {
                var remaining = _waitTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                if (!await _gate.WaitAsync(remaining, ct).ConfigureAwait(false)) return false;
                if (error || Volatile.Read(ref _pendingErrors) == 0) return true;
                _gate.Release();
                if (remaining == TimeSpan.Zero) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10, remaining.TotalMilliseconds)), ct).ConfigureAwait(false);
            }
        }
        finally { if (error) Interlocked.Decrement(ref _pendingErrors); }
    }

    private async Task<OcrResult> RecognizeCoreAsync(Gray8Image image, CancellationToken ct, bool error)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAvailable) return OcrResult.NotAvailable;
        bool entered = await EnterAsync(error, ct).ConfigureAwait(false);
        if (!entered)
        {
            ReportSkipped();
            return OcrResult.NotAvailable;
        }

        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<OcrResult>? running = null;
        bool deferredRelease = false;
        try
        {
            running = _inner.RecognizeAsync(image, executionCts.Token);
            return await running.WaitAsync(_executionTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref _timedOut, 1);
            ReportSkipped();
            return OcrResult.NotAvailable;
        }
        finally
        {
            if (running is { IsCompleted: false })
            {
                deferredRelease = true;
                // Własność bramki zostaje przy rzeczywistym WinRT. Anulowanie oczekiwania
                // NIE upoważnia do uruchomienia drugiego rozpoznania na tej instancji.
                Task cancellation = executionCts.CancelAsync();
                _ = ReleaseWhenFinishedAsync(running, executionCts, cancellation);
            }
            if (!deferredRelease)
            {
                executionCts.Dispose();
                Interlocked.Exchange(ref _timedOut, 0);
                _gate.Release();
            }
        }
    }

    private async Task ReleaseWhenFinishedAsync(Task<OcrResult> running, CancellationTokenSource cts, Task cancellation)
    {
        try { await Task.WhenAll(running, cancellation).ConfigureAwait(false); }
        catch (Exception) { /* Wynik porzuconego odczytu nie wraca do monitora. */ }
        finally
        {
            cts.Dispose();
            Interlocked.Exchange(ref _timedOut, 0);
            _gate.Release();
        }
    }

    private void ReportSkipped()
    {
        Interlocked.Increment(ref _skippedSinceLastWarn);

        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastWarnTickMs);
        if (now - last < WarnThrottleMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastWarnTickMs, now, last) != last)
        {
            // Inny wątek właśnie wygrał wyścig o zalogowanie tego okna — jego licznik obejmie to pominięcie.
            return;
        }

        int count = Interlocked.Exchange(ref _skippedSinceLastWarn, 0);
        _logger.LogWarning(
            "Silnik OCR ('{Engine}') zajęty lub przekroczył czas wykonania — pominięto odczyt (limit kolejki {QueueMs} ms, wykonania {ExecutionMs} ms). Od poprzedniego ostrzeżenia pominięto {SkippedCount} odczyt(ów).",
            _inner.Name, (int)_waitTimeout.TotalMilliseconds, (int)_executionTimeout.TotalMilliseconds, count);
    }
}
