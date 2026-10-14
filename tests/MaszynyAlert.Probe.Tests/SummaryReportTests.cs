using System.Text.Json;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Probe.Tests;

public sealed class SummaryReportTests
{
    [Fact]
    public void Machine_TegoSamegoIdentyfikatora_ZwracaTenSamWiersz()
    {
        var summary = new SummaryReport();

        MachineSummary first = summary.Machine("aidite1");
        first.Rfb = "3.8";
        MachineSummary again = summary.Machine("aidite1");
        again.Security = "VncAuth";

        Assert.Same(first, again);
        MachineSummary row = Assert.Single(summary.Rows);
        Assert.Equal("3.8", row.Rfb);
        Assert.Equal("VncAuth", row.Security);
    }

    [Fact]
    public void Rows_ZachowujaKolejnoscPierwszegoDodania()
    {
        var summary = new SummaryReport();
        summary.Machine("aidite3");
        summary.Machine("aidite1");
        summary.Machine("amm580");
        summary.Machine("aidite1"); // ponowne dotknięcie nie przestawia kolejności

        Assert.Equal(["aidite3", "aidite1", "amm580"], summary.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Machine_PustegoIdentyfikatora_RzucaWyjatek()
    {
        var summary = new SummaryReport();

        Assert.Throws<ArgumentException>(() => summary.Machine(""));
        Assert.Throws<ArgumentException>(() => summary.Machine("   "));
        Assert.Throws<ArgumentNullException>(() => summary.Machine(null!));
    }

    [Fact]
    public void MachineSummary_NowegoWiersza_MaWartosciDomyslneMyslnik()
    {
        // Dlaczego: „-” znaczy „jeszcze nie sprawdzone w tym przebiegu” i musi się różnić od „nie znaleziono”
        // (sprawdzone, nie ma) — na tym rozróżnieniu stoi diagnoza „czy krok w ogóle się wykonał”.
        MachineSummary row = new SummaryReport().Machine("aidite1");

        Assert.Equal("aidite1", row.Id);
        Assert.Equal("aidite1", row.Name); // nazwa domyślnie = identyfikator, dopóki info jej nie ustawi
        Assert.Equal("-", row.Rfb);
        Assert.Equal("-", row.Security);
        Assert.Equal("-", row.Screen);
        Assert.Equal("-", row.PixelFormat);
        Assert.Equal("-", row.ProgressField);
        Assert.Equal("-", row.FileNameField);
        Assert.Equal("-", row.AlertWindow);
        Assert.Equal(0, row.Saved);
    }

    [Fact]
    public void Render_DwochMaszyn_DajeNaglowekSeparatorIPoJednymWierszuNaMaszyne()
    {
        // Dlaczego: to jest dokładnie to, co użytkownik zobaczy na ekranie i wklei/prześle — wszystkie
        // fakty o maszynie mają być w JEDNEJ linii, żeby dało się je porównać między maszynami.
        var summary = new SummaryReport();
        MachineSummary a = summary.Machine("aidite1");
        a.Name = "Aidite 1";
        a.Rfb = "3.8";
        a.Security = "VncAuth";
        a.Screen = "1024x768";
        a.ProgressField = "440,380,120,24 (odczyt 79 %)";
        a.FileNameField = "300,60,320,20";
        a.AlertWindow = "zmian: 3, kolory tekstu: czerwony, żółty";
        a.Saved = 7;

        MachineSummary b = summary.Machine("amm580");
        b.Name = "AMM 580";
        b.AlertWindow = "bez zmian";

        List<string> lines = summary.Render().ToList();

        Assert.Equal(4, lines.Count); // nagłówek + separator + 2 wiersze
        Assert.Contains("maszyna", lines[0], StringComparison.Ordinal);
        Assert.Contains("okno alertów", lines[0], StringComparison.Ordinal);
        Assert.Contains("zrzutów", lines[0], StringComparison.Ordinal);
        Assert.Matches("^[-+ ]+$", lines[1]);

        Assert.Contains("Aidite 1", lines[2], StringComparison.Ordinal);
        Assert.Contains("3.8", lines[2], StringComparison.Ordinal);
        Assert.Contains("VncAuth", lines[2], StringComparison.Ordinal);
        Assert.Contains("1024x768", lines[2], StringComparison.Ordinal);
        Assert.Contains("440,380,120,24 (odczyt 79 %)", lines[2], StringComparison.Ordinal);
        Assert.Contains("300,60,320,20", lines[2], StringComparison.Ordinal);
        Assert.Contains("zmian: 3, kolory tekstu: czerwony, żółty", lines[2], StringComparison.Ordinal);
        Assert.Contains("7", lines[2], StringComparison.Ordinal);

        Assert.Contains("AMM 580", lines[3], StringComparison.Ordinal);
        Assert.Contains("bez zmian", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Render_KolumnyWszystkichWierszy_SaWyrownaneDoTejSamejSzerokosci()
    {
        // Dlaczego: tabela ma być czytelna w Notatniku i w konsoli — nierówne kolumny przy 12 maszynach
        // zamieniają ją w nieczytelną kaszę.
        var summary = new SummaryReport();
        summary.Machine("m1").Name = "Krótka";
        summary.Machine("m2").Name = "Bardzo długa nazwa maszyny";

        List<string> lines = summary.Render().ToList();

        Assert.Equal(lines[0].Length, lines[1].Length);
        Assert.All(lines, l => Assert.Equal(lines[0].Length, l.Length));
    }

    [Fact]
    public void Render_ObserwacjiZUcietymTekstemBledu_PokazujeLiczbeUcietych()
    {
        // Dlaczego: „ucięte: N” mówi, że czerwony tekst dotykał krawędzi ROI, czyli obszar jest za wąski —
        // to jedna z niewielu rzeczy, które ma wykryć ZA użytkownika (sam by tego nie zauważył).
        var summary = new SummaryReport();
        summary.Machine("aidite1").AlertWindow = "zmian: 2, kolory tekstu: czerwony, ucięte: 4";

        Assert.Contains("ucięte: 4", Assert.Single(summary.Render().Skip(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PustegoPodsumowania_DajeSamNaglowekISeparator()
    {
        List<string> lines = new SummaryReport().Render().ToList();

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void RenderJson_TychSamychDanych_JestPoprawnymJsonemZTymiSamymiPolami()
    {
        // Dlaczego: plik podsumowanie.json ma być odczytywalny maszynowo — jeśli nie da się
        // go sparsować albo pola się rozjadą z tabelą, nie ma z niego żadnego pożytku.
        var summary = new SummaryReport();
        MachineSummary a = summary.Machine("aidite1");
        a.Name = "Aidite 1";
        a.Rfb = "3.8";
        a.Security = "VncAuth";
        a.Screen = "1024x768";
        a.ProgressField = "440,380,120,24 (odczyt 79 %)";
        a.FileNameField = "nie znaleziono";
        a.AlertWindow = "zmian: 3, kolory tekstu: czerwony";
        a.Saved = 7;
        summary.Machine("amm580").Name = "AMM 580";

        using JsonDocument doc = JsonDocument.Parse(summary.RenderJson());
        JsonElement root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(2, root.EnumerateObject().Count());

        JsonElement m = root.GetProperty("aidite1");
        Assert.Equal("Aidite 1", m.GetProperty("name").GetString());
        Assert.Equal("3.8", m.GetProperty("rfb").GetString());
        Assert.Equal("VncAuth", m.GetProperty("security").GetString());
        Assert.Equal("1024x768", m.GetProperty("screen").GetString());
        Assert.Equal("440,380,120,24 (odczyt 79 %)", m.GetProperty("progressField").GetString());
        Assert.Equal("nie znaleziono", m.GetProperty("fileNameField").GetString());
        Assert.Equal("zmian: 3, kolory tekstu: czerwony", m.GetProperty("alertWindow").GetString());
        Assert.Equal(7, m.GetProperty("saved").GetInt32());

        // Maszyna nietknięta przez kroki ocr/watch ma w JSON-ie te same „-”, co w tabeli.
        Assert.Equal("-", root.GetProperty("amm580").GetProperty("progressField").GetString());
    }

    [Fact]
    public void RenderJson_PolskichZnakow_NieZamieniaNaKodyUnicode()
    {
        // Dlaczego: plik ma być czytelny także dla człowieka, który go otworzy w Notatniku — „żółty”
        // zamiast „żółty” sprawia, że użytkownik myśli, że plik jest uszkodzony.
        var summary = new SummaryReport();
        summary.Machine("aidite1").AlertWindow = "kolory tekstu: żółty, czerwony (zmian: 2)";

        string json = new SummaryReport().RenderJson();
        Assert.Equal("{}", json.Trim());

        string withData = summary.RenderJson();
        Assert.Contains("żółty", withData, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", withData, StringComparison.Ordinal);
    }


    [Fact]
    public void Render_MaszynyZFormatemPiksela_PokazujeKolumneMiedzyEkranemAPolemPostepu()
    {
        var summary = new SummaryReport();
        MachineSummary a = summary.Machine("aidite1");
        a.Name = "Aidite 1";
        a.Screen = "1024x768";
        a.PixelFormat = RfbPixelFormat.Rgb565LittleEndian.Describe();

        List<string> lines = summary.Render().ToList();

        int ekran = lines[0].IndexOf("ekran", StringComparison.Ordinal);
        int formatPiksela = lines[0].IndexOf("format piksela", StringComparison.Ordinal);
        int polePostepu = lines[0].IndexOf("pole postępu", StringComparison.Ordinal);
        Assert.True(formatPiksela > ekran, "Kolumna „format piksela” ma stać PO kolumnie „ekran”.");
        Assert.True(formatPiksela < polePostepu, "Kolumna „format piksela” ma stać PRZED kolumną „pole postępu”.");

        Assert.Contains(RfbPixelFormat.Rgb565LittleEndian.Describe(), lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MaszynyZDwochGrupEtapu0_PokazujeRozneFormatyPikselaWKazdymWierszu()
    {
        // Dlaczego: to jest cały powód istnienia tej kolumny — w JEDNEJ tabeli ma być widać, że maszyny
        // dzielą się na dwie grupy. Gdyby wartość była brana raz na cały raport (a nie z wiersza), obie
        // maszyny pokazywałyby to samo i różnica znów by zniknęła.
        var summary = new SummaryReport();
        MachineSummary opencnc = summary.Machine("aidite1");
        opencnc.Name = "Aidite 1";
        opencnc.PixelFormat = "32bpp depth 24 BGRA32 LE";
        MachineSummary wince = summary.Machine("aidite3");
        wince.Name = "Aidite 3";
        wince.PixelFormat = "16bpp RGB555";

        List<string> lines = summary.Render().ToList();

        Assert.Contains("32bpp depth 24 BGRA32 LE", lines[2], StringComparison.Ordinal);
        Assert.Contains("16bpp RGB555", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJson_MaszynyZFormatemPiksela_MaPolePixelFormatZTaSamaWartoscia()
    {
        // Dlaczego: podsumowanie.json jest maszynowym odpowiednikiem tabeli — gdyby nowa kolumna była
        // tylko w tekście, plik przestałby odpowiadać temu, co użytkownik widzi na ekranie (a to na nim
        // opiera się późniejsze dostrajanie profili ekranu).
        var summary = new SummaryReport();
        MachineSummary a = summary.Machine("aidite3");
        a.Name = "Aidite 3";
        a.Screen = "1024x768";
        a.PixelFormat = "16bpp RGB555";

        using JsonDocument doc = JsonDocument.Parse(summary.RenderJson());
        JsonElement m = doc.RootElement.GetProperty("aidite3");

        Assert.Equal("1024x768", m.GetProperty("screen").GetString());
        Assert.Equal("16bpp RGB555", m.GetProperty("pixelFormat").GetString());
    }

    [Fact]
    public void RenderJson_MaszynyNietknietejKrokiemInfo_MaPolePixelFormatZMyslnikiem()
    {
        // Dlaczego: „-” znaczy „krok info nie połączył się z tą maszyną”, a nie „maszyna nie ma formatu
        // piksela” — bez tego rozróżnienia brak danych wyglądałby jak wynik pomiaru.
        var summary = new SummaryReport();
        summary.Machine("amm580").Name = "AMM 580";

        using JsonDocument doc = JsonDocument.Parse(summary.RenderJson());

        Assert.Equal("-", doc.RootElement.GetProperty("amm580").GetProperty("pixelFormat").GetString());
    }
}
