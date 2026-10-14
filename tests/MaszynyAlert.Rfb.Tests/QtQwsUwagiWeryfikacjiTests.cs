using System.Buffers.Binary;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtQwsUwagiWeryfikacjiTests
{
    private const int Width = 32;
    private const int Height = 16;

    [Fact]
    public async Task SerwerQtPaletowy_FormatPoprzedniegoKlienta8bpp_Charakterystyka_SzaraKlatkaBezBledu()
    {
        const byte Bgr233Red = 0x07; // R = 7 (max) przy przesunięciu 0, G = B = 0
        byte[] serverInit = QtQwsServerModel.ServerInitBytes(8, Width, Height, [0xA5, 0x5A, 0xC3]);
        await using var server = new QtLikeServer(serverInit, async (srv, s, n, ct) =>
        {
            if (n == 0 && srv.Encodings.Contains(-239))
            {
                byte[] cursor = new byte[4 + 12 + (16 * 16 * 1) + (2 * 16)];
                BinaryPrimitives.WriteUInt16BigEndian(cursor.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(cursor.AsSpan(8), 16);
                BinaryPrimitives.WriteUInt16BigEndian(cursor.AsSpan(10), 16);
                BinaryPrimitives.WriteInt32BigEndian(cursor.AsSpan(12), -239);
                await s.WriteAsync(cursor, ct);
                return;
            }

            // QRfbHextileEncoder<quint8>: każdy kafel 16×16 osobnym prostokątem, kafel jednokolorowy = podkodowanie 2 + tło.
            byte[] header = [0, 0, 0, 2];
            await s.WriteAsync(header, ct);
            for (int i = 0; i < 2; i++)
            {
                byte[] rect = new byte[12 + 2];
                BinaryPrimitives.WriteUInt16BigEndian(rect.AsSpan(0), (ushort)(i * 16));
                BinaryPrimitives.WriteUInt16BigEndian(rect.AsSpan(4), 16);
                BinaryPrimitives.WriteUInt16BigEndian(rect.AsSpan(6), 16);
                BinaryPrimitives.WriteInt32BigEndian(rect.AsSpan(8), RfbEncodings.Hextile);
                rect[12] = 2;
                rect[13] = Bgr233Red;
                await s.WriteAsync(rect, ct);
            }
        });
        server.Start();

        RgbaImage? image = null;
        Exception? error;
        await using (var client = new RfbClient(new RfbClientOptions { Host = "127.0.0.1", Port = server.Port, ReadTimeout = TimeSpan.FromSeconds(5) }))
        {
            error = await Record.ExceptionAsync(async () =>
            {
                await client.ConnectAsync(CancellationToken.None);
                image = await client.CaptureAsync(new RectInt(0, 0, Width, Height), incremental: false, CancellationToken.None);
            });
        }

        // żadnego błędu, klatka zwrócona.
        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal((Width, Height), (image.Width, image.Height));

        // Cała klatka w szarym zastępczym palety (128,128,128), choć serwer wysłał w każdym kaflu czerwień BGR233 0x07.
        var colours = new HashSet<Rgba>();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                colours.Add(image.GetPixel(x, y));
            }
        }

        Assert.Equal(new Rgba(128, 128, 128), Assert.Single(colours));

        Assert.Equal(new byte[] { 2, 3, 3 }, server.MessageTypes);
        Assert.Equal(new[] { 5, 1, 0, -239 }, server.Encodings);
    }
}
