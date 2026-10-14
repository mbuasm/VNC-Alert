using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MaszynyAlert.App.Services;
using MaszynyAlert.App.Views;
using Xunit.Abstractions;
using Size = System.Windows.Size;

namespace MaszynyAlert.App.Tests;

/// <summary>testy behawioralne; wszystkie okna wyłącznie OffScreen.</summary>
public sealed class AuditCToastLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public void C01_ImmediateDismissClosesToastWhoseHideAnimationAlreadyStarted()
    {
        WpfTestHost.Run(() =>
        {
            ToastWindow toast = Open();
            try
            {
                bool closed = false;
                toast.Closed += (_, _) => closed = true;
                toast.Dismiss(); // Ta sama ścieżka co upłynięcie 2 s lub kliknięcie.
                Assert.True(toast.IsVisible);

                // ToastNotifications.Show wywołuje dokładnie to przed pokazaniem następnego dymka.
                toast.Dismiss(immediately: true);

                Assert.True(closed, "Zastępowany dymek nadal czeka na animację 550 ms; nowy może być widoczny równocześnie.");
                Assert.False(toast.IsVisible);
            }
            finally { toast.Close(); }
        });
    }

    [Fact]
    public void ImmediateDismissClosesToastBeforeItsHideAnimationStarted()
    {
        WpfTestHost.Run(() =>
        {
            ToastWindow toast = Open();
            try
            {
                bool closed = false;
                toast.Closed += (_, _) => closed = true;
                toast.Dismiss(immediately: true);
                Assert.True(closed);
                Assert.False(toast.IsVisible);
            }
            finally { toast.Close(); }
        });
    }

    [Fact]
    public void RepeatedTaskbarIconRefreshDoesNotAccumulateNativeGuiHandles()
    {
        WpfTestHost.Run(() =>
        {
            var window = WpfTestHost.ShowOffScreen(new Window { Width = 200, Height = 100 });
            try
            {
                for (int i = 0; i < 8; i++) TaskbarIcon.Refresh(window);
                WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
                using Process process = Process.GetCurrentProcess();
                uint userBefore = GetGuiResources(process.Handle, 1);
                uint gdiBefore = GetGuiResources(process.Handle, 0);
                Assert.True(userBefore > 0 && gdiBefore > 0, "Pomiar uchwytów GUI musi być dostępny.");

                for (int i = 0; i < 128; i++) TaskbarIcon.Refresh(window);
                WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
                uint userAfter = GetGuiResources(process.Handle, 1);
                uint gdiAfter = GetGuiResources(process.Handle, 0);
                output.WriteLine($"128 odświeżeń ikony: USER {userBefore} -> {userAfter}, GDI {gdiBefore} -> {gdiAfter}.");
                Assert.True(userAfter <= userBefore + 4, $"Uchwyty USER: {userBefore} -> {userAfter}");
                Assert.True(gdiAfter <= gdiBefore + 4, $"Uchwyty GDI: {gdiBefore} -> {gdiAfter}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1.25, -1920)]
    [InlineData(1.50, 1920)]
    public void ToastPositionKeepsBottomEdgeAfterDeviceToDipConversion(double scale, double deviceLeft)
    {
        // To kontrola czystej geometrii, nie dowód działania natywnego wyboru monitora/DPI.
        var work = new Rect(deviceLeft / scale, 0, 1920 / scale, 1040 / scale);
        var anchor = new Rect((deviceLeft + 100) / scale, 100 / scale, 1100 / scale, 800 / scale);
        var size = new Size(440, 100);
        var position = WindowPlacement.ToastPosition(work, size, anchor);
        Assert.Equal(anchor.Bottom, position.Y + size.Height, 6);
        Assert.InRange(position.X, work.Left, work.Right - size.Width);
    }

    private static ToastWindow Open()
    {
        var toast = WpfTestHost.ShowOffScreen(new ToastWindow(NotificationKind.Success,
            "Zapisano raport PDF", null, null, TimeSpan.FromMinutes(1)));
        WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
        toast.UpdateLayout();
        return toast;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);
}