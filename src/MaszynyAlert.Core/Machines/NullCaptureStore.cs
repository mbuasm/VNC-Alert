using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Machines;

/// <summary>Implementacja <see cref="ICaptureStore"/>, która nic nie zapisuje — do testów i sytuacji, gdy zrzuty są wyłączone.</summary>
public sealed class NullCaptureStore : ICaptureStore
{
    /// <inheritdoc />
    public string? Save(string machineId, RgbaImage image, DateTimeOffset at, string tag) => null;

    /// <inheritdoc />
    public void Prune(TimeSpan retention)
    {
    }
}
