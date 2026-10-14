using System.Text.RegularExpressions;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.History;

/// <summary>Rodzaje błędu w raporcie postojów poza kategoriami słownika (<c>narzedzie</c>, <c>tacka</c>, <c>restart</c>, <c>inny</c>).</summary>
public static class DowntimeCategories
{
    /// <summary>Błąd bez opisu w słowniku albo nieczytelny — ta sama kategoria, którą nadaje mu <c>Ocr.MessageMapper</c>.</summary>
    public const string Other = "inny";

    public const string Unrecognized = "nierozpoznany";

    public const string Idle = Alerts.AlertStateMachine.IdleCategory;

    /// <summary>Postój bez błędu „Stoi na 0 % – wciśnij Start” (kategoria <see cref="Alerts.AlertStateMachine.IdleAtZeroCategory"/>).</summary>
    public const string IdleAtZero = Alerts.AlertStateMachine.IdleAtZeroCategory;

    /// <summary>Czy rodzaj to postój bez błędu (<see cref="Idle"/> albo <see cref="IdleAtZero"/>).</summary>
    public static bool IsIdle(string? category) => KindOf(category) != DowntimeKind.Error;

    public static DowntimeKind KindOf(string? category)
    {
        string? normalized = Normalize(category);
        if (string.Equals(normalized, Idle, StringComparison.OrdinalIgnoreCase)) return DowntimeKind.Idle;
        if (string.Equals(normalized, IdleAtZero, StringComparison.OrdinalIgnoreCase)) return DowntimeKind.IdleAtZero;
        return DowntimeKind.Error;
    }

    /// <summary>
    /// Rodzaj postoju bez błędu rozpoznany z tekstu alertu (wpis bez pola <c>Category</c>, np. przekazany dalej przez wersję,
    /// która go nie zna) albo <see langword="null"/>, gdy tekst nie jest tekstem alertu postoju.
    /// </summary>
    public static string? IdleCategoryOf(string? reason)
    {
        string text = (reason ?? string.Empty).Trim();
        return text == Alerts.AlertStateMachine.MachineIdleShortText ? Idle
            : text == Alerts.AlertStateMachine.MachineIdleAtZeroShortText ? IdleAtZero
            : null;
    }

    /// <summary>Rodzaj epizodu do raportu: zapisany, a bez niego — postój bez błędu rozpoznany z tekstu.</summary>
    public static string? Of(DowntimeEpisode episode) =>
        Normalize(episode.Category) ?? IdleCategoryOf(episode.Reason);

    public static string? Normalize(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        string trimmed = category.Trim();
        return string.Equals(trimmed, Rule.IgnoredCategory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, Rule.SuppressedCategory, StringComparison.OrdinalIgnoreCase)
            ? null : trimmed;
    }
}

/// <summary>
/// Rodzaj błędu dla epizodów zapisanych bez <see cref="DowntimeEpisode.Category"/> (sprzed wersji z tym polem albo
/// przekazanych dalej przez starszą wersję w grupie). Klasyfikacja idzie po zapisanym tekście
/// (<see cref="DowntimeEpisode.Reason"/> = <c>Alert.ShortText</c>) i słowniku błędów typu maszyny: identyfikator
/// maszyny → <c>MachineConfig.Profile</c> → baza błędów tego profilu (<see cref="RuleSet.Databases"/>, nazwa bez wielkości
/// liter, a bez własnej bazy — listy wspólne), dokładnie jak w <c>Ocr.MessageMapper</c>. Alarm na żywo podaje mu
/// <c>ScreenProfile.Id</c> profilu maszyny — równy kluczowi profilu: z config.json ustawia go tak
/// <see cref="ConfigLoader.ParseAppConfig(string)"/>, w profilu grupy wymusza to
/// <see cref="GroupFileCodec"/>. Dopasowanie jest ODWROTNE: tekst epizodu
/// porównuje się z krótkimi opisami (<c>shortText</c>) kodów i reguł; opis zakresu kodów ze znacznikiem <c>{n}</c>
/// dopasowuje się z dowolnym numerem pozycji, a pojedynczego kodu — ze znakiem „?” w miejscu <c>{n}</c>.
/// <para>
/// Kolejność jak przy rozpoznawaniu na żywo: pojedyncze kody, zakresy, reguły (w listach wspólnych najpierw
/// reguły profilu). Wpisy „nie błąd” i „bez alarmu” są pomijane — ich tekst nigdy nie zostaje opisem alertu.
/// Teksty zastępcze <c>MessageMapper</c> („Błąd nr …”, „Błąd: …”, „Błąd (nie odczytano treści)”) dają
/// <see cref="DowntimeCategories.Other"/>, bo taką kategorię miał alert; reszta —
/// <see cref="DowntimeCategories.Unrecognized"/>.
/// </para>
/// <para>
/// Klasyfikacja liczy się przy budowie raportu i niczego nie zapisuje (plik <c>downtime.jsonl</c> zostaje bez
/// zmian). Wynik zależy od bieżącego słownika: tekst, którego już w nim nie ma, staje się „nierozpoznany”.
/// Klasa nie jest bezpieczna wątkowo (pamięć podręczna wyników) — tworzona na jedno odświeżenie raportu.
/// </para>
/// </summary>
public sealed class DowntimeCategoryClassifier
{
    private const string ProgramAbortCategory = "narzedzie";

    private const string PositionPlaceholder = "{n}";

    private readonly Dictionary<string, string> _profileByMachine = new(StringComparer.Ordinal);
    private readonly RuleSet _rules;

    private readonly Dictionary<string, RuleDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<Matcher>> _matchersByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Machine, string Reason), string> _cache = new();

    public DowntimeCategoryClassifier(RuleSet rules, IEnumerable<MachineConfig> machines)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(machines);
        _rules = rules;
        if (rules.Databases is not null)
            foreach (var (key, database) in rules.Databases)
                if (!string.IsNullOrWhiteSpace(key) && database is not null)
                    _databases.TryAdd(key, database);
        foreach (var machine in machines)
            if (machine is not null && !string.IsNullOrWhiteSpace(machine.Id) && !string.IsNullOrWhiteSpace(machine.Profile))
                _profileByMachine.TryAdd(machine.Id, machine.Profile);
    }

    /// <summary>
    /// Rodzaj błędu epizodu: zapisany w epizodzie (po <see cref="DowntimeCategories.Normalize"/>), a gdy go nie ma
    /// albo jest pusty, „nie błąd” lub „bez alarmu” — z <see cref="Classify"/>.
    /// </summary>
    public string CategoryOf(DowntimeEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return DowntimeCategories.Normalize(episode.Category) ?? Classify(episode.MachineId, episode.Reason);
    }

    /// <summary>Rodzaj błędu z zapisanego tekstu <paramref name="reason"/> maszyny <paramref name="machineId"/> (opis klasy).</summary>
    public string Classify(string machineId, string? reason)
    {
        string text = (reason ?? string.Empty).Trim();
        var key = (machineId ?? string.Empty, text);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        string result = ClassifyUncached(key.Item1, text);
        _cache[key] = result;
        return result;
    }

    private string ClassifyUncached(string machineId, string text)
    {
        if (_profileByMachine.TryGetValue(machineId, out var profile) && text.Length > 0)
        {
            foreach (var matcher in MatchersFor(profile))
                if (matcher.IsMatch(text)) return matcher.Category;
        }
        if (DowntimeCategories.IdleCategoryOf(text) is { } idle) return idle;
        if (IsPlaceholderText(text)) return DowntimeCategories.Other;
        if (text == Machines.MachineMonitor.ProgramAbortShortText) return ProgramAbortCategory;
        return DowntimeCategories.Unrecognized;
    }

    /// <summary>Teksty, które <c>Ocr.MessageMapper.Map</c> nadaje błędowi bez opisu w słowniku (kategoria „inny”).</summary>
    private static bool IsPlaceholderText(string text) =>
        text.StartsWith("Błąd nr ", StringComparison.Ordinal)
        || text.StartsWith("Błąd: ", StringComparison.Ordinal)
        || text == "Błąd (nie odczytano treści)";

    private List<Matcher> MatchersFor(string profile)
    {
        if (_matchersByProfile.TryGetValue(profile, out var cached)) return cached;
        var matchers = new List<Matcher>();
        if (_databases.TryGetValue(profile, out var database))
        {
            AddCodes(matchers, database.Codes);
            AddRules(matchers, database.Rules);
        }
        else
        {
            AddCodes(matchers, _rules.Codes);
            // Listy wspólne: najpierw reguły przypisane do profilu, potem reguły bez profilu (jak w MessageMapper).
            AddRules(matchers, _rules.Rules.Where(r => r?.Profile is not null
                && string.Equals(r.Profile, profile, StringComparison.OrdinalIgnoreCase)));
            AddRules(matchers, _rules.Rules.Where(r => r is not null && string.IsNullOrWhiteSpace(r.Profile)));
        }
        _matchersByProfile[profile] = matchers;
        return matchers;
    }

    private static void AddCodes(List<Matcher> matchers, IEnumerable<AlarmCode>? codes)
    {
        if (codes is null) return;
        var list = codes.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.ShortText) && !IsSilent(c.Category)).ToArray();
        // Pojedyncze numery wygrywają z zakresami — tak samo jak w Ocr.AlarmCodeTable.TryResolve.
        foreach (var code in list.Where(c => !c.IsRange && c.Code.HasValue))
            matchers.Add(Matcher.Exact(code.ShortText.Replace(PositionPlaceholder, "?", StringComparison.Ordinal), code.Category));
        foreach (var range in list.Where(c => c.IsRange && c.From.HasValue && c.To.HasValue && c.From <= c.To && !c.Code.HasValue))
            matchers.Add(range.ShortText.Contains(PositionPlaceholder, StringComparison.Ordinal)
                ? Matcher.Pattern(range.ShortText, range.Category)
                : Matcher.Exact(range.ShortText, range.Category));
    }

    private static void AddRules(List<Matcher> matchers, IEnumerable<Rule>? rules)
    {
        if (rules is null) return;
        foreach (var rule in rules)
            if (rule is not null && !string.IsNullOrWhiteSpace(rule.ShortText) && !IsSilent(rule.Category))
                matchers.Add(Matcher.Exact(rule.ShortText, rule.Category));
    }

    /// <summary>Kategorie „nie błąd” i „bez alarmu” — ich opis nigdy nie trafia do alertu, więc i do dziennika postojów.</summary>
    private static bool IsSilent(string? category) =>
        string.IsNullOrWhiteSpace(category)
        || string.Equals(category, Rule.IgnoredCategory, StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, Rule.SuppressedCategory, StringComparison.OrdinalIgnoreCase);

    private sealed record Matcher(string? Text, Regex? Regex, string Category)
    {
        public static Matcher Exact(string text, string category) => new(text.Trim(), null, category);

        /// <summary><c>{n}</c> = numer pozycji w zakresie (liczba całkowita), reszta tekstu dosłownie.</summary>
        public static Matcher Pattern(string shortText, string category)
        {
            string pattern = "^" + string.Join(@"\d+", shortText.Trim()
                .Split(PositionPlaceholder).Select(Regex.Escape)) + "$";
            return new(null, new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)), category);
        }

        public bool IsMatch(string text)
        {
            if (Text is not null) return string.Equals(Text, text, StringComparison.Ordinal);
            try { return Regex!.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
