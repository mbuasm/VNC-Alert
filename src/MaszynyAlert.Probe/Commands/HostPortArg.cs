using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Probe.Commands;

/// <summary>Rozbiór argumentu <c>adres:port</c> i opcji <c>--roi x,y,w,h</c>, współdzielone przez <c>capture</c> i <c>shared-test</c>.</summary>
internal static class HostPortArg
{
    public static (string Host, int Port) Parse(string arg, string commandExample)
    {
        int colon = arg.LastIndexOf(':');
        if (colon <= 0 || colon == arg.Length - 1)
        {
            throw new ProbeUsageException(
                $"Nieprawidłowy adres \"{arg}\" — podaj adres i port oddzielone dwukropkiem, np. {commandExample}.");
        }

        string host = arg[..colon];
        if (!int.TryParse(arg.AsSpan(colon + 1), out int port) || port is < 1 or > 65535)
        {
            throw new ProbeUsageException($"Nieprawidłowy port w \"{arg}\" — port musi być liczbą 1-65535.");
        }

        return (host, port);
    }

    public static RectInt ParseRoi(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out int x)
            || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int w)
            || !int.TryParse(parts[3], out int h))
        {
            throw new ProbeUsageException(
                $"Nieprawidłowa wartość --roi \"{value}\" — podaj cztery liczby oddzielone przecinkami: x,y,width,height.");
        }

        return new RectInt(x, y, w, h);
    }
}
