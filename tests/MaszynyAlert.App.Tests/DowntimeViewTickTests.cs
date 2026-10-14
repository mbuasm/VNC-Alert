using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.Views;
using MaszynyAlert.Core.History;

namespace MaszynyAlert.App.Tests;

public sealed class DowntimeViewTickTests : IAsyncLifetime
{
    private readonly PanelTestHost _host = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime, nie IAsyncDisposable: xUnit 2.9 nie woła IAsyncDisposable na klasie testów.
    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public void PanelTick_KeepsUserSortOfEpisodesTable()
    {
        _host.DataDirectory();
        var orchestrator = _host.Orchestrator(null);
        // Ostatni dzień roboczy przed dzisiejszym (domyślny zakres widoku: dziś − 6 … dziś; weekend poza raportem).
        var day = DateTime.Today.AddDays(-1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
        for (int i = 0; i < 4; i++)
        {
            var local = day.AddHours(8 + i * 2);
            var start = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            orchestrator.Downtime.Merge(new DowntimeEpisode("aidite1", "Aidite 1", "Servo 6", start, start.AddMinutes(5 + i * 7),
                start.AddMinutes(5 + i * 7), EpisodeId: Guid.NewGuid(), Category: "restart"));
        }
        string result = WpfTestHost.Run(() =>
        {
            var view = new DowntimeView(orchestrator);
            var root = new Grid();
            root.Children.Add(view);
            var window = WpfTestHost.ShowOffScreen(new Window { Content = root, Width = 1220, Height = 850 });
            try
            {
                RealDowntimePanelTests.Settle(window);
                var grid = view.EpisodesGrid;
                Assert.Equal(4, grid.Items.Count);
                // Jak klik w nagłówek kolumny „Minuty”: kierunek na kolumnie i opis sortowania w widoku.
                var minutes = grid.Columns.First(c => c.SortMemberPath == "Minutes");
                minutes.SortDirection = ListSortDirection.Descending;
                grid.Items.SortDescriptions.Add(new SortDescription("Minutes", ListSortDirection.Descending));
                RealDowntimePanelTests.Settle(window);
                Assert.Single(grid.Items.SortDescriptions);
                view.Refresh(); // tik zegara Panelu (StatusWindow, co 1 s) — dane bez zmian
                RealDowntimePanelTests.Settle(window);
                return $"po tiku: sortowanie {grid.Items.SortDescriptions.Count}, kierunek kolumny {minutes.SortDirection?.ToString() ?? "brak"}";
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal("po tiku: sortowanie 1, kierunek kolumny Descending", result);
    }
}
