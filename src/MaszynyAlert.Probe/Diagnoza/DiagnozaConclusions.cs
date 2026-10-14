using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Wnioski o jednej maszynie wyprowadzone z <see cref="MachineDiagnosticResult"/> — do tabeli i zdań „co dalej”.</summary>
public sealed record MachineConclusion(
    string CzarnyEkran,
    string Motyw,
    string NajlepszyProfil,
    string Ekran,
    string SugerowanyRoi,
    IReadOnlyList<string> Wnioski,
    string CoDalej);

public static class DiagnozaConclusions
{
    public const string BrakCzerwieni = "brak czerwieni w przebiegu";

    public static MachineConclusion Build(MachineDiagnosticResult r, IReadOnlyDictionary<string, ScreenProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(profiles);

        var wnioski = new List<string>();
        string czarny = BlackVerdict(r, wnioski);
        string motyw = ThemeVerdict(r);
        string profil = BestProfile(r, profiles, wnioski);
        string ekran = r.Server is { } s ? $"{s.Width}x{s.Height}" : "-";
        string sugerowanyRoi = SuggestedRedRoi(r, wnioski);

        if (!r.EverConnected)
        {
            wnioski.Add(r.Status switch
            {
                MachineStatus.Unreachable => $"nie odpowiada na porcie {r.Port}" + (r.PortScan is { } scan ? $"; {PortScanner.DescribeScan(scan)}" : string.Empty)
                    + $" (prób TCP: {r.TcpProbes.Count}, otwartych: {r.TcpProbes.Count(p => p.Open)})",
                MachineStatus.PasswordRequired => "serwer wymaga hasła VNC, a hasła nie podano — maszyna pozostaje pominięta",
                MachineStatus.WrongPassword => "serwer odrzucił podane hasło VNC",
                MachineStatus.WrongCredentials => "serwer odrzucił poświadczenia RSA-AES (nazwa użytkownika/hasło)",
                MachineStatus.MissingUsername => "serwer RSA-AES wymaga nazwy użytkownika (logowanie systemowe) — potrzebne potwierdzenie poświadczeń",
                MachineStatus.UnsupportedSecurity => "serwer oferuje tylko nieobsługiwane typy zabezpieczeń",
                MachineStatus.ConfigError => r.Reason ?? "błąd konfiguracji",
                _ => r.Reason ?? "nie połączono",
            });
        }
        else
        {
            if (r.Server is { } info)
            {
                wnioski.Add($"RFB {info.ProtocolVersion} (nagłówek {info.RawVersionHeader}), {info.UsedSecurityType}, pulpit \"{info.DesktopName}\", {info.PixelFormat.Describe()}" +
                    (info.ServerKeySha256 is { } key ? $", odcisk klucza RSA {key[..Math.Min(16, key.Length)]}…" : string.Empty));
            }

            if (r.FirstFrameMs is { } ms)
            {
                wnioski.Add($"pierwsza klatka po {ms} ms");
            }

            int unplanned = r.Disconnects.Count(d => !d.Przyczyna.StartsWith("planowe", StringComparison.Ordinal));
            wnioski.Add(unplanned == 0
                ? $"bez zerwań (połączeń: {r.Connections})"
                : $"zerwań: {unplanned} (połączeń: {r.Connections}) — ostatnie: {r.Disconnects.Last(d => !d.Przyczyna.StartsWith("planowe", StringComparison.Ordinal)).Przyczyna}");

            if (r.ReconnectTest is { } rt)
            {
                wnioski.Add($"ponowne połączenie: {rt.Opis}" + (rt.PierwszaKlatkaMs is { } rms ? $" ({rms} ms do klatki)" : string.Empty));
            }

            if (r.SharedTest is { } st)
            {
                wnioski.Add($"drugi klient: {st.Opis}");
            }

            if (r.UpdateRate is { } ur)
            {
                wnioski.Add($"aktualizacje przyrostowe: {ur.Opis}");
            }

            wnioski.Add($"klatek {r.Frames.Count}, zmian obrazu {r.HashChanges}, " +
                (r.Frames.Count > 0 ? $"odpowiedź serwera {r.Frames.Min(f => f.OdpowiedzMs)}–{r.Frames.Max(f => f.OdpowiedzMs)} ms" : "bez klatek"));

            wnioski.Add(r.OcrLanguages.Count == 0
                ? "OCR niedostępny na tym komputerze"
                : $"OCR {string.Join("/", r.OcrLanguages)}: {r.OcrRuns.Count} odczytów" +
                  (r.OcrRuns.LastOrDefault() is { } last ? $", ostatni ({last.Jezyk}, {last.Slow} słów): \"{last.Poczatek}\"" : string.Empty));
        }

        if (r.Errors.Count > 0)
        {
            wnioski.Add($"błędy: {r.Errors.Count} (np. {r.Errors[0]})");
        }

        return new MachineConclusion(czarny, motyw, profil, ekran, sugerowanyRoi, wnioski, NextStep(r, czarny, motyw, profil, ekran, sugerowanyRoi));
    }

    /// <summary>„czernieje po X s po połączeniu” / „nie czernieje” / „czarna od początku” / „brak klatek”.</summary>
    public static string BlackVerdict(MachineDiagnosticResult r, List<string>? wnioski = null)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (r.Frames.Count == 0)
        {
            return "brak klatek";
        }

        FrameSample first = r.Frames[0];
        if (first.Pomiar.IsBlack)
        {
            bool everBright = r.Frames.Any(f => !f.Pomiar.IsBlack);
            string verdict = everBright
                ? $"czarna od początku, rozjaśnia się po {r.Frames.First(f => !f.Pomiar.IsBlack).OdPolaczeniaSek:0} s"
                : "czarna od początku";
            wnioski?.Add($"ekran {verdict} ({r.BlackFrames} z {r.Frames.Count} klatek czarnych)");
            return verdict;
        }

        FrameSample? firstBlack = r.Frames.FirstOrDefault(f => f.Pomiar.IsBlack);
        if (firstBlack is null)
        {
            wnioski?.Add("ekran nie czernieje po połączeniu");
            return "nie czernieje";
        }

        string after = $"czernieje po {firstBlack.OdPolaczeniaSek:0} s po połączeniu";
        if (r.ReconnectTest is { CzarnaPoPowrocie: { } back })
        {
            after += back ? ", po ponownym połączeniu nadal czarna" : ", po ponownym połączeniu wraca";
        }

        wnioski?.Add($"ekran {after} ({r.BlackFrames} z {r.Frames.Count} klatek czarnych)");
        return after;
    }

    /// <summary>Motyw większości klatek nieczarnych (jasny/ciemny), albo „nieokreślony”.</summary>
    public static string ThemeVerdict(MachineDiagnosticResult r)
    {
        ArgumentNullException.ThrowIfNull(r);

        List<FrameSample> visible = r.Frames.Where(f => !f.Pomiar.IsBlack).ToList();
        if (visible.Count == 0)
        {
            return FrameMetrics.MotywNieokreslony;
        }

        int light = visible.Count(f => f.Pomiar.Motyw == FrameMetrics.MotywJasny);
        int dark = visible.Count(f => f.Pomiar.Motyw == FrameMetrics.MotywCiemny);
        return light > dark ? FrameMetrics.MotywJasny : dark > light ? FrameMetrics.MotywCiemny : FrameMetrics.MotywNieokreslony;
    }

    /// <summary>
    /// Najlepszy profil wg czerwieni: para (progi profilu, ROI profilu), która najczęściej przekraczała próg profilu
    /// — a przy remisie z najwyższym udziałem. Gdy żadna para w żadnej klatce nie przekroczyła progu → <see cref="BrakCzerwieni"/>.
    /// Profil maszyny z konfiguracji jest preferowany przy równym wyniku.
    /// </summary>
    public static string BestProfile(MachineDiagnosticResult r, IReadOnlyDictionary<string, ScreenProfile> profiles, List<string>? wnioski = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(profiles);

        if (r.Frames.Count == 0)
        {
            return "-";
        }

        var best = r.Frames
            .SelectMany(f => f.Pomiar.Czerwien)
            .GroupBy(c => (c.Profil, c.Roi))
            .Select(g => (g.Key.Profil, g.Key.Roi, Nad: g.Count(c => c.PowyzejProgu), Max: g.Max(c => c.Udzial)))
            .Where(x => x.Nad > 0)
            .OrderByDescending(x => x.Nad)
            .ThenByDescending(x => x.Max)
            .ThenByDescending(x => x.Profil == r.Profile && x.Roi == r.Profile ? 1 : 0)
            .FirstOrDefault();

        if (best.Nad == 0)
        {
            if (r.Frames.Any(f => f.Pomiar.CzerwienCalaKlatka.Values.Any(v => v > 0)))
            {
                const string outside = "czerwień poza progami znanych ROI — profil nieustalony";
                wnioski?.Add(outside);
                return outside;
            }
            wnioski?.Add($"{BrakCzerwieni} — żadna para profil×ROI nie przekroczyła progu w {r.Frames.Count} klatkach");
            return BrakCzerwieni;
        }

        string text = best.Profil == best.Roi
            ? $"{best.Profil} ({best.Nad} z {r.Frames.Count} klatek nad progiem, maks. {best.Max:P1})"
            : $"progi {best.Profil} w ROI {best.Roi} ({best.Nad} z {r.Frames.Count} klatek nad progiem, maks. {best.Max:P1})";
        wnioski?.Add($"kandydat wg czerwieni: {text}; kolor przycisku nie dowodzi alarmu — wymagane klatki normalna i z alarmem");
        return $"kandydat: {text}";
    }

    public const string BboxProfile = "opencnc";

    public static string SuggestedRedRoi(MachineDiagnosticResult r, List<string>? wnioski = null)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (r.Frames.Select(f => (f.Pomiar.Width, f.Pomiar.Height)).Distinct().Skip(1).Any())
        {
            wnioski?.Add("zmiana rozdzielczości — ROI trzeba zmierzyć osobno dla każdego rozmiaru; brak wspólnej sugestii");
            return "-";
        }

        List<RectInt> boxes = r.Frames
            .Select(f => f.Pomiar.CzerwienBbox.TryGetValue(BboxProfile, out RectInt? b) ? b
                : f.Pomiar.CzerwienBbox.TryGetValue(r.Profile, out RectInt? own) ? own : null)
            .Where(b => b is { IsEmpty: false })
            .Select(b => b!.Value)
            .ToList();
        if (boxes.Count == 0)
        {
            return "-";
        }

        static int Median(IEnumerable<int> values)
        {
            int[] sorted = values.OrderBy(v => v).ToArray();
            return sorted[sorted.Length / 2];
        }

        int x = Math.Max(0, Median(boxes.Select(b => b.X)) - 8);
        int y = Math.Max(0, Median(boxes.Select(b => b.Y)) - 8);
        int right = Math.Min(r.Frames[0].Pomiar.Width, Median(boxes.Select(b => b.Right)) + 8);
        int bottom = Math.Min(r.Frames[0].Pomiar.Height, Median(boxes.Select(b => b.Bottom)) + 8);
        string text = $"{x},{y},{right - x},{bottom - y} (z {boxes.Count} klatek z czerwienią; bbox wg progów {BboxProfile})";
        wnioski?.Add($"czerwone piksele leżą w okolicy {text} — kandydat na statusBarRoi, sprawdź na klatkach");
        return text;
    }

    private static string NextStep(MachineDiagnosticResult r, string czarny, string motyw, string profil, string ekran, string sugerowanyRoi)
    {
        string name = r.Name;
        if (!r.EverConnected)
        {
            return r.Status switch
            {
                MachineStatus.Unreachable when r.PortScan is { } scan && scan.Any(p => p.Open) =>
                    $"{name}: port {r.Port} milczy, ale otwarte są {string.Join(", ", scan.Where(p => p.Open).Select(p => p.Port))} — sam otwarty port nie dowodzi VNC; zachowaj ZIP do analizy, bez zmian HMI i dalszych prób na hali.",
                MachineStatus.Unreachable when r.TcpProbes.Any(p => p.Open) =>
                    $"{name}: port otwierał się tylko czasem ({r.TcpProbes.Count(p => p.Open)} z {r.TcpProbes.Count} prób) — przyczyna nieustalona; zachowaj ZIP, bez restartów i dalszych prób na hali.",
                MachineStatus.Unreachable =>
                    $"{name}: nie odpowiada — zachowaj ZIP i obejrzyj zastany stan maszyny; nie włączaj jej ani nie zmieniaj sieci/HMI na potrzeby diagnozy.",
                MachineStatus.PasswordRequired =>
                    $"{name}: brak hasła tej maszyny — pozostaw ją pominiętą; podaj hasło dopiero przy kolejnym uzgodnionym przebiegu (runner pyta tylko o 580).",
                MachineStatus.WrongPassword or MachineStatus.WrongCredentials =>
                    $"{name}: hasło/poświadczenia odrzucone — nie ponawiaj na hali; zapisz komunikat i potwierdź poświadczenia z właścicielem.",
                MachineStatus.MissingUsername =>
                    $"{name}: serwer chce nazwy użytkownika — zachowaj wynik i potwierdź poświadczenia z właścicielem; nie zmieniaj zabezpieczeń serwera.",
                MachineStatus.UnsupportedSecurity =>
                    $"{name}: typ zabezpieczeń nieobsługiwany ({r.Reason}) — prześlij dziennik; na maszynie nic nie zmieniaj.",
                _ => $"{name}: {r.Reason ?? "nie połączono"} — prześlij dziennik.",
            };
        }

        if (r.Frames.Count == 0)
        {
            return $"{name}: połączono, ale nie odebrano żadnej klatki — brak danych do oceny czerni, profilu i ROI. Zachowaj ZIP; nie zmieniaj maszyny.";
        }

        var parts = new List<string> { $"{name}: ekran {ekran} {motyw}" };
        if (czarny.StartsWith("czarna od początku", StringComparison.Ordinal))
        {
            parts.Add("VNC przysyła czarny obraz od początku — przyczyna nieustalona; zrób zdjęcie HMI bez dotykania i zachowaj ZIP");
        }
        else if (czarny.StartsWith("czernieje", StringComparison.Ordinal))
        {
            parts.Add($"obraz VNC {czarny}; nie dowodzi to zgaszenia fizycznego HMI ani przyczyny — zapisz godzinę, zrób zdjęcie bez dotykania i zakończ próbę");
        }
        else
        {
            parts.Add("ekran nie czernieje");
        }

        int unplanned = r.Disconnects.Count(d => !d.Przyczyna.StartsWith("planowe", StringComparison.Ordinal));
        if (unplanned > 0)
        {
            parts.Add($"zerwań {unplanned} — sprawdź sieć/serwer VNC (dziennik)");
        }

        if (r.SharedTest is { DrugiPolaczony: true, PierwszyPrzezyl: false })
        {
            parts.Add("serwer wyrzuca poprzedniego klienta — nie uruchamiaj drugiego obserwatora; samo polling nie usuwa konfliktu");
        }

        if (profil == DiagnozaConclusions.BrakCzerwieni)
        {
            parts.Add($"brak czerwieni w {r.Frames.Count} klatkach — profil niepotwierdzony; potrzebna klatka prawdziwego alarmu z istniejących danych, bez wywoływania błędu" +
                (sugerowanyRoi == "-" ? string.Empty : $"; czerwone piksele poniżej progów w okolicy {sugerowanyRoi}"));
        }
        else
        {
            parts.Add($"czerwień: {profil}; profil i ROI wymagają porównania z klatką normalną i z prawdziwym alarmem, bez zmiany konfiguracji na hali" + (sugerowanyRoi == "-" ? string.Empty : $"; kandydat ROI {sugerowanyRoi}"));
        }

        if (r.OcrLanguages.Count == 0)
        {
            parts.Add("OCR niedostępny — obrazy i kolor zapisano; analizę tekstu wykonaj później na komputerze z OCR");
        }
        else if (r.Anchors is { } a && a.ProgressRoi is null)
        {
            parts.Add("OCR nie znalazł etykiety postępu — pole postępu do zmierzenia ręcznie na klatce");
        }

        return string.Join("; ", parts) + ".";
    }
}
