namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>Jak atrapa wysyła odpowiedź: w całości, po 1 bajcie albo losowymi kawałkami (z losowymi opóźnieniami).</summary>
public enum WriteMode
{
    Whole,
    OneByte,
    RandomChunks,
}

/// <summary>
/// Zapis odpowiedzi atrapy w trybie <see cref="WriteMode"/> — do sprawdzenia, że dzielenie strumienia TCP (gniazdo atrapy
/// ma <c>NoDelay</c>, więc każdy zapis to osobny segment na pętli zwrotnej) nie rozjeżdża parsera klienta. Losowość z ziarna
/// (testy deterministyczne co do kolejności i wielkości kawałków; opóźnienia zależą od zegara systemu).
/// </summary>
internal sealed class ChunkedWriter
{
    private readonly WriteMode _mode;
    private readonly Random _rng;
    private readonly int _maxChunk;
    private readonly double _delayProbability;

    public ChunkedWriter(WriteMode mode, int seed, int maxChunk = 97, double delayProbability = 0.02)
    {
        _mode = mode;
        _rng = new Random(seed);
        _maxChunk = Math.Max(1, maxChunk);
        _delayProbability = delayProbability;
    }

    /// <summary>Liczba wywołań zapisu do gniazda (dla 1 B — liczba bajtów).</summary>
    public long Writes { get; private set; }

    /// <summary>Liczba wstawionych opóźnień.</summary>
    public long Delays { get; private set; }

    public async Task WriteAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        switch (_mode)
        {
            case WriteMode.Whole:
                await stream.WriteAsync(data, ct);
                Writes++;
                return;

            case WriteMode.OneByte:
                for (int i = 0; i < data.Length; i++)
                {
                    await stream.WriteAsync(data.AsMemory(i, 1), ct);
                    Writes++;
                    // Rzadkie opóźnienie także przy 1 B (co ~400 bajtów), żeby klient zastał strumień urwany w środku pola.
                    if (i < data.Length - 1 && _rng.Next(400) == 0)
                    {
                        Delays++;
                        await Task.Delay(1 + _rng.Next(3), ct);
                    }
                }

                return;

            default:
                int position = 0;
                while (position < data.Length)
                {
                    int n = Math.Min(data.Length - position, 1 + _rng.Next(_maxChunk));
                    await stream.WriteAsync(data.AsMemory(position, n), ct);
                    Writes++;
                    position += n;
                    if (position < data.Length && _rng.NextDouble() < _delayProbability)
                    {
                        Delays++;
                        await Task.Delay(1 + _rng.Next(3), ct);
                    }
                }

                return;
        }
    }
}
