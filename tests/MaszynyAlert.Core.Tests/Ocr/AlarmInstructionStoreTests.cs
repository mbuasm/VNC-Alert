using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Ocr;

public sealed class AlarmInstructionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alarm-help-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private AlarmInstructionStore Store => new(Path.Combine(_directory, "instructions.json"));

    private InstructionEntry Save(AlarmInstructionStore store, string key, string? text,
        DateTimeOffset? at = null, string host = "PC-A") => store.Save(key, text, at ?? Noon, host);

    [Fact]
    public void SaveSurvivesReloadPreservesOtherEntriesAndCreatesBackup()
    {
        var first = Store;
        var second = Store;
        Save(first, "code:705", "Pierwszy krok\nDrugi krok: sprawdź komunikat.");
        Save(second, "offline", "Sprawdź zasilanie.");
        Assert.Equal(2, Store.Load().Count);
        Assert.Equal("Pierwszy krok\nDrugi krok: sprawdź komunikat.", Store.Load()["code:705"]);
        Assert.True(File.Exists(Store.Path + ".bak"));
        Save(first, "code:705", null);
        Assert.Equal("Sprawdź zasilanie.", Assert.Single(Store.Load()).Value);
    }

    [Fact]
    public void RestoringTheDefaultLeavesATombstoneSoTheDeletionCanTravel()
    {
        Save(Store, "code:705", "Własna treść");
        Save(Store, "code:705", null);

        // Sama treść znika z pomocy, ale ślad po usunięciu zostaje: bez niego komputer włączony później
        // rozesłałby skasowaną treść z powrotem.
        Assert.Empty(Store.Load());
        var entry = Assert.Single(Store.LoadEntries()).Value;
        Assert.True(entry.Deleted);
        Assert.Equal("PC-A", entry.EditedBy);
    }

    [Fact]
    public void FileInFormatOneIsReadWithTheFileTimeAndThisComputerAsAuthor()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path, "{\"Version\":1,\"Instructions\":{\"code:705\":\"Stara treść\"}}");
        var written = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Store.Path, written);

        var entry = Assert.Single(Store.LoadEntries()).Value;
        Assert.Equal("Stara treść", entry.Text);
        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), entry.EditedAt);
        Assert.Equal(Environment.MachineName, entry.EditedBy);

        // Zapis migruje plik do formatu 2 i nie gubi wcześniejszej treści.
        Save(Store, "offline", "Sprawdź zasilanie.");
        Assert.Equal(["code:705", "offline"], Store.LoadEntries().Keys.Order().ToArray());
        Assert.Contains("\"Version\": 2", File.ReadAllText(Store.Path));
    }

    [Fact]
    public void MergeKeepsTheNewerChangeAndTheHigherHostNameOnATie()
    {
        Save(Store, "code:705", "Z tego komputera", Noon);

        Assert.False(Store.Merge([new InstructionEntry
        { Key = "code:705", Text = "Starsza z sieci", EditedAt = Noon.AddMinutes(-1), EditedBy = "PC-Z" }]));
        Assert.Equal("Z tego komputera", Store.Load()["code:705"]);

        Assert.True(Store.Merge([new InstructionEntry
        { Key = "code:705", Text = "Nowsza z sieci", EditedAt = Noon.AddMinutes(1), EditedBy = "PC-B" }]));
        Assert.Equal("Nowsza z sieci", Store.Load()["code:705"]);

        // Remis czasu rozstrzyga nazwa komputera, tak samo na każdym z nich.
        Assert.False(Store.Merge([new InstructionEntry
        { Key = "code:705", Text = "Remis, mniejszy host", EditedAt = Noon.AddMinutes(1), EditedBy = "PC-A" }]));
        Assert.True(Store.Merge([new InstructionEntry
        { Key = "code:705", Text = "Remis, większy host", EditedAt = Noon.AddMinutes(1), EditedBy = "PC-C" }]));
        Assert.Equal("Remis, większy host", Store.Load()["code:705"]);
    }

    [Fact]
    public void OwnEditWinsOverANewerEntryFromAComputerWithAClockAhead()
    {
        Store.Merge([new InstructionEntry
        { Key = "code:705", Text = "Z komputera z zegarem do przodu", EditedAt = Noon.AddHours(1), EditedBy = "PC-Z" }]);

        var saved = Save(Store, "code:705", "Moja treść", Noon);

        Assert.Equal("Moja treść", Store.Load()["code:705"]);
        Assert.True(saved.EditedAt > Noon.AddHours(1));
    }

    [Fact]
    public void MergeIgnoresEntriesThatCouldNeverBeSavedHere()
    {
        Assert.False(Store.Merge([new InstructionEntry
        { Key = "zly-klucz", Text = "Treść", EditedAt = Noon, EditedBy = "PC-B" }]));
        Assert.False(Store.Merge([new InstructionEntry
        { Key = "code:705", Text = new string('x', AlarmInstructionStore.MaxTextLength + 1), EditedAt = Noon, EditedBy = "PC-B" }]));
        Assert.Empty(Store.LoadEntries());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":3,\"Instructions\":{}}")]
    [InlineData("{\"Version\":1,\"Instructions\":{\"code:705\":null}}")]
    public void CorruptOrNewerDocumentIsNeverOverwritten(string original)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path, original);
        Assert.ThrowsAny<Exception>(() => Save(Store, "code:705", "Nowa treść"));
        Assert.Equal(original, File.ReadAllText(Store.Path));
    }

    [Fact]
    public void FailedWriteAndInvalidInputPreservePreviousInstruction()
    {
        Save(Store, "code:705", "Poprzednia treść");
        Assert.Throws<ArgumentException>(() => Save(Store, "code:705", " "));
        Assert.Throws<ArgumentException>(() => Save(Store, "code:705", new string('x', 4001)));
        using (var gate = new FileStream(Store.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => Save(Store, "code:705", "Nowa treść"));
        Assert.Equal("Poprzednia treść", Store.Load()["code:705"]);
    }

    [Theory]
    [InlineData("rule:code-1200", "code:1200")]
    [InlineData("alarm:1200", "code:1200")]
    [InlineData("rule:home-request", "code:1200")]
    [InlineData("rule:tray-grab", "code:1248")]
    [InlineData("rule:tool-setting-exceeds", "code:1615")]
    [InlineData("rule:custom", "rule:custom")]
    [InlineData("text:unrecognized", "category:inny")]
    [InlineData("rule:amm580-code-1099", "code:amm580-1099")]
    [InlineData("alarm:amm580-1099", "code:amm580-1099")]
    [InlineData("rule:amm580-tool-life", "rule:amm580-tool-life")]
    [InlineData("rule:amm580-code-abc", "rule:amm580-code-abc")]
    public void CodeAndTextFallbackUseConsistentInstructionKeys(string discriminator, string expected)
    {
        var alert = new Alert { Key = new("m", discriminator), MachineId = "m", MachineName = "M", ShortText = "Błąd", Category = "inny", Kind = AlertKind.Error };
        Assert.Equal(expected, AlarmHelp.InstructionKey(alert));
        Assert.Contains("Nie kasuje błędu w maszynie.", AlarmHelp.Format(alert, "Własna instrukcja"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
