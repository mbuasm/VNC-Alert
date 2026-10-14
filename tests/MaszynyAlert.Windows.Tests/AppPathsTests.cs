namespace MaszynyAlert.Windows.Tests;

public sealed class AppPathsTests
{
    [Theory]
    [InlineData(@"C:\Program Files\MaszynyAlert", true)]
    [InlineData(@"c:\PROGRAM FILES\MaszynyAlert\", true)]
    [InlineData(@"C:\Program Files (x86)\MaszynyAlert", true)]
    [InlineData(@"C:\Program Files\VNC Alert", true)]
    [InlineData(@"c:\program files\vnc alert\", true)]
    [InlineData(@"C:\Program Files (x86)\VNC Alert", true)]
    [InlineData(@"C:\Program Files-backup\MaszynyAlert", false)]
    [InlineData(@"C:\Program Files (x86)-backup\MaszynyAlert", false)]
    [InlineData(@"C:\Program Files\..\portable", false)]
    [InlineData(@"C:\Program Files\..\VNC Alert", false)]
    [InlineData(@"D:\MaszynyAlert", false)]
    [InlineData(@"D:\VNC Alert", false)]
    public void InstallationRequiresAnActualProgramFilesDirectory(string path, bool installed)
        => Assert.Equal(installed, AppPaths.IsInstalledLocation(path, @"C:\Program Files", @"C:\Program Files (x86)"));

    [Fact]
    public void MissingSpecialFoldersDoNotMatchPortable()
        => Assert.False(AppPaths.IsInstalledLocation(@"D:\MaszynyAlert", "", ""));
}
