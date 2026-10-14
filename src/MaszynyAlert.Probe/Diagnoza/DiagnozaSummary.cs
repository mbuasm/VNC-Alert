using System.Text.Encodings.Web;
using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>
/// Podsumowanie całego przebiegu <c>diagnoza</c>:<c>podsumowanie.json</c> (maszynowo) i
/// <c>podsumowanie.txt</c> (tabela po polsku + wnioski + „co dalej” per maszyna). Czysta logika — zapis robi
/// <see cref="DiagnozaCommand"/>; hasła nigdy tu nie trafiają (wynik nie zna hasła, tylko „podano/nie podano”).
/// </summary>
public static class DiagnozaSummary
{
    public static string RenderJson(IReadOnlyList<MachineDiagnosticResult> results, IReadOnlyDictionary<string, ScreenProfile> profiles, DateTimeOffset started, DateTimeOffset finished, DiagnozaSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(schedule);

        var machines = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (MachineDiagnosticResult r in results)
        {
            MachineConclusion c = DiagnozaConclusions.Build(r, profiles);
            machines[r.Id] = new
            {
                nazwa = r.Name,
                host = r.Host,
                port = r.Port,
                profilKonfiguracji = r.Profile,
                status = r.Status,
                powod = r.Reason,
                polaczonoO = r.ConnectedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                polaczen = r.Connections,
                pierwszaKlatkaPo = r.FirstFrameMs,
                tcp = new
                {
                    prob = r.TcpProbes.Count,
                    otwartych = r.TcpProbes.Count(p => p.Open),
                    pierwsza = r.TcpProbes.FirstOrDefault()?.Describe(),
                    skanPortow = r.PortScan?.Where(p => p.Open).Select(p => p.Port).ToList(),
                    skanow = r.PortScans,
                },
                rfb = r.Server is { } s ? new
                {
                    wersja = s.ProtocolVersion,
                    wersjaRfbNaglowek = s.RawVersionHeader,
                    typyZabezpieczen = new { oferowane = RfbFormat.FormatOfferedTypes(s.OfferedSecurityTypes), wybrany = s.UsedSecurityType.ToString() },
                    wariantRsaAes = s.DescribeCredentialSubtype(),
                    odciskKluczaRsaSha256 = s.ServerKeySha256,
                    serwerNazwa = s.DesktopName,
                    ekran = $"{s.Width}x{s.Height}",
                    piksel = s.PixelFormat.Describe(),
                    wymagaHasla = r.RequiresPassword,
                    kodowania = r.Encodings,
                } : null,
                wymagaHasla = r.RequiresPassword,
                klatek = r.Frames.Count,
                czarnychKlatek = r.BlackFrames,
                zmianObrazu = r.HashChanges,
                odpowiedzMs = r.Frames.Count == 0 ? null : new { min = r.Frames.Min(f => f.OdpowiedzMs), max = r.Frames.Max(f => f.OdpowiedzMs), srednia = Math.Round(r.Frames.Average(f => f.OdpowiedzMs)) },
                testCzerni = r.BlackTests.Select(b => new { planSek = b.PlanSek, faktSek = b.FaktSek, czarna = b.Czarna, klasa = b.Klasa }),
                czarnyEkran = c.CzarnyEkran,
                motyw = c.Motyw,
                najlepszyProfil = c.NajlepszyProfil,
                sugerowanyRoiCzerwieni = c.SugerowanyRoi,
                klatekZCzerwienia = r.Frames.Count(f => f.Pomiar.JakakolwiekCzerwien),
                rozlaczenia = r.Disconnects.Select(d => new { czas = d.Czas.ToString("yyyy-MM-dd HH:mm:ss"), przyczyna = d.Przyczyna }),
                ponownePolaczenie = r.ReconnectTest,
                sharedTest = r.SharedTest,
                czestoscAktualizacji = r.UpdateRate,
                ocr = new { jezyki = r.OcrLanguages, odczytow = r.OcrRuns.Count, ostatni = r.OcrRuns.LastOrDefault() },
                kotwice = r.Anchors is { } a ? new { poleProstepu = ScreenAnchors.DescribeProgressField(a), nazwaPliku = ScreenAnchors.DescribeFileNameField(a) } : null,
                bledy = r.Errors,
                wnioski = c.Wnioski,
                coDalej = c.CoDalej,
            };
        }

        var doc = new
        {
            probe = VersionInfo.InformationalVersion,
            start = started.ToString("yyyy-MM-dd HH:mm:ss"),
            koniec = finished.ToString("yyyy-MM-dd HH:mm:ss"),
            przebiegSek = schedule.TotalSeconds,
            klatkaCoSek = schedule.FrameIntervalSeconds,
            kodWyjscia = ExitCode(results),
            maszyny = machines,
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Tabela + wnioski + „co dalej” — to, co operator widzi na końcu i co trafia do podsumowanie.txt.</summary>
    public static IReadOnlyList<string> RenderText(IReadOnlyList<MachineDiagnosticResult> results, IReadOnlyDictionary<string, ScreenProfile> profiles, DateTimeOffset started, DateTimeOffset finished)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(profiles);

        var lines = new List<string>
        {
            $"Diagnoza hali — probe {VersionInfo.InformationalVersion}, {started:yyyy-MM-dd HH:mm:ss} – {finished:HH:mm:ss}, maszyn: {results.Count}, kod wyjścia: {ExitCode(results)}",
            string.Empty,
        };

        var table = new SimpleTable("maszyna", "status", "RFB", "zabezpieczenia", "ekran", "piksel", "motyw", "czarny ekran?", "klatek", "zmian", "najlepszy profil (czerwień)", "OCR");
        lines.Add("WYNIK DLA WŁAŚCICIELA (kod połączeń nie zatwierdza profilu):");
        foreach (MachineDiagnosticResult r in results)
        {
            lines.Add($"  {r.Name}: " + (r.Status == MachineStatus.Skipped ? $"POMINIĘTO — {r.Reason}"
                : r.Frames.Count == 0 ? $"BRAK POMIARU — {r.Status}, 0 klatek; zachowaj ZIP"
                : $"MATERIAŁ ZEBRANY — {r.Frames.Count} klatek; profil i ROI do analizy, nie do automatycznego zastosowania"));
        }
        lines.Add("Nie zmieniaj ustawień maszyn. Zabierz ZIP i zdjęcia; 520 tylko z eksportu działającego VNC Alert.");
        lines.Add(string.Empty);
        var conclusions = new List<(MachineDiagnosticResult R, MachineConclusion C)>();
        foreach (MachineDiagnosticResult r in results)
        {
            MachineConclusion c = DiagnozaConclusions.Build(r, profiles);
            conclusions.Add((r, c));
            table.AddRow(
                r.Name,
                r.Status,
                r.Server?.ProtocolVersion ?? "-",
                r.Server is { } s ? $"{s.UsedSecurityType} z [{string.Join(",", s.OfferedSecurityTypes)}]" : "-",
                c.Ekran,
                r.Server?.PixelFormat.Describe() ?? "-",
                c.Motyw,
                c.CzarnyEkran,
                r.Frames.Count.ToString(),
                r.HashChanges.ToString(),
                c.NajlepszyProfil,
                r.OcrLanguages.Count == 0 ? "brak" : $"{string.Join("/", r.OcrLanguages)} ({r.OcrRuns.Count})");
        }

        lines.AddRange(table.Render());
        lines.Add(string.Empty);
        lines.Add("Wnioski:");
        foreach ((MachineDiagnosticResult r, MachineConclusion c) in conclusions)
        {
            lines.Add($"  {r.Name} ({r.Host}:{r.Port}, profil {r.Profile}):");
            foreach (string w in c.Wnioski)
            {
                lines.Add($"    - {w}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Co dalej:");
        foreach ((_, MachineConclusion c) in conclusions)
        {
            lines.Add($"  - {c.CoDalej}");
        }

        return lines;
    }

    /// <summary>Kod wyjścia wg <see cref="ProbeExitCodes"/> z wyników maszyn (0 wszystkie połączone, 3 część, 4 żadna).</summary>
    public static int ExitCode(IReadOnlyList<MachineDiagnosticResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            return ProbeExitCodes.NoneConnected;
        }

        int connected = results.Count(r => r.EverConnected);
        return connected == results.Count ? ProbeExitCodes.AllConnected : connected == 0 ? ProbeExitCodes.NoneConnected : ProbeExitCodes.SomeConnected;
    }
}
