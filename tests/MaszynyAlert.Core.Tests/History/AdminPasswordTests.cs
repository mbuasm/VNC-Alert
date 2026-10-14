using MaszynyAlert.Core.Abstractions;

namespace MaszynyAlert.Core.Tests.History;

public class AdminPasswordTests
{
    [Fact]
    public void Login_DoesNotAcceptArbitraryPasswordsAndSurvivesBrokenStore()
    {
        foreach (string guess in new[] { "", "admin", "password", "VNC Alert", "Hasło do panelu 2026" })
        {
            Assert.False(AdminPassword.Verify(guess, null));
            Assert.False(AdminPassword.VerifyLogin(guess, () => null));
            Assert.False(AdminPassword.Verify(guess, "broken"));
        }
    }

    [Fact]
    public void HashIsSaltedAndOnlyCorrectPasswordUnlocks()
    {
        const string password = "Hasło do panelu 2026";
        string hash = AdminPassword.Hash(password);
        Assert.NotEqual(hash, AdminPassword.Hash(password));
        Assert.DoesNotContain(password, hash);
        Assert.True(AdminPassword.Verify(password, hash));
        Assert.False(AdminPassword.Verify("incorrect", hash));
        Assert.False(AdminPassword.Verify("", null));
        Assert.False(AdminPassword.Verify(password, "broken"));
        Assert.Throws<ArgumentException>(() => AdminPassword.Hash("short"));
    }
}
