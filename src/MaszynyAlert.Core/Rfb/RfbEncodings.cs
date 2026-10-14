namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Numery kodowań prostokątów aktualizacji ekranu (RFC 6143 §7.7). Klient obsługuje tylko
/// <see cref="Raw"/>, <see cref="CopyRect"/> i <see cref="Hextile"/> — pozostałe (<see cref="RRE"/>,
/// <see cref="ZRLE"/>) są tu zdefiniowane wyłącznie do celów informacyjnych/logów; nie są dekodowane.
/// </summary>
public static class RfbEncodings
{
    /// <summary>Surowe piksele, bez kompresji.</summary>
    public const int Raw = 0;

    /// <summary>Kopiowanie fragmentu już posiadanej ramki (np. przewinięcie).</summary>
    public const int CopyRect = 1;

    /// <summary>Rise-and-Run-length Encoding — niewspierane w tym kliencie.</summary>
    public const int RRE = 2;

    /// <summary>Kodowanie kafelkowe 16×16 z tłem/pierwszym planem i podprostokątami.</summary>
    public const int Hextile = 5;

    /// <summary>Zlib Run-Length Encoding — niewspierane w tym kliencie.</summary>
    public const int ZRLE = 16;
}
