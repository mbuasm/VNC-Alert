using System.Text.Json;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class ManufacturerAlarmListsTests
{
    private static MessageMapper ExampleMapper() => new(TestPaths.ExampleRules);

    private static string ListPath(string file) =>
        Path.Combine(TestPaths.RepositoryRoot, "tests", "MaszynyAlert.Core.Tests", "Ocr", "listy-bledow-2026-09-25", file);

    private static List<(string Profile, string Label, string Content, string Rule)> LoadRows()
    {
        var rows = new List<(string, string, string, string)>();
        foreach ((string file, string profile) in new[] { ("520.json", "aidite5x"), ("580.json", "amm580") })
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ListPath(file)));
            int index = 0;
            foreach (JsonElement row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                index++;
                string label = row.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String
                    ? code.GetString()!
                    : $"{file} wiersz {index}";
                rows.Add((profile, label, row.GetProperty("content").GetString()!, row.GetProperty("rule").GetString()!));
            }
        }

        return rows;
    }

    public static TheoryData<string, string, string, string> Rows()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach ((string profile, string label, string content, string rule) in LoadRows())
        {
            data.Add(profile, label, content, rule);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void KazdaPozycjaZListyProducenta_TrafiaWSwojaRegule(string profile, string label, string content, string rule)
    {
        MappedMessage m = ExampleMapper().Map(content, profile);

        Assert.True(m.Matched, $"{label} („{content}”) nie trafił w żadną regułę bazy {profile} (wyszło: {m.ShortText}).");
        Assert.Equal(rule, m.RuleId);
    }

    [Fact]
    public void ListyProducenta_MajaKompletWierszy()
    {
        var rows = LoadRows();
        Assert.Equal(62, rows.Count(r => r.Profile == "aidite5x"));
        Assert.Equal(32, rows.Count(r => r.Profile == "amm580"));
    }

    // Słownictwo ekranu bywa inne niż w instrukcji (knife → tool, cutting → milling, security → safety,
    // knife instrument → tool setter) — reguły mają łapać oba warianty.
    [Theory]
    [InlineData("aidite5x", "The safety door is not closed and the milling has been suspended", "aidite5x-door")]
    [InlineData("aidite5x", "Milling fluid flow is abnormal, please reset the system", "aidite5x-milling-fluid-flow")]
    [InlineData("aidite5x", "The tool number is not in the range T0 to T10", "aidite5x-tool-number")]
    [InlineData("aidite5x", "Alarm on the stroke over the tool setter", "aidite5x-tool-setter-stroke")]
    [InlineData("aidite5x", "The tool library is abnormal, the empty tool is abnormal", "aidite5x-magazine")]
    [InlineData("aidite5x", "Automatic calibration is complete, please reset the system", "aidite5x-calibration-done")]
    [InlineData("aidite5x", "{T3} Tool life was reached. Please change the tool.", "aidite5x-tool-life")]
    [InlineData("amm580", "The safety door is not closed. Please close the door", "amm580-door")]
    [InlineData("amm580", "The tool may have been worn or broken", "amm580-tool-broken")]
    [InlineData("amm580", "Insufficient external air pressure", "amm580-air-pressure")]
    public void WariantySlownictwaEkranu_TrafiajaWTeSameReguly(string profile, string text, string rule)
    {
        Assert.Equal(rule, ExampleMapper().Map(text, profile).RuleId);
    }

    [Theory]
    [InlineData("aidite5x", "In auto matic measurement, excessive load of Z axis is detected", "aidite5x-measurement-axis-load")]
    [InlineData("aidite5x", "Center-X devi ation is too large", "aidite5x-calibration")]
    [InlineData("aidite5x", "Illegal ill egal number was detected in the knife", "aidite5x-tool-number")]
    [InlineData("aidite5x", "Spi ndle inverter alarm", "aidite5x-spindle-inverter")]
    [InlineData("amm580", "The secur ity door is not closed", "amm580-door")]
    public void RozbiteSlowoOcr_NieGubiDopasowania(string profile, string text, string rule)
    {
        Assert.Equal(rule, ExampleMapper().Map(text, profile).RuleId);
    }

    [Theory]
    [InlineData("No tool on spindle!")]
    [InlineData("IDLE: Abort")]
    [InlineData("RUN: Normal")]
    public void KomunikatyInformacyjne580_NieDostajaNazwyBledu(string text)
    {
        MappedMessage m = ExampleMapper().Map(text, "amm580");

        Assert.False(m.Matched, $"„{text}” trafił w regułę {m.RuleId} ({m.ShortText}).");
    }

    [Fact]
    public void ListaAidite17_NieDostalaRegulZListy520I580()
    {
        MappedMessage m = ExampleMapper().Map("Automatic correction is complete, please reset the system", TestPaths.AiditeProfile);

        Assert.False(m.RuleId?.StartsWith("aidite5x-", StringComparison.Ordinal) ?? false);
        Assert.False(m.RuleId?.StartsWith("amm580-", StringComparison.Ordinal) ?? false);
    }

    private const string Tip520M02401Ocr = "EDIT READY M024. 01 Before using machi ne. AUTO USB PROTECT 2026.09.25 68";

    [Theory]
    [InlineData(Tip520M02401Ocr)]
    [InlineData("TIP:M024.01 Before using machine, Please")]
    [InlineData("Before using the machine, clean the bin and knife bank, check the pressure and ensure no oil and water, clean the vacuum cleaner and start the vacuum cleaner")]
    [InlineData("M024. 01 Bef ore using machine, Please clean the bin")]
    public void Przypomnienie520M02401_JestBezAlarmu(string text)
    {
        MappedMessage m = ExampleMapper().Map(text, "aidite5x");

        Assert.Equal("aidite5x-before-using", m.RuleId);
        Assert.Equal("bez alarmu", m.Category);
        Assert.True(m.Suppressed);
        Assert.True(m.Ignored);
    }

    [Theory]
    // Wyciszenie przegrywa z każdą inną pasującą regułą: gdy obok przypomnienia OCR widzi prawdziwy błąd, jest alarm.
    [InlineData("TIP:M024.01 Before using machine, Please Insufficient external air pressure", "aidite5x-air-pressure")]
    [InlineData("M024. 01 Before using machi ne. Spindle inverter alarm", "aidite5x-spindle-inverter")]
    // Sąsiedni kod M024.00 (ciśnienie powietrza) różni się jedną cyfrą — nie może trafić w regułę wyciszającą.
    [InlineData("TIP:M024.00 Insufficient external air pressure pressure", "aidite5x-air-pressure")]
    // Magazyn narzędzi nadal jest błędem — wyłączony jest tylko fragment M024.01 „bin and knife bank”.
    [InlineData("The knife bank is abnormal", "aidite5x-magazine")]
    // MO23.12 zostaje zwykłym przypomnieniem z alertem.
    [InlineData("Please clean and maintain the machine", "aidite5x-maint-clean")]
    public void Przypomnienie520M02401_NieWyciszaInnychKomunikatow(string text, string rule)
    {
        MappedMessage m = ExampleMapper().Map(text, "aidite5x");

        Assert.Equal(rule, m.RuleId);
        Assert.False(m.Suppressed);
    }

    private const string Szyny3600Screen =
        "STOPPED [3600] Job_2026-09-24_20-06-39.NC line3000824: The actual measured tool length i";

    private const string Szyny3600Full =
        "[3600] Job_2026-09-24_20-06-39.NC line3000824: The actual measured tool length is less than the set minimum tool length";

    [Fact]
    public void ListaProducenta520_NieZnaA3600AniNarzedziaZaKrotkiego()
    {
        string list = File.ReadAllText(ListPath("520.json"));

        Assert.DoesNotContain("3600", list, StringComparison.Ordinal);
        Assert.DoesNotContain("minimum", list, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Szyny3600Full)]
    [InlineData("The actual measured tool length is less than the set minimum tool length")]
    [InlineData("The actual measured knife length is less than the set minimum knife length")]
    // Rozbicie słowa przez Windows OCR (jak „Spi ndle”) i ucięcie końca po „minimum”.
    [InlineData("The actual measured tool length is less than the set mini mum")]
    public void Komunikat3600_ZmierzoneNarzedzieZaKrotkie(string text)
    {
        MappedMessage m = ExampleMapper().Map(text, "aidite5x");

        Assert.Equal("aidite5x-tool-too-short", m.RuleId);
        Assert.Equal("narzedzie", m.Category);
        Assert.False(m.Suppressed);
    }

    [Theory]
    // Dokładnie to, co mieści pasek: kierunku (za długie / za krótkie) z tego nie widać.
    [InlineData(Szyny3600Screen)]
    [InlineData("The actual measured tool length i")]
    [InlineData("… STOPPED [3600] Job_2026-09-24_20-06-39. NC line3000824: The actual measured tool Iength i")]
    public void Komunikat3600UcietyNaPasku_DajeOpisBezKierunku(string text)
    {
        MappedMessage m = ExampleMapper().Map(text, "aidite5x");

        Assert.Equal("aidite5x-tool-length", m.RuleId);
        Assert.Equal("narzedzie", m.Category);
    }

    [Fact]
    public void A3581_NadalNarzedzieZaDlugie_MimoTegoSamegoPoczatku()
    {
        // Kolejność: „za długie” i „za krótkie” stoją PRZED regułą uciętego komunikatu.
        MappedMessage m = ExampleMapper().Map(
            "The actual measured tool length is greater than the set longest knife length", "aidite5x");

        Assert.Equal("aidite5x-tool-too-long", m.RuleId);
    }

    [Theory]
    // Z przypomnieniem M024.01 na tym samym pasku: komunikat o długości narzędzia wygrywa z wyciszeniem.
    [InlineData("TIP:M024.01 Before using machine, Please " + Szyny3600Screen, "aidite5x-tool-length")]
    [InlineData("TIP:M024.01 Before using machine, Please " + Szyny3600Full, "aidite5x-tool-too-short")]
    public void Komunikat3600_NieJestWyciszanyPrzezPrzypomnienie(string text, string rule)
    {
        MappedMessage m = ExampleMapper().Map(text, "aidite5x");

        Assert.Equal(rule, m.RuleId);
        Assert.False(m.Suppressed);
    }

    [Theory]
    // „MAXIMUM” różni się od „MINIMUM” dwiema literami — tolerancja reguły to 1, więc nie może jej złapać.
    [InlineData("The maximum tool length is exceeded")]
    public void MaksymalnaDlugosc_NieJestNarzedziemZaKrotkim(string text)
    {
        Assert.NotEqual("aidite5x-tool-too-short", ExampleMapper().Map(text, "aidite5x").RuleId);
    }

    [Fact]
    public void Przypomnienie520M02401_NaInnychBazachNieJestWyciszane()
    {
        Assert.False(ExampleMapper().Map(Tip520M02401Ocr, "amm580").Suppressed);
        Assert.False(ExampleMapper().Map(Tip520M02401Ocr, TestPaths.AiditeProfile).Suppressed);
    }
}
