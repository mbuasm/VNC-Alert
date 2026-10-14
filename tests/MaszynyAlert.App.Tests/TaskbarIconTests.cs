using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaszynyAlert.App.Views;

namespace MaszynyAlert.App.Tests;

public sealed class TaskbarIconTests
{
    private const int WmGetIcon = 0x7F;
    private const nint IconBig = 1;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [Fact]
    public void RefreshSendsNewIconHandles_AndKeepsTheWindowsOwnIcon()
    {
        WpfTestHost.Run(() =>
        {
            var icon = BitmapFrame.Create(new Uri("pack://application:,,,/MaszynyAlert.App;component/Assets/VncAlert.ico"));
            var window = WpfTestHost.ShowOffScreen(new Window { Width = 200, Height = 100, Icon = icon });
            try
            {
                WpfTestHost.Pump(DispatcherPriority.ApplicationIdle);
                nint handle = new WindowInteropHelper(window).Handle;
                nint before = SendMessage(handle, WmGetIcon, IconBig, 0);

                TaskbarIcon.Refresh(window);

                nint after = SendMessage(handle, WmGetIcon, IconBig, 0);
                Assert.NotEqual(0, before);
                Assert.NotEqual(0, after);
                Assert.NotEqual(before, after);
                Assert.Same(icon, window.Icon);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WindowWithoutItsOwnIcon_GetsTheProgramIcon()
    {
        WpfTestHost.Run(() =>
        {
            var window = WpfTestHost.ShowOffScreen(new Window { Width = 200, Height = 100 });
            try
            {
                TaskbarIcon.Refresh(window);

                var frame = Assert.IsAssignableFrom<BitmapFrame>(window.Icon);
                Assert.True(frame.IsFrozen); // jedna, wspólna ikona programu dla wszystkich okien
                Assert.Equal(8, frame.Decoder.Frames.Count); // VncAlert.ico: 16, 20, 24, 32, 48, 64, 128, 256 px
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Tylko okna z przyciskiem na pasku zadań — dymek, alert i nakładka (ShowInTaskbar=False) nic nie robią.</summary>
    [Fact]
    public void OnlyWindowsWithATaskbarButtonAreAttached()
    {
        WpfTestHost.Run(() =>
        {
            var withButton = new Window();
            var withoutButton = new Window { ShowInTaskbar = false };

            TaskbarIcon.Attach(withButton);
            TaskbarIcon.Attach(withoutButton);

            Assert.True(TaskbarIcon.IsAttached(withButton));
            Assert.False(TaskbarIcon.IsAttached(withoutButton));
        });
    }

    [Fact]
    public void AppRegistersTheRefreshForEveryWindow()
    {
        string app = File.ReadAllText(WpfTestHost.SourceFile("src", "MaszynyAlert.App", "App.xaml.cs"));
        Assert.Contains("Views.TaskbarIcon.RegisterForAllWindows();", app, StringComparison.Ordinal);
    }
}
