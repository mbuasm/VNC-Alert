using System.Diagnostics;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Probe.Commands;

/// <summary>
/// <c>probe.exe capture &lt;adres:port&gt; [--roi x,y,w,h]</c> — pojedynczy zrzut ekranu JEDNEJ maszyny,
/// bez łączenia się z pozostałymi (przydatne do szybkiego sprawdzenia/powtórzenia zrzutu).
/// </summary>
public sealed class CaptureCommand : ICommand
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            throw new ProbeUsageException(
                "Brakuje adresu maszyny. Użycie: probe.exe capture <adres:port> [--roi x,y,w,h] " +
                "(np. probe.exe capture 192.168.1.108:9100).");
        }

        (string host, int port) = HostPortArg.Parse(args[0], "probe.exe capture 192.168.1.108:9100");
        string? roiText = ArgsUtil.FindOption(args, "--roi");
        RectInt? roi = roiText is null ? null : HostPortArg.ParseRoi(roiText);

        report.Line($"Cel: {host}:{port}" + (roi is { } r ? $", --roi {r.X},{r.Y},{r.Width},{r.Height}" : string.Empty));
        report.Line();

        string machineId = $"{host}:{port}";
        ConnectOutcome outcome = await session.Connections.ConnectAsync(machineId, host, host, port, ConnectTimeout, ct).ConfigureAwait(false);

        if (!outcome.Success)
        {
            report.Line($"Nie udało się połączyć z {host}:{port}: {outcome.SkipReason}");
            return 1;
        }

        await using RfbClient client = outcome.Client!;
        RfbServerInfo info = outcome.Info!;

        report.Line($"Połączono: RFB {info.ProtocolVersion}, auth {info.UsedSecurityType}, {info.Width}x{info.Height}, {info.PixelFormat.Describe()}, \"{info.DesktopName}\"");

        var sw = Stopwatch.StartNew();
        var fullRect = new RectInt(0, 0, info.Width, info.Height);
        var full = await client.CaptureAsync(fullRect, incremental: false, ct).ConfigureAwait(false);
        sw.Stop();

        Directory.CreateDirectory(global.OutDir);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        string baseName = $"capture_{host}_{port}_{stamp}";
        string fullPath = Path.Combine(global.OutDir, $"{baseName}.png");
        PngCodec.EncodeToFile(full, fullPath);
        report.Line($"Zapisano pełny zrzut: {Path.GetFullPath(fullPath)} (pobranie klatki: {Fmt.Ms(sw.ElapsedMilliseconds)})");

        if (roi is { } requestedRoi)
        {
            RectInt clamped = requestedRoi.Clamp(info.Width, info.Height);
            if (clamped.IsEmpty)
            {
                report.Line($"--roi {requestedRoi.X},{requestedRoi.Y},{requestedRoi.Width},{requestedRoi.Height} leży całkowicie poza ekranem ({info.Width}x{info.Height}) — pomijam wycinek i OCR-przygotowanie.");
            }
            else
            {
                var roiImage = full.Crop(clamped);
                string roiPath = Path.Combine(global.OutDir, $"{baseName}_roi.png");
                PngCodec.EncodeToFile(roiImage, roiPath);
                report.Line($"Zapisano wycinek ROI: {Path.GetFullPath(roiPath)}");

                var ocrPrepared = ImageOps.PrepareForOcr(roiImage, new RedThresholds());
                string ocrPath = Path.Combine(global.OutDir, $"{baseName}_ocr.png");
                PngCodec.EncodeToFile(ocrPrepared, ocrPath);
                report.Line($"Zapisano wycinek przygotowany do OCR (domyślne progi czerwieni): {Path.GetFullPath(ocrPath)}");
            }
        }

        return 0;
    }
}
