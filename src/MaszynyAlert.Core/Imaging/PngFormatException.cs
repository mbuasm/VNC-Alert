namespace MaszynyAlert.Core.Imaging;

/// <summary>Zgłaszany, gdy dekodowany plik PNG jest uszkodzony albo używa nieobsługiwanej odmiany formatu
/// (np. przeplotu/interlace, nieznanego typu koloru, złego CRC).</summary>
public sealed class PngFormatException : Exception
{
    public PngFormatException(string message) : base(message)
    {
    }

    public PngFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
