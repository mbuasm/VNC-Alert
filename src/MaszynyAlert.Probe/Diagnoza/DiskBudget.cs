using System.Globalization;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Wynik kontroli dysku dla jednej maszyny: rozmiar katalogu, ile klatek usunięto przy przerzedzaniu, czy limit przekroczony.</summary>
public sealed record DiskBudgetResult(long Bytes, int Deleted, bool OverLimit);

/// <summary>
/// Limit dysku kolektora (tryb długi): katalog maszyny ma dni <c>yyyy-MM-dd/</c>, a w nich <c>okresowe/</c>, <c>zmiany/</c>,
/// <c>epizody/</c>, <c>ocr/</c>. Klatki okresowe i zmiany z dni starszych niż <see cref="DlugiOptions.ThinAfterDays"/> są
/// przerzedzane do JEDNEJ na godzinę (zostaje najstarsza w każdej godzinie); <c>epizody/</c> i <c>epizody.jsonl</c> nigdy
/// nie są kasowane. Kolektor sprawdza rozmiar każdego zapisu, a przy braku miejsca kończy zbieranie także epizodów,
/// OCR i dziennika; rezerwa pozwala domknąć otwarte epizody. Czysta logika na katalogu — testowana na małym limicie.
/// </summary>
public static class DiskBudget
{
    /// <summary>Podkatalogi dnia, które wolno przerzedzać.</summary>
    public static readonly IReadOnlyList<string> ThinnableDirs = ["okresowe", "zmiany"];

    public static DiskBudgetResult Enforce(string machineDir, DateTimeOffset now, long limitBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineDir);

        int deleted = 0;
        if (Directory.Exists(machineDir))
        {
            foreach (string dayDir in Directory.GetDirectories(machineDir))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
                {
                    continue;
                }

                if ((now.Date - day).TotalDays < DlugiOptions.ThinAfterDays)
                {
                    continue;
                }

                foreach (string sub in ThinnableDirs)
                {
                    deleted += ThinToHourly(Path.Combine(dayDir, sub));
                }
            }
        }

        long bytes = Size(machineDir);
        return new DiskBudgetResult(bytes, deleted, bytes > limitBytes);
    }

    /// <summary>Zostawia w katalogu najstarszy plik z każdej godziny (po stemplu <c>yyyyMMdd-HHmmss</c> na początku nazwy); zwraca liczbę usuniętych.</summary>
    public static int ThinToHourly(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        int deleted = 0;
        foreach (string file in Directory.GetFiles(dir, "*.png").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            string hour = name.Length >= 11 ? name[..11] : name; // yyyyMMdd-HH
            if (keep.Add(hour))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                // zajęty — następnym razem
            }
        }

        return deleted;
    }

    /// <summary>Rozmiar katalogu w bajtach (rekurencyjnie); 0, gdy nie istnieje.</summary>
    public static long Size(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // plik w trakcie zapisu — pomiń
            }
        }

        return total;
    }
}
