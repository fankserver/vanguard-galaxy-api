using System;
using System.Collections.Generic;
using System.Linq;
using Behaviour.Managers;
using Behaviour.Unit;
using Source.Data;
using Source.Data.Persistable;
using Source.Dungeon;
using Source.Galaxy;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonAegisTests : IDisposable
{
    private readonly List<Exception> _reports = new();
    private readonly List<string> _notices = new();
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly DungeonAegisService _service;
    private readonly DungeonAegisRuntime _runtime;
    private readonly List<CombatStationPart> _liveParts = new();
    private readonly Guid _session;
    private readonly MapPointOfInterest _station = new() { guid = "arc5-station-a" };
    private readonly DungeonLocationData _location = new();
    private double _now;

    public DungeonAegisTests()
    {
        _hub.SetCapability("session-lifecycle", true, "Test binding.");
        _service = new DungeonAegisService(_hub); _service.SetAvailable(true);
        _runtime = new DungeonAegisRuntime(typeof(DungeonLocationData).Assembly, _hub, _service,
            () => _liveParts.Cast<object>().ToArray(), _notices.Add, _reports.Add);
        _session = _hub.Begin(SessionOrigin.SaveLoad, "save"); _hub.PlayerReady(_session);
        _station.AddTestPersistable(_location);
        GalaxyMapData.current = new GalaxyMapData();
        GalaxyMapData.current.AddPoi(_station);
        DungeonManager.SetTestInstance = new DungeonManager();
    }
    private void Advance() { _now += 3; _runtime.Tick(_now); }

    [Fact]
    public void DeclaredTargetIsHardenedIncludingAlreadyLivePartsWhileOthersStayVanilla()
    {
        var other = new MapPointOfInterest { guid = "other-station" };
        var otherLocation = new DungeonLocationData();
        other.AddTestPersistable(otherLocation); GalaxyMapData.current!.AddPoi(other);
        var ours = new CombatStationPart { dungeonLocationData = _location };
        var foreign = new CombatStationPart { dungeonLocationData = otherLocation };
        _liveParts.AddRange(new[] { ours, foreign });
        using var declaration = _service.Declare("mod.a", "arc5-station-a");
        Advance();
        Assert.True(_location.stationIsInvincible);
        Assert.True(ours.isInvincible);
        Assert.False(otherLocation.stationIsInvincible);
        Assert.False(foreign.isInvincible);
        Assert.Empty(_reports);
    }

    [Fact]
    public void BrokenSaveIsRepairedOnceReportedAndLegitimateClearsAreNeverTouched()
    {
        _location.dungeonData = new DungeonData
        {
            dockingDestroyed = true,
            facilityIntegrity = 0.05f,
            simulation = new DungeonSimulation { structuralCollapse = true }
        };
        _location.stationData = new CombatStationData(); // No docking part survives.
        using var declaration = _service.Declare("mod.a", "arc5-station-a");
        Advance();
        Assert.False(_location.dungeonData.dockingDestroyed);
        Assert.Equal(-1f, _location.dungeonData.facilityIntegrity);
        Assert.Null(_location.dungeonData.simulation); // Broken interior regenerates on next entry.
        Assert.Null(_location.stationData); // Dockingless station data regenerates natively.
        Assert.Empty(_reports); // An intended repair is informational, not an error.
        Assert.Single(_notices); Assert.Contains("arc5-station-a", _notices[0]);
        // A legitimately cleared interior is a player outcome and is never reset.
        _location.dungeonData.simulation = new DungeonSimulation { isComplete = true, victoryAchieved = true };
        _location.dungeonData.dockingDestroyed = true;
        Advance();
        Assert.True(_location.dungeonData.dockingDestroyed);
        Assert.NotNull(_location.dungeonData.simulation);
        Assert.Single(_notices); Assert.Empty(_reports);
    }

    [Fact]
    public void RepairDefersDuringLiveOperationsAndPlayerPresenceWithoutLosingTheGuarantee()
    {
        _location.dungeonData = new DungeonData { dockingDestroyed = true };
        using var declaration = _service.Declare("mod.a", "arc5-station-a");
        _location.dungeonData.isOperationActive = true;
        Advance();
        Assert.True(_location.dungeonData.dockingDestroyed);
        _location.dungeonData.isOperationActive = false;
        Source.Player.GamePlayer.current = new Source.Player.GamePlayer { currentPointOfInterest = _station };
        Advance();
        Assert.True(_location.dungeonData.dockingDestroyed);
        Source.Player.GamePlayer.current = null;
        DungeonManager.SetTestInstance = new DungeonManager { LiveOperations = { _location } };
        Advance();
        Assert.True(_location.dungeonData.dockingDestroyed);
        DungeonManager.SetTestInstance = new DungeonManager();
        Advance();
        Assert.False(_location.dungeonData.dockingDestroyed);
        Assert.True(_location.stationIsInvincible); // Hardening applied throughout.
    }

    [Fact]
    public void HealthyRetreatlessStatesAndUndamagedStationsAreLeftCompletelyAlone()
    {
        _location.dungeonData = new DungeonData
        {
            facilityIntegrity = 0.8f,
            simulation = new DungeonSimulation { isRetreating = true, victoryAchieved = true }
        };
        using var declaration = _service.Declare("mod.a", "arc5-station-a");
        Advance();
        Assert.Equal(0.8f, _location.dungeonData.facilityIntegrity);
        Assert.NotNull(_location.dungeonData.simulation);
        Assert.Empty(_reports);
    }

    [Fact]
    public void DisposalRestoresOnlyHardeningThisSessionObservedOff()
    {
        var declaration = _service.Declare("mod.a", "arc5-station-a");
        Advance();
        Assert.True(_location.stationIsInvincible);
        declaration.Dispose();
        Advance();
        Assert.False(_location.stationIsInvincible); // We turned it on; disposal turns it off.
        _location.stationIsInvincible = true; // Native or foreign hardening (a stronghold).
        var second = _service.Declare("mod.a", "arc5-station-a");
        Advance();
        second.Dispose();
        Advance();
        Assert.True(_location.stationIsInvincible); // Observed on: never cleared.
    }

    [Fact]
    public void DeclarationBindsLateCreatedInstallationsAndRebindsAcrossReload()
    {
        using var declaration = _service.Declare("mod.a", "missing-station");
        Advance();
        Assert.Empty(_reports);
        var late = new MapPointOfInterest { guid = "missing-station" };
        var lateLocation = new DungeonLocationData();
        late.AddTestPersistable(lateLocation);
        GalaxyMapData.current!.AddPoi(late);
        Advance();
        Assert.True(lateLocation.stationIsInvincible);
        // Reload rebuilds native objects; the persistent identity is what stays declared.
        var reloaded = new MapPointOfInterest { guid = "missing-station" };
        var reloadedLocation = new DungeonLocationData { dungeonData = new DungeonData { dockingDestroyed = true } };
        reloaded.AddTestPersistable(reloadedLocation);
        GalaxyMapData.current = new GalaxyMapData();
        GalaxyMapData.current.AddPoi(reloaded);
        var replacement = _hub.Begin(SessionOrigin.SaveLoad, "save"); _hub.PlayerReady(replacement);
        Advance();
        Assert.True(reloadedLocation.stationIsInvincible);
        Assert.False(reloadedLocation.dungeonData!.dockingDestroyed);
    }

    [Fact]
    public void AmbiguousIdentityUnavailabilityAndFaultsFailOpen()
    {
        GalaxyMapData.current!.AddPoi(new MapPointOfInterest { guid = "arc5-station-a" });
        using var declaration = _service.Declare("mod.a", "arc5-station-a");
        Advance();
        Assert.False(_location.stationIsInvincible); // Duplicate guid: nothing is hardened.
        GalaxyMapData.current = null;
        Advance();
        _service.SetAvailable(false);
        Advance();
        Assert.Empty(_service.DeclaredTargets());
        Assert.Empty(_reports);
    }

    [Fact]
    public void InstallationSurfaceDeclaresAndProviderDisposalReleases()
    {
        var installation = _hub.Installations.Get("mod.a", "arc5-station-a", null);
        var keep = installation.KeepEnterable();
        Assert.Equal(new[] { "arc5-station-a" }, _service2Targets());
        keep.Dispose();
        Assert.Empty(_service2Targets());
        var again = installation.KeepEnterable();
        Assert.Single(_service2Targets());
        installation.Dispose(); // Provider teardown releases outstanding declarations.
        Assert.Empty(_service2Targets());
        Assert.Throws<ObjectDisposedException>(() => installation.KeepEnterable());
        again.Dispose();
        string[] _service2Targets() => _hub.Installations.Aegis.DeclaredTargets();
    }

    public void Dispose()
    {
        GalaxyMapData.current = null;
        Source.Player.GamePlayer.current = null;
        DungeonManager.SetTestInstance = null;
        _service.Dispose(); _hub.Dispose();
    }
}
