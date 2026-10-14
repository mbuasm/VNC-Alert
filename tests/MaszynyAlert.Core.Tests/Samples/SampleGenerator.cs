using System.Text.Json;
using System.Text.Json.Serialization;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Samples;

/// <summary>Oczekiwany wynik detekcji dla jednej próbki.</summary>
public sealed record SampleExpectation(bool IsRed);

/// <summary>Zawartość pliku <c>expected.json</c> dla jednego profilu ekranu.</summary>
public sealed record SampleExpectations(string Profile, Dictionary<string, SampleExpectation> Files);

/// <summary>
/// Generator SYNTETYCZNYCH próbek paska statusu (tests/samples/&lt;profil&gt;/...). Próbki służą jako
/// „zamrożony” zestaw regresyjny dla detektora czerwieni.
/// Rozmiar każdej próbki = <c>statusBarRoi</c> profilu, dzięki czemu obraz jest już wycięty do ROI.
/// </summary>
public static class SampleGenerator
{
    /// <summary>Identyfikatory profili, dla których generujemy próbki (klucze z config.example.json).</summary>
    public static readonly string[] ProfileIds = ["opencnc", "amm580", "aidite-light2"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Opis pojedynczej próbki: ścieżka względna, oczekiwany wynik i sposób narysowania.</summary>
    public sealed record SampleSpec(string RelativePath, bool ExpectedRed, Func<Palette, RgbaImage> Render);

    /// <summary>Zestaw kolorów tła używany przez jeden profil.</summary>
    public sealed record Palette(int Width, int Height, Rgba Background, Rgba AlternateBackground);

    /// <summary>Zwraca paletę (rozmiar ROI + kolory tła) dla podanego profilu.</summary>
    public static Palette PaletteFor(string profileId)
    {
        ScreenProfile profile = TestPaths.Profile(profileId);
        RectInt roi = profile.StatusBarRoi;
        return profileId switch
        {
            "opencnc" => new Palette(roi.Width, roi.Height, SyntheticScreens.LightBackground, new Rgba(228, 230, 233)),
            "amm580" => new Palette(roi.Width, roi.Height, SyntheticScreens.DarkBackground, new Rgba(24, 26, 36)),
            "aidite-light2" => new Palette(roi.Width, roi.Height, SyntheticScreens.Light2Background, new Rgba(240, 242, 246)),
            _ => throw new ArgumentException($"Nieznany profil próbek: '{profileId}'.", nameof(profileId)),
        };
    }

    /// <summary>Lista próbek dla profilu: 2× normal (biały tekst, pusty pasek), 2× warning (żółty, bursztynowy), 3× error (czerwienie).</summary>
    public static IReadOnlyList<SampleSpec> SpecsFor(string profileId)
    {
        string s = profileId;
        return
        [
            new SampleSpec("normal/bialy-tekst.png", false, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.Background, SyntheticScreens.WhiteText, s + "-normal-white")),
            new SampleSpec("normal/pusty-pasek.png", false, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.Background, SyntheticScreens.WhiteText, seed: null)),
            new SampleSpec("warning/zolty-tekst.png", false, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.Background, SyntheticScreens.YellowText, s + "-warn-yellow")),
            new SampleSpec("warning/bursztynowy-tekst.png", false, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.Background, SyntheticScreens.AmberText, s + "-warn-amber")),
            new SampleSpec("error/czerwony-tekst.png", true, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.Background, SyntheticScreens.RedText, s + "-err-red")),
            new SampleSpec("error/ciemnoczerwony-tekst.png", true, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.Background, SyntheticScreens.DarkRedText, s + "-err-darkred")),
            new SampleSpec("error/czerwony-tekst-inne-tlo.png", true, p =>
                SyntheticScreens.StatusBar(p.Width, p.Height, p.AlternateBackground, SyntheticScreens.RedText, s + "-err-red-alt")),
        ];
    }

    /// <summary>
    /// Zapisuje komplet próbek profilu (PNG + expected.json) do katalogu <paramref name="rootDirectory"/>
    /// (czyli do <c>&lt;root&gt;/&lt;profil&gt;/...</c>). Nadpisuje istniejące pliki.
    /// </summary>
    public static void Write(string profileId, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);

        Palette palette = PaletteFor(profileId);
        string profileDir = Path.Combine(rootDirectory, profileId);
        var expectations = new Dictionary<string, SampleExpectation>(StringComparer.Ordinal);

        foreach (SampleSpec spec in SpecsFor(profileId))
        {
            string target = Path.Combine(profileDir, spec.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            PngCodec.EncodeToFile(spec.Render(palette), target);
            expectations[spec.RelativePath] = new SampleExpectation(spec.ExpectedRed);
        }

        Directory.CreateDirectory(profileDir);
        File.WriteAllText(
            Path.Combine(profileDir, "expected.json"),
            JsonSerializer.Serialize(new SampleExpectations(profileId, expectations), JsonOptions) + Environment.NewLine);
    }

    /// <summary>Prawda, gdy w katalogu profilu nie ma jeszcze żadnej próbki PNG.</summary>
    public static bool IsEmpty(string profileId, string rootDirectory)
    {
        string profileDir = Path.Combine(rootDirectory, profileId);
        return !Directory.Exists(profileDir)
            || !Directory.EnumerateFiles(profileDir, "*.png", SearchOption.AllDirectories).Any();
    }

    /// <summary>Wczytuje <c>expected.json</c> profilu z podanego katalogu głównego próbek.</summary>
    public static SampleExpectations ReadExpectations(string profileId, string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, profileId, "expected.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SampleExpectations>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Plik '{path}' jest pusty lub nieczytelny.");
    }
}
