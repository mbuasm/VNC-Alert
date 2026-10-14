using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

/// <summary>Ścieżki błędów uwierzytelnienia: złe hasło, brak hasła, tylko niewspierane typy.</summary>
public sealed class AuthenticationTests
{
    [Fact]
    public async Task VncAuth_WrongPassword_ThrowsAuthenticationExceptionWithServerReason()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.VncAuth],
            Password = "poprawne",
            RejectVncAuth = true,
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = "zle-haslo"));

        RfbAuthenticationException ex =
            await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(cts.Token));

        Assert.NotNull(ex.ServerReason);
        Assert.Contains("Authentication failed", ex.ServerReason);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task VncAuth_WithoutConfiguredPassword_Throws()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.VncAuth],
            Password = "tajne",
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = null));

        RfbAuthenticationException ex =
            await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(cts.Token));

        Assert.Contains("hasła", ex.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task OnlyRa2Offered_NegotiatesRsaAesInsteadOfFailing()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        var options = new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.RA2, (byte)RfbSecurityType.RA2ne],
        };
        options.RsaAes.ExpectedPassword = "tajne";
        await using var server = new FakeRfbServer(options);

        RfbClientOptions clientOptions = TestSupport.ClientOptions(server);
        clientOptions.Password = "tajne";
        await using var client = new RfbClient(clientOptions);

        RfbServerInfo info = await client.ConnectAsync(cts.Token);

        // Z pary 5/6 wybieramy wariant szyfrujący całą sesję, a nie „ne”.
        Assert.Equal(RfbSecurityType.RA2, info.UsedSecurityType);
        Assert.Equal(new byte[] { 5, 6 }, info.OfferedSecurityTypes);
    }

    /// <summary>
    /// Typy, których naprawdę nie obsługujemy (Tight, TLS, VeNCrypt), mają nadal dawać czytelny wyjątek
    /// z pełną listą — to on trafia do raportu <c>probe.exe</c> i do kolumny „ostatni błąd”.
    /// </summary>
    [Fact]
    public async Task OnlyUnsupportedTypesOffered_ThrowsWithOfferedTypes()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.Tight, (byte)RfbSecurityType.TLS, (byte)RfbSecurityType.VeNCrypt],
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        RfbAuthenticationException ex =
            await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(cts.Token));

        Assert.Equal(new byte[] { 16, 18, 19 }, ex.OfferedSecurityTypes);
        Assert.Contains("Tight", ex.Message);
        // Ta lista nie zawiera RSA-AES, więc nie wolno doklejać podpowiedzi o nim.
        Assert.DoesNotContain("RSA-AES", ex.Message);
    }

    [Fact]
    public async Task Protocol33_UnsupportedSecurityType_ThrowsWithOfferedType()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.3",
            SecurityTypes = [(byte)RfbSecurityType.Tight],
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server));

        RfbAuthenticationException ex =
            await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(cts.Token));

        Assert.Equal(new byte[] { 16 }, ex.OfferedSecurityTypes);
    }

    [Fact]
    public async Task Protocol33_VncAuth_WrongPassword_Throws()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.3",
            SecurityTypes = [(byte)RfbSecurityType.VncAuth],
            Password = "prawidlowe",
        });

        await using var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = "inne"));

        RfbAuthenticationException ex =
            await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(cts.Token));

        // W 3.3 serwer nie wysyła tekstu powodu — klient musi to znieść i dać własny komunikat PL.
        Assert.Null(ex.ServerReason);
        Assert.Contains("hasło", ex.Message);
    }
}
