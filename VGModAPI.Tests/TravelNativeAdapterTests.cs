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
        w.Adapter.OnDeparture(w.Player);                        // origin vacated
        w.Player.elapsedTime = 9;
        w.PlaceAt(w.InSystemPoi, w.Origin);                     // destination manager ready
        var token = w.Adapter.OnArrivalEnter(w.OriginManager);
        w.Adapter.OnArrivalExit(token, w.OriginManager, null);
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
        w.Adapter.OnDeparture(w.Player);
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
    public void CrossSystemJumpClosesBeforeNextLegCanStart()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.Transitions.Clear(); w.Player.elapsedTime = 3;
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
        var kinds = w.Transitions.Select(t => t.Kind).ToArray();
        Assert.Equal(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived, TravelTransitionKind.RouteCompleted }, kinds);
        Assert.Equal("poi-dest", w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).ActualLocation!.PoiId);
        Assert.Equal(TravelMode.JumpGate, w.Transitions.Single(t => t.Kind == TravelTransitionKind.Arrived).Mode);
        Assert.Empty(w.Faults);
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
    public void DockingStatesAreDistinctFromInitialPlacementAndInteriorReady()
    {
        using var w = new World();
        w.Adapter.Tick(w.Player, w.OriginManager); w.StationFacts.Clear();
        w.Player.currentSpaceShip!.dockingState = null;
        w.Adapter.ObserveDockingState(w.Player); // establish watch, no emit
        w.Player.currentSpaceShip.dockingState = DockingState.Docked;
        w.Adapter.ObserveDockingState(w.Player);
        w.Player.currentSpaceShip.dockingState = DockingState.Undocking;
        w.Adapter.ObserveDockingState(w.Player);
        w.Player.currentSpaceShip.dockingState = DockingState.Leaving;
        w.Adapter.ObserveDockingState(w.Player);
        Assert.Equal(new[] { StationTransitionKind.DockedPhysical, StationTransitionKind.Undocking, StationTransitionKind.Leaving },
            w.StationFacts.Select(s => s.Kind).ToArray());
        Assert.All(w.StationFacts, s => Assert.Equal("poi-origin", s.Station!.PoiId));
        // Interior readiness is a separate scoped fact, not a dock-state alias.
        var station = new SpaceStation { system = w.Origin, guid = "station" };
        SpaceStationInterior.instance = new SpaceStationInterior { spacestation = station };
        w.Adapter.OnInteriorReady(w.Player, station, null);
        Assert.Equal(StationTransitionKind.InteriorReady, w.StationFacts.Last().Kind);
    }

    [Fact]
    public void InteriorDestroyOnlyRevokesCurrentLease()
    {
        using var w = new World();
        var station = new SpaceStation { system = w.Origin, guid = "station" };
        SpaceStationInterior.instance = new SpaceStationInterior { spacestation = station };
        w.Adapter.OnInteriorReady(w.Player, station, null);
        w.Adapter.OnInteriorDestroyed(w.Player, station);
        Assert.Equal(new[] { StationTransitionKind.InteriorReady, StationTransitionKind.InteriorDestroyed },
            w.StationFacts.Select(s => s.Kind).ToArray());
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
