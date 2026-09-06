using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the native CROSS-SYSTEM travel pilot (phase
/// <see cref="Phase"/>), the separate optional phase that follows travel-in-system-station-v1.
/// It contains no Unity, BepInEx or reflection dependency, so the exact rules that decide
/// PASS/FAIL, the exact public-fact ordering and the native-iterator evidence rules are host
/// regressions rather than prose.
///
/// This phase never widens the in-system phase: travel-in-system-station-v1 keeps its own six
/// required cases and keeps recording the cross-system matrix cells as optional NOT-RUN rows. A
/// pass here is coverage of THIS phase's two cases only.
/// </summary>
internal static class TravelCrossSystemReceipt
{
    /// <summary>Honest scope of the delivered phase; the residual travel matrix stays open.</summary>
    internal const string Phase = "travel-cross-system-v1";
    internal const string JumpGateCase = "cross-system-jumpgate";
    internal const string WormholeCase = "cross-system-wormhole";
    internal const string JumpGateDescription = "A real native jump-gate hop (JumpToSystem) requested from the gate emits Requested->Departed->Arrived->RouteCompleted for the cross-system leg, observed from the owned jump iterator.";
    internal const string WormholeDescription = "A real native wormhole hop (JumpToWormhole/TravelToWormholeDestination) emits Requested->Departed->Arrived->RouteCompleted for the cross-system leg, observed from the owned jump iterator.";

    /// <summary>
    /// Optional, explicitly opt-in sandbox fixture PREPARATION row. It is recorded outside the
    /// required-case list, so it can never be coverage: creating test data is not travel evidence.
    /// </summary>
    internal const string WormholeFixtureCase = "wormhole-fixture-setup";
    internal const string WormholeFixtureDescription = "Opt-in disposable sandbox fixture preparation: a connected wormhole pair created by the game's own native factory before any route is driven. Fixture setup, never travel evidence and never coverage.";
    /// <summary>The exact inspected native factory the preparation is allowed to call, recorded in the receipt.</summary>
    internal const string WormholeFactorySignature = "Source.Simulation.World.WormholeSpawner.PlaceWormhole(Source.Galaxy.SystemMapData, System.Boolean, System.Collections.Generic.List`1<Source.Galaxy.POI.Wormhole>) : Source.Galaxy.POI.Wormhole";

    /// <summary>
    /// The phase passes only when BOTH cross-system case identities have exactly one PASSED row.
    /// A missing, not-run or failed required case is a phase failure: a fixture that cannot
    /// exercise one of the two native routines produces a recorded FAIL, never an empty PASS.
    /// </summary>
    internal static readonly string[] RequiredCases = { JumpGateCase, WormholeCase };

    // Declared per-wait deadlines (seconds). These are the SINGLE source the driver's waits use,
    // and the phase budget is summed from the plan below, so a changed deadline moves the
    // published budget and cannot drift away from the launcher reservation silently.
    internal const float ReadinessSeconds = 90;    // shared harness Wait deadline (fixture load and service binding)
    internal const float SettleSeconds = 2;        // shared harness Settle grace period
    internal const float TravelReadySeconds = 4;   // native delayTravelAttempt window after a warp start
    internal const float UndockSeconds = 60;       // native undock routine before the first route
    internal const float ApproachArrivalSeconds = 240; // in-system leg to the gate/wormhole
    internal const float HandoffSeconds = 180;     // native gate/wormhole approach until the jump iterator starts
    internal const float JumpArrivalSeconds = 240; // jump iterator: scene load, readiness and arrival animation
    internal const float BoundarySeconds = 60;     // TravelToNextWaypoint final-route boundary
    /// <summary>Bounded deadline for the opt-in native wormhole fixture creation and its verification.</summary>
    internal const float FixtureCreationSeconds = 30;
    /// <summary>Process time the launcher reserves for this phase (mirrors $TravelCrossSystemBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 2400;

    // Per-case wait multiplicities, named after the driver call sites they come from, so the plan
    // below is DERIVED from the case count instead of hand-counted. A hand-typed occurrence is
    // exactly how the route boundary was previously under-declared (once instead of twice per
    // case), which understated the published budget.
    /// <summary>Fixture load plus travel-service binding, once each per case (TravelCrossSystemDriver.Prepare).</summary>
    internal const int LoadWaitsPerCase = 2;
    /// <summary>The approach route boundary and the cross-system route boundary (DriveCrossSystemRoute).</summary>
    internal const int RouteBoundariesPerCase = 2;
    /// <summary>Settle after preparation, after the approach and after the cross hop.</summary>
    internal const int SettlesPerCase = 3;
    /// <summary>The restoring fixture load that follows the last case.</summary>
    internal const int RestoringLoadWaits = 1;
    /// <summary>Settle after the restoring load and after the opt-in fixture creation.</summary>
    internal const int PhaseLevelSettles = 2;

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    /// <summary>
    /// Worst case for the whole phase, derived from the number of required cases: each case loads
    /// the fixture fresh (load + service binding), undocks, samples travel availability, drives an
    /// in-system approach leg with its own route boundary and a cross-system hop with its own route
    /// boundary, and settles three times. The phase then adds the opt-in fixture creation and the
    /// restoring fixture load with their settles.
    /// </summary>
    internal static readonly PhaseWait[] PhaseWaits = BuildPhaseWaits(RequiredCases.Length);

    private static PhaseWait[] BuildPhaseWaits(int cases) => new[]
    {
        new PhaseWait("fixture-load-and-binding", ReadinessSeconds, LoadWaitsPerCase * cases + RestoringLoadWaits),
        new PhaseWait("undock", UndockSeconds, cases),
        new PhaseWait("travel-availability", TravelReadySeconds, cases),
        new PhaseWait("approach-arrival", ApproachArrivalSeconds, cases),
        new PhaseWait("jump-handoff", HandoffSeconds, cases),
        new PhaseWait("jump-arrival", JumpArrivalSeconds, cases),
        new PhaseWait("route-boundary", BoundarySeconds, RouteBoundariesPerCase * cases),
        new PhaseWait("wormhole-fixture-creation", FixtureCreationSeconds, 1),
        new PhaseWait("settle", SettleSeconds, SettlesPerCase * cases + PhaseLevelSettles)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    /// <summary>Native local manager type expected at the destination of each cross-system mode.</summary>
    internal static string ManagerTypeFor(TravelMode mode) => mode switch
    {
        TravelMode.JumpGate => "Behaviour.Travel.JumpGateManager",
        TravelMode.Wormhole => "Behaviour.Travel.WormholeManager",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a cross-system travel mode.")
    };

    /// <summary>One expected leg: its mode and the three locations the public facts must carry.</summary>
    internal sealed class ExpectedLeg
    {
        internal TravelMode Mode { get; }
        internal string OriginSystem { get; }
        internal string? OriginPoi { get; }
        internal string RequestedSystem { get; }
        internal string? RequestedPoi { get; }
        internal string ActualSystem { get; }
        internal string? ActualPoi { get; }
        internal ExpectedLeg(TravelMode mode, string originSystem, string? originPoi,
            string requestedSystem, string? requestedPoi, string actualSystem, string? actualPoi)
        {
            Mode = mode;
            OriginSystem = originSystem; OriginPoi = originPoi;
            RequestedSystem = requestedSystem; RequestedPoi = requestedPoi;
            ActualSystem = actualSystem; ActualPoi = actualPoi;
        }
    }

    /// <summary>
    /// Read-only native state sampled at the moment a public fact was delivered. It records only
    /// what the loaded world reports (running jump iterator, active travel, live local manager type
    /// and the current location key, which is the system alone in empty space). Ship positions are
    /// deliberately not used: a pointer/teleport position can never be arrival evidence here.
    /// </summary>
    internal readonly struct NativeSnapshot
    {
        internal bool JumpIteratorRunning { get; }
        internal bool TravelActive { get; }
        internal string ManagerType { get; }
        internal string LocationKey { get; }
        internal bool ManagerReady { get; }
        /// <summary>
        /// The live native travel manager and player were still the exact instances this case
        /// captured at its own fixture-load boundary, in the same session. A fact observed while
        /// this is false belongs to a replaced or destroyed world and is not this case's evidence.
        /// </summary>
        internal bool OwnedByCase { get; }
        internal NativeSnapshot(bool jumpIteratorRunning, bool travelActive, string managerType, string locationKey, bool managerReady, bool ownedByCase)
        {
            JumpIteratorRunning = jumpIteratorRunning; TravelActive = travelActive;
            ManagerType = managerType; LocationKey = locationKey; ManagerReady = managerReady; OwnedByCase = ownedByCase;
        }
        internal string ToDetail() => "jumpIterator=" + JumpIteratorRunning + ",travelActive=" + TravelActive
            + ",manager=" + (string.IsNullOrEmpty(ManagerType) ? "<none>" : ManagerType) + ",ready=" + ManagerReady
            + ",owned=" + OwnedByCase + ",location=" + LocationKey;
    }

    // --- opt-in fixture preparation rules -------------------------------------------------

    /// <summary>
    /// Fixture preparation must be observably INERT on the travel surface: creating map data is not
    /// movement, so a request, departure, arrival, completion or placement observed inside the
    /// creation window means the preparation did something it must never do.
    /// </summary>
    internal static string? CheckFixtureCreationWindow(IReadOnlyList<TravelTransition> facts)
        => facts.Count == 0 ? null
            : "Fixture preparation is not travel: " + facts.Count + " public travel fact(s) were observed during the creation window, starting with "
                + TravelStationReceipt.Describe(facts[0]) + ".";

    /// <summary>
    /// The created pair must be exactly two NEW native wormholes with distinct identities in two
    /// distinct systems, one of them the player's current system. Anything else is a preparation
    /// defect, not a fixture.
    /// </summary>
    internal static string? CheckFixtureCreation(int wormholesBefore, int wormholesAfter,
        string currentSystemId, string sourceSystemId, string sourceWormholeId, string destinationSystemId, string destinationWormholeId)
    {
        if (wormholesAfter != wormholesBefore + 2)
            return "Fixture preparation changed the native wormhole count from " + wormholesBefore + " to " + wormholesAfter + " instead of adding exactly two.";
        if (string.IsNullOrEmpty(sourceWormholeId) || string.IsNullOrEmpty(destinationWormholeId) || sourceWormholeId == destinationWormholeId)
            return "Fixture preparation did not produce two distinct native wormhole identities.";
        if (string.IsNullOrEmpty(sourceSystemId) || string.IsNullOrEmpty(destinationSystemId) || sourceSystemId == destinationSystemId)
            return "Fixture preparation did not place the pair in two distinct native systems.";
        if (sourceSystemId != currentSystemId)
            return "Fixture preparation placed the source wormhole in " + sourceSystemId + " instead of the player's current system " + currentSystemId + ".";
        return null;
    }

    /// <summary>The recorded preparation detail: what was created, by which inspected factory.</summary>
    internal static string DescribeFixtureCreation(int wormholesBefore, int wormholesAfter,
        string sourceSystemId, string sourceWormholeId, string destinationSystemId, string destinationWormholeId, int observedTravelFacts)
        => "selection=travel-wormhole-fixture; factory=" + WormholeFactorySignature
            + "; wormholesBefore=" + wormholesBefore + "; wormholesAfter=" + wormholesAfter
            + "; source=" + Location(sourceSystemId, sourceWormholeId)
            + "; destination=" + Location(destinationSystemId, destinationWormholeId)
            + "; travelFactsDuringCreation=" + observedTravelFacts
            + "; fixture preparation only, not travel evidence.";

    private static string Location(string systemId, string? poiId) => TravelStationReceipt.Location(systemId, poiId);

    // --- public-fact ordering rules ------------------------------------------------------

    /// <summary>
    /// The case window must contain exactly the expected native routes: each route is
    /// Requested/Departed/Arrived per leg followed by exactly one RouteCompleted, every leg carries
    /// its own operation identity, and each fact carries the expected mode and locations. A
    /// single-leg stream can never satisfy a two-route expectation, and a fact of another session
    /// is rejected here instead of being filtered out of the window.
    /// </summary>
    internal static string? CheckRoutes(IReadOnlyList<TravelTransition> slice, Guid session,
        IReadOnlyList<IReadOnlyList<ExpectedLeg>> routes)
    {
        if (routes.Count == 0 || routes.Any(route => route.Count == 0)) return "A cross-system case must expect at least one leg per route.";
        var foreign = slice.FirstOrDefault(fact => fact.SessionId != session);
        if (foreign != null) return "Foreign-session travel fact in the case window: " + TravelStationReceipt.Describe(foreign);
        var expected = new List<TravelTransitionKind>();
        foreach (var route in routes)
        {
            foreach (var _ in route)
                expected.AddRange(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived });
            expected.Add(TravelTransitionKind.RouteCompleted);
        }
        if (!slice.Select(fact => fact.Kind).SequenceEqual(expected))
            return "Observed [" + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "] instead of ["
                + string.Join(", ", expected) + "].";
        for (int index = 1; index < slice.Count; index++)
        {
            if (slice[index].Sequence <= slice[index - 1].Sequence) return "Public sequences are not strictly increasing.";
            if (slice[index].GameSeconds < slice[index - 1].GameSeconds) return "Public game time moved backwards.";
        }
        var operations = new List<Guid>();
        int position = 0;
        foreach (var route in routes)
        {
            Guid lastOperation = Guid.Empty;
            var lastLeg = route[route.Count - 1];
            foreach (var leg in route)
            {
                var requested = slice[position];
                var departed = slice[position + 1];
                var arrived = slice[position + 2];
                position += 3;
                if (requested.OperationId == null || requested.OperationId != departed.OperationId || requested.OperationId != arrived.OperationId)
                    return "Leg facts do not share one native operation identity: " + TravelStationReceipt.Describe(requested)
                        + " / " + TravelStationReceipt.Describe(departed) + " / " + TravelStationReceipt.Describe(arrived);
                if (operations.Contains(requested.OperationId.Value)) return "A leg reused an earlier operation identity: " + requested.OperationId;
                operations.Add(requested.OperationId.Value);
                lastOperation = requested.OperationId.Value;
                foreach (var fact in new[] { requested, departed, arrived })
                    if (fact.Mode != leg.Mode) return "Expected mode " + leg.Mode + " but observed " + TravelStationReceipt.Describe(fact);
                if (!TravelStationReceipt.Same(requested.RequestedDestination, leg.RequestedSystem, leg.RequestedPoi))
                    return "Leg requested " + TravelStationReceipt.Location(requested.RequestedDestination) + " instead of "
                        + TravelStationReceipt.Location(leg.RequestedSystem, leg.RequestedPoi) + ".";
                if (!TravelStationReceipt.Same(departed.Origin, leg.OriginSystem, leg.OriginPoi))
                    return "Leg departed from " + TravelStationReceipt.Location(departed.Origin) + " instead of "
                        + TravelStationReceipt.Location(leg.OriginSystem, leg.OriginPoi) + ".";
                if (!TravelStationReceipt.Same(arrived.ActualLocation, leg.ActualSystem, leg.ActualPoi))
                    return "Leg arrived at " + TravelStationReceipt.Location(arrived.ActualLocation) + " instead of "
                        + TravelStationReceipt.Location(leg.ActualSystem, leg.ActualPoi) + ".";
                if (!TravelStationReceipt.Same(arrived.RequestedDestination, leg.RequestedSystem, leg.RequestedPoi))
                    return "Arrival lost its requested destination: " + TravelStationReceipt.Describe(arrived);
                if (!TravelStationReceipt.Same(arrived.Origin, leg.OriginSystem, leg.OriginPoi))
                    return "Arrival reports origin " + TravelStationReceipt.Location(arrived.Origin) + " instead of "
                        + TravelStationReceipt.Location(leg.OriginSystem, leg.OriginPoi) + ".";
            }
            var completed = slice[position];
            position++;
            if (completed.OperationId != lastOperation)
                return "RouteCompleted belongs to " + completed.OperationId + " instead of the final leg operation " + lastOperation + ".";
            if (completed.Mode != lastLeg.Mode)
                return "RouteCompleted reports mode " + completed.Mode + " instead of the final leg mode " + lastLeg.Mode + ".";
            if (!TravelStationReceipt.Same(completed.ActualLocation, lastLeg.ActualSystem, lastLeg.ActualPoi))
                return "RouteCompleted reports " + TravelStationReceipt.Location(completed.ActualLocation) + " instead of the final leg "
                    + TravelStationReceipt.Location(lastLeg.ActualSystem, lastLeg.ActualPoi) + ".";
        }
        return null;
    }

    /// <summary>
    /// The cross-system hop must have been observed from the OWNED jump iterator, not from the
    /// in-system <c>SpaceshipHasArrived</c> path: the inspected <c>JumpToSystem</c> and
    /// <c>JumpToWormhole</c> routines set <c>usingJumpgate</c> as their first step and clear it
    /// only after the destination manager is ready, and neither routine ever calls
    /// <c>SpaceshipHasArrived</c>. So the hop's Departed/Arrived must be sampled while the native
    /// jump iterator is running, the destination manager must be the mode's own live and
    /// initialized manager, and no in-system fact of the case may be sampled inside a jump.
    /// </summary>
    internal static string? CheckJumpIteratorEvidence(IReadOnlyList<TravelTransition> slice,
        IReadOnlyDictionary<long, NativeSnapshot> snapshots, TravelMode crossMode)
    {
        if (crossMode != TravelMode.JumpGate && crossMode != TravelMode.Wormhole) return "Not a cross-system travel mode: " + crossMode + ".";
        var crossFacts = slice.Where(fact => fact.Mode == crossMode).ToArray();
        if (crossFacts.Length == 0) return "No fact of the cross-system mode " + crossMode + " was observed.";
        foreach (var fact in slice)
        {
            if (!snapshots.TryGetValue(fact.Sequence, out var snapshot))
                return "No native snapshot was recorded for " + TravelStationReceipt.Describe(fact) + ".";
            if (fact.Mode != crossMode)
            {
                if (snapshot.JumpIteratorRunning)
                    return "An in-system fact was observed inside a running jump iterator: " + TravelStationReceipt.Describe(fact);
                continue;
            }
            if (fact.Kind != TravelTransitionKind.Departed && fact.Kind != TravelTransitionKind.Arrived) continue;
            if (!snapshot.OwnedByCase)
                return "Cross-system " + fact.Kind + " was observed while the live native travel manager/player was not the instance this case captured ("
                    + snapshot.ToDetail() + ").";
            if (!snapshot.JumpIteratorRunning || !snapshot.TravelActive)
                return "Cross-system " + fact.Kind + " was not observed from the running native jump iterator ("
                    + snapshot.ToDetail() + ").";
            if (fact.Kind != TravelTransitionKind.Arrived) continue;
            var manager = ManagerTypeFor(crossMode);
            if (snapshot.ManagerType != manager || !snapshot.ManagerReady)
                return "Cross-system arrival did not happen at an initialized " + manager + " (" + snapshot.ToDetail() + ").";
            if (snapshot.LocationKey != TravelStationReceipt.Location(fact.ActualLocation))
                return "Cross-system arrival reports " + TravelStationReceipt.Location(fact.ActualLocation)
                    + " while the loaded world reports " + snapshot.LocationKey + ".";
        }
        return null;
    }

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
            + ", whose own optional cross-system rows stay NOT-RUN, and the residual travel matrix (empty-origin reroute, restore/relink dock, stale-session replay) stays open.");
        text.AppendLine("RuntimeQualified=false; #12 open pending owner in-game qualification.");
        return text.ToString();
    }
}
