using System.IO.Pipes;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Windows.Tests;

/// <summary>Wspólne atrapy dla testów helpera UAC: magazyn sekretów w pamięci, zapora bez PowerShell,
/// katalog tymczasowy i skryptowany „proces” helpera po prawdziwym pipe w tym samym procesie.</summary>
internal static class GroupHelperTestKit
{
    public static readonly HelperTimeouts Fast = new(Prepare: TimeSpan.FromSeconds(5), Commit: TimeSpan.FromSeconds(5),
        Abort: TimeSpan.FromSeconds(5), Exit: TimeSpan.FromSeconds(5));
    public static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    public static GroupSettingsProposal Proposal(bool enabled = true)
    {
        var config = new AppConfig
        {
            Machines = [new() { Id = "aidite1", Name = "Aidite 1", Host = "192.168.1.108", Port = 9100, Profile = "opencnc" }],
            Profiles = new() { ["opencnc"] = new() { Id = "opencnc", StatusBarRoi = new RectInt(423, 40, 543, 66) } }
        };
        var invitation = GroupFile.Create("Hala", GroupProfile.Capture(config, new RuleSet(), new Dictionary<string, string>()));
        return new GroupSettingsProposal(invitation, config.Cluster, enabled);
    }

    public static HelperRequest PrepareRequest(GroupSettingsProposal proposal, Guid? expectedCurrent) => new("prepare",
        Convert.ToBase64String(GroupFileCodec.Serialize(proposal.ExportInvitation())), proposal.GetData().Cluster, expectedCurrent, null);

    public static string PipeName() => "MaszynyAlert.Group." + Guid.NewGuid().ToString("N");

    public static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Otwiera wyłączną blokadę pliku; powodzenie oznacza, że helper ją zwolnił.</summary>
    public static FileStream Lock(string directory, string file = "apply.lock") => new(Path.Combine(directory, file),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public sealed class TestDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MaszynyAlert-helper-tests", Guid.NewGuid().ToString("N"));
        public TestDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public sealed class InMemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public bool Remove(string key) => _values.Remove(key);
        public IReadOnlyCollection<string> Keys => _values.Keys;
    }

    public sealed class FakeFirewall : IGroupFirewall
    {
        public GroupFirewallRule? Rule = new("app.exe", 47777, "LocalSubnet", false);
        public int Applies, Restores;
        public GroupFirewallRule? Capture() => Rule;
        public void Apply(ClusterConfig c) { Applies++; Rule = new("app.exe", c.UdpPort, "LocalSubnet", c.Enabled); }
        public void Restore(GroupFirewallRule? previous) { Restores++; Rule = previous; }
    }

    /// <summary>Atrapa uruchamiania helpera: zamiast UAC startuje skrypt łączący się z pipe w tym procesie.
    /// Nie kończy skryptu przy Dispose (podniesionego procesu i tak nie da się zabić); kończy go <see cref="Stop"/>.</summary>
    public sealed class ScriptedHelper : IHelperLauncher, IHelperProcess
    {
        private readonly Func<NamedPipeClientStream, CancellationToken, Task>? _script;
        private readonly Func<string, CancellationToken, Task>? _raw;
        private readonly CancellationTokenSource _stop = new();
        public Task Run { get; private set; } = Task.CompletedTask;
        public Exception? LaunchFailure;
        public string? PipeName;
        public int Launches, Disposals;
        public List<string> Seen { get; } = [];
        private ScriptedHelper(Func<NamedPipeClientStream, CancellationToken, Task>? script, Func<string, CancellationToken, Task>? raw) { _script = script; _raw = raw; }
        public static ScriptedHelper Idle() => new(null, null);
        public static ScriptedHelper Script(Func<NamedPipeClientStream, CancellationToken, Task> script) => new(script, null);
        /// <summary>Skrypt, który sam otwiera pipe (np. prawdziwy <c>RunHelperAsync</c>).</summary>
        public static ScriptedHelper Raw(Func<string, CancellationToken, Task> raw) => new(null, raw);

        public IHelperProcess Launch(string pipeName)
        {
            Launches++; PipeName = pipeName;
            if (LaunchFailure is not null) throw LaunchFailure;
            if (_raw is not null) { Run = Task.Run(() => _raw(pipeName, _stop.Token)); return this; }
            if (_script is null) return this;
            Run = Task.Run(async () =>
            {
                using var pipe = Client(pipeName);
                await pipe.ConnectAsync(5000, _stop.Token);
                await _script(pipe, _stop.Token);
            });
            return this;
        }
        public async Task WaitForExitAsync(CancellationToken ct)
        {
            try { await Run.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* zakończony błędem = zakończony */ }
        }
        public void Dispose() => Disposals++;
        public void Stop() => _stop.Cancel();

        /// <summary>Czyta żądanie i zapisuje jego operację w <see cref="Seen"/>.</summary>
        public async Task<HelperRequest> Next(Stream pipe, CancellationToken ct)
        {
            var request = await GroupSettingsClient.Read<HelperRequest>(pipe, ct);
            lock (Seen) Seen.Add(request.Operation);
            return request;
        }
        /// <summary>Próbuje odczytać jeszcze jedno żądanie po zakończeniu scenariusza (np. niechciany abort).</summary>
        public async Task Drain(Stream pipe)
        {
            using var timeout = new CancellationTokenSource(Short);
            try { await Next(pipe, timeout.Token); } catch (Exception) { }
        }
    }
}
