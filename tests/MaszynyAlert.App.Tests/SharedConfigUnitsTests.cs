using System.IO;
using System.Reflection;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class SharedConfigUnitsTests
{
    private static readonly string[] MinuteKeys =
    [
        "timing.idleAlertAfterMs", "timing.idleAtZeroAfterMs", "timing.escalationAfterAckMs", "timing.alertSoundRepeatMs",
        "timing.offlineAlertAfterMs", "timing.sameMessageRejoinMs", "timing.wrongScreenRemindAfterMs",
    ];

    private static AppConfig BaseConfig(TimingConfig? timing = null, OverlayConfig? overlay = null) =>
        new()
        {
            Enabled = false,
            Timing = timing ?? new TimingConfig(),
            Ui = new UiConfig { Overlay = overlay ?? new OverlayConfig() },
        };

    private static SharedConfigViewInput Input(AppConfig? baseConfig = null, AppConfig? effective = null,
        SharedConfigDocument? document = null)
    {
        AppConfig @base = baseConfig ?? BaseConfig();
        return new SharedConfigViewInput(@base, effective ?? @base, document, "", null, false, null);
    }

    private sealed class Recorder
    {
        public List<(TimingConfig? Timing, OverlayConfig? Overlay)> Calls { get; } = [];

        public Task Apply(TimingConfig? timing, OverlayConfig? overlay, CancellationToken ct)
        {
            Calls.Add((timing, overlay));
            return Task.CompletedTask;
        }
    }

    private static SharedConfigViewModel Create(SharedConfigViewInput input, Recorder? recorder = null) =>
        new(() => input, (recorder ?? new Recorder()).Apply);

    private static SharedConfigField Field(SharedConfigViewModel viewModel, string key) =>
        viewModel.Fields.Single(f => f.Key == key);

    private static SharedConfigSection Section(SharedConfigViewModel viewModel, string title) =>
        viewModel.Sections.Single(s => s.Title == title);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    // ---------------------------------------------------------------------------------------------
    // Sekcje
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SixSectionsInOrderAndEverySettingInExactlyOneOfThem()
    {
        var viewModel = Create(Input());

        Assert.Equal(["Postój maszyny", "Alarm i dźwięk", "Połączenie z maszyną", "Ekran HMI i OCR", "Nakładka", "Maszyny"],
            viewModel.Sections.Select(s => s.Title));
        Assert.Equal([3, 6, 9, 7, 6, 0], viewModel.Sections.Select(s => s.Fields.Count));
        Assert.NotEqual("", Section(viewModel, "Maszyny").EmptyText);

        string[] expected = typeof(TimingConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => "timing." + Camel(p.Name))
            .Concat(typeof(OverlayConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => "ui.overlay." + Camel(p.Name)))
            .ToArray();
        string[] inSections = viewModel.Sections.SelectMany(s => s.Fields).Select(f => f.Key).ToArray();

        Assert.Equal(inSections.Length, inSections.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Order(StringComparer.Ordinal), inSections.Order(StringComparer.Ordinal));
        Assert.All(viewModel.Sections.SelectMany(s => s.Fields), f => Assert.Same(f, Field(viewModel, f.Key)));
        Assert.All(Section(viewModel, "Nakładka").Fields, f => Assert.StartsWith("ui.overlay.", f.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void StartSelectsFirstSectionAndItsFirstField()
    {
        var viewModel = Create(Input());

        Assert.Same(viewModel.Sections[0], viewModel.SelectedSection);
        Assert.Same(viewModel.Sections[0].Fields[0], viewModel.SelectedField);
        Assert.True(viewModel.SelectedField!.IsSelected);
        Assert.Single(viewModel.Fields, f => f.IsSelected);
    }

    [Fact]
    public void SelectingAFieldMovesTheHighlightAndChangingSectionSelectsItsFirstField()
    {
        var viewModel = Create(Input());
        SharedConfigField first = viewModel.SelectedField!;
        SharedConfigField second = viewModel.Sections[0].Fields[1];

        viewModel.SelectedField = second;
        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);

        SharedConfigSection connection = Section(viewModel, "Połączenie z maszyną");
        viewModel.SelectedSection = connection;
        Assert.Same(connection.Fields[0], viewModel.SelectedField);
        Assert.False(second.IsSelected);
        Assert.Single(viewModel.Fields, f => f.IsSelected);

        // Pole z wybranej sekcji zostaje zaznaczone, gdy sekcja jest wybrana ponownie.
        viewModel.SelectedField = connection.Fields[4];
        viewModel.SelectedSection = connection;
        Assert.Same(connection.Fields[4], viewModel.SelectedField);
    }

    // ---------------------------------------------------------------------------------------------
    // Jednostki pól i format
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryMillisecondFieldIsShownInSecondsOrMinutesAndTheRestKeepsItsUnit()
    {
        var viewModel = Create(Input());

        foreach (SharedConfigField field in viewModel.Fields.Where(f => f.Key.StartsWith("timing.", StringComparison.Ordinal)))
        {
            string expected = field.Key switch
            {
                "timing.idlePopupSeconds" => "s",
                "timing.ackMemoryHours" => "godz.",
                "timing.ocrMaxAttempts" => "prób",
                "timing.offlineAlertSound" or "timing.wrongScreenAlertSound" => "",
                _ when MinuteKeys.Contains(field.Key) => "min",
                _ when field.Key.EndsWith("Ms", StringComparison.Ordinal) => "s",
                _ => throw new InvalidOperationException("Nieznane pole: " + field.Key),
            };
            Assert.True(expected == field.Unit, $"{field.Key}: jednostka „{field.Unit}”, oczekiwano „{expected}”.");
            if (field.Key.EndsWith("Ms", StringComparison.Ordinal) && field.Key != "timing.reconnectBackoffMs")
                Assert.Equal(SharedConfigFieldKind.Decimal, field.Kind);
        }

        Assert.Equal(SharedConfigFieldKind.NumberList, Field(viewModel, "timing.reconnectBackoffMs").Kind);
    }

    /// <summary>Opisy i podpowiedzi „z config.json” mówią w jednostce pola — nigdzie liczby w milisekundach.</summary>
    [Fact]
    public void DescriptionsAndHintsDoNotTalkInMilliseconds()
    {
        var viewModel = Create(Input());

        foreach (SharedConfigField field in viewModel.Fields)
        {
            Assert.DoesNotMatch(@"\d\s*ms\b", field.Description);
            Assert.DoesNotMatch(@"\bms\b", field.DefaultHint);
            Assert.DoesNotMatch(@"\d{4,}", field.DefaultHint);
        }
    }

    [Theory]
    [InlineData("timing.pollIntervalMs", 1000, "1")]
    [InlineData("timing.pollIntervalMs", 100, "0.1")]
    [InlineData("timing.pollIntervalMs", 1234, "1.234")]
    [InlineData("timing.fullFrameIntervalMs", 60000, "60")]
    [InlineData("timing.escalationAfterAckMs", 240000, "4")]
    [InlineData("timing.escalationAfterAckMs", 125000, "2.0833")]
    [InlineData("timing.escalationAfterAckMs", 90000, "1.5")]
    [InlineData("timing.escalationAfterAckMs", 0, "0")]
    [InlineData("timing.sameMessageRejoinMs", 1, "0")]
    [InlineData("timing.alertSoundRepeatMs", 1000, "0.0167")]
    public void MillisecondsAreFormattedInTheFieldUnitWithADot(string key, int milliseconds, string expected)
    {
        TimingConfig timing = With(new TimingConfig { EpisodeBreakAfterMs = 0 }, key, milliseconds);
        var viewModel = Create(Input(BaseConfig(timing)));

        Assert.Equal(expected, Field(viewModel, key).Text);
        Assert.False(Field(viewModel, key).IsChanged);
    }

    [Fact]
    public void FlagHintSaysOnOrOff()
    {
        var viewModel = Create(Input(BaseConfig(new TimingConfig { OfflineAlertSound = true })));

        Assert.Equal("Z config.json / profilu grupy: włączone", Field(viewModel, "timing.offlineAlertSound").DefaultHint);
        Assert.Equal("Z config.json / profilu grupy: wyłączone", Field(viewModel, "timing.wrongScreenAlertSound").DefaultHint);
        Assert.Equal("Z config.json / profilu grupy: 1 s", Field(viewModel, "timing.pollIntervalMs").DefaultHint);
        Assert.Equal("Z config.json / profilu grupy: 2; 5; 15; 30 s", Field(viewModel, "timing.reconnectBackoffMs").DefaultHint);
    }

    // ---------------------------------------------------------------------------------------------
    // Zaokrąglenie przy zapisie — wartość niezmieniona wraca identyczna
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Wartości, których nie da się pokazać dokładnie w minutach (4 miejsca) — po zapisie innego pola wracają do
    /// <c>apply</c> co do milisekundy.
    /// </summary>
    [Theory]
    [InlineData(125000)]
    [InlineData(1)]
    [InlineData(59999)]
    [InlineData(1001)]
    [InlineData(100003)]
    public async Task UnchangedValueIsSavedExactlyEvenWhenAnotherFieldChanged(int milliseconds)
    {
        var timing = new TimingConfig
        {
            EscalationAfterAckMs = milliseconds, OfflineAlertAfterMs = milliseconds, WrongScreenRemindAfterMs = milliseconds,
            AlertSoundRepeatMs = Math.Max(1000, milliseconds), PollIntervalMs = Math.Max(100, milliseconds % 7919),
        };
        var recorder = new Recorder();
        var viewModel = Create(Input(BaseConfig(timing)), recorder);

        Field(viewModel, "ui.overlay.scale").Text = "1.25";
        await viewModel.SaveCommand.ExecuteAsync(null);

        TimingConfig saved = Assert.Single(recorder.Calls).Timing!;
        Assert.Equal(milliseconds, saved.EscalationAfterAckMs);
        Assert.Equal(milliseconds, saved.OfflineAlertAfterMs);
        Assert.Equal(milliseconds, saved.WrongScreenRemindAfterMs);
        Assert.Equal(timing.AlertSoundRepeatMs, saved.AlertSoundRepeatMs);
        Assert.Equal(timing.PollIntervalMs, saved.PollIntervalMs);
    }

    /// <summary>Pole zmienione na czas, którego nie ma w ms dokładnie, i przywrócone ręcznie = „niezmienione”.</summary>
    [Fact]
    public async Task FieldRetypedToTheSameTextCountsAsUnchanged()
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(BaseConfig(new TimingConfig { EscalationAfterAckMs = 125000 })), recorder);
        SharedConfigField field = Field(viewModel, "timing.escalationAfterAckMs");

        field.Text = "3";
        Assert.True(field.IsChanged);
        field.Text = "2.0833";
        Assert.False(field.IsChanged);

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Empty(recorder.Calls);
        Assert.Equal("Brak zmian do zapisania.", viewModel.Message);

        // Zmiana innego pola: przepisane pole wraca co do ms.
        Field(viewModel, "timing.idlePopupSeconds").Text = "5";
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(125000, Assert.Single(recorder.Calls).Timing!.EscalationAfterAckMs);
    }

    /// <summary>Pole świadomie przepisane na tę liczbę minut = ta liczba minut (zaokrąglona do pełnych ms).</summary>
    [Theory]
    [InlineData("2,0833", 124998)]
    [InlineData("2.08333", 125000)]
    [InlineData(" 2 ", 120000)]
    [InlineData("1,5", 90000)]
    [InlineData("1.5", 90000)]
    [InlineData("0", 0)]
    public async Task ChangedMinuteFieldIsConvertedToWholeMilliseconds(string entered, int expected)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(BaseConfig(new TimingConfig { EscalationAfterAckMs = 125000 })), recorder);

        Field(viewModel, "timing.escalationAfterAckMs").Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        if (expected == 125000)
        {
            Assert.Empty(recorder.Calls);
            Assert.False(Field(viewModel, "timing.escalationAfterAckMs").IsChanged);
            return;
        }
        Assert.Equal(expected, Assert.Single(recorder.Calls).Timing!.EscalationAfterAckMs);
    }

    [Theory]
    [InlineData("0,5", 500)]
    [InlineData("0.5", 500)]
    [InlineData("0.0005", 1)]
    [InlineData("0.0015", 2)]
    [InlineData("1e3", 1000000)]
    [InlineData("12.3456", 12346)]
    public async Task ChangedSecondFieldIsConvertedToWholeMillisecondsAwayFromZero(string entered, int expected)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);

        Field(viewModel, "timing.ocrRetryIntervalMs").Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, Assert.Single(recorder.Calls).Timing!.OcrRetryIntervalMs);
    }

    /// <summary>
    /// Własność: dla losowych (poprawnych) wartości w ms każde pole czasu, którego administrator nie dotknął, wraca
    /// do zapisu identyczne — także gdy zmieniono inne pole tej samej i innej sekcji. Ziarno stałe (powtarzalność).
    /// </summary>
    [Fact]
    public async Task Property_RandomMillisecondsSurviveSaveOfAnotherField()
    {
        var random = new Random(20261005);
        for (int run = 0; run < 300; run++)
        {
            TimingConfig timing = RandomValidTiming(random);
            ConfigLoader.ValidateTiming(timing);
            var recorder = new Recorder();
            var viewModel = Create(Input(BaseConfig(timing)), recorder);

            // Raz zmiana w innej sekcji (nakładka), raz w tej samej grupie (pole bez jednostki czasu).
            if (run % 2 == 0) Field(viewModel, "ui.overlay.opacity").Text = "0.7";
            else Field(viewModel, "timing.ocrMaxAttempts").Text = "4";
            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.True(recorder.Calls.Count == 1, $"Przebieg {run}: zapis nie przeszedł — {viewModel.Message}");
            TimingConfig saved = recorder.Calls[0].Timing!;
            foreach (PropertyInfo property in typeof(TimingConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name == nameof(TimingConfig.OcrMaxAttempts)) continue;
                object? before = property.GetValue(timing);
                object? after = property.GetValue(saved);
                if (before is int[] a)
                    Assert.True(a.SequenceEqual((int[])after!), $"Przebieg {run}: {property.Name} {string.Join(",", a)} → {string.Join(",", (int[])after!)}");
                else
                    Assert.True(Equals(before, after), $"Przebieg {run}: {property.Name} {before} → {after}");
            }
        }
    }

    /// <summary>
    /// Własność dla pól w sekundach: pokazana liczba sekund (3 miejsca) jest DOKŁADNA, więc nawet przepisana ręcznie
    /// (ten sam tekst wpisany od nowa po innym) daje tę samą liczbę ms.
    /// </summary>
    [Fact]
    public async Task Property_SecondsTextRetypedGivesTheSameMilliseconds()
    {
        var random = new Random(51);
        for (int run = 0; run < 200; run++)
        {
            int ms = random.Next(0, 3_600_000);
            var recorder = new Recorder();
            var viewModel = Create(Input(BaseConfig(new TimingConfig { OcrRetryIntervalMs = ms })), recorder);
            SharedConfigField field = Field(viewModel, "timing.ocrRetryIntervalMs");
            string shown = field.Text;

            field.Text = shown + "0"; // zmiana…
            field.Text = shown.Contains('.') ? shown + "0" : shown + ".0"; // …i ta sama liczba zapisana inaczej
            Assert.False(field.IsChanged);
            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.Empty(recorder.Calls);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Błędne dane wejściowe
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("timing.escalationAfterAckMs", "abc", "Wpisz liczbę minut, np. 2 albo 1,5.")]
    [InlineData("timing.pollIntervalMs", "abc", "Wpisz liczbę sekund, np. 10 albo 0,5.")]
    [InlineData("timing.pollIntervalMs", "", "Wpisz liczbę sekund, np. 10 albo 0,5.")]
    [InlineData("timing.pollIntervalMs", "1 s", "Wpisz liczbę sekund, np. 10 albo 0,5.")]
    [InlineData("timing.escalationAfterAckMs", "99999999", "Liczba minut jest za duża.")]
    [InlineData("timing.pollIntervalMs", "3000000", "Liczba sekund jest za duża.")]
    [InlineData("timing.escalationAfterAckMs", "-99999999", "Liczba minut jest za duża.")]
    public async Task WrongEntryStopsSaveWithAMessageOnTheField(string key, string entered, string expected)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);

        Field(viewModel, key).Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Calls);
        Assert.Equal(expected, Field(viewModel, key).Error);
        Assert.Contains("popraw pola", viewModel.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Liczba, którą <c>decimal</c> przyjmuje, ale która po przeliczeniu na ms nie mieści się w <c>decimal</c>
    /// (np. „1e25” minut = 6·10²⁹ ms) — ma dać ten sam błąd „za duża” przy polu co „99999999”, a nie wyjątek z zapisu.
    /// </summary>
    [Theory]
    [InlineData("timing.escalationAfterAckMs", "1e25", "Liczba minut jest za duża.")]
    [InlineData("timing.escalationAfterAckMs", "79228162514264337593543950335", "Liczba minut jest za duża.")]
    [InlineData("timing.pollIntervalMs", "1e26", "Liczba sekund jest za duża.")]
    public async Task HugeNumberBeyondDecimalRangeIsAFieldErrorNotACrash(string key, string entered, string expected)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);

        Field(viewModel, key).Text = entered;
        Exception? thrown = await Record.ExceptionAsync(() => viewModel.SaveCommand.ExecuteAsync(null));

        Assert.Null(thrown);
        Assert.Empty(recorder.Calls);
        Assert.Equal(expected, Field(viewModel, key).Error);
    }

    [Fact]
    public async Task HugeNumberInTheSecondsListIsAFieldErrorNotACrash()
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);

        Field(viewModel, "timing.reconnectBackoffMs").Text = "2; 1e26";
        Exception? thrown = await Record.ExceptionAsync(() => viewModel.SaveCommand.ExecuteAsync(null));

        Assert.Null(thrown);
        Assert.Empty(recorder.Calls);
        Assert.True(Field(viewModel, "timing.reconnectBackoffMs").HasError);
    }

    // ---------------------------------------------------------------------------------------------
    // Lista sekund (reconnectBackoffMs)
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2; 5; 15; 30", new[] { 2000, 5000, 15000, 30000 })]
    [InlineData("2,5; 5", new[] { 2500, 5000 })]
    [InlineData("2.5;5", new[] { 2500, 5000 })]
    [InlineData("2, 5", new[] { 2000, 5000 })]
    [InlineData("2,5", new[] { 2000, 5000 })] // bez średnika przecinek rozdziela (jak dotąd w config.json)
    [InlineData("2;;5", new[] { 2000, 5000 })]
    [InlineData(" 7 ", new[] { 7000 })]
    [InlineData("0", new[] { 0 })]
    public async Task SecondsListIsParsedToMilliseconds(string entered, int[] expected)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(BaseConfig(new TimingConfig { ReconnectBackoffMs = [1000] })), recorder);

        Field(viewModel, "timing.reconnectBackoffMs").Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, Assert.Single(recorder.Calls).Timing!.ReconnectBackoffMs);
    }

    [Theory]
    [InlineData(";;", "Wpisz co najmniej jedną liczbę sekund, np. 2; 5; 15; 30.")]
    [InlineData("", "Wpisz co najmniej jedną liczbę sekund, np. 2; 5; 15; 30.")]
    [InlineData("   ", "Wpisz co najmniej jedną liczbę sekund, np. 2; 5; 15; 30.")]
    [InlineData("abc; 2", "Wpisz liczby sekund oddzielone średnikiem, np. 2; 5; 15; 30.")]
    public async Task WrongSecondsListStopsSave(string entered, string expected)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);

        Field(viewModel, "timing.reconnectBackoffMs").Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Calls);
        Assert.Equal(expected, Field(viewModel, "timing.reconnectBackoffMs").Error);
    }

    [Fact]
    public void ListShownWithFractionsRoundTripsThroughTheSemicolonForm()
    {
        var viewModel = Create(Input(BaseConfig(new TimingConfig { ReconnectBackoffMs = [2500, 500, 30000] })));

        Assert.Equal("2.5; 0.5; 30", Field(viewModel, "timing.reconnectBackoffMs").Text);
    }

    // ---------------------------------------------------------------------------------------------
    // Komunikaty walidacji (ConfigLoader.ValidateTiming) w jednostkach pól — każdy komunikat
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Każda reguła <see cref="ConfigLoader.ValidateTiming"/> wywołana wartością tuż za granicą: komunikat ma
    /// liczby w jednostce pola (bez „ms”), trafia do pola, którego dotyczy, a zapis nie idzie. Pilnuje wzorców
    /// tekstu, na których opiera się <c>ToFieldUnits</c> — zmiana treści w Core bez poprawki Panelu zaczerwieni test.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidationCases))]
    public async Task EveryTimingValidationMessageIsInTheFieldUnits(string key, string entered, string expected, string? otherKey, string? otherText)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);
        if (otherKey is not null) Field(viewModel, otherKey).Text = otherText!;

        Field(viewModel, key).Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(recorder.Calls);
        Assert.Equal(expected, Field(viewModel, key).Error);
        Assert.Equal("Nie zapisano: " + expected, viewModel.Message);
        Assert.DoesNotMatch(@"\bms\b", viewModel.Message);
        Assert.Same(Field(viewModel, key), viewModel.SelectedField);
        Assert.Single(viewModel.Fields, f => f.HasError);
    }

    public static TheoryData<string, string, string, string?, string?> ValidationCases() => new()
    {
        { "timing.pollIntervalMs", "0.099", "Pole 'timing.pollIntervalMs' (0.099 s) musi wynosić co najmniej 0.1 s.", null, null },
        { "timing.pollIntervalMs", "0.0004", "Pole 'timing.pollIntervalMs' (0 s) musi wynosić co najmniej 0.1 s.", null, null },
        { "timing.pollingModeIntervalMs", "0.499", "Pole 'timing.pollingModeIntervalMs' (0.499 s) musi wynosić co najmniej 0.5 s.", null, null },
        { "timing.fullFrameIntervalMs", "0.999", "Pole 'timing.fullFrameIntervalMs' (0.999 s) nie może być mniejsze niż 'timing.pollIntervalMs' (1 s).", null, null },
        { "timing.episodeCloseAfterMs", "0.999", "Pole 'timing.episodeCloseAfterMs' (0.999 s) musi wynosić co najmniej 1 s.", null, null },
        { "timing.episodeBreakAfterMs", "-0.001", "Pole 'timing.episodeBreakAfterMs' (-0.001 s) nie może być ujemne.", null, null },
        { "timing.episodeBreakAfterMs", "30.001", "Pole 'timing.episodeBreakAfterMs' (30.001 s) nie może być większe niż 'timing.episodeCloseAfterMs' (30 s).", null, null },
        { "timing.sameMessageRejoinMs", "-0.5", "Pole 'timing.sameMessageRejoinMs' (-0.5 min) nie może być ujemne (0 = bez łączenia powracającego komunikatu).", null, null },
        { "timing.sameMessageRejoinMs", "0.1", "Pole 'timing.sameMessageRejoinMs' (0.1 min) nie może być mniejsze niż 'timing.episodeBreakAfterMs' (10 s).", null, null },
        { "timing.escalationAfterAckMs", "-1", "Pole 'timing.escalationAfterAckMs' (-1 min) nie może być ujemne (0 = eskalacja wyłączona).", null, null },
        { "timing.alertSoundRepeatMs", "0.01", "Pole 'timing.alertSoundRepeatMs' (0.01 min) musi wynosić co najmniej 0.0167 min.", null, null },
        { "timing.mainScreenCheckIntervalMs", "0.999", "Pole 'timing.mainScreenCheckIntervalMs' (0.999 s) musi wynosić co najmniej 1 s.", null, null },
        { "timing.mainScreenOcrGraceMs", "4.999", "Pole 'timing.mainScreenOcrGraceMs' (4.999 s) musi wynosić co najmniej 5 s.", null, null },
        { "timing.wrongScreenAlertAfterMs", "-0.001", "Pole 'timing.wrongScreenAlertAfterMs' (-0.001 s) nie może być ujemne (0 = alert od razu).", null, null },
        { "timing.wrongScreenRemindAfterMs", "-1", "Pole 'timing.wrongScreenRemindAfterMs' (-1 min) nie może być ujemne (0 = bez ponawiania).", null, null },
        { "timing.offlineAlertAfterMs", "-2", "Pole 'timing.offlineAlertAfterMs' (-2 min) nie może być ujemne.", null, null },
        { "timing.ocrMaxAttempts", "-1", "Pole 'timing.ocrMaxAttempts' (-1) nie może być ujemne.", null, null },
        { "timing.ocrRetryIntervalMs", "-2", "Pole 'timing.ocrRetryIntervalMs' (-2 s) nie może być ujemne.", null, null },
        { "timing.connectTimeoutMs", "0.499", "Pole 'timing.connectTimeoutMs' (0.499 s) musi wynosić co najmniej 0.5 s.", null, null },
        { "timing.readTimeoutMs", "0.999", "Pole 'timing.readTimeoutMs' (0.999 s) musi wynosić co najmniej 1 s.", "timing.idleUpdateTimeoutMs", "0.5" },
        { "timing.idleUpdateTimeoutMs", "0.099", "Pole 'timing.idleUpdateTimeoutMs' (0.099 s) musi mieścić się w zakresie od 0.1 s do wartości 'timing.readTimeoutMs' (30 s) włącznie.", null, null },
        { "timing.idleUpdateTimeoutMs", "30.001", "Pole 'timing.idleUpdateTimeoutMs' (30.001 s) musi mieścić się w zakresie od 0.1 s do wartości 'timing.readTimeoutMs' (30 s) włącznie.", null, null },
        { "timing.reconnectBackoffMs", "2; -1", "Pole 'timing.reconnectBackoffMs' zawiera ujemną wartość (-1 s) na pozycji 2.", null, null },
        { "timing.idleAlertAfterMs", "0.1", "Pole 'timing.idleAlertAfterMs' (0.1 min) musi wynosić co najmniej 0.1667 min.", null, null },
        { "timing.idleAtZeroAfterMs", "0.4999", "Pole 'timing.idleAtZeroAfterMs' (0.4999 min) musi wynosić co najmniej 0.5 min.", null, null },
        { "timing.idlePopupSeconds", "61", "Pole 'timing.idlePopupSeconds' (61) musi mieścić się w zakresie 0..60 (0 = pop-up czeka na skasowanie przez operatora).", null, null },
        { "timing.idlePopupSeconds", "-1", "Pole 'timing.idlePopupSeconds' (-1) musi mieścić się w zakresie 0..60 (0 = pop-up czeka na skasowanie przez operatora).", null, null },
    };

    /// <summary>Ta sama granica od strony „dozwolone” — wartość graniczna w jednostce pola przechodzi.</summary>
    [Theory]
    [InlineData("timing.pollIntervalMs", "0.1", null, null)]
    [InlineData("timing.pollingModeIntervalMs", "0.5", null, null)]
    [InlineData("timing.fullFrameIntervalMs", "1", null, null)]
    [InlineData("timing.episodeCloseAfterMs", "1", "timing.episodeBreakAfterMs", "1")]
    [InlineData("timing.episodeBreakAfterMs", "0", null, null)]
    [InlineData("timing.episodeBreakAfterMs", "30", null, null)]
    [InlineData("timing.sameMessageRejoinMs", "0", null, null)]
    [InlineData("timing.sameMessageRejoinMs", "0.1667", null, null)]
    [InlineData("timing.escalationAfterAckMs", "0", null, null)]
    [InlineData("timing.alertSoundRepeatMs", "0.0167", null, null)]
    [InlineData("timing.mainScreenCheckIntervalMs", "1", null, null)]
    [InlineData("timing.mainScreenOcrGraceMs", "5", null, null)]
    [InlineData("timing.wrongScreenAlertAfterMs", "0", null, null)]
    [InlineData("timing.wrongScreenRemindAfterMs", "0", null, null)]
    [InlineData("timing.offlineAlertAfterMs", "0", null, null)]
    [InlineData("timing.ocrRetryIntervalMs", "0", null, null)]
    [InlineData("timing.connectTimeoutMs", "0.5", null, null)]
    [InlineData("timing.readTimeoutMs", "1", "timing.idleUpdateTimeoutMs", "1")]
    [InlineData("timing.idleUpdateTimeoutMs", "0.1", null, null)]
    [InlineData("timing.idleUpdateTimeoutMs", "30", null, null)]
    [InlineData("timing.reconnectBackoffMs", "0; 0", null, null)]
    [InlineData("timing.idleAlertAfterMs", "0.1667", null, null)]
    [InlineData("timing.idleAtZeroAfterMs", "0.5", null, null)]
    [InlineData("timing.idlePopupSeconds", "60", null, null)]
    public async Task BoundaryValueInTheFieldUnitIsAccepted(string key, string entered, string? otherKey, string? otherText)
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);
        if (otherKey is not null) Field(viewModel, otherKey).Text = otherText!;
        if (Field(viewModel, key).Text == entered && otherKey is null) Field(viewModel, "timing.idlePopupSeconds").Text = "5";

        Field(viewModel, key).Text = entered;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(recorder.Calls.Count == 1, viewModel.Message);
        Assert.Equal("", Field(viewModel, key).Error);
    }

    /// <summary>Komunikat z <c>apply</c> (walidacja w orkiestratorze) też jest przeliczany na jednostki pól.</summary>
    [Fact]
    public async Task ValidationMessageFromApplyIsAlsoConverted()
    {
        var viewModel = new SharedConfigViewModel(() => Input(),
            (_, _, _) => throw new ConfigException("Pole 'timing.escalationAfterAckMs' (-60000) nie może być ujemne (0 = eskalacja wyłączona)."));

        Field(viewModel, "timing.idlePopupSeconds").Text = "5";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Pole 'timing.escalationAfterAckMs' (-1 min) nie może być ujemne (0 = eskalacja wyłączona).",
            Field(viewModel, "timing.escalationAfterAckMs").Error);
        Assert.StartsWith("Nie zapisano: ", viewModel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteErrorFromApplyIsReportedWithoutFieldErrors()
    {
        var viewModel = new SharedConfigViewModel(() => Input(),
            (_, _, _) => throw new IOException("Plik zajęty."));

        Field(viewModel, "timing.idlePopupSeconds").Text = "5";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Nie zapisano wspólnej konfiguracji: Plik zajęty.", viewModel.Message);
        Assert.DoesNotContain(viewModel.Fields, f => f.HasError);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData("Pole 'timing.pollIntervalMs' (50) musi wynosić co najmniej 100 ms.", "Pole 'timing.pollIntervalMs' (0.05 s) musi wynosić co najmniej 0.1 s.")]
    [InlineData("Pole 'timing.idleAlertAfterMs' (6000) musi wynosić co najmniej 10000 ms.", "Pole 'timing.idleAlertAfterMs' (0.1 min) musi wynosić co najmniej 0.1667 min.")]
    [InlineData("Coś innego bez kluczy (50 ms).", "Coś innego bez kluczy (50 ms).")]
    [InlineData("ui.overlay.opacity musi być w zakresie 0,2–1,0 (jest: 5).", "ui.overlay.opacity musi być w zakresie 0,2–1,0 (jest: 5).")]
    public void ToFieldUnitsConvertsOnlyMessagesAboutDurationFields(string message, string expected)
    {
        var viewModel = Create(Input());

        Assert.Equal(expected, viewModel.ToFieldUnits(message));
    }

    // ---------------------------------------------------------------------------------------------
    // Przejście do sekcji z błędem
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ErrorInAnotherSectionSelectsThatSectionAndField()
    {
        var viewModel = Create(Input());
        Assert.Equal("Postój maszyny", viewModel.SelectedSection!.Title);

        Field(viewModel, "timing.pollIntervalMs").Text = "0.05";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Połączenie z maszyną", viewModel.SelectedSection!.Title);
        Assert.Same(Field(viewModel, "timing.pollIntervalMs"), viewModel.SelectedField);
        Assert.True(viewModel.SelectedField!.IsSelected);
    }

    [Fact]
    public async Task ErrorInTheSelectedSectionKeepsTheSectionAndSelectsTheFieldWithError()
    {
        var viewModel = Create(Input());
        SharedConfigSection connection = Section(viewModel, "Połączenie z maszyną");
        viewModel.SelectedSection = connection;
        viewModel.SelectedField = Field(viewModel, "timing.readTimeoutMs");

        Field(viewModel, "timing.connectTimeoutMs").Text = "abc";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Same(connection, viewModel.SelectedSection);
        Assert.Same(Field(viewModel, "timing.connectTimeoutMs"), viewModel.SelectedField);
    }

    [Fact]
    public async Task FormatErrorsInTwoSectionsSelectTheFirstSectionInTheList()
    {
        var viewModel = Create(Input());
        viewModel.SelectedSection = Section(viewModel, "Nakładka");

        Field(viewModel, "timing.ocrRetryIntervalMs").Text = "x";
        Field(viewModel, "timing.escalationAfterAckMs").Text = "y";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Alarm i dźwięk", viewModel.SelectedSection!.Title);
        Assert.Same(Field(viewModel, "timing.escalationAfterAckMs"), viewModel.SelectedField);
        Assert.Equal("błąd", Section(viewModel, "Ekran HMI i OCR").StatusText);
    }

    // ---------------------------------------------------------------------------------------------
    // Znaczniki sekcji
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SectionStatusGoesFromNothingToChangesToErrorAndBack()
    {
        var recorder = new Recorder();
        var viewModel = Create(Input(), recorder);
        SharedConfigSection alarm = Section(viewModel, "Alarm i dźwięk");
        SharedConfigField field = Field(viewModel, "timing.escalationAfterAckMs");
        var raised = new List<string?>();
        alarm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("", alarm.StatusText);
        field.Text = "-1";
        Assert.Equal("zmiany", alarm.StatusText);
        Assert.True(alarm.HasChanges);
        Assert.Contains(nameof(SharedConfigSection.StatusText), raised);
        Assert.All(viewModel.Sections.Where(s => s != alarm), s => Assert.Equal("", s.StatusText));

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal("błąd", alarm.StatusText);
        Assert.True(alarm.HasErrors);

        field.Text = "5";
        Assert.Equal("błąd", alarm.StatusText); // błąd zostaje do następnej próby zapisu
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Single(recorder.Calls);
        Assert.Equal("", alarm.StatusText);
        Assert.False(field.IsChanged);
        Assert.False(alarm.HasChanges);
    }

    [Fact]
    public void RefreshClearsChangesAndErrors()
    {
        var viewModel = Create(Input());
        SharedConfigField field = Field(viewModel, "ui.overlay.scale");

        field.Text = "1.7";
        Assert.Equal("zmiany", Section(viewModel, "Nakładka").StatusText);
        viewModel.RefreshCommand.Execute(null);

        Assert.False(field.IsChanged);
        Assert.Equal("", Section(viewModel, "Nakładka").StatusText);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void SectionSourceTextFollowsTheOverriddenLayer()
    {
        var document = new SharedConfigDocument
        {
            Overlay = new OverlayConfig(), EditedAt = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), EditedBy = "PC-A",
        };
        var viewModel = Create(Input(document: document));

        Assert.Contains("PC-A", Section(viewModel, "Nakładka").SourceText, StringComparison.Ordinal);
        Assert.All(viewModel.Sections.Where(s => s.Title != "Nakładka"),
            s => Assert.Contains("bez nadpisania", s.SourceText, StringComparison.Ordinal));
    }


    /// <summary>
    /// Odświeżenie z sieci przy niezapisanych wpisach (jak dotąd): wpis zostaje, komunikat mówi o „Odśwież”, a po
    /// „Odśwież” wartości z innego komputera są w nowych jednostkach.
    /// </summary>
    [Fact]
    public void RefreshFromOutsideShowsNewValuesInFieldUnits()
    {
        AppConfig current = BaseConfig(new TimingConfig { PollIntervalMs = 1000, EscalationAfterAckMs = 240000 });
        var viewModel = new SharedConfigViewModel(() => Input(current, current), (_, _, _) => Task.CompletedTask);

        Field(viewModel, "timing.pollIntervalMs").Text = "2";
        current = BaseConfig(new TimingConfig { PollIntervalMs = 1500, EscalationAfterAckMs = 150000 });
        viewModel.RefreshFromOutside(changedElsewhere: true);
        Assert.Equal("2", Field(viewModel, "timing.pollIntervalMs").Text);
        Assert.Equal("4", Field(viewModel, "timing.escalationAfterAckMs").Text);

        viewModel.RefreshCommand.Execute(null);
        Assert.Equal("1.5", Field(viewModel, "timing.pollIntervalMs").Text);
        Assert.Equal("2.5", Field(viewModel, "timing.escalationAfterAckMs").Text);
    }

    // ---------------------------------------------------------------------------------------------

    private static TimingConfig With(TimingConfig timing, string key, int value)
    {
        string property = char.ToUpperInvariant(key["timing.".Length]) + key[("timing.".Length + 1)..];
        TimingConfig copy = timing with { };
        typeof(TimingConfig).GetProperty(property)!.SetValue(copy, value);
        return copy;
    }

    private static TimingConfig RandomValidTiming(Random r)
    {
        int poll = r.Next(100, 10_001);
        int close = r.Next(1000, 600_001);
        int brk = r.Next(0, close + 1);
        int rejoin = r.Next(2) == 0 ? 0 : r.Next(Math.Max(1, brk), 3_600_001);
        int read = r.Next(1000, 120_001);
        return new TimingConfig
        {
            PollIntervalMs = poll,
            FullFrameIntervalMs = r.Next(poll, 600_001),
            PollingModeIntervalMs = r.Next(500, 60_001),
            EpisodeCloseAfterMs = close,
            EpisodeBreakAfterMs = brk,
            SameMessageRejoinMs = rejoin,
            EscalationAfterAckMs = r.Next(0, 3_600_001),
            AlertSoundRepeatMs = r.Next(1000, 3_600_001),
            OfflineAlertAfterMs = r.Next(0, 3_600_001),
            ReconnectBackoffMs = Enumerable.Range(0, r.Next(1, 6)).Select(_ => r.Next(0, 120_001)).ToArray(),
            ConnectTimeoutMs = r.Next(500, 60_001),
            ReadTimeoutMs = read,
            IdleUpdateTimeoutMs = r.Next(100, read + 1),
            OcrRetryIntervalMs = r.Next(0, 60_001),
            IdleAlertAfterMs = r.Next(10_000, 3_600_001),
            IdleAtZeroAfterMs = r.Next(30_000, 3_600_001),
            MainScreenCheckIntervalMs = r.Next(1000, 600_001),
            MainScreenOcrGraceMs = r.Next(5000, 600_001),
            WrongScreenAlertAfterMs = r.Next(0, 3_600_001),
            WrongScreenRemindAfterMs = r.Next(0, 3_600_001),
        };
    }
}
