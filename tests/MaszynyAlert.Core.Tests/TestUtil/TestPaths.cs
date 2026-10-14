using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Ścieżki do plików kopiowanych do katalogu wyjściowego testów (config/*.json, samples/**)
/// oraz do ŹRÓDŁOWEGO katalogu repozytorium (potrzebne generatorowi próbek PNG).
/// </summary>
public static class TestPaths
{
    /// <summary>Katalog, z którego uruchomione są testy (bin/&lt;konfiguracja&gt;/net10.0).</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>Ścieżka do skopiowanego <c>config/config.example.json</c>.</summary>
    public static string ConfigExample => Path.Combine(BaseDirectory, "config", "config.example.json");

    /// <summary>Ścieżka do skopiowanego <c>config/rules.example.json</c>.</summary>
    public static string RulesExample => Path.Combine(BaseDirectory, "config", "rules.example.json");

    /// <summary>Katalog ze skopiowanymi próbkami PNG (<c>tests/samples</c> w katalogu wyjściowym).</summary>
    public static string SamplesOutput => Path.Combine(BaseDirectory, "samples");

    /// <summary>Katalog główny repozytorium — szukany w górę od katalogu testów po pliku <c>src/MaszynyAlert.sln</c>.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Źródłowy katalog próbek w repozytorium (<c>&lt;repo&gt;/tests/samples</c>).</summary>
    public static string SamplesSource => Path.Combine(RepositoryRoot, "tests", "samples");

    /// <summary>Wczytana i sprawdzona przykładowa konfiguracja (współdzielona przez testy — jest tylko do odczytu).</summary>
    public static AppConfig ExampleConfig { get; } = ConfigLoader.LoadAppConfig(ConfigExample);

    /// <summary>Wczytany przykładowy słownik reguł.</summary>
    public static RuleSet ExampleRules { get; } = ConfigLoader.LoadRules(RulesExample);

    public const string AiditeProfile = "opencnc";

    public static RuleDatabase ExampleAidite { get; } = ExampleRules.Databases[AiditeProfile];

    /// <summary>Zwraca profil o podanym identyfikatorze z przykładowej konfiguracji.</summary>
    public static ScreenProfile Profile(string id) => ExampleConfig.Profiles[id];

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "MaszynyAlert.sln")))
            {
                return dir.FullName;
            }

            if (File.Exists(Path.Combine(dir.FullName, "MaszynyAlert.sln")) && dir.Parent is not null)
            {
                // Plik solution leży w katalogu src/ — katalogiem repozytorium jest jego rodzic.
                return dir.Parent.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Nie znaleziono katalogu repozytorium (pliku src/MaszynyAlert.sln) idąc w górę od '{BaseDirectory}'.");
    }
}
