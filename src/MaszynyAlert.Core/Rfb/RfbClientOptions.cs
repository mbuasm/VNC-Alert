namespace MaszynyAlert.Core.Rfb;

/// <summary>Opcje połączenia <see cref="RfbClient"/> z jednym serwerem RFB (jedną maszyną).</summary>
public sealed class RfbClientOptions
{
    /// <summary>Adres IP lub nazwa hosta maszyny.</summary>
    public required string Host { get; set; }

    public int Port { get; set; } = 5900;

    /// <summary>Hasło VNC (VncAuth albo RSA-AES); null/pusty, gdy serwer nie wymaga uwierzytelnienia.</summary>
    public string? Password { get; set; }

    public string? Username { get; set; }

    public string? ExpectedServerKeySha256 { get; set; }

    public bool Shared { get; set; } = true;

    /// <summary>Limit czasu na nawiązanie połączenia TCP.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Limit czasu na pojedynczy odczyt z gniazda (stosowany do każdego odczytu z osobna).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Kodowania zgłaszane serwerowi w <c>SetEncodings</c>, w podanej kolejności (kolejność jest
    /// preferencją klienta wg RFC 6143 §7.5.2). Domyślnie Hextile, CopyRect, Raw — celowo BEZ
    /// pseudo-kodowań (np. DesktopSize), których klient i tak honoruje, gdy serwer je przyśle.
    /// </summary>
    public int[] Encodings { get; set; } = [RfbEncodings.Hextile, RfbEncodings.CopyRect, RfbEncodings.Raw];
}
