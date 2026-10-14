using System.Net;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Windows.Tests;

/// <summary>
/// Sprawdzenie gotowości zapory przed UAC: tylko odczyt, każda nieznana odpowiedź jest „nieznana”, nigdy „ok”,
/// a profil Publiczny i zatrzymana usługa są nazwane po polsku razem z tym, co operator ma zrobić.
/// </summary>
public sealed class GroupFirewallReadinessTests
{
    private static readonly string Own = Path.Combine(Path.GetTempPath(), "MaszynyAlert.App.exe");
    private static readonly LanInterface Lan = new("Ethernet", IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));

    private static (GroupFirewallReadiness Readiness, List<string> Scripts) Create(string output)
    {
        var scripts = new List<string>();
        return (new GroupFirewallReadiness(Own, "MaszynyAlert.Test.Udp", s => { scripts.Add(s); return output; }), scripts);
    }

    private static string Json(string service = "Running", string category = "Public", string network = "Sieć niezidentyfikowana",
        string allowLocal = "NotConfigured", string allowInbound = "NotConfigured", string enabled = "True",
        bool ruleExists = false, string ruleGroup = "", string policy = "") =>
        $$"""
        {"ServiceStatus":"{{service}}","ServiceStartType":"Automatic","InterfaceAlias":"Ethernet 2","NetworkName":"{{network}}","NetworkCategory":"{{category}}","UnidentifiedPolicy":"{{policy}}",
         "Profiles":[{"Name":"Domain","Enabled":"{{enabled}}","AllowLocalFirewallRules":"{{allowLocal}}","AllowInboundRules":"{{allowInbound}}"},
                     {"Name":"Private","Enabled":"{{enabled}}","AllowLocalFirewallRules":"{{allowLocal}}","AllowInboundRules":"{{allowInbound}}"},
                     {"Name":"Public","Enabled":"{{enabled}}","AllowLocalFirewallRules":"False","AllowInboundRules":"False"}],
         "RuleExists":{{(ruleExists ? "true" : "false")}},"RuleGroup":"{{ruleGroup}}","RuleEnabled":"True"}
        """;

    private static FirewallReadinessItem Item(FirewallReadinessReport report, string key) => Assert.Single(report.Items, i => i.Key == key);

    [Fact]
    public void PublicUnidentifiedNetwork_IsAProblemWithPrivateProfileOfferAndSecpolHint()
    {
        var (readiness, scripts) = Create(Json());
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        string script = Assert.Single(scripts);
        Assert.DoesNotContain("New-NetFirewallRule", script);
        Assert.DoesNotContain("Set-NetFirewallRule", script);
        Assert.DoesNotContain("Set-NetConnectionProfile", script);
        Assert.Contains("-IPAddress '192.168.1.10'", script);
        Assert.True(report.HasProblems);
        Assert.True(report.CanOfferPrivateProfile);
        Assert.True(report.IsUnidentifiedNetwork);
        Assert.Equal("192.168.1.10", report.Address);
        Assert.Equal("Ethernet 2", report.InterfaceAlias);
        var profile = Item(report, "profile");
        Assert.Equal(FirewallReadinessState.Problem, profile.State);
        Assert.Contains("Publiczny", profile.Result);
        Assert.Contains("zasadę lokalną", profile.Detail);
        Assert.False(report.UnidentifiedPolicyPrivate);
        Assert.DoesNotContain(report.Items, i => i.Key == "policy-unidentified");
        Assert.Equal(FirewallReadinessState.Ok, Item(report, "service").State);
        Assert.Equal(FirewallReadinessState.Ok, Item(report, "policy").State);
        Assert.Equal(FirewallReadinessState.Ok, Item(report, "rule").State);
    }

    [Fact]
    public void PrivateUnidentifiedNetworkWithoutPolicy_IsAWarningAndStillOffersTheDurableFix()
    {
        var (readiness, _) = Create(Json(category: "Private"));
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        Assert.False(report.HasProblems);
        Assert.True(report.HasWarnings);
        Assert.True(report.CanOfferPrivateProfile);
        Assert.False(report.UnidentifiedPolicyPrivate);
        var policy = Item(report, "policy-unidentified");
        Assert.Equal(FirewallReadinessState.Warning, policy.State);
        Assert.Contains("restarcie", policy.Detail);
    }

    [Fact]
    public void PrivateUnidentifiedNetworkWithPolicy_IsFullyReady()
    {
        var (readiness, scripts) = Create(Json(category: "Private", policy: "1"));
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        Assert.Contains(NetworkProfileHelper.UnidentifiedNetworkPolicyKey, Assert.Single(scripts));
        Assert.False(report.HasProblems);
        Assert.False(report.HasWarnings);
        Assert.False(report.CanOfferPrivateProfile);
        Assert.True(report.UnidentifiedPolicyPrivate);
        Assert.Equal(FirewallReadinessState.Ok, Item(report, "policy-unidentified").State);
    }

    [Fact]
    public void PrivateNetworkWithFirewallSwitchedOff_IsReadyAndSaysTheRuleStillGetsWritten()
    {
        var (readiness, _) = Create(Json(category: "Private", network: "Hala", enabled: "False"));
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        Assert.False(report.HasProblems);
        Assert.False(report.HasUnknown);
        Assert.False(report.HasWarnings);
        Assert.False(report.CanOfferPrivateProfile);
        Assert.Null(report.UnidentifiedPolicyPrivate);
        Assert.DoesNotContain(report.Items, i => i.Key == "policy-unidentified");
        Assert.Contains("Prywatny", Item(report, "profile").Result);
        Assert.Contains("wyłączona na profilu", Item(report, "policy").Detail);
    }

    [Fact]
    public void StoppedFirewallService_IsAProblemAndLeavesPolicyAndRuleUnknown()
    {
        var (readiness, _) = Create(Json(service: "Stopped", category: "Private"));
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        Assert.True(report.HasProblems);
        var service = Item(report, "service");
        Assert.Equal(FirewallReadinessState.Problem, service.State);
        Assert.Contains("Stopped", service.Result);
        Assert.Contains("MpsSvc", service.Detail);
        Assert.Equal(FirewallReadinessState.Unknown, Item(report, "policy").State);
        Assert.Equal(FirewallReadinessState.Unknown, Item(report, "rule").State);
    }

    [Fact]
    public void PolicyBlockingLocalRules_IsAProblemPointingToIt()
    {
        var (readiness, _) = Create(Json(category: "Domain", allowLocal: "False"));
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        var policy = Item(report, "policy");
        Assert.Equal(FirewallReadinessState.Problem, policy.State);
        Assert.Contains("Domena", policy.Result);
        Assert.Contains("Prywatny", policy.Result);
        Assert.Contains("IT", policy.Detail);
        Assert.False(report.CanOfferPrivateProfile);
    }

    [Fact]
    public void ForeignRuleOfSameName_IsAProblem_OwnRuleIsOk()
    {
        var (foreign, _) = Create(Json(category: "Private", ruleExists: true, ruleGroup: "Inna"));
        Assert.Equal(FirewallReadinessState.Problem, Item(foreign.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]), "rule").State);

        var (own, _) = Create(Json(category: "Private", ruleExists: true, ruleGroup: GroupFirewall.OwnerGroup));
        var rule = Item(own.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]), "rule");
        Assert.Equal(FirewallReadinessState.Ok, rule.State);
        Assert.Contains("Istnieje", rule.Result);
    }

    [Fact]
    public void TwoInterfacesWithoutSavedAddress_ListsCandidatesAndAsksForAChoice()
    {
        var wifi = new LanInterface("Wi-Fi", IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.0"));
        var (readiness, scripts) = Create(Json());
        var report = readiness.Check(new ClusterConfig(), [wifi, Lan]);

        Assert.Empty(scripts);
        Assert.Null(report.Address);
        Assert.Equal(2, report.Candidates.Count);
        Assert.Equal("Ethernet — 192.168.1.10", report.Candidates[1].Display);
        Assert.Contains("Wybierz interfejs", Item(report, "interface").Detail);

        var chosen = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [wifi, Lan]);
        Assert.Equal("192.168.1.10", chosen.Address);
        Assert.Equal(2, chosen.Candidates.Count);
        Assert.Single(scripts);
    }

    [Fact]
    public void NoLanInterface_IsAProblemWithoutRunningPowerShell()
    {
        var (readiness, scripts) = Create(Json());
        var report = readiness.Check(new ClusterConfig(), []);

        Assert.Empty(scripts);
        Assert.True(report.HasProblems);
        Assert.Null(report.Address);
        Assert.False(report.CanOfferPrivateProfile);
        Assert.Contains("Brak aktywnego interfejsu LAN", Item(report, "interface").Detail);
    }

    [Fact]
    public void FailedRead_IsUnknownNotOkAndDoesNotBlockTheApply()
    {
        var readiness = new GroupFirewallReadiness(Own, "MaszynyAlert.Test.Udp",
            _ => throw new GroupFirewallException("UNKNOWN", "PowerShell nie odpowiedział."));
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);

        Assert.False(report.HasProblems);
        Assert.True(report.HasUnknown);
        var read = Item(report, "read");
        Assert.Contains("PowerShell nie odpowiedział.", read.Detail);
        Assert.Contains("nadal jest możliwe", read.Detail);
    }

    [Fact]
    public void GarbageOutput_IsUnknown()
    {
        var (readiness, _) = Create("not json");
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10" }, [Lan]);
        Assert.True(report.HasUnknown);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public void ItInstruction_NamesTheExactRuleAndFindingsWithoutSecrets()
    {
        var (readiness, _) = Create(Json());
        var report = readiness.Check(new ClusterConfig { LocalAddress = "192.168.1.10", UdpPort = 47777 }, [Lan]);
        string text = report.BuildItInstruction(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("New-NetFirewallRule", text);
        Assert.Contains("-LocalPort 47777", text);
        Assert.Contains("-Program " + GroupFirewall.Quote(Path.GetFullPath(Own)), text);
        Assert.Contains("-RemoteAddress 'LocalSubnet'", text);
        Assert.Contains("-Profile Domain,Private", text);
        Assert.Contains("Publiczny", text);
        Assert.Contains("secpol.msc", text);
        Assert.Contains("MpsSvc", text);
        Assert.DoesNotContain("NetworkKey", text);
    }
}
