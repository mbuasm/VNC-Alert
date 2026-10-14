using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Core.Machines;

public sealed class VncScreenSource : IScreenSource
{
    private readonly MachineConfig _machine;
    private readonly string? _password;
    private readonly TimingConfig _timing;
    private readonly ILogger<VncScreenSource> _logger;
    private readonly ILogger<RfbClient> _rfbLogger;

    /// <summary>Klient trzymany między wywołaniami TYLKO w trybie Persistent; zawsze null w trybie Polling.</summary>
    private RfbClient? _persistentClient;

    public VncScreenSource(MachineConfig m, string? password, TimingConfig t, ILoggerFactory? lf = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(t);

        _machine = m;
        _password = password;
        _timing = t;
        _logger = lf?.CreateLogger<VncScreenSource>() ?? NullLogger<VncScreenSource>.Instance;
        _rfbLogger = lf?.CreateLogger<RfbClient>() ?? NullLogger<RfbClient>.Instance;
    }

    /// <inheritdoc />
    public string MachineId => _machine.Id;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public ScreenSourceInfo? Info { get; private set; }

    /// <inheritdoc />
    public async Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct)
    {
        if (_machine.Mode == ConnectionMode.Polling)
        {
            (RfbServerInfo serverInfo, RfbClient client) = await OpenAndConnectAsync(ct).ConfigureAwait(false);
            await SafeDisposeAsync(client).ConfigureAwait(false);
            return AcceptSuccess(serverInfo);
        }

        await DisposePersistentClientAsync().ConfigureAwait(false);

        try
        {
            (RfbServerInfo serverInfo, RfbClient client) = await OpenAndConnectAsync(ct).ConfigureAwait(false);
            _persistentClient = client;
            return AcceptSuccess(serverInfo);
        }
        catch
        {
            IsConnected = false;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct)
    {
        if (_machine.Mode == ConnectionMode.Polling)
        {
            return await CapturePollingAsync(roi, incremental: false, ct).ConfigureAwait(false);
        }

        return await CapturePersistentAsync(roi, incremental, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// W trybie Polling zawsze łączy się i pobiera normalnie (żądanie nieprzyrostowe zawsze dostaje
    /// natychmiastową odpowiedź, więc limit bezczynności nie ma tu znaczenia). W trybie Persistent
    /// deleguje do <see cref="RfbClient.TryCaptureAsync"/>, który zwraca <see langword="null"/>, gdy
    /// serwer (typowo WinCE/OpenCnc) nie odpowie na żądanie przyrostowe w oknie <paramref name="idleTimeout"/>.
    /// </remarks>
    public async Task<RgbaImage?> TryCaptureAsync(RectInt roi, bool incremental, TimeSpan idleTimeout, CancellationToken ct)
    {
        if (_machine.Mode == ConnectionMode.Polling)
        {
            return await CapturePollingAsync(roi, incremental: false, ct).ConfigureAwait(false);
        }

        if (_persistentClient is null)
        {
            throw new InvalidOperationException(
                $"Brak połączenia VNC z maszyną '{_machine.Id}' — wywołaj najpierw ConnectAsync.");
        }

        try
        {
            RgbaImage? image = await _persistentClient.TryCaptureAsync(roi, incremental, idleTimeout, ct).ConfigureAwait(false);
            IsConnected = true;
            return image;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or RfbScreenResizedException))
        {
            IsConnected = false;
            await DisposePersistentClientAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<RgbaImage> CaptureFullAsync(CancellationToken ct)
    {
        if (_machine.Mode == ConnectionMode.Polling)
        {
            RectInt fullRect = new(0, 0, int.MaxValue / 2, int.MaxValue / 2);
            return await CapturePollingAsync(fullRect, incremental: false, ct).ConfigureAwait(false);
        }

        RfbClient? client = _persistentClient;
        if (client is null || client.ServerInfo is null)
        {
            throw new InvalidOperationException(
                $"Brak połączenia VNC z maszyną '{_machine.Id}' — wywołaj najpierw ConnectAsync.");
        }

        RfbServerInfo info = client.ServerInfo;
        var full = new RectInt(0, 0, info.Width, info.Height);
        return await CapturePersistentAsync(full, incremental: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposePersistentClientAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<RgbaImage> CapturePersistentAsync(RectInt roi, bool incremental, CancellationToken ct)
    {
        if (_persistentClient is null)
        {
            throw new InvalidOperationException(
                $"Brak połączenia VNC z maszyną '{_machine.Id}' — wywołaj najpierw ConnectAsync.");
        }

        try
        {
            RgbaImage image = await _persistentClient.CaptureAsync(roi, incremental, ct).ConfigureAwait(false);
            IsConnected = true;
            return image;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or RfbScreenResizedException))
        {
            IsConnected = false;
            await DisposePersistentClientAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<RgbaImage> CapturePollingAsync(RectInt roi, bool incremental, CancellationToken ct)
    {
        RfbClient? client = null;
        try
        {
            (RfbServerInfo serverInfo, client) = await OpenAndConnectAsync(ct).ConfigureAwait(false);
            RgbaImage image = await client.CaptureAsync(roi, incremental, ct).ConfigureAwait(false);
            AcceptSuccess(serverInfo);
            return image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsConnected = false;
            throw;
        }
        finally
        {
            if (client is not null)
            {
                await SafeDisposeAsync(client).ConfigureAwait(false);
            }
        }
    }

    private async Task<(RfbServerInfo ServerInfo, RfbClient Client)> OpenAndConnectAsync(CancellationToken ct)
    {
        var options = new RfbClientOptions
        {
            Host = _machine.Host,
            Port = _machine.Port,
            Password = _password,
            Username = _machine.Username,
            ExpectedServerKeySha256 = _machine.RsaAesKeySha256,
            Shared = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(_timing.ConnectTimeoutMs),
            ReadTimeout = TimeSpan.FromMilliseconds(_timing.ReadTimeoutMs),
        };

        var client = new RfbClient(options, _rfbLogger);
        try
        {
            RfbServerInfo info = await client.ConnectAsync(ct).ConfigureAwait(false);
            return (info, client);
        }
        catch (Exception)
        {
            IsConnected = false;
            await SafeDisposeAsync(client).ConfigureAwait(false);
            throw;
        }
    }

    private ScreenSourceInfo AcceptSuccess(RfbServerInfo serverInfo)
    {
        IsConnected = true;
        Info = new ScreenSourceInfo(
            Kind: "vnc",
            ProtocolVersion: serverInfo.ProtocolVersion,
            SecurityType: serverInfo.UsedSecurityType.ToString(),
            PixelFormat: serverInfo.PixelFormat.Describe(),
            Width: serverInfo.Width,
            Height: serverInfo.Height,
            DesktopName: serverInfo.DesktopName);
        return Info;
    }

    private async Task DisposePersistentClientAsync()
    {
        if (_persistentClient is null)
        {
            return;
        }

        RfbClient client = _persistentClient;
        _persistentClient = null;
        await SafeDisposeAsync(client).ConfigureAwait(false);
    }

    private async Task SafeDisposeAsync(RfbClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Błąd (zignorowany) podczas zwalniania połączenia VNC maszyny '{MachineId}'.", _machine.Id);
        }
    }
}
