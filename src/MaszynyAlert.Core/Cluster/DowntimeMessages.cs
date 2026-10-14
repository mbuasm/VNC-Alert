using MaszynyAlert.Core.History;

namespace MaszynyAlert.Core.Cluster;

public interface IDowntimeSync
{
    /// <summary>
    /// Najpóźniejsza znana obserwacja; <see langword="null"/>, gdy dziennik jest pusty. Od niej liczy się ogon
    /// <see cref="DowntimeSyncRequest.Since"/>, gdy partner odpowiedział już na naszą prośbę o PEŁNE okno od ostatniego nawiązania
    /// łączności (odpowiedź na prośbę ogona albo dalszego ciągu tego nie kończy) — patrz <see cref="GroupClusterNode"/>.
    /// </summary>
    DateTimeOffset? LatestObservedAt { get; }

    /// <summary>
    /// Wpisy, których ostatnia obserwacja nie jest wcześniejsza niż <paramref name="since"/>, uporządkowane
    /// rosnąco po <c>(LastObservedAt, EpisodeId)</c> — ta kolejność pozwala prosić o dalszy ciąg jednym
    /// znacznikiem (<see cref="DowntimeSyncReply.Cursor"/>). Zwraca najwyżej <paramref name="limit"/> wpisów;
    /// wołający poznaje po ich liczbie, że dalej coś jeszcze zostało.
    /// </summary>
    IReadOnlyList<DowntimeEpisode> EpisodesSince(DateTimeOffset since, int limit);

    /// <summary>Scala wpisy z innego komputera. Zwraca <see langword="true"/>, gdy cokolwiek się zmieniło.</summary>
    bool Apply(IReadOnlyList<DowntimeEpisode> episodes);
}

/// <summary>
/// Prośba o postoje partnera od chwili <see cref="Since"/> — droga, którą komputer
/// wyłączony w czasie postoju dostaje epizod, którego nigdy nie widział na żywo.
///
/// <para>Inaczej niż <see cref="InstructionsSyncRequest"/> (tam pytamy o CAŁY zbiór, bo odcisk nie mówi, czyj
/// wpis jest nowszy) tutaj <c>Since</c> ma sens: wpisy są rozłączne w czasie, a scalanie jest idempotentne,
/// więc okno „od najstarszej luki” wystarcza i nie każe przesyłać przy każdym pytaniu całej historii.</para>
/// </summary>
public sealed record DowntimeSyncRequest : PeerMessage
{
    public Guid RequestId { get; init; }

    public DateTimeOffset Since { get; init; }
}

/// <summary>Odpowiedź porcjami; <see cref="More"/> mówi, że po tej porcji jest jeszcze coś dalej.</summary>
public sealed record DowntimeSyncReply : PeerMessage
{
    public Guid RequestId { get; init; }

    /// <summary>Host, który prosił — pozostali odbiorcy tę odpowiedź pomijają (jak w <see cref="InstructionsSyncReply"/>).</summary>
    public string? ReplyTo { get; init; }

    public IReadOnlyList<DowntimeEpisode> Episodes { get; init; } = [];

    public bool More { get; init; }

    /// <summary>
    /// Ostatnia obserwacja z tej porcji — proszący wysyła z nią kolejne żądanie, gdy <see cref="More"/>.
    /// Wpisy o tym samym czasie mogą przyjść drugi raz; scalanie jest idempotentne, więc jest to bez skutku.
    /// </summary>
    public DateTimeOffset? Cursor { get; init; }
}
