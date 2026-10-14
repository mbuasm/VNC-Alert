using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Probe.Tests;

public sealed class ConnectionHelperRsaAesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static FakeRfbServerOptions SystemLoginServer()
    {
        var image = new RgbaImage(32, 16);
        image.Fill(new Rgba(10, 120, 200));
        var options = new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [5],
            Screen = image,
        };
        options.RsaAes.ExpectedPassword = "tajne-haslo";
        options.RsaAes.Subtype = FakeRsaAesServer.SubtypeUserAndPassword;
        options.RsaAes.ExpectedUsername = "serwis";
        return options;
    }

    [Fact]
    public async Task NazwaUzytkownikaZKonfiguracjiMaszyny_DochodziDoSerwera()
    {
        FakeRfbServerOptions options = SystemLoginServer();
        await using var server = new FakeRfbServer(options);
        var helper = new ConnectionHelper("tajne-haslo", noAuthPrompt: true);

        ConnectOutcome outcome = await helper.ConnectAsync(
            "amm580", "580", "127.0.0.1", server.Port, Timeout, CancellationToken.None, username: "serwis");

        Assert.True(outcome.Success, outcome.SkipReason);
        await outcome.Client!.DisposeAsync();
        Assert.Equal("serwis", options.RsaAes.ReceivedUsername);
        Assert.Equal("tajne-haslo", options.RsaAes.ReceivedPassword);
    }

    [Fact]
    public async Task NazwaUzytkownikaZWierszaPolecen_DzialaGdyNieMaJejWKonfiguracji()
    {
        // --username ratuje przebieg bez edycji config.json (komendy biorące sam adres, jak capture,
        // nie mają skąd wziąć tej nazwy).
        FakeRfbServerOptions options = SystemLoginServer();
        await using var server = new FakeRfbServer(options);
        var helper = new ConnectionHelper("tajne-haslo", noAuthPrompt: true, session: null, cliUsername: "serwis");

        ConnectOutcome outcome = await helper.ConnectAsync(
            "127.0.0.1:5900", "580", "127.0.0.1", server.Port, Timeout, CancellationToken.None);

        Assert.True(outcome.Success, outcome.SkipReason);
        await outcome.Client!.DisposeAsync();
        Assert.Equal("serwis", options.RsaAes.ReceivedUsername);
    }

    [Fact]
    public async Task BezNazwyUzytkownika_ProbujePustej_ISerwerRozstrzyga()
    {
        FakeRfbServerOptions options = SystemLoginServer();
        await using var server = new FakeRfbServer(options);
        var helper = new ConnectionHelper("tajne-haslo", noAuthPrompt: true);

        ConnectOutcome outcome = await helper.ConnectAsync(
            "amm580", "580", "127.0.0.1", server.Port, Timeout, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(string.Empty, options.RsaAes.ReceivedUsername); // próba naprawdę poszła
        Assert.Contains("PUSTĄ nazwą użytkownika", outcome.SkipReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("nie odpowiada", outcome.SkipReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BezNazwyUzytkownika_GdySerwerowiPustaWystarczy_LaczySie()
    {
        // serwer prosi o wariant „nazwa
        // użytkownika i hasło”, ale nazwy nie sprawdza.
        FakeRfbServerOptions options = SystemLoginServer();
        options.RsaAes.ExpectedUsername = string.Empty;
        await using var server = new FakeRfbServer(options);
        var helper = new ConnectionHelper("tajne-haslo", noAuthPrompt: true);

        ConnectOutcome outcome = await helper.ConnectAsync(
            "amm580", "580", "127.0.0.1", server.Port, Timeout, CancellationToken.None);

        Assert.True(outcome.Success, outcome.SkipReason);
        await outcome.Client!.DisposeAsync();
        Assert.Equal(string.Empty, options.RsaAes.ReceivedUsername);
    }
}
