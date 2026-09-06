using Behaviour.Managers;
using Source.Galaxy;
using Source.Player;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class TravelNativeBindingsTests
{
    [Fact]
    public void SnapshotUsesOpaqueIdentityWithoutInvokingLazyNameGeneration()
    {
        var bindings = new TravelNativeBindings(typeof(GamePlayer).Assembly);
        var system = new SystemMapData { guid = "system" };
        var poi = new MapPointOfInterest { guid = "poi", system = system };
        var player = new GamePlayer { currentSystem = system, currentPointOfInterest = poi, elapsedTime = 7 };
        var snapshot = bindings.CurrentLocation(player)!;
        Assert.Equal("system", snapshot.SystemId); Assert.Equal("poi", snapshot.PoiId);
        Assert.Null(snapshot.SystemName); Assert.Null(snapshot.PoiName);
        Assert.Equal(0, system.NameReads); Assert.Equal(0, poi.NameReads); Assert.Equal(7d, bindings.Time(player));
        Assert.Equal("system", bindings.Destination(poi)!.SystemId);
    }

    [Fact]
    public void ReadinessRequiresExactLocalPoiNotJustSomeInitializedManager()
    {
        var bindings = new TravelNativeBindings(typeof(GamePlayer).Assembly);
        var actual = new MapPointOfInterest(); var other = new MapPointOfInterest();
        var local = new TestPoiManager { poi = actual };
        Assert.False(bindings.Ready(local, actual)); local.initializedAndReady = true;
        Assert.True(bindings.Ready(local, actual)); Assert.False(bindings.Ready(local, other)); Assert.False(bindings.Ready(local, null));
        local.poi = null; Assert.True(bindings.Ready(local, null));
        var travel = new TravelManager { localPoiManager = local, localTarget = actual, targetPoi = other };
        Assert.Same(local, bindings.LocalManager(travel)); Assert.Same(actual, bindings.LocalTarget(travel)); Assert.Same(other, bindings.Target(travel));
    }

    [Fact]
    public void MissingSystemIsUnknownAndEmptySpaceHasNoPoiIdentity()
    {
        var bindings = new TravelNativeBindings(typeof(GamePlayer).Assembly); var player = new GamePlayer();
        Assert.Null(bindings.CurrentLocation(player));
        player.currentSystem = new SystemMapData { guid = "system", name = "Known" };
        var location = bindings.CurrentLocation(player)!;
        Assert.Null(location.PoiId); Assert.Null(location.PoiName); Assert.Equal("Known", location.SystemName);
        player.currentPointOfInterest = new MapPointOfInterest { system = new SystemMapData() };
        Assert.Null(bindings.CurrentLocation(player));
    }
}
