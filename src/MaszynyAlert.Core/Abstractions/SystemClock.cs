namespace MaszynyAlert.Core.Abstractions;

/// <summary>Zegar korzystający z rzeczywistego czasu systemowego.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Współdzielona instancja — nie trzeba tworzyć własnej, gdy nie potrzeba testów.</summary>
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset Now => DateTimeOffset.Now;

    /// <summary>Czas od uruchomienia systemu (Environment.TickCount64) — monotoniczny, w milisekundach.</summary>
    public long MonotonicMs => Environment.TickCount64;
}
