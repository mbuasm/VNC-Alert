using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Probe.Commands;

namespace MaszynyAlert.Probe.Tests;

public sealed class ConnectionHelperTests
{
    /// <summary>Odmowa serwera po próbie z pustą nazwą użytkownika (RfbClient.BuildSecurityFailureMessage).</summary>
    private const string EmptyUsernameRejected =
        "Serwer 192.168.1.110:5900 odrzucił poświadczenia RSA-AES, wysłane z PUSTĄ nazwą użytkownika — " +
        "ten serwer naprawdę wymaga logowania systemowego, więc oprócz hasła potrzebna jest nazwa " +
        "użytkownika: uzupełnij pole 'username' w config.json.";

    [Fact]
    public void BuildSkipReason_BrakNazwyUzytkownika_NieNazywaMaszynyWylaczona()
    {
        string reason = ConnectionHelper.BuildSkipReason(new RfbAuthenticationException(EmptyUsernameRejected));

        Assert.DoesNotContain("nie odpowiada", reason, StringComparison.Ordinal);
        Assert.Contains("PUSTĄ nazwą użytkownika", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSkipReason_BrakNazwyUzytkownika_PodpowiadaObieDrogiUzupelnienia()
    {
        string reason = ConnectionHelper.BuildSkipReason(new RfbAuthenticationException(EmptyUsernameRejected));

        // Operator ma dwa wyjścia i oba muszą być w raporcie: wpis w config.json (trwały) albo
        // --username w jednym przebiegu probe.exe (szybki test bez edycji pliku).
        Assert.Contains("config.json", reason, StringComparison.Ordinal);
        Assert.Contains("--username", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSkipReason_BledUwierzytelnienia_NieNazywaMaszynyWylaczona()
    {
        // Każdy RfbAuthenticationException znaczy, że serwer ODPOWIEDZIAŁ — handshake ruszył.
        string reason = ConnectionHelper.BuildSkipReason(
            new RfbAuthenticationException("Serwer 192.168.1.110:5900 przerwał uwierzytelnienie."));

        Assert.DoesNotContain("wyłączona?", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSkipReason_BrakLacznosci_NadalMowiZeMaszynaNieOdpowiada()
    {
        // prawdziwie milcząca maszyna ma nadal być opisana
        // jako wyłączona/nieosiągalna — inaczej operator nie odróżniłby jej od problemu z hasłem.
        string reason = ConnectionHelper.BuildSkipReason(
            new RfbException("Nie udało się połączyć z 192.168.1.199:5900 w ciągu 5 s."));

        Assert.Contains("nie odpowiada (wyłączona?)", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EmptyUsernameRejected, "brak nazwy użytkownika")]
    [InlineData("Serwer 192.168.1.110:5900 odrzucił poświadczenia RSA-AES (nazwa użytkownika i hasło) — nieprawidłowa nazwa użytkownika albo hasło.", "błędne poświadczenia")]
    [InlineData("Serwer 192.168.1.110:5900 wymaga hasła VNC (szyfrowane logowanie RSA-AES), ale w konfiguracji nie podano hasła dla tej maszyny — wpisz je w Ustawieniach.", "wymaga hasła")]
    [InlineData("serwer odrzucił hasło VNC podane przez --password — to hasło jest dla tej maszyny nieprawidłowe.", "błędne hasło")]
    [InlineData("Serwer 192.168.1.110:5900 oferuje tylko typy zabezpieczeń 13, 133 (wymaga RSA-AES)", "nieobsł. zabezpieczenia")]
    [InlineData("nie odpowiada (wyłączona?) — Nie udało się połączyć z 192.168.1.199:5900 w ciągu 5 s.", "nie odpowiada")]
    public void SkipStatus_OpisujeRzeczywistyPowod(string reason, string expected)
    {
        Assert.Equal(expected, InfoCommand.SkipStatus(reason));
    }
}
