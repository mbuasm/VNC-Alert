namespace MaszynyAlert.Probe;

/// <summary>
/// Stan współdzielony między krokami JEDNEGO uruchomienia <c>probe.exe</c> — jedna instancja tworzona w
/// Program.cs i przekazywana każdej komendzie (<see cref="Commands.ICommand.RunAsync"/>). Dzięki temu
/// <c>probe.exe auto</c> (info → ocr → watch po sobie) pyta o hasło tylko raz na cały przebieg i wyznacza
/// obszary ekranu (kotwiczenie OCR) tylko raz, a samodzielne komendy (<c>info</c>, <c>ocr</c>, <c>watch</c>)
/// działają tak samo, tylko ze swoją własną, jednorazową sesją.
/// </summary>
public sealed class ProbeSession
{
    private readonly Dictionary<string, string?> _knownPasswords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MachineAnchors> _anchors = new(StringComparer.Ordinal);

    public ProbeSession(GlobalOptions global)
    {
        ArgumentNullException.ThrowIfNull(global);
        Connections = new ConnectionHelper(global.Password, global.NoAuthPrompt, this, global.Username);
    }

    /// <summary>Wspólne łączenie z maszynami VNC — pamięta hasła (per maszyna i "to samo dla wszystkich"), patrz <see cref="ConnectionHelper"/>.</summary>
    public ConnectionHelper Connections { get; }

    /// <summary>Zbierane w trakcie przebiegu fakty o każdej maszynie — jedna tabela podsumowująca na koniec.</summary>
    public SummaryReport Summary { get; } = new();

    /// <summary>Nasłuch testu sieci w tle, gdy uruchomiony przez <c>auto</c>; <see langword="null"/> poza <c>auto</c> albo gdy port był zajęty.</summary>
    public BusListener? Bus { get; set; }

    /// <summary>Prawda, gdy komendy są wykonywane jako kroki <c>probe.exe auto</c> — wtedy komendy nie wypisują „Co dalej: wpisz…", bo kolejny krok i tak rusza sam.</summary>
    public bool IsAutoRun { get; set; }

    /// <summary>Czy na koniec przebiegu spakować folder wynikowy do zip — ustawiane przez <c>auto</c> (Program.cs czyta to w <c>finally</c>).</summary>
    public bool ZipRequested { get; set; }

    /// <summary>
    /// Zapamiętuje hasło, które faktycznie zadziałało dla danej maszyny w tym przebiegu (albo
    /// <see langword="null"/>, gdy maszyna nie wymaga hasła) — kolejne połączenie z tą samą maszyną
    /// (np. w kroku <c>watch</c> po <c>info</c>/<c>ocr</c>) użyje go bez ponownego pytania operatora.
    /// </summary>
    public void RememberPassword(string machineId, string? password) => _knownPasswords[machineId] = password;

    /// <summary>Prawda, gdy dla tej maszyny już wiadomo (w tym przebiegu), jakiego hasła (albo braku hasła) użyć.</summary>
    public bool HasKnownPassword(string machineId) => _knownPasswords.ContainsKey(machineId);

    /// <summary>Zapamiętane hasło dla tej maszyny (patrz <see cref="HasKnownPassword"/>) — sprawdź <see cref="HasKnownPassword"/> zamiast porównywać z <see langword="null"/>, bo "brak hasła" to też prawidłowa, zapamiętana odpowiedź.</summary>
    public string? KnownPassword(string machineId) => _knownPasswords.TryGetValue(machineId, out string? p) ? p : null;

    /// <summary>Zapamiętuje wynik kotwiczenia OCR dla danej maszyny — <c>watch</c> z niego korzysta, żeby nie liczyć go ponownie.</summary>
    public void SetAnchors(string machineId, MachineAnchors anchors) => _anchors[machineId] = anchors;

    /// <summary>Wynik kotwiczenia OCR zapamiętany wcześniej w tym przebiegu (przez <c>ocr</c>) dla danej maszyny, albo <see langword="null"/>, gdy jeszcze nie liczony.</summary>
    public MachineAnchors? GetAnchors(string machineId) => _anchors.TryGetValue(machineId, out MachineAnchors? a) ? a : null;
}
