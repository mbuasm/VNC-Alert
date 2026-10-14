using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Dekoder kodowania Hextile (RFC 6143 §7.7.4): prostokąt podzielony na kafle 16×16 (brzegowe
/// mniejsze), każdy kafel opisany bitami sub-encoding. Tło i pierwszy plan są „sticky” — obowiązują
/// dla kolejnych kafli w obrębie TEGO SAMEGO prostokąta, dopóki nie zostaną nadpisane (stąd zmienne
/// <c>background</c>/<c>foreground</c> żyją przez cały czas trwania <see cref="DecodeAsync"/>, a nie
/// per kafel). Kafel Raw ignoruje pozostałe bity i nie zmienia sticky tła/pierwszego planu.
/// </summary>
internal static class HextileDecoder
{
    private const byte SubencodingRaw = 1;
    private const byte SubencodingBackgroundSpecified = 2;
    private const byte SubencodingForegroundSpecified = 4;
    private const byte SubencodingAnySubrects = 8;
    private const byte SubencodingSubrectsColoured = 16;

    private const int TileSize = 16;

    public static async Task DecodeAsync(
        RfbBinaryReader reader,
        RfbPixelFormat format,
        RectInt rect,
        Framebuffer framebuffer,
        RfbColourMap? colourMap,
        CancellationToken ct)
    {
        if (rect.Right > framebuffer.Width || rect.Bottom > framebuffer.Height || rect.X < 0 || rect.Y < 0)
        {
            throw new RfbProtocolException(
                $"Serwer przysłał prostokąt Hextile poza granicami ekranu {framebuffer.Width}x{framebuffer.Height}: {rect}.");
        }

        if (rect.Width == 0 || rect.Height == 0)
        {
            return;
        }

        int bpp = format.BytesPerPixel;
        // A=255 (nie default(Rgba), które ma A=0): w nietypowym przypadku, gdy serwer nie ustawia
        // SubencodingBackgroundSpecified/ForegroundSpecified w pierwszym kaflu prostokąta, do bufora
        // mają trafić piksele w pełni nieprzezroczyste, a nie przezroczyste (A=0).
        Rgba background = new Rgba(0, 0, 0);
        Rgba foreground = new Rgba(0, 0, 0);

        for (int ty = rect.Y; ty < rect.Bottom; ty += TileSize)
        {
            int tileH = Math.Min(TileSize, rect.Bottom - ty);
            for (int tx = rect.X; tx < rect.Right; tx += TileSize)
            {
                int tileW = Math.Min(TileSize, rect.Right - tx);
                byte subEncoding = await reader.ReadByteAsync(ct).ConfigureAwait(false);

                if ((subEncoding & SubencodingRaw) != 0)
                {
                    await DecodeRawTileAsync(reader, format, colourMap, tx, ty, tileW, tileH, framebuffer, bpp, ct).ConfigureAwait(false);
                    // Kafel Raw nie używa ani nie zmienia sticky tła/pierwszego planu — przechodzimy dalej.
                    continue;
                }

                if ((subEncoding & SubencodingBackgroundSpecified) != 0)
                {
                    uint raw = await ReadPixelAsync(reader, format, bpp, ct).ConfigureAwait(false);
                    background = RawDecoder.ResolveColor(format, colourMap, raw);
                }

                FillRect(framebuffer, tx, ty, tileW, tileH, background);

                if ((subEncoding & SubencodingForegroundSpecified) != 0)
                {
                    uint raw = await ReadPixelAsync(reader, format, bpp, ct).ConfigureAwait(false);
                    foreground = RawDecoder.ResolveColor(format, colourMap, raw);
                }

                if ((subEncoding & SubencodingAnySubrects) != 0)
                {
                    bool coloured = (subEncoding & SubencodingSubrectsColoured) != 0;
                    byte count = await reader.ReadByteAsync(ct).ConfigureAwait(false);

                    for (int i = 0; i < count; i++)
                    {
                        Rgba color = foreground;
                        if (coloured)
                        {
                            uint raw = await ReadPixelAsync(reader, format, bpp, ct).ConfigureAwait(false);
                            color = RawDecoder.ResolveColor(format, colourMap, raw);
                        }

                        byte xy = await reader.ReadByteAsync(ct).ConfigureAwait(false);
                        byte wh = await reader.ReadByteAsync(ct).ConfigureAwait(false);

                        int sx = (xy >> 4) & 0x0F;
                        int sy = xy & 0x0F;
                        int sw = ((wh >> 4) & 0x0F) + 1;
                        int sh = (wh & 0x0F) + 1;

                        // Obronnie przycinamy do granic kafla — chroni przed uszkodzonymi danymi z serwera.
                        sw = Math.Min(sw, tileW - sx);
                        sh = Math.Min(sh, tileH - sy);
                        if (sw <= 0 || sh <= 0)
                        {
                            continue;
                        }

                        FillRect(framebuffer, tx + sx, ty + sy, sw, sh, color);
                    }
                }
            }
        }
    }

    private static async Task DecodeRawTileAsync(
        RfbBinaryReader reader,
        RfbPixelFormat format,
        RfbColourMap? colourMap,
        int tx,
        int ty,
        int tileW,
        int tileH,
        Framebuffer framebuffer,
        int bpp,
        CancellationToken ct)
    {
        byte[] row = new byte[tileW * bpp];
        for (int y = 0; y < tileH; y++)
        {
            await reader.ReadExactlyAsync(row, 0, row.Length, ct).ConfigureAwait(false);
            for (int x = 0; x < tileW; x++)
            {
                uint raw = format.ReadPixel(row.AsSpan(x * bpp, bpp));
                Rgba color = RawDecoder.ResolveColor(format, colourMap, raw);
                framebuffer.Image.SetPixel(tx + x, ty + y, color);
            }
        }
    }

    private static async Task<uint> ReadPixelAsync(RfbBinaryReader reader, RfbPixelFormat format, int bpp, CancellationToken ct)
    {
        byte[] px = await reader.ReadExactlyAsync(bpp, ct).ConfigureAwait(false);
        return format.ReadPixel(px);
    }

    private static void FillRect(Framebuffer framebuffer, int x0, int y0, int w, int h, Rgba color)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                framebuffer.Image.SetPixel(x0 + x, y0 + y, color);
            }
        }
    }
}
