using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>Jeden prostokąt odebranej aktualizacji (pozycja, rozmiar, kodowanie).</summary>
internal sealed record ViewerRect(int X, int Y, int W, int H, int Encoding);

internal sealed class ScriptedVncViewer : IAsyncDisposable
{
    public static readonly int[] RealVnc4Encodings = [-239, -223, 16, 1, 5, 2, 0];

    public static readonly int[] RealVncExtendedEncodings = [16, 5, 1, 0, 2, -239, -240, -223, -224, -307, -308, -23, -247];

    /// <summary>Przeglądarka BEZ kursora lokalnego (<c>UseLocalCursor = 0</c> albo nieznany wariant): bez −239.</summary>
    public static readonly int[] NoCursorEncodings = [16, 5, 1, 0, -223];

    /// <summary>Bajty <c>SetEncodings</c>: Hextile, CopyRect, Raw.</summary>
    public static readonly int[] OldVncAlertEncodings = [5, 1, 0];

    public static byte[] LowColour8 => QtPixelFormat.Wire(8, 6, false, true, 3, 3, 3, 4, 2, 0);

    public static byte[] VeryLowColour8 => QtPixelFormat.Wire(8, 3, false, true, 1, 1, 1, 2, 1, 0);

    /// <summary>Pełny kolor przeglądarki na Windows: 32 bpp, depth 24, max 255, przesunięcia 16/8/0.</summary>
    public static byte[] FullColour32 => QtPixelFormat.Wire(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private int _bytesPerPixel;

    private ScriptedVncViewer(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string DesktopName { get; private set; } = "";

    /// <summary>Format z <c>ServerInit</c> (16 B z przewodu).</summary>
    public byte[] ServerFormat { get; private set; } = [];

    /// <summary>Uzgodnienie RFB 3.3 jak przeglądarka: wersja, typ zabezpieczeń od serwera (None), <c>ClientInit</c> (shared = 1), <c>ServerInit</c>.</summary>
    public static async Task<ScriptedVncViewer> ConnectAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", port, ct);
        var viewer = new ScriptedVncViewer(tcp);
        try
        {
            byte[] version = await viewer.ReadExactAsync(12, ct);
            string text = Encoding.ASCII.GetString(version);
            if (!text.StartsWith("RFB 003.", StringComparison.Ordinal)) throw new IOException($"Nieoczekiwana wersja: {text}");
            await viewer.SendAsync("RFB 003.003\n"u8.ToArray(), ct);
            uint security = BinaryPrimitives.ReadUInt32BigEndian(await viewer.ReadExactAsync(4, ct));
            if (security != 1) throw new IOException($"Serwer zażądał zabezpieczenia {security}, symulacja zna tylko None.");
            await viewer.SendAsync([1], ct); // ClientInit: shared
            byte[] init = await viewer.ReadExactAsync(24, ct);
            viewer.Width = BinaryPrimitives.ReadUInt16BigEndian(init);
            viewer.Height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
            viewer.ServerFormat = init.AsSpan(4, 16).ToArray();
            viewer._bytesPerPixel = init[4] / 8;
            int nameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
            viewer.DesktopName = Encoding.UTF8.GetString(await viewer.ReadExactAsync(nameLength, ct));
            return viewer;
        }
        catch
        {
            await viewer.DisposeAsync();
            throw;
        }
    }

    /// <summary><c>SetPixelFormat</c> (typ 0) z podanym PIXEL_FORMAT; od tej chwili aktualizacje czytane w tym formacie.</summary>
    public async Task SetPixelFormatAsync(byte[] pixelFormat, CancellationToken ct)
    {
        byte[] msg = new byte[20];
        pixelFormat.CopyTo(msg, 4);
        await SendAsync(msg, ct);
        _bytesPerPixel = pixelFormat[0] / 8;
    }

    /// <summary><c>SetEncodings</c> (typ 2).</summary>
    public async Task SetEncodingsAsync(int[] encodings, CancellationToken ct)
    {
        byte[] msg = new byte[4 + (encodings.Length * 4)];
        msg[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4 + (i * 4)), encodings[i]);
        await SendAsync(msg, ct);
    }

    /// <summary>Dowolne bajty jednym zapisem (np. kilka wiadomości w jednym pakiecie TCP).</summary>
    public async Task SendRawAsync(byte[] data, CancellationToken ct) => await SendAsync(data, ct);

    /// <summary><c>FramebufferUpdateRequest</c> (typ 3) na cały ekran.</summary>
    public async Task RequestAsync(bool incremental, CancellationToken ct)
    {
        byte[] msg = new byte[10];
        msg[0] = 3;
        msg[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)Height);
        await SendAsync(msg, ct);
    }

    /// <summary>
    /// Czyta jedną wiadomość <c>FramebufferUpdate</c> (pomija Bell/CutText/ColourMap) i zwraca jej prostokąty; piksele
    /// pomija według bieżącego formatu (Hextile — strukturalnie po podkodowaniach, Raw, CopyRect, Cursor −239).
    /// </summary>
    public async Task<IReadOnlyList<ViewerRect>> ReadUpdateAsync(CancellationToken ct)
    {
        while (true)
        {
            byte type = (await ReadExactAsync(1, ct))[0];
            switch (type)
            {
                case 0:
                    break;
                case 1:
                    byte[] head = await ReadExactAsync(5, ct);
                    await ReadExactAsync(6 * BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(3)), ct);
                    continue;
                case 2:
                    continue;
                case 3:
                    byte[] cut = await ReadExactAsync(7, ct);
                    await ReadExactAsync((int)BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3)), ct);
                    continue;
                default:
                    throw new IOException($"Nieznany typ wiadomości serwera: {type}.");
            }

            byte[] header = await ReadExactAsync(3, ct);
            int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
            var rects = new List<ViewerRect>(count);
            for (int i = 0; i < count; i++)
            {
                byte[] r = await ReadExactAsync(12, ct);
                var rect = new ViewerRect(
                    BinaryPrimitives.ReadUInt16BigEndian(r), BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2)),
                    BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4)), BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6)),
                    BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(8)));
                rects.Add(rect);
                switch (rect.Encoding)
                {
                    case -239: // RichCursor: w*h*bpp + maska
                        await ReadExactAsync((rect.W * rect.H * _bytesPerPixel) + (((rect.W + 7) / 8) * rect.H), ct);
                        break;
                    case -223:
                        break;
                    case 0:
                        await ReadExactAsync(rect.W * rect.H * _bytesPerPixel, ct);
                        break;
                    case 1:
                        await ReadExactAsync(4, ct);
                        break;
                    case 5:
                        await SkipHextileAsync(rect, ct);
                        break;
                    default:
                        throw new IOException($"Nieobsługiwane kodowanie {rect.Encoding} (przeglądarka skryptowa).");
                }
            }

            return rects;
        }
    }

    /// <summary>Czy w ciągu <paramref name="window"/> przyszły jakiekolwiek bajty (nie zjada ich).</summary>
    public async Task<bool> DataArrivesWithinAsync(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (_tcp.Client.Available > 0) return true;
            await Task.Delay(10);
        }

        return _tcp.Client.Available > 0;
    }

    /// <summary>Czy serwer zamknął połączenie (odczyt zwraca 0 albo błąd) w ciągu <paramref name="window"/>.</summary>
    public async Task<bool> ClosedByServerWithinAsync(TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        byte[] one = new byte[1];
        try
        {
            while (true)
            {
                int n = await _stream.ReadAsync(one, cts.Token);
                if (n == 0) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _tcp.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        return ValueTask.CompletedTask;
    }

    private async Task SkipHextileAsync(ViewerRect rect, CancellationToken ct)
    {
        for (int ty = rect.Y; ty < rect.Y + rect.H; ty += 16)
        {
            for (int tx = rect.X; tx < rect.X + rect.W; tx += 16)
            {
                int tw = Math.Min(16, rect.X + rect.W - tx), th = Math.Min(16, rect.Y + rect.H - ty);
                byte subenc = (await ReadExactAsync(1, ct))[0];
                if ((subenc & 1) != 0)
                {
                    await ReadExactAsync(tw * th * _bytesPerPixel, ct);
                    continue;
                }

                int skip = 0;
                if ((subenc & 2) != 0) skip += _bytesPerPixel;
                if ((subenc & 4) != 0) skip += _bytesPerPixel;
                if (skip > 0) await ReadExactAsync(skip, ct);
                if ((subenc & 8) != 0)
                {
                    int n = (await ReadExactAsync(1, ct))[0];
                    int each = ((subenc & 16) != 0 ? _bytesPerPixel : 0) + 2;
                    if (n * each > 0) await ReadExactAsync(n * each, ct);
                }
            }
        }
    }

    private async Task SendAsync(byte[] data, CancellationToken ct) => await _stream.WriteAsync(data, ct);

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        if (count > 0) await _stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}
