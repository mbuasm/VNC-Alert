using System.Xml;
using System.Xml.Linq;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Packaging;

public sealed class AppManifestTests
{
    /// <summary>Wszystkie manifesty w repozytorium (ścieżki względem katalogu głównego repo).</summary>
    public static TheoryData<string> ManifestFiles()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(TestPaths.RepositoryRoot, "src"), "*.manifest", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(TestPaths.RepositoryRoot, path);

            // Katalogi wynikowe kompilacji zawierają kopie tych samych plików — sprawdzamy tylko źródła.
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            data.Add(relative);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ManifestFiles))]
    public void Manifest_JestPoprawnymXml(string relativePath)
    {
        string path = Path.Combine(TestPaths.RepositoryRoot, relativePath);

        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Manifest '{relativePath}' nie jest poprawnym XML-em ({ex.Message}). " +
                "Windows odrzuci taki plik przy starcie programu (błąd SxS: „konfiguracja równoczesna jest " +
                "niepoprawna”), mimo że kompilacja przejdzie. Najczęstsza przyczyna: podwójny dywiz \"--\" " +
                "wewnątrz komentarza XML (np. opis przełącznika wiersza polecenia).");
        }

        // Sanity check, że wczytaliśmy manifest, a nie przypadkowy plik XML.
        Assert.NotNull(document.Root);
        Assert.Equal("assembly", document.Root!.Name.LocalName);
        Assert.Equal("urn:schemas-microsoft-com:asm.v1", document.Root.Name.NamespaceName);
    }

    [Fact]
    public void ManifestAplikacji_MaOczekiwaneUstawienia()
    {
        XDocument document = XDocument.Load(Path.Combine(TestPaths.RepositoryRoot, "src", "MaszynyAlert.App", "app.manifest"));

        XElement? level = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "requestedExecutionLevel");
        Assert.NotNull(level);
        Assert.Equal("asInvoker", level!.Attribute("level")?.Value);

        Assert.Contains(
            document.Descendants().Where(e => e.Name.LocalName == "dpiAwareness"),
            e => e.Value.Contains("PerMonitorV2", StringComparison.Ordinal));

        Assert.Contains(
            document.Descendants().Where(e => e.Name.LocalName == "supportedOS"),
            e => e.Attribute("Id")?.Value == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }
}
