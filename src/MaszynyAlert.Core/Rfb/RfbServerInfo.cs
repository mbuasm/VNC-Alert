namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Informacje o serwerze RFB zebrane w czasie handshake'u — wersja protokołu, wynik negocjacji
/// zabezpieczeń, rozmiar ekranu, format piksela i nazwa pulpitu. Pokazywane w oknie statusu
/// oraz w raporcie <c>probe.exe</c>.
/// </summary>
public sealed record RfbServerInfo(
    string ProtocolVersion,
    IReadOnlyList<byte> OfferedSecurityTypes,
    RfbSecurityType UsedSecurityType,
    int Width,
    int Height,
    RfbPixelFormat PixelFormat,
    string DesktopName)
{
    public byte? RsaAesCredentialSubtype { get; init; }

    public string? RawVersionHeader { get; init; }

    public string? ServerKeySha256 { get; init; }

    /// <summary>Opis wariantu poświadczeń po polsku, albo <see langword="null"/> poza RSA-AES.</summary>
    public string? DescribeCredentialSubtype() => RsaAesCredentialSubtype switch
    {
        1 => "nazwa użytkownika i hasło (logowanie systemowe)",
        2 => "samo hasło VNC",
        null => null,
        _ => $"nieznany wariant ({RsaAesCredentialSubtype})",
    };

    /// <summary>Krótki, czytelny opis do logów/diagnostyki.</summary>
    public string Describe() =>
        $"RFB {ProtocolVersion}, auth {UsedSecurityType}, {Width}x{Height}, {PixelFormat.Describe()}, \"{DesktopName}\"";
}
