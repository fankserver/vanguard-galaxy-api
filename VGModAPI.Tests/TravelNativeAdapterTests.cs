using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Behaviour.Managers;
using Behaviour.UI.Spacestation;
using Source.Galaxy;
using Source.Galaxy.POI;
using Source.Player;
using Source.SpaceShip;
using Source.SpaceShip.Auto;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class TravelNativeAdapterTests
{
    private sealed class World : IDisposable
    {
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly GamePlayer Player = new();
        internal readonly SystemMapData Origin = new() { guid = "sys-a", name = "Alpha" };
        internal readonly SystemMapData Dest = new() { guid = "sys-b", name = "Beta" };
        internal readonly MapPointOfInterest OriginPoi = new();
        internal readonly MapPointOfInterest InSystemPoi = new();
        internal readonly MapPointOfInterest DestPoi = new();
        internal readonly MapPointOfInterest NominalPoi = new();
        internal readonly MapPointOfInterest TutorialPoi = new();
        internal readonly TravelManager Travel = new();
        internal TestPoiManager OriginManager;
        internal readonly List<TravelTransition> Transitions = new();
        internal readonly List<StationTransition> StationFacts = new();
        internal readonly List<string> Faults = new();
        internal readonly TravelNativeAdapter Adapter;

        internal World()
        {
            OriginPoi.system = Origin; OriginPoi.guid = "poi-origin";
            InSystemPoi.system = Origin; InSystemPoi.guid = "poi-insystem";
            DestPoi.system = Dest; DestPoi.guid = "poi-dest";
            NominalPoi.system = Dest; NominalPoi.guid = "poi-nominal";
            TutorialPoi.system = Dest; TutorialPoi.guid = "poi-tutorial";
            Player.currentSystem = Origin; Player.currentPointOfInterest = OriginPoi;
            Player.currentSpaceShip = new SpaceShipData();
            Player.elapsedTime = 0;
            GamePlayer.current = Player;
            OriginManager = new TestPoiManager { poi = OriginPoi, initializedAndReady = true };
            Travel.localPoiManager = OriginManager; Travel.localTarget = OriginPoi; Travel.targetPoi = OriginPoi;
            Travel.usingJumpgate = false;
            Behaviour.Util.Singleton<TravelManager>.SetTestInstance = Travel;
            Adapter = new TravelNativeAdapter(new TravelNativeBindings(typeof(GamePlayer).Assembly),
                (owner, ex) => Faults.Add(owner + ":" + ex.GetType().Name));
            Adapter.SetSession(Session);
            Adapter.Events.Subscribe("test", Transitions.Add);
            Adapter.Station.Subscribe("test", StationFacts.Add);
        }

        internal void PlaceAt(MapPointOfInterest poi, SystemMapData system)
        {
            Player.currentSystem = system; Player.currentPointOfInterest = poi; Player.elapsedTime++;
            OriginManager = new TestPoiManager { poi = poi, initializedAndReady = true };
            Travel.localPoiManager = OriginManager; Travel.localTarget = poi; Travel.targetPoi = poi;
        }

        public void Dispose()
        {
            Adapter.SetSession(null); Adapter.Dispose();
            GamePlayer.current = null;
            SpaceStationInterior.instance = null;
            Behaviour.Util.Singleton<TravelManager>.SetTestInstance = null;
        }
    }

    [Fact]
    public void ReadyPlacementIsInitialNotArrivalAndStartsDwell()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager);
        var got = w.Transitions;
        Assert.Equal(TravelTransitionKind.InitialPlacement, Assert.Single(got).Kind);
        Assert.Null(got[0].OperationId);
        var placed = got[0].ActualLocation!;
        Assert.Equal("sys-a", placed.SystemId);
        Assert.Equal("poi-origin", placed.PoiId);
        Assert.Empty(w.Faults);
    }

    [Fact]
    public void InSystemRouteEndsWithVerifiedRouteCompletedAndDwell()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Player.elapsedTime = 5;
        w.Adapter.OnRouteRequested(w.Player, w.InSystemPoi);   // accepted request
        w.Adapter.OnDeparture(w.Player, true);                  // origin vacated (verified)
        w.Player.elapsedTime = 9;
        w.PlaceAt(w.InSystemPoi, w.Origin);                     // destination manager ready
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        w.Adapter.CheckRouteBoundary(w.Travel);                 // verified final-route boundary
        var kinds = w.Transitions.Select(t => t.Kind).ToArray();
        Assert.Equal(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived, TravelTransitionKind.RouteCompleted }, kinds);
        Assert.Equal("poi-insystem", w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).ActualLocation!.PoiId);
        Assert.Equal(5d, w.Transitions.Single(t => t.Kind == TravelTransitionKind.Departed).DwellSeconds);
        Assert.All(w.Transitions, t => Assert.Equal(TravelMode.InSystem, t.Mode));
        Assert.NotNull(w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).OperationId);
    }

    [Fact]
    public void ActualTutorialDestinationIsNotReplacedWithRequestedDestination()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Adapter.OnRouteRequested(w.Player, w.NominalPoi);   // nominal destination requested
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.TutorialPoi, w.Dest);                       // tutorial rewrites actual
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        var requested = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Requested);
        var arrived = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived);
        Assert.Equal("poi-nominal", requested.RequestedDestination!.PoiId);
        Assert.Equal("poi-tutorial", arrived.ActualLocation!.PoiId);
        Assert.Equal(requested.OperationId, arrived.OperationId);
    }

    [Fact]
    public void CrossSystemJumpClosesBeforeNextLegCanStartAndReportsRealRequestedDestination()
    {
        using var w = new World();
        var gate = new JumpGate { system = w.Origin, guid = "gate", targetSystemGuid = w.Dest.guid, targetPoiGuid = w.DestPoi.guid };
        AttachGalaxy(w);
        w.Player.currentSystem = w.Origin; w.Player.currentPointOfInterest = gate;
        w.Player.waypoints.Clear(); w.Player.elapsedTime = 3;
        var viaGate = new TestPoiManager { poi = gate, initializedAndReady = true };
        w.Travel.localPoiManager = viaGate; w.Travel.localTarget = gate; w.Travel.targetPoi = gate;
        w.Adapter.Tick(w.Player, viaGate); w.Transitions.Clear(); // placed at the gate
        var inner = new FakeJump(() =>
        {
            w.Player.currentSystem = w.Dest; w.Player.currentPointOfInterest = w.DestPoi;
            w.Player.waypoints.Clear();
            var mgr = new TestPoiManager { poi = w.DestPoi, initializedAndReady = true };
            w.Travel.localPoiManager = mgr; w.Travel.localTarget = w.DestPoi; w.Travel.targetPoi = w.DestPoi;
        });
        var wrapped = w.Adapter.WrapJump(inner, TravelMode.JumpGate, w.Travel, w.Player);
        Assert.True(wrapped.MoveNext()); // assigns destination; observer departs and arrives
        Assert.False(wrapped.MoveNext());
        w.Adapter.CheckRouteBoundary(w.Travel);
        var kinds = w.Transitions.Select(t => t.Kind).ToArray();
        Assert.Equal(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived, TravelTransitionKind.RouteCompleted }, kinds);
        var requested = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Requested);
        // The requested destination is the gate's real target, never the departure-location proxy.
        Assert.Equal("poi-dest", requested.RequestedDestination!.PoiId);
        Assert.Equal("poi-dest", w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).ActualLocation!.PoiId);
        Assert.Equal(TravelMode.JumpGate, w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).Mode);
        Assert.Empty(w.Faults);
    }
    private static void AttachGalaxy(World w)
    {
        var galaxy = new GalaxyMapData();
        galaxy.AddSystem(w.Origin); galaxy.AddSystem(w.Dest);
        galaxy.AddPoi(w.OriginPoi); galaxy.AddPoi(w.InSystemPoi); galaxy.AddPoi(w.DestPoi);
        galaxy.AddPoi(w.NominalPoi); galaxy.AddPoi(w.TutorialPoi);
        GalaxyMapData.current = galaxy;
        w.Player.map = galaxy;
    }

    [Fact]
    public void CancelIsIdempotentAndLateArrivalIsDropped()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Adapter.OnRouteRequested(w.Player, w.InSystemPoi);
        w.Adapter.OnTravelCancelled();
        w.Adapter.OnTravelCancelled(); // second cancel: no change
        var cancelled = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Cancelled);
        Assert.Equal("poi-origin", cancelled.ActualLocation!.PoiId);
        // Late arrival for the now-cancelled hop must not manufacture success.
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        Assert.DoesNotContain(w.Transitions, t => t.Kind == TravelTransitionKind.Arrived);
    }

    [Fact]
    public void ReplacementSessionDiscardsOldEvidence()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Adapter.OnRouteRequested(w.Player, w.InSystemPoi);
        var replacement = Guid.NewGuid();
        w.Adapter.SetSession(replacement); // replaced session resets travel
        w.Transitions.Clear();
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null); // stale: no pending leg for new session
        Assert.Empty(w.Transitions);
        Assert.Equal(replacement, w.Adapter.CurrentSession);
    }

    [Fact]
    public void DockedPhysicalUndockingLeavingAreEmittedFromNativeBoundariesNotInitialState()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        var option = PlayerOption(w);
        // A ship already docked (initial loaded state) with no native boundary emits nothing.
        Assert.Empty(w.StationFacts);
        w.Adapter.OnDockedPhysical(option);   // DockQuick/Dock completion boundary
        w.Adapter.OnUndocking(option);          // Undock first step
        w.Adapter.OnLeaving(option);            // Undock completion / EmergencyUndock
        Assert.Equal(new[] { StationTransitionKind.DockedPhysical, StationTransitionKind.Undocking, StationTransitionKind.Leaving },
            w.StationFacts.Select(s => s.Kind).ToArray());
        Assert.All(w.StationFacts, s => Assert.Equal("poi-origin", s.Station!.PoiId));
        Assert.All(w.StationFacts, s => Assert.Equal(w.Session, s.SessionId));
    }

    [Fact]
    public void NpcShipOrStaleSessionNeverProducesStationFacts()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        // A different (replacement) ship is not attributed to the player's session.
        var npc = new Behaviour.Unit.SpaceShip { spaceShipData = new SpaceShipData() };
        w.Adapter.OnDockedPhysical(new Behaviour.Spacestation.Docking.DockingOption { dockingSpaceship = npc });
        Assert.Empty(w.StationFacts);
    }

    [Fact]
    public void InteriorReadyRequiresAttributedAwakeThenStartAndDestroyRevokesCurrentLease()
    {
        using var w = new World();
        var station = new SpaceStation { system = w.Origin, guid = "station" };
        var interior = new SpaceStationInterior { spacestation = station };
        SpaceStationInterior.instance = interior;
        // Start without an attributed Awake is not readiness (interior may have been created else-when).
        w.Adapter.OnInteriorStart(interior, w.Player, null);
        Assert.Empty(w.StationFacts);
        w.Adapter.OnInteriorAwake(interior, w.Player, null);
        w.Adapter.OnInteriorStart(interior, w.Player, null);
        Assert.Equal(StationTransitionKind.InteriorReady, w.StationFacts.Single().Kind);
        // Destroying a stale (replaced) interior does not revoke the current lease.
        var other = new SpaceStationInterior { spacestation = station };
        w.Adapter.OnInteriorDestroyed(other, w.Player);
        Assert.DoesNotContain(w.StationFacts, s => s.Kind == StationTransitionKind.InteriorDestroyed);
        // Destroying the current live interior revokes the lease exactly once.
        w.Adapter.OnInteriorDestroyed(interior, w.Player);
        Assert.Equal(StationTransitionKind.InteriorReady, w.StationFacts[0].Kind);
        Assert.Equal(StationTransitionKind.InteriorDestroyed, w.StationFacts[1].Kind);
        w.Adapter.OnInteriorDestroyed(interior, w.Player); // idempotent
        Assert.Equal(2, w.StationFacts.Count);
    }
    private static Behaviour.Spacestation.Docking.DockingOption PlayerOption(World w)
        => new() { dockingSpaceship = new Behaviour.Unit.SpaceShip { spaceShipData = w.Player.currentSpaceShip } };

    [Fact]
    public void ArrivedRetainsOriginAndRequestedMetadata()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Player.elapsedTime = 5;
        w.Adapter.OnRouteRequested(w.Player, w.NominalPoi);
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.TutorialPoi, w.Dest); // tutorial rewrites the actual destination
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        var arrived = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived);
        Assert.Equal("poi-origin", arrived.Origin!.PoiId);
        Assert.Equal("poi-nominal", arrived.RequestedDestination!.PoiId);
        Assert.Equal("poi-tutorial", arrived.ActualLocation!.PoiId);
        Assert.Equal(w.Session, arrived.SessionId);
    }

    [Fact]
    public void StaleJumpIteratorCannotTouchReplacementLeg()
    {
        using var w = new World();
        var gate = new JumpGate { system = w.Origin, guid = "gate", targetSystemGuid = w.Dest.guid, targetPoiGuid = w.DestPoi.guid };
        AttachGalaxy(w);
        w.Player.currentSystem = w.Origin; w.Player.currentPointOfInterest = gate;
        var viaGate = new TestPoiManager { poi = gate, initializedAndReady = true };
        w.Travel.localPoiManager = viaGate; w.Travel.localTarget = gate; w.Travel.targetPoi = gate;
        w.Adapter.Tick(w.Player, viaGate); w.Transitions.Clear();
        var wrapped = w.Adapter.WrapJump(new FakeJump(() => { }), TravelMode.JumpGate, w.Travel, w.Player);
        // A replacement session supersedes travel before the stale iterator ever advances.
        var replacement = Guid.NewGuid();
        w.Adapter.SetSession(replacement); w.Transitions.Clear();
        Assert.True(wrapped.MoveNext());
        Assert.False(wrapped.MoveNext());
        Assert.Empty(w.Transitions); // stale iterator must not emit for the replacement session
    }

    [Fact]
    public void DepartureIgnoredWhenUnloadIsNoOp()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Adapter.OnRouteRequested(w.Player, w.InSystemPoi);
        // UnloadCurrentScene with no manager to unload is a NOOP: not a real departure.
        w.Travel.localPoiManager = null;
        w.Adapter.OnDeparture(w.Player, false);
        Assert.DoesNotContain(w.Transitions, t => t.Kind == TravelTransitionKind.Departed);
        // A real manager->null transition is departure.
        w.Travel.localPoiManager = new TestPoiManager { poi = null, initializedAndReady = true };
        w.Adapter.OnDeparture(w.Player, true);
        Assert.Contains(w.Transitions, t => t.Kind == TravelTransitionKind.Departed);
    }

    [Fact]
    public void ReplacementPlayerInSameSessionIsRejected()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        var other = new GamePlayer { currentSystem = w.Origin, currentPointOfInterest = w.OriginPoi, currentSpaceShip = new SpaceShipData() };
        w.Adapter.OnRouteRequested(other, w.InSystemPoi);
        Assert.Empty(w.Transitions);
    }

    [Fact]
    public void EmptySpacePlacementObservedWhenAttributable()
    {
        using var w = new World();
        w.Player.currentSystem = w.Origin; w.Player.currentPointOfInterest = null;
        var emptyMgr = new TestPoiManager { poi = null, initializedAndReady = true };
        w.Travel.localPoiManager = emptyMgr; w.Travel.localTarget = null; w.Travel.targetPoi = null;
        w.Adapter.Tick(w.Player, emptyMgr);
        var placed = Assert.Single(w.Transitions);
        Assert.Equal(TravelTransitionKind.InitialPlacement, placed.Kind);
        Assert.Equal("sys-a", placed.ActualLocation!.SystemId);
        Assert.Null(placed.ActualLocation.PoiId);
        // A loading (not-initialized) manager is not an attributable placement.
        w.Transitions.Clear();
        var loading = new TestPoiManager { poi = null, initializedAndReady = false };
        w.Travel.localPoiManager = loading;
        w.Adapter.Tick(w.Player, loading);
        Assert.Empty(w.Transitions);
    }

    [Fact]
    public void RouteCompletedIsOnceOnlyAndSessionAttributed()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.Player.elapsedTime = 5;
        w.Adapter.OnRouteRequested(w.Player, w.InSystemPoi);
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.InSystemPoi, w.Origin);
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        var arrivedLeg = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).OperationId;
        w.Adapter.CheckRouteBoundary(w.Travel);
        w.Adapter.CheckRouteBoundary(w.Travel); // repeated boundary: once-only
        var completed = w.Transitions.Single(t => t.Kind == TravelTransitionKind.RouteCompleted);
        Assert.Equal(arrivedLeg, completed.OperationId);
        Assert.Equal(w.Session, completed.SessionId);
    }

    [Fact]
    public void StaleOperationErrorDoesNotDisableReplacementButFaultDoes()
    {
        using var w = new World();
        // An operation-level error (e.g. a stale/old-session callback) is reported but the
        // replacement session keeps working.
        w.Adapter.Guard(() => throw new InvalidOperationException("transient stale op"));
        Assert.False(w.Adapter.IsFaulted);
        w.Adapter.Tick(w.Player, w.OriginManager);
        Assert.Contains(w.Transitions, t => t.Kind == TravelTransitionKind.InitialPlacement);
        // A real fault (main-thread violation) disables the group: no further observation.
        w.Transitions.Clear();
        w.Adapter.Fault(new InvalidOperationException("fatal thread violation"));
        Assert.True(w.Adapter.IsFaulted);
        w.Adapter.Tick(w.Player, w.OriginManager);
        Assert.Empty(w.Transitions);
    }

    [Fact]
    public void ThrowingSubscriberIsIsolatedFromLaterSubscribers()
    {
        using var w = new World();
        var reached = new List<Guid>();
        w.Adapter.Events.Subscribe("thrower", _ => throw new InvalidOperationException("boom"));
        w.Adapter.Events.Subscribe("later", t => reached.Add(t.SessionId));
        w.Adapter.Tick(w.Player, w.OriginManager);
        Assert.Single(reached);
        Assert.Equal(w.Session, reached[0]);
        Assert.Contains(w.Faults, f => f.Contains("thrower"));
    }

    private sealed class FakeJump : IEnumerator, IDisposable
    {
        private readonly Action _onStep;
        private bool _done;
        internal FakeJump(Action onStep) => _onStep = onStep;
        public object? Current => null;
        public bool MoveNext()
        {
            if (_done) return false;
            _done = true; _onStep(); return true;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
