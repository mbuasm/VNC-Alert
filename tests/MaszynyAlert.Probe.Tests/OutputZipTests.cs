using MaszynyAlert.Probe;

namespace MaszynyAlert.Probe.Tests;

public sealed class OutputZipTests
{
    [Fact]
    public void BuildZipPath_TworzyPlikObokFolderuOutZeStemplemCzasu()
    {
        // Dlaczego: zip ma leżeć OBOK folderu out (inaczej pakowałby sam siebie), a nazwa ma mówić, z kiedy jest.
        string outDir = Path.Combine(Path.GetTempPath(), "probe-test", "out");
        var now = new DateTimeOffset(2026, 9, 11, 15, 30, 5, TimeSpan.Zero);

        string zip = OutputZip.BuildZipPath(outDir, now);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "probe-test", "out_20260911-153005.zip"), zip);
        Assert.False(zip.StartsWith(outDir + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildZipPath_IgnorujeKoncowySeparator()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "probe-test", "out") + Path.DirectorySeparatorChar;
        var now = new DateTimeOffset(2026, 9, 11, 15, 30, 5, TimeSpan.Zero);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "probe-test", "out_20260911-153005.zip"), OutputZip.BuildZipPath(outDir, now));
    }

    [Fact]
    public void BuildZipPath_PustejSciezki_RzucaWyjatek()
    {
        Assert.Throws<ArgumentException>(() => OutputZip.BuildZipPath(" ", DateTimeOffset.Now));
    }
}
