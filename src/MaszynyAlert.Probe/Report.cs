using System.Text;

namespace MaszynyAlert.Probe;

/// <summary>
/// Zapis wyniku komendy jednocześnie na konsolę i do pliku <c>&lt;out&gt;\raport.txt</c> (dopisywanie,
/// UTF-8 z BOM — kodowanie zgodne z Excelem PL). Każde uruchomienie komendy dopisuje nowy nagłówek
/// <c>=== probe &lt;komenda&gt; &lt;data godzina&gt; ===</c>, więc historia wszystkich uruchomień zostaje w pliku.
/// </summary>
public sealed class Report : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public Report(string outDir, string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        string fullOutDir;
        try
        {
            Directory.CreateDirectory(outDir);
            fullOutDir = System.IO.Path.GetFullPath(outDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ProbeUsageException(
                $"Nie udało się utworzyć folderu wyjściowego \"{outDir}\": {ex.Message}");
        }

        Path = System.IO.Path.Combine(fullOutDir, "raport.txt");

        try
        {
            // UTF8Encoding(true) = z BOM — StreamWriter dopisuje BOM tylko przy tworzeniu nowego pliku
            // (gdy dopisujemy do istniejącego, pozycja strumienia nie jest zerowa i BOM nie jest powtarzany).
            _writer = new StreamWriter(Path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _writer.AutoFlush = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProbeUsageException($"Nie udało się otworzyć pliku raportu \"{Path}\": {ex.Message}");
        }

        _writer.WriteLine();
        _writer.WriteLine($"=== probe {commandName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    /// <summary>Pełna ścieżka do <c>raport.txt</c>.</summary>
    public string Path { get; }

    /// <summary>Wypisuje linię jednocześnie na konsolę i do raportu. Bezpieczne z wielu wątków naraz (np. <c>watch</c> — Task per maszyna).</summary>
    public void Line(string text = "")
    {
        lock (_lock)
        {
            Console.WriteLine(text);
            _writer.WriteLine(text);
        }
    }

    /// <summary>Wypisuje linię TYLKO do pliku raportu (np. szczegóły techniczne błędu).</summary>
    public void FileOnly(string text)
    {
        lock (_lock)
        {
            _writer.WriteLine(text);
        }
    }

    /// <summary>Wypisuje linię TYLKO na konsolę (np. bieżący pasek postępu, niepotrzebny w pliku).</summary>
    public void ConsoleOnly(string text)
    {
        lock (_lock)
        {
            Console.WriteLine(text);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
