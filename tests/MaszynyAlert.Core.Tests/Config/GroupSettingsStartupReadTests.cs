using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Config;

public sealed class GroupSettingsStartupReadTests
{
    private const string JournalWithGeneration =
        "{\"version\":1,\"number\":1,\"current\":\"11111111-1111-1111-1111-111111111111\",\"pending\":null}";

    [Fact]
    public void BrakPlikuZatwierdzonejGeneracji_DajeInvalidDataException()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "journal.json"), JournalWithGeneration);
        var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());

        Exception? ex = Record.Exception(() => store.ReadForStartup());

        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("generation-11111111111111111111111111111111.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DziennikNiedostepny_DajeInvalidDataException()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "journal.json"));
        var store = new GroupSettingsStore(dir.Path, new InMemorySecretStore());

        Exception? ex = Record.Exception(() => store.ReadForStartup());

        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void BrakKataloguGrupy_ToBrakGrupy()
    {
        using var dir = new TempDir();
        var store = new GroupSettingsStore(Path.Combine(dir.Path, "nie-ma"), new InMemorySecretStore());

        Assert.Null(store.ReadForStartup().Data);
    }
}
