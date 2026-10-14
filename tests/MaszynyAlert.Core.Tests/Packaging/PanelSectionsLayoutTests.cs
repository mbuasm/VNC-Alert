using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class PanelSectionsLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Sekcje zakładki „Podgląd techniczny” w kolejności, w jakiej mają leżeć jedna pod drugą.</summary>
    private static readonly string[] SectionHeaders =
    [
        "Maszyny",
        "Komputery",
        "Alerty aktywne",
        "Maszyny bez pracy – dołóż prace",
        "Ekran HMI – włącz okno podglądu",
        "Historia (ostatnie 50)",
        "Log (ostatnie 50 linii)",
    ];

    private static string AppDir => Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App");

    private static XDocument StatusWindow() =>
        XDocument.Load(Path.Combine(AppDir, "Views", "StatusWindow.xaml"), LoadOptions.SetLineInfo);

    private static XDocument AppXaml() =>
        XDocument.Load(Path.Combine(AppDir, "App.xaml"), LoadOptions.SetLineInfo);

    private static XElement TechnicalTab(XDocument window) =>
        Assert.Single(window.Descendants(Xaml + "TabItem"), e => e.Attribute("Header")?.Value == "Podgląd techniczny");

    private static XElement Section(XElement technicalTab, string header) =>
        Assert.Single(technicalTab.Descendants(Xaml + "GroupBox"), e => e.Attribute("Header")?.Value == header);

    [Fact]
    public void TabelaMaszyn_MiesciSieWOknieBezPoziomegoPaska()
    {
        XElement grid = Section(TechnicalTab(StatusWindow()), "Maszyny").Descendants(Xaml + "DataGrid").First();
        double fixedWidth = grid.Descendants(Xaml + "DataGridTextColumn")
            .Select(c => c.Attribute("Width")?.Value)
            .Where(w => w is not null && double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Sum(w => double.Parse(w!, System.Globalization.CultureInfo.InvariantCulture));
        double minWidth = grid.Descendants(Xaml + "DataGridTextColumn")
            .Sum(c => double.TryParse(c.Attribute("MinWidth")?.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double m) ? m : 0);

        Assert.True(fixedWidth + minWidth <= 1100, $"Stałe szerokości {fixedWidth} px + minimalne {minWidth} px > 1100 px.");
    }

    [Fact]
    public void BlokadaPanelu_PrzewijanieKolkiemToAktywnosc()
    {
        string code = File.ReadAllText(Path.Combine(AppDir, "Views", "StatusWindow.xaml.cs"));
        Assert.Contains("PreviewMouseWheel +=", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewMouseMove +=", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PodgladTechniczny_SekcjeLezaJednaPodDrugaWJednymPionowymStosie()
    {
        XElement tab = TechnicalTab(StatusWindow());

        XElement[] sections = [.. tab.Descendants(Xaml + "GroupBox")];
        Assert.Equal(SectionHeaders, sections.Select(s => s.Attribute("Header")?.Value));

        // Jeden rodzic dla wszystkich sekcji: pionowy StackPanel (bez Orientation="Horizontal"), żadnych
        // kolumn siatki.
        XElement parent = Assert.Single(sections.Select(s => s.Parent!).Distinct());
        Assert.Equal(Xaml + "StackPanel", parent.Name);
        Assert.NotEqual("Horizontal", parent.Attribute("Orientation")?.Value);
        foreach (XElement section in sections)
        {
            Assert.Null(section.Attribute("Grid.Column"));
            Assert.Null(section.Attribute("Grid.Row"));
        }

        // Stos jest dłuższy niż okno (850 px), więc zakładka przewija się w pionie jako całość.
        Assert.Contains(parent.Ancestors(), a => a.Name == Xaml + "ScrollViewer");
    }

    [Fact]
    public void PodgladTechniczny_LogMaMiejsceNaWieleLinii()
    {
        // 50 linii Consolas 11 pt to ~900 px.
        XElement log = Section(TechnicalTab(StatusWindow()), "Log (ostatnie 50 linii)");
        XElement list = Assert.Single(log.Descendants(Xaml + "ListBox"));

        double minHeight = double.Parse(list.Attribute("MinHeight")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(minHeight >= 300, $"Lista logu ma mieć MinHeight ≥ 300 px, jest {minHeight}.");
    }

    [Fact]
    public void PodgladTechniczny_KazdaSekcjaMaGornyLimitWysokosci()
    {
        // W pionowym stosie bez limitu jedna długa lista (np. 50 linii logu albo 12 maszyn) zepchnęłaby resztę
        // poza ekran; każda sekcja przewija się sama powyżej swojej MaxHeight.
        XElement tab = TechnicalTab(StatusWindow());
        foreach (string header in SectionHeaders)
        {
            XElement section = Section(tab, header);
            bool bounded = section.Descendants().Any(e =>
                (e.Name == Xaml + "ListBox" || e.Name == Xaml + "DataGrid" || e.Name == Xaml + "ScrollViewer")
                && e.Attribute("MaxHeight") is not null);
            Assert.True(bounded, $"Sekcja „{header}” ma mieć listę z MaxHeight.");
        }
    }

    [Fact]
    public void GroupBox_MaWlasnySzablonBezBialychLiniiIBierzeKolorRamkiZMotywu()
    {
        XElement style = Assert.Single(AppXaml().Descendants(Xaml + "Style"), s => s.Attribute("TargetType")?.Value == "GroupBox");

        XElement template = Assert.Single(style.Descendants(Xaml + "ControlTemplate"));
        Assert.Equal("GroupBox", template.Attribute("TargetType")?.Value);

        // Żadnego koloru wpisanego na stałe w szablonie:
        // tu każdy pędzel ma iść z motywu (TemplateBinding albo DynamicResource).
        foreach (XAttribute brush in template.Descendants().Attributes().Where(a => a.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal) || a.Name.LocalName == "Background"))
        {
            Assert.True(brush.Value.StartsWith("{", StringComparison.Ordinal) || brush.Value == "Transparent",
                $"Szablon GroupBox ma kolor wpisany na stałe: {brush.Name.LocalName}=\"{brush.Value}\".");
        }

        XElement borderSetter = Assert.Single(style.Elements(Xaml + "Setter"), s => s.Attribute("Property")?.Value == "BorderBrush");
        Assert.Equal("{DynamicResource SectionBorderBrush}", borderSetter.Attribute("Value")?.Value);
        Assert.Contains(AppXaml().Descendants(Xaml + "SolidColorBrush"),
            b => b.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "SectionBorderBrush"));
    }
}
