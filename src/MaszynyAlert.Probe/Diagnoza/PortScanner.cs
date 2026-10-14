using System.Diagnostics;
using System.Net.Sockets;

namespace MaszynyAlert.Probe.Diagnoza;

/// <summary>Wynik jednej próby połączenia TCP (bez żadnej wymiany danych) — otwarty port, czas, albo powód odmowy.</summary>
public sealed record TcpProbeResult(string Host, int Port, bool Open, long ElapsedMs, string? Error)
{
    /// <summary>Krótki opis po polsku do dziennika.</summary>
    public string Describe() => Open
        ? $"port {Port} otwarty ({ElapsedMs} ms)"
        : $"port {Port} zamknięty/nie odpowiada ({ElapsedMs} ms: {Error})";
}

public static class PortScanner
{
    /// <summary>Porty sprawdzane po nieudanej próbie na porcie z konfiguracji: 5900–5909, 5800 (VNC HTTP), 9100, 9101.</summary>
    public static readonly IReadOnlyList<int> DefaultScanPorts = [.. Enumerable.Range(5900, 10), 5800, 9100, 9101];

    /// <summary>Domyślny limit czasu jednej próby w skanie.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Próba połączenia TCP z <paramref name="host"/>:<paramref name="port"/>; nigdy nie rzuca (poza anulowaniem).</summary>
    public static async Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var sw = Stopwatch.StartNew();
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return new TcpProbeResult(host, port, true, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new TcpProbeResult(host, port, false, sw.ElapsedMilliseconds, $"limit czasu {timeout.TotalSeconds:0.#} s");
        }
        catch (SocketException ex)
        {
            sw.Stop();
            return new TcpProbeResult(host, port, false, sw.ElapsedMilliseconds, $"{ex.SocketErrorCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new TcpProbeResult(host, port, false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>Skan listy portów (po kolei, 1 s na port) — zwraca wszystkie wyniki; otwarte porty są w <see cref="TcpProbeResult.Open"/>.</summary>
    public static async Task<IReadOnlyList<TcpProbeResult>> ScanAsync(string host, IEnumerable<int> ports, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var results = new List<TcpProbeResult>();
        foreach (int port in ports)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ProbeAsync(host, port, timeout, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Jedna linia do dziennika: „otwarte: 5900, 9100” albo „żaden z 13 portów nie odpowiada”.</summary>
    public static string DescribeScan(IReadOnlyList<TcpProbeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<int> open = results.Where(r => r.Open).Select(r => r.Port).ToList();
        return open.Count == 0
            ? $"żaden z {results.Count} sprawdzonych portów nie odpowiada ({string.Join(", ", results.Select(r => r.Port))})"
            : $"otwarte porty: {string.Join(", ", open)} (sprawdzono {results.Count})";
    }
}
