using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarNativeWorldTests
{
    public sealed class Patron { public int seat = 1; public bool Owned; }
    public sealed class Bar { public List<Patron> availablePatrons = new(); public long lastUpdateTime = 1; }
    public sealed class Station { public string guid = "station"; public Bar bar = new(); }
    public sealed class Player { public static Player? current; public object? currentPointOfInterest; }
    public sealed class PropertyPlayer
    {
        public static int Calls;
        public static PropertyPlayer current { get { Calls++; return new PropertyPlayer(); } }
        public object? currentPointOfInterest = null;
    }
    private static BarNativeWorld World(Func<object?> current, Func<object, bool>? owned = null)
    {
        Player.current = new Player { currentPointOfInterest = current() };
        return new(typeof(Station), typeof(Bar), typeof(Patron), new BarStationSource(typeof(Player), typeof(Station)),
            owned ?? (value => ((Patron)value).Owned), (_, _) => new Patron(), 5);
    }

    [Fact]
    public void CommitSourceRejectsCallbackPropertiesWithoutInvokingThem()
    {
        PropertyPlayer.Calls = 0;
        Assert.Throws<System.MissingFieldException>(() => new BarStationSource(typeof(PropertyPlayer), typeof(Station)));
        Assert.Equal(0, PropertyPlayer.Calls);
    }

    [Fact]
    public void CapturePreservesVanillaAndAtomicSwapReplacesOldOwnedPresentation()
    {
        var station = new Station(); var vanilla = new Patron(); var owned = new Patron();
        station.bar.availablePatrons.AddRange(new[] { vanilla, owned });
        var world = World(() => station, value => ReferenceEquals(value, owned) || ((Patron)value).Owned);
        var captured = world.Capture("station")!;
        Assert.Same(vanilla, Assert.Single(captured.VanillaPatrons));
        var replacement = new Patron { Owned = true };
        Assert.True(world.Apply(captured, new object[] { vanilla, replacement }, () => true));
        Assert.Equal(new[] { vanilla, replacement }, station.bar.availablePatrons);
        Assert.Equal(1, vanilla.seat);
        Assert.Equal(2, replacement.seat);
        Assert.False(world.Apply(captured, new object[] { vanilla }, () => true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ReentrantChangesRefuseWithoutOverwritingNewNativeState(int change)
    {
        var station = new Station(); var patron = new Patron(); station.bar.availablePatrons.Add(patron);
        object current = station;
        var world = World(() => current);
        var snapshot = world.Capture("station")!;
        Assert.False(world.Apply(snapshot, new object[] { new Patron { Owned = true } }, () =>
        {
            if (change == 0) Player.current!.currentPointOfInterest = new Station();
            else if (change == 1) station.bar.availablePatrons = new List<Patron> { patron };
            else if (change == 2) station.bar.availablePatrons.Add(new Patron());
            else if (change == 3) patron.seat = 3;
            else station.guid = "another-station";
            return true;
        }));
        Assert.Same(patron, station.bar.availablePatrons[0]);
    }

    [Fact]
    public void ExclusivePresentationRetainsVanillaForRevocationButNotAfterNativeRefresh()
    {
        var station = new Station(); var vanilla = new Patron(); station.bar.availablePatrons.Add(vanilla);
        var world = World(() => station);
        Assert.True(world.Apply(world.Capture("station")!, new object[] { new Patron { Owned = true } }, () => true));
        Assert.Same(vanilla, Assert.Single(world.RetainedVanilla(station.bar)!));
        var restore = world.Capture("station")!;
        Assert.Same(vanilla, Assert.Single(restore.VanillaPatrons));
        Assert.True(world.Apply(restore, restore.VanillaPatrons, () => true));
        Assert.Same(vanilla, Assert.Single(station.bar.availablePatrons));
        var fresh = new Patron();
        var refresh = world.BeginNativeRefresh(station.bar);
        station.bar.availablePatrons.Clear(); station.bar.availablePatrons.Add(fresh);
        station.bar.lastUpdateTime++;
        Assert.True(world.CompleteNativeRefresh(refresh, true, true));
        Assert.False(world.CompleteNativeRefresh(refresh, true, true));
        Assert.Null(world.RetainedVanilla(station.bar));
        Assert.Same(fresh, Assert.Single(world.Capture("station")!.VanillaPatrons));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnverifiedMutationRefusesInsteadOfDroppingHiddenVanilla(int mutation)
    {
        var station = new Station(); var vanilla = new Patron(); station.bar.availablePatrons.Add(vanilla);
        var world = World(() => station);
        Assert.True(world.Apply(world.Capture("station")!, new object[] { new Patron { Owned = true } }, () => true));
        if (mutation == 0) station.bar.availablePatrons.Add(new Patron());
        else if (mutation == 1) station.bar.availablePatrons.Clear();
        else station.bar.availablePatrons = new List<Patron>(station.bar.availablePatrons);
        Assert.Throws<InvalidOperationException>(() => world.RetainedVanilla(station.bar));
        Assert.Throws<InvalidOperationException>(() => world.Capture("station"));
        var noop = world.BeginNativeRefresh(station.bar);
        Assert.False(world.CompleteNativeRefresh(noop, true, true));
        Assert.Throws<InvalidOperationException>(() => world.RetainedVanilla(station.bar));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NestedRefreshRequiresSuccessfulOutermostOriginalCompletion(bool ran, bool succeeded)
    {
        var station = new Station(); var vanilla = new Patron(); station.bar.availablePatrons.Add(vanilla);
        var world = World(() => station);
        Assert.True(world.Apply(world.Capture("station")!, new object[] { new Patron { Owned = true } }, () => true));
        var epoch = world.CaptureRefreshEpoch(station.bar);
        var outer = world.BeginNativeRefresh(station.bar);
        var inner = world.BeginNativeRefresh(station.bar);
        station.bar.lastUpdateTime++;
        Assert.False(world.CompleteNativeRefresh(outer, true, true));
        Assert.False(world.CompleteNativeRefresh(inner, true, true));
        Assert.Throws<InvalidOperationException>(() => world.RetainedVanilla(station.bar));
        Assert.Equal(ran && succeeded, world.CompleteNativeRefresh(outer, ran, succeeded));
        Assert.False(world.IsRefreshEpochCurrent(station.bar, epoch));
        if (ran && succeeded) Assert.Null(world.RetainedVanilla(station.bar));
        else Assert.Same(vanilla, Assert.Single(world.RetainedVanilla(station.bar)!));
    }

    [Fact]
    public void ForeignSnapshotsAndDuplicateReferencesAreRefused()
    {
        var station = new Station(); var world = World(() => station); var second = World(() => station);
        var snapshot = world.Capture("station")!; var patron = new Patron();
        Assert.False(second.Apply(snapshot, new object[] { patron }, () => true));
        Assert.False(world.Apply(snapshot, new object[] { patron, patron }, () => true));
        Assert.Empty(station.bar.availablePatrons);
    }
}
