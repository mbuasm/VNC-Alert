using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Probe.Commands;

/// <summary>Formatowanie informacji RFB do czytelnych komunikatów PL (współdzielone przez kilka komend).</summary>
internal static class RfbFormat
{
    /// <summary>Nazwa typu zabezpieczeń RFB wg rejestru IANA (dla liczb spoza <see cref="RfbSecurityType"/> — "nieznany (N)").</summary>
    public static string SecurityTypeName(byte t) => t switch
    {
        0 => "Invalid",
        1 => "None",
        2 => "VncAuth",
        5 => "RA2",
        6 => "RA2ne",
        16 => "Tight",
        17 => "Ultra",
        18 => "TLS",
        19 => "VeNCrypt",
        129 => "RA2_256",
        130 => "RA2ne_256",
        _ => $"nieznany ({t})",
    };

    /// <summary>Nazwa kodowania RFB (RFC 6143 §7.7 i pseudo-kodowania) — do kolumny „kodowania” w podsumowaniu.</summary>
    public static string EncodingName(int encoding) => encoding switch
    {
        RfbEncodings.Raw => "Raw",
        RfbEncodings.CopyRect => "CopyRect",
        RfbEncodings.RRE => "RRE",
        RfbEncodings.Hextile => "Hextile",
        RfbEncodings.ZRLE => "ZRLE",
        -223 => "DesktopSize",
        -239 => "Cursor",
        _ => $"nieznane ({encoding})",
    };

    /// <summary>Lista oferowanych typów jako "2 (VncAuth), 1 (None)".</summary>
    public static string FormatOfferedTypes(IReadOnlyList<byte> offered) =>
        offered.Count == 0 ? "(brak)" : string.Join(", ", offered.Select(t => $"{t} ({SecurityTypeName(t)})"));
}
