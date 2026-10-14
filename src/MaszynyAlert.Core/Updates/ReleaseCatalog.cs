using System.Globalization;
using System.Text.RegularExpressions;

namespace MaszynyAlert.Core.Updates;

public sealed record ReleaseEntry(string FileName, Version Version, long SizeBytes, string Sha256, DateTimeOffset? PublishedAt);

public static partial class ReleaseCatalog
{
    private static readonly Version Zero = new(0, 0, 0, 0);

    public static bool TryParseFileVersion(string fileName, out Version version)
    {
        version = Zero;
        if (string.IsNullOrEmpty(fileName)) return false;
        var match = SetupFileRegex().Match(fileName);
        if (!match.Success) return false;
        return TryBuildVersion(match.Groups["v"].Value, out version);
    }

    public static IReadOnlyList<ReleaseEntry> ParseIndex(string markdown)
    {
        var result = new List<ReleaseEntry>();
        if (string.IsNullOrEmpty(markdown)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in markdown.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('|')) continue;
            string[] cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3) continue;

            string name = cells[0];
            var link = MarkdownLinkRegex().Match(name);
            if (link.Success) name = link.Groups["text"].Value.Trim();
            if (!TryParseFileVersion(name, out var version)) continue;

            long? size = null;
            string? sha = null;
            DateTimeOffset? publishedAt = null;
            foreach (string cell in cells.Skip(1))
            {
                if (size is null && SizeRegex().Match(cell) is { Success: true } sizeMatch
                    && long.TryParse(sizeMatch.Groups["b"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes))
                {
                    size = bytes;
                }
                else if (sha is null && ShaRegex().Match(cell) is { Success: true } shaMatch)
                {
                    sha = shaMatch.Groups["sha"].Value.ToLowerInvariant();
                }
                else if (publishedAt is null && cell.Length >= 10 && char.IsAsciiDigit(cell[0])
                    && DateTimeOffset.TryParse(cell, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    publishedAt = date;
                }
            }

            if (size is not > 0 || sha is null) continue;
            if (!seen.Add(name)) continue;
            result.Add(new ReleaseEntry(name, version, size.Value, sha, publishedAt));
        }

        return result;
    }

    /// <summary>
    /// Wydanie o najwyższej wersji (NIE ostatni wiersz pliku — patrz opis klasy) albo
    /// <see langword="null"/>, gdy spis jest pusty. Przy dwóch plikach tej samej wersji wygrywa pierwszy.
    /// </summary>
    public static ReleaseEntry? Latest(IEnumerable<ReleaseEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ReleaseEntry? best = null;
        foreach (var entry in entries)
        {
            if (best is null || entry.Version > best.Version) best = entry;
        }
        return best;
    }

    /// <summary>
    /// Wersja zainstalowanego programu z wersji informacyjnej (<c>BuildIdentity.Version</c>):
    /// <c>0.4.26</c>, <c>0.4.26+sha</c>, <c>0.3.0-dev</c> → <c>0.4.26.0</c> / <c>0.3.0.0</c>.
    /// Wynik jest czteroczęściowy, bo <see cref="Version"/> traktuje brakującą część jako MNIEJSZĄ od zera —
    /// bez wyrównania 0.4.22 byłaby starsza od 0.4.22.0 z nazwy pliku i stanowisko instalowałoby tę samą
    /// wersję w kółko. Wartość nieczytelna daje <c>0.0.0.0</c> (każde wydanie jest wtedy nowsze — świadomie:
    /// program bez numeru wersji to build lokalny, a ten i tak nie jest instalowany, patrz
    /// <see cref="Config.UpdatesConfig"/>).
    /// </summary>
    public static Version ParseInstalledVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return Zero;
        string value = informationalVersion.Trim();
        int cut = value.IndexOfAny(['-', '+']);
        if (cut >= 0) value = value[..cut];
        return TryBuildVersion(value, out var version) ? version : Zero;
    }

    /// <summary>Składa czteroczęściową wersję z 1–4 liczb rozdzielonych kropkami (bez znaków, bez przepełnienia).</summary>
    internal static bool TryBuildVersion(string text, out Version version)
    {
        version = Zero;
        string[] parts = text.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    [GeneratedRegex(@"^(?:MaszynyAlert|VncAlert|VNC-Alert)-Setup-(?<v>[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)\.exe\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetupFileRegex();

    [GeneratedRegex(@"^\[(?<text>[^\]]*)\]\([^)]*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"\((?<b>[0-9]+)\s*B\)", RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^`?(?<sha>[0-9A-Fa-f]{64})`?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShaRegex();
}
