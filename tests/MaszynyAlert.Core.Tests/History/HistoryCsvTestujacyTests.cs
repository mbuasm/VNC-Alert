using System.Text;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using Xunit.Abstractions;
using static MaszynyAlert.Core.Tests.History.HistoryCsvFuzz;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryCsvTestujacyTests(ITestOutputHelper output)
{
    /// <summary>
    /// Nowe ziarna. Lokalnie HISTORIA_TESTUJACY_ZIARNA=&lt;n&gt; uruchamia n ziaren i PEŁNĄ macierz (64 KiB zer przy każdym cięciu);
    /// domyślnie (CI) 2 ziarna, 64 KiB co 8. cięcie — otwarcie świeżo zapisanego pliku 64 KiB czeka na skan antywirusa (~15 ms).
    /// </summary>
    public static IEnumerable<object[]> Seeds => Enumerable.Range(12_101, ExplorationSeeds ?? 2).Select(seed => new object[] { seed });

    private static int? ExplorationSeeds =>
        int.TryParse(Environment.GetEnvironmentVariable("HISTORIA_TESTUJACY_ZIARNA"), out int n) && n > 0 ? n : null;

    private static readonly int[] ZeroRuns = [0, 1, 511, 4096, 65536];

    /// <summary>Długości zer dla cięcia: 0/1/511/4096 zawsze, 64 KiB przy każdym cięciu w eksploracji, inaczej co 8. cięcie.</summary>
    private static IEnumerable<int> ZeroRunsFor(int cut) => ZeroRuns.Where(z => z < 65536 || ExplorationSeeds is not null || cut % 8 == 0);

    private static readonly Regex RecordStart =
        new("[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2};", RegexOptions.CultureInvariant);

    private enum Kind { NewHeader, Appended, KeptCompleteLooking, TrimmedTornTail, Unrecognized }

    /// <summary>Wiersz zbudowany pole po polu, ze stanem po każdym prefiksie (indeks listy = długość prefiksu w bajtach).</summary>
    private sealed class BuiltLine
    {
        public List<byte> Content { get; } = [];
        public byte[] Terminator { get; init; } = [];
        public List<int> Fields { get; } = [1];
        public List<bool> InQuotes { get; } = [false];
        public List<bool> AfterQuote { get; } = [false];
        public List<bool> MidChar { get; } = [false];
        public HashSet<int> QuotedFieldStarts { get; } = [];

        public void Add(byte value, int fields, bool inQuotes, bool afterQuote, bool midChar)
        {
            Content.Add(value);
            Fields.Add(fields);
            InQuotes.Add(inQuotes);
            AfterQuote.Add(afterQuote);
            MidChar.Add(midChar);
        }

        /// <summary>Prefiks <paramref name="p"/> bajtów wygląda na kompletny rekord: 8 pól, cudzysłów zamknięty, cały znak UTF-8.</summary>
        public bool LooksComplete(int p) => Fields[p] == 8 && !InQuotes[p] && !MidChar[p];

        /// <summary>Zera wstawione po <paramref name="p"/> bajtach psują wiersz: środek znaku UTF-8, tuż po cudzysłowie, przed cudzysłowem otwierającym.</summary>
        public bool ZerosBreakAt(int p) => MidChar[p] || AfterQuote[p] || QuotedFieldStarts.Contains(p);
    }

    private sealed class BuiltFile
    {
        public required byte[] BomBytes { get; init; }
        public required byte[] HeaderTerminator { get; init; }
        public List<BuiltLine> Records { get; } = [];
        public int HeaderContentEnd => BomBytes.Length + HeaderBytes.Length;
        public int HeaderEnd => HeaderContentEnd + HeaderTerminator.Length;
        public int Start(int j) => HeaderEnd + Records.Take(j).Sum(r => r.Content.Count + r.Terminator.Length);
        public byte[] Bytes => Concat([BomBytes, HeaderBytes, HeaderTerminator, .. Records.Select(r => Concat([.. r.Content], r.Terminator))]);
    }

    private static BuiltLine BuildRecord(HistorySpec spec, string terminator)
    {
        var line = new BuiltLine { Terminator = ProgramEncoding.GetBytes(terminator) };
        string[] values = [.. spec.RawFields.Select(Protect)];
        byte[] utf8 = new byte[4];
        for (int i = 0; i < values.Length; i++)
        {
            int field = i + 1;
            if (i > 0) line.Add((byte)';', field, false, false, false);
            bool quoted = values[i].AsSpan().IndexOfAny(";\"\n\r") >= 0;
            if (quoted)
            {
                line.QuotedFieldStarts.Add(line.Content.Count);
                line.Add((byte)'"', field, true, false, false);
            }

            foreach (Rune rune in values[i].EnumerateRunes()) // samotny surogat = U+FFFD, jak koder programu
            {
                if (quoted && rune.Value == '"')
                {
                    line.Add((byte)'"', field, false, true, false); // pierwsza połowa pary "" — chwilowo poza cytowaniem
                    line.Add((byte)'"', field, true, false, false);
                    continue;
                }

                int length = rune.EncodeToUtf8(utf8);
                for (int k = 0; k < length; k++) line.Add(utf8[k], field, quoted, false, k < length - 1);
            }

            if (quoted) line.Add((byte)'"', field, false, true, false);
        }

        Assert.Equal(ProgramEncoding.GetBytes(spec.Line), line.Content.ToArray());
        return line;
    }

    private static BuiltFile NewFile(HistoryGen gen, int records, bool timestampInLast)
    {
        var file = new BuiltFile { BomBytes = gen.Chance(0.7) ? Bom : [], HeaderTerminator = ProgramEncoding.GetBytes(gen.Terminator()) };
        for (int i = 0; i < records; i++) file.Records.Add(BuildRecord(gen.Spec(), gen.Terminator()));
        HistorySpec last = gen.RichSpec();
        if (timestampInLast)
        {
            // Odczyt HMI ze znacznikiem czasu i średnikiem: początek „kolejnego rekordu” wewnątrz urwanego pola.
            last = last with { FullText = gen.Text(3, true) + " Odczyt HMI 2026-10-07 07:58:00;E-STOP\r\nlinia " + gen.Text(2, false) };
        }

        file.Records.Add(BuildRecord(last, Environment.NewLine)); // ostatni rekord tak, jak zapisuje go ta wersja programu
        return file;
    }

    private static byte[] SeparatorAfter(byte[] kept) =>
        kept.Length == 0 || kept[^1] == (byte)'\n' ? [] : kept[^1] == (byte)'\r' ? [(byte)'\n'] : NewLine;

    /// <summary>Czy cięcie po <paramref name="cut"/> bajtach wypada na końcu wiersza (także między CR i LF): wszystko przed nim kompletne.</summary>
    private static bool IsLineBoundary(BuiltFile file, int cut)
    {
        if (cut == file.HeaderEnd || (file.HeaderTerminator.Length == 2 && cut == file.HeaderContentEnd + 1)) return true;
        for (int j = 0; j < file.Records.Count; j++)
        {
            int contentEnd = file.Start(j) + file.Records[j].Content.Count;
            int end = contentEnd + file.Records[j].Terminator.Length;
            if (cut == end || (file.Records[j].Terminator.Length == 2 && cut == contentEnd + 1)) return true;
        }

        return false;
    }

    /// <summary>Czy zera wstawione przed bajtem <paramref name="at"/> pliku psują jego rozpoznanie (wszystko przed pierwszym rekordem psuje).</summary>
    private static bool MiddleZerosBreak(BuiltFile file, int at)
    {
        if (at < file.HeaderEnd) return true; // przed BOM, w BOM, w nagłówku, przed jego końcem linii, między CR i LF
        for (int j = 0; j < file.Records.Count; j++)
        {
            int start = file.Start(j);
            BuiltLine line = file.Records[j];
            int contentEnd = start + line.Content.Count;
            if (at == start) return false;                              // początek wiersza: zera należą do pierwszego pola
            if (at <= contentEnd) return line.ZerosBreakAt(at - start);  // w treści wiersza
            if (at < contentEnd + line.Terminator.Length) return true;   // między CR i LF
        }

        throw new InvalidOperationException("zera za końcem pliku");
    }

    /// <summary>
    /// Wyrocznia konstrukcyjna. Plik = <paramref name="file"/> urwany po <paramref name="cut"/> bajtach, z <paramref name="middleZeros"/>
    /// zerami wstawionymi przed bajtem <paramref name="middleAt"/> (&lt; cut) i <paramref name="trailingZeros"/> zerami na końcu.
    /// </summary>
    private static (byte[] Physical, byte[] Expected, Kind Kind) Model(
        BuiltFile file, int cut, int middleAt, int middleZeros, int trailingZeros, byte[] record)
    {
        byte[] original = file.Bytes;
        byte[] logical = middleZeros > 0 ? Concat(original[..middleAt], new byte[middleZeros], original[middleAt..cut]) : original[..cut];
        byte[] physical = Concat(logical, new byte[trailingZeros]);
        if (middleZeros > 0 && MiddleZerosBreak(file, middleAt))
            return (physical, Concat(physical, SeparatorAfter(physical), record), Kind.Unrecognized);
        if (cut < file.HeaderContentEnd) return (physical, Concat(Bom, HeaderBytes, NewLine, record), Kind.NewHeader);
        if (cut == file.HeaderContentEnd) return (physical, Concat(logical, NewLine, record), Kind.Appended);
        if (IsLineBoundary(file, cut)) return (physical, Concat(logical, SeparatorAfter(logical), record), Kind.Appended);

        int j = Enumerable.Range(0, file.Records.Count).Last(i => file.Start(i) < cut);
        BuiltLine line = file.Records[j];
        int p = cut - file.Start(j);
        int tornStart = file.Start(j) + (middleZeros > 0 && middleAt < file.Start(j) ? middleZeros : 0);
        if (line.LooksComplete(p)) return (physical, Concat(logical, NewLine, record), Kind.KeptCompleteLooking);
        if (RecordStart.IsMatch(Encoding.Latin1.GetString(logical, tornStart + 1, logical.Length - tornStart - 1)))
            return (physical, Concat(physical, SeparatorAfter(physical), record), Kind.Unrecognized);
        byte[] kept = logical[..tornStart];
        return (physical, Concat(kept, SeparatorAfter(kept), record), Kind.TrimmedTornTail);
    }

    /// <summary>Koniec ostatniego kompletnego wiersza przed cięciem — w bajtach pliku fizycznego (z zerami w środku).</summary>
    private static int CompletePrefix(BuiltFile file, int cut, int middleAt, int middleZeros)
    {
        int end = 0;
        if (cut >= file.HeaderEnd) end = file.HeaderEnd;
        for (int j = 0; j < file.Records.Count; j++)
        {
            int recordEnd = file.Start(j) + file.Records[j].Content.Count + file.Records[j].Terminator.Length;
            if (recordEnd <= cut) end = recordEnd;
        }

        return end + (middleZeros > 0 && middleAt < end ? middleZeros : 0);
    }

    private static Kind Check(string path, BuiltFile file, int cut, int middleAt, int middleZeros, int trailingZeros, HistorySpec next, string label)
    {
        byte[] record = next.Bytes(Environment.NewLine);
        (byte[] physical, byte[] expected, Kind kind) = Model(file, cut, middleAt, middleZeros, trailingZeros, record);
        File.WriteAllBytes(path, physical);
        var logs = new RecordingLoggerFactory();
        var writer = new HistoryWriter(path, new FakeClock(next.Time), logs.CreateLogger("history"));
        writer.Append(next.Event, next.Host, next.User);
        byte[] after = File.ReadAllBytes(path);
        if (!after.AsSpan().SequenceEqual(expected))
        {
            int diff = 0;
            while (diff < after.Length && diff < expected.Length && after[diff] == expected[diff]) diff++;
            Assert.Fail($"{label}: model {kind}, plik {physical.Length} B → {after.Length} B (oczekiwano {expected.Length} B), pierwsza różnica w bajcie {diff}");
        }

        Assert.True(writer.PendingCount == 0 && writer.Error is null, $"{label}: wpis został w pamięci");
        Assert.True((kind == Kind.Unrecognized) == (logs.Containing("nie rozpoznaje").Count > 0), $"{label}: model {kind}, ostrzeżenie niezgodne");

        // Niezależnie od modelu: kompletne wiersze sprzed cięcia bajt w bajt, nowy rekord na końcu i od nowej linii.
        Assert.True(after.AsSpan().StartsWith(physical.AsSpan(0, CompletePrefix(file, cut, middleAt, middleZeros))), $"{label}: zmieniony kompletny prefiks");
        Assert.True(after.AsSpan().EndsWith(record) && after[^(record.Length + 1)] == (byte)'\n', $"{label}: nowy rekord nie od nowej linii");
        if (kind != Kind.Unrecognized)
        {
            List<string[]>? rows = ParseRows(after, 8);
            Assert.True(rows is not null, $"{label}: plik rozpoznany ({kind}) nie czyta się ściśle po 8 pól");
            Assert.Equal(next.ExpectedValues, rows[^1]);
            if (middleZeros == 0) Assert.True(after.AsSpan().IndexOf((byte)0) < 0, $"{label}: w pliku rozpoznanym zostały bajty NUL");
        }

        return kind;
    }

    private static string Summary(Dictionary<Kind, int> kinds) => string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));

    // (1) Cięcie w KAŻDYM bajcie całego pliku (BOM, nagłówek, każdy rekord), a za nim 0 / 1 / 511 / 4096 / 65 536 bajtów zer.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryCutOfTheWholeFile_WithZeroRunsUpTo64KiB_MatchesTheConstructionModel(int seed)
    {
        var gen = new HistoryGen(seed);
        BuiltFile file = NewFile(gen, records: gen.Next(1, 3), timestampInLast: seed % 2 == 0);
        HistorySpec next = gen.Spec();
        byte[] original = file.Bytes;
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var kinds = new Dictionary<Kind, int>();
        int midChar = 0, quoted = 0, cases = 0;
        for (int cut = 0; cut <= original.Length; cut++)
        {
            foreach (int zeros in ZeroRunsFor(cut))
            {
                Kind kind = Check(path, file, cut, 0, 0, zeros, next, $"ziarno {seed}, cięcie {cut}/{original.Length}, zer {zeros}");
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                cases++;
            }

            if (cut < original.Length && (original[cut] & 0xC0) == 0x80) midChar++;
            int last = file.Start(file.Records.Count - 1);
            if (cut > last && file.Records[^1].InQuotes[Math.Min(cut - last, file.Records[^1].Content.Count)]) quoted++;
        }

        output.WriteLine($"ziarno {seed}: plik {original.Length} B, przypadków {cases}, cięć w środku znaku {midChar}, "
            + $"w cytowaniu ostatniego rekordu {quoted}; {Summary(kinds)}");
        Assert.True(midChar > 0 && quoted > 0, $"ziarno {seed}: słabe pokrycie");
        Assert.True(kinds.GetValueOrDefault(Kind.NewHeader) > 0 && kinds.GetValueOrDefault(Kind.TrimmedTornTail) > 0
            && kinds.GetValueOrDefault(Kind.KeptCompleteLooking) > 0 && kinds.GetValueOrDefault(Kind.Appended) > 0, Summary(kinds));
        if (seed % 2 == 0) Assert.True(kinds.GetValueOrDefault(Kind.Unrecognized) > 0, $"ziarno {seed}: wariant ze znacznikiem czasu bez nierozpoznanych");
        else Assert.Equal(0, kinds.GetValueOrDefault(Kind.Unrecognized));
    }

    // (2) Zera W ŚRODKU w każdym bajcie pliku (przed BOM, w nagłówku, na początku wiersza, w polu, w środku znaku, po cudzysłowie,
    // przed cudzysłowem otwierającym, między CR i LF) plus zera na końcu i często urwany ostatni rekord.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZerosInTheMiddleAtEveryByte_PlusTrailingZeros_MatchTheConstructionModel(int seed)
    {
        var gen = new HistoryGen(seed + 500);
        BuiltFile file = NewFile(gen, records: 2, timestampInLast: false);
        HistorySpec next = gen.Spec();
        byte[] original = file.Bytes;
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var kinds = new Dictionary<Kind, int>();
        for (int at = 0; at < original.Length; at++)
        {
            int cut = gen.Chance(0.5) ? original.Length : gen.Next(at + 1, original.Length + 1);
            int middle = gen.Pick(new[] { 1, 7, 4096 });
            int trailing = gen.Pick(ZeroRuns);
            if (trailing == 65536 && ExplorationSeeds is null && at % 8 != 0) trailing = 4096;
            Kind kind = Check(path, file, cut, at, middle, trailing, next, $"ziarno {seed}, zera {middle} B przed bajtem {at}, cięcie {cut}/{original.Length}, zer na końcu {trailing}");
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }

        output.WriteLine($"ziarno {seed}: plik {original.Length} B; {Summary(kinds)}");
        Assert.True(kinds.GetValueOrDefault(Kind.Unrecognized) > 0 && kinds.GetValueOrDefault(Kind.TrimmedTornTail) > 0, Summary(kinds));
    }

    // (3) Plik zaczynający się BOM i same zera, urwany BOM, sam urwany nagłówek (z BOM i bez) — z zerami każdej długości: nagłówek
    // odbudowany; pełny nagłówek bez końca linii albo z samym CR + zera: dopisanie od nowej linii; BOM + zera + reszta pliku = zera
    // w środku, plik nierozpoznany, bajt w bajt.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void BomOrTornHeaderFollowedByZeros_RebuildsTheHeader_AndZerosRightAfterTheBomKeepTheFile(int zeros)
    {
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_999);
        HistorySpec next = gen.Spec();
        byte[] record = next.Bytes(Environment.NewLine);
        byte[] fresh = Concat(Bom, HeaderBytes, NewLine, record);
        byte[] AppendTo(byte[] before)
        {
            File.WriteAllBytes(path, before);
            var writer = new HistoryWriter(path, new FakeClock(next.Time));
            writer.Append(next.Event, next.Host, next.User);
            Assert.True(writer.PendingCount == 0 && writer.Error is null);
            return File.ReadAllBytes(path);
        }

        foreach (byte[] start in new[] { Bom[..0], Bom[..1], Bom[..2], Bom }) Assert.Equal(fresh, AppendTo(Concat(start, new byte[zeros])));
        foreach (byte[] start in new[] { Bom[..0], Bom }) // urwany BOM, a za nim bajty nagłówka, to już nie „urwany nagłówek”
        {
            for (int h = 1; h < HeaderBytes.Length; h++) Assert.Equal(fresh, AppendTo(Concat(start, HeaderBytes[..h], new byte[zeros])));
        }

        Assert.Equal(Concat(Bom, HeaderBytes, NewLine, record), AppendTo(Concat(Bom, HeaderBytes, new byte[zeros])));
        Assert.Equal(Concat(HeaderBytes, NewLine, record), AppendTo(Concat(HeaderBytes, new byte[zeros])));
        Assert.Equal(Concat(Bom, HeaderBytes, "\r\n"u8.ToArray(), record), AppendTo(Concat(Bom, HeaderBytes, "\r"u8.ToArray(), new byte[zeros])));

        byte[] rest = Concat(HeaderBytes, "\r\n"u8.ToArray(), gen.Spec().Bytes("\r\n"));
        byte[] afterBom = Concat(Bom, new byte[Math.Max(1, zeros)], rest); // zera W ŚRODKU: za nimi nagłówek i rekord
        Assert.Equal(Concat(afterBom, record), AppendTo(afterBom));
    }

    // (4) Bardzo długi ciąg zer (1 MiB) za urwanym rekordem i plik z samych zer 1 MiB: odczyt od końca przez 256 bloków.
    [Fact]
    public void OneMebibyteOfZeros_AfterATornRecordOrAlone_IsTreatedAsTheTornTail()
    {
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_998);
        byte[] complete = Concat(Bom, HeaderBytes, "\r\n"u8.ToArray(), gen.Spec().Bytes("\r\n"));
        byte[] torn = Encoding.UTF8.GetBytes("2026-10-07 07:59:59;Aidite 1;nowy;Servo;inny;\"Pełny\r\ntekst"); // otwarte cytowanie
        HistorySpec next = gen.Spec();
        byte[] record = next.Bytes(Environment.NewLine);
        foreach ((byte[] before, byte[] expected) in new[]
        {
            (Concat(complete, torn, new byte[1 << 20]), Concat(complete, record)),
            (new byte[1 << 20], Concat(Bom, HeaderBytes, NewLine, record)),
        })
        {
            File.WriteAllBytes(path, before);
            var writer = new HistoryWriter(path, new FakeClock(next.Time));
            writer.Append(next.Event, next.Host, next.User);
            Assert.True(writer.PendingCount == 0 && writer.Error is null);
            Assert.Equal(expected, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void UnrecognizedFileWithTrailingZeros_StaysByteForByteAcrossRestarts()
    {
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_997);
        byte[] ansi = [.. Encoding.ASCII.GetBytes("2026-10-07 07:59:58;Aidite 1;nowy;B"), 0xB3, 0xB9, .. Encoding.ASCII.GetBytes("d;inny;Servo;PC-A;operator\r\n")];
        byte[] before = Concat(Bom, HeaderBytes, "\r\n"u8.ToArray(), ansi, gen.RichSpec().Bytes("\r\n")[..25], new byte[777]);
        File.WriteAllBytes(path, before);
        byte[] expected = Concat(before, NewLine);
        for (int restart = 0; restart < 3; restart++)
        {
            HistorySpec next = gen.Spec();
            var logs = new RecordingLoggerFactory();
            var writer = new HistoryWriter(path, new FakeClock(next.Time), logs.CreateLogger("history"));
            writer.Append(next.Event, next.Host, next.User);
            expected = Concat(expected, next.Bytes(Environment.NewLine));
            Assert.True(writer.PendingCount == 0 && writer.Error is null);
            Assert.Single(logs.Containing("nie rozpoznaje"));
            Assert.Equal(expected, File.ReadAllBytes(path));
        }
    }

    // (6) Znak U+0000 w treści pola program zapisuje jako bajt 0:
    // zero w ŚRODKU rekordu nie psuje rozpoznania po restarcie — plik dalej rozpoznany, bez ostrzeżenia, bajt w bajt.
    [Fact]
    public void NulCharacterInsideAField_KeepsTheFileRecognizedAfterRestart()
    {
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_996);
        HistorySpec withNul = gen.Spec() with { ShortText = "Servo\0alarm" };
        var first = new HistoryWriter(path, new FakeClock(withNul.Time));
        first.Append(withNul.Event, withNul.Host, withNul.User);
        byte[] written = File.ReadAllBytes(path);
        Assert.Contains((byte)0, written);

        HistorySpec next = gen.Spec();
        var logs = new RecordingLoggerFactory();
        var writer = new HistoryWriter(path, new FakeClock(next.Time), logs.CreateLogger("history"));
        writer.Append(next.Event, next.Host, next.User);
        Assert.Empty(logs.Containing("nie rozpoznaje"));
        Assert.Equal(Concat(written, next.Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }

    // ---------------- granice szybkiej ścieżki ----------------

    private static void Write(HistoryWriter writer, HistorySpec spec) => writer.Append(spec.Event, spec.Host, spec.User);

    /// <summary>Uchwyt innego programu, który wpuszcza wyłącznie otwarcia BEZ prawa odczytu (dowód, że dopisanie nie czytało pliku).</summary>
    private static FileStream WriteOnlySharing(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Write);

    /// <summary>Zmiana „innego programu” w miejscu (ten sam plik, ten sam czas utworzenia), po upływie taktu zegara znaczników.</summary>
    private static void RewriteInPlace(string path, byte[] content)
    {
        Thread.Sleep(50); // znaczniki NTFS mają ziarno taktu zegara (≤ 15,6 ms); zmiana w tym samym takcie — test Limitation niżej
        using var other = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        other.SetLength(0);
        other.Write(content);
    }

    // (7) Zmiana pliku przez inny program między dopisaniami — każda kończy się pełną analizą końca (obcięcie, ta sama długość z inną
    // treścią, podmiana przez zmianę nazwy, usunięcie i ponowne utworzenie, zera NTFS tej samej długości, obcy pełny wiersz).
    // Po niej kolejne dopisanie znów idzie szybką ścieżką (bez prawa odczytu — sprawdzane uchwytem, który odczytu nie wpuszcza).
    [Theory]
    [InlineData("obciecie-do-rekordu")]
    [InlineData("obciecie-w-rekordzie")]
    [InlineData("obciecie-do-zera")]
    [InlineData("obciecie-w-naglowku")]
    [InlineData("ta-sama-dlugosc-urwany-koniec")]
    [InlineData("ta-sama-dlugosc-zera-ntfs")]
    [InlineData("ta-sama-dlugosc-podmiana-przez-zmiane-nazwy")]
    [InlineData("ta-sama-dlugosc-usuniety-i-utworzony")]
    [InlineData("obcy-pelny-wiersz")]
    public void ExternalChangeBetweenAppends_AlwaysEndsInTheTailAnalysis(string change)
    {
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_990);
        HistorySpec a = gen.Spec(), b = gen.RichSpec(), c = gen.Spec(), d = gen.Spec(), foreign = gen.Spec();
        var writer = new HistoryWriter(path, new FakeClock(a.Time)); // zegar stoi: każdy wiersz ma czas a
        Write(writer, a);
        Write(writer, b); // szybka ścieżka; znacznik zapamiętany
        byte[] headerPart = Concat(Bom, HeaderBytes, NewLine);
        byte[] aBytes = a.Bytes(Environment.NewLine);
        byte[] bBytes = (b with { Time = a.Time }).Bytes(Environment.NewLine);
        byte[] onDisk = File.ReadAllBytes(path);
        Assert.Equal(Concat(headerPart, aBytes, bBytes), onDisk);

        // Treści TEJ SAMEJ długości co ostatni rekord b: urwany rekord (otwarte cytowanie) albo rekord z kompletem 8 pól bez końca linii.
        byte[] SameLength(string start, char fill) => Encoding.ASCII.GetBytes(start + new string(fill, bBytes.Length - start.Length));
        byte[] tornSame = SameLength("2026-10-07 09:00:00;Obcy;nowy;\"", 'x');
        byte[] completeSame = SameLength("2026-10-07 09:00:00;Obcy;nowy;Servo;inny;Servo;PC-B;", 'y');

        byte[] kept;
        switch (change)
        {
            case "obciecie-do-rekordu":
                RewriteInPlace(path, kept = Concat(headerPart, aBytes));
                break;
            case "obciecie-w-rekordzie":
                RewriteInPlace(path, Concat(headerPart, aBytes, tornSame[..40]));
                kept = Concat(headerPart, aBytes);
                break;
            case "obciecie-do-zera":
                RewriteInPlace(path, []);
                kept = headerPart;
                break;
            case "obciecie-w-naglowku":
                RewriteInPlace(path, headerPart[..10]);
                kept = headerPart;
                break;
            case "ta-sama-dlugosc-urwany-koniec":
                RewriteInPlace(path, Concat(headerPart, aBytes, tornSame));
                kept = Concat(headerPart, aBytes);
                break;
            case "ta-sama-dlugosc-zera-ntfs":
                RewriteInPlace(path, Concat(headerPart, aBytes, new byte[bBytes.Length]));
                kept = Concat(headerPart, aBytes);
                break;
            case "ta-sama-dlugosc-podmiana-przez-zmiane-nazwy":
                Thread.Sleep(50);
                File.WriteAllBytes(path + ".nowy", Concat(headerPart, aBytes, completeSame));
                File.Move(path + ".nowy", path, overwrite: true);
                kept = Concat(headerPart, aBytes, completeSame, NewLine); // „kompletny z wyglądu” zostaje, nowy wiersz od nowej linii
                break;
            case "ta-sama-dlugosc-usuniety-i-utworzony":
                Thread.Sleep(50);
                File.Delete(path);
                File.WriteAllBytes(path, Concat(headerPart, aBytes, tornSame));
                kept = Concat(headerPart, aBytes);
                break;
            default:
                File.AppendAllText(path, (foreign with { Time = a.Time }).Line + Environment.NewLine, new UTF8Encoding(false));
                kept = Concat(onDisk, (foreign with { Time = a.Time }).Bytes(Environment.NewLine));
                break;
        }

        Assert.Equal(onDisk.Length != File.ReadAllBytes(path).Length, !change.StartsWith("ta-sama-dlugosc", StringComparison.Ordinal));
        Write(writer, c);
        Assert.True(writer.PendingCount == 0 && writer.Error is null, change);
        byte[] cBytes = (c with { Time = a.Time }).Bytes(Environment.NewLine);
        byte[] expected = Concat(kept, cBytes);
        Assert.Equal(expected, File.ReadAllBytes(path));

        if (!OperatingSystem.IsWindows()) return; // FileShare na Linuksie to tylko blokada doradcza
        using (WriteOnlySharing(path))
        {
            Write(writer, d); // znów szybką ścieżką, bez odczytu
            Assert.True(writer.PendingCount == 0 && writer.Error is null, change + ": po analizie szybka ścieżka nie wróciła");
        }

        Assert.Equal(Concat(expected, (d with { Time = a.Time }).Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }

    [Fact]
    public void SameLengthChangeWithUnchangedTimestamps_IsNotSeenByTheFastPath_Limitation()
    {
        if (!OperatingSystem.IsWindows()) return; // czas utworzenia i ziarno znaczników — zachowanie NTFS
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_991);
        HistorySpec a = gen.Spec(), b = gen.Spec();
        var writer = new HistoryWriter(path, new FakeClock(a.Time));
        Write(writer, a);
        var info = new FileInfo(path);
        (long length, DateTime created, DateTime written) = (info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        byte[] changed = File.ReadAllBytes(path);
        changed[^1] = (byte)'x'; // koniec linii zastąpiony treścią — ta sama długość
        if (NewLine.Length == 2) changed[^2] = (byte)'x';
        using (var other = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) other.Write(changed);
        File.SetLastWriteTimeUtc(path, written);
        info.Refresh();
        Assert.Equal((length, created, written), (info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc));

        Write(writer, b);
        Assert.Equal(Concat(changed, (b with { Time = a.Time }).Bytes(Environment.NewLine)), File.ReadAllBytes(path)); // sklejone
    }

    // (9) Arkusz kalkulacyjny trzyma plik (zapis, bez współdzielenia zapisu), wpisy czekają w pamięci; arkusz w tym czasie sam dopisuje
    // urwany wiersz. Po zwolnieniu Flush robi pełną analizę (po błędzie szybka ścieżka jest wyłączona): urwany wiersz przycięty,
    // zaległe wiersze raz, w kolejności; potem znów szybka ścieżka.
    [Fact]
    public void SpreadsheetLockThatAlsoWritesATornLine_FlushAnalyzesAndWritesTheQueueOnceInOrder()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_992);
        HistorySpec a = gen.Spec(), b = gen.Spec(), c = gen.RichSpec(), d = gen.Spec();
        var writer = new HistoryWriter(path, new FakeClock(a.Time));
        Write(writer, a);
        byte[] before = File.ReadAllBytes(path);
        using (var sheet = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Write(writer, b);
            Write(writer, c);
            writer.Flush();
            Assert.Equal(2, writer.PendingCount);
            Assert.NotNull(writer.Error);
            sheet.Seek(0, SeekOrigin.End);
            sheet.Write(Encoding.UTF8.GetBytes("2026-10-07 09:00:00;Obcy;nowy;\"urwany"));
        }

        writer.Flush();
        Assert.True(writer.PendingCount == 0 && writer.Error is null);
        byte[] expected = Concat(before, (b with { Time = a.Time }).Bytes(Environment.NewLine), (c with { Time = a.Time }).Bytes(Environment.NewLine));
        Assert.Equal(expected, File.ReadAllBytes(path));
        using (WriteOnlySharing(path)) Write(writer, d);
        Assert.Equal(Concat(expected, (d with { Time = a.Time }).Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }

    // (10) Dwa obiekty HistoryWriter na jednym pliku, na zmianę (bez równoległości): każdy widzi cudze dopisanie po znaczniku
    // (długość), żaden wiersz nie ginie, nie powtarza się ani nie skleja; kolejność = kolejność wywołań.
    [Fact]
    public void TwoWritersTakingTurns_EachSeesTheOthersAppend()
    {
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_993);
        HistorySpec first = gen.Spec();
        var writers = new[] { new HistoryWriter(path, new FakeClock(first.Time)), new HistoryWriter(path, new FakeClock(first.Time)) };
        var specs = Enumerable.Range(0, 40).Select(_ => gen.Spec() with { Time = first.Time }).ToList();
        for (int i = 0; i < specs.Count; i++)
        {
            HistoryWriter writer = writers[(i / 3) % 2]; // serie 3 dopisań jednym obiektem, potem drugim
            Write(writer, specs[i]);
            Assert.True(writer.PendingCount == 0 && writer.Error is null, $"dopisanie {i}");
        }

        Assert.Equal(Concat([Bom, HeaderBytes, NewLine, .. specs.Select(s => s.Bytes(Environment.NewLine))]), File.ReadAllBytes(path));
    }

    // (11) Atrybut „tylko do odczytu” ustawiony z zewnątrz między dopisaniami: szybka ścieżka nie otwiera pliku, wpisy czekają; po
    // zdjęciu atrybutu Flush dopisuje je raz, w kolejności.
    [Fact]
    public void ReadOnlyAttributeSetBetweenAppends_QueuesUntilCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_994);
        HistorySpec a = gen.Spec(), b = gen.Spec(), c = gen.Spec();
        var writer = new HistoryWriter(path, new FakeClock(a.Time));
        Write(writer, a);
        byte[] before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Write(writer, b);
            Write(writer, c);
            Assert.Equal(2, writer.PendingCount);
            Assert.NotNull(writer.Error);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        writer.Flush();
        Assert.True(writer.PendingCount == 0 && writer.Error is null);
        Assert.Equal(Concat(before, (b with { Time = a.Time }).Bytes(Environment.NewLine), (c with { Time = a.Time }).Bytes(Environment.NewLine)),
            File.ReadAllBytes(path));
    }

    // (12) Katalog historii usunięty między dopisaniami: szybka ścieżka (FileMode.Append) tworzy pusty plik, znacznik się nie zgadza,
    // pełna ścieżka pisze BOM, nagłówek i wiersz — jak pierwszy zapis.
    [Fact]
    public void HistoryDirectoryRemovedBetweenAppends_NextAppendStartsANewFileWithTheHeader()
    {
        using var dir = new TempDir("history-testujacy");
        string path = Path.Combine(dir.File("historia"), "history.csv");
        var gen = new HistoryGen(12_995);
        HistorySpec a = gen.Spec(), b = gen.Spec();
        var writer = new HistoryWriter(path, new FakeClock(a.Time));
        Write(writer, a);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        Write(writer, b);
        Assert.True(writer.PendingCount == 0 && writer.Error is null);
        Assert.Equal(Concat(Bom, HeaderBytes, NewLine, (b with { Time = a.Time }).Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }

    // (13) pierwszy zapis po starcie przycina urwany rekord z zerami (pełna analiza), kolejne idą szybką ścieżką bez odczytu.
    [Fact]
    public void AfterTheZerosWereTrimmed_FollowingAppendsUseTheFastPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-testujacy");
        string path = dir.File("history.csv");
        var gen = new HistoryGen(12_989);
        byte[] complete = Concat(Bom, HeaderBytes, "\r\n"u8.ToArray(), gen.Spec().Bytes("\r\n"));
        byte[] torn = Encoding.UTF8.GetBytes("2026-10-07 07:59:59;Aidite 1;nowy;Ł")[..^1];
        File.WriteAllBytes(path, Concat(complete, torn, new byte[4096]));
        HistorySpec a = gen.Spec(), b = gen.Spec(), c = gen.Spec();
        var writer = new HistoryWriter(path, new FakeClock(a.Time));
        Write(writer, a);
        using (WriteOnlySharing(path))
        {
            Write(writer, b);
            Write(writer, c);
            Assert.True(writer.PendingCount == 0 && writer.Error is null);
        }

        Assert.Equal(Concat(complete, a.Bytes(Environment.NewLine), (b with { Time = a.Time }).Bytes(Environment.NewLine),
            (c with { Time = a.Time }).Bytes(Environment.NewLine)), File.ReadAllBytes(path));
    }
}
