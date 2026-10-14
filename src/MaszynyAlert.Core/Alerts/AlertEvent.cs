namespace MaszynyAlert.Core.Alerts;

/// <summary>Zdarzenie zgłoszone przez <see cref="AlertStateMachine"/> — do UI (dźwięk/okno), historii i sieci.</summary>
/// <param name="Kind">Rodzaj zdarzenia.</param>
/// <param name="Alert">Stan alertu po zdarzeniu.</param>
public sealed record AlertEvent(AlertEventKind Kind, Alert Alert)
{
    public string? Reason { get; init; }
}
