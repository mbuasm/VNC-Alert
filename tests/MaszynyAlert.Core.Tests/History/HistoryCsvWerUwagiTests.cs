using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using static MaszynyAlert.Core.Tests.History.HistoryCsvFuzz;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryCsvWerUwagiTests
{
    private const string CompleteRecord = "2026-10-07 07:59:58;Aidite 1;nowy;\"Opis; z \"\"cytatem\"\"\";inny;\"Pełny\r\ntekst\nŁódź\";PC-A;operator";
    private const string NextRecord = "2026-10-07 08:00:00;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator";
    private const string TornQuoted = "2026-10-07 08:00:05;Aidite 2;nowy;Servo;inny;\"Pełny\r\nteks";
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private static string Line(string description) =>
        NextRecord.Replace(";Servo;inny;", ";" + description + ";inny;", StringComparison.Ordinal);

    private static AlertEvent Event(string description) => new(AlertEventKind.Raised, new Alert
    {
        Key = AlertKey.ForRule("aidite1", "servo"), MachineId = "aidite1", MachineName = "Aidite 1",
        Kind = AlertKind.Error, ShortText = description, FullText = "Servo", Category = "inny",
        StartedAt = T0, LastSeenAt = T0, Status = AlertStatus.Active,
    });

    private static void Append(HistoryWriter writer, string description = "Servo") =>
        writer.Append(Event(description), "PC-A", "operator");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("Błąd osi Ł", -2)]
    [InlineData("Błąd osi Ł", -1)]
    [InlineData("Alarm 😀", -3)]            // cięcie po pierwszym bajcie emoji (F0), potem zera
    [InlineData("Cena 5 €", -2)]
    [InlineData("\"Opis; z \"\"x\"\"\";", -1)] // cięcie po cudzysłowie zamykającym pole (przed ';'), potem zera
    [InlineData("\"Opis; z \"\"x\"\"\";", -6)] // cięcie po pierwszej połowie pary "", potem zera
    [InlineData("\"Opis; z \"\"x\"\"\";", -7)] // kontrola: cięcie przed tą parą (wewnątrz cytowania), potem zera → przycięte
    public void ZeroFillAfterATornCharacterOrQuote_IsTrimmedLikeAnyOtherTornTail(string text, int cutFromEnd)
    {
        using var dir = new TempDir("history-wer");
        string path = dir.File("history.csv");
        string prefix = Header + "\r\n" + CompleteRecord + "\r\n";
        byte[] torn = Utf8("2026-10-07 07:59:59;Aidite 1;nowy;" + text);
        File.WriteAllBytes(path, Concat(Bom, Utf8(prefix), torn[..^-cutFromEnd], new byte[600]));

        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);

        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(Concat(Bom, Utf8(prefix + NextRecord + Environment.NewLine)), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0, false)]   // same zera
    [InlineData(3, false)]   // BOM + zera
    [InlineData(15, false)]  // BOM + „czas;maszyna” + zera
    [InlineData(-1, true)]   // kontrola: BOM + pełny nagłówek + CRLF + zera → zera przycięte
    public void ZeroFilledFirstWrite_RebuildsTheHeaderLikeATornHeader(int keptBytes, bool fullHeader)
    {
        using var dir = new TempDir("history-wer");
        string path = dir.File("history.csv");
        byte[] start = fullHeader ? Concat(Bom, Utf8(Header + "\r\n")) : Concat(Bom, Utf8(Header))[..keptBytes];
        File.WriteAllBytes(path, Concat(start, new byte[300]));

        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);

        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        byte[] header = fullHeader ? Concat(Bom, Utf8(Header + "\r\n")) : Concat(Bom, Utf8(Header + Environment.NewLine));
        Assert.Equal(Concat(header, Utf8(NextRecord + Environment.NewLine)), File.ReadAllBytes(path));
    }

    // w serii awarii (jak RepeatedCrashes_… w HistoryCsvTornWriteFuzzTests, ale zera NTFS także tuż po
    // urwanym znaku wielobajtowym i po cudzysłowie): po serii plik ma się dać odczytać jako CSV po 8 pól, bez utraty i duplikatów.
    [Theory]
    [MemberData(nameof(HistoryCsvTornWriteFuzzTests.Seeds), MemberType = typeof(HistoryCsvTornWriteFuzzTests))]
    public void RepeatedCrashesWithZeroFillAnywhere_FileStaysReadable(int seed) =>
        HistoryCsvTornWriteFuzzTests.RunRepeatedCrashes(seed, zeroFillAnywhere: true, exactOracle: false);

    [Fact]
    public void ZeroFillAfterATornCharacter_ThenATornQuotedRecord_NewRecordsAreSeparate()
    {
        using var dir = new TempDir("history-wer");
        string path = dir.File("history.csv");
        string head = Header + "\r\n" + CompleteRecord + "\r\n";
        File.WriteAllBytes(path, Concat(Bom, Utf8(head), Utf8("2026-10-07 07:59:59;Aidite 1;nowy;Ł")[..^1], new byte[600]));
        Append(new HistoryWriter(path, new FakeClock(T0)), "Po pierwszej awarii");
        File.AppendAllText(path, TornQuoted, new UTF8Encoding(false));

        var writer = new HistoryWriter(path, new FakeClock(T0)); // restart po drugiej awarii
        Append(writer, "Pierwszy po awarii");
        Append(writer, "Drugi po awarii");

        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(Concat(Bom, Utf8(head + Line("Po pierwszej awarii") + Environment.NewLine + Line("Pierwszy po awarii")
            + Environment.NewLine + Line("Drugi po awarii") + Environment.NewLine)), File.ReadAllBytes(path));
        List<string[]>? rows = ParseRows(File.ReadAllBytes(path), 8);
        Assert.NotNull(rows);
        Assert.Equal(Line("Pierwszy po awarii").Split(';'), rows[^2]);
        Assert.Equal(Line("Drugi po awarii").Split(';'), rows[^1]);
    }

    [Theory]
    [InlineData("wiersz-ansi")]
    [InlineData("sklejenie-starszej-wersji")]
    [InlineData("znacznik-czasu-w-tekscie")]
    public void Backlog02_TornQuotedRecordAfterUnrecognizedContent_IsKeptAndSwallowsTheNextRecords(string variant)
    {
        using var dir = new TempDir("history-wer");
        string path = dir.File("history.csv");
        string head = Header + "\r\n" + CompleteRecord + "\r\n";
        byte[] before;
        switch (variant)
        {
            case "wiersz-ansi":
                byte[] ansi = [.. Encoding.ASCII.GetBytes("2026-10-07 07:59:59;Aidite 1;nowy;B"), 0xB3, 0xB9,
                    .. Encoding.ASCII.GetBytes("d;inny;Servo;PC-A;operator\r\n")];
                before = Concat(Bom, Utf8(head), ansi, Utf8(TornQuoted));
                break;
            case "sklejenie-starszej-wersji":
                before = Concat(Bom, Utf8(head + "2026-10-07 07:59:57;Aidite 1;now"
                    + "2026-10-07 07:59:58;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator\r\n" + TornQuoted));
                break;
            default:
                // Odczyt HMI ze znacznikiem czasu i średnikiem: analizator bierze go za początek kolejnego rekordu.
                before = Concat(Bom, Utf8(head
                    + "2026-10-07 08:00:05;Aidite 2;nowy;Servo;inny;\"Odczyt HMI 2026-10-07 07:58:00;E-STOP\r\nlin"));
                break;
        }

        File.WriteAllBytes(path, before);
        var logs = new RecordingLoggerFactory();
        var writer = new HistoryWriter(path, new FakeClock(T0), logs.CreateLogger("history")); // restart po awarii
        Append(writer, "Pierwszy po awarii");
        Append(writer, "Drugi po awarii");

        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        // Nic nie przycięte ani nie przepisane; urwany koniec nie kończy się CR/LF, więc przed nowym wierszem koniec linii programu.
        Assert.Equal(Concat(before, Utf8(Environment.NewLine + Line("Pierwszy po awarii") + Environment.NewLine
            + Line("Drugi po awarii") + Environment.NewLine)), File.ReadAllBytes(path));
        Assert.Single(logs.Containing("nie rozpoznaje")); // ostrzeżenie przy pierwszym zapisie; drugi idzie szybką ścieżką
        // Obecne ograniczenie: otwarty cudzysłów urwanego pola połyka nowe wiersze — czytnik CSV nie widzi rekordów.
        Assert.Null(ParseRows(File.ReadAllBytes(path), requiredFields: null, strictUtf8: false));
    }

    [Fact]
    public void Backlog03_PartialWriteFailure_RetryRepeatsTheRecordsAlreadyOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return; // blokada zakresu blokuje zapis innego uchwytu tylko na Windows
        using var dir = new TempDir("history-wer");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer, "Start");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (int i = 0; i < 100; i++) Append(writer, $"Zaległy {i:000}"); // plik otwarty w arkuszu — wiersze czekają
        }

        Assert.Equal(100, writer.PendingCount);
        long before = new FileInfo(path).Length;
        using (var range = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            range.Lock(before + 5000, 1 << 20); // pierwsze kilobajty paczki przejdą, dalszy zapis nie
            writer.Flush();
            Assert.Equal(100, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }

        // Kompletne wiersze paczki, które trafiły na dysk; kawałek za nimi nie wygląda na rekord (mniej niż 8 pól), więc odpadnie.
        string partial = Encoding.UTF8.GetString(File.ReadAllBytes(path)[(int)before..]);
        int onDisk = partial.Count(c => c == '\n') + (partial.EndsWith('\r') ? 1 : 0);
        Assert.InRange(onDisk, 1, 99); // dowód scenariusza: część paczki trafiła na dysk przed błędem
        Assert.True(partial[(partial.LastIndexOfAny(['\r', '\n']) + 1)..].Split(';').Length < 8, "urwany kawałek wygląda na rekord");

        writer.Flush();
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        List<string[]>? rows = ParseRows(File.ReadAllBytes(path), 8);
        Assert.NotNull(rows);
        List<string> batch = [.. Enumerable.Range(0, 100).Select(i => $"Zaległy {i:000}")];
        List<string> expected = ["Start", .. batch.Take(onDisk), .. batch]; // obecne ograniczenie: początek paczki dwa razy
        Assert.Equal(expected, rows.Skip(1).Select(r => r[3]));
    }
}
