using System.Text.RegularExpressions;

namespace MaszynyAlert.Core.Diagnostics;

/// <summary>Tożsamość wersji widoczna w Statusie i paczce diagnostycznej.</summary>
public sealed partial record BuildIdentity(string Version, string? Commit)
{
    public string DisplayText => Commit is null
        ? $"Wersja {Version} (commit nieznany)"
        : $"Wersja {Version} (commit {Commit[..Math.Min(12, Commit.Length)]})";

    /// <summary>
    /// Odczytuje pełny SHA wyłącznie z metadanych wersji informacyjnej, np.
    /// <c>0.1.0+0123456789abcdef...</c>. Skrócone i niejednoznaczne identyfikatory
    /// nie są przedstawiane jako commit.
    /// </summary>
    public static BuildIdentity FromInformationalVersion(string? value)
    {
        string version = string.IsNullOrWhiteSpace(value) ? "unknown/dev" : value.Trim();
        int metadataStart = version.IndexOf('+');
        if (metadataStart < 0 || metadataStart == version.Length - 1)
        {
            return new BuildIdentity(version, null);
        }

        MatchCollection matches = FullShaRegex().Matches(version[(metadataStart + 1)..]);
        string[] distinct = matches
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new BuildIdentity(version, distinct.Length == 1 ? distinct[0] : null);
    }

    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{64}|[0-9A-Fa-f]{40})(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex FullShaRegex();
}
