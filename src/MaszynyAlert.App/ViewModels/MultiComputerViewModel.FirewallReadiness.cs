using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class MultiComputerViewModel
{
    private readonly Func<ClusterConfig, CancellationToken, Task<FirewallReadinessReport>>? _checkFirewallReadiness;
    private readonly Func<string, CancellationToken, Task<NetworkProfileChangeResult>>? _setPrivateNetworkProfile;
    private readonly Func<string, GroupFileExportTarget?>? _selectItInstructionExport;
    private FirewallReadinessReport? _lastFirewallReadiness;

    /// <summary>Address chosen in the wizard for this attempt; null means the saved setting (or the only interface).
    /// It travels into the proposal, so a successful apply saves it as <c>cluster.localAddress</c>.</summary>
    private string? _chosenLocalAddress;

    [ObservableProperty]
    private IReadOnlyList<GroupDiagnosticsRow> _firewallReadinessRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanInterfaceChoiceVisible))]
    private IReadOnlyList<LanInterfaceChoice> _lanInterfaceChoices = [];

    [ObservableProperty]
    private LanInterfaceChoice? _selectedLanInterface;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirewallReadinessSummaryVisible))]
    private string _firewallReadinessSummaryText = "";

    [ObservableProperty]
    private string _firewallReadinessSummaryDetail = "";

    [ObservableProperty]
    private bool _isFirewallReadinessProblem;

    [ObservableProperty]
    private bool _isFirewallReadinessRunning;

    [ObservableProperty]
    private bool _isNetworkProfileRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNetworkProfileStatusVisible))]
    private string _networkProfileStatusText = "";

    [ObservableProperty]
    private bool _isNetworkProfileError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItInstructionStatusVisible))]
    private string _itInstructionStatusText = "";

    [ObservableProperty]
    private bool _isItInstructionError;

    /// <summary>Last completed readiness read, for tests and the smoke test; null until the first read.</summary>
    public FirewallReadinessReport? LastFirewallReadiness => _lastFirewallReadiness;

    public bool IsFirewallReadinessVisible => _checkFirewallReadiness is not null
        && (IsCreateConfirmationStep || IsJoinPreparationStep);
    public bool IsFirewallReadinessSummaryVisible => !string.IsNullOrWhiteSpace(FirewallReadinessSummaryText);
    /// <summary>The picker shows whenever there is anything to pick; with one interface the choice is automatic.</summary>
    public bool IsLanInterfaceChoiceVisible => LanInterfaceChoices.Count > 1;
    public string LanInterfaceChoiceHint => "Interfejs do maszyn. Wi‑Fi i inne karty zostaw poza grupą; wybór zapisze się razem z ustawieniami grupy.";

    /// <summary>The local settings the apply will save: the wizard's interface choice on top of the saved ones.</summary>
    private ClusterConfig WithChosenLocalAddress(ClusterConfig cluster)
        => _chosenLocalAddress is null ? cluster : cluster with { LocalAddress = _chosenLocalAddress };

    partial void OnSelectedLanInterfaceChanged(LanInterfaceChoice? value)
    {
        if (value is null || value.Address == (_chosenLocalAddress ?? _lastFirewallReadiness?.Address))
            return;
        _chosenLocalAddress = value.Address;
        if (CanCheckFirewallReadiness)
            _ = CheckFirewallReadinessAsync(CancellationToken.None);
    }
    public bool IsNetworkProfileStatusVisible => !string.IsNullOrWhiteSpace(NetworkProfileStatusText);
    public bool IsItInstructionStatusVisible => !string.IsNullOrWhiteSpace(ItInstructionStatusText);
    private bool IsFirewallActionRunning => IsFirewallReadinessRunning || IsNetworkProfileRunning || IsCreateApplyRunning || IsJoinApplyRunning;

    public bool CanCheckFirewallReadiness => IsFirewallReadinessVisible && !IsFirewallActionRunning;

    /// <summary>Offered only when Windows reports the chosen LAN interface as Public; the elevated helper re-checks
    /// and changes nothing for a Private or Domain network.</summary>
    public bool CanSetPrivateNetworkProfile => IsFirewallReadinessVisible && _setPrivateNetworkProfile is not null
        && _lastFirewallReadiness?.CanOfferPrivateProfile == true && !IsFirewallActionRunning;

    public bool CanExportItInstruction => IsFirewallReadinessVisible && _selectItInstructionExport is not null
        && _lastFirewallReadiness is { } report && (report.HasProblems || report.HasWarnings || report.HasUnknown) && !IsFirewallActionRunning;

    public string FirewallReadinessHint => "Odczyt bez uprawnień administratora: profil sieci, usługa zapory, zasady organizacji i "
        + "istniejąca reguła. Problem tutaj nie blokuje przycisku poniżej, ale zapis reguły zakończy się tą samą odmową.";

    private void NotifyFirewallReadinessChanged()
    {
        OnPropertyChanged(nameof(IsFirewallReadinessVisible));
        OnPropertyChanged(nameof(CanCheckFirewallReadiness));
        OnPropertyChanged(nameof(CanSetPrivateNetworkProfile));
        OnPropertyChanged(nameof(CanExportItInstruction));
        CheckFirewallReadinessCommand.NotifyCanExecuteChanged();
        SetPrivateNetworkProfileCommand.NotifyCanExecuteChanged();
        ExportItInstructionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFirewallReadinessRunningChanged(bool value) => NotifyFirewallReadinessChanged();
    partial void OnIsNetworkProfileRunningChanged(bool value) => NotifyFirewallReadinessChanged();
    partial void OnIsCreateApplyRunningChanged(bool value) => NotifyFirewallReadinessChanged();
    partial void OnIsJoinApplyRunningChanged(bool value) => NotifyFirewallReadinessChanged();
    partial void OnSelectedPathChanged(MultiComputerSetupPath value) => NotifyFirewallReadinessChanged();
    partial void OnCreateStepChanged(CreateGroupWizardStep value) => NotifyFirewallReadinessChanged();
    partial void OnJoinStepChanged(JoinGroupWizardStep value) => NotifyFirewallReadinessChanged();
    partial void OnDiagnosticsOriginChanged(GroupDiagnosticsOrigin value) => NotifyFirewallReadinessChanged();

    /// <summary>The cluster the elevated step would apply: the join invitation's port for a join, otherwise the
    /// current local settings. Only the local interface and port matter for the read.</summary>
    private ClusterConfig FirewallReadinessCluster()
    {
        if (IsJoinPreparationStep && CurrentJoinProposal is { } join)
            return WithChosenLocalAddress(join.GetApplyProposal().GetData().Cluster);
        return WithChosenLocalAddress(_currentCluster);
    }

    /// <summary>Started when a step that ends in UAC opens, and on demand. Read-only; safe to repeat.</summary>
    internal Task StartFirewallReadinessAsync()
    {
        ResetFirewallReadiness();
        return CanCheckFirewallReadiness ? CheckFirewallReadinessAsync(CancellationToken.None) : Task.CompletedTask;
    }

    private void ResetFirewallReadiness()
    {
        _lastFirewallReadiness = null;
        _chosenLocalAddress = null;
        LanInterfaceChoices = [];
        SelectedLanInterface = null;
        FirewallReadinessRows = [];
        FirewallReadinessSummaryText = "";
        FirewallReadinessSummaryDetail = "";
        IsFirewallReadinessProblem = false;
        NetworkProfileStatusText = "";
        IsNetworkProfileError = false;
        ItInstructionStatusText = "";
        IsItInstructionError = false;
        NotifyFirewallReadinessChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckFirewallReadiness))]
    private async Task CheckFirewallReadinessAsync(CancellationToken cancellationToken)
    {
        if (!CanCheckFirewallReadiness || _checkFirewallReadiness is null)
            return;

        IsFirewallReadinessRunning = true;
        FirewallReadinessSummaryText = "Sprawdzanie gotowości zapory…";
        FirewallReadinessSummaryDetail = "Odczyt profilu sieci, usługi zapory i zasad. Nic nie jest zmieniane.";
        IsFirewallReadinessProblem = false;
        try
        {
            FirewallReadinessReport report = await _checkFirewallReadiness(FirewallReadinessCluster(), cancellationToken);
            ShowFirewallReadiness(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FirewallReadinessSummaryText = "Sprawdzanie przerwane";
            FirewallReadinessSummaryDetail = "Użyj Sprawdź ponownie.";
        }
        catch (Exception)
        {
            _lastFirewallReadiness = null;
            FirewallReadinessRows = [];
            IsFirewallReadinessProblem = false;
            FirewallReadinessSummaryText = "Nie udało się odczytać gotowości zapory";
            FirewallReadinessSummaryDetail = "Odczyt zakończył się błędem. Utwórz i włącz nadal jest możliwe; proces administracyjny "
                + "sprawdzi warunki ponownie i poda przyczynę odmowy.";
        }
        finally
        {
            IsFirewallReadinessRunning = false;
        }
    }

    private void ShowFirewallReadiness(FirewallReadinessReport report)
    {
        _lastFirewallReadiness = report;
        LanInterfaceChoices = report.Candidates;
        LanInterfaceChoice? current = report.Candidates.FirstOrDefault(c => c.Address == (_chosenLocalAddress ?? report.Address));
        if (!ReferenceEquals(SelectedLanInterface, current) && (SelectedLanInterface?.Address != current?.Address))
            SelectedLanInterface = current;
        FirewallReadinessRows = report.Items.Select(item => new GroupDiagnosticsRow(item.Label, item.Result, item.Detail, item.State switch
        {
            FirewallReadinessState.Ok => GroupDiagnosticsSeverity.Ok,
            FirewallReadinessState.Problem => GroupDiagnosticsSeverity.Problem,
            FirewallReadinessState.Warning => GroupDiagnosticsSeverity.Waiting,
            _ => GroupDiagnosticsSeverity.NotChecked,
        })).ToList();
        IsFirewallReadinessProblem = report.HasProblems;
        if (report.HasProblems)
        {
            FirewallReadinessSummaryText = "Zapis reguły zapory zakończy się odmową";
            FirewallReadinessSummaryDetail = report.CanOfferPrivateProfile
                ? "Interfejs do maszyn ma profil Publiczny. Przycisk Ustaw profil Prywatny (UAC) zmienia tylko ten jeden interfejs "
                  + "(dla sieci niezidentyfikowanej także zasadę lokalną, żeby profil przetrwał restart); potem sprawdzenie powtórzy się samo."
                : "Usuń wskazane przyczyny albo przekaż IT wyeksportowaną instrukcję, potem użyj Sprawdź ponownie.";
        }
        else if (report.HasWarnings)
        {
            FirewallReadinessSummaryText = "Komputer jest gotowy na regułę zapory, ale profil może nie przetrwać restartu";
            FirewallReadinessSummaryDetail = "Sieć niezidentyfikowana bez zasady lokalnej wraca po restarcie do profilu Publicznego. "
                + "Przycisk Ustaw profil Prywatny (UAC) ustawia tę zasadę; można też pominąć i utworzyć grupę teraz.";
        }
        else if (report.HasUnknown)
        {
            FirewallReadinessSummaryText = "Gotowości zapory nie udało się w pełni odczytać";
            FirewallReadinessSummaryDetail = "Wiersze „Nie sprawdzono” nie oznaczają problemu. Proces administracyjny sprawdzi je ponownie.";
        }
        else
        {
            FirewallReadinessSummaryText = "Komputer jest gotowy na regułę zapory";
            FirewallReadinessSummaryDetail = "Proces administracyjny sprawdzi te warunki jeszcze raz w chwili zapisu.";
        }
        NotifyFirewallReadinessChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSetPrivateNetworkProfile))]
    private async Task SetPrivateNetworkProfileAsync(CancellationToken cancellationToken)
    {
        string? address = _lastFirewallReadiness?.Address;
        if (!CanSetPrivateNetworkProfile || _setPrivateNetworkProfile is null || address is null)
            return;

        IsNetworkProfileRunning = true;
        IsNetworkProfileError = false;
        NetworkProfileStatusText = "Zmiana profilu sieci… Potwierdź uprawnienia administratora.";
        try
        {
            NetworkProfileChangeResult result = await _setPrivateNetworkProfile(address, cancellationToken);
            IsNetworkProfileError = result.Outcome is NetworkProfileChangeOutcome.Denied or NetworkProfileChangeOutcome.Failed;
            NetworkProfileStatusText = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsNetworkProfileError = true;
            NetworkProfileStatusText = "Zmiana profilu sieci przerwana. Sprawdź stan przyciskiem Sprawdź ponownie.";
        }
        catch (Exception)
        {
            IsNetworkProfileError = true;
            NetworkProfileStatusText = "Nie udało się uruchomić zmiany profilu sieci. Profil pozostaje bez zmian.";
        }
        finally
        {
            IsNetworkProfileRunning = false;
        }
        if (CanCheckFirewallReadiness)
            await CheckFirewallReadinessAsync(cancellationToken);
    }

    /// <summary>Writes the IT instruction (rule parameters and the findings; no secrets) to a file the user picks.</summary>
    [RelayCommand(CanExecute = nameof(CanExportItInstruction))]
    private void ExportItInstruction()
    {
        FirewallReadinessReport? report = _lastFirewallReadiness;
        if (!CanExportItInstruction || _selectItInstructionExport is null || report is null)
            return;

        IsItInstructionError = false;
        string name;
        try
        {
            byte[] content = System.Text.Encoding.UTF8.GetBytes(report.BuildItInstruction(_now()));
            GroupFileExportTarget? target = _selectItInstructionExport("VNC Alert - instrukcja dla IT - zapora.txt");
            if (target is null)
            {
                ItInstructionStatusText = "Nie zapisano instrukcji dla IT.";
                return;
            }
            using Stream stream = target.Content;
            stream.Write(content, 0, content.Length);
            stream.Flush();
            name = DescribeFileName(target.DisplayName);
        }
        catch (Exception)
        {
            IsItInstructionError = true;
            ItInstructionStatusText = "Nie udało się zapisać instrukcji dla IT. Spróbuj zapisać w innym miejscu.";
            return;
        }
        ItInstructionStatusText = $"Zapisano instrukcję dla IT {name}. Zawiera parametry reguły i odczytany stan, bez haseł ani klucza grupy.";
    }

    private static GroupFileExportTarget? SelectItInstructionExport(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Zapisz instrukcję dla IT",
            Filter = "Plik tekstowy (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = suggestedFileName,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
            return null;

        var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
        return new GroupFileExportTarget(Path.GetFileName(dialog.FileName), stream);
    }
}
