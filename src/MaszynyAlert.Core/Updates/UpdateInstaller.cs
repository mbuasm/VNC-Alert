namespace MaszynyAlert.Core.Updates;

/// <summary>Wynik uruchomienia instalatora (<see cref="IUpdateInstaller.LaunchAsync"/>).</summary>
public enum InstallLaunchResult
{
    /// <summary>Instalator ruszył (UAC potwierdzone) — proces programu zaraz zostanie zatrzymany przez Setup.</summary>
    Started,

    /// <summary>Okno UAC odrzucone albo nikt go nie potwierdził — instalacja czeka na administratora.</summary>
    CancelledByUser,

    /// <summary>Błąd uruchomienia albo instalator zakończył się kodem błędu (szczegół w komunikacie).</summary>
    Failed,
}

public interface IUpdateInstaller
{
    Task<(InstallLaunchResult Result, string? Error)> LaunchAsync(string setupPath, Version version, string expectedSha256, CancellationToken ct);
}
