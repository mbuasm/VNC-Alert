using System.Reflection;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class ReadOnlySafetyTests
{
    private const string SetPixelFormatFromServerInit = "SendSetPixelFormatFromServerInitAsync";

    [Fact]
    public async Task FullSession_NeverSendsInputMessages()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        RgbaImage screen = TestScreens.Gradient(64, 48);
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.8",
            SecurityTypes = [(byte)RfbSecurityType.VncAuth],
            Password = "tajne123",
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            Screen = screen,
            SendBellBeforeUpdate = true,
            SendServerCutTextBeforeUpdate = true,
        });

        FakeClientSession session;
        await using (var client = new RfbClient(TestSupport.ClientOptions(server, o => o.Password = "tajne123")))
        {
            await client.ConnectAsync(cts.Token);
            session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);

            for (int i = 0; i < 5; i++)
            {
                await client.CaptureAsync(new RectInt(0, 40, 64, 8), incremental: i > 0, cts.Token);
            }
        }

        IReadOnlyList<byte> types = session.ReceivedMessageTypes;
        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.True(t is 2 or 3, $"Klient wysłał zabronioną wiadomość typu {t}."));
        Assert.DoesNotContain((byte)0, types);
        Assert.DoesNotContain((byte)4, types);
        Assert.DoesNotContain((byte)5, types);
        Assert.DoesNotContain((byte)6, types);
        Assert.All(server.AllReceivedMessageTypes, t => Assert.True(t is 2 or 3));
        Assert.Equal(5, types.Count(t => t == 3));
    }

    /// <summary>
    /// „Strażnik na przyszłość”: klasa <see cref="RfbClient"/> nie może zyskać metody wysyłającej
    /// klawiaturę/mysz/schowek ani zmieniającej format piksela. Jedynym dozwolonym wyjątkiem jest
    /// <c>ReadServerCutTextAsync</c> — to odczyt (i odrzucenie) schowka SERWERA, nie wysyłka.
    /// </summary>
    [Fact]
    public void RfbClient_HasNoInputApi()
    {
        string[] forbidden = ["Key", "Pointer", "CutText", "SetPixelFormat"];
        string[] allowed = ["ReadServerCutTextAsync", SetPixelFormatFromServerInit];

        MethodInfo[] methods = typeof(RfbClient).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        List<string> offenders = methods
            .Select(m => m.Name)
            .Where(name => !allowed.Contains(name, StringComparer.Ordinal))
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, $"RfbClient zyskał metody wejścia: {string.Join(", ", offenders)}.");

        // Żadna metoda wysyłająca nie może być publiczna poza jawnie dozwolonymi.
        string[] publicApi = typeof(RfbClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal) && !n.StartsWith("set_", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // TryCaptureAsync/TryReceiveUpdateAsync: odpowiedniki CaptureAsync/
        // ReceiveUpdateAsync z limitem bezczynności (serwer WinCE/OpenCnc potrafi nie odpowiadać na
        // żądanie przyrostowe w ogóle, dopóki ekran się nie zmieni) — nadal tylko odczyt, zero wejścia.
        Assert.Equal(
            new[]
            {
                "CaptureAsync", "ConnectAsync", "DisposeAsync", "ReceiveUpdateAsync", "RequestUpdateAsync",
                "TryCaptureAsync", "TryReceiveUpdateAsync", "UpdateAsync",
            },
            publicApi);
    }

    [Fact]
    public void RfbClient_SetPixelFormat_TylkoPrywatnaMetodaBezParametruFormatu()
    {
        MethodInfo method = Assert.Single(
            typeof(RfbClient).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly),
            m => m.Name.Contains("SetPixelFormat", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(SetPixelFormatFromServerInit, method.Name);
        Assert.True(method.IsPrivate);
        Assert.Equal(new[] { typeof(CancellationToken) }, method.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public async Task FullSession_SerwerQt_JedenSetPixelFormatNaPoczatku_ZeroWejscia()
    {
        using CancellationTokenSource cts = TestSupport.NewCts();
        await using var server = new FakeRfbServer(new FakeRfbServerOptions
        {
            ProtocolVersion = "3.3",
            SecurityTypes = [(byte)RfbSecurityType.None],
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            Encoding = FakeEncodingMode.Hextile,
            Screen = TestScreens.Gradient(64, 48),
            DesktopName = QtEmbeddedVncServer.DesktopName,
        });

        FakeClientSession session;
        await using (var client = new RfbClient(TestSupport.ClientOptions(server)))
        {
            await client.ConnectAsync(cts.Token);
            session = await server.WaitForSessionAsync(0, TestSupport.ShortWait);
            for (int i = 0; i < 5; i++)
            {
                await client.CaptureAsync(new RectInt(0, 40, 64, 8), incremental: i > 0, cts.Token);
            }
        }

        IReadOnlyList<byte> types = session.ReceivedMessageTypes;
        Assert.Equal((byte)0, types[0]);
        Assert.Single(types, t => t == 0);
        Assert.All(types.Skip(1), t => Assert.True(t is 2 or 3, $"Klient wysłał zabronioną wiadomość typu {t}."));
        Assert.DoesNotContain((byte)4, types);
        Assert.DoesNotContain((byte)5, types);
        Assert.DoesNotContain((byte)6, types);
        Assert.Equal(5, types.Count(t => t == 3));
    }

    [Theory]
    [InlineData("68EDA4721759")]
    [InlineData("WinCE")]
    [InlineData("OPENCNC2")]
    [InlineData("TightVNC")]
    [InlineData("")]
    [InlineData("Qt VNC Server")]
    public async Task SerwerInnyNizQt_ZapisWiadomosciKlientaBajtWBajtJakPrzedPoprawka(string desktopName)
    {
        await using var server = new ScriptedRfbServer
        {
            Width = 64,
            Height = 32,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = desktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, 64, 32, RfbEncodings.Raw), ct);
                await s.WriteAsync(new byte[64 * 32 * 2], ct);
            },
        };
        server.Start();

        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port }))
        {
            await client.ConnectAsync(CancellationToken.None);
            await client.CaptureAsync(new RectInt(0, 0, 64, 32), incremental: false, CancellationToken.None);
        }

        byte[] expected =
        [
            2, 0, 0, 3, // SetEncodings, wypełnienie, liczba = 3
            0, 0, 0, 5, 0, 0, 0, 1, 0, 0, 0, 0, // Hextile, CopyRect, Raw
            3, 0, 0, 0, 0, 0, 0, 64, 0, 32, // FramebufferUpdateRequest, nieprzyrostowy, (0,0) 64×32
        ];
        byte[] actual = server.ClientBytesAfterInit;
        for (int i = 0; actual.Length < expected.Length && i < 50; i++)
        {
            await Task.Delay(20); // serwer mógł jeszcze nie zapisać ostatniej wiadomości
            actual = server.ClientBytesAfterInit;
        }

        Assert.Equal(expected, actual);
    }
}
