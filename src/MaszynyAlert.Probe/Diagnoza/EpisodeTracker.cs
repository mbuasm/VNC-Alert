using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Probe.Diagnoza;

public enum EpisodeColor
{
    Czerwien,
    Zolc,
}

/// <summary>Zamknięty epizod koloru na ekranie maszyny — wiersz <c>epizody.jsonl</c>.</summary>
public sealed record Episode(
    EpisodeColor Kolor,
    DateTimeOffset Poczatek,
    DateTimeOffset Koniec,
    double MaksUdzial,
    RectInt? Bbox,
    int Klatek);

/// <summary>Zdarzenie z <see cref="EpisodeTracker.Update"/>: początek, klatka do zapisu (offset w s), koniec (z gotowym epizodem).</summary>
public sealed record EpisodeEvent(EpisodeColor Kolor, string Rodzaj, int? OffsetSek, Episode? Epizod)
{
    public const string Start = "start";
    public const string SaveFrame = "klatka";
    public const string End = "koniec";
}

/// <summary>Udział i prostokąt pikseli w zakresie HSV — jeden pomiar koloru na klatce.</summary>
public sealed record ColorRegion(double Udzial, RectInt? Bbox);

public sealed class EpisodeTracker
{
    /// <summary>Udział koloru w całej klatce, od którego zaczyna się epizod (0,05 % — jeden wiersz tekstu na 1024×768 to ok. 0,3 %).</summary>
    public const double StartFraction = 0.0005;

    /// <summary>Ile czasu bez koloru kończy epizod (mignięcia i odświeżanie ekranu nie mają go dzielić).</summary>
    public static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(10);

    public static readonly HsvRange Yellow = new() { HueMinDeg = 40, HueMaxDeg = 70, MinSaturation = 0.5, MinValue = 0.5 };

    private readonly Dictionary<EpisodeColor, Active> _active = [];

    private sealed class Active
    {
        public required DateTimeOffset Start { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public double Max { get; set; }
        public RectInt? Bbox { get; set; }
        public int Frames { get; set; }
        public HashSet<int> SavedOffsets { get; } = [];
    }

    /// <summary>Czy trwa epizod tego koloru.</summary>
    public bool IsActive(EpisodeColor color) => _active.ContainsKey(color);

    /// <summary>Aktualizacja po klatce — zwraca zdarzenia do obsłużenia przez kolektor (zapis klatki, OCR, wiersz jsonl).</summary>
    public IReadOnlyList<EpisodeEvent> Update(DateTimeOffset now, ColorRegion red, ColorRegion yellow)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(yellow);

        var events = new List<EpisodeEvent>();
        Observe(EpisodeColor.Czerwien, now, red, events);
        Observe(EpisodeColor.Zolc, now, yellow, events);
        return events;
    }

    /// <summary>Zamyka wszystkie trwające epizody (koniec przebiegu / rozłączenie na długo) — zwraca zdarzenia końca.</summary>
    public IReadOnlyList<EpisodeEvent> CloseAll(DateTimeOffset now)
    {
        var events = new List<EpisodeEvent>();
        foreach ((EpisodeColor color, Active a) in _active.ToList())
        {
            _active.Remove(color);
            events.Add(new EpisodeEvent(color, EpisodeEvent.End, null, new Episode(color, a.Start, now, a.Max, a.Bbox, a.Frames)));
        }

        return events;
    }

    private void Observe(EpisodeColor color, DateTimeOffset now, ColorRegion region, List<EpisodeEvent> events)
    {
        bool visible = region.Udzial >= StartFraction;
        if (_active.TryGetValue(color, out Active? a))
        {
            if (visible)
            {
                a.LastSeen = now;
                a.Max = Math.Max(a.Max, region.Udzial);
                a.Bbox = Union(a.Bbox, region.Bbox);
                a.Frames++;
                foreach (int offset in DlugiOptions.EpisodeFrameOffsetsSek)
                {
                    if (!a.SavedOffsets.Contains(offset) && (now - a.Start).TotalSeconds >= offset)
                    {
                        a.SavedOffsets.Add(offset);
                        events.Add(new EpisodeEvent(color, EpisodeEvent.SaveFrame, offset, null));
                    }
                }
            }
            else if (now - a.LastSeen >= EndAfter)
            {
                _active.Remove(color);
                events.Add(new EpisodeEvent(color, EpisodeEvent.End, null, new Episode(color, a.Start, a.LastSeen, a.Max, a.Bbox, a.Frames)));
            }

            return;
        }

        if (visible)
        {
            var started = new Active { Start = now, LastSeen = now, Max = region.Udzial, Bbox = region.Bbox, Frames = 1 };
            started.SavedOffsets.Add(0);
            _active[color] = started;
            events.Add(new EpisodeEvent(color, EpisodeEvent.Start, null, null));
            events.Add(new EpisodeEvent(color, EpisodeEvent.SaveFrame, 0, null));
        }
    }

    private static RectInt? Union(RectInt? a, RectInt? b) => a is null ? b : b is null ? a : a.Value.Union(b.Value);

    /// <summary>Udział i bbox pikseli czerwonych wg progów <paramref name="red"/> (z <c>opencnc.red</c>) w całej klatce.</summary>
    public static ColorRegion MeasureRed(RgbaImage frame, RedThresholds red)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(red);

        RedDetectionResult r = RedTextDetector.Analyze(frame, red, StartFraction);
        return new ColorRegion(r.RedFraction, r.RedBounds);
    }

    /// <summary>Udział i bbox pikseli w zakresie HSV <paramref name="range"/> (żółć) w całej klatce.</summary>
    public static ColorRegion MeasureRange(RgbaImage frame, HsvRange range)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(range);

        byte[] px = frame.Pixels;
        int w = frame.Width;
        int count = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int i = 0, idx = 0; i < px.Length; i += 4, idx++)
        {
            if (!ColorMath.InRange(new Rgba(px[i], px[i + 1], px[i + 2], px[i + 3]), range))
            {
                continue;
            }

            count++;
            int x = idx % w;
            int y = idx / w;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        int total = w * frame.Height;
        return new ColorRegion(total == 0 ? 0 : count / (double)total, count == 0 ? null : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1));
    }
}
