namespace MaszynyAlert.Core.Updates;

/// <summary>Wynik sprawdzenia katalogu wydań.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>Zainstalowana wersja jest najnowsza (albo nowsza od katalogu).</summary>
    UpToDate,

    /// <summary>W katalogu jest nowsza wersja (<see cref="UpdateCheckResult.Latest"/>).</summary>
    Available,

    /// <summary>
    /// Katalog odpowiedział przekierowaniem na logowanie albo 401/403 (Cloudflare Access bez tokenu usługi
    /// lub ze złym tokenem). To nie awaria programu — brakuje ustawienia.
    /// </summary>
    NeedsLogin,

    /// <summary>Serwer niedostępny, przekroczony czas, inny kod HTTP albo nieczytelny spis wydań.</summary>
    Unavailable,
}

/// <summary>
/// Wynik <see cref="IUpdateSource.CheckAsync"/>. <see cref="Error"/> niesie komunikat po polsku (dla
/// <see cref="UpdateCheckOutcome.NeedsLogin"/> — gotową podpowiedź dla operatora).
/// </summary>
/// <param name="Outcome">Rozstrzygnięcie.</param>
/// <param name="Latest">Najnowsze wydanie z katalogu albo <see langword="null"/>, gdy spisu nie udało się odczytać.</param>
/// <param name="Error">Opis problemu albo <see langword="null"/> przy udanym odczycie.</param>
/// <param name="CheckedAt">Czas sprawdzenia (UTC, z zegara programu).</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseEntry? Latest, string? Error, DateTimeOffset CheckedAt)
{
    public IReadOnlyList<ReleaseEntry> Releases { get; init; } = [];
}

/// <summary>
/// Źródło wydań programu. Implementacja produkcyjna: <see cref="HttpUpdateSource"/> (katalog na serwerze
/// wydań za Cloudflare Access); w testach — podstawiona.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// Odczytuje spis wydań i porównuje najnowsze z <paramref name="installed"/>. Błędy sieci i serwera
    /// zwraca w wyniku (<see cref="UpdateCheckOutcome.NeedsLogin"/>/<see cref="UpdateCheckOutcome.Unavailable"/>),
    /// nie jako wyjątek — monitoring maszyn ma działać dalej bez względu na serwer wydań. Wyjątek tylko przy
    /// anulowaniu przez <paramref name="ct"/>.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(Version installed, CancellationToken ct);

    /// <summary>
    /// Pobiera plik wydania do katalogu; weryfikuje rozmiar i SHA-256; zwraca pełną ścieżkę gotowego pliku.
    /// Plik już obecny i zgodny → zwraca od razu (bez pobierania). Postęp 0..1.
    /// </summary>
    Task<string> DownloadAsync(ReleaseEntry release, string targetDirectory, IProgress<double>? progress, CancellationToken ct);
}
