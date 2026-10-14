using System.IO;
using System.Reflection;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Windows;

namespace MaszynyAlert.App.Tests;

public sealed class MultiComputerViewModelTests
{
    [Fact]
    public void ReportSourceRowsNameEveryComputerAndWhetherItAlreadySupplied()
    {
        var now = new DateTimeOffset(2026, 9, 15, 14, 2, 0, TimeSpan.Zero);
        MaszynyAlert.Core.Cluster.PeerInfo Peer(string host, MaszynyAlert.Core.Cluster.PeerRole role,
            DateTimeOffset? synced) =>
            new(host, 100, role, "0.2.0", now, 1, null, 7, 7) { DowntimeSyncedAt = synced };

        var rows = GroupReportSourceRow.From(
            [Peer("PC-B", MaszynyAlert.Core.Cluster.PeerRole.Ui, null),
             Peer("PC-A", MaszynyAlert.Core.Cluster.PeerRole.Watcher, now),
             Peer("PC-LOCAL", MaszynyAlert.Core.Cluster.PeerRole.Ui, null)],
            localHost: "PC-LOCAL");

        Assert.Equal(["PC-A", "PC-B", "PC-LOCAL (ten komputer)"], rows.Select(r => r.Host));
        Assert.Equal("Obserwator", rows[0].RoleText);
        Assert.Equal(now.ToLocalTime().ToString("dd.MM HH:mm"), rows[0].ReportSyncedText);
        Assert.Contains("brak", rows[1].ReportSyncedText, StringComparison.Ordinal);
        Assert.Contains("mogą w raporcie brakować", rows[1].ReportSyncedText, StringComparison.Ordinal);
        // Własne obserwacje są w dzienniku od razu — nie ma tu czego uzupełniać ani na co czekać.
        Assert.Equal("na bieżąco", rows[2].ReportSyncedText);
    }

    [Fact]
    public void ReportSourceRowsNameIncompatibleComputerAndWhy()
    {
        var now = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        var rows = GroupReportSourceRow.From(
            [new MaszynyAlert.Core.Cluster.PeerInfo("PSZEMEK", 100, MaszynyAlert.Core.Cluster.PeerRole.Watcher, "0.4.32",
                now, 1, null, 9, 9) { Compatibility = MaszynyAlert.Core.Cluster.GroupCompatibility.OtherProfile },
             new MaszynyAlert.Core.Cluster.PeerInfo("AIDITEKOMP1", 100, MaszynyAlert.Core.Cluster.PeerRole.Watcher, "0.4.79",
                now, 1, null, 9, 9)],
            localHost: "AIDITEKOMP1", localVersion: "0.4.79");

        var incompatible = rows.Single(r => r.Host == "PSZEMEK");
        Assert.Equal("inne ustawienia grupy albo inna wersja programu (0.4.32) — nie współpracuje z tym komputerem; zainstaluj tę samą wersję, a jeśli to nie pomoże — wczytaj na obu komputerach ten sam, aktualny plik grupy",
            incompatible.CooperationText);
        Assert.DoesNotContain("mogą w raporcie brakować", incompatible.ReportSyncedText, StringComparison.Ordinal);
        Assert.Contains("nie współpracuje", incompatible.ReportSyncedText, StringComparison.Ordinal);
        Assert.Equal("ten komputer", rows.Single(r => r.Host.StartsWith("AIDITEKOMP1", StringComparison.Ordinal)).CooperationText);
    }

    [Fact]
    public void RuntimeCardWarnsAboutIncompatibleComputerAndDoesNotCountItAsCooperating()
    {
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: true, SavedGroupName: "Hala — Aidite", NetworkActive: true, Failure: null,
            HasInterruptedChange: false, VisibleComputers: 4, IsWatcher: true, NoWatcher: false,
            StateFresh: true, WatcherHost: "AIDITEKOMP1")
        {
            IncompatibleComputers = ["PSZEMEK — inna wersja programu (0.4.32)"],
            IncompatibleAdvice = "Zainstaluj na wszystkich komputerach grupy tę samą wersję programu.",
            WatcherHosts = ["AIDITEKOMP1", "PSZEMEK"],
        });

        Assert.Equal("Widoczne komputery: 4, w tym niezgodne: 1", viewModel.VisibleComputersText);
        Assert.Equal("Niezgodne komputery w grupie", viewModel.RuntimeStateText);
        Assert.True(viewModel.IsRuntimeWarning);
        Assert.Contains("PSZEMEK — inna wersja programu (0.4.32)", viewModel.RuntimeStateDetail);
        Assert.Contains("tę samą wersję", viewModel.RuntimeStateDetail);
        Assert.Contains("AIDITEKOMP1, PSZEMEK", viewModel.RuntimeStateDetail);
        Assert.DoesNotContain("profileHash", viewModel.RuntimeStateDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompatibleCard_UsesTheAdviceForTheReason()
    {
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: true, SavedGroupName: "Hala — Aidite", NetworkActive: true, Failure: null,
            HasInterruptedChange: false, VisibleComputers: 2, IsWatcher: true, NoWatcher: false,
            StateFresh: true, WatcherHost: "PC-A")
        {
            IncompatibleComputers = ["PC-B — inne ustawienia grupy"],
            IncompatibleAdvice = "Wczytaj na wszystkich komputerach grupy ten sam, aktualny plik grupy.",
        });

        Assert.Contains("ten sam, aktualny plik grupy", viewModel.RuntimeStateDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("tę samą wersję", viewModel.RuntimeStateDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, "Oczekiwanie na obserwatora")]
    [InlineData(false, false, "Problem z połączeniem")]
    public void NoWatcherAndStaleState_WinOverIncompatibleCard(bool noWatcher, bool stateFresh, string expected)
    {
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: true, SavedGroupName: "Hala — Aidite", NetworkActive: true, Failure: null,
            HasInterruptedChange: false, VisibleComputers: 2, IsWatcher: false, NoWatcher: noWatcher,
            StateFresh: stateFresh, WatcherHost: null)
        {
            IncompatibleComputers = ["PSZEMEK — inna wersja programu (0.4.32)"],
            IncompatibleAdvice = "Zainstaluj na wszystkich komputerach grupy tę samą wersję programu.",
        });

        Assert.Equal(expected, viewModel.RuntimeStateText);
        Assert.True(viewModel.IsRuntimeWarning);
        // Niezgodny komputer nadal nazwany — w tej samej karcie.
        Assert.Contains("PSZEMEK — inna wersja programu (0.4.32)", viewModel.RuntimeStateDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCardWarnsAboutSecondObserver()
    {
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: true, SavedGroupName: "Hala — Aidite", NetworkActive: true, Failure: null,
            HasInterruptedChange: false, VisibleComputers: 2, IsWatcher: true, NoWatcher: false,
            StateFresh: true, WatcherHost: "PC-A")
        {
            WatcherHosts = ["PC-A", "PC-B"],
        });

        Assert.Equal("Widoczne komputery: 2", viewModel.VisibleComputersText);
        Assert.Equal("Dwóch obserwatorów naraz", viewModel.RuntimeStateText);
        Assert.True(viewModel.IsRuntimeWarning);
        Assert.Contains("PC-A, PC-B", viewModel.RuntimeStateDetail);
    }

    [Fact]
    public void ReportSourceRowsReachTheViewModelFromTheStatusInput()
    {
        var input = new MultiComputerStatusInput(
            SavedEnabled: true, SavedGroupName: "Hala — Aidite", NetworkActive: true, Failure: null,
            HasInterruptedChange: false, VisibleComputers: 2, IsWatcher: true, NoWatcher: false,
            StateFresh: true, WatcherHost: "PC-A")
        {
            ReportSources = [new GroupReportSourceRow("PC-B", "Tylko alerty", "15.09 14:02")],
        };

        var viewModel = new MultiComputerViewModel(() => input);

        var row = Assert.Single(viewModel.ReportSourceRows);
        Assert.Equal("PC-B", row.Host);
        Assert.Equal("15.09 14:02", row.ReportSyncedText);
    }

    [Fact]
    public void SeparatesSavedEnabledSettingFromInactiveRuntimeFailure()
    {
        var input = new MultiComputerStatusInput(
            SavedEnabled: true,
            SavedGroupName: "Hala — Aidite",
            NetworkActive: false,
            Failure: "Obserwacja lokalna, brak współpracy: nie można otworzyć portu UDP.",
            HasInterruptedChange: false,
            VisibleComputers: 0,
            IsWatcher: true,
            NoWatcher: false,
            StateFresh: true,
            WatcherHost: null);

        var viewModel = new MultiComputerViewModel(() => input);

        Assert.Equal("Włączona", viewModel.SavedSettingText);
        Assert.Equal("Grupa: Hala — Aidite", viewModel.SavedGroupText);
        Assert.Equal("Problem z połączeniem", viewModel.RuntimeStateText);
        Assert.Contains("Monitorowanie lokalne", viewModel.RuntimeStateDetail);
        Assert.True(viewModel.RuntimeDiffersFromSaved);
        Assert.True(viewModel.IsRuntimeWarning);
    }

    [Fact]
    public void KeepsSavedGroupVisibleWhenCooperationIsDisabled()
    {
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: false,
            SavedGroupName: "Hala — Aidite",
            NetworkActive: false,
            Failure: null,
            HasInterruptedChange: false,
            VisibleComputers: 0,
            IsWatcher: true,
            NoWatcher: false,
            StateFresh: true,
            WatcherHost: null));

        Assert.Equal("Wyłączona", viewModel.SavedSettingText);
        Assert.Equal("Grupa: Hala — Aidite", viewModel.SavedGroupText);
        Assert.Contains("pozostają zapisane", viewModel.SavedSettingDetail);
        Assert.Equal("Wyłączone", viewModel.RuntimeStateText);
        Assert.False(viewModel.RuntimeDiffersFromSaved);
        Assert.False(viewModel.IsRuntimeWarning);
    }

    [Theory]
    [InlineData(1, true, "Oczekiwanie na drugi komputer")]
    [InlineData(2, true, "Połączono")]
    [InlineData(2, false, "Połączono")]
    public void DescribesLiveGroupWithoutTreatingSavedSettingAsProofOfConnection(
        int visibleComputers, bool isWatcher, string expectedRuntimeState)
    {
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: true,
            SavedGroupName: "Hala — Aidite",
            NetworkActive: true,
            Failure: null,
            HasInterruptedChange: false,
            VisibleComputers: visibleComputers,
            IsWatcher: isWatcher,
            NoWatcher: false,
            StateFresh: true,
            WatcherHost: isWatcher ? null : "PC-A"));

        Assert.Equal(expectedRuntimeState, viewModel.RuntimeStateText);
        Assert.Equal($"Widoczne komputery: {visibleComputers}", viewModel.VisibleComputersText);
        Assert.False(viewModel.RuntimeDiffersFromSaved);
    }

    [Fact]
    public void CreateAndJoinAreDraftChoicesWithoutChangingStatusSource()
    {
        int reads = 0;
        var source = new MultiComputerStatusInput(false, null, false, null, false, 0, true, false, true, null);
        var viewModel = new MultiComputerViewModel(() => { reads++; return source; });

        viewModel.SelectCreateGroupCommand.Execute(null);

        Assert.True(viewModel.IsCreateGroupSelected);
        Assert.False(viewModel.IsJoinGroupSelected);
        Assert.Contains("Utwórz grupę", viewModel.ChoiceTitle);
        Assert.Equal(1, reads);

        viewModel.SelectJoinGroupCommand.Execute(null);

        Assert.False(viewModel.IsCreateGroupSelected);
        Assert.True(viewModel.IsJoinGroupSelected);
        Assert.Contains("Dołącz do grupy", viewModel.ChoiceTitle);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void CreateDraftSummarizesNameMachinesProfilesAndLocalReadinessWithoutApplyingAnything()
    {
        int reads = 0;
        var input = new MultiComputerStatusInput(false, null, false, null, false, 0, true, false, true, null)
        {
            MonitoringEnabled = true,
            OcrAvailable = true,
            ConfiguredProfileCount = 3,
            MissingPasswordMachineIds = [],
            Machines =
            [
                new("aidite1", "Aidite 1", "opencnc", true),
                new("aidite2", "Aidite 2", "opencnc", true),
                new("cameo1", "Cameo 1", "aidite-light2", false),
            ],
        };
        var viewModel = new MultiComputerViewModel(() => { reads++; return input; });

        viewModel.SelectCreateGroupCommand.Execute(null);

        Assert.True(viewModel.IsCreateGroupSelected);
        Assert.Equal("Hala — Aidite", viewModel.DraftGroupName);
        Assert.True(viewModel.IsDraftGroupNameValid);
        Assert.Contains("2 z 3", viewModel.CreateMachineSummaryText);
        Assert.Contains("opencnc (2)", viewModel.CreateProfilesText);
        Assert.Contains("3", viewModel.CreateProfilesText);
        Assert.Collection(viewModel.CreateMachines,
            row => Assert.Equal(("Aidite 1", "opencnc"), (row.Name, row.Profile)),
            row => Assert.Equal(("Aidite 2", "opencnc"), (row.Name, row.Profile)));
        Assert.Equal("Ten komputer jest gotowy lokalnie", viewModel.LocalReadinessText);
        Assert.Contains("2/2", viewModel.PasswordsReadinessText);
        Assert.Contains("dostępny", viewModel.OcrReadinessText);
        Assert.False(viewModel.IsLocalReadinessWarning);

        viewModel.DraftGroupName = "Zmiana nocna";

        Assert.Equal("Zmiana nocna", viewModel.DraftGroupName);
        Assert.True(viewModel.IsDraftGroupNameValid);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void CreateDraftShowsMissingPasswordsAndOcrLimitationAndValidatesOnlyLocalNameDraft()
    {
        var input = new MultiComputerStatusInput(false, null, false, null, false, 0, true, false, true, null)
        {
            MonitoringEnabled = true,
            OcrAvailable = false,
            ConfiguredProfileCount = 1,
            MissingPasswordMachineIds = ["aidite2"],
            Machines =
            [
                new("aidite1", "Aidite 1", "opencnc", true),
                new("aidite2", "Aidite 2", "opencnc", true),
            ],
        };
        var viewModel = new MultiComputerViewModel(() => input);

        viewModel.SelectCreateGroupCommand.Execute(null);

        Assert.Equal("Uzupełnij przygotowanie tego komputera", viewModel.LocalReadinessText);
        Assert.Contains("Aidite 2", viewModel.PasswordsReadinessText);
        Assert.Contains("czerwone alarmy nadal działają", viewModel.OcrReadinessText);
        Assert.True(viewModel.IsLocalReadinessWarning);

        viewModel.DraftGroupName = " ";
        Assert.False(viewModel.IsDraftGroupNameValid);
        Assert.Contains("Wpisz nazwę", viewModel.DraftGroupNameStatus);

        viewModel.DraftGroupName = new string('A', 101);
        Assert.False(viewModel.IsDraftGroupNameValid);
        Assert.Contains("100", viewModel.DraftGroupNameStatus);
    }

    [Theory]
    [InlineData(true, true, true, "Hala — Aidite", true)]
    [InlineData(true, true, false, "Hala — Aidite", false)]
    [InlineData(true, false, true, "Hala — Aidite", false)]
    [InlineData(true, true, true, " ", false)]
    [InlineData(false, true, true, "Hala — Aidite", true)]
    public void ContinueRequiresValidNameAndWatcherReadinessButNotOcr(
        bool ocrAvailable, bool monitoringEnabled, bool canObserve, string name, bool expected)
    {
        var viewModel = new MultiComputerViewModel(() => ReadyInput(
            monitoringEnabled: monitoringEnabled,
            ocrAvailable: ocrAvailable,
            canObserve: canObserve));
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = name;

        Assert.Equal(expected, viewModel.CanContinueCreate);
        Assert.Equal(expected, viewModel.ContinueCreateCommand.CanExecute(null));
    }

    [Fact]
    public void ContinueCreatesFrozenProfileProposalAndBackKeepsEnteredNameWithoutApplyingAnything()
    {
        int reads = 0;
        MultiComputerStatusInput input = ReadyInput();
        var sourceProfile = input.CurrentProfile!;
        var viewModel = new MultiComputerViewModel(() => { reads++; return input; });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = "Hala testowa";

        viewModel.ContinueCreateCommand.Execute(null);

        CreateGroupDraftProposal proposal = Assert.IsType<CreateGroupDraftProposal>(viewModel.CurrentCreateProposal);
        Assert.True(viewModel.IsCreateConfirmationStep);
        Assert.False(viewModel.IsCreateDraftStep);
        Assert.Equal("Hala testowa", proposal.GroupName);
        Assert.Equal(2, proposal.ActiveMachineCount);
        Assert.Equal("opencnc (2)", proposal.ProfilesText);
        Assert.Equal(GroupFileCodec.ProfileHash(sourceProfile), proposal.ProfileHash);
        Assert.Equal(1, reads);

        sourceProfile.Machines[0] = sourceProfile.Machines[0] with { Name = "Zmieniona po przejściu" };
        GroupProfile firstCopy = proposal.CopyProfile();
        Assert.Equal("Aidite 1", firstCopy.Machines[0].Name);
        firstCopy.Machines[0] = firstCopy.Machines[0] with { Name = "Zmieniona kopia" };
        Assert.Equal("Aidite 1", proposal.CopyProfile().Machines[0].Name);

        viewModel.BackToCreateDraftCommand.Execute(null);

        Assert.True(viewModel.IsCreateDraftStep);
        Assert.False(viewModel.IsCreateConfirmationStep);
        Assert.Null(viewModel.CurrentCreateProposal);
        Assert.Equal("Hala testowa", viewModel.DraftGroupName);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ApplyCreatesSecretProposalOnlyAfterExplicitClickAndRefreshesSavedAndRuntimeState()
    {
        int applies = 0;
        GroupSettingsProposal? applied = null;
        MultiComputerStatusInput input = ReadyInput();
        var viewModel = new MultiComputerViewModel(() => input, (proposal, _) =>
        {
            applies++;
            applied = proposal;
            input = ReadyInput() with
            {
                SavedEnabled = true,
                SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                NetworkActive = true,
                VisibleComputers = 1,
            };
            return Task.CompletedTask;
        });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = "Hala testowa";
        viewModel.ContinueCreateCommand.Execute(null);

        Assert.Equal(0, applies);
        Assert.True(viewModel.ApplyCreateCommand.CanExecute(null));

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.Equal(1, applies);
        GroupSettingsData data = Assert.IsType<GroupSettingsProposal>(applied).GetData();
        Assert.True(data.Cluster.Enabled);
        Assert.Equal("Hala testowa", data.Cluster.Group!.GroupName);
        Assert.Equal(GroupFileCodec.ProfileHash(ReadyInput().CurrentProfile!), data.Cluster.Group.ProfileHash);
        Assert.Equal(32, Convert.FromBase64String(applied!.ExportInvitation().NetworkKey).Length);
        Assert.True(viewModel.IsCreateApplyCompleted);
        Assert.False(viewModel.IsCreateApplyError);
        Assert.Equal("Grupa utworzona i włączona", viewModel.CreateApplyStatusText);
        Assert.Equal("Włączona", viewModel.SavedSettingText);
        Assert.Equal("Oczekiwanie na drugi komputer", viewModel.RuntimeStateText);
        Assert.False(viewModel.ApplyCreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyBlocksDuplicateExecutionWhileOperationIsRunning()
    {
        int applies = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MultiComputerViewModel(() => ReadyInput(), async (_, _) =>
        {
            applies++;
            started.SetResult();
            await release.Task;
        });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);

        Task first = viewModel.ApplyCreateCommand.ExecuteAsync(null);
        await started.Task;
        Task second = viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCreateApplyRunning);
        Assert.False(viewModel.ApplyCreateCommand.CanExecute(null));
        Assert.Equal(1, applies);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task ApplyReportsUacDenialWithoutSuccessAndAllowsRetry()
    {
        int applies = 0;
        var viewModel = new MultiComputerViewModel(() => ReadyInput(), (_, _) =>
        {
            applies++;
            throw new GroupSettingsDeniedException(new InvalidOperationException("secret diagnostic"));
        });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.Equal(1, applies);
        Assert.False(viewModel.IsCreateApplyCompleted);
        Assert.True(viewModel.IsCreateApplyError);
        Assert.Equal("Nie utworzono grupy", viewModel.CreateApplyStatusText);
        Assert.Contains("Odmówiono uprawnień administratora", viewModel.CreateApplyDetail);
        Assert.DoesNotContain("secret diagnostic", viewModel.CreateApplyDetail);
        Assert.True(viewModel.ApplyCreateCommand.CanExecute(null));
    }

    [Fact]
    public void JoinReadsOneValidatedInvitationAndFreezesSafeSummaryWithoutApplyingAnything()
    {
        MultiComputerStatusInput input = ReadyInput();
        GroupProfile local = input.CurrentProfile!;
        AppConfig importedConfig = local.ApplyTo(new AppConfig());
        importedConfig.Machines[0] = importedConfig.Machines[0] with { Name = "Aidite 1 z grupy" };
        importedConfig.Machines.RemoveAt(1);
        importedConfig.Machines.Add(new MachineConfig
        {
            Id = "aidite3", Name = "Aidite 3", Host = "127.0.0.1", Profile = "opencnc",
        });
        GroupProfile imported = GroupProfile.Capture(importedConfig, local.Rules, local.Instructions);
        GroupFile invitation = GroupFile.Create("Hala importowana", imported);
        int reads = 0;
        int applies = 0;
        var viewModel = new MultiComputerViewModel(
            () => input,
            (_, _) => { applies++; return Task.CompletedTask; },
            () => { reads++; return invitation; });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);

        JoinGroupDraftProposal summary = Assert.IsType<JoinGroupDraftProposal>(viewModel.CurrentJoinProposal);
        Assert.Equal(1, reads);
        Assert.Equal(0, applies);
        Assert.True(viewModel.IsJoinSummaryStep);
        Assert.False(viewModel.IsJoinFileStep);
        Assert.Equal("Hala importowana", summary.GroupName);
        Assert.Equal(2, summary.ActiveMachineCount);
        Assert.Equal(2, summary.TotalMachineCount);
        Assert.Equal(2, summary.Machines.Count);
        Assert.True(summary.Difference.ProfileChanged);
        Assert.Equal(["aidite3"], summary.Difference.AddedMachines);
        Assert.Equal(["aidite2"], summary.Difference.RemovedMachines);
        Assert.Equal(["aidite1"], summary.Difference.ChangedMachines);
        Assert.Equal(["aidite3"], summary.Difference.EnabledMachines);
        Assert.Equal(["aidite2"], summary.Difference.DisabledMachines);
        Assert.Contains(summary.DifferenceRows, row => row.Label == "Dodane maszyny" && row.Value == "Aidite 3");

        invitation.Profile.Machines[0] = invitation.Profile.Machines[0] with { Name = "secret mutation" };
        Assert.Equal("Aidite 1 z grupy", summary.Machines[0].Name);
        Assert.DoesNotContain("secret mutation", summary.ToString());
        Assert.False(viewModel.IsJoinImportStatusVisible);
    }

    [Fact]
    public void JoinCancellationKeepsConfigurationAndRuntimeUntouched()
    {
        MultiComputerStatusInput input = ReadyInput() with
        {
            SavedEnabled = false,
            NetworkActive = false,
        };
        int reads = 0;
        int applies = 0;
        var viewModel = new MultiComputerViewModel(
            () => input,
            (_, _) => { applies++; return Task.CompletedTask; },
            () => { reads++; return null; });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);

        Assert.Equal(1, reads);
        Assert.Equal(0, applies);
        Assert.Null(viewModel.CurrentJoinProposal);
        Assert.True(viewModel.IsJoinFileStep);
        Assert.False(viewModel.IsJoinImportError);
        Assert.Contains("Nie wybrano", viewModel.JoinImportStatusText);
        Assert.Equal("Wyłączona", viewModel.SavedSettingText);
        Assert.Equal("Wyłączone", viewModel.RuntimeStateText);
    }

    [Fact]
    public void JoinParserFailureIsRedactedAndAllowsAnotherSelection()
    {
        const string secret = "networkKey=TOP-SECRET-CONTENT";
        int reads = 0;
        int applies = 0;
        var viewModel = new MultiComputerViewModel(
            () => ReadyInput(),
            (_, _) => { applies++; return Task.CompletedTask; },
            () =>
            {
                reads++;
                throw new InvalidDataException(secret);
            });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);

        Assert.Equal(1, reads);
        Assert.Equal(0, applies);
        Assert.Null(viewModel.CurrentJoinProposal);
        Assert.True(viewModel.IsJoinImportError);
        Assert.Contains("Nie udało się odczytać", viewModel.JoinImportStatusText);
        Assert.DoesNotContain(secret, viewModel.JoinImportStatusText);
        Assert.True(viewModel.SelectJoinGroupFileCommand.CanExecute(null));
    }

    [Fact]
    public void UseGroupConfigurationOnlyOpensLocalPreparationBuiltFromWatcherReadiness()
    {
        int reads = 0;
        int applies = 0;
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => { reads++; return input; },
            (_, _) => { applies++; return Task.CompletedTask; },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);

        Assert.True(viewModel.CanUseGroupConfiguration);
        Assert.True(viewModel.UseGroupConfigurationCommand.CanExecute(null));

        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.Equal(0, applies);
        Assert.Equal(1, reads);
        Assert.True(viewModel.IsJoinPreparationStep);
        Assert.False(viewModel.IsJoinSummaryStep);
        Assert.False(viewModel.IsJoinFileStep);
        Assert.NotNull(viewModel.CurrentJoinProposal);
        Assert.Equal("Ten komputer jest gotowy lokalnie", viewModel.JoinReadinessText);
        Assert.False(viewModel.IsJoinReadinessWarning);
        Assert.Empty(viewModel.JoinMissingPasswordMachines);
        Assert.Contains("mają już lokalne hasło", viewModel.JoinMissingPasswordsHint);
        Assert.Contains("gotowe (2/2)", viewModel.JoinPasswordsReadinessText);
        Assert.Contains("nigdy nie przenosi haseł", viewModel.JoinPasswordsReadinessText);
        Assert.Contains("dostępny", viewModel.JoinOcrReadinessText);
        Assert.Contains("zostaną sprawdzone w późniejszym kroku", viewModel.JoinReadinessDetail);
        Assert.Equal("Wyłączona", viewModel.SavedSettingText);
        Assert.Equal("Wyłączone", viewModel.RuntimeStateText);
    }

    [Fact]
    public void JoinPreparationNamesGroupMachinesWithoutLocalPasswordWithoutCopyingAnySecret()
    {
        var asked = new List<string>();
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: id =>
        {
            asked.Add(id);
            return id is "aidite1" or "aidite2";
        });
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.True(viewModel.IsJoinPreparationStep);
        Assert.Equal("Uzupełnij lokalne hasła VNC", viewModel.JoinReadinessText);
        Assert.True(viewModel.IsJoinReadinessWarning);
        MultiComputerMachineSummary missing = Assert.Single(viewModel.JoinMissingPasswordMachines);
        Assert.Equal(("Aidite 3", "opencnc"), (missing.Name, missing.Profile));
        Assert.Equal("", viewModel.JoinMissingPasswordsHint);
        Assert.Contains("brakuje 1 z 3", viewModel.JoinPasswordsReadinessText);
        Assert.Contains("plik grupy nie zawiera haseł", viewModel.JoinPasswordsReadinessText);
        Assert.Contains("aidite3", asked);
        Assert.DoesNotContain(invitation.NetworkKey, viewModel.JoinPasswordsReadinessText);
        Assert.DoesNotContain(invitation.NetworkKey, viewModel.JoinReadinessDetail);
        Assert.DoesNotContain(invitation.NetworkKey, viewModel.CurrentJoinProposal!.ToString());
    }

    [Fact]
    public void JoinPreparationKeepsMissingOcrAsWarningOnlyAndNeverAsBlockedRedAlarms()
    {
        MultiComputerStatusInput input = ReadyInput(ocrAvailable: false);
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.Equal("Gotowy do czerwonych alarmów — OCR ograniczony", viewModel.JoinReadinessText);
        Assert.True(viewModel.IsJoinReadinessWarning);
        Assert.Empty(viewModel.JoinMissingPasswordMachines);
        Assert.Contains("czerwone alarmy nadal działają", viewModel.JoinOcrReadinessText);
    }

    [Fact]
    public void JoinPreparationDoesNotReadTheSecretStoreWhenMonitoringIsGloballyDisabled()
    {
        int passwordChecks = 0;
        MultiComputerStatusInput input = ReadyInput(monitoringEnabled: false, canObserve: false,
            hasLocalVncPassword: _ => { passwordChecks++; return true; });
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.True(viewModel.IsJoinPreparationStep);
        Assert.Equal(0, passwordChecks);
        Assert.Equal("Włącz monitorowanie przed dołączeniem do grupy", viewModel.JoinReadinessText);
        Assert.Contains("nie są sprawdzane", viewModel.JoinPasswordsReadinessText);
        Assert.Contains("po włączeniu globalnego monitorowania", viewModel.JoinMissingPasswordsHint);
        Assert.Empty(viewModel.JoinMissingPasswordMachines);
    }

    [Fact]
    public void BackFromLocalPreparationDropsThePrivateInvitationProposalWithoutSavingAnything()
    {
        int applies = 0;
        int reads = 0;
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (_, _) => { applies++; return Task.CompletedTask; },
            () => { reads++; return invitation; });

        viewModel.SelectJoinGroupCommand.Execute(null);
        Assert.False(viewModel.CanUseGroupConfiguration);
        Assert.False(viewModel.UseGroupConfigurationCommand.CanExecute(null));

        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        Assert.NotEmpty(viewModel.JoinMissingPasswordMachines);

        viewModel.BackFromJoinPreparationCommand.Execute(null);

        Assert.Null(viewModel.CurrentJoinProposal);
        Assert.True(viewModel.IsJoinFileStep);
        Assert.False(viewModel.IsJoinPreparationStep);
        Assert.False(viewModel.IsJoinSummaryStep);
        Assert.False(viewModel.CanUseGroupConfiguration);
        Assert.Empty(viewModel.JoinMissingPasswordMachines);
        Assert.Equal("", viewModel.JoinMissingPasswordsHint);
        Assert.Equal("", viewModel.JoinReadinessText);
        Assert.Equal("", viewModel.JoinPasswordsReadinessText);
        Assert.False(viewModel.IsJoinImportError);
        Assert.Contains("Porzucono wczytane zaproszenie", viewModel.JoinImportStatusText);
        Assert.Equal(0, applies);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void JoinPreparationOffersOnePasswordFieldPerInvitedMachineWithoutALocalPassword()
    {
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: id => id is "aidite1");
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation, (_, _) => { });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);

        Assert.Empty(viewModel.JoinPasswordEntries);
        Assert.False(viewModel.IsJoinPasswordEntryVisible);

        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.True(viewModel.IsJoinPasswordEntryVisible);
        Assert.Equal(["aidite2", "aidite3"], viewModel.JoinPasswordEntries.Select(entry => entry.MachineId));
        Assert.Equal(["Aidite 2", "Aidite 3"], viewModel.JoinPasswordEntries.Select(entry => entry.Name));
        Assert.All(viewModel.JoinPasswordEntries, entry => Assert.False(entry.HasPassword));
        Assert.False(viewModel.UseSharedJoinPassword);
        Assert.True(viewModel.IsJoinPerMachinePasswordMode);
        Assert.Contains("do uzupełnienia: 2", viewModel.JoinPasswordEntryHint);
        Assert.Contains("nie są nadpisywane", viewModel.JoinPasswordEntryHint);
        Assert.False(viewModel.IsJoinPasswordSaveStatusVisible);
        Assert.False(viewModel.CanSaveJoinPasswords);
        Assert.False(viewModel.SaveJoinPasswordsCommand.CanExecute(null));
    }

    [Fact]
    public void SavingTypedPasswordsWritesOnlyTheLocalSecretStoreAndRecomputesTheSameReadiness()
    {
        var store = new Dictionary<string, string>(StringComparer.Ordinal) { ["aidite1"] = "stare" };
        int reads = 0;
        int applies = 0;
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: store.ContainsKey);
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(
            () => { reads++; return input; },
            (_, _) => { applies++; return Task.CompletedTask; },
            () => invitation,
            (machineId, password) => store[machineId] = password);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.Contains("brakuje 2 z 3", viewModel.JoinPasswordsReadinessText);

        viewModel.SetJoinPassword("aidite2", "haslo-2");

        Assert.True(viewModel.CanSaveJoinPasswords);
        Assert.True(viewModel.SaveJoinPasswordsCommand.CanExecute(null));

        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aidite1"] = "stare",
            ["aidite2"] = "haslo-2",
        }, store);
        Assert.Equal(0, applies);
        Assert.Equal(1, reads);
        Assert.True(viewModel.IsJoinPreparationStep);
        Assert.Equal("Uzupełnij lokalne hasła VNC", viewModel.JoinReadinessText);
        Assert.Contains("brakuje 1 z 3", viewModel.JoinPasswordsReadinessText);
        MultiComputerMachineSummary stillMissing = Assert.Single(viewModel.JoinMissingPasswordMachines);
        Assert.Equal("Aidite 3", stillMissing.Name);
        JoinPasswordEntry remaining = Assert.Single(viewModel.JoinPasswordEntries);
        Assert.Equal("aidite3", remaining.MachineId);
        Assert.False(remaining.HasPassword);
        Assert.False(viewModel.IsJoinPasswordSaveError);
        Assert.Contains("Zapisano lokalnie hasła VNC (1)", viewModel.JoinPasswordSaveStatusText);
        Assert.Contains("pozostają bez zmian", viewModel.JoinPasswordSaveStatusText);
        Assert.False(viewModel.CanSaveJoinPasswords);
        Assert.Equal("Wyłączona", viewModel.SavedSettingText);
        Assert.Equal("Wyłączone", viewModel.RuntimeStateText);
    }

    [Fact]
    public void OneSharedPasswordFillsEveryMissingMachineWithoutOverwritingTheExistingOnes()
    {
        var store = new Dictionary<string, string>(StringComparer.Ordinal) { ["aidite1"] = "stare" };
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: store.ContainsKey);
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation,
            (machineId, password) => store[machineId] = password);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        viewModel.UseSharedJoinPassword = true;

        Assert.True(viewModel.IsJoinSharedPasswordMode);
        Assert.False(viewModel.IsJoinPerMachinePasswordMode);
        Assert.False(viewModel.CanSaveJoinPasswords);

        viewModel.SetSharedJoinPassword("wspolne");

        Assert.True(viewModel.CanSaveJoinPasswords);

        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aidite1"] = "stare",
            ["aidite2"] = "wspolne",
            ["aidite3"] = "wspolne",
        }, store);
        Assert.Equal("Ten komputer jest gotowy lokalnie", viewModel.JoinReadinessText);
        Assert.False(viewModel.IsJoinReadinessWarning);
        Assert.Empty(viewModel.JoinMissingPasswordMachines);
        Assert.Empty(viewModel.JoinPasswordEntries);
        Assert.False(viewModel.IsJoinPasswordEntryVisible);
        Assert.Equal("", viewModel.JoinPasswordEntryHint);
        Assert.Contains("gotowe (3/3)", viewModel.JoinPasswordsReadinessText);
        Assert.Contains("Zapisano lokalnie hasła VNC (2)", viewModel.JoinPasswordSaveStatusText);
        Assert.False(viewModel.CanSaveJoinPasswords);
    }

    [Fact]
    public void SharedPasswordIsDroppedFromMemoryAfterSavingSoASecondSaveNeedsItAgain()
    {
        var writes = new List<string>();
        var store = new Dictionary<string, string>(StringComparer.Ordinal) { ["aidite1"] = "stare" };
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: store.ContainsKey);
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation,
            (machineId, _) => { writes.Add(machineId); store[machineId] = "zapisane"; });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.UseSharedJoinPassword = true;
        viewModel.SetSharedJoinPassword("wspolne");
        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(["aidite2", "aidite3"], writes);

        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(["aidite2", "aidite3"], writes);
    }

    [Fact]
    public void FailedSecretWriteIsReportedAsACountWithoutLeakingTheTypedPassword()
    {
        const string secret = "TOP-SECRET-VNC";
        var store = new Dictionary<string, string>(StringComparer.Ordinal) { ["aidite1"] = "stare" };
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: store.ContainsKey);
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation,
            (machineId, password) =>
            {
                if (machineId == "aidite3")
                    throw new IOException($"magazyn zajęty: {password}");
                store[machineId] = password;
            });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.UseSharedJoinPassword = true;
        viewModel.SetSharedJoinPassword(secret);
        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.True(viewModel.IsJoinPasswordSaveError);
        Assert.Contains("Zapisano 1 z 2", viewModel.JoinPasswordSaveStatusText);
        Assert.DoesNotContain(secret, viewModel.JoinPasswordSaveStatusText);
        Assert.DoesNotContain(secret, viewModel.JoinPasswordEntryHint);
        Assert.DoesNotContain(secret, viewModel.JoinPasswordsReadinessText);
        Assert.Equal("aidite3", Assert.Single(viewModel.JoinPasswordEntries).MachineId);
        Assert.Equal("Uzupełnij lokalne hasła VNC", viewModel.JoinReadinessText);
        Assert.Contains("aidite2", store.Keys);
    }

    [Fact]
    public void GloballyDisabledMonitoringOffersNoPasswordFieldsAndCannotSaveAnySecret()
    {
        int writes = 0;
        MultiComputerStatusInput input = ReadyInput(monitoringEnabled: false, canObserve: false,
            hasLocalVncPassword: _ => false);
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation, (_, _) => writes++);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.SetSharedJoinPassword("wspolne");
        viewModel.UseSharedJoinPassword = true;
        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Empty(viewModel.JoinPasswordEntries);
        Assert.False(viewModel.IsJoinPasswordEntryVisible);
        Assert.False(viewModel.CanSaveJoinPasswords);
        Assert.Equal(0, writes);
        Assert.Equal("", viewModel.JoinPasswordSaveStatusText);
        Assert.Equal("Włącz monitorowanie przed dołączeniem do grupy", viewModel.JoinReadinessText);
    }

    [Fact]
    public void LeavingLocalPreparationDropsTypedPasswordsWithoutWritingAnySecret()
    {
        int writes = 0;
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: id => id is "aidite1" or "aidite2");
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation, (_, _) => writes++);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.SetJoinPassword("aidite3", "haslo-3");
        Assert.True(viewModel.CanSaveJoinPasswords);

        viewModel.BackFromJoinPreparationCommand.Execute(null);

        Assert.Equal(0, writes);
        Assert.Empty(viewModel.JoinPasswordEntries);
        Assert.False(viewModel.CanSaveJoinPasswords);
        Assert.Equal("", viewModel.JoinPasswordEntryHint);
        Assert.Equal("", viewModel.JoinPasswordSaveStatusText);

        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.All(viewModel.JoinPasswordEntries, entry => Assert.False(entry.HasPassword));
        Assert.False(viewModel.CanSaveJoinPasswords);

        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(0, writes);
    }

    [Fact]
    public void PasswordFieldsAreUnavailableWithoutALocalSecretWriter()
    {
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: id => id is "aidite1" or "aidite2");
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.SetJoinPassword("aidite3", "haslo-3");

        Assert.NotEmpty(viewModel.JoinPasswordEntries);
        Assert.False(viewModel.CanSaveJoinPasswords);
        Assert.False(viewModel.SaveJoinPasswordsCommand.CanExecute(null));
    }

    [Fact]
    public void TypedPasswordIsNeverExposedByTheEntryRowOrTheProposal()
    {
        const string secret = "TOP-SECRET-VNC";
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: id => id is "aidite1" or "aidite2");
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation, (_, _) => { });

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.SetJoinPassword("aidite3", secret);

        JoinPasswordEntry entry = Assert.Single(viewModel.JoinPasswordEntries);
        Assert.True(entry.HasPassword);
        Assert.DoesNotContain(secret, entry.ToString());
        Assert.DoesNotContain(secret, viewModel.CurrentJoinProposal!.ToString());
        Assert.DoesNotContain(secret, string.Join('|', typeof(JoinPasswordEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetValue(entry)?.ToString())));
        Assert.DoesNotContain(invitation.NetworkKey, viewModel.JoinPasswordEntryHint);
    }

    [Fact]
    public void UnknownMachineIdFromTheViewNeverCreatesAPasswordToSave()
    {
        int writes = 0;
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: id => id is "aidite1" or "aidite2");
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(() => input, null, () => invitation, (_, _) => writes++);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        viewModel.SetJoinPassword("nie-ma-takiej", "haslo");

        Assert.False(viewModel.CanSaveJoinPasswords);

        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(0, writes);
        Assert.Equal("", viewModel.JoinPasswordSaveStatusText);
    }

    [Fact]
    public async Task JoinAppliesTheFrozenInvitationOnlyAfterExplicitClickAndRefreshesSavedAndRuntimeState()
    {
        int applies = 0;
        GroupSettingsProposal? applied = null;
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (proposal, _) =>
            {
                applies++;
                applied = proposal;
                input = ReadyInput() with
                {
                    SavedEnabled = true,
                    SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                    NetworkActive = true,
                    VisibleComputers = 1,
                };
                return Task.CompletedTask;
            },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);

        Assert.False(viewModel.CanApplyJoin);

        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.Equal(0, applies);
        Assert.True(viewModel.ApplyJoinCommand.CanExecute(null));
        Assert.Contains("Dołącz i włącz", viewModel.JoinApplyHint);
        Assert.False(viewModel.IsJoinApplyStatusVisible);

        await viewModel.ApplyJoinCommand.ExecuteAsync(null);

        Assert.Equal(1, applies);
        GroupSettingsData data = Assert.IsType<GroupSettingsProposal>(applied).GetData();
        Assert.True(data.Cluster.Enabled);
        Assert.Equal("Hala importowana", data.Cluster.Group!.GroupName);
        Assert.Equal(invitation.NetworkKey, applied!.ExportInvitation().NetworkKey);
        Assert.True(viewModel.IsJoinApplyCompleted);
        Assert.False(viewModel.IsJoinApplyError);
        Assert.Equal("Dołączono do grupy i włączono", viewModel.JoinApplyStatusText);
        Assert.True(viewModel.IsJoinApplyStatusVisible);
        Assert.Equal("Włączona", viewModel.SavedSettingText);
        Assert.Equal("Grupa: Hala importowana", viewModel.SavedGroupText);
        Assert.Equal("Oczekiwanie na drugi komputer", viewModel.RuntimeStateText);
        Assert.False(viewModel.ApplyJoinCommand.CanExecute(null));
        Assert.DoesNotContain(invitation.NetworkKey, viewModel.JoinApplyDetail);
        Assert.DoesNotContain(invitation.NetworkKey, viewModel.JoinApplyHint);
    }

    [Fact]
    public async Task JoinApplyBlocksDuplicateExecutionAndLeavingTheStepWhileOperationIsRunning()
    {
        int applies = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            async (_, _) =>
            {
                applies++;
                started.SetResult();
                await release.Task;
            },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Task first = viewModel.ApplyJoinCommand.ExecuteAsync(null);
        await started.Task;
        Task second = viewModel.ApplyJoinCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsJoinApplyRunning);
        Assert.False(viewModel.ApplyJoinCommand.CanExecute(null));
        Assert.False(viewModel.BackFromJoinPreparationCommand.CanExecute(null));
        Assert.Equal(1, applies);

        // Neither Back nor switching the wizard path may drop the proposal while the transaction is in flight.
        viewModel.BackFromJoinPreparationCommand.Execute(null);
        viewModel.SelectCreateGroupCommand.Execute(null);

        Assert.True(viewModel.IsJoinPreparationStep);
        Assert.NotNull(viewModel.CurrentJoinProposal);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, applies);
        Assert.True(viewModel.BackFromJoinPreparationCommand.CanExecute(null));
    }

    [Fact]
    public async Task JoinApplyReportsUacDenialWithoutSuccessAndAllowsRetry()
    {
        int applies = 0;
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (_, _) =>
            {
                applies++;
                throw new GroupSettingsDeniedException(new InvalidOperationException("secret diagnostic"));
            },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        await viewModel.ApplyJoinCommand.ExecuteAsync(null);

        Assert.Equal(1, applies);
        Assert.False(viewModel.IsJoinApplyCompleted);
        Assert.True(viewModel.IsJoinApplyError);
        Assert.Equal("Nie dołączono do grupy", viewModel.JoinApplyStatusText);
        Assert.Contains("Odmówiono uprawnień administratora", viewModel.JoinApplyDetail);
        Assert.DoesNotContain("secret diagnostic", viewModel.JoinApplyDetail);
        Assert.Equal("Wyłączona", viewModel.SavedSettingText);
        Assert.Equal("Wyłączone", viewModel.RuntimeStateText);
        Assert.True(viewModel.ApplyJoinCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task JoinApplyStaysBlockedUntilLocalPreparationIsComplete(
        bool monitoringEnabled, bool passwordsKnown)
    {
        int applies = 0;
        MultiComputerStatusInput input = ReadyInput(
            monitoringEnabled: monitoringEnabled,
            hasLocalVncPassword: passwordsKnown ? id => id is "aidite1" or "aidite2" : _ => false);
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (_, _) => { applies++; return Task.CompletedTask; },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.False(viewModel.CanApplyJoin);
        Assert.False(viewModel.ApplyJoinCommand.CanExecute(null));
        Assert.Contains("odblokuje się", viewModel.JoinApplyHint);

        await viewModel.ApplyJoinCommand.ExecuteAsync(null);

        Assert.Equal(0, applies);
        Assert.False(viewModel.IsJoinApplyStatusVisible);
        Assert.Equal("Wyłączona", viewModel.SavedSettingText);
    }

    [Fact]
    public void SavingTheMissingPasswordUnlocksTheExplicitJoinWithoutApplyingAnything()
    {
        int applies = 0;
        var stored = new HashSet<string>(StringComparer.Ordinal) { "aidite1", "aidite2" };
        MultiComputerStatusInput input = ReadyInput(hasLocalVncPassword: stored.Contains);
        GroupFile invitation = InvitationOf(input.CurrentProfile!, extraMachine: true);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (_, _) => { applies++; return Task.CompletedTask; },
            () => invitation,
            (machineId, _) => stored.Add(machineId));

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.False(viewModel.CanApplyJoin);

        viewModel.SetJoinPassword("aidite3", "haslo-lokalne");
        viewModel.SaveJoinPasswordsCommand.Execute(null);

        Assert.Equal(0, applies);
        Assert.Empty(viewModel.JoinPasswordEntries);
        Assert.Equal("Ten komputer jest gotowy lokalnie", viewModel.JoinReadinessText);
        Assert.True(viewModel.CanApplyJoin);
        Assert.True(viewModel.ApplyJoinCommand.CanExecute(null));
    }

    [Fact]
    public async Task LeavingLocalPreparationAfterJoiningDropsTheInvitationAndTheApplyStatus()
    {
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input, (_, _) => Task.CompletedTask, () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        await viewModel.ApplyJoinCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsJoinApplyCompleted);
        Assert.False(viewModel.CanSaveJoinPasswords);

        viewModel.BackFromJoinPreparationCommand.Execute(null);

        Assert.Null(viewModel.CurrentJoinProposal);
        Assert.True(viewModel.IsJoinFileStep);
        Assert.False(viewModel.IsJoinApplyCompleted);
        Assert.False(viewModel.IsJoinApplyStatusVisible);
        Assert.Equal("", viewModel.JoinApplyDetail);
        Assert.False(viewModel.CanApplyJoin);
    }

    [Fact]
    public async Task NetworkDiagnosticsAreOfferedOnlyAfterJoiningAndOnlyReadTheRuntime()
    {
        int applies = 0;
        int reads = 0;
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => { reads++; return input; },
            (proposal, _) =>
            {
                applies++;
                input = ReadyInput() with
                {
                    SavedEnabled = true,
                    SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                    NetworkActive = true,
                    VisibleComputers = 1,
                };
                return Task.CompletedTask;
            },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);

        Assert.False(viewModel.CanCheckGroupNetwork);
        Assert.False(viewModel.CheckGroupNetworkCommand.CanExecute(null));
        Assert.False(viewModel.IsGroupDiagnosticsResultVisible);

        await viewModel.ApplyJoinCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanCheckGroupNetwork);
        Assert.Contains("Sprawdź sieć grupy", viewModel.JoinApplyHint);
        Assert.False(viewModel.IsJoinDiagnosticsStep);

        int readsBeforeCheck = reads;
        viewModel.CheckGroupNetworkCommand.Execute(null);

        Assert.Equal(1, applies);
        Assert.True(reads > readsBeforeCheck);
        Assert.True(viewModel.IsJoinDiagnosticsStep);
        Assert.False(viewModel.IsJoinPreparationStep);
        Assert.False(viewModel.CanApplyJoin);
        Assert.False(viewModel.CanSaveJoinPasswords);
        Assert.True(viewModel.IsGroupDiagnosticsResultVisible);
        Assert.Contains("tylko odczytuje", viewModel.GroupDiagnosticsScopeText);
        Assert.Contains("nie zmienia zapory", viewModel.GroupDiagnosticsScopeText);
    }

    [Fact]
    public async Task NetworkDiagnosticsSeparateTheSavedSettingFromWaitingForTheSecondComputer()
    {
        MultiComputerViewModel viewModel = await JoinedViewModelAsync(
            runtime => runtime with { NetworkActive = true, VisibleComputers = 1 });

        viewModel.CheckGroupNetworkCommand.Execute(null);

        GroupDiagnosticsRow saved = RowOf(viewModel, "Zapisane ustawienie grupy");
        Assert.Equal(GroupDiagnosticsSeverity.Ok, saved.Severity);
        Assert.Contains("Hala importowana", saved.Result);
        Assert.Contains("wyłącznie zapis w ustawieniach", saved.Detail);

        GroupDiagnosticsRow transport = RowOf(viewModel, "Transport UDP grupy (port 47777)");
        Assert.Equal(GroupDiagnosticsSeverity.Ok, transport.Severity);
        Assert.Contains("nie dowodzi", transport.Detail);

        GroupDiagnosticsRow visible = RowOf(viewModel, "Widoczność drugiego komputera");
        Assert.Equal(GroupDiagnosticsSeverity.Waiting, visible.Severity);
        Assert.Equal("Tylko ten komputer", visible.Result);
        Assert.Contains("zapora blokuje UDP 47777", visible.Detail);

        Assert.True(viewModel.IsGroupDiagnosticsWarning);
        Assert.Contains("grupa czeka", viewModel.GroupDiagnosticsSummaryText);
        Assert.Contains("Uruchom drugi komputer", viewModel.GroupDiagnosticsSummaryDetail);
    }

    [Fact]
    public async Task NetworkDiagnosticsReportAProblemWhenTheTransportDoesNotRunDespiteTheSavedSetting()
    {
        MultiComputerViewModel viewModel = await JoinedViewModelAsync(
            runtime => runtime with { NetworkActive = false, VisibleComputers = 0 });

        viewModel.CheckGroupNetworkCommand.Execute(null);

        GroupDiagnosticsRow transport = RowOf(viewModel, "Transport UDP grupy (port 47777)");
        Assert.Equal(GroupDiagnosticsSeverity.Problem, transport.Severity);
        Assert.Equal("Nie działa", transport.Result);

        foreach (string label in new[] { "Widoczność drugiego komputera", "Świeżość wspólnego stanu", "Rola w grupie" })
            Assert.Equal(GroupDiagnosticsSeverity.NotChecked, RowOf(viewModel, label).Severity);

        Assert.True(viewModel.IsGroupDiagnosticsWarning);
        Assert.Contains("nie działa poprawnie", viewModel.GroupDiagnosticsSummaryText);
        Assert.Contains("pozostają bez zmian", viewModel.GroupDiagnosticsSummaryDetail);
    }

    [Fact]
    public async Task NetworkDiagnosticsNeverPromiseShopFloorAcceptanceEvenWhenEverythingWorks()
    {
        MultiComputerViewModel viewModel = await JoinedViewModelAsync(runtime => runtime with
        {
            NetworkActive = true,
            VisibleComputers = 2,
            IsWatcher = false,
            NoWatcher = false,
            StateFresh = true,
            WatcherHost = "HALA-PC1",
        });

        viewModel.CheckGroupNetworkCommand.Execute(null);

        Assert.False(viewModel.IsGroupDiagnosticsWarning);
        Assert.Equal("Grupa działa na tym komputerze", viewModel.GroupDiagnosticsSummaryText);
        Assert.Contains("nie jest odbiór na hali", viewModel.GroupDiagnosticsSummaryDetail);
        Assert.Equal("Widoczne komputery: 2", RowOf(viewModel, "Widoczność drugiego komputera").Result);
        Assert.Contains("HALA-PC1", RowOf(viewModel, "Rola w grupie").Result);
        Assert.Equal(GroupDiagnosticsSeverity.Ok, RowOf(viewModel, "Świeżość wspólnego stanu").Severity);

        GroupDiagnosticsRow outOfScope = RowOf(viewModel, "Zapora, sieć i odbiór na hali");
        Assert.Equal(GroupDiagnosticsSeverity.NotChecked, outOfScope.Severity);
        Assert.Equal("Nie sprawdzono", outOfScope.Result);
        Assert.Contains("dwóch fizycznych komputerach", outOfScope.Detail);
    }

    [Fact]
    public async Task RerunningNetworkDiagnosticsPicksUpTheNewRuntimeWithoutApplyingAnything()
    {
        int applies = 0;
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (proposal, _) =>
            {
                applies++;
                input = ReadyInput() with
                {
                    SavedEnabled = true,
                    SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                    NetworkActive = true,
                    VisibleComputers = 1,
                };
                return Task.CompletedTask;
            },
            () => invitation,
            now: () => new DateTimeOffset(2026, 9, 15, 13, 45, 7, TimeSpan.Zero));

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        await viewModel.ApplyJoinCommand.ExecuteAsync(null);
        viewModel.CheckGroupNetworkCommand.Execute(null);

        Assert.Equal(GroupDiagnosticsSeverity.Waiting, RowOf(viewModel, "Widoczność drugiego komputera").Severity);
        Assert.Contains("Sprawdzono: 13:45:07", viewModel.GroupDiagnosticsCheckedAtText);
        Assert.Contains("sprawdź ponownie", viewModel.GroupDiagnosticsCheckedAtText);

        input = input with { VisibleComputers = 2, IsWatcher = true };
        viewModel.RunGroupDiagnosticsCommand.Execute(null);

        Assert.Equal(1, applies);
        Assert.Equal("Widoczne komputery: 2", RowOf(viewModel, "Widoczność drugiego komputera").Result);
        Assert.Equal("Połączono", viewModel.RuntimeStateText);
        Assert.False(viewModel.IsGroupDiagnosticsWarning);
    }

    [Fact]
    public async Task LeavingTheNetworkStepReturnsToPreparationAndLeavingTheJoinPathDropsTheSnapshot()
    {
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input, (_, _) => Task.CompletedTask, () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        await viewModel.ApplyJoinCommand.ExecuteAsync(null);
        viewModel.CheckGroupNetworkCommand.Execute(null);

        Assert.True(viewModel.IsJoinDiagnosticsStep);

        viewModel.BackFromGroupDiagnosticsCommand.Execute(null);

        Assert.True(viewModel.IsJoinPreparationStep);
        Assert.False(viewModel.IsJoinDiagnosticsStep);
        Assert.True(viewModel.IsJoinApplyCompleted);
        Assert.True(viewModel.IsGroupDiagnosticsResultVisible);

        viewModel.BackFromJoinPreparationCommand.Execute(null);

        Assert.True(viewModel.IsJoinFileStep);
        Assert.False(viewModel.IsGroupDiagnosticsResultVisible);
        Assert.Empty(viewModel.GroupDiagnosticsRows);
        Assert.Equal("", viewModel.GroupDiagnosticsSummaryText);
        Assert.Equal("", viewModel.GroupDiagnosticsCheckedAtText);
        Assert.False(viewModel.CanCheckGroupNetwork);
        Assert.False(viewModel.CanRunGroupDiagnostics);
    }

    [Fact]
    public async Task NetworkDiagnosticsNeverQuoteTheInvitationSecret()
    {
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (proposal, _) =>
            {
                input = ReadyInput() with
                {
                    SavedEnabled = true,
                    SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                    NetworkActive = true,
                    VisibleComputers = 2,
                };
                return Task.CompletedTask;
            },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        await viewModel.ApplyJoinCommand.ExecuteAsync(null);
        viewModel.CheckGroupNetworkCommand.Execute(null);

        string everything = string.Join("\n", viewModel.GroupDiagnosticsRows
            .Select(row => $"{row.Label} {row.Result} {row.Detail}")
            .Append(viewModel.GroupDiagnosticsSummaryText)
            .Append(viewModel.GroupDiagnosticsSummaryDetail)
            .Append(viewModel.GroupDiagnosticsScopeText)
            .Append(viewModel.GroupDiagnosticsCheckedAtText));

        Assert.DoesNotContain(invitation.NetworkKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(invitation.Identity.GroupId.ToString(), everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatedGroupNetworkCheckIsAvailableOnlyAfterASuccessfulCreateAndEnable()
    {
        MultiComputerStatusInput input = ReadyInput();
        var viewModel = new MultiComputerViewModel(() => input, (proposal, _) =>
        {
            input = ReadyInput() with
            {
                SavedEnabled = true,
                SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                NetworkActive = true,
                VisibleComputers = 1,
            };
            return Task.CompletedTask;
        });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = "Hala testowa";
        viewModel.ContinueCreateCommand.Execute(null);

        Assert.False(viewModel.CanCheckCreatedGroupNetwork);
        Assert.False(viewModel.CheckCreatedGroupNetworkCommand.CanExecute(null));

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanCheckCreatedGroupNetwork);
        Assert.True(viewModel.CheckCreatedGroupNetworkCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeniedCreateApplyNeverOffersTheCreatedGroupNetworkCheck()
    {
        var viewModel = new MultiComputerViewModel(() => ReadyInput(), (_, _) =>
            throw new GroupSettingsDeniedException(new InvalidOperationException("secret diagnostic")));
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCreateApplyError);
        Assert.False(viewModel.IsCreateApplyCompleted);
        Assert.False(viewModel.CanCheckCreatedGroupNetwork);
        Assert.False(viewModel.CheckCreatedGroupNetworkCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpeningTheNetworkCheckFromTheCreatePanelHidesTheConfirmationPanelUnderneath()
    {
        MultiComputerViewModel viewModel = await CreatedViewModelAsync();

        viewModel.CheckCreatedGroupNetworkCommand.Execute(null);

        Assert.True(viewModel.IsCreateDiagnosticsStep);
        Assert.False(viewModel.IsJoinDiagnosticsStep);
        Assert.True(viewModel.IsGroupDiagnosticsStep);
        Assert.False(viewModel.IsCreateConfirmationStep);
        Assert.False(viewModel.IsGroupFileExportVisible);
        Assert.False(viewModel.CanApplyCreate);
        Assert.False(viewModel.CanExportGroupFile);
    }

    [Fact]
    public async Task CreatedGroupNetworkCheckAppliesNothingAndReportsTheSameRowsAsTheJoinPath()
    {
        int applies = 0;
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(
            applies: () => applies++, groupName: "Hala testowa");

        viewModel.CheckCreatedGroupNetworkCommand.Execute(null);

        Assert.Equal(1, applies);
        GroupDiagnosticsRow saved = RowOf(viewModel, "Zapisane ustawienie grupy");
        Assert.Equal(GroupDiagnosticsSeverity.Ok, saved.Severity);
        Assert.Contains("Hala testowa", saved.Result);
        GroupDiagnosticsRow visible = RowOf(viewModel, "Widoczność drugiego komputera");
        Assert.Equal(GroupDiagnosticsSeverity.Waiting, visible.Severity);
        GroupDiagnosticsRow outOfScope = RowOf(viewModel, "Zapora, sieć i odbiór na hali");
        Assert.Equal(GroupDiagnosticsSeverity.NotChecked, outOfScope.Severity);

        viewModel.RunGroupDiagnosticsCommand.Execute(null);

        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task BackFromCreatedGroupNetworkCheckRestoresTheConfirmationPanelAndKeepsTheResult()
    {
        MultiComputerViewModel viewModel = await CreatedViewModelAsync();
        viewModel.CheckCreatedGroupNetworkCommand.Execute(null);

        viewModel.BackFromGroupDiagnosticsCommand.Execute(null);

        Assert.True(viewModel.IsCreateConfirmationStep);
        Assert.False(viewModel.IsCreateDiagnosticsStep);
        Assert.False(viewModel.IsGroupDiagnosticsStep);
        Assert.True(viewModel.IsGroupFileExportVisible);
        Assert.True(viewModel.CanExportGroupFile);
        Assert.True(viewModel.IsCreateApplyCompleted);
        Assert.True(viewModel.IsGroupDiagnosticsResultVisible);
    }

    [Fact]
    public async Task InvitationSurvivesTheCreatedGroupNetworkCheckSoExportStillWritesTheSameGroup()
    {
        GroupSettingsProposal? applied = null;
        var exports = new List<RecordedExport>();
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(
            exports, applied: proposal => applied = proposal, groupName: "Hala testowa");

        viewModel.CheckCreatedGroupNetworkCommand.Execute(null);
        viewModel.BackFromGroupDiagnosticsCommand.Execute(null);
        viewModel.ExportGroupFileCommand.Execute(null);

        GroupFile written = GroupFileCodec.Parse(Assert.Single(exports).Stream.ToArray());
        GroupFile enabled = Assert.IsType<GroupSettingsProposal>(applied).ExportInvitation();
        Assert.Equal(enabled.Identity.GroupId, written.Identity.GroupId);
        Assert.Equal(enabled.Identity.KeyId, written.Identity.KeyId);
        Assert.Equal(enabled.NetworkKey, written.NetworkKey);
    }

    [Fact]
    public async Task LeavingTheCreateStepWhileTheNetworkCheckIsOpenClosesItAndDropsItsSnapshot()
    {
        MultiComputerViewModel viewModel = await CreatedViewModelAsync();
        viewModel.CheckCreatedGroupNetworkCommand.Execute(null);
        Assert.True(viewModel.IsGroupDiagnosticsStep);

        viewModel.BackToCreateDraftCommand.Execute(null);

        Assert.False(viewModel.IsGroupDiagnosticsStep);
        Assert.Empty(viewModel.GroupDiagnosticsRows);
        Assert.Equal("", viewModel.GroupDiagnosticsSummaryText);
        Assert.Equal("", viewModel.GroupDiagnosticsCheckedAtText);
        Assert.False(viewModel.CanRunGroupDiagnostics);
        Assert.False(viewModel.CanCheckCreatedGroupNetwork);
    }

    [Fact]
    public async Task CreatedGroupNetworkCheckNeverQuotesTheInvitationSecret()
    {
        GroupSettingsProposal? applied = null;
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(applied: proposal => applied = proposal);

        viewModel.CheckCreatedGroupNetworkCommand.Execute(null);

        GroupFile invitation = Assert.IsType<GroupSettingsProposal>(applied).ExportInvitation();
        string everything = string.Join("\n", viewModel.GroupDiagnosticsRows
            .Select(row => $"{row.Label} {row.Result} {row.Detail}")
            .Append(viewModel.GroupDiagnosticsSummaryText)
            .Append(viewModel.GroupDiagnosticsSummaryDetail)
            .Append(viewModel.GroupDiagnosticsScopeText)
            .Append(viewModel.GroupDiagnosticsCheckedAtText));

        Assert.DoesNotContain(invitation.NetworkKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(invitation.Identity.GroupId.ToString(), everything, StringComparison.Ordinal);
        Assert.DoesNotContain(invitation.Identity.KeyId.ToString(), everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateNetworkCheckHintAppearsOnlyAfterApplyAndExplainsItIsReadOnly()
    {
        MultiComputerStatusInput input = ReadyInput();
        var viewModel = new MultiComputerViewModel(() => input, (proposal, _) =>
        {
            input = ReadyInput() with
            {
                SavedEnabled = true,
                SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                NetworkActive = true,
                VisibleComputers = 1,
            };
            return Task.CompletedTask;
        });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);

        Assert.Equal("", viewModel.CreateNetworkCheckHint);
        Assert.False(viewModel.IsCreateNetworkCheckHintVisible);

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCreateNetworkCheckHintVisible);
        Assert.Contains("Nie tworzy", viewModel.CreateNetworkCheckHint);
        Assert.Contains("po raz drugi", viewModel.CreateNetworkCheckHint);
        Assert.Contains("nie zmienia ustawień ani zapory", viewModel.CreateNetworkCheckHint);
    }

    [Fact]
    public async Task CreateAndJoinNetworkChecksDoNotMixTheirDiagnosticsOriginFlags()
    {
        MultiComputerViewModel created = await CreatedViewModelAsync();
        created.CheckCreatedGroupNetworkCommand.Execute(null);
        Assert.True(created.IsCreateDiagnosticsStep);
        Assert.False(created.IsJoinDiagnosticsStep);

        MultiComputerViewModel joined = await JoinedViewModelAsync(runtime => runtime);
        joined.CheckGroupNetworkCommand.Execute(null);
        Assert.True(joined.IsJoinDiagnosticsStep);
        Assert.False(joined.IsCreateDiagnosticsStep);
    }

    [Fact]
    public async Task GroupFileIsExportedOnlyAfterTheGroupWasReallyCreatedAndEnabled()
    {
        var exports = new List<RecordedExport>();
        MultiComputerStatusInput input = ReadyInput();
        var viewModel = new MultiComputerViewModel(() => input, (proposal, _) =>
        {
            input = ReadyInput() with
            {
                SavedEnabled = true,
                SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                NetworkActive = true,
                VisibleComputers = 1,
            };
            return Task.CompletedTask;
        }, selectGroupFileExport: name => Record(exports, name));

        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = "Hala testowa";
        Assert.False(viewModel.IsGroupFileExportVisible);
        Assert.False(viewModel.ExportGroupFileCommand.CanExecute(null));

        viewModel.ContinueCreateCommand.Execute(null);

        Assert.False(viewModel.IsGroupFileExportVisible);
        Assert.False(viewModel.ExportGroupFileCommand.CanExecute(null));

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsGroupFileExportVisible);
        Assert.True(viewModel.ExportGroupFileCommand.CanExecute(null));
        Assert.Empty(exports);
    }

    [Fact]
    public async Task ExportWritesExactlyOneReadableInvitationPerClickWithoutApplyingAnythingAgain()
    {
        int applies = 0;
        var exports = new List<RecordedExport>();
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(
            exports, () => applies++, groupName: "Hala testowa");

        viewModel.ExportGroupFileCommand.Execute(null);

        RecordedExport export = Assert.Single(exports);
        Assert.Equal("Hala testowa.vncgroup", export.SuggestedName);
        GroupFile written = GroupFileCodec.Parse(export.Stream.ToArray());
        Assert.Equal("Hala testowa", written.Identity.GroupName);
        Assert.Equal(32, Convert.FromBase64String(written.NetworkKey).Length);
        Assert.Equal(1, applies);
        Assert.True(viewModel.IsGroupFileExported);
        Assert.False(viewModel.IsGroupFileExportError);
        Assert.Contains("Zapisano plik grupy Hala testowa.vncgroup", viewModel.GroupFileExportStatusText);

        viewModel.ExportGroupFileCommand.Execute(null);

        Assert.Equal(2, exports.Count);
        Assert.Equal(1, applies);
        Assert.Equal(exports[0].Stream.ToArray(), exports[1].Stream.ToArray());
    }

    [Fact]
    public async Task ExportedFileCarriesTheGroupThatIsActuallyRunningOnThisComputer()
    {
        GroupSettingsProposal? applied = null;
        var exports = new List<RecordedExport>();
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(
            exports, applied: proposal => applied = proposal);

        viewModel.ExportGroupFileCommand.Execute(null);

        GroupFile written = GroupFileCodec.Parse(Assert.Single(exports).Stream.ToArray());
        GroupFile enabled = Assert.IsType<GroupSettingsProposal>(applied).ExportInvitation();
        Assert.Equal(enabled.Identity.GroupId, written.Identity.GroupId);
        Assert.Equal(enabled.Identity.KeyId, written.Identity.KeyId);
        Assert.Equal(enabled.Identity.ProfileHash, written.Identity.ProfileHash);
        Assert.Equal(enabled.NetworkKey, written.NetworkKey);
        Assert.Equal(enabled.UdpPort, written.UdpPort);
    }

    [Fact]
    public async Task ExportStatusWarnsAboutTheSecretWithoutEverQuotingTheNetworkKey()
    {
        var exports = new List<RecordedExport>();
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(exports);

        Assert.Contains("Wstecz", viewModel.GroupFileExportHint);
        viewModel.ExportGroupFileCommand.Execute(null);

        GroupFile written = GroupFileCodec.Parse(Assert.Single(exports).Stream.ToArray());
        string everything = string.Join("\n", viewModel.GroupFileExportStatusText,
            viewModel.GroupFileExportWarningText, viewModel.GroupFileExportHint, viewModel.CreateApplyDetail);

        Assert.DoesNotContain(written.NetworkKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Identity.KeyId.ToString(), everything, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Identity.GroupId.ToString(), everything, StringComparison.Ordinal);
        Assert.Contains("sekretem", viewModel.GroupFileExportWarningText);
        Assert.Contains("Nie wysyłaj go pocztą", viewModel.GroupFileExportWarningText);
    }

    [Fact]
    public async Task CancelledSaveDialogWritesNothingAndKeepsTheInvitationForAnotherAttempt()
    {
        int asked = 0;
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(
            selectGroupFileExport: _ => { asked++; return null; });

        viewModel.ExportGroupFileCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.False(viewModel.IsGroupFileExported);
        Assert.False(viewModel.IsGroupFileExportError);
        Assert.Contains("Nie zapisano pliku grupy", viewModel.GroupFileExportStatusText);
        Assert.True(viewModel.ExportGroupFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedWriteIsReportedSafelyAndTheExportCanBeRepeated()
    {
        var exports = new List<RecordedExport>();
        bool failFirst = true;
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(
            selectGroupFileExport: name =>
            {
                if (failFirst)
                {
                    failFirst = false;
                    throw new IOException("D:\\tajne\\zaproszenie.vncgroup jest zajęty");
                }

                return Record(exports, name);
            });

        viewModel.ExportGroupFileCommand.Execute(null);

        Assert.True(viewModel.IsGroupFileExportError);
        Assert.False(viewModel.IsGroupFileExported);
        Assert.Contains("Nie udało się zapisać pliku grupy", viewModel.GroupFileExportStatusText);
        Assert.DoesNotContain("tajne", viewModel.GroupFileExportStatusText);
        Assert.Empty(exports);
        Assert.True(viewModel.ExportGroupFileCommand.CanExecute(null));

        viewModel.ExportGroupFileCommand.Execute(null);

        Assert.False(viewModel.IsGroupFileExportError);
        Assert.True(viewModel.IsGroupFileExported);
        Assert.NotEmpty(GroupFileCodec.Parse(Assert.Single(exports).Stream.ToArray()).NetworkKey);
    }

    [Fact]
    public async Task LeavingTheCreateStepDropsTheInvitationSoItCanNoLongerBeExported()
    {
        var exports = new List<RecordedExport>();
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(exports);
        viewModel.ExportGroupFileCommand.Execute(null);
        Assert.Single(exports);

        viewModel.BackToCreateDraftCommand.Execute(null);

        Assert.False(viewModel.IsGroupFileExportVisible);
        Assert.False(viewModel.ExportGroupFileCommand.CanExecute(null));
        Assert.False(viewModel.IsGroupFileExported);
        Assert.Equal("", viewModel.GroupFileExportStatusText);

        viewModel.ExportGroupFileCommand.Execute(null);
        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.ExportGroupFileCommand.Execute(null);

        Assert.Single(exports);
    }

    [Fact]
    public async Task ExportIsUnavailableWithoutASaveDialogAndAfterAFailedCreate()
    {
        var viewModel = new MultiComputerViewModel(() => ReadyInput(), (_, _) => Task.CompletedTask);
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsGroupFileExportVisible);
        Assert.False(viewModel.ExportGroupFileCommand.CanExecute(null));

        var exports = new List<RecordedExport>();
        var denied = new MultiComputerViewModel(() => ReadyInput(),
            (_, _) => throw new GroupSettingsDeniedException(new InvalidOperationException("secret diagnostic")),
            selectGroupFileExport: name => Record(exports, name));
        denied.SelectCreateGroupCommand.Execute(null);
        denied.ContinueCreateCommand.Execute(null);
        await denied.ApplyCreateCommand.ExecuteAsync(null);

        Assert.True(denied.IsCreateApplyError);
        Assert.False(denied.IsGroupFileExportVisible);
        Assert.False(denied.ExportGroupFileCommand.CanExecute(null));
        denied.ExportGroupFileCommand.Execute(null);
        Assert.Empty(exports);
    }

    [Theory]
    [InlineData("Hala — Aidite", "Hala — Aidite.vncgroup")]
    [InlineData("Hala/2: \"linia\"?", "Hala-2- -linia--.vncgroup")]
    [InlineData("   ...", "grupa.vncgroup")]
    public async Task SuggestedFileNameComesFromTheGroupNameAndStaysAValidWindowsName(
        string groupName, string expected)
    {
        var exports = new List<RecordedExport>();
        MultiComputerViewModel viewModel = await CreatedViewModelAsync(exports, groupName: groupName);

        viewModel.ExportGroupFileCommand.Execute(null);

        string suggested = Assert.Single(exports).SuggestedName;
        Assert.Equal(expected, suggested);
        Assert.DoesNotContain(suggested, Path.GetInvalidFileNameChars().Contains);
    }

    private sealed record RecordedExport(string SuggestedName, MemoryStream Stream);

    private static GroupFileExportTarget Record(List<RecordedExport> exports, string suggestedName)
    {
        var stream = new MemoryStream();
        exports.Add(new RecordedExport(suggestedName, stream));
        return new GroupFileExportTarget(suggestedName, stream);
    }

    /// <summary>A view model that already created and enabled a group, so only the export step is left to exercise.</summary>
    private static async Task<MultiComputerViewModel> CreatedViewModelAsync(
        List<RecordedExport>? exports = null,
        Action? applies = null,
        Action<GroupSettingsProposal>? applied = null,
        Func<string, GroupFileExportTarget?>? selectGroupFileExport = null,
        string groupName = "Hala testowa")
    {
        MultiComputerStatusInput input = ReadyInput();
        var viewModel = new MultiComputerViewModel(() => input, (proposal, _) =>
        {
            applies?.Invoke();
            applied?.Invoke(proposal);
            input = ReadyInput() with
            {
                SavedEnabled = true,
                SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                NetworkActive = true,
                VisibleComputers = 1,
            };
            return Task.CompletedTask;
        }, selectGroupFileExport: selectGroupFileExport
            ?? (name => Record(exports ?? [], name)));

        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = groupName;
        viewModel.ContinueCreateCommand.Execute(null);
        await viewModel.ApplyCreateCommand.ExecuteAsync(null);
        return viewModel;
    }

    private static async Task<MultiComputerViewModel> JoinedViewModelAsync(
        Func<MultiComputerStatusInput, MultiComputerStatusInput> afterApply)
    {
        MultiComputerStatusInput input = ReadyInput();
        GroupFile invitation = InvitationOf(input.CurrentProfile!);
        var viewModel = new MultiComputerViewModel(
            () => input,
            (proposal, _) =>
            {
                input = afterApply(ReadyInput() with
                {
                    SavedEnabled = true,
                    SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                });
                return Task.CompletedTask;
            },
            () => invitation);

        viewModel.SelectJoinGroupCommand.Execute(null);
        viewModel.SelectJoinGroupFileCommand.Execute(null);
        viewModel.UseGroupConfigurationCommand.Execute(null);
        await viewModel.ApplyJoinCommand.ExecuteAsync(null);
        return viewModel;
    }

    private static GroupDiagnosticsRow RowOf(MultiComputerViewModel viewModel, string label) =>
        Assert.Single(viewModel.GroupDiagnosticsRows, row => row.Label == label);

    private static GroupFile InvitationOf(GroupProfile localProfile, bool extraMachine = false)
    {
        AppConfig importedConfig = localProfile.ApplyTo(new AppConfig());
        if (extraMachine)
            importedConfig.Machines.Add(new MachineConfig
            {
                Id = "aidite3", Name = "Aidite 3", Host = "127.0.0.1", Profile = "opencnc",
            });
        GroupProfile imported = GroupProfile.Capture(
            importedConfig, localProfile.Rules, localProfile.Instructions);
        return GroupFile.Create("Hala importowana", imported);
    }

    private static MultiComputerStatusInput ReadyInput(
        bool monitoringEnabled = true, bool ocrAvailable = true, bool canObserve = true,
        Func<string, bool>? hasLocalVncPassword = null)
    {
        var machines = new List<MachineConfig>
        {
            new() { Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Profile = "opencnc" },
            new() { Id = "aidite2", Name = "Aidite 2", Host = "127.0.0.1", Profile = "opencnc" },
        };
        var config = new AppConfig
        {
            Enabled = monitoringEnabled,
            Machines = machines,
            Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal)
            {
                ["opencnc"] = new()
                {
                    Id = "opencnc",
                    StatusBarRoi = new MaszynyAlert.Core.Imaging.RectInt(0, 0, 100, 20),
                },
            },
        };
        var profile = GroupProfile.Capture(config, new RuleSet(), new Dictionary<string, string>());
        return new(false, null, false, null, false, 0, true, false, true, null)
        {
            MonitoringEnabled = monitoringEnabled,
            OcrAvailable = ocrAvailable,
            ConfiguredProfileCount = 1,
            LocalCanObserve = canObserve,
            HasLocalVncPassword = hasLocalVncPassword ?? (id => id is "aidite1" or "aidite2"),
            CurrentProfile = profile,
            Machines = machines
                .Select(machine => new MultiComputerMachineInput(
                    machine.Id, machine.Name, machine.Profile, machine.Enabled))
                .ToArray(),
        };
    }
}

/// <summary>
/// Blok gotowości zapory przed UAC: uruchamia się przy wejściu na ekran potwierdzenia, nie blokuje Utwórz i włącz,
/// oferuje zmianę profilu tylko dla sieci Publicznej i eksportuje instrukcję dla IT bez sekretów.
/// </summary>
public sealed class MultiComputerFirewallReadinessTests
{
    private static readonly string Own = Path.Combine(Path.GetTempPath(), "MaszynyAlert.App.exe");

    private static FirewallReadinessReport Report(bool publicProfile, bool unknown = false)
    {
        var items = new List<FirewallReadinessItem>
        {
            new("interface", "Interfejs LAN", FirewallReadinessState.Ok, "Ethernet 3 (192.168.1.10)", ""),
            new("service", "Usługa Zapora Windows Defender", FirewallReadinessState.Ok, "Działa", ""),
            publicProfile
                ? new("profile", "Profil sieci", FirewallReadinessState.Problem, "Publiczny — sieć „Sieć niezidentyfikowana” (Ethernet 3)", "Ustaw profil Prywatny.")
                : new("profile", "Profil sieci", FirewallReadinessState.Ok, "Prywatny — sieć „Hala” (Ethernet 3)", ""),
            unknown
                ? new("rule", "Reguła VNC Alert", FirewallReadinessState.Unknown, "Nie sprawdzono", "")
                : new("rule", "Reguła VNC Alert", FirewallReadinessState.Ok, "Jeszcze nie istnieje", ""),
        };
        return new(items, "Ethernet 3", "192.168.1.10", publicProfile ? "Sieć niezidentyfikowana" : "Hala",
            publicProfile ? "Public" : "Private", 47777, Own);
    }

    private static MultiComputerStatusInput ReadyInput()
    {
        var config = new AppConfig
        {
            Machines = [new() { Id = "aidite1", Name = "Aidite 1", Host = "127.0.0.1", Profile = "opencnc" }],
            Profiles = new Dictionary<string, ScreenProfile>(StringComparer.Ordinal)
            {
                ["opencnc"] = new() { Id = "opencnc", StatusBarRoi = new MaszynyAlert.Core.Imaging.RectInt(0, 0, 100, 20) },
            },
        };
        var profile = GroupProfile.Capture(config, new RuleSet(), new Dictionary<string, string>());
        return new(false, null, false, null, false, 0, true, false, true, null)
        {
            LocalCanObserve = true,
            ConfiguredProfileCount = 1,
            CurrentProfile = profile,
            CurrentCluster = new ClusterConfig { LocalAddress = "192.168.1.10" },
            Machines = [new("aidite1", "Aidite 1", "opencnc", true)],
        };
    }

    [Fact]
    public async Task ConfirmationStep_ReadsReadinessWithoutBlockingApply_AndOffersPrivateProfileForPublicNetwork()
    {
        int reads = 0; ClusterConfig? readCluster = null;
        var viewModel = new MultiComputerViewModel(ReadyInput, (_, _) => Task.CompletedTask,
            checkFirewallReadiness: (cluster, _) => { reads++; readCluster = cluster; return Task.FromResult(Report(publicProfile: true)); },
            setPrivateNetworkProfile: (_, _) => Task.FromResult(new NetworkProfileChangeResult(NetworkProfileChangeOutcome.Changed, "ok")));

        Assert.False(viewModel.IsFirewallReadinessVisible);
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.DraftGroupName = "Hala";
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();

        Assert.True(viewModel.IsFirewallReadinessVisible);
        Assert.Equal(1, reads);
        Assert.Equal("192.168.1.10", readCluster!.LocalAddress);
        Assert.True(viewModel.IsFirewallReadinessProblem);
        Assert.Equal("Zapis reguły zapory zakończy się odmową", viewModel.FirewallReadinessSummaryText);
        Assert.Equal(4, viewModel.FirewallReadinessRows.Count);
        Assert.Contains(viewModel.FirewallReadinessRows, row => row.IsProblem && row.Label == "Profil sieci");
        Assert.True(viewModel.CanSetPrivateNetworkProfile);
        Assert.True(viewModel.SetPrivateNetworkProfileCommand.CanExecute(null));
        Assert.False(viewModel.CanExportItInstruction, "brak delegata eksportu → brak przycisku");
        Assert.True(viewModel.ApplyCreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task SetPrivateProfile_UsesTheReadInterfaceAddress_ThenReReadsReadiness()
    {
        int reads = 0; string? changed = null;
        var viewModel = new MultiComputerViewModel(ReadyInput, (_, _) => Task.CompletedTask,
            checkFirewallReadiness: (_, _) => Task.FromResult(Report(publicProfile: reads++ == 0)),
            setPrivateNetworkProfile: (address, _) => { changed = address; return Task.FromResult(new NetworkProfileChangeResult(NetworkProfileChangeOutcome.Changed, "Profil sieci ustawiono na Prywatny.")); });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();
        Assert.True(viewModel.CanSetPrivateNetworkProfile);

        await viewModel.SetPrivateNetworkProfileCommand.ExecuteAsync(null);

        Assert.Equal("192.168.1.10", changed);
        Assert.Equal(2, reads);
        Assert.False(viewModel.IsNetworkProfileError);
        Assert.Contains("Prywatny", viewModel.NetworkProfileStatusText);
        Assert.False(viewModel.IsFirewallReadinessProblem);
        Assert.False(viewModel.CanSetPrivateNetworkProfile);
        Assert.Equal("Komputer jest gotowy na regułę zapory", viewModel.FirewallReadinessSummaryText);
    }

    [Fact]
    public async Task DeniedUac_IsAnErrorAndLeavesTheOfferInPlace()
    {
        var viewModel = new MultiComputerViewModel(ReadyInput, (_, _) => Task.CompletedTask,
            checkFirewallReadiness: (_, _) => Task.FromResult(Report(publicProfile: true)),
            setPrivateNetworkProfile: (_, _) => Task.FromResult(new NetworkProfileChangeResult(NetworkProfileChangeOutcome.Denied, "Odmówiono uprawnień administratora.")));
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();

        await viewModel.SetPrivateNetworkProfileCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsNetworkProfileError);
        Assert.Contains("Odmówiono", viewModel.NetworkProfileStatusText);
        Assert.True(viewModel.CanSetPrivateNetworkProfile);
    }

    [Fact]
    public async Task FailedRead_IsReportedAsUnknownAndNeverBlocksApply()
    {
        var viewModel = new MultiComputerViewModel(ReadyInput, (_, _) => Task.CompletedTask,
            checkFirewallReadiness: (_, _) => throw new IOException("PowerShell missing"));
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();

        Assert.Equal("Nie udało się odczytać gotowości zapory", viewModel.FirewallReadinessSummaryText);
        Assert.False(viewModel.IsFirewallReadinessProblem);
        Assert.False(viewModel.CanSetPrivateNetworkProfile);
        Assert.True(viewModel.ApplyCreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task ItInstructionExport_WritesRuleAndFindingsWithoutSecrets()
    {
        var content = new MemoryStream();
        var viewModel = new MultiComputerViewModel(ReadyInput, (_, _) => Task.CompletedTask,
            checkFirewallReadiness: (_, _) => Task.FromResult(Report(publicProfile: true)),
            selectItInstructionExport: name => new GroupFileExportTarget(name, content));
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();
        Assert.True(viewModel.CanExportItInstruction);

        viewModel.ExportItInstructionCommand.Execute(null);

        string text = System.Text.Encoding.UTF8.GetString(content.ToArray());
        Assert.Contains("New-NetFirewallRule", text);
        Assert.Contains("-LocalPort 47777", text);
        Assert.Contains("Publiczny", text);
        Assert.False(viewModel.IsItInstructionError);
        Assert.Contains("Zapisano instrukcję dla IT", viewModel.ItInstructionStatusText);
    }

    [Fact]
    public async Task ReadyNetwork_HidesBothButtons_AndBackClearsTheBlock()
    {
        var viewModel = new MultiComputerViewModel(ReadyInput, (_, _) => Task.CompletedTask,
            checkFirewallReadiness: (_, _) => Task.FromResult(Report(publicProfile: false)),
            setPrivateNetworkProfile: (_, _) => Task.FromResult(new NetworkProfileChangeResult(NetworkProfileChangeOutcome.Changed, "")),
            selectItInstructionExport: name => null);
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();

        Assert.False(viewModel.IsFirewallReadinessProblem);
        Assert.False(viewModel.CanSetPrivateNetworkProfile);
        Assert.False(viewModel.CanExportItInstruction);
        Assert.NotNull(viewModel.LastFirewallReadiness);

        viewModel.BackToCreateDraftCommand.Execute(null);

        Assert.False(viewModel.IsFirewallReadinessVisible);
        Assert.Null(viewModel.LastFirewallReadiness);
        Assert.Empty(viewModel.FirewallReadinessRows);
    }

    [Fact]
    public async Task ChoosingTheLanInterface_ReReadsReadinessAndSavesTheAddressWithTheGroup()
    {
        var candidates = new List<LanInterfaceChoice> { new("Wi-Fi", "10.0.0.5"), new("Ethernet 3", "192.168.1.10") };
        var reads = new List<string?>();
        GroupSettingsProposal? applied = null;
        var viewModel = new MultiComputerViewModel(() => ReadyInput() with { CurrentCluster = new ClusterConfig() },
            (proposal, _) => { applied = proposal; return Task.CompletedTask; },
            checkFirewallReadiness: (cluster, _) =>
            {
                reads.Add(cluster.LocalAddress);
                if (cluster.LocalAddress is null)
                    return Task.FromResult(new FirewallReadinessReport(
                        [new("interface", "Interfejs LAN", FirewallReadinessState.Problem, "Nie wybrano", "Wybierz interfejs LAN.")],
                        null, null, null, null, 47777, Own) { Candidates = candidates });
                return Task.FromResult(Report(publicProfile: false) with { Candidates = candidates });
            });
        viewModel.SelectCreateGroupCommand.Execute(null);
        viewModel.ContinueCreateCommand.Execute(null);
        await Task.Yield();

        Assert.True(viewModel.IsLanInterfaceChoiceVisible);
        Assert.Null(viewModel.SelectedLanInterface);
        Assert.True(viewModel.IsFirewallReadinessProblem);
        Assert.Equal([null], reads);

        viewModel.SelectedLanInterface = candidates[1];
        await Task.Yield();

        Assert.Equal([null, "192.168.1.10"], reads);
        Assert.False(viewModel.IsFirewallReadinessProblem);
        Assert.Same(candidates[1], viewModel.SelectedLanInterface);

        await viewModel.ApplyCreateCommand.ExecuteAsync(null);

        Assert.Equal("192.168.1.10", applied!.GetData().Cluster.LocalAddress);
        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public void StartupCheckProblem_IsNamedInRuntimeStateInsteadOfWaiting()
    {
        var check = new FirewallStartupCheck(true, "Odczyt przy starcie: Profil sieci: Publiczny — sieć „Sieć niezidentyfikowana” (Ethernet 3).", DateTimeOffset.Now);
        var viewModel = new MultiComputerViewModel(() => new MultiComputerStatusInput(
            SavedEnabled: true, SavedGroupName: "Hala", NetworkActive: true, Failure: null, HasInterruptedChange: false,
            VisibleComputers: 1, IsWatcher: true, NoWatcher: false, StateFresh: true, WatcherHost: null)
        { StartupFirewallCheck = check });

        Assert.Equal("Problem z profilem sieci lub zaporą", viewModel.RuntimeStateText);
        Assert.Contains("Publiczny", viewModel.RuntimeStateDetail);
        Assert.True(viewModel.IsRuntimeWarning);
    }

    [Fact]
    public void StartupCheckOk_KeepsWaitingState_AndDiagnosticsRowIsGreen()
    {
        var check = new FirewallStartupCheck(false, "Odczyt przy starcie: bez zastrzeżeń.", DateTimeOffset.Now);
        var viewModel = new MultiComputerViewModel(() => ReadyInput() with
        {
            SavedEnabled = true, SavedGroupName = "Hala", NetworkActive = true, VisibleComputers = 1, StartupFirewallCheck = check,
        });

        Assert.Equal("Oczekiwanie na drugi komputer", viewModel.RuntimeStateText);
    }
}
