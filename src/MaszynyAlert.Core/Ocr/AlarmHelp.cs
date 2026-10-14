using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.Core.Ocr;

/// <summary>Operator guidance; never changes controller parameters or requests a remote reset.</summary>
public static class AlarmHelp
{
    public static string For(Alert alert) => Format(alert, InstructionsFor(alert));

    public static string Format(Alert alert, string instructions) => alert.Kind == AlertKind.Offline
        ? instructions
        : $"{alert.ShortText}\n\n{instructions}\n\n„Idę sprawdzić” wycisza powiadomienie. Nie kasuje błędu w maszynie.";

    public static string InstructionKey(Alert alert)
    {
        if (alert.Kind == AlertKind.Offline) return "offline";
        if (CodeFor(alert) is { } code) return $"code:{code}";
        string discriminator = alert.Key.Discriminator;
        if (discriminator.StartsWith("alarm:", StringComparison.Ordinal)) return "code:" + discriminator["alarm:".Length..];
        // Ten sam numer, gdy JEST już w słowniku bazy, ma klucz alertu. Instrukcja
        // musi trafić pod TEN SAM klucz co wyżej — inaczej treść wpisana dla numeru
        // spoza słownika znikałaby operatorowi w chwili, gdy ktoś dopisze ten numer do bazy (np. wgrywając
        // listę producenta), a Panel pokazywałby dwa wiersze dla jednego numeru.
        if (TryPrefixedCode(discriminator) is { } prefixed) return "code:" + prefixed;
        return discriminator.StartsWith("rule:", StringComparison.Ordinal)
            ? discriminator : $"category:{alert.Category}";
    }

    public const string ProgramAbortRuleSuffix = "-program-abort";

    /// <summary>Czy alert to „program przerwany” z pudełka stanu programu (patrz <see cref="ProgramAbortRuleSuffix"/>).</summary>
    public static bool IsProgramAbort(Alert alert) =>
        alert.Kind == AlertKind.Error
        && alert.Key.Discriminator.StartsWith("rule:", StringComparison.Ordinal)
        && alert.Key.Discriminator.EndsWith(ProgramAbortRuleSuffix, StringComparison.Ordinal);

    private static string? TryPrefixedCode(string discriminator)
    {
        const string rule = "rule:";
        const string marker = "code-";
        if (!discriminator.StartsWith(rule, StringComparison.Ordinal)) return null;
        int at = discriminator.LastIndexOf(marker, StringComparison.Ordinal);
        if (at <= rule.Length) return null; // bez przedrostka bazy numer obsługuje CodeFor
        string number = discriminator[(at + marker.Length)..];
        if (number.Length is 0 or > 4 || !number.All(char.IsAsciiDigit)) return null;
        return discriminator[rule.Length..at] + number;
    }

    private static int? CodeFor(Alert alert)
    {
        string key = alert.Key.Discriminator;
        int? code = int.TryParse(key.Replace("rule:code-", "").Replace("alarm:", ""), out int n) ? n : null;
        return code ?? (key switch { "rule:home-request" => 1200, "rule:tool-setting-exceeds" => 1615, "rule:tray-grab" => 1248, _ => (int?)null });
    }

    public static string InstructionsFor(Alert alert)
    {
        if (alert.Kind == AlertKind.Offline)
            return "1. Sprawdź, czy maszyna jest włączona i czy jej ekran działa.\n2. Sprawdź widoczne połączenie przewodu sieciowego.\n3. Jeśli ekran pokazuje konflikt adresu IP, przekaż jego zdjęcie osobie obsługującej sieć. Nie zmieniaj adresów maszyny samodzielnie.";
        if (IsProgramAbort(alert))
            return "Maszyna przerwała program w trakcie pracy (na ekranie „IDLE: Abort”), bez komunikatu błędu — najczęściej pęknięte albo złamane narzędzie.\n1. Po bezpiecznym zatrzymaniu sprawdź narzędzie we wrzecionie i obrabiany detal.\n2. Uszkodzone narzędzie wymień i zmierz jego długość zgodnie z instrukcją stanowiska.\n3. Wznów pracę (BreakPoint Resume) albo uruchom program od początku.\nAlert zniknie sam, gdy maszyna wróci do pracy (RUN).";
        int? code = CodeFor(alert);
        string steps = code switch
        {
            200 or 1499 => "Sprawdź przyczynę zatrzymania awaryjnego. Przed odblokowaniem upewnij się zgodnie z procedurą stanowiska, że zagrożenie zostało usunięte. Nie zwalniaj zatrzymania tylko po to, by skasować komunikat.",
            1200 or 1700 => "Maszyna wymaga bazowania osi. Sprawdź przestrzeń roboczą i wykonaj bazowanie (Home) zgodnie z instrukcją stanowiska. Jeśli bazowanie się nie powiedzie, zgłoś problem osobie odpowiedzialnej za maszynę lub serwisowi.",
            1615 => "Pomiar narzędzia jest poza tolerancją. Po bezpiecznym zatrzymaniu sprawdź stan i osadzenie wskazanego narzędzia oraz czystość miejsca pomiaru. Ponowny pomiar wykonaj według instrukcji maszyny; nie zwiększaj tolerancji, aby ominąć błąd.",
            1204 => "Sprawdź wskazanie ciśnienia i dostępność sprężonego powietrza według instrukcji stanowiska. Jeśli ciśnienie jest nieprawidłowe lub widać nieszczelność, zgłoś problem osobie odpowiedzialnej za instalację.",
            1203 => "Sprawdź widoczne wskazania układu chłodzenia i jego komunikaty. Zgłoś nieprawidłowy poziom płynu, temperaturę lub pracę pompy osobie odpowiedzialnej za maszynę.",
            1248 => "Chwytak Z2 nie pobrał tacki. Po bezpiecznym zatrzymaniu sprawdź obecność i poprawne ułożenie wskazanej tacki. Nie wymuszaj ruchu chwytaka. Ponowienie pobrania wykonaj według procedury stanowiska; jeśli błąd wraca, zgłoś go serwisowi.",
            1249 => "Chwytak Z2 nie odłożył tacki. Sprawdź, czy wskazane miejsce odkładania jest wolne i czy tacka jest poprawnie ułożona. Nie przesuwaj ręcznie pracującego mechanizmu. Dalszą obsługę wykonaj według procedury stanowiska.",
            1618 or 1630 or 1631 => "Sprawdź numer tacki przypisany do pracy oraz obecność materiału w odpowiadającym mu miejscu magazynu. Popraw przypisanie według procedury przygotowania pracy; nie zmieniaj konfiguracji pojemności magazynu.",
            1633 => "Miejsce odkładania tacki jest zajęte. Sprawdź zgodność fizycznego ułożenia materiału z listą magazynu, a następnie uporządkuj je według instrukcji stanowiska.",
            >= 1225 and <= 1234 => $"Narzędzie {code.Value - 1224} osiągnęło limit zużycia. Wymień je zgodnie z instrukcją stanowiska. Licznik zużycia zeruj dopiero po rzeczywistej wymianie właściwego narzędzia.",
            342 or 199 => "Sprawdź stan narzędzia i dostępność czujnika pomiarowego po bezpiecznym zatrzymaniu. Usuń zanieczyszczenia zgodnie z instrukcją stanowiska. Nie zmieniaj zakresu czujnika ani tolerancji pomiaru, aby skasować alarm.",
            1632 => "Narzędzie pozostało we wrzecionie. Sprawdź komunikat wymiany i właściwe miejsce magazynowe. Nie wyciągaj narzędzia z aktywnego wrzeciona; użyj procedury odzyskania po błędzie wymiany.",
            520 or 2502 => "Ten komunikat może towarzyszyć innemu alarmowi. Sprawdź pozostałe numery wyświetlane kolejno na maszynie i usuń najpierw przyczynę wskazaną przez alarm napędu, narzędzia lub tacki. Jeśli przyczyna nie jest jasna, przekaż komplet kodów serwisowi.",
            622 or 1020 or 1023 or 1025 or >= 1040 and <= 1043 or >= 1107 and <= 1115 or 1172 or 1503 => "Sprawdź nazwę pliku pracy i numer linii wskazany przez sterownik. Przekaż je osobie przygotowującej pracę w CAM; ponownie wczytaj właściwy, zweryfikowany plik według procedury stanowiska. Nie poprawiaj przypadkowo kodu na maszynie.",
            >= 700 and <= 759 => $"Komunikat dotyczy napędu osi {(code.Value - 700) % 20 + 1}. Zapisz numer alarmu i sprawdź komunikaty napędu zgodnie z instrukcją stanowiska. Nie wymuszaj ruchu osi ani nie zmieniaj parametrów serwa. Powtarzający się alarm wymaga serwisu.",
            >= 760 and <= 799 or >= 3300 and <= 3340 => "Oś osiągnęła limit ruchu. Sprawdź komunikat na maszynie i zgłoś problem z pozycją lub programem pracy. Nie omijaj krańcówek ani ograniczeń programowych.",
            _ => alert.Category switch
            {
                "narzedzie" => "Po bezpiecznym zatrzymaniu sprawdź wskazane narzędzie: stan, osadzenie i zgodność z pracą. Wymianę lub ponowny pomiar wykonaj zgodnie z procedurą stanowiska. Jeśli komunikat wraca, zgłoś numer alarmu serwisowi.",
                "tacka" => "Sprawdź wskazaną tackę lub materiał oraz ich położenie według instrukcji stanowiska. Nie wkładaj rąk do pracującego mechanizmu. Ponów czynność dopiero po usunięciu przyczyny; przy ponownym błędzie zgłoś problem.",
                _ => "Sprawdź pełny komunikat na ekranie maszyny i odszukaj ten numer w instrukcji stanowiska. Zapisz numer alarmu oraz wykonywaną pracę. Jeśli brak zatwierdzonej procedury dla tego kodu, przekaż informacje osobie odpowiedzialnej za maszynę lub serwisowi; nie zmieniaj parametrów sterownika."
            }
        };
        return steps;
    }
}
