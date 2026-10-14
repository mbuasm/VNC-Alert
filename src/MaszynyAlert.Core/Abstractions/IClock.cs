namespace MaszynyAlert.Core.Abstractions;

/// <summary>Źródło czasu — pozwala podmienić zegar systemowy w testach (np. sterować upływem czasu ręcznie).</summary>
public interface IClock
{
    /// <summary>Bieżący czas UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Bieżący czas lokalny.</summary>
    DateTimeOffset Now { get; }

    /// <summary>
    /// Czas monotoniczny w milisekundach (nie cofa się, niezależny od zmiany zegara systemowego) —
    /// używany m.in. do wykrywania uśpienia komputera (duży skok między odczytami).
    /// </summary>
    long MonotonicMs { get; }
}
