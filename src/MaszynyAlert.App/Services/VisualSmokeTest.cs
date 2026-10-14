using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace MaszynyAlert.App.Services;

/// <summary>Only used by --smoke-test. Renders actual WPF controls without machine connections.</summary>
internal static class VisualSmokeTest
{
    internal static async Task VerifyPreviewActivationAsync(IServiceProvider services)
    {
        var preview = services.GetRequiredService<OperatorWindow>();
        var tray = services.GetRequiredService<TrayIconService>();
        preview.Hide();
        string name = @"Local\MaszynyAlert.PreviewSmoke." + Guid.NewGuid().ToString("N");
        using var activation = new MaszynyAlert.Windows.PreviewActivation(name);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activation.Listen(() => preview.Dispatcher.BeginInvoke(new Action(() =>
        {
            tray.ShowStatus();
            shown.TrySetResult();
        })));
        async Task Request()
        {
            if (!await Task.Run(() => MaszynyAlert.Windows.PreviewActivation.TryRequest(name)))
                throw new InvalidOperationException("Nie dostarczono żądania Podglądu.");
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!preview.IsVisible || preview.WindowState == WindowState.Minimized || preview.Icon is null)
                throw new InvalidOperationException("Skrót nie przywrócił Podglądu z ikoną.");
        }
        await Request();
        preview.WindowState = WindowState.Minimized;
        shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Request();
        preview.Close(); // Close is intentionally mapped to Hide by OperatorWindow.
        shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Request();
        preview.Hide();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "preview-activation.txt"),
            "PASS: named activation opens the singleton operator preview, restores minimized and closed windows, and loads its icon.");
    }

    internal static async Task VerifyMachineScreenPreviewAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output");
        Directory.CreateDirectory(Path.Combine(output, "ui"));

        if (!orchestrator.Config.Enabled)
        {
            var disabled = new ScreenPreviewWindow(orchestrator.Preview, "smoke", "TEST wyłączenia");
            disabled.Show();
            await disabled.RefreshCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            if (disabled.PreviewImage.Source is not null || !disabled.StatusText.Text.Contains("wyłączone"))
                throw new InvalidOperationException("Pokaż ekran nie respektuje globalnego wyłączenia monitorowania.");
            disabled.Close();
            File.WriteAllText(Path.Combine(output, "screen-preview-disabled.txt"),
                "PASS: global monitoring disable blocks the screen preview source.");
            return;
        }

        const string id = "smoke-screen-preview";
        var machine = new MaszynyAlert.Core.Config.MachineConfig
        {
            Id = id, Name = "TEST podglądu ekranu", Host = "127.0.0.1", Port = 1, Profile = "opencnc",
        };
        orchestrator.Config.Machines.Add(machine);
        Directory.CreateDirectory(orchestrator.Config.Paths.TestScreensDir);
        string path = Path.Combine(orchestrator.Config.Paths.TestScreensDir, id + ".png");
        var image = new MaszynyAlert.Core.Imaging.RgbaImage(320, 180);
        image.Fill(new(28, 64, 110));
        for (int y = 35; y < 145; y++)
            for (int x = 55; x < 265; x++)
                image.SetPixel(x, y, new(235, 240, 245));
        File.WriteAllBytes(path, MaszynyAlert.Core.Imaging.PngCodec.Encode(image));

        var window = new ScreenPreviewWindow(orchestrator.Preview, id, machine.Name);
        window.Show();
        await window.RefreshCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        if (window.PreviewImage.Source is null || !window.RefreshButton.IsEnabled
            || !window.StatusText.Text.Contains("TEST Z PLIKU")
            || !window.StatusText.Text.Contains("Obraz z ")
            || !window.StatusText.Text.Contains("Pojedynczy obraz"))
            throw new InvalidOperationException("Okno Pokaż ekran nie pokazało obrazu, czasu, pochodzenia lub trybu pojedynczej klatki.");
        Render(window, Path.Combine(output, "ui", "pokaz-ekran.png"), 900, 700);

        File.WriteAllBytes(path, [1, 2, 3, 4]);
        window.RefreshButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await window.RefreshCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        if (window.PreviewImage.Source is not null || !window.RefreshButton.IsEnabled
            || !window.StatusText.Text.Contains("Nie można pobrać"))
            throw new InvalidOperationException("Błąd odświeżenia pozostawił stary obraz albo zablokował przycisk.");
        window.Close();

        const string closingId = "smoke-screen-preview-close";
        var closingMachine = machine with { Id = closingId, Name = "TEST zamknięcia" };
        var closingConfig = new MaszynyAlert.Core.Config.AppConfig { Machines = [closingMachine] };
        var blocking = new BlockingScreenSource(closingId);
        var closingService = new MaszynyAlert.Core.Machines.MachinePreviewService(
            () => closingConfig, _ => null, (_, _) => blocking,
            new MaszynyAlert.Core.Abstractions.SystemClock());
        var closingWindow = new ScreenPreviewWindow(closingService, closingId, closingMachine.Name);
        closingWindow.Show();
        await blocking.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        closingWindow.Close();
        await closingWindow.RefreshCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        if (blocking.DisposeCount != 1)
            throw new InvalidOperationException("Zamknięcie okna podczas pobierania nie zwolniło źródła obrazu.");

        File.WriteAllText(Path.Combine(output, "screen-preview.txt"),
            "PASS: real WPF window loads a test PNG, shows time/origin/single-frame status, clears stale image on refresh error, and cancels/disposes an in-flight read when closed.");
    }

    internal static async Task VerifyResumeAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        if (!orchestrator.Config.Enabled) return;
        var cfg = orchestrator.Config;
        const string id = "smoke-reconnect";
        cfg.Profiles[id] = new MaszynyAlert.Core.Config.ScreenProfile
            { Id = id, StatusBarRoi = new(0, 0, 4, 4) };
        var machine = new MaszynyAlert.Core.Config.MachineConfig
            { Id = id, Name = "TEST reconnect", Host = "127.0.0.1", Port = 1, Profile = id };
        Directory.CreateDirectory(cfg.Paths.TestScreensDir);
        var frame = new MaszynyAlert.Core.Imaging.RgbaImage(4, 4);
        frame.Fill(new(255, 255, 255));
        File.WriteAllBytes(Path.Combine(cfg.Paths.TestScreensDir, id + ".png"), MaszynyAlert.Core.Imaging.PngCodec.Encode(frame));
        services.GetRequiredService<MaszynyAlert.Core.Abstractions.ISecretStore>()
            .Set(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword(id), "smoke-only");
        cfg.Machines.Add(machine);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var supervisor = orchestrator.Supervisor;
        await supervisor.StopAsync();
        await supervisor.StoppedTask.WaitAsync(deadline.Token);
        await supervisor.StartAsync(deadline.Token);
        while (supervisor.Get(id)?.Snapshot.Source != "plik") await Task.Delay(50, deadline.Token);
        var previous = supervisor.Get(id);
        orchestrator.Cluster.ForceYield();
        while (ReferenceEquals(previous, supervisor.Get(id)) || supervisor.Get(id)?.Snapshot.Source != "plik")
            await Task.Delay(50, deadline.Token);
        if (!orchestrator.Cluster.IsWatcher || !supervisor.IsRunning)
            throw new InvalidOperationException("Obserwacja nie wróciła po wybudzeniu w trybie jednego PC.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "resume.txt"),
            "PASS: single-PC resume stops the previous monitor and starts a new monitor; file source only, no machine connection.");
    }

    internal static async Task VerifyInstructionEditorAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        var admin = services.GetRequiredService<StatusWindow>();
        var view = (AlarmInstructionsView)((ContentControl)admin.FindName("InstructionsHost")).Content;
        var editor = (AlarmInstructionsViewModel)view.DataContext;
        ((System.Windows.Controls.TabControl)admin.FindName("AdminTabs")).SelectedIndex = 1;
        var example = AlarmInstructionsViewModel.Example("rule:code-705", "Servo 6", "restart");
        var machine = new MaszynyAlert.Core.Config.MachineConfig { Id = "editor", Name = "TEST edytora", Host = "unused", Profile = "opencnc" };
        var state = services.GetRequiredService<AlertStateMachine>();
        var alert = state.OnRedConfirmed(machine, new MaszynyAlert.Core.Ocr.MappedMessage("code-705", "Servo 6", "restart", "", "", true), null);
        var preview = services.GetRequiredService<OperatorWindow>();
        preview.Show();
        async Task Flush() => await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Flush();
        editor.Search = "705";
        editor.Selected = editor.Entries.Single(r => r.Key == "code:705");
        var edited = editor.Selected;
        const string text = "1. Sprawdź numer alarmu.\n2. Zastosuj zatwierdzoną procedurę stanowiska.";
        edited.Text = text;
        editor.Search = "1200";
        editor.Selected = editor.Entries.First();
        editor.Search = "705";
        editor.Selected = edited;
        if (edited.Text != text) throw new InvalidOperationException("Zmiana wyboru zgubiła treść instrukcji.");
        await editor.SaveCommand.ExecuteAsync(null);
        await Flush();
        if (orchestrator.Help.InstructionsFor(example) != text || edited.DraftStatus == "Niezapisane zmiany")
            throw new InvalidOperationException("Zapis edytora nie zastosował instrukcji.");
        var alarmVm = (AlertWindowViewModel)services.GetRequiredService<AlertWindow>().DataContext;
        var operatorItems = ((ItemsControl)preview.FindName("AlertCards")).Items.Cast<AlertItemViewModel>();
        var technical = services.GetRequiredService<StatusWindowViewModel>();
        technical.Refresh(DateTimeOffset.UtcNow);
        if (!alarmVm.Items.Single(a => a.Key == alert.Key).Solution.Contains(text)
            || !operatorItems.Single(a => a.Key == alert.Key).Solution.Contains(text)
            || !technical.ActiveAlerts.Single(a => a.Key == alert.Key).Solution.Contains(text))
            throw new InvalidOperationException("Instrukcja nie odświeżyła się we wszystkich widokach alertu.");
        var reloaded = new AlarmHelpService(orchestrator.Help.Rules,
            Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "alarm-instructions.json"), false);
        if (reloaded.InstructionsFor(example) != text) throw new InvalidOperationException("Instrukcja nie przetrwała ponownego odczytu pliku.");
        Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "edytor-instrukcji.png"), 1220, 850);
        editor.RestoreCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        await Flush();
        if (orchestrator.Help.InstructionsFor(example) != MaszynyAlert.Core.Ocr.AlarmHelp.InstructionsFor(example))
            throw new InvalidOperationException("Nie przywrócono domyślnej instrukcji.");
        await orchestrator.AcknowledgeAsync(alert.Key);
        preview.Hide();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "instruction-editor.txt"),
            "PASS: search and draft retention; save and reload; live help in alarm/operator/technical views; restore default.");
    }

    internal static async Task VerifyMultiComputerWizardStartAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        var admin = services.GetRequiredService<StatusWindow>();
        var view = (MultiComputerView)((ContentControl)admin.FindName("MultiComputerHost")).Content;
        var viewModel = (MultiComputerViewModel)view.DataContext;
        var secrets = services.GetRequiredService<MaszynyAlert.Core.Abstractions.ISecretStore>();
        var originalMachines = orchestrator.Config.Machines.ToArray();
        var smokeMachines = new[]
        {
            new MaszynyAlert.Core.Config.MachineConfig
                { Id = "smoke-group-aidite1", Name = "Aidite 1", Host = "127.0.0.1", Port = 1, Profile = "opencnc" },
            new MaszynyAlert.Core.Config.MachineConfig
                { Id = "smoke-group-aidite2", Name = "Aidite 2", Host = "127.0.0.1", Port = 1, Profile = "opencnc" },
        };
        orchestrator.Config.Machines.Clear();
        foreach (var machine in smokeMachines)
        {
            orchestrator.Config.Machines.Add(machine);
            secrets.Set(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword(machine.Id), "smoke-only");
        }

        try
        {
            viewModel.Refresh();

            var saved = (TextBlock)view.FindName("SavedSettingText");
            var runtime = (TextBlock)view.FindName("RuntimeStateText");
            var create = (System.Windows.Controls.Button)view.FindName("CreateGroupButton");
            var join = (System.Windows.Controls.Button)view.FindName("JoinGroupButton");
            if (saved.Text != "Wyłączona" || runtime.Text != "Wyłączone"
                || ReferenceEquals(saved, runtime)
                || !((TextBlock)view.FindName("VisibleComputersText")).Text.Contains("transport grupy nie działa"))
                throw new InvalidOperationException("Zakładka nie rozróżnia zapisanego ustawienia od faktycznego runtime.");

            bool savedEnabled = orchestrator.Config.Cluster.Enabled;
            bool networkActive = orchestrator.Cluster.NetworkActive;
            if (create.Command is null || join.Command is null)
                throw new InvalidOperationException("Przyciski pierwszego kroku kreatora nie mają podłączonych poleceń.");
            create.Command.Execute(create.CommandParameter);
            if (!viewModel.IsCreateGroupSelected || viewModel.IsJoinGroupSelected
                || !viewModel.ChoiceTitle.Contains("Utwórz grupę"))
                throw new InvalidOperationException("Pierwszy wybór kreatora nie zaznaczył ścieżki tworzenia grupy.");
            view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

            var createDraft = (Border)view.FindName("CreateDraftPanel");
            var createConfirmation = (Border)view.FindName("CreateConfirmationPanel");
            var name = (System.Windows.Controls.TextBox)view.FindName("DraftGroupNameTextBox");
            var continueButton = (System.Windows.Controls.Button)view.FindName("ContinueCreateButton");
            var backButton = (System.Windows.Controls.Button)view.FindName("BackToCreateDraftButton");
            var applyButton = (System.Windows.Controls.Button)view.FindName("ApplyCreateButton");
            var machines = (ItemsControl)view.FindName("CreateMachinesList");
            string machineSummary = ((TextBlock)view.FindName("CreateMachineSummaryText")).Text;
            string profileSummary = ((TextBlock)view.FindName("CreateProfilesText")).Text;
            string readiness = ((TextBlock)view.FindName("LocalReadinessText")).Text;
            string passwords = ((TextBlock)view.FindName("PasswordsReadinessText")).Text;
            bool readinessMatchesMode = orchestrator.Config.Enabled
                ? readiness.Contains("OCR ograniczony") && passwords.Contains("2/2")
                : readiness.Contains("Włącz monitorowanie") && passwords.Contains("nie są sprawdzane");
            if (createDraft.Visibility != Visibility.Visible || name.Text != "Hala — Aidite"
                || machines.Items.Count != 2
                || !machineSummary.Contains("2 z 2")
                || !profileSummary.Contains("opencnc (2)")
                || !readinessMatchesMode)
                throw new InvalidOperationException(
                    $"Szkic tworzenia grupy ma niepełne dane: visible={createDraft.Visibility}, name='{name.Text}', "
                    + $"items={machines.Items.Count}, machines='{machineSummary}', profiles='{profileSummary}', "
                    + $"readiness='{readiness}', passwords='{passwords}'.");

            name.Text = " ";
            name.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            if (continueButton.Command?.CanExecute(continueButton.CommandParameter) != false)
                throw new InvalidOperationException("Dalej musi być zablokowane dla niepoprawnej nazwy grupy.");

            name.Text = "Hala testowa";
            name.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            if (viewModel.DraftGroupName != "Hala testowa" || !viewModel.IsDraftGroupNameValid)
                throw new InvalidOperationException("Pole nazwy grupy nie aktualizuje wyłącznie lokalnego szkicu.");
            bool canContinue = continueButton.Command?.CanExecute(continueButton.CommandParameter) == true;
            if (canContinue != orchestrator.Config.Enabled)
                throw new InvalidOperationException(
                    "Dalej nie odpowiada lokalnej gotowości: aktywne monitorowanie z hasłami ma przejść, wyłączone ma blokować.");

            ((System.Windows.Controls.TabControl)admin.FindName("AdminTabs")).SelectedIndex = 2;
            string modeSuffix = orchestrator.Config.Enabled ? "" : "-monitoring-wylaczony";
            Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", $"praca-na-kilku-komputerach{modeSuffix}.png"), 1220, 1000);
            var scroller = (ScrollViewer)view.Content;
            scroller.ScrollToEnd();
            view.UpdateLayout();
            Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", $"utworz-grupe-szkic{modeSuffix}.png"), 1220, 1000);
            scroller.ScrollToHome();

            if (orchestrator.Config.Enabled)
            {
                continueButton.Command!.Execute(continueButton.CommandParameter);
                view.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

                var confirmationMachines = (ItemsControl)view.FindName("ConfirmationMachinesList");
                string confirmationName = ((TextBlock)view.FindName("ConfirmationGroupNameText")).Text;
                if (!viewModel.IsCreateConfirmationStep || viewModel.IsCreateDraftStep
                    || createDraft.Visibility != Visibility.Collapsed
                    || createConfirmation.Visibility != Visibility.Visible
                    || confirmationName != "Hala testowa"
                    || confirmationMachines.Items.Count != 2
                    || viewModel.CurrentCreateProposal is null)
                    throw new InvalidOperationException(
                        $"Ekran potwierdzenia jest niepełny: draft={createDraft.Visibility}, "
                        + $"confirmation={createConfirmation.Visibility}, name='{confirmationName}', "
                        + $"items={confirmationMachines.Items.Count}.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "utworz-grupe-potwierdzenie.png"), 1220, 1000);
                scroller.ScrollToHome();

                int fakeApplyCalls = 0;
                int fakeExportDialogs = 0;
                var fakeExports = new List<(string Name, MemoryStream Content)>();
                MultiComputerStatusInput fakeStatus = MultiComputerStatusInput.From(orchestrator);
                // Only the settings transaction and the file dialog are faked; the invitation is serialized for real
                // into memory, so nothing lands on this computer's disk and no UAC, firewall or runtime is touched.
                var fakeViewModel = new MultiComputerViewModel(() => fakeStatus, (proposal, _) =>
                {
                    fakeApplyCalls++;
                    fakeStatus = fakeStatus with
                    {
                        SavedEnabled = true,
                        SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                        NetworkActive = true,
                        VisibleComputers = 1,
                    };
                    return Task.CompletedTask;
                }, selectGroupFileExport: suggestedName =>
                {
                    fakeExportDialogs++;
                    var content = new MemoryStream();
                    fakeExports.Add((suggestedName, content));
                    return new GroupFileExportTarget(suggestedName, content);
                });
                fakeViewModel.SelectCreateGroupCommand.Execute(null);
                fakeViewModel.DraftGroupName = "Hala testowa";
                fakeViewModel.ContinueCreateCommand.Execute(null);
                view.DataContext = fakeViewModel;
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                await fakeViewModel.ApplyCreateCommand.ExecuteAsync(null);
                view.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                if (fakeApplyCalls != 1 || !fakeViewModel.IsCreateApplyCompleted
                    || fakeViewModel.IsCreateApplyError
                    || ((TextBlock)view.FindName("CreateApplyStatusText")).Text != "Grupa utworzona i włączona"
                    || ((TextBlock)view.FindName("SavedSettingText")).Text != "Włączona"
                    || ((TextBlock)view.FindName("RuntimeStateText")).Text != "Oczekiwanie na drugi komputer"
                    || applyButton.Command?.CanExecute(applyButton.CommandParameter) != false)
                    throw new InvalidOperationException("Utwórz i włącz nie pokazało bezpiecznego sukcesu i odświeżonych, rozdzielonych stanów.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "utworz-grupe-sukces.png"), 1220, 1000);
                scroller.ScrollToHome();

                // The separate, explicit export. It must only write a file, so the faked apply counter stays at one.
                var exportPanel = (Border)view.FindName("GroupFileExportPanel");
                var exportButton = (System.Windows.Controls.Button)view.FindName("ExportGroupFileButton");
                var exportStatusPanel = (Border)view.FindName("GroupFileExportStatusPanel");
                string exportWarning = ((TextBlock)view.FindName("GroupFileExportWarningText")).Text;
                if (exportPanel.Visibility != Visibility.Visible
                    || exportStatusPanel.Visibility != Visibility.Collapsed
                    || exportButton.Command?.CanExecute(exportButton.CommandParameter) != true
                    || fakeExportDialogs != 0
                    || !exportWarning.Contains("sekretem")
                    || !((TextBlock)view.FindName("GroupFileExportHintText")).Text.Contains("Wstecz"))
                    throw new InvalidOperationException(
                        $"Utworzenie grupy nie odblokowało jawnego, ostrzegającego kroku eksportu pliku grupy: "
                        + $"panel={exportPanel.Visibility}, okna={fakeExportDialogs}.");

                exportButton.Command.Execute(exportButton.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                string exportStatus = ((TextBlock)view.FindName("GroupFileExportStatusText")).Text;
                (string Name, MemoryStream Content) exported = fakeExports.Count == 1
                    ? fakeExports[0]
                    : throw new InvalidOperationException(
                        $"Jedno kliknięcie eksportu ma zapisać dokładnie jeden plik, a zapisało {fakeExports.Count}.");
                GroupFile exportedInvitation = GroupFileCodec.Parse(exported.Content.ToArray());
                if (fakeApplyCalls != 1 || fakeExportDialogs != 1
                    || exported.Name != "Hala testowa.vncgroup"
                    || exportedInvitation.Identity.GroupName != "Hala testowa"
                    || exportStatusPanel.Visibility != Visibility.Visible
                    || fakeViewModel.IsGroupFileExportError
                    || !exportStatus.Contains("Zapisano plik grupy Hala testowa.vncgroup")
                    || exportStatus.Contains(exportedInvitation.NetworkKey, StringComparison.Ordinal)
                    || exportWarning.Contains(exportedInvitation.NetworkKey, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Eksport nie zapisał dokładnie jednego czytelnego zaproszenia bez cytowania klucza: "
                        + $"applies={fakeApplyCalls}, okna={fakeExportDialogs}, nazwa='{exported.Name}'.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "utworz-grupe-plik-zapisany.png"), 1220, 1000);
                scroller.ScrollToHome();

                // The same read-only network step exists on the create path: the first computer of a group has the
                // very same question to answer, without a second apply, a new invitation or an administrator prompt.
                var checkCreatedGroupNetwork = (System.Windows.Controls.Button)view.FindName("CheckCreatedGroupNetworkButton");
                var createNetworkCheckHintText = (TextBlock)view.FindName("CreateNetworkCheckHintText");
                var createGroupDiagnosticsPanel = (Border)view.FindName("GroupDiagnosticsPanel");
                if (checkCreatedGroupNetwork.Command?.CanExecute(checkCreatedGroupNetwork.CommandParameter) != true
                    || createGroupDiagnosticsPanel.Visibility != Visibility.Collapsed
                    || createNetworkCheckHintText.Visibility != Visibility.Visible
                    || !createNetworkCheckHintText.Text.Contains("Nie tworzy grupy po raz drugi", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Utworzenie grupy nie odblokowało jawnego sprawdzenia sieci grupy z opisem, że krok nie tworzy grupy po raz drugi.");

                checkCreatedGroupNetwork.Command.Execute(checkCreatedGroupNetwork.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                var createGroupDiagnosticsList = (ItemsControl)view.FindName("GroupDiagnosticsList");
                if (createGroupDiagnosticsPanel.Visibility != Visibility.Visible
                    || createConfirmation.Visibility != Visibility.Collapsed
                    || exportPanel.Visibility != Visibility.Collapsed
                    || createGroupDiagnosticsList.Items.Count != 7
                    || fakeViewModel.GroupDiagnosticsRows
                        .Single(row => row.Label == "Zapora, sieć i odbiór na hali").Severity
                        != GroupDiagnosticsSeverity.NotChecked
                    // The startup read of the network profile is the orchestrator's; a faked status has none.
                    || fakeViewModel.GroupDiagnosticsRows
                        .Single(row => row.Label == "Profil sieci i reguła zapory").Severity
                        != GroupDiagnosticsSeverity.NotChecked
                    || fakeViewModel.GroupDiagnosticsRows.Any(row =>
                        row.Detail.Contains(exportedInvitation.NetworkKey, StringComparison.Ordinal))
                    || fakeApplyCalls != 1)
                    throw new InvalidOperationException(
                        $"Sprawdzenie sieci po utworzeniu grupy nie pokazało bezpiecznej, tylko odczytanej diagnostyki: "
                        + $"panel={createGroupDiagnosticsPanel.Visibility}, potwierdzenie={createConfirmation.Visibility}, "
                        + $"eksport={exportPanel.Visibility}, wiersze={createGroupDiagnosticsList.Items.Count}, applies={fakeApplyCalls}.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "utworz-grupe-siec-grupy.png"), 1220, 1000);
                scroller.ScrollToHome();

                // Repeating the read must not apply settings again either.
                var createRunGroupDiagnostics = (System.Windows.Controls.Button)view.FindName("RunGroupDiagnosticsButton");
                createRunGroupDiagnostics.Command!.Execute(createRunGroupDiagnostics.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                if (fakeApplyCalls != 1)
                    throw new InvalidOperationException("Ponowne sprawdzenie sieci po utworzeniu grupy zastosowało ustawienia po raz drugi.");

                var backFromCreateGroupDiagnostics = (System.Windows.Controls.Button)view.FindName("BackFromGroupDiagnosticsButton");
                backFromCreateGroupDiagnostics.Command!.Execute(backFromCreateGroupDiagnostics.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                if (createGroupDiagnosticsPanel.Visibility != Visibility.Collapsed
                    || createConfirmation.Visibility != Visibility.Visible
                    || exportPanel.Visibility != Visibility.Visible
                    || exportButton.Command?.CanExecute(exportButton.CommandParameter) != true
                    || fakeApplyCalls != 1)
                    throw new InvalidOperationException(
                        "Wstecz z diagnostyki sieci utworzonej grupy nie wróciło do potwierdzenia z zaproszeniem gotowym do eksportu.");

                view.DataContext = viewModel;
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

                // Leaving the create step drops the secret invitation, so the export cannot outlive it.
                fakeViewModel.BackToCreateDraftCommand.Execute(null);
                fakeViewModel.ExportGroupFileCommand.Execute(null);
                if (fakeApplyCalls != 1 || fakeExportDialogs != 1 || fakeViewModel.IsGroupFileExportVisible
                    || fakeViewModel.GroupFileExportStatusText.Length != 0)
                    throw new InvalidOperationException(
                        "Wstecz z potwierdzenia nie porzuciło zaproszenia utworzonej grupy razem z krokiem eksportu.");

                backButton.Command!.Execute(backButton.CommandParameter);
                view.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                if (!viewModel.IsCreateDraftStep || viewModel.IsCreateConfirmationStep
                    || name.Text != "Hala testowa" || viewModel.CurrentCreateProposal is not null)
                    throw new InvalidOperationException("Wstecz nie zachowało nazwy albo pozostawiło nieaktualną propozycję.");
            }
            else if (createConfirmation.Visibility != Visibility.Collapsed || viewModel.CurrentCreateProposal is not null)
                throw new InvalidOperationException("Wyłączone monitorowanie nie może otworzyć potwierdzenia tworzenia grupy.");

            MultiComputerStatusInput joinStatus = MultiComputerStatusInput.From(orchestrator);
            GroupProfile localProfile = joinStatus.CurrentProfile!;
            AppConfig importedConfig = localProfile.ApplyTo(new AppConfig());
            importedConfig.Machines[0] = importedConfig.Machines[0] with { Name = "Aidite 1 z pliku" };
            // Machines that exist only in the invitation have no local VNC password; the file never carries one.
            // Two of them let the step exercise both the per-machine and the one shared password variant.
            importedConfig.Machines.Add(new MachineConfig
            {
                Id = "smoke-group-aidite3", Name = "Aidite 3 tylko w grupie",
                Host = "127.0.0.1", Port = 1, Profile = "opencnc",
            });
            importedConfig.Machines.Add(new MachineConfig
            {
                Id = "smoke-group-aidite4", Name = "Aidite 4 tylko w grupie",
                Host = "127.0.0.1", Port = 1, Profile = "opencnc",
            });
            GroupProfile importedProfile = GroupProfile.Capture(
                importedConfig, localProfile.Rules, localProfile.Instructions);
            GroupFile invitation = GroupFile.Create("Hala smoke import", importedProfile);
            int fakeImportReads = 0;
            int fakeJoinApplies = 0;
            // The apply operation and the file dialog are faked; the local secret write is the real one. The fake
            // only moves the injected status source, exactly as a real transaction would be seen by the next read.
            var joinViewModel = new MultiComputerViewModel(
                () => joinStatus,
                (proposal, _) =>
                {
                    fakeJoinApplies++;
                    joinStatus = joinStatus with
                    {
                        SavedEnabled = true,
                        SavedGroupName = proposal.GetData().Cluster.Group!.GroupName,
                        NetworkActive = true,
                        VisibleComputers = 1,
                    };
                    return Task.CompletedTask;
                },
                () => { fakeImportReads++; return invitation; },
                orchestrator.SetLocalVncPassword);
            view.DataContext = joinViewModel;
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

            join.Command!.Execute(join.CommandParameter);
            var joinFilePanel = (Border)view.FindName("JoinFilePanel");
            var joinSummaryPanel = (Border)view.FindName("JoinSummaryPanel");
            var selectJoinFile = (System.Windows.Controls.Button)view.FindName("SelectJoinGroupFileButton");
            if (joinViewModel.IsCreateGroupSelected || !joinViewModel.IsJoinGroupSelected
                || !joinViewModel.ChoiceTitle.Contains("Dołącz do grupy")
                || createDraft.Visibility != Visibility.Collapsed
                || joinFilePanel.Visibility != Visibility.Visible
                || selectJoinFile.Command?.CanExecute(selectJoinFile.CommandParameter) != true)
                throw new InvalidOperationException("Pierwszy wybór kreatora nie otworzył bezpiecznego wyboru pliku grupy.");

            selectJoinFile.Command.Execute(selectJoinFile.CommandParameter);
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            view.UpdateLayout();
            var joinMachines = (ItemsControl)view.FindName("JoinMachinesList");
            var joinDifferences = (ItemsControl)view.FindName("JoinDifferencesList");
            string importedGroupName = ((TextBlock)view.FindName("JoinGroupNameText")).Text;
            string differenceText = ((TextBlock)view.FindName("JoinProfileDifferenceText")).Text;
            if (fakeImportReads != 1 || fakeJoinApplies != 0
                || !joinViewModel.IsJoinSummaryStep || joinViewModel.IsJoinFileStep
                || joinFilePanel.Visibility != Visibility.Collapsed
                || joinSummaryPanel.Visibility != Visibility.Visible
                || importedGroupName != "Hala smoke import"
                || joinMachines.Items.Count != 4
                || joinDifferences.Items.Count != 6
                || !joinDifferences.Items.Cast<MaszynyAlert.App.ViewModels.JoinGroupDifferenceRow>()
                    .Any(row => row.Label == "Instrukcje naprawy" && !string.IsNullOrWhiteSpace(row.Value))
                || !differenceText.Contains("różni się"))
                throw new InvalidOperationException(
                    $"Import nie pokazał zamrożonego podsumowania grupy i różnic bez zastosowania ustawień: "
                    + $"reads={fakeImportReads}, applies={fakeJoinApplies}, summary={joinViewModel.IsJoinSummaryStep}, "
                    + $"file={joinViewModel.IsJoinFileStep}, filePanel={joinFilePanel.Visibility}, "
                    + $"summaryPanel={joinSummaryPanel.Visibility}, name='{importedGroupName}', "
                    + $"machines={joinMachines.Items.Count}, differences={joinDifferences.Items.Count}, "
                    + $"differenceText='{differenceText}', importError={joinViewModel.IsJoinImportError}, "
                    + $"importStatus='{joinViewModel.JoinImportStatusText}'.");
            scroller.ScrollToEnd();
            view.UpdateLayout();
            Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "dolacz-grupe-podsumowanie.png"), 1220, 1000);
            scroller.ScrollToHome();

            var useGroupConfiguration = (System.Windows.Controls.Button)view.FindName("UseGroupConfigurationButton");
            var joinPreparationPanel = (Border)view.FindName("JoinPreparationPanel");
            if (useGroupConfiguration.Command?.CanExecute(useGroupConfiguration.CommandParameter) != true
                || joinPreparationPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Podsumowanie importu nie udostępnia jawnego przejścia do przygotowania lokalnego.");

            useGroupConfiguration.Command.Execute(useGroupConfiguration.CommandParameter);
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            view.UpdateLayout();
            var joinMissingPasswords = (ItemsControl)view.FindName("JoinMissingPasswordsList");
            string joinReadiness = ((TextBlock)view.FindName("JoinReadinessText")).Text;
            string joinPasswords = ((TextBlock)view.FindName("JoinPasswordsReadinessText")).Text;
            string joinOcr = ((TextBlock)view.FindName("JoinOcrReadinessText")).Text;
            // The invitation adds two machines with no local password; a globally disabled monitor never asks the store.
            bool joinReadinessMatchesMode = orchestrator.Config.Enabled
                ? joinReadiness == "Uzupełnij lokalne hasła VNC" && joinMissingPasswords.Items.Count == 2
                    && joinPasswords.Contains("brakuje 2 z 4")
                : joinReadiness.Contains("Włącz monitorowanie") && joinMissingPasswords.Items.Count == 0
                    && joinPasswords.Contains("nie są sprawdzane");
            if (fakeJoinApplies != 0 || !joinViewModel.IsJoinPreparationStep || joinViewModel.IsJoinSummaryStep
                || joinSummaryPanel.Visibility != Visibility.Collapsed
                || joinPreparationPanel.Visibility != Visibility.Visible
                || !joinReadinessMatchesMode
                || !joinOcr.StartsWith("OCR:", StringComparison.Ordinal)
                || joinPasswords.Contains(invitation.NetworkKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Użyj konfiguracji tej grupy nie pokazało bezpiecznego przygotowania lokalnego: "
                    + $"applies={fakeJoinApplies}, preparation={joinViewModel.IsJoinPreparationStep}, "
                    + $"summaryPanel={joinSummaryPanel.Visibility}, preparationPanel={joinPreparationPanel.Visibility}, "
                    + $"readiness='{joinReadiness}', passwords='{joinPasswords}', ocr='{joinOcr}', "
                    + $"missing={joinMissingPasswords.Items.Count}.");
            scroller.ScrollToEnd();
            view.UpdateLayout();
            Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", $"dolacz-grupe-przygotowanie{modeSuffix}.png"), 1220, 1000);
            scroller.ScrollToHome();

            var joinPasswordEntryPanel = (Border)view.FindName("JoinPasswordEntryPanel");
            var joinPasswordEntries = (ItemsControl)view.FindName("JoinPasswordEntriesList");
            var joinSharedPasswordPanel = (StackPanel)view.FindName("JoinSharedPasswordPanel");
            var joinSharedPasswordBox = (PasswordBox)view.FindName("JoinSharedPasswordBox");
            var joinSharedPasswordCheckBox = (System.Windows.Controls.CheckBox)view.FindName("JoinSharedPasswordCheckBox");
            var saveJoinPasswords = (System.Windows.Controls.Button)view.FindName("SaveJoinPasswordsButton");
            var applyJoin = (System.Windows.Controls.Button)view.FindName("ApplyJoinButton");
            var joinApplyStatusPanel = (Border)view.FindName("JoinApplyStatusPanel");
            // Incomplete local preparation must keep the explicit join locked, whatever the monitoring mode is.
            if (applyJoin.Command?.CanExecute(applyJoin.CommandParameter) != false
                || joinApplyStatusPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException(
                    $"Niekompletne przygotowanie nie zablokowało Dołącz i włącz: "
                    + $"panel={joinApplyStatusPanel.Visibility}.");
            if (!orchestrator.Config.Enabled)
            {
                // Globally disabled monitoring never asks the secret store, so there is nothing to fill in here.
                if (joinPasswordEntryPanel.Visibility != Visibility.Collapsed
                    || joinPasswordEntries.Items.Count != 0
                    || saveJoinPasswords.Command?.CanExecute(saveJoinPasswords.CommandParameter) != false
                    || applyJoin.Command?.CanExecute(applyJoin.CommandParameter) != false
                    || !((TextBlock)view.FindName("JoinApplyHintText")).Text.Contains("odblokuje się"))
                    throw new InvalidOperationException(
                        "Wyłączone monitorowanie nie może oferować pól haseł VNC, zapisu sekretu ani Dołącz i włącz.");
            }
            else
            {
                if (joinPasswordEntryPanel.Visibility != Visibility.Visible
                    || joinPasswordEntries.Items.Count != 2
                    || joinPasswordEntries.Visibility != Visibility.Visible
                    || joinSharedPasswordPanel.Visibility != Visibility.Collapsed
                    || joinSharedPasswordCheckBox.IsChecked != false
                    || saveJoinPasswords.Command?.CanExecute(saveJoinPasswords.CommandParameter) != false)
                    throw new InvalidOperationException(
                        $"Przygotowanie nie pokazało pustych pól haseł VNC bez możliwości zapisu: "
                        + $"panel={joinPasswordEntryPanel.Visibility}, pola={joinPasswordEntries.Items.Count}, "
                        + $"lista={joinPasswordEntries.Visibility}, wspolne={joinSharedPasswordPanel.Visibility}.");

                // One typed field at a time: the real PasswordBox from the row template feeds the view model.
                joinPasswordEntries.UpdateLayout();
                PasswordBox machinePasswordBox = RequireDescendant<PasswordBox>(
                    joinPasswordEntries.ItemContainerGenerator.ContainerFromIndex(0));
                machinePasswordBox.Password = "smoke-only-3";
                if (saveJoinPasswords.Command?.CanExecute(saveJoinPasswords.CommandParameter) != true)
                    throw new InvalidOperationException("Wpisane hasło nie odblokowało lokalnego zapisu.");

                saveJoinPasswords.Command.Execute(saveJoinPasswords.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                string savedStatus = ((TextBlock)view.FindName("JoinPasswordSaveStatusText")).Text;
                if (secrets.Get(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword("smoke-group-aidite3")) is null
                    || secrets.Get(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword("smoke-group-aidite4")) is not null
                    || joinPasswordEntries.Items.Count != 1
                    || joinMissingPasswords.Items.Count != 1
                    || ((TextBlock)view.FindName("JoinReadinessText")).Text != "Uzupełnij lokalne hasła VNC"
                    || !((TextBlock)view.FindName("JoinPasswordsReadinessText")).Text.Contains("brakuje 1 z 4")
                    || !savedStatus.Contains("Zapisano lokalnie hasła VNC (1)")
                    || savedStatus.Contains("smoke-only-3", StringComparison.Ordinal)
                    || joinViewModel.IsJoinPasswordSaveError
                    || fakeJoinApplies != 0)
                    throw new InvalidOperationException(
                        $"Zapis jednego hasła nie przeliczył tej samej gotowości bez zastosowania ustawień: "
                        + $"pola={joinPasswordEntries.Items.Count}, brakujace={joinMissingPasswords.Items.Count}, "
                        + $"status='{savedStatus}', applies={fakeJoinApplies}.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "dolacz-grupe-haslo-zapisane.png"), 1220, 1000);
                scroller.ScrollToHome();

                // The shared variant fills every machine still missing a password from a single field.
                joinSharedPasswordCheckBox.IsChecked = true;
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                view.UpdateLayout();
                if (!joinViewModel.UseSharedJoinPassword
                    || joinSharedPasswordPanel.Visibility != Visibility.Visible
                    || joinPasswordEntries.Visibility != Visibility.Collapsed
                    || saveJoinPasswords.Command?.CanExecute(saveJoinPasswords.CommandParameter) != false)
                    throw new InvalidOperationException(
                        "Wariant jednego wspólnego hasła nie przełączył pól ani nie wymagał wpisania hasła.");

                joinSharedPasswordBox.Password = "smoke-only-wspolne";
                if (saveJoinPasswords.Command?.CanExecute(saveJoinPasswords.CommandParameter) != true)
                    throw new InvalidOperationException("Wspólne hasło nie odblokowało lokalnego zapisu.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "dolacz-grupe-wspolne-haslo.png"), 1220, 1000);
                scroller.ScrollToHome();

                saveJoinPasswords.Command.Execute(saveJoinPasswords.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                string sharedStatus = ((TextBlock)view.FindName("JoinPasswordSaveStatusText")).Text;
                if (secrets.Get(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword("smoke-group-aidite4")) is null
                    || joinPasswordEntryPanel.Visibility != Visibility.Collapsed
                    || joinPasswordEntries.Items.Count != 0
                    || joinMissingPasswords.Items.Count != 0
                    || joinSharedPasswordBox.Password.Length != 0
                    || !((TextBlock)view.FindName("JoinPasswordsReadinessText")).Text.Contains("gotowe (4/4)")
                    || !sharedStatus.Contains("Zapisano lokalnie hasła VNC (1)")
                    || sharedStatus.Contains("smoke-only-wspolne", StringComparison.Ordinal)
                    || joinViewModel.IsJoinPasswordSaveError
                    || fakeJoinApplies != 0)
                    throw new InvalidOperationException(
                        $"Wspólne hasło nie domknęło lokalnej gotowości bez zapisu ustawień: "
                        + $"panel={joinPasswordEntryPanel.Visibility}, pola={joinPasswordEntries.Items.Count}, "
                        + $"brakujace={joinMissingPasswords.Items.Count}, status='{sharedStatus}', "
                        + $"applies={fakeJoinApplies}.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "dolacz-grupe-hasla-uzupelnione.png"), 1220, 1000);
                scroller.ScrollToHome();

                // Complete local preparation unlocks the one explicit join; UAC and the real transaction are faked.
                if (applyJoin.Command?.CanExecute(applyJoin.CommandParameter) != true
                    || !((TextBlock)view.FindName("JoinApplyHintText")).Text.Contains("Dołącz i włącz")
                    || joinApplyStatusPanel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException(
                        "Kompletne przygotowanie nie odblokowało jawnego Dołącz i włącz.");

                await joinViewModel.ApplyJoinCommand.ExecuteAsync(null);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                string joinApplyStatus = ((TextBlock)view.FindName("JoinApplyStatusText")).Text;
                string joinApplyDetail = ((TextBlock)view.FindName("JoinApplyDetailText")).Text;
                if (fakeJoinApplies != 1 || !joinViewModel.IsJoinApplyCompleted
                    || joinViewModel.IsJoinApplyError
                    || joinApplyStatusPanel.Visibility != Visibility.Visible
                    || joinApplyStatus != "Dołączono do grupy i włączono"
                    || ((TextBlock)view.FindName("SavedSettingText")).Text != "Włączona"
                    || ((TextBlock)view.FindName("SavedGroupText")).Text != "Grupa: Hala smoke import"
                    || ((TextBlock)view.FindName("RuntimeStateText")).Text != "Oczekiwanie na drugi komputer"
                    || applyJoin.Command?.CanExecute(applyJoin.CommandParameter) != false
                    || saveJoinPasswords.Command?.CanExecute(saveJoinPasswords.CommandParameter) != false
                    || joinApplyDetail.Contains(invitation.NetworkKey, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Dołącz i włącz nie pokazało bezpiecznego sukcesu i odświeżonych, rozdzielonych stanów: "
                        + $"applies={fakeJoinApplies}, panel={joinApplyStatusPanel.Visibility}, "
                        + $"status='{joinApplyStatus}'.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "dolacz-grupe-dolaczono.png"), 1220, 1000);
                scroller.ScrollToHome();

                // The separate, read-only network step. It must never apply settings again, so the faked apply
                // counter has to stay at one across both diagnostics runs.
                var checkGroupNetwork = (System.Windows.Controls.Button)view.FindName("CheckGroupNetworkButton");
                var joinDiagnosticsPanel = (Border)view.FindName("GroupDiagnosticsPanel");
                if (checkGroupNetwork.Command?.CanExecute(checkGroupNetwork.CommandParameter) != true
                    || joinDiagnosticsPanel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException(
                        "Dołączenie nie odblokowało osobnego kroku sprawdzenia sieci grupy.");

                checkGroupNetwork.Command.Execute(checkGroupNetwork.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                var groupDiagnosticsList = (ItemsControl)view.FindName("GroupDiagnosticsList");
                string diagnosticsSummary = ((TextBlock)view.FindName("GroupDiagnosticsSummaryText")).Text;
                string diagnosticsCheckedAt = ((TextBlock)view.FindName("GroupDiagnosticsCheckedAtText")).Text;
                if (joinDiagnosticsPanel.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Sprawdź sieć grupy nie otworzyło panelu diagnostyki.");
                GroupDiagnosticsRow waitingRow = joinViewModel.GroupDiagnosticsRows
                    .Single(row => row.Label == "Widoczność drugiego komputera");
                if (fakeJoinApplies != 1 || !joinViewModel.IsJoinDiagnosticsStep
                    || joinPreparationPanel.Visibility != Visibility.Collapsed
                    || groupDiagnosticsList.Items.Count != joinViewModel.GroupDiagnosticsRows.Count
                    || joinViewModel.GroupDiagnosticsRows.Count != 7
                    || waitingRow.Severity != GroupDiagnosticsSeverity.Waiting
                    || !joinViewModel.IsGroupDiagnosticsWarning
                    || !diagnosticsSummary.Contains("grupa czeka")
                    || !diagnosticsCheckedAt.Contains("Sprawdzono:")
                    || joinViewModel.GroupDiagnosticsRows
                        .Single(row => row.Label == "Zapora, sieć i odbiór na hali").Severity
                        != GroupDiagnosticsSeverity.NotChecked
                    || joinViewModel.GroupDiagnosticsRows.Any(row =>
                        row.Detail.Contains(invitation.NetworkKey, StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "Diagnostyka sieci nie pokazała bezpiecznego, tylko odczytanego stanu oczekiwania: "
                        + $"applies={fakeJoinApplies}, rows={joinViewModel.GroupDiagnosticsRows.Count}, "
                        + $"summary='{diagnosticsSummary}'.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "siec-grupy-czeka.png"), 1220, 1000);
                scroller.ScrollToHome();

                // Only the injected status source moves; the real runtime is untouched, as the final check proves.
                joinStatus = joinStatus with { VisibleComputers = 2, IsWatcher = true };
                var runGroupDiagnostics = (System.Windows.Controls.Button)view.FindName("RunGroupDiagnosticsButton");
                runGroupDiagnostics.Command!.Execute(runGroupDiagnostics.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                if (fakeJoinApplies != 1 || joinViewModel.IsGroupDiagnosticsWarning
                    || ((TextBlock)view.FindName("GroupDiagnosticsSummaryText")).Text != "Grupa działa na tym komputerze"
                    || ((TextBlock)view.FindName("RuntimeStateText")).Text != "Połączono"
                    || joinViewModel.GroupDiagnosticsRows
                        .Single(row => row.Label == "Widoczność drugiego komputera").Result != "Widoczne komputery: 2")
                    throw new InvalidOperationException(
                        "Ponowne sprawdzenie nie odczytało nowego stanu runtime albo zastosowało ustawienia: "
                        + $"applies={fakeJoinApplies}.");
                scroller.ScrollToEnd();
                view.UpdateLayout();
                Render(admin, Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui", "siec-grupy-dziala.png"), 1220, 1000);
                scroller.ScrollToHome();

                var backFromGroupDiagnostics = (System.Windows.Controls.Button)view.FindName("BackFromGroupDiagnosticsButton");
                backFromGroupDiagnostics.Command!.Execute(backFromGroupDiagnostics.CommandParameter);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                if (!joinViewModel.IsJoinPreparationStep || joinDiagnosticsPanel.Visibility != Visibility.Collapsed
                    || joinPreparationPanel.Visibility != Visibility.Visible
                    || !joinViewModel.IsJoinApplyCompleted || fakeJoinApplies != 1)
                    throw new InvalidOperationException(
                        "Wstecz z diagnostyki nie wróciło do przygotowania bez ponownego zastosowania ustawień.");
            }

            var backFromJoinPreparation = (System.Windows.Controls.Button)view.FindName("BackFromJoinPreparationButton");
            backFromJoinPreparation.Command!.Execute(backFromJoinPreparation.CommandParameter);
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            view.UpdateLayout();
            int expectedJoinApplies = orchestrator.Config.Enabled ? 1 : 0;
            if (joinViewModel.GroupDiagnosticsRows.Count != 0
                || joinViewModel.CurrentJoinProposal is not null || !joinViewModel.IsJoinFileStep
                || joinPreparationPanel.Visibility != Visibility.Collapsed
                || joinFilePanel.Visibility != Visibility.Visible
                || !joinViewModel.JoinImportStatusText.Contains("Porzucono wczytane zaproszenie")
                || joinViewModel.JoinMissingPasswordMachines.Count != 0
                || joinViewModel.IsJoinApplyCompleted
                || joinViewModel.JoinApplyStatusText.Length != 0
                || fakeJoinApplies != expectedJoinApplies)
                throw new InvalidOperationException(
                    "Wstecz z przygotowania nie porzuciło prywatnej propozycji importu ani statusu dołączenia.");

            if (orchestrator.Config.Cluster.Enabled != savedEnabled
                || orchestrator.Cluster.NetworkActive != networkActive)
                throw new InvalidOperationException(
                    "Podgląd importu ani zaatrapowane Dołącz i włącz nie mogą zapisywać ani przełączać prawdziwego runtime.");
            view.DataContext = viewModel;
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "multi-computer-wizard.txt"),
                "PASS: saved setting and live runtime are separate; Create validates local readiness, freezes an in-memory profile proposal, shows confirmation, and applies exactly once through an injected no-UAC operation; only then does the separate export step appear, warning that the file is a secret and that Back drops it, and one click writes exactly one real, re-readable invitation through an injected file dialog without quoting the network key or applying settings again; the very same read-only network step is offered after Create as well, hiding the confirmation and export panels while it is open, applying nothing, and giving both of them back with the invitation still exportable; Join reads an injected invitation without a system dialog and shows a frozen name, machine list and local profile differences without applying; Use this group's configuration opens local preparation only, naming the invited machines that have no local VNC password and the OCR limitation; real PasswordBox rows and the one-shared-password variant write only to the local secret store, recompute the very same WatcherReadiness right away, never echo a typed password into status text and drop it from memory afterwards; the explicit Join and enable stays locked until that local readiness is complete and then runs exactly once through an injected no-UAC operation, refreshing the separate saved and runtime states; Back drops the private invitation proposal and the join status; the separate network step then only reads the runtime, reports waiting, a working group and what it did not check, and can be repeated without applying settings again. No AppConfig write, UAC, firewall, real file written to disk, real network probe or real runtime change was invoked by smoke.");
        }
        finally
        {
            // Includes the invitation-only machines whose passwords the local preparation step just saved.
            foreach (string id in smokeMachines.Select(machine => machine.Id)
                .Concat(["smoke-group-aidite3", "smoke-group-aidite4"]))
            {
                secrets.Remove(MaszynyAlert.Core.Abstractions.SecretKeys.VncPassword(id));
            }
            orchestrator.Config.Machines.Clear();
            orchestrator.Config.Machines.AddRange(originalMachines);
            view.DataContext = viewModel;
            viewModel.Refresh();
        }
    }

    internal static async Task VerifyAlertDismissalAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        var state = services.GetRequiredService<AlertStateMachine>();
        var preview = services.GetRequiredService<OperatorWindow>();
        var alarm = services.GetRequiredService<AlertWindow>();
        var idle = services.GetRequiredService<IdlePopupWindow>();
        var screen = services.GetRequiredService<ScreenPopupWindow>();
        var cards = (ItemsControl)preview.FindName("AlertCards");
        async Task Flush() => await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        foreach (var test in orchestrator.VisibleAlerts.Where(a => a.MachineId == "test"))
            await orchestrator.AcknowledgeAsync(test.Key);
        preview.Show();
        var machine = new MaszynyAlert.Core.Config.MachineConfig
            { Id = "ui-dismissal", Name = "TEST UI", Host = "unused", Profile = "opencnc" };
        var other = machine with { Id = "ui-dismissal-other" };
        state.OnMachineOffline(machine, DateTimeOffset.UtcNow.AddHours(-1));
        state.OnMachineOffline(other, DateTimeOffset.UtcNow.AddHours(-1));
        state.Tick();
        await Flush();
        Check(cards.Items.Count == 2 && alarm.IsVisible, "Brak alertów testowych w Podglądzie/oknie alarmu.");
        var item = cards.Items.Cast<AlertItemViewModel>().Single(a => a.Key == AlertKey.Offline(machine.Id));
        await item.AcknowledgeCommand.ExecuteAsync(null);
        await Flush();
        Check(cards.Items.Count == 1 && alarm.IsVisible, "ACK musi usunąć tylko potwierdzony wpis z Do sprawdzenia.");
        Check(state.ActiveAlerts.Any(a => a.Key == item.Key && a.Status == AlertStatus.Acknowledged),
            "ACK nie może kasować trwającego epizodu.");
        var technical = services.GetRequiredService<StatusWindowViewModel>();
        technical.Refresh(DateTimeOffset.UtcNow);
        Check(technical.ActiveAlerts.Any(a => a.Key == item.Key && a.IsAcknowledged), "Brak potwierdzenia w Podglądzie technicznym.");
        // Wymuś migawkę sprzed zamknięcia, niezależnie od fazy sekundowego tiku.
        orchestrator.Cluster.SetLocalState(state.ActiveAlerts, orchestrator.Supervisor.Snapshots);
        state.OnMachineOnline(other.Id);
        Check(!orchestrator.VisibleAlerts.Any(a => a.Key == AlertKey.Offline(other.Id)),
            "Zamknięty alert wrócił ze starej migawki klastra w trybie jednego komputera.");
        await Flush();
        Check(cards.Items.Count == 0 && !alarm.IsVisible, "Po ustąpieniu ostatniego alertu okno musi zniknąć.");
        state.OnMachineOnline(machine.Id);

        var idleCards = (ItemsControl)preview.FindName("IdleCards");
        var noIdle = (TextBlock)preview.FindName("NoIdle");
        Check(idleCards.Items.Count == 0 && noIdle.Visibility == Visibility.Visible,
            "Sekcja „Maszyny bez pracy” pokazuje pozycje, choć żadna maszyna nie stoi.");

        orchestrator.TestIdlePopup();
        await Flush();
        // postój ma być widoczny także w Podglądzie, nie tylko w znikającym pop-upie.
        Check(idleCards.Items.Count == 1 && noIdle.Visibility == Visibility.Collapsed,
            "Postój nie pojawił się w sekcji „Maszyny bez pracy” Podglądu.");
        // Zasięg skasowania musi być napisany wprost przy liście: w trybie jednego komputera to ostrzeżenie „tylko tutaj”.
        var idleViewModel = (IdlePopupViewModel)idle.DataContext;
        Check(!string.IsNullOrWhiteSpace(idleViewModel.ScopeNote), "Pop-up postoju nie mówi, dokąd sięga skasowanie.");
        Check(orchestrator.Cluster.Configuration.Enabled || idleViewModel.IsScopeWarning,
            "Bez grupy pop-up postoju nie ostrzega, że skasowanie działa tylko na tym komputerze.");
        Check(idleViewModel.Items.All(i => !string.IsNullOrWhiteSpace(i.DismissHint)),
            "Przycisk skasowania postoju nie ma podpowiedzi o zasięgu.");
        idle.ShowPinned();
        Check(idle.IsVisible, "Brak przypiętego powiadomienia postoju.");
        var close = (System.Windows.Controls.Button)idle.FindName("CloseButton");
        idle.UpdateLayout();
        var label = FindDescendant<ContentPresenter>(close);
        Check(label is { ActualWidth: > 0, ActualHeight: > 0 }, "Krzyżyk postoju jest przycięty przez padding przycisku.");
        await ((IdlePopupViewModel)idle.DataContext).Items.Single().DismissCommand.ExecuteAsync(null);
        await Flush();
        Check(!idle.IsVisible, "Przypięte okno postoju zostało po skasowaniu ostatniego wpisu.");
        Check(idleCards.Items.Count == 0 && noIdle.Visibility == Visibility.Visible,
            "Skasowany postój został w sekcji „Maszyny bez pracy” Podglądu.");
        orchestrator.TestIdlePopup();
        await Flush();
        Check(idle.IsVisible, "Nowe przypomnienie postoju nie pokazało się ponownie.");
        int popupSeconds = orchestrator.Config.Timing.IdlePopupSeconds;
        await Task.Delay(TimeSpan.FromSeconds((popupSeconds <= 0 ? 3 : popupSeconds) + 1));
        await Flush();
        Check(orchestrator.IdleAlerts.Count == 1, "Pop-up postoju skasował epizod bez udziału operatora.");
        Check(popupSeconds <= 0 ? idle.IsVisible : !idle.IsVisible,
            popupSeconds <= 0
                ? "Pop-up postoju schował się sam, mimo idlePopupSeconds = 0 (ma czekać na skasowanie)."
                : "Auto-ukrywanie pop-upu postoju nie działa.");
        await orchestrator.DismissIdleAlertAsync(orchestrator.IdleAlerts.Single().Key);
        await Flush();
        Check(!idle.IsVisible, "Pop-up postoju został po skasowaniu ostatniego wpisu.");

        orchestrator.TestWrongScreenPopup();
        await Flush();
        screen.ShowPinned();
        Check(screen.IsVisible, "Brak przypiętego ostrzeżenia HMI.");
        await orchestrator.DismissWrongScreenAlertAsync(orchestrator.WrongScreenAlerts.Single().Key);
        await Flush();
        Check(!screen.IsVisible, "Przypięte okno HMI zostało po skasowaniu ostatniego wpisu.");
        preview.Hide();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "alert-dismissal.txt"),
            "PASS: ACK removes only the pending operator task; technical acknowledgement retained; resolved alerts hide; pinned popups hide when emptied; next idle popup auto-hides; close glyph is visible.");
    }

    internal static async Task VerifyPopupLayoutAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        var state = services.GetRequiredService<AlertStateMachine>();
        var alarm = services.GetRequiredService<AlertWindow>();
        var idle = services.GetRequiredService<IdlePopupWindow>();
        var screen = services.GetRequiredService<ScreenPopupWindow>();
        async Task Flush() => await alarm.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static Rect Bounds(Window w) => new(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
        static bool Overlap(Rect a, Rect b)
        {
            Rect common = Rect.Intersect(a, b);
            return !common.IsEmpty && common.Width > 0.5 && common.Height > 0.5;
        }

        var machines = Enumerable.Range(1, 4).Select(i => new MaszynyAlert.Core.Config.MachineConfig
            { Id = $"ui-layout-{i}", Name = $"TEST UKŁAD {i}", Host = "unused", Profile = "opencnc" }).ToArray();
        for (int i = 0; i < 3; i++) state.OnMachineOffline(machines[i], DateTimeOffset.UtcNow.AddHours(-3 + i));
        state.Tick();
        orchestrator.TestIdlePopup();
        orchestrator.TestWrongScreenPopup();
        screen.ShowPinned();
        idle.ShowPinned();
        await Flush();
        alarm.UpdateLayout(); idle.UpdateLayout(); screen.UpdateLayout();
        await Flush();
        Check(alarm.IsVisible && idle.IsVisible && screen.IsVisible, "Okna alertu, postoju i ekranu HMI mają być widoczne naraz.");
        Check(!Overlap(Bounds(idle), Bounds(screen)),
            $"Okno postoju {Bounds(idle)} nachodzi na okno ekranu HMI {Bounds(screen)}.");
        Check(!Overlap(Bounds(idle), Bounds(alarm)),
            $"Okno postoju {Bounds(idle)} nachodzi na okno alertu {Bounds(alarm)}.");

        var scroll = (ScrollViewer)alarm.FindName("AlertsScroll");
        scroll.ScrollToBottom();
        state.OnMachineOffline(machines[3], DateTimeOffset.UtcNow.AddMinutes(-30));
        state.Tick();
        // Dwa razy: odświeżenie okna alertu kolejkuje przewinięcie na górę dopiero po przebudowie listy.
        await Flush();
        await Flush();
        var first = ((AlertWindowViewModel)alarm.DataContext).Items.FirstOrDefault();
        Check(first?.Key == AlertKey.Offline(machines[3].Id), $"Nowy alarm nie jest pierwszy na liście okna alertu (pierwszy: {first?.Line1}).");
        Check(scroll.VerticalOffset <= 0.5, $"Lista okna alertu nie wróciła na górę do nowego alarmu (pozycja {scroll.VerticalOffset:F0}).");

        foreach (var machine in machines)
        {
            await orchestrator.AcknowledgeAsync(AlertKey.Offline(machine.Id));
            state.OnMachineOnline(machine.Id);
        }
        foreach (var key in orchestrator.IdleAlerts.Select(a => a.Key).ToArray()) await orchestrator.DismissIdleAlertAsync(key);
        foreach (var key in orchestrator.WrongScreenAlerts.Select(a => a.Key).ToArray()) await orchestrator.DismissWrongScreenAlertAsync(key);
        state.Tick();
        await Flush();
        Check(!alarm.IsVisible && !idle.IsVisible && !screen.IsVisible, "Okna testu układu zostały po sprzątnięciu alertów.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "popup-layout.txt"),
            "PASS: idle popup overlaps neither the HMI screen popup nor the alert window; newest alarm is first and the list scrolls to the top.");
    }

    internal static async Task VerifyAlertHelpPlacementAsync(IServiceProvider services)
    {
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        var state = services.GetRequiredService<AlertStateMachine>();
        var alarm = services.GetRequiredService<AlertWindow>();
        async Task Flush() => await alarm.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var machine = new MaszynyAlert.Core.Config.MachineConfig
            { Id = "ui-help-placement", Name = "TEST POMOC", Host = "unused", Profile = "opencnc" };
        state.OnMachineOffline(machine, DateTimeOffset.UtcNow.AddHours(-1));
        state.Tick();
        await Flush();
        alarm.UpdateLayout();
        Check(alarm.IsVisible, "Okno alertu nie pokazało się dla testu pozycji rozwiniętej pomocy.");

        Rect workArea = WindowPlacement.WorkAreaFor(alarm);
        const double margin = WindowPlacement.DefaultEdgeMargin;
        // Tolerancja 0,5 px na zaokrąglenia przy przeliczaniu pikseli urządzenia na jednostki niezależne od DPI.
        const double tolerance = 0.5;
        Check(alarm.MaxHeight <= workArea.Height - (2 * margin) + tolerance,
            $"Okno alertu może być wyższe niż obszar roboczy (MaxHeight {alarm.MaxHeight}, obszar {workArea.Height}).");

        // Operator przeciąga okno tak, że jego dół dotyka dolnego marginesu, a potem rozwija pomoc.
        double left = workArea.Left + 40;
        alarm.TestUserMove(left, workArea.Bottom - alarm.ActualHeight - margin);
        await Flush();
        var expander = RequireDescendant<Expander>((DependencyObject)alarm.Content);
        expander.IsExpanded = true;
        alarm.UpdateLayout();
        await Flush();

        Check(alarm.ActualHeight > 0, "Okno alertu nie ma policzonej wysokości — test pozycji nic nie sprawdza.");
        Check(alarm.Top + alarm.ActualHeight <= workArea.Bottom - margin + tolerance,
            $"Rozwinięta pomoc wychodzi poza dół obszaru roboczego (Top {alarm.Top}, wysokość {alarm.ActualHeight}, dół {workArea.Bottom}).");
        Check(alarm.Top >= workArea.Top + margin - tolerance,
            $"Korekta wypchnęła okno alertu ponad górną krawędź obszaru roboczego (Top {alarm.Top}, góra {workArea.Top}).");
        Check(Math.Abs(alarm.Left - left) <= tolerance,
            $"Korekta pionowa nie może przesuwać okna w poziomie (Left {alarm.Left}, oczekiwano {left}).");

        expander.IsExpanded = false;
        await orchestrator.AcknowledgeAsync(AlertKey.Offline(machine.Id));
        await Flush();
        Check(!alarm.IsVisible, "Okno alertu zostało po potwierdzeniu testowego alertu pozycji pomocy.");
        state.OnMachineOnline(machine.Id);
        state.Tick();
        await Flush();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "alert-help-placement.txt"),
            "PASS: expanded help stays inside the work area of its monitor after the window was dragged to the bottom edge.");
    }

    internal static async Task VerifyOverlayAsync(IServiceProvider services)
    {
        var overlay = services.GetRequiredService<OverlayWindow>();
        var preview = services.GetRequiredService<OperatorWindow>();
        var store = services.GetRequiredService<MaszynyAlert.Core.Config.OverlaySettingsStore>();
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output");
        Directory.CreateDirectory(Path.Combine(output, "ui"));
        async Task Flush() => await overlay.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        overlay.SetEnabled(true);
        await Flush();
        Check(overlay.IsVisible, "Nakładka statusu nie pokazała się po włączeniu z menu zasobnika.");
        Check(!overlay.ShowInTaskbar && overlay.Topmost, "Nakładka musi być nad innymi oknami i poza paskiem zadań.");
        Check(new OverlaySettingsStore(store.Path).Load().Enabled == true,
            "Włączenie nakładki nie zostało zapisane w profilu użytkownika.");

        // Kafelki z pełnym zestawem stanów — te same teksty i kolory, co karty w Podglądzie.
        var tiles = new[]
        {
            SampleCard("aidite1", "Aidite 1", MaszynyAlert.Core.Machines.MachineCardTone.Error, "Błąd maszyny", "Błąd", "Servo 6", "!", "12 %"),
            SampleCard("aidite2", "Aidite 2", MaszynyAlert.Core.Machines.MachineCardTone.Warning, "Sprawdzane", "Sprawdzane", "Zgłoszono, ktoś sprawdza — od 14:05.", "…", "12 %"),
            SampleCard("aidite3", "Aidite 3", MaszynyAlert.Core.Machines.MachineCardTone.Warning, "Stoi bez pracy (100 %)", "Stoi 100 %", "Od 14:02 — dołóż prace.", "＋", "100 %"),
            SampleCard("aidite4", "Aidite 4", MaszynyAlert.Core.Machines.MachineCardTone.Warning, "Stoi na 0 %", "Stoi 0 %", "Od 14:09 — wciśnij Start.", "▶", "0 %"),
            SampleCard("aidite5", "Aidite 5", MaszynyAlert.Core.Machines.MachineCardTone.Warning, "Przełącz ekran maszyny", "Zły ekran", "Wróć do widoku z komunikatami.", "!", "—"),
            SampleCard("aidite6", "Aidite 6", MaszynyAlert.Core.Machines.MachineCardTone.Ok, MaszynyAlert.Core.Machines.MachineCardState.WatchedState,
                MaszynyAlert.Core.Machines.MachineCardState.WatchedState, "Postęp pracy: 66%", "✓", "66 %"),
            SampleCard("aidite7", "Aidite 7", MaszynyAlert.Core.Machines.MachineCardTone.Ok, MaszynyAlert.Core.Machines.MachineCardState.WatchedState,
                MaszynyAlert.Core.Machines.MachineCardState.WatchedState, "Postęp pracy: 41%", "✓", "41 %"),
        };
        overlay.Tiles.ItemsSource = tiles;
        overlay.UpdateLayout();
        await Flush();
        Render(overlay, Path.Combine(output, "ui", "nakladka.png"), 1220, 140);

        Check(overlay.Layout == MaszynyAlert.Core.Config.OverlayLayout.OneRow,
            "Przed zmianą układu nakładka ma mieć układ domyślny (jeden wiersz).");
        Rect TileRect(int index)
        {
            var container = (FrameworkElement)overlay.Tiles.ItemContainerGenerator.ContainerFromIndex(index)
                ?? throw new InvalidOperationException($"Brak kontenera kafelka nr {index}.");
            return container.TransformToAncestor(overlay.Tiles).TransformBounds(new Rect(container.RenderSize));
        }
        void CheckGrid(MaszynyAlert.Core.Config.OverlayLayout layout, MachineCardViewModel[] cards, int rows, int columns)
        {
            overlay.SetLayout(layout);
            overlay.Tiles.ItemsSource = cards;
            overlay.ApplyLayout();
            overlay.UpdateLayout();
            Check(overlay.Layout == layout, $"Nakładka nie przyjęła układu {layout}.");
            var panel = System.Windows.Media.VisualTreeHelper.GetParent(overlay.Tiles.ItemContainerGenerator.ContainerFromIndex(0))
                as System.Windows.Controls.Primitives.UniformGrid;
            Check(panel is not null, $"Kafelki w układzie {layout} nie leżą w UniformGrid.");
            Rect workArea = WindowPlacement.WorkAreaFor(overlay);
            (int fitRows, int fitColumns) = MaszynyAlert.Core.Config.OverlayLayouts.FitGrid(layout, cards.Length,
                MaszynyAlert.Core.Config.OverlayLayouts.MaxColumns(workArea.Width, overlay.Zoom.ScaleX));
            if ((fitRows, fitColumns) != MaszynyAlert.Core.Config.OverlayLayouts.FixedGrid(layout))
            {
                Check(panel!.Rows == fitRows && panel.Columns == fitColumns,
                    $"Układ {layout} na ekranie {workArea.Width:0} px ma {panel.Rows}×{panel.Columns} zamiast zawiniętego {fitRows}×{fitColumns}.");
                Check(overlay.ActualWidth <= workArea.Width + 1,
                    $"Nakładka w układzie {layout} ma {overlay.ActualWidth:0} px przy obszarze roboczym {workArea.Width:0} px.");
                Check(new OverlaySettingsStore(store.Path).Load().Layout == layout,
                    $"Układ {layout} nie został zapisany w profilu użytkownika.");
                return;
            }
            (int fixedRows, int fixedColumns) = MaszynyAlert.Core.Config.OverlayLayouts.FixedGrid(layout);
            Check(panel!.Rows == fixedRows && panel.Columns == fixedColumns,
                $"Siatka układu {layout} ma {panel.Rows}×{panel.Columns} zamiast {fixedRows}×{fixedColumns}.");
            Check(MaszynyAlert.Core.Config.OverlayLayouts.Shape(layout, cards.Length) == (rows, columns),
                $"Układ {layout} przy {cards.Length} kafelkach ma dać {rows} wierszy i {columns} kolumn.");
            Rect first = TileRect(0);
            if (columns > 1)
            {
                Rect right = TileRect(1);
                Check(Math.Abs(right.Top - first.Top) < 0.5 && right.Left > first.Right - 0.5,
                    $"Układ {layout}: drugi kafelek ma leżeć na prawo od pierwszego, w tym samym wierszu.");
            }
            if (rows > 1)
            {
                Rect below = TileRect(columns);
                Check(Math.Abs(below.Left - first.Left) < 0.5 && below.Top > first.Bottom - 0.5,
                    $"Układ {layout}: kafelek nr {columns} ma leżeć pod pierwszym, w tej samej kolumnie.");
            }
            if (columns > 1 && rows > 1)
            {
                Rect lastInRow = TileRect(columns - 1);
                Check(Math.Abs(lastInRow.Top - first.Top) < 0.5, $"Układ {layout}: ostatni kafelek pierwszego wiersza ma leżeć w pierwszym wierszu.");
            }
            Check(new OverlaySettingsStore(store.Path).Load().Layout == layout,
                $"Układ {layout} nie został zapisany w profilu użytkownika.");
        }

        // „2 wiersze po 3 maszyny jeden wiersz nad drugim” — sześć maszyn.
        CheckGrid(MaszynyAlert.Core.Config.OverlayLayout.TwoRows, tiles.Take(6).ToArray(), rows: 2, columns: 3);
        Render(overlay, Path.Combine(output, "ui", "nakladka-dwa-wiersze.png"), 560, 220);
        CheckGrid(MaszynyAlert.Core.Config.OverlayLayout.OneColumn, tiles.Take(3).ToArray(), rows: 3, columns: 1);
        Render(overlay, Path.Combine(output, "ui", "nakladka-kolumna.png"), 300, 300);
        CheckGrid(MaszynyAlert.Core.Config.OverlayLayout.ThreeColumns, tiles, rows: 3, columns: 3);
        Render(overlay, Path.Combine(output, "ui", "nakladka-trzy-kolumny.png"), 560, 300);
        // Z powrotem do układu domyślnego: oba przebiegi smoke dzielą katalog, a kolejny start ma zastać
        // w overlay.json układ domyślny (tak jak zostawiamy „wyłączona” niżej).
        CheckGrid(MaszynyAlert.Core.Config.OverlayLayout.OneRow, tiles, rows: 1, columns: 7);
        overlay.Tiles.ItemsSource = tiles;
        overlay.UpdateLayout();

        var layoutButton = (System.Windows.Controls.Button)overlay.FindName("LayoutButton");
        var dragHandle = (Border)overlay.FindName("DragHandle");
        var layoutMenu = (System.Windows.Controls.ContextMenu)overlay.FindName("LayoutMenu");
        var root = (FrameworkElement)overlay.Content;
        Check(layoutButton.Visibility == Visibility.Visible, "Przycisk układu kafelków ma być widoczny, gdy nakładka łapie mysz.");
        Rect handleRect = dragHandle.TransformToAncestor(root).TransformBounds(new Rect(dragHandle.RenderSize));
        Rect buttonRect = layoutButton.TransformToAncestor(root).TransformBounds(new Rect(layoutButton.RenderSize));
        Check(buttonRect.Top >= handleRect.Bottom - 0.5 && Math.Abs(buttonRect.Left - handleRect.Left) < 0.5,
            $"Przycisk układu ma leżeć pod uchwytem przeciągania (uchwyt {handleRect}, przycisk {buttonRect}).");
        Check(buttonRect.Width > 0 && buttonRect.Height > 0, "Przycisk układu kafelków nie ma rozmiaru.");
        string[] labels = MaszynyAlert.Core.Config.OverlayLayouts.All.Select(MaszynyAlert.Core.Config.OverlayLayouts.Label).ToArray();
        Check(layoutMenu.Items.Cast<System.Windows.Controls.MenuItem>().Select(i => i.Header as string).SequenceEqual(labels),
            "Menu układu na nakładce ma mieć te same pozycje, co menu zasobnika (OverlayLayouts.All).");
        overlay.RefreshLayoutMenuChecks();
        Check(layoutMenu.Items.Cast<System.Windows.Controls.MenuItem>().Count(i => i.IsChecked) == 1
              && ((System.Windows.Controls.MenuItem)layoutMenu.Items[0]).IsChecked,
            "Ptaszek w menu nakładki ma stać przy bieżącym układzie (jeden wiersz).");
        ((System.Windows.Controls.MenuItem)layoutMenu.Items[1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Check(overlay.Layout == MaszynyAlert.Core.Config.OverlayLayout.TwoRows
              && new OverlaySettingsStore(store.Path).Load().Layout == MaszynyAlert.Core.Config.OverlayLayout.TwoRows,
            "Kliknięcie pozycji w menu nakładki nie zmieniło układu na dwa wiersze.");
        overlay.RefreshLayoutMenuChecks();
        Check(((System.Windows.Controls.MenuItem)layoutMenu.Items[1]).IsChecked && !((System.Windows.Controls.MenuItem)layoutMenu.Items[0]).IsChecked,
            "Ptaszek w menu nakładki nie przeszedł na „Dwa wiersze”.");
        ((System.Windows.Controls.MenuItem)layoutMenu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Check(overlay.Layout == MaszynyAlert.Core.Config.OverlayLayout.OneRow, "Powrót do jednego wiersza z menu nakładki nie zadziałał.");
        overlay.Tiles.ItemsSource = tiles;
        overlay.UpdateLayout();
        overlay.SetClickThrough(true);
        Check(layoutButton.Visibility == Visibility.Collapsed, "Przy przenikaniu kliknięć przycisk układu ma zniknąć.");
        overlay.SetClickThrough(false);
        Check(layoutButton.Visibility == Visibility.Visible && !overlay.ClickThrough, "Po wyłączeniu przenikania przycisk układu ma wrócić.");
        Render(overlay, Path.Combine(output, "ui", "nakladka-przycisk-ukladu.png"), 1220, 140);

        preview.Hide();
        overlay.Tiles.ItemsSource = tiles;
        overlay.UpdateLayout();
        var chosen = RequireDescendant<System.Windows.Controls.Button>(
            overlay.Tiles.ItemContainerGenerator.ContainerFromIndex(3));
        Check((string?)chosen.Tag == "aidite4", "Kafelek nakładki nie niesie identyfikatora maszyny.");
        chosen.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Flush();
        Check(preview.IsVisible, "Kliknięcie kafelka nakładki nie otworzyło Podglądu.");
        preview.Hide();

        overlay.SetEnabled(false);
        await Flush();
        Check(!overlay.IsVisible, "Nakładka statusu została na ekranie po wyłączeniu.");
        Check(new OverlaySettingsStore(store.Path).Load().Enabled == false,
            "Wyłączenie nakładki nie zostało zapisane w profilu użytkownika.");

        File.WriteAllText(Path.Combine(output, "overlay.txt"),
            "PASS: status overlay shows/hides from the tray, stores the operator choice in the user profile, lays tiles out in one row / two rows / one column / three columns, offers the same layouts from a button under its drag handle (hidden while click-through is on), and a tile click opens the operator preview.");
    }

    /// <summary>Przykładowy kafelek nakładki/karta Podglądu — wyłącznie do zrzutów testu wyglądu.</summary>
    private static MachineCardViewModel SampleCard(
        string id, string name, MaszynyAlert.Core.Machines.MachineCardTone tone,
        string state, string shortState, string detail, string symbol, string progress) =>
        new(id, name, state, detail, MachineCardViewModel.AccentFor(tone), symbol, tone, shortState, progress);

    internal static void Capture(IServiceProvider services)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui");
        Directory.CreateDirectory(output);
        var operatorWindow = services.GetRequiredService<OperatorWindow>();
        ((TextBlock)operatorWindow.FindName("Summary")).Text = "4 z 7 maszyn wymaga uwagi";
        ((TextBlock)operatorWindow.FindName("SummaryDetail")).Text = "Dane przykładowe • test wyglądu, bez połączeń z maszynami";
        // Pięć stanów karty na jednym zrzucie: błąd, zgłoszony błąd („Sprawdzane”), oba postoje i zwykła praca.
        ((ItemsControl)operatorWindow.FindName("MachineCards")).ItemsSource = Enumerable.Range(1, 7).Select(n => n switch
        {
            2 => SampleCard("aidite2", "Aidite 2", MaszynyAlert.Core.Machines.MachineCardTone.Error,
                "Błąd maszyny", "Błąd", "Servo 6", "!", "12 %"),
            3 => SampleCard("aidite3", "Aidite 3", MaszynyAlert.Core.Machines.MachineCardTone.Warning,
                "Sprawdzane", "Sprawdzane", "Zgłoszono, ktoś sprawdza — od 14:05. Nie udało się pobrać tacki", "…", "31 %"),
            5 => SampleCard("aidite5", "Aidite 5", MaszynyAlert.Core.Machines.MachineCardTone.Warning,
                "Stoi bez pracy (100 %)", "Stoi 100 %", "Od 14:02 — dołóż prace. Maszyna czeka na kolejne zadanie.", "＋", "100 %"),
            6 => SampleCard("aidite6", "Aidite 6", MaszynyAlert.Core.Machines.MachineCardTone.Warning,
                "Stoi na 0 %", "Stoi 0 %", "Od 14:09 — wciśnij Start. Praca jest wczytana, ale maszyna jej nie zaczęła.", "▶", "0 %"),
            _ => SampleCard($"aidite{n}", $"Aidite {n}", MaszynyAlert.Core.Machines.MachineCardTone.Ok,
                MaszynyAlert.Core.Machines.MachineCardState.WatchedState,
                MaszynyAlert.Core.Machines.MachineCardState.WatchedState,
                $"Postęp pracy: {n * 11}%", "✓", $"{n * 11} %"),
        }).ToArray();
        var alert = new Alert
        {
            Key = AlertKey.ForRule("aidite2", "code-705"), MachineId = "aidite2", MachineName = "Aidite 2",
            ShortText = "Servo 6", Category = "restart", Kind = AlertKind.Error, Sound = true,
            StartedAt = DateTimeOffset.Now.AddMinutes(-7), LastSeenAt = DateTimeOffset.Now
        };
        ((ItemsControl)operatorWindow.FindName("AlertCards")).ItemsSource = new[] { new AlertItemViewModel(alert, _ => Task.CompletedTask) };
        ((TextBlock)operatorWindow.FindName("NoAlerts")).Visibility = Visibility.Collapsed;
        var idleSample = new Alert
        {
            Key = AlertKey.Idle("aidite5"), MachineId = "aidite5", MachineName = "Aidite 5",
            ShortText = "Dołóż kolejne prace", Category = "postoj", Kind = AlertKind.MachineIdle, Sound = false,
            StartedAt = DateTimeOffset.Now.AddMinutes(-12), LastSeenAt = DateTimeOffset.Now
        };
        var idleAtZeroSample = new Alert
        {
            Key = AlertKey.Idle("aidite6"), MachineId = "aidite6", MachineName = "Aidite 6",
            ShortText = AlertStateMachine.MachineIdleAtZeroShortText, Category = AlertStateMachine.IdleAtZeroCategory,
            Kind = AlertKind.MachineIdle, Sound = false,
            StartedAt = DateTimeOffset.Now.AddMinutes(-5), LastSeenAt = DateTimeOffset.Now
        };
        var scope = services.GetRequiredService<AppOrchestrator>();
        ((ItemsControl)operatorWindow.FindName("IdleCards")).ItemsSource = new[]
        {
            new IdleAlertItemViewModel(idleSample, _ => Task.CompletedTask, scope.DismissButtonHint),
            new IdleAlertItemViewModel(idleAtZeroSample, _ => Task.CompletedTask, scope.DismissButtonHint),
        };
        ((TextBlock)operatorWindow.FindName("NoIdle")).Visibility = Visibility.Collapsed;
        // Zrzut ma pokazywać także zdanie o zasięgu „✕ Skasuj” — sekcja jest tu wypełniana z pominięciem Refresh().
        ((TextBlock)operatorWindow.FindName("IdleScope")).Text = scope.DismissScopeNote;
        Render(operatorWindow, Path.Combine(output, "podglad.png"), 1100, 950);
        var orchestrator = services.GetRequiredService<AppOrchestrator>();
        var start = DateTimeOffset.Now.Date.AddHours(13).AddMinutes(55);
        orchestrator.Downtime.Record(alert with { StartedAt = start, LastSeenAt = start.AddMinutes(23), Status = AlertStatus.Closed });
        var admin = services.GetRequiredService<StatusWindow>();
        ((DowntimeView)((ContentControl)admin.FindName("ReportHost")).Content).Refresh();
        Render(admin, Path.Combine(output, "panel-administracyjny.png"), 1220, 850);
        admin.ShowSharedConfigTab();
        Render(admin, Path.Combine(output, "panel-konfiguracja.png"), 1220, 850);
        admin.ShowReportTab();
        Render(services.GetRequiredService<SetupWindow>(), Path.Combine(output, "ustawienia.png"), 680, 820);
        var alarmWindow = services.GetRequiredService<AlertWindow>();
        var vm = (AlertWindowViewModel)alarmWindow.DataContext;
        vm.Refresh([alert]);
        var originalItem = vm.Items[0];
        vm.Refresh([alert with { LastSeenAt = alert.LastSeenAt.AddSeconds(1) }]);
        if (!ReferenceEquals(originalItem, vm.Items[0])) throw new InvalidOperationException("Pomoc przy błędzie została odtworzona przy odświeżeniu.");
        Render(alarmWindow, Path.Combine(output, "alert.png"), 460, 340);
        var expander = FindDescendant<Expander>((DependencyObject)alarmWindow.Content);
        if (expander is null) throw new InvalidOperationException("Brak rozwijanej instrukcji błędu.");
        expander.IsExpanded = true;
        Render(alarmWindow, Path.Combine(output, "alert-pomoc.png"), 460, 600);
        var idleWindow = services.GetRequiredService<IdlePopupWindow>();
        ((IdlePopupViewModel)idleWindow.DataContext).Refresh(
            [alert with { Kind = AlertKind.MachineIdle, ShortText = "Dołóż kolejne prace", Category = "postoj", Sound = false }],
            scope.DismissScopeNote, scope.IsDismissScopeWarning, scope.DismissButtonHint);
        Render(idleWindow, Path.Combine(output, "postoj.png"), 420, 235);
        var secrets = services.GetRequiredService<MaszynyAlert.Core.Abstractions.ISecretStore>();
        Render(new AdminLoginWindow(secrets), Path.Combine(output, "logowanie.png"), 430, 330);
    }

    internal static void VerifyTechnicalTab(IServiceProvider services)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui");
        var admin = services.GetRequiredService<StatusWindow>();
        var logger = services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("SmokeLog");

        admin.ShowReportTab();
        for (int i = 0; i < 60; i++)
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Wpis przed wejściem na zakładkę {Numer}", i);
        admin.RefreshTechnical(followLog: true);
        Layout(admin, 1220, 850);
        admin.ShowTechnicalTab();
        Layout(admin, 1220, 850);
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Wpis po wejściu na zakładkę");
        admin.RefreshTechnical();
        Layout(admin, 1220, 850);
        RequireLogAtEnd(admin, "po wejściu na zakładkę z innej zakładki Panelu (U-01b)");

        Render(admin, Path.Combine(output, "panel-podglad-techniczny.png"), 1220, 850);

        var stack = (StackPanel)admin.FindName("TechnicalSections");
        if (stack.Orientation != System.Windows.Controls.Orientation.Vertical)
            throw new InvalidOperationException("Sekcje Podglądu technicznego mają leżeć w pionowym stosie.");
        if (FindAncestor<ScrollViewer>(stack) is null)
            throw new InvalidOperationException("Stos sekcji Podglądu technicznego ma być w ScrollViewer (zakładka przewija się jako całość).");

        string[] expected = ["Maszyny", "Komputery", "Alerty aktywne", "Maszyny bez pracy – dołóż prace", "Ekran HMI – włącz okno podglądu", "Historia (ostatnie 50)", "Log (ostatnie 50 linii)"];
        var sections = stack.Children.OfType<System.Windows.Controls.GroupBox>().ToArray();
        if (!sections.Select(g => g.Header as string).SequenceEqual(expected))
            throw new InvalidOperationException("Kolejność sekcji Podglądu technicznego: " + string.Join(" | ", sections.Select(g => g.Header)));

        double previousBottom = double.NegativeInfinity;
        double stackWidth = stack.ActualWidth;
        foreach (System.Windows.Controls.GroupBox section in sections.Where(g => g.Visibility == Visibility.Visible))
        {
            Rect bounds = section.TransformToAncestor(stack).TransformBounds(new Rect(0, 0, section.ActualWidth, section.ActualHeight));
            if (bounds.Top < previousBottom - 0.5)
                throw new InvalidOperationException($"Sekcja „{section.Header}” leży obok poprzedniej (góra {bounds.Top:F0} < dół poprzedniej {previousBottom:F0}), a ma być pod nią.");
            if (bounds.Width < stackWidth - 1)
                throw new InvalidOperationException($"Sekcja „{section.Header}” ma {bounds.Width:F0} px szerokości zamiast całej szerokości {stackWidth:F0} px.");
            previousBottom = bounds.Bottom;
        }

        var log = RequireDescendant<System.Windows.Controls.ListBox>(sections[^1]);
        if (log.ActualHeight < 320)
            throw new InvalidOperationException($"Lista logu ma {log.ActualHeight:F0} px wysokości — ma mieć co najmniej 320 px, żeby linie nie były ściśnięte.");
        if (previousBottom <= 850)
            throw new InvalidOperationException($"Stos sekcji kończy się na {previousBottom:F0} px — przy siedmiu sekcjach jedna pod drugą ma być dłuższy niż okno (850 px) i przewijać się.");

        for (int i = 0; i < 60; i++)
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, new InvalidOperationException("test logu\nsiad stosu"), "Wpis testowy logu {Numer}", i);
        admin.RefreshTechnical(followLog: true);
        Render(admin, Path.Combine(output, "panel-podglad-techniczny-log.png"), 1220, 850);
        var logViewModel = services.GetRequiredService<StatusWindowViewModel>();
        if (logViewModel.LogLines.Count == 0 || logViewModel.LogLines.Any(l => l.Contains('\n')))
            throw new InvalidOperationException("Wpis logu z wyjątkiem w Podglądzie technicznym ma zajmować jedną linię (bez śladu stosu).");
        // Przewinięcie jest kolejkowane po układzie listy — jeszcze jeden obieg dyspozytora i układu.
        admin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ((FrameworkElement)admin.Content).UpdateLayout();
        ScrollViewer logScroll = admin.FindLogScroll()
            ?? throw new InvalidOperationException("Lista logu nie ma własnego ScrollViewer.");
        if (logScroll.ScrollableHeight <= 0)
            throw new InvalidOperationException("Test przewinięcia logu nic nie sprawdza — 50 wpisów mieści się bez przewijania.");
        if (logScroll.VerticalOffset < logScroll.ScrollableHeight - 1)
            throw new InvalidOperationException($"Log nie jest przewinięty do najnowszego wpisu (pozycja {logScroll.VerticalOffset:F0} z {logScroll.ScrollableHeight:F0}).");

        var page = FindAncestor<ScrollViewer>(stack)!;
        foreach (string position in new[] { "góra", "środek", "dół" })
        {
            page.ScrollToVerticalOffset(position switch { "góra" => 0, "środek" => page.ScrollableHeight / 2, _ => page.ScrollableHeight });
            Layout(admin, 1220, 850);
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Wpis przy sprawdzeniu układu: {Pozycja}", position);
            admin.RefreshTechnical();
            ((FrameworkElement)admin.Content).UpdateLayout();
            string rendered = TechnicalLayout(page, stack);
            Layout(admin, 1220, 850);
            string settled = TechnicalLayout(page, stack);
            if (rendered != settled)
                throw new InvalidOperationException($"Podgląd techniczny ({position}): układ po odświeżeniu różni się od końcowego.\nrender: {rendered}\nkoniec: {settled}");
            RequireLogAtEnd(admin, $"po odświeżeniu ({position})");
        }

        admin.ShowReportTab();
    }

    /// <summary>Układ i przewinięcia zakładki „Podgląd techniczny” w DIP (porównanie przed / po odświeżeniu).</summary>
    private static string TechnicalLayout(ScrollViewer page, StackPanel stack)
    {
        var text = new System.Text.StringBuilder(FormattableString.Invariant($"strona {page.VerticalOffset:F2}/{page.ExtentHeight:F2}"));
        foreach (FrameworkElement section in stack.Children.OfType<FrameworkElement>().Where(s => s.Visibility == Visibility.Visible))
        {
            double top = section.TransformToAncestor(stack).Transform(new System.Windows.Point(0, 0)).Y;
            text.Append(FormattableString.Invariant($"; {(section as HeaderedContentControl)?.Header ?? "nagłówek"} y={top:F2} h={section.ActualHeight:F2}"));
            foreach (ScrollViewer inner in FindDescendants<ScrollViewer>(section))
                text.Append(FormattableString.Invariant($" lista={inner.VerticalOffset:F2}/{inner.ExtentHeight:F2}"));
        }

        return text.ToString();
    }

    private static void RequireLogAtEnd(StatusWindow admin, string when)
    {
        ScrollViewer logScroll = admin.FindLogScroll()
            ?? throw new InvalidOperationException($"Lista logu nie ma własnego ScrollViewer ({when}).");
        if (logScroll.ScrollableHeight <= 0)
            throw new InvalidOperationException($"Test przewinięcia logu nic nie sprawdza ({when}) — wpisy mieszczą się bez przewijania.");
        if (logScroll.VerticalOffset < logScroll.ScrollableHeight - 1)
            throw new InvalidOperationException($"Log nie stoi na najnowszym wpisie {when} (pozycja {logScroll.VerticalOffset:F0} z {logScroll.ScrollableHeight:F0}).");
    }

    /// <summary>Układ okna bez zrzutu: to samo, co <see cref="Render"/> przed narysowaniem bitmapy.</summary>
    private static void Layout(Window window, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        content.UpdateLayout();
    }

    internal static void VerifyDarkTheme(IServiceProvider services)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui");
        var theme = services.GetRequiredService<ThemeService>();
        var store = services.GetRequiredService<ThemeSettingsStore>();
        var operatorWindow = services.GetRequiredService<OperatorWindow>();
        var admin = services.GetRequiredService<StatusWindow>();
        var alarmWindow = services.GetRequiredService<AlertWindow>();
        var idleWindow = services.GetRequiredService<IdlePopupWindow>();
        ResourceDictionary resources = System.Windows.Application.Current.Resources;
        IReadOnlyDictionary<string, System.Windows.Media.Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);
        IReadOnlyDictionary<string, System.Windows.Media.Color> light = ThemePalette.ColorsFor(AppTheme.Light);

        // Oba przebiegi smoke (zwykły i --monitoring-disabled) dzielą katalog, więc plik może już istnieć — ma mówić „jasny”.
        if (theme.IsDark || store.Load().Resolve() != AppTheme.Light)
            throw new InvalidOperationException("Test startu zaczyna w jasnym motywie (theme.json testu pusty albo „Light”).");
        if (operatorWindow.ThemeButtonText != "Tryb ciemny")
            throw new InvalidOperationException("Przycisk w jasnym motywie ma proponować tryb ciemny.");
        foreach (string key in ThemePalette.Keys)
            if (resources[key] is not System.Windows.Media.SolidColorBrush lightBrush || lightBrush.Color != light[key])
                throw new InvalidOperationException($"Pędzel {key} w App.xaml różni się od palety jasnej w ThemePalette.");

        theme.Toggle();

        if (!theme.IsDark || store.Load().Resolve() != AppTheme.Dark)
            throw new InvalidOperationException("Przełączenie nie zapisało ciemnego motywu w pliku profilu.");
        if (operatorWindow.ThemeButtonText != "Tryb jasny")
            throw new InvalidOperationException("Przycisk w ciemnym motywie ma proponować tryb jasny.");
        if (operatorWindow.Background is not System.Windows.Media.SolidColorBrush background || background.Color != dark["PanelBackgroundBrush"])
            throw new InvalidOperationException("Tło Podglądu nie podąża za motywem (StaticResource zamiast DynamicResource?).");
        if (admin.Background is not System.Windows.Media.SolidColorBrush adminBackground || adminBackground.Color != dark["PanelBackgroundBrush"])
            throw new InvalidOperationException("Tło Panelu administracyjnego nie podąża za motywem.");

        Render(operatorWindow, Path.Combine(output, "podglad-ciemny.png"), 1100, 950);
        if (((System.Windows.Controls.Button)operatorWindow.FindName("ThemeButton")).Content as string != "Tryb jasny")
            throw new InvalidOperationException("Przycisk w Podglądzie nie zmienił napisu po przełączeniu motywu.");
        Render(admin, Path.Combine(output, "panel-administracyjny-ciemny.png"), 1220, 850);
        admin.ShowSharedConfigTab();
        Render(admin, Path.Combine(output, "panel-konfiguracja-ciemny.png"), 1220, 850);
        AssertNoLightSurfaces(admin, dark, "Panel administracyjny — Konfiguracja");
        admin.ShowTechnicalTab();
        Render(admin, Path.Combine(output, "panel-podglad-techniczny-ciemny.png"), 1220, 850);
        AssertNoLightSurfaces(admin, dark, "Panel administracyjny — Podgląd techniczny");
        AssertSectionBordersFromPalette(admin, dark, "Panel administracyjny — Podgląd techniczny");
        admin.ShowReportTab();
        var setupWindow = services.GetRequiredService<SetupWindow>();
        Render(setupWindow, Path.Combine(output, "ustawienia-ciemny.png"), 680, 820);
        AssertNoLightSurfaces(setupWindow, dark, "Ustawienia");
        AssertSectionBordersFromPalette(setupWindow, dark, "Ustawienia");
        Render(alarmWindow, Path.Combine(output, "alert-ciemny.png"), 460, 600);
        Render(idleWindow, Path.Combine(output, "postoj-ciemny.png"), 420, 235);
        AssertNoLightSurfaces(operatorWindow, dark, "Podgląd");
        AssertNoLightSurfaces(admin, dark, "Panel administracyjny");
        AssertNoLightSurfaces(alarmWindow, dark, "Alert");
        AssertNoLightSurfaces(idleWindow, dark, "Pop-up postoju");

        theme.Toggle();

        if (theme.IsDark || store.Load().Resolve() != AppTheme.Light)
            throw new InvalidOperationException("Powrót do jasnego motywu nie został zapisany.");
        if (operatorWindow.Background is not System.Windows.Media.SolidColorBrush restored || restored.Color != light["PanelBackgroundBrush"])
            throw new InvalidOperationException("Tło Podglądu nie wróciło do motywu jasnego.");
    }

    internal static async Task VerifyUpdateWindowAsync(IServiceProvider services)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output", "ui");
        Directory.CreateDirectory(output);
        var coordinator = services.GetRequiredService<UpdateCoordinator>();
        if (coordinator.State.Phase != UpdatePhase.Disabled)
            throw new InvalidOperationException($"W teście startu aktualizacje mają być wyłączone, a koordynator jest w fazie {coordinator.State.Phase}.");

        var window = services.GetRequiredService<UpdateWindow>();
        var state = new UpdateState
        {
            Phase = UpdatePhase.Available,
            InstalledVersion = new Version(0, 4, 1, 0),
            Latest = new ReleaseEntry("VncAlert-Setup-0.4.99.0.exe", new Version(0, 4, 99, 0), 57_461_760,
                new string('0', 64), DateTimeOffset.Now.AddHours(-2)),
            Message = "Dostępna jest nowa wersja programu (dane przykładowe — test wyglądu, bez połączenia z katalogiem wydań).",
        };
        window.ShowPreview(state);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (!window.IsVisible)
            throw new InvalidOperationException("Okno aktualizacji nie pokazało się.");

        var primary = (System.Windows.Controls.Button)window.FindName("PrimaryButton");
        if (primary.Visibility != Visibility.Visible || !primary.IsEnabled || primary.Content as string != "Pobierz i zainstaluj")
            throw new InvalidOperationException(
                $"Przycisk główny okna aktualizacji: widoczność {primary.Visibility}, aktywny {primary.IsEnabled}, napis „{primary.Content}”.");
        if (((TextBlock)window.FindName("InstalledVersion")).Text != "Zainstalowana wersja: 0.4.1"
            || ((TextBlock)window.FindName("AvailableVersion")).Text is not { } available
            || !available.StartsWith("Dostępna wersja: 0.4.99 (opublikowana ", StringComparison.Ordinal))
            throw new InvalidOperationException("Okno aktualizacji nie pokazuje wersji zainstalowanej i dostępnej.");
        if (((System.Windows.Controls.ProgressBar)window.FindName("DownloadProgress")).IsVisible)
            throw new InvalidOperationException("Pasek pobierania ma być widoczny tylko w trakcie pobierania.");
        Render(window, Path.Combine(output, "aktualizacja.png"), 580, 380);

        var theme = services.GetRequiredService<ThemeService>();
        if (theme.IsDark) throw new InvalidOperationException("Sprawdzenie okna aktualizacji zaczyna w jasnym motywie.");
        theme.Toggle();
        try
        {
            Render(window, Path.Combine(output, "aktualizacja-ciemny.png"), 580, 380);
            AssertNoLightSurfaces(window, ThemePalette.ColorsFor(AppTheme.Dark), "Aktualizacja");
        }
        finally
        {
            theme.Toggle();
        }

        window.Hide();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (window.IsVisible) throw new InvalidOperationException("Okno aktualizacji nie schowało się.");
        if (window.ViewModel.Phase != UpdatePhase.Disabled)
            throw new InvalidOperationException("Po schowaniu podglądu okno aktualizacji ma znów pokazywać stan koordynatora.");
    }

    /// <summary>Jasne tło spoza palety ciemnej w zrealizowanym drzewie wizualnym okna = kolor wpisany na stałe.</summary>
    private static void AssertNoLightSurfaces(Window window, IReadOnlyDictionary<string, System.Windows.Media.Color> palette, string label)
    {
        var allowed = palette.Values.ToHashSet();
        var offenders = new List<string>();
        CollectLightSurfaces((DependencyObject)window.Content, allowed, offenders);
        if (offenders.Count > 0)
            throw new InvalidOperationException(
                $"Ciemny motyw, okno „{label}”: jasne tła spoza palety ({offenders.Count}): {string.Join("; ", offenders.Take(10))}");
    }

    /// <summary>
    /// Każda widoczna linia obramowania wewnątrz sekcji (GroupBox) ma kolor z palety ciemnej, a każda sekcja ma
    /// ramkę w kolorze SectionBorderBrush; linia spoza palety = domyślny szablon Windows (BorderBrush="White")
    /// albo kolor wpisany na stałe.
    /// </summary>
    private static void AssertSectionBordersFromPalette(Window window, IReadOnlyDictionary<string, System.Windows.Media.Color> palette, string label)
    {
        var sections = new List<System.Windows.Controls.GroupBox>();
        CollectDescendants((DependencyObject)window.Content, sections);
        // Sekcja schowana (Collapsed — np. „Komputery” w trybie jednego komputera) nie ma zrealizowanego szablonu,
        // więc nie ma czego sprawdzać; liczą się tylko sekcje widoczne.
        sections.RemoveAll(section => section.Visibility != Visibility.Visible);
        if (sections.Count == 0)
            throw new InvalidOperationException($"Ciemny motyw, okno „{label}”: brak sekcji System.Windows.Controls.GroupBox do sprawdzenia.");
        var allowed = palette.Values.ToHashSet();
        var offenders = new List<string>();
        int sectionFrames = 0;
        foreach (System.Windows.Controls.GroupBox section in sections)
        {
            var borders = new List<Border>();
            CollectDescendants(section, borders);
            foreach (Border border in borders)
            {
                if (border.BorderThickness is { Left: 0, Top: 0, Right: 0, Bottom: 0 } || border.BorderBrush is not System.Windows.Media.SolidColorBrush { Color.A: > 0 } solid)
                    continue;
                // Kolor spoza palety ciemnej = biała linia domyślnego szablonu albo kolor wpisany na stałe
                // (sama jasność nie wystarcza: akcent morskiej zieleni z palety jest legalną ramką pola).
                if (!allowed.Contains(solid.Color))
                    offenders.Add($"{section.Header}: {solid.Color}");
                else if (solid.Color == palette["SectionBorderBrush"])
                    sectionFrames++;
            }
        }
        if (offenders.Count > 0)
            throw new InvalidOperationException(
                $"Ciemny motyw, okno „{label}”: jasne lub obce linie obramowania sekcji ({offenders.Count}): {string.Join("; ", offenders.Distinct().Take(10))}");
        if (sectionFrames < sections.Count)
            throw new InvalidOperationException(
                $"Ciemny motyw, okno „{label}”: {sectionFrames} z {sections.Count} sekcji ma ramkę w kolorze SectionBorderBrush.");
    }

    private static void CollectDescendants<T>(DependencyObject node, List<T> found) where T : DependencyObject
    {
        if (node is T match) found.Add(match);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            CollectDescendants(VisualTreeHelper.GetChild(node, i), found);
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static void CollectLightSurfaces(DependencyObject node, HashSet<System.Windows.Media.Color> allowed, List<string> offenders)
    {
        // Części systemowe z własnymi szablonami (kalendarz w DatePicker, pasek przewijania) nie są oceniane.
        if (node is DatePicker or System.Windows.Controls.Primitives.ScrollBar or Calendar) return;
        if (node is FrameworkElement { Visibility: Visibility.Visible } element)
        {
            System.Windows.Media.Brush? background = node switch
            {
                Border border => border.Background,
                System.Windows.Controls.Panel panel => panel.Background,
                System.Windows.Controls.Control control => control.Background,
                _ => null,
            };
            if (background is System.Windows.Media.SolidColorBrush { Color.A: > 0 } solid
                && !allowed.Contains(solid.Color) && Luminance(solid.Color) > 0.6)
                offenders.Add($"{node.GetType().Name}{(string.IsNullOrEmpty(element.Name) ? "" : " " + element.Name)}={solid.Color}");
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            CollectLightSurfaces(VisualTreeHelper.GetChild(node, i), allowed, offenders);
    }

    private static double Luminance(System.Windows.Media.Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Reaches the real control inside a generated <see cref="ItemsControl"/> row template.</summary>
    private static T RequireDescendant<T>(DependencyObject? root) where T : DependencyObject =>
        (root is null ? null : FindDescendant<T>(root))
        ?? throw new InvalidOperationException(
            $"Nie znaleziono elementu {typeof(T).Name} w wygenerowanym wierszu listy.");

    private sealed class BlockingScreenSource(string machineId) : MaszynyAlert.Core.Machines.IScreenSource
    {
        private int _disposeCount;

        public string MachineId { get; } = machineId;
        public bool IsConnected { get; private set; }
        public MaszynyAlert.Core.Machines.ScreenSourceInfo? Info { get; private set; }
        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<MaszynyAlert.Core.Machines.ScreenSourceInfo> ConnectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IsConnected = true;
            Info = new("vnc", "3.8", "None", "32 bpp", 320, 180, "smoke");
            return Task.FromResult(Info);
        }

        public Task<MaszynyAlert.Core.Imaging.RgbaImage> CaptureAsync(
            MaszynyAlert.Core.Imaging.RectInt roi, bool incremental, CancellationToken ct) => CaptureFullAsync(ct);

        public async Task<MaszynyAlert.Core.Imaging.RgbaImage> CaptureFullAsync(CancellationToken ct)
        {
            CaptureStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Nieosiągalne po anulowaniu testu.");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private static void Render(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
