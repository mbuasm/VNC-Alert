using MaszynyAlert.Core.Config;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

public sealed class OcrInputsTests
{
    private static readonly MachineConfig Amm580 = new()
    {
        Id = "amm580",
        Name = "580",
        Host = "192.168.1.110",
        Port = 5900,
        Profile = "amm580",
    };

    [Fact]
    public void ZrzutZKroku_capture_TrafiaDoOdczytu_IJestPrzypisanyDoMaszynyPoAdresie()
    {
        using var dir = new TempDir();
        dir.File("amm580_full.png");
        dir.File("capture_192.168.1.110_5900_20260919-003618.png");

        var inputs = OcrCommand.CollectInputs([], dir.Path, [Amm580]);

        Assert.Equal(2, inputs.Count);
        // Najpierw klatki z kroku info, potem zrzuty z capture (rosnąco po nazwie, czyli od najstarszego).
        Assert.Equal("amm580_full.png", Path.GetFileName(inputs[0].Path));
        Assert.Equal("amm580", inputs[0].MachineId);
        Assert.Equal("amm580_ocr.png", inputs[0].PreparedImageName);

        Assert.Equal("capture_192.168.1.110_5900_20260919-003618.png", Path.GetFileName(inputs[1].Path));
        Assert.Equal("amm580", inputs[1].MachineId);
        Assert.Contains("192.168.1.110:5900", inputs[1].MachineSource, StringComparison.Ordinal);

        // Obraz przygotowany do OCR nie może nadpisać tego, który zapisuje samo "capture --roi".
        Assert.NotEqual("capture_192.168.1.110_5900_20260919-003618_ocr.png", inputs[1].PreparedImageName);
    }

    [Fact]
    public void KlatkaZKrokuInfo_ZWielkimiLiterami_JestPrzypisanaDoMaszyny()
    {
        // Zbieranie plików nie rozróżnia wielkości liter (Windows), więc opis zrzutu też nie może:
        // inaczej „AMM580_FULL.PNG” stawał się maszyną „AMM580_FULL”, której profilu nikt nie zna.
        using var dir = new TempDir();
        dir.File("AMM580_FULL.PNG");

        var inputs = OcrCommand.CollectInputs([], dir.Path, [Amm580]);

        Assert.Single(inputs);
        Assert.Equal("amm580", inputs[0].MachineId, ignoreCase: true);
        Assert.DoesNotContain("_FULL", inputs[0].MachineId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObrazyPochodne_NieSaCzytanePonownie()
    {
        using var dir = new TempDir();
        dir.File("capture_192.168.1.110_5900_20260919-003618.png");
        dir.File("capture_192.168.1.110_5900_20260919-003618_roi.png");
        dir.File("capture_192.168.1.110_5900_20260919-003618_ocr.png");
        dir.File("amm580_ocr.png");
        dir.File("podsumowanie.json");

        var inputs = OcrCommand.CollectInputs([], dir.Path, [Amm580]);

        Assert.Single(inputs);
        Assert.Equal("capture_192.168.1.110_5900_20260919-003618.png", Path.GetFileName(inputs[0].Path));
    }

    [Fact]
    public void WycinekPaskaZPoprzedniegoPrzebiegu_NieJestCzytanyJakoKolejnyZrzut()
    {
        using var dir = new TempDir();
        dir.File("capture_192.168.1.110_5900_20260919-065007.png");
        dir.File("capture_192.168.1.110_5900_20260919-065007_pasek-ocr.png");

        var inputs = OcrCommand.CollectInputs([], dir.Path, [Amm580]);

        Assert.Single(inputs);
        Assert.Equal("capture_192.168.1.110_5900_20260919-065007.png", Path.GetFileName(inputs[0].Path));
    }

    [Fact]
    public void KazdyObrazZapisywanyPrzezKrokOcr_JestPomijanyPrzyNastepnymPrzebiegu()
    {
        using var dir = new TempDir();
        dir.File("amm580_full.png");
        dir.File("capture_192.168.1.110_5900_20260919-065007.png");

        foreach (string prepared in OcrCommand.CollectInputs([], dir.Path, [Amm580]).Select(i => i.PreparedImageName))
        {
            Assert.True(
                OcrCommand.IsDerivedImage(prepared),
                $"Obraz \"{prepared}\" zapisywany przez krok ocr zostałby przy następnym przebiegu wzięty jako zrzut ekranu.");
        }
    }

    [Fact]
    public void NajnowszyZrzutMaszynyJestOstatni_WiecWygrywaWSugerowanychRoi()
    {
        using var dir = new TempDir();
        dir.File("capture_192.168.1.110_5900_20260919-010000.png");
        dir.File("capture_192.168.1.110_5900_20260919-003618.png");

        var inputs = OcrCommand.CollectInputs([], dir.Path, [Amm580]);

        Assert.Equal(
            new[] { "capture_192.168.1.110_5900_20260919-003618.png", "capture_192.168.1.110_5900_20260919-010000.png" },
            inputs.Select(i => Path.GetFileName(i.Path)).ToArray());
    }

    [Fact]
    public void MaszynaSpozaKonfiguracji_DostajeIdZAdresu_IPowodWRaporcie()
    {
        using var dir = new TempDir();
        dir.File("capture_192.168.1.200_5901_20260919-003618.png");

        var inputs = OcrCommand.CollectInputs([], dir.Path, [Amm580]);

        Assert.Equal("192.168.1.200:5901", inputs[0].MachineId);
        Assert.Contains("nie ma takiej maszyny", inputs[0].MachineSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WskazanyWprostPlik_JestCzytanyNawetGdyLezyPozaFolderemOut()
    {
        using var dir = new TempDir();
        string path = dir.File("moj-zrzut.png");

        var inputs = OcrCommand.CollectInputs([path], "out", [Amm580]);

        Assert.Single(inputs);
        Assert.Equal(Path.GetFullPath(path), inputs[0].Path);
        Assert.Equal("moj-zrzut", inputs[0].MachineId);
    }

    [Fact]
    public void WskazanyWprostFolder_DzialaTakSamoJakDomyslny_IBezPowtorzen()
    {
        using var dir = new TempDir();
        string path = dir.File("amm580_full.png");

        var inputs = OcrCommand.CollectInputs([dir.Path, path], "out", [Amm580]);

        Assert.Single(inputs);
    }

    [Fact]
    public void NieistniejacaSciezka_MowiCoZrobic_ZamiastMilczecoPominac()
    {
        ProbeUsageException ex = Assert.Throws<ProbeUsageException>(
            () => OcrCommand.CollectInputs(["nie-ma-takiego-pliku.png"], "out", [Amm580]));

        Assert.Contains("nie-ma-takiego-pliku.png", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("capture_192.168.1.110_5900_20260919-003618", true, "192.168.1.110", 5900)]
    [InlineData("capture_pc_hali_2_9100_20260919-003618", true, "pc_hali_2", 9100)]
    [InlineData("capture_192.168.1.110_5900", false, null, 0)]
    [InlineData("amm580_full", false, null, 0)]
    [InlineData("capture_192.168.1.110_port_20260919-003618", false, null, 0)]
    public void RozbiorNazwyZrzutuZCapture(string fileName, bool expected, string? host, int port)
    {
        bool parsed = OcrCommand.TryParseCaptureFileName(fileName, out string? parsedHost, out int parsedPort);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(host, parsedHost);
            Assert.Equal(port, parsedPort);
        }
    }

    /// <summary>Katalog tymczasowy z plikami-atrapami (same nazwy — wybór plików nie zagląda do środka).</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("probe-ocr-inputs").FullName;

        public string Path { get; }

        public string File(string name)
        {
            string full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(full, [0]);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Sprzątanie testu nie jest wynikiem testu.
            }
        }
    }
}
