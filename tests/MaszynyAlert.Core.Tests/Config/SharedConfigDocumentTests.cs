using System.Text.Json;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.History;
using MaszynyAlert.Core.Tests.TestUtil;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class SharedConfigDocumentTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static SharedConfigDocument Document(TimingConfig? timing = null, OverlayConfig? overlay = null,
        DateTimeOffset? at = null, string host = "PC-A") =>
        new() { Timing = timing, Overlay = overlay, EditedAt = at ?? Noon, EditedBy = host };

    [Fact]
    public void NewerDocumentWins()
    {
        var older = Document(at: Noon);
        var newer = Document(at: Noon.AddSeconds(1));

        Assert.True(SharedConfigDocument.Wins(newer, older));
        Assert.False(SharedConfigDocument.Wins(older, newer));
    }

    [Fact]
    public void MissingDocumentLosesWithEverything()
    {
        Assert.True(SharedConfigDocument.Wins(Document(), null));
    }

    [Fact]
    public void TieIsResolvedByTheComputerName()
    {
        var fromA = Document(host: "PC-A");
        var fromB = Document(host: "PC-B");

        // Ten sam czas na dwóch stanowiskach: rozstrzygnięcie musi być takie samo na każdym komputerze.
        Assert.True(SharedConfigDocument.Wins(fromB, fromA));
        Assert.False(SharedConfigDocument.Wins(fromA, fromB));
    }

    [Fact]
    public void IdenticalDocumentDoesNotWin()
    {
        Assert.False(SharedConfigDocument.Wins(Document(), Document()));
    }

    [Fact]
    public void TieOnTimeAndComputerIsResolvedByTheFingerprint()
    {
        var quiet = Document(timing: new TimingConfig { AlertSoundRepeatMs = 30000 });
        var loud = Document(timing: new TimingConfig { AlertSoundRepeatMs = 90000 });

        // Wynik ma być przeciwny w obie strony — inaczej dwa komputery odsyłałyby sobie dokumenty w kółko.
        Assert.NotEqual(SharedConfigDocument.Wins(quiet, loud), SharedConfigDocument.Wins(loud, quiet));
    }

    [Fact]
    public void EmptyFieldMeansTheValueFromTheLayerBelow()
    {
        var baseConfig = TestPaths.ExampleConfig;
        var document = Document(overlay: baseConfig.Ui.Overlay with { Scale = 1.5 });

        var effective = document.ApplyTo(baseConfig);

        Assert.True(document.Timing is null);
        Assert.Equal(baseConfig.Timing, effective.Timing);
        Assert.Equal(1.5, effective.Ui.Overlay.Scale);
        // Warstwa nie może przy okazji ruszyć niczego poza swoimi dwiema sekcjami.
        Assert.Equal(baseConfig.Machines, effective.Machines);
        Assert.Equal(baseConfig.Ui.AlertOpacity, effective.Ui.AlertOpacity);
    }

    [Fact]
    public void EmptyDocumentRestoresEverythingFromTheLayerBelow()
    {
        var baseConfig = TestPaths.ExampleConfig;
        var document = Document();

        Assert.True(document.IsEmpty);
        Assert.True(document.IsValid());
        Assert.Equal(baseConfig, document.ApplyTo(baseConfig));
    }

    [Theory]
    // Te same granice, co przy wczytywaniu config.json — warstwa z sieci nie może ich obchodzić.
    [InlineData(0.05, 1.0)]
    [InlineData(0.55, 5.0)]
    public void OverlayOutsideTheAllowedRangeIsNotValid(double opacity, double scale)
    {
        Assert.False(Document(overlay: new OverlayConfig { Opacity = opacity, Scale = scale }).IsValid());
    }

    [Fact]
    public void TimingOutsideTheAllowedRangeIsNotValid()
    {
        Assert.False(Document(timing: new TimingConfig { PollIntervalMs = 10 }).IsValid());
    }

    [Fact]
    public void DocumentWithoutAuthorOrTimeIsNotValid()
    {
        Assert.False(Document(host: "").IsValid());
        Assert.False(Document(host: "PCA").IsValid());
        Assert.False(Document(host: new string('x', 101)).IsValid());
        Assert.False((Document() with { EditedAt = default }).IsValid());
    }

    [Fact]
    public void FingerprintIsStableAndChangesWithEveryPartOfTheDocument()
    {
        var document = Document(timing: new TimingConfig { AlertSoundRepeatMs = 30000 });

        Assert.Equal(64, document.Hash.Length);
        Assert.Equal(document.Hash, Document(timing: new TimingConfig { AlertSoundRepeatMs = 30000 }).Hash);
        Assert.Equal(document.Hash.ToLowerInvariant(), document.Hash);
        Assert.NotEqual(document.Hash, (document with { EditedAt = Noon.AddTicks(1) }).Hash);
        Assert.NotEqual(document.Hash, (document with { EditedBy = "PC-B" }).Hash);
        Assert.NotEqual(document.Hash, (document with { Timing = new TimingConfig { AlertSoundRepeatMs = 90000 } }).Hash);
        Assert.NotEqual(document.Hash, (document with { Overlay = new OverlayConfig() }).Hash);
    }


    private static UpdateApproval Approval(string version = "0.4.26.0", string host = "PC-B") => new()
    {
        Version = version, FileName = $"MaszynyAlert-Setup-{version}.exe", ApprovedBy = host, ApprovedAt = Noon,
    };

    [Fact]
    public void DocumentWithoutAnApprovalSerializesExactlyAsBeforeSoOlderComputersAgreeOnTheFingerprint()
    {
        string json = JsonSerializer.Serialize(Document(timing: new TimingConfig()), SharedConfigDocument.CanonicalJsonOptions);

        Assert.DoesNotContain("updateApproval", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalRoundTripsAndIsCoveredByTheFingerprint()
    {
        var document = Document() with { UpdateApproval = Approval() };

        string json = JsonSerializer.Serialize(document, SharedConfigDocument.CanonicalJsonOptions);
        var back = JsonSerializer.Deserialize<SharedConfigDocument>(json, SharedConfigDocument.CanonicalJsonOptions)!;

        Assert.Contains("\"updateApproval\"", json, StringComparison.Ordinal);
        Assert.Equal(document.UpdateApproval, back.UpdateApproval);
        Assert.Equal(document.Hash, back.Hash);
        Assert.NotEqual(Document().Hash, document.Hash);
        Assert.Equal(new Version(0, 4, 26, 0), back.UpdateApproval!.ParsedVersion);
        Assert.DoesNotContain("parsedVersion", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentWithOnlyAnApprovalIsValidAndStillOverridesNoSettings()
    {
        var document = Document() with { UpdateApproval = Approval() };

        Assert.True(document.IsValid());
        // IsEmpty mówi o nadpisaniach konfiguracji — zgoda nic w konfiguracji nie zmienia.
        Assert.True(document.IsEmpty);
        Assert.Equal(TestPaths.ExampleConfig, document.ApplyTo(TestPaths.ExampleConfig));
    }

    [Theory]
    [InlineData("nie-wersja", "MaszynyAlert-Setup-0.4.26.0.exe", "PC-B")]
    [InlineData("0.4.26.0", "../MaszynyAlert-Setup-0.4.26.0.exe", "PC-B")]
    [InlineData("0.4.26.0", "MaszynyAlert..exe", "PC-B")]
    [InlineData("0.4.26.0", "C:\\x.exe", "PC-B")]
    [InlineData("0.4.26.0", "", "PC-B")]
    [InlineData("0.4.26.0", "MaszynyAlert-Setup-0.4.26.0.exe", "")]
    [InlineData("0.4.26.0", "MaszynyAlert-Setup-0.4.26.0.exe", "PC\nB")]
    public void InvalidApprovalMakesTheWholeDocumentInvalid(string version, string fileName, string host)
    {
        var approval = new UpdateApproval { Version = version, FileName = fileName, ApprovedBy = host, ApprovedAt = Noon };

        Assert.False(approval.IsValid());
        Assert.False((Document() with { UpdateApproval = approval }).IsValid());
    }

    [Fact]
    public void ApprovalWithoutTimeOrWithATooLongNameIsNotValid()
    {
        Assert.False((Approval() with { ApprovedAt = default }).IsValid());
        Assert.False((Approval() with { ApprovedBy = new string('x', 101) }).IsValid());
        Assert.False((Approval() with { FileName = new string('a', 122) + ".exe" }).IsValid());
        Assert.True(Approval().IsValid());
    }

    [Fact]
    public void ThreePartApprovalVersionComparesLikeTheFourPartOne()
    {
        Assert.Equal(new Version(0, 4, 26, 0), (Approval() with { Version = "0.4.26" }).ParsedVersion);
    }

    [Fact]
    public void OnATieTheCopyThatStillCarriesTheApprovalWins()
    {
        // Dlaczego: komputer ze starszą wersją programu nie zna pola zgody — odsyła ten sam dokument (ten sam
        // czas i autor) BEZ niej. Gdyby remis rozstrzygał tylko odcisk, połowa takich wymian kasowałaby
        // zgodę na komputerach, które ją rozumieją.
        var withApproval = Document() with { UpdateApproval = Approval() };
        var stripped = Document();

        Assert.True(SharedConfigDocument.Wins(withApproval, stripped));
        Assert.False(SharedConfigDocument.Wins(stripped, withApproval));
    }
}
