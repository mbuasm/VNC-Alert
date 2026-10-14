using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Tests;

public sealed class ToastWindowDismissTests
{
    [Fact]
    public void C01_ImmediateDismissClosesToastWhoseHideAnimationAlreadyStarted()
    {
        WpfTestHost.Run(() =>
        {
            var toast = WpfTestHost.ShowOffScreen(new ToastWindow(NotificationKind.Success,
                "Zapisano raport PDF", null, null, TimeSpan.FromMinutes(1)));
            WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
            toast.UpdateLayout();
            try
            {
                int closed = 0;
                toast.Closed += (_, _) => closed++;
                toast.Dismiss();
                Assert.True(toast.IsVisible);

                // ToastNotifications.Show musi zamknąć poprzedni dymek przed pokazaniem następnego.
                toast.Dismiss(immediately: true);

                Assert.Equal(1, closed);
                Assert.False(toast.IsVisible);
                // Ponowne żądanie nie wywołuje drugi raz odbiorcy Closed (np. edytora --setup).
                toast.Dismiss(immediately: true);
                Assert.Equal(1, closed);
            }
            finally { toast.Close(); }
        });
    }
}
