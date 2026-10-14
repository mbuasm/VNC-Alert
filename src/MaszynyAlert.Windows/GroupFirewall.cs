using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Windows;

/// <summary>A firewall step failed. <see cref="Code"/> is one of the <see cref="GroupFirewall"/> reason codes
/// (or <c>UNKNOWN</c>); <see cref="Message"/> is the operator-facing Polish explanation.</summary>
public sealed class GroupFirewallException : IOException
{
    public string Code { get; }
    public GroupFirewallException(string code, string message) : base(message) { Code = code; }
}

/// <summary>Only the application's own inbound UDP rule. No Public profile, VNC ports or global policy changes.
/// Every PowerShell failure is reported with its reason: the script throws short reason codes, the catch block writes
/// them to stderr unwrapped, and <see cref="DescribeFailure"/> turns them into the operator's sentence.</summary>
public sealed class GroupFirewall : IGroupFirewall
{
    public const string RuleId = "MaszynyAlert.Group.Udp";
    public const string ErrorLogFileName = "firewall-last-error.txt";
    internal const string Marker = "VNCALERT|";
    internal const string OwnerGroup = "MaszynyAlert.Group";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private readonly string _program;
    private readonly string _ruleId;
    private readonly Func<string, string> _execute;
    public GroupFirewall(string program, string? logDirectory = null)
        : this(program, RuleId, script => Execute(script, logDirectory)) { }
    internal GroupFirewall(string program, string ruleId, Func<string, string> execute)
    { _program = Path.GetFullPath(program); _ruleId = ruleId; _execute = execute; }

    /// <summary>Common prologue: the firewall service must run (a firewall switched off in Windows Security still
    /// runs it; a stopped service breaks every Net*Firewall* cmdlet), the module is imported explicitly so a missing
    /// module has its own message, and a rule of the same name owned by someone else is never touched.</summary>
    private string Header => "$ruleId=" + Quote(_ruleId) + "\n"
        + "$r=Get-NetFirewallRule -PolicyStore PersistentStore -Name $ruleId -ErrorAction SilentlyContinue\n"
        + "if ($r -and ($r.Group -ne '" + OwnerGroup + "')) { throw ('OWNERSHIP|' + $r.Group + '|' + $r.DisplayName) }\n";

    internal static string ServiceCheck =>
        "$svc=Get-Service -Name MpsSvc -ErrorAction SilentlyContinue\n"
        + "if (-not $svc) { throw 'SERVICE_DOWN|brak' }\n"
        + "if ($svc.Status -ne 'Running') { throw ('SERVICE_DOWN|' + $svc.Status) }\n"
        + "Import-Module NetSecurity -ErrorAction Stop\n";

    /// <summary>Common first lines of every script. Progress records are silenced because with redirected streams
    /// Windows PowerShell serializes them as CLIXML on stderr, which buries the real error text.</summary>
    internal const string Prologue = "$ErrorActionPreference='Stop'\n$ProgressPreference='SilentlyContinue'\n";

    /// <summary>Wraps a step so any failure reaches stderr as one unwrapped <c>VNCALERT|code|details</c> line.
    /// Windows PowerShell wraps formatted error records at the console width, which would split the reason.
    /// The script always ends with an explicit <c>exit 0</c>: <c>powershell.exe -EncodedCommand</c> otherwise returns 1
    /// whenever <c>$?</c> of the last executed command is false, and <c>Get-NetFirewallRule -ErrorAction SilentlyContinue</c>
    /// for a rule that does not exist yet leaves exactly that behind (an <c>if</c> whose body did not run does not reset
    /// it).</summary>
    internal static string Wrap(string step, string body, bool requireService = true) => "# VNC Alert firewall step: " + step + "\n"
        + Prologue + "try {\n" + (requireService ? ServiceCheck : "") + body + "\n} catch {\n"
        + "[Console]::Error.WriteLine('" + Marker + "' + $_.Exception.Message)\nexit 1\n}\nexit 0\n";

    public GroupFirewallRule? Capture()
    {
        string result = _execute(Wrap("Capture", Header + """
            if ($r) {
              $p=$r | Get-NetFirewallPortFilter
              $a=$r | Get-NetFirewallAddressFilter
              $app=$r | Get-NetFirewallApplicationFilter
              if ($r.Direction -ne 'Inbound' -or $r.Action -ne 'Allow' -or $p.Protocol -ne 'UDP' -or $r.Profile.ToString() -ne 'Domain, Private') { throw ('RULE_SHAPE|' + $r.Direction + ' ' + $r.Action + ' ' + $p.Protocol + ' ' + $r.Profile) }
              @{ Program=$app.Program; Port=[int]$p.LocalPort; RemoteAddresses=($a.RemoteAddress -join ','); Enabled=($r.Enabled -eq 'True'); LocalAddress=($a.LocalAddress -join ',') } | ConvertTo-Json -Compress
            }
            """));
        if (string.IsNullOrWhiteSpace(result)) return null;
        var captured = JsonSerializer.Deserialize<GroupFirewallRule>(result) ?? throw new InvalidDataException("Uszkodzony zapis reguły.");
        // A manually broadened rule (e.g. RemoteAddress Any) is never carried into the undo record.
        return Validated(captured);
    }
    public void Apply(ClusterConfig config)
    {
        if (!config.Enabled)
        {
            _execute(Wrap("Disable", Header + "if ($r) { $r | Disable-NetFirewallRule | Out-Null }")); return;
        }
        var lan = LanInterface.Select(config.LocalAddress);
        if (config.UdpPort is < 1 or > 65535) throw new ArgumentException("Nieprawidłowy port UDP.");
        // Numeric destinations only; cross-subnet unicast adds exact addresses, never Any.
        var peers = config.Peers.Select(p => p.Split(':')[0]).Select(p =>
            IsUnicastIPv4(p, out var ip) ? ip.ToString() : throw new ArgumentException("Nieprawidłowy adres peera grupy."));
        string remote = string.Join(',', new[] { "LocalSubnet" }.Concat(peers).Distinct());
        Write("Apply", new(_program, config.UdpPort, remote, true), ProfileCheck(lan.Address.ToString()));
    }

    /// <summary>The network-profile and policy preconditions of the elevated write. Each failure names what was
    /// found, so the operator sees the interface and the Windows network name, never a generic refusal.</summary>
    internal static string ProfileCheck(string address) =>
        "$ip=Get-NetIPAddress -AddressFamily IPv4 -IPAddress " + Quote(address) + " -ErrorAction SilentlyContinue | Select-Object -First 1\n"
        + "if (-not $ip) { throw ('NO_IP|' + " + Quote(address) + ") }\n"
        + "$network=Get-NetConnectionProfile -InterfaceIndex $ip.InterfaceIndex -ErrorAction SilentlyContinue | Select-Object -First 1\n"
        + "if (-not $network) { throw ('NO_PROFILE|' + $ip.InterfaceAlias + '|' + " + Quote(address) + ") }\n"
        + "if ($network.NetworkCategory -eq 'Public') { throw ('PUBLIC_PROFILE|' + $ip.InterfaceAlias + '|' + " + Quote(address) + " + '|' + $network.Name) }\n"
        + "$blocked=Get-NetFirewallProfile -PolicyStore ActiveStore -Name Domain,Private | Where-Object { $_.AllowLocalFirewallRules -eq 'False' -or $_.AllowInboundRules -eq 'False' }\n"
        + "if ($blocked) { throw ('POLICY_BLOCK|' + (($blocked | ForEach-Object { $_.Name }) -join ', ')) }\n";

    public void Restore(GroupFirewallRule? previous)
    {
        if (previous is null) _execute(Wrap("Remove", Header + "if ($r) { $r | Remove-NetFirewallRule }"));
        else Write("Restore", previous, "");
    }
    /// <summary>
    /// The undo record (a file in the settings directory, user-writable for portable) and the captured
    /// rule are untrusted input to an elevated process. Whatever they say, the rule is written for this
    /// executable only, with LocalSubnet and/or explicit unicast IPv4 peers, and a concrete local address.
    /// </summary>
    internal GroupFirewallRule Validated(GroupFirewallRule value)
    {
        if (value is null || value.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(value.Program) || value.RemoteAddresses is null)
            throw new InvalidDataException("Uszkodzony zapis reguły.");
        var remote = value.RemoteAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remote.Length == 0 || remote.Any(a => a != "LocalSubnet" && !IsUnicastIPv4(a, out _)))
            throw new InvalidDataException("Niedozwolony zakres adresów reguły zapory.");
        var local = (value.LocalAddress ?? "Any").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (local.Length == 0 || local.Any(a => a != "Any" && !IsUnicastIPv4(a, out _)))
            throw new InvalidDataException("Niedozwolony adres lokalny reguły zapory.");
        return value with { Program = _program, RemoteAddresses = string.Join(',', remote.Distinct()), LocalAddress = string.Join(',', local.Distinct()) };
    }
    internal static bool IsUnicastIPv4(string text, out IPAddress ip)
    {
        ip = IPAddress.None;
        return IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && parsed.GetAddressBytes()[0] is not (0 or >= 224) && !IPAddress.IsLoopback(parsed) && (ip = parsed) is not null;
    }
    private void Write(string step, GroupFirewallRule value, string checks)
    {
        value = Validated(value);
        _execute(Wrap(step, Header + checks + "if ($r) { Set-NetFirewallRule -PolicyStore PersistentStore -Name $ruleId" + RuleFields(value)
            + " | Out-Null } else { New-NetFirewallRule -PolicyStore PersistentStore -Name $ruleId -DisplayName 'VNC Alert — grupa komputerów' -Group '"
            + OwnerGroup + "'" + RuleFields(value) + " | Out-Null }\n"
            + "if (-not (Get-NetFirewallRule -PolicyStore ActiveStore -Name $ruleId -ErrorAction SilentlyContinue)) { throw 'NOT_EFFECTIVE|' }"));
    }
    /// <summary>The exact rule parameters, shared with the IT instruction so both describe the same rule.</summary>
    internal static string RuleFields(GroupFirewallRule value) =>
        " -Direction Inbound -Action Allow -Profile Domain,Private -Protocol UDP -LocalPort " + value.Port
        + " -Program " + Quote(value.Program) + " -RemoteAddress " + string.Join(',', value.RemoteAddresses.Split(',').Select(Quote))
        + " -LocalAddress " + string.Join(',', (value.LocalAddress ?? "Any").Split(',').Select(Quote)) + " -Enabled " + (value.Enabled ? "True" : "False");
    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Runs one script in Windows PowerShell 5.1. On failure the reason is mapped by
    /// <see cref="DescribeFailure"/> and the raw output is kept in <see cref="ErrorLogFileName"/> when a log
    /// directory is known (the elevated helper writes into the group settings directory).</summary>
    internal static string Execute(string script, string? logDirectory)
    {
        var start = new ProcessStartInfo(PowerShellPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" }) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new GroupFirewallException("NO_POWERSHELL", "Nie uruchomiono Windows PowerShell do obsługi zapory.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(true);
            string timeoutMessage = $"Operacja zapory nie zakończyła się w ciągu {Timeout.TotalSeconds:0} s (krok: {StepName(script)}). "
                + "Sprawdź obciążenie komputera i skanowanie antywirusowe, potem ponów.";
            LogFailure(logDirectory, script, -1, watch.Elapsed, "", timeoutMessage);
            throw new GroupFirewallException("TIMEOUT", timeoutMessage);
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            var (code, message) = DescribeFailure(process.ExitCode, stderr.Result);
            LogFailure(logDirectory, script, process.ExitCode, watch.Elapsed, stdout.Result, stderr.Result);
            throw new GroupFirewallException(code, message);
        }
        return stdout.Result.Trim();
    }

    internal static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static string StepName(string script)
    {
        const string prefix = "# VNC Alert firewall step: ";
        string first = script.Split('\n', 2)[0].Trim();
        return first.StartsWith(prefix, StringComparison.Ordinal) ? first[prefix.Length..] : "?";
    }

    private static void LogFailure(string? directory, string script, int exitCode, TimeSpan elapsed, string stdout, string stderr)
    {
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ErrorLogFileName),
                $"time={DateTimeOffset.Now:O}\nstep={StepName(script)}\nexitCode={exitCode}\nelapsedMs={elapsed.TotalMilliseconds:0}\n"
                + $"--- stderr ---\n{stderr}\n--- stdout ---\n{stdout}\n", Encoding.UTF8);
        }
        catch (Exception) { /* diagnostics only; the reason still reaches the operator through the exception */ }
    }

    /// <summary>Maps PowerShell output to a reason code and an operator sentence. A <c>VNCALERT|</c> line is the
    /// script's own reason; anything else is the first meaningful line of the error text.</summary>
    internal static (string Code, string Message) DescribeFailure(int exitCode, string stderr)
    {
        string? reason = (stderr ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (reason is null)
        {
            return ("UNKNOWN", $"Nie udało się przygotować zapory (PowerShell zakończył się kodem {exitCode}): {Clip(FirstErrorLine(stderr ?? ""))} "
                + "Szczegóły są w pliku " + ErrorLogFileName + " w katalogu ustawień grupy.");
        }
        var parts = reason[Marker.Length..].Split('|');
        string code = parts[0];
        string Part(int i) => parts.Length > i ? parts[i].Trim() : "";
        return code switch
        {
            "SERVICE_DOWN" => (code, $"Usługa Zapora Windows Defender (MpsSvc) nie działa (stan: {Part(1)}). Bez niej nie da się zapisać reguły, "
                + "nawet gdy zapora jest wyłączona w Zabezpieczeniach Windows. Uruchom usługę (services.msc, typ uruchomienia: Automatyczny) i ponów."),
            "PUBLIC_PROFILE" => (code, $"Sieć „{Part(3)}” na interfejsie {Part(1)} ({Part(2)}) ma profil Publiczny. VNC Alert nie zmienia profilu bez wyraźnego polecenia. "
                + "Użyj przycisku Ustaw profil Prywatny na tym ekranie, zmień profil w Ustawieniach Windows (Sieć i internet → Ethernet → Profil sieci) "
                + "albo poproś IT, potem ponów. " + UnidentifiedHint(Part(3))),
            "NO_PROFILE" => (code, $"Windows nie przypisał profilu sieci interfejsowi {Part(1)} ({Part(2)}). Sprawdź, czy kabel i interfejs są aktywne, "
                + "poczekaj chwilę na rozpoznanie sieci i ponów."),
            "NO_IP" => (code, $"Adres {Part(1)} nie jest już przypisany do żadnego interfejsu tego komputera. Sprawdź połączenie sieciowe i ponów."),
            "POLICY_BLOCK" => (code, $"Zasady organizacji (GPO/MDM) blokują lokalne reguły przychodzące zapory w profilu: {Part(1)}. "
                + "Aplikacja tego nie obejdzie — użyj przycisku Eksportuj instrukcję dla IT i przekaż ją administratorowi."),
            "OWNERSHIP" => (code, $"Reguła zapory o nazwie {RuleId} już istnieje, ale nie należy do VNC Alert (grupa: „{Part(1)}”, nazwa: „{Part(2)}”). "
                + "Usuń ją w Zaporze Windows Defender z zabezpieczeniami zaawansowanymi lub poproś IT, potem ponów."),
            "RULE_SHAPE" => (code, $"Istniejąca reguła {RuleId} ma nieoczekiwany kształt ({Part(1)}). Usuń tę regułę w Zaporze Windows Defender "
                + "z zabezpieczeniami zaawansowanymi i ponów; VNC Alert utworzy ją od nowa."),
            "NOT_EFFECTIVE" => (code, "Reguła została zapisana, ale nie jest widoczna w aktywnych zasadach zapory. Sprawdź z IT zasady organizacji "
                + "i stan usługi Zapora Windows Defender."),
            _ => ("UNKNOWN", $"Nie udało się przygotować zapory: {Clip(string.Join(" | ", parts))} Szczegóły są w pliku "
                + ErrorLogFileName + " w katalogu ustawień grupy."),
        };
    }

    internal static bool IsUnidentifiedNetworkName(string networkName) =>
        networkName.Contains("niezident", StringComparison.OrdinalIgnoreCase) || networkName.Contains("unidentified", StringComparison.OrdinalIgnoreCase);

    internal static string UnidentifiedHint(string networkName) => IsUnidentifiedNetworkName(networkName)
        ? "Sieć niezidentyfikowana (bez bramy) nie ma przełącznika profilu w Ustawieniach; przycisk Ustaw profil Prywatny (UAC) ustawia "
          + "profil i zasadę lokalną „Sieci niezidentyfikowane → Prywatna” (secpol.msc), żeby przetrwał restart."
        : "";

    private static readonly Regex ClixmlErrorRecord = new("<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ClixmlEscape = new("_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant);

    /// <summary>The first meaningful line of what PowerShell wrote to stderr. With redirected streams Windows PowerShell
    /// emits CLIXML (<c>#&lt; CLIXML</c> plus serialized progress and error records); only the error records carry the
    /// message, so those are decoded and the progress noise is skipped instead of being quoted to the operator.</summary>
    internal static string FirstErrorLine(string stderr)
    {
        var lines = stderr.Split('\n').Select(l => l.Trim()).ToList();
        if (!lines.Any(l => l.StartsWith("#< CLIXML", StringComparison.Ordinal)))
            return lines.FirstOrDefault(l => l.Length > 0) ?? "brak treści błędu";
        string records = string.Concat(ClixmlErrorRecord.Matches(stderr).Select(m => DecodeClixml(m.Groups[1].Value)));
        string? first = records.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
            ?? lines.FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#<", StringComparison.Ordinal) && !l.StartsWith('<'));
        return first ?? "PowerShell nie zgłosił treści błędu (w strumieniu błędów były tylko komunikaty postępu).";
    }

    private static string DecodeClixml(string value) =>
        WebUtility.HtmlDecode(ClixmlEscape.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()));

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
