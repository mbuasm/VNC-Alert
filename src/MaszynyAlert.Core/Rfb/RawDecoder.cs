using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

/// <summary>Dekoder kodowania Raw (RFC 6143 §7.7.1): surowe piksele, wiersz po wierszu, bez kompresji.</summary>
internal static class RawDecoder
{
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
                $"Serwer przysłał prostokąt Raw poza granicami ekranu {framebuffer.Width}x{framebuffer.Height}: {rect}.");
        }

        if (rect.Width == 0 || rect.Height == 0)
        {
            return;
        }

        int bpp = format.BytesPerPixel;
        byte[] row = new byte[rect.Width * bpp];

        for (int y = 0; y < rect.Height; y++)
        {
            await reader.ReadExactlyAsync(row, 0, row.Length, ct).ConfigureAwait(false);
            int py = rect.Y + y;
            for (int x = 0; x < rect.Width; x++)
            {
                uint raw = format.ReadPixel(row.AsSpan(x * bpp, bpp));
                Rgba color = ResolveColor(format, colourMap, raw);
                framebuffer.Image.SetPixel(rect.X + x, py, color);
            }
        }
    }

    /// <summary>
    /// Zamienia surową wartość piksela na kolor RGBA — dla trybu paletowego (TrueColour = false)
    /// używa <paramref name="colourMap"/> (indeks piksela = klucz palety), dla true-colour deleguje
    /// do <see cref="RfbPixelFormat.ToRgba"/>. Współdzielone przez wszystkie dekodery.
    /// </summary>
    internal static Rgba ResolveColor(RfbPixelFormat format, RfbColourMap? colourMap, uint raw)
    {
        if (!format.TrueColour && colourMap is not null)
        {
            return colourMap.Resolve(raw);
        }

        return format.ToRgba(raw);
    }
}
