using CommunityToolkit.Mvvm.ComponentModel;

namespace MaszynyAlert.App.ViewModels;

public sealed partial class SetupWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _windowsNotificationsEnabled = true;
    [ObservableProperty]
    private bool _samePasswordForAllMachines = true;

    /// <summary>Klucz sieciowy klastra w postaci Base64 (pole tekstowe — bez ryzyka bezpieczeństwa jak przy haśle).</summary>
    [ObservableProperty]
    private string _networkKeyBase64 = string.Empty;

    /// <summary>Priorytet tego komputera przy wyborze obserwatora, jako tekst (walidacja liczby przy zapisie).</summary>
    [ObservableProperty]
    private string _priorityText = "100";

    /// <summary>Czy program ma się uruchamiać automatycznie przy logowaniu.</summary>
    [ObservableProperty]
    private bool _autostartEnabled;

    /// <summary>
    /// Identyfikator tokenu Cloudflare Access do katalogu wydań (<c>updates.accessClientId</c>) — jawny, więc
    /// pole tekstowe; sekret tokenu jest w <see cref="System.Windows.Controls.PasswordBox"/> jak hasła VNC.
    /// </summary>
    [ObservableProperty]
    private string _updatesAccessClientId = string.Empty;
}
