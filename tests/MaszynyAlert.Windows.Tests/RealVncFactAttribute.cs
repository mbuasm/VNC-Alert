namespace MaszynyAlert.Windows.Tests;

public sealed class RealVncFactAttribute : FactAttribute
{
    /// <summary>Adres serwera VNC: <c>host:port</c> (albo sam <c>host</c> = port 5900).</summary>
    public const string EndpointVariable = "VNCALERT_REAL_VNC";

    public const string PasswordVariable = "VNCALERT_REAL_VNC_PASSWORD";

    public RealVncFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Test prawdziwego VNC wymaga Windows (okno WPF na pulpicie).";
        else if (InteractiveSessionFactAttribute.IsServiceSession)
            Skip = "Sesja 0: brak pulpitu, który serwer VNC mógłby pokazać — test wymaga sesji interaktywnej (runner ui-msi w VM).";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EndpointVariable)))
            Skip = $"Brak zmiennej {EndpointVariable} (host:port serwera VNC tego pulpitu) — ustawia ją tylko ui-sprawdz.yml na runnerze ui-msi.";
    }

    /// <summary>
    /// Czyta <c>host:port</c> ze zmiennej <see cref="EndpointVariable"/>. Zły format to błąd testu (nie pominięcie) —
    /// zmienną ustawił ktoś, kto chciał ten test wykonać.
    /// </summary>
    internal static (string Host, int Port) ReadEndpoint()
    {
        string raw = (Environment.GetEnvironmentVariable(EndpointVariable) ?? "").Trim();
        int colon = raw.LastIndexOf(':');
        if (colon < 0) return (raw, 5900);

        string host = raw[..colon];
        if (host.Length == 0 || !int.TryParse(raw[(colon + 1)..], out int port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"{EndpointVariable}='{raw}' — oczekiwano host:port, np. 127.0.0.1:5900.");
        return (host, port);
    }

    /// <summary>Hasło z <see cref="PasswordVariable"/>; <see langword="null"/>, gdy puste.</summary>
    internal static string? ReadPassword()
    {
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        return string.IsNullOrEmpty(password) ? null : password;
    }
}
