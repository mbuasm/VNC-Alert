using System.Collections.Concurrent;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Silnik OCR na potrzeby testów: zwraca kolejne odpowiedzi z kolejki (po jej wyczerpaniu —
/// <see cref="DefaultResult"/>), liczy wywołania i zapamiętuje ostatni przekazany obraz.
/// </summary>
public sealed class FakeOcrEngine : IOcrEngine
{
    private readonly ConcurrentQueue<OcrResult> _queue = new();
    private int _callCount;

    /// <inheritdoc />
    public string Name { get; set; } = "test";

    /// <inheritdoc />
    public bool IsAvailable { get; set; } = true;

    /// <inheritdoc />
    public string? LanguageTag { get; set; } = "en-US";

    /// <summary>Wynik zwracany po wyczerpaniu kolejki (domyślnie pusty).</summary>
    public OcrResult DefaultResult { get; set; } = OcrResult.Empty;

    /// <summary>
    /// Gdy ustawione — <see cref="RecognizeAsync"/> RZUCA wyjątek zwrócony przez tę fabrykę (symulacja
    /// awarii silnika OCR, np. WinRT) zamiast zwrócić wynik. Wywołanie i tak jest liczone w
    /// <see cref="CallCount"/> — „próba się zużywa”.
    /// </summary>
    public Func<Exception>? Failure { get; set; }

    /// <summary>Ile PIERWSZYCH wywołań ma rzucić <see cref="Failure"/> (domyślnie wszystkie).</summary>
    public int FailingCalls { get; set; } = int.MaxValue;

    /// <summary>Ile razy wywołano <see cref="RecognizeAsync"/>.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Ostatni obraz przekazany do rozpoznania (albo null, gdy jeszcze nie wywołano).</summary>
    public Gray8Image? LastImage { get; private set; }

    /// <summary>Dodaje do kolejki wynik z podanym tekstem (jedna linia). Pusty tekst → wynik pusty.</summary>
    public FakeOcrEngine Enqueue(string? text)
    {
        _queue.Enqueue(string.IsNullOrEmpty(text)
            ? OcrResult.Empty
            : new OcrResult(text, new[] { text }));
        return this;
    }

    /// <summary>
    /// Dodaje do kolejki wynik zbudowany ze słów z pozycjami (<see cref="OcrResult.FromWords"/>) — do testów
    /// korzystających z <see cref="OcrResult.Words"/> (np. <c>Vision.LabelAnchorLocator</c>), gdzie samego
    /// tekstu linii nie wystarczy podać — potrzebne są też prostokąty poszczególnych słów.
    /// </summary>
    public FakeOcrEngine EnqueueWords(params OcrWord[] words)
    {
        _queue.Enqueue(OcrResult.FromWords(words));
        return this;
    }

    /// <summary>Dodaje do kolejki <paramref name="count"/> pustych wyników (symulacja nieudanego odczytu).</summary>
    public FakeOcrEngine EnqueueEmpty(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            _queue.Enqueue(OcrResult.Empty);
        }

        return this;
    }

    /// <inheritdoc />
    public Task<OcrResult> RecognizeAsync(Gray8Image image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int call = Interlocked.Increment(ref _callCount);
        LastImage = image;

        if (Failure is not null && call <= FailingCalls)
        {
            throw Failure();
        }

        return Task.FromResult(_queue.TryDequeue(out OcrResult? next) ? next : DefaultResult);
    }
}
