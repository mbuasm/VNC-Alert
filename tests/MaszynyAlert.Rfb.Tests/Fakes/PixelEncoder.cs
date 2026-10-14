using System.Buffers.Binary;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>
/// Koder pikseli fałszywego serwera: zamienia kolor RGBA na surową wartość w formacie piksela
/// serwera i zapisuje ją zgodnie z kolejnością bajtów (LE/BE). Świadomie NIE korzysta z kodu
/// dekodującego z <c>MaszynyAlert.Core.Rfb</c> — to niezależna implementacja „drugiej strony”.
/// </summary>
internal sealed class PixelEncoder
{
    private readonly RfbPixelFormat _format;
    private readonly IReadOnlyList<Rgba>? _palette;

    public PixelEncoder(RfbPixelFormat format, IReadOnlyList<Rgba>? palette)
    {
        _format = format;
        _palette = palette;
        BytesPerPixel = format.BitsPerPixel / 8;
        if (BytesPerPixel is not (1 or 2 or 4))
        {
            throw new ArgumentException($"Fałszywy serwer obsługuje 8/16/32 bpp, nie {format.BitsPerPixel}.", nameof(format));
        }
    }

    public int BytesPerPixel { get; }

    /// <summary>Zamienia kolor na surową wartość piksela (true-colour: skalowanie kanałów; paleta: indeks).</summary>
    public uint ToRaw(Rgba color)
    {
        if (!_format.TrueColour)
        {
            if (_palette is null)
            {
                throw new InvalidOperationException("Format paletowy wymaga ustawienia FakeRfbServerOptions.Palette.");
            }

            for (int i = 0; i < _palette.Count; i++)
            {
                Rgba entry = _palette[i];
                if (entry.R == color.R && entry.G == color.G && entry.B == color.B)
                {
                    return (uint)i;
                }
            }

            throw new InvalidOperationException($"Kolor {color} nie występuje w palecie fałszywego serwera.");
        }

        uint r = Quantize(color.R, _format.RedMax);
        uint g = Quantize(color.G, _format.GreenMax);
        uint b = Quantize(color.B, _format.BlueMax);
        return (r << _format.RedShift) | (g << _format.GreenShift) | (b << _format.BlueShift);
    }

    /// <summary>Zapisuje surową wartość piksela do bufora (dokładnie <see cref="BytesPerPixel"/> bajtów).</summary>
    public void Write(uint raw, Span<byte> dst)
    {
        switch (BytesPerPixel)
        {
            case 1:
                dst[0] = (byte)raw;
                break;
            case 2:
                if (_format.BigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)raw);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)raw);
                }

                break;
            default:
                if (_format.BigEndian)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(dst, raw);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dst, raw);
                }

                break;
        }
    }

    /// <summary>Zapisuje jeden piksel (kolor → surowa wartość → bajty) do strumienia wyjściowego.</summary>
    public void WritePixel(Rgba color, Stream output)
    {
        Span<byte> buf = stackalloc byte[4];
        Write(ToRaw(color), buf);
        output.Write(buf[..BytesPerPixel]);
    }

    /// <summary>Koduje prostokąt obrazu jako surowe piksele (kodowanie Raw, wiersz po wierszu).</summary>
    public void WriteRawRect(RgbaImage image, RectInt rect, Stream output)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                WritePixel(image.GetPixel(x, y), output);
            }
        }
    }

    private static uint Quantize(byte value, ushort max) =>
        max == 0 ? 0u : (uint)Math.Round(value * max / 255.0, MidpointRounding.AwayFromZero);
}
