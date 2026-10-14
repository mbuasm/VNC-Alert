using System.Text.RegularExpressions;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class U1TextTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepositoryRoot, .. parts]));

    [Fact]
    public void VncalertCmd_OnlyAscii()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", "vncalert.cmd"));
        int index = Array.FindIndex(bytes, b => b > 0x7F);
        Assert.True(index < 0, $"vncalert.cmd ma bajt spoza ASCII na pozycji {index}.");
    }

    [Fact]
    public void Program_SharedConfigReloadAfterMutex()
    {
        string program = Read("src", "MaszynyAlert.App", "Program.cs");
        int mutex = program.IndexOf("new Mutex(initiallyOwned: true, SingleInstanceMutexName", StringComparison.Ordinal);
        int reload = program.IndexOf("new SharedConfigReloadRequest()", StringComparison.Ordinal);
        Assert.True(mutex > 0 && reload > mutex, "Sygnał przeładowania tworzy dopiero instancja programu (po muteksie).");
    }

    [Fact]
    public void GroupWizard_HasNoChoiceSentences()
    {
        string xaml = Read("src", "MaszynyAlert.App", "Views", "MultiComputerView.xaml");
        string viewModel = Read("src", "MaszynyAlert.App", "ViewModels", "MultiComputerViewModel.cs");
        Assert.DoesNotContain("Sam wybór ścieżki", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ten wybór jeszcze niczego nie zapisuje", viewModel, StringComparison.Ordinal);
        Assert.Matches(new Regex("x:Name=\"ChoiceDetailText\"[^>]*Visibility=\"\\{Binding ChoiceDetail, Converter=\\{StaticResource NullToVisibility\\}\\}\""), xaml);
    }
}
