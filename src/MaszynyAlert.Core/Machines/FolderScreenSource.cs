using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Machines;

public sealed class FolderScreenSource : IScreenSource
{
    public FolderScreenSource(string machineId, string directory)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        ArgumentNullException.ThrowIfNull(directory);

        MachineId = machineId;
        FilePath = Path.Combine(directory, machineId + ".png");
    }

    /// <inheritdoc />
    public string MachineId { get; }

    /// <summary>Pełna ścieżka pliku testowego ekranu tej maszyny (<c>&lt;directory&gt;/&lt;machineId&gt;.png</c>).</summary>
    public string FilePath { get; }

    /// <summary>Czy plik aktualnie istnieje na dysku — sprawdzane na bieżąco (żywy stan systemu plików), bez cache'owania.</summary>
    public bool FileExists => File.Exists(FilePath);

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public ScreenSourceInfo? Info { get; private set; }

    /// <inheritdoc />
    public Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct)
    {
        RgbaImage image = DecodeOrThrow();
        return Task.FromResult(AcceptSuccess(image));
    }

    /// <inheritdoc />
    public Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct)
    {
        RgbaImage image = DecodeOrThrow();
        AcceptSuccess(image);
        return Task.FromResult(image.Crop(roi.Clamp(image.Width, image.Height)));
    }

    /// <inheritdoc />
    public Task<RgbaImage> CaptureFullAsync(CancellationToken ct)
    {
        RgbaImage image = DecodeOrThrow();
        AcceptSuccess(image);
        return Task.FromResult(image);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private ScreenSourceInfo AcceptSuccess(RgbaImage image)
    {
        IsConnected = true;
        Info = new ScreenSourceInfo(
            Kind: "plik",
            ProtocolVersion: null,
            SecurityType: null,
            PixelFormat: null,
            Width: image.Width,
            Height: image.Height,
            DesktopName: Path.GetFileName(FilePath));
        return Info;
    }

    /// <summary>
    /// Dekoduje plik od nowa; brak/blokada/uszkodzenie pliku → <see cref="ScreenSourceException"/>
    /// (PL komunikat) — <see cref="MachineMonitor"/> traktuje to jak brak pliku w bieżącym cyklu,
    /// nie jak utratę łączności z maszyną.
    /// </summary>
    private RgbaImage DecodeOrThrow()
    {
        if (!File.Exists(FilePath))
        {
            IsConnected = false;
            throw new ScreenSourceException($"Plik testowego ekranu '{FilePath}' nie istnieje.");
        }

        try
        {
            return PngCodec.DecodeFile(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsConnected = false;
            throw new ScreenSourceException(
                $"Nie można odczytać pliku testowego ekranu '{FilePath}' — jest właśnie zapisywany albo zablokowany przez inny program. Spróbuję ponownie w kolejnym cyklu.",
                ex);
        }
        catch (PngFormatException ex)
        {
            IsConnected = false;
            throw new ScreenSourceException(
                $"Plik testowego ekranu '{FilePath}' jest uszkodzony lub niekompletny (prawdopodobnie w trakcie zapisu): {ex.Message}",
                ex);
        }
    }
}
