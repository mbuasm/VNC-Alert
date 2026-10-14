using System.Text;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using Xunit.Abstractions;
using static MaszynyAlert.Core.Tests.History.HistoryCsvFuzz;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryCsvTornWriteFuzzTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Seeds => Enumerable.Range(1, 8).Select(seed => new object[] { seed });

    private sealed class Stats
    {
        public Dictionary<Outcome, int> Outcomes { get; } = [];
        public int Cases { get; set; }
        public int Utf8Cuts { get; set; }
        public int CrlfCuts { get; set; }
        public int QuotedCuts { get; set; }

        public override string ToString() =>
            $"przypadki {Cases}, " + string.Join(", ", Outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}"))
            + (Utf8Cuts + CrlfCuts + QuotedCuts == 0 ? "" : $"; cięcia w środku znaku UTF-8 {Utf8Cuts}, między CR i LF {CrlfCuts}, wewnątrz cytowania {QuotedCuts}");
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDir _dir = new("history-fuzz");

        public string Path => _dir.File("history.csv");

        /// <summary>Świeży HistoryWriter (jak po restarcie programu) dopisuje <paramref name="next"/> do pliku <paramref name="before"/>.</summary>
        public (byte[] After, int Pending, string? Error, int Warnings) Append(byte[] before, HistorySpec next)
        {
            File.WriteAllBytes(Path, before);
            var logs = new RecordingLoggerFactory();
            var writer = new HistoryWriter(Path, new FakeClock(next.Time), logs.CreateLogger("history"));
            writer.Append(next.Event, next.Host, next.User);
            return (File.ReadAllBytes(Path), writer.PendingCount, writer.Error, logs.Containing("nie rozpoznaje").Count);
        }

        public void Dispose() => _dir.Dispose();
    }

    private static void Check(Harness harness, byte[] before, HistorySpec next, string label, Stats stats)
    {
        byte[] record = next.Bytes(Environment.NewLine);
        (byte[] expected, Outcome outcome) = ExpectedAfterAppend(before, record);
        (byte[] after, int pending, string? error, int warnings) = harness.Append(before, next);
        stats.Cases++;
        stats.Outcomes[outcome] = stats.Outcomes.GetValueOrDefault(outcome) + 1;

        if (!after.AsSpan().SequenceEqual(expected))
        {
            int diff = 0;
            while (diff < after.Length && diff < expected.Length && after[diff] == expected[diff]) diff++;
            Assert.Fail($"{label}: wyrocznia {outcome}, plik {before.Length} B → {after.Length} B (oczekiwano {expected.Length} B), "
                + $"pierwsza różnica w bajcie {diff}; koniec pliku przed: {Show(before[Math.Max(0, before.Length - 60)..])}");
        }

        Assert.True(pending == 0 && error is null, $"{label}: wpis został w pamięci ({pending}, {error})");
        Assert.True((outcome == Outcome.Unrecognized) == (warnings > 0),
            $"{label}: wyrocznia {outcome}, ostrzeżeń o nierozpoznanej treści {warnings}");

        // (d) Rozpoznany plik czyta się ściśle: nagłówek, zachowane rekordy bez zmian, nowy rekord z oczekiwanymi wartościami.
        if (outcome != Outcome.Unrecognized)
        {
            List<string[]>? rows = ParseRows(after, 8);
            Assert.True(rows is not null, $"{label}: po dopisaniu ({outcome}) plik nie daje się odczytać jako CSV po 8 pól");
            Assert.Equal(Header.Split(';'), rows[0]);
            Assert.Equal(next.ExpectedValues, rows[^1]);
            byte[] keptPart = after[..^record.Length];
            List<string[]> keptRows = ParseRows(keptPart, 8) ?? throw new InvalidOperationException(label + ": zachowana część");
            Assert.Equal(keptRows, rows[..^1]);
        }
    }

    private static string Show(byte[] bytes) =>
        "\"" + Encoding.UTF8.GetString(bytes).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    [Fact]
    public void Generator_WritesExactlyWhatHistoryWriterWrites()
    {
        // Bez tego fuzz sprawdzałby własny format zamiast formatu programu.
        using var harness = new Harness();
        for (int seed = 1; seed <= 20; seed++)
        {
            var gen = new HistoryGen(seed);
            File.Delete(harness.Path);
            var clock = new FakeClock();
            var writer = new HistoryWriter(harness.Path, clock);
            var specs = Enumerable.Range(0, 15).Select(_ => gen.Spec()).ToList();
            foreach (HistorySpec spec in specs)
            {
                clock.Now = spec.Time;
                writer.Append(spec.Event, spec.Host, spec.User);
            }

            byte[] expected = Concat([Bom, HeaderBytes, NewLine, .. specs.Select(s => s.Bytes(Environment.NewLine))]);
            Assert.Equal(expected, File.ReadAllBytes(harness.Path));
            List<string[]>? rows = ParseRows(expected, 8);
            Assert.NotNull(rows);
            List<string[]> expectedRows = [Header.Split(';'), .. specs.Select(s => s.ExpectedValues)];
            Assert.Equal(expectedRows, rows);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TornLastRecord_AtEveryByte_KeepsCompleteRecordsAndTrimsOnlyTheRecognizedTornTail(int seed)
    {
        var gen = new HistoryGen(seed);
        (byte[] prefix, List<HistorySpec> specs, _) = gen.File(bom: gen.Chance(0.75), records: gen.Next(0, 5));
        List<string[]> prefixRows = [Header.Split(';'), .. specs.Select(s => s.ExpectedValues)];
        Assert.Equal(prefixRows, ParseRows(prefix, 8));
        HistorySpec last = gen.RichSpec();
        byte[] lastBytes = last.Bytes(Environment.NewLine); // tak urwałby się zapis tej wersji programu
        HistorySpec next = gen.Spec();
        var stats = new Stats();
        using var harness = new Harness();
        for (int cut = 0; cut <= lastBytes.Length; cut++)
        {
            byte[] before = Concat(prefix, lastBytes[..cut]);
            Check(harness, before, next, $"ziarno {seed}, cięcie {cut}/{lastBytes.Length}", stats);
            if (cut < lastBytes.Length && (lastBytes[cut] & 0xC0) == 0x80) stats.Utf8Cuts++;
            if (cut > 0 && cut < lastBytes.Length && lastBytes[cut - 1] == '\r' && lastBytes[cut] == '\n') stats.CrlfCuts++;
            if (CsvScan.Run(lastBytes[..cut], 0, null).TailInQuotes) stats.QuotedCuts++;

            // (a) kompletne rekordy przed urwanym — bajt w bajt, zawsze.
            Assert.True(File.ReadAllBytes(harness.Path).AsSpan().StartsWith(prefix), $"ziarno {seed}, cięcie {cut}: zmieniony prefiks");
        }

        output.WriteLine($"ziarno {seed}: {stats}");
        Assert.True(stats.Utf8Cuts > 0 && stats.CrlfCuts > 0 && stats.QuotedCuts > 0, $"ziarno {seed}: słabe pokrycie: {stats}");
        Assert.True(stats.Outcomes.GetValueOrDefault(Outcome.TrimmedTornTail) > 0 && stats.Outcomes.GetValueOrDefault(Outcome.KeptCompleteLooking) > 0);
        Assert.Equal(0, stats.Outcomes.GetValueOrDefault(Outcome.Unrecognized));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TornEarlierRecord_AtRandomBytes_BehavesLikeATornLastRecord(int seed)
    {
        // Plik urwany wcześniej (np. awaria w trakcie zapisu kilku zaległych wierszy naraz): urwany rekord jest wtedy ostatni.
        var gen = new HistoryGen(1000 + seed);
        (byte[] file, _, List<int> ends) = gen.File(bom: gen.Chance(0.75), records: gen.Next(3, 9));
        HistorySpec next = gen.Spec();
        var stats = new Stats();
        using var harness = new Harness();
        for (int trial = 0; trial < 40; trial++)
        {
            int record = gen.Next(1, ends.Count); // rekord między ends[record - 1] i ends[record]
            int cut = gen.Next(ends[record - 1], ends[record] + 1);
            Check(harness, file[..cut], next, $"ziarno {seed}, rekord {record}, cięcie w bajcie {cut}", stats);
            Assert.True(File.ReadAllBytes(harness.Path).AsSpan().StartsWith(file.AsSpan(0, ends[record - 1])));
        }

        output.WriteLine($"ziarno {seed}: {stats}");
        Assert.Equal(0, stats.Outcomes.GetValueOrDefault(Outcome.Unrecognized));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TornBomHeaderOrFirstRecord_AtEveryByte_RebuildsOnlyAnIncompleteHeader(bool bom)
    {
        // Nowy plik: program pisze BOM, nagłówek i pierwszy rekord jednym zapisem — urwać się może każdy z nich.
        var gen = new HistoryGen(bom ? 77 : 78);
        HistorySpec first = gen.RichSpec();
        HistorySpec next = gen.Spec();
        byte[] content = Concat(bom ? Bom : [], HeaderBytes, NewLine, first.Bytes(Environment.NewLine));
        var stats = new Stats();
        using var harness = new Harness();
        for (int cut = 0; cut <= content.Length; cut++)
        {
            Check(harness, content[..cut], next, $"BOM {bom}, cięcie {cut}/{content.Length}", stats);
        }

        output.WriteLine(stats.ToString());
        Assert.True(stats.Outcomes.GetValueOrDefault(Outcome.NewHeader) >= HeaderBytes.Length);
        Assert.Equal(0, stats.Outcomes.GetValueOrDefault(Outcome.Unrecognized));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZeroFilledTail_AfterPowerLoss_FollowsTheSameRule(int seed)
    {
        // Po utracie zasilania NTFS potrafi zostawić rozmiar pliku bez danych — koniec pliku to zera (NUL).
        var gen = new HistoryGen(2000 + seed);
        (byte[] prefix, _, _) = gen.File(bom: true, records: gen.Next(1, 5));
        HistorySpec next = gen.Spec();
        var stats = new Stats();
        using var harness = new Harness();
        for (int trial = 0; trial < 30; trial++)
        {
            byte[] last = gen.RichSpec().Bytes(Environment.NewLine);
            int cut = gen.Next(0, last.Length + 1);
            // Zera tuż po urwanym znaku wielobajtowym albo po cudzysłowie (HistoryCsvWerUwagiTests).
            while (ZeroFillIsUnrecognizedAt(last, cut)) cut--;
            byte[] zeros = new byte[gen.Next(1, 5000)];
            byte[] before = Concat(prefix, last[..cut], zeros);
            string label = $"ziarno {seed}, próba {trial}, cięcie {cut}, zer {zeros.Length}";
            byte[] record = next.Bytes(Environment.NewLine);
            Outcome outcome = ExpectedAfterAppend(before, record).Outcome;
            if (outcome == Outcome.KeptCompleteLooking)
            {
                // Urwany rekord z kompletem pól, a za nim zera: tu tylko niezmienniki kontraktu.
                (byte[] after, int pending, string? error, _) = harness.Append(before, next);
                stats.Cases++;
                stats.Outcomes[outcome] = stats.Outcomes.GetValueOrDefault(outcome) + 1;
                Assert.True(pending == 0 && error is null, $"{label}: wpis został w pamięci");
                Assert.True(after.AsSpan().EndsWith(record), $"{label}: nowy wiersz nie jest na końcu pliku");
                List<string[]>? rows = ParseRows(after, 8);
                Assert.True(rows is not null, $"{label}: plik nie daje się odczytać jako CSV po 8 pól");
                Assert.Equal(next.ExpectedValues, rows[^1]);
            }
            else
            {
                Check(harness, before, next, label, stats);
            }

            Assert.True(File.ReadAllBytes(harness.Path).AsSpan().StartsWith(prefix));
        }

        output.WriteLine($"ziarno {seed}: {stats}");
        Assert.Equal(0, stats.Outcomes.GetValueOrDefault(Outcome.Unrecognized));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void OldVersionGlue_TornRecordFollowedByLaterRecords_KeepsEveryByteBeforeTheNewTornTail(int seed)
    {
        var gen = new HistoryGen(3000 + seed);
        var stats = new Stats();
        using var harness = new Harness();
        for (int trial = 0; trial < 30; trial++)
        {
            (byte[] file, _, List<int> ends) = gen.File(bom: gen.Chance(0.75), records: gen.Next(3, 8));
            int record = gen.Next(1, ends.Count - 1);
            int cut = gen.Next(ends[record - 1] + 1, ends[record]);
            byte[] glued = Concat(file[..cut], file[ends[record]..]);
            byte[] newTear = gen.Chance(0.5) ? gen.RichSpec().Bytes(Environment.NewLine) : [];
            byte[] newTearPiece = newTear.Length == 0 ? [] : newTear[..gen.Next(1, newTear.Length)];
            byte[] before = Concat(glued, newTearPiece);
            HistorySpec next = gen.Spec();
            string label = $"ziarno {seed}, próba {trial}, sklejenie w bajcie {cut}, nowe urwanie {newTearPiece.Length} B";
            Outcome outcome = ExpectedAfterAppend(before, next.Bytes(Environment.NewLine)).Outcome; // tylko statystyka
            stats.Cases++;
            stats.Outcomes[outcome] = stats.Outcomes.GetValueOrDefault(outcome) + 1;
            (byte[] after, int pending, string? error, _) = harness.Append(before, next);

            // Niezmienniki kontraktu (bez przesądzania, czy nowy urwany kawałek zostaje): (a) nic sprzed niego nie znika,
            // (c) nowy wiersz jest na końcu i zaczyna się od nowej linii; zapis nie czeka w pamięci.
            Assert.True(pending == 0 && error is null, $"{label}: wpis został w pamięci");
            Assert.True(after.AsSpan().StartsWith(glued), $"{label}: zniknęły bajty sprzed nowego urwania");
            byte[] record8 = next.Bytes(Environment.NewLine);
            Assert.True(after.AsSpan().EndsWith(record8), $"{label}: nowy wiersz nie jest na końcu pliku");
            Assert.True(after[^(record8.Length + 1)] is (byte)'\n' or (byte)'\r', $"{label}: nowy wiersz nie od nowej linii");
            Assert.True(after.Length - record8.Length >= glued.Length, label);
        }

        output.WriteLine($"ziarno {seed}: {stats}");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RepeatedCrashes_AppendTearAppend_NeverLoseOrDuplicateACompleteRecord(int seed)
    {
        // (f) Seria: restart → dopisanie 1–3 wierszy (osobno albo zaległe jednym zapisem) → urwanie w tym, co dopisano
        // (czasem z zerami NTFS) → restart… Kompletny rekord, który raz trafił na dysk, musi być w pliku do końca, jeden raz.
        output.WriteLine(RunRepeatedCrashes(seed, zeroFillAnywhere: false, exactOracle: true));
    }

    /// <summary>
    /// Czy zera NTFS dopisane po urwaniu w bajcie <paramref name="cut"/> trafiają w przypadek: tuż po
    /// pierwszych bajtach znaku wielobajtowego (niepoprawny UTF-8) albo tuż po cudzysłowie (dane po zamknięciu cytowania).
    /// </summary>
    internal static bool ZeroFillIsUnrecognizedAt(byte[] bytes, int cut) =>
        cut > 0 && ((cut < bytes.Length && (bytes[cut] & 0xC0) == 0x80) || bytes[cut - 1] == '"');

    /// <summary>
    /// Seria awarii dla ziarna. <paramref name="zeroFillAnywhere"/> — zera NTFS także w przypadkach
    /// (<see cref="ZeroFillIsUnrecognizedAt"/>); <paramref name="exactOracle"/> — każde dopisanie porównywane bajt w bajt z wyrocznią;
    /// <paramref name="zeroFillOracle"/> — wyrocznią z regułą końcowych zer (<see cref="HistoryCsvFuzz.ExpectedAfterAppendWithZeroFill"/>), bez wyjątku dla urwanego rekordu z kompletem pól i zerami za nim.
    /// </summary>
    internal static string RunRepeatedCrashes(int seed, bool zeroFillAnywhere, bool exactOracle, bool zeroFillOracle = false)
    {
        var gen = new HistoryGen(4000 + seed);
        var stats = new Stats();
        using var harness = new Harness();
        var mustExist = new HashSet<int>();
        var mayExist = new HashSet<int>(); // urwane rekordy, które wyglądały na kompletne i zostały w pliku
        int? torn = null;
        bool zeroFilled = false;
        byte[] file = gen.Chance(0.5) ? [] : gen.File(bom: true, records: 0).Bytes;
        File.WriteAllBytes(harness.Path, file);
        var clock = new FakeClock();
        for (int round = 0; round < 30; round++)
        {
            var batch = Enumerable.Range(0, gen.Next(1, 4)).Select(_ => Marked(gen.Spec())).ToList();
            byte[] batchBytes = Concat([.. batch.Select(s => s.Bytes(Environment.NewLine))]);
            (byte[] expected, Outcome outcome) = zeroFillOracle ? ExpectedAfterAppendWithZeroFill(file, batchBytes) : ExpectedAfterAppend(file, batchBytes);
            stats.Cases++;
            stats.Outcomes[outcome] = stats.Outcomes.GetValueOrDefault(outcome) + 1;
            if (torn is { } id && outcome is Outcome.KeptCompleteLooking or Outcome.Unrecognized) mayExist.Add(id);
            torn = null;

            var writer = new HistoryWriter(harness.Path, clock); // restart programu
            if (batch.Count > 1 && gen.Chance(0.5))
            {
                using (new FileStream(harness.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (HistorySpec spec in batch) Write(writer, clock, spec);
                    Assert.Equal(batch.Count, writer.PendingCount);
                }

                writer.Flush(); // zaległe wiersze jednym zapisem
            }
            else
            {
                foreach (HistorySpec spec in batch) Write(writer, clock, spec);
            }

            file = File.ReadAllBytes(harness.Path);
            // Urwany rekord z kompletem pól, a za nim zera: może zera usunąć — wtedy tylko niezmienniki
            // (chyba że wyrocznia zna regułę zer).
            if (exactOracle && (zeroFillOracle || !(zeroFilled && outcome == Outcome.KeptCompleteLooking)))
                Assert.True(file.AsSpan().SequenceEqual(expected), $"ziarno {seed}, runda {round}: wynik inny niż wyrocznia ({outcome})");
            Assert.True(file.AsSpan().EndsWith(batchBytes), $"ziarno {seed}, runda {round}: dopisane wiersze nie są na końcu");
            Assert.Equal(0, writer.PendingCount);

            // Urwanie w dopisanych bajtach: rekordy przed miejscem urwania są kompletne na dysku.
            int batchStart = file.Length - batchBytes.Length;
            int tear = gen.Chance(0.85) ? gen.Next(batchStart, file.Length + 1) : file.Length;
            int at = batchStart;
            foreach (HistorySpec spec in batch)
            {
                int end = at + spec.Bytes(Environment.NewLine).Length;
                bool crOnlyLeft = Environment.NewLine == "\r\n" && tear == end - 1; // brakuje tylko LF — rekord kompletny
                if (end <= tear || crOnlyLeft) mustExist.Add(spec.Id);
                else if (at < tear) torn = spec.Id; // urwany: zostaje tylko, jeśli wygląda na kompletny
                at = end;
            }

            // Zera po urwanym znaku wielobajtowym albo po cudzysłowie (HistoryCsvWerUwagiTests).
            bool zeroFillAllowed = zeroFillAnywhere || !ZeroFillIsUnrecognizedAt(file, tear);
            file = file[..tear];
            zeroFilled = zeroFillAllowed && gen.Chance(0.25);
            if (zeroFilled) file = Concat(file, new byte[gen.Next(1, 700)]);
            File.WriteAllBytes(harness.Path, file);
        }

        // Ostatnie dopisanie domyka serię, potem cały plik czyta się ściśle.
        HistorySpec closing = Marked(gen.Spec());
        (byte[] closingExpected, Outcome closingOutcome) = zeroFillOracle
            ? ExpectedAfterAppendWithZeroFill(file, closing.Bytes(Environment.NewLine))
            : ExpectedAfterAppend(file, closing.Bytes(Environment.NewLine));
        if (torn is { } last && closingOutcome is Outcome.KeptCompleteLooking or Outcome.Unrecognized) mayExist.Add(last);
        var final = new HistoryWriter(harness.Path, clock);
        Write(final, clock, closing);
        mustExist.Add(closing.Id);
        if (exactOracle && (zeroFillOracle || !(zeroFilled && closingOutcome == Outcome.KeptCompleteLooking)))
            Assert.Equal(closingExpected, File.ReadAllBytes(harness.Path));
        List<string[]>? rows = ParseRows(File.ReadAllBytes(harness.Path), 8);
        Assert.True(rows is not null, $"ziarno {seed}: po serii awarii plik nie daje się odczytać jako CSV po 8 pól; {stats}");
        var markers = rows.Skip(1).Select(r => Marker(r[3])).ToList();
        Assert.Equal(markers.Count, markers.Distinct().Count()); // bez duplikatów
        Assert.Empty(mustExist.Except(markers));                   // żaden kompletny rekord nie zginął
        Assert.Empty(markers.Except(mustExist).Except(mayExist));  // i nie pojawił się żaden obcy
        if (exactOracle) Assert.Equal(0, stats.Outcomes.GetValueOrDefault(Outcome.Unrecognized));
        return $"ziarno {seed}: rekordów {markers.Count}, kompletnych na dysku {mustExist.Count}; {stats}";
    }

    private static HistorySpec Marked(HistorySpec spec) => spec with { ShortText = spec.ShortText + " #" + spec.Id };

    private static int Marker(string shortText) =>
        int.Parse(shortText[(shortText.LastIndexOf('#') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    private static void Write(HistoryWriter writer, FakeClock clock, HistorySpec spec)
    {
        clock.Now = spec.Time;
        writer.Append(spec.Event, spec.Host, spec.User);
    }
}
