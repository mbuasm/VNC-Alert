using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Jedna klatka pobrana w przebiegu: kiedy (od pierwszego połączenia), ile serwer odpowiadał, pomiary, plik.</summary>
public sealed record FrameSample(
    DateTimeOffset Czas,
    double OdPolaczeniaSek,
    long OdpowiedzMs,
    FrameMeasurement Pomiar,
    string? Plik,
    bool ZmianaObrazu);

/// <summary>Punkt testu „ekran gaśnie po połączeniu”: zaplanowany offset, faktyczny czas i czy klatka była czarna.</summary>
public sealed record BlackTestPoint(int PlanSek, double FaktSek, bool Czarna, string Klasa);

/// <summary>Wynik testu drugiego klienta (<c>shared-test</c> automatycznie): czy drugi się połączył i czy pierwszy przeżył.</summary>
public sealed record SharedTestResult(bool DrugiPolaczony, bool PierwszyPrzezyl, string Opis);

/// <summary>Wynik ponownego połączenia po planowym rozłączeniu: czas do pierwszej klatki i czy ekran był czarny.</summary>
public sealed record ReconnectTestResult(bool Udane, long? PierwszaKlatkaMs, bool? CzarnaPoPowrocie, string Opis);

/// <summary>Pomiar częstości aktualizacji przyrostowych: ile klatek serwer przysłał w zadanym czasie.</summary>
public sealed record UpdateRateResult(double Sekund, int Aktualizacji, int BezZmian, string Opis);

/// <summary>Jedno uruchomienie OCR (język, kiedy, ile słów, początek tekstu, plik).</summary>
public sealed record OcrRun(DateTimeOffset Czas, string Jezyk, int Slow, string Poczatek, string Plik);

/// <summary>
/// Stan i wynik diagnostyki jednej maszyny (<see cref="MachineDiagnostic"/>) — wszystko, co potem czyta
/// <see cref="DiagnozaConclusions"/> i <see cref="DiagnozaSummary"/>. Mutowalny w trakcie przebiegu (jeden wątek
/// na maszynę), czytany po jego zakończeniu.
/// </summary>
public sealed class MachineDiagnosticResult(string id, string name, string host, int port, string profile)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Profile { get; } = profile;

    /// <summary>Status wg słownika <see cref="MachineStatus"/>.</summary>
    public string Status { get; set; } = MachineStatus.Unknown;

    /// <summary>Powód statusu innego niż „połączono” (pełny tekst), albo <see langword="null"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>Wszystkie próby TCP na port z konfiguracji, w kolejności.</summary>
    public List<TcpProbeResult> TcpProbes { get; } = [];

    /// <summary>Ostatni skan portów (gdy port z konfiguracji nie odpowiadał), albo <see langword="null"/>.</summary>
    public IReadOnlyList<TcpProbeResult>? PortScan { get; set; }

    /// <summary>Ile skanów portów wykonano.</summary>
    public int PortScans { get; set; }

    /// <summary>Fakty z udanego handshake'u (ostatniego), albo <see langword="null"/>.</summary>
    public RfbServerInfo? Server { get; set; }

    /// <summary>Czy serwer wymaga hasła (VncAuth/RSA-AES) — ustalone z handshake'u albo z powodu pominięcia.</summary>
    public bool? RequiresPassword { get; set; }

    /// <summary>Chwila pierwszego udanego połączenia RFB.</summary>
    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>Ile razy połączenie RFB się udało (pierwsze + ponowne).</summary>
    public int Connections { get; set; }

    /// <summary>Czas od połączenia TCP do pierwszej pełnej klatki przy PIERWSZYM połączeniu, w ms.</summary>
    public long? FirstFrameMs { get; set; }

    /// <summary>Wszystkie pobrane klatki (pomiary w pamięci; pliki wg <see cref="FrameStore"/>).</summary>
    public List<FrameSample> Frames { get; } = [];

    /// <summary>Punkty testu „ekran gaśnie po połączeniu”.</summary>
    public List<BlackTestPoint> BlackTests { get; } = [];

    /// <summary>Rozłączenia (planowe i nieplanowe) z przyczyną.</summary>
    public List<DisconnectRecord> Disconnects { get; } = [];

    /// <summary>Wynik ponownego połączenia po planowym rozłączeniu, albo <see langword="null"/>, gdy do niego nie doszło.</summary>
    public ReconnectTestResult? ReconnectTest { get; set; }

    /// <summary>Wynik automatycznego <c>shared-test</c>, albo <see langword="null"/>.</summary>
    public SharedTestResult? SharedTest { get; set; }

    /// <summary>Wynik pomiaru częstości aktualizacji, albo <see langword="null"/>.</summary>
    public UpdateRateResult? UpdateRate { get; set; }

    /// <summary>Kodowania użyte przez serwer (nazwy, bez duplikatów).</summary>
    public List<string> Encodings { get; } = [];

    /// <summary>Uruchomienia OCR.</summary>
    public List<OcrRun> OcrRuns { get; } = [];

    /// <summary>Języki OCR dostępne w tym przebiegu (tagi), pusta lista = brak OCR.</summary>
    public List<string> OcrLanguages { get; } = [];

    /// <summary>Wynik kotwiczenia ROI (<see cref="ScreenAnchors"/>) na pierwszej klatce, albo <see langword="null"/>.</summary>
    public MachineAnchors? Anchors { get; set; }

    /// <summary>Błędy niezwiązane z połączeniem (zapis pliku, OCR) — do dziennika i podsumowania.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Liczba klatek, w których obraz różnił się od poprzedniej (skrót).</summary>
    public int HashChanges => Frames.Count(f => f.ZmianaObrazu);

    /// <summary>Liczba klatek czarnych / prawie czarnych.</summary>
    public int BlackFrames => Frames.Count(f => f.Pomiar.IsBlack);

    /// <summary>Prawda, gdy maszyna połączyła się co najmniej raz.</summary>
    public bool EverConnected => ConnectedAt is not null;
}
