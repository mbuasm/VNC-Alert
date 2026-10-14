using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Mapa kolorów (paleta) dla serwerów z <c>PixelFormat.TrueColour == false</c> (8 bpp indeksowane).
/// Wypełniana z komunikatu <c>SetColourMapEntries</c> (typ 1) i przekazywana dekoderom, żeby mogły
/// zamienić indeks piksela na kolor RGBA. Gdy dla danego indeksu nie ma jeszcze wpisu (serwer nie
/// zdążył go jeszcze przysłać), zwracany jest szary — patrz <see cref="Resolve"/>.
/// </summary>
internal sealed class RfbColourMap
{
    private static readonly Rgba FallbackGray = new(128, 128, 128);

    private readonly Dictionary<uint, Rgba> _entries = new();

    public void Set(uint index, Rgba color) => _entries[index] = color;

    public Rgba Resolve(uint index) => _entries.TryGetValue(index, out Rgba color) ? color : FallbackGray;
}
