using System.Diagnostics;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Probe.Commands;

/// <summary>
/// <c>probe.exe info</c> — łączy się po kolei ze wszystkimi maszynami z listy (także wyłączonymi w
/// konfiguracji — te są tylko oznaczone, bez próby połączenia, chyba że operator wskazał je przez
/// <c>--machines</c> albo <c>--all</c>), sprawdza wersję RFB, typy zabezpieczeń, format piksela i rozmiar
/// ekranu, zapisuje pełny zrzut i mierzy udział czerwieni. Kod wyjścia wg <see cref="ProbeExitCodes"/>.
/// </summary>
public sealed class InfoCommand : ICommand
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct) =>
        RunCoreAsync(args, global, report, session, ct);

    /// <summary>Logika komendy, wyodrębniona żeby <c>probe.exe auto</c> (Commands/AutoCommand.cs) mogła wykonać KROK 1 bez duplikowania kodu.</summary>
    public static async Task<int> RunCoreAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        MachineListResult listResult = MachineList.Load(global);
        report.Line($"Lista maszyn: {listResult.SourcePath}");
        report.Line($"Maszyn w konfiguracji: {listResult.Config.Machines.Count}" +
            (listResult.FilterActive ? $", wybranych do tego przebiegu: {listResult.Selected.Count} ({string.Join(", ", listResult.Selected.Select(m => m.Id))})" : string.Empty));
        report.Line();

        var summary = new SimpleTable("nazwa", "status", "RFB", "auth", "format", "rozmiar");
        int connected = 0;
        int skipped = 0;

        IReadOnlyList<MachineConfig> toVisit = listResult.FilterActive ? listResult.Selected : listResult.Config.Machines;

        foreach (MachineConfig machine in toVisit)
        {
            ct.ThrowIfCancellationRequested();
            report.Line($"--- {machine.Name} ({machine.Host}:{machine.Port}), profil \"{machine.Profile}\" ---");
            MachineSummary row = session.Summary.Machine(machine.Id);
            row.Name = machine.Name;

            if (!machine.Enabled && !listResult.FilterActive)
            {
                report.Line("wyłączona w konfiguracji (enabled: false) — pomijam, nie łączę się (żeby ją objąć, użyj --machines <id> albo --all).");
                summary.AddRow(machine.Name, MachineStatus.Disabled, "-", "-", "-", "-");
                row.Status = MachineStatus.Disabled;
                row.Reason = "enabled: false w konfiguracji";
                skipped++;
                report.Line();
                continue;
            }

            if (!listResult.Config.Profiles.TryGetValue(machine.Profile, out ScreenProfile? profile))
            {
                report.Line($"pominięto: profil \"{machine.Profile}\" nie istnieje w konfiguracji.");
                summary.AddRow(machine.Name, MachineStatus.ConfigError, "-", "-", "-", "-");
                row.Status = MachineStatus.ConfigError;
                row.Reason = $"profil \"{machine.Profile}\" nie istnieje w konfiguracji";
                skipped++;
                report.Line();
                continue;
            }

            ConnectOutcome outcome = await session.Connections
                .ConnectAsync(machine.Id, machine.Name, machine.Host, machine.Port, ConnectTimeout, ct, machine.Username)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                report.Line($"pominięto: {outcome.SkipReason}");
                summary.AddRow(machine.Name, SkipStatus(outcome.SkipReason!), "-", "-", "-", "-");
                row.ApplySkip(outcome.SkipReason!);
                skipped++;
                report.Line();
                continue;
            }

            await using RfbClient client = outcome.Client!;
            RfbServerInfo info = outcome.Info!;
            connected++;
            row.ApplyServerInfo(info, DateTimeOffset.Now);

            report.Line($"wersja RFB: {info.ProtocolVersion} (nagłówek serwera: {info.RawVersionHeader ?? "-"})");
            report.Line($"typy zabezpieczeń oferowane: {RfbFormat.FormatOfferedTypes(info.OfferedSecurityTypes)}");
            report.Line($"typ zabezpieczeń użyty: {info.UsedSecurityType} ({(int)info.UsedSecurityType})");
            if (info.DescribeCredentialSubtype() is { } subtype)
            {
                // Przy RSA-AES serwer sam mówi, czego chce.
                report.Line($"wariant poświadczeń RSA-AES: {subtype}");
            }

            if (info.ServerKeySha256 is { } keySha)
            {
                report.Line($"odcisk klucza RSA serwera (sha256, do ewentualnego przypięcia w machines[].rsaAesKeySha256): {keySha}");
            }

            report.Line($"rozmiar ekranu: {info.Width}x{info.Height}");
            report.Line($"format piksela: {info.PixelFormat.Describe()}");
            report.Line($"nazwa pulpitu: \"{info.DesktopName}\"");

            summary.AddRow(
                machine.Name,
                MachineStatus.Connected,
                info.ProtocolVersion,
                info.UsedSecurityType.ToString(),
                info.PixelFormat.Describe(),
                $"{info.Width}x{info.Height}");

            var sw = Stopwatch.StartNew();
            RgbaImage full;
            try
            {
                var fullRect = new RectInt(0, 0, info.Width, info.Height);
                FramebufferUpdate update = await client.UpdateAsync(fullRect, incremental: false, ct).ConfigureAwait(false);
                row.NoteEncodings(update.EncodingsUsed);
                full = client.Framebuffer.Crop(fullRect.Clamp(client.Framebuffer.Width, client.Framebuffer.Height));
            }
            catch (Exception ex) when (ex is RfbException or RfbScreenResizedException or ArgumentException)
            {
                sw.Stop();
                report.Line($"nie udało się pobrać pełnej klatki ({ex.Message}) — maszyna odpowiedziała na handshake, ale obraz nie przyszedł; przechodzę do następnej.");
                row.AddDisconnect(DateTimeOffset.Now, $"pierwsza klatka: {ex.Message}");
                report.Line();
                continue;
            }

            sw.Stop();
            long fullMs = sw.ElapsedMilliseconds;
            row.Frames++;
            row.FirstFrameAfterMs ??= fullMs;

            string pngPath = Path.Combine(global.OutDir, $"{machine.Id}_full.png");
            Directory.CreateDirectory(global.OutDir);
            PngCodec.EncodeToFile(full, pngPath);
            report.Line($"zrzut pełnego ekranu: {Path.GetFullPath(pngPath)} (pobranie klatki: {Fmt.Ms(fullMs)})");

            RectInt roi = profile.StatusBarRoi.Clamp(info.Width, info.Height);
            if (RoiWarning(profile.StatusBarRoi, roi, info.Width, info.Height) is { } warning)
            {
                report.Line(warning);
            }

            if (!roi.IsEmpty)
            {
                int idleTimeoutMs = listResult.Config.Timing.IdleUpdateTimeoutMs;
                var idleTimeout = TimeSpan.FromMilliseconds(idleTimeoutMs);
                sw.Restart();
                try
                {
                    RgbaImage? fresh = await client.TryCaptureAsync(roi, incremental: true, idleTimeout, ct).ConfigureAwait(false);
                    sw.Stop();
                    report.Line(fresh is not null
                        ? $"pobranie przyrostowej aktualizacji ROI profilu ({profile.Id}): {Fmt.Ms(sw.ElapsedMilliseconds)}"
                        : $"przyrostowa aktualizacja ROI profilu ({profile.Id}): brak zmian na ekranie w ciągu " +
                          $"{Fmt.Ms(idleTimeoutMs)} — to normalne na nieruchomym ekranie " +
                          "(obserwator traktuje to jako \"bez zmian\", nie jako utratę łączności).");
                }
                catch (Exception ex) when (ex is RfbException or RfbScreenResizedException or ArgumentException)
                {
                    report.Line($"nie udało się pobrać przyrostowej aktualizacji ROI profilu: {ex.Message}");
                    row.LastError = ex.Message;
                }
            }

            RedDetectionResult roiRed = new RedTextDetector(profile).AnalyzeFullFrame(full);
            double fullRed = ImageOps.RedFraction(full, profile.Red);
            report.Line($"udział czerwieni w ROI profilu: {Fmt.Pct2(roiRed.RedFraction)} (próg profilu: {Fmt.Pct2(profile.MinRedFraction)})");
            report.Line($"udział czerwieni w całej klatce: {Fmt.Pct2(fullRed)}");

            report.Line();
        }

        report.Line("Podsumowanie:");
        foreach (string line in summary.Render())
        {
            report.Line(line);
        }

        report.Line();
        report.Line($"Połączono: {connected}, pominięto: {skipped}, razem: {toVisit.Count}.");
        if (!session.IsAutoRun)
        {
            report.Line(
                "Co dalej: przejrzyj powyższą tabelę i zrzuty w out\\*_full.png, a potem wpisz \"probe.exe ocr\" " +
                "(albo od razu \"probe.exe auto\", które robi wszystkie kroki samo). Opcjonalnie " +
                "\"probe.exe shared-test <adres:port>\" dla jednej maszyny Aidite (test opcjonalny — może zerwać czyjś podgląd).");
        }

        SummaryPrinter.PrintAndSave(session.Summary, report, global.OutDir);

        return ProbeExitCodes.FromSummary(session.Summary);
    }

    internal static string? RoiWarning(RectInt configured, RectInt clamped, int screenWidth, int screenHeight)
    {
        if (clamped == configured)
        {
            return null;
        }

        string what = clamped.IsEmpty
            ? "leży CAŁKOWICIE poza ekranem tej maszyny"
            : $"wystaje poza ekran tej maszyny (po przycięciu zostaje {clamped.Width}x{clamped.Height} px)";

        return $"UWAGA: obszar paska statusu profilu ({configured.X},{configured.Y} {configured.Width}x{configured.Height}) " +
               $"{what} — ekran ma {screenWidth}x{screenHeight}. Udział czerwieni niżej jest policzony z tego, co zostało, " +
               "więc NIE opisuje alarmu tej maszyny; popraw \"statusBarRoi\" w profilu.";
    }

    internal static string SkipStatus(string reason) => reason switch
    {
        _ when reason.Contains("PUSTĄ nazwą użytkownika", StringComparison.Ordinal) => MachineStatus.MissingUsername,
        _ when reason.Contains("wymaga hasła VNC", StringComparison.Ordinal) => MachineStatus.PasswordRequired,
        _ when reason.Contains("hasło VNC", StringComparison.Ordinal) => MachineStatus.WrongPassword,
        _ when reason.Contains("odrzucił poświadczenia RSA-AES", StringComparison.Ordinal) => MachineStatus.WrongCredentials,
        _ when reason.Contains("oferuje tylko typy zabezpieczeń", StringComparison.Ordinal) => MachineStatus.UnsupportedSecurity,
        _ when reason.Contains("nie odpowiada", StringComparison.Ordinal) => MachineStatus.Unreachable,
        _ => MachineStatus.Skipped,
    };
}
