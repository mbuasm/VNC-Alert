namespace MaszynyAlert.Core.Ocr;

/// <summary>Odległość edycyjna Levenshteina i dopasowanie rozmyte ("zawiera w przybliżeniu") na jej podstawie.</summary>
public static class Levenshtein
{
    /// <summary>
    /// Klasyczna odległość edycyjna Levenshteina (wstawienie/usunięcie/zamiana znaku = koszt 1).
    /// Implementacja O(n*m) w czasie, O(min(n,m)) w pamięci (dwa wiersze zamiast pełnej macierzy).
    /// </summary>
    public static int Distance(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;

        // Krótszy ciąg jako "b", żeby wiersze robocze były jak najmniejsze.
        if (a.Length < b.Length)
        {
            (a, b) = (b, a);
        }

        int n = a.Length;
        int m = b.Length;

        if (m == 0)
        {
            return n;
        }

        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (int j = 0; j <= m; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            current[0] = i;
            char ca = a[i - 1];
            for (int j = 1; j <= m; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                int deletion = previous[j] + 1;
                int insertion = current[j - 1] + 1;
                int substitution = previous[j - 1] + cost;
                current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[m];
    }

    /// <summary>
    /// Sprawdza, czy w <paramref name="haystack"/> istnieje podciąg "podobny" do <paramref name="needle"/>
    /// (odległość Levenshteina &lt;= <paramref name="maxDistance"/>). Sprawdzane są okna o długości od
    /// <c>len(needle) - maxDistance</c> (minimum 1) do <c>len(needle) + maxDistance</c>. Szybka ścieżka:
    /// gdy <paramref name="haystack"/> zawiera <paramref name="needle"/> dosłownie, zwraca od razu
    /// <see langword="true"/>. Puste <paramref name="needle"/> → zawsze <see langword="false"/>.
    /// </summary>
    public static bool FuzzyContains(string haystack, string needle, int maxDistance)
    {
        haystack ??= string.Empty;
        needle ??= string.Empty;

        if (needle.Length == 0)
        {
            return false;
        }

        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            return true;
        }

        if (maxDistance < 0)
        {
            return false;
        }

        int needleLen = needle.Length;
        int minLen = Math.Max(1, needleLen - maxDistance);
        int maxLen = needleLen + maxDistance;

        for (int len = minLen; len <= maxLen; len++)
        {
            if (len > haystack.Length)
            {
                continue;
            }

            for (int start = 0; start + len <= haystack.Length; start++)
            {
                string window = haystack.Substring(start, len);
                if (Distance(window, needle) <= maxDistance)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
