using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

public sealed class PdfExportTests
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    // Separator tysięcy pl-PL to twarda spacja (U+00A0); w PDF to zwykły odstęp między słowami.
    private static string N1(double minutes) => minutes.ToString("N1", Pl).Replace(' ', ' ').Replace(' ', ' ');

    private static string Hours(double minutes) => $"{(int)(minutes / 60)} godz. {(int)(minutes % 60)} min";

    [Fact]
    public void Pdf_A4Poziomo_TytulWlasciwosciDokumentu()
    {
        using var pdf = PdfProbe.Write(WeekData());

        Assert.Equal(4, pdf.PageCount);
        foreach (var page in pdf.Sharp.Pages)
        {
            Assert.InRange(page.Width.Point, 840, 843);
            Assert.InRange(page.Height.Point, 594, 597);
        }
        Assert.Equal("Raport postojów maszyn", pdf.Sharp.Info.Title);
        Assert.Equal("VNC Alert", pdf.Sharp.Info.Author);
    }

    [Fact]
    public void Strona1_PodTytulemLiniaRaportStworzonyPrzezVncAlert()
    {
        using var pdf = PdfProbe.Write(WeekData());
        var lines = pdf.Lines(1);

        // Tytuł = linia o największym stopniu pisma na stronie 1; nagłówek strony też zaczyna się od tytułu, ale małym pismem.
        var title = lines.MaxBy(l => l.Size)!;
        Assert.Equal("Raport postojów maszyn", title.Text);
        int index = lines.ToList().IndexOf(title);
        var next = lines[index + 1];
        Assert.Equal("Raport stworzony przez VNC Alert", next.Text);
        Assert.True(next.Y < title.Y, "linia ma być POD tytułem");
        Assert.True(title.Y - next.Y < 40, $"linia ma być tuż pod tytułem (odstęp {title.Y - next.Y:0} pt)");
        Assert.DoesNotContain("VCN", pdf.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Strona1_ZakresWygenerowanoKomputerZakresDanych()
    {
        var data = WeekData();
        using var pdf = PdfProbe.Write(data);
        string text = pdf.Text(1);

        Assert.Contains("Zakres (dni zmian) | 28.09.2026 – 06.10.2026", text, StringComparison.Ordinal);
        Assert.Contains("Zmiana | Wszystkie zmiany", text, StringComparison.Ordinal);
        Assert.Contains("Dane | Dane lokalne tego komputera", text, StringComparison.Ordinal);
        Assert.Contains($"Wygenerowano | {data.GeneratedAt.ToLocalTime():dd.MM.yyyy HH:mm}", text, StringComparison.Ordinal);
        Assert.Contains("Komputer | TEST-PC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Strona1_ZakresDanychGrupyZNazwa()
    {
        using var pdf = PdfProbe.Write(WeekData(scope: DowntimeExportData.ScopeTextFor(true, "Hala A")));
        Assert.Contains("Dane | Dane grupy komputerów „Hala A”", pdf.Text(1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-09-30", "2026-09-30")] // jeden dzień (Od = Do)
    [InlineData("2026-10-02", "2026-10-04")] // pt–nd: jeden dzień roboczy
    [InlineData("2026-10-05", "2026-09-30")] // Od > Do → Do := Od
    public void ZakresZJednymDniemRoboczym_PdfPowstaje(string from, string to)
    {
        var data = Data(Week(), DateOnly.Parse(from), DateOnly.Parse(to));
        Assert.Single(data.Charts.Days.Days);
        Assert.Single(data.Charts.Days.Series[0].Points);
        using var stream = new MemoryStream();
        var outcome = DowntimePdfExporter.Write(data, stream);
        Assert.True(outcome.Success, outcome.Error);
    }

    [Fact]
    public void CalaHistoria_WszystkiePostojeZDzis_PdfPowstaje()
    {
        var data = Data([Ep("a1", "Servo 6", "2026-10-06T08:00:00Z", 10, "restart")], null, null);
        Assert.Equal(Tue, data.From);
        Assert.Equal(Tue, data.To);
        using var stream = new MemoryStream();
        var outcome = DowntimePdfExporter.Write(data, stream);
        Assert.True(outcome.Success, outcome.Error);
    }

    [Fact]
    public void Zakres_JedenDzien_BezMyslnika()
    {
        var day = new DateOnly(2026, 9, 30);
        using var pdf = PdfProbe.Write(DowntimeExportData.Create([], day, day, null, [], [], Utc, Now, "TEST-PC", Local));
        Assert.Contains("Zakres (dni zmian) | 30.09.2026", pdf.Text(1), StringComparison.Ordinal);
        Assert.DoesNotContain("30.09.2026 – 30.09.2026", pdf.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Strona1_SumyZDlugimiIBezLiczbaPostojowIDlugich()
    {
        var data = WeekData();
        using var pdf = PdfProbe.Write(data);
        string text = pdf.Text(1);

        Assert.Equal(195, data.Totals.All.Minutes, 6);
        Assert.Equal(120, data.Totals.WithoutLong.Minutes, 6);
        Assert.Contains("3 godz. 15 min | 2 godz. 0 min", text, StringComparison.Ordinal);
        Assert.Contains("195,0 min | 120,0 min", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?m)^7$"), text);
        Assert.Contains("W tym długich postojów (> 1 h): 1", text, StringComparison.Ordinal);
        Assert.Contains("Aidite 2 | 80,0 | 5,0 | 80,0 | 2 | 1 | 2", text, StringComparison.Ordinal);
        Assert.Contains("W tym błędy maszyn: 195,0 min", text, StringComparison.Ordinal); // dalszy ciąg („· bez pracy: 0,0 min”) zawinięty w karcie
        Assert.Contains("Według rodzaju postoju: Błędy maszyn 100,0 % (195,0 min, postojów: 7)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    public void SumyWPdf_ZgadzajaSieZTotalsIZTabelaPanelu_LosoweZestawy(int seed)
    {
        var episodes = Random(seed, 60);
        var slices = PanelSlices(episodes, Mon, Tue);
        var totals = DowntimeReport.Totals(slices);
        var data = Data(episodes, Mon, Tue);
        using var pdf = PdfProbe.Write(data);
        string page1 = pdf.Text(1);
        string all = pdf.AllText();

        Assert.Equal(totals, data.Totals);
        Assert.Contains($"{Hours(totals.All.Minutes)} | {Hours(totals.WithoutLong.Minutes)}", page1, StringComparison.Ordinal);
        Assert.Contains($"{N1(totals.All.Minutes)} min | {N1(totals.WithoutLong.Minutes)} min", page1, StringComparison.Ordinal);
        Assert.Contains($"W tym długich postojów (> 1 h): {totals.All.Episodes - totals.WithoutLong.Episodes}", page1, StringComparison.Ordinal);
        // Wiersz „Razem” pod tabelą epizodów.
        Assert.Contains($"Razem (z długimi postojami) | {N1(totals.All.Minutes)} | Bez długich postojów: {N1(totals.WithoutLong.Minutes)} min",
            all, StringComparison.Ordinal);
        foreach (var day in slices.GroupBy(s => s.Day))
            Assert.Equal(day.Sum(s => s.Minutes), data.Days.Single(d => d.Day == day.Key).Totals.All.Minutes, 6);
    }

    [Fact]
    public void PostojNaDwochZmianach_JedenPostoj_DwaWiersze()
    {
        // 13:30–14:30: jeden postój 60 min, dwie zmiany.
        var data = Data([Ep("a1", "Servo 6", "2026-09-30T13:30:00Z", 60, "restart")], Mon, Tue);
        using var pdf = PdfProbe.Write(data);

        Assert.Equal(2, data.Episodes.Count);
        Assert.Single(data.Episodes.Select(e => e.EpisodeId).Distinct());
        Assert.Equal(1, data.Totals.All.Episodes);
        Assert.Matches(new Regex(@"(?m)^1$"), pdf.Text(1));
        string table = pdf.Text(4);
        Assert.Contains("06:00–14:00 | Aidite 1 | 30.09.2026 13:30 | 30.09.2026 14:00 | 30,0", table, StringComparison.Ordinal);
        Assert.Contains("14:00–22:00 | Aidite 1 | 30.09.2026 14:00 | 30.09.2026 14:30 | 30,0", table, StringComparison.Ordinal);
    }

    [Fact]
    public void PolskieZnaki_CzcionkaOsadzona_TekstBezZnakowZapytania()
    {
        var episodes = new[]
        {
            Ep("a1", "Łożysko źle ściśnięte – „ćwiczenie” żółć gęś ń", "2026-09-29T08:00:00Z", 10, "inny",
                name: "Zażółć gęślą jaźń"),
        };
        using var pdf = PdfProbe.Write(Data(episodes, Mon, Tue));
        string all = pdf.AllText();

        Assert.True(pdf.HasEmbeddedTrueType(), "czcionka TrueType ma być osadzona (/FontFile2)");
        foreach (string letter in new[] { "ą", "ę", "ł", "ó", "ś", "ż", "ź", "ć", "ń", "Ł", "Z", "„", "”", "–" })
            Assert.Contains(letter, all, StringComparison.Ordinal);
        Assert.Contains("Zażółć gęślą jaźń", all, StringComparison.Ordinal);
        Assert.Contains("Łożysko źle ściśnięte – „ćwiczenie” żółć gęś ń", all, StringComparison.Ordinal);
        Assert.DoesNotContain("?", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Paginacja_300Wierszy_NaglowekTabeliIStopkaStronaXzY()
    {
        var start = At("2026-09-28T06:10:00Z");
        var episodes = Enumerable.Range(0, 320).Select(i =>
        {
            // pn–pt, 4 maszyny, różne godziny; każdy epizod 5 minut, bez nakładania na tej samej maszynie.
            var day = start.AddDays(new[] { 0, 1, 2, 3, 4, 7, 8 }[i % 7]);
            return Ep("a" + (i % 3 + 1), "Servo " + i % 11, day.AddMinutes(i / 7 * 9).ToString("o"), 5, "restart");
        }).ToArray();
        var data = Data(episodes, Mon, Tue);
        Assert.Equal(320, data.Episodes.Count);
        using var pdf = PdfProbe.Write(data);

        Assert.True(pdf.PageCount >= 6, $"stron: {pdf.PageCount}");
        int y = pdf.PageCount;
        for (int page = 1; page <= y; page++)
        {
            string text = pdf.Text(page);
            Assert.Contains($"Strona {page} z {y}", text, StringComparison.Ordinal);
            Assert.Contains("Raport stworzony przez VNC Alert · wygenerowano", text, StringComparison.Ordinal);
            Assert.StartsWith("Raport postojów maszyn · 28.09.2026 – 06.10.2026 · Wszystkie zmiany", text, StringComparison.Ordinal);
            if (page >= 4)
                Assert.Contains("Nazwa błędu | Grupa błędu | Stan", text, StringComparison.Ordinal);
        }
        // Każdy wiersz tabeli jest w PDF (data dnia zmiany na początku linii).
        int rows = Enumerable.Range(4, y - 3).Sum(p => pdf.Lines(p).Count(l => Regex.IsMatch(l.Text, @"^\d\d\.\d\d\.2026 \| ")));
        Assert.Equal(320, rows);
    }

    [Fact]
    public void PustyRaport_PdfZNotamiZamiastWykresowITabeli()
    {
        var data = DowntimeExportData.Create([], Mon, Tue, null, [], [], Utc, Now, "TEST-PC", Local);
        using var pdf = PdfProbe.Write(data);

        Assert.Equal(3, pdf.PageCount);
        Assert.Contains("W wybranym okresie nie ma postojów — wykresy nie są rysowane.", pdf.Text(2), StringComparison.Ordinal);
        Assert.Contains("W wybranym okresie i zmianie nie ma postojów.", pdf.Text(3), StringComparison.Ordinal);
        Assert.Contains("0 godz. 0 min | 0 godz. 0 min", pdf.Text(1), StringComparison.Ordinal);
        // Bez dziennika obserwacji „brak danych” jest nieznany, nie zerowy.
        Assert.Contains("Program nie ma jeszcze zapisu czasu obserwacji", pdf.Text(1), StringComparison.Ordinal);
    }

    [Fact]
    public void Tacka_JakoRamka_WTabeliIWykresie_BezSlowaTacka()
    {
        using var pdf = PdfProbe.Write(WeekData());
        string all = pdf.AllText();

        Assert.Contains("Ramka niepobrana | Ramka", pdf.Text(4), StringComparison.Ordinal);
        Assert.Contains("Ramka nieodłożona | Ramka", pdf.Text(4), StringComparison.Ordinal);
        Assert.Contains("Ramka niepobrana", pdf.Text(3), StringComparison.Ordinal); // legenda wykresu kołowego błędów
        Assert.DoesNotMatch(new Regex("tack", RegexOptions.IgnoreCase), all);
    }

    [Fact]
    public void BrakDanych_ZDziennikiem_KartaIKolumnyWMinutach()
    {
        ObservationInterval[] observations =
        [
            new("a1", ObservationState.Observed, At("2026-09-28T06:00:00Z"), At("2026-10-06T12:00:00Z")),
            new("a2", ObservationState.NoConnection, At("2026-09-29T08:00:00Z"), At("2026-09-29T10:00:00Z")),
            new("a2", ObservationState.ScreenHidden, At("2026-09-30T08:00:00Z"), At("2026-09-30T08:30:00Z")),
        ];
        var data = WeekData(observations);
        using var pdf = PdfProbe.Write(data);
        string text = pdf.Text(1);

        Assert.Equal(TimeSpan.FromHours(2), data.NoConnection);
        Assert.Contains("Brak danych - brak połączenia z", text, StringComparison.Ordinal);
        Assert.Contains("2 godz. 0 min", text, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(30), data.ScreenHidden);
        Assert.Contains("Brak danych - ekran HMI niewidoczny: 0 godz.", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?m)^(.*\| )?30 min$"), text);
        Assert.Contains("Aidite 2 | 80,0 | 5,0 | 80,0 | 2 | 1 | 2 | 120,0 | 30,0", text, StringComparison.Ordinal);
        Assert.Null(data.ObservationKnownFrom);
        Assert.DoesNotContain("jest znany od", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DziennikZaczetyWTrakcieZakresu_NotaZnanyOd()
    {
        ObservationInterval[] observations = [new("a1", ObservationState.Observed, At("2026-10-01T09:15:00Z"), At("2026-10-06T12:00:00Z"))];
        var data = WeekData(observations);
        using var pdf = PdfProbe.Write(data);

        Assert.Equal(At("2026-10-01T09:15:00Z"), data.ObservationKnownFrom);
        Assert.Contains($"„Brak danych” jest znany od {data.ObservationKnownFrom!.Value.ToLocalTime():dd.MM.yyyy HH:mm}",
            pdf.Text(1), StringComparison.Ordinal);
    }

    [Fact]
    public void StanTrwajacyIPrzerwany_JakWPanelu_DlugiPogrubiony()
    {
        using var pdf = PdfProbe.Write(WeekData());
        var lines = pdf.Lines(4);
        string text = pdf.Text(4);

        Assert.Contains("| Narzędzie | Przerwana obserwacja", text, StringComparison.Ordinal);
        Assert.Contains("| Servo 6 | Restart | Otwarty epizod", text, StringComparison.Ordinal);
        // Długi postój: kolumna „Stan” pogrubiona (czcionka Bold), zwykły wiersz — nie.
        var longRow = lines.Single(l => l.Text.Contains("Zakończony, długi postój", StringComparison.Ordinal));
        var stateLetters = longRow.Letters.SkipWhile(l => l.Value != "Z" || l.StartBaseLine.X < 600).ToArray();
        Assert.NotEmpty(stateLetters);
        Assert.All(stateLetters, l => Assert.Contains("Bold", l.FontName, StringComparison.Ordinal));
        var normalRow = lines.First(l => l.Text.EndsWith("| Ramka | Zakończony", StringComparison.Ordinal));
        Assert.DoesNotContain(normalRow.Letters, l => l.FontName?.Contains("Bold", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DlugieNazwy_PdfPowstaje_LegendaSkrocona()
    {
        string longReason = "Bardzo długa nazwa błędu " + new string('x', 200);
        string longMachine = "Maszyna o bardzo długiej nazwie " + new string('m', 120);
        var episodes = new[]
        {
            Ep("a9", longReason, "2026-09-29T08:00:00Z", 30, "inny", name: longMachine),
            Ep("a2", "Servo 6", "2026-09-29T09:00:00Z", 10, "restart"),
        };
        using var pdf = PdfProbe.Write(Data(episodes, Mon, Tue));
        string charts = pdf.Text(2);
        string errorCharts = pdf.Text(3);

        Assert.Matches(new Regex("(?m)^Bardzo długa nazwa błędu x{8}…$"), errorCharts); // legenda kołowego błędów: 33 znaki + „…”
        Assert.Contains("Maszyna o bardz…", charts, StringComparison.Ordinal); // oś słupkowego: 16 znaków
        Assert.Contains("Maszyna o bardz…", errorCharts, StringComparison.Ordinal); // oś słupkowego błędów
        // Tabela (strona 4) zawija długą nazwę (wiele linii), ale cały tekst jest w pliku.
        Assert.Equal(200, pdf.Text(4).Count(c => c == 'x'));
    }

    [Fact]
    public void BardzoDlugiZakres_Od2020_PdfWRozsadnymCzasie()
    {
        var episodes = new[]
        {
            Ep("a1", "Servo 6", "2020-01-07T08:00:00Z", 10, "restart"),
            Ep("a2", "Servo 6", "2023-05-10T08:00:00Z", 20, "restart"),
            Ep("a3", "Servo 6", "2026-10-01T08:00:00Z", 30, "restart"),
        };
        ObservationInterval[] observations = [new("a1", ObservationState.NoConnection, At("2020-01-06T06:00:00Z"), At("2026-10-06T06:00:00Z"))];
        var watch = Stopwatch.StartNew();
        var data = Data(episodes, new DateOnly(2020, 1, 1), Tue, observations: observations);
        using var pdf = PdfProbe.Write(data);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"czas: {watch.Elapsed}");
        Assert.True(data.Charts.Days.Series[0].Points.Count <= DowntimeCharts.MaxLineDays);
        Assert.Equal(30, data.Charts.Days.Series[0].Points.Last(p => p.Label == "01.10").Value, 6);
        Assert.True(data.NoConnection > TimeSpan.FromDays(1000));
    }

    [Fact]
    public void WybranaZmiana_NaglowekIInfoMowiaKtora()
    {
        using var pdf = PdfProbe.Write(WeekData(shift: "14:00–22:00"));
        Assert.Contains("Zmiana | 14:00–22:00", pdf.Text(1), StringComparison.Ordinal);
        Assert.StartsWith("Raport postojów maszyn · 28.09.2026 – 06.10.2026 · 14:00–22:00", pdf.Text(2), StringComparison.Ordinal);
        Assert.DoesNotContain("| 06:00–14:00 |", pdf.Text(4), StringComparison.Ordinal);
    }
}
