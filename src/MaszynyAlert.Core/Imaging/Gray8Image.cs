namespace MaszynyAlert.Core.Imaging;

/// <summary>Obraz w odcieniach szarości, 1 bajt na piksel, row-major, bez paddingu. Używany m.in. jako wejście do OCR.</summary>
public sealed class Gray8Image
{
    /// <summary>Szerokość obrazu w pikselach.</summary>
    public int Width { get; }

    /// <summary>Wysokość obrazu w pikselach.</summary>
    public int Height { get; }

    /// <summary>Surowe dane pikseli (1 bajt = 1 piksel, wiersz po wierszu).</summary>
    public byte[] Pixels { get; }

    /// <summary>Tworzy pusty (czarny) obraz o podanym rozmiarze.</summary>
    public Gray8Image(int w, int h)
    {
        if (w < 0 || h < 0)
        {
            throw new ArgumentException("Szerokość i wysokość obrazu nie mogą być ujemne.");
        }

        Width = w;
        Height = h;
        Pixels = new byte[checked(w * h)];
    }

    /// <summary>Tworzy obraz z gotowej tablicy pikseli (bez kopiowania — przejmuje własność tablicy).</summary>
    public Gray8Image(int w, int h, byte[] px)
    {
        if (w < 0 || h < 0)
        {
            throw new ArgumentException("Szerokość i wysokość obrazu nie mogą być ujemne.");
        }

        int expected = checked(w * h);
        if (px is null || px.Length != expected)
        {
            throw new ArgumentException(
                $"Tablica pikseli ma nieprawidłowy rozmiar: oczekiwano {expected} bajtów, otrzymano {px?.Length ?? 0}.",
                nameof(px));
        }

        Width = w;
        Height = h;
        Pixels = px;
    }

    private void CheckBounds(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Punkt ({x},{y}) leży poza obrazem {Width}x{Height}.");
        }
    }

    /// <summary>Zwraca wartość jasności piksela w punkcie (x,y).</summary>
    public byte Get(int x, int y)
    {
        CheckBounds(x, y);
        return Pixels[y * Width + x];
    }

    /// <summary>Ustawia wartość jasności piksela w punkcie (x,y).</summary>
    public void Set(int x, int y, byte v)
    {
        CheckBounds(x, y);
        Pixels[y * Width + x] = v;
    }

    /// <summary>Konwertuje obraz do RGBA (R=G=B=jasność, A=255).</summary>
    public RgbaImage ToRgba()
    {
        var result = new RgbaImage(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            int srcRow = y * Width;
            int dstRow = y * result.Stride;
            for (int x = 0; x < Width; x++)
            {
                byte v = Pixels[srcRow + x];
                int di = dstRow + x * 4;
                result.Pixels[di] = v;
                result.Pixels[di + 1] = v;
                result.Pixels[di + 2] = v;
                result.Pixels[di + 3] = 255;
            }
        }

        return result;
    }
}
