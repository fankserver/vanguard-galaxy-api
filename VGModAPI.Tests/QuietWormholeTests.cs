using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// A pair declared quiet must actually quiet both owned ends through the ambient-traffic service and
/// release that quieting when the pair dissolves. This exercises the whole wiring (definition ->
/// declaration -> handle -> service), which unit tests of either half cannot catch.
/// </summary>
public sealed class QuietWormholeTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, e) => throw e);
        internal readonly FakeWormholePairs Native = new();
        internal readonly AmbientTrafficService Ambient;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly WormholePairRegistry Definitions;
        internal readonly WormholePairCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal readonly IWorldProvider Provider;
        internal Guid Session;
        internal Harness()
        {
            var plugin = new object();
            StoryHostAuthenticator auth = (o, a) => ReferenceEquals(o, plugin) ? new StoryHostPlugin("author.a", a) : null;
            Combat = new(auth, Hub.CheckThread); Definitions = new(auth, Hub.CheckThread);
            Coordinator = new(Hub, Definitions, Native, _ => true, _ => { });
            Ambient = new AmbientTrafficService(Hub);
            Ambient.SetAvailable(true);
            Service = new WorldContentService(Hub, Combat, null!, () => true, null, Ambient, null, null,
                null, null, null, null, null, null, null, Definitions, Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void Begin() { Session = Hub.Begin(SessionOrigin.NewGame, null); Hub.PlayerReady(Session); Hub.GameplayInitialized(Session); }
        public void Dispose() { Provider.Dispose(); Coordinator.Dispose(); Service.Dispose(); Definitions.Dispose(); Combat.Dispose(); Hub.Dispose(); }
    }

    private static string? NoAnchor(string _) => null;

    [Fact]
    public void QuietPocketSystemSilencesTheWholeAuthoredSystem()
    {
        var hub = new LifecycleHub((_, e) => throw e);
        var native = new FakePocketSystemNative();
        var plugin = new object();
        StoryHostAuthenticator auth = (o, a) => ReferenceEquals(o, plugin) ? new StoryHostPlugin("author.a", a) : null;
        var combat = new WorldDefinitionRegistry(auth, hub.CheckThread);
        var systems = new PocketSystemRegistry(auth, hub.CheckThread);
        var coordinator = new PocketSystemCoordinator(hub, systems, native, () => true, _ => true, _ => { });
        var ambient = new AmbientTrafficService(hub);
        ambient.SetAvailable(true);
        using var service = new WorldContentService(hub, combat, null!, () => true, null, ambient, null, null, systems, coordinator);
        using var provider = service.AcquireProvider(plugin)!;
        Assert.Equal(WorldStatus.Succeeded, provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Quiet Cluster", PocketSystemPlacement.Visible, factionId: null, sectorName: null, quiet: true)));
        var session = hub.Begin(SessionOrigin.NewGame, null);
        hub.PlayerReady(session); hub.GameplayInitialized(session);
        var pocket = provider.CreatePocketSystem("p", "k", "anchor")!;
        var systemId = pocket.SystemId!;
        string? Resolve(string anchor) => anchor == systemId ? systemId : null;

        // The authored system is silent: nothing spawns at its gates or wormholes, and no patrol.
        Assert.True(ambient.ShouldSuppress(AmbientSpawnSite.JumpGate, "cluster-gate", systemId, Resolve));
        Assert.True(ambient.ShouldSuppress(AmbientSpawnSite.Wormhole, "cluster-rift", systemId, Resolve));
        Assert.True(ambient.ShouldSuppressPatrol("cluster-gate", systemId, Resolve));
        // Vanilla elsewhere is untouched.
        Assert.False(ambient.ShouldSuppress(AmbientSpawnSite.JumpGate, "vanilla-gate", "other", Resolve));

        Assert.True(pocket.Dissolve().Succeeded);
        Assert.False(ambient.ShouldSuppress(AmbientSpawnSite.JumpGate, "cluster-gate", systemId, Resolve));
    }

    [Fact]
    public void QuietPairQuietsBothEndsAndDissolveReleasesTheQuieting()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterWormholePair(
            new WormholePairDefinition("rift", 1, "Rift", quiet: true)));
        harness.Begin();

        var pair = harness.Provider.CreateWormholePair("rift", "k1", "sys-a", "sys-b");
        Assert.NotNull(pair);
        var first = pair!.FirstWormholePoiId;
        var second = pair.SecondWormholePoiId;
        Assert.NotNull(first);
        Assert.NotNull(second);

        // Both ends stop decorative traffic and the patrol that would otherwise spawn there.
        Assert.True(harness.Ambient.ShouldSuppress(AmbientSpawnSite.Wormhole, first, "sys-a", NoAnchor));
        Assert.True(harness.Ambient.ShouldSuppress(AmbientSpawnSite.Wormhole, second, "sys-b", NoAnchor));
        Assert.True(harness.Ambient.ShouldSuppressPatrol(first, null, NoAnchor));
        Assert.True(harness.Ambient.ShouldSuppressPatrol(second, null, NoAnchor));
        // Nothing else about those systems changes.
        Assert.False(harness.Ambient.ShouldSuppress(AmbientSpawnSite.Station, "some-station", "sys-a", NoAnchor));

        Assert.True(pair.Dissolve().Succeeded);
        Assert.False(harness.Ambient.ShouldSuppress(AmbientSpawnSite.Wormhole, first, "sys-a", NoAnchor));
        Assert.False(harness.Ambient.ShouldSuppressPatrol(second, null, NoAnchor));
    }

    [Fact]
    public void AnOrdinaryPairLeavesVanillaTrafficAlone()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterWormholePair(
            new WormholePairDefinition("rift", 1, "Rift")));
        harness.Begin();

        var pair = harness.Provider.CreateWormholePair("rift", "k1", "sys-a", "sys-b");
        Assert.NotNull(pair);
        Assert.False(harness.Ambient.ShouldSuppress(AmbientSpawnSite.Wormhole, pair!.FirstWormholePoiId, "sys-a", NoAnchor));
        Assert.False(harness.Ambient.ShouldSuppressPatrol(pair.FirstWormholePoiId, null, NoAnchor));
    }
}
