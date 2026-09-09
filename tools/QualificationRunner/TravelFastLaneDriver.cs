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
    // Native driver for phase travel-fast-lane-v1. It runs inside the plugin iterator context so
    // real Unity coroutines drive every transition, and it only calls vanilla entry points a player
    // action would call: TravelManager.CanWeTravel / TryInitiateTravel (the map travel action) and
    // the station exit action. It writes NOTHING native: the fast-lane unlock flag, the travel
    // multiplier, waypoints, locations and docking state are all read-only here.
    private sealed class TravelFastLaneDriver
    {
        private readonly Plugin _p;
        private readonly Type _travelType, _poiType, _gameplayType, _shipType, _stationType;
        private readonly Type _exteriorType, _interiorType, _dockingOptionType, _jumpGateType, _mapType;
        private readonly Type _miningType, _salvageType, _combatType, _wormholeType, _factionType;
        private readonly MethodInfo _tryInitiateTravel, _canWeTravel, _travelActive, _localPoiReady;
        private readonly MethodInfo _shortestRoute, _getTargetPoi, _startUndocking, _exitSpacestation, _getDockingOption;
        private readonly MethodInfo _isEnemy, _storyMissionPoi;
        private NativeCaseOwner? _owner;
        private Guid _session;
        private string _systemId = "";
        private string _startPoiId = "";
        private bool _prepared;
        private string _notPrepared = "";

        internal TravelFastLaneDriver(Plugin p)
        {
            _p = p;
            _travelType = Named("Behaviour.Managers.TravelManager");
            _poiType = Named("Source.Galaxy.MapPointOfInterest");
            _gameplayType = Named("GameplayManager");
            _shipType = Named("Behaviour.Unit.SpaceShip");
            _stationType = Named("Source.Galaxy.POI.SpaceStation");
            _exteriorType = Named("SpacestationExteriorManager");
            _interiorType = Named("Behaviour.UI.Spacestation.SpaceStationInterior");
            _dockingOptionType = Named("Behaviour.Spacestation.Docking.DockingOption");
            _jumpGateType = Named("Source.Galaxy.POI.JumpGate");
            _mapType = Named("Source.Galaxy.GalaxyMapData");
            _miningType = Named("Source.Galaxy.POI.Mining");
            _salvageType = Named("Source.Galaxy.POI.Salvage");
            _combatType = Named("Source.Galaxy.POI.Combat");
            _wormholeType = Named("Source.Galaxy.POI.Wormhole");
            _factionType = Named("Source.Galaxy.Faction");
            // Exact declared signatures: a name-only lookup can bind the wrong overload.
            _tryInitiateTravel = TravelStationDriver.Bind(_travelType, "TryInitiateTravel", typeof(bool), _poiType);
            _canWeTravel = TravelStationDriver.Bind(_travelType, "CanWeTravel", typeof(bool), _poiType);
            _travelActive = TravelStationDriver.Bind(_travelType, "TravelActive", typeof(bool));
            _localPoiReady = TravelStationDriver.Bind(_travelType, "IsLocalPoiReady", typeof(bool));
            _shortestRoute = TravelStationDriver.Bind(_travelType, "GenerateShortestRoute",
                typeof(List<>).MakeGenericType(_poiType), _poiType);
            _getTargetPoi = TravelStationDriver.Bind(_jumpGateType, "GetTargetPOI", _poiType);
            _startUndocking = TravelStationDriver.Bind(_exteriorType, "StartUndocking", typeof(void));
            _exitSpacestation = TravelStationDriver.Bind(_interiorType, "ExitSpacestation", typeof(void));
            _getDockingOption = TravelStationDriver.Bind(_exteriorType, "GetDockingOption", _dockingOptionType, _shipType);
            _isEnemy = TravelStationDriver.Bind(_factionType, "IsEnemy", typeof(bool), _factionType);
            _storyMissionPoi = TravelStationDriver.Bind(_poiType, "IsStoryMissionPoi", typeof(bool));
        }

        private object NativeTravel(string action)
        {
            var owner = _owner;
            Require(owner != null, "Refusing " + action + ": this phase captured no native owner at its fixture-load boundary.");
            var failure = owner!.CheckCurrent(action, _p._api!.CurrentSession?.Id,
                SpGet(_travelType, "Instance"), SpGet(_p._player, "current"), TravelStationDriver.Alive);
            Require(failure == null, failure!);
            return owner.TravelManager;
        }

        private void RequireOwned(string action) => NativeTravel(action);

        private List<TravelTransition> Travel => _p.PendingFastLaneTravel!;
        private List<StationTransition> Stations => _p.PendingFastLaneStation!;
        private List<TravelTransition> Slice(int offset) => TravelStationReceipt.Window(Travel, offset);
        private List<StationTransition> StationSlice(int offset) => TravelStationReceipt.Window(Stations, offset);
        private object Player => _p.CurrentPlayer;
        private object? PlayerShip => SpGet(SpGet(_gameplayType, "Instance")!, "spaceShip");
        private object? Exterior => SpGet(_exteriorType, "Instance");

        internal IEnumerable<object?> Run()
        {
            yield return null;
            foreach (var frame in CaseFastLaneChain()) yield return frame;
            EndCase();
            // The route legitimately ends in another system. Restore the fixture the later pilots
            // expect; this is harness cleanup and is never recorded as coverage.
            foreach (var frame in _p.SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            _p.FlCheckpoint();
        }

        // --- the driven route ----------------------------------------------------------------

        // ONE native planner route across TWO gates into a third system. In the intermediate system
        // the next waypoint is the second gate, which is exactly what the inspected
        // GamePlayer.DoFastLaneTravel() requires, so the native jump routine takes its charge branch
        // (TheGate.ChargeFastLaneTravelToNextGate) and stores travelMultiplier = 7f. That store is
        // the ONLY one in the whole inspected assembly, so observing the multiplier at 7 during the
        // gate-to-gate leg is proof the branch ran - never merely that the save carries the unlock
        // flag or that the route contained gates.
        //
        // The unlock flag itself is only READ. A fixture without it records an honest NOT-RUN; no
        // save, config or native field is ever written by this phase.
        private IEnumerable<object?> CaseFastLaneChain()
        {
            _p.FlCase(TravelFastLaneReceipt.GateChainCase, TravelFastLaneReceipt.GateChainDescription);
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRunBoth(_notPrepared); yield break; }
            bool unlocked = (bool)SpGet(Player, "fastLaneTravelUnlocked")!;
            if (!unlocked)
            {
                NotRunBoth("The loaded fixture reports fastLaneTravelUnlocked=false. The native fast lane cannot engage, and this phase never writes that flag: it needs a fixture whose own history unlocked it.");
                yield break;
            }
            foreach (var frame in Undock()) yield return frame;
            if (_notPrepared.Length > 0) { NotRunBoth(_notPrepared); yield break; }
            var first = SelectChain(out object? second, out object? destination, out string refusal);
            if (first == null || second == null || destination == null) { NotRunBoth(refusal); yield break; }
            var firstId = (string)SpGet(first, "guid")!;
            var secondId = (string)SpGet(second, "guid")!;
            var destinationId = (string)SpGet(destination, "guid")!;
            var destinationSystemId = (string)SpGet(SpGet(destination, "system")!, "guid")!;
            // The RAW gate targets the adapter must preserve, captured from the loaded world BEFORE
            // any jump can rewrite them.
            var firstTargetSystemId = (string)SpGet(first, "targetSystemGuid")!;
            var firstTargetPoiId = (string)SpGet(first, "targetPoiGuid")!;
            var secondTargetSystemId = (string)SpGet(second, "targetSystemGuid")!;
            var secondTargetPoiId = (string)SpGet(second, "targetPoiGuid")!;
            var intermediateSystemId = (string)SpGet(SpGet(second, "system")!, "guid")!;
            int offset = Travel.Count;
            int stationOffset = Stations.Count;
            foreach (var frame in PollFor(() => false, TravelFastLaneReceipt.TravelReadySeconds)) yield return frame;
            var unsolicited = TravelStationReceipt.CheckNoUnsolicitedTravel("the fast-lane chain", Slice(offset),
                (bool)_travelActive.Invoke(NativeTravel("the fast-lane chain"), null)!, _p.NativeAutonomyDetail());
            Require(unsolicited == null, unsolicited!);
            if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the fast-lane chain"), new[] { destination })!)
            {
                NotRunBoth("Native CanWeTravel refused the fast-lane chain route to " + destinationId + ".");
                yield break;
            }
            Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the fast-lane chain"), new[] { destination })!,
                "Native TryInitiateTravel refused the fast-lane chain route.");
            var frame0 = TravelResilienceReceipt.CheckRequestFrame(Slice(offset), "the accepted fast-lane chain request");
            Require(frame0 == null, frame0 ?? "");
            var waypoints = (IList)SpGet(Player, "waypoints")!;
            Require(waypoints.Count == 3 && ReferenceEquals(waypoints[0], first) && ReferenceEquals(waypoints[1], second)
                && ReferenceEquals(waypoints[2], destination),
                "The native route planner did not produce the expected [gate, gate, destination] waypoint list.");
            // Leg one: the in-system approach to the first gate.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.Arrived),
                TravelFastLaneReceipt.ArrivalSeconds, "the native in-system arrival at the first gate " + firstId)) yield return frame;
            RequireNoEarlyCompletion(offset, "the approach to the first gate");
            // Leg two: the first gate hop.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Mode == TravelMode.JumpGate),
                TravelFastLaneReceipt.HandoffSeconds, "the native handoff into the first jump routine")) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Mode == TravelMode.JumpGate && fact.Kind == TravelTransitionKind.Arrived),
                TravelFastLaneReceipt.JumpArrivalSeconds, "the native jump arrival in " + firstTargetSystemId)) yield return frame;
            RequireNoEarlyCompletion(offset, "the first gate hop");
            // Leg three: the GATE-TO-GATE in-system leg. The native charge branch precedes it, so
            // this is the leg whose facts must be sampled at the 7x transient.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Kind == TravelTransitionKind.Arrived
                && fact.Mode == TravelMode.InSystem) >= 2,
                TravelFastLaneReceipt.ArrivalSeconds, "the native gate-to-gate in-system arrival at " + secondId)) yield return frame;
            RequireNoEarlyCompletion(offset, "the gate-to-gate leg");
            // Leg four: the second gate hop.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Mode == TravelMode.JumpGate
                && fact.Kind == TravelTransitionKind.Requested) >= 2,
                TravelFastLaneReceipt.HandoffSeconds, "the native handoff into the second jump routine")) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Mode == TravelMode.JumpGate
                && fact.Kind == TravelTransitionKind.Arrived) >= 2,
                TravelFastLaneReceipt.JumpArrivalSeconds, "the native jump arrival in " + secondTargetSystemId)) yield return frame;
            RequireNoEarlyCompletion(offset, "the second gate hop");
            // Leg five: the final in-system leg, the only one that may close the route.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Kind == TravelTransitionKind.Arrived
                && fact.Mode == TravelMode.InSystem) >= 3,
                TravelFastLaneReceipt.ArrivalSeconds, "the native final in-system arrival at " + destinationId)) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                TravelFastLaneReceipt.BoundarySeconds, "the native final route boundary at " + destinationId)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var slice = Slice(offset);
            var legs = new[]
            {
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, _systemId, _startPoiId, _systemId, firstId, _systemId, firstId),
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.JumpGate, _systemId, firstId,
                    firstTargetSystemId, firstTargetPoiId, firstTargetSystemId, firstTargetPoiId),
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, intermediateSystemId, firstTargetPoiId,
                    intermediateSystemId, secondId, intermediateSystemId, secondId),
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.JumpGate, intermediateSystemId, secondId,
                    secondTargetSystemId, secondTargetPoiId, secondTargetSystemId, secondTargetPoiId),
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, destinationSystemId, secondTargetPoiId,
                    destinationSystemId, destinationId, destinationSystemId, destinationId)
            };
            var failure = TravelFastLaneReceipt.CheckGateChain(slice, _session, legs);
            Require(failure == null, failure!);
            // The public facts are compared against the loaded world, never the other way round.
            var arrivedOwner = NativeTravel("the fast-lane arrival checks");
            var actualSystem = SpGet(Player, "currentSystem");
            var actualPoi = SpGet(Player, "currentPointOfInterest");
            Require(actualSystem != null && (string)SpGet(actualSystem!, "guid")! == destinationSystemId,
                "The native player is not in the destination system after the fast-lane chain.");
            Require(ReferenceEquals(actualPoi, destination), "The native current POI is not the chain's destination.");
            Require(NativeManagerReadyFor(arrivedOwner, actualPoi), "The native local manager is not the initialized manager of the destination POI.");
            Require((bool)_localPoiReady.Invoke(arrivedOwner, null)!, "Native IsLocalPoiReady is false after the fast-lane chain.");
            Require(!(bool)_travelActive.Invoke(arrivedOwner, null)!, "Native travel is still active after the final route boundary.");
            Require(!(bool)SpGet(arrivedOwner, "usingJumpgate")!, "The native jump routine is still running after the final route boundary.");
            Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints remain after the final route boundary.");
            Require(TravelStationReceipt.Same(ModApi.Services.Travel.CurrentLocation, destinationSystemId, destinationId),
                "Public CurrentLocation does not match the chain's destination.");
            var stationFacts = StationSlice(stationOffset);
            Require(stationFacts.All(fact => fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed),
                "The fast-lane chain emitted physical station facts: " + string.Join(", ", stationFacts.Select(TravelStationReceipt.Describe)));
            var chainLegs = TravelFastLaneReceipt.Legs(slice);
            var completion = slice[slice.Count - 1];
            Pass(TravelFastLaneReceipt.GateChainCase, TravelStationReceipt.Location(destinationSystemId, destinationId),
                completion.OperationId, TravelStationReceipt.Evidence(slice, null),
                "firstGate=" + TravelStationReceipt.Location(_systemId, firstId)
                + "; secondGate=" + TravelStationReceipt.Location(intermediateSystemId, secondId)
                + "; destination=" + TravelStationReceipt.Location(destinationSystemId, destinationId)
                + "; systems=3; gates=2; legs=" + slice.Count(fact => fact.Kind == TravelTransitionKind.Requested)
                + "; routeCompletions=" + slice.Count(fact => fact.Kind == TravelTransitionKind.RouteCompleted)
                + "; completionSnapshot=" + _p._flSnapshots[completion.Sequence].ToDetail());
            // The second required case: the fast-lane branch really ran, proven by the native
            // multiplier at the correct boundary and by its absence on the legs around it.
            _p.FlCase(TravelFastLaneReceipt.MultiplierCase, TravelFastLaneReceipt.MultiplierDescription);
            failure = TravelFastLaneReceipt.CheckFastLaneEvidence(slice, _p._flSnapshots, out float observed);
            Require(failure == null, failure!);
            var fastLaneLeg = chainLegs[TravelFastLaneReceipt.FastLaneLegIndex];
            Pass(TravelFastLaneReceipt.MultiplierCase, TravelStationReceipt.Location(intermediateSystemId, secondId),
                fastLaneLeg[0].OperationId, TravelStationReceipt.Evidence(fastLaneLeg, null),
                "fastLaneUnlocked=" + unlocked + " (read-only; never written)"
                + "; fastLaneLeg=" + TravelStationReceipt.Location(intermediateSystemId, firstTargetPoiId)
                + "->" + TravelStationReceipt.Location(intermediateSystemId, secondId)
                + "; fastLaneMultiplier=" + observed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + "; fastLaneActive=True"
                + "; approachMultiplier=" + _p._flSnapshots[chainLegs[0][0].Sequence].Multiplier.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + "; postFastLaneMultiplier=" + _p._flSnapshots[chainLegs[TravelFastLaneReceipt.RouteLegs - 1][0].Sequence].Multiplier.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + "; requestedSnapshot=" + _p._flSnapshots[fastLaneLeg[0].Sequence].ToDetail()
                + "; departedSnapshot=" + _p._flSnapshots[fastLaneLeg[1].Sequence].ToDetail()
                + "; arrivedSnapshot=" + _p._flSnapshots[fastLaneLeg[2].Sequence].ToDetail());
        }

        // No route completion may appear before the final in-system leg closes the route.
        private void RequireNoEarlyCompletion(int offset, string stage)
        {
            var early = Slice(offset).FirstOrDefault(fact => fact.Kind == TravelTransitionKind.RouteCompleted);
            Require(early == null, "The route was completed at " + stage + " although native waypoints remained: "
                + (early == null ? "" : TravelStationReceipt.Describe(early)));
        }

        // --- fixture selection ---------------------------------------------------------------

        private List<object> NativeRoute(object destination)
            => ((IEnumerable)_shortestRoute.Invoke(NativeTravel("the native route planner"), new[] { destination })!).Cast<object>().ToList();

        // Two usable, non-tutorial gates leading through an intermediate system into a third one,
        // plus a SAFE follow-on POI there that the native planner really routes to as
        // [firstGate, secondGate, destination]. Every gate passes the phase's own pure refusal rule,
        // and the destination passes the shared in-system target rule, so the chain cannot end in a
        // station, another gate, a dynamic event or a native combat encounter.
        private object? SelectChain(out object? second, out object? destination, out string reason)
        {
            second = null;
            destination = null;
            var current = SpGet(Player, "currentPointOfInterest");
            var refusals = new Dictionary<string, int>(StringComparer.Ordinal);
            var firstGates = SystemGates(SpGet(Player, "currentSystem")!, refusals)
                .Where(gate => !ReferenceEquals(gate, current))
                .OrderBy(Distance)
                .ToArray();
            int inspectedIntermediates = 0;
            int inspectedSecondGates = 0;
            int safeDestinations = 0;
            foreach (var first in firstGates)
            {
                var intermediate = SpGet(first, "targetSystem");
                if (intermediate == null) continue;
                inspectedIntermediates++;
                var arrival = _getTargetPoi.Invoke(first, null);
                foreach (var candidateSecond in SystemGates(intermediate, refusals)
                    .Where(gate => !ReferenceEquals(gate, arrival))
                    .Where(gate => (string)SpGet(gate, "targetSystemGuid")! != _systemId
                        && (string)SpGet(gate, "targetSystemGuid")! != (string)SpGet(intermediate, "guid")!)
                    .OrderBy(gate => (string)SpGet(gate, "guid")!, StringComparer.Ordinal))
                {
                    var third = SpGet(candidateSecond, "targetSystem");
                    if (third == null) continue;
                    inspectedSecondGates++;
                    var arrivalInThird = _getTargetPoi.Invoke(candidateSecond, null);
                    var candidates = SafeTargetsInSystem(third, arrivalInThird);
                    safeDestinations += candidates.Length;
                    foreach (var candidate in candidates)
                    {
                        var route = NativeRoute(candidate);
                        if (route.Count != 3 || !ReferenceEquals(route[0], first) || !ReferenceEquals(route[1], candidateSecond)
                            || !ReferenceEquals(route[2], candidate)) continue;
                        second = candidateSecond;
                        destination = candidate;
                        reason = "";
                        return first;
                    }
                }
            }
            reason = "No native two-gate chain into a third system with a safe follow-on POI the planner routes to as [gate, gate, destination]: "
                + "candidateFirstGates=" + firstGates.Length + ", inspectedIntermediateSystems=" + inspectedIntermediates
                + ", candidateSecondGates=" + inspectedSecondGates + ", safeDestinations=" + safeDestinations
                + ", refusedGates=[" + string.Join(", ", refusals.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Key + ":" + entry.Value)) + "]"
                + "; the native fast-lane chain cannot be driven by this world.";
            return null;
        }

        // Gates of one system that pass the phase's own pure refusal rule. Every field read is a
        // plain native field or method; the lazy name generator is never touched.
        private IEnumerable<object> SystemGates(object system, IDictionary<string, int> refusals)
        {
            var map = SpGet(_mapType, "current");
            if (map == null) return Array.Empty<object>();
            var playerFaction = SpGet(_factionType, "player");
            var systemId = (string)SpGet(system, "guid")!;
            var selected = new List<object>();
            foreach (var poi in ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(poi => poi.GetType() == _jumpGateType && ReferenceEquals(SpGet(poi, "system"), system)))
            {
                var target = SpGet(poi, "targetSystem");
                var targetSystemId = (string?)SpGet(poi, "targetSystemGuid");
                var candidate = new TravelFastLaneReceipt.GateCandidate(
                    (string)SpGet(poi, "guid")!,
                    (bool)SpGet(poi, "canUseJumpGate")!,
                    (bool)SpGet(poi, "hidden")!,
                    (bool)SpGet(poi, "isDynamicPoi")!,
                    IsTutorialExitGate(poi),
                    SpGet(poi, "faction") is { } faction && playerFaction != null
                        && (bool)_isEnemy.Invoke(faction, new[] { playerFaction })!,
                    (bool)_storyMissionPoi.Invoke(poi, null)!,
                    ((ICollection)SpGet(poi, "guardDescriptors")!).Count,
                    target != null && !string.IsNullOrEmpty(targetSystemId) && targetSystemId != systemId,
                    !string.IsNullOrEmpty((string?)SpGet(poi, "targetPoiGuid")));
                var refusal = TravelFastLaneReceipt.RefuseFastLaneGate(candidate);
                if (refusal == null) selected.Add(poi);
                else refusals[refusal] = refusals.TryGetValue(refusal, out int count) ? count + 1 : 1;
            }
            return selected;
        }

        // The shared authoritative safe-target rule (TravelStationReceipt.RefuseTravelTarget),
        // applied to the destination system's POIs.
        private object[] SafeTargetsInSystem(object system, object? excluded)
        {
            var map = SpGet(_mapType, "current");
            if (map == null) return Array.Empty<object>();
            var playerFaction = SpGet(_factionType, "player");
            var selected = new List<object>();
            foreach (var poi in ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(poi => ReferenceEquals(SpGet(poi, "system"), system) && !ReferenceEquals(poi, excluded)))
            {
                var candidate = new TravelStationReceipt.TravelTargetCandidate(
                    (string)SpGet(poi, "guid")!,
                    _miningType.IsInstanceOfType(poi) || _salvageType.IsInstanceOfType(poi),
                    _combatType.IsInstanceOfType(poi),
                    _stationType.IsInstanceOfType(poi),
                    _jumpGateType.IsInstanceOfType(poi),
                    _wormholeType.IsInstanceOfType(poi),
                    (bool)SpGet(poi, "hidden")!,
                    (bool)SpGet(poi, "isDynamicPoi")!,
                    SpGet(poi, "faction") is { } faction && playerFaction != null
                        && (bool)_isEnemy.Invoke(faction, new[] { playerFaction })!,
                    (bool)_storyMissionPoi.Invoke(poi, null)!,
                    ((ICollection)SpGet(poi, "guardDescriptors")!).Count);
                if (TravelStationReceipt.RefuseTravelTarget(candidate) == null) selected.Add(poi);
            }
            return selected.OrderBy(poi => (string)SpGet(poi, "guid")!, StringComparer.Ordinal).ToArray();
        }

        // The one-way tutorial exit (Hermetis -> Canis Majoris) transitions the world and may never
        // be driven by a qualification phase; it stays source-attested only.
        private bool IsTutorialExitGate(object gate)
        {
            var system = SpGet(gate, "system");
            var target = SpGet(gate, "targetSystem");
            return system != null && target != null
                && (string?)SpGet(system, "_name") == "Hermetis" && (string?)SpGet(target, "_name") == "Canis Majoris";
        }

        private float Distance(object poi)
            => Vector2.Distance((Vector2)SpGet(poi, "position")!, (Vector2)SpGet(Player, "mapPosition")!);

        // --- fixture preparation -------------------------------------------------------------

        private IEnumerable<object?> Prepare()
        {
            _prepared = false; _notPrepared = ""; _owner = null; _p.FastLaneOwner = null;
            foreach (var frame in _p.SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            _session = _p._api!.CurrentSession!.Id;
            var session = _session;
            foreach (var frame in _p.Wait(() => ModApi.Services.Travel.SessionId == session
                && ModApi.Services.Travel.CurrentLocation != null && _p.NativeTravelReady(), "travel service binding and native POI readiness")) yield return frame;
            var system = SpGet(Player, "currentSystem");
            var poi = SpGet(Player, "currentPointOfInterest");
            if (system == null || poi == null)
            {
                _notPrepared = "The fixture did not load at a known native system and POI (system=" + (system != null) + ", poi=" + (poi != null) + ").";
                yield break;
            }
            _systemId = (string)SpGet(system!, "guid")!;
            _startPoiId = (string)SpGet(poi!, "guid")!;
            var manager = SpGet(_travelType, "Instance");
            var owner = SpGet(_p._player, "current");
            if (!TravelStationDriver.Alive(manager) || !TravelStationDriver.Alive(owner))
            {
                _notPrepared = "The freshly loaded fixture has no live native travel manager/player to own the phase (manager="
                    + NativeCaseOwner.Identity(manager, TravelStationDriver.Alive) + ", player="
                    + NativeCaseOwner.Identity(owner, TravelStationDriver.Alive) + ").";
                yield break;
            }
            _owner = new NativeCaseOwner(_session, manager!, owner!, _systemId, _startPoiId);
            _p.FastLaneOwner = _owner;
            _prepared = true;
        }

        private IEnumerable<object?> Undock()
        {
            var ship = PlayerShip;
            if (!TravelStationDriver.Alive(ship)) { _notPrepared = "The fixture has no live player ship to undock."; yield break; }
            if (DockingState(ship!) == null) yield break; // Already in space.
            var exterior = Exterior;
            var startPoi = SpGet(Player, "currentPointOfInterest");
            if (startPoi == null || !_stationType.IsInstanceOfType(startPoi) || !TravelStationDriver.Alive(exterior))
            {
                _notPrepared = "The fixture ship reports docking state " + DockingState(ship!) + " without a live station exterior to exit.";
                yield break;
            }
            RequireOwned("the native station exit before the route");
            var interior = SpGet(_interiorType, "instance");
            if (TravelStationDriver.Alive(interior)) _exitSpacestation.Invoke(interior!, null);
            else _startUndocking.Invoke(exterior!, null);
            foreach (var frame in AwaitOrFail(() => DockingState(ship!) != "Docked"
                && !TravelStationDriver.Alive(SpGet(exterior!, "undockingRoutine"))
                && !TravelStationDriver.Alive(_getDockingOption.Invoke(exterior!, new[] { ship })),
                TravelFastLaneReceipt.UndockSeconds, "the native undock before the route")) yield return frame;
        }

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

        // Case-owned, explicit deadline. A timeout or a failed session is a recorded failure; the
        // phase drives ONE route and never retries an assertion.
        private IEnumerable<object?> AwaitOrFail(Func<bool> ready, float seconds, string description)
        {
            Time.timeScale = 1;
            float until = Time.realtimeSinceStartup + seconds;
            while (!ready())
            {
                Require(_p._api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for " + description + ".");
                Require(Time.realtimeSinceStartup < until, "Timed out after " + seconds + "s waiting for " + description
                    + " (" + _p.FastLaneSnapshot().ToDetail() + ").");
                yield return null;
            }
        }

        // A precondition the world did not offer is recorded for BOTH required cases, so a missing
        // fixture capability can never leave a required case silently absent.
        private void NotRunBoth(string reason)
        {
            foreach (var caseId in TravelFastLaneReceipt.RequiredCases)
            {
                var description = caseId == TravelFastLaneReceipt.GateChainCase
                    ? TravelFastLaneReceipt.GateChainDescription : TravelFastLaneReceipt.MultiplierDescription;
                _p.FlRecord(caseId, description, TravelStationReceipt.NotRun, "", _session, null, "", reason);
            }
            _p.FlCheckpoint();
        }

        private void Pass(string caseId, string nativeIdentity, Guid? operation, string evidence, string detail)
        {
            var description = caseId == TravelFastLaneReceipt.GateChainCase
                ? TravelFastLaneReceipt.GateChainDescription : TravelFastLaneReceipt.MultiplierDescription;
            _p.FlRecord(caseId, description, TravelStationReceipt.Passed, nativeIdentity, _session, operation, evidence, detail);
            _p.FlCheckpoint();
        }

        private void EndCase() { _p.FlEndCase(); _p.FlCheckpoint(); }

        private static Type Named(string name) => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name, "type");
    }
}
