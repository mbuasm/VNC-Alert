using System.Text.RegularExpressions;
using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class DowntimeReportR1aViewTests
{
    private static string Src(params string[] parts) => Path.Combine([TestPaths.RepositoryRoot, "src", .. parts]);

    private static string ViewXaml => File.ReadAllText(Src("MaszynyAlert.App", "Views", "DowntimeView.xaml"));

    private static string CodeBehind => File.ReadAllText(Src("MaszynyAlert.App", "Views", "DowntimeView.xaml.cs"));

    private static IEnumerable<string> UiTexts()
    {
        var view = XDocument.Parse(ViewXaml);
        foreach (var attribute in view.Descendants().Attributes().Where(a => a.Name.LocalName is "Text" or "Content" or "Header"))
            yield return attribute.Value;
        foreach (Match literal in Regex.Matches(CodeBehind, "\"(?:[^\"\\\\]|\\\\.)*\""))
            yield return literal.Value;
    }

    [Fact]
    public void WidokNieMaStarejRegulyGodziny()
    {
        foreach (string text in UiTexts())
        {
            Assert.DoesNotContain("nie dłuższ", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nie dłużej", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("od 3 minut do godziny", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nie wchodzi do raportu wcale", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WidokMowiOWeekendzieIDlugimPostoju()
    {
        string xaml = ViewXaml;
        Assert.Contains("od soboty 06:00 do poniedziałku 06:00", xaml, StringComparison.Ordinal);
        Assert.Contains("długi postój (> 1 h)", xaml, StringComparison.Ordinal);
        Assert.Contains("(weekend od soboty 06:00 do poniedziałku 06:00 nie wchodzi do raportu)", CodeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void KolumnaStan_TabelaIEksportUzywajaTegoSamegoOznaczenia()
    {
        string code = CodeBehind;
        Assert.Contains("State = StateText(s)", code, StringComparison.Ordinal);
        // Ostatnia komórka wiersza CSV to kolumna Stan — ta sama funkcja, co w tabeli.
        Assert.Matches(new Regex(@"s\.Minutes\.ToString\(""F2"".*?\),\s*StateText\(s\) \}\.Select\(Cell\)", RegexOptions.Singleline), code);
        Assert.Contains("private const string LongDowntimeMark = \"długi postój (> 1 h)\";", code, StringComparison.Ordinal);
        // Stare stany bez zmian (tylko z dopiskiem dla długich).
        Assert.Contains("\"Przerwana obserwacja\"", code, StringComparison.Ordinal);
        Assert.Contains("\"Otwarty epizod\"", code, StringComparison.Ordinal);
        Assert.Contains("\"Zakończony\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void KartySum_DwaWariantyZTotals()
    {
        var view = XDocument.Parse(ViewXaml);
        string[] names = [.. view.Descendants().Attributes().Where(a => a.Name.LocalName == "Name").Select(a => a.Value)];
        foreach (string name in new[] { "TotalTime", "TotalTimeWithoutLong", "TotalMinutes", "TotalMinutesWithoutLong", "EpisodeCount", "EpisodeCountLong" })
            Assert.Contains(name, names);

        string code = CodeBehind;
        Assert.Contains("DowntimeReport.Totals(_slices)", code, StringComparison.Ordinal);
        Assert.Contains("totals.WithoutLong.Minutes", code, StringComparison.Ordinal);
        Assert.Contains("new DowntimeCategoryClassifier(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("s.Machine == e.MachineName", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EpizodZSieci_NiesieRodzajBleduAlertu()
    {
        // AppOrchestrator.OnRemoteEpisodesReceived jest prywatne i wymaga pełnego DI — pilnujemy źródła, a regułę
        // zachowania sprawdza DowntimeSyncTests.CategoryOfTheObserversAlertReachesThePeersJournal.
        string code = File.ReadAllText(Src("MaszynyAlert.App", "Services", "AppOrchestrator.cs"));
        int start = code.IndexOf("private void OnRemoteEpisodesReceived(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = code[start..end];
        Assert.Contains("Downtime.Merge(DowntimeEpisode.FromAlert(alert, origin))", body, StringComparison.Ordinal);
        string journal = File.ReadAllText(Src("MaszynyAlert.Core", "History", "DowntimeJournal.cs"));
        int from = journal.IndexOf("public static DowntimeEpisode FromAlert(", StringComparison.Ordinal);
        Assert.True(from >= 0);
        string factory = journal[from..journal.IndexOf("\n    }", from, StringComparison.Ordinal)];
        Assert.Contains("Category: DowntimeCategories.Normalize(alert.Category)", factory, StringComparison.Ordinal);
    }
}
