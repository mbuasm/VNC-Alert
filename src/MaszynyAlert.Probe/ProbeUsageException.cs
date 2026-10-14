namespace MaszynyAlert.Probe;

/// <summary>
/// Błąd użycia programu (zły argument, brakująca wartość, nieznana komenda) — komunikat jest po
/// polsku i ma trafić prosto na konsolę użytkownika (bez stack trace). Odróżniona od
/// <see cref="MaszynyAlert.Core.Config.ConfigException"/> tylko po to, żeby Program.cs mógł je
/// ewentualnie inaczej sformatować; oba kończą program kodem 1.
/// </summary>
public sealed class ProbeUsageException : Exception
{
    public ProbeUsageException(string message) : base(message)
    {
    }
}
