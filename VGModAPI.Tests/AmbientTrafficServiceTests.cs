using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class AmbientTrafficServiceTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly AmbientTrafficService _service;
    public AmbientTrafficServiceTests()
    {
        _service = new AmbientTrafficService(_hub);
        _service.SetAvailable(true);
    }
    private static string? NoAnchor(string _) => null;

    [Fact]
    public void WormholeDeclarationFullyQuietsThatWormholeOnly()
    {
        IAmbientTrafficService api = _service;
        using var declaration = api.SuppressAtWormhole("rift-a");
        // Traffic through this rift stops, and so does the security patrol it would otherwise spawn.
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Wormhole, "rift-a", "sys-1", NoAnchor));
        Assert.True(_service.ShouldSuppressPatrol("rift-a", null, NoAnchor));
        // A different wormhole, gates and stations are untouched.
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Wormhole, "rift-b", "sys-1", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "rift-a", "sys-1", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "rift-a", "sys-1", NoAnchor));
        Assert.False(_service.ShouldSuppressPatrol("rift-b", null, NoAnchor));
    }

    [Fact]
    public void SystemDeclarationAlsoQuietsWormholeTrafficButNotItsPatrol()
    {
        using var declaration = _service.SuppressInSystemContaining("anchor-station");
        string? Resolve(string anchor) => anchor == "anchor-station" ? "pocket" : null;
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Wormhole, "pocket-rift", "pocket", Resolve));
        // Security presence in a whole system is deliberately left alone; only a quieted wormhole strips it.
        Assert.False(_service.ShouldSuppressPatrol("pocket-rift", null, NoAnchor));
    }

    [Fact]
    public void WormholeQuietingDisposesCleanly()
    {
        var declaration = _service.SuppressAtWormhole("rift-a");
        Assert.True(_service.ShouldSuppressPatrol("rift-a", null, NoAnchor));
        declaration.Dispose(); declaration.Dispose();
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Wormhole, "rift-a", "sys-1", NoAnchor));
        Assert.False(_service.ShouldSuppressPatrol("rift-a", null, NoAnchor));
    }

    [Fact]
    public void SystemDeclarationCanSilencePatrolsTooForAnAuthoredCluster()
    {
        string? Resolve(string anchor) => anchor == "cluster-system" ? "cluster-system" : null;
        // Without the flag, a quiet system still keeps security presence (existing behaviour).
        using var sites = _service.SuppressInSystemContaining("cluster-system");
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "cluster-gate", "cluster-system", Resolve));
        Assert.False(_service.ShouldSuppressPatrol("cluster-gate", "cluster-system", Resolve));
        sites.Dispose();
        // With it, the whole authored system goes silent, patrols included.
        using var everything = _service.SuppressInSystemContaining("cluster-system", includeSecurityPatrols: true);
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "cluster-gate", "cluster-system", Resolve));
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Wormhole, "cluster-rift", "cluster-system", Resolve));
        Assert.True(_service.ShouldSuppressPatrol("cluster-gate", "cluster-system", Resolve));
        Assert.False(_service.ShouldSuppressPatrol("elsewhere-gate", "other-system", Resolve));
    }

    [Fact]
    public void StationDeclarationQuietsExactlyThatStationSpawner()
    {
        IAmbientTrafficService api = _service;
        using var declaration = api.SuppressAtStation("station-a");
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys-1", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-b", "sys-1", NoAnchor));
        // The same system's gate and even an identically named gate site stay vanilla.
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "station-a", "sys-1", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "gate-1", "sys-1", NoAnchor));
    }

    [Fact]
    public void SystemDeclarationQuietsStationsAndGatesOnlyInTheResolvedSystem()
    {
        using var declaration = _service.SuppressInSystemContaining("anchor-station");
        string? Resolve(string anchor) => anchor == "anchor-station" ? "pocket" : null;
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Station, "anchor-station", "pocket", Resolve));
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Station, "other-station", "pocket", Resolve));
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "pocket-gate", "pocket", Resolve));
        // The peer gate one jump away is in the neighbouring system and stays busy.
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "peer-gate", "omega-norir", Resolve));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "hub-station", "omega-norir", Resolve));
    }

    [Fact]
    public void UnresolvedOrAmbiguousAnchorsFailOpenEverywhere()
    {
        using var declaration = _service.SuppressInSystemContaining("missing");
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "any", "sys", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.JumpGate, "any", "sys", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, null, "sys", NoAnchor));
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "any", null, _ => "sys"));
    }

    [Fact]
    public void DisposalIsPerDeclarationIdempotentAndIndependentBetweenConsumers()
    {
        var one = _service.SuppressAtStation("station-a");
        var two = _service.SuppressAtStation("station-a");
        var other = _service.SuppressInSystemContaining("station-a");
        string? Resolve(string _) => "sys-1";
        one.Dispose(); one.Dispose();
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys-1", Resolve));
        two.Dispose();
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys-1", Resolve));
        other.Dispose();
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys-1", Resolve));
    }

    [Fact]
    public void UnavailableIntegrationIsInertWithoutDiscardingDeclarations()
    {
        using var declaration = _service.SuppressAtStation("station-a");
        _service.SetAvailable(false);
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys", NoAnchor));
        _service.SetAvailable(true);
        Assert.True(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys", NoAnchor));
    }

    [Fact]
    public void InvalidIdentitiesThreadViolationsAndDisposedServiceAreProgrammingErrors()
    {
        Assert.Throws<ArgumentException>(() => _service.SuppressAtStation(" "));
        Assert.Throws<ArgumentException>(() => _service.SuppressInSystemContaining("a\u0007b"));
        Assert.Throws<ArgumentException>(() => _service.SuppressAtStation(new string('x', 5000)));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _service.SuppressAtStation("station-a")));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(
            () => _service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys", NoAnchor)));
        var declaration = _service.SuppressAtStation("station-a");
        _service.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, _service.Availability.Reason);
        Assert.False(_service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", "sys", NoAnchor));
        Assert.Throws<ObjectDisposedException>(() => _service.SuppressAtStation("station-a"));
        declaration.Dispose(); _service.Dispose();
    }

    public void Dispose() { _service.Dispose(); _hub.Dispose(); }
}
