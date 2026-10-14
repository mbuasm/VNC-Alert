using System.Media;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaszynyAlert.Windows;

[SupportedOSPlatform("windows")]
public sealed class WavSoundPlayer : IDisposable
{
    private readonly string _wavPath;
    private readonly ILogger _logger;
    private readonly SoundPlayer? _player;
    private bool _disposed;

    public WavSoundPlayer(string wavPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(wavPath);
        _wavPath = wavPath;
        _logger = logger ?? NullLogger.Instance;

        if (File.Exists(wavPath))
        {
            _player = new SoundPlayer(wavPath);
        }
        else
        {
            _logger.LogWarning("Plik dźwięku alarmu \"{Path}\" nie istnieje — będzie użyty zastępczy dźwięk systemowy.", wavPath);
        }
    }

    /// <summary>Czy dźwięk jest aktualnie odtwarzany w pętli (śledzone przez tę klasę — SoundPlayer sam tego nie raportuje).</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Odtwarza dźwięk w pętli, aż do <see cref="Stop"/>.</summary>
    public void PlayLooping()
    {
        if (_disposed || IsPlaying)
        {
            return;
        }

        if (_player is null)
        {
            PlayOnce();
            return;
        }

        try
        {
            _player.PlayLooping();
            IsPlaying = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Nie udało się odtworzyć dźwięku alarmu w pętli z pliku \"{Path}\".", _wavPath);
        }
    }

    /// <summary>Odtwarza dźwięk jednokrotnie (np. "Test alarmu"); brak pliku → dźwięk systemowy (SystemSounds.Exclamation).</summary>
    public void PlayOnce()
    {
        if (_disposed)
        {
            return;
        }

        if (_player is null)
        {
            SystemSounds.Exclamation.Play();
            return;
        }

        try
        {
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Nie udało się odtworzyć dźwięku alarmu z pliku \"{Path}\" — odtwarzam dźwięk systemowy.", _wavPath);
            SystemSounds.Exclamation.Play();
        }
    }

    /// <summary>Zatrzymuje odtwarzanie w pętli.</summary>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _player?.Stop();
        IsPlaying = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _player?.Stop();
        }
        catch (InvalidOperationException)
        {
            // ignorujemy — zamykamy i tak
        }

        _player?.Dispose();
    }
}
