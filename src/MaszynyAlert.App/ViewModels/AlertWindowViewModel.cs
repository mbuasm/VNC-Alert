using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class AlertWindowViewModel : ObservableObject
{
    private readonly Func<AlertKey, Task> _acknowledge;
    private string _signature = "";
    private readonly Func<Alert, string> _help;

    public AlertWindowViewModel(Func<AlertKey, Task> acknowledge, Func<Alert, string>? help = null)
    {
        _acknowledge = acknowledge ?? throw new ArgumentNullException(nameof(acknowledge));
        _help = help ?? MaszynyAlert.Core.Ocr.AlarmHelp.For;
    }

    public ObservableCollection<AlertItemViewModel> Items { get; } = new();

    /// <summary>Czy jest choć jeden widoczny alert — steruje pokazaniem/ukryciem okna.</summary>
    public bool HasVisibleAlerts => Items.Count > 0;

    /// <summary>Czy powinien grać dźwięk: jest widoczny alert z Sound i statusem Active/Escalated.</summary>
    public bool ShouldPlaySound => Items.Any(i => i.Sound && i.IsRinging);

    /// <summary>
    /// Przelicza listę widocznych alertów na podstawie pełnego zestawu z orkiestratora. Wołane na wątku UI.
    /// Widoczność każdego alertu rozstrzyga <see cref="AlertVisibility.IsVisibleInAlertWindow"/> — pomija
    /// m.in. <see cref="AlertKind.MachineIdle"/> (to duże czerwone okno jest tylko dla błędów/braku
    /// łączności; alerty "dołóż prace" mają własny, mniejszy pop-up, patrz <see cref="Views.IdlePopupWindow"/>)
    /// i alerty Acknowledged (znikają z tego okna od razu po „Idę sprawdzić”).
    /// Zwraca <c>true</c>, gdy na liście pojawił się alert, którego nie było przy poprzednim odświeżeniu — okno
    /// przewija wtedy listę na górę, do nowego alarmu.
    /// </summary>
    public bool Refresh(IReadOnlyList<Alert> allAlerts)
    {
        ArgumentNullException.ThrowIfNull(allAlerts);

        List<Alert> visible = allAlerts.Where(AlertVisibility.IsVisibleInAlertWindow)
            .OrderBy(a => a.Status is AlertStatus.Active or AlertStatus.Escalated ? 0 : 1)
            .ThenByDescending(a => a.StartedAt)
            .ThenBy(a => a.Key.MachineId, StringComparer.Ordinal)
            .ThenBy(a => a.Key.Discriminator, StringComparer.Ordinal)
            .ToList();
        string signature = string.Join("|", visible.Select(a => $"{a.Key}:{a.StartedAt}:{a.ShortText}:{a.FullText}:{a.Status}:{a.CaptureFile}:{_help(a)}"));
        // Preserve the expanded help while unrelated machine updates arrive.
        if (_signature == signature) return false;
        _signature = signature;

        var expanded = Items.Where(i => i.IsHelpExpanded).Select(i => i.Key).ToHashSet();
        var known = Items.Select(i => i.Key).ToHashSet();
        bool hasNew = visible.Any(a => !known.Contains(a.Key));
        Items.Clear();
        foreach (Alert alert in visible)
        {
            Items.Add(new AlertItemViewModel(alert, _acknowledge, _help) { IsHelpExpanded = expanded.Contains(alert.Key) });
        }

        OnPropertyChanged(nameof(HasVisibleAlerts));
        OnPropertyChanged(nameof(ShouldPlaySound));
        return hasNew;
    }
}
