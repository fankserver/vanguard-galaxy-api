using System;
using System.Collections.Generic;
using Behaviour.Managers;
using Source.Galaxy;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class AmbientTrafficRuntimeTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly AmbientTrafficService _service;
    private readonly AmbientTrafficRuntime _runtime;
    private readonly List<Exception> _errors = new();
    private readonly SystemMapData _pocket = new() { guid = "pocket" }, _neighbour = new() { guid = "omega" };
    private readonly MapPointOfInterest _station, _pocketGate, _peerGate, _hubStation;

    public AmbientTrafficRuntimeTests()
    {
        _service = new AmbientTrafficService(_hub); _service.SetAvailable(true);
        _runtime = new AmbientTrafficRuntime(typeof(MapElement).Assembly, _service, _errors.Add);
        _station = Poi("authored-station", _pocket);
        _pocketGate = Poi("pocket-gate", _pocket);
        _peerGate = Poi("peer-gate", _neighbour);
        _hubStation = Poi("hub-station", _neighbour);
        GalaxyMapData.current = new GalaxyMapData();
        foreach (var poi in new[] { _station, _pocketGate, _peerGate, _hubStation }) GalaxyMapData.current.AddPoi(poi);
    }
    private static MapPointOfInterest Poi(string guid, SystemMapData system)
    {
        var poi = new MapPointOfInterest { guid = guid, system = system };
        system.pointsOfInterest.Add(poi);
        return poi;
    }
    private static TestPoiManager Manager(MapPointOfInterest? poi) => new() { poi = poi };

    [Fact]
    public void StationDeclarationSuppressesOnlyThatStationsVisitorSpawner()
    {
        using var declaration = _service.SuppressAtStation("authored-station");
        Assert.True(_runtime.SuppressStationVisitor(Manager(_station)));
        Assert.False(_runtime.SuppressStationVisitor(Manager(_hubStation)));
        Assert.False(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        Assert.False(_runtime.SuppressGateTraffic(Manager(_peerGate)));
        Assert.Empty(_errors);
    }

    [Fact]
    public void SystemDeclarationQuietsPocketStationAndGateWhilePeerGateStaysBusy()
    {
        using var declaration = _service.SuppressInSystemContaining("authored-station");
        Assert.True(_runtime.SuppressStationVisitor(Manager(_station)));
        Assert.True(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        Assert.False(_runtime.SuppressGateTraffic(Manager(_peerGate)));
        Assert.False(_runtime.SuppressStationVisitor(Manager(_hubStation)));
        Assert.Empty(_errors);
    }

    [Fact]
    public void DeclarationMadeBeforeTheAnchorExistsBindsWhenTheAnchorAppears()
    {
        using var declaration = _service.SuppressInSystemContaining("late-station");
        Assert.False(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        GalaxyMapData.current!.AddPoi(Poi("late-station", _pocket));
        Assert.True(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        Assert.True(_runtime.SuppressStationVisitor(Manager(_station)));
        Assert.False(_runtime.SuppressGateTraffic(Manager(_peerGate)));
    }

    [Fact]
    public void ReloadRebindsAgainstTheReplacementMapWithoutConsumerAction()
    {
        using var declaration = _service.SuppressInSystemContaining("authored-station");
        Assert.True(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        // A reload that rebuilds the pocket produces new native objects for the same identities.
        var rebuiltSystem = new SystemMapData { guid = "pocket-rebuilt" };
        var rebuiltStation = Poi("authored-station", rebuiltSystem);
        var rebuiltGate = Poi("pocket-gate", rebuiltSystem);
        GalaxyMapData.current = new GalaxyMapData();
        GalaxyMapData.current.AddPoi(rebuiltStation); GalaxyMapData.current.AddPoi(rebuiltGate);
        Assert.True(_runtime.SuppressGateTraffic(Manager(rebuiltGate)));
        Assert.True(_runtime.SuppressStationVisitor(Manager(rebuiltStation)));
        // A save without the authored pocket keeps the whole galaxy vanilla.
        GalaxyMapData.current = new GalaxyMapData();
        var vanillaGate = Poi("pocket-gate", new SystemMapData { guid = "pocket" });
        GalaxyMapData.current.AddPoi(vanillaGate);
        Assert.False(_runtime.SuppressGateTraffic(Manager(vanillaGate)));
    }

    [Fact]
    public void MissingMapMissingPoiOrDuplicateAnchorIdentityFailsOpen()
    {
        using var declaration = _service.SuppressInSystemContaining("authored-station");
        Assert.False(_runtime.SuppressStationVisitor(Manager(null)));
        Assert.False(_runtime.SuppressStationVisitor(null));
        Assert.False(_runtime.SuppressStationVisitor(new object()));
        GalaxyMapData.current!.AddPoi(new MapPointOfInterest { guid = "authored-station", system = _neighbour });
        Assert.False(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        GalaxyMapData.current = null;
        Assert.False(_runtime.SuppressGateTraffic(Manager(_pocketGate)));
        Assert.Empty(_errors);
    }

    [Fact]
    public void PatchEntryPointsSkipOnlyTheDeclaredDecorativeSpawn()
    {
        AmbientTrafficPatches.Runtime = _runtime;
        using var declaration = _service.SuppressInSystemContaining("authored-station");
        var result = true;
        Assert.False(AmbientTrafficPatches.StationVisitor.Prefix(Manager(_station), ref result));
        Assert.False(result);
        result = true;
        Assert.True(AmbientTrafficPatches.StationVisitor.Prefix(Manager(_hubStation), ref result));
        Assert.True(result);
        Assert.False(AmbientTrafficPatches.GateTraffic.Prefix(Manager(_pocketGate)));
        Assert.True(AmbientTrafficPatches.GateTraffic.Prefix(Manager(_peerGate)));
        AmbientTrafficPatches.Runtime = null;
        result = true;
        Assert.True(AmbientTrafficPatches.StationVisitor.Prefix(Manager(_station), ref result));
        Assert.True(AmbientTrafficPatches.GateTraffic.Prefix(Manager(_pocketGate)));
    }

    [Fact]
    public void ForeignThreadSpawnFaultsAreContainedReportedOnceAndFailOpen()
    {
        using var declaration = _service.SuppressAtStation("authored-station");
        var suppressed = new List<bool>();
        // A decorative spawn driven off the main thread must proceed as vanilla, not crash the spawner.
        Assert.Null(ServiceNotificationTests.OnWorker(() =>
        {
            suppressed.Add(_runtime.SuppressStationVisitor(Manager(_station)));
            suppressed.Add(_runtime.SuppressStationVisitor(Manager(_station)));
        }));
        Assert.Equal(new[] { false, false }, suppressed);
        Assert.Single(_errors);
        Assert.True(_runtime.SuppressStationVisitor(Manager(_station)));
    }

    public void Dispose()
    {
        AmbientTrafficPatches.Runtime = null;
        GalaxyMapData.current = null;
        _service.Dispose(); _hub.Dispose();
    }
}
