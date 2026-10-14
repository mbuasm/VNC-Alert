using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaszynyAlert.Core.Alerts;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class AlertItemViewModel : ObservableObject
{
    public AlertItemViewModel(Alert alert, Func<AlertKey, Task> acknowledge, Func<Alert, string>? help = null)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(acknowledge);

        Key = alert.Key;
        Solution = (help ?? MaszynyAlert.Core.Ocr.AlarmHelp.For)(alert);
        Line1 = alert.DisplayLine();
        Line2 = string.IsNullOrEmpty(alert.FullText) || alert.FullText == alert.ShortText ? null : alert.FullText;
        Sound = alert.Sound;
        IsRinging = alert.Status is AlertStatus.Active or AlertStatus.Escalated;
        IsAcknowledged = alert.Status == AlertStatus.Acknowledged;
        StatusText = alert.Status == AlertStatus.Acknowledged && alert.AckedByHost is not null
            ? $"sprawdza: {alert.AckedByHost} / {alert.AckedByUser}, {alert.AcknowledgedAt?.ToLocalTime():HH:mm}"
            : null;
        AcknowledgeCommand = new AsyncRelayCommand(() => acknowledge(Key));

        CaptureFile = alert.CaptureFile;
        HasCapture = !string.IsNullOrEmpty(CaptureFile) && File.Exists(CaptureFile);
        OpenCaptureCommand = new RelayCommand(OpenCapture, () => HasCapture);
    }

    /// <summary>Klucz epizodu — identyfikuje ten alert niezależnie od komputera, który go zgłosił.</summary>
    public AlertKey Key { get; }
    public string Solution { get; }

    /// <summary>Pierwszy wiersz: "NAZWA — opis — od HH:MM".</summary>
    public string Line1 { get; }

    /// <summary>Pełny tekst odczytany z ekranu (mniejszą czcionką pod spodem), gdy różni się od skrótu.</summary>
    public string? Line2 { get; }

    /// <summary>Czy ten alert ma odtwarzać dźwięk (przy Active/Escalated).</summary>
    public bool Sound { get; }

    /// <summary>Czy alert aktualnie "dzwoni" (Active/Escalated) — jeszcze niepotwierdzony albo eskalowany.</summary>
    public bool IsRinging { get; }

    /// <summary>
    /// Czy alert jest potwierdzony (pokazujemy "sprawdza: ..."). Potwierdzony alert NIE jest już widoczny
    /// w oknie alertu w ogóle (znika od razu po "Idę sprawdzić", patrz
    /// <see cref="AlertVisibility.IsVisibleInAlertWindow"/> i <see cref="ViewModels.AlertWindowViewModel"/>) —
    /// to pole ma sens tylko dla listy alertów aktywnych w oknie Status, gdzie potwierdzone alerty nadal się pokazują.
    /// </summary>
    public bool IsAcknowledged { get; }

    /// <summary>"sprawdza: PC / użytkownik HH:mm", gdy potwierdzony; inaczej null.</summary>
    public string? StatusText { get; }

    [ObservableProperty]
    private bool _isHelpExpanded;

    /// <summary>Komenda przycisku "Idę sprawdzić".</summary>
    public IAsyncRelayCommand AcknowledgeCommand { get; }

    public string? CaptureFile { get; }

    /// <summary>Czy przycisk "Pokaż zrzut" ma być widoczny — ścieżka niepusta i plik istnieje NA TYM komputerze.</summary>
    public bool HasCapture { get; }

    /// <summary>Komenda przycisku "Pokaż zrzut" — otwiera plik w domyślnej przeglądarce obrazów systemu.</summary>
    public IRelayCommand OpenCaptureCommand { get; }

    private void OpenCapture()
    {
        if (CaptureFile is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(CaptureFile) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Brak skojarzonej przeglądarki obrazów albo plik zniknął między sprawdzeniem a kliknięciem —
            // celowo ignorujemy (przycisk i tak jest widoczny tylko, gdy plik istniał przy tworzeniu wiersza).
        }
    }
}
