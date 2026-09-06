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
    // Per-case native driver for phase travel-cross-system-v1. It runs inside the plugin iterator
    // context so real Unity coroutines drive every transition, and it only calls vanilla entry
    // points a player action would call: TravelManager.CanWeTravel/TryInitiateTravel (the map
    // travel action), the station exit action, and the read-only native route planner
    // TravelManager.GenerateShortestRoute for fixture selection.
    //
    // Each case is a two-step player route: an in-system approach to the gate/wormhole (the native
    // arrival parks there, because the emptied waypoint list makes the native auto-handoff a
    // no-op), then the actual cross-system request from it. The cross hop itself is entirely
    // native: TheGate/TheWormhole move the ship, cross the threshold and start JumpToSystem /
    // JumpToWormhole, whose owned iterator loads the destination scene and waits for the
    // destination manager. The pilot never advances a jump iterator, never assigns a location and
    // never teleports.
    private sealed class TravelCrossSystemDriver
    {
        private readonly Plugin _p;
        private readonly Type _travelType, _poiType, _mapType, _gameplayType, _shipType;
        private readonly Type _jumpGateType, _wormholeType, _stationType, _exteriorType, _interiorType, _dockingOptionType;
        private readonly Type _systemType, _wormholeSpawnerType;
        private readonly MethodInfo _tryInitiateTravel, _canWeTravel, _travelActive, _localPoiReady, _shortestRoute;
        private readonly MethodInfo _getTargetPoi, _connectedWormholes, _startUndocking, _exitSpacestation, _getDockingOption;
        private readonly MethodInfo _placeWormhole, _sectorIsUnlocked;
        // Captured ONLY at this case's own fresh fixture load and readiness boundary. It is never
        // taken before a load (the load destroys the previous manager) and it is never re-bound to
        // a replacement: a destroyed or replaced instance is a recorded failure, not a new owner.
        private NativeCaseOwner? _owner;
        private Guid _session;
        private string _systemId = "";
        private string _startPoiId = "";
        private bool _prepared;
        private string _notPrepared = "";
        private bool _fixtureCreated;

        internal TravelCrossSystemDriver(Plugin p)
        {
            _p = p;
            _travelType = Named("Behaviour.Managers.TravelManager");
            _poiType = Named("Source.Galaxy.MapPointOfInterest");
            _mapType = Named("Source.Galaxy.GalaxyMapData");
            _gameplayType = Named("GameplayManager");
            _shipType = Named("Behaviour.Unit.SpaceShip");
            _jumpGateType = Named("Source.Galaxy.POI.JumpGate");
            _wormholeType = Named("Source.Galaxy.POI.Wormhole");
            _stationType = Named("Source.Galaxy.POI.SpaceStation");
            _exteriorType = Named("SpacestationExteriorManager");
            _interiorType = Named("Behaviour.UI.Spacestation.SpaceStationInterior");
            _dockingOptionType = Named("Behaviour.Spacestation.Docking.DockingOption");
            _systemType = Named("Source.Galaxy.SystemMapData");
            _wormholeSpawnerType = Named("Source.Simulation.World.WormholeSpawner");
            // Exact declared signatures: a name-only lookup can bind the wrong overload.
            _tryInitiateTravel = TravelStationDriver.Bind(_travelType, "TryInitiateTravel", typeof(bool), _poiType);
            _canWeTravel = TravelStationDriver.Bind(_travelType, "CanWeTravel", typeof(bool), _poiType);
            _travelActive = TravelStationDriver.Bind(_travelType, "TravelActive", typeof(bool));
            _localPoiReady = TravelStationDriver.Bind(_travelType, "IsLocalPoiReady", typeof(bool));
            _shortestRoute = TravelStationDriver.Bind(_travelType, "GenerateShortestRoute",
                typeof(List<>).MakeGenericType(_poiType), _poiType);
            _getTargetPoi = TravelStationDriver.Bind(_jumpGateType, "GetTargetPOI", _poiType);
            _connectedWormholes = TravelStationDriver.Bind(_wormholeType, "GetConnectedWormholes",
                typeof(List<>).MakeGenericType(_wormholeType));
            _startUndocking = TravelStationDriver.Bind(_exteriorType, "StartUndocking", typeof(void));
            _exitSpacestation = TravelStationDriver.Bind(_interiorType, "ExitSpacestation", typeof(void));
            _getDockingOption = TravelStationDriver.Bind(_exteriorType, "GetDockingOption", _dockingOptionType, _shipType);
            // The ONLY native content factory this phase may call, and only under the explicit
            // opt-in fixture selection. Bound by its exact declared static signature.
            _placeWormhole = BindStatic(_wormholeSpawnerType, "PlaceWormhole", _wormholeType,
                _systemType, typeof(bool), typeof(List<>).MakeGenericType(_wormholeType));
            _sectorIsUnlocked = TravelStationDriver.Bind(Named("Source.Galaxy.SectorMapData"), "IsUnlocked", typeof(bool));
            // Deliberately no manager instance here: the driver is constructed before the first
            // case loads its fixture, and that load destroys whatever manager exists now.
        }

        // The captured owner, proven to still be the live current manager/player of this case's
        // session before every native drive and every native read. A wrong or destroyed instance
        // fails here with identity/liveness/session diagnostics instead of reaching native code
        // that would start a coroutine on a destroyed behaviour.
        private object NativeTravel(string action)
        {
            var owner = _owner;
            Require(owner != null, "Refusing " + action + ": this case captured no native owner at its fixture-load boundary.");
            var failure = owner!.CheckCurrent(action, _p._api!.CurrentSession?.Id,
                SpGet(_travelType, "Instance"), SpGet(_p._player, "current"), TravelStationDriver.Alive);
            Require(failure == null, failure!);
            return owner.TravelManager;
        }

        private List<TravelTransition> Travel => _p.PendingCrossSystemTravel!;
        // Case-local window: every fact observed after this case started driving, INCLUDING facts of
        // another session. Foreign facts are rejected by the validators, never filtered away here.
        private List<TravelTransition> Slice(int offset) => TravelStationReceipt.Window(Travel, offset);
        private object Player => _p.CurrentPlayer;
        private object? PlayerShip => SpGet(SpGet(_gameplayType, "Instance")!, "spaceShip");

        internal IEnumerable<object?> Run()
        {
            yield return null;
            foreach (var frame in CaseCrossSystem(TravelCrossSystemReceipt.JumpGateCase,
                TravelCrossSystemReceipt.JumpGateDescription, TravelMode.JumpGate)) yield return frame;
            EndCase();
            foreach (var frame in CaseCrossSystem(TravelCrossSystemReceipt.WormholeCase,
                TravelCrossSystemReceipt.WormholeDescription, TravelMode.Wormhole)) yield return frame;
            EndCase();
            // The cases deliberately end in another system. Restore the fixture the later pilots
            // expect; this is harness cleanup and is never recorded as coverage.
            foreach (var frame in _p.SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            _p.XsCheckpoint();
        }

        // --- cases ------------------------------------------------------------------------

        private IEnumerable<object?> CaseCrossSystem(string caseId, string description, TravelMode mode)
        {
            _p.XsCase(caseId, description);
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            object? source = null, destination = null;
            string reason = "";
            if (mode == TravelMode.JumpGate) source = SelectJumpGate(out destination, out reason);
            else source = SelectWormhole(out destination, out reason);
            if (source == null && mode == TravelMode.Wormhole && _p.TravelWormholeFixtureSelected)
            {
                // Explicit opt-in: this fixture world has no usable pair, so create one with the
                // game's own factory BEFORE the case window opens. Preparation, never evidence.
                foreach (var frame in CreateWormholeFixture(reason)) yield return frame;
                if (_fixtureCreated) source = SelectWormhole(out destination, out reason);
            }
            if (source == null || destination == null) { NotRun(reason); yield break; }
            foreach (var frame in DriveCrossSystemRoute(mode, source, destination)) yield return frame;
        }

        // The whole case: the window opens BEFORE anything is driven, so no earlier fact can
        // satisfy it. Step one is an in-system approach leg to the gate/wormhole; step two is the
        // actual cross-system request from it, which native code turns into the jump routine.
        private IEnumerable<object?> DriveCrossSystemRoute(TravelMode mode, object source, object destination)
        {
            var sourceId = (string)SpGet(source, "guid")!;
            var destinationSystemId = (string)SpGet(SpGet(destination, "system")!, "guid")!;
            var destinationId = (string)SpGet(destination, "guid")!;
            bool approach = !ReferenceEquals(SpGet(Player, "currentPointOfInterest"), source);
            int offset = Travel.Count;
            var routes = new List<IReadOnlyList<TravelCrossSystemReceipt.ExpectedLeg>>();
            Require(!(bool)_travelActive.Invoke(NativeTravel("the cross-system case"), null)!, "Native travel was already active before the cross-system case.");
            if (approach)
            {
                if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the in-system approach"), new[] { source })!)
                {
                    NotRun("Native CanWeTravel refused the in-system approach to " + sourceId + ".");
                    yield break;
                }
                Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the in-system approach"), new[] { source })!,
                    "Native TryInitiateTravel refused the in-system approach.");
                var waypoints = (IList)SpGet(Player, "waypoints")!;
                Require(waypoints.Count == 1 && ReferenceEquals(waypoints[0], source),
                    "The native in-system approach did not produce the expected single waypoint.");
                foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Kind == TravelTransitionKind.Arrived) >= 1,
                    TravelCrossSystemReceipt.ApproachArrivalSeconds, "native in-system arrival at " + sourceId)) yield return frame;
                foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Kind == TravelTransitionKind.RouteCompleted) >= 1,
                    TravelCrossSystemReceipt.BoundarySeconds, "native approach route boundary at " + sourceId)) yield return frame;
                foreach (var frame in Settle()) yield return frame;
                Require(ReferenceEquals(SpGet(Player, "currentPointOfInterest"), source), "The native current POI is not the approached " + mode + " POI.");
                routes.Add(new[] { new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem,
                    _systemId, _startPoiId, _systemId, sourceId, _systemId, sourceId) });
            }
            // Native travel refuses for three real seconds after a warp start (delayTravelAttempt),
            // so availability is sampled after that window instead of being read as a refusal.
            foreach (var frame in PollFor(() => false, TravelCrossSystemReceipt.TravelReadySeconds)) yield return frame;
            var direct = NativeRoute(destination);
            if (direct.Count != 1 || !ReferenceEquals(direct[0], destination))
            {
                // A planner preference, not an API defect: refuse to drive a route this phase
                // cannot model instead of asserting against a different native plan.
                NotRun("The native route planner plans " + direct.Count + " hop(s) from the " + mode
                    + " POI instead of one direct cross-system hop to " + destinationId + ".");
                yield break;
            }
            // The RAW request the adapter must preserve, captured from the loaded world BEFORE the
            // jump can rewrite anything (the tutorial exit rewrites a gate's target in flight).
            var requestedSystemId = mode == TravelMode.JumpGate ? (string)SpGet(source, "targetSystemGuid")! : destinationSystemId;
            var requestedPoiId = mode == TravelMode.JumpGate ? (string)SpGet(source, "targetPoiGuid")! : destinationId;
            int crossOffset = Travel.Count;
            if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the cross-system hop"), new[] { destination })!)
            {
                NotRun("Native CanWeTravel refused the cross-system hop to " + destinationId + ".");
                yield break;
            }
            Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the cross-system hop"), new[] { destination })!,
                "Native TryInitiateTravel refused the cross-system hop.");
            var crossWaypoints = (IList)SpGet(Player, "waypoints")!;
            Require(crossWaypoints.Count == 1 && ReferenceEquals(crossWaypoints[0], destination),
                "The native cross-system route did not produce the expected single cross-system waypoint.");
            // The native approach across the gate/wormhole is a physical manoeuvre driven by
            // TheGate/TheWormhole, so the deadline is generous but explicit; a timeout is a
            // recorded failure, never a silent skip.
            foreach (var frame in AwaitOrFail(() => Slice(crossOffset).Any(fact => fact.Mode == mode),
                TravelCrossSystemReceipt.HandoffSeconds, "native " + mode + " handoff into the jump routine")) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(crossOffset).Any(fact => fact.Mode == mode && fact.Kind == TravelTransitionKind.Arrived),
                TravelCrossSystemReceipt.JumpArrivalSeconds, "native " + mode + " arrival in " + destinationSystemId)) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(crossOffset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                TravelCrossSystemReceipt.BoundarySeconds, "native final route boundary after the " + mode + " hop")) yield return frame;
            foreach (var frame in Settle()) yield return frame;

            // The public facts are compared against the loaded world, never the other way round.
            var actualSystem = SpGet(Player, "currentSystem");
            var actualPoi = SpGet(Player, "currentPointOfInterest");
            Require(actualSystem != null, "The native player has no current system after the cross-system hop.");
            var actualSystemId = (string)SpGet(actualSystem!, "guid")!;
            var actualPoiId = actualPoi == null ? null : (string)SpGet(actualPoi, "guid")!;
            Require(actualSystemId != _systemId, "The native player is still in the origin system after a cross-system hop.");
            routes.Add(new[] { new TravelCrossSystemReceipt.ExpectedLeg(mode,
                _systemId, sourceId, requestedSystemId, requestedPoiId, actualSystemId, actualPoiId) });
            var slice = Slice(offset);
            var failure = TravelCrossSystemReceipt.CheckRoutes(slice, _session, routes);
            Require(failure == null, failure!);
            failure = TravelCrossSystemReceipt.CheckJumpIteratorEvidence(slice, _p._xsSnapshots, mode);
            Require(failure == null, failure!);
            var arrivedOwner = NativeTravel("the cross-system arrival checks");
            var manager = SpGet(arrivedOwner, "localPoiManager");
            Require(TravelStationDriver.Alive(manager) && ReferenceEquals(SpGet(manager!, "poi"), actualPoi)
                && (bool)SpGet(manager!, "initializedAndReady")!,
                "The native local manager is not the initialized manager of the arrived cross-system POI.");
            Require(manager!.GetType().FullName == TravelCrossSystemReceipt.ManagerTypeFor(mode),
                "The arrived local manager is " + manager.GetType().FullName + " instead of " + TravelCrossSystemReceipt.ManagerTypeFor(mode) + ".");
            Require((bool)_localPoiReady.Invoke(arrivedOwner, null)!, "Native IsLocalPoiReady is false after the cross-system arrival.");
            Require(!(bool)_travelActive.Invoke(arrivedOwner, null)!, "Native travel is still active after the final route boundary.");
            Require(!(bool)SpGet(arrivedOwner, "usingJumpgate")!, "The native jump routine is still running after the final route boundary.");
            Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints remain after the final route boundary.");
            Require(TravelStationReceipt.Same(ModApi.Travel!.CurrentLocation, actualSystemId, actualPoiId),
                "Public CurrentLocation does not match the arrived cross-system location.");
            var crossFacts = slice.Where(fact => fact.Mode == mode).ToArray();
            var arrived = crossFacts.First(fact => fact.Kind == TravelTransitionKind.Arrived);
            bool redirected = requestedSystemId != actualSystemId || requestedPoiId != actualPoiId;
            Pass(TravelStationReceipt.Location(actualSystemId, actualPoiId), arrived.OperationId,
                TravelStationReceipt.Evidence(slice, null),
                "mode=" + mode + "; approachLeg=" + approach + "; source=" + TravelStationReceipt.Location(_systemId, sourceId)
                + "; requestedRaw=" + TravelStationReceipt.Location(requestedSystemId, requestedPoiId)
                + "; redirected=" + redirected + "; facts=" + slice.Count
                + "; arrivalSnapshot=" + (_p._xsSnapshots.TryGetValue(arrived.Sequence, out var snapshot) ? snapshot.ToDetail() : "<none>"));
        }

        // --- fixture preparation ----------------------------------------------------------

        // Each case starts from the same fresh fixture session: the cases legitimately end in
        // another system, so reusing the previous world would make the second case's start
        // undefined. This is documented fixture setup, never arrival evidence.
        private IEnumerable<object?> Prepare()
        {
            // The previous owner belongs to the session this load replaces; drop it before the
            // load so nothing can drive a manager the load is about to destroy.
            _prepared = false; _notPrepared = ""; _owner = null; _p.CrossSystemOwner = null;
            foreach (var frame in _p.SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            _session = _p._api!.CurrentSession!.Id;
            var session = _session;
            foreach (var frame in _p.Wait(() => ModApi.Travel?.SessionId == session
                && ModApi.Travel.CurrentLocation != null && _p.NativeTravelReady(), "travel service binding and native POI readiness")) yield return frame;
            var system = SpGet(Player, "currentSystem");
            var poi = SpGet(Player, "currentPointOfInterest");
            if (system == null || poi == null)
            {
                _notPrepared = "The fixture did not load at a known native system and POI (system=" + (system != null) + ", poi=" + (poi != null) + ").";
                yield break;
            }
            _systemId = (string)SpGet(system!, "guid")!;
            _startPoiId = (string)SpGet(poi!, "guid")!;
            // The one and only capture boundary: this case's own freshly loaded, ready session.
            var manager = SpGet(_travelType, "Instance");
            var owner = SpGet(_p._player, "current");
            if (!TravelStationDriver.Alive(manager) || !TravelStationDriver.Alive(owner))
            {
                _notPrepared = "The freshly loaded fixture has no live native travel manager/player to own the case (manager="
                    + NativeCaseOwner.Identity(manager, TravelStationDriver.Alive) + ", player="
                    + NativeCaseOwner.Identity(owner, TravelStationDriver.Alive) + ").";
                yield break;
            }
            _owner = new NativeCaseOwner(_session, manager!, owner!, _systemId, _startPoiId);
            _p.CrossSystemOwner = _owner;
            foreach (var frame in Undock(poi!)) yield return frame;
            _prepared = _notPrepared.Length == 0;
        }

        // The player's own exit action, exactly as the in-system phase drives it. It is preparation
        // for the route, so it is not asserted here; the phase's evidence is the travel facts.
        private IEnumerable<object?> Undock(object startPoi)
        {
            var ship = PlayerShip;
            if (!TravelStationDriver.Alive(ship)) { _notPrepared = "The fixture has no live player ship to undock."; yield break; }
            if (DockingState(ship!) == null) yield break; // Already in space.
            var exterior = SpGet(_exteriorType, "Instance");
            if (!_stationType.IsInstanceOfType(startPoi) || !TravelStationDriver.Alive(exterior))
            {
                _notPrepared = "The fixture ship reports docking state " + DockingState(ship!) + " without a live station exterior to exit.";
                yield break;
            }
            var interior = SpGet(_interiorType, "instance");
            if (TravelStationDriver.Alive(interior)) _exitSpacestation.Invoke(interior!, null);
            else _startUndocking.Invoke(exterior!, null);
            foreach (var frame in AwaitOrFail(() => DockingState(ship!) != "Docked"
                && !TravelStationDriver.Alive(SpGet(exterior!, "undockingRoutine"))
                && !TravelStationDriver.Alive(_getDockingOption.Invoke(exterior!, new[] { ship })),
                TravelCrossSystemReceipt.UndockSeconds, "native undock before the cross-system route")) yield return frame;
        }

        // --- opt-in native wormhole fixture preparation ------------------------------------

        // Explicitly selected disposable sandbox test data, created ONLY with the game's own
        // factory (WormholeSpawner.PlaceWormhole) in the freshly loaded fixture clone, before the
        // case window opens and before anything is driven. It creates a connected pair between the
        // player's actual current system and an actual other native system; it never teleports,
        // never assigns a location, never touches the original save files and is never travel
        // evidence. The route itself is afterwards driven exactly like the unmodified phase.
        private IEnumerable<object?> CreateWormholeFixture(string missingReason)
        {
            _p.XsCase(TravelCrossSystemReceipt.WormholeFixtureCase, TravelCrossSystemReceipt.WormholeFixtureDescription);
            int offset = Travel.Count;
            var currentSystem = SpGet(Player, "currentSystem")!;
            int before = AllPois().Count(poi => _wormholeType.IsInstanceOfType(poi));
            var destinationSystem = SelectFixtureDestinationSystem(out string refusal);
            if (destinationSystem == null)
            {
                NotRun("No usable destination system for the opt-in wormhole fixture: " + refusal + " (" + missingReason + ")");
                _p.XsCase(TravelCrossSystemReceipt.WormholeCase, TravelCrossSystemReceipt.WormholeDescription);
                yield break;
            }
            // Native factory only: create the destination side first, then the current-system side
            // with the destination as its declared target, then complete the reciprocal directed
            // link on the list the factory itself assigns. Both POIs are ours; no existing POI is
            // relinked and no campaign-wide unlock/spawn is performed.
            var destination = _placeWormhole.Invoke(null, new object?[] { destinationSystem, true, null })!;
            var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_wormholeType))!;
            targets.Add(destination);
            var source = _placeWormhole.Invoke(null, new object?[] { currentSystem, true, targets })!;
            ((IList)SpGet(destination, "targetWormholeGuids")!).Add((string)SpGet(source, "guid")!);
            // Bounded verification that the native planner really routes the created pair; a
            // timeout is a recorded failure, never a silent skip.
            foreach (var frame in AwaitOrFail(() => SelectWormhole(out _, out _) != null,
                TravelCrossSystemReceipt.FixtureCreationSeconds, "the created native wormhole pair to become routable")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            int after = AllPois().Count(poi => _wormholeType.IsInstanceOfType(poi));
            var sourceSystemId = (string)SpGet(SpGet(source, "system")!, "guid")!;
            var sourceId = (string)SpGet(source, "guid")!;
            var destinationSystemId = (string)SpGet(SpGet(destination, "system")!, "guid")!;
            var destinationId = (string)SpGet(destination, "guid")!;
            var window = Slice(offset);
            // Creating map data is not movement: the travel surface must have stayed silent.
            var failure = TravelCrossSystemReceipt.CheckFixtureCreationWindow(window);
            Require(failure == null, failure!);
            failure = TravelCrossSystemReceipt.CheckFixtureCreation(before, after, _systemId,
                sourceSystemId, sourceId, destinationSystemId, destinationId);
            Require(failure == null, failure!);
            _p.XsRecord(TravelCrossSystemReceipt.WormholeFixtureCase, TravelCrossSystemReceipt.WormholeFixtureDescription,
                TravelStationReceipt.Passed, TravelStationReceipt.Location(sourceSystemId, sourceId), _session, null, "",
                TravelCrossSystemReceipt.DescribeFixtureCreation(before, after, sourceSystemId, sourceId,
                    destinationSystemId, destinationId, window.Count));
            _p.XsCheckpoint();
            _fixtureCreated = true;
            _p.XsCase(TravelCrossSystemReceipt.WormholeCase, TravelCrossSystemReceipt.WormholeDescription);
        }

        // An actual other native system for the created destination. Direct jump-gate neighbours
        // are excluded because the native planner enqueues the current system's gate edges before
        // its wormhole edges, so a neighbour destination would be planned through the gate instead
        // of the created wormhole. Pocket systems, sectorless systems and systems that already own
        // a wormhole are excluded, and the candidate must sit in the SAME native map quadrant as
        // the player and in a sector the player can already reach: either the player's own sector
        // or one the native SectorMapData.IsUnlocked() reports as unlocked. Every field read here
        // is a plain native field/method, never the lazy name generator. The remaining candidates
        // are ordered deterministically (map distance, then guid).
        private object? SelectFixtureDestinationSystem(out string reason)
        {
            var currentSystem = SpGet(Player, "currentSystem")!;
            var currentSector = SpGet(currentSystem, "sector");
            if (currentSector == null) { reason = "the player's current system has no sector"; return null; }
            // Authoritative parent reference: a system's quadrant is its sector's quadrant field.
            var quadrant = (int)SpGet(currentSector, "quadrant")!;
            var neighbours = new HashSet<string>(SystemPois()
                .Where(poi => _jumpGateType.IsInstanceOfType(poi) && (bool)SpGet(poi, "canUseJumpGate")!)
                .Select(poi => (string?)SpGet(poi, "targetSystemGuid") ?? ""), StringComparer.Ordinal);
            var map = SpGet(_mapType, "current");
            if (map == null) { reason = "no live galaxy map"; return null; }
            var origin = (Vector2)SpGet(currentSystem, "mapPosition")!;
            var sectored = ((IEnumerable)SpGet(map, "allSystems")!).Cast<object>()
                .Where(system => SpGet(system, "sector") != null)
                .Where(system => (string)SpGet(system, "guid")! != _systemId)
                .ToArray();
            var candidates = sectored
                .Where(system => !neighbours.Contains((string)SpGet(system, "guid")!))
                .Where(system => !(bool)SpGet(system, "pocketSystem")!)
                .Where(system => !((IEnumerable)SpGet(system, "pointsOfInterest")!).Cast<object>().Any(poi => _wormholeType.IsInstanceOfType(poi)))
                .Where(system => (int)SpGet(SpGet(system, "sector")!, "quadrant")! == quadrant)
                .Where(system => SectorReachable(SpGet(system, "sector")!, currentSector))
                .OrderBy(system => Vector2.Distance((Vector2)SpGet(system, "mapPosition")!, origin))
                .ThenBy(system => (string)SpGet(system, "guid")!, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                reason = "no native system in quadrant " + quadrant + " is a reachable non-pocket, non-neighbour, wormhole-free destination (sectoredSystems="
                    + sectored.Length + ")";
                return null;
            }
            reason = "";
            return candidates[0];
        }

        // The player's own sector is reachable by definition; any other sector must be reported
        // unlocked by the game's own read-only check.
        private bool SectorReachable(object sector, object currentSector)
            => ReferenceEquals(sector, currentSector) || (bool)_sectorIsUnlocked.Invoke(sector, null)!;

        // --- fixture selection ------------------------------------------------------------

        // The native route planner is a read-only BFS over the loaded map, so it selects the hop
        // without driving anything and without assuming which gate the planner prefers.
        private List<object> NativeRoute(object destination)
            => ((IEnumerable)_shortestRoute.Invoke(NativeTravel("the native route planner"), new[] { destination })!).Cast<object>().ToList();

        // A usable, non-tutorial gate in the current system whose paired POI the native planner
        // really routes to through exactly this gate.
        private object? SelectJumpGate(out object? destination, out string reason)
        {
            destination = null;
            var current = SpGet(Player, "currentPointOfInterest");
            var candidates = SystemPois()
                .Where(poi => poi.GetType() == _jumpGateType)
                .Where(poi => !(bool)SpGet(poi, "hidden")! && !(bool)SpGet(poi, "isDynamicPoi")!)
                .Where(poi => (bool)SpGet(poi, "canUseJumpGate")!)
                .Where(poi => SpGet(poi, "targetSystem") != null
                    && (string)SpGet(poi, "targetSystemGuid")! != _systemId
                    && !string.IsNullOrEmpty((string?)SpGet(poi, "targetPoiGuid")))
                .Where(poi => !IsTutorialExitGate(poi))
                .OrderBy(Distance)
                .ToArray();
            foreach (var gate in candidates)
            {
                var target = _getTargetPoi.Invoke(gate, null);
                if (target == null) continue;
                var route = NativeRoute(target);
                bool atGate = ReferenceEquals(current, gate);
                bool planned = atGate
                    ? route.Count == 1 && ReferenceEquals(route[0], target)
                    : route.Count == 2 && ReferenceEquals(route[0], gate) && ReferenceEquals(route[1], target);
                if (!planned) continue;
                destination = target;
                reason = "";
                return gate;
            }
            reason = "No usable non-tutorial jump gate in the current system plans a single native gate hop: gates="
                + SystemPois().Count(poi => _jumpGateType.IsInstanceOfType(poi))
                + ", exactType=" + SystemPois().Count(poi => poi.GetType() == _jumpGateType)
                + ", candidates=" + candidates.Length + ".";
            return null;
        }

        // A usable wormhole in the current system with a native connection the planner really
        // routes through as a single wormhole hop.
        private object? SelectWormhole(out object? destination, out string reason)
        {
            destination = null;
            var current = SpGet(Player, "currentPointOfInterest");
            var candidates = SystemPois()
                .Where(poi => poi.GetType() == _wormholeType)
                .Where(poi => !(bool)SpGet(poi, "hidden")! && !(bool)SpGet(poi, "isDynamicPoi")!)
                .Where(poi => (bool)SpGet(poi, "canUseWormhole")!)
                .OrderBy(Distance)
                .ToArray();
            foreach (var wormhole in candidates)
            {
                foreach (var target in ((IEnumerable)_connectedWormholes.Invoke(wormhole, null)!).Cast<object>())
                {
                    var route = NativeRoute(target);
                    bool atWormhole = ReferenceEquals(current, wormhole);
                    bool planned = atWormhole
                        ? route.Count == 1 && ReferenceEquals(route[0], target)
                        : route.Count == 2 && ReferenceEquals(route[0], wormhole) && ReferenceEquals(route[1], target);
                    if (!planned) continue;
                    destination = target;
                    reason = "";
                    return wormhole;
                }
            }
            var all = AllPois();
            reason = "No usable connected wormhole pair in this fixture: galaxyWormholes=" + all.Count(poi => _wormholeType.IsInstanceOfType(poi))
                + ", currentSystemWormholes=" + SystemPois().Count(poi => _wormholeType.IsInstanceOfType(poi))
                + ", usableCandidates=" + candidates.Length
                + ", wormholesUnlocked=" + SpGet(Player, "wormholesUnlocked")
                + "; the native JumpToWormhole routine cannot be driven by this world.";
            return null;
        }

        // Source-derived exclusion: JumpToSystem rewrites its nominal destination through
        // TransitionTutorialToSandbox for the Hermetis -> Canis Majoris gate, which is a one-way
        // world transition and must never be driven by a qualification phase. The stored name field
        // is read directly so the lazy name generator cannot run and consume world randomness.
        private bool IsTutorialExitGate(object gate)
        {
            var system = SpGet(gate, "system");
            var target = SpGet(gate, "targetSystem");
            return system != null && target != null
                && (string?)SpGet(system, "_name") == "Hermetis" && (string?)SpGet(target, "_name") == "Canis Majoris";
        }

        private IEnumerable<object> AllPois()
        {
            var map = SpGet(_mapType, "current");
            return map == null ? Array.Empty<object>() : ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>();
        }

        private IEnumerable<object> SystemPois()
        {
            var system = SpGet(Player, "currentSystem");
            return AllPois().Where(poi => ReferenceEquals(SpGet(poi, "system"), system));
        }

        private float Distance(object poi)
            => Vector2.Distance((Vector2)SpGet(poi, "position")!, (Vector2)SpGet(Player, "mapPosition")!);

        private string? DockingState(object ship)
        {
            var data = SpGet(ship, "spaceShipData");
            return data == null ? null : SpGet(data, "dockingState")?.ToString();
        }

        // --- shared waiting and recording -------------------------------------------------

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
                Require(Time.realtimeSinceStartup < until, "Timed out after " + seconds + "s waiting for " + description
                    + " (" + _p.CrossSystemSnapshot().ToDetail() + ").");
                yield return null;
            }
        }

        private void NotRun(string reason)
        {
            _p.XsRecord(_p._xsCase, _p._xsDescription, TravelStationReceipt.NotRun, "", _session, null, "", reason);
            _p.XsCheckpoint();
        }
        private void Pass(string nativeIdentity, Guid? operation, string evidence, string detail)
        {
            _p.XsRecord(_p._xsCase, _p._xsDescription, TravelStationReceipt.Passed, nativeIdentity, _session, operation, evidence, detail);
            _p.XsCheckpoint();
        }
        // The active event label belongs to a driving case only; between cases nothing may claim it.
        private void EndCase() { _p.XsEndCase(); _p.XsCheckpoint(); }

        private static Type Named(string name) => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name, "type");

        // Exact declared STATIC signature; a name-only lookup can bind a different factory.
        private static MethodInfo BindStatic(Type type, string name, Type returnType, params Type[] parameters)
        {
            var method = AccessTools.Method(type, name, parameters);
            Require(method != null && method.IsStatic && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters),
                "Native member shape changed: " + type.FullName + "." + name);
            return method!;
        }
    }
}
