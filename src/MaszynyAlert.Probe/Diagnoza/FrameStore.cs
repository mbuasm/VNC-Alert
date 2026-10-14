using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Probe.Diagnoza;

public sealed class FrameStore(string directory, int maxFrames = FrameStore.DefaultMaxFrames)
{
    public const int DefaultMaxFrames = 150;
    public const int KeepEvery = 5;

    /// <summary>Pierwsze klatki (indeksy poniżej tej liczby) nigdy nie są przerzedzane — to test „ekran gaśnie po połączeniu”.</summary>
    public const int ProtectedHead = 10;

    private readonly List<(int Index, string Path)> _saved = [];
    private int _nextIndex;

    /// <summary>Katalog klatek.</summary>
    public string Directory { get; } = directory;

    /// <summary>Ile klatek zapisano łącznie (razem z usuniętymi przy przerzedzaniu).</summary>
    public int SavedTotal => _nextIndex;

    /// <summary>Ścieżki klatek, które są na dysku teraz.</summary>
    public IReadOnlyList<string> Files => _saved.Select(s => s.Path).ToList();

    /// <summary>Zapisuje klatkę; nazwa pliku to stempel czasu + numer (żeby dwie klatki w tej samej sekundzie się nie nadpisały).</summary>
    public string Save(RgbaImage frame, DateTimeOffset now, string? suffix = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        System.IO.Directory.CreateDirectory(Directory);
        int index = _nextIndex++;
        string name = $"{now:yyyyMMdd-HHmmss}-{index:0000}{(suffix is null ? string.Empty : "-" + suffix)}.png";
        string path = Path.Combine(Directory, name);
        PngCodec.EncodeToFile(frame, path);
        _saved.Add((index, path));
        Thin();
        return path;
    }

    /// <summary>Tyle najnowszych klatek nigdy nie jest przerzedzanych — koniec przebiegu ma być w komplecie.</summary>
    public const int ProtectedTail = 10;

    private void Thin()
    {
        // Dwa przejścia od najstarszych, oba omijają chronioną głowę i ogon: najpierw klatki spoza „co piątej”, a gdy to
        // nie wystarczy — także co piąte. Limit jest twardy (z dokładnością do głowy i ogona), żeby --co-sekund 2 przez
        // godzinę nie zapełniło dysku.
        foreach (bool deleteKept in new[] { false, true })
        {
            for (int i = 0; i < _saved.Count - ProtectedTail && _saved.Count > maxFrames; i++)
            {
                (int index, string path) = _saved[i];
                if (index < ProtectedHead || (!deleteKept && index % KeepEvery == 0))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Plik zajęty — spróbujemy przy następnym zapisie.
                    continue;
                }

                _saved.RemoveAt(i);
                i--;
            }
        }
    }
}
