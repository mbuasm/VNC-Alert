using MaszynyAlert.Core.Abstractions;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Zegar sterowany ręcznie: czas UTC/lokalny oraz licznik monotoniczny są ustawiane wprost,
/// dzięki czemu testy cyklu życia alertów nie muszą niczego odczekiwać.
/// </summary>
public sealed class FakeClock : IClock
{
    /// <summary>Domyślny punkt startowy — stała data, żeby testy były w pełni deterministyczne.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 3, 17, 8, 0, 0, TimeSpan.Zero);

    public FakeClock(DateTimeOffset? start = null, long monotonicMs = 1_000_000)
    {
        UtcNow = (start ?? DefaultStart).ToUniversalTime();
        MonotonicMs = monotonicMs;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; }

    /// <summary>Przesunięcie strefy "lokalnej" tego zegara (domyślnie UTC, żeby testy nie zależały od strefy maszyny CI).</summary>
    public TimeSpan LocalOffset { get; set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public DateTimeOffset Now
    {
        get => UtcNow.ToOffset(LocalOffset);
        set => UtcNow = value.ToUniversalTime();
    }

    /// <inheritdoc />
    public long MonotonicMs { get; set; }

    /// <summary>Przesuwa spójnie czas kalendarzowy i licznik monotoniczny (normalny upływ czasu).</summary>
    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
        MonotonicMs += (long)delta.TotalMilliseconds;
    }

    /// <summary>Przesuwa spójnie czas kalendarzowy i licznik monotoniczny o podaną liczbę milisekund.</summary>
    public void AdvanceMs(long milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Przesuwa WYŁĄCZNIE licznik monotoniczny — symulacja uśpienia/wybudzenia komputera widziana
    /// przez kod, który porównuje kolejne odczyty <see cref="MonotonicMs"/>.
    /// </summary>
    public void JumpMonotonic(long milliseconds) => MonotonicMs += milliseconds;
}
