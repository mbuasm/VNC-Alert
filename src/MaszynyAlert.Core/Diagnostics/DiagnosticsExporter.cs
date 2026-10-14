using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MaszynyAlert.Core.Diagnostics;

/// <summary>
/// Tworzy ograniczoną paczkę diagnostyczną. Źródła są wybierane przez allowlistę;
/// klasa celowo nie zna konfiguracji ani magazynu sekretów.
/// </summary>
public sealed class DiagnosticsExporter
{
    private const int GeneratedEntryCount = 4;
    private const int MaxOmissionDetails = 500;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DiagnosticsExportOptions _options;

    public DiagnosticsExporter(DiagnosticsExportOptions? options = null)
    {
        _options = options ?? new DiagnosticsExportOptions();
        if (_options.MaxFiles < GeneratedEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Limit plików musi wynosić co najmniej {GeneratedEntryCount}.");
        }

        if (_options.MaxTotalBytes <= 0 || _options.MaxFileBytes <= 0 || _options.MaxFileBytes > _options.MaxTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Limity bajtów muszą być dodatnie, a limit pliku nie może przekraczać limitu paczki.");
        }

        if (_options.MaxScannedEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Limit skanowanych wpisów musi być dodatni.");
        }
    }

    public DiagnosticsExportResult Export(
        string destination,
        DiagnosticsExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Build);
        ArgumentNullException.ThrowIfNull(request.StatusJson);

        cancellationToken.ThrowIfCancellationRequested();

        string archivePath = Path.GetFullPath(destination);
        if (!IsLocalPath(archivePath))
        {
            throw new NotSupportedException("Paczka diagnostyczna może być zapisana wyłącznie na dysku lokalnym.");
        }

        string? destinationDirectory = Path.GetDirectoryName(archivePath);
        if (destinationDirectory is null || !Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException("Katalog docelowy paczki diagnostycznej nie istnieje.");
        }

        if (IsUnsafePath(new DirectoryInfo(destinationDirectory)))
        {
            throw new NotSupportedException("Katalog docelowy prowadzi przez dowiązanie lub reparse point.");
        }

        if (File.Exists(archivePath) || Directory.Exists(archivePath))
        {
            throw new IOException($"Plik docelowy już istnieje: {archivePath}");
        }

        byte[] buildInfo = Serialize(new BuildInfoDocument(request.Build.Version, request.Build.Commit));
        byte[] status = ValidateAndNormalizeStatus(request.StatusJson);
        byte[] acceptanceForm = Utf8NoBom.GetBytes(CreateAcceptanceForm(request));

        ValidateGeneratedEntry("build-info.json", buildInfo);
        ValidateGeneratedEntry("status.json", status);
        ValidateGeneratedEntry("formularz-odbioru.md", acceptanceForm);

        var warnings = new List<string>();
        var omissions = new List<ManifestOmission>();
        var candidates = new List<CandidateFile>();
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "build-info.json",
            "status.json",
            "formularz-odbioru.md",
            "manifest.json",
        };

        var scanBudget = new ScanBudget(_options.MaxScannedEntries);
        CollectLogs(request.LogsDirectory, archivePath, candidates, omissions, warnings, entryNames, scanBudget, cancellationToken);
        CollectCaptures(request.CapturesDirectory, archivePath, candidates, omissions, warnings, entryNames, scanBudget, cancellationToken);
        CollectHistory(request.HistoryPath, archivePath, candidates, omissions, warnings, entryNames, scanBudget);
        if (request.UpdatesDirectory is { } updates && Directory.Exists(updates))
            CollectUpdateLogs(updates, "updates", archivePath, candidates, omissions, warnings, entryNames, scanBudget, cancellationToken);
        if (request.UpdateTaskDirectory is { } updateTask && Directory.Exists(updateTask))
            CollectUpdateLogs(updateTask, "updates-admin", archivePath, candidates, omissions, warnings, entryNames, scanBudget, cancellationToken);

        candidates.Sort(static (left, right) =>
        {
            int byDate = right.LastWriteTime.CompareTo(left.LastWriteTime);
            return byDate != 0 ? byDate : StringComparer.Ordinal.Compare(left.EntryName, right.EntryName);
        });

        long generatedBytes = buildInfo.LongLength + status.LongLength + acceptanceForm.LongLength;
        if (generatedBytes > _options.MaxTotalBytes)
        {
            throw new InvalidDataException("Metadane diagnostyczne przekraczają limit rozmiaru paczki.");
        }

        List<CandidateFile> selected = SelectWithinLimits(candidates, generatedBytes, omissions, warnings);
        List<PreparedFile> prepared = PrepareFiles(selected, archivePath, omissions, warnings, cancellationToken);

        try
        {
            byte[] manifest = FitManifestWithinLimit(
                request,
                buildInfo,
                status,
                acceptanceForm,
                prepared,
                omissions,
                warnings);
            ValidateGeneratedEntry("manifest.json", manifest);

            bool destinationCreated = false;
            FileStream? destinationStream = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                destinationStream = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                destinationCreated = true;

                using (destinationStream)
                using (var archive = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    WriteBytes(archive, "build-info.json", buildInfo, cancellationToken);
                    WriteBytes(archive, "status.json", status, cancellationToken);
                    WriteBytes(archive, "formularz-odbioru.md", acceptanceForm, cancellationToken);

                    foreach (PreparedFile file in prepared)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _options.BeforeCopyFileForTests?.Invoke(file.Candidate.EntryName);
                        WriteFileSnapshot(archive, file, cancellationToken);
                    }

                    WriteBytes(archive, "manifest.json", manifest, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                destinationStream?.Dispose();
                if (destinationCreated)
                {
                    TryDeleteIncompleteArchive(archivePath);
                }

                throw;
            }

            return new DiagnosticsExportResult(archivePath, warnings.AsReadOnly(), GeneratedEntryCount + prepared.Count);
        }
        finally
        {
            foreach (PreparedFile file in prepared)
            {
                file.Stream.Dispose();
            }
        }
    }

    private byte[] ValidateAndNormalizeStatus(string json)
    {
        int byteCount = Utf8NoBom.GetByteCount(json);
        if (byteCount > _options.MaxFileBytes)
        {
            throw new InvalidDataException("Migawka Statusu przekracza limit pojedynczego pliku.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            return JsonSerializer.SerializeToUtf8Bytes(document.RootElement, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Migawka Statusu nie jest prawidłowym dokumentem JSON.", exception);
        }
    }

    private void ValidateGeneratedEntry(string name, byte[] contents)
    {
        if (contents.LongLength > _options.MaxFileBytes)
        {
            throw new InvalidDataException($"Plik {name} przekracza limit pojedynczego pliku.");
        }
    }

    private void CollectUpdateLogs(
        string path,
        string prefix,
        string archivePath,
        List<CandidateFile> candidates,
        List<ManifestOmission> omissions,
        List<string> warnings,
        HashSet<string> entryNames,
        ScanBudget scanBudget,
        CancellationToken cancellationToken)
    {
        if (scanBudget.Exhausted)
        {
            ReportScanLimit(scanBudget, omissions, warnings);
            return;
        }

        if (!TryGetSafeDirectory(path, "aktualizacji", omissions, warnings, out DirectoryInfo? root))
        {
            return;
        }

        try
        {
            foreach (FileSystemInfo item in root.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryTakeScanEntry(scanBudget, omissions, warnings))
                {
                    break;
                }

                bool allowed = item is FileInfo && item.Name.StartsWith("install-", StringComparison.OrdinalIgnoreCase)
                    && (item.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                        || item.Name.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    AddOmission(omissions, $"{prefix}/{SafeName(item.Name)}", "Poza allowlistą install-*.log / install-*.result.json.");
                    continue;
                }

                AddCandidate((FileInfo)item, $"{prefix}/{item.Name}", archivePath, candidates, omissions, warnings, entryNames);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddWarning(warnings, $"Nie udało się odczytać katalogu aktualizacji: {exception.Message}");
            AddOmission(omissions, prefix + "/", "Katalogu nie można odczytać.");
        }
    }

    private void CollectLogs(
        string path,
        string archivePath,
        List<CandidateFile> candidates,
        List<ManifestOmission> omissions,
        List<string> warnings,
        HashSet<string> entryNames,
        ScanBudget scanBudget,
        CancellationToken cancellationToken)
    {
        if (scanBudget.Exhausted)
        {
            ReportScanLimit(scanBudget, omissions, warnings);
            return;
        }

        if (!TryGetSafeDirectory(path, "logów", omissions, warnings, out DirectoryInfo? root))
        {
            return;
        }

        try
        {
            foreach (FileSystemInfo item in root.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryTakeScanEntry(scanBudget, omissions, warnings))
                {
                    break;
                }

                if (item is not FileInfo file || !file.Name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ||
                    !file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                {
                    AddOmission(omissions, $"logs/{SafeName(item.Name)}", "Poza allowlistą app-*.log.");
                    continue;
                }

                AddCandidate(file, $"logs/{file.Name}", archivePath, candidates, omissions, warnings, entryNames);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddWarning(warnings, $"Nie udało się odczytać katalogu logów: {exception.Message}");
            AddOmission(omissions, "logs/", "Katalogu nie można odczytać.");
        }
    }

    private void CollectCaptures(
        string path,
        string archivePath,
        List<CandidateFile> candidates,
        List<ManifestOmission> omissions,
        List<string> warnings,
        HashSet<string> entryNames,
        ScanBudget scanBudget,
        CancellationToken cancellationToken)
    {
        if (scanBudget.Exhausted)
        {
            ReportScanLimit(scanBudget, omissions, warnings);
            return;
        }

        if (!TryGetSafeDirectory(path, "zrzutów", omissions, warnings, out DirectoryInfo? root))
        {
            return;
        }

        var pending = new Stack<(DirectoryInfo Directory, string Relative)>();
        pending.Push((root, string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanBudget.Exhausted)
            {
                ReportScanLimit(scanBudget, omissions, warnings);
                break;
            }

            (DirectoryInfo directory, string relative) = pending.Pop();
            try
            {
                foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryTakeScanEntry(scanBudget, omissions, warnings))
                    {
                        pending.Clear();
                        break;
                    }

                    string childRelative = string.IsNullOrEmpty(relative) ? item.Name : $"{relative}/{item.Name}";
                    string logical = LogicalPath("captures", childRelative);

                    if (IsReparsePoint(item))
                    {
                        AddOmission(omissions, logical, "Pominięto dowiązanie lub reparse point.");
                        AddWarning(warnings, $"Pominięto {logical}: dowiązanie lub reparse point.");
                        continue;
                    }

                    if (item is DirectoryInfo childDirectory)
                    {
                        if (IsSensitiveName(childDirectory.Name))
                        {
                            AddOmission(omissions, logical, "Pominięto katalog o nazwie zastrzeżonej.");
                        }
                        else
                        {
                            pending.Push((childDirectory, childRelative));
                        }

                        continue;
                    }

                    if (item is not FileInfo file || !file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || IsSensitiveName(file.Name))
                    {
                        AddOmission(omissions, logical, "Poza allowlistą plików PNG.");
                        continue;
                    }

                    AddCandidate(file, logical, archivePath, candidates, omissions, warnings, entryNames);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, $"Nie udało się odczytać części katalogu zrzutów: {exception.Message}");
                AddOmission(omissions, LogicalPath("captures", relative), "Katalogu nie można odczytać.");
            }
        }
    }

    private void CollectHistory(
        string path,
        string archivePath,
        List<CandidateFile> candidates,
        List<ManifestOmission> omissions,
        List<string> warnings,
        HashSet<string> entryNames,
        ScanBudget scanBudget)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AddWarning(warnings, "Nie podano ścieżki historii; paczka nie zawiera history.csv.");
            AddOmission(omissions, "history/history.csv", "Nie podano ścieżki.");
            return;
        }

        if (!TryTakeScanEntry(scanBudget, omissions, warnings))
        {
            AddOmission(omissions, "history/history.csv", "Nie sprawdzono z powodu limitu skanu.");
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddWarning(warnings, "Ścieżka historii jest nieprawidłowa; paczka nie zawiera historii.");
            AddOmission(omissions, "history/history.csv", "Nieprawidłowa ścieżka.");
            return;
        }

        if (!IsLocalPath(fullPath))
        {
            AddWarning(warnings, "Sieciowa ścieżka historii została odrzucona; eksport korzysta wyłącznie z danych lokalnych.");
            AddOmission(omissions, "history/history.csv", "Ścieżka sieciowa jest niedozwolona.");
            return;
        }

        if (!File.Exists(fullPath))
        {
            AddWarning(warnings, "Nie znaleziono pliku historii; utworzono paczkę bez history.csv.");
            AddOmission(omissions, "history/history.csv", "Plik nie istnieje.");
            return;
        }

        var file = new FileInfo(fullPath);
        if (!file.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || IsSensitiveName(file.Name))
        {
            AddWarning(warnings, "Plik historii ma niedozwoloną nazwę lub rozszerzenie i został pominięty.");
            AddOmission(omissions, "history/history.csv", "Dozwolony jest wyłącznie bezpieczny plik CSV.");
            return;
        }

        if (IsUnsafePath(file))
        {
            AddWarning(warnings, "Plik historii prowadzi przez dowiązanie lub reparse point i został pominięty.");
            AddOmission(omissions, "history/history.csv", "Dowiązanie lub reparse point.");
            return;
        }

        AddCandidate(file, "history/history.csv", archivePath, candidates, omissions, warnings, entryNames);
    }

    private void AddCandidate(
        FileInfo file,
        string logicalSource,
        string archivePath,
        List<CandidateFile> candidates,
        List<ManifestOmission> omissions,
        List<string> warnings,
        HashSet<string> entryNames)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(file.FullName);
            file.Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddWarning(warnings, $"Pominięto {logicalSource}: nie można odczytać metadanych pliku.");
            AddOmission(omissions, logicalSource, "Nie można odczytać metadanych pliku.");
            return;
        }

        if (PathsEqual(fullPath, archivePath))
        {
            AddWarning(warnings, "Pominięto plik docelowy eksportu, aby paczka nie próbowała spakować samej siebie.");
            AddOmission(omissions, logicalSource, "Plik docelowy eksportu.");
            return;
        }

        if (IsUnsafePath(file))
        {
            AddWarning(warnings, $"Pominięto {logicalSource}: dowiązanie lub reparse point.");
            AddOmission(omissions, logicalSource, "Dowiązanie lub reparse point.");
            return;
        }

        string entryName = NormalizeEntryName(logicalSource);
        if (!entryNames.Add(entryName))
        {
            AddWarning(warnings, $"Pominięto {logicalSource}: kolizja nazwy w archiwum.");
            AddOmission(omissions, logicalSource, "Kolizja nazwy w archiwum.");
            return;
        }

        long length;
        DateTimeOffset lastWrite;
        try
        {
            length = file.Length;
            lastWrite = file.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddWarning(warnings, $"Pominięto {logicalSource}: nie można odczytać rozmiaru pliku.");
            AddOmission(omissions, logicalSource, "Nie można odczytać rozmiaru pliku.");
            return;
        }

        candidates.Add(new CandidateFile(fullPath, logicalSource, entryName, length, lastWrite));
    }

    private List<CandidateFile> SelectWithinLimits(
        IReadOnlyList<CandidateFile> candidates,
        long generatedBytes,
        List<ManifestOmission> omissions,
        List<string> warnings)
    {
        int availableFiles = _options.MaxFiles - GeneratedEntryCount;
        long remainingBytes = _options.MaxTotalBytes - generatedBytes;
        var selected = new List<CandidateFile>(Math.Min(availableFiles, candidates.Count));

        foreach (CandidateFile candidate in candidates)
        {
            if (candidate.Length > _options.MaxFileBytes)
            {
                AddOmission(omissions, candidate.LogicalSource, $"Przekracza limit {_options.MaxFileBytes} B na plik.");
                continue;
            }

            if (selected.Count >= availableFiles)
            {
                AddOmission(omissions, candidate.LogicalSource, $"Przekroczono limit {_options.MaxFiles} plików paczki.");
                continue;
            }

            if (candidate.Length > remainingBytes)
            {
                AddOmission(omissions, candidate.LogicalSource, $"Przekroczono limit {_options.MaxTotalBytes} B paczki.");
                continue;
            }

            selected.Add(candidate);
            remainingBytes -= candidate.Length;
        }

        if (selected.Count != candidates.Count)
        {
            AddWarning(warnings, "Część danych pominięto zgodnie z limitami lub allowlistą; szczegóły są w manifest.json.");
        }

        return selected;
    }

    private List<PreparedFile> PrepareFiles(
        IReadOnlyList<CandidateFile> selected,
        string archivePath,
        List<ManifestOmission> omissions,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedFile>(selected.Count);
        try
        {
            foreach (CandidateFile candidate in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    var file = new FileInfo(candidate.FullPath);
                    if (PathsEqual(candidate.FullPath, archivePath) || IsUnsafePath(file))
                    {
                        AddWarning(warnings, $"Pominięto {candidate.LogicalSource}: plik stał się niebezpiecznym dowiązaniem.");
                        AddOmission(omissions, candidate.LogicalSource, "Dowiązanie lub reparse point.");
                        continue;
                    }

                    stream = new FileStream(
                        candidate.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 64 * 1024,
                        FileOptions.SequentialScan);
                    long snapshotLength = stream.Length;
                    if (snapshotLength > _options.MaxFileBytes)
                    {
                        stream.Dispose();
                        AddWarning(warnings, $"Pominięto {candidate.LogicalSource}: plik urósł ponad limit.");
                        AddOmission(omissions, candidate.LogicalSource, "Plik urósł ponad limit pojedynczego pliku.");
                        continue;
                    }

                    prepared.Add(new PreparedFile(candidate, stream, snapshotLength));
                    stream = null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    stream?.Dispose();
                    AddWarning(warnings, $"Pominięto {candidate.LogicalSource}: pliku nie można otworzyć do odczytu.");
                    AddOmission(omissions, candidate.LogicalSource, "Pliku nie można otworzyć do odczytu.");
                }
            }

            return prepared;
        }
        catch
        {
            foreach (PreparedFile file in prepared)
            {
                file.Stream.Dispose();
            }

            throw;
        }
    }

    private byte[] FitManifestWithinLimit(
        DiagnosticsExportRequest request,
        byte[] buildInfo,
        byte[] status,
        byte[] acceptanceForm,
        List<PreparedFile> prepared,
        List<ManifestOmission> omissions,
        List<string> warnings)
    {
        while (true)
        {
            byte[] manifest = CreateManifest(request, buildInfo, status, acceptanceForm, prepared, omissions, warnings);
            long total = buildInfo.LongLength + status.LongLength + acceptanceForm.LongLength + manifest.LongLength +
                         prepared.Sum(file => file.SnapshotLength);
            if (manifest.LongLength <= _options.MaxFileBytes && total <= _options.MaxTotalBytes)
            {
                return manifest;
            }

            if (prepared.Count == 0)
            {
                throw new InvalidDataException("Manifest i wymagane metadane nie mieszczą się w limitach paczki diagnostycznej.");
            }

            PreparedFile removed = prepared[^1];
            prepared.RemoveAt(prepared.Count - 1);
            removed.Stream.Dispose();
            AddOmission(omissions, removed.Candidate.LogicalSource, "Pominięto, aby zmieścić manifest w limicie paczki.");
            AddWarning(warnings, "Najstarsze dane pominięto, aby manifest zmieścił się w limicie paczki.");
        }
    }

    private byte[] CreateManifest(
        DiagnosticsExportRequest request,
        byte[] buildInfo,
        byte[] status,
        byte[] acceptanceForm,
        IReadOnlyList<PreparedFile> prepared,
        IReadOnlyList<ManifestOmission> omissions,
        IReadOnlyList<string> warnings)
    {
        var included = new List<ManifestIncluded>
        {
            new("build-info.json", "tożsamość builda", buildInfo.LongLength, null),
            new("status.json", "jawna migawka Statusu", status.LongLength, null),
            new("formularz-odbioru.md", "formularz odbioru", acceptanceForm.LongLength, null),
        };
        included.AddRange(prepared.Select(file => new ManifestIncluded(
            file.Candidate.EntryName,
            file.Candidate.LogicalSource,
            file.SnapshotLength,
            file.Candidate.LastWriteTime)));
        included.Add(new ManifestIncluded("manifest.json", "spis paczki", null, null));

        var document = new ManifestDocument(
            SchemaVersion: 1,
            CreatedAt: request.CreatedAt,
            AcceptanceStatus: "NIE ODEBRANO",
            Notice: "Paczka służy do diagnostyki. Nie potwierdza gotowości produkcyjnej ani odbioru prób na hali.",
            Limits: new ManifestLimits(_options.MaxFiles, _options.MaxTotalBytes, _options.MaxFileBytes, _options.MaxScannedEntries),
            Included: included,
            Omitted: omissions,
            Warnings: warnings);
        return Serialize(document);
    }

    private static void WriteBytes(ZipArchive archive, string entryName, byte[] contents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ZipArchiveEntry entry = archive.CreateEntry(NormalizeEntryName(entryName), CompressionLevel.Optimal);
        using Stream output = entry.Open();
        output.Write(contents);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void WriteFileSnapshot(ZipArchive archive, PreparedFile file, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(file.Candidate.EntryName, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        byte[] buffer = new byte[64 * 1024];
        long remaining = file.SnapshotLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = file.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException($"Plik {file.Candidate.LogicalSource} został skrócony podczas eksportu.");
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private bool TryGetSafeDirectory(
        string path,
        string description,
        List<ManifestOmission> omissions,
        List<string> warnings,
        [NotNullWhen(true)] out DirectoryInfo? directory)
    {
        directory = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            AddWarning(warnings, $"Nie podano katalogu {description}; paczka powstała bez tych danych.");
            AddOmission(omissions, $"{description}/", "Nie podano ścieżki.");
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddWarning(warnings, $"Ścieżka katalogu {description} jest nieprawidłowa.");
            AddOmission(omissions, $"{description}/", "Nieprawidłowa ścieżka.");
            return false;
        }

        if (!IsLocalPath(fullPath))
        {
            AddWarning(warnings, $"Sieciowa ścieżka katalogu {description} została odrzucona.");
            AddOmission(omissions, $"{description}/", "Ścieżka sieciowa jest niedozwolona.");
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            AddWarning(warnings, $"Nie znaleziono katalogu {description}; paczka powstała bez tych danych.");
            AddOmission(omissions, $"{description}/", "Katalog nie istnieje.");
            return false;
        }

        directory = new DirectoryInfo(fullPath);
        if (IsSensitiveName(directory.Name))
        {
            AddWarning(warnings, $"Katalog {description} ma zastrzeżoną nazwę i został pominięty.");
            AddOmission(omissions, $"{description}/", "Katalog o nazwie zastrzeżonej.");
            directory = null;
            return false;
        }

        if (IsUnsafePath(directory))
        {
            AddWarning(warnings, $"Katalog {description} prowadzi przez dowiązanie lub reparse point i został pominięty.");
            AddOmission(omissions, $"{description}/", "Dowiązanie lub reparse point w ścieżce.");
            directory = null;
            return false;
        }

        return true;
    }

    private static bool IsUnsafePath(FileSystemInfo item)
    {
        if (IsReparsePoint(item))
        {
            return true;
        }

        DirectoryInfo? directory = item is DirectoryInfo directoryItem ? directoryItem.Parent : ((FileInfo)item).Directory;
        while (directory is not null)
        {
            if (IsReparsePoint(directory))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private static bool IsReparsePoint(FileSystemInfo item)
    {
        try
        {
            item.Refresh();
            return (item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsSensitiveName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("secrets", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("secretstore", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("secret-store", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("config", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("rules", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("test-ekran", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEntryName(string value)
    {
        string normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Niedozwolona ścieżka wpisu ZIP.");
        }

        return normalized;
    }

    private static string LogicalPath(string prefix, string relative) =>
        string.IsNullOrEmpty(relative) ? $"{prefix}/" : NormalizeEntryName($"{prefix}/{relative}");

    private static string SafeName(string name) => name.Replace('\\', '_').Replace('/', '_');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsLocalPath(string fullPath)
    {
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryTakeScanEntry(
        ScanBudget budget,
        List<ManifestOmission> omissions,
        List<string> warnings)
    {
        if (!budget.TryTake())
        {
            ReportScanLimit(budget, omissions, warnings);
            return false;
        }

        return true;
    }

    private static void ReportScanLimit(
        ScanBudget budget,
        List<ManifestOmission> omissions,
        List<string> warnings)
    {
        if (budget.LimitReported)
        {
            return;
        }

        budget.LimitReported = true;
        AddWarning(warnings, $"Przerwano wybieranie danych po sprawdzeniu {budget.Limit} plików i katalogów.");
        AddOmission(omissions, "(pozostałe źródła)", $"Nie sprawdzono z powodu limitu skanu: {budget.Limit} wpisów.");
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning, StringComparer.Ordinal))
        {
            warnings.Add(warning);
        }
    }

    private static void AddOmission(List<ManifestOmission> omissions, string source, string reason)
    {
        if (omissions.Count < MaxOmissionDetails)
        {
            omissions.Add(new ManifestOmission(source.Length <= 512 ? source : source[..512], reason));
        }
        else if (omissions.Count == MaxOmissionDetails)
        {
            omissions.Add(new ManifestOmission("(pozostałe)", "Dalsze pominięcia zgrupowano, aby ograniczyć rozmiar manifestu."));
        }
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string CreateAcceptanceForm(DiagnosticsExportRequest request) =>
        $"""
        # Formularz odbioru VNC Alert

        Stan pakietu: **NIE ODEBRANO**
        Utworzono: {request.CreatedAt:O}
        Build: {request.Build.DisplayText}

        Ta paczka jest materiałem diagnostycznym. Nie potwierdza gotowości produkcyjnej.

        - [ ] NIE SPRAWDZONO — start aplikacji portable i widoczność ikony w zasobniku
        - [ ] NIE SPRAWDZONO — połączenie z Aidite 1–7 oraz świeżość obrazu w Statusie
        - [ ] NIE SPRAWDZONO — prawdziwy czerwony alarm, opis, dźwięk i „Idę sprawdzić”
        - [ ] NIE SPRAWDZONO — kolejny epizod alarmu po naprawie lub restarcie maszyny
        - [ ] NIE SPRAWDZONO — postój 100% przez dwie minuty i zniknięcie po starcie pracy
        - [ ] NIE SPRAWDZONO — ostrzeżenie o innym ekranie HMI i powrót na ekran główny
        - [ ] NIE SPRAWDZONO — restart aplikacji oraz dłuższa próba stabilności na hali

        Uwagi operatora:

        ........................................................................
        """;

    private static void TryDeleteIncompleteArchive(string archivePath)
    {
        try
        {
            File.Delete(archivePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CandidateFile(
        string FullPath,
        string LogicalSource,
        string EntryName,
        long Length,
        DateTimeOffset LastWriteTime);

    private sealed record PreparedFile(CandidateFile Candidate, FileStream Stream, long SnapshotLength);

    private sealed class ScanBudget(int limit)
    {
        private int _used;

        public int Limit { get; } = limit;

        public bool Exhausted => _used >= Limit;

        public bool LimitReported { get; set; }

        public bool TryTake()
        {
            if (Exhausted)
            {
                return false;
            }

            _used++;
            return true;
        }
    }

    private sealed record BuildInfoDocument(string Version, string? Commit);

    private sealed record ManifestLimits(int MaxFiles, long MaxTotalBytes, long MaxFileBytes, int MaxScannedEntries);

    private sealed record ManifestIncluded(string Path, string Source, long? Bytes, DateTimeOffset? LastWriteTime);

    private sealed record ManifestOmission(string Source, string Reason);

    private sealed record ManifestDocument(
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        string AcceptanceStatus,
        string Notice,
        ManifestLimits Limits,
        IReadOnlyList<ManifestIncluded> Included,
        IReadOnlyList<ManifestOmission> Omitted,
        IReadOnlyList<string> Warnings);
}
