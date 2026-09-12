using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// The game adds first-visit "window dressing" (gun platform, asteroid field, cargo containers, derelict
/// ship) to a wormhole or jump gate that is empty and unvisited. An authored door is created deliberately
/// empty, so it always matches that condition; owned content must therefore be excluded, while everything
/// else keeps vanilla dressing.
/// </summary>
public sealed class OwnedPoiWindowDressingTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, e) => throw e);
        internal readonly FakeWormholePairs Wormholes = new();
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly WormholePairRegistry Definitions;
        internal readonly WormholePairCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal readonly IWorldProvider Provider;
        internal Harness()
        {
            var plugin = new object();
            StoryHostAuthenticator auth = (o, a) => ReferenceEquals(o, plugin) ? new StoryHostPlugin("author.a", a) : null;
            Combat = new(auth, Hub.CheckThread); Definitions = new(auth, Hub.CheckThread);
            Coordinator = new(Hub, Definitions, Wormholes, _ => true, _ => { });
            Service = new(Hub, Combat, null!, () => true, wormholeDefinitions: Definitions, wormholeCoordinator: Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void Begin() { var s = Hub.Begin(SessionOrigin.NewGame, null); Hub.PlayerReady(s); Hub.GameplayInitialized(s); }
        public void Dispose()
        { Provider.Dispose(); Coordinator.Dispose(); Service.Dispose(); Definitions.Dispose(); Combat.Dispose(); Hub.Dispose(); }
    }

    [Fact]
    public void BothEndsOfAnOwnedWormholeSkipTheDressing()
    {
        using var h = new Harness();
        h.Provider.RegisterWormholePair(new("rift", 1, "Cluster Rift"));
        h.Begin();
        var pair = h.Provider.CreateWormholePair("rift", "door", "system-a", "system-b")!;

        Assert.True(h.Service.OwnsUndressedPoi(pair.FirstWormholePoiId, "system-a"));
        Assert.True(h.Service.OwnsUndressedPoi(pair.SecondWormholePoiId, "system-b"));
    }

    [Fact]
    public void UnownedPointsOfInterestKeepVanillaDressing()
    {
        using var h = new Harness();
        h.Provider.RegisterWormholePair(new("rift", 1, "Cluster Rift"));
        h.Begin();
        h.Provider.CreateWormholePair("rift", "door", "system-a", "system-b");

        // A vanilla wormhole in the same system, and any unrelated POI, must still be dressed.
        Assert.False(h.Service.OwnsUndressedPoi("vanilla-wormhole", "system-a"));
        Assert.False(h.Service.OwnsUndressedPoi("some-gate", "unowned-system"));
        Assert.False(h.Service.OwnsUndressedPoi(null, null));
        Assert.False(h.Service.OwnsUndressedPoi("", ""));
    }

    [Fact]
    public void RemovingAnOwnedWormholeReturnsItToVanillaDressing()
    {
        using var h = new Harness();
        h.Provider.RegisterWormholePair(new("rift", 1, "Cluster Rift"));
        h.Begin();
        var pair = h.Provider.CreateWormholePair("rift", "door", "system-a", "system-b")!;
        var firstPoi = pair.FirstWormholePoiId;
        Assert.True(h.Service.OwnsUndressedPoi(firstPoi, "system-a"));

        Assert.True(pair.Remove().Succeeded);

        // Ownership ends with the occurrence: nothing lingers claiming a freed identity.
        Assert.False(h.Service.OwnsUndressedPoi(firstPoi, "system-a"));
    }
}
