using System.Text.Json.Serialization;

namespace MaszynyAlert.Core.Config;

public enum OverlayLayout
{
    /// <summary>Jeden poziomy wiersz kafelków — wartość domyślna.</summary>
    OneRow,

    /// <summary>Dwa wiersze, jeden nad drugim; kafelki idą wierszami (przy sześciu maszynach: 2 × 3).</summary>
    TwoRows,

    /// <summary>Jedna pionowa kolumna — wąski słupek przy krawędzi ekranu.</summary>
    OneColumn,

    /// <summary>Dwie kolumny, kafelki idą wierszami (przy dziewięciu maszynach: 5 × 2).</summary>
    TwoColumns,

    /// <summary>Trzy kolumny, kafelki idą wierszami (przy dziewięciu maszynach: 3 × 3).</summary>
    ThreeColumns,
}

/// <summary>
/// Jedno miejsce z wiedzą o układach nakładki: nazwa z <c>config.json</c>, etykieta dla menu i Panelu oraz
/// geometria siatki (WPF rysuje kafelki w <c>UniformGrid</c>, który ma stały jeden wymiar i drugi liczy
/// z liczby elementów). Menu zasobnika, zakładka Konfiguracja i okno nakładki korzystają z tego samego źródła,
/// żeby nowy układ nie wymagał poprawek w trzech plikach.
/// </summary>
public static class OverlayLayouts
{
    /// <summary>Wszystkie układy w kolejności pokazywanej operatorowi.</summary>
    public static IReadOnlyList<OverlayLayout> All { get; } =
    [
        OverlayLayout.OneRow,
        OverlayLayout.TwoRows,
        OverlayLayout.OneColumn,
        OverlayLayout.TwoColumns,
        OverlayLayout.ThreeColumns,
    ];

    /// <summary>Nazwa z <c>config.json</c> (<c>"one-row"</c>, <c>"two-rows"</c>…).</summary>
    public static string Name(OverlayLayout layout) => layout switch
    {
        OverlayLayout.OneRow => "one-row",
        OverlayLayout.TwoRows => "two-rows",
        OverlayLayout.OneColumn => "one-column",
        OverlayLayout.TwoColumns => "two-columns",
        OverlayLayout.ThreeColumns => "three-columns",
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Nieznany układ nakładki."),
    };

    /// <summary>Lista nazw do komunikatów i pól wyboru: <c>one-row, two-rows, one-column, two-columns, three-columns</c>.</summary>
    public static string NamesText => string.Join(", ", All.Select(Name));

    /// <summary>Etykieta po polsku dla menu zasobnika i opisu w Panelu.</summary>
    public static string Label(OverlayLayout layout) => layout switch
    {
        OverlayLayout.OneRow => "Jeden wiersz (pasek poziomy)",
        OverlayLayout.TwoRows => "Dwa wiersze (jeden nad drugim)",
        OverlayLayout.OneColumn => "Jedna kolumna (słupek pionowy)",
        OverlayLayout.TwoColumns => "Dwie kolumny",
        OverlayLayout.ThreeColumns => "Trzy kolumny",
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Nieznany układ nakładki."),
    };

    /// <summary>Nazwa z <c>config.json</c> → układ; wielkość liter i spacje wokół bez znaczenia.</summary>
    public static bool TryParse(string? text, out OverlayLayout layout)
    {
        string wanted = (text ?? "").Trim().ToLowerInvariant();
        foreach (OverlayLayout candidate in All)
        {
            if (Name(candidate) == wanted)
            {
                layout = candidate;
                return true;
            }
        }

        layout = OverlayLayout.OneRow;
        return false;
    }

    /// <summary>
    /// Stały wymiar siatki: liczba wierszy dla układów poziomych, liczba kolumn dla pionowych; zero = „policz
    /// z liczby kafelków” (dokładnie tak działa <c>UniformGrid.Rows</c>/<c>Columns</c> w WPF).
    /// </summary>
    public static (int Rows, int Columns) FixedGrid(OverlayLayout layout) => layout switch
    {
        OverlayLayout.OneRow => (1, 0),
        OverlayLayout.TwoRows => (2, 0),
        OverlayLayout.OneColumn => (0, 1),
        OverlayLayout.TwoColumns => (0, 2),
        OverlayLayout.ThreeColumns => (0, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Nieznany układ nakładki."),
    };

    /// <summary>
    /// Ile wierszy i kolumn dostanie <paramref name="tiles"/> kafelków w danym układzie — ta sama arytmetyka,
    /// którą robi <c>UniformGrid</c> (brakujący wymiar = sufit z dzielenia). Dla logu i testów; przy zerze
    /// kafelków siatka jest pusta.
    /// </summary>
    /// <summary>Szerokość kafelka nakładki z odstępem (132 + 5 px przy skali 1,0).</summary>
    public const double TileWidth = 137;

    /// <summary>Szerokość nakładki poza kafelkami: uchwyt, przycisk układu, krzyżyk, marginesy (przy skali 1,0).</summary>
    public const double ChromeWidth = 81;

    public static int MaxColumns(double workAreaWidth, double scale)
    {
        double s = scale > 0 && double.IsFinite(scale) ? scale : 1.0;
        return Math.Max(1, (int)Math.Floor((workAreaWidth / s - ChromeWidth) / TileWidth));
    }

    public static (int Rows, int Columns) FitGrid(OverlayLayout layout, int tiles, int maxColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tiles);
        int max = Math.Max(1, maxColumns);
        (int rows, int columns) = FixedGrid(layout);
        if (rows > 0)
        {
            int needed = (tiles + rows - 1) / rows;
            return needed > max ? (0, max) : (rows, 0);
        }

        return (0, Math.Min(columns, max));
    }

    public static (int Rows, int Columns) Shape(OverlayLayout layout, int tiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tiles);
        (int rows, int columns) = FixedGrid(layout);
        if (rows == 0)
        {
            rows = (tiles + columns - 1) / columns;
        }
        else
        {
            columns = (tiles + rows - 1) / rows;
        }

        return (rows, columns);
    }
}

/// <summary>Róg obszaru roboczego, w którym nakładka statusu staje się po pierwszym uruchomieniu.</summary>
/// <remarks>
/// W <c>config.json</c> zapis jest z myślnikiem (<c>"top-left"</c>, <c>"bottom-right"</c>) — patrz
/// konwerter w <see cref="ConfigLoader"/>.
/// </remarks>
public enum OverlayPosition
{
    TopLeft,

    /// <summary>Prawy górny róg.</summary>
    TopRight,

    /// <summary>Lewy dolny róg.</summary>
    BottomLeft,

    /// <summary>Prawy dolny róg.</summary>
    BottomRight,
}

public sealed record OverlayConfig
{
    /// <summary>Czy nakładka ma się pokazać przy starcie programu (dopóki operator nie zdecyduje inaczej).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Przezroczystość nakładki, gdy kursor NIE jest nad nią (0,2–1,0); po najechaniu myszą nakładka staje się
    /// w pełni nieprzezroczysta, tak samo jak okno alertu (<see cref="UiConfig.AlertOpacity"/>).
    /// </summary>
    public double Opacity { get; init; } = 0.55;

    /// <summary>Róg ekranu, w którym nakładka staje, zanim operator ją przeciągnie.</summary>
    public OverlayPosition Position { get; init; } = OverlayPosition.TopLeft;

    /// <summary>Powiększenie kafelków (0,6–2,0) — dla ekranu 4K albo dla operatora patrzącego z dystansu.</summary>
    public double Scale { get; init; } = 1.0;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OverlayLayout Layout { get; init; } = OverlayLayout.OneRow;

    public bool ClickThrough { get; init; }
}
