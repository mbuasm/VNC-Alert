using System.Collections.Concurrent;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Probe.Tests.TestUtil;

/// <summary>
/// Silnik OCR na potrzeby testów narzędzia <c>probe.exe</c>: zwraca kolejne, z góry zadane wyniki
/// (po wyczerpaniu kolejki — <see cref="OcrResult.Empty"/>) i zapamiętuje obrazy, które dostał.
/// Odpowiednik <c>FakeOcrEngine</c> z testów Core, ale trzymany tutaj, żeby projekt testów Probe nie
/// zależał od projektu testów Core.
/// </summary>
public sealed class QueueOcrEngine : IOcrEngine
{
    private readonly ConcurrentQueue<OcrResult> _queue = new();
    private int _callCount;

    /// <inheritdoc />
    public string Name { get; set; } = "test";

    /// <inheritdoc />
    public bool IsAvailable { get; set; } = true;

    /// <inheritdoc />
    public string? LanguageTag { get; set; } = "en-US";

    /// <summary>Ile razy wywołano <see cref="RecognizeAsync"/> (kolejność wywołań jest częścią umowy <c>ScreenAnchors.ComputeAsync</c>).</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Rozmiary obrazów przekazanych do rozpoznania, w kolejności wywołań (np. do sprawdzenia, że drugie wywołanie dostało klatkę ×2).</summary>
    public List<(int Width, int Height)> ImageSizes { get; } = [];

    /// <summary>Dodaje do kolejki wynik ze słowami i ich położeniem (potrzebny do kotwiczenia na etykietach).</summary>
    public QueueOcrEngine EnqueueWords(params OcrWord[] words)
    {
        _queue.Enqueue(OcrResult.FromWords(words));
        return this;
    }

    /// <summary>Dodaje do kolejki wynik z samym tekstem, bez pozycji słów (tak wygląda weryfikacja wyciętego pola).</summary>
    public QueueOcrEngine EnqueueText(string text)
    {
        _queue.Enqueue(string.IsNullOrEmpty(text) ? OcrResult.Empty : new OcrResult(text, [text]));
        return this;
    }

    /// <summary>Dodaje do kolejki <paramref name="count"/> pustych wyników („OCR nic nie odczytał”).</summary>
    public QueueOcrEngine EnqueueEmpty(int count = 1)
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
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _callCount);
        lock (ImageSizes)
        {
            ImageSizes.Add((image.Width, image.Height));
        }

        return Task.FromResult(_queue.TryDequeue(out OcrResult? next) ? next : OcrResult.Empty);
    }
}
