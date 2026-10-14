using System.Globalization;
using System.IO;
using System.Reflection;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class SharedConfigViewModelTests
{
    /// <summary>Konfiguracja bazowa bez maszyn: obserwacja wyłączona, żeby walidacja nie wymagała listy maszyn.</summary>
    private static AppConfig BaseConfig(TimingConfig? timing = null, OverlayConfig? overlay = null) =>
        new()
        {
            Enabled = false,
            Timing = timing ?? new TimingConfig(),
            Ui = new UiConfig { Overlay = overlay ?? new OverlayConfig() },
        };

    private static SharedConfigViewInput Input(AppConfig? baseConfig = null, AppConfig? effective = null,
        SharedConfigDocument? document = null, string syncStatus = "", string? loadError = null,
        bool inGroup = false, string? path = null)
    {
        AppConfig @base = baseConfig ?? BaseConfig();
        return new SharedConfigViewInput(@base, effective ?? @base, document, syncStatus, loadError, inGroup, path);
    }

    private static SharedConfigViewModel Create(SharedConfigViewInput input,
        Func<TimingConfig?, OverlayConfig?, CancellationToken, Task>? apply = null) =>
        new(() => input, apply ?? ((_, _, _) => Task.CompletedTask));

    private static SharedConfigField Field(SharedConfigViewModel viewModel, string key) =>
        viewModel.Fields.Single(f => f.Key == key);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    [Fact]
    public void EverySettingOfTimingAndOverlayHasLabelAndDescription()
    {
        var viewModel = Create(Input());

        var expected = typeof(TimingConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => "timing." + Camel(p.Name))
            .Concat(typeof(OverlayConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => "ui.overlay." + Camel(p.Name)))
            .ToArray();

        foreach (string key in expected)
        {
            SharedConfigField field = Field(viewModel, key);
            Assert.False(string.IsNullOrWhiteSpace(field.Label), key + " nie ma etykiety.");
            Assert.True(field.Description.Length >= 20, key + " nie ma opisu: " + field.Description);
        }

        Assert.Equal(expected.Order(StringComparer.Ordinal),
            viewModel.Fields.Select(f => f.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OverlayLayoutIsAChoiceOfTheFiveNamesFromConfigJson()
    {
        OverlayConfig? saved = null;
        var viewModel = Create(Input(BaseConfig(overlay: new OverlayConfig { Layout = OverlayLayout.OneRow })),
            (_, overlay, _) => { saved = overlay; return Task.CompletedTask; });
        SharedConfigField field = Field(viewModel, "ui.overlay.layout");

        Assert.Equal(SharedConfigFieldKind.Choice, field.Kind);
        Assert.Equal(["one-row", "two-rows", "one-column", "two-columns", "three-columns"], field.Choices);
        Assert.Equal("one-row", field.Text);
        Assert.Contains("two-rows", field.Description, StringComparison.Ordinal);

        field.Text = "two-rows";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", field.Error);
        Assert.NotNull(saved);
        Assert.Equal(OverlayLayout.TwoRows, saved!.Layout);

        saved = null;
        field.Text = "spirala";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Contains("one-row", field.Error, StringComparison.Ordinal);
        Assert.Null(saved);
    }

    [Fact]
    public void FieldsShowEffectiveValueAndDefaultFromTheLayerBelow()
    {
        AppConfig @base = BaseConfig(new TimingConfig { EscalationAfterAckMs = 240000 },
            new OverlayConfig { Scale = 1.0 });
        AppConfig effective = BaseConfig(new TimingConfig { EscalationAfterAckMs = 60000 },
            new OverlayConfig { Scale = 1.25 });

        var viewModel = Create(Input(@base, effective));

        Assert.Equal("1", Field(viewModel, "timing.escalationAfterAckMs").Text);
        Assert.Equal("4", Field(viewModel, "timing.escalationAfterAckMs").DefaultText);
        Assert.Equal("min", Field(viewModel, "timing.escalationAfterAckMs").Unit);
        Assert.Equal("Z config.json / profilu grupy: 4 min", Field(viewModel, "timing.escalationAfterAckMs").DefaultHint);
        // Liczby dziesiętne zawsze z kropką — plik konfiguracji ma wyglądać tak samo na każdym komputerze.
        Assert.Equal("1.25", Field(viewModel, "ui.overlay.scale").Text);
        Assert.Equal("1", Field(viewModel, "ui.overlay.scale").DefaultText);
        Assert.Equal("2; 5; 15; 30", Field(viewModel, "timing.reconnectBackoffMs").Text);
        Assert.Equal("s", Field(viewModel, "timing.reconnectBackoffMs").Unit);
        Assert.Equal("false", Field(viewModel, "timing.offlineAlertSound").Text);
        Assert.Equal("top-left", Field(viewModel, "ui.overlay.position").Text);
    }

    /// <summary>Przy wpisywaniu przecinek działa tak samo jak kropka — nikt nie ma pamiętać o kulturze pliku.</summary>
    [Theory]
    [InlineData("1,5")]
    [InlineData("1.5")]
    public async Task DecimalValueAcceptsBothCommaAndDot(string entered)
    {
        OverlayConfig? saved = null;
        var viewModel = Create(Input(), (_, overlay, _) => { saved = overlay; return Task.CompletedTask; });

        Field(viewModel, "ui.overlay.scale").Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(1.5, saved!.Scale, 3);
        Assert.Equal("", Field(viewModel, "ui.overlay.scale").Error);
    }

    /// <summary>Wpis, który nie jest liczbą, zatrzymuje zapis i pokazuje powód przy TYM polu.</summary>
    [Fact]
    public async Task NonNumericEntryStopsSaveAndIsReportedOnTheField()
    {
        bool applied = false;
        var viewModel = Create(Input(), (_, _, _) => { applied = true; return Task.CompletedTask; });

        Field(viewModel, "timing.pollIntervalMs").Text = "co sekundę";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(applied);
        Assert.True(Field(viewModel, "timing.pollIntervalMs").HasError);
        Assert.Contains("popraw pola", viewModel.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfRangeValueIsReportedOnTheFieldAndNothingIsSaved()
    {
        bool applied = false;
        var viewModel = Create(Input(), (_, _, _) => { applied = true; return Task.CompletedTask; });

        Field(viewModel, "timing.pollIntervalMs").Text = "0.05";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(applied);
        Assert.Contains("timing.pollIntervalMs", Field(viewModel, "timing.pollIntervalMs").Error, StringComparison.Ordinal);
        Assert.Contains("timing.pollIntervalMs", viewModel.Message, StringComparison.Ordinal);
        Assert.Contains("(0.05 s)", Field(viewModel, "timing.pollIntervalMs").Error, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bms\b", Field(viewModel, "timing.pollIntervalMs").Error);
    }

    /// <summary>Zapis przekazuje KOMPLET obu sekcji — warstwa wspólna nie może zapisać połowy ustawień.</summary>
    [Fact]
    public async Task SavePassesWholeTimingAndOverlaySections()
    {
        TimingConfig? timing = null;
        OverlayConfig? overlay = null;
        var viewModel = Create(Input(), (t, o, _) => { timing = t; overlay = o; return Task.CompletedTask; });

        Field(viewModel, "timing.escalationAfterAckMs").Text = "5";
        Field(viewModel, "timing.offlineAlertSound").Flag = true;
        Field(viewModel, "timing.reconnectBackoffMs").Text = "1; 4";
        Field(viewModel, "ui.overlay.position").Text = "bottom-right";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(timing);
        Assert.NotNull(overlay);
        Assert.Equal(300000, timing!.EscalationAfterAckMs);
        Assert.True(timing.OfflineAlertSound);
        Assert.Equal(new[] { 1000, 4000 }, timing.ReconnectBackoffMs);
        // Pozostałe pola sekcji zostają takie, jakie były — nie gubimy ich przy zapisie.
        Assert.Equal(new TimingConfig().PollIntervalMs, timing.PollIntervalMs);
        Assert.Equal(OverlayPosition.BottomRight, overlay!.Position);
        Assert.Contains("Zapisano.", viewModel.Message, StringComparison.Ordinal);
    }

    /// <summary>„Przywróć timing z config.json” zdejmuje nadpisanie czasu, zostawiając nadpisanie nakładki.</summary>
    [Fact]
    public async Task ResetTimingClearsOnlyTheTimingLayer()
    {
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig { EscalationAfterAckMs = 60000 },
            Overlay = new OverlayConfig { Scale = 1.4 },
            EditedAt = new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero),
            EditedBy = "PC-A",
        };
        TimingConfig? timing = new();
        OverlayConfig? overlay = null;
        var viewModel = Create(Input(document: document), (t, o, _) => { timing = t; overlay = o; return Task.CompletedTask; });

        await viewModel.ResetTimingCommand.ExecuteAsync(null);

        Assert.Null(timing);
        Assert.NotNull(overlay);
        Assert.Equal(1.4, overlay!.Scale, 3);
    }

    [Fact]
    public async Task SavedIsRaisedOnlyAfterASuccessfulSaveOrReset()
    {
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig { EscalationAfterAckMs = 60000 },
            EditedAt = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero),
            EditedBy = "PC-A",
        };
        bool fail = false;
        var viewModel = Create(Input(document: document),
            (_, _, _) => fail ? throw new IOException("Plik jest używany przez inny proces.") : Task.CompletedTask);
        var saved = new List<string>();
        viewModel.Saved += saved.Add;

        await viewModel.SaveCommand.ExecuteAsync(null); // brak zmian
        Field(viewModel, "ui.overlay.layout").Text = "spirala";
        await viewModel.SaveCommand.ExecuteAsync(null); // błąd walidacji
        Field(viewModel, "ui.overlay.layout").Text = "two-rows";
        fail = true;
        await viewModel.SaveCommand.ExecuteAsync(null); // błąd zapisu
        Assert.Empty(saved);
        Assert.StartsWith("Nie zapisano wspólnej konfiguracji", viewModel.Message, StringComparison.Ordinal);

        fail = false;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["Zapisano ustawienia"], saved);

        await viewModel.ResetTimingCommand.ExecuteAsync(null);
        Assert.Equal(["Zapisano ustawienia", "Przywrócono ustawienia"], saved);
    }

    [Fact]
    public void ViewHasNoDedicatedReportSectionNorResetReportButton()
    {
        string xaml = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "SharedConfigView.xaml"));

        Assert.DoesNotContain("ReportSourceText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetReportCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportFields", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Przywróć raport", xaml, StringComparison.Ordinal);
        Assert.Matches("x:Name=\"DetailDescriptionText\"[^>]*Visibility=\"\\{Binding SelectedField.Description, Converter=\\{StaticResource NullToVisibility\\}\\}\"", xaml);
        Assert.Matches("x:Name=\"DetailKeyText\"[^>]*Visibility=\"\\{Binding SelectedField.KeyDiffersFromLabel, Converter=\\{StaticResource BoolToVisibility\\}\\}\"", xaml);
    }

    [Fact]
    public async Task ResetTimingAndOverlayPassOnlyTheOtherSection()
    {
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig(),
            Overlay = new OverlayConfig(),
            EditedAt = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero),
            EditedBy = "PC-A",
        };
        var calls = new List<(TimingConfig? Timing, OverlayConfig? Overlay)>();
        var viewModel = Create(Input(document: document), (t, o, _) => { calls.Add((t, o)); return Task.CompletedTask; });

        await viewModel.ResetTimingCommand.ExecuteAsync(null);
        await viewModel.ResetOverlayCommand.ExecuteAsync(null);

        Assert.Equal(2, calls.Count);
        Assert.Null(calls[0].Timing);
        Assert.NotNull(calls[0].Overlay);
        Assert.NotNull(calls[1].Timing);
        Assert.Null(calls[1].Overlay);
    }

    /// <summary>„Przywróć nakładkę z config.json” działa symetrycznie.</summary>
    [Fact]
    public async Task ResetOverlayClearsOnlyTheOverlayLayer()
    {
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig { EscalationAfterAckMs = 60000 },
            Overlay = new OverlayConfig { Scale = 1.4 },
            EditedAt = new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero),
            EditedBy = "PC-A",
        };
        TimingConfig? timing = null;
        OverlayConfig? overlay = new();
        var viewModel = Create(Input(document: document), (t, o, _) => { timing = t; overlay = o; return Task.CompletedTask; });

        await viewModel.ResetOverlayCommand.ExecuteAsync(null);

        Assert.Null(overlay);
        Assert.NotNull(timing);
        Assert.Equal(60000, timing!.EscalationAfterAckMs);
    }

    /// <summary>„Źródło wartości” mówi wprost, czy sekcja jest nadpisana wspólną konfiguracją, czy nie.</summary>
    [Fact]
    public void SourceTextNamesWhoAndWhenOverrodeTheSection()
    {
        var edited = new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero);
        var document = new SharedConfigDocument
        {
            Timing = new TimingConfig(),
            EditedAt = edited,
            EditedBy = "PC-A",
        };

        var viewModel = Create(Input(document: document));

        Assert.Contains("wspólna konfiguracja", viewModel.TimingSourceText, StringComparison.Ordinal);
        Assert.Contains(edited.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.CurrentCulture),
            viewModel.TimingSourceText, StringComparison.Ordinal);
        Assert.Contains("PC-A", viewModel.TimingSourceText, StringComparison.Ordinal);
        // Nakładki nikt nie nadpisał — wartości pochodzą z warstwy niżej.
        Assert.Contains("config.json", viewModel.OverlaySourceText, StringComparison.Ordinal);
    }

    /// <summary>Błąd odczytu pliku wspólnej konfiguracji i zdanie o grupie trafiają do widoku.</summary>
    [Fact]
    public void LoadErrorAndGroupSentenceReachTheView()
    {
        var viewModel = Create(Input(syncStatus: "Konfiguracja jest wspólna dla grupy; zgodna z 1 z 2 komputerami.",
            loadError: "Nie można wczytać wspólnej konfiguracji: plik uszkodzony.", inGroup: true,
            path: @"C:\ProgramData\MaszynyAlert\shared-config\shared-config.json"));

        Assert.Contains("zgodna z 1 z 2", viewModel.SyncStatus, StringComparison.Ordinal);
        Assert.Contains("plik uszkodzony", viewModel.LoadError, StringComparison.Ordinal);
        Assert.Contains("shared-config.json", viewModel.FilePathText, StringComparison.Ordinal);
    }

    /// <summary>Niezapisana zmiana jest widoczna dla widoku i znika po zapisie.</summary>
    [Fact]
    public async Task UnsavedChangesAreTrackedAndClearedBySave()
    {
        var viewModel = Create(Input());
        Assert.False(viewModel.HasUnsavedChanges);

        Field(viewModel, "timing.idlePopupSeconds").Text = "10";
        Assert.True(viewModel.HasUnsavedChanges);

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    /// <summary>
    /// Zmiana na innym komputerze grupy NIE kasuje tego, co administrator właśnie wpisuje — zamiast tego
    /// zakładka mówi, że trzeba kliknąć „Odśwież”.
    /// </summary>
    [Fact]
    public void ExternalChangeDoesNotOverwriteUnsavedEntries()
    {
        AppConfig first = BaseConfig(new TimingConfig { EscalationAfterAckMs = 240000 });
        AppConfig second = BaseConfig(new TimingConfig { EscalationAfterAckMs = 90000 });
        AppConfig current = first;
        var viewModel = new SharedConfigViewModel(() => Input(current, current), (_, _, _) => Task.CompletedTask);

        Field(viewModel, "timing.escalationAfterAckMs").Text = "7";
        current = second;
        // Samo pokazanie zakładki (bez zmiany z sieci) nie nadpisuje wpisów i nie straszy komunikatem.
        viewModel.RefreshFromOutside(changedElsewhere: false);
        Assert.Equal("7", Field(viewModel, "timing.escalationAfterAckMs").Text);
        Assert.Equal("", viewModel.Message);

        viewModel.RefreshFromOutside(changedElsewhere: true);

        Assert.Equal("7", Field(viewModel, "timing.escalationAfterAckMs").Text);
        Assert.Contains("Odśwież", viewModel.Message, StringComparison.Ordinal);

        // Po świadomym „Odśwież” wchodzą wartości z innego komputera.
        viewModel.RefreshCommand.Execute(null);
        Assert.Equal("1.5", Field(viewModel, "timing.escalationAfterAckMs").Text);
        Assert.False(viewModel.HasUnsavedChanges);
    }
}
