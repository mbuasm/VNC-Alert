using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Cluster;

public interface ISharedConfigSync
{
    /// <summary>Odcisk dokumentu — równy u obu stron znaczy „nie ma czego wysyłać”; <see langword="null"/> bez dokumentu.</summary>
    string? Hash { get; }

    /// <summary>Czas ostatniej zmiany; po nim poznajemy, kto ma wysyłać, a kto czekać.</summary>
    DateTimeOffset? EditedAt { get; }

    /// <summary>Dokument do rozesłania; <see langword="null"/>, gdy ten komputer nie ma nadpisania.</summary>
    SharedConfigDocument? Current { get; }

    /// <summary>Stosuje dokument z innego komputera. Zwraca <see langword="true"/>, gdy coś się zmieniło.</summary>
    bool Apply(SharedConfigDocument document);

    SharedConfigDocument? DetachMachines();

    /// <summary>Cofa <see cref="DetachMachines"/>: zmiany grupy nie udało się zatwierdzić, przełączniki wracają.</summary>
    void RestoreMachines(SharedConfigDocument before);
}

/// <summary>
/// Wspólna konfiguracja rozgłaszana zaraz po zapisaniu w Panelu i przy uzgadnianiu odcisków. Dokument jest
/// jeden i mały (poniżej 4 KB), więc — inaczej niż instrukcje naprawy — nie wymaga porcjowania ani osobnej
/// prośby o dane: wystarczy go wysłać.
/// </summary>
public sealed record SharedConfigMessage : PeerMessage
{
    public required SharedConfigDocument Document { get; init; }
}
