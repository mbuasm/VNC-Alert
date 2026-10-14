using System.Globalization;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Vision;

/// <summary>Udział jednego nazwanego kubełka koloru w obrazie — patrz <see cref="ColorSummary.Summarize"/>.</summary>
/// <param name="Name">Polska nazwa kubełka koloru (np. „czerwony”, „biały”) — patrz opis klasy <see cref="ColorSummary"/>.</param>
/// <param name="Fraction">Udział pikseli tego kubełka w całym obrazie, w zakresie 0..1 (piksele przezroczyste nie są liczone — patrz <see cref="ColorSummary.Summarize"/>).</param>
public sealed record ColorShare(string Name, double Fraction);

/// <summary>
/// Klasyfikuje piksele zrzutu ekranu do nazwanych kubełków koloru (po HSV) i podsumowuje ich udział —
/// narzędzie diagnostyczne <c>probe.exe</c> ma tym sposobem NOTOWAĆ dominujące kolory okienka
/// alertu HMI bez konieczności ręcznego opisywania progów przez użytkownika: wystarczy zrzut ekranu
/// z chwili błędu, a to podsumowanie odpowiada na pytanie „jakim kolorem maszyna sygnalizuje błąd”
/// (np. czerwony tekst na niebieskim tle okienka, żółty napis ostrzeżenia, biały tekst na czerwonym tle).
/// </summary>
/// <remarks>
/// Kubełki (rozłączne, sprawdzane w podanej kolejności — pierwszy spełniony warunek wygrywa):
/// <list type="bullet">
/// <item>„biały” — V &gt;= 0.85 i S &lt;= 0.15 (jasne, praktycznie bez barwy).</item>
/// <item>„czarny” — V &lt;= 0.2 (bardzo ciemne, niezależnie od barwy/nasycenia).</item>
/// <item>„szary” — S &lt;= 0.15 (pozostałe niejasne/nieczarne piksele bez barwy — półtony).</item>
/// <item>„czerwony” — H &gt;= 340 lub H &lt;= 20 (zakres "przez zero", jak <see cref="ColorMath.IsRed"/>), S &gt;= 0.45, V &gt;= 0.35.</item>
/// <item>„pomarańczowy” — H w [20, 45), te same progi S/V co czerwony.</item>
/// <item>„żółty” — H w [45, 70).</item>
/// <item>„zielony” — H w [70, 170).</item>
/// <item>„niebieski” — H w [170, 260).</item>
/// <item>„fioletowy” — H w [260, 340).</item>
/// <item>„ciemny/inny” — reszta (typowo: nasycone, ale za ciemne na pewny kubełek koloru — S &gt; 0.15, V &lt; 0.35, poza zakresem "czarny").</item>
/// </list>
/// Piksele przezroczyste (A &lt; 128, jak w <see cref="ColorMath"/>) są całkowicie ignorowane — nie liczą
/// się ani do żadnego kubełka, ani do mianownika udziału.
/// </remarks>
public static class ColorSummary
{
    private const double WhiteMinValue = 0.85;
    private const double WhiteMaxSaturation = 0.15;
    private const double BlackMaxValue = 0.2;
    private const double GrayMaxSaturation = 0.15;
    private const double ColorfulMinSaturation = 0.45;
    private const double ColorfulMinValue = 0.35;

    /// <summary>
    /// Klasyfikuje piskele obrazu <paramref name="img"/> do nazwanych kubełków koloru (patrz opis klasy)
    /// i zwraca <paramref name="top"/> kubełków o największym udziale, malejąco, ograniczone do udziałów
    /// &gt; 0. Pusty obraz (0x0 albo wszystkie piksele przezroczyste) → pusta lista.
    /// </summary>
    public static IReadOnlyList<ColorShare> Summarize(RgbaImage img, int top = 3)
    {
        ArgumentNullException.ThrowIfNull(img);

        var counts = new Dictionary<string, long> ();
        long total = 0;

        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                Rgba c = img.GetPixel(x, y);
                if (c.A < 128)
                {
                    continue;
                }

                string bucket = Classify(c);
                counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
                total++;
            }
        }

        if (total == 0)
        {
            return Array.Empty<ColorShare>();
        }

        return counts
            .Select(kv => new ColorShare(kv.Key, (double)kv.Value / total))
            .Where(s => s.Fraction > 0)
            .OrderByDescending(s => s.Fraction)
            .Take(Math.Max(0, top))
            .ToList();
    }

    /// <summary>
    /// Formatuje udziały kolorów po polsku, np. „niebieski 62 %, czerwony 9 %, biały 4 %” — procenty
    /// całkowite (zaokrąglone), a udział poniżej 1 % (ale &gt; 0) pisany jako „&lt;1 %”, żeby nie pokazywać
    /// zmylającego „0 %” dla koloru, który realnie na obrazie jest (choćby w kilku pikselach).
    /// Pusta lista → „(brak)”.
    /// </summary>
    public static string Describe(IReadOnlyList<ColorShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);

        if (shares.Count == 0)
        {
            return "(brak)";
        }

        return string.Join(", ", shares.Select(FormatShare));
    }

    private static string FormatShare(ColorShare share)
    {
        int percent = (int)Math.Round(share.Fraction * 100, MidpointRounding.AwayFromZero);
        string percentText = percent == 0 && share.Fraction > 0
            ? "<1 %"
            : percent.ToString(CultureInfo.InvariantCulture) + " %";
        return $"{share.Name} {percentText}";
    }

    /// <summary>
    /// Nazwa najliczniejszego kubełka SPOZA tła obrazu (tło = kubełek o największym udziale) — odpowiedź
    /// na pytanie „jakim kolorem jest napisany tekst błędu na okienku alertu” (tło okienka to jeden
    /// dominujący kolor, np. niebieski, a tekst błędu to drugi w kolejności kubełek). Zwraca
    /// <see langword="null"/>, gdy nie ma drugiego kubełka z udziałem &gt;= 0.5 % (obraz jednokolorowy,
    /// albo cały "tekst" to szum OCR-owalny na poziomie kompresji/antyaliasingu).
    /// </summary>
    public static string? DominantTextColor(RgbaImage img)
    {
        ArgumentNullException.ThrowIfNull(img);

        IReadOnlyList<ColorShare> shares = Summarize(img, top: int.MaxValue);
        if (shares.Count < 2)
        {
            return null;
        }

        // Summarize() już sortuje malejąco po udziale — shares[0] to tło, shares[1] to następny w kolejności.
        ColorShare second = shares[1];
        return second.Fraction >= 0.005 ? second.Name : null;
    }

    private static string Classify(Rgba c)
    {
        (double h, double s, double v) = ColorMath.RgbToHsv(c.R, c.G, c.B);

        if (v >= WhiteMinValue && s <= WhiteMaxSaturation)
        {
            return "biały";
        }

        if (v <= BlackMaxValue)
        {
            return "czarny";
        }

        if (s <= GrayMaxSaturation)
        {
            return "szary";
        }

        if (s >= ColorfulMinSaturation && v >= ColorfulMinValue)
        {
            if (h >= 340.0 || h <= 20.0)
            {
                return "czerwony";
            }

            if (h < 45.0)
            {
                return "pomarańczowy";
            }

            if (h < 70.0)
            {
                return "żółty";
            }

            if (h < 170.0)
            {
                return "zielony";
            }

            if (h < 260.0)
            {
                return "niebieski";
            }

            return "fioletowy";
        }

        return "ciemny/inny";
    }
}
