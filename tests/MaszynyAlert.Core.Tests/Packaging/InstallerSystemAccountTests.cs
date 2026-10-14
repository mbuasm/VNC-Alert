using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class InstallerSystemAccountTests
{
    private static string Iss => File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "installer", "MaszynyAlert.iss"));

    private static string RunningAsSystemBody()
    {
        string iss = Iss.Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = iss.IndexOf("function RunningAsSystem: Boolean;", StringComparison.Ordinal);
        Assert.True(start > 0, "Brak funkcji RunningAsSystem.");
        int end = iss.IndexOf("\nend;", start, StringComparison.Ordinal);
        return iss[start..end];
    }

    [Fact]
    public void RunningAsSystem_KontoNieustalone_PrzyjmujeSystem()
    {
        string body = RunningAsSystemBody();
        int assumeSystem = body.IndexOf("SystemSetup := True;", StringComparison.Ordinal);
        int whoami = body.IndexOf(@"ExecAndCaptureOutput(ExpandConstant('{sys}\whoami.exe')", StringComparison.Ordinal);
        Assert.True(assumeSystem > 0 && whoami > assumeSystem, "Przed odczytem whoami.exe konto ma być przyjęte jako SYSTEM.");
        Assert.DoesNotContain("SystemSetup := False;", body, StringComparison.Ordinal);
        // Wynik whoami liczy się tylko, gdy zawiera jakikolwiek SID; inaczej zostaje SYSTEM (z wpisem w logu instalatora).
        int anySid = body.IndexOf("'\"S-1-5-'", StringComparison.Ordinal);
        int systemSid = body.IndexOf("'\"S-1-5-18\"'", StringComparison.Ordinal);
        Assert.True(anySid > whoami && systemSid > anySid, "Rozstrzygnięcie „nie SYSTEM” tylko po odczytanym SID-zie.");
        Assert.Contains("przyjeto SYSTEM", body, StringComparison.Ordinal);
    }
}
