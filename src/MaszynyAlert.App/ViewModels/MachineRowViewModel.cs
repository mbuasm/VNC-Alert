using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.ViewModels;

public sealed class MachineRowViewModel : ObservableObject
{
    public MachineRowViewModel(MachineStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        MachineId = snapshot.MachineId;
        Name = snapshot.Name;
        Address = $"{snapshot.Host}:{snapshot.Port}";
        Profile = snapshot.Profile;
        StateText = TranslateState(snapshot.State);
        Source = snapshot.Source == "plik" ? "TEST Z PLIKU — VNC wyłączone" : snapshot.Source;
        RfbInfo = DescribeInfo(snapshot);
        LastFrameText = snapshot.LastFrameAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
        if (snapshot.UsingCachedFrame) LastFrameText += " (obraz z pamięci)";
        RedPercentText = (snapshot.LastRedFraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        LastOcrText = string.IsNullOrEmpty(snapshot.LastOcrText) ? "-" : snapshot.LastOcrText;
        LastError = string.IsNullOrEmpty(snapshot.LastError) ? "-" : snapshot.LastError;
        IsProblem = snapshot.State is MachineConnectionState.Error or MachineConnectionState.Disconnected
            || snapshot.ScreenObservationUnavailable || !snapshot.OcrAvailable || snapshot.Source == "plik";
        ProgressText = DescribeProgress(snapshot);
        ScreenText = DescribeScreen(snapshot);
    }

    private string _name = "";
    private string _address = "";
    private string _profile = "";
    private string _stateText = "";
    private string _source = "";
    private string _rfbInfo = "";
    private string _lastFrameText = "";
    private string _redPercentText = "";
    private string _lastOcrText = "";
    private string _lastError = "";
    private string _progressText = "";
    private string _screenText = "";
    private bool _isProblem;

    public void CopyFrom(MachineRowViewModel fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        Name = fresh.Name;
        Address = fresh.Address;
        Profile = fresh.Profile;
        StateText = fresh.StateText;
        Source = fresh.Source;
        RfbInfo = fresh.RfbInfo;
        LastFrameText = fresh.LastFrameText;
        RedPercentText = fresh.RedPercentText;
        LastOcrText = fresh.LastOcrText;
        LastError = fresh.LastError;
        ProgressText = fresh.ProgressText;
        ScreenText = fresh.ScreenText;
        IsProblem = fresh.IsProblem;
    }

    public string MachineId { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Address { get => _address; private set => SetProperty(ref _address, value); }
    public string Profile { get => _profile; private set => SetProperty(ref _profile, value); }
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string Source { get => _source; private set => SetProperty(ref _source, value); }
    public string RfbInfo { get => _rfbInfo; private set => SetProperty(ref _rfbInfo, value); }
    public string LastFrameText { get => _lastFrameText; private set => SetProperty(ref _lastFrameText, value); }
    public string RedPercentText { get => _redPercentText; private set => SetProperty(ref _redPercentText, value); }
    public string LastOcrText { get => _lastOcrText; private set => SetProperty(ref _lastOcrText, value); }
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    /// <summary>
    /// Czy HMI pokazuje ekran główny (sekcja <c>mainScreen</c> profilu): "-", gdy funkcja jest wyłączona dla tej
    /// maszyny (brak sekcji, <c>enabled: false</c> albo brak OCR); "?" — jeszcze nie sprawdzono; "główny" — OK;
    /// "INNY od HH:MM" — HMI pokazuje inną zakładkę (np. „Production info”) od podanej godziny.
    /// </summary>
    public string ScreenText { get => _screenText; private set => SetProperty(ref _screenText, value); }

    /// <summary>Czy ten wiersz sygnalizuje problem (do podświetlenia w tabeli).</summary>
    public bool IsProblem { get => _isProblem; private set => SetProperty(ref _isProblem, value); }

    private static string TranslateState(MachineConnectionState state) => state switch
    {
        MachineConnectionState.Disconnected => "Brak połączenia",
        MachineConnectionState.Connecting => "Łączenie…",
        MachineConnectionState.Connected => "Połączono",
        MachineConnectionState.Error => "Błąd",
        MachineConnectionState.Disabled => "Wyłączona",
        _ => state.ToString(),
    };

    private static string DescribeInfo(MachineStatusSnapshot s)
    {
        if (s.Info is not { } info)
        {
            return "-";
        }

        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(info.ProtocolVersion))
        {
            parts.Add($"RFB {info.ProtocolVersion}");
        }

        if (!string.IsNullOrEmpty(info.SecurityType))
        {
            parts.Add(info.SecurityType);
        }

        if (!string.IsNullOrEmpty(info.PixelFormat))
        {
            parts.Add(info.PixelFormat);
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private static string DescribeProgress(MachineStatusSnapshot s)
    {
        if (!s.IdleWatchEnabled)
        {
            return "-";
        }

        if (s.ProgressFullSince is { } since)
        {
            return $"100 % od {since.ToLocalTime():HH:mm}";
        }

        if (s.ProgressZeroSince is { } zeroSince)
        {
            return $"0 % od {zeroSince.ToLocalTime():HH:mm}";
        }

        if (s.LastProgressPercent is null)
        {
            // Tryb "number": OCR nie zwrócił (jeszcze) żadnej rozpoznawalnej liczby procent — pokazujemy
            // to WPROST zamiast milczącego "0 %", żeby administrator od razu widział, że trzeba dostroić
            // progressRoi/ocrScale/invertForOcr, a nie że maszyna faktycznie stoi na zerze.
            return DescribeUnreadable(s.LastProgressText);
        }

        string percent = s.LastProgressPercent.Value.ToString(CultureInfo.InvariantCulture) + " %";
        // Źródło widać tylko przy pasku — odczyt OCR zostaje zapisany (samo „N %”).
        return s.LastProgressSource == "pasek" ? percent + " (pasek)" : percent;
    }

    private static string DescribeScreen(MachineStatusSnapshot s)
    {
        if (s.MainScreenWatchRequired && !s.MainScreenWatchEnabled)
        {
            return "Brak kontroli ekranu (OCR niedostępny)";
        }
        if (!s.MainScreenWatchEnabled)
        {
            return "-";
        }

        return s.MainScreenVisible switch
        {
            null => "Nierozpoznany — brak wiarygodnej obserwacji",
            true => "główny",
            false => s.WrongScreenSince is { } since ? $"INNY od {since.ToLocalTime():HH:mm}" : "INNY",
        };
    }

    private const int UnreadableTextMaxLength = 20;

    private static string DescribeUnreadable(string? rawOcrText)
    {
        if (string.IsNullOrEmpty(rawOcrText))
        {
            return "? (OCR nie odczytał)";
        }

        string shortened = rawOcrText.Length > UnreadableTextMaxLength
            ? rawOcrText[..UnreadableTextMaxLength]
            : rawOcrText;
        return $"? (OCR nie odczytał: „{shortened}”)";
    }
}
