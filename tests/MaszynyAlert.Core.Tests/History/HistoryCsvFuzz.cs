using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.Core.Tests.History;

internal sealed record HistorySpec(
    int Id, DateTimeOffset Time, AlertEventKind Kind, string Machine, string ShortText, string Category,
    string? FullText, string? Reason, string Host, string User)
{
    public AlertEvent Event => new(Kind, new Alert
    {
        Key = AlertKey.ForRule("m" + Id, "r"), MachineId = "m" + Id, MachineName = Machine, Kind = AlertKind.Error,
        ShortText = ShortText, FullText = FullText, Category = Category,
        StartedAt = Time, LastSeenAt = Time, Status = AlertStatus.Active,
    }) { Reason = Reason };

    /// <summary>Pola przed zabezpieczeniem formuł i cytowaniem — tak, jak je składa program.</summary>
    public string[] RawFields =>
    [
        Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Machine, EventName(Kind), ShortText, Category,
        FullTextWithReason(), Host, User,
    ];

    /// <summary>Wartości, które czytnik CSV ma odczytać z pliku (apostrof przed formułą, samotny surogat jako U+FFFD).</summary>
    public string[] ExpectedValues => [.. RawFields.Select(v => HistoryCsvFuzz.RoundTrip(HistoryCsvFuzz.Protect(v)))];

    public string Line => string.Join(';', RawFields.Select(v => HistoryCsvFuzz.Quote(HistoryCsvFuzz.Protect(v))));

    public byte[] Bytes(string terminator) => HistoryCsvFuzz.ProgramEncoding.GetBytes(Line + terminator);

    private string FullTextWithReason()
    {
        string text = FullText ?? string.Empty;
        if (Kind is not (AlertEventKind.Raised or AlertEventKind.Escalated) || Reason is not { Length: > 0 } reason) return text;
        return text.Length == 0 ? $"[powód: {reason}]" : $"{text} [powód: {reason}]";
    }

    private static string EventName(AlertEventKind kind) => kind switch
    {
        AlertEventKind.Raised => "nowy",
        AlertEventKind.Acknowledged => "potwierdzony",
        AlertEventKind.Escalated => "eskalacja",
        AlertEventKind.Closed => "zamknięty",
        AlertEventKind.Updated => "aktualizacja",
        _ => kind.ToString(),
    };
}

/// <summary>
/// Generator rekordów w formacie programu (deterministyczny dla ziarna) — pola ze średnikami, cudzysłowami, nowymi
/// liniami CR/LF/CRLF, polskimi znakami, emoji (4 bajty UTF-8), początkami formuł <c>= + - @</c> i tabulatorem.
/// </summary>
internal sealed class HistoryGen
{
    private static readonly string[] Words =
        ["Servo", "alarm", "Tool life was reached", "IDLE: Abort", "No.1532", "0.20%", "Błąd", "Łódź", "zażółć gęślą jaźń", "ZAŻÓŁĆ", "x"];
    private static readonly string[] Punctuation = [" ", " ", ";", "\"", "\"\"", ",", ":", "'", "(", ")"];
    private static readonly string[] LineBreaks = ["\n", "\r\n", "\r"];
    private static readonly string[] Wide = ["€", "ß", "😀", "𝔘", "🛠️", "日本", " "];
    private static readonly string[] Formula = ["=", "+", "-", "@", "\t"];
    private static readonly string[] Machines =
        ["Aidite 1", "Aidite 7", "520 (1)", "580", "Premile", "Cameo 1", "Maszyna; z średnikiem", "Łódź \"A\"", "=SUMA(A1)", "-5", "+48", "@admin", "😀 robot", "𝔘nikod"];
    private static readonly string[] Categories = ["inny", "narzędzie", "tacka", "ekran", "postój", "nie błąd", "=kat", ""];
    private static readonly string[] Hosts = ["PC-A", "PC-B", "KOMP-3", "-host", "@pc", "pc;1", "Łódź-PC"];
    private static readonly string[] Users = ["operator", "", "użytkownik", "op;1", "=1+1", "\"cytat\"", "😀", "Łódź\r\nII"];
    private static readonly string?[] Reasons =
        [null, "przerwa 11 s", "ten sam komunikat wrócił po przerwie 3 s — epizod połączony, bez nowego dźwięku", "eskalacja po 4 min od potwierdzenia"];

    private readonly Random _random;
    private DateTimeOffset _time;
    private int _nextId = 1;

    public HistoryGen(int seed)
    {
        Seed = seed;
        _random = new Random(seed);
        _time = new DateTimeOffset(2026, 10, 7, 6, 0, 0, TimeSpan.Zero).AddSeconds(seed * 7919 % 86_400);
    }

    public int Seed { get; }

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

    public bool Chance(double probability) => _random.NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    /// <summary>Koniec wiersza istniejącego pliku: CRLF (Windows), LF (plik z Linuksa) albo rzadko samo CR.</summary>
    public string Terminator() => Next(10) switch { < 6 => "\r\n", < 9 => "\n", _ => "\r" };

    public string Text(int maxPieces, bool multiline)
    {
        while (true)
        {
            var text = new StringBuilder();
            if (Chance(0.15)) text.Append(Pick(Formula));
            int pieces = Next(0, maxPieces + 1);
            for (int i = 0; i < pieces; i++)
            {
                int kind = Next(100);
                if (kind < 45) text.Append(Pick(Words));
                else if (kind < 70) text.Append(Pick(Punctuation));
                else if (kind < 85) text.Append(Pick(Wide));
                else if (kind < 97) text.Append(multiline ? Pick(LineBreaks) : " ");
                else text.Append('\uD83D'); // samotny surogat (np. tekst ucięty w środku pary) — program zapisze U+FFFD
            }

            string value = text.ToString();
            if (!HistoryCsvFuzz.ContainsRecordStart(value)) return value; // znacznik czasu z ';' w treści: osobny test
        }
    }

    public HistorySpec Spec()
    {
        _time = _time.AddSeconds(Next(1, 600));
        var kind = (AlertEventKind)Next(5);
        return new HistorySpec(_nextId++, _time, kind, Pick(Machines), Text(6, Chance(0.2)), Pick(Categories),
            Chance(0.15) ? null : Text(12, true), Pick(Reasons), Pick(Hosts), Pick(Users));
    }

    /// <summary>Rekord, który na pewno ma znaki 2-, 3- i 4-bajtowe, CRLF wewnątrz cytowania i cudzysłów podwojony.</summary>
    public HistorySpec RichSpec()
    {
        HistorySpec spec = Spec();
        return spec with
        {
            FullText = Text(6, true) + " ł€😀 \"cytat\"\r\nlinia;2\n" + Text(4, true),
            User = Chance(0.5) ? Pick(Users) : "op \"" + Text(3, true) + "\"",
        };
    }

    /// <summary>Nagłówek (z BOM albo bez) i <paramref name="records"/> kompletnych rekordów z losowymi końcami wierszy.</summary>
    public (byte[] Bytes, List<HistorySpec> Specs, List<int> Ends) File(bool bom, int records)
    {
        var bytes = new List<byte>(bom ? HistoryCsvFuzz.Bom : []);
        bytes.AddRange(HistoryCsvFuzz.HeaderBytes);
        bytes.AddRange(HistoryCsvFuzz.ProgramEncoding.GetBytes(Terminator()));
        var specs = new List<HistorySpec>();
        var ends = new List<int> { bytes.Count };
        for (int i = 0; i < records; i++)
        {
            HistorySpec spec = Spec();
            specs.Add(spec);
            bytes.AddRange(spec.Bytes(Terminator()));
            ends.Add(bytes.Count);
        }

        return ([.. bytes], specs, ends);
    }
}

internal static class HistoryCsvFuzz
{
    public const string Header = "czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik";
    public static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    public static readonly byte[] HeaderBytes = Encoding.ASCII.GetBytes(Header);
    public static readonly UTF8Encoding ProgramEncoding = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly char[] CharsRequiringQuotes = [';', '"', '\n', '\r'];
    private static readonly Regex RecordStart =
        new("[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2};", RegexOptions.CultureInvariant);

    public enum Outcome { NewHeader, Appended, KeptCompleteLooking, TrimmedTornTail, Unrecognized }

    public static byte[] NewLine => ProgramEncoding.GetBytes(Environment.NewLine);

    public static string Protect(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    public static string Quote(string value) =>
        value.IndexOfAny(CharsRequiringQuotes) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string RoundTrip(string value) => ProgramEncoding.GetString(ProgramEncoding.GetBytes(value));

    public static bool ContainsRecordStart(string value) => RecordStart.IsMatch(value);

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }

    /// <summary>Separator przed nowym wierszem za zachowaną treścią: nic po LF, LF po CR, inaczej koniec linii programu.</summary>
    public static byte[] Separator(ReadOnlySpan<byte> kept) =>
        kept.Length == 0 || kept[^1] == (byte)'\n' ? [] : kept[^1] == (byte)'\r' ? [(byte)'\n'] : NewLine;

    /// <summary>Oczekiwana zawartość pliku po dopisaniu bajtów <paramref name="record"/> do pliku <paramref name="file"/>.</summary>
    public static (byte[] Expected, Outcome Outcome) ExpectedAfterAppend(byte[] file, byte[] record)
    {
        byte[] fresh = Concat(Bom, HeaderBytes, NewLine, record);
        if (file.Length == 0) return (fresh, Outcome.NewHeader);
        int start = 0;
        if (file.AsSpan().StartsWith(Bom)) start = Bom.Length;
        else if (Bom.AsSpan().StartsWith(file)) return (fresh, Outcome.NewHeader); // urwany sam BOM

        ReadOnlySpan<byte> rest = file.AsSpan(start);
        if (rest.Length < HeaderBytes.Length && HeaderBytes.AsSpan().StartsWith(rest)) return (fresh, Outcome.NewHeader);
        if (!rest.StartsWith(HeaderBytes)) return Unrecognized(file, record);
        if (rest.Length == HeaderBytes.Length) return (Concat(file, NewLine, record), Outcome.Appended);
        if (rest[HeaderBytes.Length] is not ((byte)'\r' or (byte)'\n')) return Unrecognized(file, record);

        Utf8State utf8 = CheckUtf8(file.AsSpan(start));
        if (utf8 == Utf8State.Invalid) return Unrecognized(file, record);
        CsvScan scan = CsvScan.Run(file, start, requiredFields: 8);
        if (scan.Malformed) return Unrecognized(file, record);
        if (scan.Boundary == file.Length) return (Concat(file, Separator(file), record), Outcome.Appended);
        if (!scan.TailInQuotes && scan.TailFields == 8 && utf8 == Utf8State.Valid)
            return (Concat(file, NewLine, record), Outcome.KeptCompleteLooking);

        string tail = Encoding.Latin1.GetString(file, scan.Boundary, file.Length - scan.Boundary);
        if (RecordStart.IsMatch(tail[1..])) return Unrecognized(file, record); // dalszy początek rekordu: nie przycinać
        byte[] kept = file[..scan.Boundary];
        return (Concat(kept, Separator(kept), record), Outcome.TrimmedTornTail);
    }

    private static (byte[] Expected, Outcome Outcome) Unrecognized(byte[] file, byte[] record) =>
        (Concat(file, Separator(file), record), Outcome.Unrecognized);

    public static (byte[] Expected, Outcome Outcome) ExpectedAfterAppendWithZeroFill(byte[] file, byte[] record)
    {
        int end = file.AsSpan().LastIndexOfAnyExcept((byte)0) + 1;
        if (end == file.Length) return ExpectedAfterAppend(file, record);
        (byte[] expected, Outcome outcome) = ExpectedAfterAppend(file[..end], record);
        return outcome == Outcome.Unrecognized ? Unrecognized(file, record) : (expected, outcome);
    }

    /// <summary>
    /// Ścisłe odczytanie wierszy (bez BOM; ostatni niezakończony wiersz też, gdy cytowanie zamknięte). <see langword="null"/>
    /// przy niepoprawnym UTF-8 (chyba że <paramref name="strictUtf8"/> = false — wtedy jak czytnik z zamianą na U+FFFD),
    /// złym cytowaniu, otwartym cudzysłowie na końcu albo (gdy podano) innej liczbie pól niż wymagana.
    /// </summary>
    public static List<string[]>? ParseRows(byte[] file, int? requiredFields, bool strictUtf8 = true)
    {
        int start = file.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        if (strictUtf8 && CheckUtf8(file.AsSpan(start)) != Utf8State.Valid) return null;
        CsvScan scan = CsvScan.Run(file, start, requiredFields);
        if (scan.Malformed || scan.TailInQuotes) return null;
        if (scan.TailHasContent)
        {
            if (requiredFields is { } required && scan.TailFields != required) return null;
            scan.Rows.Add(scan.TailValues);
        }

        return scan.Rows;
    }

    public enum Utf8State { Valid, IncompleteAtEnd, Invalid }

    public static Utf8State CheckUtf8(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            switch (System.Text.Rune.DecodeFromUtf8(bytes, out _, out int consumed))
            {
                case System.Buffers.OperationStatus.Done:
                    bytes = bytes[consumed..];
                    break;
                case System.Buffers.OperationStatus.NeedMoreData:
                    return Utf8State.IncompleteAtEnd;
                default:
                    return Utf8State.Invalid;
            }
        }

        return Utf8State.Valid;
    }

    /// <summary>Prosty skaner CSV na bajtach (znaki specjalne CSV są ASCII, więc nie występują wewnątrz znaków UTF-8).</summary>
    public sealed class CsvScan
    {
        private enum State { FieldStart, Unquoted, Quoted, AfterQuote }

        public List<string[]> Rows { get; } = [];
        public int Boundary { get; private set; }
        public bool Malformed { get; private set; }
        public bool TailInQuotes { get; private set; }
        public bool TailHasContent { get; private set; }
        public int TailFields { get; private set; }
        public string[] TailValues { get; private set; } = [];

        public static CsvScan Run(byte[] file, int start, int? requiredFields)
        {
            var scan = new CsvScan { Boundary = start };
            var fields = new List<string>();
            var field = new List<byte>();
            State state = State.FieldStart;
            bool content = false;
            int i = start;
            while (i < file.Length)
            {
                byte b = file[i];
                if (state == State.Quoted)
                {
                    if (b == '"') state = State.AfterQuote;
                    else field.Add(b);
                    i++;
                    continue;
                }

                if (state == State.AfterQuote && b == '"')
                {
                    field.Add(b);
                    state = State.Quoted;
                    i++;
                    continue;
                }

                if (b == ';')
                {
                    fields.Add(Decode(field));
                    field.Clear();
                    content = true;
                    state = State.FieldStart;
                    if (requiredFields is { } max && fields.Count >= max) return scan.Fail();
                    i++;
                    continue;
                }

                if (b is (byte)'\r' or (byte)'\n')
                {
                    int end = i + 1;
                    if (b == '\r' && end < file.Length && file[end] == '\n') end++;
                    if (content)
                    {
                        fields.Add(Decode(field));
                        if (requiredFields is { } required && fields.Count != required) return scan.Fail();
                        scan.Rows.Add([.. fields]);
                    }

                    fields.Clear();
                    field.Clear();
                    content = false;
                    state = State.FieldStart;
                    scan.Boundary = end;
                    i = end;
                    continue;
                }

                if (state == State.AfterQuote) return scan.Fail(); // dane po zamknięciu cytowania
                if (b == '"')
                {
                    if (state != State.FieldStart) return scan.Fail(); // cudzysłów wewnątrz pola bez cytowania
                    state = State.Quoted;
                    content = true;
                    i++;
                    continue;
                }

                field.Add(b);
                content = true;
                state = State.Unquoted;
                i++;
            }

            scan.TailInQuotes = state == State.Quoted;
            scan.TailHasContent = content;
            scan.TailFields = content ? fields.Count + 1 : 0;
            if (content) scan.TailValues = [.. fields, Decode(field)];
            return scan;
        }

        private CsvScan Fail()
        {
            Malformed = true;
            return this;
        }

        private static string Decode(List<byte> bytes) => Encoding.UTF8.GetString([.. bytes]);
    }
}
