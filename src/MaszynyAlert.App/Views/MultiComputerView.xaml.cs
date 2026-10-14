using System.Windows;
using System.Windows.Controls;
using MaszynyAlert.App.ViewModels;

namespace MaszynyAlert.App.Views;

public partial class MultiComputerView : System.Windows.Controls.UserControl
{
    private MultiComputerViewModel? _subscribed;

    public MultiComputerView(MultiComputerViewModel viewModel)
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        JoinSharedPasswordBox.Clear();
        if (_subscribed is not null)
            _subscribed.JoinPasswordInputCleared -= OnJoinPasswordInputCleared;
        _subscribed = e.NewValue as MultiComputerViewModel;
        if (_subscribed is not null)
            _subscribed.JoinPasswordInputCleared += OnJoinPasswordInputCleared;
    }

    private void OnJoinPasswordInputCleared() => JoinSharedPasswordBox.Clear();

    // Hasła nigdy nie są bindowane (PasswordBox.Password nie jest właściwością zależności) ani odczytywane
    // z powrotem do pól — model widoku dostaje wpisaną wartość tylko do jawnego zapisu w lokalnym magazynie.
    private void JoinSharedPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MultiComputerViewModel viewModel)
            viewModel.SetSharedJoinPassword(((PasswordBox)sender).Password);
    }

    private void JoinMachinePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (DataContext is MultiComputerViewModel viewModel && box.DataContext is JoinPasswordEntry entry)
            viewModel.SetJoinPassword(entry.MachineId, box.Password);
    }
}
