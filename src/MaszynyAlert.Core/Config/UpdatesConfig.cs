namespace MaszynyAlert.Core.Config;

public sealed record UpdatesConfig
{
    public bool Enabled { get; init; } = true;

    public string Url { get; init; } = "https://wydania.mbuas.pl/maszyny-alert/";

    /// <summary>
    /// Co ile minut sprawdzać katalog (5–1440). Godzina wystarcza: nowe wydanie i tak czeka na zgodę
    /// człowieka, a częstsze odpytywanie nic nie przyspiesza.
    /// </summary>
    public int CheckIntervalMinutes { get; init; } = 60;

    /// <summary>
    /// Identyfikator tokenu usługi Cloudflare Access (nagłówek <c>CF-Access-Client-Id</c>) albo
    /// <see langword="null"/>. Identyfikator jest jawny, więc może stać w <c>config.json</c>; sekret tokenu
    /// leży w magazynie sekretów stanowiska pod <see cref="Abstractions.SecretKeys.UpdatesAccessClientSecret"/>.
    /// Bez tokenu katalog odpowiada przekierowaniem na logowanie Google i program pokazuje „katalog wymaga
    /// logowania” — monitoring maszyn działa wtedy dalej normalnie.
    /// </summary>
    public string? AccessClientId { get; init; }

    /// <summary>
    /// Czy ten komputer instaluje sam wersję zatwierdzoną na innym komputerze grupy (po zakończeniu alarmu).
    /// <see langword="false"/> zostawia okno z przyciskiem „Zainstaluj” — instalacja dopiero po kliknięciu.
    /// </summary>
    public bool AutoInstallWhenApproved { get; init; } = true;
}
