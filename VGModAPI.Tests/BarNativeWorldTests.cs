using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarNativeWorldTests
{
    public sealed class Patron { }
    public sealed class Bar { public List<Patron> availablePatrons = new(); }
    public sealed class Station { public string guid = "station"; public Bar bar = new(); }
    private static BarNativeWorld World(Func<object?> current, Func<object, bool>? owned = null) =>
        new(typeof(Station), typeof(Bar), typeof(Patron), current, owned ?? (_ => false), (_, _) => new Patron(), 5);

    [Fact]
    public void CapturePreservesVanillaAndAtomicSwapReplacesOldOwnedPresentation()
    {
        var station = new Station(); var vanilla = new Patron(); var owned = new Patron();
        station.bar.availablePatrons.AddRange(new[] { vanilla, owned });
        var world = World(() => station, value => ReferenceEquals(value, owned));
        var captured = world.Capture("station")!;
        Assert.Same(vanilla, Assert.Single(captured.VanillaPatrons));
        var replacement = new Patron();
        Assert.True(world.Apply(captured, new object[] { vanilla, replacement }, () => true));
        Assert.Equal(new[] { vanilla, replacement }, station.bar.availablePatrons);
        Assert.False(world.Apply(captured, new object[] { vanilla }, () => true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReentrantChangesRefuseWithoutOverwritingNewNativeState(int change)
    {
        var station = new Station(); var patron = new Patron(); station.bar.availablePatrons.Add(patron);
        object current = station;
        var world = World(() => current);
        var snapshot = world.Capture("station")!;
        Assert.False(world.Apply(snapshot, new object[] { new Patron() }, () =>
        {
            if (change == 0) current = new Station();
            else if (change == 1) station.bar.availablePatrons = new List<Patron> { patron };
            else station.bar.availablePatrons.Add(new Patron());
            return true;
        }));
        Assert.Same(patron, station.bar.availablePatrons[0]);
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
