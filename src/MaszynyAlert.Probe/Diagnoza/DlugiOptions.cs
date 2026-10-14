namespace MaszynyAlert.Probe.Diagnoza;

public sealed record DlugiOptions(
    int? TotalSeconds,
    int OkresowaSek,
    int OdpytywanieCoSek,
    int OdpytywanieTrwaSek,
    int MinOdstepZmianySek,
    int MinOdczytSek,
    int PodsumowanieCoSek,
    long LimitBytes,
    bool NoOcr)
{
    public const int DefaultOkresowaSek = 600;
    public const int DefaultOdpytywanieCoSek = 300;
    public const int DefaultOdpytywanieTrwaSek = 15;
    public const int DefaultMinOdstepZmianySek = 5;
    public const int DefaultMinOdczytSek = 2;
    public const int DefaultPodsumowanieCoSek = 600;
    public const long DefaultLimitMb = 2048;

    /// <summary>Klatki epizodu czerwieni/żółci: w tych sekundach od początku epizodu (0, 2, 10, 60).</summary>
    public static readonly IReadOnlyList<int> EpisodeFrameOffsetsSek = [0, 2, 10, 60];

    /// <summary>Klatki okresowe starsze niż tyle dni są przerzedzane do jednej na godzinę.</summary>
    public const int ThinAfterDays = 3;

    public static DlugiOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int? seconds = ParseOptional(args, "--seconds");
        int? interval = ParseOptional(args, "--interval-s");
        long limitMb = ParseOptional(args, "--limit-mb") ?? DefaultLimitMb;
        bool noOcr = ArgsUtil.HasFlag(args, "--no-ocr");

        if (interval is { } n)
        {
            return new DlugiOptions(seconds, n * 5, n * 3, n, Math.Min(DefaultMinOdstepZmianySek, n), Math.Min(DefaultMinOdczytSek, n), n * 4, limitMb * 1024 * 1024, noOcr);
        }

        return new DlugiOptions(seconds, DefaultOkresowaSek, DefaultOdpytywanieCoSek, DefaultOdpytywanieTrwaSek, DefaultMinOdstepZmianySek,
            DefaultMinOdczytSek, DefaultPodsumowanieCoSek, limitMb * 1024 * 1024, noOcr);
    }

    private static int? ParseOptional(IReadOnlyList<string> args, string option)
    {
        string? text = ArgsUtil.FindOption(args, option);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, out int value) || value <= 0)
        {
            throw new ProbeUsageException($"Opcja {option} musi być dodatnią liczbą całkowitą.");
        }

        return value;
    }
}
