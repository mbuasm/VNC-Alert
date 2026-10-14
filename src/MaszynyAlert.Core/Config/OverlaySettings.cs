using System.Text.Json;

namespace MaszynyAlert.Core.Config;

public sealed record OverlaySettings
{
    /// <summary>Czy operator włączył nakładkę (menu zasobnika „Nakładka statusu”).</summary>
    public bool? Enabled { get; init; }

    /// <summary>Czy operator włączył przepuszczanie kliknięć.</summary>
    public bool? ClickThrough { get; init; }

    /// <summary>
    /// Wartość <c>ui.overlay.enabled</c> ze stanowiska obowiązująca w chwili zapisania
    /// <see cref="Enabled"/>. Dzięki niej ZMIANA ustawienia w <c>config.json</c> jest widoczna od razu:
    /// wybór operatora obowiązuje tylko dopóki administrator nie zmieni ustawienia stanowiska — patrz
    /// <see cref="ResolveEnabled"/>.
    /// </summary>
    public bool? EnabledDefault { get; init; }

    /// <summary>Wartość <c>ui.overlay.clickThrough</c> obowiązująca w chwili zapisania <see cref="ClickThrough"/>.</summary>
    public bool? ClickThroughDefault { get; init; }

    public OverlayLayout? Layout { get; init; }

    /// <summary>Wartość <c>ui.overlay.layout</c> obowiązująca w chwili zapisania <see cref="Layout"/>.</summary>
    public OverlayLayout? LayoutDefault { get; init; }

    /// <summary>Lewa krawędź okna nakładki po przeciągnięciu, w jednostkach niezależnych od DPI.</summary>
    public double? Left { get; init; }

    /// <summary>Górna krawędź okna nakładki po przeciągnięciu.</summary>
    public double? Top { get; init; }

    /// <summary>Zapisane położenie okna — tylko gdy obie współrzędne są sensownymi liczbami.</summary>
    public bool HasPlacement => Left is { } left && Top is { } top && double.IsFinite(left) && double.IsFinite(top);

    /// <summary>
    /// Czy nakładka ma być widoczna. Wybór operatora wygrywa z ustawieniem stanowiska, ale tylko dopóki
    /// to ustawienie się nie zmieniło: po edycji <c>ui.overlay.enabled</c> w <c>config.json</c> obowiązuje
    /// nowa wartość z pliku, a nie zapamiętany wcześniej wybór. Stary plik <c>overlay.json</c> bez
    /// <see cref="EnabledDefault"/> również oddaje pierwszeństwo konfiguracji — inaczej jedno kliknięcie
    /// krzyżyka chowałoby nakładkę na zawsze.
    /// </summary>
    public bool ResolveEnabled(OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return Resolve(Enabled, EnabledDefault, configured.Enabled);
    }

    /// <summary>Czy nakładka przepuszcza kliknięcia — te same zasady co w <see cref="ResolveEnabled"/>.</summary>
    public bool ResolveClickThrough(OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return Resolve(ClickThrough, ClickThroughDefault, configured.ClickThrough);
    }

    /// <summary>Układ kafelków — te same zasady co w <see cref="ResolveEnabled"/>.</summary>
    public OverlayLayout ResolveLayout(OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return Layout is { } chosen && LayoutDefault == configured.Layout ? chosen : configured.Layout;
    }

    /// <summary>Zapamiętuje wybór operatora razem z ustawieniem stanowiska, wobec którego został podjęty.</summary>
    public OverlaySettings WithEnabled(bool enabled, OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return this with { Enabled = enabled, EnabledDefault = configured.Enabled };
    }

    /// <summary>Zapamiętuje wybór przepuszczania kliknięć razem z ustawieniem stanowiska.</summary>
    public OverlaySettings WithClickThrough(bool clickThrough, OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return this with { ClickThrough = clickThrough, ClickThroughDefault = configured.ClickThrough };
    }

    /// <summary>Zapamiętuje wybrany układ kafelków razem z ustawieniem stanowiska.</summary>
    public OverlaySettings WithLayout(OverlayLayout layout, OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return this with { Layout = layout, LayoutDefault = configured.Layout };
    }

    public OverlaySettings Normalize(OverlayConfig configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        OverlaySettings result = this;
        if (Layout is not null && LayoutDefault != configured.Layout)
            result = result with { Layout = null, LayoutDefault = null };
        if (Enabled is not null && EnabledDefault != configured.Enabled)
            result = result with { Enabled = null, EnabledDefault = null };
        if (ClickThrough is not null && ClickThroughDefault != configured.ClickThrough)
            result = result with { ClickThrough = null, ClickThroughDefault = null };
        return result;
    }

    private static bool Resolve(bool? chosen, bool? defaultWhenChosen, bool configured) =>
        chosen is { } value && defaultWhenChosen == configured ? value : configured;
}

/// <summary>
/// Odczyt i zapis <see cref="OverlaySettings"/> w jednym pliku JSON (zwykle
/// <c>%LocalAppData%\MaszynyAlert\overlay.json</c>). Plik jest wyłącznie wygodą operatora, więc żaden błąd
/// dysku nie może przewrócić programu: uszkodzony albo niedostępny plik daje ustawienia puste, a nieudany
/// zapis jest po cichu pomijany (nakładka zadziała, tylko nie zapamięta położenia).
/// </summary>
public sealed class OverlaySettingsStore
{
    private readonly string _path;

    /// <summary>Tworzy magazyn dla pliku <paramref name="path"/>; plik nie musi jeszcze istnieć.</summary>
    public OverlaySettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Ścieżka pliku z ustawieniami nakładki.</summary>
    public string Path => _path;

    /// <summary>
    /// Wczytuje ustawienia. Brak pliku, pusty plik, uszkodzony JSON i brak dostępu dają
    /// <c>new OverlaySettings()</c>, czyli „operator niczego nie zmieniał”.
    /// </summary>
    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new OverlaySettings();
            }

            return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(_path), ConfigLoader.JsonOptions)
                ?? new OverlaySettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OverlaySettings();
        }
    }

    /// <summary>
    /// Zapisuje ustawienia (zapis atomowy przez plik tymczasowy obok docelowego). Zwraca
    /// <see langword="false"/>, gdy zapis się nie udał — wołający nie ma co z tym robić poza wpisem w logu.
    /// </summary>
    public bool Save(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, ConfigLoader.JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Plik tymczasowy zostanie po nieudanym zapisie — nie jest to powód do przerwania programu.
            }
        }
    }
}
