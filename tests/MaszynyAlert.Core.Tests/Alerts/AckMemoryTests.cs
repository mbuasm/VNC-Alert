using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Tests.TestUtil;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AckMemoryTests
{
    private static readonly AlertKey Key = AlertKey.ForRule("aidite1", "tray-grab");

    [Fact]
    public void Record_ZapamietujePotwierdzenieIUdostepniaJePrzezGet()
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        var ack = new AckRecord(Key, "PC-1", "jkowalski", clock.UtcNow);

        memory.Record(ack);

        Assert.True(memory.IsAcked(Key));
        Assert.Equal(ack, memory.Get(Key));
        Assert.Single(memory.All);
    }

    [Fact]
    public void IsAcked_DlaNieznanegoKlucza_ZwracaFalsz()
    {
        var memory = new AckMemory(new FakeClock(), TimeSpan.FromHours(2));

        Assert.False(memory.IsAcked(Key));
        Assert.Null(memory.Get(Key));
        Assert.Empty(memory.All);
    }

    [Fact]
    public void Record_NowszePotwierdzenie_NadpisujeStarsze()
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        memory.Record(new AckRecord(Key, "PC-1", "anna", clock.UtcNow));

        clock.Advance(TimeSpan.FromMinutes(1));
        memory.Record(new AckRecord(Key, "PC-2", "bartek", clock.UtcNow));

        AckRecord? current = memory.Get(Key);
        Assert.NotNull(current);
        Assert.Equal("PC-2", current.Host);
        Assert.Equal("bartek", current.User);
    }

    [Fact]
    public void Record_StarszePotwierdzenie_NieNadpisujeNowszego()
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        DateTimeOffset later = clock.UtcNow.AddMinutes(5);
        memory.Record(new AckRecord(Key, "PC-2", "bartek", later));

        memory.Record(new AckRecord(Key, "PC-1", "anna", clock.UtcNow));

        Assert.Equal("PC-2", memory.Get(Key)!.Host);
    }

    [Fact]
    public void Prune_UsuwaPotwierdzeniaStarszeNizCzasZycia()
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        memory.Record(new AckRecord(Key, "PC-1", "anna", clock.UtcNow));

        clock.Advance(TimeSpan.FromHours(1));
        memory.Prune();
        Assert.True(memory.IsAcked(Key));

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        memory.Prune();
        Assert.False(memory.IsAcked(Key));
        Assert.Empty(memory.All);
    }

    [Fact]
    public void Prune_ZostawiaPotwierdzeniaInnychKluczy()
    {
        var clock = new FakeClock();
        var memory = new AckMemory(clock, TimeSpan.FromHours(2));
        AlertKey stary = AlertKey.ForRule("m1", "a");
        AlertKey nowy = AlertKey.ForRule("m2", "b");

        memory.Record(new AckRecord(stary, "PC-1", "u", clock.UtcNow));
        clock.Advance(TimeSpan.FromHours(3));
        memory.Record(new AckRecord(nowy, "PC-1", "u", clock.UtcNow));

        memory.Prune();

        Assert.False(memory.IsAcked(stary));
        Assert.True(memory.IsAcked(nowy));
    }
}
