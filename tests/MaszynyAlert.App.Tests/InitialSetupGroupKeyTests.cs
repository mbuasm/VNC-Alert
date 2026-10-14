using System.Reflection;
using System.Runtime.CompilerServices;
using MaszynyAlert.App.Services;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Tests;

public sealed class InitialSetupGroupKeyTests
{
    private static readonly MachineConfig Machine = new() { Id = "aidite1", Name = "Aidite 1", Host = "192.168.1.11", Profile = "aidite" };

    private static GroupIdentity Identity() => new()
    {
        GroupId = Guid.NewGuid(), GroupName = "Hala", KeyId = Guid.NewGuid(), ProtocolVersion = 1, ProfileRevision = 1, ProfileHash = new string('a', 64),
    };

    private static bool NeedsInitialSetup(AppConfig config, ISecretStore secrets)
    {
        var orchestrator = (AppOrchestrator)RuntimeHelpers.GetUninitializedObject(typeof(AppOrchestrator));
        typeof(AppOrchestrator).GetField("_cfg", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(orchestrator, config);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        typeof(TrayIconService).GetField("_orchestrator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tray, orchestrator);
        typeof(TrayIconService).GetField("_secrets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tray, secrets);
        return tray.NeedsInitialSetup();
    }

    private static Secrets WithVncPassword() => new() { [SecretKeys.VncPassword(Machine.Id)] = "haslo" };

    [Fact]
    public void GrupaZKreatoraBezStaregoKlucza_OknoUstawienSieNieOtwiera()
    {
        var config = new AppConfig { Machines = [Machine], Cluster = new ClusterConfig { Enabled = true, Group = Identity() } };

        Assert.False(NeedsInitialSetup(config, WithVncPassword()));
        Assert.True(NeedsInitialSetup(new AppConfig { Machines = [Machine], Cluster = new ClusterConfig { Enabled = true } }, WithVncPassword()));
    }

    [Fact]
    public void ZeStarymKluczemAlboBezGrupy_OknoSieNieOtwiera()
    {
        var secrets = WithVncPassword();
        secrets[SecretKeys.ClusterHmacKey] = Convert.ToBase64String(HmacSigner.GenerateKey());
        Assert.False(NeedsInitialSetup(new AppConfig { Machines = [Machine], Cluster = new ClusterConfig { Enabled = true, Group = Identity() } }, secrets));
        Assert.False(NeedsInitialSetup(new AppConfig { Machines = [Machine], Cluster = new ClusterConfig { Enabled = false } }, WithVncPassword()));
        Assert.True(NeedsInitialSetup(new AppConfig { Machines = [Machine], Cluster = new ClusterConfig { Enabled = false } }, new Secrets()));
    }

    private sealed class Secrets : Dictionary<string, string>, ISecretStore
    {
        public Secrets() : base(StringComparer.Ordinal) { }
        public string? Get(string key) => this.GetValueOrDefault(key);
        public void Set(string key, string value) => this[key] = value;
        bool ISecretStore.Remove(string key) => Remove(key);
        IReadOnlyCollection<string> ISecretStore.Keys => Keys;
    }
}
