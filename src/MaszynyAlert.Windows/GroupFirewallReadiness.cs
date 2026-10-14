using System.Text.Json;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Windows;

public enum FirewallReadinessState { Ok, Warning, Problem, Unknown }

/// <summary>One line of the pre-UAC check. <see cref="Key"/> is stable for tests and the IT instruction.</summary>
public sealed record FirewallReadinessItem(string Key, string Label, FirewallReadinessState State, string Result, string Detail);

/// <summary>Read-only picture of what the elevated firewall step will meet, taken without UAC. It never proves the
/// write will succeed (the helper checks again, transactionally); it only names problems before the prompt.</summary>
/// <summary>One real LAN interface this computer could use for the group (Ethernet or Wi‑Fi, up, IPv4).</summary>
public sealed record LanInterfaceChoice(string Name, string Address)
{
    public string Display => $"{Name} — {Address}";
}

public sealed record FirewallReadinessReport(IReadOnlyList<FirewallReadinessItem> Items, string? InterfaceAlias, string? Address,
    string? NetworkName, string? NetworkCategory, int UdpPort, string Program)
{
    /// <summary>Every candidate interface, so the wizard can offer the choice when none or more than one qualifies.</summary>
    public IReadOnlyList<LanInterfaceChoice> Candidates { get; init; } = [];
    public bool HasProblems => Items.Any(i => i.State == FirewallReadinessState.Problem);
    public bool HasUnknown => Items.Any(i => i.State == FirewallReadinessState.Unknown);
    public bool HasWarnings => Items.Any(i => i.State == FirewallReadinessState.Warning);
    /// <summary>Whether the local policy for unidentified networks already says Private; null when not read.</summary>
    public bool? UnidentifiedPolicyPrivate { get; init; }
    /// <summary>The explicit, operator-triggered change is offered for a real LAN interface that Windows classifies
    /// as Public, and for an unidentified network whose Private profile is not yet made durable by the local policy;
    /// never for a domain network or an unknown state.</summary>
    public bool CanOfferPrivateProfile => Address is not null
        && (NetworkCategory == "Public" || (NetworkCategory == "Private" && IsUnidentifiedNetwork && UnidentifiedPolicyPrivate == false));
    public bool IsUnidentifiedNetwork => NetworkName is not null && GroupFirewall.IsUnidentifiedNetworkName(NetworkName);

    /// <summary>Text for the IT department: the exact rule, what was found and what the organization must allow.
    /// No secrets: program path, port, interface, addresses and Windows policy names only.</summary>
    public string BuildItInstruction(DateTimeOffset now)
    {
        var rule = new GroupFirewallRule(Program, UdpPort, "LocalSubnet", true);
        var lines = new List<string>
        {
            "VNC Alert — instrukcja dla IT: reguła zapory dla pracy na kilku komputerach",
            $"Wygenerowano: {now:yyyy-MM-dd HH:mm} na komputerze {Environment.MachineName}",
            "",
            "Czego potrzebuje aplikacja:",
            $"- jedna reguła przychodząca UDP, port {UdpPort}, tylko dla programu {Program},",
            "- zdalne adresy: LocalSubnet (ta sama podsieć) lub jawne adresy IPv4 drugiego komputera,",
            "- profile Domena i Prywatny; profil Publiczny nie jest otwierany,",
            $"- nazwa reguły: {GroupFirewall.RuleId}, grupa: {GroupFirewall.OwnerGroup}.",
            "",
            "Równoważne polecenie PowerShell (administrator):",
            "New-NetFirewallRule -PolicyStore PersistentStore -Name '" + GroupFirewall.RuleId + "' -DisplayName 'VNC Alert — grupa komputerów' -Group '"
                + GroupFirewall.OwnerGroup + "'" + GroupFirewall.RuleFields(rule),
            "",
            "Stan odczytany na tym komputerze (bez uprawnień administratora):",
        };
        foreach (var item in Items)
            lines.Add($"- {item.Label}: {item.Result}" + (item.State == FirewallReadinessState.Ok ? "" : $" — {item.Detail}"));
        lines.Add("");
        lines.Add("Co może wymagać decyzji IT:");
        lines.Add("- profil sieci interfejsu do maszyn powinien być Prywatny lub Domena (aplikacja nie zmienia go bez polecenia operatora);");
        lines.Add("- dla sieci niezidentyfikowanej (bez bramy): zasada lokalna secpol.msc → Zasady Menedżera listy sieci → Sieci niezidentyfikowane → Prywatna;");
        lines.Add("- zasady GPO/MDM „Zastosuj lokalne reguły zapory” i „Zastosuj lokalne reguły połączeń” muszą pozwalać na reguły lokalne, "
            + "albo organizacja wdraża powyższą regułę centralnie;");
        lines.Add("- usługa Zapora Windows Defender (MpsSvc) musi działać, także gdy zapora jest wyłączona na profilu.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

/// <summary>What the application found about the group interface when it started with the group enabled. Read
/// once, without UAC, so a profile that fell back to Public after a restart is named in the panel instead of
/// showing up only as "waiting for the second computer".</summary>
public sealed record FirewallStartupCheck(bool Problem, string Text, DateTimeOffset At)
{
    public static FirewallStartupCheck From(FirewallReadinessReport report, DateTimeOffset at)
    {
        if (report.HasProblems)
        {
            var problems = report.Items.Where(i => i.State == FirewallReadinessState.Problem).Select(i => $"{i.Label}: {i.Result}.");
            return new(true, "Odczyt przy starcie: " + string.Join(" ", problems)
                + " Reguła zapory VNC Alert nie zadziała na tym interfejsie, więc drugi komputer nie będzie widoczny. "
                + "Otwórz Utwórz grupę lub Dołącz do grupy — blok gotowości pokazuje przycisk Ustaw profil Prywatny (UAC) i szczegóły.", at);
        }
        if (report.HasWarnings)
            return new(false, "Odczyt przy starcie: profil sieci pozwala na regułę, ale bez zasady lokalnej dla sieci niezidentyfikowanych "
                + "może wrócić do Publicznego po kolejnym restarcie. Blok gotowości w kreatorze ustawia tę zasadę.", at);
        if (report.HasUnknown)
            return new(false, "Odczyt przy starcie nie objął wszystkich warunków (usługa zapory lub odczyt niedostępne).", at);
        return new(false, "Odczyt przy starcie: profil sieci, usługa zapory, zasady i reguła bez zastrzeżeń.", at);
    }
}

/// <summary>The non-elevated reading behind the "readiness" block on the create and join steps. Runs one read-only
/// PowerShell script as the operator; nothing here writes, and a failed read is reported as unknown, never as ok.</summary>
public sealed class GroupFirewallReadiness
{
    private readonly string _program;
    private readonly string _ruleId;
    private readonly Func<string, string> _execute;
    public GroupFirewallReadiness(string program) : this(program, GroupFirewall.RuleId, script => GroupFirewall.Execute(script, null)) { }
    internal GroupFirewallReadiness(string program, string ruleId, Func<string, string> execute)
    { _program = Path.GetFullPath(program); _ruleId = ruleId; _execute = execute; }

    public FirewallReadinessReport Check(ClusterConfig cluster) => Check(cluster, null);

    internal FirewallReadinessReport Check(ClusterConfig cluster, IReadOnlyList<LanInterface>? interfaces)
    {
        LanInterface? lan = null; string? lanError = null;
        IReadOnlyList<LanInterface> available;
        try { available = interfaces ?? LanInterface.Available(); }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException) { available = []; lanError = ex.Message; }
        try { lan = LanInterface.Select(cluster.LocalAddress, available); }
        catch (InvalidOperationException ex) { lanError = ex.Message; }
        var candidates = available.Select(a => new LanInterfaceChoice(a.Name, a.Address.ToString())).ToList();
        Snapshot? snapshot = null; string? readError = null;
        if (lan is not null)
        {
            try
            {
                string json = _execute(Script(lan.Address.ToString()));
                snapshot = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Snapshot>(json);
                if (snapshot is null) readError = "Odczyt nie zwrócił danych.";
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception) { readError = ex.Message; }
        }
        return Evaluate(lan, lanError, snapshot, readError, cluster.UdpPort) with { Candidates = candidates };
    }

    /// <summary>Read-only script; like <see cref="GroupFirewall.Wrap"/> it ends with an explicit <c>exit 0</c>, so the
    /// exit code never depends on <c>$?</c> of the last cmdlet.</summary>
    internal string Script(string address) => "# VNC Alert firewall step: Readiness\n" + GroupFirewall.Prologue + "try {\n"
        + "$svc=Get-Service -Name MpsSvc -ErrorAction SilentlyContinue\n"
        + "$ip=Get-NetIPAddress -AddressFamily IPv4 -IPAddress " + GroupFirewall.Quote(address) + " -ErrorAction SilentlyContinue | Select-Object -First 1\n"
        + "$net=$null\nif ($ip) { $net=Get-NetConnectionProfile -InterfaceIndex $ip.InterfaceIndex -ErrorAction SilentlyContinue | Select-Object -First 1 }\n"
        + "$policy=(Get-ItemProperty -Path " + GroupFirewall.Quote(NetworkProfileHelper.UnidentifiedNetworkPolicyKey) + " -Name Category -ErrorAction SilentlyContinue).Category\n"
        + "$profiles=@()\n$r=$null\nif ($svc -and $svc.Status -eq 'Running') {\n"
        + "  $profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { @{ Name=[string]$_.Name; Enabled=[string]$_.Enabled; AllowLocalFirewallRules=[string]$_.AllowLocalFirewallRules; AllowInboundRules=[string]$_.AllowInboundRules } })\n"
        + "  $r=Get-NetFirewallRule -PolicyStore PersistentStore -Name " + GroupFirewall.Quote(_ruleId) + " -ErrorAction SilentlyContinue\n}\n"
        + "@{ ServiceStatus=$(if ($svc) { [string]$svc.Status } else { '' }); ServiceStartType=$(if ($svc) { [string]$svc.StartType } else { '' });"
        + " InterfaceAlias=$(if ($ip) { [string]$ip.InterfaceAlias } else { '' }); NetworkName=$(if ($net) { [string]$net.Name } else { '' });"
        + " NetworkCategory=$(if ($net) { [string]$net.NetworkCategory } else { '' }); UnidentifiedPolicy=$(if ($null -ne $policy) { [string]$policy } else { '' }); Profiles=[object[]]$profiles;"
        + " RuleExists=[bool]$r; RuleGroup=$(if ($r) { [string]$r.Group } else { '' }); RuleEnabled=$(if ($r) { [string]$r.Enabled } else { '' }) } | ConvertTo-Json -Compress -Depth 4\n"
        + "} catch {\n[Console]::Error.WriteLine('" + GroupFirewall.Marker + "' + $_.Exception.Message)\nexit 1\n}\nexit 0\n";

    internal FirewallReadinessReport Evaluate(LanInterface? lan, string? lanError, Snapshot? s, string? readError, int udpPort)
    {
        var items = new List<FirewallReadinessItem>();
        string? address = lan?.Address.ToString();
        if (lan is null)
        {
            items.Add(new("interface", "Interfejs LAN", FirewallReadinessState.Problem, "Nie wybrano",
                (lanError ?? "Brak interfejsu.") + " Wybierz interfejs do maszyn z listy powyżej; wybór zapisze się razem z ustawieniami grupy."));
            return new(items, null, null, null, null, udpPort, _program);
        }
        items.Add(new("interface", "Interfejs LAN", FirewallReadinessState.Ok, $"{lan.Name} ({address})", "Interfejs, na którym komputery będą się widzieć."));
        if (s is null)
        {
            items.Add(new("read", "Odczyt stanu zapory", FirewallReadinessState.Unknown, "Nie udało się",
                (readError ?? "Brak danych.") + " Utwórz i włącz nadal jest możliwe; helper sprawdzi warunki ponownie."));
            return new(items, lan.Name, address, null, null, udpPort, _program);
        }

        bool serviceOk = s.ServiceStatus == "Running";
        items.Add(serviceOk
            ? new("service", "Usługa Zapora Windows Defender", FirewallReadinessState.Ok, "Działa", "MpsSvc działa; reguły można zapisywać.")
            : new("service", "Usługa Zapora Windows Defender", FirewallReadinessState.Problem,
                s.ServiceStatus.Length == 0 ? "Brak usługi" : $"Nie działa ({s.ServiceStatus}, start: {s.ServiceStartType})",
                "Bez działającej usługi MpsSvc nie da się zapisać reguły, nawet gdy zapora jest wyłączona w Zabezpieczeniach Windows. "
                + "Uruchom usługę (services.msc, typ uruchomienia: Automatyczny)."));

        string alias = s.InterfaceAlias.Length > 0 ? s.InterfaceAlias : lan.Name;
        string? category = s.NetworkCategory.Length > 0 ? s.NetworkCategory : null;
        string? networkName = s.NetworkName.Length > 0 ? s.NetworkName : null;
        bool unidentified = networkName is not null && GroupFirewall.IsUnidentifiedNetworkName(networkName);
        bool? policyPrivate = unidentified ? s.UnidentifiedPolicy == "1" : null;
        if (category is null)
            items.Add(new("profile", "Profil sieci", FirewallReadinessState.Problem, "Brak profilu",
                $"Windows nie przypisał jeszcze profilu sieci interfejsowi {alias}. Sprawdź połączenie i poczekaj na rozpoznanie sieci."));
        else if (category == "Public")
            items.Add(new("profile", "Profil sieci", FirewallReadinessState.Problem, $"Publiczny — sieć „{networkName}” ({alias})",
                "Reguła VNC Alert obejmuje tylko profile Domena i Prywatny, więc zapis zakończy się odmową. Przycisk Ustaw profil Prywatny (UAC) "
                + "zmienia tylko ten interfejs" + (unidentified ? " i ustawia zasadę lokalną dla sieci niezidentyfikowanych, żeby profil przetrwał restart." : ".")));
        else
            items.Add(new("profile", "Profil sieci", FirewallReadinessState.Ok, $"{Translate(category)} — sieć „{networkName}” ({alias})",
                "Profil pozwala na regułę VNC Alert."));
        if (unidentified && category != "Public")
            items.Add(policyPrivate == true
                ? new("policy-unidentified", "Zasada sieci niezidentyfikowanych", FirewallReadinessState.Ok, "Prywatna",
                    "Zasada lokalna Windows utrzyma profil Prywatny po restarcie.")
                : new("policy-unidentified", "Zasada sieci niezidentyfikowanych", FirewallReadinessState.Warning, "Nieustawiona",
                    "Sieć bez bramy jest niezidentyfikowana; bez zasady lokalnej Windows może po restarcie przywrócić profil Publiczny. "
                    + "Przycisk Ustaw profil Prywatny (UAC) ustawia tę zasadę."));

        if (serviceOk)
        {
            var blocked = s.Profiles.Where(p => p.Name is "Domain" or "Private"
                && (p.AllowLocalFirewallRules == "False" || p.AllowInboundRules == "False")).Select(p => Translate(p.Name)).ToList();
            var disabled = s.Profiles.Where(p => p.Enabled == "False").Select(p => Translate(p.Name)).ToList();
            items.Add(blocked.Count > 0
                ? new("policy", "Zasady zapory", FirewallReadinessState.Problem, "Lokalne reguły zablokowane: " + string.Join(", ", blocked),
                    "Zasady organizacji (GPO/MDM) nie pozwalają na lokalne reguły przychodzące. Aplikacja tego nie obejdzie; użyj Eksportuj instrukcję dla IT.")
                : new("policy", "Zasady zapory", FirewallReadinessState.Ok, "Lokalne reguły dozwolone",
                    disabled.Count > 0 ? "Zapora jest wyłączona na profilu: " + string.Join(", ", disabled) + ". Reguła i tak zostanie zapisana i zadziała po włączeniu zapory."
                        : "Reguły lokalne i przychodzące są dozwolone."));
            items.Add(!s.RuleExists
                ? new("rule", "Reguła VNC Alert", FirewallReadinessState.Ok, "Jeszcze nie istnieje", $"Zostanie utworzona przy Utwórz i włącz (UDP {udpPort}, tylko ten program).")
                : s.RuleGroup == GroupFirewall.OwnerGroup
                    ? new("rule", "Reguła VNC Alert", FirewallReadinessState.Ok, s.RuleEnabled == "True" ? "Istnieje, włączona" : "Istnieje, wyłączona",
                        "Zostanie zaktualizowana do bieżącego portu i adresów.")
                    : new("rule", "Reguła VNC Alert", FirewallReadinessState.Problem, "Istnieje, ale nie należy do VNC Alert",
                        $"Reguła o nazwie {_ruleId} ma grupę „{s.RuleGroup}”. Usuń ją w Zaporze Windows Defender z zabezpieczeniami zaawansowanymi lub poproś IT."));
        }
        else
        {
            items.Add(new("policy", "Zasady zapory", FirewallReadinessState.Unknown, "Nie sprawdzono", "Wymaga działającej usługi zapory."));
            items.Add(new("rule", "Reguła VNC Alert", FirewallReadinessState.Unknown, "Nie sprawdzono", "Wymaga działającej usługi zapory."));
        }
        return new(items, alias, address, networkName, category, udpPort, _program) { UnidentifiedPolicyPrivate = policyPrivate };
    }

    internal static string Translate(string category) => category switch
    {
        "Public" => "Publiczny",
        "Private" => "Prywatny",
        "Domain" or "DomainAuthenticated" => "Domena",
        _ => category,
    };

    internal sealed class Snapshot
    {
        public string ServiceStatus { get; set; } = "";
        public string ServiceStartType { get; set; } = "";
        public string InterfaceAlias { get; set; } = "";
        public string NetworkName { get; set; } = "";
        public string NetworkCategory { get; set; } = "";
        public string UnidentifiedPolicy { get; set; } = "";
        public List<ProfileSnapshot> Profiles { get; set; } = [];
        public bool RuleExists { get; set; }
        public string RuleGroup { get; set; } = "";
        public string RuleEnabled { get; set; } = "";
    }

    internal sealed class ProfileSnapshot
    {
        public string Name { get; set; } = "";
        public string Enabled { get; set; } = "";
        public string AllowLocalFirewallRules { get; set; } = "";
        public string AllowInboundRules { get; set; } = "";
    }
}
