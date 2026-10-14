using MaszynyAlert.Core.Diagnostics;

namespace MaszynyAlert.Core.Tests.Diagnostics;

public sealed class BuildIdentityTests
{
    private const string Sha40 = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("0.1.0+0123456789abcdef0123456789abcdef01234567", Sha40)]
    [InlineData("0.1.0+linux.0123456789ABCDEF0123456789ABCDEF01234567", Sha40)]
    [InlineData("0.1.0+0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", Sha64)]
    [InlineData("0.3.412+0123456789abcdef0123456789abcdef01234567", Sha40)]
    [InlineData("0.3.413-pr78+0123456789abcdef0123456789abcdef01234567", Sha40)]
    [InlineData("0.3.0-dev+0123456789abcdef0123456789abcdef01234567", Sha40)]
    public void FromInformationalVersion_ZachowujeWersjeIWydobywaTylkoPelnySha(string value, string expectedCommit)
    {
        BuildIdentity identity = BuildIdentity.FromInformationalVersion(value);

        Assert.Equal(value, identity.Version);
        Assert.Equal(expectedCommit, identity.Commit);
        Assert.Contains("Wersja", identity.DisplayText, StringComparison.Ordinal);
        Assert.Contains(expectedCommit[..12], identity.DisplayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromInformationalVersion_BrakWersjiJestJawnieBuildemUnknownDev(string? value)
    {
        BuildIdentity identity = BuildIdentity.FromInformationalVersion(value);

        Assert.Equal("unknown/dev", identity.Version);
        Assert.Null(identity.Commit);
        Assert.Contains("commit nieznany", identity.DisplayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.0+abcdef1")]
    [InlineData("0.1.0+0123456789abcdef0123456789abcdef012345678")]
    [InlineData("0.1.0+0123456789abcdef0123456789abcdef01234567.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void FromInformationalVersion_NieWymyslaCommituZGituGdyShaNieJestWiarygodny(string value)
    {
        BuildIdentity identity = BuildIdentity.FromInformationalVersion(value);

        Assert.Equal(value, identity.Version);
        Assert.Null(identity.Commit);
    }
}
