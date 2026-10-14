using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MaszynyAlert.Core.Export;
using MaszynyAlert.Core.History;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

public sealed class ExcelExportTests
{
    private static XLWorkbook Write(DowntimeExportData data)
    {
        var stream = new MemoryStream();
        var outcome = DowntimeExcelExporter.Write(data, stream);
        Assert.True(outcome.Success, outcome.Error);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    private static IEnumerable<IXLCell> AllCells(XLWorkbook workbook) => workbook.Worksheets.SelectMany(w => w.CellsUsed());

    /// <summary>Wiersz z <paramref name="text"/> w kolumnie A arkusza (pierwszy).</summary>
    private static IXLRow RowOf(IXLWorksheet sheet, string text) =>
        sheet.RowsUsed().First(r => r.Cell(1).GetString() == text);

    [Fact]
    public void Arkusze_DokladnieEpizodySumyInformacje_NaglowkiEpizodow()
    {
        using var workbook = Write(WeekData());

        Assert.Equal(["Epizody", "Sumy", "Błędy", "Informacje"], workbook.Worksheets.Select(w => w.Name));
        var sheet = workbook.Worksheet("Epizody");
        string[] expected =
        [
            "Identyfikator epizodu", "Dzień zmiany", "Zmiana", "Maszyna", "Początek", "Koniec", "Czas [min]",
            "Nazwa błędu", "Grupa błędu", "Stan", "Długi postój (> 1 h)",
        ];
        Assert.Equal(expected, DowntimeExcelExporter.EpisodeHeaders);
        Assert.Equal(expected, Enumerable.Range(1, expected.Length).Select(c => sheet.Cell(1, c).GetString()));
        Assert.True(sheet.Cell(1, expected.Length + 1).IsEmpty());
    }

    [Fact]
    public void Epizody_DatyJakoDaty_MinutyJakoLiczby_BezFormul()
    {
        var data = WeekData();
        using var workbook = Write(data);
        var sheet = workbook.Worksheet("Epizody");

        Assert.Equal(data.Episodes.Count + 1, sheet.LastRowUsed()!.RowNumber());
        for (int i = 0; i < data.Episodes.Count; i++)
        {
            var row = data.Episodes[i];
            int r = i + 2;
            Assert.Equal(row.EpisodeId.ToString(), sheet.Cell(r, 1).GetString());
            Assert.True(sheet.Cell(r, 2).Value.IsDateTime, $"B{r}");
            Assert.Equal(row.Day.ToDateTime(TimeOnly.MinValue), sheet.Cell(r, 2).GetDateTime());
            Assert.Equal("yyyy-mm-dd", sheet.Cell(r, 2).Style.DateFormat.Format);
            Assert.True(sheet.Cell(r, 5).Value.IsDateTime, $"E{r}");
            Assert.True(sheet.Cell(r, 6).Value.IsDateTime, $"F{r}");
            Assert.Equal(row.Start, sheet.Cell(r, 5).GetDateTime());
            Assert.Equal(row.End, sheet.Cell(r, 6).GetDateTime());
            Assert.Equal("yyyy-mm-dd hh:mm:ss", sheet.Cell(r, 5).Style.DateFormat.Format);
            Assert.True(sheet.Cell(r, 7).Value.IsNumber, $"G{r}");
            Assert.Equal(row.Minutes, sheet.Cell(r, 7).GetDouble(), 4);
            Assert.Equal(row.IsLong ? "tak" : "nie", sheet.Cell(r, 11).GetString());
        }
        Assert.DoesNotContain(AllCells(workbook), c => c.HasFormula);
    }

    [Fact]
    public void Epizody_StrefaRaportu_PoczatekToZegarScienny()
    {
        var warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var data = Data([Ep("a1", "Servo 6", "2026-09-29T05:30:00Z", 10, "restart")], Mon, Tue, zone: warsaw);
        using var workbook = Write(data);
        var sheet = workbook.Worksheet("Epizody");

        Assert.Equal(new DateTime(2026, 9, 29, 7, 30, 0), sheet.Cell(2, 5).GetDateTime()); // 05:30 UTC = 07:30 w Warszawie (CEST)
        Assert.Equal("06:00–14:00", sheet.Cell(2, 3).GetString());
    }

    [Fact]
    public void Epizody_AutofiltrNaCalosci_ZamrozonyNaglowek_SzerokosciUstawione()
    {
        var data = WeekData();
        using var workbook = Write(data);
        var sheet = workbook.Worksheet("Epizody");

        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal($"A1:K{data.Episodes.Count + 1}", sheet.AutoFilter.Range.RangeAddress.ToString());
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal(0, sheet.SheetView.SplitColumn);
        for (int c = 1; c <= 11; c++)
            Assert.True(sheet.Column(c).Width >= 11, $"kolumna {c}: {sheet.Column(c).Width}");
    }

    [Fact]
    public void PustyRaport_SameNaglowkiIAutofiltrNaWierszu1()
    {
        var data = DowntimeExportData.Create([], Mon, Tue, null, [], [], Utc, Now, "TEST-PC", Local);
        using var workbook = Write(data);
        var sheet = workbook.Worksheet("Epizody");

        Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal("A1:K1", sheet.AutoFilter.Range.RangeAddress.ToString());
        Assert.Equal(1, sheet.SheetView.SplitRow);
        var sums = workbook.Worksheet("Sumy");
        var total = RowOf(sums, "Razem");
        Assert.Equal(0, total.Cell(2).GetDouble());
        Assert.Equal(0, total.Cell(4).GetDouble());
        Assert.Equal(0, total.Cell(6).GetDouble());
        Assert.Equal("brak zapisu", total.Cell(8).GetString());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(2026)]
    public void Sumy_ZgadzajaSieZTotalsIEpizodami_LosoweZestawy(int seed)
    {
        var episodes = Random(seed, 80);
        var slices = PanelSlices(episodes, Mon, Tue);
        var totals = DowntimeReport.Totals(slices);
        var data = Data(episodes, Mon, Tue);
        using var workbook = Write(data);
        var sums = workbook.Worksheet("Sumy");
        var rows = workbook.Worksheet("Epizody").RowsUsed().Skip(1).ToArray();

        var razem = RowOf(sums, "Razem");
        Assert.Equal(totals.All.Minutes, razem.Cell(2).GetDouble(), 3);
        Assert.Equal(totals.WithoutLong.Minutes, razem.Cell(3).GetDouble(), 3);
        Assert.Equal(totals.All.Episodes, (int)razem.Cell(4).GetDouble());
        Assert.Equal(totals.All.Episodes - totals.WithoutLong.Episodes, (int)razem.Cell(5).GetDouble());
        Assert.True(razem.Cell(4).Value.IsNumber);

        // Według nazwy błędu: minuty = suma kolumny „Czas [min]” w „Epizody” po nazwie.
        int header = sums.RowsUsed().First(r => r.Cell(1).GetString() == "Nazwa błędu").RowNumber();
        int dayHeader = sums.RowsUsed().First(r => r.Cell(1).GetString() == "Dzień zmiany").RowNumber();
        var byName = rows.GroupBy(r => r.Cell(8).GetString()).ToDictionary(g => g.Key, g => g.Sum(r => r.Cell(7).GetDouble()));
        int named = 0;
        for (int r = header + 1; r < dayHeader && !sums.Cell(r, 1).IsEmpty(); r++, named++)
            Assert.Equal(byName[sums.Cell(r, 1).GetString()], sums.Cell(r, 3).GetDouble(), 2);
        Assert.Equal(byName.Count, named);

        // Według dnia: data jako data, minuty = suma epizodów dnia = tabela Panelu „Razem w dniu”.
        var panelDays = slices.GroupBy(s => s.Day).ToDictionary(g => g.Key, g => g.Sum(s => s.Minutes));
        int days = 0;
        for (int r = dayHeader + 1; !sums.Cell(r, 1).IsEmpty(); r++, days++)
        {
            Assert.True(sums.Cell(r, 1).Value.IsDateTime);
            var day = DateOnly.FromDateTime(sums.Cell(r, 1).GetDateTime());
            Assert.Equal(panelDays[day], sums.Cell(r, 2).GetDouble(), 3);
        }
        Assert.Equal(panelDays.Count, days);

        // Według maszyny: suma minut maszyn = całość; liczba postojów sumuje się (postój należy do jednej maszyny).
        int machineHeader = sums.RowsUsed().First(r => r.Cell(1).GetString() == "Maszyna").RowNumber();
        double machineMinutes = 0;
        int machineEpisodes = 0;
        for (int r = machineHeader + 1; sums.Cell(r, 1).GetString() != "Razem"; r++)
        {
            machineMinutes += sums.Cell(r, 2).GetDouble();
            machineEpisodes += (int)sums.Cell(r, 4).GetDouble();
        }
        Assert.Equal(totals.All.Minutes, machineMinutes, 2);
        Assert.Equal(totals.All.Episodes, machineEpisodes);
    }

    [Fact]
    public void PostojNaDwochZmianach_JednaPozycjaWLiczbie_DwaWierszeZTymSamymId()
    {
        var data = Data([Ep("a1", "Servo 6", "2026-09-30T13:30:00Z", 60, "restart")], Mon, Tue);
        using var workbook = Write(data);
        var sheet = workbook.Worksheet("Epizody");

        Assert.Equal(sheet.Cell(2, 1).GetString(), sheet.Cell(3, 1).GetString());
        Assert.Equal(["06:00–14:00", "14:00–22:00"], new[] { sheet.Cell(2, 3).GetString(), sheet.Cell(3, 3).GetString() });
        Assert.Equal(1, (int)RowOf(workbook.Worksheet("Sumy"), "Razem").Cell(4).GetDouble());
        Assert.Equal(1, (int)workbook.Worksheet("Informacje").RowsUsed().First(r => r.Cell(1).GetString() == "Liczba postojów").Cell(2).GetDouble());
    }

    [Fact]
    public void Tacka_JakoRamka_WNazwieIGrupie_BezSlowaTacka()
    {
        using var workbook = Write(WeekData());
        var sheet = workbook.Worksheet("Epizody");

        var names = sheet.RowsUsed().Skip(1).Select(r => (Name: r.Cell(8).GetString(), Group: r.Cell(9).GetString())).ToArray();
        Assert.Contains(("Ramka niepobrana", "Ramka"), names);
        Assert.Contains(("Ramka nieodłożona", "Ramka"), names);
        Assert.DoesNotContain(AllCells(workbook), c => Regex.IsMatch(c.GetString(), "tack", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Informacje_ZakresDataKomputerTworca()
    {
        var data = WeekData(scope: DowntimeExportData.ScopeTextFor(true, "Hala A"));
        using var workbook = Write(data);
        var info = workbook.Worksheet("Informacje");
        string Value(string label) => info.RowsUsed().First(r => r.Cell(1).GetString() == label).Cell(2).GetString();
        IXLCell Cell(string label) => info.RowsUsed().First(r => r.Cell(1).GetString() == label).Cell(2);

        Assert.Equal("Raport postojów maszyn", info.Cell(1, 1).GetString());
        Assert.Equal("Raport stworzony przez VNC Alert", info.Cell(2, 1).GetString());
        Assert.True(Cell("Zakres od (dzień zmiany)").Value.IsDateTime);
        Assert.Equal(new DateTime(2026, 9, 28), Cell("Zakres od (dzień zmiany)").GetDateTime());
        Assert.Equal(new DateTime(2026, 10, 6), Cell("Zakres do (dzień zmiany)").GetDateTime());
        Assert.Equal("Wszystkie zmiany", Value("Zmiana"));
        Assert.Equal("Dane grupy komputerów „Hala A”", Value("Dane"));
        Assert.Equal("TEST-PC", Value("Komputer"));
        Assert.True(Cell("Wygenerowano").Value.IsDateTime);
        Assert.Equal(195, Cell("Łączny czas postojów z długimi postojami [min]").GetDouble(), 6);
        Assert.Equal(120, Cell("Łączny czas postojów bez długich postojów [min]").GetDouble(), 6);
        Assert.Equal(7, Cell("Liczba postojów").GetDouble());
        Assert.Equal(1, Cell("W tym długich postojów (> 1 h)").GetDouble());
        Assert.Equal("brak zapisu", Value("Brak danych - brak połączenia z maszyną [min]"));
    }

    [Fact]
    public void BrakDanych_ZDziennikiem_Minuty_IZnanyOd()
    {
        ObservationInterval[] observations =
        [
            new("a2", ObservationState.NoConnection, At("2026-09-29T08:00:00Z"), At("2026-09-29T10:00:00Z")),
            new("a2", ObservationState.NoConnection, At("2026-10-03T08:00:00Z"), At("2026-10-03T10:00:00Z")), // sobota — poza raportem
        ];
        var data = WeekData(observations);
        using var workbook = Write(data);
        var info = workbook.Worksheet("Informacje");
        IXLCell Cell(string label) => info.RowsUsed().First(r => r.Cell(1).GetString() == label).Cell(2);

        Assert.Equal(120, Cell("Brak danych - brak połączenia z maszyną [min]").GetDouble(), 6);
        Assert.Equal(0, Cell("Brak danych - ekran HMI niewidoczny [min]").GetDouble(), 6);
        Assert.True(Cell("„Brak danych” znany od").Value.IsDateTime);
        var sums = workbook.Worksheet("Sumy");
        Assert.Equal(120, RowOf(sums, "Aidite 2").Cell(8).GetDouble(), 6);
        Assert.Equal(120, RowOf(sums, "Razem").Cell(8).GetDouble(), 6);
    }

    [Fact]
    public void StanTrwajacyPrzerwanyDlugi_JakWPanelu()
    {
        using var workbook = Write(WeekData());
        var states = workbook.Worksheet("Epizody").RowsUsed().Skip(1).Select(r => r.Cell(10).GetString()).ToArray();

        Assert.Contains("Przerwana obserwacja", states);
        Assert.Contains("Otwarty epizod", states);
        Assert.Contains("Zakończony, długi postój (> 1 h)", states);
        Assert.Contains("Zakończony", states);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+48 123")]
    [InlineData("-5")]
    [InlineData("@SUM(A1)")]
    [InlineData("=HYPERLINK(\"http://x\",\"y\")")]
    public void TekstZaczynajacySieOdZnakuFormuly_ZostajeTekstem(string reason)
    {
        var data = Data([Ep("a1", reason, "2026-09-29T08:00:00Z", 10, "inny", name: reason)], Mon, Tue);
        using var workbook = Write(data);
        var sheet = workbook.Worksheet("Epizody");

        Assert.False(sheet.Cell(2, 8).HasFormula);
        Assert.True(sheet.Cell(2, 8).Value.IsText);
        Assert.Equal(reason, sheet.Cell(2, 8).GetString());
        Assert.Equal(reason, sheet.Cell(2, 4).GetString());
        Assert.DoesNotContain(AllCells(workbook), c => c.HasFormula);
    }

    /// <summary>
    /// Tekst błędu bywa surowym odczytem OCR („Błąd: …”, <c>MessageMapper</c>) albo przychodzi z sieci grupy. Znak sterujący
    /// (niedozwolony w XML) w jednym wierszu nie może zepsuć CAŁEGO eksportu.
    /// </summary>
    [Theory]
    [InlineData("Błąd: \u0001ABC")]
    [InlineData("Błąd: A\u0000B")]
    [InlineData("Błąd: \u001Fx")]
    public void ZnakSterujacyWTekscieBledu_EksportExcelPowstaje(string reason)
    {
        var data = Data([Ep("a1", reason, "2026-09-29T08:00:00Z", 10, "inny"), Ep("a2", "Servo 6", "2026-09-29T09:00:00Z", 10, "restart")],
            Mon, Tue);
        using var stream = new MemoryStream();
        var outcome = DowntimeExcelExporter.Write(data, stream);
        Assert.True(outcome.Success, outcome.Error);
    }

    [Theory]
    [InlineData("Błąd: \u0001ABC")]
    [InlineData("Błąd: A\u0000B")]
    public void ZnakSterujacyWTekscieBledu_EksportPdfPowstaje(string reason)
    {
        var data = Data([Ep("a1", reason, "2026-09-29T08:00:00Z", 10, "inny")], Mon, Tue);
        using var stream = new MemoryStream();
        var outcome = DowntimePdfExporter.Write(data, stream);
        Assert.True(outcome.Success, outcome.Error);
    }

    /// <summary>
    /// Zastępczo za LibreOffice: plik przechodzi walidację schematu OpenXML (Office 2016) — błędy struktury
    /// zwykle psują otwarcie w innych programach niż Excel.
    /// </summary>
    [Fact]
    public void Skoroszyt_ZgodnyZeSchematemOpenXml()
    {
        foreach (var data in new[] { WeekData(), DowntimeExportData.Create([], Mon, Tue, null, [], [], Utc, Now, "TEST-PC", Local) })
        {
            using var stream = new MemoryStream();
            Assert.True(DowntimeExcelExporter.Write(data, stream).Success);
            stream.Position = 0;
            using var document = SpreadsheetDocument.Open(stream, false);
            var errors = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2016).Validate(document)
                .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToArray();
            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
        }
    }
}
