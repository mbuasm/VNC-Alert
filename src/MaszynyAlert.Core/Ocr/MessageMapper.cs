using System.Text;
using System.Text.RegularExpressions;
using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Ocr;

public sealed class MessageMapper
{
    private const int RegexTimeoutMs = 200;
    private const int ShortTextMaxLength = 60;

    /// <summary>Ile znaków frazy musi przypadać na jedną dopuszczalną pomyłkę OCR — patrz <see cref="EffectiveDistance"/>.</summary>
    private const int FuzzyCharsPerEdit = 3;

    /// <summary>Prefiks <see cref="MappedMessage.RuleId"/> dla dopasowania po numerze alarmu: <c>code-705</c>.</summary>
    public const string CodeRuleIdPrefix = "code-";

    /// <summary>Baza dla maszyn bez własnej bazy: wspólne <c>codes</c> i <c>rules</c> z rules.json.</summary>
    private readonly CompiledDatabase _shared;

    /// <summary>Bazy błędów po identyfikatorze profilu ekranu (typie maszyn) — porównanie bez wielkości liter.</summary>
    private readonly Dictionary<string, CompiledDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _warnings = new();

    public MessageMapper(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _shared = Compile(null, rules.Codes, rules.Rules, codePattern: null, codeIdPrefix: string.Empty);

        foreach (KeyValuePair<string, RuleDatabase> entry in rules.Databases)
        {
            RuleDatabase? database = entry.Value;
            if (string.IsNullOrWhiteSpace(entry.Key) || database is null)
            {
                _warnings.Add("Baza błędów bez nazwy profilu (klucz w sekcji databases) — baza pominięta.");
                continue;
            }

            CompiledDatabase compiled = Compile(
                entry.Key, database.Codes, database.Rules, database.CodePattern, database.CodeIdPrefix ?? string.Empty);
            if (!_databases.TryAdd(entry.Key, compiled))
            {
                _warnings.Add($"Baza błędów \"{entry.Key}\" powtarza się w sekcji databases — użyta pierwsza, ta pominięta.");
            }
        }
    }

    private CompiledDatabase Compile(string? key, List<AlarmCode> codes, List<Rule> rules, string? codePattern, string codeIdPrefix)
    {
        string where = key is null ? "listach wspólnych" : $"bazie \"{key}\"";

        var table = new AlarmCodeTable(codes);
        foreach (string warning in table.Warnings)
        {
            _warnings.Add($"W {where}: {warning}");
        }

        var compiledRules = new List<CompiledRule>(rules.Count);
        foreach (Rule rule in rules)
        {
            Regex? pattern = null;
            if (!string.IsNullOrWhiteSpace(rule.Pattern))
            {
                try
                {
                    pattern = new Regex(
                        rule.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(RegexTimeoutMs));
                }
                catch (ArgumentException ex)
                {
                    _warnings.Add($"Reguła \"{rule.Id}\" w {where}: nieprawidłowe wyrażenie regularne \"{rule.Pattern}\" — reguła pominięta ({ex.Message}).");
                    continue;
                }
            }

            compiledRules.Add(new CompiledRule(rule, pattern));
        }

        Regex? alarmNumberPattern = null;
        if (!string.IsNullOrWhiteSpace(codePattern))
        {
            try
            {
                alarmNumberPattern = new Regex(
                    codePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(RegexTimeoutMs));
            }
            catch (ArgumentException ex)
            {
                _warnings.Add($"W {where}: nieprawidłowe wyrażenie regularne numeru alarmu \"{codePattern}\" — użyty zapis domyślny „No.705” ({ex.Message}).");
            }
        }

        return new CompiledDatabase(key, table, compiledRules, alarmNumberPattern, codeIdPrefix);
    }

    /// <summary>Ostrzeżenia zebrane przy kompilacji reguł i kodów (np. o pominiętych, błędnych wyrażeniach regularnych) — do logu.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Liczba użytecznych wpisów słownika kodów alarmów — ze WSZYSTKICH baz (do logu przy starcie).</summary>
    public int CodeCount => _shared.Codes.Count + _databases.Values.Sum(d => d.Codes.Count);

    /// <summary>Liczba reguł na treści komunikatu — ze WSZYSTKICH baz (do logu przy starcie).</summary>
    public int RuleCount => _shared.Rules.Count + _databases.Values.Sum(d => d.Rules.Count);

    /// <summary>
    /// Opis baz błędów do logu przy starcie
    /// — żeby z logu było widać, którym typom maszyn jaka baza rzeczywiście służy (pusty, gdy baz nie ma).
    /// </summary>
    public string DatabasesSummary => string.Join("; ", _databases.Select(
        entry => $"{entry.Key}: {entry.Value.Codes.Count} kodów, {entry.Value.Rules.Count} reguł"));

    public MappedMessage Map(string? ocrText, string? profileId = null)
    {
        string raw = ocrText ?? string.Empty;
        string normalized = TextNormalizer.Normalize(raw);
        // Baza tego typu maszyn (albo listy wspólne) — poza nią NIC się nie dopasowuje.
        CompiledDatabase database = DatabaseFor(profileId);

        if (normalized.Length == 0)
        {
            return new MappedMessage(null, "Błąd (nie odczytano treści)", "inny", raw, string.Empty, false);
        }

        string normalizedKeepDigits = TextNormalizer.NormalizeKeepDigits(raw);

        // 1. Numer alarmu w słowniku kodów — najpewniejsza ścieżka (patrz XML-doc wyżej).
        bool hasCode = database.TryExtractAlarmCode(raw, out int code);
        if (hasCode && database.Codes.TryResolve(code, out ResolvedAlarmCode resolved))
        {
            return new MappedMessage(
                database.CodeIdPrefix + CodeRuleIdPrefix + code, resolved.ShortText, resolved.Category, raw, normalized, true)
            {
                Priority = resolved.Priority,
                AlarmNumber = code.ToString(),
                CodeNamespace = database.CodeIdPrefix,
                // Wpis słownika kodów też może być „nie błąd” (np. No.4534 „plan produkcji wykonany”).
                Ignored = string.Equals(resolved.Category, Rule.IgnoredCategory, StringComparison.OrdinalIgnoreCase),
            };
        }

        MappedMessage? suppressed = null;
        foreach (CompiledRule candidate in CandidatesInOrder(database, profileId))
        {
            if (Matches(candidate, normalized, normalizedKeepDigits))
            {
                var message = new MappedMessage(candidate.Rule.Id, candidate.Rule.ShortText, candidate.Rule.Category, raw, normalized, true)
                {
                    Priority = candidate.Rule.Priority,
                    Ignored = candidate.Rule.Ignored || candidate.Rule.Suppressed,
                    Suppressed = candidate.Rule.Suppressed,
                };
                if (candidate.Rule.Suppressed)
                {
                    suppressed ??= message;
                    continue;
                }

                return message;
            }
        }

        if (suppressed is not null && !hasCode)
        {
            return suppressed;
        }

        if (hasCode)
        {
            // 3. Numer alarmu bez opisu w słowniku (ani w "codes", ani w regułach) — dokładnie TEN SAM numer
            // ma dać TEN SAM klucz alertu niezależnie od tego, jaki "śmieć" OCR doczytał po numerze (patrz
            // MappedMessage.Discriminator). Operator widzi numer i może go podać serwisowi / dopisać do rules.json.
            string alarmNumber = code.ToString();
            return new MappedMessage(null, $"Błąd nr {alarmNumber} (brak opisu w słowniku)", "inny", raw, normalized, false)
            {
                AlarmNumber = alarmNumber,
                // Numer 705 z listy jednego producenta to INNY błąd niż 705 z listy drugiego — klucz
                // alertu i instrukcji rozróżnia je przedrostkiem bazy (patrz RuleDatabase.CodeIdPrefix).
                CodeNamespace = database.CodeIdPrefix,
            };
        }

        if (!TextReadability.IsReadable(raw))
        {
            // Tekst wygląda na "śmieci" OCR (pojedyncze symbole, przypadkowe znaki) — surowy tekst i tak
            // trafia do Alert.FullText/logu (RawText niżej), ale nie do ShortText widocznego na pasku alertu.
            return new MappedMessage(null, "Błąd (nie odczytano treści)", "inny", raw, normalized, false);
        }

        return new MappedMessage(null, "Błąd: " + Shorten(raw), "inny", raw, normalized, false);
    }

    /// <summary>
    /// Czy baza profilu <paramref name="profileId"/> ma choć jedną regułę wyciszającą alarm
    /// (<see cref="Rule.SuppressedCategory"/>). Tylko na takich maszynach <c>Machines.MachineMonitor</c> czeka
    /// z alarmem na pierwszy odczyt treści — pozostałe dzwonią od razu po kolorze.
    /// </summary>
    public bool HasSuppressRules(string? profileId) =>
        CandidatesInOrder(DatabaseFor(profileId), profileId).Any(r => r.Rule.Suppressed);

    /// <summary>
    /// Baza błędów dla profilu ekranu <paramref name="profileId"/> (typu maszyn), a gdy taki profil nie ma
    /// własnej bazy — listy wspólne z rules.json. Patrz <see cref="Config.RuleDatabase"/>.
    /// </summary>
    private CompiledDatabase DatabaseFor(string? profileId) =>
        profileId is not null && _databases.TryGetValue(profileId, out CompiledDatabase? database) ? database : _shared;

    /// <summary>
    /// Kolejność sprawdzania reguł. We WŁASNEJ bazie typu maszyn to po prostu kolejność z pliku (wszystkie
    /// reguły bazy dotyczą tego typu). W listach wspólnych — najpierw reguły przypisane do
    /// tego profilu polem <c>profile</c>, potem reguły bez profilu.
    /// </summary>
    private static IEnumerable<CompiledRule> CandidatesInOrder(CompiledDatabase database, string? profileId)
    {
        if (database.Key is not null)
        {
            return database.Rules;
        }

        return SharedCandidatesInOrder(database.Rules, profileId);
    }

    private static IEnumerable<CompiledRule> SharedCandidatesInOrder(List<CompiledRule> rules, string? profileId)
    {
        if (profileId is not null)
        {
            foreach (CompiledRule r in rules)
            {
                if (r.Rule.Profile is not null && string.Equals(r.Rule.Profile, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    yield return r;
                }
            }
        }

        foreach (CompiledRule r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Rule.Profile))
            {
                yield return r;
            }
        }
    }

    private static bool Matches(CompiledRule candidate, string normalizedText, string normalizedKeepDigitsText)
    {
        if (candidate.Pattern is not null)
        {
            try
            {
                // Sprawdzamy OBA warianty: zwykły (litery/słowa) i "KeepDigits"
                // (numery komunikatów, patrz XML-doc Map) — regex dopasowany na KTÓRYMKOLWIEK wystarcza.
                if (candidate.Pattern.IsMatch(normalizedText) || candidate.Pattern.IsMatch(normalizedKeepDigitsText))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Regex nie zdążył w limicie czasu — traktujemy jak brak dopasowania, próbujemy dalej frazami.
            }
        }

        foreach (string phrase in candidate.Rule.Phrases)
        {
            string normalizedPhrase = TextNormalizer.Normalize(phrase);
            if (FuzzyContainsWords(normalizedText, normalizedPhrase, EffectiveDistance(normalizedPhrase, candidate.Rule.MaxDistance)))
            {
                return true;
            }
        }

        return false;
    }

    internal static int EffectiveDistance(string normalizedPhrase, int maxDistance) =>
        Math.Max(0, Math.Min(maxDistance, (normalizedPhrase.Length - 1) / FuzzyCharsPerEdit));

    /// <summary>
    /// Dopasowanie rozmyte frazy do CAŁYCH SŁÓW tekstu, nie do dowolnego podciągu: dokładne wystąpienie
    /// (<c>Contains</c>) wygrywa od razu; w przeciwnym razie porównujemy frazę (k słów) z każdym oknem
    /// k−1..k+1 kolejnych słów tekstu (zapas na słowo sklejone/rozdzielone przez OCR) odległością
    /// Levenshteina ≤ <paramref name="maxDistance"/>. Bez ograniczenia do słów krótka fraza „door" z
    /// tolerancją 2 łapała każde okno „D?R" wewnątrz dłuższych słów — np. „DUR" w „PROCEDURES", przez co
    /// żółty komunikat „All procedures have been processed…" mapował się na „Otwarte drzwi". Z ograniczeniem
    /// do słów „DOOOR OPEN" (pomyłka OCR) nadal trafia w „DOOR" (odległość 1), a „PROCEDURES" nie (odległość 6).
    /// </summary>
    private static bool FuzzyContainsWords(string normalizedText, string normalizedPhrase, int maxDistance)
    {
        if (normalizedPhrase.Length == 0)
        {
            return false;
        }

        if (normalizedText.Contains(normalizedPhrase, StringComparison.Ordinal))
        {
            return true;
        }

        if (maxDistance <= 0)
        {
            return false;
        }

        string[] words = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int phraseWords = normalizedPhrase.Count(c => c == ' ') + 1;
        int minWindow = Math.Max(1, phraseWords - 1);
        int maxWindow = phraseWords + 1;

        for (int window = minWindow; window <= maxWindow; window++)
        {
            for (int start = 0; start + window <= words.Length; start++)
            {
                string candidate = string.Join(' ', words, start, window);
                // Szybkie odrzucenie: różnica długości już przekracza tolerancję.
                if (Math.Abs(candidate.Length - normalizedPhrase.Length) > maxDistance)
                {
                    continue;
                }

                if (Levenshtein.Distance(candidate, normalizedPhrase) <= maxDistance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Skraca surowy tekst do <see cref="ShortTextMaxLength"/> znaków (pojedyncze spacje) + "…" gdy ucięty.</summary>
    private static string Shorten(string rawText)
    {
        string collapsed = CollapseWhitespace(rawText.Trim());
        return collapsed.Length <= ShortTextMaxLength ? collapsed : collapsed[..ShortTextMaxLength] + "…";
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }

    private sealed record CompiledRule(Rule Rule, Regex? Pattern);

    private sealed record CompiledDatabase(
        string? Key,
        AlarmCodeTable Codes,
        List<CompiledRule> Rules,
        Regex? AlarmNumberPattern,
        string CodeIdPrefix)
    {
        /// <summary>Wyłuskuje numer alarmu z surowego tekstu OCR zapisem właściwym dla tego typu maszyn.</summary>
        public bool TryExtractAlarmCode(string? rawText, out int code)
        {
            if (AlarmNumberPattern is null)
            {
                return TextReadability.TryExtractAlarmCode(rawText, out code);
            }

            return TextReadability.TryExtractAlarmCode(rawText, AlarmNumberPattern, out code);
        }
    }
}
