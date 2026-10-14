using MaszynyAlert.Windows;

namespace MaszynyAlert.Windows.Tests;

public sealed class WatchdogTaskTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(),
        "MaszynyAlert.WatchdogTests." + Guid.NewGuid().ToString("N"));

    // Testy nie mogą dotknąć prawdziwej nazwy Global\MaszynyAlert.App: na stanowisku deweloperskim
    // mógłby akurat działać program.
    private static string UniqueMutex() => @"Local\MaszynyAlert.WatchdogTest." + Guid.NewGuid().ToString("N");

    public WatchdogTaskTests() => Directory.CreateDirectory(_dataDir);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WithoutMarkerTheWatchdogMayStartTheApplication()
    {
        Assert.False(WatchdogTask.IsSuppressed(_dataDir, DateTimeOffset.Now));
        Assert.True(WatchdogTask.ShouldStart(_dataDir, DateTimeOffset.Now, UniqueMutex()));
    }

    [Fact]
    public void StopCommandSilencesTheWatchdogForItsWindowAndNotLonger()
    {
        var stopped = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.FromHours(2));
        WatchdogTask.Suppress(_dataDir, stopped);

        Assert.True(File.Exists(WatchdogTask.MarkerPath(_dataDir)));
        Assert.True(WatchdogTask.IsSuppressed(_dataDir, stopped));
        Assert.True(WatchdogTask.IsSuppressed(_dataDir, stopped + WatchdogTask.SuppressionWindow - TimeSpan.FromSeconds(1)));
        Assert.False(WatchdogTask.IsSuppressed(_dataDir, stopped + WatchdogTask.SuppressionWindow));
        Assert.False(WatchdogTask.ShouldStart(_dataDir, stopped, UniqueMutex()));
        Assert.True(WatchdogTask.ShouldStart(_dataDir, stopped + WatchdogTask.SuppressionWindow, UniqueMutex()));
    }

    [Fact]
    public void MarkerFromAnotherTimeZoneIsReadAsTheSameMoment()
    {
        var stopped = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.FromHours(2));
        WatchdogTask.Suppress(_dataDir, stopped);

        // Ten sam moment zapisany w UTC: znacznik ma zadziałać dokładnie tak samo.
        Assert.True(WatchdogTask.IsSuppressed(_dataDir, stopped.ToUniversalTime() + TimeSpan.FromMinutes(1)));
        Assert.False(WatchdogTask.IsSuppressed(_dataDir, stopped.ToUniversalTime() + TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void DamagedMarkerDoesNotSwitchTheProtectionOffForGood()
    {
        string path = WatchdogTask.MarkerPath(_dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "nie-data");

        Assert.False(WatchdogTask.IsSuppressed(_dataDir, DateTimeOffset.Now));
        Assert.True(WatchdogTask.ShouldStart(_dataDir, DateTimeOffset.Now, UniqueMutex()));
    }

    [Fact]
    public void StartingTheApplicationByHandClearsTheMarker()
    {
        WatchdogTask.Suppress(_dataDir, DateTimeOffset.Now);
        WatchdogTask.ClearSuppression(_dataDir);

        Assert.False(File.Exists(WatchdogTask.MarkerPath(_dataDir)));
        Assert.False(WatchdogTask.IsSuppressed(_dataDir, DateTimeOffset.Now));

        // Powtórzone kasowanie nie może rzucić wyjątkiem: każdy zwykły start woła je bezwarunkowo.
        WatchdogTask.ClearSuppression(_dataDir);
    }

    [Fact]
    public void WatchdogDoesNotStartASecondCopyOfARunningApplication()
    {
        string mutexName = UniqueMutex();
        using var running = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        Assert.True(createdNew);

        Assert.False(WatchdogTask.ShouldStart(_dataDir, DateTimeOffset.Now, mutexName));
    }

    [Fact]
    public void MissingDataDirectoryNeitherThrowsNorSilencesTheWatchdog()
    {
        string missing = Path.Combine(_dataDir, "nie-ma-takiego-katalogu");

        Assert.False(WatchdogTask.IsSuppressed(missing, DateTimeOffset.Now));
        WatchdogTask.ClearSuppression(missing);

        // Zapis sam tworzy brakujący katalog: komenda zatrzymania działa też przed pierwszym startem.
        WatchdogTask.Suppress(missing, DateTimeOffset.Now);
        Assert.True(WatchdogTask.IsSuppressed(missing, DateTimeOffset.Now));
    }

    [Fact]
    public void StopIssuedBySystemWritesOnlyToTheAdministratorsDirectory()
    {
        var stopped = DateTimeOffset.Now;
        WatchdogTask.Suppress(_dataDir, stopped, asSystem: true);

        Assert.False(File.Exists(WatchdogTask.MarkerPath(_dataDir)));
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "state")));
        Assert.Equal(Path.Combine(_dataDir, "updates-admin", "stop.requested"), WatchdogTask.SystemMarkerPath(_dataDir));
        Assert.True(File.Exists(WatchdogTask.SystemMarkerPath(_dataDir)));
        Assert.True(WatchdogTask.IsSuppressed(_dataDir, stopped));
        Assert.False(WatchdogTask.ShouldStart(_dataDir, stopped, UniqueMutex()));
        Assert.False(WatchdogTask.IsSuppressed(_dataDir, stopped + WatchdogTask.SuppressionWindow));
    }

    [Fact]
    public void OperatorStartClearsOnlyTheOperatorMarker()
    {
        // ClearSuppression (zwykły start operatora) kasuje tylko znacznik w `state`; znacznik SYSTEM zdejmuje zadanie.
        var now = DateTimeOffset.Now;
        WatchdogTask.Suppress(_dataDir, now);
        WatchdogTask.Suppress(_dataDir, now, asSystem: true);

        WatchdogTask.ClearSuppression(_dataDir);

        Assert.False(File.Exists(WatchdogTask.MarkerPath(_dataDir)));
        Assert.True(File.Exists(WatchdogTask.SystemMarkerPath(_dataDir)));
    }
}
