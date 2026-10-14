using System.Text.RegularExpressions;
using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class DowntimeReportScopeTests
{
    private const string ScopeHeading = "Dane lokalne tego komputera";

    private const string GroupScopeHeading = "Dane grupy komputerów";

    private static string ViewsDir =>
        Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", "Views");

    private static string ViewPath => Path.Combine(ViewsDir, "DowntimeView.xaml");

    private static string CodeBehindPath => ViewPath + ".cs";

    private static XDocument View() => XDocument.Load(ViewPath, LoadOptions.SetLineInfo);

    private static string CodeBehind() => File.ReadAllText(CodeBehindPath);

    /// <summary>Zarówno <c>x:Name</c>, jak i zwykłe <c>Name</c> dają kodowi dostęp do elementu.</summary>
    private static bool HasName(XElement element) =>
        element.Attributes().Any(a => a.Name.LocalName == "Name");

    private static string? TextOf(XElement element) => element.Attribute("Text")?.Value;

    /// <summary>Element z nagłówkiem informacji o zakresie raportu.</summary>
    private static XElement ScopeHeadingElement(XDocument view, string heading)
    {
        XElement[] matches = [.. view.Descendants().Where(e => TextOf(e)?.Contains(heading, StringComparison.Ordinal) == true)];

        Assert.True(matches.Length == 1,
            $"W DowntimeView.xaml oczekiwano dokładnie jednego stałego tekstu „{heading}”, znaleziono {matches.Length}. " +
            "Ta informacja musi być w widoku dokładnie raz, jako stały napis.");

        return matches[0];
    }

    private static XElement ScopeBox(XDocument view, string heading = ScopeHeading)
    {
        XElement? box = ScopeHeadingElement(view, heading).Ancestors().FirstOrDefault(a => a.Name.LocalName == "Border");
        Assert.NotNull(box);
        return box!;
    }

    private static string NameOf(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? string.Empty;

    [Theory]
    [InlineData(ScopeHeading)]
    [InlineData(GroupScopeHeading)]
    public void ZakresRaportu_JestStalymNapisemBezDostepuZKodu(string heading)
    {
        XDocument view = View();
        XElement box = ScopeBox(view, heading);

        // są DWA bloki zakresu i kod przełącza między nimi, więc sama ramka musi mieć
        // nazwę. Wszystko, co niesie tekst, nazwy mieć nie może: kod ma prawo wyłącznie pokazać albo
        // ukryć cały blok, nigdy podmienić jego treść. Jedyny wyjątek to jawnie zmienna lista
        // komputerów grupy, która z natury nie może stać na stałe w XAML.
        foreach (XElement element in box.DescendantsAndSelf())
        {
            bool isBoxItself = element == box;
            bool isVariableSourceList = NameOf(element) == "GroupScopeSources";
            if (isBoxItself || isVariableSourceList) continue;

            Assert.False(HasName(element),
                $"Element <{element.Name.LocalName}> w bloku „{heading}” ma nazwę dostępną z kodu " +
                "(x:Name/Name). Informacja o zakresie raportu ma być stała: żadna ścieżka " +
                "wykonania nie może jej nadpisać.");
        }

        // Żaden przodek nie ma Visibility: nie da się ukryć OBU bloków naraz, chowając ich wspólny
        // kontener. Sama ramka bloku jest jedynym miejscem, gdzie przełączanie jest dozwolone.
        foreach (XElement element in box.Ancestors())
        {
            Assert.Null(element.Attribute("Visibility"));
        }

        Assert.DoesNotContain(heading, CodeBehind(), StringComparison.Ordinal);
    }

    [Fact]
    public void ZakresRaportu_DokladnieJedenZDwochBlokowJestWidoczny()
    {
        XDocument view = View();
        string local = NameOf(ScopeBox(view, ScopeHeading));
        string group = NameOf(ScopeBox(view, GroupScopeHeading));

        Assert.Equal("LocalScopeBox", local);
        Assert.Equal("GroupScopeBox", group);

        string code = CodeBehind();

        // Oba bloki są przełączane jednym warunkiem i na krzyż — nie ma ścieżki, w której raport
        // pokazuje liczby bez ŻADNEJ informacji o tym, czyje to obserwacje.
        Assert.Matches(new Regex(@"LocalScopeBox\.Visibility\s*=\s*group\s*\?\s*Visibility\.Collapsed\s*:\s*Visibility\.Visible"), code);
        Assert.Matches(new Regex(@"GroupScopeBox\.Visibility\s*=\s*group\s*\?\s*Visibility\.Visible\s*:\s*Visibility\.Collapsed"), code);
        // Przełącznik liczy się z konfiguracji grupy, a nie np. z tego, czy w dzienniku są cudze wpisy:
        // komputer chwilowo odcięty od grupy dalej ma raport grupy i musi to mówić.
        Assert.Contains("GroupReportMode", code, StringComparison.Ordinal);
        // Odświeżanie widoku zawsze przechodzi przez przełącznik.
        Assert.Matches(new Regex(@"UpdateScope\(\);"), code);
    }

    [Fact]
    public void ZakresGrupy_MowiOSumieObserwacjiIOGranicachOkna()
    {
        string opis = string.Join(' ', ScopeBox(View(), GroupScopeHeading)
            .DescendantsAndSelf().Select(TextOf).Where(t => t is not null));

        Assert.Contains("wszystkie komputery grupy", opis, StringComparison.Ordinal);
        Assert.Contains("żaden komputer grupy nie obserwował", opis, StringComparison.Ordinal);
        Assert.Contains("hali", opis, StringComparison.Ordinal);
    }

    [Fact]
    public void ZakresGrupy_WymieniaKomputeryICzasUzupelnienia()
    {
        XElement box = ScopeBox(View(), GroupScopeHeading);

        // Lista „kto i kiedy uzupełnił”: bez niej operator
        // nie wie, czy pusty raport znaczy „nie było postoju”, czy „PC-C jeszcze nic nie przysłał”.
        Assert.Contains(box.Descendants(), e => NameOf(e) == "GroupScopeSources");

        string code = CodeBehind();
        Assert.Contains("GroupScopeSources.Text", code, StringComparison.Ordinal);
        Assert.Contains("GroupReportSourcesText", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ZakresRaportu_WyjasniaLukiIBrakHistoriiGrupy()
    {
        XElement box = ScopeBox(View());

        string opis = string.Join(' ', box.DescendantsAndSelf().Select(TextOf).Where(t => t is not null));

        // Sam nagłówek nie wystarczy: operator musi wiedzieć, czego w raporcie NIE ma.
        Assert.Contains("obserwował maszyny", opis, StringComparison.Ordinal);
        Assert.Contains("wyłączony", opis, StringComparison.Ordinal);
        Assert.Contains("grupy", opis, StringComparison.Ordinal);
        Assert.Contains("hali", opis, StringComparison.Ordinal);
    }

    [Fact]
    public void ZakresRaportu_StoiPrzedFiltramiIWynikami()
    {
        XElement box = ScopeBox(View());
        List<XElement> siblings = [.. box.Parent!.Elements()];

        int scope = siblings.IndexOf(box);
        int filters = siblings.FindIndex(e => e.Name.LocalName == "WrapPanel");
        int grids = siblings.FindIndex(e => e.DescendantsAndSelf().Any(d => d.Name.LocalName == "DataGrid"));

        Assert.True(filters > scope,
            "Informacja o lokalnym zakresie ma stać nad filtrami raportu; inaczej operator widzi ją " +
            "dopiero po przewinięciu, a wnioski wyciąga z liczb u góry.");
        Assert.True(grids > scope, "Informacja o lokalnym zakresie ma stać nad tabelami raportu.");
    }

    [Fact]
    public void OstrzezenieBiezace_JestOsobneOdInformacjiOZakresie()
    {
        XDocument view = View();
        XElement? warning = view.Descendants()
            .FirstOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "ReportWarning"));

        Assert.NotNull(warning);
        Assert.DoesNotContain(ScopeBox(view).DescendantsAndSelf(), e => e == warning);

        string code = CodeBehind();

        // Pole ostrzeżenia ma nieść wyłącznie sytuacje bieżące.
        Assert.DoesNotMatch(new Regex(@"Downtime\.Error\s*\?\?"), code);
        Assert.Contains("Downtime.Error", code, StringComparison.Ordinal);
        Assert.Contains("Data końcowa jest wcześniejsza niż początkowa", code, StringComparison.Ordinal);

        // Bez ostrzeżenia pole ma zniknąć, żeby pusta ramka nie wyglądała na zastrzeżenie.
        Assert.Matches(new Regex(@"ReportWarningBox\.Visibility\s*="), code);
        Assert.Contains("Visibility.Collapsed", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PustyRaport_MowiOBrakuWpisowLokalnych()
    {
        XElement? empty = View().Descendants()
            .FirstOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "EmptyText"));

        Assert.NotNull(empty);
        string text = TextOf(empty!) ?? string.Empty;

        Assert.Contains("lokalnych danych tego komputera", text, StringComparison.Ordinal);
        Assert.Contains("Nie znaczy to", text, StringComparison.Ordinal);
        Assert.Contains("nie obserwował", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Eksport_NazywaZakresRaportuWEtykiecieIWNazwiePliku()
    {
        XElement? button = View().Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button" && e.Attribute("Click")?.Value == "Export_Click");

        Assert.NotNull(button);
        // Domyślna (poza grupą) etykieta zostaje w XAML; tryb grupy podmienia ją w kodzie.
        Assert.Equal("Eksportuj lokalny raport CSV", button!.Attribute("Content")?.Value);

        string code = CodeBehind();
        Assert.Contains("Eksportuj raport grupy CSV", code, StringComparison.Ordinal);

        // Plik CSV bywa oglądany poza programem, bez panelu i bez informacji o zakresie, więc sama
        // nazwa pliku musi mówić, czyje to obserwacje — „lokalny” albo „grupa”.
        Match name = Regex.Match(code, @"FileName\s*=\s*\$""(?<name>[^""]*)""");
        Assert.True(name.Success, "Nie znaleziono domyślnej nazwy pliku eksportu w DowntimeView.xaml.cs.");
        Assert.Contains("{scope}", name.Groups["name"].Value, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"scope\s*=\s*[^;]*GroupReportMode\s*\?\s*""grupa""\s*:\s*""lokalny"""), code);
    }

    [Fact]
    public void PustyRaportGrupy_NieUdajeZeraPostojowWHali()
    {
        // Ten sam powód, co przy raporcie lokalnym: brak wierszy nie znaczy „nie było postoju”.
        // W trybie grupy prawdziwym powodem luki jest okres bez ŻADNEGO włączonego komputera.
        string code = CodeBehind();

        Assert.Contains("Nie znaczy to", code, StringComparison.Ordinal);
        Assert.Contains("żaden komputer grupy nie obserwował maszyn", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OperatorWindow.xaml")]
    [InlineData("OverlayWindow.xaml")]
    [InlineData("IdlePopupWindow.xaml")]
    public void RaportPostojow_NieMaGoPozaPanelemAdministracyjnym(string file)
    {
        string xaml = File.ReadAllText(Path.Combine(ViewsDir, file));

        foreach (string forbidden in new[] { "DowntimeView", "ReportHost", "Raport postojów" })
        {
            Assert.DoesNotContain(forbidden, xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RaportPostojow_JestOsadzonyWylacznieWPanelu()
    {
        // Druga strona tego samego zakazu: dokładnie jedno okno hostuje widok raportu i jest nim
        // okno Panelu administracyjnego.
        string[] windows = [.. Directory.EnumerateFiles(ViewsDir, "*.xaml")
            .Where(f => File.ReadAllText(f).Contains("ReportHost", StringComparison.Ordinal))
            .Select(Path.GetFileName)!];

        Assert.Equal(["StatusWindow.xaml"], windows);
    }

    [Fact]
    public void WskaznikUzupelnienia_JestWZakladcePracaNaKilkuKomputerach()
    {
        // kolumna „Raport uzupełniony” z czasem ostatniej odpowiedzi stoi w zakładce
        // „Praca na kilku komputerach”, a NIE w Podglądzie operatora — to wciąż Panel administracyjny.
        XDocument view = XDocument.Load(Path.Combine(ViewsDir, "MultiComputerView.xaml"), LoadOptions.SetLineInfo);

        XElement column = Assert.Single(view.Descendants()
            .Where(e => e.Attribute("Header")?.Value == "Raport uzupełniony"));

        Assert.Equal("{Binding ReportSyncedText}", column.Attribute("Binding")?.Value);
    }

    [Fact]
    public void Eksport_MaNaglowekZPolskimiZnakamiWUtf8ZBom()
    {
        string code = CodeBehind();

        Assert.Contains("\"Dzień zmiany;Zmiana;Maszyna;Powód;Od;Do;Minuty;Stan\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Dzien zmiany", code, StringComparison.Ordinal);
        Assert.Contains("new UTF8Encoding(true)", code, StringComparison.Ordinal);
    }
}
