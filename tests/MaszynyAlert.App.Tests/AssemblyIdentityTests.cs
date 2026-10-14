using System.Diagnostics;

namespace MaszynyAlert.App.Tests;

public sealed class AssemblyIdentityTests
{
    [Fact]
    public void PlikProgramuNazywaSieVncAlert()
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(typeof(App).Assembly.Location);

        Assert.Equal("VNC Alert", info.FileDescription);
        Assert.Equal("VNC Alert", info.ProductName);
    }
}
