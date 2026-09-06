using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Per-case native driver for phase travel-in-system-station-v1. It runs inside the plugin
    // iterator context so real Unity coroutines drive every transition, and it only calls vanilla
    // entry points a player action would call: TravelManager.TryInitiateTravel / CancelTravel,
    // SpaceStationInterior.ExitSpacestation or SpacestationExteriorManager.StartUndocking, and the
    // native arrival docking path. Waypoint-list SETUP for the chained route is labelled as setup;
    // native Travel()/TravelToNextWaypoint still advance the chain and emit every fact.
    private sealed class TravelStationDriver
    {
        private readonly Plugin _p;
        private readonly Guid _session;
        private readonly Type _travelType, _poiType, _shipType, _exteriorType, _interiorType;
        private readonly Type _dockingOptionType, _stationType, _jumpGateType, _wormholeType, _mapType, _gameplayType;
        private readonly MethodInfo _tryInitiateTravel, _canWeTravel, _cancelTravel, _travelActive, _localPoiReady;
        private readonly MethodInfo _startUndocking, _getDockingOption, _exitSpacestation, _playerIsFriendly;
        private readonly object _travel;
        private object? _startPoi;
        private string _systemId = "";
        private string _startPoiId = "";
        private object[] _hops = Array.Empty<object>();
        private int _dockWindow;
        private bool _canTravel;
        private bool _routeDrove;
        private bool _chainReturned;

        internal TravelStationDriver(Plugin p, Guid session)
        {
            _p = p; _session = session;
            _travelType = Named("Behaviour.Managers.TravelManager");
            _poiType = Named("Source.Galaxy.MapPointOfInterest");
            _shipType = Named("Behaviour.Unit.SpaceShip");
            _exteriorType = Named("SpacestationExteriorManager");
            _interiorType = Named("Behaviour.UI.Spacestation.SpaceStationInterior");
            _dockingOptionType = Named("Behaviour.Spacestation.Docking.DockingOption");
            _stationType = Named("Source.Galaxy.POI.SpaceStation");
            _jumpGateType = Named("Source.Galaxy.POI.JumpGate");
            _wormholeType = Named("Source.Galaxy.POI.Wormhole");
            _mapType = Named("Source.Galaxy.GalaxyMapData");
            _gameplayType = Named("GameplayManager");
            // Exact declared signatures: a name-only lookup can bind the wrong overload.
            _tryInitiateTravel = Bind(_travelType, "TryInitiateTravel", typeof(bool), _poiType);
            _canWeTravel = Bind(_travelType, "CanWeTravel", typeof(bool), _poiType);
            _cancelTravel = Bind(_travelType, "CancelTravel", typeof(bool), typeof(Vector2?));
            _travelActive = Bind(_travelType, "TravelActive", typeof(bool));
            _localPoiReady = Bind(_travelType, "IsLocalPoiReady", typeof(bool));
            _startUndocking = Bind(_exteriorType, "StartUndocking", typeof(void));
            _getDockingOption = Bind(_exteriorType, "GetDockingOption", _dockingOptionType, _shipType);
            _exitSpacestation = Bind(_interiorType, "ExitSpacestation", typeof(void));
            _playerIsFriendly = Bind(_stationType, "PlayerIsFriendly", typeof(bool));
            var travel = SpGet(_travelType, "Instance");
            Require(Alive(travel), "Native TravelManager singleton is not live.");
            _travel = travel!;
        }

        private List<TravelTransition> Travel => _p.PendingTravel!;
        private List<StationTransition> Stations => _p.PendingStation!;
        // Case-local window: only facts observed after this case started driving, for this session.
        private List<TravelTransition> Slice(int offset) => Travel.Skip(offset).Where(t => t.SessionId == _session).ToList();
        private List<StationTransition> StationSlice(int offset) => Stations.Skip(offset).Where(s => s.SessionId == _session).ToList();
        private object Player => _p.CurrentPlayer;
        private object? PlayerShip => SpGet(SpGet(_gameplayType, "Instance")!, "spaceShip");

        internal IEnumerable<object?> Run()
        {
            yield return null;
            foreach (var frame in CaseInitialPlacement()) yield return frame;
            foreach (var frame in CaseUndock()) yield return frame;
            foreach (var frame in CaseInSystemRoute()) yield return frame;
            foreach (var frame in CaseEarlyCancel()) yield return frame;
            foreach (var frame in CaseChainedRoute()) yield return frame;
            foreach (var frame in CaseDock()) yield return frame;
            RecordResidualMatrix();
        }

        // --- cases ------------------------------------------------------------------------

        private IEnumerable<object?> CaseInitialPlacement()
        {
            _p.TsCase(InitialPlacementCase, InitialPlacementDescription);
            yield return null;
            var poi = SpGet(Player, "currentPointOfInterest");
            var system = SpGet(Player, "currentSystem");
            Require(system != null, "Native player has no current system after the fixture load.");
            _systemId = (string)SpGet(system!, "guid")!;
            var poiId = poi == null ? null : (string)SpGet(poi, "guid")!;
            var failure = TravelStationReceipt.CheckInitialPlacement(Slice(0), _session, _systemId, poiId);
            Require(failure == null, failure!);
            Require(TravelStationReceipt.Same(ModApi.Travel!.CurrentLocation, _systemId, poiId),
                "Public CurrentLocation does not match the actual native location after the load.");
            _startPoi = poi;
            _startPoiId = poiId ?? "";
            // A docked load can legitimately produce station facts (native re-init dock tolerance),
            // so they are recorded as evidence here instead of being asserted away.
            _p.TsRecord(TravelStationReceipt.Passed, TravelStationReceipt.Location(_systemId, poiId), _session, null,
                "travelFacts=" + Slice(0).Count + "; stationFactsAtLoad=" + StationSlice(0).Count);
        }

        private IEnumerable<object?> CaseUndock()
        {
            _p.TsCase("station-undock", "The native station exit path emits Undocking then Leaving for the player ship at the docked station, with no travel fact.");
            var ship = PlayerShip;
            var exterior = SpGet(_exteriorType, "Instance");
            if (_startPoi == null || !_stationType.IsInstanceOfType(_startPoi) || !Alive(exterior) || !Alive(ship))
            {
                NotRun("Fixture does not start at a live station exterior with a player ship; no native undock exists to drive.");
                yield break;
            }
            // The initial dock can still be settling natively right after the load.
            foreach (var frame in PollFor(() => DockingState(ship!) == "Docked", 20)) yield return frame;
            if (DockingState(ship!) != "Docked" || !Alive(_getDockingOption.Invoke(exterior!, new[] { ship })))
            {
                NotRun("Fixture ship state is " + (DockingState(ship!) ?? "undocked") + " with no holding docking option; no native undock to drive.");
                yield break;
            }
            if (!(bool)_playerIsFriendly.Invoke(_startPoi!, null)!)
            {
                NotRun("Start station is not player-friendly; refusing to drive dock/undock at a hostile station.");
                yield break;
            }
            if (!(bool)_canWeTravel.Invoke(_travel, new object?[] { null })!)
            {
                NotRun("Native CanWeTravel refused, so the native exit path would refuse the undock too.");
                yield break;
            }
            int travelOffset = Travel.Count;
            int stationOffset = Stations.Count;
            var interior = SpGet(_interiorType, "instance");
            bool interiorExit = Alive(interior);
            // Exactly the player's own exit action: the interior exit when the interior is open (it
            // toggles the interior and calls StartUndocking itself), otherwise the exterior entry.
            if (interiorExit) _exitSpacestation.Invoke(interior!, null);
            else _startUndocking.Invoke(exterior!, null);
            foreach (var frame in AwaitOrFail(() => StationSlice(stationOffset).Any(s => s.Kind == StationTransitionKind.Leaving),
                60, "native undock Leaving fact")) yield return frame;
            foreach (var frame in AwaitOrFail(() => !Alive(SpGet(exterior!, "undockingRoutine")), 60, "native undocking routine completion")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var failure = TravelStationReceipt.CheckStationPhase(StationSlice(stationOffset), _session, _systemId, _startPoiId,
                new[] { StationTransitionKind.Undocking, StationTransitionKind.Leaving });
            Require(failure == null, failure!);
            Require(DockingState(ship!) == "Leaving", "Native ship docking state is " + (DockingState(ship!) ?? "null") + " after a completed undock.");
            Require(!Alive(_getDockingOption.Invoke(exterior!, new[] { ship })), "A native docking option still holds the player ship after undocking.");
            Require(Slice(travelOffset).Count == 0, "Undocking emitted travel facts: "
                + string.Join(", ", Slice(travelOffset).Select(TravelStationReceipt.Describe)));
            _p.TsRecord(TravelStationReceipt.Passed, TravelStationReceipt.Location(_systemId, _startPoiId), _session, null,
                "interiorExit=" + interiorExit + "; interiorFacts="
                + StationSlice(stationOffset).Count(s => s.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed));
        }

        private IEnumerable<object?> CaseInSystemRoute()
        {
            _p.TsCase("in-system-route", "A real single-hop native in-system route emits ordered Requested->Departed->Arrived->RouteCompleted for one operation.");
            _hops = SafeTargets();
            if (_hops.Length < 2)
            {
                NotRun("Only " + _hops.Length + " safe non-station in-system target(s) beside the start station; this phase's route plan needs two.");
                yield break;
            }
            foreach (var frame in DriveRoute(new[] { _hops[0] }, _startPoiId)) yield return frame;
        }

        private IEnumerable<object?> CaseEarlyCancel()
        {
            _p.TsCase("early-cancel", "Cancelling before any departure keeps the actual origin current and emits Requested->Cancelled for one operation, with no Departed or RecoveredPlacement.");
            var origin = SpGet(Player, "currentPointOfInterest");
            var target = _hops.FirstOrDefault(h => !ReferenceEquals(h, origin));
            if (origin == null || target == null)
            {
                NotRun("No known origin POI or no in-system target other than the current POI to request a cancellable route to.");
                yield break;
            }
            var originId = (string)SpGet(origin, "guid")!;
            foreach (var frame in ReadyToTravel(target)) yield return frame;
            if (!_canTravel)
            {
                NotRun("Native CanWeTravel refused the cancellable route.");
                yield break;
            }
            int offset = Travel.Count;
            int stationOffset = Stations.Count;
            Require(!(bool)_travelActive.Invoke(_travel, null)!, "Native travel was already active before the cancel case.");
            Require((bool)_tryInitiateTravel.Invoke(_travel, new[] { target })!, "Native TryInitiateTravel refused the cancel-case route.");
            // Same frame: no origin unload can have happened, so any Departed here is fabricated.
            Require((bool)_cancelTravel.Invoke(_travel, new object?[] { null })!, "Native CancelTravel(null) refused.");
            foreach (var frame in Settle()) yield return frame;
            var slice = Slice(offset);
            var failure = TravelStationReceipt.CheckEarlyCancel(slice, _session, _systemId, originId, (string)SpGet(target, "guid")!);
            Require(failure == null, failure!);
            Require(ReferenceEquals(SpGet(Player, "currentPointOfInterest"), origin), "Native current POI changed during an early cancel.");
            Require(!(bool)_travelActive.Invoke(_travel, null)!, "Native travel is still active after CancelTravel.");
            Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints survived CancelTravel.");
            Require(TravelStationReceipt.Same(ModApi.Travel!.CurrentLocation, _systemId, originId), "Public CurrentLocation left the unchanged origin.");
            Require(StationSlice(stationOffset).Count == 0, "Early cancel emitted station facts.");
            _p.TsRecord(TravelStationReceipt.Passed, TravelStationReceipt.Location(_systemId, originId), _session,
                slice[0].OperationId, "cancelledAt=" + TravelStationReceipt.Location(slice[1].ActualLocation)
                + "; cancelledAfterSeconds=" + (slice[1].GameSeconds - slice[0].GameSeconds).ToString("F3"));
        }

        private IEnumerable<object?> CaseChainedRoute()
        {
            _p.TsCase("chained-route", "A real two-hop native chain (second waypoint set up, native TravelToNextWaypoint advancing) emits both hops and exactly one RouteCompleted at the final boundary.");
            var origin = SpGet(Player, "currentPointOfInterest");
            var second = _hops.FirstOrDefault(h => !ReferenceEquals(h, origin));
            if (origin == null || second == null || _startPoi == null || ReferenceEquals(_startPoi, origin))
            {
                NotRun("Chained route needs a known origin plus a distinct second in-system target and the start station as the returning final hop.");
                yield break;
            }
            _dockWindow = Stations.Count;
            foreach (var frame in DriveRoute(new[] { second, _startPoi! }, (string)SpGet(origin, "guid")!)) yield return frame;
            _chainReturned = _routeDrove;
        }

        private IEnumerable<object?> CaseDock()
        {
            _p.TsCase("station-dock", "The native arrival docking path reaches physical Docked and emits exactly one DockedPhysical for the returned station.");
            var poi = SpGet(Player, "currentPointOfInterest");
            var exterior = SpGet(_exteriorType, "Instance");
            var ship = PlayerShip;
            if (!_chainReturned || _startPoi == null || !ReferenceEquals(poi, _startPoi) || !Alive(exterior) || !Alive(ship))
            {
                NotRun("The chained route did not return to the live start station, so no native arrival dock is observable.");
                yield break;
            }
            // The native approach is a physical manoeuvre driven by DockingOption.Update, so the
            // deadline is generous but explicit; a timeout is a recorded failure, never a skip.
            foreach (var frame in AwaitOrFail(() => StationSlice(_dockWindow).Any(s => s.Kind == StationTransitionKind.DockedPhysical),
                240, "native arrival DockedPhysical")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var failure = TravelStationReceipt.CheckStationPhase(StationSlice(_dockWindow), _session, _systemId, _startPoiId,
                new[] { StationTransitionKind.DockedPhysical });
            Require(failure == null, failure!);
            Require(DockingState(ship!) == "Docked", "Native ship docking state is " + (DockingState(ship!) ?? "null") + " after DockedPhysical.");
            var option = _getDockingOption.Invoke(exterior!, new[] { ship });
            Require(Alive(option) && ReferenceEquals(SpGet(option!, "dockingSpaceship"), ship), "No native docking option holds the player ship after DockedPhysical.");
            _p.TsRecord(TravelStationReceipt.Passed, TravelStationReceipt.Location(_systemId, _startPoiId), _session, null,
                "interiorFacts=" + StationSlice(_dockWindow).Count(s => s.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed)
                + " (interior/physical ordering deliberately unasserted)");
        }

        // Residual matrix cells this phase deliberately does not attempt. They are optional rows:
        // they never count as coverage and can never make the phase pass.
        private void RecordResidualMatrix()
        {
            void Cell(string id, string description, string reason)
            {
                _p.TsCase(id, description);
                _p.TsRecord(TravelStationReceipt.NotRun, "", _session, null, reason);
            }
            Cell("cross-system-jumpgate", "Cross-system jump-gate leg (JumpToSystem) request/arrival and fast-lane chaining.",
                "Out of phase " + TravelStationReceipt.Phase + ": needs a gate-route fixture and a cross-system readiness plan.");
            Cell("cross-system-wormhole", "Cross-system wormhole leg (JumpToWormhole) request/arrival.",
                "Out of phase " + TravelStationReceipt.Phase + ": needs a connected usable wormhole pair fixture.");
            Cell("empty-origin-reroute", "Re-route while the origin scene is already unloaded (TravelInSystem warp-start departure evidence).",
                "Out of phase " + TravelStationReceipt.Phase + ": needs a controlled in-transit re-route fixture.");
            Cell("restore-relink-dock", "Restore/relink Dock() suppression (interior scene current at factory time).",
                "Out of phase " + TravelStationReceipt.Phase + ": needs a different-docking-size re-init fixture.");
            Cell("stale-session-replay", "Old-session coroutine steps after a replacement load emit nothing into the new session.",
                "Out of phase " + TravelStationReceipt.Phase + ": needs a load injected mid-coroutine, which is not safe to drive here.");
        }

        // --- shared driving ---------------------------------------------------------------

        // Drives one native route. hops[0] is requested through the real player entry point; any
        // further hop is appended to GamePlayer.waypoints as explicit SETUP (the same list a native
        // multi-hop route uses) and is advanced only by native Travel()/TravelToNextWaypoint.
        private IEnumerable<object?> DriveRoute(object[] hops, string originPoiId)
        {
            _routeDrove = false;
            foreach (var frame in ReadyToTravel(hops[0])) yield return frame;
            if (!_canTravel)
            {
                NotRun("Native CanWeTravel refused the route to " + (string)SpGet(hops[0], "guid")!);
                yield break;
            }
            int offset = Travel.Count;
            Require(!(bool)_travelActive.Invoke(_travel, null)!, "Native travel was already active before the case.");
            Require((bool)_tryInitiateTravel.Invoke(_travel, new[] { hops[0] })!, "Native TryInitiateTravel refused the route.");
            var waypoints = (IList)SpGet(Player, "waypoints")!;
            Require(waypoints.Count == 1 && ReferenceEquals(waypoints[0], hops[0]), "The native route did not produce the expected single first waypoint.");
            for (int hop = 1; hop < hops.Length; hop++) waypoints.Add(hops[hop]); // labelled setup only
            var hopIds = hops.Select(h => (string)SpGet(h, "guid")!).ToArray();
            for (int hop = 0; hop < hops.Length; hop++)
            {
                int arrivals = hop + 1;
                foreach (var frame in AwaitOrFail(() => Slice(offset).Count(t => t.Kind == TravelTransitionKind.Arrived) >= arrivals,
                    240, "native arrival " + arrivals + "/" + hops.Length + " at " + hopIds[hop])) yield return frame;
            }
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(t => t.Kind == TravelTransitionKind.RouteCompleted),
                60, "native final route boundary at " + hopIds[hops.Length - 1])) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var slice = Slice(offset);
            var failure = TravelStationReceipt.CheckRoute(slice, _session, _systemId, originPoiId, hopIds);
            Require(failure == null, failure!);
            var last = hops[hops.Length - 1];
            Require(ReferenceEquals(SpGet(Player, "currentPointOfInterest"), last), "The native current POI is not the final hop after RouteCompleted.");
            var manager = SpGet(_travel, "localPoiManager");
            Require(Alive(manager) && ReferenceEquals(SpGet(manager!, "poi"), last) && (bool)SpGet(manager!, "initializedAndReady")!,
                "The native local manager is not the initialized manager of the final hop.");
            Require((bool)_localPoiReady.Invoke(_travel, null)!, "Native IsLocalPoiReady is false after arrival.");
            Require(!(bool)_travelActive.Invoke(_travel, null)!, "Native travel is still active after the final route boundary.");
            Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints remain after the final route boundary.");
            Require(TravelStationReceipt.Same(ModApi.Travel!.CurrentLocation, _systemId, hopIds[hops.Length - 1]),
                "Public CurrentLocation does not match the arrived final hop.");
            _p.TsRecord(TravelStationReceipt.Passed, TravelStationReceipt.Location(_systemId, hopIds[hops.Length - 1]), _session,
                slice[slice.Count - 1].OperationId, "hops=" + string.Join(">", hopIds) + "; facts=" + slice.Count
                + "; setupWaypoints=" + (hops.Length - 1));
            _routeDrove = true;
        }

        // Native travel refuses for three real seconds after a warp start (delayTravelAttempt), so
        // availability is sampled after that window instead of being reported as a native refusal.
        private IEnumerable<object?> ReadyToTravel(object target)
        {
            foreach (var frame in PollFor(() => false, 4)) yield return frame;
            _canTravel = (bool)_canWeTravel.Invoke(_travel, new[] { target })!;
        }

        private static IEnumerable<object?> PollFor(Func<bool> ready, float seconds)
        {
            Time.timeScale = 1;
            float until = Time.realtimeSinceStartup + seconds;
            while (!ready() && Time.realtimeSinceStartup < until) yield return null;
        }

        // Case-owned, explicit deadline. A timeout or a failed session is a recorded case failure.
        private IEnumerable<object?> AwaitOrFail(Func<bool> ready, float seconds, string description)
        {
            Time.timeScale = 1;
            float until = Time.realtimeSinceStartup + seconds;
            while (!ready())
            {
                Require(_p._api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for " + description + ".");
                Require(Time.realtimeSinceStartup < until, "Timed out after " + seconds + "s waiting for " + description + ".");
                yield return null;
            }
        }

        private void NotRun(string reason) => _p.TsRecord(TravelStationReceipt.NotRun, "", _session, null, reason);

        // Minimal, safe in-system targets, nearest first: visible, non-dynamic, never a gate or
        // wormhole (whose routes hand off to the cross-system machinery this phase does not
        // qualify), and never another SpaceStation, because arriving at a friendly station makes
        // native CheckForDocking dock there and the phase must own exactly one dock/undock pair.
        // POI danger is deliberately NOT inspected: MapPointOfInterest.totalEnemyCount forces
        // EnsureContentGenerated(), and observation must not generate native content.
        private object[] SafeTargets()
        {
            var system = SpGet(Player, "currentSystem");
            var current = SpGet(Player, "currentPointOfInterest");
            var position = (Vector2)SpGet(Player, "mapPosition")!;
            var map = SpGet(_mapType, "current");
            if (map == null) return Array.Empty<object>();
            return ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(p => ReferenceEquals(SpGet(p, "system"), system) && !ReferenceEquals(p, current))
                .Where(p => !(bool)SpGet(p, "hidden")! && !(bool)SpGet(p, "isDynamicPoi")!)
                .Where(p => !_jumpGateType.IsInstanceOfType(p) && !_wormholeType.IsInstanceOfType(p) && !_stationType.IsInstanceOfType(p))
                .OrderBy(p => Vector2.Distance((Vector2)SpGet(p, "position")!, position))
                .ToArray();
        }

        private string? DockingState(object ship)
        {
            var data = SpGet(ship, "spaceShipData");
            return data == null ? null : SpGet(data, "dockingState")?.ToString();
        }

        private static Type Named(string name) => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name, "type");

        internal static bool Alive(object? value) => value is UnityEngine.Object unity ? (bool)unity : value != null;

        internal static MethodInfo Bind(Type type, string name, Type returnType, params Type[] parameters)
        {
            var method = AccessTools.Method(type, name, parameters);
            Require(method != null && !method.IsStatic && method.ReturnType == returnType
                && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters),
                "Native member shape changed: " + type.FullName + "." + name);
            return method!;
        }

        internal static object? CallExact(object target, string name, Type returnType, params Type[] parameters)
            => Bind(target.GetType(), name, returnType, parameters).Invoke(target, null);
    }
}
