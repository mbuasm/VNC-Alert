using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MaszynyAlert.App.ViewModels;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.Tests;

public sealed class CollectionSyncTests
{
    private sealed record Item(string Key, string Text);

    private static (ObservableCollection<T> Collection, List<NotifyCollectionChangedAction> Events) Observed<T>(params T[] items)
    {
        var collection = new ObservableCollection<T>(items);
        var events = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => events.Add(e.Action);
        return (collection, events);
    }

    private static Item Keep(Item existing, Item fresh) => existing.Text == fresh.Text ? existing : fresh;

    [Fact]
    public void BezZmianNieMaZadnegoZdarzeniaIZostajaTeSameObiekty()
    {
        Item a = new("a", "1"), b = new("b", "2");
        var (collection, events) = Observed(a, b);

        CollectionSync.SyncByKey(collection, [new Item("a", "1"), new Item("b", "2")], i => i.Key, Keep);

        Assert.Empty(events);
        Assert.Same(a, collection[0]);
        Assert.Same(b, collection[1]);
    }

    [Fact]
    public void ZmienionaPozycjaJestPodmienianaWSwoimMiejscuBezReset()
    {
        Item a = new("a", "1"), c = new("c", "3");
        var (collection, events) = Observed(a, new Item("b", "2"), c);

        CollectionSync.SyncByKey(collection, [new Item("a", "1"), new Item("b", "2*"), new Item("c", "3")], i => i.Key, Keep);

        Assert.Equal([NotifyCollectionChangedAction.Replace], events);
        Assert.Same(a, collection[0]);
        Assert.Equal("2*", collection[1].Text);
        Assert.Same(c, collection[2]);
    }

    [Fact]
    public void NoweWstawiaZnikniteUsuwaKolejnoscJakWZrodle()
    {
        Item b = new("b", "2"), c = new("c", "3");
        var (collection, events) = Observed(new Item("a", "1"), b, c);

        CollectionSync.SyncByKey(collection, [new Item("c", "3"), new Item("d", "4"), new Item("b", "2")], i => i.Key, Keep);

        Assert.Equal(["c", "d", "b"], collection.Select(i => i.Key));
        Assert.Same(b, collection[2]);
        Assert.Same(c, collection[0]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
    }

    [Fact]
    public void LogDopisujeNaKoncuIZdejmujeNajstarsze()
    {
        var (collection, events) = Observed("l1", "l2", "l3", "l4");

        CollectionSync.SyncLines(collection, ["l3", "l4", "l5", "l6"]);

        Assert.Equal(["l3", "l4", "l5", "l6"], collection);
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add],
            events);
    }

    [Fact]
    public void LogRosnieDoLimituTylkoDopisujac()
    {
        var (collection, events) = Observed("l1", "l2");

        CollectionSync.SyncLines(collection, ["l1", "l2", "l3"]);

        Assert.Equal(["l1", "l2", "l3"], collection);
        Assert.Equal([NotifyCollectionChangedAction.Add], events);
    }

    [Fact]
    public void HistoriaNajnowszeNaGorzeWstawiaNaPoczatkuIZdejmujeZKonca()
    {
        var (collection, events) = Observed("h3", "h2", "h1");

        CollectionSync.SyncLines(collection, ["h4", "h3", "h2"]);

        Assert.Equal(["h4", "h3", "h2"], collection);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], events);
    }

    [Fact]
    public void HistoriaRosnieDoLimituTylkoWstawiajac()
    {
        var (collection, events) = Observed("h2", "h1");

        CollectionSync.SyncLines(collection, ["h3", "h2", "h1"]);

        Assert.Equal(["h3", "h2", "h1"], collection);
        Assert.Equal([NotifyCollectionChangedAction.Add], events);
    }

    [Fact]
    public void LinieBezWspolnejCzesciPodmienianeNaMiejscu()
    {
        var (collection, events) = Observed("a", "b", "c");

        CollectionSync.SyncLines(collection, ["x", "b"]);

        Assert.Equal(["x", "b"], collection);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
    }

    [Fact]
    public void LinieBezZmianBezZdarzen()
    {
        var (collection, events) = Observed("a", "b");

        CollectionSync.SyncLines(collection, ["a", "b"]);

        Assert.Empty(events);
    }

    [Fact]
    public void WierszMaszynyAktualizujeTylkoZmienioneKomorki()
    {
        var snapshot = new MachineStatusSnapshot
        {
            MachineId = "m1", Name = "Aidite 1", Host = "10.0.0.1", Port = 9100, Profile = "opencnc",
            State = MachineConnectionState.Connected, Source = "vnc", LastFrameAt = DateTimeOffset.Now, LastRedFraction = 0.01,
        };
        var row = new MachineRowViewModel(snapshot);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.CopyFrom(new MachineRowViewModel(snapshot with { LastRedFraction = 0.05 }));

        Assert.Equal([nameof(MachineRowViewModel.RedPercentText)], changed);
        Assert.Equal("5.0 %", row.RedPercentText);
    }

    [Fact]
    public void SynchronizacjaMaszynZostawiaTeSameWiersze()
    {
        var snapshot = new MachineStatusSnapshot { MachineId = "m1", Name = "Aidite 1", Host = "10.0.0.1", Profile = "opencnc", Source = "vnc" };
        var rows = new ObservableCollection<MachineRowViewModel>();
        StatusWindowViewModel.SyncMachines(rows, [new MachineRowViewModel(snapshot)]);
        MachineRowViewModel first = rows[0];
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        StatusWindowViewModel.SyncMachines(rows, [new MachineRowViewModel(snapshot with { LastError = "Nie udało się połączyć" })]);

        Assert.Empty(events);
        Assert.Same(first, rows[0]);
        Assert.Equal("Nie udało się połączyć", rows[0].LastError);
    }
}
