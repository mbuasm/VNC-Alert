using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Bufor ramki (żywy obraz ekranu maszyny) utrzymywany przez <see cref="RfbClient"/>. Dekodery
/// (<see cref="Raw"/>-, CopyRect- i Hextile-owy) piszą bezpośrednio do <see cref="Image"/> podczas
/// przetwarzania kolejnych <c>FramebufferUpdate</c>.
/// </summary>
public sealed class Framebuffer
{
    /// <summary>Aktualna szerokość ekranu (piksele) — może się zmienić po pseudo-kodowaniu DesktopSize.</summary>
    public int Width { get; private set; }

    /// <summary>Aktualna wysokość ekranu (piksele).</summary>
    public int Height { get; private set; }

    /// <summary>Żywy bufor pikseli RGBA. Dekodery modyfikują go w miejscu.</summary>
    public RgbaImage Image { get; private set; }

    public Framebuffer(int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentException("Wymiary ekranu nie mogą być ujemne.");
        }

        Width = width;
        Height = height;
        Image = new RgbaImage(width, height);
    }

    /// <summary>Niezależna kopia całego bufora w danej chwili (np. do podglądu „Pokaż ekran”).</summary>
    public RgbaImage Snapshot() => Image.Clone();

    /// <summary>
    /// Wycina fragment bufora opisany przez <paramref name="r"/> (przycinany do granic ekranu).
    /// Deleguje do <see cref="RgbaImage.Crop"/> — pusty (po przycięciu) obszar rzuca
    /// <see cref="ArgumentException"/>, zgodnie z kontraktem <c>RgbaImage</c>.
    /// </summary>
    public RgbaImage Crop(RectInt r) => Image.Crop(r);

    /// <summary>
    /// Zmienia rozmiar ekranu (pseudo-kodowanie DesktopSize, RFC 6143 §7.6.3 / -223). Zachowuje
    /// wspólny obszar starego i nowego bufora (reszta nowego bufora jest czarna/przezroczysta —
    /// zostanie nadpisana kolejnymi aktualizacjami).
    /// </summary>
    internal void Resize(int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentException("Wymiary ekranu nie mogą być ujemne.");
        }

        var newImage = new RgbaImage(width, height);
        newImage.Blit(Image, 0, 0);
        Image = newImage;
        Width = width;
        Height = height;
    }
}
