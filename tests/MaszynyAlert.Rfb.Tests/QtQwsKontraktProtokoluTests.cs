using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtQwsKontraktProtokoluTests
{
    private static readonly byte[] StackGarbage = [0xA5, 0x5A, 0xC3];

    private const string QtName = "Qt for Embedded Linux VNC Server";

    // ---- SetPixelFormat → SetEncodings → FramebufferUpdateRequest w readClient przy każdym podziale --------------

    [Fact]
    public async Task Klient520_BajtyPoServerInit_ModelQt48_KazdyPodzialNaSegmentyTcp_TenSamPrzebieg()
    {
        byte[] bytes = await CaptureQt16ClientBytesAsync();
        Assert.Equal(60, bytes.Length);
        byte[] first = bytes[..50];
        byte[] second = bytes[50..];
        Assert.Equal(3, second[0]); // ponowione żądanie po odpowiedzi „sam kursor”

        QtQwsServerModel reference = Run520Model([first], second);
        Assert.Equal(
            new[]
            {
                "SetPixelFormat 16 bpp", "SetEncodings [5,1,0,-239]", "FramebufferUpdateRequest pełne", "wysłano kursor −239 (2 B/piksel)",
                "FramebufferUpdateRequest pełne", "wysłano klatkę Hextile<16> (2 B/piksel)",
            },
            reference.Events);
        Assert.Equal(QtQwsServerModel.ServerInitFormat(16), reference.ClientFormat);
        Assert.True(reference.SameEndian);
        Assert.True(reference.NeedConversion);
        Assert.Equal(2, reference.ClientBytesPerPixel);
        Assert.True(reference.HasClientCursor);
        Assert.False(reference.Crashed);
        Assert.Equal(0, reference.BytesAvailable);

        int partitions = 0;
        foreach (int[] cuts in Partitions(first.Length))
        {
            QtQwsServerModel model = Run520Model(Split(first, cuts), second);
            Assert.Equal(reference.Events, model.Events);
            Assert.Equal(reference.ClientFormat, model.ClientFormat);
            Assert.Equal(reference.Encoder, model.Encoder);
            Assert.Equal(reference.SameEndian, model.SameEndian);
            Assert.Equal(reference.NeedConversion, model.NeedConversion);
            Assert.False(model.Crashed);
            Assert.Equal(0, model.BytesAvailable);
            partitions++;
        }

        Assert.True(partitions > 3000, $"sprawdzono {partitions} podziałów");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModelQt48_SekwencjaSprzed156_PoRealVnc_AwariaAlbo1BajtNaPiksel(bool dirtyCursor)
    {
        byte[] old = [2, 0, 0, 3, 0, 0, 0, 5, 0, 0, 0, 1, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 64, 0, 32];
        var model = new QtQwsServerModel(16, Bgr233, dirtyCursor);
        model.ReadyRead(old);

        if (dirtyCursor)
        {
            Assert.True(model.Crashed);
        }
        else
        {
            Assert.Contains("wysłano klatkę Hextile<16> (1 B/piksel)", model.Events);
        }
    }

    [Fact]
    public async Task KlientQt_UzgadnianieKrokPoKroku_NicNieWysylaPrzedOdpowiedziaSerwera()
    {
        await using var server = new QtLikeServer(QtQwsServerModel.ServerInitBytes(16, 64, 32, StackGarbage)) { CheckPipelining = true };
        server.Start();
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(5) }))
        {
            await client.ConnectAsync(CancellationToken.None);
            await server.EncodingsReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, server.BytesBeforeAuth);
        Assert.Equal(0, server.BytesBeforeServerInit);
    }


    public static TheoryData<int, bool> QtDepths() => new()
    {
        { 32, false }, { 24, false }, { 18, true }, { 16, true }, { 15, true }, { 12, true },
    };

    [Theory]
    [MemberData(nameof(QtDepths))]
    public async Task SerwerQt_KazdaGlebiaPelnegoKoloru_FormatOdeslanyZServerInit_StanQtISpojneDekodowanie(int depth, bool expectedNeedConversion)
    {
        byte[] serverInit = QtQwsServerModel.ServerInitBytes(depth, 64, 32, StackGarbage);
        await using var server = new QtLikeServer(serverInit);
        server.Start();
        RfbPixelFormat clientFormat;
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(5) }))
        {
            await client.ConnectAsync(CancellationToken.None);
            clientFormat = client.ServerInfo!.PixelFormat;
            await server.EncodingsReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        byte[] sent = server.ClientBytesAfterInit;
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, sent[..4]);
        Assert.Equal(serverInit.AsSpan(4, 13).ToArray(), sent[4..17]);  // pola PIXEL_FORMAT bajt w bajt
        Assert.Equal(new byte[] { 0, 0, 0 }, sent[17..20]);
        Assert.Equal(2, sent[20]);                                       // dalej SetEncodings

        var model = new QtQwsServerModel(depth, Bgra32Leftover, leftoverDirtyCursor: false);
        model.ReadyRead(sent);
        Assert.Equal(QtQwsServerModel.ServerInitFormat(depth), model.ClientFormat);
        Assert.False(model.Discarded);
        Assert.True(model.SameEndian);
        Assert.Equal(expectedNeedConversion, model.NeedConversion);
        Assert.Equal(clientFormat.BytesPerPixel, model.ClientBytesPerPixel);

        if (clientFormat.BitsPerPixel == 24)
        {
            Assert.Equal(3, model.ClientBytesPerPixel);
            return;
        }

        foreach ((int r, int g, int b) in new[] { (255, 0, 0), (200, 16, 40), (255, 255, 255), (8, 252, 8), (0, 0, 0) })
        {
            Rgba expected = Expected(clientFormat, r, g, b);
            Assert.Equal(expected, clientFormat.ToRgba(clientFormat.ReadPixel(model.ConvertPixel(r, g, b))));
            if (!model.NeedConversion)
            {
                Assert.Equal(expected, clientFormat.ToRgba(clientFormat.ReadPixel(model.ScreenBytes(r, g, b))));
            }
        }
    }

    [Fact]
    public void SerwerQt_ServerInitZawszeLittleEndian()
    {
        foreach (int depth in new[] { 32, 24, 18, 16, 15, 12, 8, 4 })
        {
            Assert.False(QtQwsServerModel.ServerInitFormat(depth)!.BigEndian);
        }
    }


    public static TheoryData<string?, bool> Names() => new()
    {
        { QtName, true },
        { "qt for embedded linux vnc server", true },
        { "QT FOR EMBEDDED LINUX VNC SERVER", true },
        { QtName + "\0", true },
        { " " + QtName + "\r\n", true },
        { "Qt\tfor  Embedded\nLinux VNC Server", true },
        { "﻿" + QtName, true },
        { QtName + " (520)", true },
        { "HMI " + QtName, true },
        { "68EDA4721759", false },
        { "WinCE", false },
        { "OPENCNC2", false },
        { "TightVNC", false },
        { "", false },
        { "   ", false },
        { null, false },
        { "Qt VNC Server", false },
        { "Qt for Embedded Linux", false },
        { "Qt for Embedded Linux VNC", false },
        { "QtforEmbeddedLinuxVNCServer", false },
        { "Qt for Embedded Linux VNC-Server", false },
        { "Qt Embedded Linux VNC Server", false },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void IsQtEmbedded_TablicaNazw(string? name, bool expected) =>
        Assert.Equal(expected, QtEmbeddedVncServer.IsQtEmbedded(name));

    [Fact]
    public async Task NazwaZNulNaKoncu_PrzezServerInit_KlientWysylaSetPixelFormat()
    {
        byte[] name = Encoding.ASCII.GetBytes(QtName + "\0");
        byte[] serverInit = QtQwsServerModel.ServerInitBytes(16, 64, 32, StackGarbage)[..20];
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)name.Length);
        await using var server = new QtLikeServer([.. serverInit, .. length, .. name]);
        server.Start();
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(5) }))
        {
            await client.ConnectAsync(CancellationToken.None);
            await server.EncodingsReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QtName + "\0", client.ServerInfo!.DesktopName);
        }

        Assert.Equal(0, server.ClientBytesAfterInit[0]);
    }

    // ---- strażnik „tylko odczyt” niezależny od nazw metod ---------------------------------------------------------

    /// <summary>
    /// <c>ReadOnlySafetyTests.RfbClient_HasNoInputApi</c> sprawdza tylko NAZWY metod. Ten test czyta IL wszystkich metod
    /// <see cref="RfbClient"/> (z maszynami stanów async i lambdami): <c>SendAsync</c> wołają wyłącznie znane kroki
    /// uzgadniania i trzy wiadomości (SetPixelFormat z ServerInit, SetEncodings, FramebufferUpdateRequest),
    /// <c>SendSetPixelFormatFromServerInitAsync</c> woła tylko <c>ConnectAsync</c>, a do strumienia pisze wyłącznie
    /// <c>SendAsync</c>. Nowa metoda wysyłająca (pod dowolną nazwą) albo zapis z pominięciem <c>SendAsync</c> zmienia te zbiory.
    /// </summary>
    [Fact]
    public void RfbClient_IL_WysylkaTylkoZeZnanychMiejsc()
    {
        MethodInfo send = typeof(RfbClient).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo setPixelFormat = typeof(RfbClient).GetMethod("SendSetPixelFormatFromServerInitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.Equal(
            new[]
            {
                "DoVncAuthAsync", "NegotiateProtocolVersionAsync", "NegotiateSecurityAsync", "RequestUpdateAsync",
                "SendClientInitAsync", "SendSetEncodingsAsync", "SendSetPixelFormatFromServerInitAsync",
            },
            CallersOf(m => Same(m, send)));
        Assert.Equal(new[] { "ConnectAsync" }, CallersOf(m => Same(m, setPixelFormat)));
        Assert.Equal(
            new[] { "SendAsync" },
            CallersOf(m => m.Name is "Write" or "WriteAsync" or "WriteByte" or "BeginWrite"
                && typeof(Stream).IsAssignableFrom(m.DeclaringType)));
        Assert.Empty(CallersOf(m => m.DeclaringType == typeof(Socket) && m.Name.StartsWith("Send", StringComparison.Ordinal)));
    }

    public static TheoryData<string, int> Formats() => new()
    {
        { "RGB565 LE (520)", 0 }, { "BGRA32 LE", 1 }, { "RGB565 BE", 2 }, { "BGRA32 BE", 3 }, { "RGB555", 4 },
        { "RGB444 w 16 bpp", 5 }, { "BGR233 8 bpp pełny kolor", 6 },
    };

    private static readonly RfbPixelFormat[] FormatList =
    [
        RfbPixelFormat.Rgb565LittleEndian,
        RfbPixelFormat.Bgra32LittleEndian,
        RfbPixelFormat.Rgb565LittleEndian with { BigEndian = true },
        RfbPixelFormat.Bgra32LittleEndian with { BigEndian = true },
        new(16, 15, false, true, 31, 31, 31, 10, 5, 0),
        new(16, 12, false, true, 15, 15, 15, 8, 4, 0),
        new(8, 8, false, true, 7, 7, 3, 0, 3, 6),
    ];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task SerwerQt_DowolnyFormatPelnegoKoloru_JedenSetPixelFormatZPolamiServerInit(string label, int index)
    {
        _ = label;
        RfbPixelFormat format = FormatList[index];
        byte[] init = new byte[24 + QtName.Length];
        BinaryPrimitives.WriteUInt16BigEndian(init, 64);
        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), 32);
        format.Write(init.AsSpan(4, 16));
        StackGarbage.CopyTo(init, 17);
        BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)QtName.Length);
        Encoding.ASCII.GetBytes(QtName).CopyTo(init, 24);

        await using var server = new QtLikeServer(init, async (_, s, n, ct) =>
        {
            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 64);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 32);
            await s.WriteAsync(header, ct);
            await s.WriteAsync(new byte[64 * 32 * format.BytesPerPixel], ct);
        });
        server.Start();
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(5) }))
        {
            await client.ConnectAsync(CancellationToken.None);
            var full = new RectInt(0, 0, 64, 32);
            await client.CaptureAsync(full, incremental: false, CancellationToken.None);
            await client.TryCaptureAsync(new RectInt(4, 4, 20, 8), incremental: true, TimeSpan.FromSeconds(3), CancellationToken.None);
            await client.TryCaptureAsync(new RectInt(4, 4, 20, 8), incremental: true, TimeSpan.FromSeconds(3), CancellationToken.None);
            await client.CaptureAsync(full, incremental: false, CancellationToken.None);
        }

        byte[] sent = await server.WaitForTypesAsync(6);
        Assert.Equal(new byte[] { 0, 2, 3, 3, 3, 3 }, server.MessageTypes);
        Assert.Equal(init.AsSpan(4, 13).ToArray(), sent[4..17]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, sent[..4]);
        Assert.Equal(new byte[] { 0, 0, 0 }, sent[17..20]);
        Assert.Equal(format, RfbPixelFormat.Parse(sent.AsSpan(4, 16)));
    }

    // ---- pomocnicze -------------------------------------------------------------------------------------------------

    private static readonly QtQwsServerModel.PixelFormat Bgr233 = new(8, 8, false, true, 3, 3, 2, 0, 3, 6);

    private static readonly QtQwsServerModel.PixelFormat Bgra32Leftover = new(32, 24, false, true, 8, 8, 8, 16, 8, 0);

    private static Rgba Expected(RfbPixelFormat f, int r, int g, int b)
    {
        static byte Channel(int c8, ushort max)
        {
            int bits = 0;
            for (int m = max; m != 0; m >>= 1) bits++;
            return (byte)((c8 >> (8 - bits)) * 255 / max);
        }

        return new Rgba(Channel(r, f.RedMax), Channel(g, f.GreenMax), Channel(b, f.BlueMax));
    }

    private static QtQwsServerModel Run520Model(IEnumerable<byte[]> firstChunks, byte[] second)
    {
        var model = new QtQwsServerModel(16, Bgr233, leftoverDirtyCursor: true);
        foreach (byte[] chunk in firstChunks) model.ReadyRead(chunk);
        model.ReadyRead(second[..3]);
        model.ReadyRead(second[3..]);
        return model;
    }

    private static List<byte[]> Split(byte[] data, int[] cuts)
    {
        var chunks = new List<byte[]>();
        int start = 0;
        foreach (int cut in cuts.Append(data.Length))
        {
            chunks.Add(data[start..cut]);
            start = cut;
        }

        return chunks;
    }

    /// <summary>Wszystkie podziały z 1 i 2 cięciami, bajt po bajcie i 2000 losowych (ziarno stałe).</summary>
    private static IEnumerable<int[]> Partitions(int length)
    {
        for (int a = 1; a < length; a++) yield return [a];
        for (int a = 1; a < length; a++)
        {
            for (int b = a + 1; b < length; b++) yield return [a, b];
        }

        yield return Enumerable.Range(1, length - 1).ToArray();
        var random = new Random(156);
        for (int i = 0; i < 2000; i++)
        {
            int count = random.Next(3, 20);
            yield return Enumerable.Range(0, count).Select(_ => random.Next(1, length)).Distinct().Order().ToArray();
        }
    }

    private static async Task<byte[]> CaptureQt16ClientBytesAsync()
    {
        await using var server = new QtLikeServer(QtQwsServerModel.ServerInitBytes(16, 64, 32, StackGarbage), async (_, s, n, ct) =>
        {
            byte[] fbu = [0, 0, 0, 1];
            if (n == 0)
            {
                byte[] cursor = new byte[12 + (16 * 16 * 2) + (2 * 16)];
                BinaryPrimitives.WriteUInt16BigEndian(cursor.AsSpan(4), 16);
                BinaryPrimitives.WriteUInt16BigEndian(cursor.AsSpan(6), 16);
                BinaryPrimitives.WriteInt32BigEndian(cursor.AsSpan(8), -239);
                await s.WriteAsync((byte[])[.. fbu, .. cursor], ct);
                return;
            }

            byte[] raw = new byte[12 + (64 * 32 * 2)];
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4), 64);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(6), 32);
            await s.WriteAsync((byte[])[.. fbu, .. raw], ct);
        });
        server.Start();
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(5) }))
        {
            await client.ConnectAsync(CancellationToken.None);
            await client.CaptureAsync(new RectInt(0, 0, 64, 32), incremental: false, CancellationToken.None);
        }

        return await server.WaitForTypesAsync(4);
    }

    private static bool Same(MethodBase candidate, MethodInfo target) =>
        candidate.Module == target.Module && candidate.MetadataToken == target.MetadataToken;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Logiczne nazwy metod <see cref="RfbClient"/> (maszyna stanów async/lambda → metoda źródłowa), które wołają metodę spełniającą warunek.</summary>
    private static string[] CallersOf(Func<MethodBase, bool> predicate)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type type in AllTypes(typeof(RfbClient)))
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (CalledMethods(method).Any(predicate)) result.Add(LogicalName(type, method));
            }
        }

        return [.. result];
    }

    private static IEnumerable<Type> AllTypes(Type root)
    {
        yield return root;
        foreach (Type nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (Type t in AllTypes(nested)) yield return t;
        }
    }

    private static string LogicalName(Type type, MethodBase method)
    {
        Match fromType = Regex.Match(type.Name, "^<+([A-Za-z0-9_]+)>");
        if (fromType.Success) return fromType.Groups[1].Value;
        Match fromMethod = Regex.Match(method.Name, "^<+([A-Za-z0-9_]+)>");
        return fromMethod.Success ? fromMethod.Groups[1].Value : method.Name;
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        int i = 0;
        while (i < il.Length)
        {
            OpCode op;
            if (il[i] == 0xFE)
            {
                op = OpCodesByValue[unchecked((short)(0xFE00 | il[i + 1]))];
                i += 2;
            }
            else
            {
                op = OpCodesByValue[il[i]];
                i += 1;
            }

            int size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, i) * 4),
                _ => 4,
            };
            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? target = null;
                try
                {
                    target = method.Module.ResolveMethod(
                        BitConverter.ToInt32(il, i),
                        method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }

                if (target is not null) yield return target;
            }

            i += size;
        }
    }
}
