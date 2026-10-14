using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Vision;

namespace MaszynyAlert.Core.Tests.Samples;

/// <summary>
/// Testy zestawu próbek paska statusu (tests/samples). Gdy katalog próbek profilu jest pusty,
/// test najpierw GENERUJE komplet syntetycznych próbek do katalogu źródłowego repozytorium
/// (żeby trafiły do gita), a następnie sprawdza je detektorem czerwieni.
/// </summary>
public sealed class SamplesTests
{
    public static TheoryData<string> Profiles()
    {
        var data = new TheoryData<string>();
        foreach (string id in SampleGenerator.ProfileIds)
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Samples_Exist_And_Match_Expected(string profileId)
    {
        string outputRoot = TestPaths.SamplesOutput;

        if (SampleGenerator.IsEmpty(profileId, outputRoot))
        {
            // Katalog próbek pusty — generujemy komplet do ŹRÓDŁA repozytorium (tests/samples),
            // żeby pliki PNG dało się dodać do repozytorium, oraz do katalogu wyjściowego tego uruchomienia.
            SampleGenerator.Write(profileId, TestPaths.SamplesSource);
            SampleGenerator.Write(profileId, outputRoot);
        }

        SampleExpectations expectations = SampleGenerator.ReadExpectations(profileId, outputRoot);
        Assert.Equal(profileId, expectations.Profile);
        Assert.Equal(7, expectations.Files.Count);

        ScreenProfile profile = TestPaths.Profile(profileId);
        var detector = new RedTextDetector(profile);

        foreach ((string relativePath, SampleExpectation expected) in expectations.Files)
        {
            string path = Path.Combine(outputRoot, profileId, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Brak pliku próbki '{path}'.");

            RgbaImage image = PngCodec.DecodeFile(path);
            Assert.Equal(profile.StatusBarRoi.Width, image.Width);
            Assert.Equal(profile.StatusBarRoi.Height, image.Height);

            RedDetectionResult result = detector.Analyze(image);
            Assert.True(
                expected.IsRed == result.IsRed,
                $"Próbka '{profileId}/{relativePath}': oczekiwano isRed={expected.IsRed}, " +
                $"a detektor zwrócił {result.IsRed} (udział czerwieni {result.RedFraction:P3}).");
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Samples_Error_Files_Have_Red_Fraction_Above_Threshold(string profileId)
    {
        string outputRoot = TestPaths.SamplesOutput;
        if (SampleGenerator.IsEmpty(profileId, outputRoot))
        {
            SampleGenerator.Write(profileId, TestPaths.SamplesSource);
            SampleGenerator.Write(profileId, outputRoot);
        }

        ScreenProfile profile = TestPaths.Profile(profileId);
        var detector = new RedTextDetector(profile);
        SampleExpectations expectations = SampleGenerator.ReadExpectations(profileId, outputRoot);

        foreach ((string relativePath, SampleExpectation expected) in expectations.Files)
        {
            string path = Path.Combine(outputRoot, profileId, relativePath.Replace('/', Path.DirectorySeparatorChar));
            RedDetectionResult result = detector.Analyze(PngCodec.DecodeFile(path));

            if (expected.IsRed)
            {
                Assert.True(result.RedFraction >= profile.MinRedFraction,
                    $"'{relativePath}': udział czerwieni {result.RedFraction:P3} poniżej progu {profile.MinRedFraction:P3}.");
                Assert.NotNull(result.RedBounds);
            }
            else
            {
                Assert.True(result.RedFraction < profile.MinRedFraction,
                    $"'{relativePath}': udział czerwieni {result.RedFraction:P3} powyżej progu {profile.MinRedFraction:P3}.");
            }
        }
    }
}
