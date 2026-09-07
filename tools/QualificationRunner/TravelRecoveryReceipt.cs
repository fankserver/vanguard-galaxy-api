using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the native travel RECOVERY/CONTINUATION pilot (phase
/// <see cref="Phase"/>), the fourth separate optional phase after travel-in-system-station-v1,
/// travel-cross-system-v1 and travel-resilience-v1. It contains no Unity, BepInEx or reflection
/// dependency, so the rules that decide PASS/FAIL are host regressions rather than prose.
///
/// It closes the two travel-matrix cells the existing phases deliberately left open:
/// <list type="bullet">
/// <item>a POSITIVELY driven <c>RecoveredPlacement</c>, emitted by the adapter's own readiness
/// observation after a native cancel that left the player at a loaded POI with no pending leg;</item>
/// <item>the post-gate in-system chain continuation, where a real multi-waypoint native route
/// crosses a jump gate and finishes at a follow-on POI, so <c>RouteCompleted</c> is proven to be
/// withheld at the gate arrival and emitted exactly once at the final native leg.</item>
/// </list>
///
/// The phase never widens the other phases: they keep their own required cases, and a pass here is
/// coverage of THIS phase's two case identities only.
/// </summary>
internal static class TravelRecoveryReceipt
{
    internal const string Phase = "travel-recovery-continuation-v1";
    internal const string RecoveredPlacementCase = "recovered-placement";
    internal const string ContinuationCase = "post-gate-continuation";
    internal const string RecoveredPlacementDescription = "A native in-system route cancelled after its verified departure, in the native window where the destination POI is already the player's current POI and its manager is initialized but SpaceshipHasArrived has not run, emits Requested->Departed->Cancelled for the leg and then exactly one operation-less RecoveredPlacement at that POI from the adapter's own readiness observation.";
    internal const string ContinuationDescription = "One native multi-waypoint route across a jump gate to a follow-on POI in the destination system emits three legs (in-system approach, gate hop, post-gate in-system leg) with distinct operation identities and exactly one RouteCompleted, withheld at the gate arrival while native waypoints remain and emitted only at the final native leg.";

    /// <summary>
    /// The phase passes only when EVERY one of these case identities has exactly one PASSED row.
    /// A missing, not-run or failed required case is a phase failure: a fixture or a native window
    /// that could not be exercised produces a recorded row, never an empty PASS.
    /// </summary>
    internal static readonly string[] RequiredCases = { RecoveredPlacementCase, ContinuationCase };

    /// <summary>This phase has no mandatory subcase rows; both cells are whole cases.</summary>
    internal static readonly string[] RequiredSubcaseRows = Array.Empty<string>();

    // Declared per-wait deadlines (seconds). These are the SINGLE source the driver's waits use, and
    // the phase budget is summed from the plan below, so a changed deadline moves the published
    // budget and cannot drift away from the launcher reservation silently.
    internal const float ReadinessSeconds = 90;        // shared harness Wait deadline (fixture load and service binding)
    internal const float SettleSeconds = 2;            // shared harness Settle grace period
    internal const float TravelReadySeconds = 4;       // native delayTravelAttempt window after a warp start
    internal const float UndockSeconds = 60;           // native undock routine before the first route
    internal const float DepartureSeconds = 240;       // in-system leg until the verified origin unload
    internal const float ArrivalSeconds = 240;         // in-system leg until the native arrival (or the cancel window)
    internal const float PlacementSeconds = 30;        // adapter readiness observation after the native cancel
    internal const float HandoffSeconds = 180;         // native gate approach until the jump iterator starts
    internal const float JumpArrivalSeconds = 240;     // jump iterator: scene load, readiness and arrival animation
    internal const float BoundarySeconds = 60;         // TravelToNextWaypoint final-route boundary

    /// <summary>
    /// Bounded attempts for the recovery case. The native window it needs (POI assigned, manager
    /// initialized, <c>SpaceshipHasArrived</c> not yet run) is at least one frame wide but can be
    /// missed when the native coroutine resumes before the pilot's own poll in that frame. Each
    /// attempt is a complete, real route to another safe POI; after the last one the case records a
    /// NOT-RUN with the per-attempt log instead of claiming coverage.
    /// </summary>
    internal const int RecoveryAttempts = 3;

    /// <summary>Process time the launcher reserves for this phase (mirrors $TravelRecoveryBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 4200;

    // Per-case wait multiplicities, named after the driver call sites they come from, so the plan
    // below is DERIVED from the case count and the per-case call-site counts instead of being
    // hand-typed. Under-declaring an occurrence is exactly how a published budget stops covering
    // the waits the driver actually performs.
    /// <summary>Fixture load plus travel-service binding, once each per case (TravelRecoveryDriver.Prepare).</summary>
    internal const int LoadWaitsPerCase = 2;
    /// <summary>The restoring fixture load that follows the last case.</summary>
    internal const int RestoringLoadWaits = 1;
    /// <summary>Undock before the first route of each case.</summary>
    internal const int UndockWaits = 2;
    /// <summary>Travel-availability samples: one per recovery attempt plus one for the continuation route.</summary>
    internal const int TravelReadySamples = RecoveryAttempts + 1;
    /// <summary>Verified origin unloads awaited: one per recovery attempt plus the continuation's three legs.</summary>
    internal const int DepartureWaits = RecoveryAttempts + 3;
    /// <summary>
    /// In-system arrival-or-cancel-window waits: one per recovery attempt (the window sampling and,
    /// when the window is missed, the completing arrival), the continuation's approach leg and its
    /// post-gate leg.
    /// </summary>
    internal const int ArrivalWaits = RecoveryAttempts + 2;
    /// <summary>Readiness placements awaited after a successful native cancel.</summary>
    internal const int PlacementWaits = 1;
    /// <summary>Route boundaries awaited: an attempt that completed instead of cancelling, plus the final route.</summary>
    internal const int BoundaryWaits = RecoveryAttempts + 1;
    /// <summary>Settle call sites: recovery preparation, per attempt and at its pass; continuation preparation, per leg and at its pass; plus the restoring load.</summary>
    internal const int RecoverySettles = RecoveryAttempts + 2;
    internal const int ContinuationSettles = 2;
    internal const int PhaseLevelSettles = 1;

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    /// <summary>
    /// Worst case for the whole phase: both cases load the fixture fresh (load + service binding)
    /// and undock; the recovery case drives up to three complete real routes; the continuation case
    /// drives one route of three native legs; and the phase then adds the restoring fixture load
    /// with its settle.
    /// </summary>
    internal static readonly PhaseWait[] PhaseWaits = BuildPhaseWaits(RequiredCases.Length);

    private static PhaseWait[] BuildPhaseWaits(int cases) => new[]
    {
        new PhaseWait("fixture-load-and-binding", ReadinessSeconds, LoadWaitsPerCase * cases + RestoringLoadWaits),
        new PhaseWait("undock", UndockSeconds, UndockWaits),
        new PhaseWait("travel-availability", TravelReadySeconds, TravelReadySamples),
        new PhaseWait("departure", DepartureSeconds, DepartureWaits),
        new PhaseWait("arrival-or-cancel-window", ArrivalSeconds, ArrivalWaits),
        new PhaseWait("readiness-placement", PlacementSeconds, PlacementWaits),
        new PhaseWait("gate-handoff", HandoffSeconds, 1),
        new PhaseWait("jump-arrival", JumpArrivalSeconds, 1),
        new PhaseWait("route-boundary", BoundarySeconds, BoundaryWaits),
        new PhaseWait("settle", SettleSeconds, RecoverySettles + ContinuationSettles + PhaseLevelSettles)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    /// <summary>
    /// Read-only native state sampled at the moment a public fact was delivered. It records only
    /// what the loaded world reports about the travel state; ship positions are deliberately never
    /// read, so a pointer/teleport can never become departure, arrival or placement evidence.
    /// </summary>
    internal readonly struct NativeSnapshot
    {
        /// <summary>The native player has a current POI (the origin or destination scene is loaded).</summary>
        internal bool CurrentPoiKnown { get; }
        /// <summary>A live local POI manager for exactly the player's current POI reports <c>initializedAndReady</c>.</summary>
        internal bool ManagerReady { get; }
        /// <summary>Native <c>TravelActive()</c> (route coroutine or jump running).</summary>
        internal bool TravelActive { get; }
        /// <summary>Native <c>usingJumpgate</c>: the jump routine owns the transition.</summary>
        internal bool UsingJumpgate { get; }
        /// <summary>Remaining native waypoints of the route.</summary>
        internal int Waypoints { get; }
        internal string LocationKey { get; }
        /// <summary>
        /// The live native travel manager and player were still the exact instances this case
        /// captured at its own fixture-load boundary, in the same session.
        /// </summary>
        internal bool OwnedByCase { get; }
        internal NativeSnapshot(bool currentPoiKnown, bool managerReady, bool travelActive, bool usingJumpgate,
            int waypoints, string locationKey, bool ownedByCase)
        {
            CurrentPoiKnown = currentPoiKnown; ManagerReady = managerReady; TravelActive = travelActive;
            UsingJumpgate = usingJumpgate; Waypoints = waypoints; LocationKey = locationKey; OwnedByCase = ownedByCase;
        }
        internal string ToDetail() => "currentPoi=" + (CurrentPoiKnown ? "known" : "unknown")
            + ",managerReady=" + ManagerReady + ",travelActive=" + TravelActive + ",usingJumpgate=" + UsingJumpgate
            + ",waypoints=" + Waypoints.ToString(CultureInfo.InvariantCulture)
            + ",owned=" + OwnedByCase + ",location=" + LocationKey;
    }

    // --- recovered placement rules ----------------------------------------------------------

    /// <summary>
    /// The exact public stream of the recovery case: one leg requested at the loaded origin, its
    /// verified departure from that origin, its cancellation AFTER the departure (which reports an
    /// unknown location and never pretends the ship returned) and then exactly one
    /// <c>RecoveredPlacement</c> at the POI the native world reports as current. The placement is a
    /// placement, not an arrival: it carries no operation identity, no origin and no requested
    /// destination, and the cancelled leg is never completed by it.
    /// </summary>
    internal static string? CheckRecoveredPlacement(IReadOnlyList<TravelTransition> slice, Guid session,
        string systemId, string? originPoiId, string targetPoiId)
    {
        var expected = new[]
        {
            TravelTransitionKind.Requested, TravelTransitionKind.Departed,
            TravelTransitionKind.Cancelled, TravelTransitionKind.RecoveredPlacement
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
        var operation = slice[0].OperationId;
        if (operation == null) return "The cancelled leg was reported without an operation identity.";
        if (slice[1].OperationId != operation || slice[2].OperationId != operation)
            return "The leg's departure/cancellation do not share its operation identity: "
                + TravelStationReceipt.Describe(slice[1]) + " / " + TravelStationReceipt.Describe(slice[2]);
        foreach (var fact in slice.Take(3))
            if (fact.Mode != TravelMode.InSystem)
                return "The cancelled in-system leg observed mode " + fact.Mode + ": " + TravelStationReceipt.Describe(fact);
        if (!TravelStationReceipt.Same(slice[0].RequestedDestination, systemId, targetPoiId))
            return "The leg requested " + TravelStationReceipt.Location(slice[0].RequestedDestination)
                + " instead of " + TravelStationReceipt.Location(systemId, targetPoiId) + ".";
        if (!TravelStationReceipt.Same(slice[1].Origin, systemId, originPoiId))
            return "The leg departed from " + TravelStationReceipt.Location(slice[1].Origin)
                + " instead of the loaded origin " + TravelStationReceipt.Location(systemId, originPoiId) + ".";
        if (slice[2].ActualLocation != null)
            return "Cancellation after departure reports " + TravelStationReceipt.Location(slice[2].ActualLocation)
                + " instead of an unknown location: " + TravelStationReceipt.Describe(slice[2]);
        var placement = slice[3];
        if (placement.OperationId != null)
            return "The recovered placement carries operation identity " + placement.OperationId
                + "; a placement completes no leg: " + TravelStationReceipt.Describe(placement);
        if (placement.Origin != null || placement.RequestedDestination != null)
            return "The recovered placement invented an origin/requested destination: " + TravelStationReceipt.Describe(placement);
        if (placement.Mode != TravelMode.Unknown)
            return "The recovered placement reports travel mode " + placement.Mode
                + " instead of Unknown: " + TravelStationReceipt.Describe(placement);
        if (!TravelStationReceipt.Same(placement.ActualLocation, systemId, targetPoiId))
            return "The recovered placement reports " + TravelStationReceipt.Location(placement.ActualLocation)
                + " instead of the native current POI " + TravelStationReceipt.Location(systemId, targetPoiId) + ".";
        if (placement.DwellSeconds != null)
            return "The recovered placement reports a dwell it cannot know: " + TravelStationReceipt.Describe(placement);
        return null;
    }

    /// <summary>
    /// The native state each recovery fact was sampled in. This is what separates a genuine
    /// readiness recovery from a fabricated arrival: the leg departs with the origin already
    /// unloaded, and the cancellation and the placement are both observed with the DESTINATION POI
    /// already current, its manager initialized and the native travel coroutine gone - the exact
    /// state the adapter's own tick observes, with no arrival callback anywhere in the window.
    /// </summary>
    internal static string? CheckRecoveryEvidence(IReadOnlyList<TravelTransition> slice,
        IReadOnlyDictionary<long, NativeSnapshot> snapshots, string targetLocationKey)
    {
        if (slice.Count != 4) return "The recovery evidence rules need the four-fact recovery window.";
        foreach (var fact in slice)
        {
            if (!snapshots.TryGetValue(fact.Sequence, out var snapshot))
                return "No native snapshot was recorded for " + TravelStationReceipt.Describe(fact) + ".";
            if (!snapshot.OwnedByCase)
                return TravelStationReceipt.Describe(fact) + " was observed while the live native travel manager/player was not the instance this case captured ("
                    + snapshot.ToDetail() + ").";
            if (snapshot.UsingJumpgate)
                return TravelStationReceipt.Describe(fact) + " was observed inside a native jump routine (" + snapshot.ToDetail() + ").";
        }
        var departure = snapshots[slice[1].Sequence];
        if (departure.CurrentPoiKnown)
            return "The departure was observed while the native origin was still loaded (" + departure.ToDetail() + ").";
        var cancellation = snapshots[slice[2].Sequence];
        if (!cancellation.CurrentPoiKnown || !cancellation.ManagerReady)
            return "The cancellation was not observed in the native window this case needs - destination POI current and its manager initialized ("
                + cancellation.ToDetail() + ").";
        if (cancellation.LocationKey != targetLocationKey)
            return "The cancellation was observed at " + cancellation.LocationKey + " instead of " + targetLocationKey + ".";
        var placement = snapshots[slice[3].Sequence];
        if (!placement.CurrentPoiKnown || !placement.ManagerReady)
            return "The recovered placement was not observed with a current POI whose manager is initialized (" + placement.ToDetail() + ").";
        if (placement.TravelActive)
            return "The recovered placement was observed while native travel was still active (" + placement.ToDetail() + ").";
        if (placement.Waypoints != 0)
            return "The recovered placement was observed while native waypoints remained (" + placement.ToDetail() + ").";
        if (placement.LocationKey != targetLocationKey)
            return "The recovered placement was observed at " + placement.LocationKey + " instead of " + targetLocationKey + ".";
        return null;
    }

    /// <summary>
    /// One recovery attempt's outcome, recorded verbatim in the receipt. A missed native window is
    /// reported with what the world actually did, never smoothed over.
    /// </summary>
    internal static string DescribeAttempt(int attempt, string targetPoiId, string outcome)
        => "attempt" + attempt.ToString(CultureInfo.InvariantCulture) + "={target=" + targetPoiId + ",outcome=" + outcome + "}";

    // --- post-gate continuation rules --------------------------------------------------------

    /// <summary>
    /// The continuation case drives ONE native route with three legs. The per-leg identity, mode,
    /// origin/requested/actual and single-RouteCompleted rules are the cross-system phase's own
    /// (<see cref="TravelCrossSystemReceipt.CheckRoutes"/>); this rule adds what makes the case a
    /// CONTINUATION: the shape must really be in-system -> gate -> in-system, the route completion
    /// must belong to the last leg and not to the gate arrival, and no completion may appear
    /// anywhere before it.
    /// </summary>
    internal static string? CheckContinuation(IReadOnlyList<TravelTransition> slice, Guid session,
        IReadOnlyList<TravelCrossSystemReceipt.ExpectedLeg> legs)
    {
        if (legs.Count != 3 || legs[0].Mode != TravelMode.InSystem || legs[1].Mode != TravelMode.JumpGate
            || legs[2].Mode != TravelMode.InSystem)
            return "The continuation case expects an in-system approach, a gate hop and a post-gate in-system leg.";
        var route = TravelCrossSystemReceipt.CheckRoutes(slice, session, new[] { legs });
        if (route != null) return route;
        var completions = slice.Where(fact => fact.Kind == TravelTransitionKind.RouteCompleted).ToArray();
        if (completions.Length != 1)
            return "The route published " + completions.Length + " RouteCompleted facts: ["
                + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "].";
        if (!ReferenceEquals(completions[0], slice[slice.Count - 1]))
            return "RouteCompleted is not the last fact of the route: [" + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "].";
        var gateArrival = slice.First(fact => fact.Mode == TravelMode.JumpGate && fact.Kind == TravelTransitionKind.Arrived);
        if (completions[0].OperationId == gateArrival.OperationId)
            return "The gate arrival's own operation completed the route; the post-gate leg never closed it: "
                + TravelStationReceipt.Describe(completions[0]);
        if (completions[0].Mode != TravelMode.InSystem)
            return "RouteCompleted reports mode " + completions[0].Mode + " instead of the post-gate in-system leg's mode.";
        return null;
    }

    /// <summary>
    /// The native state the continuation facts were sampled in. The decisive evidence is the gate
    /// arrival: the native route still had a remaining waypoint there, so the absence of a
    /// RouteCompleted at that point is the observed native truth and not a timing artefact. The
    /// post-gate leg is then observed outside the jump routine, and the completion only with the
    /// native waypoint list empty and no native travel running.
    /// </summary>
    internal static string? CheckContinuationEvidence(IReadOnlyList<TravelTransition> slice,
        IReadOnlyDictionary<long, NativeSnapshot> snapshots)
    {
        if (slice.Count != 10) return "The continuation evidence rules need the ten-fact three-leg window.";
        foreach (var fact in slice)
        {
            if (!snapshots.TryGetValue(fact.Sequence, out var snapshot))
                return "No native snapshot was recorded for " + TravelStationReceipt.Describe(fact) + ".";
            if (!snapshot.OwnedByCase)
                return TravelStationReceipt.Describe(fact) + " was observed while the live native travel manager/player was not the instance this case captured ("
                    + snapshot.ToDetail() + ").";
        }
        foreach (var fact in slice.Where(candidate => candidate.Mode == TravelMode.JumpGate
            && candidate.Kind is TravelTransitionKind.Departed or TravelTransitionKind.Arrived))
        {
            var snapshot = snapshots[fact.Sequence];
            if (!snapshot.UsingJumpgate)
                return "The gate hop's " + fact.Kind + " was not observed inside the native jump routine (" + snapshot.ToDetail() + ").";
        }
        var gateArrival = slice.First(fact => fact.Mode == TravelMode.JumpGate && fact.Kind == TravelTransitionKind.Arrived);
        var atGate = snapshots[gateArrival.Sequence];
        if (atGate.Waypoints < 1)
            return "The gate arrival was observed with no remaining native waypoint, so this route had no post-gate continuation to prove ("
                + atGate.ToDetail() + ").";
        var postGate = slice.Skip(slice.ToList().IndexOf(gateArrival) + 1).ToArray();
        foreach (var fact in postGate.Where(candidate => candidate.Kind != TravelTransitionKind.RouteCompleted))
        {
            var snapshot = snapshots[fact.Sequence];
            if (snapshot.UsingJumpgate)
                return "A post-gate in-system fact was observed inside the native jump routine: "
                    + TravelStationReceipt.Describe(fact) + " (" + snapshot.ToDetail() + ").";
        }
        var completion = snapshots[slice[slice.Count - 1].Sequence];
        if (completion.Waypoints != 0 || completion.TravelActive || completion.UsingJumpgate)
            return "RouteCompleted was observed before the native route really ended (" + completion.ToDetail() + ").";
        if (!completion.CurrentPoiKnown || !completion.ManagerReady)
            return "RouteCompleted was observed without the final POI loaded and initialized (" + completion.ToDetail() + ").";
        return null;
    }

    // --- shared receipt plumbing -----------------------------------------------------------

    /// <summary>
    /// Every passed case must reference real observed events of its own session, and every required
    /// case must reference at least one.
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
            .AppendLine("rows=" + rows.Count
                + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun));
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            text.AppendLine("required-case " + required + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        var optional = rows.Where(row => !RequiredCases.Contains(row.Case)).ToArray();
        text.AppendLine("optional-not-run=" + string.Join(",", optional.Where(row => row.Status == TravelStationReceipt.NotRun).Select(row => row.Case)));
        text.AppendLine("fault=" + (string.IsNullOrEmpty(fault) ? "none" : TravelStationReceipt.Clean(fault)));
        text.AppendLine("result=" + (failure ?? "phase satisfied"));
        text.AppendLine("Controlled native evidence for this phase only; it does not widen " + TravelStationReceipt.Phase
            + ", " + TravelCrossSystemReceipt.Phase + " or " + TravelResilienceReceipt.Phase + ".");
        text.AppendLine("RuntimeQualified=false; #12 open pending owner in-game qualification.");
        return text.ToString();
    }
}
