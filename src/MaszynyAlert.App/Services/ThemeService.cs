using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MaszynyAlert.Core.Config;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App.Services;

public static class ThemePalette
{
    private static readonly IReadOnlyDictionary<string, string> LightHex = new Dictionary<string, string>
    {
        // Tła i powierzchnie.
        ["PanelBackgroundBrush"] = "#F2F5F7",
        ["CardBackgroundBrush"] = "#FFFFFF",
        ["CardHighlightBrush"] = "#F4F9FB",
        ["SurfaceAltBrush"] = "#F7F9FA",
        ["SurfaceHoverBrush"] = "#EAF2F6",
        ["ControlBackgroundBrush"] = "#E7EDF1",
        ["InputBackgroundBrush"] = "#FFFFFF",
        ["HeaderBackgroundBrush"] = "#EAF0F3",
        ["GridLineBrush"] = "#EDF1F4",
        ["DividerBrush"] = "#DDE5E9",
        ["ControlBorderBrush"] = "#CAD6DD",
        ["SectionBorderBrush"] = "#CBD2D6",
        // Tekst.
        ["TextPrimaryBrush"] = "#172D3C",
        ["TextSecondaryBrush"] = "#526176",
        ["TextMutedBrush"] = "#677583",
        // Ciemny pas nagłówka Panelu administracyjnego i ciemne pop-upy (te same w obu motywach poza tekstem).
        ["HeaderBandBrush"] = "#17232E",
        ["HeaderBandTextBrush"] = "#FFFFFF",
        ["HeaderBandMutedTextBrush"] = "#CAD8DF",
        ["WindowBackgroundBrush"] = "#17232E",
        // Akcent (morska zieleń) i stany.
        ["AccentBrush"] = "#087E70",
        ["AccentSoftBrush"] = "#DDEDE9",
        ["AccentTextOnBrush"] = "#FFFFFF",
        ["OkGreenBrush"] = "#2E7D32",
        ["OkSoftBackgroundBrush"] = "#E7F5F2",
        ["OkSoftBorderBrush"] = "#9CCFC7",
        ["WarningYellowBrush"] = "#F7AB49",
        ["WarningSoftBackgroundBrush"] = "#FFF5E5",
        ["WarningSoftBorderBrush"] = "#F0C57B",
        ["WarningTextBrush"] = "#7A4C00",
        ["WarningBandBrush"] = "#FFE3B5",
        ["AlertRedBrush"] = "#B42338",
        ["ErrorBorderBrush"] = "#D76775",
        ["ErrorSoftBackgroundBrush"] = "#FFF0F0",
        ["ErrorSoftBorderBrush"] = "#DB9B9B",
        ["ErrorTextBrush"] = "#9C2032",
        ["ErrorBandBrush"] = "#FFE4E5",
    };

    private static readonly IReadOnlyDictionary<string, string> DarkHex = new Dictionary<string, string>
    {
        ["PanelBackgroundBrush"] = "#1B1D1F",
        ["CardBackgroundBrush"] = "#25282A",
        ["CardHighlightBrush"] = "#2C3133",
        ["SurfaceAltBrush"] = "#2B2F31",
        ["SurfaceHoverBrush"] = "#33383A",
        ["ControlBackgroundBrush"] = "#34393C",
        ["InputBackgroundBrush"] = "#1F2224",
        ["HeaderBackgroundBrush"] = "#2D3234",
        ["GridLineBrush"] = "#34393C",
        ["DividerBrush"] = "#3B4043",
        ["ControlBorderBrush"] = "#4B5154",
        ["SectionBorderBrush"] = "#60676B",
        ["TextPrimaryBrush"] = "#E8EAEB",
        ["TextSecondaryBrush"] = "#ADB4B8",
        ["TextMutedBrush"] = "#959DA1",
        ["HeaderBandBrush"] = "#121415",
        ["HeaderBandTextBrush"] = "#F3F5F6",
        ["HeaderBandMutedTextBrush"] = "#9DA6AB",
        ["WindowBackgroundBrush"] = "#121415",
        ["AccentBrush"] = "#2FB39B",
        ["AccentSoftBrush"] = "#1F3B37",
        ["AccentTextOnBrush"] = "#0F1A18",
        ["OkGreenBrush"] = "#4CAF50",
        ["OkSoftBackgroundBrush"] = "#1F3330",
        ["OkSoftBorderBrush"] = "#2F6B60",
        ["WarningYellowBrush"] = "#F7AB49",
        ["WarningSoftBackgroundBrush"] = "#3A2F1B",
        ["WarningSoftBorderBrush"] = "#8A6A2E",
        ["WarningTextBrush"] = "#F2C777",
        ["WarningBandBrush"] = "#4A3B1E",
        ["AlertRedBrush"] = "#E4475B",
        ["ErrorBorderBrush"] = "#C5505F",
        ["ErrorSoftBackgroundBrush"] = "#3B2326",
        ["ErrorSoftBorderBrush"] = "#7C3A42",
        ["ErrorTextBrush"] = "#F08A96",
        ["ErrorBandBrush"] = "#4A2A2E",
    };

    /// <summary>Klucze pędzli motywu — w kolejności z palety jasnej.</summary>
    public static IReadOnlyList<string> Keys { get; } = LightHex.Keys.ToArray();

    /// <summary>Kolory motywu <paramref name="theme"/> według klucza pędzla.</summary>
    public static IReadOnlyDictionary<string, Color> ColorsFor(AppTheme theme)
    {
        IReadOnlyDictionary<string, string> hex = theme == AppTheme.Dark ? DarkHex : LightHex;
        return hex.ToDictionary(pair => pair.Key, pair => (Color)System.Windows.Media.ColorConverter.ConvertFromString(pair.Value));
    }

    /// <summary>
    /// Nakłada pędzle motywu <paramref name="theme"/> na słownik zasobów (z reguły
    /// <c>Application.Current.Resources</c>) — zamrożone <see cref="SolidColorBrush"/>, nigdy gradient.
    /// </summary>
    public static void ApplyTo(ResourceDictionary resources, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach ((string key, Color color) in ColorsFor(theme))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }
}

/// <summary>
/// Przełącznik motywu (jasny/ciemny) dla wszystkich okien programu. Wybór operatora jest zapisywany w
/// profilu użytkownika (<see cref="ThemeSettingsStore"/>, zwykle <c>%LocalAppData%\MaszynyAlert\theme.json</c>)
/// i obowiązuje od następnego startu; w trakcie pracy zmiana jest natychmiastowa, bo widoki odwołują się do
/// pędzli przez <c>DynamicResource</c>.
/// </summary>
public sealed class ThemeService
{
    private readonly ThemeSettingsStore _store;
    private readonly ResourceDictionary _resources;
    private readonly ILogger<ThemeService>? _logger;

    /// <summary>Usługa dla słownika zasobów bieżącej aplikacji WPF.</summary>
    public ThemeService(ThemeSettingsStore store, ILogger<ThemeService>? logger = null)
        : this(store, System.Windows.Application.Current?.Resources
            ?? throw new InvalidOperationException("Brak aplikacji WPF — motyw nie ma gdzie zostać nałożony."), logger)
    {
    }

    /// <summary>Usługa dla dowolnego słownika zasobów (testy).</summary>
    public ThemeService(ThemeSettingsStore store, ResourceDictionary resources, ILogger<ThemeService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _logger = logger;
    }

    /// <summary>Motyw obecnie nałożony na okna.</summary>
    public AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>Czy obecnie obowiązuje motyw ciemny.</summary>
    public bool IsDark => Current == AppTheme.Dark;

    /// <summary>Zgłaszane po każdej zmianie motywu (po nałożeniu pędzli).</summary>
    public event Action<AppTheme>? ThemeChanged;

    /// <summary>Ścieżka pliku z zapisanym wyborem.</summary>
    public string SettingsPath => _store.Path;

    /// <summary>Nakłada motyw zapisany w profilu użytkownika (przy starcie programu, przed tworzeniem okien).</summary>
    public void Initialize() => Apply(_store.Load().Resolve());

    /// <summary>Przełącza na podany motyw i zapamiętuje wybór; ten sam motyw co obecny niczego nie zmienia.</summary>
    public void Set(AppTheme theme)
    {
        if (theme == Current)
        {
            return;
        }

        Apply(theme);
        if (!_store.Save(new ThemeSettings { Theme = theme }))
        {
            _logger?.LogWarning("Nie udało się zapisać wyboru motywu w pliku {Path}.", _store.Path);
        }
    }

    /// <summary>Jasny ↔ ciemny.</summary>
    public void Toggle() => Set(IsDark ? AppTheme.Light : AppTheme.Dark);

    private void Apply(AppTheme theme)
    {
        ThemePalette.ApplyTo(_resources, theme);
        Current = theme;
        _logger?.LogInformation("Motyw okien: {Theme}.", theme == AppTheme.Dark ? "ciemny" : "jasny");
        ThemeChanged?.Invoke(theme);
    }
}
