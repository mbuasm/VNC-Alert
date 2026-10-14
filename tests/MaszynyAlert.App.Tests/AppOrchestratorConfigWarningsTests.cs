using System.IO;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App.Tests;

public sealed class AppOrchestratorConfigWarningsTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, string? Format)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string? format = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(p => p.Key == "{OriginalFormat}").Value as string;
            Entries.Add((logLevel, formatter(state, exception), format));
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }

    private static AppConfig ExampleConfig() => ConfigLoader.LoadAppConfig(Path.Combine(RepositoryRoot(), "config", "config.example.json"));

    private static RuleSet ExampleRules() => ConfigLoader.LoadRules(Path.Combine(RepositoryRoot(), "config", "rules.example.json"));

    [Fact]
    public void LogConfigWarnings_TeSameOstrzezeniaCoStartProgramu_KazdeOsobnymWpisemWarning()
    {
        AppConfig przyklad = ExampleConfig();
        AppConfig cfg = przyklad with
        {
            Profiles = przyklad.Profiles.Where(p => p.Key is not ("cameo1" or "premile"))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Machines = [.. przyklad.Machines.Select(m => m.Id is "cameo1" or "premile" ? m with { Profile = "aidite-light2" } : m)],
            Cluster = przyklad.Cluster with { Enabled = false, UdpPort = 0 },
        };
        RuleSet rules = ExampleRules();
        IReadOnlyList<string> przyStarcie = [.. ConfigLoader.Warnings(cfg), .. ConfigLoader.Warnings(rules, cfg)]; // jak Program.RunInstance
        var logger = new RecordingLogger();

        IReadOnlyList<string> zalogowane = AppOrchestrator.LogConfigWarnings(logger, cfg, rules);

        Assert.Equal(3, przyStarcie.Count);
        Assert.StartsWith("Sekcja 'cluster' jest wyłączona", przyStarcie[0], StringComparison.Ordinal);
        Assert.StartsWith("Baza błędów 'cameo1' ", przyStarcie[1], StringComparison.Ordinal);
        Assert.StartsWith("Baza błędów 'premile' ", przyStarcie[2], StringComparison.Ordinal);
        Assert.Equal(przyStarcie, zalogowane);
        Assert.Equal(przyStarcie, logger.Entries.Select(e => e.Message));
        Assert.All(logger.Entries, e =>
        {
            Assert.Equal(LogLevel.Warning, e.Level);
            Assert.Equal("{Warning}", e.Format);
        });
    }

    /// <summary>Pliki wzorcowe (każda baza ma profil, grupa wyłączona z poprawnymi wartościami) — nic do logu.</summary>
    [Fact]
    public void LogConfigWarnings_PlikiWzorcowe_NicNieLoguje()
    {
        var logger = new RecordingLogger();

        Assert.Empty(AppOrchestrator.LogConfigWarnings(logger, ExampleConfig(), ExampleRules()));
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Okablowanie w <see cref="AppOrchestrator.ApplyGroupSettingsAsync"/>: ostrzeżenia dla konfiguracji z nałożonym profilem nowej
    /// generacji (<c>nextBase</c>) i jej słownika (<c>data.Profile.Rules</c>) idą do logu PO bloku zastosowania — za
    /// <c>ApplyPreparedAsync</c> (wraca bez wyjątku tylko, gdy generacja działa; po wycofaniu rzuca) i za sprzątaniem pomocnika, na tym
    /// samym poziomie zagnieżdżenia co przygotowanie przez pomocnika (nie w <c>try</c>/<c>catch</c>/<c>finally</c> zastosowania),
    /// przed zwolnieniem bramki.
    /// </summary>
    [Fact]
    public void ApplyGroupSettingsAsync_PoUdanymZastosowaniuNowejGeneracji_LogujeOstrzezeniaJakPrzyStarcie()
    {
        const string ReleaseGate = "finally { _applyGate.Release(); }";
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MaszynyAlert.App", "Services", "AppOrchestrator.cs"));
        int start = source.IndexOf("public async Task ApplyGroupSettingsAsync(", StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf(ReleaseGate, start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Nie znaleziono metody ApplyGroupSettingsAsync w AppOrchestrator.cs.");
        string method = source[start..(end + ReleaseGate.Length)];

        int prepare = method.IndexOf("var writer = await GroupSettingsClient.PrepareAsync(", StringComparison.Ordinal);
        int apply = method.IndexOf("await ApplyPreparedAsync(", StringComparison.Ordinal);
        int dispose = method.IndexOf("await writer.DisposeAsync()", StringComparison.Ordinal);
        int log = method.IndexOf("LogConfigWarnings(_logger, nextBase, data.Profile.Rules);", StringComparison.Ordinal);
        int release = method.IndexOf(ReleaseGate, StringComparison.Ordinal);

        Assert.True(prepare > 0 && apply > prepare && dispose > apply && release > dispose,
            "Zmienił się kształt ApplyGroupSettingsAsync — sprawdź, gdzie trafiają ostrzeżenia nowej generacji.");
        Assert.True(log > dispose && log < release,
            "Ostrzeżenia nowej generacji mają trafiać do logu po bloku zastosowania (za ApplyPreparedAsync i sprzątaniem pomocnika).");
        Assert.Equal(Depth(method, prepare), Depth(method, log));
        Assert.Equal(Depth(method, log) + 1, Depth(method, apply));
        Assert.Equal(1, CountOf(method, "LogConfigWarnings("));
    }

    /// <summary>Poziom zagnieżdżenia klamr w <paramref name="text"/> przed pozycją <paramref name="position"/>.</summary>
    private static int Depth(string text, int position) =>
        text[..position].Count(c => c == '{') - text[..position].Count(c => c == '}');

    private static int CountOf(string text, string fragment)
    {
        int count = 0;
        for (int i = text.IndexOf(fragment, StringComparison.Ordinal); i >= 0; i = text.IndexOf(fragment, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
