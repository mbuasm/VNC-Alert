using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class WindowBehaviourCodeTests
{
    private static string AppFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", .. parts]));

    [Fact]
    public void ZapisUstawien_InnyBladUruchomieniaNizAnulowanie_DajeKomunikatZamiastAwarii()
    {
        string code = AppFile("Views", "SetupView.xaml.cs");
        int cancelled = code.IndexOf("catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)", StringComparison.Ordinal);
        int other = code.IndexOf("catch (System.ComponentModel.Win32Exception ex)\n", cancelled + 1, StringComparison.Ordinal);
        if (other < 0) other = code.IndexOf("catch (System.ComponentModel.Win32Exception ex)\r\n", cancelled + 1, StringComparison.Ordinal);
        Assert.True(cancelled > 0 && other > cancelled, "Po obsłudze anulowania UAC ma być obsługa pozostałych Win32Exception.");
    }

    [Fact]
    public void KomunikatNieoczekiwanegoBledu_WskazujeIstniejaceMiejsceFolderuLogow()
    {
        string app = AppFile("App.xaml.cs");
        Assert.DoesNotContain("zasobniku → Otwórz folder logów", app, StringComparison.Ordinal);
        Assert.Contains("Podgląd techniczny → Otwórz folder logów", app, StringComparison.Ordinal);
        Assert.Contains("Content=\"Otwórz folder logów\"", AppFile("Views", "StatusWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void OknoAlertu_ZawszeChowaZamiastZamykac()
    {
        string code = AppFile("Views", "AlertWindow.xaml.cs");
        string onClosing = code[code.IndexOf("private void OnClosing", StringComparison.Ordinal)..];
        Assert.DoesNotContain("HasVisibleAlerts", onClosing, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true;", onClosing, StringComparison.Ordinal);
    }
}
