using System.Security.Cryptography;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class RsaAesHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static RgbaImage Screen()
    {
        var image = new RgbaImage(32, 16);
        image.Fill(new Rgba(10, 120, 200));
        return image;
    }

    private static FakeRfbServerOptions ServerOptions(byte securityType, Action<FakeRsaAesOptions>? configure = null)
    {
        var options = new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [securityType],
            Screen = Screen(),
        };
        options.RsaAes.ExpectedPassword = "tajne-haslo";
        configure?.Invoke(options.RsaAes);
        return options;
    }

    private static RfbClientOptions ClientOptions(FakeRfbServer server, string? password = "tajne-haslo", string? username = null) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Password = password,
        Username = username,
        ConnectTimeout = Timeout,
        ReadTimeout = Timeout,
    };

    /// <summary>
    /// Wszystkie cztery warianty przechodzą pełny przebieg: uwierzytelnienie, <c>ServerInit</c>
    /// i prawdziwy odczyt obrazu. Ostatni krok jest istotny: dla RA2/RA2_256 leci on JUŻ przez
    /// szyfrowany strumień, więc sprawdza także ramkowanie AES-EAX, a nie sam handshake.
    /// </summary>
    [Theory]
    [InlineData((byte)5)]    // RA2 — AES-128, sesja szyfrowana
    [InlineData((byte)6)]    // RA2ne — AES-128, sesja jawna po uwierzytelnieniu
    [InlineData((byte)129)]  // RA2_256 — AES-256, sesja szyfrowana
    [InlineData((byte)130)]  // RA2ne_256 — AES-256, sesja jawna po uwierzytelnieniu
    public async Task WszystkieWariantyRsaAes_LaczaSieIPobierajaObraz(byte securityType)
    {
        await using var server = new FakeRfbServer(ServerOptions(securityType));
        await using var client = new RfbClient(ClientOptions(server));

        RfbServerInfo info = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal((RfbSecurityType)securityType, info.UsedSecurityType);
        Assert.Equal(32, info.Width);
        Assert.Equal(16, info.Height);

        RgbaImage image = await client.CaptureAsync(new RectInt(0, 0, 32, 16), incremental: false, CancellationToken.None);

        Assert.Equal(32, image.Width);
        Assert.Equal(new Rgba(10, 120, 200), image.GetPixel(5, 5));
    }

    /// <summary>Hasło ma dojść do serwera dokładnie takie, jakie podano — i dopiero po szyfrowaniu.</summary>
    [Fact]
    public async Task PoUwierzytelnieniu_SerwerDostajeDokladnieToHaslo()
    {
        FakeRfbServerOptions options = ServerOptions(5);
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("tajne-haslo", options.RsaAes.ReceivedPassword);
        Assert.Equal(string.Empty, options.RsaAes.ReceivedUsername);
    }

    [Theory]
    [InlineData(FakeRsaAesServer.SubtypePasswordOnly, (byte)2)]
    [InlineData(FakeRsaAesServer.SubtypeUserAndPassword, (byte)1)]
    public async Task WariantPoswiadczen_TrafiaDoInformacjiOSerwerze(byte serverSubtype, byte expected)
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa =>
        {
            rsa.Subtype = serverSubtype;
            rsa.ExpectedUsername = string.Empty;
        });
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        RfbServerInfo info = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(expected, info.RsaAesCredentialSubtype);
        Assert.NotNull(info.DescribeCredentialSubtype());
    }

    /// <summary>Przy VncAuth i None nie ma żadnego wariantu poświadczeń do pokazania.</summary>
    [Fact]
    public async Task PozaRsaAes_WariantPoswiadczenJestPusty()
    {
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.None],
            Screen = Screen(),
        });
        await using var client = new RfbClient(ClientOptions(server, password: null));

        RfbServerInfo info = await client.ConnectAsync(CancellationToken.None);

        Assert.Null(info.RsaAesCredentialSubtype);
        Assert.Null(info.DescribeCredentialSubtype());
    }

    [Fact]
    public async Task WariantZNazwaUzytkownika_WysylaNazweIHaslo()
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa =>
        {
            rsa.Subtype = FakeRsaAesServer.SubtypeUserAndPassword;
            rsa.ExpectedUsername = "serwis";
        });
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server, username: "serwis"));

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("serwis", options.RsaAes.ReceivedUsername);
        Assert.Equal("tajne-haslo", options.RsaAes.ReceivedPassword);
    }

    [Fact]
    public async Task BrakNazwyUzytkownika_GdySerwerJejZada_WysylaPustaNazweIPytaSerwer()
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa =>
        {
            rsa.Subtype = FakeRsaAesServer.SubtypeUserAndPassword;
            rsa.ExpectedUsername = string.Empty; // serwer, któremu pusta nazwa wystarcza
        });
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(string.Empty, options.RsaAes.ReceivedUsername);
        Assert.Equal("tajne-haslo", options.RsaAes.ReceivedPassword);
    }

    /// <summary>
    /// Ten sam wariant, ale serwer pustej nazwy NIE przyjmuje — dopiero jego odmowa uprawnia nas do
    /// powiedzenia, że nazwa użytkownika jest naprawdę potrzebna. Komunikat musi to rozróżniać,
    /// bo od tego zależy, czy operator idzie szukać nazwy konta, czy hasła.
    /// </summary>
    [Fact]
    public async Task PustaNazwaOdrzuconaPrzezSerwer_MowiZeNazwaJestJednakPotrzebna()
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa =>
        {
            rsa.Subtype = FakeRsaAesServer.SubtypeUserAndPassword;
            rsa.ExpectedUsername = "serwis";
        });
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        var ex = await Assert.ThrowsAsync<RfbAuthenticationException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("PUSTĄ nazwą użytkownika", ex.Message, StringComparison.Ordinal);
        Assert.Contains("username", ex.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, options.RsaAes.ReceivedUsername);
    }

    /// <summary>
    /// Nazwa użytkownika BYŁA podana i serwer i tak odmówił — wtedy nie wolno pisać „brakuje nazwy”,
    /// bo równie dobrze złe może być hasło. `probe.exe` rozpoznaje ten komunikat jako „spytaj o hasło
    /// jeszcze raz” (ConnectionHelper.IsWrongPassword).
    /// </summary>
    [Fact]
    public async Task ZlaNazwaLubHaslo_NieUdajeZeBrakujeNazwyUzytkownika()
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa =>
        {
            rsa.Subtype = FakeRsaAesServer.SubtypeUserAndPassword;
            rsa.ExpectedUsername = "serwis";
        });
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server, username: "nie-ten-uzytkownik"));

        var ex = await Assert.ThrowsAsync<RfbAuthenticationException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("odrzucił poświadczenia RSA-AES", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PUSTĄ nazwą", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Brak hasła w konfiguracji ma dać ten sam rodzaj czytelnego błędu co przy VncAuth — z tą samą
    /// frazą „wymaga hasła VNC”, bo po niej <c>probe.exe</c> rozpoznaje, że ma zapytać operatora
    /// o hasło i ponowić próbę zamiast pomijać maszynę (<c>ConnectionHelper.IsRequiresPassword</c>).
    /// </summary>
    [Fact]
    public async Task BrakHasla_DajeCzytelnyBlad()
    {
        await using var server = new FakeRfbServer(ServerOptions(5));
        await using var client = new RfbClient(ClientOptions(server, password: null));

        var ex = await Assert.ThrowsAsync<RfbAuthenticationException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("nie podano hasła", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wymaga hasła VNC", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Najważniejsza asercja bezpieczeństwa tej ścieżki: gdy serwer nie potwierdzi własnego klucza
    /// publicznego, klient MA przerwać, zanim wyśle hasło. Inaczej wystarczyłoby stanąć w środku
    /// połączenia, żeby je wyłudzić.
    /// </summary>
    [Fact]
    public async Task ZlySkrotKluczaSerwera_PrzerywaPrzedWyslaniemHasla()
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa => rsa.CorruptServerHash = true);
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        var ex = await Assert.ThrowsAsync<RfbAuthenticationException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("nie potwierdził własnego klucza publicznego", ex.Message, StringComparison.Ordinal);
        Assert.Null(options.RsaAes.ReceivedPassword);
    }

    [Fact]
    public async Task ListaKilkuWariantow_WybieraNajmocniejszy()
    {
        FakeRfbServerOptions options = ServerOptions(129);
        options.SecurityTypes = [13, 133, 5, 129, 192];
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        RfbServerInfo info = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(RfbSecurityType.RA2_256, info.UsedSecurityType);
        Assert.Equal(new byte[] { 13, 133, 5, 129, 192 }, info.OfferedSecurityTypes);
    }

    [Fact]
    public async Task SesjaSzyfrowana_NieWysylaZadnegoKomunikatuWejscia()
    {
        await using var server = new FakeRfbServer(ServerOptions(5));
        await using var client = new RfbClient(ClientOptions(server));

        await client.ConnectAsync(CancellationToken.None);
        await client.CaptureAsync(new RectInt(0, 0, 32, 16), incremental: false, CancellationToken.None);
        await client.CaptureAsync(new RectInt(0, 0, 16, 8), incremental: true, CancellationToken.None);

        Assert.All(server.AllReceivedMessageTypes, type => Assert.True(type is 2 or 3, $"Klient wysłał komunikat typu {type}."));
        Assert.DoesNotContain((byte)0, server.AllReceivedMessageTypes); // SetPixelFormat
        Assert.DoesNotContain((byte)4, server.AllReceivedMessageTypes); // KeyEvent
        Assert.DoesNotContain((byte)5, server.AllReceivedMessageTypes); // PointerEvent
        Assert.DoesNotContain((byte)6, server.AllReceivedMessageTypes); // ClientCutText
    }

    /// <summary>
    /// Kilka aktualizacji z rzędu sprawdza, czy licznik liczb jednorazowych idzie w obie strony tak
    /// samo. Rozjechany licznik daje „niepoprawny znacznik” dopiero przy DRUGIEJ wiadomości, więc test
    /// jednego pobrania by tego nie złapał.
    /// </summary>
    [Fact]
    public async Task WieleAktualizacji_LicznikiObuStronPozostajaZgodne()
    {
        FakeRfbServerOptions options = ServerOptions(129);
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));
        await client.ConnectAsync(CancellationToken.None);

        for (int i = 0; i < 12; i++)
        {
            var screen = new RgbaImage(32, 16);
            screen.Fill(new Rgba((byte)(i * 8), 60, 90));
            server.Screen = screen;

            RgbaImage image = await client.CaptureAsync(
                new RectInt(0, 0, 32, 16), incremental: false, CancellationToken.None);

            Assert.Equal(new Rgba((byte)(i * 8), 60, 90), image.GetPixel(1, 1));
        }
    }


    [Theory]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0x01, 0x00, 0x00 })] // parzysty
    public async Task SlabyWykladnikRsaSerwera_OdrzuconyPrzedWyslaniemHasla(byte[] exponent)
    {
        FakeRfbServerOptions options = ServerOptions(5, rsa => rsa.OverrideServerExponent = exponent);
        await using var server = new FakeRfbServer(options);
        await using var client = new RfbClient(ClientOptions(server));

        RfbAuthenticationException ex = await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("wykładnik", ex.Message, StringComparison.Ordinal);
        Assert.Null(options.RsaAes.ReceivedPassword);
    }

    [Fact]
    public async Task PrzypietyOdciskKlucza_Zgodny_LaczySie()
    {
        using RSA key = RSA.Create(2048);
        FakeRfbServerOptions options = ServerOptions(5, rsa => rsa.FixedServerKey = key);
        await using var server = new FakeRfbServer(options);
        RfbClientOptions clientOptions = ClientOptions(server);
        clientOptions.ExpectedServerKeySha256 = Fingerprint(key, 2048).ToUpperInvariant();
        await using var client = new RfbClient(clientOptions);

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("tajne-haslo", options.RsaAes.ReceivedPassword);
    }

    [Fact]
    public async Task PrzypietyOdciskKlucza_InnyKluczSerwera_PrzerwanePrzedWyslaniemHasla()
    {
        using RSA pinned = RSA.Create(2048);
        using RSA other = RSA.Create(2048);
        FakeRfbServerOptions options = ServerOptions(5, rsa => rsa.FixedServerKey = other);
        await using var server = new FakeRfbServer(options);
        RfbClientOptions clientOptions = ClientOptions(server);
        clientOptions.ExpectedServerKeySha256 = Fingerprint(pinned, 2048);
        await using var client = new RfbClient(clientOptions);

        RfbAuthenticationException ex = await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("odcisk", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Fingerprint(other, 2048), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(options.RsaAes.ReceivedPassword);
    }

    /// <summary>Odcisk tak, jak liczy go klient: SHA-256 z modułu i wykładnika w postaci z przewodu (dopełnione do długości klucza).</summary>
    private static string Fingerprint(RSA key, int bits)
    {
        RSAParameters p = key.ExportParameters(false);
        int len = (bits + 7) / 8;
        byte[] wire = new byte[len * 2];
        p.Modulus!.CopyTo(wire, len - p.Modulus!.Length);
        p.Exponent!.CopyTo(wire, (2 * len) - p.Exponent!.Length);
        return Convert.ToHexString(SHA256.HashData(wire)).ToLowerInvariant();
    }
}
