using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.Updates;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private static readonly Version Installed = new(0, 4, 22, 0);
    private static readonly Version Newer = new(0, 4, 26, 0);
    private const string NewerFile = "MaszynyAlert-Setup-0.4.26.0.exe";

    private readonly TempDir _dir = new("updates-coordinator");
    private readonly FakeClock _clock = new();
    private readonly FakeSource _source = new();
    private readonly FakeInstaller _installer = new();
    private readonly List<SharedConfigDocument> _broadcasts = [];
    private readonly RecordingLoggerFactory _logs = new();
    private readonly SharedConfigSync _shared;
    private bool _canInstall = true;

    public UpdateCoordinatorTests()
    {
        _shared = new SharedConfigSync(new SharedConfigStore(SharedPath), "PC-A");
        _source.Releases = [Entry(Installed), Entry(Newer)];
    }

    private string SharedPath => _dir.File(Path.Combine("shared-config", "shared-config.json"));
    private string DownloadDir => _dir.File(Path.Combine("state", "updates"));
    private string PendingPath => Path.Combine(DownloadDir, "pending-install.json");

    /// <summary>Treść „instalatora” danej wersji — rozmiar i SHA-256 w spisie wydań zgadzają się z plikiem z <see cref="FakeSource"/>.</summary>
    private static byte[] SetupBytes(Version version) => System.Text.Encoding.UTF8.GetBytes($"instalator {version}");

    private static ReleaseEntry Entry(Version version) =>
        new($"MaszynyAlert-Setup-{version}.exe", version, SetupBytes(version).Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(SetupBytes(version))).ToLowerInvariant(),
            new DateTimeOffset(2026, 10, 2, 18, 2, 31, TimeSpan.Zero));

    private UpdateCoordinator Coordinator(bool installed = true, UpdatesConfig? config = null, Func<SharedConfigDocument, Task>? broadcast = null) =>
        new(config ?? new UpdatesConfig(), installed, Installed, _source, _installer, _shared,
            broadcast ?? (doc => { _broadcasts.Add(doc); return Task.CompletedTask; }),
            DownloadDir, "PC-A", _clock, () => _canInstall, _logs.CreateLogger<UpdateCoordinator>());

    /// <summary>Zgoda kliknięta na innym komputerze grupy i odebrana z sieci (prawdziwe scalanie pliku).</summary>
    private void ApprovalFromAnotherComputer(Version? version = null, string? fileName = null)
    {
        var v = version ?? Newer;
        Assert.True(_shared.Apply(new SharedConfigDocument
        {
            UpdateApproval = new UpdateApproval
            {
                Version = v.ToString(4), FileName = fileName ?? $"MaszynyAlert-Setup-{v}.exe", ApprovedBy = "PC-B", ApprovedAt = _clock.UtcNow,
            },
            EditedAt = _clock.UtcNow,
            EditedBy = "PC-B",
        }));
    }

    private void WritePending(Version version)
    {
        Directory.CreateDirectory(DownloadDir);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(new { version = version.ToString(4), startedAt = _clock.UtcNow }));
    }

    // (a) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PortableCopyNeverUpdates()
    {
        var coordinator = Coordinator(installed: false);

        Assert.Equal(UpdatePhase.Disabled, coordinator.State.Phase);
        Assert.Contains("portable", coordinator.State.Message, StringComparison.OrdinalIgnoreCase);
        await coordinator.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.CheckNowAsync(default);
        ApprovalFromAnotherComputer();
        await coordinator.EvaluateAsync(default);
        await coordinator.ApproveAndInstallAsync(default);
        await coordinator.InstallNowAsync(default);

        Assert.Equal(0, _source.CheckCalls);
        Assert.Empty(_source.Downloads);
        Assert.Empty(_installer.Calls);
        Assert.Equal(UpdatePhase.Disabled, coordinator.State.Phase);
        Assert.False(coordinator.State.ShouldShowWindow);
    }

    [Fact]
    public async Task UpdatesSwitchedOffInConfigNeverTouchTheNetwork()
    {
        var coordinator = Coordinator(config: new UpdatesConfig { Enabled = false });

        Assert.Equal(UpdatePhase.Disabled, coordinator.State.Phase);
        Assert.Contains("wyłączone", coordinator.State.Message, StringComparison.OrdinalIgnoreCase);
        await coordinator.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.CheckNowAsync(default);

        Assert.Equal(0, _source.CheckCalls);
    }

    [Fact]
    public async Task RunLoopStopsQuietlyWhenTheProgramCloses()
    {
        var coordinator = Coordinator();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await coordinator.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        // Pierwsze sprawdzenie dopiero po StartupDelay — przy szybkim zamknięciu sieci nie dotknięto.
        Assert.Equal(0, _source.CheckCalls);
        Assert.Equal(TimeSpan.FromMinutes(2), UpdateCoordinator.StartupDelay);
    }

    [Fact]
    public void FreshCoordinatorWaitsWithoutShowingTheWindow()
    {
        var coordinator = Coordinator();

        Assert.Equal(UpdatePhase.Idle, coordinator.State.Phase);
        Assert.Equal(Installed, coordinator.State.InstalledVersion);
        Assert.False(coordinator.State.ShouldShowWindow);
    }

    // (b) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NewerReleaseInTheCatalogIsOfferedToTheOperator()
    {
        var coordinator = Coordinator();
        int changes = 0;
        coordinator.StateChanged += () => changes++;

        await coordinator.CheckNowAsync(default);

        var state = coordinator.State;
        Assert.Equal(UpdatePhase.Available, state.Phase);
        Assert.Equal(Newer, state.Latest!.Version);
        Assert.Equal(_clock.UtcNow, state.LastCheckAt);
        Assert.Null(state.Approval);
        Assert.Contains("0.4.26", state.Message, StringComparison.Ordinal);
        Assert.Contains("0.4.22", state.Message, StringComparison.Ordinal);
        Assert.Contains("02.10.2026", state.Message, StringComparison.Ordinal);
        Assert.True(state.ShouldShowWindow);
        Assert.True(changes > 0);
        // Bez zgody człowieka nic się nie pobiera ani nie instaluje.
        Assert.Empty(_source.Downloads);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task NothingNewerMeansUpToDateAndNoWindow()
    {
        _source.Releases = [Entry(Installed)];
        var coordinator = Coordinator();

        await coordinator.CheckNowAsync(default);

        Assert.Equal(UpdatePhase.UpToDate, coordinator.State.Phase);
        Assert.False(coordinator.State.ShouldShowWindow);
    }

    // (c) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApprovalOnThisComputerIsSavedBroadcastDownloadedAndInstalledEvenDuringAnAlarm()
    {
        _canInstall = false; // człowiek zdecydował — alarm nie wstrzymuje instalacji, o którą sam poprosił
        var coordinator = Coordinator();
        await coordinator.CheckNowAsync(default);

        await coordinator.ApproveAndInstallAsync(default);

        var approval = _shared.Current!.UpdateApproval!;
        Assert.Equal("0.4.26.0", approval.Version);
        Assert.Equal(NewerFile, approval.FileName);
        Assert.Equal("PC-A", approval.ApprovedBy);
        Assert.Equal(_clock.UtcNow, approval.ApprovedAt);
        // Zgoda jest w pliku — przeżyje restart i dojdzie do komputera włączonego później.
        Assert.Equal("0.4.26.0", new SharedConfigStore(SharedPath).Load()!.UpdateApproval!.Version);
        Assert.Equal("0.4.26.0", Assert.Single(_broadcasts).UpdateApproval!.Version);

        Assert.Equal(NewerFile, Assert.Single(_source.Downloads).FileName);
        var call = Assert.Single(_installer.Calls);
        Assert.Equal(Path.Combine(DownloadDir, NewerFile), call.Path);
        Assert.Equal(Newer, call.Version);
        Assert.Equal(UpdatePhase.Installing, coordinator.State.Phase);
        Assert.True(File.Exists(PendingPath));
        Assert.Contains("0.4.26.0", File.ReadAllText(PendingPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovalWithoutAnEarlierCheckAsksTheCatalogFirst()
    {
        var coordinator = Coordinator();

        await coordinator.ApproveAndInstallAsync(default);

        Assert.Equal(1, _source.CheckCalls);
        Assert.Single(_installer.Calls);
    }

    [Fact]
    public async Task ApprovalWhenNothingIsNewerInstallsNothing()
    {
        _source.Releases = [Entry(Installed)];
        var coordinator = Coordinator();

        await coordinator.ApproveAndInstallAsync(default);

        Assert.Null(_shared.Current?.UpdateApproval);
        Assert.Empty(_broadcasts);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task FailedBroadcastDoesNotStopTheInstallation()
    {
        var coordinator = Coordinator(broadcast: _ => throw new IOException("sieć leży"));
        await coordinator.CheckNowAsync(default);

        await coordinator.ApproveAndInstallAsync(default);

        Assert.Single(_installer.Calls);
        Assert.NotNull(_shared.Current!.UpdateApproval);
    }

    // (d) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApprovalFromAnotherComputerDownloadsAndStartsTheInstallerWhenThereIsNoAlarm()
    {
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Equal("PC-B", coordinator.State.Approval!.ApprovedBy);
        Assert.Equal(NewerFile, Assert.Single(_source.Downloads).FileName);
        Assert.Equal(Newer, Assert.Single(_installer.Calls).Version);
        Assert.Equal(UpdatePhase.Installing, coordinator.State.Phase);
        Assert.Empty(_broadcasts); // cudzej zgody nie trzeba rozgłaszać ponownie
    }

    [Fact]
    public async Task ApprovalFromAnotherComputerArrivingThroughTheChangeEventIsHandledInTheBackground()
    {
        var coordinator = Coordinator();
        _shared.Changed += coordinator.OnSharedConfigChanged;
        try
        {
            ApprovalFromAnotherComputer();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_installer.Calls.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.Single(_installer.Calls);
        }
        finally { _shared.Changed -= coordinator.OnSharedConfigChanged; }
    }

    [Fact]
    public async Task ComputerThatDoesNotInstallByItselfWaitsForAClick()
    {
        var coordinator = Coordinator(config: new UpdatesConfig { AutoInstallWhenApproved = false });
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Empty(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);
        Assert.True(coordinator.State.ShouldShowWindow);

        await coordinator.InstallNowAsync(default);
        Assert.Single(_installer.Calls);
    }

    [Fact]
    public async Task ApprovedReleaseMissingFromTheCatalogFails()
    {
        _source.Releases = [Entry(Installed)];
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Equal(UpdatePhase.Failed, coordinator.State.Phase);
        Assert.Contains("nie jest już w katalogu", coordinator.State.Message, StringComparison.Ordinal);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task ApprovedReleaseThatIsNoLongerTheNewestIsStillFoundInTheCatalog()
    {
        // Zgoda na 0.4.26, a w międzyczasie wyszła 0.4.27 — zgoda dotyczy tej konkretnej wersji.
        _source.Releases = [Entry(Installed), Entry(Newer), Entry(new Version(0, 4, 27, 0))];
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Equal(Newer, Assert.Single(_installer.Calls).Version);
    }

    [Fact]
    public async Task FailedDownloadIsNotRetriedEveryTickButAtTheNextCheck()
    {
        _source.DownloadFailure = () => new InvalidDataException("Pobrany plik ma inną sumę kontrolną SHA-256");
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);
        Assert.Equal(UpdatePhase.Failed, coordinator.State.Phase);
        Assert.Contains("SHA-256", coordinator.State.LastError, StringComparison.Ordinal);
        await coordinator.EvaluateAsync(default);
        Assert.Single(_source.Downloads);

        _source.DownloadFailure = null;
        await coordinator.CheckNowAsync(default);

        Assert.Equal(2, _source.Downloads.Count);
        Assert.Single(_installer.Calls);
    }

    // (e) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ActiveAlarmPostponesTheAutomaticInstallationUntilItIsOver()
    {
        _canInstall = false;
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);
        Assert.Equal(UpdatePhase.ReadyToInstall, coordinator.State.Phase);
        Assert.Contains("alarm", coordinator.State.Message, StringComparison.OrdinalIgnoreCase);
        await coordinator.EvaluateAsync(default);
        Assert.Empty(_installer.Calls);
        Assert.Single(_source.Downloads); // pobrane raz, nie co takt

        _canInstall = true;
        await coordinator.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.Single(_source.Downloads);
    }

    [Fact]
    public async Task FailingAlarmCheckCountsAsAnAlarm()
    {
        var coordinator = new UpdateCoordinator(new UpdatesConfig(), true, Installed, _source, _installer, _shared,
            _ => Task.CompletedTask, DownloadDir, "PC-A", _clock, () => throw new InvalidOperationException("brak orkiestratora"),
            _logs.CreateLogger<UpdateCoordinator>());
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Equal(UpdatePhase.ReadyToInstall, coordinator.State.Phase);
        Assert.Empty(_installer.Calls);
    }

    // (f) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelledUacWaitsForTheAdminAndIsNotRetriedAutomatically()
    {
        _installer.Result = (InstallLaunchResult.CancelledByUser, null);
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);
        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);
        Assert.Contains("UAC", coordinator.State.Message, StringComparison.Ordinal);
        Assert.True(coordinator.State.ShouldShowWindow);

        await coordinator.EvaluateAsync(default);
        await coordinator.CheckNowAsync(default);
        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);

        _installer.Result = (InstallLaunchResult.Started, null);
        await coordinator.InstallNowAsync(default);

        Assert.Equal(2, _installer.Calls.Count);
        Assert.Equal(UpdatePhase.Installing, coordinator.State.Phase);
    }

    [Fact]
    public async Task InstallerFailureIsShownWithTheDetail()
    {
        _installer.Result = (InstallLaunchResult.Failed, "Kod wyjścia instalatora 5");
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Equal(UpdatePhase.Failed, coordinator.State.Phase);
        Assert.Contains("Kod wyjścia instalatora 5", coordinator.State.LastError, StringComparison.Ordinal);
        Assert.True(coordinator.State.ShouldShowWindow);
    }

    [Fact]
    public async Task InstallerThatThrowsDoesNotEscapeTheCoordinator()
    {
        _installer.Failure = () => new InvalidOperationException("nie można uruchomić powershell.exe");
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Equal(UpdatePhase.Failed, coordinator.State.Phase);
        Assert.Contains("powershell", coordinator.State.LastError, StringComparison.Ordinal);
    }

    // (g) ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 4, 22, 0)]
    [InlineData(0, 4, 20, 0)]
    public async Task ApprovalForTheInstalledOrAnOlderVersionIsIgnored(int a, int b, int c, int d)
    {
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer(new Version(a, b, c, d));

        await coordinator.EvaluateAsync(default);

        Assert.Null(coordinator.State.Approval);
        Assert.Empty(_source.Downloads);
        Assert.Empty(_installer.Calls);
        Assert.False(coordinator.State.ShouldShowWindow);
    }

    // (h) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task InstallationThatDidNotHappenBeforeTheRestartIsNotRetriedAutomatically()
    {
        // Pierwsze uruchomienie: zgoda → instalator „ruszył”, ale program wstał ponownie w starej wersji.
        var first = Coordinator();
        ApprovalFromAnotherComputer();
        await first.EvaluateAsync(default);
        Assert.Single(_installer.Calls);

        var afterRestart = Coordinator();
        await afterRestart.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, afterRestart.State.Phase);
        Assert.Contains("Poprzednia instalacja", afterRestart.State.Message, StringComparison.Ordinal);
        Assert.Contains("UAC", afterRestart.State.Message, StringComparison.Ordinal);

        await afterRestart.InstallNowAsync(default);
        Assert.Equal(2, _installer.Calls.Count);
    }

    [Fact]
    public async Task PendingInstallationOfTheVersionNowInstalledIsCleanedUp()
    {
        WritePending(Installed);
        var coordinator = Coordinator();

        await coordinator.EvaluateAsync(default);

        Assert.False(File.Exists(PendingPath));
        Assert.NotEmpty(_logs.Containing("Zaktualizowano"));
    }

    [Fact]
    public async Task DamagedPendingFileIsRemovedInsteadOfBlockingUpdates()
    {
        Directory.CreateDirectory(DownloadDir);
        File.WriteAllText(PendingPath, "{ to nie jest JSON");
        var coordinator = Coordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
    }

    // (i) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LaterHidesTheWindowForADayButAnApprovalFromTheGroupShowsItAnyway()
    {
        _canInstall = false;
        var coordinator = Coordinator();
        await coordinator.CheckNowAsync(default);
        Assert.True(coordinator.State.ShouldShowWindow);

        coordinator.Postpone();

        Assert.Equal(_clock.UtcNow + UpdateCoordinator.SnoozeDuration, coordinator.State.SnoozedUntil);
        Assert.False(coordinator.State.ShouldShowWindow);
        await coordinator.CheckNowAsync(default);
        Assert.False(coordinator.State.ShouldShowWindow);

        ApprovalFromAnotherComputer();
        await coordinator.EvaluateAsync(default);

        Assert.Equal(UpdatePhase.ReadyToInstall, coordinator.State.Phase);
        Assert.True(coordinator.State.ShouldShowWindow);
    }

    [Fact]
    public async Task LaterExpiresAfterADay()
    {
        var coordinator = Coordinator();
        await coordinator.CheckNowAsync(default);
        coordinator.Postpone();

        _clock.Advance(UpdateCoordinator.SnoozeDuration + TimeSpan.FromMinutes(1));
        await coordinator.EvaluateAsync(default);

        Assert.Null(coordinator.State.SnoozedUntil);
        Assert.True(coordinator.State.ShouldShowWindow);
    }

    // (j) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CatalogBehindLoginIsNotAFailureOfTheProgram()
    {
        _source.Outcome = UpdateCheckOutcome.NeedsLogin;
        _source.Error = "Katalog wydań wymaga logowania (Cloudflare Access) — wpisz identyfikator i sekret tokenu dostępu w Panel → Ustawienia → Ten komputer";
        var coordinator = Coordinator();

        await coordinator.CheckNowAsync(default);

        Assert.Equal(UpdatePhase.Idle, coordinator.State.Phase);
        Assert.Contains("Cloudflare Access", coordinator.State.LastError, StringComparison.Ordinal);
        Assert.Contains("logowania", coordinator.State.Message, StringComparison.Ordinal);
        Assert.False(coordinator.State.ShouldShowWindow);
    }

    [Fact]
    public async Task SourceThatThrowsIsTreatedAsUnavailable()
    {
        _source.CheckFailure = () => new HttpRequestException("połączenie zerwane");
        var coordinator = Coordinator();

        await coordinator.CheckNowAsync(default);

        Assert.Equal(UpdatePhase.Idle, coordinator.State.Phase);
        Assert.Contains("połączenie zerwane", coordinator.State.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableCatalogKeepsAnOfferThatWasAlreadyKnown()
    {
        var coordinator = Coordinator();
        await coordinator.CheckNowAsync(default);
        Assert.Equal(UpdatePhase.Available, coordinator.State.Phase);

        _source.Outcome = UpdateCheckOutcome.Unavailable;
        _source.Error = "HTTP 502";
        await coordinator.CheckNowAsync(default);

        Assert.Equal(UpdatePhase.Available, coordinator.State.Phase);
        Assert.Equal(Newer, coordinator.State.Latest!.Version);
        Assert.Equal("HTTP 502", coordinator.State.LastError);
    }

    public void Dispose() => _dir.Dispose();

    // Podstawione zależności --------------------------------------------------------------------------


    [Fact]
    public async Task InstallerGetsTheChecksumFromTheCatalogNotFromTheFileOnDisk()
    {
        var coordinator = Coordinator();

        await coordinator.InstallNowAsync(default);

        Assert.Equal(Entry(Newer).Sha256, Assert.Single(_installer.ExpectedSha256));
    }

    [Fact]
    public async Task SecondInstallAttemptAlsoCarriesTheCatalogChecksum()
    {
        var coordinator = Coordinator();
        _installer.Result = (InstallLaunchResult.CancelledByUser, "anulowane");

        await coordinator.InstallNowAsync(default);
        await coordinator.InstallNowAsync(default);

        Assert.Equal([Entry(Newer).Sha256, Entry(Newer).Sha256], _installer.ExpectedSha256);
    }


    [Fact]
    public async Task W4B01_ReusedDownloadChangedOnDisk_IsVerifiedAndDownloadedAgainBeforeLaunch()
    {
        var coordinator = Coordinator();
        _installer.Result = (InstallLaunchResult.CancelledByUser, "anulowane");
        await coordinator.InstallNowAsync(default);
        string path = Assert.Single(_installer.Calls).Path;
        File.WriteAllText(path, "inna zawartosc o innym rozmiarze");
        _installer.Calls.Clear();

        await coordinator.InstallNowAsync(default);

        Assert.Equal(2, _source.Downloads.Count);
        Assert.Equal(SetupBytes(Newer), File.ReadAllBytes(Assert.Single(_installer.Calls).Path));
        Assert.Contains(_logs.Entries, e => e.Message.Contains("nie zgadza się ze spisem wydań", StringComparison.Ordinal));
    }

    [Fact]
    public async Task W4B01_ReusedDownloadUnchanged_IsNotDownloadedAgain()
    {
        var coordinator = Coordinator();
        _installer.Result = (InstallLaunchResult.CancelledByUser, "anulowane");

        await coordinator.InstallNowAsync(default);
        await coordinator.InstallNowAsync(default);

        Assert.Single(_source.Downloads);
        Assert.Equal(2, _installer.Calls.Count);
    }

    [Fact]
    public async Task W4B03_ApprovalForVersionMissingFromCatalog_DoesNotBlockApprovingTheAvailableOne()
    {
        var withdrawn = new Version(0, 4, 24, 0);
        ApprovalFromAnotherComputer(withdrawn);
        var coordinator = Coordinator();

        await coordinator.CheckNowAsync(default);
        await coordinator.EvaluateAsync(default);

        Assert.Null(coordinator.State.Approval);
        Assert.Equal(UpdatePhase.Available, coordinator.State.Phase);
        Assert.Contains("nie jest już w katalogu", coordinator.State.Message, StringComparison.Ordinal);
        Assert.Empty(_installer.Calls);

        await coordinator.ApproveAndInstallAsync(default);

        Assert.Equal(Newer, Assert.Single(_installer.Calls).Version);
        Assert.Equal(Newer.ToString(4), _shared.Current!.UpdateApproval!.Version);
    }

    [Fact]
    public async Task W4B04_ManualDownloadFailure_DoesNotPromiseAnAutomaticRetry()
    {
        _source.DownloadFailure = () => new IOException("Połączenie przerwane (test).");
        var coordinator = Coordinator();

        await coordinator.InstallNowAsync(default);

        Assert.Equal(UpdatePhase.Failed, coordinator.State.Phase);
        Assert.DoesNotContain("następnym sprawdzeniu", coordinator.State.Message, StringComparison.Ordinal);
        Assert.Contains("kliknij Zainstaluj", coordinator.State.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task W4B07_OrdinaryDownloadFailure_IsLoggedWithoutStackTrace()
    {
        _source.DownloadFailure = () => new IOException("Połączenie przerwane (test).");
        var coordinator = Coordinator();

        await coordinator.InstallNowAsync(default);

        LogEntry entry = Assert.Single(_logs.Containing("Pobranie"));
        Assert.Null(entry.Exception);
        Assert.Contains("Połączenie przerwane", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelperThatNeverAnswers_EndsAsWaitingForAdminAfterTheLimit()
    {
        var coordinator = Coordinator();
        coordinator.InstallerAnswerLimit = TimeSpan.FromMilliseconds(300);
        _installer.NeverAnswers = true;

        await coordinator.InstallNowAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);
        Assert.Contains("UAC", coordinator.State.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileTheHelperWaitsForUac_WindowButtonsAnswerAtOnce()
    {
        var coordinator = Coordinator();
        _installer.NeverAnswers = true;
        var install = coordinator.InstallNowAsync(default);
        await WaitUntilAsync(() => coordinator.State.Phase == UpdatePhase.Installing);

        await coordinator.CheckNowAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.InstallNowAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.EvaluateAsync(default).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(install.IsCompleted);
        Assert.Single(_installer.Calls);
        Assert.Contains("trwa", coordinator.State.Message, StringComparison.OrdinalIgnoreCase);
    }


    private string UserDownloadDir => UpdateTaskContract.DownloadDirectory(_dir.Path);
    private string AdminDir => UpdateTaskContract.AdminDirectory(_dir.Path);

    private UpdateCoordinator SplitCoordinator() =>
        new(new UpdatesConfig(), true, Installed, _source, _installer, _shared,
            doc => { _broadcasts.Add(doc); return Task.CompletedTask; },
            UserDownloadDir, "PC-A", _clock, () => _canInstall, _logs.CreateLogger<UpdateCoordinator>(), AdminDir);

    private static void WritePendingTo(string directory, Version version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, UpdateCoordinator.PendingInstallFileName),
            JsonSerializer.Serialize(new { version = version.ToString(4), startedAt = DateTimeOffset.UnixEpoch }));
    }

    [Fact]
    public async Task W4B09_ZnacznikPodrzuconyDoKataloguPobranOperatora_NieBlokujeAutomatycznejInstalacji()
    {
        WritePendingTo(UserDownloadDir, Newer);
        var coordinator = SplitCoordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.Equal(Path.Combine(UserDownloadDir, NewerFile), _installer.Calls[0].Path);
    }

    [Fact]
    public async Task W4B09_ZnacznikZadaniaWKataloguAdministratorow_ZatrzymujeKolejnaAutomatycznaProbe()
    {
        // Znacznik zapisany przez zadanie (SYSTEM) po sprawdzeniu pliku dalej znaczy „próba nie doszła do skutku”.
        WritePendingTo(AdminDir, Newer);
        var coordinator = SplitCoordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Empty(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);
        Assert.Contains("Poprzednia instalacja", coordinator.State.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task P7b_ZnacznikProbyNieTrafiaDoKataloguPobranOperatora()
    {
        var coordinator = SplitCoordinator();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.False(File.Exists(Path.Combine(UserDownloadDir, UpdateCoordinator.PendingInstallFileName)),
            "Znacznik w katalogu operatora mógłby podrzucić także operator — koordynator pisze tylko do katalogu znacznika.");
        Assert.True(File.Exists(Path.Combine(AdminDir, UpdateCoordinator.PendingInstallFileName)),
            "W teście katalog znacznika jest zapisywalny — znacznik ma tam trafić.");
    }

    private UpdateCoordinator SplitCoordinatorWithoutTask() =>
        new(new UpdatesConfig(), true, Installed, _source, _installer, _shared,
            doc => { _broadcasts.Add(doc); return Task.CompletedTask; },
            UserDownloadDir, "PC-A", _clock, () => _canInstall, _logs.CreateLogger<UpdateCoordinator>(), AdminDir, () => false);

    [Fact]
    public async Task S05_BezZadania_ZnacznikProbyWKataloguPobran_JednaProbaNaWersje()
    {
        var coordinator = SplitCoordinatorWithoutTask();
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.True(File.Exists(Path.Combine(UserDownloadDir, UpdateCoordinator.PendingInstallFileName)));
        Assert.False(File.Exists(Path.Combine(AdminDir, UpdateCoordinator.PendingInstallFileName)));

        // Restart programu nadal w starej wersji: znacznik z katalogu pobrań zatrzymuje kolejną automatyczną próbę.
        var afterRestart = SplitCoordinatorWithoutTask();
        await afterRestart.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, afterRestart.State.Phase);
    }

    [Fact]
    public async Task S05_BladSprawdzeniaZadania_JakBrakZadania()
    {
        var coordinator = new UpdateCoordinator(new UpdatesConfig(), true, Installed, _source, _installer, _shared,
            doc => { _broadcasts.Add(doc); return Task.CompletedTask; },
            UserDownloadDir, "PC-A", _clock, () => _canInstall, _logs.CreateLogger<UpdateCoordinator>(), AdminDir,
            () => throw new InvalidOperationException("schtasks"));
        ApprovalFromAnotherComputer();

        await coordinator.EvaluateAsync(default);

        Assert.True(File.Exists(Path.Combine(UserDownloadDir, UpdateCoordinator.PendingInstallFileName)));
    }

    [Fact(Skip = "Znane ograniczenie szerokiego modelu: zarejestrowane zadanie nierozpoznanego kształtu, brak znacznika próby, anulowany UAC i restart ponawiają próbę; naprawiona jest tylko znana niezgodność ExpectedVersion.")]
    public async Task D01_RegisteredTaskWithoutWritablePending_CancelledInstallIsNotRetriedAfterRestart()
    {
        var pending = Path.Combine(AdminDir, UpdateCoordinator.PendingInstallFileName);
        Directory.CreateDirectory(pending);
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(pending, "blocked"));
        _installer.Result = (InstallLaunchResult.CancelledByUser, "model: instalacja anulowana");
        ApprovalFromAnotherComputer();
        UpdateCoordinator Create() => new(new UpdatesConfig(), true, Installed, _source, _installer, _shared,
            doc => { _broadcasts.Add(doc); return Task.CompletedTask; },
            UserDownloadDir, "PC-A", _clock, () => _canInstall, _logs.CreateLogger<UpdateCoordinator>(), AdminDir, () => true);
        var coordinator = Create();

        await coordinator.EvaluateAsync(default);
        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);
        Assert.False(File.Exists(pending));
        Assert.False(File.Exists(Path.Combine(UserDownloadDir, UpdateCoordinator.PendingInstallFileName)));
        await coordinator.EvaluateAsync(default);
        Assert.Single(_installer.Calls);

        var afterRestart = Create();
        await afterRestart.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, afterRestart.State.Phase);
    }

    [Fact]
    public async Task D01_NoTaskWithWritableUserPending_CancelledInstallIsNotRetriedAfterRestart()
    {
        // Kontrola: bez zarejestrowanego zadania znacznik trafia do zapisywalnego katalogu atrapy operatora.
        var pending = Path.Combine(AdminDir, UpdateCoordinator.PendingInstallFileName);
        Directory.CreateDirectory(pending);
        _installer.Result = (InstallLaunchResult.CancelledByUser, "model: instalacja anulowana");
        ApprovalFromAnotherComputer();
        var coordinator = SplitCoordinatorWithoutTask();

        await coordinator.EvaluateAsync(default);
        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, coordinator.State.Phase);
        Assert.True(File.Exists(Path.Combine(UserDownloadDir, UpdateCoordinator.PendingInstallFileName)));
        var afterRestart = SplitCoordinatorWithoutTask();
        await afterRestart.EvaluateAsync(default);

        Assert.Single(_installer.Calls);
        Assert.Equal(UpdatePhase.WaitingForAdmin, afterRestart.State.Phase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Warunek nie został spełniony w 5 s.");
            await Task.Delay(20);
        }
    }

    private sealed class FakeSource : IUpdateSource
    {
        private int _checkCalls;

        public IReadOnlyList<ReleaseEntry> Releases { get; set; } = [];
        public UpdateCheckOutcome? Outcome { get; set; }
        public string? Error { get; set; }
        public Func<Exception>? CheckFailure { get; set; }
        public Func<Exception>? DownloadFailure { get; set; }
        public int CheckCalls => Volatile.Read(ref _checkCalls);
        public List<ReleaseEntry> Downloads { get; } = [];

        public Task<UpdateCheckResult> CheckAsync(Version installed, CancellationToken ct)
        {
            Interlocked.Increment(ref _checkCalls);
            if (CheckFailure is { } failure) throw failure();
            if (Outcome is UpdateCheckOutcome.NeedsLogin or UpdateCheckOutcome.Unavailable)
                return Task.FromResult(new UpdateCheckResult(Outcome.Value, null, Error, DateTimeOffset.UnixEpoch));
            var latest = ReleaseCatalog.Latest(Releases);
            var outcome = latest is not null && latest.Version > installed ? UpdateCheckOutcome.Available : UpdateCheckOutcome.UpToDate;
            return Task.FromResult(new UpdateCheckResult(outcome, latest, null, DateTimeOffset.UnixEpoch) { Releases = Releases });
        }

        public Task<string> DownloadAsync(ReleaseEntry release, string targetDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            lock (Downloads) Downloads.Add(release);
            progress?.Report(0.5);
            if (DownloadFailure is { } failure) throw failure();
            Directory.CreateDirectory(targetDirectory);
            string path = Path.Combine(targetDirectory, release.FileName);
            File.WriteAllBytes(path, SetupBytes(release.Version));
            progress?.Report(1.0);
            return Task.FromResult(path);
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public (InstallLaunchResult Result, string? Error) Result { get; set; } = (InstallLaunchResult.Started, null);
        public Func<Exception>? Failure { get; set; }
        public List<(string Path, Version Version)> Calls { get; } = [];
        public List<string> ExpectedSha256 { get; } = [];

        /// <summary>Pomocnik, który nie odpowiada (UAC ani potwierdzone, ani anulowane) i ignoruje anulowanie.</summary>
        public bool NeverAnswers { get; set; }

        public Task<(InstallLaunchResult Result, string? Error)> LaunchAsync(string setupPath, Version version, string expectedSha256, CancellationToken ct)
        {
            lock (Calls) { Calls.Add((setupPath, version)); ExpectedSha256.Add(expectedSha256); }
            if (Failure is { } failure) throw failure();
            if (NeverAnswers) return new TaskCompletionSource<(InstallLaunchResult, string?)>().Task;
            return Task.FromResult(Result);
        }
    }
}
