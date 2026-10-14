using MaszynyAlert.App.Services;
using MaszynyAlert.App.ViewModels;

namespace MaszynyAlert.App.Views;

public partial class AlarmInstructionsView : System.Windows.Controls.UserControl
{
    public AlarmInstructionsView(AlarmHelpService help, Func<string>? groupStatus = null)
    {
        InitializeComponent();
        var model = new AlarmInstructionsViewModel(help, groupStatus);
        DataContext = model;
        // Instrukcje mogą się zmienić bez udziału tego operatora: w grupie przychodzą z innego komputera
        // Zdarzenie przychodzi z wątku odbioru, więc wracamy na wątek okna.
        void OnChanged() => Dispatcher.BeginInvoke(new Action(model.RefreshMetadata));
        help.Changed += OnChanged;
        Unloaded += (_, _) => help.Changed -= OnChanged;
    }
}
