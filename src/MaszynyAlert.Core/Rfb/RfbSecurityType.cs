namespace MaszynyAlert.Core.Rfb;

public enum RfbSecurityType : byte
{
    /// <summary>Wartość wartownicza — brak zabezpieczeń/błąd na etapie listy typów (RFC 6143 §7.1.2).</summary>
    Invalid = 0,

    /// <summary>Brak uwierzytelnienia.</summary>
    None = 1,

    /// <summary>Standardowe uwierzytelnienie hasłem VNC (DES).</summary>
    VncAuth = 2,

    RA2 = 5,

    RA2ne = 6,

    /// <summary>Tight Security Type — niewspierane.</summary>
    Tight = 16,

    /// <summary>Ultra VNC — niewspierane.</summary>
    Ultra = 17,

    /// <summary>TLS — niewspierane.</summary>
    TLS = 18,

    /// <summary>VeNCrypt — niewspierane.</summary>
    VeNCrypt = 19,

    RA2_256 = 129,

    RA2ne_256 = 130,
}
