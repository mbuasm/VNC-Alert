namespace MaszynyAlert.Core.Config;

/// <summary>Korzeń konfiguracji programu (całość pliku config.json).</summary>
public sealed record AppConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>Nazwa pliku ze słownikiem reguł błędów, względem katalogu konfiguracji.</summary>
    public string RulesFile { get; init; } = "rules.json";

    /// <summary>Lista monitorowanych maszyn.</summary>
    public List<MachineConfig> Machines { get; init; } = new();

    /// <summary>Profile ekranu, kluczowane identyfikatorem profilu (patrz <see cref="ScreenProfile.Id"/>).</summary>
    public Dictionary<string, ScreenProfile> Profiles { get; init; } = new();

    /// <summary>Ustawienia komunikacji między komputerami operatorów.</summary>
    public ClusterConfig Cluster { get; init; } = new();

    /// <summary>Odstępy czasowe pętli obserwatora i cyklu życia alertów.</summary>
    public TimingConfig Timing { get; init; } = new();

    /// <summary>Ustawienia OCR.</summary>
    public OcrConfig Ocr { get; init; } = new();

    /// <summary>Ścieżki plików danych aplikacji.</summary>
    public PathsConfig Paths { get; init; } = new();

    /// <summary>Ustawienia interfejsu użytkownika (okno alertu).</summary>
    public UiConfig Ui { get; init; } = new();

    /// <summary>Ustawienia materiału diagnostycznego (zrzuty zbierane „na zapas”).</summary>
    public DiagnosticsConfig Diagnostics { get; init; } = new();

    public UpdatesConfig Updates { get; init; } = new();
}
