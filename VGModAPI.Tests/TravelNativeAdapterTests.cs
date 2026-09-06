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
        internal readonly JumpGate SourceGate = new();
        internal readonly MapPointOfInterest TargetGate = new();
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
            NominalPoi.system = Origin; NominalPoi.guid = "poi-nominal"; // an alternative in-current-system target
            TutorialPoi.system = Origin; TutorialPoi.guid = "poi-tutorial"; // a rewritten actual (non-gate) POI
            SourceGate.system = Origin; SourceGate.guid = "gate-a";
            TargetGate.system = Dest; TargetGate.guid = "gate-b";
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
        internal void SetWaypoints(params MapPointOfInterest[] waypoints)
        {
            Player.waypoints.Clear(); Player.waypoints.AddRange(waypoints);
        }
        // Park the player at a ready manager for a POI/system.
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

    // --- finding 1: nested-yield lifecycle propagation ---

    [Fact]
    public void NestedReadinessWaitChildDoesNotCancelJumpLeg()
    {
        using var w = new World();
        w.Player.currentSystem = w.Origin; w.Player.currentPointOfInterest = w.SourceGate;
        var viaGate = new TestPoiManager { poi = w.SourceGate, initializedAndReady = true };
        w.Travel.localPoiManager = viaGate; w.Travel.localTarget = w.SourceGate; w.Travel.targetPoi = w.SourceGate;
        w.Adapter.Tick(w.Player, viaGate); w.Transitions.Clear();
        w.SourceGate.targetSystemGuid = w.Dest.guid; w.SourceGate.targetPoiGuid = w.TargetGate.guid;
        var wrapped = w.Adapter.WrapJump(JumpWithReadinessWait(w, new FakeWaitN(2)), TravelMode.JumpGate, w.Travel, w.Player, w.SourceGate);
        Drive(wrapped);
        // Must reach Arrived without a Cancelled caused by the nested WaitUntil child completing.
        Assert.DoesNotContain(w.Transitions, t => t.Kind == TravelTransitionKind.Cancelled);
        Assert.Contains(w.Transitions, t => t.Kind == TravelTransitionKind.Arrived);
        Assert.Equal(1, w.Transitions.Count(t => t.Kind == TravelTransitionKind.Requested));
        Assert.Empty(w.Faults);
    }

    [Fact]
    public void NestedDockProcedureDoesNotEmitPrematureDockedPhysicalAndDisposeIsNotCompletion()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        var ship = UnitFor(w);
        ship.spaceShipData!.dockingState = DockingState.Docking;
        var docket = new DockFake(ship, new FakeWaitN(1)); // procedure child, then sets Docked at root end
        var observer = new CoroutineBoundaryObserver(docket, onDone: () => w.Adapter.OnDockedPhysical(docket.Ship));
        Drive(observer);
        // Exactly one DockedPhysical (at root completion), not one per nested child.
        Assert.Single(w.StationFacts);
        Assert.Equal(StationTransitionKind.DockedPhysical, w.StationFacts[0].Kind);
        // Dispose of an in-flight dock is cancellation, never DockedPhysical.
        w.StationFacts.Clear();
        ship.spaceShipData!.dockingState = DockingState.Docking;
        var inflight = new DockFake(ship, new FakeWaitN(10));
        var observer2 = new CoroutineBoundaryObserver(inflight, onDone: () => w.Adapter.OnDockedPhysical(inflight.Ship));
        Assert.True(observer2.MoveNext()); // first parent step runs the procedure child
        (observer2.Current as IEnumerator)?.MoveNext(); // drive child once, still in-flight
        observer2.Dispose(); // cancellation
        Assert.Empty(w.StationFacts);
    }

    // --- finding 3/5: Undock resets ship but Leaving retains attribution ---

    [Fact]
    public void UndockCapturesShipBeforeResetAndEmitsLeavingOnCompletion()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        var ship = new Behaviour.Unit.SpaceShip { spaceShipData = w.Player.currentSpaceShip };
        ship.spaceShipData!.dockingState = DockingState.Docked;
        // Undock: sets Undocking, runs procedure child, sets Leaving, then resets dockingSpaceship=null.
        var undock = new UndockFake(ship, new FakeWaitN(1));
        var observer = new CoroutineBoundaryObserver(undock,
            onFirst: () => w.Adapter.OnUndocking(undock.Ship), onDone: () => w.Adapter.OnLeaving(undock.Ship));
        Drive(observer);
        var kinds = w.StationFacts.Select(s => s.Kind).ToArray();
        Assert.Equal(new[] { StationTransitionKind.Undocking, StationTransitionKind.Leaving }, kinds);
        Assert.All(w.StationFacts, f => Assert.Equal(w.Session, f.SessionId));
        Assert.Empty(w.Faults);
    }

    [Fact]
    public void LeavingRequiresDockingStateLeaving()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        var ship = UnitFor(w);
        ship.spaceShipData!.dockingState = DockingState.Docking; // not Leaving
        w.Adapter.OnLeaving(ship);
        Assert.Empty(w.StationFacts);
    }

    // --- finding 2: TravelToNextWaypoint requests in-system legs (gate->in-system->final) ---

    [Fact]
    public void MultiHopRouteEndsWithFinalArrivedAndRouteCompleted()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        // SetRouteToPOI cross-system: first hop is the source gate (in Origin system).
        w.SetWaypoints(w.SourceGate, w.TargetGate, w.DestPoi);
        w.Adapter.RequestWaypointLeg();                          // in-system to source gate
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.SourceGate, w.Origin);                       // arrive at source gate
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        // TravelToNextWaypoint: cross-system waypoint (target gate) is a jump; no in-system request.
        w.Player.waypoints.Remove(w.SourceGate);
        w.Adapter.RequestWaypointLeg();
        Assert.DoesNotContain(w.Transitions, t => t.Kind == TravelTransitionKind.Cancelled);
        // Jump through the gate to the target gate.
        w.SourceGate.targetSystemGuid = w.Dest.guid; w.SourceGate.targetPoiGuid = w.TargetGate.guid;
        var jump = w.Adapter.WrapJump(JumpWithReadinessWait(w, new FakeWaitN(1)), TravelMode.JumpGate, w.Travel, w.Player, w.SourceGate);
        Drive(jump);
        w.Transitions.Clear(); // isolate final in-system leg
        w.Player.waypoints.Clear(); w.Player.waypoints.Add(w.DestPoi);   // in Dest system
        w.Adapter.RequestWaypointLeg();                          // final in-system leg requested
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.DestPoi, w.Dest);
        var token2 = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token2, w.OriginManager, null);
        w.Player.waypoints.Clear(); // final waypoint removed before the boundary
        w.Adapter.CheckRouteBoundary(w.Travel);
        var kinds = w.Transitions.Select(t => t.Kind).ToArray();
        Assert.Equal(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived, TravelTransitionKind.RouteCompleted }, kinds);
        Assert.Equal("poi-dest", w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).ActualLocation!.PoiId);
        Assert.Equal(TravelMode.InSystem, w.Transitions.Single(t => t.Kind == TravelTransitionKind.RouteCompleted).Mode);
    }

    // --- finding 4: tutorial nominal from raw guids, no lazy fallback, no throw ---

    [Fact]
    public void TutorialJumpNominalIsRawAndUnresolvableTargetDoesNotThrowOrLoseLeg()
    {
        using var w = new World();
        w.Player.currentSystem = w.Origin; w.Player.currentPointOfInterest = w.SourceGate;
        var viaGate = new TestPoiManager { poi = w.SourceGate, initializedAndReady = true };
        w.Travel.localPoiManager = viaGate; w.Travel.localTarget = w.SourceGate; w.Travel.targetPoi = w.SourceGate;
        w.Adapter.Tick(w.Player, viaGate); w.Transitions.Clear();
        // Nominal target is absent from any resolvable world (tutorial map) -> raw guids only.
        w.SourceGate.targetSystemGuid = "sys-virtual"; w.SourceGate.targetPoiGuid = "poi-virtual";
        // After the tutorial->sandbox transition the current POI is a plain (non-gate) POI.
        w.Player.currentSystem = w.Dest; w.Player.currentPointOfInterest = w.TutorialPoi;
        w.Adapter.RequestWaypointLeg(); // must NOT be treated as the gate/proxy
        var jump = w.Adapter.WrapJump(JumpWithReadinessWait(w, new FakeWaitN(1)), TravelMode.JumpGate, w.Travel, w.Player, w.SourceGate);
        Drive(jump);
        Assert.Empty(w.Faults);
        var requested = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Requested);
        Assert.Equal("sys-virtual", requested.RequestedDestination!.SystemId);   // nominal valid w/o resolution
        Assert.Equal("poi-virtual", requested.RequestedDestination.PoiId);
        Assert.Contains(w.Transitions, t => t.Kind == TravelTransitionKind.Arrived);
    }

    // --- finding 6: initial-load DockQuick suppressed; real transition emits ---

    [Fact]
    public void InitialLoadDockQuickIsNotATransitionButRealDockEmitted()
    {
        using var w = new World();
        var ship = UnitFor(w);
        ship.spaceShipData!.dockingState = DockingState.Docked;
        // No placement, no prior transition: initial load DockQuick must not emit.
        w.Adapter.OnDockedPhysical(ship);
        Assert.Empty(w.StationFacts);
        // After a placement the reducer knows the player transitioned: emit.
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        w.Adapter.OnDockedPhysical(UnitFor(w));
        Assert.Equal(StationTransitionKind.DockedPhysical, Assert.Single(w.StationFacts).Kind);
    }

    // --- finding 7: no fictitious in-system Requested+Cancelled for direct gate hop ---

    [Fact]
    public void DirectGateHopAvoidsFictitiousInSystemRequestCancelledPair()
    {
        using var w = new World();
        w.Player.currentSystem = w.Origin; w.Player.currentPointOfInterest = w.SourceGate;
        var viaGate = new TestPoiManager { poi = w.SourceGate, initializedAndReady = true };
        w.Travel.localPoiManager = viaGate; w.Travel.localTarget = w.SourceGate; w.Travel.targetPoi = w.SourceGate;
        w.Adapter.Tick(w.Player, viaGate); w.Transitions.Clear();
        // Player already at gate; SetRouteToPOI's waypoints[0] is a cross-system waypoint -> skip.
        w.SetWaypoints(w.TargetGate, w.DestPoi);
        w.Adapter.RequestWaypointLeg();
        Assert.Empty(w.Transitions);
        w.SourceGate.targetSystemGuid = w.Dest.guid; w.SourceGate.targetPoiGuid = w.TargetGate.guid;
        var jump = w.Adapter.WrapJump(JumpWithReadinessWait(w, new FakeWaitN(1)), TravelMode.JumpGate, w.Travel, w.Player, w.SourceGate);
        Drive(jump);
        var kinds = w.Transitions.Select(t => t.Kind).ToArray();
        Assert.Equal(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived }, kinds);
        Assert.DoesNotContain(w.Transitions, t => t.Kind == TravelTransitionKind.Cancelled);
        Assert.Equal(TravelMode.JumpGate, w.Transitions.Single(t => t.Kind == TravelTransitionKind.Requested).Mode);
    }

    // --- finding 8: null-first player binding is retried; replacement not adopted ---

    [Fact]
    public void PlayerBindsOnFirstNonNullAndNeverAdoptsReplacement()
    {
        using var w = new World();
        GamePlayer.current = null;
        w.Adapter.SetSession(w.Session); // null player at first active frame
        GamePlayer.current = w.Player;
        w.Adapter.SetSession(w.Session); // same id, first non-null player binds
        w.Adapter.Tick(w.Player, w.OriginManager);
        Assert.Contains(w.Transitions, t => t.Kind == TravelTransitionKind.InitialPlacement);
        // A replacement player object under the same session id is NOT adopted.
        w.Transitions.Clear();
        var other = new GamePlayer { currentSystem = w.Origin, currentPointOfInterest = w.OriginPoi, currentSpaceShip = new SpaceShipData() };
        GamePlayer.current = other;
        w.Adapter.SetSession(w.Session);
        w.Adapter.Tick(other, w.OriginManager);
        Assert.Empty(w.Transitions);
    }

    // --- finding 9: Awake registration is single (Finalizer-only) ---

    [Fact]
    public void InteriorAwakeRegistersLeaseOnceForReadiness()
    {
        using var w = new World();
        var station = new SpaceStation { system = w.Origin, guid = "station" };
        var interior = new SpaceStationInterior { spacestation = station };
        SpaceStationInterior.instance = interior;
        // The patch now drives attribution only through the (single) Finalizer; the adapter
        // itself is idempotent on repeated Awake for the same instance.
        w.Adapter.OnInteriorAwake(interior, w.Player, null);
        w.Adapter.OnInteriorAwake(interior, w.Player, null);
        w.Adapter.OnInteriorStart(interior, w.Player, null);
        Assert.Equal(StationTransitionKind.InteriorReady, Assert.Single(w.StationFacts).Kind);
    }

    // ---------- remaining original coverage ----------

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
        w.SetWaypoints(w.InSystemPoi);
        w.Player.elapsedTime = 5;
        w.Adapter.RequestWaypointLeg();   // accepted request for the actual waypoint
        w.Adapter.OnDeparture(w.Player, true);
        w.Player.elapsedTime = 9;
        w.PlaceAt(w.InSystemPoi, w.Origin);
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        w.Player.waypoints.Clear(); // Travel() removed the final waypoint before the boundary
        w.Adapter.CheckRouteBoundary(w.Travel);
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
        w.SetWaypoints(w.NominalPoi);
        w.Adapter.RequestWaypointLeg();   // nominal destination requested (waypoint)
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.TutorialPoi, w.Origin);   // tutorial rewrites actual
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        var requested = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Requested);
        var arrived = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived);
        Assert.Equal("poi-nominal", requested.RequestedDestination!.PoiId);
        Assert.Equal("poi-tutorial", arrived.ActualLocation!.PoiId);
        Assert.Equal(requested.OperationId, arrived.OperationId);
    }

    [Fact]
    public void CancelIsIdempotentAndLateArrivalIsDropped()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.SetWaypoints(w.InSystemPoi);
        w.Adapter.RequestWaypointLeg();
        w.Adapter.OnTravelCancelled();
        w.Adapter.OnTravelCancelled();
        var cancelled = Assert.Single(w.Transitions, t => t.Kind == TravelTransitionKind.Cancelled);
        Assert.Equal("poi-origin", cancelled.ActualLocation!.PoiId);
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        Assert.DoesNotContain(w.Transitions, t => t.Kind == TravelTransitionKind.Arrived);
    }

    [Fact]
    public void ReplacementSessionDiscardsOldEvidence()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear();
        w.SetWaypoints(w.InSystemPoi);
        w.Adapter.RequestWaypointLeg();
        var replacement = Guid.NewGuid();
        w.Adapter.SetSession(replacement);
        w.Transitions.Clear();
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        Assert.Empty(w.Transitions);
        Assert.Equal(replacement, w.Adapter.CurrentSession);
    }

    [Fact]
    public void NpcShipOrStaleSessionNeverProducesStationFacts()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        var npc = new Behaviour.Unit.SpaceShip { spaceShipData = new SpaceShipData() };
        npc.spaceShipData!.dockingState = DockingState.Docked;
        w.Adapter.OnDockedPhysical(npc);
        Assert.Empty(w.StationFacts);
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
        w.SetWaypoints(w.InSystemPoi);
        w.Player.elapsedTime = 5;
        w.Adapter.RequestWaypointLeg();
        w.Adapter.OnDeparture(w.Player, true);
        w.PlaceAt(w.InSystemPoi, w.Origin);
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
        var arrivedLeg = w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).OperationId;
        w.Player.waypoints.Clear(); // final waypoint removed before the boundary
        w.Adapter.CheckRouteBoundary(w.Travel);
        w.Adapter.CheckRouteBoundary(w.Travel);
        var completed = w.Transitions.Single(t => t.Kind == TravelTransitionKind.RouteCompleted);
        Assert.Equal(arrivedLeg, completed.OperationId);
        Assert.Equal(w.Session, completed.SessionId);
    }

    [Fact]
    public void StaleOperationErrorDoesNotDisableReplacementButFaultDoes()
    {
        using var w = new World();
        w.Adapter.Guard(() => throw new InvalidOperationException("transient stale op"));
        Assert.False(w.Adapter.IsFaulted);
        w.Adapter.Tick(w.Player, w.OriginManager);
        Assert.Contains(w.Transitions, t => t.Kind == TravelTransitionKind.InitialPlacement);
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

    // ---------- iterator fakes and drivers ----------

    // Drive an iterator tree exactly as Unity drives nested IEnumerator yields (children first).
    private static void Drive(IEnumerator root)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var top = stack.Peek();
            if (top.MoveNext())
            {
                if (top.Current is IEnumerator child) stack.Push(child);
            }
            else stack.Pop();
        }
    }

    // A real C# iterator (the jump coroutine) that yields a nested WaitUntil-like child for
    // readiness, warps, then yields one observer-visible step so the arrival is observed.
    private static IEnumerator JumpWithReadinessWait(World w, IEnumerator waitChild)
    {
        yield return waitChild;                       // nested child completes without lifecycle callbacks
        w.Player.currentSystem = w.Dest; w.Player.currentPointOfInterest = w.DestPoi;
        w.Player.waypoints.Clear();
        var mgr = new TestPoiManager { poi = w.DestPoi, initializedAndReady = true };
        w.Travel.localPoiManager = mgr; w.Travel.localTarget = w.DestPoi; w.Travel.targetPoi = w.DestPoi;
        yield return null;                            // parent step: observer detects arrival
        yield break;
    }

    // WaitUntil-like CustomYieldInstruction: MoveNext true while not done.
    private sealed class FakeWaitN : IEnumerator
    {
        private readonly int _limit;
        private int _n;
        internal FakeWaitN(int limit) => _limit = limit;
        public object? Current => null;
        public bool MoveNext() => ++_n <= _limit;
        public void Reset() => _n = 0;
    }
    private static Behaviour.Unit.SpaceShip UnitFor(World w)
        => new() { spaceShipData = w.Player.currentSpaceShip };
    private sealed class DockFake : IEnumerator
    {
        internal readonly Behaviour.Unit.SpaceShip Ship;
        private readonly IEnumerator _procedure;
        private int _step;
        internal DockFake(Behaviour.Unit.SpaceShip ship, IEnumerator procedure) { Ship = ship; _procedure = procedure; }
        public object? Current => _step == 0 ? _procedure : null;
        public bool MoveNext()
        {
            if (_step == 0) { _step = 1; return true; }  // yield the procedure (Docking)
            if (_step == 1) { _step = 2; Ship.spaceShipData!.dockingState = DockingState.Docked; }
            return _step <= 2 ? (_step++ <= 2) : false;
        }
        public void Reset() => _step = 0;
    }
    private sealed class UndockFake : IEnumerator
    {
        internal readonly Behaviour.Unit.SpaceShip Ship;
        private readonly IEnumerator _procedure;
        private int _step;
        internal UndockFake(Behaviour.Unit.SpaceShip ship, IEnumerator procedure) { Ship = ship; _procedure = procedure; }
        public object? Current => null;
        public bool MoveNext()
        {
            _step++;
            if (_step == 1) { Ship.spaceShipData!.dockingState = DockingState.Undocking; return true; }
            if (_step == 2) return true;                        // run procedure child (as a plain step)
            if (_step == 3) { Ship.spaceShipData!.dockingState = DockingState.Leaving; return true; }
            if (_step == 4) { /* ResetDockingOption nulls the option, NOT the captured ship */ return true; }
            return false;
        }
        public void Reset() => _step = 0;
    }
}
