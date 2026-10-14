using System.Security.Cryptography;
using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Windows.Tests;

public sealed class GroupSettingsDpapiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MaszynyAlert-group-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(DataProtectionScope.CurrentUser)]
    [InlineData(DataProtectionScope.LocalMachine)]
    public void CommittedGroupKey_IsEncrypted_AndReopensWithoutOverwritingLocalPasswords(DataProtectionScope scope)
    {
        Directory.CreateDirectory(_directory);
        string secretPath = Path.Combine(_directory, "secrets.dat");
        var secrets = new DpapiSecretStore(secretPath, scope);
        secrets.Set(SecretKeys.VncPassword("aidite1"), "LOCAL-ONLY-VNC");
        var config = new AppConfig
        {
            Machines = [new() { Id = "aidite1", Name = "Aidite 1", Host = "192.168.1.108", Port = 9100, Profile = "opencnc" }],
            Profiles = new() { ["opencnc"] = new() { Id = "opencnc", StatusBarRoi = new RectInt(423, 40, 543, 66) } }
        };
        var invitation = GroupFile.Create("Hala", GroupProfile.Capture(config, new RuleSet(), new Dictionary<string, string>()));
        string groupPath = Path.Combine(_directory, "group-settings");
        var store = new GroupSettingsStore(groupPath, secrets);
        Guid id = store.Stage(new GroupSettingsProposal(invitation, config.Cluster, true), null);
        store.Commit(id);

        foreach (string file in Directory.GetFiles(_directory, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain(invitation.NetworkKey, text);
            Assert.DoesNotContain("LOCAL-ONLY-VNC", text);
        }
        var reopenedSecrets = new DpapiSecretStore(secretPath, scope);
        var reopened = new GroupSettingsStore(groupPath, reopenedSecrets);
        Assert.Equal(invitation.NetworkKey, reopened.ReadCommittedNetworkKey());
        Assert.Equal(invitation.Identity, reopened.Read().Data!.Cluster.Group);
        Assert.Equal("LOCAL-ONLY-VNC", reopenedSecrets.Get(SecretKeys.VncPassword("aidite1")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
