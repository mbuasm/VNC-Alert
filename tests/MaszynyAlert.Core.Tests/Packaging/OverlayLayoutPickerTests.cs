using System.Xml;
using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class OverlayLayoutPickerTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string ViewsDir => Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", "Views");

    private static XDocument View() => XDocument.Load(Path.Combine(ViewsDir, "OverlayWindow.xaml"), LoadOptions.SetLineInfo);

    private static string CodeBehind() => File.ReadAllText(Path.Combine(ViewsDir, "OverlayWindow.xaml.cs"));

    private static XElement Named(XDocument view, string name) =>
        Assert.Single(view.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));

    [Fact]
    public void PrzyciskUkladuLezyPodUchwytemPrzeciagania()
    {
        XDocument view = View();
        XElement handle = Named(view, "DragHandle");
        XElement button = Named(view, "LayoutButton");

        // Ten sam rodzic — pionowy stos w lewej kolumnie nakładki; uchwyt pierwszy, przycisk pod nim.
        Assert.Same(handle.Parent, button.Parent);
        Assert.Equal(Xaml + "StackPanel", handle.Parent!.Name);
        Assert.NotEqual("Horizontal", handle.Parent.Attribute("Orientation")?.Value);
        Assert.True(handle.ElementsAfterSelf().Contains(button), "LayoutButton ma być w stosie PO DragHandle (pod nim).");
    }

    [Fact]
    public void MenuUkladuJestWNakladceIBierzeUkladyZJednegoZrodla()
    {
        XDocument view = View();
        XElement menu = Named(view, "LayoutMenu");
        Assert.Equal(Xaml + "ContextMenu", menu.Name);

        // Pozycje menu buduje code-behind z OverlayLayouts.All (to samo źródło co menu zasobnika i Panel),
        // a wybór idzie przez SetLayout — ten sam zapis w overlay.json, co z menu zasobnika.
        string code = CodeBehind();
        Assert.Contains("OverlayLayouts.All", code, StringComparison.Ordinal);
        Assert.Contains("OverlayLayouts.Label(", code, StringComparison.Ordinal);
        Assert.Contains("LayoutMenu.Items.Add(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PrzyciskUkladuZnikaPrzyPrzenikaniuKliknięć()
    {
        // Przy przenikaniu kliknięć nakładka nie łapie myszy, więc przycisk byłby martwy — ma zniknąć razem
        // z możliwością klikania, a wrócić po wyłączeniu przenikania.
        string code = CodeBehind();
        Assert.Contains("LayoutButton.Visibility = ClickThrough ? Visibility.Collapsed : Visibility.Visible", code, StringComparison.Ordinal);
    }

    [Fact]
    public void UchwytIKrzyzykZnikajaPrzyPrzenikaniuKliknięćBezZmianySzerokosci()
    {
        string code = CodeBehind();
        Assert.Contains("DragHandle.Visibility = ClickThrough ? Visibility.Hidden : Visibility.Visible", code, StringComparison.Ordinal);
        Assert.Contains("HideButton.Visibility = ClickThrough ? Visibility.Hidden : Visibility.Visible", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NakladkaIJejMenuNieMajaKolorowWpisanychNaStale()
    {
        // każdy pędzel przycisku i menu to zasób motywu (DynamicResource)
        // albo wiązanie — domyślne menu WPF byłoby białe z systemowym cieniem także w ciemnym motywie.
        XDocument view = View();
        foreach (XAttribute brush in view.Descendants().Attributes()
                     .Where(a => a.Name.LocalName is "Background" or "BorderBrush" or "Foreground"))
        {
            Assert.True(brush.Value.StartsWith("{", StringComparison.Ordinal) || brush.Value == "Transparent",
                $"OverlayWindow.xaml: {brush.Name.LocalName}=\"{brush.Value}\" to kolor wpisany na stałe (wiersz {((IXmlLineInfo)brush).LineNumber}).");
        }

        Assert.Contains(view.Descendants(Xaml + "ControlTemplate"), t => t.Attribute("TargetType")?.Value == "ContextMenu");
        Assert.Contains(view.Descendants(Xaml + "ControlTemplate"), t => t.Attribute("TargetType")?.Value == "MenuItem");
    }
}
