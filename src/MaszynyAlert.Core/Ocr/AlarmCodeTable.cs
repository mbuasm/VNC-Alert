using MaszynyAlert.Core.Config;

namespace MaszynyAlert.Core.Ocr;

/// <summary>
/// Skompilowany słownik kodów alarmów (<see cref="RuleSet.Codes"/>): numer alarmu → krótki opis, kategoria,
/// priorytet. Pojedyncze numery wygrywają z zakresami (gdy ktoś świadomie nadpisze jeden numer z zakresu
/// osobnym wpisem); wpisy niepoprawne (bez numeru, zakres odwrócony, pusty opis) są pomijane z ostrzeżeniem
/// w <see cref="Warnings"/>, a nie wysadzają aplikacji — tak samo jak błędne regexy w <see cref="MessageMapper"/>.
/// </summary>
public sealed class AlarmCodeTable
{
    private const string PositionPlaceholder = "{n}";

    private readonly Dictionary<int, AlarmCode> _single = new();
    private readonly List<AlarmCode> _ranges = new();
    private readonly List<string> _warnings = new();

    public AlarmCodeTable(IEnumerable<AlarmCode> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        foreach (AlarmCode entry in codes)
        {
            if (string.IsNullOrWhiteSpace(entry.ShortText))
            {
                _warnings.Add($"Kod alarmu \"{entry.Label}\": pusty opis (shortText) — wpis pominięty.");
                continue;
            }

            if (entry.IsRange)
            {
                if (entry.Code.HasValue || !entry.From.HasValue || !entry.To.HasValue || entry.From > entry.To)
                {
                    _warnings.Add($"Kod alarmu \"{entry.Label}\": zakres wymaga pól from ≤ to (bez pola code) — wpis pominięty.");
                    continue;
                }

                _ranges.Add(entry);
            }
            else if (entry.Code.HasValue)
            {
                if (!_single.TryAdd(entry.Code.Value, entry))
                {
                    _warnings.Add($"Kod alarmu \"{entry.Label}\": numer powtarza się w sekcji codes — użyty pierwszy wpis, ten pominięty.");
                }
            }
            else
            {
                _warnings.Add("Kod alarmu bez numeru (brak pola code albo from/to) — wpis pominięty.");
            }
        }
    }

    /// <summary>Pusta tabela — żaden numer nie ma opisu.</summary>
    public static AlarmCodeTable Empty { get; } = new(Array.Empty<AlarmCode>());

    /// <summary>Ostrzeżenia zebrane przy budowie tabeli (pominięte wpisy) — do logu.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Liczba użytecznych wpisów (pojedynczych numerów + zakresów).</summary>
    public int Count => _single.Count + _ranges.Count;

    /// <summary>
    /// Szuka opisu dla numeru alarmu <paramref name="code"/>. Zwraca <see langword="true"/> i gotowy
    /// <paramref name="resolved"/> (z podstawionym <c>{n}</c> dla zakresów), gdy numer jest w słowniku.
    /// </summary>
    public bool TryResolve(int code, out ResolvedAlarmCode resolved)
    {
        if (_single.TryGetValue(code, out AlarmCode? single))
        {
            resolved = new ResolvedAlarmCode(code, single.ShortText.Replace(PositionPlaceholder, "?", StringComparison.Ordinal), single.Category, single.Priority);
            return true;
        }

        foreach (AlarmCode range in _ranges)
        {
            if (code >= range.From!.Value && code <= range.To!.Value)
            {
                int position = code - range.From.Value + 1;
                string text = range.ShortText.Replace(PositionPlaceholder, position.ToString(), StringComparison.Ordinal);
                resolved = new ResolvedAlarmCode(code, text, range.Category, range.Priority);
                return true;
            }
        }

        resolved = default;
        return false;
    }
}

/// <summary>Wynik <see cref="AlarmCodeTable.TryResolve"/>: numer alarmu i gotowy (po podstawieniu <c>{n}</c>) opis.</summary>
/// <param name="Code">Numer alarmu (np. 705).</param>
/// <param name="ShortText">Krótki opis po polsku, np. „Servo 6”.</param>
/// <param name="Category">Kategoria: narzedzie | tacka | restart | inny.</param>
/// <param name="Priority">Priorytet wpisu (patrz <see cref="AlarmCode.Priority"/>).</param>
public readonly record struct ResolvedAlarmCode(int Code, string ShortText, string Category, int Priority);
