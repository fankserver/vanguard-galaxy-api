using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the ACTUAL-CONSUMER travel probe (phase <see cref="Phase"/>).
/// It contains no Unity, BepInEx or reflection dependency, so the exact rules that decide PASS/FAIL
/// — how a witnessed public arrival must appear in the installed consumer's own visit history and
/// its own v4 sidecar — are host regressions rather than prose.
///
/// This phase never widens the native travel phases. It REUSES them: the gate and wormhole
/// arrivals it compares against are driven and asserted by <see cref="TravelCrossSystemReceipt"/>'s
/// own qualified cases, and the non-travel facts come from <see cref="TravelStationReceipt"/>'s.
/// A pass here is evidence that the installed consumer reduced those already-qualified public
/// facts correctly; it is not travel coverage of its own, not automatic content persistence and
/// not owner acceptance.
/// </summary>
internal static class AnimaTravelReceipt
{
    /// <summary>Honest scope of the delivered phase; the consumer milestones stay open.</summary>
    internal const string Phase = "anima-travel-consumer-v1";

    internal const string BindingCase = "consumer-binding";
    internal const string QuietCase = "non-travel-quiet";
    internal const string GateCase = "gate-arrival-visit";
    internal const string WormholeCase = "wormhole-arrival-visit";
    internal const string PersistenceCase = "visit-persistence";
    internal const string DegradedCase = "recording-degraded";
    internal const string GateReloadSubcase = "gate-visit-reload";
    internal const string GateRollbackSubcase = "gate-visit-rollback";

    internal const string BindingDescription = "The installed consumer is subscribed to the PUBLIC travel surface of the freshly loaded session while the native-travel capability is available, and owns no direct native travel/system-entry Harmony patch.";
    internal const string QuietDescription = "Initial placement, recovered placement, requests, cancellations, in-system POI arrivals, route completion and station facts of the qualified in-system phase leave the consumer's visited-system history unchanged.";
    internal const string GateDescription = "The qualified native jump-gate arrival increments the consumer's visit count for the ACTUAL arrival system exactly once, at the public event's game time, and its v4 sidecar carries that count.";
    internal const string WormholeDescription = "The qualified native wormhole arrival increments the consumer's visit count for the ACTUAL arrival system exactly once, at the public event's game time, and its v4 sidecar carries that count.";
    internal const string PersistenceDescription = "Saving and reloading restores the recorded counts of the saved slot, the replacement session resets the consumer's leg latch, and loading the earlier fixture rolls the history back to that slot's own counts.";
    internal const string DegradedDescription = "A controlled visit-only consumer fault stops recording while the capability stays available: regional recognition is omitted from a new gather, the recorded history is preserved and still saved, and the mission provider, its save writes and both load-safety hooks remain.";
    internal const string GateReloadDescription = "Mandatory subcase of gate-arrival-visit: reloading the saved slot restores exactly the counts that were saved, from the consumer's own sidecar.";
    internal const string GateRollbackDescription = "Mandatory subcase of gate-arrival-visit: loading the earlier fixture restores that slot's own baseline counts instead of keeping the newer session's history.";

    /// <summary>
    /// The phase passes only when EVERY one of these case identities has exactly one PASSED row.
    /// A missing, not-run or failed required case is a phase failure: a consumer that recorded
    /// nothing can never report an empty PASS.
    /// </summary>
    internal static readonly string[] RequiredCases =
    {
        BindingCase, QuietCase, GateCase, WormholeCase, PersistenceCase, DegradedCase
    };

    /// <summary>
    /// Mandatory SUBCASE rows of <see cref="GateCase"/>. The gate leg must survive its own save,
    /// reload and fixture rollback BEFORE the wormhole case's fresh fixture load replaces the
    /// registry, so those two proofs cannot be deferred into the later persistence case.
    /// </summary>
    internal static readonly string[] RequiredSubcaseRows = { GateReloadSubcase, GateRollbackSubcase };

    // Declared per-wait deadlines (seconds). These are the SINGLE source the pilot's waits use, and
    // the phase budget is summed from the plan below, so a changed deadline moves the published
    // budget and cannot drift away from the launcher reservation silently.
    internal const float ReadinessSeconds = 90;   // shared harness Wait deadline (the probe's own slot loads)
    internal const float SettleSeconds = 2;       // shared harness Settle grace period
    /// <summary>Bounded wait for the consumer's own callback to have been delivered and the public dispatch to be idle.</summary>
    internal const float QuiescenceSeconds = 20;
    /// <summary>Bounded wait for the freshly loaded session's own public placement fact.</summary>
    internal const float PlacementSeconds = 30;
    /// <summary>Process time the launcher reserves for this phase (mirrors $AnimaTravelBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 900;

    // Per-case wait multiplicities, named after the pilot call sites they come from, so the plan
    // below is DERIVED from the call-site counts instead of being hand-typed.
    /// <summary>Slot loads the probe itself performs: reload + fixture rollback for each of the two positive cases.</summary>
    internal const int ProbeLoads = 4;
    /// <summary>Placement waits: the binding case plus one after every probe load.</summary>
    internal const int PlacementWaits = ProbeLoads + 1;
    /// <summary>Quiescence samples: in-system delta, both arrival deltas, both reloads, the rollback and the degradation case.</summary>
    internal const int QuiescenceSamples = 7;
    /// <summary>Settle after every probe load plus one before the degradation case.</summary>
    internal const int Settles = ProbeLoads + 1;

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    /// <summary>
    /// Worst case for this phase ALONE. The native travel phases it reuses keep their own separate
    /// reservations; this budget covers only the consumer probe's own loads, placement waits,
    /// quiescence samples and settles.
    /// </summary>
    internal static readonly PhaseWait[] PhaseWaits =
    {
        new("probe-slot-load", ReadinessSeconds, ProbeLoads),
        new("session-placement", PlacementSeconds, PlacementWaits),
        new("callback-quiescence", QuiescenceSeconds, QuiescenceSamples),
        new("settle", SettleSeconds, Settles)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    // --- consumer history rules ------------------------------------------------------------

    /// <summary>
    /// One row of the installed consumer's own visited-system history, read either from its live
    /// registry or from its persisted v4 sidecar. The pilot reads these values from the consumer's
    /// own members; every rule below is a pure comparison over them.
    /// </summary>
    internal readonly struct VisitRecord
    {
        internal string SystemId { get; }
        internal string Name { get; }
        internal int Visits { get; }
        internal double FirstSeconds { get; }
        internal double LastSeconds { get; }
        internal VisitRecord(string systemId, string name, int visits, double firstSeconds, double lastSeconds)
        {
            SystemId = systemId; Name = name; Visits = visits; FirstSeconds = firstSeconds; LastSeconds = lastSeconds;
        }
        internal string Describe() => SystemId + "{name=" + Name + ",visits=" + Visits.ToString(CultureInfo.InvariantCulture)
            + ",first=" + FirstSeconds.ToString("F3", CultureInfo.InvariantCulture)
            + ",last=" + LastSeconds.ToString("F3", CultureInfo.InvariantCulture) + "}";
    }

    internal static string Describe(IReadOnlyList<VisitRecord> history)
        => history.Count == 0 ? "<empty>"
            : string.Join(",", history.OrderBy(record => record.SystemId, StringComparer.Ordinal).Select(record => record.Describe()));

    private static Dictionary<string, VisitRecord> Index(IReadOnlyList<VisitRecord> history)
    {
        var result = new Dictionary<string, VisitRecord>(StringComparer.Ordinal);
        foreach (var record in history) result[record.SystemId] = record;
        return result;
    }

    /// <summary>Two histories are identical, row for row. Used for save/reload, sidecar/registry and rollback proofs.</summary>
    internal static string? CheckSameHistory(IReadOnlyList<VisitRecord> expected, IReadOnlyList<VisitRecord> actual, string label)
    {
        var left = Index(expected);
        var right = Index(actual);
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other)) return label + ": system " + pair.Key + " is missing (" + Describe(actual) + ").";
            if (other.Visits != pair.Value.Visits || other.Name != pair.Value.Name
                || !Equal(other.FirstSeconds, pair.Value.FirstSeconds) || !Equal(other.LastSeconds, pair.Value.LastSeconds))
                return label + ": " + pair.Value.Describe() + " became " + other.Describe() + ".";
        }
        var added = right.Keys.Where(key => !left.ContainsKey(key)).ToArray();
        if (added.Length > 0) return label + ": unexpected system(s) " + string.Join(", ", added) + " (" + Describe(actual) + ").";
        return null;
    }

    /// <summary>
    /// Nothing in the window may have grown the history: no new system and no changed count. Used
    /// for the non-travel facts and for the quiet period after a session replacement, where a late
    /// fact of the replaced session must never be adopted.
    /// </summary>
    internal static string? CheckNoVisitGrowth(IReadOnlyList<VisitRecord> before, IReadOnlyList<VisitRecord> after, string label)
        => CheckSameHistory(before, after, label);

    /// <summary>
    /// Recorded history is never truncated: every baseline system survives with at least its
    /// baseline count and its first-visit time.
    /// </summary>
    internal static string? CheckHistoryPreserved(IReadOnlyList<VisitRecord> baseline, IReadOnlyList<VisitRecord> actual, string label)
    {
        var current = Index(actual);
        foreach (var record in baseline)
        {
            if (!current.TryGetValue(record.SystemId, out var now)) return label + ": baseline system " + record.SystemId + " was truncated.";
            if (now.Visits < record.Visits) return label + ": " + record.Describe() + " lost visits (" + now.Describe() + ").";
            if (!Equal(now.FirstSeconds, record.FirstSeconds)) return label + ": " + record.Describe() + " changed its first-visit time (" + now.Describe() + ").";
        }
        return null;
    }

    /// <summary>The witnessed public arrival the consumer had to reduce, reduced to its comparable fields.</summary>
    internal readonly struct ArrivalEvidence
    {
        internal string ActualSystemId { get; }
        internal string? ActualSystemName { get; }
        internal string? RequestedSystemId { get; }
        internal double GameSeconds { get; }
        internal TravelMode Mode { get; }
        internal ArrivalEvidence(string actualSystemId, string? actualSystemName, string? requestedSystemId, double gameSeconds, TravelMode mode)
        {
            ActualSystemId = actualSystemId; ActualSystemName = actualSystemName;
            RequestedSystemId = requestedSystemId; GameSeconds = gameSeconds; Mode = mode;
        }
        internal static ArrivalEvidence From(TravelTransition arrival)
            => new(arrival.ActualLocation!.SystemId, arrival.ActualLocation.SystemName,
                arrival.RequestedDestination?.SystemId, arrival.GameSeconds, arrival.Mode);
    }

    /// <summary>
    /// Exactly one witnessed cross-system arrival became exactly one visit, at the ACTUAL arrival
    /// system rather than the requested nominal one, with the public event's own game time and the
    /// consumer's documented nullable-label preservation. Every other system is untouched.
    /// </summary>
    internal static string? CheckVisitIncrement(IReadOnlyList<VisitRecord> before, IReadOnlyList<VisitRecord> after, ArrivalEvidence arrival)
    {
        if (arrival.Mode is not (TravelMode.JumpGate or TravelMode.Wormhole))
            return "A visit may only be claimed for a cross-system arrival, not mode " + arrival.Mode + ".";
        var left = Index(before);
        var right = Index(after);
        if (!right.TryGetValue(arrival.ActualSystemId, out var recorded))
            return "The arrival system " + arrival.ActualSystemId + " is absent from the consumer history (" + Describe(after) + ").";
        left.TryGetValue(arrival.ActualSystemId, out var previous);
        bool first = !left.ContainsKey(arrival.ActualSystemId);
        int expected = first ? 1 : previous.Visits + 1;
        if (recorded.Visits != expected)
            return "The arrival at " + arrival.ActualSystemId + " recorded " + recorded.Visits + " visit(s) instead of " + expected + ".";
        if (!Equal(recorded.LastSeconds, arrival.GameSeconds))
            return "The recorded last-visit time " + recorded.LastSeconds.ToString("F3", CultureInfo.InvariantCulture)
                + " is not the public event's game time " + arrival.GameSeconds.ToString("F3", CultureInfo.InvariantCulture) + ".";
        if (first && !Equal(recorded.FirstSeconds, arrival.GameSeconds))
            return "A first visit recorded first-visit time " + recorded.FirstSeconds.ToString("F3", CultureInfo.InvariantCulture)
                + " instead of the public event's " + arrival.GameSeconds.ToString("F3", CultureInfo.InvariantCulture) + ".";
        if (!first && !Equal(recorded.FirstSeconds, previous.FirstSeconds))
            return "A repeat visit rewrote the first-visit time of " + arrival.ActualSystemId + ".";
        // Documented nullable-label preservation: an unavailable public label keeps whatever the
        // registry already held (empty for a first visit) instead of forcing a lazy vanilla lookup.
        var expectedName = arrival.ActualSystemName ?? (first ? string.Empty : previous.Name);
        if (recorded.Name != expectedName)
            return "The stored label is '" + recorded.Name + "' instead of the preserved/observed '" + expectedName + "'.";
        // The requested nominal destination of a redirected hop must never be counted.
        if (arrival.RequestedSystemId != null && arrival.RequestedSystemId != arrival.ActualSystemId)
        {
            left.TryGetValue(arrival.RequestedSystemId, out var nominalBefore);
            bool hadNominal = left.ContainsKey(arrival.RequestedSystemId);
            right.TryGetValue(arrival.RequestedSystemId, out var nominalAfter);
            bool hasNominal = right.ContainsKey(arrival.RequestedSystemId);
            if (hasNominal != hadNominal || (hadNominal && nominalAfter.Visits != nominalBefore.Visits))
                return "The requested nominal system " + arrival.RequestedSystemId + " was counted instead of (or as well as) the actual arrival.";
        }
        foreach (var pair in right)
        {
            if (pair.Key == arrival.ActualSystemId) continue;
            if (!left.TryGetValue(pair.Key, out var untouched)) return "An unrelated system " + pair.Key + " appeared in the consumer history.";
            if (untouched.Visits != pair.Value.Visits || !Equal(untouched.LastSeconds, pair.Value.LastSeconds))
                return "An unrelated system changed: " + untouched.Describe() + " became " + pair.Value.Describe() + ".";
        }
        var lost = left.Keys.Where(key => !right.ContainsKey(key)).ToArray();
        if (lost.Length > 0) return "The arrival dropped recorded system(s) " + string.Join(", ", lost) + ".";
        return null;
    }

    /// <summary>
    /// The window the consumer saw for the qualified in-system phase. The negative claim is only
    /// meaningful when those facts really happened, so the window must carry the placement, the
    /// request, the cancellation, the in-system arrival and the route completion the phase drove,
    /// must belong to the phase's own session and must contain NO cross-system arrival.
    /// </summary>
    internal static string? CheckQuietWindow(IReadOnlyList<TravelTransition> window, Guid session)
    {
        if (window.Count == 0) return "The in-system phase produced no public facts for the consumer to ignore.";
        var foreign = window.FirstOrDefault(fact => fact.SessionId != session);
        if (foreign != null) return "Foreign-session public fact inside the consumer's quiet window: " + TravelStationReceipt.Describe(foreign);
        var crossSystem = window.FirstOrDefault(fact => fact.Kind == TravelTransitionKind.Arrived
            && fact.Mode is TravelMode.JumpGate or TravelMode.Wormhole);
        if (crossSystem != null) return "The in-system window carries a cross-system arrival: " + TravelStationReceipt.Describe(crossSystem);
        foreach (var required in new[]
        {
            TravelTransitionKind.InitialPlacement, TravelTransitionKind.Requested,
            TravelTransitionKind.Cancelled, TravelTransitionKind.RouteCompleted
        })
            if (window.All(fact => fact.Kind != required))
                return "The in-system window never carried a " + required + " fact, so the no-visit claim would be vacuous.";
        if (!window.Any(fact => fact.Kind == TravelTransitionKind.Arrived && fact.Mode == TravelMode.InSystem))
            return "The in-system window never carried an in-system arrival, so the no-visit claim would be vacuous.";
        return null;
    }

    /// <summary>
    /// The window the consumer saw for one qualified cross-system case: exactly one arrival of the
    /// case's own mode, belonging to the case's session. Facts of another session are reported, not
    /// filtered away, so a stale-session leak fails the case instead of being silently dropped.
    /// </summary>
    internal static string? CheckArrivalWindow(IReadOnlyList<TravelTransition> window, Guid session, TravelMode mode, out TravelTransition? arrival)
    {
        arrival = null;
        var foreign = window.FirstOrDefault(fact => fact.SessionId != session);
        if (foreign != null) return "Foreign-session public fact inside the consumer window: " + TravelStationReceipt.Describe(foreign);
        var arrivals = window.Where(fact => fact.Kind == TravelTransitionKind.Arrived && fact.Mode == mode).ToArray();
        if (arrivals.Length == 0)
            return "No witnessed " + mode + " arrival reached the consumer window; the qualified native case produced no callback the consumer could reduce.";
        if (arrivals.Length != 1) return "The consumer window carries " + arrivals.Length + " " + mode + " arrivals; this case owns exactly one.";
        if (arrivals[0].ActualLocation == null) return "The witnessed arrival carries no actual location.";
        if (arrivals[0].OperationId == null) return "The witnessed arrival carries no operation identity.";
        arrival = arrivals[0];
        return null;
    }

    /// <summary>
    /// A reload really replaced the session and reset the consumer's per-session leg latch, so the
    /// restored counts belong to the loaded slot and no leg of the saved session can count again.
    /// </summary>
    internal static string? CheckSessionReplacement(Guid savedSession, Guid restoredSession, Guid? observerSession, int countedLegs)
    {
        if (restoredSession == savedSession) return "The reload did not replace the session (" + savedSession + ").";
        if (countedLegs != 0) return "The consumer kept " + countedLegs + " counted leg(s) across the session replacement.";
        if (observerSession != null && observerSession != restoredSession)
            return "The consumer is still latched to session " + observerSession + " instead of the restored " + restoredSession + ".";
        return null;
    }

    /// <summary>
    /// The sidecar the probe reads must be the paired companion of the save the probe itself just
    /// wrote, inside the redirected sandbox save directory. An unknown or foreign path is never
    /// accepted as consumer persistence evidence.
    /// </summary>
    internal static string? CheckSidecarPath(string sidecarPath, string saveRoot, string saveName)
    {
        if (string.IsNullOrEmpty(sidecarPath)) return "No consumer sidecar path was resolved.";
        var expected = Path.Combine(saveRoot, saveName + ".save.vganima.json");
        if (!string.Equals(Path.GetFullPath(sidecarPath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            return "The consumer sidecar " + sidecarPath + " is not the paired companion of the written save " + expected + ".";
        return null;
    }

    /// <summary>The persisted schema must still be the current v4 contract carrying the visited map.</summary>
    internal static string? CheckSidecarVersion(int version, int expected)
        => version == expected ? null : "The consumer sidecar declares schema version " + version + " instead of " + expected + ".";

    /// <summary>
    /// Null when a live Harmony patch owned by the consumer is acceptable, otherwise the exact
    /// reason it is refused. The consumer must observe travel through the PUBLIC API only: a direct
    /// native travel, jump, system-entry or docking hook is a fallback the contract forbids, and it
    /// is refused here whether it was installed at startup or after a degradation.
    /// </summary>
    internal static string? RefuseConsumerTravelPatch(string declaringType, string method)
    {
        var type = declaringType ?? string.Empty;
        var name = method ?? string.Empty;
        foreach (var owner in new[]
        {
            "Behaviour.Managers.TravelManager", "Behaviour.Travel.", "Behaviour.Spacestation.",
            "SpacestationExteriorManager", "Behaviour.Unit.SpaceShip"
        })
            if (type.StartsWith(owner, StringComparison.Ordinal)) return "native travel/station owner " + type + "." + name;
        foreach (var member in new[]
        {
            "JumpToSystem", "JumpToWormhole", "JumpToPOIFrom", "JumpToWormholeFrom", "SetSystemEntry", "EnterSystem",
            "SpaceshipHasArrived", "SetRouteToPOI", "TryInitiateTravel", "TravelToNextWaypoint", "Dock", "Undock"
        })
            if (name == member) return "native travel member " + type + "." + name;
        return null;
    }

    private static bool Equal(double left, double right) => left.Equals(right);

    // --- shared receipt plumbing -----------------------------------------------------------

    /// <summary>
    /// Every passed case must reference real observed events of its own session, and every required
    /// case must reference at least one. The references may legitimately name the SAME public
    /// sequences the reused travel phases recorded: they are facts, not exclusive case labels.
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
                if (RequiredCases.Contains(row.Case) || RequiredSubcaseRows.Contains(row.Case))
                    return "Required case has no observed public events: " + row.Case + ".";
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
        text.AppendLine("Actual-consumer evidence over the public travel surface only. The gate and wormhole arrivals it reduces are qualified by "
            + TravelCrossSystemReceipt.Phase + " and the non-travel facts by " + TravelStationReceipt.Phase
            + "; this phase adds no travel coverage of its own and fulfils no content-persistence milestone.");
        text.AppendLine("RuntimeQualified=false; #12 open pending owner in-game qualification and the remaining consumer reconciliation.");
        return text.ToString();
    }
}
