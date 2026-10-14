using System.Diagnostics;

namespace MaszynyAlert.Core.Tests.TestUtil;

public static class PerformanceLimits
{
    /// <summary>CI po zmiennych <c>GITHUB_ACTIONS</c> / <c>CI</c> (runner GitHuba ustawia obie).</summary>
    public static bool IsCi =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    /// <summary>Limit dla bieżącego środowiska.</summary>
    public static TimeSpan For(TimeSpan local, TimeSpan ci) => IsCi ? ci : local;

    /// <summary>Czas wykonania <paramref name="body"/> (wołaj po rozgrzewce).</summary>
    public static TimeSpan Measure(Action body)
    {
        var watch = Stopwatch.StartNew();
        body();
        watch.Stop();
        return watch.Elapsed;
    }

    /// <summary>Sprawdza czas wobec limitu środowiska; komunikat podaje czas, limit i środowisko.</summary>
    public static void AssertUnder(TimeSpan elapsed, TimeSpan local, TimeSpan ci, string what)
    {
        TimeSpan limit = For(local, ci);
        Assert.True(elapsed < limit,
            $"{what}: {elapsed.TotalMilliseconds:0} ms (limit {limit.TotalMilliseconds:0} ms, {(IsCi ? "CI" : "lokalnie")})");
    }
}
