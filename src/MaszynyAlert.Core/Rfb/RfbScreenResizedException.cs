namespace MaszynyAlert.Core.Rfb;

public sealed class RfbScreenResizedException : Exception
{
    public RfbScreenResizedException(int width, int height)
        : base($"Serwer zmienił rozmiar ekranu na {width}×{height} — żądany obszar leży poza ekranem.")
    {
        Width = width;
        Height = height;
    }

    /// <summary>Nowa szerokość ekranu serwera.</summary>
    public int Width { get; }

    /// <summary>Nowa wysokość ekranu serwera.</summary>
    public int Height { get; }
}
