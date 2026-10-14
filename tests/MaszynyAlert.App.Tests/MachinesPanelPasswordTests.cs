using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using static MaszynyAlert.App.Tests.SettingsChangeConfirmationTests;

namespace MaszynyAlert.App.Tests;

public sealed class MachinesPanelPasswordTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    private static AppConfig HallLikeConfig()
    {
        AppConfig example = ConfigLoader.LoadAppConfig(Path.Combine(RepositoryRoot(), "config", "config.example.json"));
        return example with
        {
            Machines = example.Machines
                .Select(m => m.Id is "premile" or "cameo1" ? m with { Profile = "aidite-light2", RequiresPassword = true } : m).ToList(),
        };
    }

    private sealed class Passwords
    {
        public HashSet<string> Saved { get; } = ["aidite1", "aidite2", "aidite3", "aidite4", "aidite5", "aidite6", "aidite7"];
        public List<string> Asked { get; } = [];

        public bool Has(string machineId)
        {
            Asked.Add(machineId);
            return Saved.Contains(machineId);
        }
    }

    private sealed class Recorder
    {
        public List<IReadOnlyDictionary<string, bool>?> Machines { get; } = [];

        public Task Apply(TimingConfig? timing, OverlayConfig? overlay, IReadOnlyDictionary<string, bool>? machines, CancellationToken ct)
        {
            Machines.Add(machines);
            return Task.CompletedTask;
        }
    }

    private static SharedConfigViewModel Create(AppConfig config, bool inGroup, Passwords passwords, Recorder recorder,
        ISettingsChangeConfirmation confirmation, SharedConfigDocument? document = null) =>
        new(() => new SharedConfigViewInput(config, document?.ApplyTo(config) ?? config, document,
                inGroup ? "Konfiguracja jest wspólna dla grupy; zgodna z 3 z 3 komputerami." : "", null, inGroup, null, passwords.Has),
            recorder.Apply, confirmation);

    private static SharedConfigField Row(SharedConfigViewModel vm, string machineId) =>
        vm.MachineFields.Single(f => f.Key == SharedConfigViewModel.MachineKey(machineId));

    // ============================== W grupie: odmowa ==============================

    [Fact]
    public async Task WGrupie_WlaczenieMaszynyBezHaslaNaTymKomputerze_OdmowaPrzyPolu_NicNieZapisaneINiePytane()
    {
        var passwords = new Passwords();
        var recorder = new Recorder();
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(HallLikeConfig(), inGroup: true, passwords, recorder, confirmation);
        vm.SelectedSection = vm.Sections[0];

        Row(vm, "premile").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Machines);
        Assert.Empty(confirmation.Asked);
        Assert.Equal(SharedConfigViewModel.MissingPasswordInGroupMessage, Row(vm, "premile").Error);
        Assert.Contains(SharedConfigViewModel.VncPasswordPlace, Row(vm, "premile").Error, StringComparison.Ordinal);
        Assert.StartsWith("Nie zapisano — popraw", vm.Message, StringComparison.Ordinal);
        // Jak przy odmowie wyłączenia wszystkich: zakładka przechodzi do sekcji „Maszyny” i zaznacza pole z błędem.
        Assert.Equal(SharedConfigViewModel.MachinesSectionTitle, vm.SelectedSection!.Title);
        Assert.Same(Row(vm, "premile"), vm.SelectedField);
        Assert.Equal("błąd", vm.SelectedSection.StatusText);
        Assert.True(Row(vm, "premile").Flag); // wpis zostaje
        Assert.Contains("premile", passwords.Asked);
    }

    /// <summary>Hasło zapisane w „Ten komputer” w międzyczasie — kolejny zapis przechodzi bez odświeżania zakładki, błąd znika.</summary>
    [Fact]
    public async Task WGrupie_PoZapisaniuHasla_TenSamZapisPrzechodzi()
    {
        var passwords = new Passwords();
        var recorder = new Recorder();
        var vm = Create(HallLikeConfig(), inGroup: true, passwords, recorder, new RecordingConfirmation(true));
        Row(vm, "cameo1").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(recorder.Machines);

        passwords.Saved.Add("cameo1");
        await vm.SaveCommand.ExecuteAsync(null);

        IReadOnlyDictionary<string, bool>? saved = Assert.Single(recorder.Machines);
        Assert.True(saved!["cameo1"]);
        Assert.Equal("", Row(vm, "cameo1").Error);
        Assert.StartsWith("Zapisano.", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WGrupie_KilkaMaszynBezHasla_KazdaMaBladPrzyPolu_InnaZmianaNieObchodziOdmowy()
    {
        var recorder = new Recorder();
        var vm = Create(HallLikeConfig(), inGroup: true, new Passwords(), recorder, new RecordingConfirmation(true));

        Row(vm, "premile").Flag = true;
        Row(vm, "amm580").Flag = true;
        Row(vm, "amm520-2").Flag = true; // requiresPassword:false — bez błędu
        Row(vm, "aidite7").Flag = false;
        vm.Fields.Single(f => f.Key == "timing.alertSoundRepeatMs").Text = "0.5";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Machines);
        Assert.Equal(SharedConfigViewModel.MissingPasswordInGroupMessage, Row(vm, "premile").Error);
        Assert.Equal(SharedConfigViewModel.MissingPasswordInGroupMessage, Row(vm, "amm580").Error);
        Assert.Equal("", Row(vm, "amm520-2").Error);
        Assert.Equal("", Row(vm, "aidite7").Error);
    }

    /// <summary>Granice odmowy: maszyna bez wymogu hasła, maszyna z hasłem, wyłączanie — zapis przechodzi jak dotąd.</summary>
    [Theory]
    [InlineData("amm520-2", true)]  // requiresPassword:false w konfiguracji (serwer bez hasła)
    [InlineData("amm580", true)]    // hasło zapisane niżej
    [InlineData("aidite7", false)]  // wyłączenie nie wymaga hasła
    public async Task WGrupie_BezOdmowy_GdyMaszynaNieWymagaHaslaMaJeAlboJestWylaczana(string machineId, bool on)
    {
        var passwords = new Passwords();
        passwords.Saved.Add("amm580");
        var recorder = new Recorder();
        var vm = Create(HallLikeConfig(), inGroup: true, passwords, recorder, new RecordingConfirmation(true));

        Row(vm, machineId).Flag = on;
        await vm.SaveCommand.ExecuteAsync(null);

        IReadOnlyDictionary<string, bool>? saved = Assert.Single(recorder.Machines);
        Assert.Equal(on, saved![machineId]);
        Assert.All(vm.MachineFields, f => Assert.Equal("", f.Error));
    }

    /// <summary>
    /// Maszyna włączona JUŻ WCZEŚNIEJ bez hasła (np. w pliku warstwy) nie blokuje innych zmian — jej brak hasła nie jest skutkiem
    /// tego zapisu (ta sama granica co w regule Core).
    /// </summary>
    [Fact]
    public async Task WGrupie_MaszynaWlaczonaWczesniejBezHasla_NieBlokujeInnychPrzelacznikow()
    {
        var document = new SharedConfigDocument
        {
            Machines = SharedConfigDocument.CanonicalMachines(new Dictionary<string, bool> { ["amm580"] = true }),
            EditedAt = new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero), EditedBy = "PC-B",
        };
        var recorder = new Recorder();
        var vm = Create(HallLikeConfig(), inGroup: true, new Passwords(), recorder, new RecordingConfirmation(true), document);
        Assert.True(Row(vm, "amm580").Flag);

        Row(vm, "amm520-2").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        IReadOnlyDictionary<string, bool>? saved = Assert.Single(recorder.Machines);
        Assert.True(saved!["amm520-2"]);
        Assert.True(saved["amm580"]);
    }

    // ============================== Poza grupą: ostrzeżenie ==============================

    [Fact]
    public async Task PozaGrupa_WlaczenieMaszynyBezHasla_OstrzezenieWLiscieZmian_ZapisDozwolony()
    {
        var recorder = new Recorder();
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(HallLikeConfig(), inGroup: false, new Passwords(), recorder, confirmation);

        Row(vm, "premile").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        IReadOnlyList<SettingChange> asked = Assert.Single(confirmation.Asked);
        Assert.Equal([
            new SettingChange("Maszyna: Premile", "wyłączona", "włączona"),
            new SettingChange("Źródło wartości: maszyny", "config.json / profil grupy", "wspólna konfiguracja"),
            SettingChange.Warning("Uwaga — Premile: brak hasła VNC na tym komputerze", SharedConfigViewModel.MissingPasswordWarningText),
        ], asked);
        SettingChange warning = asked[^1];
        Assert.True(warning.IsWarning);
        Assert.False(warning.IsSecret);
        Assert.Equal("", warning.BeforeText);
        Assert.Equal(SharedConfigViewModel.MissingPasswordWarningText, warning.AfterText);
        Assert.Contains(SharedConfigViewModel.VncPasswordPlace, warning.AfterText, StringComparison.Ordinal);
        Assert.True(Assert.Single(recorder.Machines)!["premile"]);
        Assert.Equal("", Row(vm, "premile").Error);
    }

    [Fact]
    public async Task PozaGrupa_AnulowanieOstrzezenia_NicNieZapisuje()
    {
        var recorder = new Recorder();
        var vm = Create(HallLikeConfig(), inGroup: false, new Passwords(), recorder, new RecordingConfirmation(false));

        Row(vm, "cameo1").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Machines);
        Assert.StartsWith("Nie zapisano — anulowano", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PozaGrupa_ZHaslemAlboBezWymoguHasla_BezOstrzezenia()
    {
        var passwords = new Passwords();
        passwords.Saved.Add("amm580");
        var confirmation = new RecordingConfirmation(true);
        var vm = Create(HallLikeConfig(), inGroup: false, passwords, new Recorder(), confirmation);

        Row(vm, "amm580").Flag = true;
        Row(vm, "amm520-1").Flag = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(Assert.Single(confirmation.Asked), c => c.IsWarning);
    }

    // ============================== Widok (WpfTestHost.OffScreen) ==============================

    /// <summary>
    /// Prawdziwy <see cref="SharedConfigView"/> poza ekranem: odmowa w grupie jest widoczna W ZAKŁADCE (czerwona ramka wiersza,
    /// komunikat w panelu szczegółów, zdanie pod przyciskami) — bez okna z OK i bez dymka (<see cref="IUserMessages"/> nic nie dostaje).
    /// </summary>
    [Fact]
    public void Widok_WGrupie_OdmowaWidocznaWPanelu_BezOknaIDymka()
    {
        string report = WpfTestHost.Run(() =>
        {
            var messages = new RecordingMessages();
            var recorder = new Recorder();
            var confirmation = new RecordingConfirmation(true);
            SharedConfigViewModel vm = Create(HallLikeConfig(), inGroup: true, new Passwords(), recorder, confirmation);
            var view = new SharedConfigView(vm, messages: messages);
            Window window = WpfTestHost.ShowOffScreen(new Window { Content = view, Width = 1220, Height = 850 });
            try
            {
                vm.SelectedSection = vm.Sections.Single(s => s.Title == SharedConfigViewModel.MachinesSectionTitle);
                Settle(window);
                CheckBox box = MachineBoxOf(view, Row(vm, "premile"));
                box.IsChecked = true;
                Assert.True(Row(vm, "premile").Flag);
                Button save = Descendants<Button>(view).Single(b => Equals(b.Content, "Zapisz i zastosuj"));
                // Kliknięcie jak przez automatyzację UI: OnClick przycisku wykonuje jego polecenie (samo zdarzenie Click by go nie wykonało).
                ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)!).Invoke();
                Settle(window);

                Assert.Empty(recorder.Machines);
                Assert.Empty(confirmation.Asked);
                Assert.Empty(messages.Shown);
                Assert.Empty(messages.Notified);
                Assert.Same(Row(vm, "premile"), vm.SelectedField);
                Assert.Equal(SharedConfigViewModel.MissingPasswordInGroupMessage, view.DetailErrorText.Text);
                Assert.True(view.DetailErrorText.IsVisible);
                Assert.True(view.MessageText.IsVisible);
                Assert.StartsWith("Nie zapisano — popraw", view.MessageText.Text, StringComparison.Ordinal);
                var row = (Border)RowOf(view, Row(vm, "premile"));
                Assert.Equal(((SolidColorBrush)view.FindResource("ErrorBorderBrush")).Color, ((SolidColorBrush)row.BorderBrush).Color);
                return "odmowa widoczna: " + view.DetailErrorText.Text.Length + " znaków w panelu szczegółów";
            }
            finally { window.Close(); }
        });
        Assert.StartsWith("odmowa widoczna", report, StringComparison.Ordinal);
    }

    /// <summary>Okno potwierdzenia pokazuje ostrzeżenie jako nazwę i zdanie — bez „Było:” i bez „Po zapisaniu będzie:”.</summary>
    [Fact]
    public void OknoPotwierdzenia_OstrzezenieBezBylo()
    {
        SettingChange[] changes =
        [
            new("Maszyna: Premile", "wyłączona", "włączona"),
            SettingChange.Warning(SharedConfigViewModel.MissingPasswordWarningName("Premile"), SharedConfigViewModel.MissingPasswordWarningText),
        ];
        List<string> texts = WpfTestHost.Run(() =>
        {
            var seen = new List<string>();
            var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            ChangeConfirmationWindow? dialog = null;
            safety.Tick += (_, _) => { safety.Stop(); dialog?.Close(); };
            safety.Start();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                dialog = System.Windows.Application.Current.Windows.OfType<ChangeConfirmationWindow>().Single();
                dialog.Left = WpfTestHost.OffScreenPosition;
                dialog.Top = WpfTestHost.OffScreenPosition;
                dialog.UpdateLayout();
                seen.AddRange(Descendants<TextBlock>(dialog).Where(t => t.IsVisible).Select(t => t.Text));
                dialog.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }));
            try { new ChangeConfirmationWindow(changes).ShowDialog(); }
            finally { safety.Stop(); }
            return seen;
        });

        Assert.Contains("Uwaga — Premile: brak hasła VNC na tym komputerze", texts);
        Assert.Contains(SharedConfigViewModel.MissingPasswordWarningText, texts);
        Assert.Contains("Było: wyłączona", texts);
        Assert.Single(texts, t => t.StartsWith("Było:", StringComparison.Ordinal));
        Assert.Single(texts, t => t.StartsWith("Po zapisaniu będzie:", StringComparison.Ordinal));
    }

    private static void Settle(Window window)
    {
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
    }

    private static FrameworkElement RowOf(SharedConfigView view, SharedConfigField field)
    {
        var presenter = (ContentPresenter)view.SettingRows.ItemContainerGenerator.ContainerFromItem(field);
        return (FrameworkElement)presenter.ContentTemplate.FindName("Row", presenter);
    }

    private static CheckBox MachineBoxOf(SharedConfigView view, SharedConfigField field)
    {
        var presenter = (ContentPresenter)view.SettingRows.ItemContainerGenerator.ContainerFromItem(field);
        return (CheckBox)presenter.ContentTemplate.FindName("MachineBox", presenter);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
