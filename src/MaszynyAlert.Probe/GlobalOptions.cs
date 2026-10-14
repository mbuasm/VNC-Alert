namespace MaszynyAlert.Probe;

public sealed record GlobalOptions(
    string OutDir,
    string? Password,
    bool NoAuthPrompt,
    string? Username = null,
    string? ConfigPath = null,
    IReadOnlyList<string>? Machines = null,
    bool All = false)
{
    /// <summary>Prawda, gdy operator sam wskazał maszyny (<c>--machines</c>) albo zażądał wszystkich (<c>--all</c>) — wtedy <c>enabled:false</c> nie wyklucza maszyny.</summary>
    public bool HasMachineFilter => All || Machines is { Count: > 0 };
}

/// <summary>Wyodrębnianie opcji globalnych z listy argumentów oraz maskowanie haseł do raportu.</summary>
public static class ArgsUtil
{
    /// <summary>
    /// Zdejmuje opcje globalne (<c>--out</c>, <c>--password</c>, <c>--username</c>, <c>--no-auth-prompt</c>,
    /// <c>--config</c>, <c>--machines</c>, <c>--all</c>) z listy argumentów, gdziekolwiek się w niej znajdują,
    /// i zwraca resztę (specyficzną dla komendy).
    /// </summary>
    public static (GlobalOptions Global, List<string> Remaining) ExtractGlobal(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string outDir = "out";
        string? password = null;
        string? username = null;
        string? configPath = null;
        List<string>? machines = null;
        bool all = false;
        bool noAuthPrompt = false;
        var remaining = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--out", StringComparison.OrdinalIgnoreCase))
            {
                outDir = RequireValue(args, ref i, "--out");
            }
            else if (string.Equals(a, "--password", StringComparison.OrdinalIgnoreCase))
            {
                password = RequireValue(args, ref i, "--password");
            }
            else if (string.Equals(a, "--username", StringComparison.OrdinalIgnoreCase))
            {
                username = RequireValue(args, ref i, "--username");
            }
            else if (string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase))
            {
                configPath = RequireValue(args, ref i, "--config");
            }
            else if (string.Equals(a, "--machines", StringComparison.OrdinalIgnoreCase))
            {
                machines ??= [];
                machines.AddRange(ParseMachineIds(RequireValue(args, ref i, "--machines")));
            }
            else if (string.Equals(a, "--all", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
            }
            else if (string.Equals(a, "--no-auth-prompt", StringComparison.OrdinalIgnoreCase))
            {
                noAuthPrompt = true;
            }
            else
            {
                remaining.Add(a);
            }
        }

        return (new GlobalOptions(outDir, password, noAuthPrompt, username, configPath, machines, all), remaining);
    }

    /// <summary>Rozbiera listę <c>id,id,…</c> (przecinki i/lub średniki, białe znaki ignorowane); pusta lista = błąd użycia.</summary>
    internal static List<string> ParseMachineIds(string text)
    {
        List<string> ids = text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (ids.Count == 0)
        {
            throw new ProbeUsageException("Opcja --machines wymaga listy identyfikatorów maszyn rozdzielonych przecinkami, np. --machines amm520-1,amm580.");
        }

        return ids;
    }

    /// <summary>Zwraca wartość opcji o nazwie <paramref name="name"/> spośród <paramref name="args"/> (pierwsze wystąpienie), albo <see langword="null"/>.</summary>
    public static string? FindOption(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                int idx = i;
                return RequireValue(args, ref idx, name);
            }
        }

        return null;
    }

    /// <summary>Zwraca wszystkie wartości opcji powtarzalnej o nazwie <paramref name="name"/> (np. <c>--peer</c>).</summary>
    public static List<string> FindOptionAll(IReadOnlyList<string> args, string name)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                int idx = i;
                result.Add(RequireValue(args, ref idx, name));
                i = idx;
            }
        }

        return result;
    }

    /// <summary>Prawda, gdy przełącznik (opcja bez wartości) o nazwie <paramref name="name"/> jest obecny w <paramref name="args"/>.</summary>
    public static bool HasFlag(IReadOnlyList<string> args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string RequireValue(IReadOnlyList<string> args, ref int i, string optionName)
    {
        if (i + 1 >= args.Count)
        {
            throw new ProbeUsageException($"Brakuje wartości dla opcji {optionName}.");
        }

        i++;
        return args[i];
    }

    /// <summary>Maskuje wartość po <c>--password</c> na potrzeby zapisu wywołania w raporcie (hasła nigdy nie trafiają do plików).</summary>
    public static string MaskForReport(IReadOnlyList<string> args)
    {
        var parts = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            parts.Add(args[i]);
            if (string.Equals(args[i], "--password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                parts.Add("***");
                i++;
            }
        }

        return string.Join(' ', parts);
    }
}
