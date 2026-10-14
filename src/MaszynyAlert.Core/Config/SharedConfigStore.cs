using System.Text.Json;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Config;

/// <summary>
/// Plik wspólnej konfiguracji (<c>shared-config/shared-config.json</c>) — jeden dokument
/// <see cref="SharedConfigDocument"/> zapisywany atomowo, tą samą mechaniką co własne instrukcje naprawy
/// (<see cref="MaszynyAlert.Core.Ocr.AlarmInstructionStore"/>): plik-zamek <c>.lock</c> na czas
/// „czytaj → scal → zapisz”, zapis przez <c>.tmp</c> i podmiana z kopią <c>.bak</c>.
///
/// <para>Katalog musi być zapisywalny dla <c>BUILTIN\Users</c> (patrz <c>installer/Initialize-Data.ps1</c>):
/// zmianę odebraną z sieci trzeba zapisać bez UAC, tak samo jak wspólne instrukcje.</para>
/// </summary>
public sealed class SharedConfigStore(string path)
{
    /// <summary>Dokument jest mały (poniżej 4 KB); limit chroni przed wczytaniem podmienionego, wielkiego pliku.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private const int CurrentVersion = 1;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>
    /// Wczytuje warstwę. Brak pliku to <see langword="null"/>, nie błąd — komputer po prostu nie ma
    /// nadpisania. Uszkodzony albo obcy plik daje <see cref="InvalidDataException"/>; wołający decyduje,
    /// czy to zatrzymuje start (nie zatrzymuje — patrz <see cref="SharedConfigSync.Reload"/>).
    /// </summary>
    public SharedConfigDocument? Load()
    {
        if (!File.Exists(Path)) return null;
        byte[] bytes;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaxFileBytes) throw new InvalidDataException("Plik wspólnej konfiguracji jest zbyt duży.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        StoredFile? file;
        try { file = JsonSerializer.Deserialize<StoredFile>(bytes, SharedConfigDocument.CanonicalJsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("Plik wspólnej konfiguracji ma nieprawidłową składnię JSON.", ex); }
        if (file is null) throw new InvalidDataException("Plik wspólnej konfiguracji jest pusty.");
        if (file.Version != CurrentVersion) throw new InvalidDataException("Nieobsługiwany format wspólnej konfiguracji.");
        if (file.Document is not { } document) throw new InvalidDataException("Plik wspólnej konfiguracji nie zawiera dokumentu.");
        if (!document.IsValid()) throw new InvalidDataException("Plik wspólnej konfiguracji zawiera nieprawidłowe wartości.");
        return document;
    }

    public SharedConfigDocument? LoadUnderGate()
    {
        if (!File.Exists(Path) && !File.Exists(Path + ".lock")) return null;
        FileStream gate;
        try { gate = OpenGate(); }
        catch (UnauthorizedAccessException) { return Load(); }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            // OpenGate czekał 2 s: zamek trzyma inny zapis (np. przeczekuje skaner przy podmianie) albo zawieszony proces.
            if (LoadWhileGateBusy() is { } document) return document;
            throw;
        }
        using (gate) return Load();
    }

    /// <summary>
    /// Odczyt bez zamka, gdy zamka nie da się wziąć przez 2 s — do <see cref="BusyGateReadWindow"/>, co 50 ms. Zwraca dokument
    /// z pliku albo <see langword="null"/>, gdy w tym czasie nie udało się go przeczytać; wtedy <see cref="LoadUnderGate"/> zgłasza
    /// błąd zamka, a <see cref="SharedConfigSync.Reload"/> zostawia w pamięci poprzedni dokument.
    /// <para>Bez zamka czytelnik może trafić na podmianę w toku: stary plik już pod nazwą <c>.bak</c>, nowy jeszcze bez
    /// nazwy, albo chwilowe naruszenie współdzielenia, gdy <c>ReplaceFile</c> trzyma plik. Oba stany trwają ułamki sekundy (nieudane
    /// przeniesienie kończy kolejna próba zapisującego po 50 ms — <see cref="ReplaceWithRetry"/>), więc odczyt ponawia; „brak pliku”
    /// przy zajętym zamku NIGDY nie jest wynikiem (to byłaby utrata warstwy, przed którą chroni zamek). Plik w nazwie jest zawsze cały
    /// (zapis przez <c>.tmp</c> i podmianę), a zapisujący, któremu czytelnik bez zamka zablokuje podmianę, ponawia ją do 2 s.
    /// Uszkodzony plik (<see cref="InvalidDataException"/>) i brak prawa odczytu wychodzą od razu — jak spod zamka.</para>
    /// </summary>
    private SharedConfigDocument? LoadWhileGateBusy()
    {
        var deadline = DateTime.UtcNow + BusyGateReadWindow;
        while (true)
        {
            try
            {
                if (Load() is { } document) return document;
            }
            catch (IOException) { }
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(50);
        }
    }

    /// <summary>Jak długo <see cref="LoadWhileGateBusy"/> ponawia odczyt bez zamka (po 2 s czekania na zamek).</summary>
    private static readonly TimeSpan BusyGateReadWindow = TimeSpan.FromMilliseconds(500);

    public SharedConfigDocument Save(TimingConfig? timing, OverlayConfig? overlay, DateTimeOffset editedAt, string editedBy,
        IReadOnlyDictionary<string, bool>? machines = null)
    {
        SharedConfigDocument? saved = null;
        Write(current =>
        {
            var at = editedAt;
            if (current is { } previous && previous.EditedAt >= at) at = previous.EditedAt.AddTicks(1);
            saved = new SharedConfigDocument
            {
                Timing = timing, Overlay = overlay,
                Machines = machines is null ? current?.Machines : SharedConfigDocument.CanonicalMachines(machines),
                UpdateApproval = current?.UpdateApproval, EditedAt = at, EditedBy = editedBy,
            };
            if (!saved.IsValid()) throw new ArgumentException("Nieprawidłowa wspólna konfiguracja.");
            return saved;
        }, force: true);
        return saved!;
    }

    public SharedConfigDocument SaveApproval(UpdateApproval approval, DateTimeOffset editedAt, string editedBy)
    {
        ArgumentNullException.ThrowIfNull(approval);
        SharedConfigDocument? saved = null;
        Write(current =>
        {
            var at = editedAt;
            if (current is { } previous && previous.EditedAt >= at) at = previous.EditedAt.AddTicks(1);
            saved = new SharedConfigDocument
            {
                Timing = current?.Timing, Overlay = current?.Overlay,
                Machines = current?.Machines, UpdateApproval = approval, EditedAt = at, EditedBy = editedBy,
            };
            if (!saved.IsValid()) throw new ArgumentException("Nieprawidłowa zgoda na aktualizację.", nameof(approval));
            return saved;
        }, force: true);
        return saved!;
    }

    public bool Merge(SharedConfigDocument incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (!incoming.IsValid()) return false;
        return Write(current => KeepMachinesFromFile(incoming, current), force: false);
    }

    internal static SharedConfigDocument KeepMachinesFromFile(SharedConfigDocument incoming, SharedConfigDocument? current)
    {
        if (incoming.Machines is not null || current?.Machines is not { } kept) return incoming;
        if (current == incoming || !SharedConfigDocument.Wins(incoming, current)) return current;
        return incoming with { Machines = kept };
    }

    public SharedConfigDocument? DropMachines()
    {
        if (!File.Exists(Path)) return null;
        SharedConfigDocument? before = null;
        Write(current =>
        {
            if (current?.Machines is null) return null;
            before = current;
            return current with { Machines = null };
        }, force: true);
        return before;
    }

    /// <summary>
    /// Cofa <see cref="DropMachines"/>, gdy zmiany grupy nie udało się zatwierdzić: przełączniki z <paramref name="before"/> wracają do
    /// dokumentu w pliku, bez zmiany jego czasu i autora. Dokument, który w międzyczasie przyszedł z nowej grupy, zostaje — wracają tylko
    /// przełączniki; gdy pliku nie ma, wraca cały dokument sprzed zmiany. Zwraca <see langword="true"/>, gdy plik się zmienił.
    /// </summary>
    public bool RestoreMachines(SharedConfigDocument before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return Write(current =>
        {
            if (current is null) return before;
            var restored = current with { Machines = before.Machines };
            return string.Equals(restored.Hash, current.Hash, StringComparison.Ordinal) ? null : restored;
        }, force: true);
    }

    /// <param name="prepare">Dokument do zapisania, policzony pod zamkiem na podstawie stanu pliku; <see langword="null"/> = nic do zapisania.</param>
    /// <param name="force">Zapis własnej edycji: pomija regułę „nowszy wygrywa”.</param>
    private bool Write(Func<SharedConfigDocument?, SharedConfigDocument?> prepare, bool force)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // Jeden plik-zamek na proces panelu i proces odbierający zmiany z sieci: czytaj → scal → zapisz.
        using var gate = OpenGate();
        var current = Load();
        var incoming = prepare(current);
        if (incoming is null) return false;
        if (!force)
        {
            if (current == incoming) return false;
            if (!SharedConfigDocument.Wins(incoming, current)) return false;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredFile(CurrentVersion, incoming),
            new JsonSerializerOptions(SharedConfigDocument.CanonicalJsonOptions) { WriteIndented = true });
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Plik wspólnej konfiguracji jest zbyt duży.");
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            ReplaceWithRetry(temporary);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    private void ReplaceWithRetry(string temporary)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak", ignoreMetadataErrors: true);
                else File.Move(temporary, Path);
                return;
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private FileStream OpenGate()
    {
        // Kod błędu zajętego pliku zależy od systemu (Windows: naruszenie współdzielenia; Linux w testach CI: blokada
        // flock), więc ponawiamy każdy IOException poza brakiem pliku/katalogu — przez najwyżej 2 s.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed record StoredFile(int Version, SharedConfigDocument? Document);
}
