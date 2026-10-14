using System.Windows;
using MaszynyAlert.Core.Abstractions;

namespace MaszynyAlert.App.Views;

public partial class AdminLoginWindow : Window
{
    private readonly ISecretStore _secrets;
    private static readonly AdminLoginThrottle Throttle = new();
    public AdminLoginWindow(ISecretStore secrets)
    {
        InitializeComponent();
        _secrets = secrets;
        Loaded += (_, _) => Password.Focus();
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!LoginButton.IsEnabled) return;
        string password = Password.Password;
        ErrorText.Text = "";
        if (password.Length == 0) { ErrorText.Text = "Wpisz hasło."; Password.Focus(); return; }
        LoginButton.IsEnabled = false;
        bool waiting = Throttle.Remaining(DateTimeOffset.UtcNow) > TimeSpan.Zero;
        bool valid;
        try
        {
            valid = !waiting && await Task.Run(() => AdminPassword.VerifyLogin(password,
                () => _secrets.Get(AdminPassword.SecretKey)));
        }
        catch
        {
            if (IsVisible) { LoginButton.IsEnabled = true; ErrorText.Text = "Nie można odczytać ustawień logowania."; }
            return;
        }
        if (!IsVisible) return;
        LoginButton.IsEnabled = true;
        if (valid) { DialogResult = true; return; }
        if (waiting)
        {
            ErrorText.Text = AdminLoginThrottle.WaitMessage(Throttle.Remaining(DateTimeOffset.UtcNow));
            Password.Focus();
            return;
        }
        Password.Clear();
        Throttle.RegisterFailure(DateTimeOffset.UtcNow);
        ErrorText.Text = "Nieprawidłowe hasło. Spróbuj ponownie za 3 sekundy.";
        Password.Focus();
    }
}
