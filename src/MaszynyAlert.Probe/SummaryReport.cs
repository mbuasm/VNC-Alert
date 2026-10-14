using System.Text.Encodings.Web;
using System.Text.Json;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe;

/// <summary>Jedno rozłączenie maszyny w trakcie przebiegu (kiedy i dlaczego) — do <c>podsumowanie.json</c>.</summary>
public sealed record DisconnectRecord(DateTimeOffset Czas, string Przyczyna);

/// <summary>Status połączenia maszyny w podsumowaniu — jedno słowo, które decyduje o kodzie wyjścia (patrz <see cref="ProbeExitCodes"/>).</summary>
public static class MachineStatus
{
    /// <summary>Jeszcze nie próbowano (wartość domyślna).</summary>
    public const string Unknown = "-";

    /// <summary>Połączono co najmniej raz w tym przebiegu.</summary>
    public const string Connected = "połączono";

    /// <summary>Wyłączona w konfiguracji i nie wybrana wprost — nie próbowano.</summary>
    public const string Disabled = "wyłączona (config)";

    /// <summary>Błąd konfiguracji (np. brak profilu).</summary>
    public const string ConfigError = "błąd konfiguracji";

    /// <summary>Nie odpowiada na porcie VNC (TCP).</summary>
    public const string Unreachable = "nie odpowiada";

    /// <summary>Serwer wymaga hasła, którego nie podano.</summary>
    public const string PasswordRequired = "wymaga hasła";

    /// <summary>Serwer odrzucił podane hasło.</summary>
    public const string WrongPassword = "błędne hasło";

    /// <summary>Serwer odrzucił poświadczenia RSA-AES.</summary>
    public const string WrongCredentials = "błędne poświadczenia";

    /// <summary>Brak nazwy użytkownika do logowania systemowego RSA-AES.</summary>
    public const string MissingUsername = "brak nazwy użytkownika";

    /// <summary>Serwer oferuje tylko nieobsługiwane typy zabezpieczeń.</summary>
    public const string UnsupportedSecurity = "nieobsł. zabezpieczenia";

    /// <summary>Inny powód pominięcia.</summary>
    public const string Skipped = "pominięto";

    /// <summary>Status z powodu pominięcia (ten sam słownik, co kolumna tabeli <c>probe.exe info</c>).</summary>
    public static string FromSkipReason(string reason) => InfoCommand.SkipStatus(reason);
}

public static class ProbeExitCodes
{
    public const int AllConnected = 0;
    public const int Error = 1;
    public const int SomeConnected = 3;
    public const int NoneConnected = 4;

    /// <summary>
    /// Kod z listy wierszy: liczone są tylko maszyny, które były WYBRANE do przebiegu (status inny niż
    /// <see cref="MachineStatus.Disabled"/> i <see cref="MachineStatus.Unknown"/>). Brak takich wierszy = 4 (nie było z czym się łączyć).
    /// </summary>
    public static int FromSummary(SummaryReport summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        List<MachineSummary> considered = summary.Rows
            .Where(r => r.Status is not (MachineStatus.Disabled or MachineStatus.Unknown))
            .ToList();
        if (considered.Count == 0)
        {
            return NoneConnected;
        }

        int connected = considered.Count(r => r.Status == MachineStatus.Connected);
        if (connected == considered.Count)
        {
            return AllConnected;
        }

        return connected == 0 ? NoneConnected : SomeConnected;
    }
}

/// <summary>
/// Jeden wiersz tabeli podsumowującej (patrz <see cref="SummaryReport"/>) — obiekt mutowalny, żeby kolejne
/// kroki (<c>info</c> → <c>ocr</c> → <c>watch</c>) mogły dopisywać do niego swoje fakty bez przekazywania
/// całego rekordu w każdą stronę. Wartości domyślne to "-" (pole jeszcze nieustalone), tak jak w tabeli
/// z <c>probe.exe info</c>.
/// </summary>
public sealed class MachineSummary(string id)
{
    /// <summary>Identyfikator maszyny (jak w konfiguracji, <c>MachineConfig.Id</c>).</summary>
    public string Id { get; } = id;

    public string Name { get; set; } = id;

    /// <summary>Status połączenia (słownik <see cref="MachineStatus"/>); "-" dopóki nie próbowano.</summary>
    public string Status { get; set; } = MachineStatus.Unknown;

    /// <summary>Czytelny powód, gdy maszyna nie została połączona (pełny tekst z <see cref="ConnectOutcome.SkipReason"/>), albo "-".</summary>
    public string Reason { get; set; } = "-";

    /// <summary>Ostatni błąd zgłoszony dla tej maszyny w trakcie przebiegu (także po udanym połączeniu — np. zerwanie), albo "-".</summary>
    public string LastError { get; set; } = "-";

    /// <summary>Chwila pierwszego udanego połączenia w tym przebiegu, albo <see langword="null"/>.</summary>
    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>Rozłączenia w trakcie przebiegu (czas + przyczyna), w kolejności wystąpienia.</summary>
    public List<DisconnectRecord> Disconnects { get; } = [];

    /// <summary>Liczba pobranych pełnych klatek.</summary>
    public int Frames { get; set; }

    /// <summary>Liczba klatek uznanych za czarne / prawie czarne (patrz <c>Diagnoza.FrameMetrics</c>).</summary>
    public int BlackFrames { get; set; }

    /// <summary>Czas od nawiązania połączenia TCP do odebrania pierwszej pełnej klatki, w ms; <see langword="null"/>, gdy klatki nie było.</summary>
    public long? FirstFrameAfterMs { get; set; }

    /// <summary>Nazwa pulpitu zgłoszona przez serwer w <c>ServerInit</c>, albo "-".</summary>
    public string ServerName { get; set; } = "-";

    /// <summary>Surowy nagłówek wersji serwera (np. "RFB 003.008\n"), albo "-".</summary>
    public string RawVersionHeader { get; set; } = "-";

    /// <summary>Typy zabezpieczeń oferowane przez serwer, np. "2 (VncAuth), 1 (None)", albo "-".</summary>
    public string OfferedSecurity { get; set; } = "-";

    /// <summary>Kodowania faktycznie użyte przez serwer w aktualizacjach (np. "Hextile, Raw"), albo "-".</summary>
    public string Encodings { get; set; } = "-";

    /// <summary>Wersja protokołu RFB (np. "3.8"), albo "-", gdy maszyna nie odpowiedziała.</summary>
    public string Rfb { get; set; } = "-";

    /// <summary>Typ zabezpieczeń faktycznie użyty przy połączeniu (np. "VncAuth"), albo "-".</summary>
    public string Security { get; set; } = "-";

    /// <summary>Rozmiar ekranu, np. "1024x768", albo "-".</summary>
    public string Screen { get; set; } = "-";

    public string PixelFormat { get; set; } = "-";

    /// <summary>
    /// Wynik kotwiczenia pola postępu:"x,y,w,h (odczyt 79 %)" / "x,y,w,h (OCR nie odczytał
    /// liczby)" / "nie znaleziono" / "brak OCR" (silnik OCR niedostępny na tym komputerze) / "-" (krok
    /// <c>ocr</c> jeszcze nie uruchomiony dla tej maszyny w tym przebiegu).
    /// </summary>
    public string ProgressField { get; set; } = "-";

    /// <summary>Wynik kotwiczenia pola nazwy pliku pracy, w tym samym formacie jak <see cref="ProgressField"/> (bez "odczyt %").</summary>
    public string FileNameField { get; set; } = "-";

    /// <summary>
    /// Podsumowanie obserwacji okienka alertów w kroku <c>watch</c>, np. "zmian: 3, kolory
    /// tekstu: czerwony, żółty" / "bez zmian" / "-" (krok <c>watch</c> nie obserwował tej maszyny).
    /// </summary>
    public string AlertWindow { get; set; } = "-";

    /// <summary>Liczba wszystkich zrzutów zapisanych dla tej maszyny w tym przebiegu (błędy + okno alertów + okresowe + nowa praca).</summary>
    public int Saved { get; set; }

    /// <summary>Wpisuje fakty z udanego handshake'u (wspólne dla <c>info</c>, <c>watch</c> i <c>diagnoza</c>).</summary>
    public void ApplyServerInfo(RfbServerInfo info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);

        Status = MachineStatus.Connected;
        Reason = "-";
        ConnectedAt ??= now;
        Rfb = info.ProtocolVersion;
        Security = info.UsedSecurityType.ToString();
        Screen = $"{info.Width}x{info.Height}";
        PixelFormat = info.PixelFormat.Describe();
        ServerName = info.DesktopName;
        RawVersionHeader = info.RawVersionHeader ?? "-";
        OfferedSecurity = RfbFormat.FormatOfferedTypes(info.OfferedSecurityTypes);
    }

    /// <summary>Oznacza maszynę jako niepołączoną z powodu <paramref name="skipReason"/> (status tylko, gdy nie była jeszcze połączona w tym przebiegu).</summary>
    public void ApplySkip(string skipReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skipReason);

        LastError = skipReason;
        if (Status != MachineStatus.Connected)
        {
            Status = MachineStatus.FromSkipReason(skipReason);
            Reason = skipReason;
        }
    }

    /// <summary>Dopisuje rozłączenie (po udanym połączeniu) — status zostaje "połączono", bo maszyna raz odpowiedziała.</summary>
    public void AddDisconnect(DateTimeOffset when, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Disconnects.Add(new DisconnectRecord(when, reason));
        LastError = reason;
    }

    /// <summary>Dopisuje kodowania użyte przez serwer (bez duplikatów, w kolejności pierwszego użycia).</summary>
    public void NoteEncodings(IEnumerable<int> encodingsUsed)
    {
        ArgumentNullException.ThrowIfNull(encodingsUsed);

        var names = Encodings == "-" ? new List<string>() : Encodings.Split(", ").ToList();
        foreach (int e in encodingsUsed)
        {
            string name = RfbFormat.EncodingName(e);
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        Encodings = names.Count == 0 ? "-" : string.Join(", ", names);
    }
}

public sealed class SummaryReport
{
    private readonly Dictionary<string, MachineSummary> _rows = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly object _lock = new();

    /// <summary>
    /// Zwraca wiersz maszyny o danym <paramref name="id"/> do dalszego uzupełniania (tworzy go przy
    /// pierwszym wywołaniu) — kolejność wierszy w <see cref="Rows"/>/<see cref="Render"/> to kolejność
    /// PIERWSZEGO wywołania <see cref="Machine"/> dla danego id (czyli w praktyce kolejność z config.json,
    /// bo <c>info</c> jest zawsze pierwszym krokiem, który woła tę metodę). Bezpieczne z wielu wątków
    /// (<c>diagnoza</c> uzupełnia wiersze równolegle).
    /// </summary>
    public MachineSummary Machine(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (_lock)
        {
            if (!_rows.TryGetValue(id, out MachineSummary? row))
            {
                row = new MachineSummary(id);
                _rows[id] = row;
                _order.Add(id);
            }

            return row;
        }
    }

    /// <summary>Wszystkie wiersze, w kolejności opisanej w <see cref="Machine"/>.</summary>
    public IReadOnlyList<MachineSummary> Rows
    {
        get
        {
            lock (_lock)
            {
                return _order.Select(id => _rows[id]).ToList();
            }
        }
    }

    /// <summary>Renderuje tabelę (nagłówki + separator + wiersze) jako listę linii tekstu, do wypisania na konsolę/do raportu.</summary>
    public IEnumerable<string> Render()
    {
        var table = new SimpleTable(
            "maszyna", "status", "RFB", "zabezpieczenia", "ekran", "format piksela", "pole postępu", "nazwa pliku", "okno alertów", "zrzutów");

        foreach (MachineSummary row in Rows)
        {
            table.AddRow(row.Name, row.Status, row.Rfb, row.Security, row.Screen, row.PixelFormat, row.ProgressField, row.FileNameField, row.AlertWindow, row.Saved.ToString());
        }

        return table.Render();
    }

    public string RenderJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // polskie znaki czytelne w pliku, nie jako \uXXXX
        };

        var byId = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (MachineSummary row in Rows)
        {
            byId[row.Id] = new
            {
                name = row.Name,
                status = row.Status,
                powod = row.Reason,
                ostatniBlad = row.LastError,
                polaczonoO = row.ConnectedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                rozlaczenia = row.Disconnects.Select(d => new { czas = d.Czas.ToString("yyyy-MM-dd HH:mm:ss"), przyczyna = d.Przyczyna }).ToList(),
                klatek = row.Frames,
                czarnychKlatek = row.BlackFrames,
                pierwszaKlatkaPo = row.FirstFrameAfterMs,
                serwerNazwa = row.ServerName,
                wersjaRfbNaglowek = row.RawVersionHeader,
                typyZabezpieczen = new { oferowane = row.OfferedSecurity, wybrany = row.Security },
                kodowania = row.Encodings,
                rfb = row.Rfb,
                security = row.Security,
                screen = row.Screen,
                pixelFormat = row.PixelFormat,
                progressField = row.ProgressField,
                fileNameField = row.FileNameField,
                alertWindow = row.AlertWindow,
                saved = row.Saved,
            };
        }

        return JsonSerializer.Serialize(byId, options);
    }
}
