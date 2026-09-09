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
    // Per-case native driver for phase travel-resilience-v1. It runs inside the plugin iterator
    // context so real Unity coroutines drive every transition, and it only calls vanilla entry
    // points a player action would call: TravelManager.TryInitiateTravel / CancelTravel, the
    // station exit action, HudManager.Dock (the HUD dock button), the personal-hangar ship swap
    // (GamePlayer.SetSpaceShipData + GameplayManager.ReinitPlayerSpaceship) and
    // SpaceShip.InitSpacestationAutoActions (the native relink entry point). No native docking
    // state, location or waypoint is ever written by this phase.
    private sealed class TravelResilienceDriver
    {
        private readonly Plugin _p;
        private readonly Type _travelType, _poiType, _gameplayType, _shipType, _shipDataType;
        private readonly Type _stationType, _exteriorType, _interiorType, _dockingOptionType, _tunnelType, _hudType;
        private readonly MethodInfo _tryInitiateTravel, _canWeTravel, _cancelTravel, _travelActive, _localPoiReady;
        private readonly MethodInfo _startUndocking, _exitSpacestation, _getDockingOption, _playerIsFriendly;
        private readonly MethodInfo _initStationAutoActions, _reinitPlayerSpaceship, _setSpaceShipData, _dockingSize, _hudDock;
        // The two native coroutine factories this phase replays after a replacement load. They are
        // bound by their exact declared shapes on the type the API also patches, so the replay is
        // the real hooked boundary and not a lookalike.
        private readonly MethodInfo _undock;
        // Captured ONLY at this case's own fresh fixture load and readiness boundary. It is never
        // taken before a load (the load destroys the previous manager) and it is never re-bound to
        // a replacement: a destroyed or replaced instance is a recorded failure, not a new owner.
        private NativeCaseOwner? _owner;
        private Guid _session;
        private string _systemId = "";
        private string _startPoiId = "";
        private bool _prepared;
        private string _notPrepared = "";
        // A refusal DURING a case (as opposed to its preparation), recorded as a NOT-RUN reason.
        private string _refused = "";
        private string _reinitDetail = "";
        private string _replayDetail = "";

        internal TravelResilienceDriver(Plugin p)
        {
            _p = p;
            _travelType = Named("Behaviour.Managers.TravelManager");
            _poiType = Named("Source.Galaxy.MapPointOfInterest");
            _gameplayType = Named("GameplayManager");
            _shipType = Named("Behaviour.Unit.SpaceShip");
            _shipDataType = Named("Source.SpaceShip.SpaceShipData");
            _stationType = Named("Source.Galaxy.POI.SpaceStation");
            _exteriorType = Named("SpacestationExteriorManager");
            _interiorType = Named("Behaviour.UI.Spacestation.SpaceStationInterior");
            _dockingOptionType = Named("Behaviour.Spacestation.Docking.DockingOption");
            _tunnelType = Named("Behaviour.Spacestation.Docking.DockingTunnel");
            _hudType = Named("Behaviour.UI.HUD.HudManager");
            // Exact declared signatures: a name-only lookup can bind the wrong overload.
            _tryInitiateTravel = TravelStationDriver.Bind(_travelType, "TryInitiateTravel", typeof(bool), _poiType);
            _canWeTravel = TravelStationDriver.Bind(_travelType, "CanWeTravel", typeof(bool), _poiType);
            _cancelTravel = TravelStationDriver.Bind(_travelType, "CancelTravel", typeof(bool), typeof(Vector2?));
            _travelActive = TravelStationDriver.Bind(_travelType, "TravelActive", typeof(bool));
            _localPoiReady = TravelStationDriver.Bind(_travelType, "IsLocalPoiReady", typeof(bool));
            _startUndocking = TravelStationDriver.Bind(_exteriorType, "StartUndocking", typeof(void));
            _exitSpacestation = TravelStationDriver.Bind(_interiorType, "ExitSpacestation", typeof(void));
            _getDockingOption = TravelStationDriver.Bind(_exteriorType, "GetDockingOption", _dockingOptionType, _shipType);
            _playerIsFriendly = TravelStationDriver.Bind(_stationType, "PlayerIsFriendly", typeof(bool));
            // The native restore/relink and ship re-init entry points, plus the HUD dock button.
            _initStationAutoActions = TravelStationDriver.Bind(_shipType, "InitSpacestationAutoActions", typeof(void));
            _reinitPlayerSpaceship = TravelStationDriver.Bind(_gameplayType, "ReinitPlayerSpaceship", typeof(void));
            _setSpaceShipData = TravelStationDriver.Bind(_p._player, "SetSpaceShipData", typeof(void), _shipDataType);
            _dockingSize = TravelStationDriver.Bind(Named("Source.SpaceShip.SpaceShipRoleType"), "GetDockingOptionSize",
                Named("Behaviour.Spacestation.Docking.DockingOptionSize"));
            _hudDock = TravelStationDriver.Bind(_hudType, "Dock", typeof(void));
            _undock = TravelStationDriver.Bind(_dockingOptionType, "Undock", typeof(IEnumerator));
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

        // The same ownership proof for a native drive that does not go through the travel manager
        // (the station exit, the relink and the ship re-init all act on this case's own world).
        private void RequireOwned(string action) => NativeTravel(action);

        private List<TravelTransition> Travel => _p.PendingResilienceTravel!;
        private List<StationTransition> Stations => _p.PendingResilienceStation!;
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
            foreach (var frame in CaseEmptyOriginReroute()) yield return frame;
            EndCase();
            foreach (var frame in CaseRestoreRelinkDock()) yield return frame;
            EndCase();
            foreach (var frame in CaseStaleSessionReplay()) yield return frame;
            EndCase();
            // The cases legitimately end in another world state (parked at a re-routed POI, in a
            // swapped ship, in a replacement session). Restore the fixture the later pilots expect;
            // this is harness cleanup and is never recorded as coverage.
            foreach (var frame in _p.SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            _p.RsCheckpoint();
        }

        // --- case: empty-origin re-route ---------------------------------------------------

        // A real native in-system route is started from the loaded start station and allowed to
        // reach the verified origin unload (the public Departed). Only then, with the origin scene
        // already gone and the native current POI actually null, the route is cancelled and a NEW
        // route to a different POI is requested: that leg's departure can no longer come from an
        // UnloadCurrentScene transition (it is a NOOP now), so it must come from the actual native
        // warp start. Nothing is forced: no field is nulled and no location is assigned.
        private IEnumerable<object?> CaseEmptyOriginReroute()
        {
            _p.RsCase(TravelResilienceReceipt.EmptyOriginCase, TravelResilienceReceipt.EmptyOriginDescription);
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            foreach (var frame in Undock()) yield return frame;
            if (_notPrepared.Length > 0) { NotRun(_notPrepared); yield break; }
            var hops = SafeTargets();
            if (hops.Length < 2)
            {
                NotRun("Only " + hops.Length + " safe in-system target(s) beside the start station; a re-route needs two distinct targets (" + _p.SafeTargetSelection + ").");
                yield break;
            }
            var first = hops[0];
            var second = hops[1];
            var firstId = (string)SpGet(first, "guid")!;
            var secondId = (string)SpGet(second, "guid")!;
            // The quiet window opens BEFORE the availability wait, because that wait is exactly
            // where an unsolicited native route (qa-82's emergency-jump return) appeared.
            int offset = Travel.Count;
            int stationOffset = Stations.Count;
            foreach (var frame in PollFor(() => false, TravelResilienceReceipt.TravelReadySeconds)) yield return frame;
            var unsolicited = TravelStationReceipt.CheckNoUnsolicitedTravel("the re-route case", Slice(offset),
                (bool)_travelActive.Invoke(NativeTravel("the re-route case"), null)!, _p.NativeAutonomyDetail());
            Require(unsolicited == null, unsolicited!);
            if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the abandoned leg"), new[] { first })!)
            {
                NotRun("Native CanWeTravel refused the first in-system route to " + firstId + ".");
                yield break;
            }
            Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the abandoned leg"), new[] { first })!,
                "Native TryInitiateTravel refused the first in-system route.");
            // Same frame: nothing has been transported yet, so any Departed here would be fabricated.
            RequireNoFabricatedDeparture(offset, "the accepted request");
            // The verified origin unload: the ONLY thing that ends a loaded origin's dwell.
            foreach (var frame in AwaitOrFail(() => Slice(offset).Any(fact => fact.Kind == TravelTransitionKind.Departed),
                TravelResilienceReceipt.DepartureSeconds, "the native origin unload departure of the abandoned leg")) yield return frame;
            // The required precondition of this case, OBSERVED and never forced: the native player
            // really has no current POI any more.
            Require(SpGet(Player, "currentPointOfInterest") == null,
                "The abandoned leg departed while the native player still has a current POI; this case needs a really unloaded origin.");
            Require(!TravelStationDriver.Alive(SpGet(NativeTravel("the unloaded-origin check"), "localPoiManager")),
                "The native local POI manager is still live after the origin unload.");
            Require((bool)_cancelTravel.Invoke(NativeTravel("native CancelTravel of the abandoned leg"), new object?[] { null })!,
                "Native CancelTravel(null) refused while the origin was unloaded.");
            foreach (var frame in Settle()) yield return frame;
            Require(SpGet(Player, "currentPointOfInterest") == null, "The native current POI reappeared after cancelling an already-departed leg.");
            // Native travel refuses for three real seconds after a warp start (delayTravelAttempt),
            // so availability is sampled after that window instead of being read as a refusal.
            foreach (var frame in PollFor(() => false, TravelResilienceReceipt.TravelReadySeconds)) yield return frame;
            if (!(bool)_canWeTravel.Invoke(NativeTravel("native CanWeTravel for the re-route"), new[] { second })!)
            {
                NotRun("Native CanWeTravel refused the re-route to " + secondId + " while the origin was unloaded.");
                yield break;
            }
            int rerouteOffset = Travel.Count;
            Require((bool)_tryInitiateTravel.Invoke(NativeTravel("native TryInitiateTravel for the re-route"), new[] { second })!,
                "Native TryInitiateTravel refused the re-route.");
            RequireNoFabricatedDeparture(rerouteOffset, "the accepted re-route request");
            var waypoints = (IList)SpGet(Player, "waypoints")!;
            Require(waypoints.Count == 1 && ReferenceEquals(waypoints[0], second), "The native re-route did not produce the expected single waypoint.");
            foreach (var frame in AwaitOrFail(() => Slice(rerouteOffset).Any(fact => fact.Kind == TravelTransitionKind.Arrived),
                TravelResilienceReceipt.ArrivalSeconds, "the native arrival of the re-routed leg at " + secondId)) yield return frame;
            foreach (var frame in AwaitOrFail(() => Slice(rerouteOffset).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
                TravelResilienceReceipt.BoundarySeconds, "the native final route boundary at " + secondId)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var slice = Slice(offset);
            var failure = TravelResilienceReceipt.CheckReroute(slice, _session, _systemId, _startPoiId, firstId, secondId);
            Require(failure == null, failure!);
            failure = TravelResilienceReceipt.CheckRerouteEvidence(slice, _p._rsSnapshots);
            Require(failure == null, failure!);
            var arrivedOwner = NativeTravel("the re-routed arrival checks");
            var manager = SpGet(arrivedOwner, "localPoiManager");
            Require(TravelStationDriver.Alive(manager) && ReferenceEquals(SpGet(manager!, "poi"), second)
                && (bool)SpGet(manager!, "initializedAndReady")!,
                "The native local manager is not the initialized manager of the re-routed destination.");
            Require((bool)_localPoiReady.Invoke(arrivedOwner, null)!, "Native IsLocalPoiReady is false after the re-routed arrival.");
            Require(!(bool)_travelActive.Invoke(arrivedOwner, null)!, "Native travel is still active after the final route boundary.");
            Require(((ICollection)SpGet(Player, "waypoints")!).Count == 0, "Native waypoints remain after the final route boundary.");
            Require(ReferenceEquals(SpGet(Player, "currentPointOfInterest"), second), "The native current POI is not the re-routed destination.");
            Require(TravelStationReceipt.Same(ModApi.Services.Travel.CurrentLocation, _systemId, secondId),
                "Public CurrentLocation does not match the re-routed destination.");
            var stationFacts = StationSlice(stationOffset);
            Require(stationFacts.All(fact => fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed),
                "The re-route emitted physical station facts: " + string.Join(", ", stationFacts.Select(TravelStationReceipt.Describe)));
            var departure = _p._rsSnapshots[slice[4].Sequence];
            Pass(TravelStationReceipt.Location(_systemId, secondId), slice[3].OperationId,
                TravelStationReceipt.Evidence(slice, null),
                "origin=" + TravelStationReceipt.Location(_systemId, _startPoiId) + "; abandonedHop=" + firstId + "; rerouteHop=" + secondId
                + "; abandonedOperation=" + slice[0].OperationId + "; facts=" + slice.Count
                + "; emptyOriginDepartureSnapshot=" + departure.ToDetail());
        }

        // --- case: restore/relink dock suppression -----------------------------------------

        // Every native assignment path that is NOT a docking request must stay silent on the public
        // station surface, while a genuine request in the same session must still produce exactly
        // one DockedPhysical. The window opens BEFORE the fixture load so the load's own native
        // restore (InitializePoi(init: true) -> AssignClosestDockingOption(ship, init: true)) is
        // inside it.
        private IEnumerable<object?> CaseRestoreRelinkDock()
        {
            _p.RsCase(TravelResilienceReceipt.RestoreDockCase, TravelResilienceReceipt.RestoreDockDescription);
            int loadOffset = Travel.Count;
            int loadStationOffset = Stations.Count;
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            var ship = PlayerShip;
            var exterior = Exterior;
            if (!TravelStationDriver.Alive(ship) || !TravelStationDriver.Alive(exterior) || !_stationType.IsInstanceOfType(SpGet(Player, "currentPointOfInterest")))
            {
                NotRun("The fixture does not load docked at a live station exterior with a player ship.");
                yield break;
            }
            // The initial dock restore can still be settling natively right after the load.
            foreach (var frame in PollFor(() => DockingState(ship!) == "Docked", TravelResilienceReceipt.InitialDockSettleSeconds)) yield return frame;
            if (DockingState(ship!) != "Docked" || !TravelStationDriver.Alive(_getDockingOption.Invoke(exterior!, new[] { ship })))
            {
                NotRun("Fixture ship state is " + (DockingState(ship!) ?? "undocked") + " with no holding docking option; there is no native restore to observe.");
                yield break;
            }
            if (!(bool)_playerIsFriendly.Invoke(SpGet(Player, "currentPointOfInterest")!, null)!)
            {
                NotRun("Start station is not player-friendly; refusing to drive dock/undock at a hostile station.");
                yield break;
            }
            // Subcase 1: the load's own native restore assignment. The replaced session's facts are
            // legitimate as a contiguous prefix here (this is the load boundary), the fresh session
            // must show only its placement, and the native restore must have emitted no physical
            // station fact even though the ship really reached Docked.
            var boundary = TravelStationReceipt.CheckLoadBoundary(Slice(loadOffset), _session, out int freshIndex, out int priorFacts);
            Require(boundary == null, boundary!);
            var loadWindow = Slice(loadOffset + freshIndex);
            var placement = TravelStationReceipt.CheckInitialPlacement(loadWindow, _session, _systemId, _startPoiId);
            Require(placement == null, placement!);
            var restore = TravelResilienceReceipt.CheckLoadRestoreSuppression(StationSlice(loadStationOffset), _session,
                out int priorStationFacts, out int restoreInterior);
            Require(restore == null, restore!);

            // Subcase 2: the native relink entry point. SpaceShip.InitSpacestationAutoActions on the
            // docked player ship reaches RelinkDockedShipToStation, which assigns the ship to the
            // docking option it is physically at (skipCoroutine: true). Real native call, real
            // native assignment, and it must emit nothing.
            RequireOwned("the native relink");
            int relinkTravel = Travel.Count, relinkStation = Stations.Count;
            var optionBeforeRelink = _getDockingOption.Invoke(exterior!, new[] { ship })!;
            _initStationAutoActions.Invoke(ship!, null);
            foreach (var frame in AwaitOrFail(() => DockingState(ship!) == "Docked"
                && ReferenceEquals(_getDockingOption.Invoke(exterior!, new[] { ship }), optionBeforeRelink),
                TravelResilienceReceipt.RestoreDockSeconds, "the native relink to keep the ship physically docked")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var relink = TravelResilienceReceipt.CheckSuppressed("relink (RelinkDockedShipToStation)",
                Slice(relinkTravel), StationSlice(relinkStation), out int relinkInterior);
            Require(relink == null, relink!);

            // Subcase 3: the positive control, driven BEFORE any ship swap so it uses the fixture's
            // own known-good ship. The player's exit action and then the HUD dock button are two
            // genuine native actions, and the request path must still produce exactly one
            // DockedPhysical. Without this a permanently silent observer would "pass" the case.
            int controlTravel = Travel.Count, controlStation = Stations.Count;
            foreach (var frame in DriveGenuineDockRequest(ship!, exterior!, controlStation)) yield return frame;
            if (_refused.Length > 0) { NotRun(_refused); yield break; }
            var control = TravelStationReceipt.CheckStationPhase(StationSlice(controlStation), _session, _systemId, _startPoiId,
                new[] { StationTransitionKind.Undocking, StationTransitionKind.Leaving, StationTransitionKind.DockedPhysical });
            Require(control == null, control!);
            Require(Slice(controlTravel).Count == 0, "The genuine dock request emitted travel facts: "
                + string.Join(", ", Slice(controlTravel).Select(TravelStationReceipt.Describe)));

            // Subcases 4 and 5: the native ship re-init. The MANDATORY same-size branch keeps the
            // current docking option (skipCoroutine assignment) and needs no second owned ship: the
            // native routine re-initializes whatever GamePlayer.currentSpaceShip is, exactly as the
            // hangar's own equipment/module actions do for the current ship. The different-size
            // branch finds a new option and takes a REAL Dock() coroutine outside any docking
            // request. Neither may emit a physical fact.
            var controlSlice = StationSlice(controlStation);
            var controlEvidence = TravelStationReceipt.Evidence(null, controlSlice.Where(fact => fact.Kind == StationTransitionKind.DockedPhysical
                || fact.Kind == StationTransitionKind.Undocking || fact.Kind == StationTransitionKind.Leaving));
            foreach (var frame in DriveCurrentShipReinit(exterior!)) yield return frame;
            var sameSizeDetail = _reinitDetail;
            // Mandatory subcase row: the same-size branch is receipt evidence in its own right, and
            // a not-run row is never accepted. Its evidence references the case's own genuine
            // docking request, which proves the observer was live in this very window while the
            // re-init stayed silent.
            _p.RsRecord(TravelResilienceReceipt.SameSizeReinitCase, TravelResilienceReceipt.SameSizeReinitDescription,
                TravelStationReceipt.Passed, TravelStationReceipt.Location(_systemId, _startPoiId), _session, null,
                controlEvidence, sameSizeDetail + "; positiveControl=" + controlEvidence);
            _p.RsCheckpoint();
            var differentSize = SwapCandidates(out string differentReason).FirstOrDefault(candidate => candidate.DifferentSize);
            if (differentSize == null)
            {
                // Strictly required: a same-size-only run must never be reported as two-size
                // coverage, so this is a recorded NOT-RUN (a phase failure), never a silent skip.
                NotRun("No owned ship with a different native docking size is available for the required different-size re-init: " + differentReason);
                yield break;
            }
            foreach (var frame in DriveReinit(differentSize, exterior!)) yield return frame;
            Pass(TravelStationReceipt.Location(_systemId, _startPoiId), null, controlEvidence,
                "replacedSessionFactsBeforeLoad=" + priorFacts + "/" + priorStationFacts
                + "; initialRestore={interiorFacts=" + restoreInterior + "}"
                + "; relink={interiorFacts=" + relinkInterior + "}"
                + "; genuineRequest={facts=" + controlSlice.Count + "}"
                + "; sameSizeReinit=" + sameSizeDetail
                + "; differentSizeReinit=" + _reinitDetail);
        }

        // The player's own exit action followed by the HUD dock button: two genuine native actions
        // whose docking request must produce exactly one DockedPhysical.
        private IEnumerable<object?> DriveGenuineDockRequest(object ship, object exterior, int stationOffset)
        {
            _refused = "";
            RequireOwned("the genuine native docking request");
            var interior = SpGet(_interiorType, "instance");
            if (TravelStationDriver.Alive(interior)) _exitSpacestation.Invoke(interior!, null);
            else _startUndocking.Invoke(exterior, null);
            foreach (var frame in AwaitOrFail(() => DockingState(ship) == "Leaving"
                && !TravelStationDriver.Alive(SpGet(exterior, "undockingRoutine"))
                && !TravelStationDriver.Alive(_getDockingOption.Invoke(exterior, new[] { ship })),
                TravelResilienceReceipt.UndockSeconds, "the native undock before the genuine docking request")) yield return frame;
            // The native request path refuses while the interior scene is still current, so the
            // exit must have finished unloading it before the dock button is pressed.
            foreach (var frame in AwaitOrFail(() => !TravelStationDriver.Alive(SpGet(_interiorType, "instance")),
                TravelResilienceReceipt.UndockSeconds, "the native station interior to close before the docking request")) yield return frame;
            var hud = SpGet(_hudType, "Instance");
            if (!TravelStationDriver.Alive(hud))
            {
                _refused = "No live native HUD manager to press the dock button on.";
                yield break;
            }
            RequireOwned("the native HUD dock button");
            _hudDock.Invoke(hud!, null);
            foreach (var frame in AwaitOrFail(() => StationSlice(stationOffset).Any(fact => fact.Kind == StationTransitionKind.DockedPhysical),
                TravelResilienceReceipt.DockSeconds, "the native DockedPhysical of the genuine docking request")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            Require(DockingState(ship) == "Docked", "Native ship docking state is " + (DockingState(ship) ?? "null") + " after the genuine docking request.");
            var option = _getDockingOption.Invoke(exterior, new[] { ship });
            Require(TravelStationDriver.Alive(option) && ReferenceEquals(SpGet(option!, "dockingSpaceship"), ship),
                "No native docking option holds the player ship after the genuine docking request.");
        }

        // The MANDATORY same-docking-size branch, driven without any second owned ship and without
        // moving any inventory: the native re-init of the CURRENT owned ship, exactly as the
        // hangar's own equipment/module actions call it (GameplayManager.ReinitPlayerSpaceship when
        // the changed ship data IS GamePlayer.currentSpaceShip). The inspected
        // ReinitPlayerSpaceshipRoutine compares the current data against the live unit only in a
        // DISCARDED expression, so it re-spawns the ship anyway; its docking size therefore equals
        // the current option's size and it takes the skipCoroutine assignment branch. Nothing is
        // written here: the pilot invokes the native entry point and then waits for the actual new
        // unit and physical dock state.
        private IEnumerable<object?> DriveCurrentShipReinit(object exterior)
        {
            _reinitDetail = "";
            RequireOwned("the native current-ship re-init");
            var previousShip = PlayerShip;
            Require(TravelStationDriver.Alive(previousShip), "No live native player ship to re-initialize.");
            var shipData = SpGet(Player, "currentSpaceShip")!;
            Require(ReferenceEquals(SpGet(previousShip!, "spaceShipData"), shipData),
                "The live native player ship does not carry the player's current ship data.");
            var optionBefore = _getDockingOption.Invoke(exterior, new[] { previousShip })!;
            var sizeBefore = SpGet(optionBefore, "dockingOptionSize")!.ToString()!;
            int travelOffset = Travel.Count, stationOffset = Stations.Count;
            bool coroutineObserved = false;
            _reinitPlayerSpaceship.Invoke(SpGet(_gameplayType, "Instance")!, null);
            foreach (var frame in AwaitOrFail(() =>
                {
                    coroutineObserved |= DockingOptions(exterior).Any(option => SpGet(option, "dockingCoroutine") != null);
                    var live = PlayerShip;
                    return TravelStationDriver.Alive(live) && !ReferenceEquals(live, previousShip)
                        && ReferenceEquals(SpGet(live!, "spaceShipData"), shipData)
                        && DockingState(live!) == "Docked"
                        && TravelStationDriver.Alive(_getDockingOption.Invoke(exterior, new[] { live }));
                }, TravelResilienceReceipt.RestoreDockSeconds,
                "the native current-ship re-init to replace the unit and reach physical Docked")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var newShip = PlayerShip!;
            var optionAfter = _getDockingOption.Invoke(exterior, new[] { newShip })!;
            var sizeAfter = SpGet(optionAfter, "dockingOptionSize")!.ToString()!;
            var branch = TravelResilienceReceipt.CheckCurrentShipReinit(!ReferenceEquals(newShip, previousShip),
                ReferenceEquals(SpGet(Player, "currentSpaceShip"), shipData),
                !ReferenceEquals(optionAfter, optionBefore), sizeBefore, sizeAfter);
            Require(branch == null, branch!);
            Require(ReferenceEquals(SpGet(exterior, "currentDockingOption"), optionBefore),
                "The native exterior manager's current docking option changed during the same-size current-ship re-init.");
            Require(ReferenceEquals(SpGet(optionAfter, "dockingSpaceship"), newShip),
                "The unchanged native docking option does not hold the re-initialized ship.");
            var suppressed = TravelResilienceReceipt.CheckSuppressed("current-ship (same-size) re-init",
                Slice(travelOffset), StationSlice(stationOffset), out int interiorFacts);
            Require(suppressed == null, suppressed!);
            _reinitDetail = "{drive=native ReinitPlayerSpaceship on the current ship,unitReplaced=True,shipDataPreserved=True"
                + ",optionSizeBefore=" + sizeBefore + ",optionSizeAfter=" + sizeAfter
                + ",optionInstanceChanged=False,dockCoroutineObserved=" + coroutineObserved
                + ",interiorFacts=" + interiorFacts + "}";
        }

        // One native ship swap exactly as the personal hangar performs it (SetSpaceShipData then
        // GameplayManager.ReinitPlayerSpaceship), minus the hangar's inventory transfer so the
        // fixture's economy is not moved. The docking-size branch is proven from the native docking
        // option the routine actually used.
        private IEnumerable<object?> DriveReinit(SwapCandidate candidate, object exterior)
        {
            _reinitDetail = "";
            RequireOwned("the native ship re-init");
            var previousShip = PlayerShip;
            var optionBefore = _getDockingOption.Invoke(exterior, new[] { previousShip })!;
            var sizeBefore = SpGet(optionBefore, "dockingOptionSize")!.ToString()!;
            int travelOffset = Travel.Count, stationOffset = Stations.Count;
            bool coroutineObserved = false;
            _setSpaceShipData.Invoke(Player, new[] { candidate.Data });
            _reinitPlayerSpaceship.Invoke(SpGet(_gameplayType, "Instance")!, null);
            // The re-init destroys the old ship and spawns the new one docked; wait for the actual
            // physical state, never for the method to return. The different-size branch takes a real
            // native Dock() coroutine, so its live coroutine handle is sampled while waiting: that is
            // recorded evidence of the branch, never a substitute for the option-identity proof.
            foreach (var frame in AwaitOrFail(() =>
                {
                    coroutineObserved |= DockingOptions(exterior).Any(option => SpGet(option, "dockingCoroutine") != null);
                    var live = PlayerShip;
                    return TravelStationDriver.Alive(live) && !ReferenceEquals(live, previousShip)
                        && ReferenceEquals(SpGet(live!, "spaceShipData"), candidate.Data)
                        && DockingState(live!) == "Docked"
                        && TravelStationDriver.Alive(_getDockingOption.Invoke(exterior, new[] { live }));
                }, TravelResilienceReceipt.RestoreDockSeconds,
                "the native " + (candidate.DifferentSize ? "different" : "same") + "-size ship re-init to reach physical Docked")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var newShip = PlayerShip!;
            var optionAfter = _getDockingOption.Invoke(exterior, new[] { newShip })!;
            var sizeAfter = SpGet(optionAfter, "dockingOptionSize")!.ToString()!;
            var branch = TravelResilienceReceipt.CheckReinitBranch(candidate.DifferentSize, candidate.Size, sizeBefore, sizeAfter,
                !ReferenceEquals(optionAfter, optionBefore));
            Require(branch == null, branch!);
            Require(ReferenceEquals(SpGet(_exteriorType, "Instance"), exterior), "The native station exterior manager was replaced during the ship re-init.");
            // The routine's own current-option field: the same-size branch keeps it, the
            // different-size branch reassigns it, and either way it must be the option that
            // actually holds the re-initialized ship.
            Require(ReferenceEquals(SpGet(exterior, "currentDockingOption"), optionAfter),
                "The native exterior manager's current docking option is not the option holding the re-initialized ship.");
            var suppressed = TravelResilienceReceipt.CheckSuppressed(
                (candidate.DifferentSize ? "different" : "same") + "-size ship re-init",
                Slice(travelOffset), StationSlice(stationOffset), out int interiorFacts);
            Require(suppressed == null, suppressed!);
            _reinitDetail = "{ship=" + candidate.Size + ",optionSizeBefore=" + sizeBefore + ",optionSizeAfter=" + sizeAfter
                + ",optionInstanceChanged=" + (!ReferenceEquals(optionAfter, optionBefore))
                + ",dockCoroutineObserved=" + coroutineObserved
                + ",interiorFacts=" + interiorFacts + "}";
        }

        // --- case: stale-session replay ------------------------------------------------------

        // Two real native undock coroutines are CAPTURED (created, so the API's own factory hook
        // wraps them and pins their session/player ownership) in session A: one is never advanced
        // there, the other is advanced exactly one step so it produces its genuine session-A fact.
        // A replacement fixture load then destroys that world, and both are advanced afterwards
        // through a bounded Unity-frame replay. Neither may produce anything in the replacement
        // session, the replacement session's own operation must still work, and a vanilla exception
        // on vanilla's own destroyed objects is recorded rather than suppressed or called a pass.
        private IEnumerable<object?> CaseStaleSessionReplay()
        {
            _p.RsCase(TravelResilienceReceipt.StaleReplayCase, TravelResilienceReceipt.StaleReplayDescription);
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            var oldShip = PlayerShip;
            var oldExterior = Exterior;
            if (!TravelStationDriver.Alive(oldShip) || !TravelStationDriver.Alive(oldExterior))
            {
                NotRun("The fixture has no live player ship and station exterior to capture an old native coroutine from.");
                yield break;
            }
            foreach (var frame in PollFor(() => DockingState(oldShip!) == "Docked", TravelResilienceReceipt.InitialDockSettleSeconds)) yield return frame;
            var oldOption = _getDockingOption.Invoke(oldExterior!, new[] { oldShip });
            if (DockingState(oldShip!) != "Docked" || !TravelStationDriver.Alive(oldOption))
            {
                NotRun("Fixture ship state is " + (DockingState(oldShip!) ?? "undocked") + " with no holding docking option; there is no native undock boundary to capture.");
                yield break;
            }
            var oldSession = _session;
            RequireOwned("capturing the old native undock coroutines");
            // Creating an iterator runs no native code: the bodies below execute only when they are
            // advanced. Both are the API's own wrapped boundary, captured under session A.
            var beforeFirstAdvance = (IEnumerator)_undock.Invoke(oldOption!, null)!;
            var midIteration = (IEnumerator)_undock.Invoke(oldOption!, null)!;
            int midOffset = Stations.Count;
            bool midMoved = false;
            Exception? midError = null;
            try { midMoved = midIteration.MoveNext(); }
            catch (Exception error) { midError = error; }
            Require(midError == null, "The native undock coroutine threw on its first step in its own live session: " + midError);
            Require(midMoved, "The native undock coroutine ended on its first step in its own live session.");
            foreach (var frame in Settle()) yield return frame;
            var midFact = TravelStationReceipt.CheckStationPhase(StationSlice(midOffset), oldSession, _systemId, _startPoiId,
                new[] { StationTransitionKind.Undocking });
            Require(midFact == null, "The mid-iteration capture did not produce its own live-session Undocking fact: " + midFact);

            // The replacement load. It destroys the captured world, which is exactly the state the
            // replayed coroutines must not be able to act on.
            int loadOffset = Travel.Count;
            foreach (var frame in Prepare()) yield return frame;
            if (!_prepared) { NotRun(_notPrepared); yield break; }
            var replacement = _session;
            Require(replacement != oldSession, "The replacement load did not produce a new session identity.");
            var boundary = TravelStationReceipt.CheckLoadBoundary(Slice(loadOffset), replacement, out int freshIndex, out int priorFacts);
            Require(boundary == null, boundary!);
            var placement = TravelStationReceipt.CheckInitialPlacement(Slice(loadOffset + freshIndex), replacement, _systemId, _startPoiId);
            Require(placement == null, placement!);
            Require(!TravelStationDriver.Alive(oldOption) && !TravelStationDriver.Alive(oldShip),
                "The replacement load did not destroy the captured native docking option/ship, so this would not be a stale replay: "
                + NativeCaseOwner.Identity(oldOption, TravelStationDriver.Alive) + ", " + NativeCaseOwner.Identity(oldShip, TravelStationDriver.Alive));

            int replayTravel = Travel.Count, replayStation = Stations.Count;
            foreach (var frame in Replay(beforeFirstAdvance, "beforeFirstAdvance")) yield return frame;
            var beforeDetail = _replayDetail;
            foreach (var frame in Replay(midIteration, "midIteration")) yield return frame;
            var midDetail = _replayDetail;
            foreach (var frame in Settle()) yield return frame;
            var silence = TravelResilienceReceipt.CheckReplaySilence(Slice(replayTravel), StationSlice(replayStation), replacement, out int replayInterior);
            Require(silence == null, silence!);
            // The replay must not have faulted the observer group: a stale operation is reported,
            // never a reason to disable the replacement session's capability or services.
            Require(ModApi.Services.Travel.Availability.IsAvailable,
                "The stale replay disabled the native-travel capability.");
            Require(ModApi.Services.Travel.Availability.IsAvailable && ModApi.Services.Station.Availability.IsAvailable, "The stale replay removed the public travel/station services.");
            Require(ModApi.Services.Travel.SessionId == replacement, "The public travel service no longer owns the replacement session after the replay.");
            Require(TravelStationReceipt.Same(ModApi.Services.Travel.CurrentLocation, _systemId, _startPoiId),
                "Public CurrentLocation left the replacement session's actual location during the replay.");

            // The replacement session's own native operation must still produce its facts.
            var newShip = PlayerShip;
            var newExterior = Exterior;
            if (!TravelStationDriver.Alive(newShip) || !TravelStationDriver.Alive(newExterior))
            {
                NotRun("The replacement session has no live player ship and station exterior to drive a new native operation on.");
                yield break;
            }
            foreach (var frame in PollFor(() => DockingState(newShip!) == "Docked", TravelResilienceReceipt.InitialDockSettleSeconds)) yield return frame;
            if (DockingState(newShip!) != "Docked" || !TravelStationDriver.Alive(_getDockingOption.Invoke(newExterior!, new[] { newShip })))
            {
                NotRun("The replacement session's ship is " + (DockingState(newShip!) ?? "undocked") + " with no holding docking option; no new native undock to drive.");
                yield break;
            }
            int newOperation = Stations.Count;
            RequireOwned("the replacement session's own native undock");
            var interior = SpGet(_interiorType, "instance");
            if (TravelStationDriver.Alive(interior)) _exitSpacestation.Invoke(interior!, null);
            else _startUndocking.Invoke(newExterior!, null);
            foreach (var frame in AwaitOrFail(() => StationSlice(newOperation).Any(fact => fact.Kind == StationTransitionKind.Leaving),
                TravelResilienceReceipt.UndockSeconds, "the replacement session's own native Leaving fact")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var operation = TravelStationReceipt.CheckStationPhase(StationSlice(newOperation), replacement, _systemId, _startPoiId,
                new[] { StationTransitionKind.Undocking, StationTransitionKind.Leaving });
            Require(operation == null, operation!);
            Pass(TravelStationReceipt.Location(_systemId, _startPoiId), null,
                TravelStationReceipt.Evidence(null, StationSlice(newOperation)),
                "replacedSession=" + oldSession + "; replacementSession=" + replacement
                + "; replacedSessionFactsBeforeLoad=" + priorFacts
                + "; " + beforeDetail
                + "; " + midDetail
                + "; replayInteriorFacts=" + replayInterior);
        }

        // Bounded Unity-frame replay of one captured old coroutine, with the same nesting semantics
        // Unity's own runner uses for child iterators: a returned IEnumerator is advanced before its
        // parent continues. A vanilla exception on vanilla's own destroyed objects is recorded with
        // its actual type and throwing member; it is never swallowed silently and never on its own
        // treated as a pass.
        private IEnumerable<object?> Replay(IEnumerator root, string label)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            int steps = 0;
            string? exceptionType = null, exceptionSite = null;
            float until = Time.realtimeSinceStartup + TravelResilienceReceipt.ReplaySeconds;
            while (stack.Count > 0 && steps < TravelResilienceReceipt.ReplayStepBudget && Time.realtimeSinceStartup < until)
            {
                var current = stack.Peek();
                bool moved;
                try { moved = current.MoveNext(); }
                catch (Exception error)
                {
                    exceptionType = error.GetType().FullName;
                    exceptionSite = error.TargetSite == null ? "<unknown>" : error.TargetSite.DeclaringType?.FullName + "." + error.TargetSite.Name;
                    break;
                }
                steps++;
                if (!moved) { stack.Pop(); continue; }
                if (current.Current is IEnumerator child) stack.Push(child);
                yield return null; // a real Unity frame between steps
            }
            bool completed = stack.Count == 0;
            try { (root as IDisposable)?.Dispose(); } catch { /* disposal of a stale iterator is not evidence */ }
            _replayDetail = TravelResilienceReceipt.DescribeReplay(label, steps, completed, exceptionType, exceptionSite);
        }

        // --- fixture preparation -------------------------------------------------------------

        // Each case starts from its own fresh fixture session, and the native owner is captured ONLY
        // at that load's readiness boundary: a fixture load destroys the previous scene's manager,
        // and a cached reference to it stays live-looking while every native call on it fails.
        private IEnumerable<object?> Prepare()
        {
            _prepared = false; _notPrepared = ""; _owner = null; _p.ResilienceOwner = null;
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
                _notPrepared = "The freshly loaded fixture has no live native travel manager/player to own the case (manager="
                    + NativeCaseOwner.Identity(manager, TravelStationDriver.Alive) + ", player="
                    + NativeCaseOwner.Identity(owner, TravelStationDriver.Alive) + ").";
                yield break;
            }
            _owner = new NativeCaseOwner(_session, manager!, owner!, _systemId, _startPoiId);
            _p.ResilienceOwner = _owner;
            _prepared = true;
        }

        // The player's own exit action, exactly as the in-system phase drives it. It is preparation
        // for the route, so it is not asserted here; the phase's evidence is the travel facts.
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
                TravelResilienceReceipt.UndockSeconds, "the native undock before the in-system route")) yield return frame;
        }

        // --- fixture selection ---------------------------------------------------------------

        // One owned ship the native hangar swap could equip, with the docking size the re-init
        // routine compares against the current docking option.
        private sealed class SwapCandidate
        {
            internal object Data { get; }
            internal string Size { get; }
            internal bool DifferentSize { get; }
            internal SwapCandidate(object data, string size, bool differentSize)
            { Data = data; Size = size; DifferentSize = differentSize; }
        }

        // Read-only scan of the player's own owned ships, using exactly the eligibility the native
        // hangar uses (a different ship that is not carrier-assigned), plus conservative exclusions
        // for ships the world is currently using (active fleet, repairs, boarding). Candidates whose
        // docking size has no native option at this station are excluded so the native routine can
        // never be driven into a null docking option.
        private SwapCandidate[] SwapCandidates(out string reason)
        {
            var exterior = Exterior;
            var currentData = SpGet(Player, "currentSpaceShip");
            var ship = PlayerShip;
            if (!TravelStationDriver.Alive(exterior) || currentData == null || !TravelStationDriver.Alive(ship))
            {
                reason = "no live station exterior, player ship or current ship data";
                return Array.Empty<SwapCandidate>();
            }
            var currentOption = _getDockingOption.Invoke(exterior!, new[] { ship });
            if (!TravelStationDriver.Alive(currentOption))
            {
                reason = "the player ship is not held by a native docking option";
                return Array.Empty<SwapCandidate>();
            }
            var currentSize = SpGet(currentOption!, "dockingOptionSize")!.ToString()!;
            var fleet = ((IEnumerable)SpGet(Player, "activeFleet")!).Cast<object>().ToArray();
            var owned = ((IEnumerable)SpGet(Player, "spaceShips")!).Cast<object>().ToArray();
            var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
            var candidates = new List<SwapCandidate>();
            foreach (var data in owned)
            {
                if (ReferenceEquals(data, currentData)) continue;
                var shipClass = SpGet(data, "shipClass");
                if (!TravelStationDriver.Alive(shipClass)) continue;
                if ((bool)SpGet(data, "IsCarrierAssigned")!) continue;
                if ((bool)SpGet(data, "awayForRepairs")!) continue;
                if (SpGet(data, "boardingSimulation") != null) continue;
                if (fleet.Any(member => ReferenceEquals(member, data))) continue;
                var roleType = SpGet(shipClass!, "shipRoleType");
                if (roleType == null) continue;
                var size = _dockingSize.Invoke(roleType, null)!.ToString()!;
                sizes[size] = sizes.TryGetValue(size, out int count) ? count + 1 : 1;
                if (!HasDockingOptionForSize(exterior!, size)) continue;
                candidates.Add(new SwapCandidate(data, size, size != currentSize));
            }
            // Prefer a size whose native option is currently free, so the vanilla routine does not
            // have to displace an NPC ship that already occupies the closest matching option.
            var ordered = candidates
                .OrderByDescending(candidate => HasFreeDockingOptionForSize(exterior!, candidate.Size))
                .ToArray();
            reason = "ownedShips=" + owned.Length + ", eligible=" + candidates.Count + ", currentOptionSize=" + currentSize
                + ", ownedSizes=" + string.Join("/", sizes.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + ":" + entry.Value))
                + ", stationOptionSizes=" + string.Join("/", DockingOptions(exterior!)
                    .Select(option => SpGet(option, "dockingOptionSize")!.ToString()!)
                    .GroupBy(size => size, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key + ":" + group.Count()));
            return ordered;
        }

        private IEnumerable<object> DockingOptions(object exterior)
            => ((IEnumerable)SpGet(exterior, "dockingOptions")!).Cast<object>().Where(TravelStationDriver.Alive);

        // The native routine picks the closest option that CanDock(size), and falls back to a
        // docking tunnel; without either it would dereference a null option.
        private bool HasDockingOptionForSize(object exterior, string size)
            => DockingOptions(exterior).Any(option => SpGet(option, "dockingOptionSize")!.ToString() == size)
                || DockingOptions(exterior).Any(option => _tunnelType.IsInstanceOfType(option));

        private bool HasFreeDockingOptionForSize(object exterior, string size)
            => DockingOptions(exterior).Any(option => SpGet(option, "dockingOptionSize")!.ToString() == size
                && !(bool)SpGet(option, "occupied")!);

        // The shared authoritative safe-target selector (Plugin.SafeInSystemTargets): industrial
        // POIs only, never a station/gate/wormhole and never a native combat encounter, so the
        // re-route cannot end in a fight whose emergency jump starts an unsolicited native route.
        private object[] SafeTargets() => _p.SafeInSystemTargets();

        private string? DockingState(object ship)
        {
            var data = SpGet(ship, "spaceShipData");
            return data == null ? null : SpGet(data, "dockingState")?.ToString();
        }

        // --- shared waiting and recording -------------------------------------------------

        // An accepted native request is not a departure: nothing has been transported in the same
        // frame, so a Departed here could only be fabricated. The decision and its message are the
        // pure TravelResilienceReceipt.CheckRequestFrame rule, so the diagnostic is built only from
        // facts that exist; C# evaluates a Require message eagerly, and formatting a missing fact on
        // the success path is what aborted the healthy qa-83 case.
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
                    + " (" + _p.ResilienceSnapshot().ToDetail() + ").");
                yield return null;
            }
        }

        private void NotRun(string reason)
        {
            _p.RsRecord(_p._rsCase, _p._rsDescription, TravelStationReceipt.NotRun, "", _session, null, "", reason);
            _p.RsCheckpoint();
        }
        private void Pass(string nativeIdentity, Guid? operation, string evidence, string detail)
        {
            _p.RsRecord(_p._rsCase, _p._rsDescription, TravelStationReceipt.Passed, nativeIdentity, _session, operation, evidence, detail);
            _p.RsCheckpoint();
        }
        // The active event label belongs to a driving case only; between cases nothing may claim it.
        private void EndCase() { _p.RsEndCase(); _p.RsCheckpoint(); }

        private static Type Named(string name) => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name, "type");
    }
}
