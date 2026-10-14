using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.ViewModels;

/// <summary>
/// Multi-computer wizard state. It deliberately keeps the setting stored on disk separate from the
/// transport that is actually running. Editing and confirmation remain side-effect free; only the
/// explicit final command creates a secret invitation and invokes the transactional apply operation.
/// </summary>
public sealed partial class MultiComputerViewModel : ObservableObject
{
    private readonly Func<MultiComputerStatusInput> _readStatus;
    private readonly Func<GroupSettingsProposal, CancellationToken, Task>? _applyGroupSettings;
    private readonly Func<GroupFile?>? _selectAndReadGroupFile;
    private readonly Action<string, string>? _saveLocalVncPassword;
    private readonly Func<string, GroupFileExportTarget?>? _selectGroupFileExport;
    private readonly Func<DateTimeOffset> _now;

    public MultiComputerViewModel(AppOrchestrator orchestrator)
        : this(() => MultiComputerStatusInput.From(orchestrator), orchestrator.ApplyGroupSettingsAsync,
            SelectAndReadGroupFile, orchestrator.SetLocalVncPassword, SelectGroupFileExport,
            checkFirewallReadiness: (cluster, ct) => Task.Run(() => new GroupFirewallReadiness(Environment.ProcessPath!).Check(cluster), ct),
            setPrivateNetworkProfile: NetworkProfileHelper.RunElevatedAsync,
            selectItInstructionExport: SelectItInstructionExport)
    {
    }

    /// <summary>Testable state source; it must only read current state and must not apply settings.</summary>
    public MultiComputerViewModel(Func<MultiComputerStatusInput> readStatus,
        Func<GroupSettingsProposal, CancellationToken, Task>? applyGroupSettings = null,
        Func<GroupFile?>? selectAndReadGroupFile = null,
        Action<string, string>? saveLocalVncPassword = null,
        Func<string, GroupFileExportTarget?>? selectGroupFileExport = null,
        Func<DateTimeOffset>? now = null,
        Func<ClusterConfig, CancellationToken, Task<FirewallReadinessReport>>? checkFirewallReadiness = null,
        Func<string, CancellationToken, Task<NetworkProfileChangeResult>>? setPrivateNetworkProfile = null,
        Func<string, GroupFileExportTarget?>? selectItInstructionExport = null)
    {
        _checkFirewallReadiness = checkFirewallReadiness;
        _setPrivateNetworkProfile = setPrivateNetworkProfile;
        _selectItInstructionExport = selectItInstructionExport;
        _readStatus = readStatus ?? throw new ArgumentNullException(nameof(readStatus));
        _applyGroupSettings = applyGroupSettings;
        _selectAndReadGroupFile = selectAndReadGroupFile;
        _saveLocalVncPassword = saveLocalVncPassword;
        _selectGroupFileExport = selectGroupFileExport;
        // Only stamps "checked at" on the diagnostics snapshot, so a test can pin the wording.
        _now = now ?? (() => DateTimeOffset.Now);
        Refresh();
    }

    /// <summary>
    /// Raised after entered passwords were dropped from memory, so the view can clear the single shared
    /// <c>PasswordBox</c>; per-machine boxes are recreated with the regenerated entry rows.
    /// </summary>
    public event Action? JoinPasswordInputCleared;

    [ObservableProperty]
    private string _savedSettingText = "";

    [ObservableProperty]
    private string _savedSettingDetail = "";

    [ObservableProperty]
    private string _savedGroupText = "";

    [ObservableProperty]
    private string _runtimeStateText = "";

    [ObservableProperty]
    private string _runtimeStateDetail = "";

    [ObservableProperty]
    private string _visibleComputersText = "";

    [ObservableProperty]
    private bool _isRuntimeWarning;

    [ObservableProperty]
    private bool _runtimeDiffersFromSaved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraftGroupNameValid))]
    [NotifyPropertyChangedFor(nameof(DraftGroupNameStatus))]
    [NotifyPropertyChangedFor(nameof(CanContinueCreate))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCreateCommand))]
    private string _draftGroupName = "Hala — Aidite";

    [ObservableProperty]
    private string _createMachineSummaryText = "";

    [ObservableProperty]
    private string _createProfilesText = "";

    [ObservableProperty]
    private IReadOnlyList<MultiComputerMachineSummary> _createMachines = [];

    [ObservableProperty]
    private string _localReadinessText = "";

    [ObservableProperty]
    private string _localReadinessDetail = "";

    [ObservableProperty]
    private string _passwordsReadinessText = "";

    [ObservableProperty]
    private string _ocrReadinessText = "";

    [ObservableProperty]
    private bool _isLocalReadinessWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateGroupSelected))]
    [NotifyPropertyChangedFor(nameof(IsJoinGroupSelected))]
    [NotifyPropertyChangedFor(nameof(IsCreateDraftStep))]
    [NotifyPropertyChangedFor(nameof(IsCreateConfirmationStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinFileStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinSummaryStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinPreparationStep))]
    [NotifyPropertyChangedFor(nameof(ChoiceTitle))]
    [NotifyPropertyChangedFor(nameof(ChoiceDetail))]
    [NotifyPropertyChangedFor(nameof(CanContinueCreate))]
    [NotifyPropertyChangedFor(nameof(CanApplyCreate))]
    [NotifyPropertyChangedFor(nameof(CanBackToCreateDraft))]
    [NotifyPropertyChangedFor(nameof(CanUseGroupConfiguration))]
    [NotifyPropertyChangedFor(nameof(IsJoinPasswordEntryVisible))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackToCreateDraftCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectJoinGroupFileCommand))]
    [NotifyPropertyChangedFor(nameof(CanApplyJoin))]
    [NotifyPropertyChangedFor(nameof(CanBackFromJoinPreparation))]
    [NotifyCanExecuteChangedFor(nameof(UseGroupConfigurationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackFromJoinPreparationCommand))]
    [NotifyPropertyChangedFor(nameof(IsJoinDiagnosticsStep))]
    [NotifyPropertyChangedFor(nameof(IsCreateDiagnosticsStep))]
    [NotifyPropertyChangedFor(nameof(CanCheckGroupNetwork))]
    [NotifyPropertyChangedFor(nameof(CanCheckCreatedGroupNetwork))]
    [NotifyPropertyChangedFor(nameof(CanRunGroupDiagnostics))]
    [NotifyCanExecuteChangedFor(nameof(CheckGroupNetworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckCreatedGroupNetworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunGroupDiagnosticsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackFromGroupDiagnosticsCommand))]
    [NotifyPropertyChangedFor(nameof(IsGroupFileExportVisible))]
    [NotifyPropertyChangedFor(nameof(CanExportGroupFile))]
    [NotifyCanExecuteChangedFor(nameof(ExportGroupFileCommand))]
    private MultiComputerSetupPath _selectedPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateDraftStep))]
    [NotifyPropertyChangedFor(nameof(IsCreateConfirmationStep))]
    [NotifyPropertyChangedFor(nameof(CanContinueCreate))]
    [NotifyPropertyChangedFor(nameof(CanApplyCreate))]
    [NotifyPropertyChangedFor(nameof(CanBackToCreateDraft))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackToCreateDraftCommand))]
    [NotifyPropertyChangedFor(nameof(IsGroupFileExportVisible))]
    [NotifyPropertyChangedFor(nameof(CanExportGroupFile))]
    [NotifyCanExecuteChangedFor(nameof(ExportGroupFileCommand))]
    [NotifyPropertyChangedFor(nameof(CanCheckCreatedGroupNetwork))]
    [NotifyCanExecuteChangedFor(nameof(CheckCreatedGroupNetworkCommand))]
    private CreateGroupWizardStep _createStep = CreateGroupWizardStep.Draft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyCreate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCreateCommand))]
    private CreateGroupDraftProposal? _currentCreateProposal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyCreate))]
    [NotifyPropertyChangedFor(nameof(CanBackToCreateDraft))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackToCreateDraftCommand))]
    [NotifyPropertyChangedFor(nameof(CanExportGroupFile))]
    [NotifyCanExecuteChangedFor(nameof(ExportGroupFileCommand))]
    [NotifyPropertyChangedFor(nameof(CanCheckCreatedGroupNetwork))]
    [NotifyCanExecuteChangedFor(nameof(CheckCreatedGroupNetworkCommand))]
    private bool _isCreateApplyRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyCreate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCreateCommand))]
    [NotifyPropertyChangedFor(nameof(IsGroupFileExportVisible))]
    [NotifyPropertyChangedFor(nameof(CanExportGroupFile))]
    [NotifyCanExecuteChangedFor(nameof(ExportGroupFileCommand))]
    [NotifyPropertyChangedFor(nameof(CanCheckCreatedGroupNetwork))]
    [NotifyPropertyChangedFor(nameof(CreateNetworkCheckHint))]
    [NotifyPropertyChangedFor(nameof(IsCreateNetworkCheckHintVisible))]
    [NotifyCanExecuteChangedFor(nameof(CheckCreatedGroupNetworkCommand))]
    private bool _isCreateApplyCompleted;

    [ObservableProperty]
    private bool _isCreateApplyError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupFileExportStatusVisible))]
    private string _groupFileExportStatusText = "";

    [ObservableProperty]
    private bool _isGroupFileExportError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupFileExportHint))]
    private bool _isGroupFileExported;

    [ObservableProperty]
    private string _createApplyStatusText = "";

    [ObservableProperty]
    private string _createApplyDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseGroupConfiguration))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyPropertyChangedFor(nameof(CanApplyJoin))]
    [NotifyCanExecuteChangedFor(nameof(UseGroupConfigurationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJoinCommand))]
    private JoinGroupDraftProposal? _currentJoinProposal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinImportStatusVisible))]
    private string _joinImportStatusText = "";

    [ObservableProperty]
    private bool _isJoinImportError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinFileStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinSummaryStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinPreparationStep))]
    [NotifyPropertyChangedFor(nameof(CanUseGroupConfiguration))]
    [NotifyPropertyChangedFor(nameof(IsJoinPasswordEntryVisible))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyPropertyChangedFor(nameof(CanApplyJoin))]
    [NotifyPropertyChangedFor(nameof(CanBackFromJoinPreparation))]
    [NotifyCanExecuteChangedFor(nameof(UseGroupConfigurationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackFromJoinPreparationCommand))]
    [NotifyPropertyChangedFor(nameof(CanCheckGroupNetwork))]
    [NotifyCanExecuteChangedFor(nameof(CheckGroupNetworkCommand))]
    private JoinGroupWizardStep _joinStep = JoinGroupWizardStep.File;

    [ObservableProperty]
    private string _joinReadinessText = "";

    [ObservableProperty]
    private string _joinReadinessDetail = "";

    [ObservableProperty]
    private bool _isJoinReadinessWarning;

    [ObservableProperty]
    private string _joinPasswordsReadinessText = "";

    [ObservableProperty]
    private string _joinOcrReadinessText = "";

    [ObservableProperty]
    private IReadOnlyList<MultiComputerMachineSummary> _joinMissingPasswordMachines = [];

    [ObservableProperty]
    private string _joinMissingPasswordsHint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinPasswordEntryVisible))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    private IReadOnlyList<JoinPasswordEntry> _joinPasswordEntries = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinSharedPasswordMode))]
    [NotifyPropertyChangedFor(nameof(IsJoinPerMachinePasswordMode))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    private bool _useSharedJoinPassword;

    [ObservableProperty]
    private string _joinPasswordEntryHint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinPasswordSaveStatusVisible))]
    private string _joinPasswordSaveStatusText = "";

    [ObservableProperty]
    private bool _isJoinPasswordSaveError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyJoin))]
    [NotifyPropertyChangedFor(nameof(CanBackFromJoinPreparation))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackFromJoinPreparationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    [NotifyPropertyChangedFor(nameof(CanCheckGroupNetwork))]
    [NotifyCanExecuteChangedFor(nameof(CheckGroupNetworkCommand))]
    private bool _isJoinApplyRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyJoin))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    [NotifyPropertyChangedFor(nameof(CanCheckGroupNetwork))]
    [NotifyCanExecuteChangedFor(nameof(CheckGroupNetworkCommand))]
    private bool _isJoinApplyCompleted;

    [ObservableProperty]
    private bool _isJoinApplyError;

    [ObservableProperty]
    private string _joinApplyStatusText = "";

    [ObservableProperty]
    private string _joinApplyDetail = "";

    [ObservableProperty]
    private string _joinApplyHint = "";

    /// <summary>
    /// Which path opened the read-only network step. It is deliberately not a value of
    /// <see cref="JoinGroupWizardStep"/>: the very same step is reached from "create and enable" as well, and a
    /// step belonging to one wizard could not describe both. <see cref="GroupDiagnosticsOrigin.None"/> means the
    /// step is closed, and the path it belongs to keeps its own step untouched underneath, so returning is simply
    /// clearing this one value.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupDiagnosticsStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinDiagnosticsStep))]
    [NotifyPropertyChangedFor(nameof(IsCreateDiagnosticsStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinPreparationStep))]
    [NotifyPropertyChangedFor(nameof(IsCreateConfirmationStep))]
    [NotifyPropertyChangedFor(nameof(IsJoinPasswordEntryVisible))]
    [NotifyPropertyChangedFor(nameof(CanSaveJoinPasswords))]
    [NotifyPropertyChangedFor(nameof(CanApplyJoin))]
    [NotifyPropertyChangedFor(nameof(CanBackFromJoinPreparation))]
    [NotifyPropertyChangedFor(nameof(CanApplyCreate))]
    [NotifyPropertyChangedFor(nameof(CanBackToCreateDraft))]
    [NotifyPropertyChangedFor(nameof(IsGroupFileExportVisible))]
    [NotifyPropertyChangedFor(nameof(CanExportGroupFile))]
    [NotifyPropertyChangedFor(nameof(CanCheckGroupNetwork))]
    [NotifyPropertyChangedFor(nameof(CanCheckCreatedGroupNetwork))]
    [NotifyPropertyChangedFor(nameof(CanRunGroupDiagnostics))]
    [NotifyCanExecuteChangedFor(nameof(SaveJoinPasswordsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackFromJoinPreparationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackToCreateDraftCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportGroupFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckGroupNetworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckCreatedGroupNetworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunGroupDiagnosticsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackFromGroupDiagnosticsCommand))]
    private GroupDiagnosticsOrigin _diagnosticsOrigin;

    [ObservableProperty]
    private IReadOnlyList<GroupDiagnosticsRow> _groupDiagnosticsRows = [];

    [ObservableProperty]
    private IReadOnlyList<GroupReportSourceRow> _reportSourceRows = [];

    [ObservableProperty]
    private string _groupDiagnosticsSummaryText = "";

    [ObservableProperty]
    private string _groupDiagnosticsSummaryDetail = "";

    [ObservableProperty]
    private bool _isGroupDiagnosticsWarning;

    [ObservableProperty]
    private string _groupDiagnosticsCheckedAtText = "";

    [ObservableProperty]
    private string _groupDiagnosticsScopeText = "";

    private string _sharedJoinPassword = "";
    private GroupProfile? _currentProfile;
    private ClusterConfig _currentCluster = new();
    private bool _localCanObserve;
    private bool _monitoringEnabled = true;
    private bool _ocrAvailable = true;
    private Func<string, bool>? _hasLocalVncPassword;
    private bool _joinCanObserve;

    /// <summary>
    /// The invitation produced by the last successful "create and enable" on this computer. It is kept in memory
    /// only so the explicit export writes exactly the group that is now running. It carries the network key, so it
    /// never reaches a binding, status text or diagnostics, and leaving or restarting the create step drops it.
    /// </summary>
    private GroupFile? _createdInvitation;

    public bool IsCreateGroupSelected => SelectedPath == MultiComputerSetupPath.CreateGroup;
    public bool IsJoinGroupSelected => SelectedPath == MultiComputerSetupPath.JoinGroup;
    public bool IsCreateDraftStep => IsCreateGroupSelected && CreateStep == CreateGroupWizardStep.Draft;
    public bool IsCreateConfirmationStep => IsCreateGroupSelected
        && CreateStep == CreateGroupWizardStep.Confirmation && !IsGroupDiagnosticsStep;
    public bool IsJoinFileStep => IsJoinGroupSelected && JoinStep == JoinGroupWizardStep.File;
    public bool IsJoinSummaryStep => IsJoinGroupSelected && JoinStep == JoinGroupWizardStep.Summary;
    public bool IsJoinPreparationStep => IsJoinGroupSelected
        && JoinStep == JoinGroupWizardStep.Preparation && !IsGroupDiagnosticsStep;

    /// <summary>The one read-only network step, whichever path opened it; the path underneath keeps its own step.</summary>
    public bool IsGroupDiagnosticsStep => DiagnosticsOrigin != GroupDiagnosticsOrigin.None;
    public bool IsJoinDiagnosticsStep => DiagnosticsOrigin == GroupDiagnosticsOrigin.Join;
    public bool IsCreateDiagnosticsStep => DiagnosticsOrigin == GroupDiagnosticsOrigin.Create;
    public bool CanSelectJoinGroupFile => IsJoinGroupSelected && _selectAndReadGroupFile is not null;
    public bool CanUseGroupConfiguration => IsJoinSummaryStep && CurrentJoinProposal is not null;
    public bool IsJoinImportStatusVisible => !string.IsNullOrWhiteSpace(JoinImportStatusText);
    public bool IsJoinPasswordEntryVisible => IsJoinPreparationStep && JoinPasswordEntries.Count > 0;
    public bool IsJoinSharedPasswordMode => UseSharedJoinPassword;
    public bool IsJoinPerMachinePasswordMode => !UseSharedJoinPassword;
    public bool IsJoinPasswordSaveStatusVisible => !string.IsNullOrWhiteSpace(JoinPasswordSaveStatusText);
    public bool IsJoinApplyStatusVisible => !string.IsNullOrWhiteSpace(JoinApplyStatusText);

    /// <summary>
    /// The join is offered only once this computer is locally ready for the invited machine list, judged by the
    /// same <see cref="WatcherReadiness"/> the runtime uses — that is what the preparation step exists for. A
    /// limited OCR does not block it, exactly as it does not block creating a group.
    /// </summary>
    public bool CanApplyJoin => IsJoinPreparationStep && CurrentJoinProposal is not null
        && _applyGroupSettings is not null && _joinCanObserve
        && !IsJoinApplyRunning && !IsJoinApplyCompleted;

    public bool CanBackFromJoinPreparation => IsJoinPreparationStep && !IsJoinApplyRunning;

    /// <summary>
    /// Network diagnostics are a separate, explicit step offered only once this computer has actually joined, so
    /// the wizard never suggests that a saved setting alone proves the group works.
    /// </summary>
    public bool CanCheckGroupNetwork => IsJoinPreparationStep && IsJoinApplyCompleted && !IsJoinApplyRunning;

    /// <summary>
    /// The same rule on the create path: only a computer that really created and enabled the group may open the
    /// network step, because before that there is no running group to read. The check itself is the join one — it
    /// only reads the runtime — so offering it here adds no write and no administrator prompt.
    /// </summary>
    public bool CanCheckCreatedGroupNetwork => IsCreateConfirmationStep && IsCreateApplyCompleted && !IsCreateApplyRunning;

    public bool CanRunGroupDiagnostics => IsGroupDiagnosticsStep;
    public bool IsGroupDiagnosticsResultVisible => GroupDiagnosticsRows.Count > 0;

    /// <summary>
    /// A local secret write is offered only while at least one password was actually typed on this step. The
    /// shared variant fills every listed machine; the per-machine variant only the rows that were filled in.
    /// </summary>
    public bool CanSaveJoinPasswords => IsJoinPreparationStep
        && CurrentJoinProposal is not null
        && _saveLocalVncPassword is not null
        && !IsJoinApplyRunning
        && !IsJoinApplyCompleted
        && JoinPasswordEntries.Count > 0
        && (UseSharedJoinPassword
            ? _sharedJoinPassword.Length > 0
            : JoinPasswordEntries.Any(entry => entry.HasPassword));
    public bool CanApplyCreate => IsCreateConfirmationStep && CurrentCreateProposal is not null
        && _applyGroupSettings is not null && !IsCreateApplyRunning && !IsCreateApplyCompleted;
    public bool CanBackToCreateDraft => IsCreateConfirmationStep && !IsCreateApplyRunning;
    public bool IsCreateApplyStatusVisible => !string.IsNullOrWhiteSpace(CreateApplyStatusText);

    /// <summary>
    /// The export is offered only once this computer really created and enabled the group, because only then does an
    /// invitation exist. It writes a file and nothing else, so it may be repeated for a second computer.
    /// </summary>
    public bool IsGroupFileExportVisible => IsCreateConfirmationStep && IsCreateApplyCompleted;

    public bool CanExportGroupFile => IsGroupFileExportVisible && _createdInvitation is not null
        && _selectGroupFileExport is not null && !IsCreateApplyRunning;

    public bool IsGroupFileExportStatusVisible => !string.IsNullOrWhiteSpace(GroupFileExportStatusText);

    /// <summary>
    /// Says in one place why the file matters and why it must be handled as a secret, so the warning can be pinned
    /// by a test instead of living only in the XAML.
    /// </summary>
    public string GroupFileExportWarningText =>
        "Plik grupy jest sekretem — zawiera klucz sieciowy grupy. Kto go ma, może dołączyć do grupy. "
        + "Nie wysyłaj go pocztą ani komunikatorem i nie zostawiaj na pulpicie ani na dysku sieciowym. "
        + "Przenieś go na drugi komputer i skasuj kopię, a w razie wycieku utwórz grupę od nowa.";

    public string GroupFileExportHint => IsGroupFileExported
        ? "Plik został już zapisany w tym kroku. Możesz zapisać go ponownie, jeśli potrzebujesz kolejnej kopii dla "
            + "następnego komputera — zapis nie zmienia grupy, ustawień, zapory ani runtime."
        : "Bez tego pliku drugi komputer nie ma czego wskazać w kroku Dołącz do grupy. "
            + "Uwaga: przycisk Wstecz porzuca zaproszenie z pamięci i wtedy nie da się go już wyeksportować.";

    /// <summary>
    /// Says in one sentence what the network step on the create path is and is not, so the button is never read as
    /// a second apply. It is the same read-only step the join path offers, kept in a property so a test can pin it.
    /// </summary>
    public string CreateNetworkCheckHint => IsCreateApplyCompleted
        ? "Przycisk Sprawdź sieć grupy otwiera ten sam, wyłącznie odczytujący krok co po dołączeniu. Nie tworzy "
            + "grupy po raz drugi, nie zmienia ustawień ani zapory, a po powrocie zapis pliku grupy jest nadal możliwy."
        : "";

    public bool IsCreateNetworkCheckHintVisible => IsCreateApplyCompleted;

    public bool IsDraftGroupNameValid => !string.IsNullOrWhiteSpace(DraftGroupName)
        && DraftGroupName.Length <= 100
        && !DraftGroupName.Any(char.IsControl);

    public string DraftGroupNameStatus => string.IsNullOrWhiteSpace(DraftGroupName)
        ? "Wpisz nazwę grupy."
        : DraftGroupName.Length > 100
            ? "Nazwa grupy może mieć najwyżej 100 znaków."
            : DraftGroupName.Any(char.IsControl)
                ? "Nazwa grupy nie może zawierać znaków sterujących."
                : "To tylko lokalny szkic nazwy — nic nie zostało zapisane.";

    public bool CanContinueCreate => IsCreateDraftStep
        && IsDraftGroupNameValid
        && _localCanObserve
        && _currentProfile is not null;

    public string ChoiceTitle => SelectedPath switch
    {
        MultiComputerSetupPath.CreateGroup => "Wybrano: Utwórz grupę",
        MultiComputerSetupPath.JoinGroup => "Wybrano: Dołącz do grupy",
        _ => "Wybierz sposób rozpoczęcia",
    };

    public string ChoiceDetail => SelectedPath switch
    {
        MultiComputerSetupPath.CreateGroup =>
            "Poniżej możesz wpisać nazwę oraz sprawdzić bieżącą listę maszyn, profile i lokalną gotowość.",
        MultiComputerSetupPath.JoinGroup =>
            "W kolejnym kroku wskażesz plik grupy i zobaczysz różnice przed zaakceptowaniem zmian.",
        _ => "",
    };

    /// <summary>Refreshes only display state. The selected draft path is intentionally retained.</summary>
    public void Refresh() => ApplyStatus(_readStatus());

    /// <summary>Display-only projection of one already taken status reading; it never writes anything.</summary>
    private void ApplyStatus(MultiComputerStatusInput state)
    {
        SavedSettingText = state.SavedEnabled ? "Włączona" : "Wyłączona";
        SavedGroupText = string.IsNullOrWhiteSpace(state.SavedGroupName)
            ? "Grupa: nie wybrano"
            : $"Grupa: {state.SavedGroupName}";
        SavedSettingDetail = state.SavedEnabled
            ? string.IsNullOrWhiteSpace(state.SavedGroupName)
                ? "W ustawieniach włączono współpracę, ale nie zapisano jeszcze grupy."
                : "Zapisane ustawienie mówi, że ten komputer ma współpracować z grupą."
            : string.IsNullOrWhiteSpace(state.SavedGroupName)
                ? "Ten komputer ma zapisany tryb samodzielny."
                : "Współpraca jest wyłączona, ale dane grupy pozostają zapisane do ponownego włączenia.";

        RuntimeDiffersFromSaved = state.SavedEnabled != state.NetworkActive;
        (RuntimeStateText, RuntimeStateDetail, IsRuntimeWarning) = DescribeRuntime(state);
        VisibleComputersText = state.NetworkActive
            ? VisibleComputersLine(state)
            : "Widoczne komputery: — (transport grupy nie działa)";

        ReportSourceRows = state.ReportSources;

        RefreshCreateDraft(state);
    }

    [RelayCommand]
    private void SelectCreateGroup()
    {
        if (IsCreateApplyRunning || IsJoinApplyRunning)
            return;
        SelectedPath = MultiComputerSetupPath.CreateGroup;
        CreateStep = CreateGroupWizardStep.Draft;
        CurrentCreateProposal = null;
        CurrentJoinProposal = null;
        JoinStep = JoinGroupWizardStep.File;
        ClearJoinPreparation();
        ResetJoinImportStatus();
        ResetCreateApplyStatus();
    }

    [RelayCommand]
    private void SelectJoinGroup()
    {
        if (IsCreateApplyRunning || IsJoinApplyRunning)
            return;
        SelectedPath = MultiComputerSetupPath.JoinGroup;
        CreateStep = CreateGroupWizardStep.Draft;
        CurrentCreateProposal = null;
        CurrentJoinProposal = null;
        JoinStep = JoinGroupWizardStep.File;
        ClearJoinPreparation();
        ResetJoinImportStatus();
        ResetCreateApplyStatus();
    }

    [RelayCommand(CanExecute = nameof(CanSelectJoinGroupFile))]
    private void SelectJoinGroupFile()
    {
        if (!CanSelectJoinGroupFile || _selectAndReadGroupFile is null)
            return;

        CurrentJoinProposal = null;
        ClearJoinPreparation();
        ResetJoinImportStatus();
        JoinGroupDraftProposal proposal;
        try
        {
            GroupFile? invitation = _selectAndReadGroupFile();
            if (invitation is null)
            {
                JoinImportStatusText = "Nie wybrano pliku grupy. Dotychczasowe ustawienia nie zostały zmienione.";
                return;
            }

            if (_currentProfile is null)
                throw new InvalidOperationException();

            proposal = JoinGroupDraftProposal.Create(invitation, _currentProfile, _currentCluster);
        }
        catch (Exception)
        {
            IsJoinImportError = true;
            JoinImportStatusText = "Nie udało się odczytać pliku grupy. Sprawdź, czy to poprawny plik .vncgroup o rozmiarze do 1 MiB. Ustawienia nie zostały zmienione.";
            return;
        }

        CurrentJoinProposal = proposal;
        JoinStep = JoinGroupWizardStep.Summary;
    }

    [RelayCommand]
    private void BackToJoinFile()
    {
        CurrentJoinProposal = null;
        JoinStep = JoinGroupWizardStep.File;
        ClearJoinPreparation();
        ResetJoinImportStatus();
    }

    /// <summary>
    /// Moves from the frozen summary to local preparation only. It reuses the readiness the runtime uses, never
    /// copies passwords from the file and does not touch settings, the group secret, the firewall or the runtime.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupConfiguration))]
    private void UseGroupConfiguration()
    {
        JoinGroupDraftProposal? proposal = CurrentJoinProposal;
        if (!CanUseGroupConfiguration || proposal is null)
            return;

        BuildJoinPreparation(proposal);
        JoinStep = JoinGroupWizardStep.Preparation;
        // Read-only and without UAC; a failed read only shows as "unknown" and never blocks Dołącz i włącz.
        _ = StartFirewallReadinessAsync();
    }

    /// <summary>
    /// Receives one machine's typed password from the view. The value is kept only in this private field until
    /// the explicit save, is never turned into a bindable property and never reaches status or error text.
    /// </summary>
    public void SetJoinPassword(string machineId, string? password)
    {
        JoinPasswordEntry? entry = JoinPasswordEntries
            .FirstOrDefault(row => string.Equals(row.MachineId, machineId, StringComparison.Ordinal));
        if (entry is null)
            return;

        entry.SetPassword(password);
        NotifyJoinPasswordInputChanged();
    }

    /// <summary>Receives the one shared password used for every machine still missing a local password.</summary>
    public void SetSharedJoinPassword(string? password)
    {
        _sharedJoinPassword = password ?? "";
        NotifyJoinPasswordInputChanged();
    }

    /// <summary>
    /// Writes the typed passwords to the local secret store only, then recomputes the very same
    /// <see cref="WatcherReadiness"/> right away. It never writes <see cref="AppConfig"/>, the group secret,
    /// the firewall or the runtime, and it never copies anything out of the invitation file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveJoinPasswords))]
    private void SaveJoinPasswords()
    {
        JoinGroupDraftProposal? proposal = CurrentJoinProposal;
        if (!CanSaveJoinPasswords || proposal is null || _saveLocalVncPassword is null)
            return;

        (string MachineId, string Password)[] pending = UseSharedJoinPassword
            ? JoinPasswordEntries.Select(entry => (entry.MachineId, _sharedJoinPassword)).ToArray()
            : JoinPasswordEntries
                .Where(entry => entry.HasPassword)
                .Select(entry => (entry.MachineId, entry.Password))
                .ToArray();

        int saved = 0;
        bool failed = false;
        foreach ((string machineId, string password) in pending)
        {
            try
            {
                _saveLocalVncPassword(machineId, password);
                saved++;
            }
            catch (Exception)
            {
                // The exception text may quote the machine or the secret, so only the count is reported.
                failed = true;
            }
        }

        ClearJoinPasswordInput();
        BuildJoinPreparation(proposal);

        IsJoinPasswordSaveError = failed;
        JoinPasswordSaveStatusText = failed
            ? $"Zapisano {saved} z {pending.Length} haseł w lokalnym magazynie tego komputera. Pozostałych nie udało "
                + "się zapisać — wpisz je ponownie. Ustawienia, sekret grupy, zapora i runtime pozostają bez zmian."
            : $"Zapisano lokalnie hasła VNC ({saved}). Trafiły wyłącznie do magazynu sekretów tego komputera; "
                + "ustawienia, sekret grupy, zapora i działające monitorowanie pozostają bez zmian.";
    }

    /// <summary>
    /// The one explicit join. It hands the frozen import proposal to the very same transactional settings/UAC/runtime
    /// operation that <see cref="ApplyCreateAsync"/> uses, so the previous safe state keeps running until the switch
    /// succeeds. It performs no network diagnostics and never reports the invitation or a secret in its text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyJoin))]
    private async Task ApplyJoinAsync(CancellationToken cancellationToken)
    {
        JoinGroupDraftProposal? draft = CurrentJoinProposal;
        if (!CanApplyJoin || draft is null || _applyGroupSettings is null)
            return;

        IsJoinApplyRunning = true;
        IsJoinApplyError = false;
        JoinApplyStatusText = "Dołączanie do grupy i włączanie…";
        JoinApplyDetail = "Potwierdź uprawnienia administratora. Dotychczasowe monitorowanie działa do czasu bezpiecznego przełączenia.";
        OnPropertyChanged(nameof(IsJoinApplyStatusVisible));
        try
        {
            await _applyGroupSettings(_chosenLocalAddress is null ? draft.GetApplyProposal()
                : draft.GetApplyProposal().WithLocalAddress(_chosenLocalAddress), cancellationToken);

            IsJoinApplyCompleted = true;
            JoinApplyStatusText = "Dołączono do grupy i włączono";
            JoinApplyDetail = "Zapis zakończył się powodzeniem. Powyżej pokazano osobno zapisane ustawienie i faktyczny stan runtime. "
                + "Sieć, zapora i drugi komputer nie zostały jeszcze sprawdzone — to osobny krok.";
            Refresh();
        }
        catch (GroupSettingsDeniedException ex)
        {
            IsJoinApplyError = true;
            JoinApplyStatusText = "Nie dołączono do grupy";
            JoinApplyDetail = ex.Message;
        }
        catch (GroupSettingsHelperException ex)
        {
            IsJoinApplyError = true;
            JoinApplyStatusText = "Nie dołączono do grupy";
            JoinApplyDetail = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsJoinApplyError = true;
            JoinApplyStatusText = "Dołączanie do grupy anulowane";
            JoinApplyDetail = "Poprzedni bezpieczny stan ustawień i monitorowania pozostaje aktywny.";
        }
        catch (Exception)
        {
            IsJoinApplyError = true;
            JoinApplyStatusText = "Nie dołączono do grupy";
            JoinApplyDetail = "Nie udało się bezpiecznie zastosować ustawień grupy. Poprzedni stan pozostaje aktywny; sprawdź diagnostykę i spróbuj ponownie.";
        }
        finally
        {
            IsJoinApplyRunning = false;
            RefreshJoinApplyHint();
            OnPropertyChanged(nameof(IsJoinApplyStatusVisible));
        }
    }

    /// <summary>The invitation is a secret, so leaving local preparation drops the private import proposal.</summary>
    [RelayCommand(CanExecute = nameof(CanBackFromJoinPreparation))]
    private void BackFromJoinPreparation()
    {
        if (!CanBackFromJoinPreparation)
            return;

        CurrentJoinProposal = null;
        JoinStep = JoinGroupWizardStep.File;
        ClearJoinPreparation();
        IsJoinImportError = false;
        JoinImportStatusText = "Porzucono wczytane zaproszenie. Nic nie zostało zapisane — wybierz plik ponownie, "
            + "aby wrócić do podsumowania.";
    }

    /// <summary>
    /// Opens the separate network step after a successful join. Diagnostics only read the live runtime: they never
    /// write <see cref="AppConfig"/>, the group secret, the firewall or the cluster, and they never retry a join.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckGroupNetwork))]
    private void CheckGroupNetwork()
    {
        if (!CanCheckGroupNetwork)
            return;

        OpenGroupDiagnostics(GroupDiagnosticsOrigin.Join);
    }

    /// <summary>
    /// The same separate network step after a successful "create and enable". The first computer of a group has the
    /// very same question to answer — is the group really running here — so it gets the very same read-only check,
    /// without a second apply, a new invitation, a firewall change or an administrator prompt.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckCreatedGroupNetwork))]
    private void CheckCreatedGroupNetwork()
    {
        if (!CanCheckCreatedGroupNetwork)
            return;

        OpenGroupDiagnostics(GroupDiagnosticsOrigin.Create);
    }

    /// <summary>Opens the shared step for one path and takes the first reading; the path's own step stays untouched.</summary>
    private void OpenGroupDiagnostics(GroupDiagnosticsOrigin origin)
    {
        DiagnosticsOrigin = origin;
        RunGroupDiagnostics();
    }

    /// <summary>Re-reads the runtime for a fresh snapshot; still read-only, so it may be repeated safely.</summary>
    [RelayCommand(CanExecute = nameof(CanRunGroupDiagnostics))]
    private void RunGroupDiagnostics()
    {
        MultiComputerStatusInput state = _readStatus();
        ApplyStatus(state);
        BuildGroupDiagnostics(state);
    }

    /// <summary>
    /// Returns to whichever path opened the step. Only this one value is cleared, because the create confirmation
    /// and the local join preparation were never left; the result of the apply and the readings stay as they are,
    /// since nothing here changed them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunGroupDiagnostics))]
    private void BackFromGroupDiagnostics()
    {
        if (!IsGroupDiagnosticsStep)
            return;

        DiagnosticsOrigin = GroupDiagnosticsOrigin.None;
    }

    [RelayCommand(CanExecute = nameof(CanContinueCreate))]
    private void ContinueCreate()
    {
        if (!CanContinueCreate || _currentProfile is null)
            return;

        CurrentCreateProposal = CreateGroupDraftProposal.Create(
            DraftGroupName, _currentProfile, OcrReadinessText);
        ResetCreateApplyStatus();
        CreateStep = CreateGroupWizardStep.Confirmation;
        // Read-only and without UAC; a failed read only shows as "unknown" and never blocks Utwórz i włącz.
        _ = StartFirewallReadinessAsync();
    }

    [RelayCommand(CanExecute = nameof(CanBackToCreateDraft))]
    private void BackToCreateDraft()
    {
        CreateStep = CreateGroupWizardStep.Draft;
        CurrentCreateProposal = null;
        ResetCreateApplyStatus();
        ResetFirewallReadiness();
    }

    [RelayCommand(CanExecute = nameof(CanApplyCreate))]
    private async Task ApplyCreateAsync(CancellationToken cancellationToken)
    {
        CreateGroupDraftProposal? draft = CurrentCreateProposal;
        if (!CanApplyCreate || draft is null || _applyGroupSettings is null)
            return;

        IsCreateApplyRunning = true;
        IsCreateApplyError = false;
        CreateApplyStatusText = "Tworzenie i włączanie grupy…";
        CreateApplyDetail = "Potwierdź uprawnienia administratora. Dotychczasowe monitorowanie działa do czasu bezpiecznego przełączenia.";
        OnPropertyChanged(nameof(IsCreateApplyStatusVisible));
        try
        {
            GroupFile invitation = GroupFile.Create(draft.GroupName, draft.CopyProfile(), _currentCluster.UdpPort);
            var proposal = new GroupSettingsProposal(invitation, WithChosenLocalAddress(_currentCluster), enabled: true);
            await _applyGroupSettings(proposal, cancellationToken);

            // Retained only after a successful apply, so the export can never hand out a group that was not enabled.
            SetCreatedInvitation(invitation);
            IsCreateApplyCompleted = true;
            CreateApplyStatusText = "Grupa utworzona i włączona";
            CreateApplyDetail = "Zapis zakończył się powodzeniem. Powyżej pokazano osobno zapisane ustawienie i faktyczny stan runtime. "
                + "Drugi komputer potrzebuje jeszcze pliku grupy — to osobny, jawny krok poniżej.";
            Refresh();
        }
        catch (GroupSettingsDeniedException ex)
        {
            IsCreateApplyError = true;
            CreateApplyStatusText = "Nie utworzono grupy";
            CreateApplyDetail = ex.Message;
        }
        catch (GroupSettingsHelperException ex)
        {
            IsCreateApplyError = true;
            CreateApplyStatusText = "Nie utworzono grupy";
            CreateApplyDetail = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsCreateApplyError = true;
            CreateApplyStatusText = "Tworzenie grupy anulowane";
            CreateApplyDetail = "Poprzedni bezpieczny stan ustawień i monitorowania pozostaje aktywny.";
        }
        catch (Exception)
        {
            IsCreateApplyError = true;
            CreateApplyStatusText = "Nie utworzono grupy";
            CreateApplyDetail = "Nie udało się bezpiecznie zastosować ustawień grupy. Poprzedni stan pozostaje aktywny; sprawdź diagnostykę i spróbuj ponownie.";
        }
        finally
        {
            IsCreateApplyRunning = false;
            OnPropertyChanged(nameof(IsCreateApplyStatusVisible));
        }
    }

    /// <summary>
    /// Writes the invitation of the group this computer just created to a file the user picks. It only serializes
    /// the already created group through <see cref="GroupFileCodec"/>: it never creates another group or key, never
    /// writes <see cref="AppConfig"/>, the firewall or the runtime, never asks for administrator rights and never
    /// quotes the network key. Cancelling or a failed write leaves the invitation in memory, so it can be retried.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportGroupFile))]
    private void ExportGroupFile()
    {
        GroupFile? invitation = _createdInvitation;
        if (!CanExportGroupFile || invitation is null || _selectGroupFileExport is null)
            return;

        IsGroupFileExportError = false;
        string name;
        try
        {
            // Serialized before the dialog, so a rejected invitation never leaves a half-written file behind.
            byte[] content = GroupFileCodec.Serialize(invitation);
            GroupFileExportTarget? target = _selectGroupFileExport(
                SuggestedGroupFileName(invitation.Identity.GroupName));
            if (target is null)
            {
                GroupFileExportStatusText = "Nie zapisano pliku grupy. Utworzona grupa działa dalej, "
                    + "a zaproszenie nadal można zapisać w tym kroku.";
                return;
            }

            using Stream stream = target.Content;
            stream.Write(content, 0, content.Length);
            stream.Flush();
            name = DescribeFileName(target.DisplayName);
        }
        catch (Exception)
        {
            // The exception text may name the path or quote file content, so only a safe sentence is reported.
            IsGroupFileExportError = true;
            GroupFileExportStatusText = "Nie udało się zapisać pliku grupy. Ustawienia, grupa, zapora i monitorowanie "
                + "pozostają bez zmian — spróbuj zapisać w innym miejscu.";
            return;
        }

        IsGroupFileExported = true;
        GroupFileExportStatusText = $"Zapisano plik grupy {name}. Przenieś go na drugi komputer bezpiecznie i skasuj "
            + "kopię pośrednią. Zapis nie zmienił ustawień, zapory ani działającego monitorowania.";
    }

    /// <summary>Keeps or drops the secret invitation and tells the export command that its source changed.</summary>
    private void SetCreatedInvitation(GroupFile? invitation)
    {
        _createdInvitation = invitation;
        OnPropertyChanged(nameof(CanExportGroupFile));
        ExportGroupFileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Proposes a file name from the group name only; the name is not a secret and the key never is part of it.</summary>
    internal static string SuggestedGroupFileName(string groupName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string(groupName.Select(c => invalid.Contains(c) ? '-' : c).ToArray())
            .Trim()
            .TrimEnd('.');
        if (safe.Length > 60)
            safe = safe[..60].Trim().TrimEnd('.');
        return (safe.Length == 0 ? "grupa" : safe) + ".vncgroup";
    }

    /// <summary>Shortens the chosen file name for the status line; a long or odd name must not break the layout.</summary>
    private static string DescribeFileName(string? displayName)
    {
        string name = new string((displayName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "(bez nazwy)" : name.Length <= 80 ? name : name[..80] + "…";
    }

    private void RefreshCreateDraft(MultiComputerStatusInput state)
    {
        _localCanObserve = state.MonitoringEnabled && state.LocalCanObserve;
        _currentProfile = state.CurrentProfile;
        _currentCluster = CopyCluster(state.CurrentCluster);
        _monitoringEnabled = state.MonitoringEnabled;
        _ocrAvailable = state.OcrAvailable;
        _hasLocalVncPassword = state.HasLocalVncPassword;
        MultiComputerMachineInput[] active = state.Machines.Where(machine => machine.Enabled).ToArray();
        int disabled = state.Machines.Count - active.Length;
        CreateMachines = active.Select(machine => new MultiComputerMachineSummary(machine.Name, machine.Profile)).ToArray();
        CreateMachineSummaryText = $"Aktywne maszyny: {active.Length} z {state.Machines.Count}. "
            + $"Wyłączone wpisy: {disabled}.";

        string profiles = active.Length == 0
            ? "—"
            : string.Join(", ", active
                .GroupBy(machine => machine.Profile, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key} ({group.Count()})"));
        CreateProfilesText = $"Profile aktywnych maszyn: {profiles}. "
            + $"Profile skonfigurowane: {state.ConfiguredProfileCount}.";

        var namesById = state.Machines.ToDictionary(machine => machine.Id, machine => machine.Name,
            StringComparer.Ordinal);
        string[] missingNames = state.MissingPasswordMachineIds
            .Select(id => namesById.GetValueOrDefault(id, id))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        PasswordsReadinessText = !state.MonitoringEnabled
            ? "Hasła VNC: nie są sprawdzane, gdy monitorowanie jest wyłączone."
            : missingNames.Length == 0
                ? $"Hasła VNC: gotowe ({active.Length}/{active.Length})."
                : $"Hasła VNC: brakuje dla {string.Join(", ", missingNames)}.";
        OcrReadinessText = DescribeOcr(state.OcrAvailable);

        if (!state.MonitoringEnabled)
        {
            LocalReadinessText = "Włącz monitorowanie przed utworzeniem grupy";
            LocalReadinessDetail = "Globalny wyłącznik monitorowania jest wyłączony. Magazyn haseł nie został odczytany.";
            IsLocalReadinessWarning = true;
        }
        else if (active.Length == 0)
        {
            LocalReadinessText = "Włącz co najmniej jedną maszynę";
            LocalReadinessDetail = "Grupa potrzebuje co najmniej jednej aktywnej maszyny do obserwacji.";
            IsLocalReadinessWarning = true;
        }
        else if (missingNames.Length > 0)
        {
            LocalReadinessText = "Uzupełnij przygotowanie tego komputera";
            LocalReadinessDetail = "Brakujące hasła VNC blokują gotowość tego komputera do przejęcia obserwacji.";
            IsLocalReadinessWarning = true;
        }
        else if (!state.OcrAvailable)
        {
            LocalReadinessText = "Gotowy do czerwonych alarmów — OCR ograniczony";
            LocalReadinessDetail = "Brak OCR nie blokuje wykrywania czerwieni, lecz ogranicza opis alarmu, postój i kontrolę ekranu HMI.";
            IsLocalReadinessWarning = true;
        }
        else
        {
            LocalReadinessText = "Ten komputer jest gotowy lokalnie";
            LocalReadinessDetail = "Monitorowanie, aktywna lista maszyn, lokalne hasła VNC i OCR są przygotowane.";
            IsLocalReadinessWarning = false;
        }

        LocalReadinessDetail += " Chwilowo niedostępna maszyna nie blokuje tego kroku. "
            + "Sieć, zapora i drugi komputer zostaną sprawdzone w późniejszym kroku.";

        OnPropertyChanged(nameof(CanContinueCreate));
        ContinueCreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Describes what this computer still needs before joining. It reads the cached local state only, so it does
    /// not query the status source again, and it evaluates the imported machine list with the very same
    /// <see cref="WatcherReadiness"/> contract that decides whether the runtime may observe.
    /// </summary>
    private void BuildJoinPreparation(JoinGroupDraftProposal proposal)
    {
        WatcherReadiness readiness = proposal.EvaluateLocalReadiness(
            _monitoringEnabled, _ocrAvailable, _hasLocalVncPassword);
        var missingIds = new HashSet<string>(readiness.MissingPasswordMachineIds, StringComparer.Ordinal);
        JoinGroupMachineSummary[] missing = proposal.Machines
            .Where(machine => machine.Enabled && missingIds.Contains(machine.Id))
            .ToArray();
        JoinMissingPasswordMachines = missing
            .Select(machine => new MultiComputerMachineSummary(machine.Name, machine.Profile))
            .ToArray();
        JoinPasswordEntries = missing
            .Select(machine => new JoinPasswordEntry(machine.Id, machine.Name, machine.Profile))
            .ToArray();
        JoinPasswordEntryHint = missing.Length == 0
            ? ""
            : $"Wpisz hasła VNC dla maszyn z listy poniżej (do uzupełnienia: {missing.Length}). Zapis trafia "
                + "wyłącznie do magazynu sekretów tego komputera — nie do pliku grupy i nie do innych komputerów. "
                + "Maszyny, które mają już lokalne hasło, nie są nadpisywane.";

        int active = proposal.ActiveMachineCount;
        JoinPasswordsReadinessText = !_monitoringEnabled
            ? "Hasła VNC: nie są sprawdzane, gdy monitorowanie jest wyłączone."
            : JoinMissingPasswordMachines.Count == 0
                ? $"Hasła VNC: gotowe ({active}/{active}). Plik grupy nigdy nie przenosi haseł."
                : $"Hasła VNC: brakuje {JoinMissingPasswordMachines.Count} z {active}. "
                    + "Wpisz je lokalnie — plik grupy nie zawiera haseł i nic z niego nie zostało skopiowane.";
        JoinOcrReadinessText = DescribeOcr(_ocrAvailable);
        JoinMissingPasswordsHint = !_monitoringEnabled
            ? "Lista pojawi się po włączeniu globalnego monitorowania."
            : JoinMissingPasswordMachines.Count == 0
                ? "Wszystkie aktywne maszyny z pliku grupy mają już lokalne hasło na tym komputerze."
                : "";

        if (!_monitoringEnabled)
        {
            JoinReadinessText = "Włącz monitorowanie przed dołączeniem do grupy";
            JoinReadinessDetail = "Globalny wyłącznik monitorowania jest wyłączony. Magazyn haseł nie został odczytany.";
            IsJoinReadinessWarning = true;
        }
        else if (active == 0)
        {
            JoinReadinessText = "Plik grupy nie ma aktywnej maszyny";
            JoinReadinessDetail = "Grupa potrzebuje co najmniej jednej aktywnej maszyny do obserwacji.";
            IsJoinReadinessWarning = true;
        }
        else if (JoinMissingPasswordMachines.Count > 0)
        {
            JoinReadinessText = "Uzupełnij lokalne hasła VNC";
            JoinReadinessDetail = "Brakujące hasła blokują gotowość tego komputera do przejęcia obserwacji maszyn z grupy.";
            IsJoinReadinessWarning = true;
        }
        else if (readiness.OcrLimited)
        {
            JoinReadinessText = "Gotowy do czerwonych alarmów — OCR ograniczony";
            JoinReadinessDetail = "Brak OCR nie blokuje wykrywania czerwieni, lecz ogranicza opis alarmu, postój i kontrolę ekranu HMI.";
            IsJoinReadinessWarning = true;
        }
        else
        {
            JoinReadinessText = "Ten komputer jest gotowy lokalnie";
            JoinReadinessDetail = "Monitorowanie, maszyny z pliku grupy, lokalne hasła VNC i OCR są przygotowane.";
            IsJoinReadinessWarning = !readiness.CanObserve;
        }

        JoinReadinessDetail += " Chwilowo niedostępna maszyna nie blokuje tego kroku. "
            + "Sieć, zapora i drugi komputer zostaną sprawdzone w późniejszym kroku.";

        _joinCanObserve = readiness.CanObserve;
        RefreshJoinApplyHint();
        OnPropertyChanged(nameof(CanApplyJoin));
        ApplyJoinCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Says in one sentence what the explicit join will do, or what still blocks it, so a disabled button is never
    /// silent. It only reuses already computed local readiness and never names the invitation or a secret.
    /// </summary>
    private void RefreshJoinApplyHint() => JoinApplyHint = IsJoinApplyCompleted
        ? "Ustawienia grupy są zapisane i włączone. Stan faktyczny pokazuje sekcja powyżej, a przycisk "
            + "Sprawdź sieć grupy otwiera osobny krok, który tylko odczytuje, czy grupa działa teraz."
        : _joinCanObserve
            ? "Dopiero przycisk Dołącz i włącz zapisze ustawienia grupy, poprosi o UAC i wykona transakcyjne "
                + "przełączenie ustawień, zapory oraz runtime. Diagnostyka sieci będzie osobnym krokiem."
            : "Przycisk Dołącz i włącz odblokuje się, gdy przygotowanie tego komputera będzie kompletne.";

    /// <summary>
    /// Turns one status reading into the network checklist. Every row reports what the runtime currently says; the
    /// firewall, the LAN itself and the second computer's alarm are deliberately reported as not checked, because
    /// this step only reads local runtime state and must never claim more than it verified.
    /// </summary>
    private void BuildGroupDiagnostics(MultiComputerStatusInput state)
    {
        int port = _currentCluster.UdpPort;
        var rows = new List<GroupDiagnosticsRow>();

        rows.Add(!state.SavedEnabled
            ? new GroupDiagnosticsRow("Zapisane ustawienie grupy", "Wyłączone",
                "Ustawienia tego komputera mówią, że pracuje samodzielnie. Bez włączonej grupy nie ma czego sprawdzać w sieci.",
                GroupDiagnosticsSeverity.Problem)
            : string.IsNullOrWhiteSpace(state.SavedGroupName)
                ? new GroupDiagnosticsRow("Zapisane ustawienie grupy", "Włączone bez danych grupy",
                    "Współpraca jest włączona, ale nie zapisano tożsamości grupy. Wróć do wyboru pliku grupy.",
                    GroupDiagnosticsSeverity.Problem)
                : new GroupDiagnosticsRow("Zapisane ustawienie grupy", $"Włączone — {state.SavedGroupName}",
                    "To wyłącznie zapis w ustawieniach tego komputera. O tym, czy grupa faktycznie działa teraz, mówią kolejne wiersze.",
                    GroupDiagnosticsSeverity.Ok));

        string transportLabel = $"Transport UDP grupy (port {port})";
        rows.Add(!string.IsNullOrWhiteSpace(state.Failure)
            ? new GroupDiagnosticsRow(transportLabel, "Problem",
                $"{state.Failure} Monitorowanie lokalne pozostaje aktywne, jeśli jest włączone. Ten krok niczego nie naprawia.",
                GroupDiagnosticsSeverity.Problem)
            : state.HasInterruptedChange
                ? new GroupDiagnosticsRow(transportLabel, "Niedokończona zmiana",
                    "Poprzednia zmiana grupy nie została dokończona. Działa ostatni bezpieczny stan runtime.",
                    GroupDiagnosticsSeverity.Problem)
                : state.NetworkActive
                    ? new GroupDiagnosticsRow(transportLabel, "Nasłuchuje",
                        $"Gniazdo UDP na porcie {port} jest otwarte na tym komputerze. To nie dowodzi, że zapora przepuszcza ruch do drugiego komputera.",
                        GroupDiagnosticsSeverity.Ok)
                    : new GroupDiagnosticsRow(transportLabel, "Nie działa",
                        "Ustawienie jest zapisane jako włączone, ale transport grupy faktycznie nie działa. Ten komputer obserwuje lokalnie.",
                        GroupDiagnosticsSeverity.Problem));

        rows.Add(state.StartupFirewallCheck is null
            ? new GroupDiagnosticsRow("Profil sieci i reguła zapory", "Nie sprawdzono",
                "Odczyt wykonuje się przy starcie z włączoną grupą i po zastosowaniu ustawień; jeszcze go nie ma.",
                GroupDiagnosticsSeverity.NotChecked)
            : state.StartupFirewallCheck.Problem
                ? new GroupDiagnosticsRow("Profil sieci i reguła zapory", "Problem", state.StartupFirewallCheck.Text,
                    GroupDiagnosticsSeverity.Problem)
                : new GroupDiagnosticsRow("Profil sieci i reguła zapory", "Bez zastrzeżeń",
                    $"{state.StartupFirewallCheck.Text} Odczyt z {state.StartupFirewallCheck.At:HH:mm}; nie dowodzi, że drugi komputer odbiera.",
                    GroupDiagnosticsSeverity.Ok));

        string unicast = _currentCluster.Peers.Count == 0
            ? "Lista adresów spoza zasięgu rozgłoszeń jest pusta — liczy się rozgłoszenie w tej podsieci."
            : $"Dodatkowo odpytywane adresy spoza rozgłoszeń: {_currentCluster.Peers.Count}.";
        rows.Add(!state.NetworkActive
            ? new GroupDiagnosticsRow("Widoczność drugiego komputera", "Nie sprawdzono",
                "Bez działającego transportu grupy nie da się zobaczyć drugiego komputera.",
                GroupDiagnosticsSeverity.NotChecked)
            : state.VisibleComputers > 1 && state.IncompatibleComputers.Count > 0
                ? new GroupDiagnosticsRow("Widoczność drugiego komputera", VisibleComputersLine(state),
                    $"Nie współpracuje z tym komputerem: {string.Join("; ", state.IncompatibleComputers)}. "
                        + $"{state.IncompatibleAdvice} Klucz grupy i zapora są tu w porządku — komunikaty dochodzą. {unicast}",
                    GroupDiagnosticsSeverity.Problem)
            : state.VisibleComputers > 1
                ? new GroupDiagnosticsRow("Widoczność drugiego komputera", $"Widoczne komputery: {state.VisibleComputers}",
                    $"Ten komputer odbiera komunikaty od pozostałych komputerów grupy. {unicast}",
                    GroupDiagnosticsSeverity.Ok)
                : new GroupDiagnosticsRow("Widoczność drugiego komputera", "Tylko ten komputer",
                    $"Drugi komputer jeszcze się nie zgłosił. Najczęstsze przyczyny: nie jest uruchomiony, ma inną grupę albo inny port, "
                        + $"albo zapora blokuje UDP {port}. Zapory ten krok nie sprawdza i nie zmienia. {unicast}",
                    GroupDiagnosticsSeverity.Waiting));

        rows.Add(!state.NetworkActive
            ? new GroupDiagnosticsRow("Świeżość wspólnego stanu", "Nie sprawdzono",
                "Bez działającego transportu grupy nie ma wspólnego stanu do oceny.",
                GroupDiagnosticsSeverity.NotChecked)
            : state.NoWatcher
                ? new GroupDiagnosticsRow("Świeżość wspólnego stanu", "Brak obserwatora",
                    "Transport działa, ale żaden widoczny komputer nie przejął jeszcze obserwacji maszyn.",
                    GroupDiagnosticsSeverity.Waiting)
                : state.IsWatcher
                    ? new GroupDiagnosticsRow("Świeżość wspólnego stanu", "Stan powstaje na tym komputerze",
                        "Ten komputer obserwuje maszyny, więc wspólny stan jest tworzony lokalnie i rozsyłany do grupy.",
                        GroupDiagnosticsSeverity.Ok)
                    : state.StateFresh
                        ? new GroupDiagnosticsRow("Świeżość wspólnego stanu", "Świeży",
                            "Ten komputer ma świeży stan od obserwatora grupy.",
                            GroupDiagnosticsSeverity.Ok)
                        : new GroupDiagnosticsRow("Świeżość wspólnego stanu", "Nieświeży",
                            "Transport działa, ale ten komputer nie dostał świeżego stanu od obserwatora. Sprawdź drugi komputer.",
                            GroupDiagnosticsSeverity.Problem));

        rows.Add(!state.NetworkActive
            ? new GroupDiagnosticsRow("Rola w grupie", "Nie sprawdzono",
                "Rola ustala się dopiero przy działającym transporcie grupy.",
                GroupDiagnosticsSeverity.NotChecked)
            : state.IsWatcher
                ? new GroupDiagnosticsRow("Rola w grupie", "Obserwator",
                    "Ten komputer łączy się z maszynami i rozsyła ich stan pozostałym komputerom.",
                    GroupDiagnosticsSeverity.Ok)
                : state.NoWatcher
                    ? new GroupDiagnosticsRow("Rola w grupie", "Nieustalona",
                        "Żaden komputer nie ogłosił się obserwatorem. Przejęcie roli następuje z opóźnieniem.",
                        GroupDiagnosticsSeverity.Waiting)
                    : string.IsNullOrWhiteSpace(state.WatcherHost)
                        ? new GroupDiagnosticsRow("Rola w grupie", "Odbiorca — obserwator nieznany",
                            "Ten komputer odbiera wspólny stan, ale nie zna jeszcze nazwy obserwatora.",
                            GroupDiagnosticsSeverity.Waiting)
                        : new GroupDiagnosticsRow("Rola w grupie", $"Odbiorca — maszyny obserwuje {state.WatcherHost}",
                            "Ten komputer odbiera alerty od obserwatora i nie łączy się sam z maszynami.",
                            GroupDiagnosticsSeverity.Ok));

        rows.Add(new GroupDiagnosticsRow("Zapora, sieć i odbiór na hali", "Nie sprawdzono",
            "Ten krok tylko czyta stan runtime tego komputera. Reguł zapory, przepustowości sieci ani rzeczywistego alarmu "
                + "na drugim komputerze nie sprawdza — to wymaga próby na dwóch fizycznych komputerach.",
            GroupDiagnosticsSeverity.NotChecked));

        GroupDiagnosticsRows = rows.AsReadOnly();
        int problems = rows.Count(row => row.Severity == GroupDiagnosticsSeverity.Problem);
        int waiting = rows.Count(row => row.Severity == GroupDiagnosticsSeverity.Waiting);
        (GroupDiagnosticsSummaryText, GroupDiagnosticsSummaryDetail, IsGroupDiagnosticsWarning) = problems > 0
            ? ($"Grupa jeszcze nie działa poprawnie ({problems})",
                "Zapisane ustawienie jest jedno, a faktyczne działanie drugie — poniżej zaznaczono, co nie działa teraz. "
                    + "Ten krok niczego nie zmienił: ustawienia, sekret grupy, zapora i runtime pozostają bez zmian.", true)
            : waiting > 0
                ? ($"Ten komputer jest gotowy, grupa czeka ({waiting})",
                    "Ustawienia są zapisane i transport grupy działa na tym komputerze, ale grupa nie jest jeszcze kompletna. "
                        + "Uruchom drugi komputer i sprawdź ponownie.", true)
                : ("Grupa działa na tym komputerze",
                    "Zapisane ustawienie i faktyczny stan runtime są zgodne, a drugi komputer jest widoczny. "
                        + "To nadal nie jest odbiór na hali — ten krok nie sprawdził alarmu na maszynach.", false);

        GroupDiagnosticsCheckedAtText = $"Sprawdzono: {_now():HH:mm:ss}. To zdjęcie stanu sprzed chwili — "
            + "po zmianach na drugim komputerze sprawdź ponownie.";
        GroupDiagnosticsScopeText = "Diagnostyka tylko odczytuje stan działającego runtime. Nie zapisuje ustawień, "
            + "nie zmienia zapory, nie tworzy sekretu grupy i nie prosi o uprawnienia administratora.";
        OnPropertyChanged(nameof(IsGroupDiagnosticsResultVisible));
    }

    /// <summary>Closes the shared network step and drops its snapshot, whichever path had opened it.</summary>
    private void ClearGroupDiagnostics()
    {
        DiagnosticsOrigin = GroupDiagnosticsOrigin.None;
        GroupDiagnosticsRows = [];
        GroupDiagnosticsSummaryText = "";
        GroupDiagnosticsSummaryDetail = "";
        IsGroupDiagnosticsWarning = false;
        GroupDiagnosticsCheckedAtText = "";
        GroupDiagnosticsScopeText = "";
        OnPropertyChanged(nameof(IsGroupDiagnosticsResultVisible));
    }

    private void ClearJoinPreparation()
    {
        JoinReadinessText = "";
        JoinReadinessDetail = "";
        IsJoinReadinessWarning = false;
        JoinPasswordsReadinessText = "";
        JoinOcrReadinessText = "";
        JoinMissingPasswordMachines = [];
        JoinMissingPasswordsHint = "";
        JoinPasswordEntryHint = "";
        ClearJoinPasswordInput();
        JoinPasswordEntries = [];
        IsJoinPasswordSaveError = false;
        JoinPasswordSaveStatusText = "";
        _joinCanObserve = false;
        ResetJoinApplyStatus();
        ClearGroupDiagnostics();
    }

    /// <summary>Drops every typed password from memory; entered secrets never outlive this step.</summary>
    private void ClearJoinPasswordInput()
    {
        _sharedJoinPassword = "";
        foreach (JoinPasswordEntry entry in JoinPasswordEntries)
            entry.Clear();
        NotifyJoinPasswordInputChanged();
        JoinPasswordInputCleared?.Invoke();
    }

    private void NotifyJoinPasswordInputChanged()
    {
        OnPropertyChanged(nameof(CanSaveJoinPasswords));
        SaveJoinPasswordsCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeOcr(bool ocrAvailable) => ocrAvailable
        ? "OCR: dostępny."
        : "OCR: niedostępny — czerwone alarmy nadal działają, ale opis, postój i kontrola ekranu HMI będą ograniczone.";

    /// <summary>
    /// Leaving or restarting the create step drops the created invitation together with the apply result: it is a
    /// secret, so it must not outlive the step that produced it, even though that also ends the export. The network
    /// snapshot goes with it, exactly as leaving local preparation drops the one taken after a join.
    /// </summary>
    private void ResetCreateApplyStatus()
    {
        IsCreateApplyCompleted = false;
        IsCreateApplyError = false;
        CreateApplyStatusText = "";
        CreateApplyDetail = "";
        SetCreatedInvitation(null);
        IsGroupFileExported = false;
        IsGroupFileExportError = false;
        GroupFileExportStatusText = "";
        ClearGroupDiagnostics();
        OnPropertyChanged(nameof(IsCreateApplyStatusVisible));
    }

    private void ResetJoinApplyStatus()
    {
        IsJoinApplyCompleted = false;
        IsJoinApplyError = false;
        JoinApplyStatusText = "";
        JoinApplyDetail = "";
        RefreshJoinApplyHint();
        OnPropertyChanged(nameof(IsJoinApplyStatusVisible));
    }

    private void ResetJoinImportStatus()
    {
        IsJoinImportError = false;
        JoinImportStatusText = "";
    }

    private static GroupFile? SelectAndReadGroupFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Wybierz plik grupy VNC Alert",
            Filter = "Plik grupy VNC Alert (*.vncgroup)|*.vncgroup",
            DefaultExt = ".vncgroup",
            AddExtension = true,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
            return null;

        using var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GroupFileCodec.Read(stream);
    }

    /// <summary>
    /// Asks the user where to save the invitation and opens that one file for writing. It chooses no location by
    /// itself, so the secret never lands anywhere the user did not point at.
    /// </summary>
    private static GroupFileExportTarget? SelectGroupFileExport(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Zapisz plik grupy VNC Alert",
            Filter = "Plik grupy VNC Alert (*.vncgroup)|*.vncgroup",
            DefaultExt = ".vncgroup",
            AddExtension = true,
            FileName = suggestedFileName,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
            return null;

        var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
        return new GroupFileExportTarget(Path.GetFileName(dialog.FileName), stream);
    }

    private static ClusterConfig CopyCluster(ClusterConfig cluster) => cluster with
    {
        Peers = cluster.Peers.ToList(),
        Group = cluster.Group is null ? null : cluster.Group with { },
    };

    private static string VisibleComputersLine(MultiComputerStatusInput state) =>
        $"Widoczne komputery: {Math.Max(1, state.VisibleComputers)}"
        + (state.IncompatibleComputers.Count > 0 ? $", w tym niezgodne: {state.IncompatibleComputers.Count}" : "");

    private static (string State, string Detail, bool Warning) DescribeRuntime(MultiComputerStatusInput state)
    {
        if (!string.IsNullOrWhiteSpace(state.Failure))
            return ("Problem z połączeniem",
                $"{state.Failure} Monitorowanie lokalne pozostaje aktywne, jeśli jest włączone.", true);

        if (state.HasInterruptedChange)
            return ("Problem z konfiguracją",
                "Poprzednia zmiana grupy nie została dokończona. Działa ostatni bezpieczny stan runtime.", true);

        // A profile that fell back to Public after a restart blocks inbound UDP silently; name it before "waiting".
        if (state.SavedEnabled && state.StartupFirewallCheck is { Problem: true } firewall)
            return ("Problem z profilem sieci lub zaporą", firewall.Text, true);

        if (!state.NetworkActive)
            return ("Wyłączone",
                state.SavedEnabled
                    ? "Ustawienie jest zapisane jako włączone, ale transport grupy faktycznie nie działa. Ten komputer obserwuje lokalnie."
                    : "Transport grupy nie działa. Ten komputer pracuje samodzielnie.",
                state.SavedEnabled);

        if (!state.SavedEnabled)
            return ("Konfigurowanie",
                "Transport grupy nadal działa, chociaż zapisane ustawienie jest wyłączone. Trwa uzgadnianie zmiany.", true);

        string incompatibleNote = state.IncompatibleComputers.Count > 0
            ? $" Nie współpracuje z tym komputerem: {string.Join("; ", state.IncompatibleComputers)}. {state.IncompatibleAdvice}".TrimEnd()
            : "";
        if (state.NoWatcher)
            return ("Oczekiwanie na obserwatora",
                "Transport działa, ale żaden widoczny komputer nie przejął jeszcze obserwacji maszyn." + incompatibleNote, true);

        if (!state.StateFresh && !state.IsWatcher)
            return ("Problem z połączeniem",
                "Transport działa, ale ten komputer nie ma świeżego stanu od obserwatora." + incompatibleNote, true);

        if (state.IncompatibleComputers.Count > 0)
            return ("Niezgodne komputery w grupie",
                $"Nie współpracuje z tym komputerem: {string.Join("; ", state.IncompatibleComputers)}. "
                + "Każdy z nich może wtedy sam obserwować maszyny, a zamknięcie alertu i raport postojów nie przechodzą "
                + $"między nimi. {state.IncompatibleAdvice}".TrimEnd()
                + (state.WatcherHosts.Count > 1 ? $" Maszyny obserwują naraz (od ponad minuty): {string.Join(", ", state.WatcherHosts)}." : ""),
                true);

        if (state.WatcherHosts.Count > 1)
            return ("Dwóch obserwatorów naraz",
                $"Maszyny obserwują naraz (od ponad minuty): {string.Join(", ", state.WatcherHosts)}. Zwykle jeden z nich "
                + "ustępuje sam; jeśli to trwa, sprawdź w tabeli komputerów kolumnę „Współpraca” i wersje programu.",
                true);

        if (state.VisibleComputers <= 1)
            return ("Oczekiwanie na drugi komputer",
                state.IsWatcher
                    ? "Ten komputer obserwuje maszyny, ale nie widzi jeszcze drugiego komputera w grupie."
                    : "Transport działa, ale drugi komputer nie jest jeszcze widoczny.", false);

        return ("Połączono",
            state.IsWatcher
                ? "Ten komputer obserwuje maszyny i współpracuje z pozostałymi komputerami."
                : string.IsNullOrWhiteSpace(state.WatcherHost)
                    ? "Ten komputer odbiera wspólny stan grupy."
                    : $"Ten komputer odbiera alerty. Maszyny obserwuje {state.WatcherHost}.",
            false);
    }
}

public enum MultiComputerSetupPath
{
    None,
    CreateGroup,
    JoinGroup,
}

public enum CreateGroupWizardStep
{
    Draft,
    Confirmation,
}

public enum JoinGroupWizardStep
{
    File,
    Summary,
    Preparation,
}

/// <summary>
/// Which path opened the shared, read-only network step, or <see cref="None"/> when it is closed. The step is not a
/// value of either wizard, because both paths reach the same step and each keeps its own step underneath.
/// </summary>
public enum GroupDiagnosticsOrigin
{
    None,
    Create,
    Join,
}

/// <summary>How firmly one diagnostics row may speak; "not checked" is a first-class result here.</summary>
public enum GroupDiagnosticsSeverity
{
    Ok,
    Waiting,
    Problem,
    NotChecked,
}

public sealed record GroupReportSourceRow(string Host, string RoleText, string ReportSyncedText, string CooperationText = "")
{
    public static IReadOnlyList<GroupReportSourceRow> From(IReadOnlyList<PeerInfo> peers, string localHost,
        string? localVersion = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return [.. peers.OrderBy(p => p.Host, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            bool local = string.Equals(p.Host, localHost, StringComparison.OrdinalIgnoreCase);
            return new GroupReportSourceRow(
                p.Host + (local ? " (ten komputer)" : string.Empty),
                p.Role == PeerRole.Watcher ? "Obserwator" : "Tylko alerty",
                // Własne obserwacje są w dzienniku od razu — nie ma tu czego „uzupełniać”.
                local ? "na bieżąco"
                    : !GroupPeerStatus.Cooperates(p) ? "brak — nie współpracuje z tym komputerem, jego postoje tu nie trafiają"
                    : p.DowntimeSyncedAt is { } at ? at.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                    : "brak — jego postoje mogą w raporcie brakować",
                GroupPeerStatus.Describe(p, localVersion, local));
        })];
    }
}

public sealed record GroupDiagnosticsRow(
    string Label, string Result, string Detail, GroupDiagnosticsSeverity Severity)
{
    public bool IsOk => Severity == GroupDiagnosticsSeverity.Ok;
    public bool IsWaiting => Severity == GroupDiagnosticsSeverity.Waiting;
    public bool IsProblem => Severity == GroupDiagnosticsSeverity.Problem;
    public bool IsNotChecked => Severity == GroupDiagnosticsSeverity.NotChecked;
}

/// <summary>Small immutable boundary between saved configuration and the live cluster runtime.</summary>
public sealed record MultiComputerStatusInput(
    bool SavedEnabled,
    string? SavedGroupName,
    bool NetworkActive,
    string? Failure,
    bool HasInterruptedChange,
    int VisibleComputers,
    bool IsWatcher,
    bool NoWatcher,
    bool StateFresh,
    string? WatcherHost)
{
    public bool MonitoringEnabled { get; init; } = true;
    public bool OcrAvailable { get; init; } = true;
    public int ConfiguredProfileCount { get; init; }
    public bool LocalCanObserve { get; init; }
    public IReadOnlyList<string> MissingPasswordMachineIds { get; init; } = [];
    public IReadOnlyList<MultiComputerMachineInput> Machines { get; init; } = [];

    /// <summary>Komputery grupy i czas, kiedy ostatnio uzupełniły tutejszy raport postojów.</summary>
    public IReadOnlyList<GroupReportSourceRow> ReportSources { get; init; } = [];

    public IReadOnlyList<string> IncompatibleComputers { get; init; } = [];

    public string IncompatibleAdvice { get; init; } = "";

    public IReadOnlyList<string> WatcherHosts { get; init; } = [];
    public GroupProfile? CurrentProfile { get; init; }
    public ClusterConfig CurrentCluster { get; init; } = new();
    /// <summary>Result of the orchestrator's startup read of the group interface's network profile and firewall.</summary>
    public FirewallStartupCheck? StartupFirewallCheck { get; init; }

    /// <summary>
    /// Checks only whether a local VNC password exists for a machine id. It never returns a secret value, so the
    /// wizard can judge readiness for machines listed in an invitation without copying anything from that file.
    /// Null means the secret store is unavailable; every machine is then treated as missing its password.
    /// </summary>
    public Func<string, bool>? HasLocalVncPassword { get; init; }

    public static MultiComputerStatusInput From(AppOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        var runtime = orchestrator.Cluster;
        var config = orchestrator.Config;
        var readiness = orchestrator.GetWatcherReadiness();
        var peers = runtime.Peers;
        var summary = runtime.Configuration.Enabled
            ? GroupPeerStatus.Summarize(peers, runtime.Version, orchestrator.Clock.UtcNow,
                TimeSpan.FromMilliseconds(runtime.Configuration.PeerTimeoutMs))
            : new GroupPeerSummary([], []);
        return new(
            config.Cluster.Enabled,
            config.Cluster.Group?.GroupName,
            runtime.NetworkActive,
            runtime.Failure,
            runtime.InterruptedGeneration is not null,
            peers.Count,
            runtime.IsWatcher,
            runtime.NoWatcher,
            runtime.StateFresh,
            runtime.WatcherHost)
        {
            MonitoringEnabled = config.Enabled,
            OcrAvailable = orchestrator.Ocr.IsAvailable,
            StartupFirewallCheck = orchestrator.StartupFirewallCheck,
            ConfiguredProfileCount = config.Profiles.Count,
            LocalCanObserve = readiness.CanObserve,
            MissingPasswordMachineIds = readiness.MissingPasswordMachineIds.ToArray(),
            ReportSources = GroupReportSourceRow.From(peers, Environment.MachineName, runtime.Version),
            IncompatibleComputers = summary.Incompatible,
            IncompatibleAdvice = summary.Advice,
            WatcherHosts = runtime.Configuration.Enabled ? orchestrator.ConfirmedWatcherHosts : [],
            HasLocalVncPassword = orchestrator.HasLocalVncPassword,
            CurrentProfile = GroupProfile.Capture(
                config, orchestrator.Help.Rules, orchestrator.Help.ProfileInstructions),
            CurrentCluster = config.Cluster with
            {
                Peers = config.Cluster.Peers.ToList(),
                Group = config.Cluster.Group is null ? null : config.Cluster.Group with { },
            },
            Machines = config.Machines
                .Select(machine => new MultiComputerMachineInput(
                    machine.Id, machine.Name, machine.Profile, machine.Enabled))
                .ToArray(),
        };
    }
}

/// <summary>
/// One writable destination the user pointed at for the invitation. The view model owns the stream and closes it
/// right after writing; <see cref="DisplayName"/> is only shown in status text and never carries a secret.
/// </summary>
public sealed record GroupFileExportTarget(string DisplayName, Stream Content)
{
    public override string ToString() => $"GroupFileExportTarget {{ DisplayName = {DisplayName} }}";
}

public sealed record MultiComputerMachineInput(string Id, string Name, string Profile, bool Enabled);

public sealed record MultiComputerMachineSummary(string Name, string Profile);

/// <summary>
/// One machine from the invitation that still needs a local VNC password. The typed value lives only in a
/// private field: it is never a bindable property, never lands in the visual tree as text and is dropped as
/// soon as the step is left or the save completes.
/// </summary>
public sealed class JoinPasswordEntry
{
    private string _password = "";

    public JoinPasswordEntry(string machineId, string name, string profile)
    {
        MachineId = machineId;
        Name = name;
        Profile = profile;
    }

    public string MachineId { get; }
    public string Name { get; }
    public string Profile { get; }
    public bool HasPassword => _password.Length > 0;

    internal string Password => _password;

    internal void SetPassword(string? password) => _password = password ?? "";

    internal void Clear() => _password = "";

    public override string ToString() => $"JoinPasswordEntry {{ MachineId = {MachineId}, HasPassword = {HasPassword} }}";
}

public sealed record JoinGroupMachineSummary(string Id, string Name, string Profile, bool Enabled)
{
    public string StateText => Enabled ? "aktywna" : "wyłączona";
}

public sealed record JoinGroupDifferenceRow(string Label, string Value);

/// <summary>
/// Frozen import preview. The secret invitation is retained only inside GroupSettingsProposal and is never
/// exposed to bindings, diagnostics or error text. This step cannot apply the proposal.
/// </summary>
public sealed class JoinGroupDraftProposal
{
    private readonly GroupSettingsProposal _proposal;

    private JoinGroupDraftProposal(GroupFile invitation, GroupProfile localProfile, ClusterConfig localCluster)
    {
        _proposal = new GroupSettingsProposal(invitation, localCluster, enabled: true);
        GroupSettingsData data = _proposal.GetData();
        GroupIdentity identity = data.Cluster.Group!;
        GroupName = identity.GroupName;
        GroupId = identity.GroupId;
        ProtocolVersion = identity.ProtocolVersion;
        ProfileRevision = identity.ProfileRevision;

        JoinGroupMachineSummary[] machines = data.Profile.Machines
            .Select(machine => new JoinGroupMachineSummary(
                machine.Id, machine.Name, machine.Profile, machine.Enabled))
            .ToArray();
        Machines = Array.AsReadOnly(machines);
        ActiveMachineCount = machines.Count(machine => machine.Enabled);
        TotalMachineCount = machines.Length;

        GroupProfileDifference difference = _proposal.CompareWith(localProfile);
        Difference = Freeze(difference);
        var names = data.Profile.Machines.ToDictionary(machine => machine.Id, machine => machine.Name,
            StringComparer.Ordinal);
        foreach (var machine in localProfile.Machines)
            names.TryAdd(machine.Id, machine.Name);
        DifferenceRows = Array.AsReadOnly(new[]
        {
            new JoinGroupDifferenceRow("Dodane maszyny", Describe(Difference.AddedMachines, names)),
            new JoinGroupDifferenceRow("Usunięte maszyny", Describe(Difference.RemovedMachines, names)),
            new JoinGroupDifferenceRow("Zmienione ustawienia", Describe(Difference.ChangedMachines, names)),
            new JoinGroupDifferenceRow("Zostaną włączone", Describe(Difference.EnabledMachines, names)),
            new JoinGroupDifferenceRow("Zostaną wyłączone", Describe(Difference.DisabledMachines, names)),
            // Instrukcje naprawy nie wpływają na zgodność komputerów,
            // ale przy dołączaniu do grupy trzeba wiedzieć, że przyjdą z niej inne treści pomocy.
            new JoinGroupDifferenceRow("Instrukcje naprawy",
                difference.InstructionsChanged ? "inne niż lokalne — po dołączeniu obowiązują treści grupy" : "takie same"),
        });
    }

    public string GroupName { get; }
    public Guid GroupId { get; }
    public int ProtocolVersion { get; }
    public long ProfileRevision { get; }
    public int ActiveMachineCount { get; }
    public int TotalMachineCount { get; }
    public IReadOnlyList<JoinGroupMachineSummary> Machines { get; }
    public GroupProfileDifference Difference { get; }
    public IReadOnlyList<JoinGroupDifferenceRow> DifferenceRows { get; }
    public string ProfileDifferenceText => Difference.ProfileChanged
        ? "Profil grupy różni się od lokalnego profilu. Poniżej pokazano zakres zmian."
        : "Profil grupy jest zgodny z lokalnym profilem monitorowania.";

    /// <summary>
    /// Local readiness for the machine list carried by the invitation, using the same <see cref="WatcherReadiness"/>
    /// contract as the runtime. Only the presence of a local VNC password is checked: the file never carries
    /// passwords, nothing is copied out of it and nothing is written. A missing store means "no password yet".
    /// </summary>
    public WatcherReadiness EvaluateLocalReadiness(
        bool monitoringEnabled, bool ocrAvailable, Func<string, bool>? hasLocalVncPassword)
    {
        AppConfig projected = _proposal.GetData().Profile.ApplyTo(new AppConfig { Enabled = monitoringEnabled });
        return WatcherReadiness.Evaluate(projected, hasLocalVncPassword ?? (_ => false), ocrAvailable,
            GroupCompatibility.Compatible);
    }

    /// <summary>
    /// The frozen transactional proposal behind this preview, for the one explicit join command. It still holds the
    /// secret invitation, so it must go straight to the apply operation and never to bindings, status text or
    /// diagnostics. <see cref="GroupSettingsProposal"/> is immutable, so sharing it cannot change this preview.
    /// </summary>
    public GroupSettingsProposal GetApplyProposal() => _proposal;

    public static JoinGroupDraftProposal Create(
        GroupFile invitation, GroupProfile localProfile, ClusterConfig localCluster)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(localProfile);
        ArgumentNullException.ThrowIfNull(localCluster);
        GroupFileCodec.Validate(invitation);
        return new(invitation, localProfile, localCluster);
    }

    private static GroupProfileDifference Freeze(GroupProfileDifference difference) => new(
        difference.ProfileChanged,
        difference.InstructionsChanged,
        Array.AsReadOnly(difference.AddedMachines.ToArray()),
        Array.AsReadOnly(difference.RemovedMachines.ToArray()),
        Array.AsReadOnly(difference.ChangedMachines.ToArray()),
        Array.AsReadOnly(difference.EnabledMachines.ToArray()),
        Array.AsReadOnly(difference.DisabledMachines.ToArray()));

    private static string Describe(IReadOnlyList<string> ids, IReadOnlyDictionary<string, string> names) =>
        ids.Count == 0 ? "brak" : string.Join(", ", ids.Select(id => names.GetValueOrDefault(id, id)));

    public override string ToString() =>
        $"JoinGroupDraftProposal {{ GroupName = {GroupName}, GroupId = {GroupId}, [secret invitation] }}";
}

/// <summary>
/// Frozen, non-secret proposal shown by the confirmation step. The shared profile is kept as a private
/// deep snapshot and is only returned as another copy, so later refreshes or callers cannot mutate it.
/// It does not create a group identity or network key; those are created only by the explicit final command.
/// </summary>
public sealed class CreateGroupDraftProposal
{
    private readonly GroupProfile _profile;

    private CreateGroupDraftProposal(string groupName, GroupProfile profile, string ocrReadinessText)
    {
        GroupName = groupName;
        _profile = CloneProfile(profile);
        ProfileHash = GroupFileCodec.ProfileHash(_profile);

        MultiComputerMachineSummary[] machines = _profile.Machines
            .Where(machine => machine.Enabled)
            .Select(machine => new MultiComputerMachineSummary(machine.Name, machine.Profile))
            .ToArray();
        Machines = Array.AsReadOnly(machines);
        ActiveMachineCount = machines.Length;
        TotalMachineCount = _profile.Machines.Count;
        ConfiguredProfileCount = _profile.Profiles.Count;
        ProfilesText = machines.Length == 0
            ? "—"
            : string.Join(", ", machines
                .GroupBy(machine => machine.Profile, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key} ({group.Count()})"));
        OcrReadinessText = ocrReadinessText;
    }

    public string GroupName { get; }
    public string ProfileHash { get; }
    public int ActiveMachineCount { get; }
    public int TotalMachineCount { get; }
    public int ConfiguredProfileCount { get; }
    public string ProfilesText { get; }
    public string OcrReadinessText { get; }
    public IReadOnlyList<MultiComputerMachineSummary> Machines { get; }

    public static CreateGroupDraftProposal Create(
        string groupName, GroupProfile profile, string ocrReadinessText)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(groupName) || groupName.Length > 100 || groupName.Any(char.IsControl))
            throw new ArgumentException("Nazwa grupy musi mieć od 1 do 100 znaków bez znaków sterujących.", nameof(groupName));
        return new(groupName, profile, ocrReadinessText);
    }

    public GroupProfile CopyProfile() => CloneProfile(_profile);

    private static GroupProfile CloneProfile(GroupProfile profile)
    {
        AppConfig config = profile.ApplyTo(new AppConfig());
        return GroupProfile.Capture(config, profile.Rules, profile.Instructions);
    }

    public override string ToString() => $"CreateGroupDraftProposal {{ GroupName = {GroupName}, ProfileHash = {ProfileHash} }}";
}
