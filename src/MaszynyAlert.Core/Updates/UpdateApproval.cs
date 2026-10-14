using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MaszynyAlert.Core.Updates;

public sealed partial record UpdateApproval
{
    /// <summary>Wersja zatwierdzona do instalacji, czteroczęściowa jak FileVersion EXE, np. <c>0.4.26.0</c>.</summary>
    public string Version { get; init; } = "";

    /// <summary>Nazwa pliku instalatora w katalogu wydań (bez ścieżki).</summary>
    public string FileName { get; init; } = "";

    /// <summary>Komputer, na którym człowiek kliknął zgodę (dla okna i diagnostyki).</summary>
    public string ApprovedBy { get; init; } = "";

    /// <summary>Czas kliknięcia (UTC po stronie zatwierdzającego).</summary>
    public DateTimeOffset ApprovedAt { get; init; }

    /// <summary>
    /// <see cref="Version"/> jako czteroczęściowa <see cref="System.Version"/> (brakujące części = 0, żeby
    /// „0.4.26” i „0.4.26.0” porównywały się jako równe); <c>0.0.0.0</c>, gdy tekst jest nieczytelny.
    /// Nie trafia do JSON-a — liczy się z <see cref="Version"/>.
    /// </summary>
    [JsonIgnore]
    public System.Version ParsedVersion =>
        ReleaseCatalog.TryBuildVersion(Version ?? "", out var parsed) ? parsed : new System.Version(0, 0, 0, 0);

    /// <summary>
    /// Czy zgodę wolno zapisać i wykonać: wersja parsowalna (<see cref="System.Version.TryParse(string?, out System.Version?)"/>),
    /// nazwa pliku <c>^[A-Za-z0-9][A-Za-z0-9._-]{0,120}$</c> bez „..”, autor 1–100 znaków bez znaków
    /// sterujących, czas ustawiony.
    /// </summary>
    public bool IsValid()
    {
        if (string.IsNullOrEmpty(Version) || !System.Version.TryParse(Version, out _)
            || !ReleaseCatalog.TryBuildVersion(Version, out _)) return false;
        if (string.IsNullOrEmpty(FileName) || !FileNameRegex().IsMatch(FileName) || FileName.Contains("..", StringComparison.Ordinal)) return false;
        if (string.IsNullOrWhiteSpace(ApprovedBy) || ApprovedBy.Length > 100 || ApprovedBy.Any(char.IsControl)) return false;
        return ApprovedAt != default;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}
