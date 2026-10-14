using System.Net;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Windows.Tests;

/// <summary>Podniesiona zmiana profilu sieci: tylko dla rzeczywistego interfejsu LAN, tylko z Publiczny na Prywatny,
/// z wpisem w dzienniku zmian; adres z poleceń jest danymi niezaufanymi.</summary>
public sealed class NetworkProfileHelperTests
{
    private static readonly LanInterface Lan = new("Ethernet", IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));

    [Fact]
    public void Script_ChangesOnlyPublicAndNeverRequiresTheFirewallService()
    {
        string script = NetworkProfileHelper.Script("192.168.1.10");
        Assert.Contains("if ($network.NetworkCategory -eq 'DomainAuthenticated') { 'UNCHANGED|Domain|'", script);
        Assert.Contains("if ($network.NetworkCategory -eq 'Public') { Set-NetConnectionProfile -InterfaceIndex $ip.InterfaceIndex -NetworkCategory Private;", script);
        Assert.Contains("-match 'niezident|unidentified'", script);
        Assert.Contains(NetworkProfileHelper.UnidentifiedNetworkPolicyKey, script);
        Assert.Contains("Set-ItemProperty -Path $key -Name Category -Value 1 -Type DWord", script);
        Assert.Contains("$policy='POLICY_SET'", script);
        Assert.DoesNotContain("MpsSvc", script);
        Assert.DoesNotContain("NetFirewallRule", script);
        Assert.Equal("SetPrivateProfile", GroupFirewall.StepName(script));
    }

    [Fact]
    public void RunHelper_RejectsAddressOutsideRealLanInterfacesBeforeAnyScript()
    {
        using var dir = new GroupHelperTestKit.TestDir();
        var scripts = new List<string>();
        int code = NetworkProfileHelper.RunHelper("10.9.9.9", dir.Path, [Lan], s => { scripts.Add(s); return "CHANGED"; });
        Assert.Equal(2, code);
        Assert.Empty(scripts);
        Assert.False(File.Exists(dir.File(NetworkProfileHelper.ChangeLogFileName)));
    }

    [Fact]
    public void RunHelper_ChangedProfile_IsLoggedForIt()
    {
        using var dir = new GroupHelperTestKit.TestDir();
        int code = NetworkProfileHelper.RunHelper("192.168.1.10", dir.Path, [Lan], _ => "CHANGED|Public->Private|POLICY_SET|Sieć niezidentyfikowana|Ethernet 2");
        Assert.Equal(0, code);
        string log = File.ReadAllText(dir.File(NetworkProfileHelper.ChangeLogFileName));
        Assert.Contains("address=192.168.1.10", log);
        Assert.Contains("Public->Private|POLICY_SET", log);
    }

    [Fact]
    public void RunHelper_AlreadyPrivate_ReportsNoChange()
    {
        using var dir = new GroupHelperTestKit.TestDir();
        Assert.Equal(3, NetworkProfileHelper.RunHelper("192.168.1.10", dir.Path, [Lan], _ => "UNCHANGED|Private|POLICY_NOT_NEEDED|Hala|Ethernet 2"));
        Assert.Equal(3, NetworkProfileHelper.RunHelper("192.168.1.10", dir.Path, [Lan], _ => "UNCHANGED|Private|POLICY_KEPT|Sieć niezidentyfikowana|Ethernet 2"));
    }

    [Fact]
    public void RunHelper_OnlyPolicyMadeDurable_CountsAsAChange()
    {
        using var dir = new GroupHelperTestKit.TestDir();
        Assert.Equal(0, NetworkProfileHelper.RunHelper("192.168.1.10", dir.Path, [Lan], _ => "UNCHANGED|Private|POLICY_SET|Sieć niezidentyfikowana|Ethernet 2"));
    }

    [Fact]
    public void RunHelper_PowerShellFailure_IsExitCodeOne()
    {
        using var dir = new GroupHelperTestKit.TestDir();
        Assert.Equal(1, NetworkProfileHelper.RunHelper("192.168.1.10", dir.Path, [Lan],
            _ => throw new GroupFirewallException("NO_PROFILE", "brak profilu")));
    }
}
