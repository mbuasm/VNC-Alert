using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using static MaszynyAlert.Core.Tests.History.HistoryCsvFuzz;

namespace MaszynyAlert.Core.Tests.History;

public sealed class HistoryWriterRaceTests
{
    private static HistorySpec Marked(HistorySpec spec) => spec with { ShortText = spec.ShortText + " #" + spec.Id };

    private static int Marker(string shortText) =>
        int.Parse(shortText[(shortText.LastIndexOf('#') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    private static void Write(HistoryWriter writer, HistorySpec spec) => writer.Append(spec.Event, spec.Host, spec.User);

    /// <summary>Wiersze pliku bez nagłówka; każdy wiersz ma wartości oczekiwane dla swojego znacznika (poza czasem zegara).</summary>
    private static List<int> AssertRowsMatch(string path, IReadOnlyDictionary<int, HistorySpec> specs)
    {
        List<string[]>? rows = ParseRows(File.ReadAllBytes(path), 8);
        Assert.NotNull(rows);
        Assert.Equal(Header.Split(';'), rows[0]);
        var markers = new List<int>();
        foreach (string[] row in rows.Skip(1))
        {
            int id = Marker(row[3]);
            Assert.Equal(specs[id].ExpectedValues[1..], row[1..]);
            markers.Add(id);
        }

        return markers;
    }

    [Fact]
    public void ConcurrentAppendsFromManyThreads_EveryRecordWrittenWholeExactlyOnce()
    {
        using var dir = new TempDir("history-race");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());
        var gen = new HistoryGen(501);
        var specs = Enumerable.Range(0, 240).Select(_ => Marked(gen.Spec())).ToList();
        using var start = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            for (int i = t; i < specs.Count; i += 8) Write(writer, specs[i]);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        List<int> markers = AssertRowsMatch(path, specs.ToDictionary(s => s.Id));
        Assert.Equal(specs.Select(s => s.Id).Order(), markers.Order());
    }

    [Fact]
    public void TwoWriterInstancesOnTheSameFile_LoseNothingAndNeverGlueRecords()
    {
        // Np. dwa procesy przez chwilę naraz: drugi dostaje naruszenie współdzielenia, trzyma wpis w pamięci i dopisuje go
        // przy następnym zapisie albo Flush; zapamiętana granica pliku pierwszego obiektu traci ważność po cudzym zapisie.
        if (!OperatingSystem.IsWindows()) return; // FileShare na Linuksie to tylko blokada doradcza
        using var dir = new TempDir("history-race");
        string path = dir.File("history.csv");
        var first = new HistoryWriter(path, new FakeClock());
        var second = new HistoryWriter(path, new FakeClock());
        var gen = new HistoryGen(502);
        var specs = Enumerable.Range(0, 200).Select(_ => Marked(gen.Spec())).ToList();
        using var start = new Barrier(2);
        var threads = new[] { first, second }.Select((writer, t) => new Thread(() =>
        {
            start.SignalAndWait();
            for (int i = t; i < specs.Count; i += 2) Write(writer, specs[i]);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        first.Flush();
        second.Flush();

        Assert.Equal(0, first.PendingCount + second.PendingCount);
        Assert.Null(first.Error);
        Assert.Null(second.Error);
        List<int> markers = AssertRowsMatch(path, specs.ToDictionary(s => s.Id));
        Assert.Equal(specs.Select(s => s.Id).Order(), markers.Order());
        // Kolejność w obrębie jednego obiektu zostaje zachowana (zaległe wiersze idą przed nowymi).
        Assert.Equal(markers.Where(id => specs.FindIndex(s => s.Id == id) % 2 == 0), specs.Where((_, i) => i % 2 == 0).Select(s => s.Id));
        Assert.Equal(markers.Where(id => specs.FindIndex(s => s.Id == id) % 2 == 1), specs.Where((_, i) => i % 2 == 1).Select(s => s.Id));
    }

    [Fact]
    public void ReaderSharingWrite_LikeDiagnosticsExport_DoesNotBlockAppending()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-race");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());
        var gen = new HistoryGen(503);
        var specs = Enumerable.Range(0, 3).Select(_ => Marked(gen.Spec())).ToList();
        Write(writer, specs[0]);
        byte[] snapshot = File.ReadAllBytes(path);
        using (var export = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Write(writer, specs[1]);
            Write(writer, specs[2]);
            Assert.Equal(0, writer.PendingCount);
            Assert.Null(writer.Error);
            byte[] read = new byte[snapshot.Length];
            export.ReadExactly(read); // eksport czyta swoją migawkę bez przeszkód
            Assert.Equal(snapshot, read);
        }

        Assert.Equal(specs.Select(s => s.Id), AssertRowsMatch(path, specs.ToDictionary(s => s.Id)));
    }

    [Theory]
    [InlineData(FileAccess.ReadWrite, FileShare.Read)] // arkusz kalkulacyjny trzyma plik otwarty do zapisu
    [InlineData(FileAccess.ReadWrite, FileShare.None)]
    [InlineData(FileAccess.Read, FileShare.Read)]      // zwykły czytnik bez współdzielenia zapisu (File.ReadAllText)
    [InlineData(FileAccess.Read, FileShare.None)]
    public void LockedFile_QueuesInMemoryAndLeavesTheFileUntouched_ThenOneFlushWritesEverythingInOrder(FileAccess access, FileShare share)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-race");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());
        var gen = new HistoryGen(504);
        var specs = Enumerable.Range(0, 7).Select(_ => Marked(gen.Spec())).ToList();
        Write(writer, specs[0]);
        byte[] before = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, access, share))
        {
            foreach (HistorySpec spec in specs.Skip(1)) Write(writer, spec);
            writer.Flush();
            Assert.Equal(6, writer.PendingCount);
            Assert.NotNull(writer.Error);
        }

        Assert.Equal(before, File.ReadAllBytes(path)); // blokada nie zostawiła w pliku ani bajtu
        writer.Flush();
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(specs.Select(s => s.Id), AssertRowsMatch(path, specs.ToDictionary(s => s.Id)));
    }

    [Fact]
    public void AppendsWhileOtherProgramsKeepReadingTheFile_NothingIsLostAndNothingStaysBlocked()
    {
        // Czytnik ze współdzieleniem zapisu (eksport diagnostyki, podgląd logu) nie przeszkadza; czytnik bez niego
        // (File.ReadAllBytes) chwilami blokuje zapis — wpisy czekają i trafiają do pliku przy kolejnym zapisie albo Flush.
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir("history-race");
        string path = dir.File("history.csv");
        var writer = new HistoryWriter(path, new FakeClock());
        var gen = new HistoryGen(505);
        var specs = Enumerable.Range(0, 400).Select(_ => Marked(gen.Spec())).ToList();
        Write(writer, specs[0]);
        using var stop = new CancellationTokenSource();
        int sharedReads = 0, exclusiveReads = 0;
        var readers = new[]
        {
            new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.CopyTo(Stream.Null);
                    sharedReads++;
                }
            }),
            new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        File.ReadAllBytes(path);
                        exclusiveReads++;
                    }
                    catch (IOException)
                    {
                        // Zapis trzymał plik — czytnik próbuje dalej.
                    }
                }
            }),
        };
        foreach (Thread reader in readers) reader.Start();
        foreach (HistorySpec spec in specs.Skip(1)) Write(writer, spec);
        stop.Cancel();
        foreach (Thread reader in readers) reader.Join();
        writer.Flush();

        Assert.True(sharedReads > 0 && exclusiveReads > 0, $"czytniki: {sharedReads} / {exclusiveReads}");
        Assert.Equal(0, writer.PendingCount);
        Assert.Null(writer.Error);
        Assert.Equal(specs.Select(s => s.Id), AssertRowsMatch(path, specs.ToDictionary(s => s.Id)));
    }
}
