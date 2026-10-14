using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using Xunit.Abstractions;
using static MaszynyAlert.Core.Tests.History.HistoryCsvFuzz;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryCsvNaprawaTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Seeds => HistoryCsvTornWriteFuzzTests.Seeds;

    private const string NextRecord = "2026-10-07 08:00:00;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator";
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private static AlertEvent Event(string description) => new(AlertEventKind.Raised, new Alert
    {
        Key = AlertKey.ForRule("aidite1", "servo"), MachineId = "aidite1", MachineName = "Aidite 1",
        Kind = AlertKind.Error, ShortText = description, FullText = "Servo", Category = "inny",
        StartedAt = T0, LastSeenAt = T0, Status = AlertStatus.Active,
    });

    private static void Append(HistoryWriter writer, string description) => writer.Append(Event(description), "PC-A", "operator");

    private static string Line(string description) =>
        NextRecord.Replace(";Servo;inny;", ";" + description + ";inny;", StringComparison.Ordinal);

    /// <summary>Plik, jaki program zapisuje od zera: BOM, nagłówek i wiersze z opisami <paramref name="descriptions"/>.</summary>
    private static byte[] FileWith(params string[] descriptions) => Concat(Bom, ProgramEncoding.GetBytes(
        Header + Environment.NewLine + string.Concat(descriptions.Select(d => Line(d) + Environment.NewLine))));

    /// <summary>Uchwyt innego programu, który dzieli plik tylko do zapisu — wpuszcza wyłącznie otwarcia BEZ prawa odczytu.</summary>
    private static FileStream WriteOnlySharing(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Write);

    [Fact]
    public void VerifiedAppend_OpensTheFileWithoutReadAccess()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare na Linuksie to tylko blokada doradcza
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer, "Pierwszy"); // nowy plik: pełna analiza, granica zapamiętana

        using (WriteOnlySharing(path))
        {
            Append(writer, "Drugi");
            Assert.Equal(0, writer.PendingCount);
            Assert.Null(writer.Error);
        }

        Append(writer, "Trzeci");
        Assert.Equal(FileWith("Pierwszy", "Drugi", "Trzeci"), File.ReadAllBytes(path));
    }

    // granica szybkiej ścieżki: pierwszy zapis po starcie (nowy obiekt) i każdy zapis po błędzie
    // czytają koniec pliku — wtedy uchwyt bez współdzielenia odczytu zatrzymuje wpis w kolejce, także gdy plik się nie zmienił.
    // Po udanym zapisie szybka ścieżka wraca.
    [Fact]
    public void FirstWriteAfterStartAndEveryWriteAfterAnError_StillReadTheTail()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        Append(new HistoryWriter(path, new FakeClock(T0)), "Start");

        var writer = new HistoryWriter(path, new FakeClock(T0)); // restart programu
        using (WriteOnlySharing(path))
        {
            Append(writer, "Po starcie");
            Assert.Equal(1, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }

        writer.Flush();
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // np. arkusz kalkulacyjny
        {
            Append(writer, "Zablokowany");
            Assert.Equal(1, writer.PendingCount);
        }

        byte[] unchanged = File.ReadAllBytes(path);
        using (WriteOnlySharing(path))
        {
            writer.Flush(); // plik bez zmian od ostatniego udanego zapisu, ale po błędzie — pełna analiza
            Assert.Equal(1, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }

        Assert.Equal(unchanged, File.ReadAllBytes(path));
        writer.Flush();
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        using (WriteOnlySharing(path))
        {
            Append(writer, "Szybko"); // po udanym zapisie znów bez odczytu
            Assert.Equal(0, writer.PendingCount);
        }

        Assert.Equal(FileWith("Start", "Po starcie", "Zablokowany", "Szybko"), File.ReadAllBytes(path));
    }

    // szybka ścieżka najpierw otwiera plik do dopisania, a dopiero potem porównuje znacznik — zmiana z zewnątrz
    // (urwany albo pełny wiersz innego procesu, plik usunięty) kończy się pełną analizą końca, bez śladu po próbie.
    [Theory]
    [InlineData("urwany-wiersz")]
    [InlineData("pelny-wiersz")]
    [InlineData("usuniety-plik")]
    public void ExternalChangeAfterVerifiedWrite_FallsBackToTheTailAnalysis(string change)
    {
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer, "Pierwszy");
        byte[] expected;
        switch (change)
        {
            case "urwany-wiersz":
                File.AppendAllText(path, "2026-10-07 07:59:59;Aidite 2;\"Pełny", new UTF8Encoding(false));
                expected = FileWith("Pierwszy", "Drugi");
                break;
            case "pelny-wiersz":
                File.AppendAllText(path, Line("Obcy") + Environment.NewLine, new UTF8Encoding(false));
                expected = FileWith("Pierwszy", "Obcy", "Drugi");
                break;
            default:
                File.Delete(path);
                expected = FileWith("Drugi");
                break;
        }

        Append(writer, "Drugi");
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(expected, File.ReadAllBytes(path));
        Append(writer, "Trzeci"); // i dalej szybką ścieżką
        Assert.Equal(Concat(expected, ProgramEncoding.GetBytes(Line("Trzeci") + Environment.NewLine)), File.ReadAllBytes(path));
    }

    /// <summary>
    /// Świeży HistoryWriter (jak po restarcie) dopisuje <paramref name="next"/> do pliku <paramref name="before"/>: wynik bajt w bajt
    /// z wyrocznią z regułą zer, ostrzeżenie tylko przy treści nierozpoznanej, rozpoznany plik czyta się ściśle (8 pól), a gdy zera
    /// były wyłącznie na końcu — po dopisaniu nie ma w nim ani jednego bajtu NUL.
    /// </summary>
    private static Outcome CheckZeroFill(string path, byte[] before, HistorySpec next, string label)
    {
        byte[] record = next.Bytes(Environment.NewLine);
        (byte[] expected, Outcome outcome) = ExpectedAfterAppendWithZeroFill(before, record);
        File.WriteAllBytes(path, before);
        var logs = new RecordingLoggerFactory();
        var writer = new HistoryWriter(path, new FakeClock(next.Time), logs.CreateLogger("history"));
        writer.Append(next.Event, next.Host, next.User);
        byte[] after = File.ReadAllBytes(path);
        if (!after.AsSpan().SequenceEqual(expected))
        {
            int diff = 0;
            while (diff < after.Length && diff < expected.Length && after[diff] == expected[diff]) diff++;
            Assert.Fail($"{label}: wyrocznia {outcome}, plik {before.Length} B → {after.Length} B (oczekiwano {expected.Length} B), "
                + $"pierwsza różnica w bajcie {diff}");
        }

        Assert.True(writer.PendingCount == 0 && writer.Error is null, $"{label}: wpis został w pamięci");
        int warnings = logs.Containing("nie rozpoznaje").Count;
        Assert.True((outcome == Outcome.Unrecognized) == (warnings > 0), $"{label}: wyrocznia {outcome}, ostrzeżeń {warnings}");
        if (outcome != Outcome.Unrecognized)
        {
            List<string[]>? rows = ParseRows(after, 8);
            Assert.True(rows is not null, $"{label}: po dopisaniu ({outcome}) plik nie daje się odczytać jako CSV po 8 pól");
            Assert.Equal(next.ExpectedValues, rows[^1]);
            int contentEnd = before.AsSpan().LastIndexOfAnyExcept((byte)0) + 1;
            if (before.AsSpan(0, contentEnd).IndexOf((byte)0) < 0)
                Assert.True(after.AsSpan().IndexOf((byte)0) < 0, $"{label}: w pliku zostały bajty NUL");
        }

        return outcome;
    }

    private static string Summary(Dictionary<Outcome, int> outcomes) =>
        string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}"));

    // zera NTFS po urwaniu w KAŻDYM bajcie ostatniego rekordu — także tuż po pierwszym bajcie znaku
    // wielobajtowego i tuż po cudzysłowie. Wynik bajt w bajt jak
    // dla tego samego pliku bez zer, kompletne rekordy przed urwanym nietknięte, po dopisaniu w pliku nie ma zer.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZeroFillAfterEveryByteOfTheLastRecord_GivesTheResultOfTheFileWithoutTheZeros(int seed)
    {
        var gen = new HistoryGen(6000 + seed);
        (byte[] prefix, _, _) = gen.File(bom: gen.Chance(0.75), records: gen.Next(0, 5));
        byte[] last = gen.RichSpec().Bytes(Environment.NewLine);
        HistorySpec next = gen.Spec();
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        var outcomes = new Dictionary<Outcome, int>();
        int triggers = 0;
        for (int cut = 0; cut <= last.Length; cut++)
        {
            byte[] zeros = new byte[gen.Next(1, 5000)];
            string label = $"ziarno {seed}, cięcie {cut}/{last.Length}, zer {zeros.Length}";
            if (HistoryCsvTornWriteFuzzTests.ZeroFillIsUnrecognizedAt(last, cut)) triggers++;
            Outcome outcome = CheckZeroFill(path, Concat(prefix, last[..cut], zeros), next, label);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(prefix), $"{label}: zmieniony prefiks");
        }

        output.WriteLine($"ziarno {seed}: cięć {last.Length + 1}, w tym przypadków uwagi -01 {triggers}; {Summary(outcomes)}");
        Assert.True(triggers > 0, $"ziarno {seed}: brak przypadków uwagi -01");
        Assert.Equal(0, outcomes.GetValueOrDefault(Outcome.Unrecognized));
        Assert.True(outcomes.GetValueOrDefault(Outcome.TrimmedTornTail) > 0 && outcomes.GetValueOrDefault(Outcome.KeptCompleteLooking) > 0);
    }

    // pierwszy zapis nowego pliku (BOM, nagłówek i pierwszy rekord jednym zapisem) urwany w KAŻDYM bajcie
    // i dopełniony zerami: urwany BOM/nagłówek odbudowany (także plik z samych zer), dalej jak bez zer.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZeroFillAfterEveryByteOfTheFirstWrite_GivesTheResultOfTheFileWithoutTheZeros(bool bom)
    {
        var gen = new HistoryGen(bom ? 177 : 178);
        HistorySpec first = gen.RichSpec();
        HistorySpec next = gen.Spec();
        byte[] content = Concat(bom ? Bom : [], HeaderBytes, NewLine, first.Bytes(Environment.NewLine));
        using var dir = new TempDir("history-napr");
        var outcomes = new Dictionary<Outcome, int>();
        for (int cut = 0; cut <= content.Length; cut++)
        {
            byte[] zeros = new byte[gen.Next(1, 700)];
            string label = $"BOM {bom}, cięcie {cut}/{content.Length}, zer {zeros.Length}";
            Outcome outcome = CheckZeroFill(dir.File("history.csv"), Concat(content[..cut], zeros), next, label);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        output.WriteLine($"BOM {bom}: {Summary(outcomes)}");
        Assert.True(outcomes.GetValueOrDefault(Outcome.NewHeader) >= HeaderBytes.Length);
        Assert.Equal(0, outcomes.GetValueOrDefault(Outcome.Unrecognized));
    }

    // Końcowy ciąg zer jest czytany od końca blokami po 4 KiB — granice bloków, także plik z samych zer.
    [Theory]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8192)]
    [InlineData(12289)]
    public void LongZeroFill_AcrossReadBlocks_IsTrimmedWithTheTornRecordOrRebuildsTheHeader(int zeros)
    {
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        string prefix = Header + "\r\n" + NextRecord + "\r\n";
        byte[] torn = ProgramEncoding.GetBytes("2026-10-07 07:59:59;Aidite 1;nowy;Ł")[..^1]; // urwane po pierwszym bajcie „Ł”
        File.WriteAllBytes(path, Concat(Bom, ProgramEncoding.GetBytes(prefix), torn, new byte[zeros]));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer, "Servo");
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(Concat(Bom, ProgramEncoding.GetBytes(prefix + NextRecord + Environment.NewLine)), File.ReadAllBytes(path));

        File.WriteAllBytes(path, new byte[zeros]); // nowy plik, w którym po awarii zostały same zera
        Append(new HistoryWriter(path, new FakeClock(T0)), "Servo");
        Assert.Equal(FileWith("Servo"), File.ReadAllBytes(path));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZerosInTheMiddleFollowedByCompleteRecords_StayByteForByte(int seed)
    {
        var gen = new HistoryGen(7000 + seed);
        (byte[] prefix, _, _) = gen.File(bom: true, records: gen.Next(1, 4));
        HistorySpec next = gen.Spec();
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        var outcomes = new Dictionary<Outcome, int>();
        for (int trial = 0; trial < 40; trial++)
        {
            byte[] torn = gen.RichSpec().Bytes(Environment.NewLine);
            int cut = gen.Next(0, torn.Length + 1);
            byte[] later = Concat([.. Enumerable.Range(0, gen.Next(1, 4)).Select(_ => gen.Spec().Bytes(Environment.NewLine))]);
            byte[] middle = Concat(prefix, torn[..cut], new byte[gen.Next(1, 3000)], later);
            byte[] trailing = gen.Chance(0.3) ? new byte[gen.Next(1, 700)] : [];
            string label = $"ziarno {seed}, próba {trial}, cięcie {cut}/{torn.Length}, zera na końcu {trailing.Length}";
            Outcome outcome = CheckZeroFill(path, Concat(middle, trailing), next, label);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(middle), $"{label}: zmieniony środek pliku");
        }

        output.WriteLine($"ziarno {seed}: {Summary(outcomes)}");
        Assert.True(outcomes.GetValueOrDefault(Outcome.Unrecognized) > 0);
    }

    [Theory]
    [InlineData("utf16")]
    [InlineData("wiersz-ansi-urwanie-i-zera")]
    public void UnrecognizedFile_KeepsItsTrailingZerosByteForByte(string variant)
    {
        using var dir = new TempDir("history-napr");
        string path = dir.File("history.csv");
        byte[] ansi = [.. Encoding.ASCII.GetBytes("2026-10-07 07:59:58;Aidite 1;nowy;B"), 0xB3, 0xB9,
            .. Encoding.ASCII.GetBytes("d;inny;Servo;PC-A;operator\r\n2026-10-07 07:59:59;Aidite 1;now")];
        byte[] before = variant == "utf16"
            ? Concat(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(Header + "\r\n" + NextRecord + "\r\n"))
            : Concat(Bom, ProgramEncoding.GetBytes(Header + "\r\n"), ansi, new byte[300]);
        Assert.Equal((byte)0, before[^1]);
        File.WriteAllBytes(path, before);
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer, "Servo");
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(Concat(before, ProgramEncoding.GetBytes(Environment.NewLine + NextRecord + Environment.NewLine)), File.ReadAllBytes(path));
    }

    // w seriach awarii, ściśle: zera NTFS gdziekolwiek (także po urwanym znaku i po cudzysłowie), każde
    // dopisanie bajt w bajt z wyrocznią z regułą zer; żaden kompletny rekord nie ginie ani się nie powtarza, plik zawsze rozpoznany.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RepeatedCrashesWithZeroFillAnywhere_MatchTheZeroFillOracleExactly(int seed) =>
        output.WriteLine(HistoryCsvTornWriteFuzzTests.RunRepeatedCrashes(seed, zeroFillAnywhere: true, exactOracle: true, zeroFillOracle: true));
}
