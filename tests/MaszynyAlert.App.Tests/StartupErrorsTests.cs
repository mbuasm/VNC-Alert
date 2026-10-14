using System.IO;
using System.Text;
using MaszynyAlert.App.Services;

namespace MaszynyAlert.App.Tests;

public sealed class StartupErrorsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "startup-errors-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AppendLog_DopisujeDoDziennegoPlikuLoguZPrzyczynaIWyjatkiem()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.FromHours(2));

        string? path = StartupErrors.AppendLog(_dir, now, "Błąd konfiguracji — program nie wystartował.",
            new ArgumentNullException("overlay"));
        StartupErrors.AppendLog(_dir, now, "Drugi wpis.", null);

        Assert.Equal(Path.Combine(_dir, "app-20261004.log"), path);
        string text = File.ReadAllText(path!, Encoding.UTF8);
        Assert.StartsWith("2026-10-04 12:30:00.000 +02:00 [ERR] MaszynyAlert.App.Program: Błąd konfiguracji", text, StringComparison.Ordinal);
        Assert.Contains("System.ArgumentNullException", text, StringComparison.Ordinal);
        Assert.Contains("Drugi wpis.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendLog_GdyLoguNieDaSieZapisac_NieRzucaIZwracaNull()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "plik-zamiast-katalogu");
        File.WriteAllText(blocker, "x");

        Assert.Null(StartupErrors.AppendLog(blocker, DateTimeOffset.Now, "x", null));
    }

    [Fact]
    public void MissingDefaultsMessage_PodajePlikiIDrogeNaprawy()
    {
        string text = StartupErrors.MissingDefaultsMessage(@"C:\ProgramData\MaszynyAlert\config.json",
            @"C:\ProgramData\MaszynyAlert\rules.json", new UnauthorizedAccessException("Access denied"));

        Assert.Contains("config.json", text, StringComparison.Ordinal);
        Assert.Contains("rules.json", text, StringComparison.Ordinal);
        Assert.Contains("instalator", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowText_ZawieraPrzyczyneCoZrobicIMiejsceLogu()
    {
        string text = StartupErrors.WindowText("Pole 'ui.overlay' nie może mieć wartości null.", @"C:\logs\app-20261004.log");

        Assert.StartsWith("Pole 'ui.overlay'", text, StringComparison.Ordinal);
        Assert.Contains("uruchom program ponownie", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\logs\app-20261004.log", text, StringComparison.Ordinal);
    }
}
