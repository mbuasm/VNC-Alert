using PdfSharp.Fonts;

namespace MaszynyAlert.Export;

/// <summary>
/// Czcionka PDF z systemu (PDFsharp 6, wersja „Core” bez GDI/WPF, wymaga własnego resolvera). Każda rodzina, o którą prosi
/// MigraDoc (styl dokumentu, wykresy, wewnętrzne domyślne „Arial”/„Courier New”), dostaje TĘ SAMĄ czcionkę z polskimi
/// znakami: pierwszą znalezioną z listy — Segoe UI, Arial (Windows), DejaVu Sans, Liberation Sans (Linux). Czcionka jest
/// osadzana w PDF, więc plik czyta się tak samo na każdym komputerze.
/// <para>
/// <see cref="GlobalFontSettings.FontResolver"/> wolno ustawić tylko raz na proces — <see cref="EnsureRegistered"/> robi to
/// pod zamkiem i zwraca błąd (bez wyjątku), gdy w systemie nie ma żadnej z czcionek.
/// </para>
/// </summary>
internal sealed class ExportFontResolver : IFontResolver
{
    /// <summary>Nazwa rodziny używana w dokumencie; resolver i tak odpowiada na każdą.</summary>
    public const string FamilyName = "VNC Alert Sans";

    private const string RegularFace = "vncalert-regular";
    private const string BoldFace = "vncalert-bold";

    private static readonly object Gate = new();
    private static ExportFontResolver? _registered;
    private static string? _registrationError;

    private readonly byte[] _regular;
    private readonly byte[]? _bold;

    private ExportFontResolver(byte[] regular, byte[]? bold)
    {
        _regular = regular;
        _bold = bold;
    }

    /// <summary>Plik czcionki użyty w PDF (do komunikatów i testów).</summary>
    public static string? RegularPath { get; private set; }

    /// <summary>
    /// Rejestruje resolver, jeśli jeszcze nie ma żadnego. <see langword="null"/> — gotowe; tekst — powód, dla którego PDF
    /// nie powstanie (brak czcionki albo inny resolver zarejestrowany wcześniej w tym procesie).
    /// </summary>
    public static string? EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered is not null) return null;
            if (_registrationError is not null) return _registrationError;
            if (GlobalFontSettings.FontResolver is not null and not ExportFontResolver)
                return null; // ktoś (np. test) zarejestrował własny — używamy go
            foreach (var (regular, bold) in Candidates())
            {
                if (!File.Exists(regular)) continue;
                try
                {
                    var resolver = new ExportFontResolver(File.ReadAllBytes(regular), File.Exists(bold) ? File.ReadAllBytes(bold) : null);
                    GlobalFontSettings.FontResolver = resolver;
                    _registered = resolver;
                    RegularPath = regular;
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Następny kandydat.
                }
                catch (InvalidOperationException ex)
                {
                    // PDFsharp nie pozwala zmienić resolvera po pierwszym użyciu czcionki.
                    _registrationError = "Nie udało się przygotować czcionki PDF: " + ex.Message;
                    return _registrationError;
                }
            }
            _registrationError = "Nie znaleziono czcionki z polskimi znakami (Segoe UI, Arial, DejaVu Sans ani Liberation Sans).";
            return _registrationError;
        }
    }

    private static IEnumerable<(string Regular, string Bold)> Candidates()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrEmpty(windows))
            windows = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
        yield return (Path.Combine(windows, "segoeui.ttf"), Path.Combine(windows, "segoeuib.ttf"));
        yield return (Path.Combine(windows, "arial.ttf"), Path.Combine(windows, "arialbd.ttf"));
        foreach (var dir in new[] { "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/TTF" })
            yield return (Path.Combine(dir, "DejaVuSans.ttf"), Path.Combine(dir, "DejaVuSans-Bold.ttf"));
        foreach (var dir in new[] { "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/liberation", "/usr/share/fonts/liberation-sans" })
            yield return (Path.Combine(dir, "LiberationSans-Regular.ttf"), Path.Combine(dir, "LiberationSans-Bold.ttf"));
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Pochyłe pismo nie występuje w raporcie; gdyby ktoś go użył — symulacja zamiast osobnego pliku.
        if (isBold) return _bold is not null ? new FontResolverInfo(BoldFace, false, isItalic) : new FontResolverInfo(RegularFace, true, isItalic);
        return new FontResolverInfo(RegularFace, false, isItalic);
    }

    public byte[]? GetFont(string faceName) => faceName == BoldFace && _bold is not null ? _bold : _regular;
}
