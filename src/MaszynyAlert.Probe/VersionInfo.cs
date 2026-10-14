using System.Reflection;

namespace MaszynyAlert.Probe;

/// <summary>Wersja programu pokazywana na starcie każdej komendy (z atrybutu InformationalVersion; schemat 0.3.&lt;numer przebiegu CI&gt; z Directory.Build.props, lokalnie 0.3.0-dev).</summary>
internal static class VersionInfo
{
    public static string InformationalVersion { get; } = Resolve();

    private static string Resolve()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Deterministyczne buildy czasem dopisują "+<sha>" po numerze wersji — nieprzydatne dla operatora.
            int plusIndex = info.IndexOf('+');
            return plusIndex > 0 ? info[..plusIndex] : info;
        }

        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
