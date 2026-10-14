namespace MaszynyAlert.Core.Alerts;

/// <summary>Zapamiętane potwierdzenie ("Idę sprawdzić") jednego epizodu alertu.</summary>
/// <param name="Key">Klucz potwierdzonego epizodu.</param>
/// <param name="Host">Komputer, z którego potwierdzono.</param>
/// <param name="User">Użytkownik, który potwierdził.</param>
/// <param name="At">Kiedy potwierdzono.</param>
public sealed record AckRecord(AlertKey Key, string Host, string User, DateTimeOffset At)
{
    public Guid? EpisodeId { get; init; }
}
