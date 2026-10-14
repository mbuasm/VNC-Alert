namespace MaszynyAlert.Core.Rfb;

public static class QtEmbeddedVncServer
{
    public const string DesktopName = "Qt for Embedded Linux VNC Server";

    public static bool IsQtEmbedded(string? desktopName)
    {
        if (string.IsNullOrWhiteSpace(desktopName))
        {
            return false;
        }

        string normalized = string.Join(' ', desktopName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Contains(DesktopName, StringComparison.OrdinalIgnoreCase);
    }

    public static readonly IReadOnlySet<string> QtProfiles =
        new HashSet<string>(["aidite5x", "premile", "cameo1", "aidite-light2"], StringComparer.OrdinalIgnoreCase);

    public static bool IsQtProfile(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && QtProfiles.Contains(profileId.Trim());
}
