using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the native FAST-LANE gate-to-gate pilot (phase
/// <see cref="Phase"/>), the fifth separate optional phase after travel-in-system-station-v1,
/// travel-cross-system-v1, travel-resilience-v1 and travel-recovery-continuation-v1. It contains no
/// Unity, BepInEx or reflection dependency, so the rules that decide PASS/FAIL are host regressions
/// rather than prose.
///
/// It exercises the native fast lane
/// (<c>travelMultiplier = 7</c>). That branch is NOT reachable by the post-gate continuation phase,
/// because the inspected <c>GamePlayer.DoFastLaneTravel()</c> is true only when the NEXT waypoint is
/// a usable <c>JumpGate</c>, while that phase deliberately ends at a safe non-gate POI. This phase
/// therefore drives a real planner route across TWO gates into a third system, so the intermediate
/// system's next waypoint is another gate and the native charge branch runs.
///
/// The phase never widens the other phases: they keep their own required cases, and a pass here is
/// coverage of THIS phase's two case identities only.
/// </summary>
internal static class TravelFastLaneReceipt
{
    internal const string Phase = "travel-fast-lane-v1";
    internal const string GateChainCase = "fast-lane-gate-chain";
    internal const string MultiplierCase = "fast-lane-multiplier-observed";
    internal const string GateChainDescription = "One native multi-system planner route across TWO jump gates to a safe follow-on POI in a third system emits five legs (in-system approach, gate hop, gate-to-gate in-system leg, second gate hop, final in-system leg) with distinct operation identities and exactly one RouteCompleted, emitted only at the final native leg with the native waypoint list empty.";
    internal const string MultiplierDescription = "The native fast-lane branch really ran: the gate-to-gate in-system leg between the two jumps is observed with the native travelMultiplier at 7 and fastLaneTravelActive true, while the approach leg before it and the final leg after it are observed at 1, so the 7x state is the transient the charge branch sets and not a permanent world flag.";

    /// <summary>
    /// The phase passes only when EVERY one of these case identities has exactly one PASSED row. A
    /// missing, not-run or failed required case is a phase failure: a fixture that cannot offer the
    /// two-gate chain, or a save whose fast lane is not unlocked, produces a recorded NOT-RUN row,
    /// never an empty PASS.
    /// </summary>
    internal static readonly string[] RequiredCases = { GateChainCase, MultiplierCase };

    /// <summary>This phase has no mandatory subcase rows; both cells are whole cases.</summary>
    internal static readonly string[] RequiredSubcaseRows = Array.Empty<string>();

    /// <summary>
    /// The single native value the fast-lane branch sets. On the inspected build
    /// <c>travelMultiplier = 7f</c> is stored in exactly ONE place in the whole assembly - inside
    /// <c>&lt;JumpToSystem&gt;d__103.MoveNext</c>, immediately after
    /// <c>TheGate.ChargeFastLaneTravelToNextGate</c> - so observing it at runtime is proof that the
    /// charge branch ran, not merely that the route contained gates or that the save carries the
    /// unlock flag. The installed-assembly test pins that uniqueness.
    /// </summary>
    internal const float FastLaneMultiplier = 7;

    /// <summary>The native resting value: the constructor, <c>CancelTravel</c> and the end of <c>StartTravel</c> all store 1.</summary>
    internal const float NormalMultiplier = 1;

    /// <summary>Native legs of the driven route, in order. The fast lane belongs to the middle one.</summary>
    internal const int RouteLegs = 5;
    /// <summary>Zero-based index of the gate-to-gate in-system leg, the only leg the charge branch precedes.</summary>
    internal const int FastLaneLegIndex = 2;
    /// <summary>Public facts of the whole route: three per leg plus the single final RouteCompleted.</summary>
    internal const int RouteFacts = RouteLegs * 3 + 1;

    // Declared per-wait deadlines (seconds). These are the SINGLE source the driver's waits use, and
    // the phase budget is summed from the plan below, so a changed deadline moves the published
    // budget and cannot drift away from the launcher reservation silently.
    internal const float ReadinessSeconds = 90;     // shared harness Wait deadline (fixture load and service binding)
    internal const float SettleSeconds = 2;         // shared harness Settle grace period
    internal const float TravelReadySeconds = 4;    // native delayTravelAttempt window after a warp start
    internal const float UndockSeconds = 60;        // native undock routine before the route
    internal const float ArrivalSeconds = 240;      // one in-system leg until its native arrival
    internal const float HandoffSeconds = 180;      // native gate approach until the jump iterator starts
    internal const float JumpArrivalSeconds = 240;  // jump iterator: scene load, readiness and arrival animation
    internal const float BoundarySeconds = 60;      // TravelToNextWaypoint final-route boundary

    /// <summary>Process time the launcher reserves for this phase (mirrors $TravelFastLaneBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 2400;

    // Per-call-site wait multiplicities, so the plan below is DERIVED from the driver's own call
    // sites instead of being hand-typed.
    /// <summary>Fixture load plus travel-service binding for the single driven route, plus the restoring load.</summary>
    internal const int LoadWaits = 3;
    /// <summary>The one undock before the route.</summary>
    internal const int UndockWaits = 1;
    /// <summary>The one travel-availability sample before the route request.</summary>
    internal const int TravelReadySamples = 1;
    /// <summary>In-system arrivals awaited: the approach leg, the gate-to-gate leg and the final leg.</summary>
    internal const int ArrivalWaits = 3;
    /// <summary>Gate handoffs and jump arrivals awaited: one pair per gate.</summary>
    internal const int GateHops = 2;
    /// <summary>The single final route boundary.</summary>
    internal const int BoundaryWaits = 1;
    /// <summary>Settle call sites: after the fixture load, after the route boundary and after the restoring load.</summary>
    internal const int Settles = 3;

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    /// <summary>
    /// Worst case for the whole phase: one fixture load with its service binding, one undock, one
    /// availability sample, one driven route of five legs (three in-system arrivals, two gate
    /// handoffs, two jump arrivals, one route boundary) and the restoring fixture load. The phase
    /// drives exactly ONE route and never retries: a failure is a failure.
    /// </summary>
    internal static readonly PhaseWait[] PhaseWaits =
    {
        new("fixture-load-and-binding", ReadinessSeconds, LoadWaits),
        new("undock", UndockSeconds, UndockWaits),
        new("travel-availability", TravelReadySeconds, TravelReadySamples),
        new("in-system-arrival", ArrivalSeconds, ArrivalWaits),
        new("gate-handoff", HandoffSeconds, GateHops),
        new("jump-arrival", JumpArrivalSeconds, GateHops),
        new("route-boundary", BoundarySeconds, BoundaryWaits),
        new("settle", SettleSeconds, Settles)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    /// <summary>
    /// Read-only native state sampled at the moment a public fact was delivered. Ship positions are
    /// deliberately never read, so a pointer or a teleport can never become travel evidence; the
    /// fast-lane proof is the native multiplier the charge branch sets, read from the live travel
    /// manager the case captured.
    /// </summary>
    internal readonly struct NativeSnapshot
    {
        internal bool CurrentPoiKnown { get; }
        internal bool ManagerReady { get; }
        internal bool TravelActive { get; }
        internal bool UsingJumpgate { get; }
        /// <summary>Native <c>TravelManager.travelMultiplier</c>: 7 only while the charge branch's transient holds.</summary>
        internal float Multiplier { get; }
        /// <summary>Native <c>TravelManager.fastLaneTravelActive</c> (<c>travelMultiplier &gt; 1</c>).</summary>
        internal bool FastLaneActive { get; }
        internal int Waypoints { get; }
        internal string LocationKey { get; }
        internal bool OwnedByCase { get; }
        internal NativeSnapshot(bool currentPoiKnown, bool managerReady, bool travelActive, bool usingJumpgate,
            float multiplier, bool fastLaneActive, int waypoints, string locationKey, bool ownedByCase)
        {
            CurrentPoiKnown = currentPoiKnown; ManagerReady = managerReady; TravelActive = travelActive;
            UsingJumpgate = usingJumpgate; Multiplier = multiplier; FastLaneActive = fastLaneActive;
            Waypoints = waypoints; LocationKey = locationKey; OwnedByCase = ownedByCase;
        }
        internal string ToDetail() => "currentPoi=" + (CurrentPoiKnown ? "known" : "unknown")
            + ",managerReady=" + ManagerReady + ",travelActive=" + TravelActive + ",usingJumpgate=" + UsingJumpgate
            + ",multiplier=" + Multiplier.ToString("0.###", CultureInfo.InvariantCulture)
            + ",fastLaneActive=" + FastLaneActive
            + ",waypoints=" + Waypoints.ToString(CultureInfo.InvariantCulture)
            + ",owned=" + OwnedByCase + ",location=" + LocationKey;
    }

    // --- fixture selection rules -------------------------------------------------------------

    /// <summary>
    /// One native gate the chain may traverse. Every field is a plain native field or method read;
    /// the lazy name generator is never touched, so selection consumes no world randomness.
    /// </summary>
    internal readonly struct GateCandidate
    {
        internal string Identity { get; }
        internal bool Usable { get; }
        internal bool Hidden { get; }
        internal bool Dynamic { get; }
        internal bool TutorialExit { get; }
        internal bool HostileOwner { get; }
        internal bool StoryMission { get; }
        internal int PersistedGuards { get; }
        internal bool LeavesCurrentSystem { get; }
        internal bool HasPairedTarget { get; }
        internal GateCandidate(string identity, bool usable, bool hidden, bool dynamic, bool tutorialExit,
            bool hostileOwner, bool storyMission, int persistedGuards, bool leavesCurrentSystem, bool hasPairedTarget)
        {
            Identity = identity; Usable = usable; Hidden = hidden; Dynamic = dynamic; TutorialExit = tutorialExit;
            HostileOwner = hostileOwner; StoryMission = storyMission; PersistedGuards = persistedGuards;
            LeavesCurrentSystem = leavesCurrentSystem; HasPairedTarget = hasPairedTarget;
        }
    }

    /// <summary>
    /// Null when the gate may be traversed by this chain, otherwise the exact reason it is refused.
    /// The rule is pure so it is a host regression rather than prose. It mirrors the shared in-system
    /// target rule for the hazards a POI can carry, and adds the gate's own requirements: the native
    /// fast lane only engages for a USABLE gate, and the one-way tutorial exit may never be driven.
    /// </summary>
    internal static string? RefuseFastLaneGate(GateCandidate candidate)
    {
        if (!candidate.Usable) return "the native gate cannot be used";
        if (candidate.Hidden) return "hidden";
        if (candidate.Dynamic) return "dynamic-event POI";
        if (candidate.TutorialExit) return "the one-way tutorial exit gate";
        if (!candidate.LeavesCurrentSystem) return "the gate does not leave its own system";
        if (!candidate.HasPairedTarget) return "the gate declares no paired target POI";
        if (candidate.HostileOwner) return "owned by a faction hostile to the player";
        if (candidate.StoryMission) return "story-mission location";
        if (candidate.PersistedGuards > 0) return "persisted guard descriptors (" + candidate.PersistedGuards + ")";
        return null;
    }

    // --- route rules -------------------------------------------------------------------------

    /// <summary>
    /// The route shape of the fast-lane chain. The per-leg identity, mode, origin/requested/actual
    /// and single-RouteCompleted rules are the cross-system phase's own
    /// (<see cref="TravelCrossSystemReceipt.CheckRoutes"/>); this rule adds what makes the case a
    /// gate-to-gate CHAIN: exactly five legs in the order in-system, gate, in-system, gate,
    /// in-system, exactly one RouteCompleted as the last fact, and that completion belonging to the
    /// final in-system leg rather than to either gate hop.
    /// </summary>
    internal static string? CheckGateChain(IReadOnlyList<TravelTransition> slice, Guid session,
        IReadOnlyList<TravelCrossSystemReceipt.ExpectedLeg> legs)
    {
        var expectedModes = new[] { TravelMode.InSystem, TravelMode.JumpGate, TravelMode.InSystem, TravelMode.JumpGate, TravelMode.InSystem };
        if (legs.Count != RouteLegs || !legs.Select(leg => leg.Mode).SequenceEqual(expectedModes))
            return "The fast-lane case expects an in-system approach, a gate hop, the gate-to-gate in-system leg, a second gate hop and the final in-system leg.";
        var route = TravelCrossSystemReceipt.CheckRoutes(slice, session, new[] { legs });
        if (route != null) return route;
        var completions = slice.Where(fact => fact.Kind == TravelTransitionKind.RouteCompleted).ToArray();
        if (completions.Length != 1)
            return "The route published " + completions.Length + " RouteCompleted facts: ["
                + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "].";
        if (!ReferenceEquals(completions[0], slice[slice.Count - 1]))
            return "RouteCompleted is not the last fact of the route: [" + string.Join(", ", slice.Select(TravelStationReceipt.Describe)) + "].";
        if (completions[0].Mode != TravelMode.InSystem)
            return "RouteCompleted reports mode " + completions[0].Mode + " instead of the final in-system leg's mode.";
        var jumpOperations = slice.Where(fact => fact.Mode == TravelMode.JumpGate).Select(fact => fact.OperationId).Distinct().ToArray();
        if (jumpOperations.Length != GateHops)
            return "The chain crossed " + jumpOperations.Length + " gate hops instead of " + GateHops + ".";
        if (jumpOperations.Contains(completions[0].OperationId))
            return "A gate hop's own operation completed the route: " + TravelStationReceipt.Describe(completions[0]);
        return null;
    }

    /// <summary>
    /// The native state the chain's facts were sampled in, and the actual fast-lane proof: every
    /// fact of the gate-to-gate leg must be observed with the native multiplier at
    /// <see cref="FastLaneMultiplier"/> and <c>fastLaneTravelActive</c> true, while the approach leg
    /// before it and the final leg (with its completion) after it are observed at
    /// <see cref="NormalMultiplier"/>. That is what separates the branch actually running from a
    /// route that merely contained gates or a save that merely carries the unlock flag: on the
    /// inspected build only the charge branch stores 7.
    /// </summary>
    internal static string? CheckFastLaneEvidence(IReadOnlyList<TravelTransition> slice,
        IReadOnlyDictionary<long, NativeSnapshot> snapshots, out float observedMultiplier)
    {
        observedMultiplier = 0;
        if (slice.Count != RouteFacts) return "The fast-lane evidence rules need the " + RouteFacts + "-fact five-leg window.";
        foreach (var fact in slice)
        {
            if (!snapshots.TryGetValue(fact.Sequence, out var snapshot))
                return "No native snapshot was recorded for " + TravelStationReceipt.Describe(fact) + ".";
            if (!snapshot.OwnedByCase)
                return TravelStationReceipt.Describe(fact) + " was observed while the live native travel manager/player was not the instance this case captured ("
                    + snapshot.ToDetail() + ").";
            if (snapshot.FastLaneActive != snapshot.Multiplier > NormalMultiplier)
                return "The native fast-lane flag and multiplier disagree at " + TravelStationReceipt.Describe(fact)
                    + " (" + snapshot.ToDetail() + ").";
        }
        foreach (var fact in slice.Where(candidate => candidate.Mode == TravelMode.JumpGate
            && candidate.Kind is TravelTransitionKind.Departed or TravelTransitionKind.Arrived))
        {
            var snapshot = snapshots[fact.Sequence];
            if (!snapshot.UsingJumpgate)
                return "A gate hop's " + fact.Kind + " was not observed inside the native jump routine (" + snapshot.ToDetail() + ").";
        }
        var legs = Legs(slice);
        var fastLane = legs[FastLaneLegIndex];
        foreach (var fact in fastLane)
        {
            var snapshot = snapshots[fact.Sequence];
            if (snapshot.UsingJumpgate)
                return "The gate-to-gate in-system leg was observed inside the native jump routine: "
                    + TravelStationReceipt.Describe(fact) + " (" + snapshot.ToDetail() + ").";
            if (snapshot.Multiplier != FastLaneMultiplier || !snapshot.FastLaneActive)
                return "The gate-to-gate leg was observed at native travel multiplier "
                    + snapshot.Multiplier.ToString("0.###", CultureInfo.InvariantCulture) + " instead of "
                    + FastLaneMultiplier.ToString("0.###", CultureInfo.InvariantCulture)
                    + ", so the native fast-lane charge branch did not run for it: "
                    + TravelStationReceipt.Describe(fact) + " (" + snapshot.ToDetail() + ").";
        }
        if (snapshots[fastLane[0].Sequence].Waypoints < 1)
            return "The gate-to-gate leg was requested with no remaining native waypoint, so it was not a chain leg ("
                + snapshots[fastLane[0].Sequence].ToDetail() + ").";
        observedMultiplier = snapshots[fastLane[0].Sequence].Multiplier;
        foreach (var index in new[] { 0, RouteLegs - 1 })
            foreach (var fact in legs[index])
            {
                var snapshot = snapshots[fact.Sequence];
                if (snapshot.Multiplier != NormalMultiplier || snapshot.FastLaneActive)
                    return "The " + (index == 0 ? "approach" : "final") + " in-system leg was observed at native travel multiplier "
                        + snapshot.Multiplier.ToString("0.###", CultureInfo.InvariantCulture)
                        + ", so the 7x state is not the transient the charge branch sets: "
                        + TravelStationReceipt.Describe(fact) + " (" + snapshot.ToDetail() + ").";
            }
        var completion = snapshots[slice[slice.Count - 1].Sequence];
        if (completion.Waypoints != 0 || completion.TravelActive || completion.UsingJumpgate)
            return "RouteCompleted was observed before the native route really ended (" + completion.ToDetail() + ").";
        if (!completion.CurrentPoiKnown || !completion.ManagerReady)
            return "RouteCompleted was observed without the final POI loaded and initialized (" + completion.ToDetail() + ").";
        if (completion.Multiplier != NormalMultiplier)
            return "The native travel multiplier was still " + completion.Multiplier.ToString("0.###", CultureInfo.InvariantCulture)
                + " at the route boundary; the fast-lane transient must be reset by then (" + completion.ToDetail() + ").";
        return null;
    }

    /// <summary>The three facts of each leg, in order. The trailing RouteCompleted is not a leg.</summary>
    internal static IReadOnlyList<TravelTransition>[] Legs(IReadOnlyList<TravelTransition> slice)
        => Enumerable.Range(0, RouteLegs).Select(index => (IReadOnlyList<TravelTransition>)slice.Skip(index * 3).Take(3).ToArray()).ToArray();

    // --- shared receipt plumbing -------------------------------------------------------------

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
        var foreign = rows.FirstOrDefault(row => !RequiredCases.Contains(row.Case));
        if (foreign != null) return "Unknown case identity in the fast-lane receipt: " + foreign.Case + ".";
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
            .AppendLine("fast-lane-multiplier=" + FastLaneMultiplier.ToString("0.###", CultureInfo.InvariantCulture))
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
            .AppendLine("fast-lane-multiplier=" + FastLaneMultiplier.ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine("rows=" + rows.Count
                + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun));
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            text.AppendLine("required-case " + required + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        text.AppendLine("optional-not-run=");
        text.AppendLine("fault=" + (string.IsNullOrEmpty(fault) ? "none" : TravelStationReceipt.Clean(fault)));
        text.AppendLine("result=" + (failure ?? "phase satisfied"));
        text.AppendLine("Controlled native evidence for this phase only; it does not widen " + TravelStationReceipt.Phase
            + ", " + TravelCrossSystemReceipt.Phase + ", " + TravelResilienceReceipt.Phase
            + " or " + TravelRecoveryReceipt.Phase + ".");
        text.AppendLine("The fixture's own fastLaneTravelUnlocked value is only READ; no save, config or native flag is written.");
        text.AppendLine("RuntimeQualified=false; full in-game qualification remains pending.");
        return text.ToString();
    }
}
