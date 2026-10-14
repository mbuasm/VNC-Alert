namespace MaszynyAlert.Core.Config;

/// <summary>Sposób utrzymywania połączenia VNC z maszyną.</summary>
public enum ConnectionMode
{
    /// <summary>Jedno stałe połączenie, aktualizacje przyrostowe co PollIntervalMs (domyślny).</summary>
    Persistent,

    /// <summary>Połącz — pobierz jedną ramkę — rozłącz (co PollingModeIntervalMs). Dla serwerów, które
    /// nie znoszą dwóch równoczesnych klientów.</summary>
    Polling,
}

/// <summary>Konfiguracja pojedynczej maszyny (frezarki) — jeden wpis w sekcji "machines" pliku config.json.</summary>
public sealed record MachineConfig
{
    /// <summary>Identyfikator maszyny używany wewnętrznie (np. w kluczach alertów, nazwach plików). Bez spacji.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Adres IP lub nazwa hosta maszyny w sieci lokalnej.</summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 5900;

    /// <summary>Identyfikator profilu ekranu (klucz w sekcji "profiles"), opisującego wygląd HMI tej maszyny.</summary>
    public required string Profile { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Tryb połączenia VNC.</summary>
    public ConnectionMode Mode { get; init; } = ConnectionMode.Persistent;

    public bool RequiresPassword { get; init; } = true;

    public string? Username { get; init; }

    public string? RsaAesKeySha256 { get; init; }

    /// <summary>Dowolna notatka administratora (np. "bywa wyłączona") — nie wpływa na działanie programu.</summary>
    public string? Comment { get; init; }
}
