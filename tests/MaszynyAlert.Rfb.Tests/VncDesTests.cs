using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>
/// Własna implementacja DES z Core musi dawać dokładnie te same wyniki, co DES z biblioteki
/// standardowej (ECB, bez paddingu, klucz VNC z odwróconymi bitami).
/// </summary>
public sealed class VncDesTests
{
    private static readonly byte[][] Challenges =
    [
        [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xF0, 0x01],
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF],
    ];

    [Theory]
    [InlineData("", 0)]
    [InlineData("", 1)]
    [InlineData("a", 0)]
    [InlineData("a", 1)]
    [InlineData("password", 0)]
    [InlineData("password", 1)]
    [InlineData("12345678", 0)]
    [InlineData("12345678", 1)]
    [InlineData("dluzsze-niz-8", 0)]
    [InlineData("dluzsze-niz-8", 1)]
    public void VncDes_MatchesReferenceDes(string password, int challengeIndex)
    {
        if (ReferenceVncDes.IsRejectedByDotNetDes(password))
        {
            // Klucz słaby/półsłaby — System.Security.Cryptography.DES odmawia szyfrowania,
            // więc dla tego wektora nie ma z czym porównywać (np. hasło puste → klucz 0x00...).
            return;
        }

        byte[] challenge = Challenges[challengeIndex];
        byte[] expected = ReferenceVncDes.EncryptChallenge(challenge, password);
        byte[] actual = VncDes.EncryptChallenge(challenge, password);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VncDes_TruncatesPasswordToEightBytes()
    {
        byte[] challenge = Challenges[0];
        Assert.Equal(
            VncDes.EncryptChallenge(challenge, "dluzsze-"),
            VncDes.EncryptChallenge(challenge, "dluzsze-niz-8"));
    }

    [Fact]
    public void VncDes_RejectsChallengeOfWrongLength()
    {
        Assert.Throws<ArgumentException>(() => VncDes.EncryptChallenge(new byte[8], "abc"));
    }

    [Fact]
    public void VncDes_ProducesSixteenBytes()
    {
        Assert.Equal(16, VncDes.EncryptChallenge(Challenges[0], "abc").Length);
    }

    [Fact]
    public async Task HasloZeZnakamiSpozaLatin1_CzytelnyBladZamiastCichejZamiany()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions { SecurityTypes = [2], Password = "dobre" });
        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = "łódź"));

        RfbAuthenticationException ex = await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(cts.Token));

        Assert.Contains("znak", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ł", ex.Message, StringComparison.Ordinal);
    }
}
