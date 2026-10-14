using System.Collections.ObjectModel;

namespace MaszynyAlert.App.ViewModels;

internal static class CollectionSync
{
    /// <summary>
    /// Ustawia <paramref name="target"/> na pozycje z <paramref name="source"/> w ich kolejności, dopasowując po kluczu:
    /// pozycja o znanym kluczu zostaje (albo zostaje podmieniona, gdy <paramref name="merge"/> zwróci inny obiekt),
    /// nowe są wstawiane, zniknięte usuwane. Bez żadnej zmiany kolekcja nie dostaje ani jednego zdarzenia.
    /// </summary>
    /// <param name="merge">
    /// Dla pozycji o tym samym kluczu: zwraca obiekt, który ma zostać w kolekcji — istniejący (ewentualnie
    /// zaktualizowany w miejscu) albo świeży, gdy treść się zmieniła i nie da się jej zaktualizować w miejscu.
    /// </param>
    public static void SyncByKey<T, TKey>(ObservableCollection<T> target, IEnumerable<T> source, Func<T, TKey> key, Func<T, T, T> merge)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        T[] fresh = source.ToArray();
        var comparer = EqualityComparer<TKey>.Default;
        var wanted = new HashSet<TKey>(fresh.Select(key), comparer);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(key(target[i]))) target.RemoveAt(i);
        }

        for (int i = 0; i < fresh.Length; i++)
        {
            TKey wantedKey = key(fresh[i]);
            int found = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (comparer.Equals(key(target[j]), wantedKey)) { found = j; break; }
            }

            if (found < 0)
            {
                target.Insert(i, fresh[i]);
                continue;
            }

            if (found != i) target.Move(found, i);
            T kept = merge(target[i], fresh[i]);
            if (!ReferenceEquals(kept, target[i])) target[i] = kept;
        }

        while (target.Count > fresh.Length) target.RemoveAt(target.Count - 1);
    }

    /// <summary>
    /// Linie tekstu (log, historia): gdy lista przesunęła się o kilka wpisów — dopisuje nowe na końcu i zdejmuje
    /// najstarsze z początku (log), albo wstawia nowe na początku i zdejmuje najstarsze z końca (historia, najnowsze
    /// na górze); pozostałe linie zostają tymi samymi wierszami listy. Inaczej podmienia tylko różniące się pozycje.
    /// </summary>
    public static void SyncLines(ObservableCollection<string> target, IEnumerable<string> source)
    {
        ArgumentNullException.ThrowIfNull(target);
        string[] fresh = source.ToArray();
        if (target.SequenceEqual(fresh, StringComparer.Ordinal)) return;

        // Dopisane na końcu: koniec obecnej listy = początek nowej.
        for (int drop = 0; drop < target.Count; drop++)
        {
            int kept = target.Count - drop;
            if (kept > fresh.Length || !Matches(target, drop, fresh, 0, kept)) continue;
            for (int i = 0; i < drop; i++) target.RemoveAt(0);
            for (int i = kept; i < fresh.Length; i++) target.Add(fresh[i]);
            return;
        }

        // Wstawione na początku: początek obecnej listy = koniec nowej (po zdjęciu najstarszych z końca).
        for (int added = 1; added < fresh.Length; added++)
        {
            int kept = fresh.Length - added;
            if (kept > target.Count || !Matches(target, 0, fresh, added, kept)) continue;
            while (target.Count > kept) target.RemoveAt(target.Count - 1);
            for (int i = 0; i < added; i++) target.Insert(i, fresh[i]);
            return;
        }

        for (int i = 0; i < fresh.Length; i++)
        {
            if (i >= target.Count) target.Add(fresh[i]);
            else if (!string.Equals(target[i], fresh[i], StringComparison.Ordinal)) target[i] = fresh[i];
        }

        while (target.Count > fresh.Length) target.RemoveAt(target.Count - 1);
    }

    private static bool Matches(IList<string> a, int aStart, string[] b, int bStart, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(a[aStart + i], b[bStart + i], StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
