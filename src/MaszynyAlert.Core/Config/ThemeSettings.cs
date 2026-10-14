using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaszynyAlert.Core.Config;

/// <summary>Motyw okien programu: jasny albo ciemny.</summary>
public enum AppTheme
{
    /// <summary>Jasne tło, ciemny tekst.</summary>
    Light,

    /// <summary>Ciemne, neutralne (grafitowe) tło i jasny tekst; kolory stanów bez zmian znaczenia.</summary>
    Dark,
}

/// <summary>
/// Wybór motywu zapisany w profilu użytkownika (przełącznik „Tryb ciemny” w Podglądzie i w menu zasobnika).
/// <see langword="null"/> oznacza „nie wybrano” — obowiązuje <see cref="AppTheme.Light"/>.
/// </summary>
/// <remarks>
/// Świadomie NIE w <c>config.json</c> (tak jak <see cref="OverlaySettings"/>): w wersji zainstalowanej leży on
/// w <c>%ProgramData%</c> i jego zapis wymaga UAC, a przełączenie motywu kliknięciem nie może o niego prosić.
/// Motyw jest wygodą jednej osoby przy jednym komputerze, więc nie wchodzi też do wspólnej konfiguracji grupy.
/// </remarks>
public sealed record ThemeSettings
{
    /// <summary>Motyw wybrany przez operatora; zapisywany słownie (<c>"Light"</c>/<c>"Dark"</c>).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme? Theme { get; init; }

    /// <summary>Motyw do zastosowania: wybór operatora albo jasny, gdy nic nie wybrano.</summary>
    public AppTheme Resolve() => Theme ?? AppTheme.Light;
}

/// <summary>
/// Odczyt i zapis <see cref="ThemeSettings"/> w jednym pliku JSON (zwykle
/// <c>%LocalAppData%\MaszynyAlert\theme.json</c>). Plik jest wyłącznie wygodą operatora, więc żaden błąd
/// dysku nie może przewrócić programu: brak pliku, uszkodzony JSON, nieznana nazwa motywu albo brak dostępu
/// dają ustawienia puste (jasny motyw), a nieudany zapis jest po cichu pomijany (motyw przełączy się na czas
/// tej sesji, tylko nie zostanie zapamiętany).
/// </summary>
public sealed class ThemeSettingsStore
{
    private readonly string _path;

    /// <summary>Tworzy magazyn dla pliku <paramref name="path"/>; plik nie musi jeszcze istnieć.</summary>
    public ThemeSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Ścieżka pliku z wyborem motywu.</summary>
    public string Path => _path;

    /// <summary>Wczytuje wybór motywu; każdy problem z plikiem daje <c>new ThemeSettings()</c>.</summary>
    public ThemeSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ThemeSettings();
            }

            return JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(_path), ConfigLoader.JsonOptions)
                ?? new ThemeSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ThemeSettings();
        }
    }

    /// <summary>
    /// Zapisuje wybór (atomowo, przez plik tymczasowy obok docelowego). Zwraca <see langword="false"/>, gdy
    /// zapis się nie udał — wołający ma co najwyżej zapisać to w logu.
    /// </summary>
    public bool Save(ThemeSettings settings)
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
                // Plik tymczasowy zostanie po nieudanym zapisie — to nie powód, żeby przerywać program.
            }
        }
    }
}
