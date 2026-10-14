using System.Text.RegularExpressions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Tests.History;

public class DowntimePresentationTests
{
    [Theory]
    [InlineData("tacka", "ramka")]
    [InlineData("tacki", "ramki")]
    [InlineData("tackę", "ramkę")]
    [InlineData("tacką", "ramką")]
    [InlineData("tacko", "ramko")]
    [InlineData("tackom", "ramkom")]
    [InlineData("tackami", "ramkami")]
    [InlineData("tackach", "ramkach")]
    [InlineData("tacce", "ramce")]
    [InlineData("tacek", "ramek")]
    [InlineData("Tacka", "Ramka")]
    [InlineData("Tacki", "Ramki")]
    [InlineData("Tackę", "Ramkę")]
    [InlineData("Tacką", "Ramką")]
    [InlineData("Tackami", "Ramkami")]
    [InlineData("Tacce", "Ramce")]
    [InlineData("Tacek", "Ramek")]
    public void ErrorName_EveryFormOfTray(string input, string expected) =>
        Assert.Equal(expected, DowntimePresentation.ErrorName(input));

    [Theory]
    [InlineData("Tacka nie pobrana", "Ramka niepobrana")]
    [InlineData("Tacka nie odłożona", "Ramka nieodłożona")]
    [InlineData("Tacka nie odlozona", "Ramka nieodlozona")]
    [InlineData("Tacki nie odłożone", "Ramki nieodłożone")]
    [InlineData("Nie pobrana tacka", "Niepobrana ramka")]
    [InlineData("Miejsce tacki zajęte", "Miejsce ramki zajęte")]
    [InlineData("Nie znaleziono tacki", "Nie znaleziono ramki")]
    [InlineData("Nr tacki = 0", "Nr ramki = 0")]
    [InlineData("Nr tacki poza magazynem", "Nr ramki poza magazynem")]
    [InlineData("Błąd: tacka (12) nie pobrana!", "Błąd: ramka (12) niepobrana!")]
    [InlineData("  Tacka nie pobrana  ", "Ramka niepobrana")]
    public void ErrorName_PhrasesFromDictionaryAndOcr(string input, string expected) =>
        Assert.Equal(expected, DowntimePresentation.ErrorName(input));

    [Theory]
    [InlineData("Stacka")]
    [InlineData("atacka")]
    [InlineData("Kontacki")]
    [InlineData("Tackarz")]
    [InlineData("tacka1")]
    [InlineData("tac")]
    [InlineData("Tac ka")]
    [InlineData("Servo 6")]
    [InlineData("Bazowanie (Home)")]
    [InlineData("Błąd nr 77")]
    [InlineData("Zacisk narzędzia przy obrotach wrzeciona — sprawdź zacisk")]
    [InlineData("Nienaruszona pobrana")]
    [InlineData("Wrzeciono nie pracuje")]
    public void ErrorName_LeavesOtherWordsUnchanged(string input) =>
        Assert.Equal(input, DowntimePresentation.ErrorName(input));

    [Theory]
    [InlineData("Nie pobrano tacki", "Nie pobrano ramki")]
    [InlineData("Tacki nie odłożono", "Ramki nie odłożono")]
    public void ErrorName_ImpersonalVerbFormStaysSeparate(string input, string expected) =>
        Assert.Equal(expected, DowntimePresentation.ErrorName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ErrorName_EmptyIsUnrecognized(string? input) =>
        Assert.Equal(DowntimePresentation.UnrecognizedName, DowntimePresentation.ErrorName(input));

    [Fact]
    public void ErrorName_VeryLongOcrText_DoesNotThrow()
    {
        string text = string.Concat(Enumerable.Repeat("tacka nie pobrana ", 20_000));
        string result = DowntimePresentation.ErrorName(text);
        // Zmapowany albo (po przekroczeniu czasu wyrażenia) oryginalny — byle bez wyjątku.
        Assert.True(result == text.Trim() || result.StartsWith("ramka niepobrana", StringComparison.Ordinal), result[..40]);
    }

    /// <summary>Każdy tekst słownika zawierający wyraz „tacka” (dowolna forma) po mapowaniu nie ma już „tack…/tacc…/tacek”.</summary>
    [Fact]
    public void ErrorName_EveryTrayTextOfTheExampleDictionaryIsMapped()
    {
        string root = RepositoryRoot();
        var rules = ConfigLoader.LoadRules(Path.Combine(root, "config", "rules.example.json"));
        string json = File.ReadAllText(Path.Combine(root, "config", "rules.example.json"));
        var texts = Regex.Matches(json, "\"shortText\"\\s*:\\s*\"([^\"]*)\"").Select(m => m.Groups[1].Value)
            .Where(t => Regex.IsMatch(t, @"\b[Tt]ac(k|ce|ek)", RegexOptions.CultureInvariant)).Distinct().ToArray();
        Assert.NotNull(rules);
        Assert.NotEmpty(texts);

        foreach (string text in texts)
        {
            string shown = DowntimePresentation.ErrorName(text.Replace("{n}", "3"));
            Assert.DoesNotMatch(@"\b[Tt]ac(k|ce|ek)", shown);
            Assert.DoesNotMatch(@"\b[Nn]ie (pobran|odłożon)", shown);
        }
    }

    [Fact]
    public void ChartErrorName_UnrecognizedCategoryWins_OtherwiseMappedReason()
    {
        DowntimeSlice Slice(string reason, string category) =>
            new(new DateOnly(2026, 9, 15), "06:00–14:00", "A", reason, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), false, false)
            { Category = category };

        Assert.Equal("Nierozpoznany", DowntimePresentation.ChartErrorName(Slice("Servo 6", "nierozpoznany")));
        Assert.Equal("Nierozpoznany", DowntimePresentation.ChartErrorName(Slice("Servo 6", "Nierozpoznany")));
        Assert.Equal("Ramka niepobrana", DowntimePresentation.ChartErrorName(Slice("Tacka nie pobrana", "tacka")));
        Assert.Equal("Servo 6", DowntimePresentation.ChartErrorName(Slice("Servo 6", "restart")));
        Assert.Equal("Nierozpoznany", DowntimePresentation.ChartErrorName(Slice("", "inny")));
        // Domyślny rodzaj kawałka bez klasyfikatora to „nierozpoznany” (DowntimeSlice.Category).
        Assert.Equal("Nierozpoznany", DowntimePresentation.ChartErrorName(
            new DowntimeSlice(new DateOnly(2026, 9, 15), "06:00–14:00", "A", "Servo 6", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false)));
        Assert.Throws<ArgumentNullException>(() => DowntimePresentation.ChartErrorName(null!));
    }

    [Theory]
    [InlineData("tacka", "Ramka")]
    [InlineData("TACKA", "Ramka")]
    [InlineData(" tacka ", "Ramka")]
    [InlineData("narzedzie", "Narzędzie")]
    [InlineData("restart", "Restart")]
    [InlineData("inny", "Inny")]
    [InlineData("nierozpoznany", "Nierozpoznany")]
    [InlineData("NIEROZPOZNANY", "Nierozpoznany")]
    [InlineData("nie błąd", "Nierozpoznany")]
    [InlineData("Bez alarmu", "Nierozpoznany")]
    [InlineData("", "Nierozpoznany")]
    [InlineData("   ", "Nierozpoznany")]
    [InlineData(null, "Nierozpoznany")]
    [InlineData("zasilanie", "Zasilanie")]
    [InlineData("łożysko", "Łożysko")]
    [InlineData("tacki", "Ramki")]
    public void CategoryName_Cases(string? category, string expected) =>
        Assert.Equal(expected, DowntimePresentation.CategoryName(category));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }
}

public class ChartPaletteTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static double Channel(uint argb, int shift)
    {
        double c = ((argb >> shift) & 0xFF) / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(uint argb) => 0.2126 * Channel(argb, 16) + 0.7152 * Channel(argb, 8) + 0.0722 * Channel(argb, 0);

    private static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData(false, 0xFFFFFFFFu)]
    [InlineData(true, 0xFF25282Au)]
    public void AdjacentPaletteColorsDifferInLightness(bool dark, uint background)
    {
        var colors = Enumerable.Range(0, ChartPalette.SlotCount).Select(i => ChartPalette.Slot(i).For(dark)).ToArray();
        var report = new List<string>();
        var failures = new List<string>();
        for (int i = 0; i + 1 < colors.Length; i++)
        {
            double ratio = Contrast(colors[i], colors[i + 1]);
            report.Add($"{i}/{i + 1}: {ratio:F2}");
            if (ratio < 1.5) failures.Add($"kolory {i} (#{colors[i] & 0xFFFFFF:X6}) i {i + 1} (#{colors[i + 1] & 0xFFFFFF:X6}): {ratio:F2}");
        }
        output.WriteLine($"{(dark ? "ciemny" : "jasny")}: sąsiednie {string.Join(", ", report)}; względem tła "
            + string.Join(", ", colors.Select(c => Contrast(c, background).ToString("F2"))));
        Assert.True(failures.Count == 0, $"{(dark ? "Ciemny" : "Jasny")} motyw — kontrast sąsiednich kolorów < 1,5: {string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemainderAndUnrecognizedDifferInLightness_AndFromTheSeriesPair(bool dark)
    {
        Assert.True(Contrast(ChartPalette.Remainder.For(dark), ChartPalette.Unrecognized.For(dark)) >= 1.5);
        Assert.Equal(ChartPalette.SlotCount, Enumerable.Range(0, ChartPalette.SlotCount).Select(i => ChartPalette.Slot(i).For(dark)).Distinct().Count());
    }

    [Theory]
    [InlineData(false, 0xFFFFFFFFu)]
    [InlineData(true, 0xFF25282Au)]
    public void IdleColorsAreMutedButReadable_WithErrorsColorAndBackground(bool dark, uint background)
    {
        uint errors = ChartPalette.Errors.For(dark), idle = ChartPalette.Idle.For(dark), zero = ChartPalette.IdleAtZero.For(dark);
        var report = $"{(dark ? "ciemny" : "jasny")}: tło {Contrast(errors, background):F2}/{Contrast(idle, background):F2}/{Contrast(zero, background):F2}; "
            + $"błędy–bez pracy {Contrast(errors, idle):F2}, błędy–0 % {Contrast(errors, zero):F2}, bez pracy–0 % {Contrast(idle, zero):F2}";
        output.WriteLine(report);
        Assert.True(Contrast(errors, background) >= 3, report);
        Assert.True(Contrast(idle, background) >= 3, report);
        Assert.True(Contrast(zero, background) >= 3, report);
        Assert.True(Contrast(errors, idle) >= 1.5, report);
        Assert.True(Contrast(errors, zero) >= 1.5, report);
        Assert.True(Contrast(idle, zero) >= 1.5, report);
        Assert.Equal(ChartPalette.Slot(0), ChartPalette.Errors);
        // Szarości: różnica kanałów RGB najwyżej 8 — celowo bez barwy, żeby nie konkurować z nazwami błędów.
        foreach (uint grey in new[] { idle, zero })
        {
            int r = (int)(grey >> 16) & 0xFF, g = (int)(grey >> 8) & 0xFF, b = (int)grey & 0xFF;
            Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 8, $"#{grey & 0xFFFFFF:X6} nie jest szarością");
        }
        // Linia dzienna: „Wszystkie postoje” (2), „Bez długich postojów” (1), „Błędy maszyn” (0).
        uint s2 = ChartPalette.Slot(2).For(dark), s1 = ChartPalette.Slot(1).For(dark);
        Assert.True(Contrast(s2, s1) >= 1.5, $"2/1: {Contrast(s2, s1):F2}");
        Assert.True(Contrast(s1, errors) >= 1.5, $"1/0: {Contrast(s1, errors):F2}");
        Assert.True(Contrast(s2, errors) >= 1.5, $"2/0: {Contrast(s2, errors):F2}");
    }

    [Fact]
    public void SlotOutsideRangeThrows_HexIsLightColor_ForPicksTheme()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartPalette.Slot(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartPalette.Slot(ChartPalette.SlotCount));
        var color = new ChartColor(0xFF2A78D6, 0xFF3987E5);
        Assert.Equal("#2A78D6", color.LightHex);
        Assert.Equal(0xFF3987E5u, color.For(true));
        Assert.Equal(0xFF2A78D6u, color.For(false));
        Assert.All(Enumerable.Range(0, ChartPalette.SlotCount), i => Assert.Equal(0xFFu, ChartPalette.Slot(i).LightArgb >> 24));
        Assert.Equal("#000000", new ChartColor(0, 0).LightHex);
    }
}
