using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Updates;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class SharedConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "shared-config-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private SharedConfigStore Store => new(Path.Combine(_directory, "shared-config.json"));

    [Fact]
    public void MissingFileIsNotAnErrorButAnEmptyLayer()
    {
        Assert.Null(Store.Load());
    }

    [Fact]
    public void SaveSurvivesReloadAndKeepsTheOverlayCornerReadableLikeInConfigJson()
    {
        var saved = Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 },
            new OverlayConfig { Enabled = true, Opacity = 0.8, Scale = 1.4, Position = OverlayPosition.BottomRight },
            Noon, "PC-A");

        var loaded = Store.Load();
        // Tożsamością dokumentu jest jego odcisk: TimingConfig niesie tablicę (reconnectBackoffMs), więc
        // dwa wczytania dają rekordy równe co do wartości, ale nie równe jako obiekty.
        Assert.Equal(saved.Hash, loaded!.Hash);
        Assert.Equal(30000, loaded.Timing!.AlertSoundRepeatMs);
        Assert.Equal(OverlayPosition.BottomRight, loaded.Overlay!.Position);
        Assert.Equal("PC-A", loaded.EditedBy);

        // Róg nakładki ma w pliku wyglądać dokładnie jak w config.json — z myślnikiem, nie camelCase.
        string json = File.ReadAllText(Store.Path);
        Assert.Contains("\"position\": \"bottom-right\"", json);
        Assert.Contains("\"version\": 1", json);
    }

    [Fact]
    public void EmptyDocumentIsSavedToo()
    {
        var saved = Store.Save(null, null, Noon, "PC-A");

        Assert.True(saved.IsEmpty);
        Assert.Equal(saved.Hash, Store.Load()!.Hash);
    }

    [Fact]
    public void MergeOfAnOlderDocumentChangesNothing()
    {
        Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon, "PC-A");
        string before = File.ReadAllText(Store.Path);

        bool changed = Store.Merge(new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 90000 },
            EditedAt = Noon.AddMinutes(-5),
            EditedBy = "PC-B",
        });

        Assert.False(changed);
        Assert.Equal(before, File.ReadAllText(Store.Path));
        Assert.Equal(30000, Store.Load()!.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public void MergeOfANewerDocumentWins()
    {
        Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon, "PC-A");

        bool changed = Store.Merge(new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 90000 },
            EditedAt = Noon.AddMinutes(5),
            EditedBy = "PC-B",
        });

        Assert.True(changed);
        var loaded = Store.Load()!;
        Assert.Equal(90000, loaded.Timing!.AlertSoundRepeatMs);
        Assert.Equal("PC-B", loaded.EditedBy);
    }

    [Fact]
    public void MergeOfTheSameDocumentTwiceChangesTheFileOnlyOnce()
    {
        var incoming = new SharedConfigDocument { Overlay = new OverlayConfig { Scale = 1.5 }, EditedAt = Noon, EditedBy = "PC-B" };

        Assert.True(Store.Merge(incoming));
        Assert.False(Store.Merge(incoming));
    }

    [Fact]
    public void MergeRejectsADocumentWithValuesOutsideTheAllowedRange()
    {
        Assert.False(Store.Merge(new SharedConfigDocument
        {
            Overlay = new OverlayConfig { Opacity = 5.0 },
            EditedAt = Noon,
            EditedBy = "PC-B",
        }));
        Assert.Null(Store.Load());
    }

    [Fact]
    public void OwnEditWinsEvenWhenThePartnerClockWasAhead()
    {
        // Dokument z komputera z zegarem do przodu zdążył tu dotrzeć; bez tej reguły zmiana zrobiona
        // w Panelu „nie brałaby się”, bo stare wartości wróciłyby z sieci przy pierwszym uzgodnieniu.
        Store.Merge(new SharedConfigDocument
        {
            Timing = new TimingConfig { AlertSoundRepeatMs = 90000 },
            EditedAt = Noon.AddHours(1),
            EditedBy = "PC-B",
        });

        var saved = Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon, "PC-A");

        Assert.True(saved.EditedAt > Noon.AddHours(1));
        Assert.Equal(saved.Hash, Store.Load()!.Hash);
        Assert.Equal(30000, Store.Load()!.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public void UnknownFieldFromAnotherComputerIsIgnoredOnLoad()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path,
            "{\"version\": 1, \"document\": {\"nieznanePole\": 1.0, \"timing\": {\"alertSoundRepeatMs\": 30000}, "
            + "\"editedAt\": \"2026-10-02T12:00:00+00:00\", \"editedBy\": \"PC-B\"}}");
        var local = new AppConfig { Enabled = false };

        var loaded = Store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(30000, loaded!.ApplyTo(local).Timing.AlertSoundRepeatMs);
        Assert.Equal(local.Ui, loaded.ApplyTo(local).Ui);
        Assert.Equal(new SharedConfigDocument { Timing = loaded.Timing, EditedAt = loaded.EditedAt, EditedBy = loaded.EditedBy }.Hash, loaded.Hash);
    }


    private static UpdateApproval Approval(string version = "0.4.26.0", string host = "PC-A") => new()
    {
        Version = version, FileName = $"MaszynyAlert-Setup-{version}.exe", ApprovedBy = host, ApprovedAt = Noon,
    };

    [Fact]
    public void SavingTheConfigurationInThePanelKeepsTheApproval()
    {
        Store.SaveApproval(Approval(), Noon, "PC-A");

        var saved = Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, null, Noon.AddMinutes(1), "PC-A");

        Assert.Equal("0.4.26.0", saved.UpdateApproval!.Version);
        Assert.Equal("0.4.26.0", Store.Load()!.UpdateApproval!.Version);
        Assert.Equal(30000, Store.Load()!.Timing!.AlertSoundRepeatMs);
    }

    [Fact]
    public void SavingTheApprovalKeepsTheConfiguration()
    {
        Store.Save(new TimingConfig { AlertSoundRepeatMs = 30000 }, new OverlayConfig { Scale = 1.5 }, Noon, "PC-A");

        var saved = Store.SaveApproval(Approval(), Noon.AddMinutes(1), "PC-A");

        var loaded = Store.Load()!;
        Assert.Equal(saved.Hash, loaded.Hash);
        Assert.Equal(30000, loaded.Timing!.AlertSoundRepeatMs);
        Assert.Equal(1.5, loaded.Overlay!.Scale);
        Assert.Equal("0.4.26.0", loaded.UpdateApproval!.Version);
        Assert.Contains("\"updateApproval\"", File.ReadAllText(Store.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingTheApprovalOnAnEmptyStoreGivesADocumentWithOnlyTheApproval()
    {
        var saved = Store.SaveApproval(Approval(), Noon, "PC-A");

        Assert.True(saved.IsEmpty);
        Assert.Null(saved.Timing);
        Assert.Equal(saved.Hash, Store.Load()!.Hash);
    }

    [Fact]
    public void SavingAnInvalidApprovalIsRefusedAndLeavesTheFileAlone()
    {
        Assert.Throws<ArgumentException>(() => Store.SaveApproval(Approval() with { FileName = "../x.exe" }, Noon, "PC-A"));
        Assert.Null(Store.Load());
    }

    [Fact]
    public void ApprovalOwnSaveWinsEvenWhenThePartnerClockWasAhead()
    {
        Store.Merge(new SharedConfigDocument { Overlay = new OverlayConfig { Scale = 1.5 }, EditedAt = Noon.AddHours(1), EditedBy = "PC-B" });

        var saved = Store.SaveApproval(Approval(), Noon, "PC-A");

        Assert.True(saved.EditedAt > Noon.AddHours(1));
        Assert.Equal(1.5, Store.Load()!.Overlay!.Scale);
    }

    [Fact]
    public void NewerApprovalFromTheNetworkWinsAndOlderDoesNot()
    {
        Store.SaveApproval(Approval("0.4.26.0"), Noon, "PC-A");

        Assert.False(Store.Merge(new SharedConfigDocument { UpdateApproval = Approval("0.4.25.0", "PC-B"), EditedAt = Noon.AddMinutes(-1), EditedBy = "PC-B" }));
        Assert.Equal("0.4.26.0", Store.Load()!.UpdateApproval!.Version);

        Assert.True(Store.Merge(new SharedConfigDocument { UpdateApproval = Approval("0.4.27.0", "PC-B"), EditedAt = Noon.AddMinutes(1), EditedBy = "PC-B" }));
        Assert.Equal("0.4.27.0", Store.Load()!.UpdateApproval!.Version);
    }

    [Fact]
    public void MergeRejectsADocumentWithAnInvalidApproval()
    {
        Assert.False(Store.Merge(new SharedConfigDocument
        {
            UpdateApproval = Approval() with { ApprovedBy = "" }, EditedAt = Noon, EditedBy = "PC-B",
        }));
        Assert.Null(Store.Load());
    }

    [Theory]
    [InlineData("{ to nie jest JSON")]
    [InlineData("{\"version\": 1}")]
    [InlineData("{\"version\": 99, \"document\": {\"editedAt\": \"2026-09-18T12:00:00+00:00\", \"editedBy\": \"PC-A\"}}")]
    [InlineData("{\"version\": 1, \"document\": {\"editedAt\": \"2026-09-18T12:00:00+00:00\", \"editedBy\": \"\"}}")]
    [InlineData("{\"version\": 1, \"document\": {\"editedAt\": \"2026-09-18T12:00:00+00:00\", \"editedBy\": \"PC-A\", \"overlay\": {\"scale\": 9}}}")]
    public void BrokenOrForeignFileIsRefusedInsteadOfSilentlyIgnored(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path, json);

        Assert.Throws<InvalidDataException>(() => Store.Load());
    }

    [Fact]
    public void SaveWhileTheLockIsBrieflyHeldElsewhere_WaitsInsteadOfFailing()
    {
        Directory.CreateDirectory(_directory);
        var held = new FileStream(Store.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Osobny wątek, nie pula: przy równoległych testach na runnerze CI kontynuacja Task.Delay potrafiła nie
        // dostać wątku w 2 s.
        var release = new Thread(() => { Thread.Sleep(300); held.Dispose(); }) { IsBackground = true };
        release.Start();

        Exception? ex = Record.Exception(() => Store.Save(new TimingConfig(), null, Noon, "PC-A"));
        release.Join();

        Assert.Null(ex);
        Assert.NotNull(Store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
