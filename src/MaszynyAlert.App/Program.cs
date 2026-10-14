using System.IO;
using System.Net.Sockets;
using System.Windows;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using MaszynyAlert.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace MaszynyAlert.App;

internal static class Program
{
    /// <summary>Muteks pojedynczej instancji — pomocnik aktualizacji (<c>WindowsUpdateInstaller.Script</c>) rozpoznaje po nim żywy program.</summary>
    internal const string SingleInstanceMutexName = "Global\\MaszynyAlert.App";

    private static string SharedConfigPath => Path.Combine(AppPaths.DataDir, "shared-config", "shared-config.json");

    /// <summary>Czy konto to SYSTEM — wtedy program nie uruchamia monitoringu (sesja 0, bez pulpitu operatora).</summary>
    internal static bool IsLocalSystem(System.Security.Principal.SecurityIdentifier? account) =>
        account?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) == true;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--group-settings-helper")
        {
            Environment.ExitCode = GroupSettingsClient.RunHelper(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == NetworkProfileHelper.Argument)
        {
            Environment.ExitCode = NetworkProfileHelper.RunHelper(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--save-alarm-instruction")
        {
            Environment.ExitCode = AlarmHelpService.SaveElevated(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == InstalledSetup.ApplyArgument)
        {
            Environment.ExitCode = InstalledSetup.ApplyElevated(args[1]);
            return;
        }
        if (args.Contains("--settings-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = InstalledSetup.RunSmokeTest();
            return;
        }

        if (args.Contains(StopRequest.Argument, StringComparer.OrdinalIgnoreCase))
        {
            bool asSystem;
            using (var stopIdentity = System.Security.Principal.WindowsIdentity.GetCurrent())
                asSystem = IsLocalSystem(stopIdentity.User);
            WatchdogTask.Suppress(AppPaths.DataDir, DateTimeOffset.Now, asSystem);
            Environment.ExitCode = StopRequest.RequestStop(TimeSpan.FromSeconds(10));
            return;
        }

        if (args.Length == 1 && args[0] == Core.Updates.UpdateTaskContract.VerifyArgument)
        {
            Environment.ExitCode = UpdateTaskCommand.Run();
            return;
        }

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (IsLocalSystem(identity.User))
            {
                Environment.ExitCode = 3;
                return;
            }
        }

        bool watchdogStart = args.Contains(WatchdogTask.Argument, StringComparer.OrdinalIgnoreCase);
        if (watchdogStart && !WatchdogTask.ShouldStart(AppPaths.DataDir, DateTimeOffset.Now)) return;

        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunStartupSmokeTest(args.Contains("--monitoring-disabled", StringComparer.OrdinalIgnoreCase));
            return;
        }

        if (AppPaths.IsInstalled && args.Contains("--setup", StringComparer.OrdinalIgnoreCase))
        {
            InstalledSetup.Run();
            return;
        }

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            if (args.Contains("--preview", StringComparer.OrdinalIgnoreCase)
                && SpinWait.SpinUntil(() => PreviewActivation.TryRequest(), TimeSpan.FromSeconds(3)))
                return;

            if (watchdogStart) return;

            System.Windows.MessageBox.Show(
                "VNC Alert już działa (ikona w zasobniku obok zegara).",
                "VNC Alert",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        using var previewActivation = new PreviewActivation();
        using var stopRequest = new StopRequest();
        using var sharedConfigReload = new SharedConfigReloadRequest();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupErrors.AppendLog(StartupErrors.DefaultLogsDir, DateTimeOffset.Now,
                "Nieobsłużony wyjątek — program kończy pracę.", e.ExceptionObject as Exception);
        try
        {
            RunInstance(args, watchdogStart, previewActivation, stopRequest, sharedConfigReload);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            string? logPath = StartupErrors.AppendLog(StartupErrors.DefaultLogsDir, DateTimeOffset.Now,
                "Nieoczekiwany błąd — program zatrzymany.", ex);
            StartupErrors.Show("VNC Alert — nieoczekiwany błąd",
                StartupErrors.WindowText("Wystąpił nieoczekiwany błąd: " + ex.Message, logPath), stopRequest);
        }
    }

    private static void RunInstance(string[] args, bool watchdogStart, PreviewActivation previewActivation, StopRequest stopRequest,
        SharedConfigReloadRequest sharedConfigReload)
    {
        // Program uruchomił człowiek (skrót, autostart, instalator), a nie watchdog: okno ciszy po
        // ostatniej komendzie zatrzymania przestaje obowiązywać, więc watchdog znów pilnuje procesu.
        if (!watchdogStart) WatchdogTask.ClearSuppression(AppPaths.DataDir);

        AppConfig appConfig;
        RuleSet ruleSet;
        GroupSettingsRecovery? groupRecovery = null;
        string? groupKey = null;
        string? groupFailure = null;
        // Ostrzeżenia z wczytania config.json (dziś: pole "id" profilu ekranu inne niż jego klucz — program używa klucza). Dotyczą profili z lokalnego pliku: w grupie profile ekranu zastępuje profil grupy, więc
        // wtedy ich nie logujemy — tak jak ostrzeżenia niżej, liczone dla konfiguracji, której program faktycznie użyje.
        var configFileWarnings = new List<string>();
        try
        {
            try { AppPaths.EnsureDefaultsCopied(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ConfigException(StartupErrors.MissingDefaultsMessage(AppPaths.ConfigPath, AppPaths.RulesPath, ex), ex);
            }

            appConfig = ConfigLoader.LoadAppConfig(AppPaths.ConfigPath, configFileWarnings);
            ConfigLoader.Validate(appConfig);

            string rulesPath = AppPaths.Resolve(appConfig.RulesFile);
            ruleSet = ConfigLoader.LoadRules(rulesPath);
            ConfigLoader.Validate(ruleSet, appConfig);
            try
            {
                var groupStore = GroupSettingsClient.OpenStore();
                groupRecovery = groupStore.ReadForStartup();
                if (groupRecovery.Data is { } committed)
                {
                    appConfig = committed.ApplyTo(appConfig);
                    ruleSet = committed.Profile.Rules;
                    configFileWarnings.Clear(); // profile ekranu z profilu grupy — lokalnych program nie używa
                    try { groupKey = groupStore.ReadCommittedNetworkKey(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { groupKey = null; }
                }
            }
            catch (InvalidDataException ex)
            {
                // Uszkodzony dziennik albo zatwierdzona generacja: nie przerywamy startu. Działa lokalna
                // konfiguracja z config.json bez UDP, a panel pokazuje powód zamiast pozornej współpracy.
                groupRecovery = null; groupKey = null;
                groupFailure = "Uszkodzone ustawienia grupy komputerów — działa obserwacja lokalna. " + ex.Message;
                appConfig = appConfig with { Cluster = appConfig.Cluster with { Enabled = false } };
            }
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            Environment.ExitCode = 1;
            string? logPath = StartupErrors.AppendLog(StartupErrors.DefaultLogsDir, DateTimeOffset.Now,
                "Błąd konfiguracji — program nie wystartował. " + ex.Message, null);
            StartupErrors.Show("VNC Alert — błąd konfiguracji", StartupErrors.WindowText(ex.Message, logPath), stopRequest);
            return;
        }

        IReadOnlyList<string> configWarnings = [.. ConfigLoader.Warnings(appConfig), .. ConfigLoader.Warnings(ruleSet, appConfig)];

        AppConfig baseConfig = appConfig;
        var sharedConfig = new SharedConfigSync(new SharedConfigStore(SharedConfigPath), Environment.MachineName);
        sharedConfig.Reload();
        string? sharedConfigWarning = sharedConfig.LoadError is { } loadError
            ? "Nie wczytano wspólnej konfiguracji z Panelu administracyjnego — program działa na wartościach z config.json. " + loadError
            : null;
        if (sharedConfig.Current is { } sharedLayer)
        {
            var effective = sharedLayer.ApplyTo(baseConfig);
            try { ConfigLoader.Validate(effective); appConfig = effective; }
            catch (ConfigException ex)
            {
                sharedConfigWarning = "Wspólna konfiguracja z Panelu administracyjnego zawiera wartości, "
                    + "których ta wersja programu nie przyjmuje — pomijam ją i działam na wartościach z config.json. " + ex.Message;
            }
        }

        var logSink = new InMemoryLogSink(capacity: 500);
        string logsDir = AppPaths.Resolve(appConfig.Paths.LogsDir);

        IHost host = BuildHost(args, appConfig, ruleSet, logSink, logsDir, baseConfig, sharedConfig,
            groupRecovery: groupRecovery, groupKey: groupKey, groupFailure: groupFailure);

        try
        {
            host.Start();
            Log.Information("Uruchomiono VNC Alert: {Build}", host.Services.GetRequiredService<BuildIdentity>().DisplayText);
            if (sharedConfigWarning is not null) Log.Warning("{Warning}", sharedConfigWarning);
            foreach (string warning in configFileWarnings) Log.Warning("{Warning}", warning);
            foreach (string warning in configWarnings) Log.Warning("{Warning}", warning);
            if (groupRecovery?.ProfileFingerprintRenewed == true)
                Log.Information("Ustawienia grupy zapisała inna wersja programu — odcisk profilu wyliczono na nowo "
                    + "z zapisanego profilu. Grupa działa dalej; wszystkie komputery grupy mają mieć tę samą wersję.");

            var app = new App
            {
                Services = host.Services,
                StartupArgs = args,
            };

            previewActivation.Listen(() => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!app.Dispatcher.HasShutdownStarted)
                    host.Services.GetRequiredService<TrayIconService>().ShowStatus();
            })));
            stopRequest.Listen(() => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (app.Dispatcher.HasShutdownStarted) return;
                Log.Information("Zatrzymanie programu na żądanie (vncalert killapp albo instalator przed podmianą plików).");
                app.Shutdown();
            })));
            var orchestrator = host.Services.GetRequiredService<AppOrchestrator>();
            sharedConfigReload.Listen(() => _ = Task.Run(async () =>
            {
                try { await orchestrator.ReloadSharedConfigFromDiskAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Error(ex, "Nie udało się zastosować wspólnej konfiguracji zapisanej poza Panelem."); }
            }));
            app.Run();
        }
        finally
        {
            try
            {
                host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Błąd podczas zatrzymywania programu.");
            }

            host.Dispose();
            Log.CloseAndFlush();
        }
    }

    private static int RunStartupSmokeTest(bool monitoringDisabled)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "smoke-test-output");
        Directory.CreateDirectory(output);
        try
        {
            var config = ConfigLoader.LoadAppConfig(Path.Combine(AppContext.BaseDirectory, "config.json"));
            ConfigLoader.Validate(config);
            var rules = ConfigLoader.LoadRules(Path.Combine(AppContext.BaseDirectory, config.RulesFile));
            ConfigLoader.Validate(rules, config);
            // Izolacja smoke: brak rzeczywistych maszyn i UDP. Test wznowienia dodaje
            // później wyłącznie syntetyczny monitor z obrazem PNG i adresem loopback.
            config = config with
            {
                Enabled = !monitoringDisabled,
                Machines = monitoringDisabled
                    ? [new MachineConfig { Id = "smoke", Name = "TEST", Host = "127.0.0.1", Port = 1, Profile = "opencnc" }]
                    : [],
                Cluster = config.Cluster with { Enabled = false },
                Ocr = config.Ocr with { Engine = "none" },
                // Test startu w izolacji od sieci: żadnego zapytania do katalogu wydań (i tak portable → Disabled).
                Updates = config.Updates with { Enabled = false },
                Paths = config.Paths with
                {
                    LogsDir = Path.Combine(output, "logs"),
                    CapturesDir = Path.Combine(output, "captures"),
                    HistoryFile = Path.Combine(output, "history.csv"),
                    TestScreensDir = Path.Combine(output, "screens"),
                },
            };
            var smokeShared = new SharedConfigSync(
                new SharedConfigStore(Path.Combine(output, "shared-config", "shared-config.json")), Environment.MachineName);
            smokeShared.Reload();
            using var host = BuildHost([], config, rules, new InMemoryLogSink(100), config.Paths.LogsDir,
                config, smokeShared, smokeTest: true);
            try
            {
                host.Start();
                var app = new App { Services = host.Services, IsStartupSmokeTest = true };
                int exitCode = app.Run();
                return app.StartupSmokeTestPassed ? exitCode : 1;
            }
            finally
            {
                host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static IHost BuildHost(string[] args, AppConfig appConfig, RuleSet ruleSet, InMemoryLogSink logSink, string logsDir, AppConfig baseConfig, SharedConfigSync sharedConfig, bool smokeTest = false, GroupSettingsRecovery? groupRecovery = null, string? groupKey = null, string? groupFailure = null)
    {
        return Host.CreateDefaultBuilder(args)
            .UseContentRoot(AppPaths.ExeDir)
            .UseSerilog((_, services, loggerConfig) =>
            {
                loggerConfig
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        Path.Combine(logsDir, "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Sink(logSink);
            })
            .ConfigureServices(services =>
            {
                ConfigureServices(services, appConfig, ruleSet, logSink, baseConfig, sharedConfig, groupRecovery, groupKey, groupFailure);
                if (smokeTest)
                {
                    services.AddSingleton<ISecretStore>(new InMemorySecretStore());
                    services.AddSingleton(_ => new AlarmHelpService(ruleSet,
                        Path.Combine(Path.GetDirectoryName(logsDir)!, "alarm-instructions.json"), false));
                    // Test startu nie może zmieniać ustawień nakładki ani motywu w prawdziwym profilu użytkownika.
                    services.AddSingleton(_ => new OverlaySettingsStore(
                        Path.Combine(Path.GetDirectoryName(logsDir)!, "overlay.json")));
                    services.AddSingleton(_ => new ThemeSettingsStore(
                        Path.Combine(Path.GetDirectoryName(logsDir)!, "theme.json")));
                }
            })
            .Build();
    }

    private static void ConfigureServices(IServiceCollection services, AppConfig appConfig, RuleSet ruleSet, InMemoryLogSink logSink, AppConfig baseConfig, SharedConfigSync sharedConfig, GroupSettingsRecovery? groupRecovery, string? groupKey, string? groupFailure)
    {
        // Cała reszta programu widzi konfigurację EFEKTYWNĄ (z nałożoną warstwą z Panelu); orkiestrator
        // dostaje dodatkowo bazę, bo po każdej zmianie warstwy liczy efektywną od nowa.
        services.AddSingleton(appConfig);
        services.AddSingleton(new StartupConfig(baseConfig, appConfig));
        services.AddSingleton(sharedConfig);
        services.AddSingleton(BuildIdentity.FromInformationalVersion(
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion));
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton(ruleSet);
        services.AddSingleton(_ =>
        {
            var help = new AlarmHelpService(ruleSet, Path.Combine(AppPaths.DataDir, "alarm-instructions.json"), AppPaths.IsInstalled);
            if (groupRecovery?.Data is { } data) help.UseProfile(data.Profile);
            return help;
        });
        services.AddSingleton(logSink);
        services.AddSingleton<IClock>(SystemClock.Instance);

        services.AddSingleton<ISecretStore>(sp =>
        {
            ILoggerFactory lf = sp.GetRequiredService<ILoggerFactory>();
            return AppPaths.IsInstalled
                ? DpapiSecretStore.ForMachine(AppPaths.DataDir, lf.CreateLogger<DpapiSecretStore>())
                : DpapiSecretStore.ForCurrentUser(AppPaths.UserDataDir, lf.CreateLogger<DpapiSecretStore>());
        });

        services.AddSingleton(sp =>
        {
            var mapper = new MessageMapper(sp.GetRequiredService<RuleSet>());
            Microsoft.Extensions.Logging.ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MaszynyAlert.App.Rules");
            foreach (string warning in mapper.Warnings)
            {
                logger.LogWarning("{Warning}", warning);
            }

            logger.LogInformation("Słownik błędów: {Codes} wpisów kodów alarmów, {Rules} reguł tekstowych.", mapper.CodeCount, mapper.RuleCount);
            if (mapper.DatabasesSummary.Length > 0)
            {
                // Osobna baza błędów na typ maszyn (patrz Core.Config.RuleDatabase) — z logu ma być widać,
                // która maszyna czym jest opisywana, bo pomyłka tutaj daje alert z opisem obcego błędu.
                logger.LogInformation("Bazy błędów po typie maszyn — {Databases}.", mapper.DatabasesSummary);
            }

            return mapper;
        });

        services.AddSingleton(sp => new AckMemory(
            sp.GetRequiredService<IClock>(),
            TimeSpan.FromHours(sp.GetRequiredService<AppConfig>().Timing.AckMemoryHours)));

        services.AddSingleton(sp => new AlertStateMachine(
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<AppConfig>().Timing,
            sp.GetRequiredService<AckMemory>(),
            sp.GetRequiredService<ILogger<AlertStateMachine>>()));

        services.AddSingleton<IOcrEngine>(sp =>
        {
            AppConfig cfg = sp.GetRequiredService<AppConfig>();
            ILoggerFactory lf = sp.GetRequiredService<ILoggerFactory>();
            switch (cfg.Ocr.Engine.Trim().ToLowerInvariant())
            {
                case "windows":
                    // Silnik OCR jest tu JEDNYM singletonem współdzielonym przez wszystkie MachineMonitor
                    // (jeden na maszynę), które wołają RecognizeAsync RÓWNOLEGLE — Windows.Media.Ocr nie
                    // gwarantuje bezpieczeństwa przy współbieżnych wywołaniach, więc opakowujemy w
                    // SerializingOcrEngine (kolejkuje do jednego wywołania na raz, patrz jego XML-doc).
                    return new SerializingOcrEngine(
                        new WindowsOcrEngine(cfg.Ocr.Language, lf.CreateLogger<WindowsOcrEngine>()),
                        logger: lf.CreateLogger<SerializingOcrEngine>());
                case "tesseract":
                    lf.CreateLogger("MaszynyAlert.App").LogWarning(
                        "Silnik OCR \"tesseract\" nie jest jeszcze dostępny w tej wersji programu — używam \"none\".");
                    return new NullOcrEngine();
                default:
                    return new NullOcrEngine();
            }
        });

        services.AddSingleton(sp =>
        {
            var runtime = new ClusterRuntime(appConfig.Cluster,
                appConfig.Cluster.Group is not null ? groupKey : sp.GetRequiredService<ISecretStore>().Get(SecretKeys.ClusterHmacKey),
                sp.GetRequiredService<IClock>(), sp.GetRequiredService<AckMemory>(), Environment.MachineName,
                sp.GetRequiredService<BuildIdentity>().Version);
            runtime.InterruptedGeneration = groupRecovery?.InterruptedId;
            if (groupFailure is not null) runtime.MarkStartupFailure(groupFailure);
            return runtime;
        });

        services.AddSingleton(sp =>
        {
            AppConfig cfg = sp.GetRequiredService<AppConfig>();
            ISecretStore secrets = sp.GetRequiredService<ISecretStore>();
            ILoggerFactory lf = sp.GetRequiredService<ILoggerFactory>();
            var captures = new FileCaptureStore(AppPaths.Resolve(cfg.Paths.CapturesDir), lf.CreateLogger<FileCaptureStore>());

            return new MonitorSupervisor(
                cfg,
                machine => secrets.Get(SecretKeys.VncPassword(machine.Id)),
                sp.GetRequiredService<IOcrEngine>(),
                sp.GetRequiredService<MessageMapper>(),
                sp.GetRequiredService<AlertStateMachine>(),
                sp.GetRequiredService<IClock>(),
                captures,
                AppPaths.Resolve(cfg.Paths.TestScreensDir),
                lf);
        });

        services.AddSingleton(sp => new HistoryWriter(
            AppPaths.Resolve(sp.GetRequiredService<AppConfig>().Paths.HistoryFile),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<HistoryWriter>>()));

        services.AddSingleton(sp => new WavSoundPlayer(
            Path.Combine(AppPaths.ExeDir, "sounds", "alarm.wav"),
            sp.GetRequiredService<ILogger<WavSoundPlayer>>()));

        services.AddSingleton<PowerModeWatcher>();

        services.AddSingleton<AppOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<AppOrchestrator>());

        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        services.AddSingleton(sp => sp.GetRequiredService<UpdateService>().Coordinator);

        // ViewModele i okna — singletony: tworzone raz (na wątku UI, w App.OnStartup), później tylko chowane.
        services.AddSingleton<AlertWindow>();
        services.AddSingleton<StatusWindowViewModel>();
        services.AddSingleton<MultiComputerViewModel>();
        services.AddSingleton<MultiComputerView>();
        services.AddSingleton<StatusWindow>();
        services.AddSingleton<OperatorWindow>();
        services.AddSingleton<SetupWindowViewModel>();
        services.AddSingleton<ISettingsChangeConfirmation, DialogSettingsChangeConfirmation>();
        services.AddSingleton<IUserMessages, DesktopUserMessages>();
        services.AddSingleton(sp => new SetupWindow(sp.GetRequiredService<SetupWindowViewModel>(), sp.GetRequiredService<AppConfig>(),
            sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<AppOrchestrator>(), sp.GetRequiredService<ISettingsChangeConfirmation>(),
            sp.GetRequiredService<IUserMessages>()));
        services.AddSingleton(sp => new SetupView(new SetupWindowViewModel(), sp.GetRequiredService<AppConfig>(),
            sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<AppOrchestrator>(), includeSecrets: true,
            sp.GetRequiredService<ISettingsChangeConfirmation>(), sp.GetRequiredService<IUserMessages>()));
        services.AddSingleton<SharedConfigViewModel>();
        services.AddSingleton<SharedConfigView>();
        services.AddSingleton<IdlePopupWindow>();
        services.AddSingleton<ScreenPopupWindow>();
        services.AddSingleton<UpdateWindow>();
        // Nakładka statusu: ustawienia operatora (włączona, przenikanie kliknięć, przeciągnięte położenie)
        // leżą w profilu użytkownika, żeby ich zmiana z menu zasobnika nie wymagała uprawnień do %ProgramData%.
        services.AddSingleton(_ => new OverlaySettingsStore(
            System.IO.Path.Combine(AppPaths.UserDataDir, "overlay.json")));
        services.AddSingleton<OverlayWindow>();
        // Motyw okien (jasny/ciemny): wybór operatora w profilu użytkownika, nałożony przed utworzeniem okien
        // (App.OnStartup) i przełączany z Podglądu albo menu zasobnika.
        services.AddSingleton(_ => new ThemeSettingsStore(
            System.IO.Path.Combine(AppPaths.UserDataDir, "theme.json")));
        services.AddSingleton(sp => new ThemeService(
            sp.GetRequiredService<ThemeSettingsStore>(), sp.GetRequiredService<ILogger<ThemeService>>()));
        services.AddSingleton<TrayIconService>();
    }
}
