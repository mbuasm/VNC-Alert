using System.IO;
using System.Windows.Media;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class ThemeContrastTests
{
    private const double MinContrast = 4.5;

    public static TheoryData<AppTheme, string, string> TextOnBackground()
    {
        var data = new TheoryData<AppTheme, string, string>();
        foreach (AppTheme theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            // Tekst stanu maszyny (symbol, krótki stan) na karcie Podglądu i na kafelku nakładki.
            foreach (string text in new[] { "ErrorTextBrush", "WarningTextBrush", "AccentBrush" })
            {
                data.Add(theme, text, "CardBackgroundBrush");
                data.Add(theme, text, "SurfaceAltBrush");
            }

            data.Add(theme, "TextMutedBrush", "CardBackgroundBrush");
            data.Add(theme, "ErrorTextBrush", "PanelBackgroundBrush");
            data.Add(theme, "OkGreenBrush", "CardBackgroundBrush");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextOnBackground))]
    public void TekstMaKontrastCoNajmniej45(AppTheme theme, string text, string background)
    {
        IReadOnlyDictionary<string, Color> palette = ThemePalette.ColorsFor(theme);

        double ratio = Contrast(palette[text], palette[background]);

        Assert.True(ratio >= MinContrast, $"{theme}: {text} na {background} = {ratio:0.00}:1 (wymagane {MinContrast}:1).");
    }

    [Fact]
    public void KartyINakladka_KolorTekstuStanuZPaletyMotywu()
    {
        foreach (string view in new[] { "OverlayWindow.xaml", "OperatorWindow.xaml" })
        {
            string xaml = File.ReadAllText(ViewPath(view));
            Assert.DoesNotContain("Foreground=\"{Binding Accent}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KodWidokowNieUzywaStalychPedzliBrushes()
    {
        string views = Path.GetDirectoryName(ViewPath("SetupView.xaml.cs"))!;
        foreach (string file in Directory.GetFiles(views, "*.xaml.cs"))
        {
            Assert.DoesNotContain("Brushes.", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KrzyzykNaBursztynowymPaskuMaCiemnyTekstWObuMotywach()
    {
        string xaml = File.ReadAllText(ViewPath("ScreenPopupWindow.xaml"));
        int close = xaml.IndexOf("x:Name=\"CloseButton\"", StringComparison.Ordinal);
        Assert.True(close > 0);
        int end = xaml.IndexOf('>', close);
        string tag = xaml[xaml.LastIndexOf('<', close)..end];
        Assert.Contains("Foreground=\"#3A2C00\"", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void KalendarzRaportuMaStylZPalety()
    {
        string app = File.ReadAllText(Path.Combine(SrcApp(), "App.xaml"));
        Assert.Contains("TargetType=\"Calendar\"", app, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"CalendarDayButton\"", app, StringComparison.Ordinal);
    }

    private static string SrcApp()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "MaszynyAlert.App", "App.xaml")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("Nie znaleziono src/MaszynyAlert.App."), "src", "MaszynyAlert.App");
    }

    private static string ViewPath(string name) => Path.Combine(SrcApp(), "Views", name);

    private static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
