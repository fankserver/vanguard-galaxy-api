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
    // Per-case native driver for phase travel-recovery-continuation-v1. It runs inside the plugin
    // iterator context so real Unity coroutines drive every transition, and it only calls vanilla
    // entry points a player action would call: TravelManager.CanWeTravel / TryInitiateTravel /
    // CancelTravel (the map travel and cancel actions) and the station exit action. No native
    // location, waypoint, docking state or coroutine result is ever written by this phase, and no
    // production hook is disabled to manufacture a public fact.
    private sealed class TravelRecoveryDriver
    {
        private readonly Plugin _p;
        private readonly Type _travelType, _poiType, _gameplayType, _shipType, _stationType;
        private readonly Type _exteriorType, _interiorType, _dockingOptionType, _jumpGateType, _mapType;
        private readonly Type _miningType, _salvageType, _combatType, _wormholeType, _factionType;
        private readonly MethodInfo _tryInitiateTravel, _canWeTravel, _cancelTravel, _travelActive, _localPoiReady;
        private readonly MethodInfo _shortestRoute, _getTargetPoi, _startUndocking, _exitSpacestation, _getDockingOption;
        private readonly MethodInfo _isEnemy, _storyMissionPoi;
        // Captured ONLY at this case's own fresh fixture load and readiness boundary: a fixture load
        // destroys the previous scene's manager, and a cached reference to it stays live-looking
        // while every native call on it fails.
        private NativeCaseOwner? _owner;
        private Guid _session;
        private string _systemId = "";
        private string _startPoiId = "";
        private bool _prepared;
        private string _notPrepared = "";

        internal TravelRecoveryDriver(Plugin p)
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
            _cancelTravel = TravelStationDriver.Bind(_travelType, "CancelTravel", typeof(bool), typeof(Vector2?));
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

        // The captured owner, proven to still be the live current manager/player of this case's
        // session before every native drive and every native read.
        private object NativeTravel(string action)
        {
            var owner = _owner;
            Require(owner != null, "Refusing " + action + ": this case captured no native owner at its fixture-load boundary.");
            var failure = owner!.CheckCurrent(action, _p._api!.CurrentSession?.Id,
                SpGet(_travelType, "Instance"), SpGet(_p._player, "current"), TravelStationDriver.Alive);
            Require(failure == null, failure!);
            return owner.TravelManager;
        }

        private void RequireOwned(string action) => NativeTravel(action);

        private List<TravelTransition> Travel => _p.PendingRecoveryTravel!;
        private List<StationTransition> Stations => _p.PendingRecoveryStation!;
        // Case-local window: every fact observed after this case started driving, INCLUDING facts of
        // another session. Foreign facts are rejected by the validators, never filtered away here.
        private List<TravelTransition> Slice(int offset) => TravelStationReceipt.Window(Travel, offset);
        private List<StationTransition> StationSlice(int offset) => TravelStationReceipt.Window(Stations, offset);
        private object Player => _p.CurrentPlayer;
        private object? PlayerShip => SpGet(SpGet(_gameplayType, "Instance")!, "spaceShip");
        private object? Exterior => SpGet(_exteriorType, "Instance");

        internal IEnumerable<object?> Run()
        {
            yield return null;
            foreach (var frame in CaseRecoveredPlacement()) yield return frame;
            EndCase();
            foreach (var frame in CasePostGateContinuation()) yield return frame;
            EndCase();
            // The cases legitimately end in another world state (parked at a cancelled destination,
            // in another system). Restore the fixture the later pilots expect; this is harness
            // cleanup and is never recorded as coverage.
            foreach (var frame in _p.SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            _p.RcCheckpoint();
        }

        // --- case: recovered placement -------------------------------------------------------

        // A real native in-system route is driven to a safe POI and allowed to reach the verified
        // origin unload (the public Departed). The pilot then samples the loaded world every frame
        // until the native travel coroutine has assigned the destination POI to the player, its
        // manager reports initializedAndReady, and the native ROUTE IS STILL RUNNING
        // (TravelActive()), while SpaceshipHasArrived has not run yet (no public Arrived). In
        // exactly that window the player's own cancel action is taken, which leaves the API with a
        // placed session, no pending leg and an unknown location while the world reports a loaded,
        // ready POI - the state the adapter's own readiness observation recovers from.
        //
        // The live-route requirement is not decoration. On the inspected build the native wait
        // predicate (<Travel>b__84_0) returns TRUE when no local manager is registered, so a route
        // can end silently without an arrival and the destination manager can initialize afterwards.
        // Readiness alone would then be indistinguishable from this case's window while nothing was
        // travelling, so an acquisition without a live route is classified as a MISS and no cancel
        // is issued.
        //
        // Nothing is forced: no field is written, no hook is disabled and no adapter callback is
        // invoked. Every attempt is persisted as its own receipt row when it starts and rewritten
        // with its terminal outcome, so a later failure cannot erase an earlier attempt. A window
        // wait that expires while the native route is STILL RUNNING is a failure of this case, not a
        // retry. A missed attempt closes its own abandoned leg before the next one starts.
        //
        // Whether the window is observable at all depends on WHEN the destination manager finishes
        // its own Init coroutine relative to the POI assignment, and on Unity's coroutine and
        // CustomYieldInstruction scheduling, which the installed-assembly test does NOT pin (it pins
        // the predicate shape only). The bounded attempts therefore exist for that fixture/readiness
        // variability; no ordering is asserted here and every miss is recorded with its reason.
        private IEnumerable<object?> CaseRecoveredPlacement()
        {
            _p.RcCase(TravelRecoveryReceipt.RecoveredPlacementCase, TravelRecoveryReceipt.RecoveredPlacementDescription);
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            foreach (var frame in Undock()) yield return frame;
            if (_notPrepared.Length > 0) { NotRun(_notPrepared); yield break; }
            var attempted = new List<string>();
            var attemptLog = new List<string>();
            for (int attempt = 1; attempt <= TravelRecoveryReceipt.RecoveryAttempts; attempt++)
            {
                var current = SpGet(Player, "currentPointOfInterest");
                var target = _p.SafeInSystemTargets()
                    .FirstOrDefault(poi => !ReferenceEquals(poi, current) && !attempted.Contains((string)SpGet(poi, "guid")!));
                if (target == null)
                {
                    var exhausted = TravelRecoveryReceipt.DescribeAttempt(attempt, "<none>",
                        TravelRecoveryReceipt.AttemptNoTargetOutcome, _p.SafeTargetSelection);
                    attemptLog.Add(exhausted);
                    _p.RcCompleteAttempt(_p.RcBeginAttempt(_session, exhausted), _session, exhausted);
                    break;
                }
                var targetId = (string)SpGet(target, "guid")!;
                attempted.Add(targetId);
                var originPoi = current == null ? null : (string)SpGet(current, "guid")!;
                // Persisted BEFORE anything is driven, so an attempt that throws still leaves its
                // own row behind next to the earlier attempts' outcomes.
                int attemptRow = _p.RcBeginAttempt(_session, TravelRecoveryReceipt.DescribeStartedAttempt(attempt, targetId));
                void RecordAttempt(string result, string detail = "")
                {
                    var described = TravelRecoveryReceipt.DescribeAttempt(attempt, targetId, result, detail);
                    attemptLog.Add(described);
                    _p.RcCompleteAttempt(attemptRow, _session, described);
                }
                // Rewrites the SAME attempt row and log entry, so recording an outcome before a
                // cleanup and enriching it with the post-cleanup residual afterwards never invents a
                // second attempt.
                void UpdateAttempt(string result, string detail)
                {
                    var described = TravelRecoveryReceipt.DescribeAttempt(attempt, targetId, result, detail);
                    if (attemptLog.Count > 0) attemptLog[attemptLog.Count - 1] = described; else attemptLog.Add(described);
                    _p.RcCompleteAttempt(attemptRow, _session, described);
                }
                bool cancelled = false;
                string outcome = "";
                string outcomeDetail = "";
                var acquisition = default(TravelRecoveryReceipt.NativeSnapshot);
                int offset = Travel.Count;
                int stationOffset = Stations.Count;
                // The quiet window opens BEFORE the availability wait, because that wait is exactly
                // where an unsolicited native route (qa-82's emergency-jump return) appeared.
                foreach (var frame in PollFor(() => false, TravelRecoveryReceipt.TravelReadySeconds)) yield return frame;
                var unsolicited = TravelStationReceipt.CheckNoUnsolicitedTravel("the recovery attempt", Slice(offset),
                    (bool)_travelActive.Invoke(NativeTravel("the recovery attempt"), null)!, _p.NativeAutonomyDetail());
                Require(unsolicited == null, unsolicited!);
                if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the recovery attempt"), new[] { target })!)
                {
                    RecordAttempt(TravelRecoveryReceipt.AttemptRefusedOutcome, "native CanWeTravel refused the route");
                    continue;
                }
                Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the recovery attempt"), new[] { target })!,
                    "Native TryInitiateTravel refused the recovery attempt route.");
                RequireNoFabricatedDeparture(offset, "the accepted recovery request");
                foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.Departed),
                    TravelRecoveryReceipt.DepartureSeconds, "the native origin unload departure of the recovery attempt")) yield return frame;
                // The native window this case needs, sampled read-only every frame.
                Time.timeScale = 1;
                float until = Time.realtimeSinceStartup + TravelRecoveryReceipt.ArrivalSeconds;
                while (true)
                {
                    if (Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.Arrived))
                    {
                        outcome = TravelRecoveryReceipt.AttemptArrivalFirstOutcome;
                        outcomeDetail = "the native arrival ran before the readiness window could be sampled";
                        break;
                    }
                    var owner = NativeTravel("the recovery readiness sample");
                    var poi = SpGet(Player, "currentPointOfInterest");
                    if (ReferenceEquals(poi, target) && NativeManagerReadyFor(owner, poi))
                    {
                        // Sampled in the frame the cancel is about to be issued, BEFORE the native
                        // cancel clears its own coroutine: this is the only place a live route can
                        // still be observed. It is passed to the pure evidence rule unchanged.
                        acquisition = _p.RecoverySnapshot();
                        if (!acquisition.TravelActive)
                        {
                            // Readiness without a live route: the native route ended silently (its
                            // own wait predicate passes when no manager is registered) and the
                            // manager initialized afterwards. Cancelling here would publish a
                            // Cancelled that interrupted nothing, so no cancel is issued.
                            outcome = TravelRecoveryReceipt.AttemptRouteEndedOutcome;
                            outcomeDetail = "the native route had already ended when the readiness window became observable ("
                                + acquisition.ToDetail() + ")";
                            break;
                        }
                        // The player's own cancel action, taken inside the observed live window.
                        Require((bool)_cancelTravel.Invoke(owner, new object?[] { null })!,
                            "Native CancelTravel(null) refused inside the observed readiness window.");
                        cancelled = true;
                        break;
                    }
                    Require(_p._api!.CurrentSession?.Phase != SessionPhase.Failed,
                        "Session failed while sampling the native readiness window.");
                    if (Time.realtimeSinceStartup >= until)
                    {
                        var timedOut = _p.RecoverySnapshot();
                        if (timedOut.TravelActive)
                        {
                            // The harness ran out of time while the native route was still running.
                            // That is this case's own failure, never a clean miss and never a retry:
                            // the evidence is persisted, the world is left quiet with the player's
                            // own cancel, and the case fails at the timeout.
                            // The fault evidence is persisted BEFORE any cleanup, so the recorded
                            // outcome describes the world at the timeout itself.
                            RecordAttempt(TravelRecoveryReceipt.AttemptTimeoutRunningOutcome,
                                "timed out after " + TravelRecoveryReceipt.ArrivalSeconds + "s while the native route was still running ("
                                + timedOut.ToDetail() + "; " + _p.RecoveryResidual() + ")");
                            // Owner-exact cleanup with the player's own cancel. It is BOUNDED and
                            // honest: the native cancel stops the route and clears the waypoints but
                            // never resets isWarping, so a mid-warp timeout leaves that vanilla flag
                            // stale. Nothing is forced to hide it - no position, no warp state and no
                            // native field is written - and the residual is recorded below. A failed
                            // phase leaves no world a later phase may continue from; the harness
                            // fails here and the runner quits.
                            _cancelTravel.Invoke(NativeTravel("the cleanup cancel after the readiness-window timeout"), new object?[] { null });
                            var residual = _p.RecoveryResidual();
                            UpdateAttempt(TravelRecoveryReceipt.AttemptTimeoutRunningOutcome,
                                "timed out after " + TravelRecoveryReceipt.ArrivalSeconds + "s while the native route was still running ("
                                + timedOut.ToDetail() + "); afterCleanup=" + residual);
                            Require(false, "Timed out after " + TravelRecoveryReceipt.ArrivalSeconds
                                + "s waiting for the readiness window while the native route was still running ("
                                + timedOut.ToDetail() + "); the ordinary cancel cannot reset the vanilla warp flag, residual after cleanup: "
                                + residual + "; attempts: " + string.Join("; ", attemptLog));
                        }
                        outcome = TravelRecoveryReceipt.AttemptTimeoutIdleOutcome;
                        outcomeDetail = "timed out after " + TravelRecoveryReceipt.ArrivalSeconds
                            + "s with no native route running and no arrival (" + timedOut.ToDetail() + ")";
                        break;
                    }
                    yield return null;
                }
                if (!cancelled)
                {
                    // Leave a quiet native surface AND a closed API leg for the next attempt.
                    bool arrived = Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.Arrived);
                    bool cleanupAccepted = false;
                    if (arrived)
                    {
                        // The route completed on its own: its leg closes with the native boundary.
                        foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                            TravelRecoveryReceipt.BoundarySeconds, "the native route boundary of the completed recovery attempt")) yield return frame;
                    }
                    else
                    {
                        // The leg DEPARTED and never arrived, so it is still pending. Left open, the
                        // next route request would supersede it and the tracker would truthfully
                        // publish that leg's Cancelled inside the NEXT attempt's window, failing an
                        // otherwise good attempt on the exact four-fact rule. The player's own cancel
                        // closes it here, inside this attempt's own window, while the captured owner
                        // is still valid. It is not coverage: this attempt never acquired a live
                        // window, so its acquisition can never satisfy the case's positive rule.
                        cleanupAccepted = (bool)_cancelTravel.Invoke(
                            NativeTravel("the player cancel that closes the missed attempt's abandoned leg"), new object?[] { null })!;
                    }
                    foreach (var frame in Settle()) yield return frame;
                    if (!arrived)
                    {
                        var closure = TravelRecoveryReceipt.CheckMissCleanup(Slice(offset), cleanupAccepted);
                        var closureDetail = outcomeDetail + "; missCleanup={nativeTravelActive="
                            + _p.RecoverySnapshot().TravelActive + ",cancelAccepted=" + cleanupAccepted
                            + ",window=[" + string.Join(" ", Slice(offset).Select(fact => fact.Kind.ToString())) + "]}";
                        if (closure != null)
                        {
                            // The abandoned leg could not be proven closed. Another attempt would
                            // open on a contaminated window, so the case ends here honestly.
                            RecordAttempt(TravelRecoveryReceipt.AttemptUnclosedOutcome, closureDetail + "; " + closure);
                            NotRun("The abandoned leg of a missed recovery attempt could not be proven closed, so no further attempt may run: "
                                + closure + " (" + string.Join("; ", attemptLog) + ")");
                            yield break;
                        }
                        RecordAttempt(outcome, closureDetail);
                    }
                    else
                    {
                        RecordAttempt(outcome, outcomeDetail);
                    }
                    continue;
                }
                foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.RecoveredPlacement),
                    TravelRecoveryReceipt.PlacementSeconds, "the public RecoveredPlacement after the native cancel at " + targetId)) yield return frame;
                foreach (var frame in Settle()) yield return frame;
                var slice = Slice(offset);
                var failure = TravelRecoveryReceipt.CheckRecoveredPlacement(slice, _session, _systemId, originPoi, targetId);
                Require(failure == null, failure!);
                failure = TravelRecoveryReceipt.CheckRecoveryEvidence(slice, _p._rcSnapshots,
                    TravelStationReceipt.Location(_systemId, targetId), acquisition);
                Require(failure == null, failure!);
                // The public facts are compared against the loaded world, never the other way round.
                var arrivedOwner = NativeTravel("the recovered placement checks");
                Require(ReferenceEquals(SpGet(Player, "currentPointOfInterest"), target),
                    "The native current POI is not the cancelled route's destination after the recovery.");
                Require(NativeManagerReadyFor(arrivedOwner, target),
                    "The native local manager is not the initialized manager of the recovered POI.");
                Require((bool)_localPoiReady.Invoke(arrivedOwner, null)!, "Native IsLocalPoiReady is false after the recovery.");
                Require(!(bool)_travelActive.Invoke(arrivedOwner, null)!, "Native travel is still active after the cancel.");
                Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints remain after the cancel.");
                Require(TravelStationReceipt.Same(ModApi.Travel!.CurrentLocation, _systemId, targetId),
                    "Public CurrentLocation does not match the recovered POI.");
                var stationFacts = StationSlice(stationOffset);
                Require(stationFacts.All(fact => fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed),
                    "The recovery window emitted physical station facts: " + string.Join(", ", stationFacts.Select(TravelStationReceipt.Describe)));
                RecordAttempt(TravelRecoveryReceipt.AttemptCancelledOutcome, "cancelled inside the observed live-route readiness window");
                Pass(TravelStationReceipt.Location(_systemId, targetId), slice[0].OperationId,
                    TravelStationReceipt.Evidence(slice, null),
                    "origin=" + TravelStationReceipt.Location(_systemId, originPoi) + "; cancelledLeg=" + slice[0].OperationId
                    + "; recoveredAt=" + TravelStationReceipt.Location(_systemId, targetId)
                    + "; acquisitionSnapshot=" + acquisition.ToDetail()
                    + "; placementSnapshot=" + _p._rcSnapshots[slice[3].Sequence].ToDetail()
                    + "; attempts=" + attemptLog.Count + "; " + string.Join("; ", attemptLog));
                yield break;
            }
            NotRun("The native readiness window (destination POI current and initialized, with the native route still running, before SpaceshipHasArrived) was not observed in "
                + TravelRecoveryReceipt.RecoveryAttempts + " real routes: " + string.Join("; ", attemptLog)
                + " (" + _p.SafeTargetSelection + ")");
        }

        // --- case: post-gate continuation ----------------------------------------------------

        // ONE native multi-waypoint route is requested to a safe follow-on POI in the system behind a
        // usable jump gate, exactly as the map travel action does it: the native planner produces
        // [gate, follow-on], the in-system approach leg reaches the gate, the gate's own arrival
        // hands the ship to the jump routine, and TravelToNextWaypoint then starts the post-gate
        // in-system leg. The public route may only be completed by that last leg.
        private IEnumerable<object?> CasePostGateContinuation()
        {
            _p.RcCase(TravelRecoveryReceipt.ContinuationCase, TravelRecoveryReceipt.ContinuationDescription);
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            foreach (var frame in Undock()) yield return frame;
            if (_notPrepared.Length > 0) { NotRun(_notPrepared); yield break; }
            var gate = SelectContinuationRoute(out object? follow, out string refusal);
            if (gate == null || follow == null) { NotRun(refusal); yield break; }
            var gateId = (string)SpGet(gate, "guid")!;
            var followId = (string)SpGet(follow, "guid")!;
            var followSystemId = (string)SpGet(SpGet(follow, "system")!, "guid")!;
            // The RAW request the adapter must preserve, captured from the loaded world BEFORE the
            // jump can rewrite anything.
            var gateTargetSystemId = (string)SpGet(gate, "targetSystemGuid")!;
            var gateTargetPoiId = (string)SpGet(gate, "targetPoiGuid")!;
            int offset = Travel.Count;
            int stationOffset = Stations.Count;
            foreach (var frame in PollFor(() => false, TravelRecoveryReceipt.TravelReadySeconds)) yield return frame;
            var unsolicited = TravelStationReceipt.CheckNoUnsolicitedTravel("the continuation route", Slice(offset),
                (bool)_travelActive.Invoke(NativeTravel("the continuation route"), null)!, _p.NativeAutonomyDetail());
            Require(unsolicited == null, unsolicited!);
            if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the continuation route"), new[] { follow })!)
            {
                NotRun("Native CanWeTravel refused the continuation route to " + followId + ".");
                yield break;
            }
            Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the continuation route"), new[] { follow })!,
                "Native TryInitiateTravel refused the continuation route.");
            RequireNoFabricatedDeparture(offset, "the accepted continuation request");
            var waypoints = (IList)SpGet(Player, "waypoints")!;
            Require(waypoints.Count == 2 && ReferenceEquals(waypoints[0], gate) && ReferenceEquals(waypoints[1], follow),
                "The native route planner did not produce the expected [gate, follow-on] waypoint list.");
            // Leg one: the in-system approach to the gate.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.Arrived),
                TravelRecoveryReceipt.ArrivalSeconds, "the native in-system arrival at the gate " + gateId)) yield return frame;
            Require(!Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                "The route was completed at the gate approach although a follow-on waypoint remained.");
            // Leg two: the native gate handoff and the jump itself.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Mode == TravelMode.JumpGate),
                TravelRecoveryReceipt.HandoffSeconds, "the native jump-gate handoff into the jump routine")) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Mode == TravelMode.JumpGate && fact.Kind == TravelTransitionKind.Arrived),
                TravelRecoveryReceipt.JumpArrivalSeconds, "the native jump arrival in " + gateTargetSystemId)) yield return frame;
            Require(!Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                "The route was completed at the jump arrival although the post-gate waypoint remained.");
            // Leg three: the post-gate in-system leg TravelToNextWaypoint starts, and only its own
            // boundary may complete the route.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Count(fact => fact.Kind == TravelTransitionKind.Arrived
                && fact.Mode == TravelMode.InSystem) >= 2,
                TravelRecoveryReceipt.ArrivalSeconds, "the native post-gate in-system arrival at " + followId)) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                TravelRecoveryReceipt.BoundarySeconds, "the native final route boundary at " + followId)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var slice = Slice(offset);
            var legs = new[]
            {
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, _systemId, _startPoiId, _systemId, gateId, _systemId, gateId),
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.JumpGate, _systemId, gateId,
                    gateTargetSystemId, gateTargetPoiId, gateTargetSystemId, gateTargetPoiId),
                new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, gateTargetSystemId, gateTargetPoiId,
                    followSystemId, followId, followSystemId, followId)
            };
            var failure = TravelRecoveryReceipt.CheckContinuation(slice, _session, legs);
            Require(failure == null, failure!);
            failure = TravelRecoveryReceipt.CheckContinuationEvidence(slice, _p._rcSnapshots);
            Require(failure == null, failure!);
            var arrivedOwner = NativeTravel("the continuation arrival checks");
            var actualSystem = SpGet(Player, "currentSystem");
            var actualPoi = SpGet(Player, "currentPointOfInterest");
            Require(actualSystem != null && (string)SpGet(actualSystem!, "guid")! == followSystemId,
                "The native player is not in the follow-on system after the continuation route.");
            Require(ReferenceEquals(actualPoi, follow), "The native current POI is not the follow-on destination.");
            Require(NativeManagerReadyFor(arrivedOwner, actualPoi), "The native local manager is not the initialized manager of the follow-on POI.");
            Require((bool)_localPoiReady.Invoke(arrivedOwner, null)!, "Native IsLocalPoiReady is false after the continuation route.");
            Require(!(bool)_travelActive.Invoke(arrivedOwner, null)!, "Native travel is still active after the final route boundary.");
            Require(!(bool)SpGet(arrivedOwner, "usingJumpgate")!, "The native jump routine is still running after the final route boundary.");
            Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints remain after the final route boundary.");
            Require(TravelStationReceipt.Same(ModApi.Travel!.CurrentLocation, followSystemId, followId),
                "Public CurrentLocation does not match the follow-on destination.");
            var stationFacts = StationSlice(stationOffset);
            Require(stationFacts.All(fact => fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed),
                "The continuation route emitted physical station facts: " + string.Join(", ", stationFacts.Select(TravelStationReceipt.Describe)));
            var jumpArrival = slice.First(fact => fact.Mode == TravelMode.JumpGate && fact.Kind == TravelTransitionKind.Arrived);
            var completion = slice[slice.Count - 1];
            Pass(TravelStationReceipt.Location(followSystemId, followId), completion.OperationId,
                TravelStationReceipt.Evidence(slice, null),
                "approachGate=" + TravelStationReceipt.Location(_systemId, gateId)
                + "; jumpTargetRaw=" + TravelStationReceipt.Location(gateTargetSystemId, gateTargetPoiId)
                + "; followOn=" + TravelStationReceipt.Location(followSystemId, followId)
                + "; legs=" + slice.Count(fact => fact.Kind == TravelTransitionKind.Requested)
                + "; routeCompletions=" + slice.Count(fact => fact.Kind == TravelTransitionKind.RouteCompleted)
                + "; gateArrivalSnapshot=" + _p._rcSnapshots[jumpArrival.Sequence].ToDetail()
                + "; completionSnapshot=" + _p._rcSnapshots[completion.Sequence].ToDetail());
        }

        // --- fixture selection ---------------------------------------------------------------

        // The native route planner is a read-only BFS over the loaded map, so it selects the hops
        // without driving anything and without assuming which gate the planner prefers.
        private List<object> NativeRoute(object destination)
            => ((IEnumerable)_shortestRoute.Invoke(NativeTravel("the native route planner"), new[] { destination })!).Cast<object>().ToList();

        // A usable, non-tutorial gate in the current system plus a SAFE follow-on POI behind it that
        // the native planner really routes to as [gate, follow-on]. The follow-on uses exactly the
        // shared authoritative refusal rule the in-system phases use, so the continuation cannot end
        // in a station, another gate, a dynamic event or a native combat encounter.
        private object? SelectContinuationRoute(out object? follow, out string reason)
        {
            follow = null;
            var current = SpGet(Player, "currentPointOfInterest");
            var gates = SystemPois()
                .Where(poi => poi.GetType() == _jumpGateType)
                .Where(poi => !(bool)SpGet(poi, "hidden")! && !(bool)SpGet(poi, "isDynamicPoi")!)
                .Where(poi => (bool)SpGet(poi, "canUseJumpGate")!)
                .Where(poi => SpGet(poi, "targetSystem") != null
                    && (string)SpGet(poi, "targetSystemGuid")! != _systemId
                    && !string.IsNullOrEmpty((string?)SpGet(poi, "targetPoiGuid")))
                .Where(poi => !IsTutorialExitGate(poi))
                .Where(poi => !ReferenceEquals(poi, current))
                .OrderBy(Distance)
                .ToArray();
            int inspectedSystems = 0;
            int safeCandidates = 0;
            foreach (var gate in gates)
            {
                var targetPoi = _getTargetPoi.Invoke(gate, null);
                var targetSystem = SpGet(gate, "targetSystem");
                if (targetPoi == null || targetSystem == null) continue;
                inspectedSystems++;
                var candidates = SafeTargetsInSystem(targetSystem, targetPoi);
                safeCandidates += candidates.Length;
                foreach (var candidate in candidates)
                {
                    var route = NativeRoute(candidate);
                    if (route.Count != 2 || !ReferenceEquals(route[0], gate) || !ReferenceEquals(route[1], candidate)) continue;
                    follow = candidate;
                    reason = "";
                    return gate;
                }
            }
            reason = "No usable non-tutorial gate in the current system has a safe follow-on POI the native planner routes to as [gate, follow-on]: gates="
                + SystemPois().Count(poi => _jumpGateType.IsInstanceOfType(poi)) + ", candidateGates=" + gates.Length
                + ", inspectedTargetSystems=" + inspectedSystems + ", safeFollowOnCandidates=" + safeCandidates
                + "; the native fast-lane/gate chain cannot be driven by this world.";
            return null;
        }

        // The shared authoritative safe-target rule (TravelStationReceipt.RefuseTravelTarget),
        // applied to another system's POIs. It reads plain native fields only and never touches the
        // lazy name generator, so the selection consumes no world randomness.
        private object[] SafeTargetsInSystem(object system, object excluded)
        {
            var map = SpGet(_mapType, "current");
            if (map == null) return Array.Empty<object>();
            var playerFaction = SpGet(_factionType, "player");
            var pois = ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(poi => ReferenceEquals(SpGet(poi, "system"), system) && !ReferenceEquals(poi, excluded))
                .ToArray();
            var selected = new List<object>();
            foreach (var poi in pois)
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

        private IEnumerable<object> SystemPois()
        {
            var map = SpGet(_mapType, "current");
            if (map == null) return Array.Empty<object>();
            var system = SpGet(Player, "currentSystem");
            return ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(poi => ReferenceEquals(SpGet(poi, "system"), system));
        }

        private float Distance(object poi)
            => Vector2.Distance((Vector2)SpGet(poi, "position")!, (Vector2)SpGet(Player, "mapPosition")!);

        // --- fixture preparation -------------------------------------------------------------

        // Each case starts from its own fresh fixture session, and the native owner is captured ONLY
        // at that load's readiness boundary.
        private IEnumerable<object?> Prepare()
        {
            _prepared = false; _notPrepared = ""; _owner = null; _p.RecoveryOwner = null;
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
            _p.RecoveryOwner = _owner;
            _prepared = true;
        }

        // The player's own exit action, exactly as the other phases drive it. It is preparation for
        // the route, so it is not asserted here; the phase's evidence is the travel facts.
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
                TravelRecoveryReceipt.UndockSeconds, "the native undock before the route")) yield return frame;
        }

        private string? DockingState(object ship)
        {
            var data = SpGet(ship, "spaceShipData");
            return data == null ? null : SpGet(data, "dockingState")?.ToString();
        }

        // --- shared waiting and recording -------------------------------------------------

        // An accepted native request is not a departure: nothing has been transported in the same
        // frame, so a Departed here could only be fabricated.
        private void RequireNoFabricatedDeparture(int offset, string label)
        {
            var frame = TravelResilienceReceipt.CheckRequestFrame(Slice(offset), label);
            Require(frame == null, frame ?? "");
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
                Require(Time.realtimeSinceStartup < until, "Timed out after " + seconds + "s waiting for " + description
                    + " (" + _p.RecoverySnapshot().ToDetail() + ").");
                yield return null;
            }
        }

        private void NotRun(string reason)
        {
            _p.RcRecord(_p._rcCase, _p._rcDescription, TravelStationReceipt.NotRun, "", _session, null, "", reason);
            _p.RcCheckpoint();
        }
        private void Pass(string nativeIdentity, Guid? operation, string evidence, string detail)
        {
            _p.RcRecord(_p._rcCase, _p._rcDescription, TravelStationReceipt.Passed, nativeIdentity, _session, operation, evidence, detail);
            _p.RcCheckpoint();
        }
        // The active event label belongs to a driving case only; between cases nothing may claim it.
        private void EndCase() { _p.RcEndCase(); _p.RcCheckpoint(); }

        private static Type Named(string name) => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name, "type");
    }
}
