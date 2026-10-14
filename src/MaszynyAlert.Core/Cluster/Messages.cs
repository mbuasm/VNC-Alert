using System.Text.Json.Serialization;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.Core.Cluster;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeartbeatMessage), "heartbeat")]
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(AckMessage), "ack")]
[JsonDerivedType(typeof(GroupProbeMessage), "groupProbe")]
[JsonDerivedType(typeof(InstructionChangeMessage), "instructionChange")]
[JsonDerivedType(typeof(InstructionsSyncRequest), "instructionsRequest")]
[JsonDerivedType(typeof(InstructionsSyncReply), "instructionsReply")]
[JsonDerivedType(typeof(DowntimeSyncRequest), "downtimeRequest")]
[JsonDerivedType(typeof(DowntimeSyncReply), "downtimeReply")]
[JsonDerivedType(typeof(SharedConfigMessage), "sharedConfig")]
[JsonDerivedType(typeof(ObservationSyncRequest), "observationRequest")]
[JsonDerivedType(typeof(ObservationSyncReply), "observationReply")]
public abstract record PeerMessage
{
    public GroupIdentity? Group { get; init; }
    /// <summary>Nazwa hosta komputera, który wysłał wiadomość — identyfikuje nadawcę w całym klastrze.</summary>
    public required string Sender { get; init; }

    /// <summary>
    /// Rosnący numer kolejny, liczony osobno per uruchomienie procesu nadawcy (nadawany przez <see cref="IPeerBus"/>
    /// przy wysyłce) — używany przez <see cref="PeerTable"/> do odrzucania odtworzonych/powtórzonych pakietów.
    /// </summary>
    public long Seq { get; init; }

    /// <summary>Znacznik czasu wysyłki (UTC) — chroni przed odtwarzaniem starych pakietów (okno tolerancji zegarów).</summary>
    public DateTimeOffset SentAt { get; init; }

    /// <summary>Wersja programu nadawcy (do diagnostyki w oknie statusu).</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Losowy identyfikator TEGO URUCHOMIENIA procesu nadawcy (GUID „N”, wygenerowany raz przy starcie —
    /// patrz <see cref="IPeerBus"/>). Gdy się zmieni względem zapamiętanego, <see cref="PeerTable"/> wie, że
    /// nadawca właśnie wystartował od nowa i numeracja <see cref="Seq"/> zaczyna się od zera (bez tego, przez
    /// do minuty po restarcie, świeże heartbeaty byłyby błędnie odrzucane jako powtórki starszego numeru).
    /// Domyślnie puste dla zgodności wstecznej (starszy nadawca albo wiadomość zbudowana ręcznie w testach).
    /// </summary>
    public string InstanceId { get; init; } = "";
}

/// <summary>
/// Cykliczny sygnał życia — wysyłany przez KAŻDY komputer co <c>ClusterConfig.HeartbeatIntervalMs</c>, niezależnie
/// od roli. Na jego podstawie każdy komputer niezależnie liczy, kto powinien być obserwatorem (<see cref="Election"/>).
/// </summary>
public sealed record HeartbeatMessage : PeerMessage
{
    public WatcherReadiness? Readiness { get; init; }
    /// <summary>Priorytet tego komputera przy wyborze obserwatora — im niższa liczba, tym wyższy priorytet.</summary>
    public int Priority { get; init; }

    /// <summary>Bieżąca rola nadawcy w chwili wysyłki.</summary>
    public PeerRole Role { get; init; }

    /// <summary>Host, którego nadawca — wg własnych obliczeń — uważa za obserwatora, jeśli jakiś jest.</summary>
    public string? BelievedWatcher { get; init; }

    /// <summary>Liczba maszyn, z którymi nadawca ma aktualnie połączenie (istotne, gdy <see cref="Role"/> to Watcher).</summary>
    public int MachinesConnected { get; init; }

    /// <summary>Łączna liczba skonfigurowanych (włączonych) maszyn znanych nadawcy.</summary>
    public int MachinesTotal { get; init; }

    /// <summary>
    /// Potwierdzenia ("Idę sprawdzić") zapamiętane lokalnie przez nadawcę w ciągu ostatnich ~60 s (najwyżej 20
    /// wpisów) — retransmisja obok jednorazowego <see cref="AckMessage"/>, żeby zgubiony pakiet ACK nie kazał
    /// obserwatorowi dzwonić w nieskończoność (patrz <see cref="ClusterNode"/>).
    /// </summary>
    public IReadOnlyList<AckRecord> RecentAcks { get; init; } = [];

    /// <summary>
    /// Odcisk wspólnych instrukcji naprawy nadawcy. Inny niż własny znaczy
    /// „mamy różne zbiory”. Pole jest opcjonalne i pomijane w JSON, gdy puste, więc starszy odbiorca go
    /// nie widzi i niczego nie traci.
    /// </summary>
    public string? InstructionsHash { get; init; }

    /// <summary>Najświeższa zmiana instrukcji u nadawcy — po niej poznajemy, kto ma pytać, a kto odpowiadać.</summary>
    public DateTimeOffset? InstructionsEditedAt { get; init; }

    public string? SharedConfigHash { get; init; }

    /// <summary>Czas ostatniej zmiany wspólnej konfiguracji u nadawcy — po nim poznajemy, kto ma wysyłać.</summary>
    public DateTimeOffset? SharedConfigEditedAt { get; init; }
}

/// <summary>
/// Pełny stan alertów i maszyn — wysyłany co <c>ClusterConfig.StateIntervalMs</c> WYŁĄCZNIE przez komputer
/// pełniący aktualnie rolę obserwatora. Wysyłanie pełnego stanu (nie tylko zmian) co sekundę to celowa
/// prostota — zgubiony pakiet UDP sam się naprawia w następnej sekundzie.
/// </summary>
public sealed record StateMessage : PeerMessage
{
    public IReadOnlyList<EpisodeClosure> ClosedEpisodes { get; init; } = [];
    /// <summary>Aktywne (niezamknięte) alerty widziane przez obserwatora w chwili wysyłki.</summary>
    public IReadOnlyList<Alert> Alerts { get; init; } = [];

    /// <summary>Bieżący status każdej monitorowanej przez obserwatora maszyny.</summary>
    public IReadOnlyList<MachineStatusSnapshot> Machines { get; init; } = [];

    /// <summary>
    /// Klucze epizodów zamkniętych przez obserwatora w ciągu ostatnich ~10 minut (patrz
    /// <c>AlertStateMachine.RecentlyClosedKeys</c>) — odbiorca zapomina o ACK-ach tych kluczy
    /// (<c>AckMemory.Forget</c>), żeby kolejne wystąpienie tego samego błędu nie startowało od razu jako
    /// potwierdzone i bez dźwięku (patrz <see cref="ClusterNode"/>).
    /// </summary>
    public IReadOnlyList<AlertKey> RecentlyClosed { get; init; } = [];
}

/// <summary>
/// Potwierdzenie alertu ("Idę sprawdzić") — rozgłaszane natychmiast po kliknięciu przycisku na dowolnym
/// komputerze; każdy odbiorca od razu chowa alert, a obserwator wpisuje potwierdzenie do kolejnego <see cref="StateMessage"/>.
/// </summary>
public sealed record AckMessage : PeerMessage
{
    public Guid? EpisodeId { get; init; }
    /// <summary>Klucz potwierdzonego epizodu alertu.</summary>
    public required AlertKey Key { get; init; }

    /// <summary>Użytkownik, który potwierdził (imię/login wpisane w Ustawieniach).</summary>
    public required string User { get; init; }

    /// <summary>Kiedy potwierdzono.</summary>
    public DateTimeOffset AckedAt { get; init; }
}

public sealed record EpisodeClosure(Guid EpisodeId, string MachineId, AlertKind Kind, DateTimeOffset At);

public sealed record GroupProbeMessage : PeerMessage
{
    public Guid RequestId { get; init; }
    public string? ReplyTo { get; init; }
}
