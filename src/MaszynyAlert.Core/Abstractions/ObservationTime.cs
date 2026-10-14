namespace MaszynyAlert.Core.Abstractions;

/// <summary>Lokalny moment obserwacji: UTC do historii, licznik monotoniczny do czasu trwania.
/// Nie jest przesyłany między komputerami.</summary>
internal readonly record struct ObservationTime(DateTimeOffset Utc, long Milliseconds)
{
    public static ObservationTime Read(IClock clock) => new(clock.UtcNow, clock.MonotonicMs);
    public TimeSpan Age(IClock clock) => TimeSpan.FromMilliseconds(Math.Max(0, clock.MonotonicMs - Milliseconds));
}
