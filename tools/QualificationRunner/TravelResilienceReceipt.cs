using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the native travel RESILIENCE pilot (phase <see cref="Phase"/>),
/// the third separate optional phase after travel-in-system-station-v1 and travel-cross-system-v1.
/// It contains no Unity, BepInEx or reflection dependency, so the exact rules that decide PASS/FAIL,
/// the exact public-fact ordering and the suppression/replay evidence rules are host regressions
/// rather than prose.
///
/// This phase never widens the other two phases: they keep their own required cases and keep
/// recording these three matrix cells as their own optional NOT-RUN rows. A pass here is coverage
/// of THIS phase's three cases only.
/// </summary>
internal static class TravelResilienceReceipt
{
    /// <summary>Honest scope of the delivered phase; the residual travel matrix stays open.</summary>
    internal const string Phase = "travel-resilience-v1";
    internal const string EmptyOriginCase = "empty-origin-reroute";
    internal const string RestoreDockCase = "restore-relink-dock";
    internal const string StaleReplayCase = "stale-session-replay";
    internal const string EmptyOriginDescription = "A real native in-system re-route requested while the origin scene is already unloaded emits Requested->Departed->Cancelled for the abandoned leg and Requested->Departed->Arrived->RouteCompleted for the new leg, with the new departure observed at the actual warp start from an unknown origin.";
    internal const string RestoreDockDescription = "The native restore/relink/re-init docking assignments (load restore, RelinkDockedShipToStation, the same-docking-size re-init of the current ship and the different-size re-init that takes a real Dock() coroutine) emit no physical station fact, while a genuine native docking request in the same session still emits exactly one DockedPhysical.";
    internal const string StaleReplayDescription = "Old-session dock/undock coroutines captured before a replacement load emit nothing into the replacement session when they are advanced afterwards, and the replacement session's own native operation still works.";

    /// <summary>
    /// MANDATORY subcase row of <see cref="RestoreDockCase"/>: the same-docking-size branch of the
    /// native ship re-init. It needs no second owned ship, because the inspected
    /// <c>ReinitPlayerSpaceshipRoutine</c> re-initializes whatever <c>GamePlayer.currentSpaceShip</c>
    /// is (its identity comparison against the live unit is a DISCARDED expression, not an early
    /// return), and vanilla itself calls <c>GameplayManager.ReinitPlayerSpaceship()</c> for the
    /// CURRENT ship from the hangar's equipment/module actions. Re-initializing the current ship
    /// therefore takes the same-size branch by construction: the docking option keeps its identity
    /// and size and the assignment is the <c>skipCoroutine: true</c> one. It is recorded as its own
    /// row so the branch is independently verifiable, and a not-run row is never accepted.
    /// </summary>
    internal const string SameSizeReinitCase = "restore-reinit-same-size";
    internal const string SameSizeReinitDescription = "Mandatory subcase of restore-relink-dock: the native re-init of the CURRENT owned ship at its dock takes the same-docking-size assignment branch (unchanged docking option, skipCoroutine) and emits no physical station fact, while the case's genuine docking request proves the observer was live in the same window.";

    /// <summary>
    /// The phase passes only when EVERY one of these case identities has exactly one PASSED row.
    /// A missing, not-run or failed required case is a phase failure: a fixture that cannot
    /// exercise one of them produces a recorded row, never an empty PASS.
    /// </summary>
    internal static readonly string[] RequiredCases = { EmptyOriginCase, RestoreDockCase, StaleReplayCase };

    /// <summary>
    /// Mandatory SUBCASE rows: they belong to a required case's contract rather than being separate
    /// coverage identities, but the phase still fails when one is missing, duplicated, not-run or
    /// failed. They keep the phase's own case identities stable while making the subcase a receipt
    /// fact an external validator can check instead of prose.
    /// </summary>
    internal static readonly string[] RequiredSubcaseRows = { SameSizeReinitCase };

    // Declared per-wait deadlines (seconds). These are the SINGLE source the driver's waits use,
    // and the phase budget is summed from the plan below, so a changed deadline moves the
    // published budget and cannot drift away from the launcher reservation silently.
    internal const float ReadinessSeconds = 90;        // shared harness Wait deadline (fixture load and service binding)
    internal const float SettleSeconds = 2;            // shared harness Settle grace period
    internal const float TravelReadySeconds = 4;       // native delayTravelAttempt window after a warp start
    internal const float InitialDockSettleSeconds = 20; // the loaded fixture's own native dock restore settling
    internal const float UndockSeconds = 60;           // native undock routine (Undocking -> Leaving)
    internal const float DepartureSeconds = 240;       // in-system leg until the verified origin unload
    internal const float ArrivalSeconds = 240;         // in-system leg until the native arrival
    internal const float BoundarySeconds = 60;         // TravelToNextWaypoint final-route boundary
    internal const float RestoreDockSeconds = 60;      // one native restore/relink/re-init assignment reaching physical Docked
    internal const float DockSeconds = 240;            // the genuine native docking request (approach + Dock())
    internal const float ReplaySeconds = 30;           // bounded advancement of ONE captured stale coroutine
    /// <summary>Bounded step budget for one replayed stale coroutine, so a vanilla infinite loop cannot hang the phase.</summary>
    internal const int ReplayStepBudget = 240;
    /// <summary>Process time the launcher reserves for this phase (mirrors $TravelResilienceBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 2400;

    // Per-case wait multiplicities, named after the driver call sites they come from, so the plan
    // below is DERIVED from the case count and the per-case call-site counts instead of being
    // hand-typed. Under-declaring an occurrence is exactly how a published budget stops covering
    // the waits the driver actually performs.
    /// <summary>Fixture load plus travel-service binding, once each per case (TravelResilienceDriver.Prepare).</summary>
    internal const int LoadWaitsPerCase = 2;
    /// <summary>The stale-replay case loads a SECOND time (its replacement session), with its own binding wait.</summary>
    internal const int ReplacementLoadWaits = 2;
    /// <summary>The restoring fixture load that follows the last case.</summary>
    internal const int RestoringLoadWaits = 1;
    /// <summary>
    /// Settle call sites in the driver: the re-route case settles after its fixture load, after the
    /// cancellation and after the route boundary; the restore case after its load, the relink, the
    /// genuine request and each of its two re-inits; the stale case after each of its two loads,
    /// the mid-iteration step, the replays and the replacement operation.
    /// </summary>
    internal const int RerouteSettles = 3;
    internal const int RestoreSettles = 5;
    internal const int StaleSettles = 5;
    /// <summary>Settle after the restoring load.</summary>
    internal const int PhaseLevelSettles = 1;
    /// <summary>The loaded fixture's dock settle: the restore case once, the stale case for both of its sessions.</summary>
    internal const int InitialDockSettles = 3;
    /// <summary>Travel-availability samples: the first route and the re-route (re-route case).</summary>
    internal const int TravelReadySamples = 2;
    /// <summary>Undock waits: re-route preparation 1, restore positive control 2, stale new operation 1.</summary>
    internal const int UndockWaits = 4;
    /// <summary>
    /// Restore/relink/re-init assignments awaited, all of them mandatory: the relink, the same-size
    /// current-ship re-init and the different-size ship re-init.
    /// </summary>
    internal const int RestoreDockWaits = 3;
    /// <summary>Captured stale coroutines advanced after the replacement load.</summary>
    internal const int ReplayedCoroutines = 2;

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    /// <summary>
    /// Worst case for the whole phase: every case loads the fixture fresh (load + service binding),
    /// the re-route case undocks, samples travel availability twice and waits for a departure, an
    /// arrival and a route boundary, the restore case settles its loaded dock, drives three restore
    /// assignments and one genuine undock/dock control, and the stale case loads a second time and
    /// advances two captured coroutines before driving one native undock. The phase then adds the
    /// restoring fixture load with its settle.
    /// </summary>
    internal static readonly PhaseWait[] PhaseWaits = BuildPhaseWaits(RequiredCases.Length);

    private static PhaseWait[] BuildPhaseWaits(int cases) => new[]
    {
        new PhaseWait("fixture-load-and-binding", ReadinessSeconds, LoadWaitsPerCase * cases + ReplacementLoadWaits + RestoringLoadWaits),
        new PhaseWait("initial-dock-settle", InitialDockSettleSeconds, InitialDockSettles),
        new PhaseWait("travel-availability", TravelReadySeconds, TravelReadySamples),
        new PhaseWait("undock", UndockSeconds, UndockWaits),
        new PhaseWait("departure", DepartureSeconds, 1),
        new PhaseWait("arrival", ArrivalSeconds, 1),
        new PhaseWait("route-boundary", BoundarySeconds, 1),
        new PhaseWait("restore-dock", RestoreDockSeconds, RestoreDockWaits),
        new PhaseWait("arrival-dock", DockSeconds, 1),
        new PhaseWait("stale-replay", ReplaySeconds, ReplayedCoroutines),
        new PhaseWait("settle", SettleSeconds, RerouteSettles + RestoreSettles + StaleSettles + PhaseLevelSettles)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    /// <summary>
    /// Read-only native state sampled at the moment a public fact was delivered. It records only
    /// what the loaded world reports about the travel/dock state; ship positions are deliberately
    /// never read, so a pointer/teleport can never become departure or arrival evidence.
    /// </summary>
    internal readonly struct NativeSnapshot
    {
        /// <summary>The native player still has a current POI (a LOADED origin), rather than the unloaded/empty origin this phase needs.</summary>
        internal bool CurrentPoiKnown { get; }
        /// <summary>A live local POI manager is registered on the live travel manager.</summary>
        internal bool LocalManagerAlive { get; }
        /// <summary>Native <c>TravelActive()</c> (route coroutine or jump running).</summary>
        internal bool TravelActive { get; }
        /// <summary>Native <c>isWarping</c>: the actual in-system transport loop is running, not departure preparation.</summary>
        internal bool Warping { get; }
        /// <summary>Native player docking state name, empty when the ship is not docking/docked.</summary>
        internal string DockingState { get; }
        internal string LocationKey { get; }
        /// <summary>
        /// The live native travel manager and player were still the exact instances this case
        /// captured at its own fixture-load boundary, in the same session.
        /// </summary>
        internal bool OwnedByCase { get; }
        internal NativeSnapshot(bool currentPoiKnown, bool localManagerAlive, bool travelActive, bool warping,
            string dockingState, string locationKey, bool ownedByCase)
        {
            CurrentPoiKnown = currentPoiKnown; LocalManagerAlive = localManagerAlive;
            TravelActive = travelActive; Warping = warping;
            DockingState = dockingState; LocationKey = locationKey; OwnedByCase = ownedByCase;
        }
        internal string ToDetail() => "currentPoi=" + (CurrentPoiKnown ? "known" : "unknown")
            + ",localManager=" + LocalManagerAlive + ",travelActive=" + TravelActive + ",warping=" + Warping
            + ",dockingState=" + (string.IsNullOrEmpty(DockingState) ? "<none>" : DockingState)
            + ",owned=" + OwnedByCase + ",location=" + LocationKey;
    }

    // --- empty-origin re-route rules -----------------------------------------------------

    /// <summary>
    /// The exact public stream of the re-route case: the first leg is requested at the loaded
    /// origin and departs from it, is cancelled AFTER that departure (so the cancellation reports an
    /// unknown location and never pretends the ship returned to its origin), and the second leg is
    /// requested while the origin is already unloaded, departs with an UNKNOWN origin and arrives at
    /// its own destination under its own operation identity with exactly one final RouteCompleted.
    /// A stale first-leg identity, a fabricated departure or a placement recovery in this window is
    /// rejected here rather than filtered away.
    /// </summary>
    internal static string? CheckReroute(IReadOnlyList<TravelTransition> slice, Guid session,
        string systemId, string? originPoiId, string firstHopId, string secondHopId)
    {
        var expected = new[]
        {
            TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Cancelled,
            TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived,
            TravelTransitionKind.RouteCompleted
        };
        var foreign = slice.FirstOrDefault(fact => fact.SessionId != session);
        if (foreign != null) return "Foreign-session travel fact in the case window: " + TravelStationReceipt.Describe(foreign);
        if (!slice.Select(fact => fact.Kind).SequenceEqual(expected))
            return "Observed [" + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "] instead of ["
                + string.Join(", ", expected) + "].";
        for (int index = 1; index < slice.Count; index++)
        {
            if (slice[index].Sequence <= slice[index - 1].Sequence) return "Public sequences are not strictly increasing.";
            if (slice[index].GameSeconds < slice[index - 1].GameSeconds) return "Public game time moved backwards.";
        }
        var wrongMode = slice.FirstOrDefault(fact => fact.Mode != TravelMode.InSystem);
        if (wrongMode != null) return "In-system re-route observed mode " + wrongMode.Mode + ": " + TravelStationReceipt.Describe(wrongMode);
        var abandoned = slice[0].OperationId;
        var rerouted = slice[3].OperationId;
        if (abandoned == null || rerouted == null) return "A travel leg was reported without an operation identity.";
        if (abandoned == rerouted) return "The re-route reused the abandoned leg's operation identity " + abandoned + ".";
        if (slice[1].OperationId != abandoned || slice[2].OperationId != abandoned)
            return "The abandoned leg's departure/cancellation do not share its operation identity: "
                + TravelStationReceipt.Describe(slice[1]) + " / " + TravelStationReceipt.Describe(slice[2]);
        if (slice[4].OperationId != rerouted || slice[5].OperationId != rerouted || slice[6].OperationId != rerouted)
            return "The re-routed leg's facts do not share one operation identity: "
                + TravelStationReceipt.Describe(slice[4]) + " / " + TravelStationReceipt.Describe(slice[5])
                + " / " + TravelStationReceipt.Describe(slice[6]);
        if (!TravelStationReceipt.Same(slice[0].RequestedDestination, systemId, firstHopId))
            return "The abandoned leg requested " + TravelStationReceipt.Location(slice[0].RequestedDestination)
                + " instead of " + TravelStationReceipt.Location(systemId, firstHopId) + ".";
        if (!TravelStationReceipt.Same(slice[1].Origin, systemId, originPoiId))
            return "The abandoned leg departed from " + TravelStationReceipt.Location(slice[1].Origin)
                + " instead of the loaded origin " + TravelStationReceipt.Location(systemId, originPoiId) + ".";
        if (slice[2].ActualLocation != null)
            return "Cancellation after departure reports " + TravelStationReceipt.Location(slice[2].ActualLocation)
                + " instead of an unknown location: " + TravelStationReceipt.Describe(slice[2]);
        if (!TravelStationReceipt.Same(slice[3].RequestedDestination, systemId, secondHopId))
            return "The re-route requested " + TravelStationReceipt.Location(slice[3].RequestedDestination)
                + " instead of " + TravelStationReceipt.Location(systemId, secondHopId) + ".";
        if (slice[4].Origin != null)
            return "The empty-origin departure reports origin " + TravelStationReceipt.Location(slice[4].Origin)
                + " instead of the unknown origin it left from: " + TravelStationReceipt.Describe(slice[4]);
        if (!TravelStationReceipt.Same(slice[5].ActualLocation, systemId, secondHopId))
            return "The re-routed leg arrived at " + TravelStationReceipt.Location(slice[5].ActualLocation)
                + " instead of " + TravelStationReceipt.Location(systemId, secondHopId) + ".";
        if (!TravelStationReceipt.Same(slice[5].RequestedDestination, systemId, secondHopId))
            return "The re-routed arrival lost its requested destination: " + TravelStationReceipt.Describe(slice[5]);
        if (slice[5].Origin != null)
            return "The re-routed arrival reports origin " + TravelStationReceipt.Location(slice[5].Origin)
                + " instead of the unknown origin it departed from: " + TravelStationReceipt.Describe(slice[5]);
        if (!TravelStationReceipt.Same(slice[6].ActualLocation, systemId, secondHopId))
            return "RouteCompleted reports " + TravelStationReceipt.Location(slice[6].ActualLocation)
                + " instead of the re-routed destination " + TravelStationReceipt.Location(systemId, secondHopId) + ".";
        return null;
    }

    /// <summary>
    /// The frame in which a native travel request was just accepted: nothing can have been
    /// transported yet, so the window must END with that <c>Requested</c> fact and must contain no
    /// transport fact at all (a <c>Departed</c>, <c>Arrived</c> or <c>RouteCompleted</c> here could
    /// only be fabricated). Null when the frame is healthy.
    ///
    /// The rule is pure so the failure message is built ONLY from facts that actually exist. The
    /// qa-83 probe defect was exactly the opposite shape: the driver passed an eagerly formatted
    /// message that described a fact which is null on the SUCCESS path, so a healthy request frame
    /// threw a NullReferenceException and failed the case. The assertion itself is unchanged and is
    /// not relaxed here.
    /// </summary>
    internal static string? CheckRequestFrame(IReadOnlyList<TravelTransition> slice, string label)
    {
        if (slice.Count == 0 || slice[slice.Count - 1].Kind != TravelTransitionKind.Requested)
            return "Expected " + label + " to end the window with Requested, observed ["
                + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "].";
        var fabricated = slice.FirstOrDefault(fact => fact.Kind == TravelTransitionKind.Departed
            || fact.Kind == TravelTransitionKind.Arrived || fact.Kind == TravelTransitionKind.RouteCompleted);
        if (fabricated != null)
            return "A transport fact was published for " + label + ": " + TravelStationReceipt.Describe(fabricated) + ".";
        return null;
    }

    /// <summary>
    /// The native state each re-route fact was sampled in. This is what separates the required
    /// empty-origin boundary from an ordinary loaded-origin hop: the abandoned leg is requested
    /// while the origin is still loaded, its departure and everything after it are observed with NO
    /// current POI, and the re-routed departure is observed while the actual native warp loop is
    /// running (not during departure preparation).
    /// </summary>
    internal static string? CheckRerouteEvidence(IReadOnlyList<TravelTransition> slice,
        IReadOnlyDictionary<long, NativeSnapshot> snapshots)
    {
        if (slice.Count != 7) return "The re-route evidence rules need the seven-fact re-route window.";
        foreach (var fact in slice)
        {
            if (!snapshots.TryGetValue(fact.Sequence, out var snapshot))
                return "No native snapshot was recorded for " + TravelStationReceipt.Describe(fact) + ".";
            if (!snapshot.OwnedByCase)
                return TravelStationReceipt.Describe(fact) + " was observed while the live native travel manager/player was not the instance this case captured ("
                    + snapshot.ToDetail() + ").";
        }
        var request = snapshots[slice[0].Sequence];
        if (!request.CurrentPoiKnown)
            return "The abandoned leg was not requested from a loaded origin (" + request.ToDetail() + ").";
        foreach (var index in new[] { 1, 2, 3, 4 })
        {
            var snapshot = snapshots[slice[index].Sequence];
            if (snapshot.CurrentPoiKnown)
                return TravelStationReceipt.Describe(slice[index]) + " was observed while the native origin was still loaded ("
                    + snapshot.ToDetail() + ").";
        }
        var cancelled = snapshots[slice[2].Sequence];
        if (cancelled.TravelActive)
            return "Native travel was still active when the abandoned leg was cancelled (" + cancelled.ToDetail() + ").";
        var departed = snapshots[slice[4].Sequence];
        if (!departed.TravelActive || !departed.Warping)
            return "The empty-origin departure was not observed at the actual native warp start ("
                + departed.ToDetail() + ").";
        var arrived = snapshots[slice[5].Sequence];
        if (!arrived.CurrentPoiKnown || !arrived.LocalManagerAlive)
            return "The re-routed arrival was not observed at a loaded native POI (" + arrived.ToDetail() + ").";
        return null;
    }

    // --- restore/relink suppression rules -------------------------------------------------

    /// <summary>
    /// A restore/relink/re-init docking assignment is NOT a physical transition: it must produce no
    /// travel fact and no physical station fact at all. Interior readiness/destruction is not a
    /// physical dock fact and is counted instead of asserted, exactly as the in-system phase does.
    /// A fact of another session is reported here too rather than filtered away.
    /// </summary>
    internal static string? CheckSuppressed(string label, IReadOnlyList<TravelTransition> travel,
        IReadOnlyList<StationTransition> station, out int interiorFacts)
    {
        interiorFacts = station.Count(fact => fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed);
        if (travel.Count > 0)
            return "The native " + label + " emitted travel facts: "
                + string.Join(", ", travel.Select(TravelStationReceipt.Describe)) + ".";
        var physical = station.Where(fact => fact.Kind != StationTransitionKind.InteriorReady
            && fact.Kind != StationTransitionKind.InteriorDestroyed).ToArray();
        if (physical.Length > 0)
            return "The native " + label + " emitted physical station facts: "
                + string.Join(", ", physical.Select(TravelStationReceipt.Describe)) + ".";
        return null;
    }

    /// <summary>
    /// The LOAD window is the one explicit boundary where facts of the replaced session are
    /// legitimate: a fixture load destroys the previous world, whose native docking options run
    /// their own destruction path, and the pilot's buffers are not cleared between cases. Those
    /// facts are counted and must all be a CONTIGUOUS prefix before the fresh session's first fact
    /// (an interleaved replaced-session fact after that boundary is a stale leak). The freshly
    /// loaded session's own restore assignment (InitializePoi(init: true)) must then emit no
    /// physical station fact at all, even though the ship really reaches Docked.
    /// </summary>
    internal static string? CheckLoadRestoreSuppression(IReadOnlyList<StationTransition> window, Guid fresh,
        out int priorSessionFacts, out int interiorFacts)
    {
        int index = 0;
        while (index < window.Count && window[index].SessionId != fresh) index++;
        priorSessionFacts = index;
        interiorFacts = 0;
        for (; index < window.Count; index++)
        {
            var fact = window[index];
            if (fact.SessionId != fresh)
                return "Replaced-session station fact interleaved after the load boundary: " + TravelStationReceipt.Describe(fact);
            if (fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed) { interiorFacts++; continue; }
            return "The native load restore emitted a physical station fact: " + TravelStationReceipt.Describe(fact);
        }
        return null;
    }

    /// <summary>
    /// The two docking-size branches of the native ship re-init are distinguished by the exact
    /// docking option the native routine used: the different-size branch finds a NEW option of the
    /// new ship's size and assigns the exterior manager's current option to it, while the same-size
    /// branch keeps the current option. The receipt therefore proves which branch actually ran
    /// instead of claiming it from the selection alone.
    /// </summary>
    internal static string? CheckReinitBranch(bool differentSize, string shipSize, string optionSizeBefore,
        string optionSizeAfter, bool optionInstanceChanged)
    {
        if (differentSize)
        {
            if (shipSize == optionSizeBefore)
                return "The different-size re-init selected a ship of the current docking size " + shipSize + ".";
            if (!optionInstanceChanged)
                return "The different-size re-init kept the same native docking option, so the different-size branch never ran.";
            if (optionSizeAfter != shipSize)
                return "The different-size re-init docked at a " + optionSizeAfter + " option instead of the new ship's " + shipSize + " size.";
            return null;
        }
        if (shipSize != optionSizeBefore)
            return "The same-size re-init selected a " + shipSize + " ship for a " + optionSizeBefore + " docking option.";
        if (optionInstanceChanged)
            return "The same-size re-init changed the native docking option, so it took the different-size branch.";
        return null;
    }

    /// <summary>
    /// The mandatory same-size subcase, driven as the native re-init of the CURRENT owned ship (the
    /// hangar's own equipment/module path). It really ran when the native routine replaced the ship
    /// UNIT while keeping the player's ship DATA, and it took the same-size branch when the docking
    /// option kept its identity and its size. Keeping the data is what proves no second owned ship
    /// and no inventory transfer were involved; replacing the unit is what proves the routine
    /// actually completed instead of returning early on the identity comparison it discards.
    /// </summary>
    internal static string? CheckCurrentShipReinit(bool unitReplaced, bool shipDataPreserved,
        bool optionInstanceChanged, string optionSizeBefore, string optionSizeAfter)
    {
        if (!shipDataPreserved)
            return "The current-ship re-init changed the player's native ship data, so it was a ship swap and not a current-ship re-init.";
        if (!unitReplaced)
            return "The current-ship re-init did not replace the native ship unit, so the native routine never completed its re-spawn.";
        if (optionInstanceChanged)
            return "The current-ship re-init changed the native docking option, so it did not take the same-size branch.";
        if (optionSizeAfter != optionSizeBefore)
            return "The current-ship re-init docked at a " + optionSizeAfter + " option instead of the unchanged " + optionSizeBefore + " one.";
        return null;
    }

    // --- stale-session replay rules -------------------------------------------------------

    /// <summary>
    /// The replay window: advancing coroutines that were captured in the REPLACED session must
    /// produce no travel fact at all, no physical station fact at all, and nothing whatsoever
    /// attributed to the old session. The replacement session's own interior lifecycle is not
    /// produced by the replay and is counted instead of asserted.
    /// </summary>
    internal static string? CheckReplaySilence(IReadOnlyList<TravelTransition> travel,
        IReadOnlyList<StationTransition> station, Guid replacement, out int interiorFacts)
    {
        interiorFacts = station.Count(fact => fact.Kind is StationTransitionKind.InteriorReady or StationTransitionKind.InteriorDestroyed);
        if (travel.Count > 0)
            return "The stale replay produced travel facts: " + string.Join(", ", travel.Select(TravelStationReceipt.Describe)) + ".";
        var physical = station.Where(fact => fact.Kind != StationTransitionKind.InteriorReady
            && fact.Kind != StationTransitionKind.InteriorDestroyed).ToArray();
        if (physical.Length > 0)
            return "The stale replay produced physical station facts: " + string.Join(", ", physical.Select(TravelStationReceipt.Describe)) + ".";
        var stale = station.FirstOrDefault(fact => fact.SessionId != replacement);
        if (stale != null)
            return "A replaced-session station fact leaked into the replacement session: " + TravelStationReceipt.Describe(stale) + ".";
        return null;
    }

    /// <summary>
    /// A replayed old coroutine that threw is NOT a pass on its own: vanilla can legitimately throw
    /// on its own destroyed objects and the API must not suppress that, but the case still needs the
    /// stale ownership, the silent observer surface and a working replacement operation. This
    /// records the outcome of one replay in the receipt with the actual exception type and the
    /// native member that threw.
    /// </summary>
    internal static string DescribeReplay(string label, int advancedSteps, bool completed, string? exceptionType, string? exceptionSite)
        => label + "={steps=" + advancedSteps.ToString(CultureInfo.InvariantCulture)
            + ",completed=" + completed
            + ",vanillaException=" + (string.IsNullOrEmpty(exceptionType) ? "none" : exceptionType + " at " + (exceptionSite ?? "<unknown>")) + "}";

    // --- shared receipt plumbing -----------------------------------------------------------

    /// <summary>
    /// Every passed case must reference real observed events of its own session, and every required
    /// case must reference at least one. Overlapping case labels in the trace are irrelevant.
    /// </summary>
    internal static string? CheckEvidence(IReadOnlyList<TravelStationReceipt.Row> rows, IReadOnlyList<string> eventRows)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in eventRows)
        {
            var columns = row.Split('\t');
            if (columns.Length != TravelStationReceipt.EventsHeader.Split('\t').Length) return "Malformed event row: " + row;
            observed.Add(columns[1] + ":" + columns[0] + ":" + columns[3]);
        }
        foreach (var row in rows.Where(candidate => candidate.Status == TravelStationReceipt.Passed))
        {
            var references = TravelStationReceipt.EvidenceReferences(row.Evidence);
            if (references.Count == 0)
            {
                if (RequiredCases.Contains(row.Case)) return "Required case has no observed public events: " + row.Case + ".";
                continue;
            }
            foreach (var reference in references)
                if (!observed.Contains(reference.Key + ":" + reference.Value + ":" + row.Session))
                    return "Case " + row.Case + " references an event that is not in the trace for its session: "
                        + reference.Key + ":" + reference.Value + ".";
        }
        return null;
    }

    /// <summary>Null when the phase is satisfied, otherwise the exact reason it is not.</summary>
    internal static string? Evaluate(IReadOnlyList<TravelStationReceipt.Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        if (rows.Count == 0) return "No case rows were recorded; empty coverage is not a pass.";
        var unknown = rows.FirstOrDefault(row => row.Status != TravelStationReceipt.Passed
            && row.Status != TravelStationReceipt.Failed && row.Status != TravelStationReceipt.NotRun);
        if (unknown != null) return "Unknown case status '" + unknown.Status + "' for " + unknown.Case + ".";
        var failed = rows.Where(row => row.Status == TravelStationReceipt.Failed).Select(row => row.Case).ToArray();
        if (failed.Length > 0) return "Failed cases: " + string.Join(", ", failed) + ".";
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            if (matches.Length == 0) return "Required case did not run: " + required + ".";
            if (matches.Length > 1) return "Required case recorded " + matches.Length + " rows: " + required + ".";
            if (matches[0].Status != TravelStationReceipt.Passed) return "Required case is " + matches[0].Status + ": " + required + ".";
        }
        foreach (var subcase in RequiredSubcaseRows)
        {
            var matches = rows.Where(row => row.Case == subcase).ToArray();
            if (matches.Length == 0) return "Required subcase did not run: " + subcase + ".";
            if (matches.Length > 1) return "Required subcase recorded " + matches.Length + " rows: " + subcase + ".";
            if (matches[0].Status != TravelStationReceipt.Passed) return "Required subcase is " + matches[0].Status + ": " + subcase + ".";
        }
        var evidence = CheckEvidence(rows, eventRows);
        if (evidence != null) return evidence;
        // A harness fault is reported last so an attributed failed row keeps the more precise reason.
        if (!string.IsNullOrEmpty(fault)) return "Pilot fault: " + TravelStationReceipt.Clean(fault);
        return null;
    }

    /// <summary>
    /// The receipt written while cases are still running: never PASS, so an external kill can only
    /// leave INCOMPLETE evidence behind.
    /// </summary>
    internal static string SummarizeIncomplete(IReadOnlyList<TravelStationReceipt.Row> rows, string activeCase)
    {
        var text = new StringBuilder();
        text.AppendLine(TravelStationReceipt.Incomplete)
            .AppendLine("phase=" + Phase)
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("required-subcases=" + string.Join(",", RequiredSubcaseRows))
            .AppendLine("activeCase=" + TravelStationReceipt.Clean(activeCase))
            .AppendLine("rows=" + rows.Count + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun))
            .AppendLine("result=pilot still running or externally terminated; this is not a pass.");
        return text.ToString();
    }

    internal static string Summarize(IReadOnlyList<TravelStationReceipt.Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        var failure = Evaluate(rows, fault, eventRows);
        var text = new StringBuilder();
        text.AppendLine(failure == null ? "PASS" : "FAIL")
            .AppendLine("phase=" + Phase)
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("required-subcases=" + string.Join(",", RequiredSubcaseRows))
            .AppendLine("rows=" + rows.Count
                + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun));
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            text.AppendLine("required-case " + required + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        foreach (var subcase in RequiredSubcaseRows)
        {
            var matches = rows.Where(row => row.Case == subcase).ToArray();
            text.AppendLine("required-subcase " + subcase + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        var optional = rows.Where(row => !RequiredCases.Contains(row.Case) && !RequiredSubcaseRows.Contains(row.Case)).ToArray();
        text.AppendLine("optional-not-run=" + string.Join(",", optional.Where(row => row.Status == TravelStationReceipt.NotRun).Select(row => row.Case)));
        text.AppendLine("fault=" + (string.IsNullOrEmpty(fault) ? "none" : TravelStationReceipt.Clean(fault)));
        text.AppendLine("result=" + (failure ?? "phase satisfied"));
        text.AppendLine("Controlled native evidence for this phase only; it does not widen " + TravelStationReceipt.Phase
            + " or " + TravelCrossSystemReceipt.Phase + ", whose own optional rows for these cells stay NOT-RUN.");
        text.AppendLine("RuntimeQualified=false; full in-game qualification remains pending.");
        return text.ToString();
    }
}
