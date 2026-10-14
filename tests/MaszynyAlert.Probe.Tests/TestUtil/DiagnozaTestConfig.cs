using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Probe.Tests.TestUtil;

internal static class DiagnozaTestConfig
{
    public static ScreenProfile Profile(string id, RectInt roi, bool dark = false) => new()
    {
        Id = id,
        StatusBarRoi = roi,
        Red = new RedThresholds(),
        MinRedFraction = 0.005,
        DarkTheme = dark,
    };

    public static Dictionary<string, ScreenProfile> Profiles() => new(StringComparer.Ordinal)
    {
        ["opencnc"] = Profile("opencnc", new RectInt(0, 0, 32, 8)),
        ["aidite5x"] = Profile("aidite5x", new RectInt(0, 24, 32, 8)),
        ["amm580"] = Profile("amm580", new RectInt(8, 0, 16, 8), dark: true),
    };

    public static MachineConfig Machine(string id, int port, string profile = "amm580", bool enabled = true, string host = "127.0.0.1") => new()
    {
        Id = id,
        Name = id.ToUpperInvariant(),
        Host = host,
        Port = port,
        Profile = profile,
        Enabled = enabled,
    };

    public static AppConfig Config(params MachineConfig[] machines) => new()
    {
        Machines = machines.ToList(),
        Profiles = Profiles(),
    };

    /// <summary>Zapisuje config.json z podanymi maszynami do katalogu tymczasowego i zwraca ścieżkę.</summary>
    public static string WriteConfig(string dir, params MachineConfig[] machines)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, ConfigLoader.Serialize(Config(machines)));
        return path;
    }

    /// <summary>Katalog tymczasowy testu (pod katalogiem roboczym).</summary>
    public static string TempDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "probe-tests", $"{name}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Ekran jednolity o kolorze <paramref name="color"/>.</summary>
    public static RgbaImage Solid(int w, int h, Rgba color)
    {
        var img = new RgbaImage(w, h);
        img.Fill(color);
        return img;
    }

    /// <summary>Ekran jasny (biały) z czerwonym prostokątem w podanym miejscu.</summary>
    public static RgbaImage WithRed(int w, int h, RectInt red, Rgba? background = null)
    {
        RgbaImage img = Solid(w, h, background ?? new Rgba(255, 255, 255));
        for (int y = red.Y; y < red.Bottom; y++)
        {
            for (int x = red.X; x < red.Right; x++)
            {
                img.SetPixel(x, y, new Rgba(255, 0, 0));
            }
        }

        return img;
    }
}
