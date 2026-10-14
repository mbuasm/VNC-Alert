using System.IO;
using System.Windows;
using System.Windows.Media;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class ThemePaletteTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vnc-alert-theme-app").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ThemeSettingsStore Store() => new(Path.Combine(_directory, "theme.json"));

    [Fact]
    public void ObiePaletyMajaTeSameKlucze()
    {
        IReadOnlyDictionary<string, Color> light = ThemePalette.ColorsFor(AppTheme.Light);
        IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);

        Assert.Equal(light.Keys.Order(), dark.Keys.Order());
        Assert.Equal(ThemePalette.Keys.Order(), light.Keys.Order());
        Assert.NotEmpty(ThemePalette.Keys);
    }

    [Fact]
    public void CiemnyMotywJestCiemnyAJasnyJasny()
    {
        IReadOnlyDictionary<string, Color> light = ThemePalette.ColorsFor(AppTheme.Light);
        IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);

        foreach (string key in new[] { "PanelBackgroundBrush", "CardBackgroundBrush", "SurfaceAltBrush", "InputBackgroundBrush", "ControlBackgroundBrush" })
        {
            Assert.True(Luminance(light[key]) > 0.8, $"{key} w motywie jasnym ma być jasne.");
            Assert.True(Luminance(dark[key]) < 0.25, $"{key} w motywie ciemnym ma być ciemne.");
        }

        Assert.True(Luminance(dark["TextPrimaryBrush"]) > 0.75, "Tekst główny w ciemnym motywie ma być jasny.");
        Assert.True(Luminance(light["TextPrimaryBrush"]) < 0.25, "Tekst główny w jasnym motywie ma być ciemny.");
    }

    [Fact]
    public void CiemneTlaNieSaNiebieskoFioletowe()
    {
        // Barwa 200–300° (niebieski, granat, fiolet) jest dopuszczalna tylko przy nasyceniu tak małym, że
        // kolor czyta się jako szary/grafitowy. Wyjątkiem nie są nawet pasy nagłówka.
        IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);
        var offenders = new List<string>();
        foreach ((string key, Color color) in dark)
        {
            if (!key.Contains("Background") && !key.Contains("Surface") && !key.Contains("Band") && !key.Contains("Card") && !key.Contains("Header"))
                continue;
            (double hue, double saturation) = HueSaturation(color);
            if (hue is >= 200 and <= 300 && saturation > 0.15)
                offenders.Add($"{key}={color} (h={hue:F0}°, s={saturation:F2})");
        }

        Assert.True(offenders.Count == 0, "Niebiesko-fioletowe tła w ciemnym motywie: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PedzleMotywuSaJednolite_BezGradientow()
    {
        var resources = new ResourceDictionary();

        ThemePalette.ApplyTo(resources, AppTheme.Dark);

        foreach (string key in ThemePalette.Keys)
        {
            object brush = resources[key];
            Assert.IsType<SolidColorBrush>(brush);
            Assert.True(((Freezable)brush).IsFrozen, $"{key} ma być zamrożony.");
        }
    }

    [Fact]
    public void KoloryStanowZachowujaZnaczenieWObuMotywach()
    {
        IReadOnlyDictionary<string, Color> light = ThemePalette.ColorsFor(AppTheme.Light);
        IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);

        foreach (string key in new[] { "AlertRedBrush", "ErrorTextBrush" })
            foreach (Color color in new[] { light[key], dark[key] })
                Assert.True(HueSaturation(color).Hue is >= 335 or <= 15, $"{key} ma pozostać czerwienią ({color}).");
        foreach (string key in new[] { "WarningYellowBrush", "WarningTextBrush" })
            foreach (Color color in new[] { light[key], dark[key] })
                Assert.True(HueSaturation(color).Hue is >= 25 and <= 55, $"{key} ma pozostać bursztynem ({color}).");
        foreach (string key in new[] { "AccentBrush", "OkGreenBrush" })
            foreach (Color color in new[] { light[key], dark[key] })
                Assert.True(HueSaturation(color).Hue is >= 100 and <= 185, $"{key} ma pozostać zielenią ({color}).");
    }

    [Fact]
    public void UslugaNakladaMotywZPlikuIZapisujeZmiane()
    {
        ThemeSettingsStore store = Store();
        store.Save(new ThemeSettings { Theme = AppTheme.Dark });
        var resources = new ResourceDictionary();
        var service = new ThemeService(store, resources);
        var changes = new List<AppTheme>();
        service.ThemeChanged += changes.Add;

        service.Initialize();

        Assert.Equal(AppTheme.Dark, service.Current);
        Assert.True(service.IsDark);
        Assert.Equal(ThemePalette.ColorsFor(AppTheme.Dark)["PanelBackgroundBrush"], ((SolidColorBrush)resources["PanelBackgroundBrush"]).Color);

        service.Toggle();

        Assert.Equal(AppTheme.Light, service.Current);
        Assert.Equal(ThemePalette.ColorsFor(AppTheme.Light)["PanelBackgroundBrush"], ((SolidColorBrush)resources["PanelBackgroundBrush"]).Color);
        Assert.Equal(AppTheme.Light, Store().Load().Resolve());
        Assert.Equal(new[] { AppTheme.Dark, AppTheme.Light }, changes);
    }

    [Fact]
    public void TenSamMotywNieZapisujePlikuPonownie()
    {
        ThemeSettingsStore store = Store();
        var service = new ThemeService(store, new ResourceDictionary());
        service.Initialize();

        service.Set(AppTheme.Light);

        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void ObramowanieSekcjiJestJasnoszareWObuMotywach()
    {
        IReadOnlyDictionary<string, Color> light = ThemePalette.ColorsFor(AppTheme.Light);
        IReadOnlyDictionary<string, Color> dark = ThemePalette.ColorsFor(AppTheme.Dark);

        Color lightBorder = light["SectionBorderBrush"];
        Color darkBorder = dark["SectionBorderBrush"];

        Assert.True(HueSaturation(lightBorder).Saturation < 0.12, $"Jasna ramka ma być szara, nie kolorowa ({lightBorder}).");
        Assert.True(HueSaturation(darkBorder).Saturation < 0.12, $"Ciemna ramka ma być szara, nie kolorowa ({darkBorder}).");
        Assert.InRange(Luminance(lightBorder), 0.7, 0.9);
        Assert.True(Luminance(lightBorder) < Luminance(light["CardBackgroundBrush"]), "W jasnym motywie ramka ma być ciemniejsza od karty.");
        Assert.InRange(Luminance(darkBorder), 0.3, 0.5);
        Assert.True(Luminance(darkBorder) > Luminance(dark["ControlBorderBrush"]), "W ciemnym motywie ramka sekcji ma być jaśniejsza od obrysu pól.");
        Assert.True(Luminance(darkBorder) < 0.6, "Ramka w ciemnym motywie nie może świecić jak biała linia.");
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static (double Hue, double Saturation) HueSaturation(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double lightness = (max + min) / 2;
        double saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        double hue = 0;
        if (delta != 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;
        }
        return (hue, saturation);
    }
}
