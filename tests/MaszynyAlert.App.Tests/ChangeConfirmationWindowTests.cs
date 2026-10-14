using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class ChangeConfirmationWindowTests
{
    private static readonly SettingChange[] Changes =
    [
        new("Eskalacja po „Idę sprawdzić”", "4 min", "2.5 min"),
        SettingChange.Secret("Klucz sieciowy"),
    ];

    /// <summary>Pokazuje okno modalnie przez <paramref name="show"/>, a gdy się otworzy — wykonuje <paramref name="act"/> na nim.</summary>
    private static T WithOpenDialog<T>(Func<T> show, Action<ChangeConfirmationWindow> act)
    {
        ChangeConfirmationWindow? seen = null;
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        safety.Tick += (_, _) => { safety.Stop(); seen?.Close(); };
        safety.Start();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            seen = System.Windows.Application.Current.Windows.OfType<ChangeConfirmationWindow>().Single();
            seen.Left = WpfTestHost.OffScreenPosition;
            seen.Top = WpfTestHost.OffScreenPosition;
            act(seen);
        }));
        try { return show(); }
        finally { safety.Stop(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void SaveReturnsTrue_CancelAndCloseReturnFalse_ThroughTheProgramConfirmation()
    {
        WpfTestHost.Run(() =>
        {
            var confirmation = new DialogSettingsChangeConfirmation();
            Assert.True(WithOpenDialog(() => confirmation.Confirm(Changes), w => Click(w.ConfirmButton)));
            Assert.False(WithOpenDialog(() => confirmation.Confirm(Changes), w => Click(w.CancelButton)));
            Assert.False(WithOpenDialog(() => confirmation.Confirm(Changes), w => w.Close()));
            Assert.Empty(System.Windows.Application.Current.Windows.OfType<ChangeConfirmationWindow>());
            return 0;
        });
    }

    [Fact]
    public void ButtonsAreDefaultAndCancel_ListShowsNamesBeforeAfterAndNoSecretValue()
    {
        WpfTestHost.Run(() =>
        {
            var texts = new List<string>();
            bool isDefault = false, isCancel = false;
            object? source = null;
            bool? result = WithOpenDialog(() => new ChangeConfirmationWindow(Changes).ShowDialog(), w =>
            {
                w.UpdateLayout();
                isDefault = w.ConfirmButton.IsDefault;
                isCancel = w.CancelButton.IsCancel;
                source = w.ChangeList.ItemsSource;
                texts.AddRange(Descendants<TextBlock>(w).Where(t => t.IsVisible).Select(t => t.Text));
                Click(w.CancelButton);
            });

            Assert.False(result);
            Assert.True(isDefault);
            Assert.True(isCancel);
            Assert.Same(Changes, source);
            Assert.Contains("Czy na pewno chcesz zapisać te zmiany?", texts);
            Assert.Contains("Było: 4 min", texts);
            Assert.Contains("Po zapisaniu będzie: 2.5 min", texts);
            Assert.Contains("Klucz sieciowy", texts);
            Assert.Contains("zostanie zmienione", texts);
            Assert.DoesNotContain("Było: ", texts); // sekret: wiersz „Było” zwinięty
            return 0;
        });
    }

    [Fact]
    public void DarkThemeColorsReachTheDialog()
    {
        WpfTestHost.Run(() =>
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
                ThemePalette.ApplyTo(resources, AppTheme.Dark);
                IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);
                Color background = default, item = default;
                WithOpenDialog(() => new ChangeConfirmationWindow(Changes).ShowDialog(), w =>
                {
                    w.UpdateLayout();
                    background = ((SolidColorBrush)w.Background).Color;
                    item = ((SolidColorBrush)Descendants<Border>(w.ChangeList).First(b => b.CornerRadius.TopLeft == 8).Background).Color;
                    Click(w.CancelButton);
                });
                Assert.Equal(dark["PanelBackgroundBrush"], background);
                Assert.Equal(dark["SurfaceAltBrush"], item);
            }
            finally
            {
                foreach ((object key, object value) in shadowed) resources[key] = value;
                foreach (object key in added) resources.Remove(key);
                WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            }
            return 0;
        });
    }

    [Fact]
    public void RealModalDialog_RefreshFromTheDispatcherRunsInsideTheDialogLoop_AndResetAsksAgain()
    {
        WpfTestHost.Run(() =>
        {
            AppConfig config = new() { Enabled = false };
            SharedConfigDocument Doc(double scale, int minute) => new()
            {
                Timing = new TimingConfig { EscalationAfterAckMs = 60000 }, Overlay = new OverlayConfig { Scale = scale },
                EditedAt = new DateTimeOffset(2026, 10, 6, 12, minute, 0, TimeSpan.Zero), EditedBy = "PC-B",
            };
            SharedConfigDocument document = Doc(1.4, 0);
            var applied = new List<OverlayConfig?>();
            var vm = new SharedConfigViewModel(
                () => new SharedConfigViewInput(config, document.ApplyTo(config), document, "", null, false, null),
                (_, overlay, _) => { applied.Add(overlay); return Task.CompletedTask; }, new DialogSettingsChangeConfirmation());

            int dialogs = 0;
            bool refreshedWhileOpen = false;
            var handled = new HashSet<ChangeConfirmationWindow>();
            var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
            var deadline = DateTime.UtcNow.AddSeconds(15);
            poll.Tick += (_, _) =>
            {
                foreach (ChangeConfirmationWindow w in System.Windows.Application.Current.Windows.OfType<ChangeConfirmationWindow>().ToArray())
                {
                    if (DateTime.UtcNow > deadline) { w.Close(); continue; }
                    if (!w.IsVisible || !handled.Add(w)) continue;
                    w.Left = WpfTestHost.OffScreenPosition;
                    w.Top = WpfTestHost.OffScreenPosition;
                    dialogs++;
                    if (dialogs == 1)
                    {
                        // Zmiana z innego komputera w trakcie pytania — jak handler ConfigChanged w SharedConfigView.
                        document = Doc(1.9, 1);
                        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                        {
                            vm.RefreshFromOutside(changedElsewhere: true);
                            refreshedWhileOpen = w.IsVisible;
                        }));
                    }
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Click(w.ConfirmButton)));
                }
            };
            poll.Start();
            try
            {
                vm.ResetTimingCommand.Execute(null);
                while (vm.IsBusy || (dialogs < 2 && DateTime.UtcNow < deadline)) WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            }
            finally { poll.Stop(); }

            Assert.True(refreshedWhileOpen, "Odświeżenie z dyspozytora nie wykonało się, gdy okno było otwarte.");
            Assert.Equal(2, dialogs);
            Assert.Equal(1.9, Assert.Single(applied)!.Scale);
            return 0;
        });
    }

    [Fact]
    public void XamlUsesOnlyDynamicResourceColors()
    {
        string xaml = System.IO.File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "Views", "ChangeConfirmationWindow.xaml"));
        Assert.DoesNotMatch(@"(Background|Foreground|BorderBrush)=""(?!\{DynamicResource)", xaml);
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
