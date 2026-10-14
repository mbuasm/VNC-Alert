using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Machines;

public sealed class MonitorSupervisorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_WithoutRunningMonitors_CanBeRepeatedAndRestarted(bool disabledMachines)
    {
        var cfg = LocalConfig();
        cfg = cfg with { Machines = disabledMachines ? cfg.Machines.Select(m => m with { Enabled = false }).ToList() : [] };
        await using var supervisor = Build(cfg, new FakeClock());
        for (int i = 0; i < 3; i++)
        {
            await supervisor.StartAsync(CancellationToken.None);
            await supervisor.StopAsync();
            await supervisor.StoppedTask;
            await supervisor.StopAsync();
            Assert.False(supervisor.IsRunning);
        }
    }

    /// <summary>
    /// Przykładowa konfiguracja z hostami przekierowanymi na 127.0.0.1:1 (port zawsze odmawia połączenia),
    /// krótkimi czasami i wyłączoną maszyną „amm520-1” — pozwala uruchomić nadzorcę bez prawdziwych maszyn.
    /// </summary>
    private static AppConfig LocalConfig()
    {
        AppConfig cfg = TestPaths.ExampleConfig;
        List<MachineConfig> machines = cfg.Machines
            .Select(m => m with
            {
                Host = "127.0.0.1",
                Port = 1,
                Enabled = m.Id != "amm520-1",
            })
            .ToList();

        return cfg with
        {
            Machines = machines,
            Timing = cfg.Timing with
            {
                ConnectTimeoutMs = 200,
                PollIntervalMs = 200,
                PollingModeIntervalMs = 200,
                ReconnectBackoffMs = [200],
            },
        };
    }

    private static MonitorSupervisor Build(AppConfig cfg, FakeClock clock) => new(
        cfg,
        _ => null,
        new FakeOcrEngine { IsAvailable = false },
        new MessageMapper(TestPaths.ExampleRules),
        new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2))),
        clock,
        new NullCaptureStore(),
        testScreensDir: null);

    [Fact]
    public async Task StartAsync_DlaPrzykladowejKonfiguracji_TworzyMigawkiDlaWszystkichDwunastuMaszyn()
    {
        AppConfig cfg = LocalConfig();
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = Build(cfg, clock);

        await supervisor.StartAsync(CancellationToken.None);

        Assert.True(supervisor.IsRunning);
        Assert.Equal(12, supervisor.Snapshots.Count);
        Assert.Equal(cfg.Machines.Select(m => m.Id), supervisor.Snapshots.Select(s => s.MachineId));

        await supervisor.StopAsync();
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public async Task StartAsync_DlaMaszynyWylaczonej_UstawiaStanDisabled()
    {
        AppConfig cfg = LocalConfig();
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = Build(cfg, clock);

        await supervisor.StartAsync(CancellationToken.None);
        MachineStatusSnapshot disabled = supervisor.Snapshots.Single(s => s.MachineId == "amm520-1");

        Assert.Equal(MachineConnectionState.Disabled, disabled.State);
        Assert.Equal("520 (1)", disabled.Name);

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ReconfigureAsync_PoZmianieZbioruWlaczonychMaszyn_OdtwarzaMonitoryWedlugNowejListy()
    {
        AppConfig cfg = LocalConfig();
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = Build(cfg, clock);
        await supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(MachineConnectionState.Disabled, supervisor.Snapshots.Single(s => s.MachineId == "amm520-1").State);
        Assert.NotEqual(MachineConnectionState.Disabled, supervisor.Snapshots.Single(s => s.MachineId == "aidite1").State);

        // ReconfigureAsync sprawdza konfigurację jak przy starcie programu — krótkie czasy z LocalConfig by nie przeszły.
        AppConfig next = cfg with
        {
            Timing = TestPaths.ExampleConfig.Timing,
            Machines = cfg.Machines.Select(m => m.Id switch
            {
                "amm520-1" => m with { Enabled = true },
                "aidite1" => m with { Enabled = false },
                _ => m,
            }).ToList(),
        };
        await supervisor.ReconfigureAsync(next, new MessageMapper(TestPaths.ExampleRules), new FakeOcrEngine { IsAvailable = false }, CancellationToken.None);
        Assert.False(supervisor.IsRunning);
        await supervisor.StartAsync(CancellationToken.None);

        Assert.True(supervisor.IsRunning);
        Assert.Equal(12, supervisor.Snapshots.Count);
        Assert.NotEqual(MachineConnectionState.Disabled, supervisor.Snapshots.Single(s => s.MachineId == "amm520-1").State);
        Assert.Equal(MachineConnectionState.Disabled, supervisor.Snapshots.Single(s => s.MachineId == "aidite1").State);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WywolanyDwaRazy_NieDuplikujeMonitorow()
    {
        AppConfig cfg = LocalConfig();
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = Build(cfg, clock);

        await supervisor.StartAsync(CancellationToken.None);
        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(12, supervisor.Snapshots.Count);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StartStopStart_DzialaBezBleduIOdtwarzaMonitory()
    {
        AppConfig cfg = LocalConfig();
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = Build(cfg, clock);

        await supervisor.StartAsync(CancellationToken.None);
        await supervisor.StopAsync();
        Assert.False(supervisor.IsRunning);

        await supervisor.StartAsync(CancellationToken.None);

        Assert.True(supervisor.IsRunning);
        Assert.Equal(12, supervisor.Snapshots.Count);
        Assert.NotNull(supervisor.Get("aidite1"));

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StopAsync_BezWczesniejszegoStartu_NieRzucaWyjatku()
    {
        AppConfig cfg = LocalConfig();
        await using MonitorSupervisor supervisor = Build(cfg, new FakeClock());

        await supervisor.StopAsync();

        Assert.False(supervisor.IsRunning);
        Assert.Empty(supervisor.Snapshots);
    }

    [Fact]
    public async Task StartAsync_GdyMaszynaWskazujeNieistniejacyProfil_ZwracaMigawkeZBledem()
    {
        AppConfig cfg = LocalConfig();
        cfg = cfg with
        {
            Machines = cfg.Machines.Select(m => m.Id == "cameo1" ? m with { Profile = "nie-ma-takiego" } : m).ToList(),
        };
        await using MonitorSupervisor supervisor = Build(cfg, new FakeClock());

        await supervisor.StartAsync(CancellationToken.None);
        MachineStatusSnapshot broken = supervisor.Snapshots.Single(s => s.MachineId == "cameo1");

        Assert.Equal(MachineConnectionState.Error, broken.State);
        Assert.Contains("nie-ma-takiego", broken.LastError!, StringComparison.Ordinal);
        Assert.Null(supervisor.Get("cameo1"));

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task PruneCapturesAsync_NieRzucaWyjatkuDlaPustegoMagazynu()
    {
        AppConfig cfg = LocalConfig();
        await using MonitorSupervisor supervisor = Build(cfg, new FakeClock());

        await supervisor.PruneCapturesAsync();
    }


    /// <summary>Nadzorca z podmienionym dostawcą haseł VNC — do testów bramki <see cref="MonitorSupervisor.AnyEnabledMachineIsReadyToConnect"/>.</summary>
    private static MonitorSupervisor BuildWithPasswords(AppConfig cfg, FakeClock clock, Func<MachineConfig, string?> passwords) => new(
        cfg,
        passwords,
        new FakeOcrEngine { IsAvailable = false },
        new MessageMapper(TestPaths.ExampleRules),
        new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2))),
        clock,
        new NullCaptureStore(),
        testScreensDir: null);

    private static AppConfig PasswordMachinesOnly => TestPaths.ExampleConfig with
    {
        Machines = [.. TestPaths.ExampleConfig.Machines.Where(m => m.RequiresPassword)],
    };

    [Fact]
    public async Task AnyEnabledMachineIsReadyToConnect_GdyZadnaMaszynaNieMaZapisanegoHasla_ZwracaFalsz()
    {
        // Dlaczego: przy pierwszym uruchomieniu na nowym komputerze haseł jeszcze nie ma.
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = BuildWithPasswords(PasswordMachinesOnly, clock, _ => null);

        Assert.False(supervisor.AnyEnabledMachineIsReadyToConnect());
    }

    [Fact]
    public async Task AnyEnabledMachineIsReadyToConnect_GdyHasloMaTylkoMaszynaWylaczona_ZwracaFalsz()
    {
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = BuildWithPasswords(
            PasswordMachinesOnly, clock, m => m.Id == "amm580" ? "tajne" : null);

        Assert.False(supervisor.AnyEnabledMachineIsReadyToConnect());
    }

    [Fact]
    public async Task AnyEnabledMachineIsReadyToConnect_GdyWlaczonaMaszynaNiePotrzebujeHasla_ZwracaPrawde()
    {
        AppConfig config = TestPaths.ExampleConfig with
        {
            Machines = [.. TestPaths.ExampleConfig.Machines.Select(m => m.RequiresPassword ? m : m with { Enabled = true })],
        };
        Assert.Contains(config.Machines, m => m.Enabled && !m.RequiresPassword);

        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = BuildWithPasswords(config, clock, _ => null);

        Assert.True(supervisor.AnyEnabledMachineIsReadyToConnect());
    }

    [Fact]
    public async Task AnyEnabledMachineIsReadyToConnect_GdyHasloMaChocJednaMaszynaWlaczona_ZwracaPrawde()
    {
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = BuildWithPasswords(
            PasswordMachinesOnly, clock, m => m.Id == "aidite3" ? "tajne" : null);

        Assert.True(supervisor.AnyEnabledMachineIsReadyToConnect());
    }

    [Fact]
    public async Task AnyEnabledMachineIsReadyToConnect_GdyZapisaneHasloToSameSpacje_ZwracaFalsz()
    {
        // Dlaczego: „hasło” ze spacji powstaje z omyłkowego zapisu pustego pola w Ustawieniach. Serwer VNC
        // i tak je odrzuci, więc traktujemy je jak brak hasła — inaczej bramka przepuszczałaby obserwatora
        // dokładnie w sytuacji, przed którą ma chronić.
        var clock = new FakeClock();
        await using MonitorSupervisor supervisor = BuildWithPasswords(PasswordMachinesOnly, clock, _ => "   ");

        Assert.False(supervisor.AnyEnabledMachineIsReadyToConnect());
    }
}
