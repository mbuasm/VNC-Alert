namespace MaszynyAlert.Probe.Commands;

public static class HelpCommand
{
    public static void Run(string? unknownCommand = null)
    {
        if (unknownCommand is not null)
        {
            Console.WriteLine($"Nieznana komenda: \"{unknownCommand}\".");
            Console.WriteLine();
        }

        Console.WriteLine("probe.exe — narzędzie rozpoznawcze VNC Alert");
        Console.WriteLine();
        Console.WriteLine("Dostępne komendy:");
        Console.WriteLine();

        PrintEntry(
            "auto [--seconds N] [--interval-min M] [--no-zip] [--no-bus] [--port P]  (ZALECANE — zacznij od tej)",
            [
                "robi po kolei to, co dotąd wymagało ręcznego wpisania info -> ocr -> watch: rozpoznaje maszyny,",
                "sprawdza OCR i sama znajduje pole postępu/nazwy pliku, obserwuje aż do Ctrl+C, nasłuchuje w tle",
                "testu sieci i na koniec pakuje folder \"out\" do zip. Hasła VNC pyta tylko RAZ na cały przebieg.",
            ],
            "probe.exe auto");

        PrintEntry(
            "diagnoza [--tryb szybki|dlugi] [--minuty N] [--co-sekund K] [--seconds S] [--no-zip] [--no-ocr]  (diagnostyka hali)",
            [
                "kompleksowa diagnostyka WSZYSTKICH maszyn z listy naraz (także enabled:false; zawęź przez --machines):",
                "sieć (TCP, skan portów, ponawianie co 60 s), uścisk dłoni RFB (surowy nagłówek, typy zabezpieczeń,",
                "odcisk klucza RSA-AES), klatki co K s (domyślnie 20) z pomiarami czerni/czerwieni/odcieni, test",
                "„ekran gaśnie po połączeniu”, OCR, shared-test, ponowne połączenie, częstość aktualizacji; po N minutach",
                "(domyślnie 35) podsumowanie out\\diagnoza\\podsumowanie.txt/.json i zip. Hasła pyta tylko na początku.",
                "Kod wyjścia: 0 wszystkie maszyny połączone, 3 część, 4 żadna, 1 błąd.",
                "--tryb dlugi = kolektor bez końca (zadanie Harmonogramu z runnera): klatka okresowa co 10 min, klatka+OCR przy",
                "każdej zmianie obrazu, epizody czerwieni/żółci w całej klatce (seria klatek 0/2/10/60 s, bbox, OCR) do",
                "out\\dlugi\\<id>\\epizody.jsonl; 520 tylko odpytywaniem (15 s co 5 min); limit dysku 2 GB/maszynę;",
                "520 są zawsze pomijane przed TCP w obu trybach; dawne --z-520 nie znosi blokady.",
            ],
            "probe.exe diagnoza --all --config .\\config.json --minuty 35");

        PrintEntry(
            "haslo zapisz <id> | haslo usun [<id>] | haslo lista",
            [
                "hasła VNC dla kolektora długiego (--tryb dlugi): zapis zaszyfrowany DPAPI dla bieżącego konta na tym PC",
                "(%LOCALAPPDATA%\\diagnoza-hali\\hasla.dpapi); hasło czytane z ukrytego promptu, nigdy z argumentu;",
                "kolektor czyta je sam, gdy maszyna wymaga hasła. Hasło nie trafia do logów ani podsumowania.",
            ],
            "probe.exe haslo zapisz amm580");

        PrintEntry(
            "info",
            [
                "łączy się po kolei ze wszystkimi maszynami z listy, sprawdza wersję RFB, typ uwierzytelnienia,",
                "rozmiar ekranu i format piksela; zapisuje zrzut całego ekranu każdej maszyny.",
            ],
            "probe.exe info");

        PrintEntry(
            "shared-test <adres:port>",
            [
                "test „czy podgląd się nie rozłącza”: łączy się jako drugi klient obok Twojego RealVNC Viewer",
                "i sprawdza, czy serwer rozłącza pierwszego albo odrzuca drugiego. Test OPCJONALNY, poza \"auto\"",
                "— uruchamiaj świadomie, może zerwać czyjś podgląd.",
            ],
            "probe.exe shared-test 192.168.1.108:9100");

        PrintEntry(
            "ocr [plik.png|folder] ...",
            [
                "sprawdza dostępność wbudowanego w Windows silnika OCR, odczytuje tekst ze zrzutów zapisanych",
                "wcześniej przez „probe.exe info” (out\\*_full.png) ORAZ przez „probe.exe capture”",
                "(out\\capture_<adres>_<port>_<data>.png) i samoczynnie wyznacza obszar pola postępu",
                "(„Milling Status” albo „Progress”) oraz nazwy pliku pracy („File Name”, a gdy tej etykiety nie ma",
                "— nazwa „*.nc”) — patrz out\\sugerowane-roi.json. Bez argumentów bierze wszystkie zrzuty",
                "z folderu --out; można też wskazać pojedyncze pliki albo folder.",
            ],
            "probe.exe ocr");

        PrintEntry(
            "watch [--seconds N] [--interval-min M]",
            [
                "ogląda wszystkie maszyny w pętli: zapisuje zrzut, gdy przybywa czerwieni (błąd, folder bledy),",
                "gdy zmienia się treść okienka alertów niezależnie od koloru (okno-alertow), okresowo co",
                "--interval-min minut (domyślnie 15, okresowe) i przy zmianie nazwy pliku pracy (nowa-praca).",
                "Zostaw okno otwarte na całą zmianę, zakończ przez Ctrl+C.",
            ],
            "probe.exe watch");

        PrintEntry(
            "bus-test [--port P] [--peer host]... [--seconds N]",
            [
                "test widoczności komputerów w sieci lokalnej (UDP) — uruchom TĘ SAMĄ komendę jednocześnie",
                "na dwóch komputerach operatorów. Przy \"auto\" ZBĘDNY — auto nasłuchuje w tle przez cały czas,",
                "wystarczy w ciągu zmiany uruchomić \"auto\" (albo tę komendę) na drugim komputerze.",
            ],
            "probe.exe bus-test");

        PrintEntry(
            "capture <adres:port> [--roi x,y,w,h]",
            ["pojedynczy zrzut ekranu jednej wskazanej maszyny, bez łączenia się z pozostałymi."],
            "probe.exe capture 192.168.1.110:5900");

        PrintEntry(
            "help",
            ["wypisuje tę listę komend."],
            "probe.exe help");

        Console.WriteLine("Opcje globalne (można dodać do każdej komendy powyżej):");
        Console.WriteLine("  --out <folder>       zmienia folder wyjściowy (domyślnie: out)");
        Console.WriteLine("  --password <hasło>   hasło VNC dla wszystkich maszyn w tej komendzie");
        Console.WriteLine("  --username <nazwa>   nazwa użytkownika do logowania systemowego (RealVNC/RSA-AES),");
        Console.WriteLine("                       gdy nie ma jej w config.json — np. maszyna 580");
        Console.WriteLine("  --no-auth-prompt      nie pytaj o hasło — maszyny je wymagające zostaną pominięte");
        Console.WriteLine("  --config <plik>      plik konfiguracji zamiast config.json obok probe.exe");
        Console.WriteLine("  --machines <id,id>   tylko te maszyny (identyfikatory z config.json), także wyłączone (enabled:false)");
        Console.WriteLine("  --all                 wszystkie maszyny z listy, także wyłączone (diagnoza ma to domyślnie)");
        Console.WriteLine();
        Console.WriteLine("Kody wyjścia komend łączących się z maszynami (info, watch, auto, diagnoza): 0 = wszystkie wybrane");
        Console.WriteLine("  maszyny połączone, 3 = część, 4 = żadna, 1 = błąd użycia/awaria (Ctrl+C po starcie = jak 0/3/4).");
    }

    private static void PrintEntry(string usage, string[] descLines, string example)
    {
        Console.WriteLine($"  {usage}");
        foreach (string line in descLines)
        {
            Console.WriteLine($"      {line}");
        }

        Console.WriteLine($"      przykład: {example}");
        Console.WriteLine();
    }
}
