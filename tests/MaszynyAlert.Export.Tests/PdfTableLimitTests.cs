using System.Globalization;
using MaszynyAlert.Core.Export;
using static MaszynyAlert.Export.Tests.R3TestData;

namespace MaszynyAlert.Export.Tests;

public sealed class PdfTableLimitTests
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public void PonadLimit_NajnowszeWierszeNotaIRazemZCalosci()
    {
        int count = DowntimePdfExporter.MaxTableRows + 200;
        var start = At("2026-09-28T06:00:00Z");
        // 25 maszyn, postoje po 4 min co 10 min w dni robocze — bez nakładania na jednej maszynie.
        var episodes = Enumerable.Range(0, count).Select(i =>
        {
            int k = i / 25;
            return Ep("m" + (i % 25), "Błąd " + (i % 40), start.AddDays(k % 5).AddMinutes(k / 5 * 10).ToString("o"), 4, "inny");
        }).ToArray();
        var data = Data(episodes, Mon, Tue);
        Assert.Equal(count, data.Episodes.Count);

        using var pdf = PdfProbe.Write(data);
        string text = pdf.AllText();

        Assert.Contains($"Tabela pokazuje {DowntimePdfExporter.MaxTableRows} najnowszych z {count} postojów", text, StringComparison.Ordinal);
        Assert.Contains("Pełna lista jest w pliku Excel", text, StringComparison.Ordinal);
        // Najnowszy postój jest w tabeli, najstarszy — nie (tabela zaczyna się od pierwszego z najnowszych).
        var newest = data.Episodes[^1].Start.ToString("dd.MM.yyyy HH:mm", Pl);
        var oldest = data.Episodes[0].Start.ToString("dd.MM.yyyy HH:mm", Pl);
        Assert.Contains(newest, text, StringComparison.Ordinal);
        Assert.DoesNotContain(oldest, text, StringComparison.Ordinal);
        // „Razem” = wszystkie postoje, nie tylko pokazane.
        static string Plain(string value) => value.Replace(' ', ' ').Replace(' ', ' ');
        Assert.Contains(Plain(data.Totals.All.Minutes.ToString("N1", Pl)), Plain(text), StringComparison.Ordinal);
    }

    [Fact]
    public void WLimicie_BezNoty()
    {
        using var pdf = PdfProbe.Write(WeekData());
        Assert.DoesNotContain("najnowszych z", pdf.AllText(), StringComparison.Ordinal);
    }
}
