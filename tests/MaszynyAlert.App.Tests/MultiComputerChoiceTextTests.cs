using MaszynyAlert.App.ViewModels;

namespace MaszynyAlert.App.Tests;

public sealed class MultiComputerChoiceTextTests
{
    private static MultiComputerViewModel Create() =>
        new(() => new MultiComputerStatusInput(false, null, false, null, false, 0, true, false, true, null));

    [Fact]
    public void BeforeChoiceTheDetailIsEmpty()
    {
        var viewModel = Create();

        Assert.Equal(MultiComputerSetupPath.None, viewModel.SelectedPath);
        Assert.Equal("", viewModel.ChoiceDetail);
        Assert.Equal("Wybierz sposób rozpoczęcia", viewModel.ChoiceTitle);
    }

    [Fact]
    public void CreateAndJoinSentencesAreUnchanged()
    {
        var viewModel = Create();

        viewModel.SelectCreateGroupCommand.Execute(null);
        Assert.Equal("Poniżej możesz wpisać nazwę oraz sprawdzić bieżącą listę maszyn, profile i lokalną gotowość.", viewModel.ChoiceDetail);

        viewModel.SelectJoinGroupCommand.Execute(null);
        Assert.Equal("W kolejnym kroku wskażesz plik grupy i zobaczysz różnice przed zaakceptowaniem zmian.", viewModel.ChoiceDetail);
    }
}
