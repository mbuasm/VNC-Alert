namespace MaszynyAlert.Probe.Commands;

internal static class SummaryPrinter
{
    public static void PrintAndSave(SummaryReport summary, Report report, string outDir)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);

        report.Line();
        report.Line("Podsumowanie (wszystkie fakty zebrane dotąd o maszynach w tym przebiegu):");
        List<string> lines = summary.Render().ToList();
        foreach (string line in lines)
        {
            report.Line(line);
        }

        try
        {
            Directory.CreateDirectory(outDir);
            string txtPath = Path.Combine(outDir, "podsumowanie.txt");
            string jsonPath = Path.Combine(outDir, "podsumowanie.json");
            File.WriteAllLines(txtPath, lines);
            File.WriteAllText(jsonPath, summary.RenderJson());

            report.Line();
            report.Line($"Zapisano też: {Path.GetFullPath(txtPath)} i {Path.GetFileName(jsonPath)} (do odczytu maszynowego, np. przez inne narzędzie).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Line($"Nie udało się zapisać plików podsumowania (podsumowanie.txt/.json): {ex.Message}");
        }
    }
}
