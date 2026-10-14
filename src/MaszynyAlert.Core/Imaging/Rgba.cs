namespace MaszynyAlert.Core.Imaging;

/// <summary>Kolor piksela RGBA (8 bitów na kanał).</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);
