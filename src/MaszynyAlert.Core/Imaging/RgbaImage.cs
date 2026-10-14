namespace MaszynyAlert.Core.Imaging;

/// <summary>
/// Obraz rastrowy RGBA8888 (4 bajty na piksel: R,G,B,A), ułożony wierszami (row-major),
/// bez paddingu na końcu wiersza (<see cref="Stride"/> = Width * 4). Podstawowy typ obrazu w całym projekcie.
/// </summary>
public sealed class RgbaImage
{
    /// <summary>Szerokość obrazu w pikselach.</summary>
    public int Width { get; }

    /// <summary>Wysokość obrazu w pikselach.</summary>
    public int Height { get; }

    /// <summary>Surowe dane pikseli (R,G,B,A powtórzone dla każdego piksela, wiersz po wierszu).</summary>
    public byte[] Pixels { get; }

    /// <summary>Liczba bajtów w jednym wierszu (Width * 4).</summary>
    public int Stride => Width * 4;

    /// <summary>Tworzy pusty (przezroczysty czarny) obraz o podanym rozmiarze.</summary>
    public RgbaImage(int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentException("Szerokość i wysokość obrazu nie mogą być ujemne.");
        }

        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    /// <summary>Tworzy obraz z gotowej tablicy pikseli (bez kopiowania — przejmuje własność tablicy).</summary>
    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentException("Szerokość i wysokość obrazu nie mogą być ujemne.");
        }

        int expected = checked(width * height * 4);
        if (pixels is null || pixels.Length != expected)
        {
            throw new ArgumentException(
                $"Tablica pikseli ma nieprawidłowy rozmiar: oczekiwano {expected} bajtów, otrzymano {pixels?.Length ?? 0}.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    private void CheckBounds(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Punkt ({x},{y}) leży poza obrazem {Width}x{Height}.");
        }
    }

    /// <summary>Zwraca kolor piksela w punkcie (x,y).</summary>
    public Rgba GetPixel(int x, int y)
    {
        CheckBounds(x, y);
        int i = y * Stride + x * 4;
        return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    /// <summary>Ustawia kolor piksela w punkcie (x,y).</summary>
    public void SetPixel(int x, int y, Rgba c)
    {
        CheckBounds(x, y);
        int i = y * Stride + x * 4;
        Pixels[i] = c.R;
        Pixels[i + 1] = c.G;
        Pixels[i + 2] = c.B;
        Pixels[i + 3] = c.A;
    }

    /// <summary>Wypełnia cały obraz jednym kolorem.</summary>
    public void Fill(Rgba c)
    {
        for (int y = 0; y < Height; y++)
        {
            int rowStart = y * Stride;
            for (int x = 0; x < Width; x++)
            {
                int i = rowStart + x * 4;
                Pixels[i] = c.R;
                Pixels[i + 1] = c.G;
                Pixels[i + 2] = c.B;
                Pixels[i + 3] = c.A;
            }
        }
    }

    /// <summary>
    /// Wycina fragment obrazu opisany przez <paramref name="r"/>. Prostokąt jest najpierw przycinany
    /// do granic obrazu; jeśli po przycięciu jest pusty, rzuca <see cref="ArgumentException"/>
    /// (nie zwraca obrazu 0x0).
    /// </summary>
    public RgbaImage Crop(RectInt r)
    {
        RectInt clamped = r.Clamp(Width, Height);
        if (clamped.IsEmpty)
        {
            throw new ArgumentException("Wycinany obszar (po przycięciu do granic obrazu) jest pusty.", nameof(r));
        }

        var result = new RgbaImage(clamped.Width, clamped.Height);
        for (int y = 0; y < clamped.Height; y++)
        {
            int srcOffset = (clamped.Y + y) * Stride + clamped.X * 4;
            int dstOffset = y * result.Stride;
            Buffer.BlockCopy(Pixels, srcOffset, result.Pixels, dstOffset, result.Stride);
        }

        return result;
    }

    /// <summary>Zwraca pełną, niezależną kopię obrazu.</summary>
    public RgbaImage Clone()
    {
        var copy = new byte[Pixels.Length];
        Buffer.BlockCopy(Pixels, 0, copy, 0, Pixels.Length);
        return new RgbaImage(Width, Height, copy);
    }

    /// <summary>
    /// Kopiuje (nadpisuje, bez mieszania kanału alfa) piksele obrazu <paramref name="src"/> na ten obraz,
    /// zaczynając od (<paramref name="dstX"/>, <paramref name="dstY"/>). Wynik jest przycinany do granic
    /// obu obrazów — fragmenty wychodzące poza któryś z nich są pomijane.
    /// </summary>
    public void Blit(RgbaImage src, int dstX, int dstY)
    {
        ArgumentNullException.ThrowIfNull(src);

        int srcStartX = 0;
        int srcStartY = 0;
        int copyDstX = dstX;
        int copyDstY = dstY;

        if (copyDstX < 0)
        {
            srcStartX = -copyDstX;
            copyDstX = 0;
        }

        if (copyDstY < 0)
        {
            srcStartY = -copyDstY;
            copyDstY = 0;
        }

        int width = Math.Min(src.Width - srcStartX, Width - copyDstX);
        int height = Math.Min(src.Height - srcStartY, Height - copyDstY);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            int srcOffset = (srcStartY + y) * src.Stride + srcStartX * 4;
            int dstOffset = (copyDstY + y) * Stride + copyDstX * 4;
            Buffer.BlockCopy(src.Pixels, srcOffset, Pixels, dstOffset, rowBytes);
        }
    }

    public RgbaImage Downscale(int factor)
    {
        if (factor <= 0)
        {
            throw new ArgumentException("Współczynnik pomniejszenia musi być dodatni.", nameof(factor));
        }

        int width = Width / factor;
        int height = Height / factor;
        if (factor == 1 || width == 0 || height == 0)
        {
            return Clone();
        }

        var result = new RgbaImage(width, height);
        int block = factor * factor;
        for (int y = 0; y < height; y++)
        {
            int dstRow = y * result.Stride;
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int srcRow = ((y * factor) + dy) * Stride;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int si = srcRow + (((x * factor) + dx) * 4);
                        r += Pixels[si];
                        g += Pixels[si + 1];
                        b += Pixels[si + 2];
                        a += Pixels[si + 3];
                    }
                }

                int di = dstRow + (x * 4);
                result.Pixels[di] = (byte)(r / block);
                result.Pixels[di + 1] = (byte)(g / block);
                result.Pixels[di + 2] = (byte)(b / block);
                result.Pixels[di + 3] = (byte)(a / block);
            }
        }

        return result;
    }

    /// <summary>Zwraca obraz powiększony całkowitą liczbę razy metodą najbliższego sąsiada (nearest neighbour).</summary>
    public RgbaImage Scale(int factor)
    {
        if (factor <= 0)
        {
            throw new ArgumentException("Współczynnik skalowania musi być dodatni.", nameof(factor));
        }

        if (factor == 1)
        {
            return Clone();
        }

        var result = new RgbaImage(Width * factor, Height * factor);
        for (int y = 0; y < result.Height; y++)
        {
            int srcY = y / factor;
            int srcRowStart = srcY * Stride;
            int dstRowStart = y * result.Stride;
            for (int x = 0; x < result.Width; x++)
            {
                int srcX = x / factor;
                int si = srcRowStart + srcX * 4;
                int di = dstRowStart + x * 4;
                result.Pixels[di] = Pixels[si];
                result.Pixels[di + 1] = Pixels[si + 1];
                result.Pixels[di + 2] = Pixels[si + 2];
                result.Pixels[di + 3] = Pixels[si + 3];
            }
        }

        return result;
    }
}
