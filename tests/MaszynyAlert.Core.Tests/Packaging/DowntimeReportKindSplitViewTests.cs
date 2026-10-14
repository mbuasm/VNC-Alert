using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class DowntimeReportKindSplitViewTests
{
    private static string Src(params string[] parts) => Path.Combine([TestPaths.RepositoryRoot, "src", .. parts]);

    private static XDocument ViewXaml => XDocument.Load(Src("MaszynyAlert.App", "Views", "DowntimeView.xaml"));

    private static string CodeBehind => File.ReadAllText(Src("MaszynyAlert.App", "Views", "DowntimeView.xaml.cs"));

    private static XElement Named(XDocument view, string name) =>
        view.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));

    [Fact]
    public void Przelacznik_StanWidokuDomyslnieWlaczony_LiczyWykresyOdRazu()
    {
        var box = Named(ViewXaml, "IncludeIdleCharts");
        Assert.Equal("CheckBox", box.Name.LocalName);
        Assert.Equal("Pokaż postoje bez pracy na wykresach", box.Attribute("Content")?.Value);
        Assert.Equal("True", box.Attribute("IsChecked")?.Value);
        Assert.Equal("IncludeIdleCharts_Changed", box.Attribute("Checked")?.Value);
        Assert.Equal("IncludeIdleCharts_Changed", box.Attribute("Unchecked")?.Value);

        string code = CodeBehind;
        Assert.Contains("IncludeIdle = includeIdle", code, StringComparison.Ordinal);
        Assert.Contains("includeIdle: IncludeIdleCharts.IsChecked != false", code, StringComparison.Ordinal);
        Assert.Contains("private void IncludeIdleCharts_Changed(object sender, RoutedEventArgs e) => Refresh();", code, StringComparison.Ordinal);
        // Stan widoku, nie konfiguracja wspólna ani lokalny plik ustawień.
        Assert.DoesNotContain("IncludeIdle", File.ReadAllText(Src("MaszynyAlert.Core", "Config", "UiConfig.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsStore", code, StringComparison.Ordinal);
    }

    [Fact]
    public void KonstruktorNieRzuca_RefreshCzekaNaInitializeComponent()
    {
        string code = CodeBehind;
        Assert.Contains("private readonly bool _initialized;", code, StringComparison.Ordinal);
        Assert.Contains("if (SummaryGrid is null || !_initialized) return;", code, StringComparison.Ordinal);
        int init = code.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        int flag = code.IndexOf("_initialized = true;", StringComparison.Ordinal);
        int refresh = code.IndexOf("Refresh();", flag, StringComparison.Ordinal);
        Assert.True(init > 0 && flag > init && refresh > flag, "kolejność: InitializeComponent → _initialized = true → Refresh()");
    }

    [Fact]
    public void PiecWykresowZJednegoModelu_KartyWTymBledy()
    {
        var view = ViewXaml;
        foreach (string name in new[] { "MachinesChart", "KindsChart", "ErrorsChart", "ErrorMachinesChart", "DaysChart" })
            Assert.NotNull(Named(view, name));
        foreach (string name in new[] { "TotalTimeByKind", "TotalMinutesByKind", "EpisodeCountByKind" })
            Assert.Equal("TextBlock", Named(view, name).Name.LocalName);
        Assert.Contains(view.Descendants().Attributes().Where(a => a.Name.LocalName == "Header"), a => a.Value == "W tym błędy maszyn [min]");

        string code = CodeBehind;
        Assert.Contains("var kinds = DowntimeReport.ByKind(_slices);", code, StringComparison.Ordinal);
        Assert.Contains("KindsChart.Data = charts.Kinds;", code, StringComparison.Ordinal);
        Assert.Contains("ErrorMachinesChart.Data = charts.ErrorMachines;", code, StringComparison.Ordinal);
        // Jedno wywołanie modelu — żadnego drugiego liczenia wykresów błędów w widoku.
        Assert.Equal(1, code.Split("DowntimeCharts.Build(").Length - 1);
    }
}
