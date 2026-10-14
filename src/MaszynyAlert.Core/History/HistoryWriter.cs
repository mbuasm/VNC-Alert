using System.Globalization;
using System.Text;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.History;

/// <summary>
/// Dopisuje zdarzenia alertów do pliku CSV (separator ';', UTF-8 z BOM — czytelne wprost w polskim Excelu).
/// Wątkowo bezpieczna i nigdy nie wywala aplikacji. Gdy pliku nie da się zapisać (zablokowany w arkuszu,
/// tylko do odczytu, pełny dysk), wiersze czekają w pamięci (do <see cref="MaxPendingLines"/>) i trafiają do
/// pliku w kolejności przy następnym zapisie albo <see cref="Flush"/>; <see cref="Error"/> mówi o tym w Panelu.
/// </summary>
public sealed class HistoryWriter
{
    public const int MaxPendingLines = 10_000;

    private const string Header = "czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik";
    private static readonly char[] CharsRequiringQuotes = [';', '"', '\n', '\r'];

    private readonly string _csvPath;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly List<string> _pending = [];
    private string? _error;
    private FileStamp? _verifiedFile;

    private sealed record FileStamp(long Length, DateTime CreatedAt, DateTime WrittenAt);

    public HistoryWriter(string csvPath, IClock clock, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            throw new ArgumentException("Ścieżka pliku historii nie może być pusta.", nameof(csvPath));
        }

        _csvPath = csvPath;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Dopisuje jeden wiersz historii dla zdarzenia alertu. Gdy <paramref name="host"/>/<paramref name="user"/>
    /// nie podano, używane są dane potwierdzenia z samego alertu (<c>AckedByHost</c>/<c>AckedByUser</c>).
    /// </summary>
    public void Append(AlertEvent e, string? host = null, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        string line;
        try
        {
            line = FormatLine(e, host, user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udało się przygotować wpisu historii alertów.");
            return;
        }

        lock (_lock)
        {
            // Wiersz powstaje TERAZ (czas zdarzenia), a do pliku trafia razem z zaległymi — w kolejności.
            if (_pending.Count >= MaxPendingLines) _pending.RemoveAt(0);
            _pending.Add(line);
            WritePendingLocked();
        }
    }

    /// <summary>
    /// Liczba wierszy, które czekają w pamięci, bo pliku nie dało się zapisać (zablokowany przez inny program,
    /// tylko do odczytu, pełny dysk). Trafią do pliku przy następnym <see cref="Append"/> albo <see cref="Flush"/>.
    /// </summary>
    public int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>
    /// Komunikat dla administratora, gdy wpisy czekają w pamięci (do pokazania w Panelu); <see langword="null"/>,
    /// gdy wszystko jest zapisane.
    /// </summary>
    public string? Error
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>Próbuje dopisać zaległe wiersze (okresowo i przy zamykaniu programu). Nigdy nie rzuca.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_pending.Count > 0) WritePendingLocked();
        }
    }

    private void WritePendingLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_csvPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Zwykłe kolejne dopisanie nie skanuje rosnącego pliku. Pierwszy zapis, zmiana pliku
            // poza tym obiektem albo poprzedni błąd wymagają ponownego odczytu granicy CSV.
            FileStamp? verified = _verifiedFile;
            _verifiedFile = null;
            long? appended = verified is null ? null : AppendToVerifiedFile(verified);
            long writtenLength = appended ?? WriteAfterTailAnalysis();
            int written = _pending.Count;
            _pending.Clear();
            RememberWrittenFile(writtenLength);
            if (_error is not null)
            {
                _error = null;
                _logger.LogInformation("Plik historii alertów \"{Path}\" znów przyjmuje zapis — dopisano {Count} zaległych wpisów.", _csvPath, written);
            }
        }
        catch (Exception ex)
        {
            _verifiedFile = null;
            // Jeden wpis w logu na serię błędów, nie przy każdym zdarzeniu — plik otwarty w arkuszu potrafi
            // być zablokowany godzinami.
            if (_error is null)
            {
                _logger.LogError(ex, "Nie udało się dopisać wpisu historii alertów do pliku \"{Path}\" — wpisy czekają w pamięci.", _csvPath);
            }

            _error = $"Nie można zapisać historii alertów (history.csv) — plik jest zablokowany albo tylko do odczytu. "
                + $"{_pending.Count} wpisów czeka w pamięci i zostanie dopisanych po zwolnieniu pliku (zamknij go w arkuszu kalkulacyjnym).";
        }
    }

    private long? AppendToVerifiedFile(FileStamp verified)
    {
        using FileStream stream = new(_csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        // Trzymamy uchwyt z zapisem, bez współdzielenia zapisu — znacznik odczytany teraz obowiązuje do końca dopisania.
        if (ReadFileStamp() != verified || stream.Length != verified.Length) return null;
        return WriteLines(stream, new(verified.Length, ""));
    }

    private long WriteAfterTailAnalysis()
    {
        using FileStream stream = new(_csvPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        HistoryCsvTail.AppendPoint point = AnalyzeOrAppendAfter(stream);

        // Analyze musi skończyć cały odczyt przed pierwszą zmianą pliku. Wciąż trzymamy ten
        // sam uchwyt, bez współdzielenia zapisu/podmiany, więc granica nie może się przesunąć.
        if (point.Offset < stream.Length) stream.SetLength(point.Offset);
        stream.Position = point.Offset;
        return WriteLines(stream, point);
    }

    /// <summary>Dopisuje kolejkę od bieżącej pozycji (BOM i nagłówek tylko w nowym pliku); zwraca długość pliku po zapisie.</summary>
    private long WriteLines(FileStream stream, HistoryCsvTail.AppendPoint point)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: point.WriteHeader);
        using StreamWriter writer = new(stream, encoding, bufferSize: -1, leaveOpen: true);
        if (point.WriteHeader) writer.WriteLine(Header);
        else writer.Write(point.Separator);
        foreach (string pending in _pending) writer.WriteLine(pending);
        writer.Flush();
        return stream.Length;
    }

    private HistoryCsvTail.AppendPoint AnalyzeOrAppendAfter(FileStream stream)
    {
        try { return HistoryCsvTail.Analyze(stream, Header); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            _logger.LogWarning("Plik historii alertów \"{Path}\" ma wcześniejszą treść, której program nie rozpoznaje ({Reason}). "
                + "Zostaje bez zmian, a nowe wpisy są dopisywane od nowej linii.", _csvPath, ex.Message);
            return HistoryCsvTail.AfterUnrecognizedContent(stream);
        }
    }

    private FileStamp ReadFileStamp()
    {
        var info = new FileInfo(_csvPath);
        return new(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
    }

    private void RememberWrittenFile(long length)
    {
        // Na Windows mtime jest ostateczne po zamknięciu uchwytu. Błąd samego cache nie może
        // cofnąć udanego zapisu ani odtworzyć już opróżnionej kolejki — następny Append przeskanuje plik.
        try
        {
            FileStamp stamp = ReadFileStamp();
            _verifiedFile = stamp.Length == length ? stamp : null;
        }
        catch (IOException) { _verifiedFile = null; }
        catch (UnauthorizedAccessException) { _verifiedFile = null; }
    }

    private string FormatLine(AlertEvent e, string? host, string? user)
    {
        Alert alert = e.Alert;
        string? effectiveHost = host ?? alert.AckedByHost;
        string? effectiveUser = user ?? alert.AckedByUser;

        string[] fields =
        [
            _clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            alert.MachineName,
            EventName(e.Kind),
            alert.ShortText,
            alert.Category,
            FullTextWithReason(e),
            effectiveHost ?? string.Empty,
            effectiveUser ?? string.Empty,
        ];

        return string.Join(';', fields.Select(QuoteIfNeeded));
    }

    private static string FullTextWithReason(AlertEvent e)
    {
        string text = e.Alert.FullText ?? string.Empty;
        if (e.Kind is not (AlertEventKind.Raised or AlertEventKind.Escalated) || e.Reason is not { Length: > 0 } reason)
        {
            return text;
        }

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

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        if (value.IndexOfAny(CharsRequiringQuotes) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
