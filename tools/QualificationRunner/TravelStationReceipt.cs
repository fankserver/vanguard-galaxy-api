using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the native travel/station pilot. It contains no Unity,
/// BepInEx or reflection dependency, so the exact rules that decide PASS/FAIL and the exact
/// public-fact ordering the pilot asserts are host-testable regressions rather than prose.
/// The pilot only supplies case-local slices of observed public facts; every rule here is an
/// ordering/identity rule over those facts and never a claim about native behaviour.
/// </summary>
internal static class TravelStationReceipt
{
    /// <summary>Honest scope of the delivered phase; the full travel matrix stays open.</summary>
    internal const string Phase = "travel-in-system-station-v1";
    internal const string Passed = "passed";
    internal const string Failed = "failed";
    internal const string NotRun = "not-run";
    internal const string ReceiptHeader = "case\tdescription\tstatus\tnativeIdentity\tsession\toperation\tevidence\tdetail";
    internal const string EventsHeader = "apiSequence\tsurface\tcase\tsession\toperation\tkind\tmode\torigin\trequested\tactual\tgameSeconds\tdwellSeconds";
    /// <summary>Written while the pilot is still running; never a PASS claim.</summary>
    internal const string Incomplete = "INCOMPLETE";
    /// <summary>The label used for observed events while no case is driving.</summary>
    internal const string NoActiveCase = "no-active-case";

    // Declared per-wait deadlines (seconds). The phase budget is derived from them so the launcher
    // reservation and the pilot's own bounded waits cannot drift apart silently.
    internal const float ReadinessSeconds = 90;
    internal const float SettleSeconds = 2;
    internal const float TravelReadySeconds = 4;
    internal const float InitialDockSettleSeconds = 20;
    internal const float UndockLeavingSeconds = 60;
    internal const float UndockRoutineSeconds = 60;
    internal const float ArrivalSeconds = 240;
    internal const float BoundarySeconds = 60;
    internal const float DockSeconds = 240;
    /// <summary>
    /// Worst case for the whole phase: readiness, the undock case, three real arrivals (one
    /// single-hop route plus the two chained hops), two route boundaries, the arrival dock, the
    /// three travel-availability samples and a settle after every stage.
    /// </summary>
    internal const float PhaseBudgetSeconds = ReadinessSeconds
        + InitialDockSettleSeconds + UndockLeavingSeconds + UndockRoutineSeconds
        + 3 * ArrivalSeconds + 2 * BoundarySeconds + DockSeconds
        + 3 * TravelReadySeconds + 12 * SettleSeconds;

    /// <summary>
    /// The phase passes only when every one of these case identities has exactly one PASSED row.
    /// A missing, not-run or failed required case is a phase failure: empty coverage can never pass.
    /// </summary>
    internal static readonly string[] RequiredCases =
    {
        "initial-placement", "station-undock", "in-system-route", "early-cancel", "chained-route", "station-dock"
    };

    internal sealed class Row
    {
        internal string Case { get; }
        internal string Description { get; }
        internal string Status { get; }
        internal string NativeIdentity { get; }
        internal string Session { get; }
        internal string Operation { get; }
        /// <summary>
        /// Explicit references to the public events this case validated ("travel:5,6,7;station:3").
        /// Cases legitimately overlap in the native timeline (the return hop's dock is observed
        /// while the chained route is still driving), so evidence is referenced by surface and API
        /// sequence instead of by a mutually exclusive case label.
        /// </summary>
        internal string Evidence { get; }
        internal string Detail { get; }
        internal Row(string caseId, string description, string status, string nativeIdentity, string session, string operation, string evidence, string detail)
        {
            Case = caseId; Description = description; Status = status;
            NativeIdentity = nativeIdentity; Session = session; Operation = operation; Evidence = evidence; Detail = detail;
        }
        internal string ToTsv() => string.Join("\t", Clean(Case), Clean(Description), Clean(Status),
            Clean(NativeIdentity), Clean(Session), Clean(Operation), Clean(Evidence), Clean(Detail));
    }

    internal static string Evidence(IEnumerable<TravelTransition>? travel, IEnumerable<StationTransition>? station)
    {
        var parts = new List<string>();
        var travelRefs = travel == null ? Array.Empty<string>() : travel.Select(f => f.Sequence.ToString(CultureInfo.InvariantCulture)).ToArray();
        var stationRefs = station == null ? Array.Empty<string>() : station.Select(f => f.Sequence.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (travelRefs.Length > 0) parts.Add("travel:" + string.Join(",", travelRefs));
        if (stationRefs.Length > 0) parts.Add("station:" + string.Join(",", stationRefs));
        return string.Join(";", parts);
    }

    /// <summary>Parsed (surface, apiSequence) references of an evidence field.</summary>
    internal static List<KeyValuePair<string, string>> EvidenceReferences(string evidence)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var group in (evidence ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = group.Split(':');
            if (parts.Length != 2) continue;
            foreach (var sequence in parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                result.Add(new KeyValuePair<string, string>(parts[0].Trim(), sequence.Trim()));
        }
        return result;
    }

    /// <summary>
    /// Every passed case must reference real observed events of its own session, and every required
    /// case must reference at least one. Overlapping case labels in the trace are irrelevant.
    /// </summary>
    internal static string? CheckEvidence(IReadOnlyList<Row> rows, IReadOnlyList<string> eventRows)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in eventRows)
        {
            var columns = row.Split('\t');
            if (columns.Length != EventsHeader.Split('\t').Length) return "Malformed event row: " + row;
            observed.Add(columns[1] + ":" + columns[0] + ":" + columns[3]);
        }
        foreach (var row in rows.Where(r => r.Status == Passed))
        {
            var references = EvidenceReferences(row.Evidence);
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

    internal static string Clean(string? value)
        => (value ?? string.Empty).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Null when the phase is satisfied, otherwise the exact reason it is not.</summary>
    internal static string? Evaluate(IReadOnlyList<Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        if (rows.Count == 0) return "No case rows were recorded; empty coverage is not a pass.";
        var unknown = rows.FirstOrDefault(r => r.Status != Passed && r.Status != Failed && r.Status != NotRun);
        if (unknown != null) return "Unknown case status '" + unknown.Status + "' for " + unknown.Case + ".";
        var failed = rows.Where(r => r.Status == Failed).Select(r => r.Case).ToArray();
        if (failed.Length > 0) return "Failed cases: " + string.Join(", ", failed) + ".";
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(r => r.Case == required).ToArray();
            if (matches.Length == 0) return "Required case did not run: " + required + ".";
            if (matches.Length > 1) return "Required case recorded " + matches.Length + " rows: " + required + ".";
            if (matches[0].Status != Passed) return "Required case is " + matches[0].Status + ": " + required + ".";
        }
        var evidence = CheckEvidence(rows, eventRows);
        if (evidence != null) return evidence;
        // A harness fault is reported last so an attributed failed row keeps the more precise reason.
        if (!string.IsNullOrEmpty(fault)) return "Pilot fault: " + Clean(fault);
        return null;
    }

    /// <summary>
    /// The receipt written while cases are still running: never PASS, so an external kill can only
    /// leave INCOMPLETE evidence behind.
    /// </summary>
    internal static string SummarizeIncomplete(IReadOnlyList<Row> rows, string activeCase)
    {
        var text = new StringBuilder();
        text.AppendLine(Incomplete)
            .AppendLine("phase=" + Phase)
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("activeCase=" + Clean(activeCase))
            .AppendLine("rows=" + rows.Count + " passed=" + rows.Count(r => r.Status == Passed)
                + " failed=" + rows.Count(r => r.Status == Failed) + " notRun=" + rows.Count(r => r.Status == NotRun))
            .AppendLine("result=pilot still running or externally terminated; this is not a pass.");
        return text.ToString();
    }

    internal static string Summarize(IReadOnlyList<Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        var failure = Evaluate(rows, fault, eventRows);
        var text = new StringBuilder();
        text.AppendLine(failure == null ? "PASS" : "FAIL")
            .AppendLine("phase=" + Phase)
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("rows=" + rows.Count
                + " passed=" + rows.Count(r => r.Status == Passed)
                + " failed=" + rows.Count(r => r.Status == Failed)
                + " notRun=" + rows.Count(r => r.Status == NotRun));
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(r => r.Case == required).ToArray();
            text.AppendLine("required-case " + required + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        var optional = rows.Where(r => !RequiredCases.Contains(r.Case)).ToArray();
        text.AppendLine("optional-not-run=" + string.Join(",", optional.Where(r => r.Status == NotRun).Select(r => r.Case)));
        text.AppendLine("fault=" + (string.IsNullOrEmpty(fault) ? "none" : Clean(fault)));
        text.AppendLine("result=" + (failure ?? "phase satisfied"));
        text.AppendLine("Controlled native evidence for this phase only; the residual travel matrix (cross-system jump/wormhole, empty-origin reroute, reinit suppression, stale-session replay) stays open.");
        text.AppendLine("RuntimeQualified=false; #12 open pending owner in-game qualification.");
        return text.ToString();
    }

    // --- public-fact ordering rules ------------------------------------------------------

    /// <summary>
    /// A case window is every fact observed after the case started driving, INCLUDING facts of other
    /// sessions: a stale-session fact leaking into a case must be rejected by the validators, never
    /// filtered away silently.
    /// </summary>
    internal static List<T> Window<T>(IReadOnlyList<T> observed, int offset)
    {
        var result = new List<T>();
        for (int index = Math.Max(0, offset); index < observed.Count; index++) result.Add(observed[index]);
        return result;
    }

    /// <summary>
    /// The load window is the one explicit boundary where older-session facts are legitimate: the
    /// pilot clears its buffers BEFORE the load, so the previous session can still emit until the
    /// fresh session starts. Returns the index of the fresh session's first fact; any pre-boundary
    /// fact of the fresh session, or any foreign fact after the boundary, is rejected by the caller.
    /// </summary>
    internal static string? CheckLoadBoundary(IReadOnlyList<TravelTransition> observed, Guid session, out int freshIndex, out int priorSessionFacts)
    {
        freshIndex = 0; priorSessionFacts = 0;
        while (freshIndex < observed.Count && observed[freshIndex].SessionId != session) freshIndex++;
        priorSessionFacts = freshIndex;
        if (freshIndex == observed.Count) return "No fact of the freshly loaded session was observed.";
        for (int index = 0; index < freshIndex; index++)
            if (observed[index].SessionId == session) return "Fresh-session fact ordered before the load boundary.";
        return null;
    }

    /// <summary>
    /// The fresh session's own placement. The pilot clears its buffers BEFORE the fixture load, so
    /// this window must start at the session's first public sequence and contain nothing else: a
    /// window that starts later (cleared after load) or carries an arrival is a defect, not a pass.
    /// </summary>
    internal static string? CheckInitialPlacement(IReadOnlyList<TravelTransition> facts, Guid session, string systemId, string? poiId)
    {
        if (facts.Count == 0) return "No travel facts observed for the fresh session; the initial placement was missed or cleared away.";
        var foreign = facts.FirstOrDefault(f => f.SessionId != session);
        if (foreign != null) return "Foreign-session fact inside the fresh-session window: " + Describe(foreign);
        if (facts[0].Kind != TravelTransitionKind.InitialPlacement) return "First observed fact is " + facts[0].Kind + ", not InitialPlacement: " + Describe(facts[0]);
        var extra = facts.FirstOrDefault(f => f.Kind != TravelTransitionKind.InitialPlacement);
        if (extra != null) return "Load window carries a non-placement fact: " + Describe(extra);
        if (facts.Count != 1) return "Initial placement was emitted " + facts.Count + " times.";
        var placement = facts[0];
        if (placement.Sequence != 1) return "Initial placement is not the first public fact of the fresh session (sequence " + placement.Sequence + ").";
        if (placement.OperationId != null) return "Initial placement carries a travel operation identity.";
        if (!Same(placement.ActualLocation, systemId, poiId))
            return "Initial placement " + Location(placement.ActualLocation) + " is not the actual native location " + Location(systemId, poiId) + ".";
        return null;
    }

    /// <summary>
    /// One route of <paramref name="hopPoiIds"/> real hops: exactly Requested/Departed/Arrived per
    /// hop, then exactly one RouteCompleted at the final boundary. A single-leg fact stream can
    /// never satisfy a multi-hop expectation, and no earlier case's facts may appear.
    /// </summary>
    internal static string? CheckRoute(IReadOnlyList<TravelTransition> slice, Guid session,
        string systemId, string? originPoiId, IReadOnlyList<string> hopPoiIds)
    {
        if (hopPoiIds.Count == 0) return "A route case must expect at least one hop.";
        var expected = new List<TravelTransitionKind>();
        foreach (var _ in hopPoiIds)
            expected.AddRange(new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed, TravelTransitionKind.Arrived });
        expected.Add(TravelTransitionKind.RouteCompleted);
        var ordering = CheckSequence(slice, session, expected);
        if (ordering != null) return ordering;
        var operations = new List<Guid>();
        for (int hop = 0; hop < hopPoiIds.Count; hop++)
        {
            var requested = slice[hop * 3];
            var departed = slice[hop * 3 + 1];
            var arrived = slice[hop * 3 + 2];
            if (requested.OperationId == null || requested.OperationId != departed.OperationId || requested.OperationId != arrived.OperationId)
                return "Hop " + hop + " facts do not share one native operation identity: " + Describe(requested) + " / " + Describe(departed) + " / " + Describe(arrived);
            if (operations.Contains(requested.OperationId.Value)) return "Hop " + hop + " reused an earlier operation identity.";
            operations.Add(requested.OperationId.Value);
            if (!Same(requested.RequestedDestination, systemId, hopPoiIds[hop]))
                return "Hop " + hop + " requested " + Location(requested.RequestedDestination) + " instead of " + Location(systemId, hopPoiIds[hop]) + ".";
            var expectedOrigin = hop == 0 ? originPoiId : hopPoiIds[hop - 1];
            if (!Same(departed.Origin, systemId, expectedOrigin))
                return "Hop " + hop + " departed from " + Location(departed.Origin) + " instead of " + Location(systemId, expectedOrigin) + ".";
            if (!Same(arrived.ActualLocation, systemId, hopPoiIds[hop]))
                return "Hop " + hop + " arrived at " + Location(arrived.ActualLocation) + " instead of " + Location(systemId, hopPoiIds[hop]) + ".";
            if (!Same(arrived.RequestedDestination, systemId, hopPoiIds[hop]))
                return "Hop " + hop + " arrival lost its requested destination: " + Describe(arrived);
            if (!Same(arrived.Origin, systemId, expectedOrigin))
                return "Hop " + hop + " arrival reports origin " + Location(arrived.Origin) + " instead of " + Location(systemId, expectedOrigin) + ".";
        }
        var completed = slice[slice.Count - 1];
        if (completed.OperationId != operations[operations.Count - 1])
            return "RouteCompleted belongs to " + completed.OperationId + " instead of the final hop operation " + operations[operations.Count - 1] + ".";
        if (!Same(completed.ActualLocation, systemId, hopPoiIds[hopPoiIds.Count - 1]))
            return "RouteCompleted reports " + Location(completed.ActualLocation) + " instead of the final hop " + Location(systemId, hopPoiIds[hopPoiIds.Count - 1]) + ".";
        var wrongMode = slice.FirstOrDefault(f => f.Mode != TravelMode.InSystem);
        if (wrongMode != null) return "In-system phase observed mode " + wrongMode.Mode + ": " + Describe(wrongMode);
        return null;
    }

    /// <summary>Cancel before any departure: the origin stays current and no departure is invented.</summary>
    internal static string? CheckEarlyCancel(IReadOnlyList<TravelTransition> slice, Guid session,
        string systemId, string? originPoiId, string requestedPoiId)
    {
        var ordering = CheckSequence(slice, session,
            new[] { TravelTransitionKind.Requested, TravelTransitionKind.Cancelled });
        if (ordering != null) return ordering;
        var requested = slice[0];
        var cancelled = slice[1];
        if (requested.OperationId == null || requested.OperationId != cancelled.OperationId)
            return "Cancellation does not carry the requested operation identity: " + Describe(requested) + " / " + Describe(cancelled);
        if (!Same(requested.RequestedDestination, systemId, requestedPoiId))
            return "Cancelled route requested " + Location(requested.RequestedDestination) + " instead of " + Location(systemId, requestedPoiId) + ".";
        if (!Same(cancelled.ActualLocation, systemId, originPoiId))
            return "Cancellation reports " + Location(cancelled.ActualLocation) + " instead of the unchanged origin " + Location(systemId, originPoiId) + ".";
        return null;
    }

    /// <summary>
    /// Physical dock/undock facts only. Interior readiness/destruction is deliberately excluded:
    /// native onDocked opens the interior before the physical dock completes, so no ordering
    /// between InteriorReady and DockedPhysical is asserted.
    /// </summary>
    internal static string? CheckStationPhase(IReadOnlyList<StationTransition> slice, Guid session,
        string systemId, string? stationPoiId, IReadOnlyList<StationTransitionKind> expected)
    {
        var physical = slice.Where(s => s.Kind != StationTransitionKind.InteriorReady
            && s.Kind != StationTransitionKind.InteriorDestroyed).ToArray();
        var foreign = physical.FirstOrDefault(s => s.SessionId != session);
        if (foreign != null) return "Foreign-session station fact: " + Describe(foreign);
        if (!physical.Select(s => s.Kind).SequenceEqual(expected))
            return "Station facts were [" + string.Join(", ", physical.Select(Describe)) + "] instead of ["
                + string.Join(", ", expected) + "].";
        for (int index = 1; index < physical.Length; index++)
        {
            if (physical[index].Sequence <= physical[index - 1].Sequence) return "Station sequences are not strictly increasing.";
            if (physical[index].GameSeconds < physical[index - 1].GameSeconds) return "Station game time moved backwards.";
        }
        var wrongStation = physical.FirstOrDefault(s => !Same(s.Station, systemId, stationPoiId));
        if (wrongStation != null) return "Station fact reports " + Location(wrongStation.Station) + " instead of " + Location(systemId, stationPoiId) + ".";
        return null;
    }

    private static string? CheckSequence(IReadOnlyList<TravelTransition> slice, Guid session, IReadOnlyList<TravelTransitionKind> expected)
    {
        var foreign = slice.FirstOrDefault(f => f.SessionId != session);
        if (foreign != null) return "Foreign-session travel fact in the case window: " + Describe(foreign);
        if (!slice.Select(f => f.Kind).SequenceEqual(expected))
            return "Observed [" + string.Join(", ", slice.Select(Describe)) + "] instead of [" + string.Join(", ", expected) + "].";
        for (int index = 1; index < slice.Count; index++)
        {
            if (slice[index].Sequence <= slice[index - 1].Sequence) return "Public sequences are not strictly increasing.";
            if (slice[index].GameSeconds < slice[index - 1].GameSeconds) return "Public game time moved backwards.";
        }
        return null;
    }

    internal static bool Same(TravelLocation? location, string systemId, string? poiId)
        => location != null && location.SystemId == systemId && location.PoiId == poiId;

    internal static string Location(TravelLocation? location)
        => location == null ? "<none>" : Location(location.SystemId, location.PoiId);

    internal static string Location(string systemId, string? poiId) => systemId + ":" + (poiId ?? "<empty space>");

    internal static string Describe(TravelTransition fact)
        => fact.Kind + "(op=" + (fact.OperationId?.ToString() ?? "none") + ",seq=" + fact.Sequence.ToString(CultureInfo.InvariantCulture)
            + ",origin=" + Location(fact.Origin) + ",requested=" + Location(fact.RequestedDestination)
            + ",actual=" + Location(fact.ActualLocation) + ",mode=" + fact.Mode + ")";

    internal static string Describe(StationTransition fact)
        => fact.Kind + "(seq=" + fact.Sequence.ToString(CultureInfo.InvariantCulture) + ",station=" + Location(fact.Station) + ")";

    internal static string TravelEventRow(string caseId, TravelTransition fact)
        => string.Join("\t", fact.Sequence.ToString(CultureInfo.InvariantCulture), "travel", Clean(caseId), fact.SessionId,
            fact.OperationId?.ToString() ?? "", fact.Kind, fact.Mode, Location(fact.Origin), Location(fact.RequestedDestination),
            Location(fact.ActualLocation), fact.GameSeconds.ToString("F3", CultureInfo.InvariantCulture),
            fact.DwellSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "");

    internal static string StationEventRow(string caseId, StationTransition fact)
        => string.Join("\t", fact.Sequence.ToString(CultureInfo.InvariantCulture), "station", Clean(caseId), fact.SessionId,
            "", fact.Kind, "", "", "", Location(fact.Station), fact.GameSeconds.ToString("F3", CultureInfo.InvariantCulture), "");
}
