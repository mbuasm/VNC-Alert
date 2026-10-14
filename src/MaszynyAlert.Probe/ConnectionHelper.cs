using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Probe;

/// <summary>Wynik próby połączenia z jedną maszyną przez <see cref="ConnectionHelper"/>.</summary>
public sealed class ConnectOutcome
{
    private ConnectOutcome(bool success, RfbClient? client, RfbServerInfo? info, string? usedPassword, string? skipReason)
    {
        Success = success;
        Client = client;
        Info = info;
        UsedPassword = usedPassword;
        SkipReason = skipReason;
    }

    /// <summary>Prawda, gdy połączenie i uwierzytelnienie się powiodły.</summary>
    public bool Success { get; }

    /// <summary>Połączony klient (do dalszego użycia — wywołujący jest odpowiedzialny za <see cref="IAsyncDisposable.DisposeAsync"/>). Tylko gdy <see cref="Success"/>.</summary>
    public RfbClient? Client { get; }

    /// <summary>Informacje o serwerze zebrane w trakcie handshake'u. Tylko gdy <see cref="Success"/>.</summary>
    public RfbServerInfo? Info { get; }

    /// <summary>Hasło, które faktycznie zadziałało (albo <see langword="null"/>, gdy maszyna nie wymagała hasła) — do ponownego użycia bez pytania, np. przez <c>watch</c>. Tylko gdy <see cref="Success"/>.</summary>
    public string? UsedPassword { get; }

    /// <summary>Czytelny po polsku powód pominięcia maszyny. Tylko gdy nie <see cref="Success"/>.</summary>
    public string? SkipReason { get; }

    public static ConnectOutcome Ok(RfbClient client, RfbServerInfo info, string? usedPassword) =>
        new(true, client, info, usedPassword, null);

    public static ConnectOutcome Skip(string reason) => new(false, null, null, null, reason);
}

public sealed class ConnectionHelper(
    string? cliPassword,
    bool noAuthPrompt,
    ProbeSession? session = null,
    string? cliUsername = null)
{
    private const int MaxPasswordAttempts = 3; // 1 pierwsza próba + do 2 ponowień po złym haśle

    private bool _askedRememberAll;
    private string? _rememberedPassword;

    /// <summary>Nazwa użytkownika podana na konsoli, gdy serwer RSA-AES okazał się być na logowaniu systemowym — kolejne maszyny nie pytają o nią ponownie.</summary>
    private string? _rememberedUsername;

    /// <summary>
    /// Łączy się z jedną maszyną, prowadząc operatora przez ewentualne pytania o hasło.
    /// <paramref name="machineId"/> identyfikuje maszynę w <see cref="ProbeSession"/> —
    /// komendy bez konfiguracji maszyny (<c>capture</c>, <c>shared-test</c>) mogą podać tu np. "host:port".
    /// <paramref name="username"/> to <c>machines[].username</c> tej maszyny (nazwa użytkownika do
    /// logowania systemowego RSA-AES); gdy jest puste, używana jest wartość z <c>--username</c>.
    /// </summary>
    public async Task<ConnectOutcome> ConnectAsync(
        string machineId,
        string displayName,
        string host,
        int port,
        TimeSpan connectTimeout,
        CancellationToken ct,
        string? username = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        string? user = string.IsNullOrWhiteSpace(username) ? cliUsername ?? _rememberedUsername : username;

        ConnectOutcome noAuthOutcome = await TryConnectAsync(host, port, password: null, user, connectTimeout, ct).ConfigureAwait(false);
        if (noAuthOutcome.Success)
        {
            session?.RememberPassword(machineId, null);
            return noAuthOutcome;
        }

        if (IsUnsupportedSecurity(noAuthOutcome.SkipReason!))
        {
            return noAuthOutcome; // RA2/RA2ne — nie ma sensu próbować hasła.
        }

        if (!IsRequiresPassword(noAuthOutcome.SkipReason!))
        {
            return noAuthOutcome; // Maszyna niedostępna / inny błąd protokołu — nie chodzi o hasło.
        }

        // --- Serwer chce VncAuth: ustal hasło do użycia. ---
        string? password = ResolveInitialPassword(machineId, displayName, host, port);
        if (password is null)
        {
            return ConnectOutcome.Skip(
                "wymaga hasła VNC, a podano --no-auth-prompt (nie pytam) — pomiń albo uruchom ponownie z --password.");
        }

        bool askedUsername = false;
        for (int attempt = 1; attempt <= MaxPasswordAttempts; attempt++)
        {
            ConnectOutcome outcome = await TryConnectAsync(host, port, password, user, connectTimeout, ct).ConfigureAwait(false);
            if (outcome.Success)
            {
                session?.RememberPassword(machineId, password);
                return outcome;
            }

            if (IsUnsupportedSecurity(outcome.SkipReason!))
            {
                return outcome;
            }

            if (IsMissingUsername(outcome.SkipReason!))
            {
                // Serwer RSA-AES jest na LOGOWANIU SYSTEMOWYM (wariant "nazwa użytkownika i hasło"),
                // a nie znamy nazwy użytkownika: nie ma jej ani w machines[].username, ani w --username.
                // Handshake doszedł już do poświadczeń, więc jedno pytanie na konsoli kończy próbę w tym
                // samym przebiegu, zamiast odsyłać operatora do edycji config.json i drugiego uruchomienia.
                if (noAuthPrompt || askedUsername)
                {
                    return outcome;
                }

                askedUsername = true;
                string typed = PasswordPrompt.ReadText(
                    $"Nazwa użytkownika (logowanie systemowe) do {displayName} ({host}:{port}): ");
                if (string.IsNullOrWhiteSpace(typed))
                {
                    return outcome;
                }

                user = typed;
                _rememberedUsername ??= typed;
                continue;
            }

            if (!IsWrongPassword(outcome.SkipReason!))
            {
                return outcome; // Utrata łączności w trakcie auth itp. — nie chodzi o hasło.
            }

            // Złe hasło.
            if (cliPassword is not null || noAuthPrompt)
            {
                return ConnectOutcome.Skip(
                    "serwer odrzucił podane hasło VNC — to hasło jest dla tej maszyny nieprawidłowe.");
            }

            if (attempt >= MaxPasswordAttempts)
            {
                return ConnectOutcome.Skip("błędne hasło VNC (przekroczono limit prób).");
            }

            Console.WriteLine("Błędne hasło VNC, spróbuj ponownie.");
            password = PasswordPrompt.ReadPassword($"Hasło VNC do {displayName} ({host}:{port}): ");
        }

        return ConnectOutcome.Skip("błędne hasło VNC (przekroczono limit prób).");
    }

    private string? ResolveInitialPassword(string machineId, string displayName, string host, int port)
    {
        if (cliPassword is not null)
        {
            return cliPassword;
        }

        if (session is not null && session.HasKnownPassword(machineId))
        {
            // Ta maszyna była już połączona w tym przebiegu (np. watch po info/ocr) — użyj tego samego
            // hasła bez ponownego pytania operatora (hasło pytane raz na cały przebieg).
            return session.KnownPassword(machineId);
        }

        if (noAuthPrompt)
        {
            return null;
        }

        if (_rememberedPassword is not null)
        {
            return _rememberedPassword;
        }

        if (!_askedRememberAll)
        {
            string pwd = PasswordPrompt.ReadPassword($"Hasło VNC do {displayName} ({host}:{port}): ");
            _askedRememberAll = true;
            bool useForAll = PasswordPrompt.ReadYesNo(
                "Użyć tego samego hasła dla wszystkich maszyn? [T/n]: ", defaultYes: true);
            if (useForAll)
            {
                _rememberedPassword = pwd;
            }

            return pwd;
        }

        // Operator wcześniej wybrał "n" (osobne hasło per maszyna) — pytamy bez ponownego pytania o "wszystkie".
        return PasswordPrompt.ReadPassword($"Hasło VNC do {displayName} ({host}:{port}): ");
    }

    private static async Task<ConnectOutcome> TryConnectAsync(
        string host, int port, string? password, string? username, TimeSpan connectTimeout, CancellationToken ct)
    {
        var options = new RfbClientOptions
        {
            Host = host,
            Port = port,
            Password = password,
            Username = username,
            Shared = true,
            ConnectTimeout = connectTimeout,
        };

        var client = new RfbClient(options);
        try
        {
            RfbServerInfo info = await client.ConnectAsync(ct).ConfigureAwait(false);
            return ConnectOutcome.Ok(client, info, password);
        }
        catch (RfbException ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return ConnectOutcome.Skip(BuildSkipReason(ex));
        }
    }

    /// <summary>Powód pominięcia maszyny, wypisywany w raporcie (internal — testowany wprost, patrz ConnectionHelperTests).</summary>
    internal static string BuildSkipReason(RfbException ex)
    {
        if (ex is RfbAuthenticationException authEx && IsUnsupportedSecurity(ex.Message))
        {
            string suffix = authEx.Message.Contains("(wymaga RSA-AES)", StringComparison.Ordinal)
                ? "\n  → ta maszyna wymaga szyfrowania RSA-AES – NIC nie zmieniaj w maszynie, prześlij ten raport."
                : "\n  → ten typ zabezpieczeń nie jest jeszcze obsługiwany – NIC nie zmieniaj w maszynie, prześlij ten raport.";
            return ex.Message + suffix;
        }

        if (IsMissingUsername(ex.Message))
        {
            return ex.Message +
                "\n  → szyfrowanie RSA-AES zostało uzgodnione, a serwer ODRZUCIŁ próbę z pustą nazwą" +
                "\n    użytkownika — dopisz \"username\" do wpisu tej maszyny w config.json albo uruchom" +
                "\n    probe.exe z --username <nazwa>.";
        }

        if (ex is RfbAuthenticationException || IsRequiresPassword(ex.Message) || IsWrongPassword(ex.Message))
        {
            // Maszyna odpowiedziała (handshake ruszył) — to NIE jest "nie odpowiada (wyłączona?)".
            return ex.Message;
        }

        return $"nie odpowiada (wyłączona?) — {ex.Message}";
    }

    private static bool IsRequiresPassword(string reason) =>
        reason.Contains("wymaga hasła VNC", StringComparison.Ordinal);

    private static bool IsUnsupportedSecurity(string reason) =>
        reason.Contains("oferuje tylko typy zabezpieczeń", StringComparison.Ordinal);

    private static bool IsWrongPassword(string reason) =>
        reason.Contains("odrzucił hasło VNC", StringComparison.Ordinal)
        // RSA-AES z nazwą użytkownika: serwer nie mówi, które z dwóch poświadczeń jest złe, więc
        // traktujemy to jak złe hasło — operator dostaje szansę wpisania go jeszcze raz.
        || reason.Contains("odrzucił poświadczenia RSA-AES", StringComparison.Ordinal);

    private static bool IsMissingUsername(string reason) =>
        reason.Contains("PUSTĄ nazwą użytkownika", StringComparison.Ordinal);
}
