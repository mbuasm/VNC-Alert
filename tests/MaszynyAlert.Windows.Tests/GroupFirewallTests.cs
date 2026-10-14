using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Windows.Tests;

/// <summary>
/// Granica podwyższonych uprawnień: plik cofania i przechwycona reguła są danymi niezaufanymi.
/// Helper zapisuje regułę wyłącznie dla własnego EXE i adresów LAN, niezależnie od ich treści.
/// </summary>
public sealed class GroupFirewallTests
{
    private static readonly string Own = Path.Combine(Path.GetTempPath(), "MaszynyAlert.App.exe");

    private static (GroupFirewall Firewall, List<string> Scripts, Func<string> Output) Create(string output = "")
    {
        var scripts = new List<string>();
        string current = output;
        return (new GroupFirewall(Own, "MaszynyAlert.Test.Udp", s => { scripts.Add(s); return current; }), scripts, () => current);
    }

    [Fact]
    public void Restore_TamperedProgramPath_WritesRuleForOwnExecutableOnly()
    {
        var (firewall, scripts, _) = Create();
        firewall.Restore(new(@"C:\Users\ktos\evil.exe", 47777, "LocalSubnet", true));
        string script = Assert.Single(scripts);
        Assert.Contains("-Program " + GroupFirewall.Quote(Own), script);
        Assert.DoesNotContain("evil.exe", script);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("Internet")]
    [InlineData("LocalSubnet,Any")]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.1.0-192.168.1.255")]
    [InlineData("224.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    public void Restore_ForbiddenRemoteAddresses_RejectedBeforeAnyScriptRuns(string remote)
    {
        var (firewall, scripts, _) = Create();
        Assert.Throws<InvalidDataException>(() => firewall.Restore(new(Own, 47777, remote, true)));
        Assert.Empty(scripts);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("Any,Internet")]
    [InlineData("::")]
    public void Restore_ForbiddenLocalAddress_Rejected(string local)
    {
        var (firewall, scripts, _) = Create();
        Assert.Throws<InvalidDataException>(() => firewall.Restore(new(Own, 47777, "LocalSubnet", true, local)));
        Assert.Empty(scripts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Restore_InvalidPort_Rejected(int port)
    {
        var (firewall, scripts, _) = Create();
        Assert.Throws<InvalidDataException>(() => firewall.Restore(new(Own, port, "LocalSubnet", true)));
        Assert.Empty(scripts);
    }

    [Fact]
    public void Restore_LocalSubnetWithUnicastPeers_KeepsAddressesAndLocalAddress()
    {
        var (firewall, scripts, _) = Create();
        firewall.Restore(new(Own, 47777, "LocalSubnet, 192.168.2.7,192.168.2.7", false, "192.168.1.20"));
        string script = Assert.Single(scripts);
        Assert.Contains("-RemoteAddress 'LocalSubnet','192.168.2.7' ", script);
        Assert.Contains("-LocalAddress '192.168.1.20' -Enabled False", script);
        Assert.Contains("-LocalPort 47777 ", script);
    }

    [Fact]
    public void Restore_NoPreviousRule_RemovesOnlyOwnRule()
    {
        var (firewall, scripts, _) = Create();
        firewall.Restore(null);
        string script = Assert.Single(scripts);
        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.Contains("MaszynyAlert.Test.Udp", script);
        Assert.Contains("OWNERSHIP|", script);
    }

    [Fact]
    public void Capture_ManuallyBroadenedRule_IsRejectedInsteadOfBecomingTheUndoRecord()
    {
        var (firewall, _, _) = Create("""{"Program":"C:\\x\\a.exe","Port":47777,"RemoteAddresses":"Any","Enabled":true,"LocalAddress":"Any"}""");
        Assert.Throws<InvalidDataException>(() => firewall.Capture());
    }

    [Fact]
    public void Capture_OwnRule_NormalizesProgramToThisExecutable()
    {
        var (firewall, _, _) = Create("""{"Program":"C:\\Old\\MaszynyAlert.App.exe","Port":47777,"RemoteAddresses":"LocalSubnet","Enabled":true,"LocalAddress":"Any"}""");
        var captured = firewall.Capture();
        Assert.NotNull(captured);
        Assert.Equal(Path.GetFullPath(Own), captured!.Program);
        Assert.Equal("LocalSubnet", captured.RemoteAddresses);
        Assert.Equal("Any", captured.LocalAddress);
    }

    [Fact]
    public void EveryScript_ChecksFirewallServiceAndReportsReasonsUnwrapped()
    {
        var (firewall, scripts, _) = Create();
        firewall.Capture();
        firewall.Restore(null);
        firewall.Apply(new ClusterConfig { Enabled = false });
        Assert.Equal(3, scripts.Count);
        Assert.All(scripts, s =>
        {
            Assert.StartsWith("# VNC Alert firewall step: ", s);
            Assert.Contains("Get-Service -Name MpsSvc", s);
            Assert.Contains("SERVICE_DOWN|", s);
            Assert.Contains("Import-Module NetSecurity", s);
            Assert.Contains("[Console]::Error.WriteLine('VNCALERT|' + $_.Exception.Message)", s);
            Assert.Contains("exit 1", s);
        });
        Assert.Equal("Capture", GroupFirewall.StepName(scripts[0]));
        Assert.Equal("Remove", GroupFirewall.StepName(scripts[1]));
        Assert.Equal("Disable", GroupFirewall.StepName(scripts[2]));
    }

    [Fact]
    public void EveryScript_EndsWithExplicitExitZero_SoAMissingRuleDoesNotFailTheStep()
    {
        var (firewall, scripts, _) = Create();
        firewall.Capture();
        firewall.Restore(null);
        firewall.Restore(new(Own, 47777, "LocalSubnet", true));
        firewall.Apply(new ClusterConfig { Enabled = false });
        var readiness = new GroupFirewallReadiness(Own, "MaszynyAlert.Test.Udp", s => "").Script("192.168.1.10");
        var profile = NetworkProfileHelper.Script("192.168.1.10");
        Assert.Equal(4, scripts.Count);
        Assert.All(scripts.Concat([readiness, profile]), s =>
        {
            Assert.EndsWith("\nexit 0\n", s);
            int catchAt = s.LastIndexOf("} catch {", StringComparison.Ordinal);
            int exitOneAt = s.LastIndexOf("exit 1", StringComparison.Ordinal);
            Assert.True(catchAt >= 0 && exitOneAt > catchAt && s.LastIndexOf("exit 0", StringComparison.Ordinal) > exitOneAt,
                "exit 0 musi być poza try/catch, po exit 1 z bloku catch");
            Assert.Contains("$ProgressPreference='SilentlyContinue'", s);
            Assert.Contains("$ErrorActionPreference='Stop'", s);
        });
    }

    private const string ClixmlProgressOnly = "#< CLIXML\n"
        + "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\">"
        + "<T>System.Management.Automation.PSCustomObject</T><T>System.Object</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\">"
        + "<AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj>"
        + "<Obj S=\"progress\" RefId=\"1\"><TNRef RefId=\"0\" /><MS><I64 N=\"SourceId\">2</I64><PR N=\"Record\">"
        + "<AV>Get-NetFirewallRule -PolicyStore PersistentStore -Name $ruleId -ErrorAction SilentlyContinue</AV><AI>1417835956</AI><Nil />"
        + "<PI>-1</PI><PC>100</PC><T>Completed</T><SR>0</SR><SD>1/1 completed</SD></PR></MS></Obj></Objs>\n";

    [Fact]
    public void DescribeFailure_ClixmlProgressOnly_DoesNotQuoteTheXmlHeader()
    {
        var (code, message) = GroupFirewall.DescribeFailure(1, ClixmlProgressOnly);
        Assert.Equal("UNKNOWN", code);
        Assert.Contains("kodem 1", message);
        Assert.Contains("tylko komunikaty postępu", message);
        Assert.DoesNotContain("CLIXML", message);
        Assert.DoesNotContain("<Obj", message);
        Assert.Contains(GroupFirewall.ErrorLogFileName, message);
    }

    [Fact]
    public void DescribeFailure_ClixmlErrorRecord_DecodesTheErrorText()
    {
        string stderr = "#< CLIXML\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
            + "<Obj S=\"progress\" RefId=\"0\"><MS><PR N=\"Record\"><AV>Preparing modules for first use.</AV></PR></MS></Obj>"
            + "<S S=\"Error\">New-NetFirewallRule : Odmowa dostępu &lt;0x5&gt;_x000D__x000A_</S>"
            + "<S S=\"Error\">W wierszu:9 znak:1_x000D__x000A_</S></Objs>\n";
        var (code, message) = GroupFirewall.DescribeFailure(1, stderr);
        Assert.Equal("UNKNOWN", code);
        Assert.Contains("New-NetFirewallRule : Odmowa dostępu <0x5>", message);
        Assert.DoesNotContain("_x000D_", message);
        Assert.DoesNotContain("W wierszu", message);
        Assert.DoesNotContain("<S", message);
    }

    [Fact]
    public void DescribeFailure_MarkerNextToClixml_StillWinsOverTheXml()
    {
        var (code, message) = GroupFirewall.DescribeFailure(1, ClixmlProgressOnly + "VNCALERT|SERVICE_DOWN|Stopped\n");
        Assert.Equal("SERVICE_DOWN", code);
        Assert.Contains("MpsSvc", message);
    }

    [Fact]
    public void ApplyPreconditions_NameInterfaceNetworkAndPolicy()
    {
        string check = GroupFirewall.ProfileCheck("192.168.1.10");
        Assert.Contains("throw ('NO_IP|' + '192.168.1.10')", check);
        Assert.Contains("PUBLIC_PROFILE|' + $ip.InterfaceAlias + '|' + '192.168.1.10' + '|' + $network.Name", check);
        Assert.Contains("NO_PROFILE|", check);
        Assert.Contains("POLICY_BLOCK|", check);
        Assert.Contains("-Name Domain,Private", check);
    }

    [Theory]
    [InlineData("VNCALERT|PUBLIC_PROFILE|Ethernet|192.168.1.10|Sieć niezidentyfikowana", "PUBLIC_PROFILE", "Publiczny", "secpol.msc")]
    [InlineData("VNCALERT|PUBLIC_PROFILE|Ethernet|192.168.1.10|Hala", "PUBLIC_PROFILE", "Ustaw profil Prywatny", "Ethernet (192.168.1.10)")]
    [InlineData("VNCALERT|SERVICE_DOWN|Stopped", "SERVICE_DOWN", "MpsSvc", "Stopped")]
    [InlineData("VNCALERT|POLICY_BLOCK|Private", "POLICY_BLOCK", "GPO/MDM", "Eksportuj instrukcję dla IT")]
    [InlineData("VNCALERT|OWNERSHIP|Inna|Reguła obca", "OWNERSHIP", "nie należy do VNC Alert", "Inna")]
    [InlineData("VNCALERT|RULE_SHAPE|Outbound Allow TCP Public", "RULE_SHAPE", "nieoczekiwany kształt", "Outbound Allow TCP Public")]
    [InlineData("VNCALERT|NOT_EFFECTIVE|", "NOT_EFFECTIVE", "nie jest widoczna", "zasady")]
    [InlineData("VNCALERT|NO_IP|192.168.1.10", "NO_IP", "192.168.1.10", "nie jest już przypisany")]
    [InlineData("VNCALERT|NO_PROFILE|Ethernet|192.168.1.10", "NO_PROFILE", "Ethernet", "profilu sieci")]
    public void DescribeFailure_MapsScriptReasonsToOperatorSentences(string stderr, string code, string first, string second)
    {
        var (actualCode, message) = GroupFirewall.DescribeFailure(1, "Some noise\n  " + stderr + "\n");
        Assert.Equal(code, actualCode);
        Assert.Contains(first, message);
        Assert.Contains(second, message);
        Assert.DoesNotContain("VNCALERT", message);
    }

    [Fact]
    public void DescribeFailure_UnknownCmdletError_KeepsItsFirstLineAndPointsToTheLog()
    {
        var (code, message) = GroupFirewall.DescribeFailure(1, "VNCALERT|Nie można odnaleźć obiektu MSFT_NetFirewallRule.\nW wierszu:3 znak:1\n");
        Assert.Equal("UNKNOWN", code);
        Assert.Contains("Nie można odnaleźć obiektu MSFT_NetFirewallRule.", message);
        Assert.Contains(GroupFirewall.ErrorLogFileName, message);
    }

    [Fact]
    public void DescribeFailure_NoMarker_UsesFirstNonEmptyLineAndExitCode()
    {
        var (code, message) = GroupFirewall.DescribeFailure(-1073741510, "\n   \nAccess is denied.\nAt line:1 char:1\n");
        Assert.Equal("UNKNOWN", code);
        Assert.Contains("kodem -1073741510", message);
        Assert.Contains("Access is denied.", message);
    }

    [Fact]
    public void DescribeFailure_EmptyOutput_StillProducesASentence()
    {
        var (code, message) = GroupFirewall.DescribeFailure(1, "");
        Assert.Equal("UNKNOWN", code);
        Assert.Contains("brak treści błędu", message);
    }
}
