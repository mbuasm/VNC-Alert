using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.Tests;

public sealed class CapturesPanelViewModelTests
{
    private static readonly DateTimeOffset Teraz = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PokazujeLiczbePlikowZajeteMiejsceLimitIWiekNajstarszego()
    {
        var stats = new CaptureFolderStats(1234, 180L * 1024 * 1024, new DateTimeOffset(2026, 9, 12, 8, 30, 0, TimeSpan.Zero));

        string text = CapturesPanelViewModel.Describe(stats, limitMegabytes: 200);

        Assert.Contains("1234", text, StringComparison.Ordinal);
        Assert.Contains("180 MB", text, StringComparison.Ordinal);
        Assert.Contains("limit 200 MB", text, StringComparison.Ordinal);
        Assert.Contains("najstarszy 12.09", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PustyKatalogNieUdajeZerowegoRozmiaruPliku()
    {
        string text = CapturesPanelViewModel.Describe(CaptureFolderStats.Empty, limitMegabytes: 200);

        Assert.Contains("brak plików", text, StringComparison.Ordinal);
        Assert.Contains("limit 200 MB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WyrozniaKatalogPoOsiagnieciuLimitu()
    {
        var atLimit = new CaptureFolderStats(90, 200L * 1024 * 1024, Teraz.AddDays(-1));
        var belowLimit = new CaptureFolderStats(90, 100L * 1024 * 1024, Teraz.AddDays(-1));

        Assert.True(new CapturesPanelViewModel(() => atLimit, () => 200, _ => Task.CompletedTask).IsAtLimit);
        Assert.False(new CapturesPanelViewModel(() => belowLimit, () => 200, _ => Task.CompletedTask).IsAtLimit);
    }

    [Fact]
    public async Task PrzyciskUsuwaZrzutyStarszeNizDzienIOdswiezaLicznik()
    {
        var stats = new CaptureFolderStats(1234, 180L * 1024 * 1024, Teraz.AddDays(-3));
        TimeSpan? uzyteOkno = null;
        var viewModel = new CapturesPanelViewModel(
            () => stats,
            () => 200,
            retention =>
            {
                uzyteOkno = retention;
                stats = new CaptureFolderStats(30, 5L * 1024 * 1024, Teraz.AddHours(-2));
                return Task.CompletedTask;
            });

        await viewModel.DeleteOlderThanDayCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromDays(1), uzyteOkno);
        Assert.Contains("Usunięto 1204", viewModel.ActionText, StringComparison.Ordinal);
        Assert.Contains("30 plików", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.Contains("5 MB", viewModel.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GdyNieBylaCzegoUsunac_MowiToWprostIZostawiaLicznikBezZmian()
    {
        var stats = new CaptureFolderStats(12, 3L * 1024 * 1024, Teraz.AddHours(-2));
        var viewModel = new CapturesPanelViewModel(() => stats, () => 200, _ => Task.CompletedTask);

        await viewModel.DeleteOlderThanDayCommand.ExecuteAsync(null);

        Assert.Equal("Nie było zrzutów starszych niż dzień.", viewModel.ActionText);
        Assert.Contains("12 plików", viewModel.SummaryText, StringComparison.Ordinal);
    }
}
