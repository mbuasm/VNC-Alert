namespace MaszynyAlert.Probe.Diagnoza;

public sealed record DiagnozaOptions(int TotalSeconds, int FrameIntervalSeconds, bool NoZip, bool NoOcr)
{
    public const int DefaultMinutes = 35;
    public const int DefaultFrameIntervalSeconds = 20;

    /// <summary>Rozbiera argumenty komendy (po zdjęciu opcji globalnych). Błędne wartości = <see cref="ProbeUsageException"/> po polsku.</summary>
    public static DiagnozaOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int minutes = ParsePositive(args, "--minuty", DefaultMinutes, "minuty");
        int interval = ParsePositive(args, "--co-sekund", DefaultFrameIntervalSeconds, "sekundy");
        int? seconds = ArgsUtil.FindOption(args, "--seconds") is null ? null : ParsePositive(args, "--seconds", 0, "sekundy");
        int total = seconds ?? minutes * 60;

        if (interval > total)
        {
            throw new ProbeUsageException($"Opcja --co-sekund ({interval} s) nie może być większa niż cały przebieg ({total} s).");
        }

        return new DiagnozaOptions(total, interval, ArgsUtil.HasFlag(args, "--no-zip"), ArgsUtil.HasFlag(args, "--no-ocr"));
    }

    private static int ParsePositive(IReadOnlyList<string> args, string option, int defaultValue, string unit)
    {
        string? text = ArgsUtil.FindOption(args, option);
        if (text is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, out int value) || value <= 0)
        {
            throw new ProbeUsageException($"Opcja {option} musi być dodatnią liczbą całkowitą ({unit}).");
        }

        return value;
    }
}

/// <summary>
/// Harmonogram jednego przebiegu <c>diagnoza</c> — w sekundach od chwili pierwszego udanego połączenia maszyny.
/// Dla pełnego przebiegu (co najmniej <see cref="FullScheduleSeconds"/>) to wartości: klatki testu
/// „ekran gaśnie po połączeniu” w 0, 5, 30, 60 i 120 s, rozłączenie na 30 s po 3 min, <c>shared-test</c> raz
/// po 4 min, pomiar częstości aktualizacji przez 60 s po 5 min, OCR co ok. 2 min, ponowna próba maszyny, która
/// nie odpowiada, co 60 s. Krótszy przebieg (testy, <c>--seconds 20</c>) ma te same punkty ściśnięte
/// proporcjonalnie — żeby test na fałszywym serwerze przechodził przez WSZYSTKIE fazy, nie tylko pierwszą.
/// </summary>
public sealed record DiagnozaSchedule(
    int TotalSeconds,
    int FrameIntervalSeconds,
    IReadOnlyList<int> BlackTestOffsetsSeconds,
    int DisconnectAtSeconds,
    int DisconnectForSeconds,
    int SharedTestAtSeconds,
    int RateMeasureAtSeconds,
    int RateMeasureForSeconds,
    int OcrEverySeconds,
    int RetryUnreachableEverySeconds,
    IReadOnlyList<int> ReconnectBackoffSeconds)
{
    /// <summary>Przebieg nie krótszy niż 10 min dostaje harmonogram bez ściskania.</summary>
    public const int FullScheduleSeconds = 600;

    public static DiagnozaSchedule For(int totalSeconds, int frameIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frameIntervalSeconds, 0);

        double f = Math.Min(1.0, totalSeconds / (double)FullScheduleSeconds);
        int Scale(int baseSeconds, int minimum) => Math.Max(minimum, (int)Math.Round(baseSeconds * f));

        return new DiagnozaSchedule(
            TotalSeconds: totalSeconds,
            FrameIntervalSeconds: frameIntervalSeconds,
            BlackTestOffsetsSeconds: [0, Scale(5, 1), Scale(30, 2), Scale(60, 3), Scale(120, 4)],
            DisconnectAtSeconds: Scale(180, 5),
            DisconnectForSeconds: Scale(30, 2),
            SharedTestAtSeconds: Scale(240, 7),
            RateMeasureAtSeconds: Scale(300, 8),
            RateMeasureForSeconds: Scale(60, 2),
            OcrEverySeconds: Scale(120, 3),
            RetryUnreachableEverySeconds: Scale(60, 2),
            ReconnectBackoffSeconds: f >= 1.0 ? [2, 5, 15, 30] : [1, 1, 2, 2]);
    }
}
