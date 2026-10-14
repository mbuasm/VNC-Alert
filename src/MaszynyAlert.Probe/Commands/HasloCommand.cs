using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Probe.Diagnoza;

namespace MaszynyAlert.Probe.Commands;

/// <summary>
/// <c>probe.exe haslo zapisz &lt;id&gt;</c> / <c>haslo usun [&lt;id&gt;]</c> / <c>haslo lista</c> — hasła VNC dla kolektora
/// długiego w <see cref="KolektorPasswordStore"/> (DPAPI CurrentUser). <c>zapisz</c> czyta hasło z ukrytego promptu
/// (albo z potoku, gdy wejście jest przekierowane — tak robi runner), NIGDY z argumentu wiersza poleceń. Ukryta opcja
/// <c>--plik &lt;ścieżka&gt;</c> wskazuje inny plik (testy). Hasło nie trafia do raportu.
/// </summary>
public sealed class HasloCommand : ICommand
{
    public Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct) =>
        Task.FromResult(Run(args, report));

    private static int Run(IReadOnlyList<string> args, Report report)
    {
        string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        string? file = ArgsUtil.FindOption(args, "--plik");
        List<string> positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (file is not null)
        {
            positional.Remove(file);
        }

        if (sub is not ("zapisz" or "usun" or "lista"))
        {
            throw new ProbeUsageException("Użycie: probe.exe haslo zapisz <id> | probe.exe haslo usun [<id>] | probe.exe haslo lista");
        }

        if (!KolektorPasswordStore.IsDpapiAvailable(out string? dpapiError))
        {
            report.Line($"DPAPI niedostępne w tej sesji ({dpapiError}) — hasła nie da się zapisać ani odczytać na tym koncie. " +
                "Uruchom z konta operatora zalogowanego interaktywnie.");
            return ProbeExitCodes.Error;
        }

        ISecretStore store = KolektorPasswordStore.Open(file);
        string path = file ?? KolektorPasswordStore.DefaultPath;
        switch (sub)
        {
            case "zapisz":
                if (positional.Count < 2 || string.IsNullOrWhiteSpace(positional[1]))
                {
                    throw new ProbeUsageException("Podaj identyfikator maszyny: probe.exe haslo zapisz amm580");
                }

                string id = positional[1];
                string password;
                if (ArgsUtil.HasFlag(args, "--z-env"))
                {
                    // Runner pakietu: hasło w środowisku procesu (Unicode), bez potoku. Potok PS 5.1 zależał od zakresu
                    // $OutputEncoding — przy starcie przez -Command polskie litery szły jako „?” (BOM + ASCII).
                    if (!string.Equals(id, "amm580", StringComparison.Ordinal))
                    {
                        throw new ProbeUsageException("--z-env dotyczy wyłącznie hasła 580: probe.exe haslo zapisz amm580 --z-env");
                    }

                    password = Environment.GetEnvironmentVariable(DiagnozaCommand.RunnerPasswordVariable) ?? string.Empty;
                }
                else
                {
                    password = PasswordPrompt.ReadPassword($"Hasło VNC do {id} (zapis zaszyfrowany DPAPI dla konta {Environment.UserName} na tym PC): ");
                }
                if (string.IsNullOrEmpty(password))
                {
                    report.Line("Puste hasło — nic nie zapisano.");
                    return ProbeExitCodes.Error;
                }

                store.Set(id, password);
                report.Line($"Hasło {id} zapisane dla kolektora, zaszyfrowane dla konta {Environment.UserName} na tym PC: {path}");
                return ProbeExitCodes.AllConnected;

            case "usun":
                if (positional.Count >= 2)
                {
                    bool removed = store.Remove(positional[1]);
                    report.Line(removed ? $"Hasło {positional[1]} usunięte." : $"Nie było hasła dla {positional[1]}.");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    report.Line($"Usunięto plik haseł {path}.");
                }
                else
                {
                    report.Line("Pliku haseł nie było.");
                }

                return ProbeExitCodes.AllConnected;

            default:
                IReadOnlyCollection<string> keys = store.Keys;
                report.Line(keys.Count == 0 ? $"Brak zapisanych haseł ({path})." : $"Zapisane hasła ({path}): {string.Join(", ", keys)} (treści nie pokazuję).");
                return ProbeExitCodes.AllConnected;
        }
    }
}
