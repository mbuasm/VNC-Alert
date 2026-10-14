using System.Text;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using Microsoft.VisualBasic.FileIO;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryWriterRecoveryTests
{
    private const string Header = "czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik";
    private const string CompleteRecord = "2026-10-07 07:59:58;Aidite 1;nowy;\"Opis; z \"\"cytatem\"\"\";inny;\"Pełny\r\ntekst\nŁódź\";PC-A;operator";
    private const string NextRecord = "2026-10-07 08:00:00;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator";
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private static AlertEvent Event(string description = "Servo") => new(AlertEventKind.Raised, new Alert
    {
        Key = AlertKey.ForRule("aidite1", "servo"), MachineId = "aidite1", MachineName = "Aidite 1",
        Kind = AlertKind.Error, ShortText = description, FullText = "Servo", Category = "inny",
        StartedAt = T0, LastSeenAt = T0, Status = AlertStatus.Active,
    });

    private static void Append(HistoryWriter writer, string description = "Servo") =>
        writer.Append(Event(description), "PC-A", "operator");

    private static byte[] Bytes(string text, bool bom = true) =>
        [.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(text)];

    private static List<string[]> ReadRecords(string path)
    {
        using var reader = new TextFieldParser(path, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(";");
        var result = new List<string[]>();
        while (!reader.EndOfData) result.Add(reader.ReadFields()!);
        return result;
    }

    private static void AssertWritten(HistoryWriter writer, string path, byte[] expected, int rows)
    {
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(expected, File.ReadAllBytes(path));
        List<string[]> records = ReadRecords(path);
        Assert.Equal(rows, records.Count);
        Assert.All(records, row => Assert.Equal(8, row.Length));
        Assert.Equal(NextRecord.Split(';'), records[^1]);
    }

    [Fact]
    public void A2_02_FirstEventAfterATornCsvWriteIsASeparateCompleteRecord()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        File.WriteAllText(path, Header + "\n2026-10-07 07:59:59;Aidite 1;now", new UTF8Encoding(true));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        writer.Flush();
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Contains(NextRecord, File.ReadAllLines(path));
    }

    [Theory]
    [InlineData("2026-10-07 07:59:59;Aidite 1;now")]
    [InlineData("2026-10-07 07:59:59;\"Aidite; 1")]
    [InlineData("2026-10-07 07:59:59;Aidite 1;nowy;\"Opis z \"")]
    [InlineData("2026-10-07 07:59:59;Aidite 1;nowy;\"Opis z \"\"cytatem\"\"")]
    [InlineData("2026-10-07 07:59:59;Aidite 1;nowy;Servo;inny;\"Pełny\n")]
    [InlineData("2026-10-07 07:59:59;Aidite 1;nowy;Servo;inny;\"Pełny\r\n")]
    [InlineData("2026-10-07 07:59:59;Aidite 1;nowy;Servo;inny;\"Pełny\r")]
    [InlineData("2026-10-07 07:59:59;Aidite 1;nowy;Servo;inny;Servo;PC-A;\"operator\n")]
    public void TornLastRecord_IsRemovedWithoutChangingCompleteMultilinePrefix(string torn)
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string prefix = Header + "\r\n" + CompleteRecord + "\r\n";
        File.WriteAllBytes(path, Bytes(prefix + torn));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(prefix + NextRecord + Environment.NewLine), 3);
    }

    [Theory]
    [InlineData(true, "operator")]
    [InlineData(false, "operator")]
    [InlineData(true, "\"operator; \"\"Łódź\"\"\nII\"")]
    [InlineData(false, "")]
    public void CompleteEofRecordWithoutNewline_IsPreservedByteForByte(bool bom, string user)
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string oldRecord = CompleteRecord[..^"operator".Length] + user;
        string before = Header + "\n" + oldRecord;
        File.WriteAllBytes(path, Bytes(before, bom));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(before + Environment.NewLine + NextRecord + Environment.NewLine, bom), 3);
    }

    [Fact]
    public void CompleteRecordEndingAtCarriageReturn_IsPreservedAndLfCompletesTheSeparator()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string before = Header + "\r\n" + CompleteRecord + "\r";
        File.WriteAllBytes(path, Bytes(before));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(before + "\n" + NextRecord + Environment.NewLine), 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(60)]
    public void TornBomOrHeader_IsRebuiltExactlyOnce(int length)
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        File.WriteAllBytes(path, Bytes(Header)[..length]);
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(Header + Environment.NewLine + NextRecord + Environment.NewLine), 2);
    }

    [Fact]
    public void PartialUtf8InTheLastUnquotedField_DoesNotBecomeACorruptCompleteRecord()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string prefix = Header + "\n" + CompleteRecord + "\n";
        byte[] torn = Encoding.UTF8.GetBytes(NextRecord[..^"operator".Length] + "Ł")[..^1];
        File.WriteAllBytes(path, [.. Bytes(prefix), .. torn]);
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(prefix + NextRecord + Environment.NewLine), 3);
    }

    [Fact]
    public void FailedOpen_PreservesTheFileAndQueue_ThenFlushRepairsAndWritesInOrder()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string prefix = Header + "\r\n" + CompleteRecord + "\r\n";
        byte[] before = Bytes(prefix + "2026-10-07 07:59:59;\"Aidite\n");
        File.WriteAllBytes(path, before);
        var writer = new HistoryWriter(path, new FakeClock(T0));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Append(writer, "Pierwszy");
            Append(writer);
            writer.Flush();
            Assert.Equal(2, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        writer.Flush();
        string first = NextRecord.Replace(";Servo;inny;", ";Pierwszy;inny;", StringComparison.Ordinal);
        AssertWritten(writer, path, Bytes(prefix + first + Environment.NewLine + NextRecord + Environment.NewLine), 4);
        writer.Flush();
        Assert.Equal(4, ReadRecords(path).Count);
    }

    [Fact]
    public void ExistingTerminatedRecords_AndAnotherAppend_KeepTheirOriginalBytes()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string before = Header + "\n" + CompleteRecord + "\r\n";
        File.WriteAllBytes(path, Bytes(before));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        Append(writer);
        AssertWritten(writer, path, Bytes(before + NextRecord + Environment.NewLine + NextRecord + Environment.NewLine), 4);
    }

    [Fact]
    public void ReadFailureAfterACompletePrefix_DoesNotMutateTheFileOrLosePendingEvents()
    {
        if (!OperatingSystem.IsWindows()) return; // blokada zakresu wymusza błąd Read tylko na Windows
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string prefix = Header + "\r\n" + string.Concat(Enumerable.Repeat(CompleteRecord + "\r\n", 500));
        byte[] before = Bytes(prefix + "2026-10-07 07:59:59;\"Aidite\n");
        File.WriteAllBytes(path, before);
        var writer = new HistoryWriter(path, new FakeClock(T0));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long half = locked.Length / 2;
            locked.Lock(half, locked.Length - half);
            // Otwarcie ReadWrite działa, pierwszy blok da się odczytać, dalszy odczyt naprawdę rzuca.
            using (var proof = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                proof.ReadExactly(new byte[4096]);
                proof.Position = half;
                Assert.Throws<IOException>(() => proof.ReadExactly(new byte[1]));
            }
            Append(writer);
            Assert.Equal(1, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        writer.Flush();
        AssertWritten(writer, path, Bytes(prefix + NextRecord + Environment.NewLine), 502);
    }

    [Fact]
    public void RepeatedAppend_DoesNotReadThePreviouslyVerifiedGrowingHistory()
    {
        if (!OperatingSystem.IsWindows()) return; // odczyt z zablokowanego prefiksu rzuca tylko na Windows
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        string prefix = Header + "\n" + string.Concat(Enumerable.Repeat(CompleteRecord + "\n", 500));
        File.WriteAllBytes(path, Bytes(prefix));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            locked.Lock(0, locked.Length);
            Append(writer); // brak pomiaru czasu: jakikolwiek ponowny odczyt prefiksu spowodowałby błąd
            Assert.Equal(0, writer.PendingCount);
            Assert.Null(writer.Error);
        }
        AssertWritten(writer, path, Bytes(prefix + NextRecord + Environment.NewLine + NextRecord + Environment.NewLine), 503);
    }

    [Fact]
    public void ExternalSameLengthChange_InvalidatesTheVerifiedBoundary()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        DateTime previousWrite = File.GetLastWriteTimeUtc(path);
        // Zastąp cały końcowy terminator otwartym polem cytowanym tej samej długości.
        string tornUser = "\"" + new string('x', "operator".Length + Environment.NewLine.Length - 2) + "\n";
        File.WriteAllBytes(path, Bytes(Header + Environment.NewLine + NextRecord[..^"operator".Length] + tornUser));
        File.SetLastWriteTimeUtc(path, previousWrite.AddSeconds(5));
        Append(writer);
        AssertWritten(writer, path, Bytes(Header + Environment.NewLine + NextRecord + Environment.NewLine), 2);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4093)]
    [InlineData(4089)]
    public void Utf8AndEscapedQuotesAcrossReadBuffers_PreserveTheCompleteRecord(int specialByteOffset)
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        int textAt = CompleteRecord.IndexOf("Pełny", StringComparison.Ordinal);
        int padding = specialByteOffset - 1 - Encoding.UTF8.GetByteCount(CompleteRecord[..textAt]); // pozostałe LF po nagłówku
        string record = CompleteRecord.Replace("Pełny", new string('x', padding) + "ł😀\"\"\nPełny", StringComparison.Ordinal);
        string prefix = Header + "\r\n" + record + "\r\n";
        File.WriteAllBytes(path, Bytes(prefix + "2026-10-07 07:59:59;\"urwany"));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(prefix + NextRecord + Environment.NewLine), 3);
    }

    [Theory]
    [InlineData("nieznany;plik\n")]
    [InlineData(Header + "\nniepoprawny;zamknięty;rekord\n")]
    [InlineData("test historii")]
    [InlineData(Header + "\r\n2026-10-07 07:59:59;Aidite 1;now2026-10-07 07:59:58;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator\r\n"
        + CompleteRecord + "\r\n")]
    [InlineData(Header + "\r\n2026-10-07 07:59:59;Aidite 1;nowy;\"Opis z 2026-10-07 07:59:58;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator\r\n")]
    [InlineData(Header + "\r\n" + CompleteRecord + "\r\n2026-10-07 07:59:57;Aidite 1;nowy;Servo;inny;\"Pełny\r\ntekst"
        + "2026-10-07 07:59:58;Aidite 1;nowy;Servo;inny;Servo;PC-A;operator\r\n"
        + "2026-10-07 07:59:59;Aidite 2;nowy;Servo;inny;Servo;PC-A;operator\r\n")]
    [InlineData(Header + "\r\n" + CompleteRecord + "\r\nniepoprawny;rekord\r")]
    public void UnrecognizedContent_IsKeptByteForByte_AndHistoryContinuesAfterIt(string before)
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        File.WriteAllBytes(path, Bytes(before));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        string separator = before.EndsWith('\n') ? "" : before.EndsWith('\r') ? "\n" : Environment.NewLine;
        string expected = before + separator + NextRecord + Environment.NewLine;
        Assert.Equal(Bytes(expected), File.ReadAllBytes(path));
        Append(writer);
        Assert.Equal(Bytes(expected + NextRecord + Environment.NewLine), File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidUtf8InsideEarlierHistory_DoesNotStopHistory()
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        byte[] ansi = [.. Encoding.ASCII.GetBytes("2026-10-07 07:59:58;Aidite 1;nowy;B"), 0xB3, 0xB9,
            .. Encoding.ASCII.GetBytes("d;inny;Servo;PC-A;operator\r\n")];
        byte[] before = [.. Bytes(Header + "\r\n"), .. ansi];
        File.WriteAllBytes(path, before);
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal([.. before, .. Encoding.UTF8.GetBytes(NextRecord + Environment.NewLine)], File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompleteHeaderAtEof_IsPreservedWithoutAnotherBomOrHeader(bool bom)
    {
        using var dir = new TempDir("history-recovery");
        string path = dir.File("history.csv");
        File.WriteAllBytes(path, Bytes(Header, bom));
        var writer = new HistoryWriter(path, new FakeClock(T0));
        Append(writer);
        AssertWritten(writer, path, Bytes(Header + Environment.NewLine + NextRecord + Environment.NewLine, bom), 2);
    }
}