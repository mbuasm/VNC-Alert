using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.App.Tests;

public sealed class UpdateViewModelTests
{
    private static readonly Version Installed = new(0, 4, 1, 0);
    private static readonly DateTimeOffset Published = new(2026, 10, 2, 18, 2, 31, TimeSpan.Zero);

    private static ReleaseEntry Release(int build = 99) =>
        new($"MaszynyAlert-Setup-0.4.{build}.0.exe", new Version(0, 4, build, 0), 57_461_760, new string('a', 64), Published);

    private static UpdateApproval Approval(int build = 99) => new()
    {
        Version = $"0.4.{build}.0",
        FileName = $"MaszynyAlert-Setup-0.4.{build}.0.exe",
        ApprovedBy = "PC-HALA-1",
        ApprovedAt = new DateTimeOffset(2026, 10, 2, 19, 15, 0, TimeSpan.Zero),
    };

    private static UpdateState State(UpdatePhase phase, ReleaseEntry? latest = null, UpdateApproval? approval = null,
        double progress = 0, string message = "", string? lastError = null) => new()
    {
        Phase = phase,
        InstalledVersion = Installed,
        Latest = latest,
        Approval = approval,
        Progress = progress,
        Message = message,
        LastError = lastError,
    };

    private sealed class Calls
    {
        public int Approve, Install, Check, Postpone;
    }

    private static (UpdateViewModel ViewModel, Calls Calls) Create(bool group = false)
    {
        var calls = new Calls();
        var viewModel = new UpdateViewModel(
            _ => { calls.Approve++; return Task.CompletedTask; },
            _ => { calls.Install++; return Task.CompletedTask; },
            _ => { calls.Check++; return Task.CompletedTask; },
            () => calls.Postpone++,
            () => group,
            TimeZoneInfo.Utc);
        return (viewModel, calls);
    }

    [Fact]
    public void DostepnaWersjaPokazujeObieWersjeIPrzyciskZgody()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Available, Release(), message: "Dostępna wersja 0.4.99"));

        Assert.Equal("Aktualizacja VNC Alert", vm.Title);
        Assert.Equal("Zainstalowana wersja: 0.4.1", vm.InstalledVersionText);
        Assert.Equal("Dostępna wersja: 0.4.99 (opublikowana 02.10.2026 18:02)", vm.AvailableVersionText);
        Assert.True(vm.HasAvailableVersion);
        Assert.Equal("Dostępna wersja 0.4.99", vm.MessageText);
        Assert.True(vm.IsPrimaryVisible);
        Assert.True(vm.CanPrimary);
        Assert.Equal("Pobierz i zainstaluj", vm.PrimaryButtonText);
        Assert.True(vm.CanPostpone);
        Assert.True(vm.CanCheckNow);
        Assert.False(vm.IsProgressVisible);
        Assert.False(vm.HasApproval);
    }

    [Fact]
    public void WGrupieZgodaDotyczyWszystkichKomputerow()
    {
        var (vm, _) = Create(group: true);

        vm.Apply(State(UpdatePhase.Available, Release()));

        Assert.Equal("Pobierz i zainstaluj na wszystkich komputerach grupy", vm.PrimaryButtonText);
    }

    [Fact]
    public void PobieraniePokazujePasekIBlokujePrzyciski()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Downloading, Release(), Approval(), progress: 0.454));

        Assert.True(vm.IsProgressVisible);
        Assert.Equal(45.4, vm.ProgressPercent, 3);
        Assert.Equal("Pobrano 45 %", vm.ProgressText);
        Assert.True(vm.IsPrimaryVisible);
        Assert.False(vm.CanPrimary);
        Assert.False(vm.CanPostpone);
        Assert.False(vm.CanCheckNow);
    }

    [Fact]
    public void PostepPozaZakresemJestPrzycinany()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Downloading, Release(), Approval(), progress: 1.7));
        Assert.Equal(100, vm.ProgressPercent);

        vm.Apply(State(UpdatePhase.Downloading, Release(), Approval(), progress: double.NaN));
        Assert.Equal(0, vm.ProgressPercent);
    }

    [Fact]
    public void PodczasInstalacjiWszystkiePrzyciskiPozaZamknijSaNieaktywne()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Installing, Release(), Approval(), message: "Trwa instalacja"));

        Assert.False(vm.CanPrimary);
        Assert.False(vm.CanPostpone);
        Assert.False(vm.CanCheckNow);
        Assert.False(vm.IsProgressVisible);
        Assert.Equal("Trwa instalacja", vm.MessageText);
    }

    [Fact]
    public void CzekanieNaAdministratoraProponujeZainstaluj()
    {
        var (vm, _) = Create(group: true);

        vm.Apply(State(UpdatePhase.WaitingForAdmin, Release(), Approval(), message: "Instalacja czeka na zgodę administratora (UAC)"));

        Assert.Equal("Zainstaluj", vm.PrimaryButtonText);
        Assert.True(vm.CanPrimary);
        Assert.False(vm.CanPostpone);
        Assert.True(vm.HasApproval);
        Assert.Contains("PC-HALA-1", vm.ApprovalText);
        Assert.Contains("02.10.2026 19:15", vm.ApprovalText);
    }

    [Fact]
    public void ZgodaBezZnanegoKataloguPokazujeWersjeZeZgody()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.ReadyToInstall, latest: null, approval: Approval(), message: "Pobrano; instalacja po zakończeniu alarmu"));

        Assert.Equal("Dostępna wersja: 0.4.99", vm.AvailableVersionText);
        Assert.True(vm.HasAvailableVersion);
        Assert.Equal("Zainstaluj", vm.PrimaryButtonText);
        Assert.True(vm.CanPrimary);
    }

    [Fact]
    public void AktualnaWersjaChowaPrzyciskZgody()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.UpToDate, Release(1), message: ""));

        Assert.False(vm.IsPrimaryVisible);
        Assert.False(vm.CanPrimary);
        Assert.False(vm.HasAvailableVersion);
        Assert.Equal(string.Empty, vm.AvailableVersionText);
        Assert.True(vm.CanCheckNow);
        Assert.False(vm.CanPostpone);
        Assert.Equal("Program jest aktualny.", vm.MessageText);
    }

    [Fact]
    public void WylaczoneAktualizacjeNiePozwalajaSprawdzac()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Disabled, message: "Aktualizacje wyłączone"));

        Assert.False(vm.IsPrimaryVisible);
        Assert.False(vm.CanCheckNow);
        Assert.False(vm.CanPostpone);
        Assert.Equal("Aktualizacje wyłączone", vm.MessageText);
    }

    [Fact]
    public void SprawdzaniePokazujeKomunikatZastepczy()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Checking));

        Assert.Equal("Sprawdzam katalog wydań…", vm.MessageText);
        Assert.False(vm.CanCheckNow);
    }

    [Fact]
    public void BladPokazujeSzczegolTechniczny()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Failed, Release(), Approval(), message: "Nie udało się zainstalować", lastError: "kod 4"));

        Assert.True(vm.HasDetail);
        Assert.Contains("kod 4", vm.DetailText);
        Assert.Equal("Zainstaluj", vm.PrimaryButtonText);
        Assert.True(vm.CanPrimary);
    }

    [Fact]
    public void BladBezZgodyPozwalaOdlozyc()
    {
        var (vm, _) = Create();

        vm.Apply(State(UpdatePhase.Failed, Release(), message: "Nie udało się pobrać"));

        Assert.Equal("Pobierz i zainstaluj", vm.PrimaryButtonText);
        Assert.True(vm.CanPostpone);
    }

    [Theory]
    [InlineData(0, 4, 22, 0, "0.4.22")]
    [InlineData(1, 2, 3, 4, "1.2.3.4")]
    public void WersjaBezZerowejCzwartejCzesci(int major, int minor, int build, int revision, string expected)
    {
        Assert.Equal(expected, UpdateViewModel.FormatVersion(new Version(major, minor, build, revision)));
    }

    [Fact]
    public async Task PrzyciskGlownyBezZgodyZapisujeZgodeAZeZgodaInstaluje()
    {
        var (vm, calls) = Create();

        vm.Apply(State(UpdatePhase.Available, Release()));
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(1, calls.Approve);
        Assert.Equal(0, calls.Install);

        vm.Apply(State(UpdatePhase.WaitingForAdmin, Release(), Approval()));
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(1, calls.Approve);
        Assert.Equal(1, calls.Install);
    }

    [Fact]
    public async Task NieaktywnyPrzyciskGlownyNicNieRobi()
    {
        var (vm, calls) = Create();

        vm.Apply(State(UpdatePhase.Installing, Release(), Approval()));
        await vm.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(0, calls.Approve + calls.Install);
    }

    [Fact]
    public async Task PozniejOdkladaIZamykaOkno()
    {
        var (vm, calls) = Create();
        int closed = 0;
        vm.CloseRequested += () => closed++;

        vm.Apply(State(UpdatePhase.Available, Release()));
        vm.PostponeCommand.Execute(null);
        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.CloseCommand.Execute(null);

        Assert.Equal(1, calls.Postpone);
        Assert.Equal(1, calls.Check);
        Assert.Equal(2, closed);
    }

    [Fact]
    public async Task WyjatekKoordynatoraTrafiaDoSzczegolu()
    {
        var vm = new UpdateViewModel(
            _ => throw new InvalidOperationException("brak sieci"),
            _ => Task.CompletedTask, _ => Task.CompletedTask, () => { }, () => false, TimeZoneInfo.Utc);

        vm.Apply(State(UpdatePhase.Available, Release()));
        await vm.PrimaryCommand.ExecuteAsync(null);

        Assert.True(vm.HasDetail);
        Assert.Contains("brak sieci", vm.DetailText);
        Assert.False(vm.IsBusy);
    }
}
