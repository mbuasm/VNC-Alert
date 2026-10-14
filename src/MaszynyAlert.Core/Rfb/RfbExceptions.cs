namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Bazowy wyjątek klienta RFB. Komunikat jest zawsze po polsku i zrozumiały dla osoby
/// nieprogramującej (np. trafia do okna statusu aplikacji).
/// </summary>
public class RfbException : Exception
{
    public RfbException(string message)
        : base(message)
    {
    }

    public RfbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Serwer przysłał dane niezgodne z protokołem RFB (nieprawidłowy nagłówek, nieznany typ
/// komunikatu, nieobsługiwane kodowanie itp.).
/// </summary>
public sealed class RfbProtocolException : RfbException
{
    public RfbProtocolException(string message)
        : base(message)
    {
    }

    public RfbProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Uwierzytelnienie się nie powiodło: serwer oferuje tylko niewspierane typy zabezpieczeń,
/// odrzucił hasło VNC, zażądał hasła którego nie skonfigurowano, albo odrzucił połączenie
/// jeszcze przed etapem wyboru typu zabezpieczeń.
/// </summary>
public sealed class RfbAuthenticationException : RfbException
{
    /// <summary>Typy zabezpieczeń zaoferowane przez serwer (surowe bajty wg IANA), do wypisania w raporcie <c>probe.exe</c>.</summary>
    public IReadOnlyList<byte> OfferedSecurityTypes { get; }

    /// <summary>Tekst powodu odesłany przez serwer (tylko RFB 3.8), jeśli był dostępny.</summary>
    public string? ServerReason { get; }

    public RfbAuthenticationException(string message, IReadOnlyList<byte>? offeredSecurityTypes = null, string? serverReason = null)
        : base(message)
    {
        OfferedSecurityTypes = offeredSecurityTypes ?? Array.Empty<byte>();
        ServerReason = serverReason;
    }
}
