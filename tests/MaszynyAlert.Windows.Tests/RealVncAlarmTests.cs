using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Ocr;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MaszynyAlert.Windows.Tests;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class RealVncAlarmTests(ITestOutputHelper output)
{
    private const string MachineId = "prawdziwy-vnc";
    private const string ProfileId = "opencnc";

    /// <summary>Połączenie z serwerem VNC i pierwsza pełna klatka (z zapasem na wolny start VM po przywróceniu checkpointu).</summary>
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(30);

    /// <summary>Od zmiany obrazu na czerwony do alarmu. Typowo kilka sekund: cykl co 1 s, filtr „2 z 3”, bez reguł wyciszenia.</summary>
    private static readonly TimeSpan AlarmLimit = TimeSpan.FromSeconds(60);

    /// <summary>Obserwacja obrazu bez błędu (kontrola fałszywego alarmu) — ok. 15 cykli przy <c>pollIntervalMs</c> 1000.</summary>
    private static readonly TimeSpan CalmObservation = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ScreenVerifyLimit = TimeSpan.FromSeconds(60);

    /// <summary>Tło okna testu: jasna zieleń (barwa 120°, daleko od okna czerwieni) — po niej test poznaje, że VNC widzi JEGO okno.</summary>
    private static readonly Color Background = Color.FromRgb(208, 232, 208);

    /// <summary>Drugi odcień tła (różnica 8 na kanał — w tolerancji ±16 porównania): zmiana obrazu dla grabbera serwera VNC.</summary>
    private static readonly Color BackgroundAlt = Color.FromRgb(200, 224, 200);

    [RealVncFact]
    public async Task CzerwonyKomunikatNaPulpicie_PrawdziwyVnc_DajeAlarmBledu()
    {
        using var screen = new DesktopScreen();
        await using Harness harness = Harness.Create(output);
        screen.Show(harness.Profile.StatusBarRoi, alarm: false);

        await harness.StartAndVerifyScreenAsync(screen, nameof(CzerwonyKomunikatNaPulpicie_PrawdziwyVnc_DajeAlarmBledu));

        var sw = Stopwatch.StartNew();
        screen.SetAlarm(true);
        Alert? alarm = await harness.WaitForAsync(
            () => harness.Alerts.ActiveAlerts.FirstOrDefault(a => a.MachineId == MachineId && a.Kind == AlertKind.Error),
            AlarmLimit);

        MachineStatusSnapshot snapshot = harness.Snapshot;
        Assert.True(alarm is not null,
            $"Brak alarmu błędu po {AlarmLimit.TotalSeconds:0} s od pokazania czerwonego komunikatu. Ostatnia migawka: stan {snapshot.State}, " +
            $"czerwień {snapshot.LastRedFraction:P2} (próg {harness.Profile.MinRedFraction:P2}), RedNow {snapshot.RedNow}, " +
            $"RedConfirmed {snapshot.RedConfirmed}, błąd: {snapshot.LastError ?? "-"}.");
        output.WriteLine($"Alarm po {sw.Elapsed.TotalSeconds:0.0} s: '{alarm!.ShortText}' (kategoria {alarm.Category}), " +
            $"czerwień {snapshot.LastRedFraction:P2}, OCR: '{snapshot.LastOcrText ?? "-"}'.");

        // Po zdjęciu komunikatu czerwień znika z kolejnych klatek (alert zamyka się dopiero po episodeCloseAfterMs — tego nie czekamy).
        screen.SetAlarm(false);
        MachineStatusSnapshot? cleared = await harness.WaitForAsync(
            () => harness.Snapshot is { RedNow: false, LastRedFraction: 0 } s && s.LastFrameAt > DateTimeOffset.UtcNow.AddSeconds(-5) ? s : null,
            AlarmLimit);
        Assert.True(cleared is not null, "Czerwień nie zniknęła z obrazu VNC po zdjęciu komunikatu.");
    }

    [RealVncFact]
    public async Task KomunikatBezCzerwieniNaPulpicie_PrawdziwyVnc_NieDajeAlarmu()
    {
        using var screen = new DesktopScreen();
        await using Harness harness = Harness.Create(output);
        screen.Show(harness.Profile.StatusBarRoi, alarm: false);

        await harness.StartAndVerifyScreenAsync(screen, nameof(KomunikatBezCzerwieniNaPulpicie_PrawdziwyVnc_NieDajeAlarmu));

        DateTimeOffset? firstFrame = harness.Snapshot.LastFrameAt;
        double maxRed = 0;
        bool anyRedNow = false;
        var sw = Stopwatch.StartNew();
        int shade = 0;
        while (sw.Elapsed < CalmObservation)
        {
            // Serwer VNC odpowiada na żądanie przyrostowe (obszar komunikatu) dopiero po zmianie obrazu, a pełna klatka idzie co
            // fullFrameIntervalMs (60 s) — bez zmiany odcienia co 2 s statyczny pulpit nie dałby w 15 s żadnej nowej klatki.
            if (sw.Elapsed.TotalSeconds >= 2 * (shade + 1)) screen.SetShade(alternate: ++shade % 2 == 1);
            MachineStatusSnapshot s = harness.Snapshot;
            maxRed = Math.Max(maxRed, s.LastRedFraction);
            anyRedNow |= s.RedNow;
            Assert.DoesNotContain(harness.Alerts.ActiveAlerts, a => a.MachineId == MachineId && a.Kind == AlertKind.Error);
            await Task.Delay(200);
        }

        MachineStatusSnapshot last = harness.Snapshot;
        output.WriteLine($"Obserwacja {CalmObservation.TotalSeconds:0} s: maks. czerwień {maxRed:P2}, stan {last.State}, ostatnia klatka {last.LastFrameAt:O}.");
        Assert.Equal(MachineConnectionState.Connected, last.State);
        Assert.True(last.LastFrameAt > firstFrame, "W czasie obserwacji nie przyszła żadna nowa klatka z VNC — test niczego nie sprawdził.");
        Assert.False(anyRedNow, $"Czerwień w pasku przy żółtym komunikacie (maks. {maxRed:P2}).");
        Assert.DoesNotContain(harness.Alerts.ActiveAlerts, a => a.MachineId == MachineId);
    }

    /// <summary>Program w wersji minimalnej: nadzorca jednej maszyny „prawdziwy VNC” z prawdziwym źródłem VNC.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;

        private Harness(ITestOutputHelper output, MonitorSupervisor supervisor, AlertStateMachine alerts, ScreenProfile profile)
        {
            _output = output;
            Supervisor = supervisor;
            Alerts = alerts;
            Profile = profile;
        }

        public MonitorSupervisor Supervisor { get; }

        public AlertStateMachine Alerts { get; }

        public ScreenProfile Profile { get; }

        public MachineStatusSnapshot Snapshot => Supervisor.Snapshots.Single(s => s.MachineId == MachineId);

        public static Harness Create(ITestOutputHelper output)
        {
            string configDir = Path.Combine(AppContext.BaseDirectory, "config");
            AppConfig example = ConfigLoader.LoadAppConfig(Path.Combine(configDir, "config.example.json"));
            ScreenProfile profile = example.Profiles[ProfileId] with { Idle = null, MainScreen = null, ProgramState = null };

            (string host, int port) = RealVncFactAttribute.ReadEndpoint();
            string? password = RealVncFactAttribute.ReadPassword();
            var machine = new MachineConfig
            {
                Id = MachineId,
                Name = "Prawdziwy VNC (test)",
                Host = host,
                Port = port,
                Profile = ProfileId,
                Enabled = true,
                Mode = ConnectionMode.Persistent,
                RequiresPassword = password is not null,
            };

            AppConfig cfg = example with
            {
                Machines = [machine],
                Profiles = new Dictionary<string, ScreenProfile> { [ProfileId] = profile },
                Timing = example.Timing with { ConnectTimeoutMs = 5000, ReconnectBackoffMs = [1000, 2000] },
            };

            var loggers = new TestOutputLoggerFactory(output);
            string[] languages = [.. WindowsOcrEngine.AvailableLanguages()];
            var ocr = new WindowsOcrEngine("en-US", loggers.CreateLogger("ocr"));
            output.WriteLine($"VNC {host}:{port}, hasło: {(password is null ? "brak (typ None)" : "tak")}; OCR: {ocr.Name} {ocr.LanguageTag ?? "-"} " +
                $"(języki: {(languages.Length == 0 ? "brak" : string.Join(", ", languages))}).");

            IClock clock = SystemClock.Instance;
            var alerts = new AlertStateMachine(clock, cfg.Timing, new AckMemory(clock, TimeSpan.FromHours(2)));
            var supervisor = new MonitorSupervisor(
                cfg,
                _ => password,
                ocr,
                new MessageMapper(ConfigLoader.LoadRules(Path.Combine(configDir, "rules.example.json"))),
                alerts,
                clock,
                new NullCaptureStore(),
                testScreensDir: null,
                loggers);
            return new Harness(output, supervisor, alerts, profile);
        }

        /// <summary>
        /// Start nadzorcy, połączenie i pełna klatka z VNC, na której jest okno testu (tło <see cref="Background"/>) — bez tego
        /// wynik „brak alarmu” mógłby znaczyć tylko tyle, że serwer pokazuje inny pulpit. Klatka bez okna testu (np. cała czarna
        /// z TightVNC przed pierwszym odczytem ekranu) nie kończy testu od razu: do <see cref="ScreenVerifyLimit"/> test zmienia
        /// odcień tła i prosi o nową pełną klatkę (<see cref="MachineMonitor.RequestFullRefreshAsync"/>); po porażce zapisuje
        /// ostatnią klatkę jako PNG (ścieżka i statystyka pikseli w wyniku testu).
        /// </summary>
        public async Task StartAndVerifyScreenAsync(DesktopScreen screen, string testName)
        {
            await Supervisor.StartAsync(CancellationToken.None);
            MachineMonitor monitor = Supervisor.Get(MachineId) ?? throw new InvalidOperationException("Brak obserwatora maszyny testowej.");
            await monitor.RequestFullRefreshAsync();

            MachinePreviewFrame? frame = await WaitForAsync(
                () => Snapshot.State == MachineConnectionState.Connected && monitor.PreviewFrame is { Source: "vnc" } f ? f : null,
                ConnectLimit);
            MachineStatusSnapshot s = Snapshot;
            Assert.True(frame is not null,
                $"Nie połączono z serwerem VNC {s.Host}:{s.Port} w {ConnectLimit.TotalSeconds:0} s (stan {s.State}, błąd: {s.LastError ?? "-"}). " +
                "Czy TightVNC Server działa i słucha na tym adresie (test negatywny oczekuje tu właśnie tego błędu)?");

            RgbaImage image = frame!.Image;
            _output.WriteLine($"Serwer: {s.Info?.ToString() ?? "-"}; klatka {image.Width}x{image.Height}; ROI {Profile.StatusBarRoi}.");
            Assert.True(image.Width >= Profile.StatusBarRoi.Right && image.Height >= Profile.StatusBarRoi.Bottom,
                $"Pulpit {image.Width}x{image.Height} jest mniejszy niż ROI paska profilu {ProfileId} ({Profile.StatusBarRoi}) — ustaw rozdzielczość VM co najmniej 1024x768.");

            var sw = Stopwatch.StartNew();
            int attempt = 1;
            int matching = CountBackgroundPoints(image);
            while (matching < 4 && sw.Elapsed < ScreenVerifyLimit)
            {
                _output.WriteLine($"Próba {attempt}: {matching}/5 punktów w kolorze tła ({Describe(image)}) — zmiana tła i nowa pełna klatka.");
                screen.SetShade(alternate: attempt % 2 == 1);
                await Task.Delay(500);
                DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
                await monitor.RequestFullRefreshAsync();
                MachinePreviewFrame? next = await WaitForAsync(
                    () => monitor.PreviewFrame is { Source: "vnc" } f && f.CapturedAt > requestedAt ? f : null,
                    TimeSpan.FromSeconds(5));
                if (next is not null) image = next.Image;
                attempt++;
                matching = CountBackgroundPoints(image);
            }

            if (matching >= 4)
            {
                _output.WriteLine($"Okno testu widoczne w VNC po {attempt} próbie(-ach), {sw.Elapsed.TotalSeconds:0.0} s.");
                return;
            }

            string png = SaveFramePng(image, testName);
            Assert.Fail(
                $"Obraz z VNC nie pokazuje okna testu po {attempt} próbach w {sw.Elapsed.TotalSeconds:0} s ({matching}/5 punktów w kolorze tła; " +
                $"{Describe(image)}; klatka: {png}). Serwer VNC pokazuje inną sesję/pulpit, okno nie jest na wierzchu albo grabber serwera " +
                "nie odczytuje ekranu.");
        }

        /// <summary>Kilka punktów poza ROI (i poza środkiem, gdzie może stać kursor) w kolorze tła okna testu (któregokolwiek odcienia).</summary>
        private static int CountBackgroundPoints(RgbaImage image)
        {
            (int X, int Y)[] points =
            [
                (image.Width / 10, image.Height / 2), (image.Width / 3, image.Height * 2 / 3), (image.Width * 2 / 3, image.Height * 3 / 4),
                (image.Width * 9 / 10, image.Height * 9 / 10), (image.Width / 5, image.Height * 9 / 10),
            ];
            return points.Count(p => Near(image.GetPixel(p.X, p.Y), Background));
        }

        /// <summary>Statystyka klatki do logu: udział czarnych pikseli, liczba kolorów (do 1000), średni kolor i piksel kontrolny.</summary>
        private static string Describe(RgbaImage image)
        {
            byte[] px = image.Pixels;
            long black = 0, r = 0, g = 0, b = 0;
            var colors = new HashSet<int>();
            int n = image.Width * image.Height;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                if (px[o] == 0 && px[o + 1] == 0 && px[o + 2] == 0) black++;
                r += px[o];
                g += px[o + 1];
                b += px[o + 2];
                if (colors.Count < 1000) colors.Add(px[o] << 16 | px[o + 1] << 8 | px[o + 2]);
            }

            return $"czarne {(double)black / n:P1}, kolorów {(colors.Count >= 1000 ? ">=1000" : colors.Count.ToString())}, " +
                $"średni RGB({r / n}, {g / n}, {b / n}), piksel ({image.Width / 10},{image.Height / 2}) {image.GetPixel(image.Width / 10, image.Height / 2)}";
        }

        /// <summary>
        /// Ostatnia klatka jako PNG w katalogu wyjścia testów (<c>real-vnc\</c>; w VM leży do przywrócenia checkpointu) i — bo CI
        /// nie wgrywa artefaktów — jej pomniejszenie 1:4 w base64 w wyniku testu (dekodowanie: <c>[IO.File]::WriteAllBytes(...,
        /// [Convert]::FromBase64String(...))</c>).
        /// </summary>
        private string SaveFramePng(RgbaImage image, string testName)
        {
            try
            {
                string base64 = Convert.ToBase64String(EncodePng(image.Downscale(4)));
                _output.WriteLine(base64.Length <= 200_000
                    ? $"Klatka 1:4 (PNG base64): {base64}"
                    : $"Klatka 1:4 pominięta w logu (base64 {base64.Length} znaków).");

                string dir = Path.Combine(AppContext.BaseDirectory, "real-vnc");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{testName}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                File.WriteAllBytes(path, EncodePng(image));
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return $"nie zapisano PNG ({ex.Message})";
            }
        }

        private static byte[] EncodePng(RgbaImage image)
        {
            byte[] bgra = new byte[image.Pixels.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = image.Pixels[i + 2];
                bgra[i + 1] = image.Pixels[i + 1];
                bgra[i + 2] = image.Pixels[i];
                bgra[i + 3] = 255;
            }

            BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        public async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan limit) where T : class
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (probe() is { } value) return value;
                if (sw.Elapsed > limit) return null;
                await Task.Delay(200);
            }
        }

        private static bool Near(Rgba a, Color b) =>
            Math.Abs(a.R - b.R) <= 16 && Math.Abs(a.G - b.G) <= 16 && Math.Abs(a.B - b.B) <= 16; // zapas na 16-bitowy format piksela serwera

        public async ValueTask DisposeAsync()
        {
            await Supervisor.StopAsync();
            await Supervisor.StoppedTask.WaitAsync(TimeSpan.FromSeconds(20));
            await Supervisor.DisposeAsync();
        }
    }

    /// <summary>
    /// Okno testu na pulpicie: pełny ekran główny, na wierzchu, bez kursora. Własny wątek STA z dyspozytorem (bez obiektu
    /// <see cref="Application"/>). Ramka komunikatu stoi dokładnie w <c>statusBarRoi</c> profilu w pikselach URZĄDZENIA —
    /// współrzędne WPF przeliczone przez <c>TransformFromDevice</c> (skalowanie ekranu VM inne niż 100 % nie przesuwa ramki).
    /// </summary>
    private sealed class DesktopScreen : IDisposable
    {
        private const string AlarmText = "TEST: Spindle alarm, please check";
        private const string CalmText = "All procedures have been processed";

        /// <summary>Pole komunikatu: biel jak na HMI i jej drugi odcień do <see cref="SetShade"/>.</summary>
        private static readonly Color Field = Color.FromRgb(250, 250, 250);
        private static readonly Color FieldAlt = Color.FromRgb(238, 238, 238);

        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;
        private Window? _window;
        private TextBlock? _text;
        private Border? _frame;

        public DesktopScreen()
        {
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Okno testu prawdziwego VNC",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            _dispatcher = dispatcher!;
        }

        public void Show(RectInt roi, bool alarm) => _dispatcher.Invoke(() =>
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Background) };
            _window = new Window
            {
                Title = "VNC Alert — test prawdziwego VNC",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
                Width = SystemParameters.PrimaryScreenWidth,
                Height = SystemParameters.PrimaryScreenHeight,
                Topmost = true,
                ShowInTaskbar = false,
                Cursor = Cursors.None,
                Content = canvas,
            };
            _window.Show();

            Matrix fromDevice = PresentationSource.FromVisual(_window)!.CompositionTarget!.TransformFromDevice;
            Point topLeft = fromDevice.Transform(new Point(roi.X, roi.Y));
            Point bottomRight = fromDevice.Transform(new Point(roi.Right, roi.Bottom));
            double height = bottomRight.Y - topLeft.Y;

            _text = new TextBlock
            {
                FontFamily = new FontFamily("Arial"),
                FontWeight = FontWeights.Bold,
                FontSize = height * 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            TextOptions.SetTextRenderingMode(_text, TextRenderingMode.Grayscale);
            var frame = new Border
            {
                Width = bottomRight.X - topLeft.X,
                Height = height,
                BorderBrush = new SolidColorBrush(Color.FromRgb(40, 90, 200)),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Field),
                Child = _text,
            };
            _frame = frame;
            Canvas.SetLeft(frame, topLeft.X);
            Canvas.SetTop(frame, topLeft.Y);
            canvas.Children.Add(frame);
            Apply(alarm);
            _window.Activate();
        });

        public void SetAlarm(bool alarm) => _dispatcher.Invoke(() => Apply(alarm));

        /// <summary>
        /// Drugi odcień tła okna i pola komunikatu (zielony i biały — bez czerwieni) albo pierwotny: zmiana obrazu, którą widzi serwer
        /// VNC — na całym ekranie (ponawianie pełnej klatki) i w obszarze komunikatu (żądania przyrostowe obserwatora).
        /// </summary>
        public void SetShade(bool alternate) => _dispatcher.Invoke(() =>
        {
            if (_window?.Content is Canvas canvas) canvas.Background = new SolidColorBrush(alternate ? BackgroundAlt : Background);
            if (_frame is not null) _frame.Background = new SolidColorBrush(alternate ? FieldAlt : Field);
        });

        private void Apply(bool alarm)
        {
            _text!.Text = alarm ? AlarmText : CalmText;
            _text.Foreground = new SolidColorBrush(alarm ? Color.FromRgb(255, 0, 0) : Color.FromRgb(230, 200, 0));
        }

        public void Dispose()
        {
            try
            {
                _dispatcher.Invoke(() => _window?.Close());
            }
            finally
            {
                _dispatcher.InvokeShutdown();
                _thread.Join(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>Log nadzorcy i obserwatora do wyniku testu (diagnoza czerwonego przebiegu na VM bez dostępu do pulpitu).</summary>
    private sealed class TestOutputLoggerFactory(ITestOutputHelper output) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(output, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(ITestOutputHelper output, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                try
                {
                    output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : " | " + exception.Message)}");
                }
                catch (InvalidOperationException)
                {
                    // Log po zakończeniu testu (pętla obserwatora jeszcze się zamyka) — xUnit już nie przyjmuje wyjścia.
                }
            }
        }
    }
}
