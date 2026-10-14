using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Cluster;

public interface IObservationSync
{
    /// <summary>Najpóźniejszy koniec znanego przedziału; <see langword="null"/>, gdy dziennik jest pusty.</summary>
    DateTimeOffset? LatestEnd { get; }

    /// <summary>
    /// Przedziały, których koniec nie jest wcześniejszy niż <paramref name="since"/>, uporządkowane rosnąco po
    /// końcu (potem maszyna, stan, początek) — ta kolejność pozwala prosić o dalszy ciąg jednym znacznikiem
    /// (<see cref="ObservationSyncReply.Cursor"/>). Najwyżej <paramref name="limit"/> wpisów.
    /// </summary>
    IReadOnlyList<ObservationInterval> IntervalsSince(DateTimeOffset since, int limit);

    /// <summary>Dodaje przedziały z innego komputera do sumy. Zwraca <see langword="true"/>, gdy cokolwiek się zmieniło.</summary>
    bool Apply(IReadOnlyList<ObservationInterval> intervals);
}

public sealed record ObservationSyncRequest : PeerMessage
{
    public Guid RequestId { get; init; }

    /// <summary>
    /// Komputer, którego pytamy — pozostali odbiorcy rozgłoszenia prośbę pomijają. Inaczej niż przy postojach okno
    /// <see cref="Since"/> liczy się osobno dla każdego partnera, więc odpowiedź kogoś innego na cudze okno
    /// mogłaby u proszącego uchodzić za komplet od tamtego komputera.
    /// </summary>
    public string? To { get; init; }

    /// <summary>Początek okna: przy pierwszej prośbie do partnera całe okno z konfiguracji, potem od ostatniej pełnej serii.</summary>
    public DateTimeOffset Since { get; init; }
}

/// <summary>Odpowiedź porcjami; <see cref="More"/> mówi, że po tej porcji jest jeszcze coś dalej.</summary>
public sealed record ObservationSyncReply : PeerMessage
{
    public Guid RequestId { get; init; }

    /// <summary>Host, który prosił — pozostali odbiorcy tę odpowiedź pomijają.</summary>
    public string? ReplyTo { get; init; }

    public IReadOnlyList<ObservationInterval> Intervals { get; init; } = [];

    public bool More { get; init; }

    /// <summary>
    /// Koniec ostatniego przedziału z tej porcji — proszący wysyła z nim kolejną prośbę, gdy <see cref="More"/>.
    /// Wpisy o tym samym końcu mogą przyjść drugi raz; suma zbiorów jest idempotentna, więc bez skutku.
    /// </summary>
    public DateTimeOffset? Cursor { get; init; }
}
