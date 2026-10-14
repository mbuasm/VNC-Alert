using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtReviewFollowUpTests
{
    [Fact]
    public async Task SerwerQtPaletowy_NieDostajeSetPixelFormat_TylkoSetEncodings()
    {
        await using var server = new ScriptedRfbServer
        {
            Width = 8, Height = 8, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian with { TrueColour = false },
            DesktopName = QtEmbeddedVncServer.DesktopName,
        };
        server.Start();
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port }))
        {
            await client.ConnectAsync(CancellationToken.None);
            byte[] actual = server.ClientBytesAfterInit;
            for (int i = 0; actual.Length < 4 && i < 50; i++)
            {
                await Task.Delay(20);
                actual = server.ClientBytesAfterInit;
            }

            Assert.NotEmpty(actual);
            Assert.Equal(2, actual[0]); // pierwsza wiadomość po ServerInit to SetEncodings, nie SetPixelFormat (typ 0)
        }
    }

    [Theory]
    [InlineData("aidite5x", true)]
    [InlineData("premile", true)]
    [InlineData("cameo1", true)]
    [InlineData("aidite-light2", true)]
    [InlineData("AIDITE-LIGHT2", true)]
    [InlineData("opencnc", false)]
    [InlineData("amm580", false)]
    [InlineData(null, false)]
    public void ProfileMaszynZSerweremQt(string? profile, bool expected) =>
        Assert.Equal(expected, QtEmbeddedVncServer.IsQtProfile(profile));
}
