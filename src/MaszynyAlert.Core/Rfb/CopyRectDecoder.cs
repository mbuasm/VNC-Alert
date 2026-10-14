using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

/// <summary>Dekoder kodowania CopyRect (RFC 6143 §7.7.2): kopiuje fragment już posiadanej ramki.</summary>
internal static class CopyRectDecoder
{
    public static async Task DecodeAsync(
        RfbBinaryReader reader,
        RectInt destRect,
        Framebuffer framebuffer,
        CancellationToken ct)
    {
        ushort srcX = await reader.ReadUInt16Async(ct).ConfigureAwait(false);
        ushort srcY = await reader.ReadUInt16Async(ct).ConfigureAwait(false);

        if (destRect.Width == 0 || destRect.Height == 0)
        {
            return;
        }

        var srcRect = new RectInt(srcX, srcY, destRect.Width, destRect.Height);
        if (srcRect.X < 0 || srcRect.Y < 0 || srcRect.Right > framebuffer.Width || srcRect.Bottom > framebuffer.Height ||
            destRect.X < 0 || destRect.Y < 0 || destRect.Right > framebuffer.Width || destRect.Bottom > framebuffer.Height)
        {
            throw new RfbProtocolException(
                $"CopyRect: prostokąt źródłowy lub docelowy poza granicami ekranu {framebuffer.Width}x{framebuffer.Height} " +
                $"(źródło {srcRect}, cel {destRect}).");
        }

        // Kopia przez bufor pośredni — poprawnie obsługuje nakładające się prostokąty (np. przewijanie),
        // bo Crop tworzy niezależną kopię źródła przed nadpisaniem obszaru docelowego przez Blit.
        RgbaImage srcCopy = framebuffer.Image.Crop(srcRect);
        framebuffer.Image.Blit(srcCopy, destRect.X, destRect.Y);
    }
}
