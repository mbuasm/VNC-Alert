namespace MaszynyAlert.Core.Vision;

public sealed record ProgressBarReading(bool Visible, int FilledColumns, int TrackWidth, int? Percent, string Description);
