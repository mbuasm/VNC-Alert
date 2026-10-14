namespace MaszynyAlert.Core.Config;

/// <summary>
/// Sposób, w jaki HMI maszyny pokazuje postęp bieżącej pracy — patrz <see cref="IdleWatchProfile.Mode"/>.
/// </summary>
public enum ProgressMode
{
    Number,

    /// <summary>
    /// Postęp jest PASKIEM wypełnianym kolorem, bez OCR (v2 — patrz <see cref="Vision.ProgressBarDetector"/>).
    /// Zostawiony jako alternatywa dla ewentualnego innego układu HMI, w którym pasek postępu naprawdę
    /// istnieje na ekranie.
    /// </summary>
    Bar,
}
