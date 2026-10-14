namespace MaszynyAlert.Core.Cluster;

/// <summary>Wynik jednego <see cref="WatcherRoleController.Evaluate"/> — bieżąca rola i widok klastra tego komputera.</summary>
/// <param name="IsWatcher">Czy TEN komputer ma być obserwatorem od tej chwili.</param>
/// <param name="WatcherHost">Host aktualnego obserwatora (ten komputer, inny żywy peer, albo <see langword="null"/>).</param>
/// <param name="NoWatcher">Czy nikt żywy (łącznie z tym komputerem) nie jest obecnie obserwatorem.</param>
/// <param name="SleepDetected">Czy w tym wywołaniu wykryto uśpienie komputera (skok zegara monotonicznego).</param>
public sealed record RoleDecision(bool IsWatcher, string? WatcherHost, bool NoWatcher, bool SleepDetected);
