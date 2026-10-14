namespace MaszynyAlert.Core.Config;

public sealed record UiConfig
{
    public bool WindowsNotificationsEnabled { get; init; } = true;
    public double AlertOpacity { get; init; } = 0.7;

    public OverlayConfig Overlay { get; init; } = new();
}
