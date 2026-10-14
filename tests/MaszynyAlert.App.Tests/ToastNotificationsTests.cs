using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Diagnostics;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MaszynyAlert.App.Tests;

public sealed class ToastNotificationsTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private static readonly Size ToastSize = new(320, 86);

    // ===== Pozycja (czysta funkcja) =====

    /// <summary>Dolna krawędź dymka = dolna krawędź okna, w którym zapisano: spod niej dymek się wysuwa i pod nią chowa.</summary>
    [Fact]
    public void ToastStandsCenteredOnTheBottomEdgeOfTheWindowItWasSavedIn()
    {
        Point at = WindowPlacement.ToastPosition(WorkArea, ToastSize, new Rect(100, 100, 1000, 800));

        Assert.Equal(100 + ((1000 - 320) / 2.0), at.X, 3);
        Assert.Equal(900 - 86, at.Y, 3);
    }

    [Fact]
    public void WithoutAWindow_ToastStandsInTheBottomRightCornerNextToTheTray()
    {
        Point at = WindowPlacement.ToastPosition(WorkArea, ToastSize, anchor: null);

        Assert.Equal(1920 - 320 - WindowPlacement.DefaultEdgeMargin, at.X, 3);
        Assert.Equal(1040 - 86, at.Y, 3); // dolna krawędź na pasku zadań — dymek chowa się pod obszarem roboczym
    }

    [Fact]
    public void ToastNeverLeavesTheWorkArea_EvenForAWindowHangingOverTheEdge()
    {
        // Okno wysunięte w lewo i w dół poza obszar roboczy (pasek zadań): dymek zostaje w całości na ekranie.
        Point at = WindowPlacement.ToastPosition(WorkArea, ToastSize, new Rect(-600, 700, 700, 600));

        Assert.Equal(WindowPlacement.DefaultEdgeMargin, at.X, 3);
        Assert.Equal(1040 - 86, at.Y, 3);
    }

    [Fact]
    public void WindowOnAnotherMonitor_IsTreatedAsNoWindow()
    {
        Point at = WindowPlacement.ToastPosition(WorkArea, ToastSize, new Rect(2200, 100, 800, 600));

        Assert.Equal(WindowPlacement.ToastPosition(WorkArea, ToastSize, anchor: null), at);
    }

    // ===== Czas =====

    [Fact]
    public void EveryToastStaysTwoSeconds_ThenSlidesDownOutOfSight()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ToastNotifications.DisplayDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(320), ToastWindow.ShowDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(550), ToastWindow.HideDuration);

        WpfTestHost.Run(() =>
        {
            foreach (NotificationKind kind in Enum.GetValues<NotificationKind>())
            {
                ToastWindow toast = Away(ToastNotifications.Show(kind, "Zapisano ustawienia",
                    "Zmiana klucza sieciowego lub priorytetu zadziała po ponownym uruchomieniu programu."));
                Assert.Equal(TimeSpan.FromSeconds(2), toast.Duration);
                toast.Dismiss(immediately: true);
            }
        });
    }

    /// <summary>
    /// Karta jest przycinana przy dolnej krawędzi okna dymka (która leży na dolnej krawędzi okna programu) i ma nad nią odstęp — dlatego
    /// przesunięta o <see cref="ToastWindow.HiddenOffset"/> znika w całości pod tą krawędzią. Zwykłe <see cref="ToastWindow.Dismiss"/>
    /// (koniec czasu, kliknięcie) nie zamyka od razu — karta zjeżdża w dół animacją.
    /// </summary>
    [Fact]
    public void CardIsClippedAtTheBottomEdge_AndDismissSlidesItDownInsteadOfClosingAtOnce()
    {
        WpfTestHost.Run(() =>
        {
            ToastWindow toast = Open(NotificationKind.Success, "Zapisano raport PDF", null);
            try
            {
                Assert.True(toast.Stage.ClipToBounds);
                Assert.True(toast.Card.Margin.Bottom >= 20);
                Assert.Equal(toast.Card.ActualHeight + toast.Card.Margin.Bottom, toast.HiddenOffset, 3);

                toast.Dismiss();

                Assert.True(toast.IsVisible);
                Assert.True(toast.Slide.HasAnimatedProperties);
                Assert.True(toast.Card.HasAnimatedProperties);
            }
            finally
            {
                toast.Close();
            }
        });
    }

    // ===== Okno dymka =====

    /// <summary>Tło, ramka, kółko i tekst z palety motywu (jak Panel) — w obu motywach i dla każdego rodzaju.</summary>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ToastTakesItsColorsFromTheTheme(AppTheme theme)
    {
        WpfTestHost.Run(() => WithTheme(theme, () =>
        {
            IReadOnlyDictionary<string, Color> palette = ThemePalette.ColorsFor(theme);
            foreach ((NotificationKind kind, string circle, string border) in new[]
            {
                (NotificationKind.Success, "OkGreenBrush", "OkSoftBorderBrush"),
                (NotificationKind.Warning, "WarningYellowBrush", "WarningSoftBorderBrush"),
                (NotificationKind.Error, "AlertRedBrush", "ErrorSoftBorderBrush"),
            })
            {
                ToastWindow toast = Open(kind, "Zapisano raport PDF", "raport.pdf · wiersze: 3");
                try
                {
                    Assert.Equal(palette["CardBackgroundBrush"], ColorOf(toast.Card.Background));
                    Assert.Equal(palette[border], ColorOf(toast.Card.BorderBrush));
                    Assert.Equal(palette[circle], ColorOf(toast.IconCircle.Fill));
                    Assert.Equal(palette["TextPrimaryBrush"], ColorOf(toast.TitleText.Foreground));
                    Assert.Equal(palette["TextSecondaryBrush"], ColorOf(toast.DetailText.Foreground));
                    Assert.Equal(kind == NotificationKind.Warning ? Color.FromRgb(0x3A, 0x2C, 0x00) : Colors.White, ColorOf(toast.IconGlyph.Stroke));
                }
                finally
                {
                    toast.Dismiss(immediately: true);
                }
            }

            return 0;
        }));
    }

    /// <summary>Przełączenie motywu, gdy dymek stoi, od razu zmienia jego kolory (odwołania do zasobów, nie kopie pędzli).</summary>
    [Fact]
    public void SwitchingTheThemeWhileShownRecolorsTheToast()
    {
        WpfTestHost.Run(() => WithTheme(AppTheme.Light, () =>
        {
            ToastWindow toast = Open(NotificationKind.Success, "Zapisano ustawienia", null);
            try
            {
                ThemePalette.ApplyTo(System.Windows.Application.Current.Resources, AppTheme.Dark);
                WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
                IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);
                Assert.Equal(dark["CardBackgroundBrush"], ColorOf(toast.Card.Background));
                Assert.Equal(dark["OkSoftBorderBrush"], ColorOf(toast.Card.BorderBrush));
                Assert.Equal(dark["OkGreenBrush"], ColorOf(toast.IconCircle.Fill));
                Assert.Equal(dark["TextPrimaryBrush"], ColorOf(toast.TitleText.Foreground));
            }
            finally
            {
                toast.Dismiss(immediately: true);
            }

            return 0;
        }));
    }

    /// <summary>Drugi wiersz znika, gdy go nie ma; podpowiedź i kursor „rączka” tylko, gdy kliknięcie pokaże plik.</summary>
    [Fact]
    public void DetailLineAndClickHintOnlyWhenThereIsSomethingToShow()
    {
        WpfTestHost.Run(() =>
        {
            ToastWindow plain = Open(NotificationKind.Success, "Zapisano ustawienia", null);
            ToastWindow withFile = Open(NotificationKind.Success, "Zapisano raport CSV", "raport.csv · wiersze: 7", @"C:\Raporty\raport.csv");
            try
            {
                Assert.Equal("Zapisano ustawienia", plain.Headline);
                Assert.Equal(Visibility.Collapsed, plain.DetailText.Visibility);
                Assert.Null(plain.ToolTip);
                Assert.Equal("VNC Alert", plain.Title);
                Assert.True(plain.Topmost);
                Assert.False(plain.Focusable);

                Assert.Equal("raport.csv · wiersze: 7", withFile.Detail);
                Assert.Equal(Visibility.Visible, withFile.DetailText.Visibility);
                Assert.Equal(@"C:\Raporty\raport.csv", withFile.RevealPath);
                Assert.Equal("Kliknij, aby pokazać plik w folderze", withFile.ToolTip);
            }
            finally
            {
                plain.Dismiss(immediately: true);
                withFile.Dismiss(immediately: true);
            }
        });
    }

    /// <summary>
    /// Naraz stoi jeden dymek: nowy zamyka poprzedni. <see cref="ToastNotifications.WhenClosed"/> (samodzielny edytor ustawień
    /// zamyka okno, a z nim proces) czeka na obecny dymek, a bez dymka wykonuje się od razu.
    /// </summary>
    [Fact]
    public void NewToastReplacesThePreviousOne_AndWhenClosedWaitsForIt()
    {
        WpfTestHost.Run(() =>
        {
            ToastWindow first = Away(ToastNotifications.Show(NotificationKind.Success, "Zapisano raport CSV"));
            ToastWindow? second = null;
            try
            {
                bool firstClosed = false;
                first.Closed += (_, _) => firstClosed = true;
                second = Away(ToastNotifications.Show(NotificationKind.Error, "Nie udało się zapisać raportu", "Dysk jest pełny."));

                Assert.True(firstClosed);
                Assert.Same(second, ToastNotifications.Current);

                int ran = 0;
                ToastNotifications.WhenClosed(() => ran++);
                Assert.Equal(0, ran);
                second.Dismiss(immediately: true);
                Assert.Equal(1, ran);
                Assert.Null(ToastNotifications.Current);

                ToastNotifications.WhenClosed(() => ran++);
                Assert.Equal(2, ran);
            }
            finally
            {
                second?.Dismiss(immediately: true);
                first.Dismiss(immediately: true);
            }
        });
    }

    // ===== Diagnostyka =====

    [Fact]
    public void DiagnosticsExport_SuccessAndWarningsAreToasts_WithTheArchiveToReveal()
    {
        var messages = new RecordingMessages();
        string archive = Path.Combine(@"C:\Diagnostyka", "VNC-Alert-diagnostyka-20261010.zip");

        TrayIconService.NotifyDiagnosticsSaved(messages, new DiagnosticsExportResult(archive, [], 42));
        TrayIconService.NotifyDiagnosticsSaved(messages, new DiagnosticsExportResult(archive, ["Pominięto zrzut 580.", "Brak logu 7."], 40));

        Assert.Empty(messages.Shown);
        Assert.Equal(
            [
                (NotificationKind.Success, "Zapisano diagnostykę",
                    "VNC-Alert-diagnostyka-20261010.zip · pliki: 42 · zapisano lokalnie, nie wysłano.", archive),
                (NotificationKind.Warning, "Zapisano diagnostykę z ostrzeżeniami (2)",
                    "Część materiałów mogła zostać pominięta — lista ostrzeżeń jest w manifest.json w archiwum.\n"
                    + "VNC-Alert-diagnostyka-20261010.zip · pliki: 40 · zapisano lokalnie, nie wysłano.", archive),
            ],
            messages.Notified);
    }

    // ===== Źródła: wynik zapisu nie otwiera okna z OK =====

    [Fact]
    public void SaveResultsUseToastsInsteadOfMessageBoxes()
    {
        string report = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "DowntimeView.xaml.cs"));
        Assert.DoesNotContain("MessageBox.Show(", report, StringComparison.Ordinal);
        Assert.Contains("_messages.Notify(NotificationKind.Success, SavedTitle(format)", report, StringComparison.Ordinal);
        Assert.Equal("Zapisano raport PDF", DowntimeView.SavedTitle("PDF"));

        string tray = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Services", "TrayIconService.cs"));
        Assert.DoesNotContain("VNC Alert — diagnostyka", tray, StringComparison.Ordinal);
        Assert.Contains("NotifyDiagnosticsSaved(_messages, result);", tray, StringComparison.Ordinal);

        string shared = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "SharedConfigView.xaml.cs"));
        Assert.Contains("_viewModel.Saved += title => notify.Notify(NotificationKind.Success, title);", shared, StringComparison.Ordinal);
    }

    // ===== Pomocnicze =====

    /// <summary>Dymek pokazany poza ekranem (nie miga na pulpicie), z długim czasem — test zamyka go sam.</summary>
    private static ToastWindow Open(NotificationKind kind, string title, string? detail, string? revealPath = null)
    {
        var toast = WpfTestHost.ShowOffScreen(new ToastWindow(kind, title, detail, revealPath, TimeSpan.FromMinutes(1)));
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        toast.UpdateLayout();
        return toast;
    }

    /// <summary>Dymek z <see cref="ToastNotifications.Show"/> — od razu poza ekran (zamykany, zanim zdąży się narysować).</summary>
    private static ToastWindow Away(ToastWindow toast)
    {
        toast.Left = WpfTestHost.OffScreenPosition;
        toast.Top = WpfTestHost.OffScreenPosition;
        return toast;
    }

    private static Color ColorOf(System.Windows.Media.Brush brush) => ((SolidColorBrush)brush).Color;

    /// <summary>Nakłada motyw na zasoby aplikacji, a po teście przywraca je dokładnie do stanu sprzed testu.</summary>
    private static T WithTheme<T>(AppTheme theme, Func<T> body)
    {
        ResourceDictionary resources = System.Windows.Application.Current.Resources;
        var shadowed = new Dictionary<object, object>();
        var added = new List<object>();
        foreach (string key in ThemePalette.Keys)
        {
            if (resources.Keys.Cast<object>().Contains(key)) shadowed[key] = resources[key];
            else added.Add(key);
        }

        try
        {
            ThemePalette.ApplyTo(resources, theme);
            return body();
        }
        finally
        {
            foreach ((object key, object value) in shadowed) resources[key] = value;
            foreach (object key in added) resources.Remove(key);
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        }
    }
}
