using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using static MaszynyAlert.Core.Tests.History.HistoryCsvFuzz;

namespace MaszynyAlert.Core.Tests.History;

public sealed class WerTestHigiena2HistoryCsvNulTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    private static HistorySpec Spec(int id, string user, int minutes) =>
        new(id, T0.AddMinutes(minutes), AlertEventKind.Raised, "Aidite " + id, "Servo alarm", "inny", "pełny tekst", null, "PC-A", user);

    private static void Append(string path, HistorySpec spec, RecordingLoggerFactory? logs = null)
    {
        // Nowy obiekt = pierwszy zapis po starcie programu: pełna analiza końca pliku (HistoryCsvTail.Analyze).
        var writer = logs is null
            ? new HistoryWriter(path, new FakeClock(spec.Time))
            : new HistoryWriter(path, new FakeClock(spec.Time), logs.CreateLogger("history"));
        writer.Append(spec.Event, spec.Host, spec.User);
        Assert.Equal(0, writer.PendingCount);
    }

    private static byte[] Prefix(HistorySpec complete) => Concat(HeaderBytes, NewLine, complete.Bytes(Environment.NewLine));

    [Fact]
    public void KompletnyRekord_U0000NaKoncuNazwyKonta_ZostajeBajtWBajt_PlikNieKonczySieZerem()
    {
        using var dir = new TempDir("wer-higiena2-nul");
        string path = dir.File("history.csv");
        HistorySpec withNul = Spec(1, "konto\0", 0);
        Append(path, withNul);
        byte[] written = File.ReadAllBytes(path);
        Assert.NotEqual((byte)0, written[^1]); // każdy wiersz programu kończy się nową linią
        Assert.Equal(Concat(Bom, Prefix(withNul)), written);

        var logs = new RecordingLoggerFactory();
        HistorySpec next = Spec(2, "operator", 1);
        Append(path, next, logs);

        Assert.Empty(logs.Containing("nie rozpoznaje"));
        Assert.Equal(Concat(written, next.Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }

    [Fact]
    public void UrwanyRekordZKompletem8Pol_U0000NaKoncuNazwyKonta_TraciTylkoToZero()
    {
        using var dir = new TempDir("wer-higiena2-nul");
        string path = dir.File("history.csv");
        HistorySpec complete = Spec(1, "operator", 0);
        HistorySpec torn = Spec(2, "konto\0", 1); // zapis urwany tuż przed końcem wiersza: 8 pól, bez terminatora
        File.WriteAllBytes(path, Concat(Prefix(complete), torn.Bytes("")));

        var logs = new RecordingLoggerFactory();
        HistorySpec next = Spec(3, "operator", 2);
        Append(path, next, logs);

        // Rekord zostaje (8 pól na EOF to poprawny CSV), ale jego końcowe U+0000 odpada jak zero wypełnienia NTFS.
        Assert.Empty(logs.Containing("nie rozpoznaje"));
        Assert.Equal(
            Concat(Prefix(complete), (torn with { User = "konto" }).Bytes(Environment.NewLine), next.Bytes(Environment.NewLine)),
            File.ReadAllBytes(path));
    }

    [Fact]
    public void UrwanyRekordZKompletem8Pol_U0000WSrodkuNazwyKonta_ZostajeBajtWBajt()
    {
        using var dir = new TempDir("wer-higiena2-nul");
        string path = dir.File("history.csv");
        HistorySpec complete = Spec(1, "operator", 0);
        HistorySpec torn = Spec(2, "ko\0nto", 1); // zero, po którym jest jeszcze inna treść — zwykły bajt pliku
        byte[] before = Concat(Prefix(complete), torn.Bytes(""));
        File.WriteAllBytes(path, before);

        HistorySpec next = Spec(3, "operator", 2);
        Append(path, next);

        Assert.Equal(Concat(before, NewLine, next.Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UrwanyRekordBezKompletu8Pol_OdpadaTakSamoZU0000NaKoncuIBezNiego(bool nulAtEnd)
    {
        using var dir = new TempDir("wer-higiena2-nul");
        string path = dir.File("history.csv");
        HistorySpec complete = Spec(1, "operator", 0);
        HistorySpec torn = Spec(2, "operator", 1);
        // Zapis urwany w polu „opis” (4. pole) — z U+0000 na końcu ucięcia albo bez: wynik ten sam, fragment odpada w całości.
        string fragment = string.Join(';', torn.RawFields.Take(4)) + (nulAtEnd ? "\0" : "");
        File.WriteAllBytes(path, Concat(Prefix(complete), ProgramEncoding.GetBytes(fragment)));

        HistorySpec next = Spec(3, "operator", 2);
        Append(path, next);

        Assert.Equal(Concat(Prefix(complete), next.Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }
}
