namespace MaszynyAlert.Core.Abstractions;

/// <summary>Konwencje nazewnicze kluczy w <see cref="ISecretStore"/>.</summary>
public static class SecretKeys
{
    public static string VncPassword(string machineId) => $"vnc:{machineId}";

    /// <summary>Klucz wspólnego klucza HMAC do komunikacji między komputerami (klaster).</summary>
    public const string ClusterHmacKey = "cluster:hmac";

    /// <summary>
    /// Sekret tokenu usługi Cloudflare Access (nagłówek <c>CF-Access-Client-Secret</c>) dla katalogu wydań.
    /// Identyfikator tokenu jest jawny i stoi w <c>config.json</c> (<c>updates.accessClientId</c>), sekret —
    /// jak hasła VNC — tylko w magazynie sekretów stanowiska. Nazwy klucza nie zmieniać: zgubiłoby to
    /// zapisany sekret na każdym komputerze.
    /// </summary>
    public const string UpdatesAccessClientSecret = "updates:access-client-secret";
}
