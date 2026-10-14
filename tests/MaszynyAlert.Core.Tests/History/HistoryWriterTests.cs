using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryWriterTests
{
    private const string Header = "czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik";

    private static Alert SampleAlert(string machineName = "Aidite 1", string shortText = "Nie udało się pobrać tacki") => new()
    {
        Key = AlertKey.ForRule("aidite1", "tray-grab"),
        MachineId = "aidite1",
        MachineName = machineName,
        Kind = AlertKind.Error,
        ShortText = shortText,
        FullText = "Failed to grab a tray",
        Category = "tacka",
        StartedAt = FakeClock.DefaultStart,
        LastSeenAt = FakeClock.DefaultStart,
        Status = AlertStatus.Active,
        Sound = true,
    };

    [Fact]
    public void Append_DoNowegoPliku_ZapisujeZnacznikBomNaglowekIWiersz()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert()), "PC-3", "jkowalski");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Header, lines[0]);
        Assert.Equal(
            "2026-03-17 08:00:00;Aidite 1;nowy;Nie udało się pobrać tacki;tacka;Failed to grab a tray;PC-3;jkowalski",
            lines[1]);
    }

    [Fact]
    public void Append_DrugiRaz_NieDopisujeAniBomAniNaglowka()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var clock = new FakeClock();
        var writer = new HistoryWriter(path, clock);

        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert()));
        clock.Advance(TimeSpan.FromMinutes(5));
        writer.Append(new AlertEvent(AlertEventKind.Acknowledged, SampleAlert()), "PC-1", "anna");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Single(bytes.Where((b, i) => b == 0xEF && i + 2 < bytes.Length && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF));

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(3, lines.Length);
        Assert.Equal(Header, lines[0]);
        Assert.StartsWith("2026-03-17 08:00:00;", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2026-03-17 08:05:00;", lines[2], StringComparison.Ordinal);
        Assert.Contains(";potwierdzony;", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AlertEventKind.Raised, "nowy")]
    [InlineData(AlertEventKind.Acknowledged, "potwierdzony")]
    [InlineData(AlertEventKind.Escalated, "eskalacja")]
    [InlineData(AlertEventKind.Closed, "zamknięty")]
    [InlineData(AlertEventKind.Updated, "aktualizacja")]
    public void Append_NazwyZdarzen_SaZapisywanePoPolsku(AlertEventKind kind, string expected)
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(kind, SampleAlert()));

        string line = File.ReadAllLines(path, Encoding.UTF8)[1];
        Assert.Equal(expected, line.Split(';')[2]);
    }

    [Fact]
    public void Append_PolZawierajacychSrednikLubCudzyslow_UjmujeJeWCudzyslowy()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        Alert alert = SampleAlert(machineName: "Maszyna; z średnikiem", shortText: "Opis z \"cudzysłowem\"");
        writer.Append(new AlertEvent(AlertEventKind.Raised, alert));

        string line = File.ReadAllLines(path, Encoding.UTF8)[1];
        Assert.Contains("\"Maszyna; z średnikiem\"", line, StringComparison.Ordinal);
        Assert.Contains("\"Opis z \"\"cudzysłowem\"\"\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_GdyBrakKatalogu_TworzyGo()
    {
        using var dir = new TempDir("historia");
        string path = Path.Combine(dir.Path, "gleboko", "zagniezdzony", "history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert()));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Append_GdyBrakDanychOKomputerze_UzywaDanychZAlertu()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        Alert alert = SampleAlert() with { AckedByHost = "PC-9", AckedByUser = "zdalny" };
        writer.Append(new AlertEvent(AlertEventKind.Acknowledged, alert));

        string[] fields = File.ReadAllLines(path, Encoding.UTF8)[1].Split(';');
        Assert.Equal("PC-9", fields[6]);
        Assert.Equal("zdalny", fields[7]);
    }

    [Fact]
    public void Append_GdyPlikJestZablokowanyPrzezInnyProces_NieRzucaWyjatku()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        using (var blokada = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert()));
        }

        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert()));
        Assert.Equal(3, File.ReadAllLines(path, Encoding.UTF8).Length);
    }

    [Fact]
    public void Konstruktor_ZPustaSciezka_RzucaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new HistoryWriter("  ", new FakeClock()));
    }

    [Theory]
    [InlineData(AlertEventKind.Raised, true)]
    [InlineData(AlertEventKind.Escalated, true)]
    [InlineData(AlertEventKind.Updated, false)]
    [InlineData(AlertEventKind.Closed, false)]
    public void Append_PowodPonownegoAlarmu_TrafiaDoKolumnyPelnyTekstTylkoDlaNowegoIEskalacji(
        AlertEventKind kind, bool expected)
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());

        writer.Append(new AlertEvent(kind, SampleAlert()) { Reason = "przerwa 11 s" });

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal("czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik", lines[0].TrimStart('\uFEFF'));
        string fullText = lines[1].Split(';')[5];
        Assert.Equal(expected ? "Failed to grab a tray [powód: przerwa 11 s]" : "Failed to grab a tray", fullText);
    }


    [Fact]
    public void Append_PrzyZablokowanymPliku_WpisyCzekajaIZapisujaSieWKolejnosciPoZwolnieniu()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var clock = new FakeClock();
        var writer = new HistoryWriter(path, clock);
        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert(machineName: "M0")));

        string firstLockedTime = "";
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (int i = 1; i <= 5; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                if (i == 1) firstLockedTime = clock.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert(machineName: "M" + i)));
            }

            Assert.Equal(5, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert(machineName: "M6")));

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(8, lines.Length);
        Assert.Equal(["M0", "M1", "M2", "M3", "M4", "M5", "M6"], lines.Skip(1).Select(l => l.Split(';')[1]));
        // Czas wiersza to chwila zdarzenia, nie chwila zapisu po zwolnieniu pliku.
        Assert.Equal(firstLockedTime, lines[2].Split(';')[0]);
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
    }

    [Fact]
    public void Flush_ZapisujeZalegleWpisyBezNowegoZdarzenia()
    {
        using var dir = new TempDir("historia");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());
        File.WriteAllText(path, Header + Environment.NewLine);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        writer.Append(new AlertEvent(AlertEventKind.Raised, SampleAlert()));
        Assert.Equal(1, writer.PendingCount);
        Assert.Contains("history.csv", writer.Error, StringComparison.Ordinal);

        File.SetAttributes(path, FileAttributes.Normal);
        writer.Flush();

        Assert.Equal(2, File.ReadAllLines(path, Encoding.UTF8).Length);
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
    }
}
