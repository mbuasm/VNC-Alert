using System.Text.RegularExpressions;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class ReleaseWorkflowGateTests
{
    private static string Wydanie =>
        File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, ".github", "workflows", "wydanie.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Treść zadania (job) od linii „  nazwa:” do następnego zadania na tym samym wcięciu albo końca pliku.</summary>
    private static string Job(string workflow, string name)
    {
        Match start = Regex.Match(workflow, "^  " + Regex.Escape(name) + ":\n", RegexOptions.Multiline);
        Assert.True(start.Success, $"Brak zadania '{name}' w wydanie.yml.");
        Match next = Regex.Match(workflow[(start.Index + start.Length)..], @"^  [A-Za-z0-9_-]+:\n", RegexOptions.Multiline);
        return next.Success ? workflow.Substring(start.Index, start.Length + next.Index) : workflow[start.Index..];
    }

    [Fact]
    public void Publikacja_CzekaNaSprawdz_TylkoRecznieZMain()
    {
        string workflow = Wydanie;
        string wydanie = Job(workflow, "wydanie");
        Assert.Matches(new Regex(@"^    needs: sprawdz$", RegexOptions.Multiline), wydanie);
        Assert.Matches(new Regex(@"^    if: github\.event_name == 'workflow_dispatch' && github\.ref == 'refs/heads/main'$", RegexOptions.Multiline), wydanie);
        Assert.Contains("uses: mbuasm/serwer-laptop/.github/workflows/wydanie.yml@main", wydanie, StringComparison.Ordinal);
        Assert.Contains("instalator: installer/MaszynyAlert-wydanie.iss", wydanie, StringComparison.Ordinal);

        string[] lines = workflow.Split('\n');
        int onLine = Array.IndexOf(lines, "on:");
        Assert.True(onLine >= 0, "Brak sekcji on: w wydanie.yml.");
        string[] triggers = lines.Skip(onLine + 1)
            .TakeWhile(l => l.Length == 0 || l[0] == ' ' || l[0] == '#')
            .Where(l => Regex.IsMatch(l, @"^  [a-z_]+:"))
            .Select(l => l.Trim().TrimEnd(':'))
            .ToArray();
        Assert.Equal(new[] { "workflow_dispatch", "pull_request" }, triggers);
    }

    [Fact]
    public void Sprawdz_NieUkrywaPorazek_IdzieWspolnymUiSprawdz()
    {
        string workflow = Wydanie;
        // Zielony przebieg ma znaczyć „wszystkie kroki przeszły” — bez wyjątków na porażki.
        Assert.DoesNotContain("continue-on-error", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("upload-artifact", workflow, StringComparison.Ordinal);

        string sprawdz = Job(workflow, "sprawdz");
        Assert.Matches(new Regex(@"^    uses: mbuasm/serwer-laptop/\.github/workflows/ui-sprawdz\.yml@main$", RegexOptions.Multiline), sprawdz);
        Assert.Matches(new Regex(@"^    if: github\.event_name != 'pull_request' \|\| github\.event\.pull_request\.draft == false$", RegexOptions.Multiline), sprawdz);
        // Domyślnie hosted.
        Assert.Contains(@"runs-on: ${{ inputs.runs_on || vars.SPRAWDZ_RUNS_ON || '""windows-latest""' }}", sprawdz, StringComparison.Ordinal);
        Assert.DoesNotContain("steps:", sprawdz, StringComparison.Ordinal);
    }
}
