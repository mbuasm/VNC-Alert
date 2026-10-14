using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Cluster;

public interface IInstructionSync
{
    /// <summary>Odcisk całego zbioru — równy u obu stron oznacza „nie ma czego wysyłać”.</summary>
    string Hash { get; }

    /// <summary>Największy czas zmiany; <see langword="null"/>, gdy nikt niczego nie zmieniał.</summary>
    DateTimeOffset? LatestEditedAt { get; }

    /// <summary>Cały zbiór wpisów (razem z nagrobkami) — to, co dostaje partner w odpowiedzi na prośbę.</summary>
    IReadOnlyList<InstructionEntry> AllEntries();

    /// <summary>Stosuje zmiany z innego komputera. Zwraca <see langword="true"/>, gdy coś się zmieniło.</summary>
    bool Apply(IReadOnlyList<InstructionEntry> entries);
}

/// <summary>
/// Pojedyncza zmiana instrukcji rozgłaszana zaraz po zapisaniu w panelu — zwykła droga „na żywo”.
/// Komputer wyłączony w tej chwili dostanie ją później przez <see cref="InstructionsSyncRequest"/>.
/// </summary>
public sealed record InstructionChangeMessage : PeerMessage
{
    public required InstructionEntry Entry { get; init; }
}

/// <summary>
/// Prośba o instrukcje partnera, gdy odciski zbiorów się różnią.
///
/// <para>partner bywa właśnie tym, który ma wpis
/// STARSZY od naszego najświeższego (dostał go od trzeciego komputera). Odpowiedź „tylko nowsze”
/// zostawiłaby wtedy różne odciski na zawsze, a prośby powtarzałyby się w kółko. Dlatego pytamy o
/// cały zbiór — porcjowany przez <see cref="MessageCodec.Chunk"/> — a ruch i tak ustaje, gdy odciski
/// się zrównają.</para>
/// </summary>
public sealed record InstructionsSyncRequest : PeerMessage
{
    public Guid RequestId { get; init; }
}

/// <summary>Odpowiedź porcjami; <see cref="More"/> mówi, że po tej porcji idzie kolejna.</summary>
public sealed record InstructionsSyncReply : PeerMessage
{
    public Guid RequestId { get; init; }

    /// <summary>Host, który prosił — pozostali odbiorcy tę odpowiedź pomijają (jak w <see cref="GroupProbeMessage"/>).</summary>
    public string? ReplyTo { get; init; }

    public IReadOnlyList<InstructionEntry> Entries { get; init; } = [];

    public bool More { get; init; }
}
