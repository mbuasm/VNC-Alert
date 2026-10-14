using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Machines;

public sealed class QtGuardState
{
    internal QtGuardState(string host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>Adres maszyny, dla którego stan powstał — inny adres albo port to inny serwer, czyli nowy stan.</summary>
    internal string Host { get; }

    /// <summary>Port VNC maszyny, dla którego stan powstał.</summary>
    internal int Port { get; }

    internal bool QtServer { get; set; }

    internal ScreenSourceInfo? QtInfo { get; set; }

    /// <summary>Ile odstępów bezpiecznika (zamknięcie przed klatką, błąd protokołu) już użyto; 0 — bezpiecznik nieaktywny.</summary>
    internal int GuardStep { get; set; }

    /// <summary>Ile odstępów po przejęciu sesji już użyto (reset po sesji stabilnej co najmniej 10 min).</summary>
    internal int TakeoverStep { get; set; }

    /// <summary>Czas monotoniczny (<c>IClock.MonotonicMs</c>), przed którym nie wolno łączyć się z maszyną; null — brak wstrzymania.</summary>
    internal long? NextAttemptMs { get; set; }

    /// <summary>
    /// LastError z chwili rozpoczęcia trwającego wstrzymania (z jednym czasem „Kolejna próba za …”) — nowy monitor pokazuje go na
    /// karcie maszyny, zanim sam spróbuje połączenia; null, gdy wstrzymania nie ma.
    /// </summary>
    internal string? HoldError { get; set; }

    /// <summary>
    /// Początek stałej sesji z klatkami, przerwanej wyłącznie zatrzymaniem monitora przez nadzorcę (przebudowa) — pierwsze
    /// połączenie następnego monitora liczy długość sesji od tej chwili (reset kroku przejęć po co najmniej 10 min stabilnej
    /// obserwacji). Każda awaria połączenia i przejście na plik testowy go kasują.
    /// </summary>
    internal long? StableSinceMs { get; set; }

    /// <summary>Czy stan dotyczy tego samego serwera co <paramref name="machine"/> (adres bez rozróżniania wielkości liter i port).</summary>
    internal bool Matches(MachineConfig machine) =>
        string.Equals(Host, machine.Host, StringComparison.OrdinalIgnoreCase) && Port == machine.Port;
}
