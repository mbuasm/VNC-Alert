using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.ViewModels;

public sealed record MachineCardViewModel(
    string Id,
    string Name,
    string State,
    string Detail,
    string Accent,
    string Symbol,
    MachineCardTone Tone,
    string ShortState,
    string Progress)
{
    /// <summary>
    /// Czy kafelek nakładki ma migać: trwa NIEPOTWIERDZONY błąd maszyny (po „Idę sprawdzić” karta jest
    /// bursztynowa „Sprawdzane”, a przy eskalacji wraca do czerwieni i znowu miga).
    /// </summary>
    public bool Alarming => Tone == MachineCardTone.Error;

    /// <summary>
    /// Czy kafelek jest wyróżniony w Podglądzie — ustawiane po kliknięciu kafelka nakładki, żeby operator
    /// zobaczył, o którą maszynę chodziło (<c>OverlayWindow</c> → <c>OperatorWindow.ShowForMachine</c>).
    /// </summary>
    public bool Highlighted { get; init; }

    /// <summary>Czerwień alertu (ta sama co <c>AlertRedBrush</c> w App.xaml).</summary>
    public const string ErrorAccent = "#B42338";

    /// <summary>Bursztyn „zrób coś przy maszynie”.</summary>
    public const string WarningAccent = "#8A5900";

    /// <summary>Zieleń „pod obserwacją”.</summary>
    public const string OkAccent = "#087E70";

    /// <summary>Buduje kartę maszyny <paramref name="machineId"/> o nazwie <paramref name="machineName"/>.</summary>
    public static MachineCardViewModel From(
        string machineId,
        string machineName,
        MachineStatusSnapshot? snapshot,
        IReadOnlyList<Alert> alerts,
        TimingConfig timing,
        DateTimeOffset now)
    {
        MachineCardState state = MachineCardState.From(machineId, snapshot, alerts, timing, now);
        return new MachineCardViewModel(
            machineId, machineName, state.State, state.Detail, AccentFor(state.Tone), state.Symbol, state.Tone,
            state.ShortState, state.Progress);
    }

    /// <summary>
    /// Karty wszystkich WŁĄCZONYCH maszyn, w kolejności z konfiguracji — jedno źródło dla Podglądu
    /// (<c>Views.OperatorWindow</c>) i nakładki statusu (<c>Views.OverlayWindow</c>), żeby oba okna nigdy nie
    /// pokazywały innego stanu tej samej maszyny. Migawki pochodzą z monitorów tego komputera albo — gdy rolę
    /// obserwatora pełni inny komputer grupy — z <see cref="MaszynyAlert.Core.Cluster.ClusterRuntime.RemoteMachines"/>.
    /// </summary>
    public static MachineCardViewModel[] ForEnabledMachines(AppOrchestrator orchestrator, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        IReadOnlyList<MachineStatusSnapshot> snapshots = orchestrator.Cluster.IsWatcher
            ? orchestrator.Supervisor.Snapshots
            : orchestrator.Cluster.RemoteMachines;
        Alert[] alerts = orchestrator.VisibleAlerts.Where(a => a.Status != AlertStatus.Closed).ToArray();
        return orchestrator.Config.Machines.Where(m => m.Enabled).Select(m => From(
            m.Id, m.Name, snapshots.FirstOrDefault(x => x.MachineId == m.Id), alerts, orchestrator.Config.Timing, now))
            .ToArray();
    }

    /// <summary>Kolor karty w zapisie XAML.</summary>
    public static string AccentFor(MachineCardTone tone) => tone switch
    {
        MachineCardTone.Error => ErrorAccent,
        MachineCardTone.Warning => WarningAccent,
        _ => OkAccent,
    };
}
