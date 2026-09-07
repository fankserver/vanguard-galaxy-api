using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the archived-journal comparison rules and for the native dwell assertions.
/// Each test reproduces a concrete way the phase could look green while proving nothing: a legacy
/// log read only in part, a fabricated compatible pair, a "lead" claimed without a distinct time,
/// a gap that did not actually reproduce, a legacy-blind claim over an empty window, or a dwell
/// that is merely non-negative.
/// </summary>
public sealed class TravelJournalReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string System1 = "system-1";
    private const string System2 = "system-2";
    private static long _sequence;

    private static TravelLocation At(string system, string? poi) => new(system, poi, "Named", poi);

    private static TravelTransition Fact(TravelTransitionKind kind, TravelMode mode = TravelMode.InSystem,
        string? poi = null, double seconds = 10, Guid? session = null, double? dwell = null, string system = System1)
        => new(session ?? Session,
            kind is TravelTransitionKind.InitialPlacement or TravelTransitionKind.RecoveredPlacement ? null : Guid.NewGuid(),
            ++_sequence, kind, mode, null,
            kind == TravelTransitionKind.Requested ? At(system, poi) : null,
            kind is TravelTransitionKind.Requested ? null : At(system, poi), seconds, dwell);

    private static TravelStationReceipt.Row Row(string id, string status, string evidence = "travel:1", string detail = "legacy:0")
        => new(id, id + " description", status, "identity", Session.ToString(), "", evidence, detail);

    private static List<string> Trace()
        => new() { string.Join("\t", 1, "travel", "case", Session, "", "Arrived", "InSystem", "", "", "", "1.000", "") };

    private static List<TravelStationReceipt.Row> PassingRows()
        => TravelJournalReceipt.RequiredCases.Select(id => Row(id, TravelStationReceipt.Passed)).ToList();

    // --- the archived sidecar, read as a file ------------------------------------------------

    private const string Sample = @"{
  ""version"": 2,
  ""events"": [
    {
      ""$kind"": ""PoiArrival"",
      ""gameSeconds"": 120.5,
      ""systemGuid"": ""system-1"",
      ""systemName"": ""Some \""Name\"""",
      ""poiGuid"": ""poi-a"",
      ""poiName"": ""Ore Field"",
      ""poiKind"": ""Mining"",
      ""dwellInPriorPoiSeconds"": 0.0
    },
    {
      ""$kind"": ""JumpgateTransit"",
      ""gameSeconds"": 300.25,
      ""systemGuid"": ""system-2"",
      ""systemName"": ""Elsewhere"",
      ""systemFactionId"": null,
      ""fromGuid"": ""system-1"",
      ""fromName"": ""Home"",
      ""dwellInPriorSystemSeconds"": 179.75
    },
    {
      ""$kind"": ""StationDock"",
      ""gameSeconds"": 60.0,
      ""systemGuid"": ""system-1"",
      ""systemName"": ""Home"",
      ""stationGuid"": ""station-a"",
      ""stationName"": ""Dock""
    }
  ]
}";

    [Fact]
    public void TheArchivedSidecarIsReadWithItsOwnWireShapeAndIndices()
    {
        var sidecar = TravelJournalReceipt.ReadSidecar(Sample);
        Assert.Equal(TravelJournalReceipt.LegacySchemaVersion, sidecar.Version);
        Assert.Equal(3, sidecar.Events.Count);
        Assert.Equal(new[] { 0, 1, 2 }, sidecar.Events.Select(row => row.Index).ToArray());
        Assert.Equal(TravelJournalReceipt.PoiArrivalKind, sidecar.Events[0].Kind);
        Assert.Equal("poi-a", sidecar.Events[0].PoiGuid);
        Assert.Equal(120.5, sidecar.Events[0].GameSeconds);
        Assert.Equal(System2, sidecar.Events[1].SystemGuid);
        Assert.Equal("station-a", sidecar.Events[2].StationGuid);
        // Names exist on the wire but are deliberately not projected: the archived plugin reads the
        // game's lazy name generator, so its names are neither ground truth nor comparable.
        Assert.DoesNotContain("Name", sidecar.Events[0].Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableOrOversizedLegacyDocumentIsRefusedInsteadOfPartiallyRead()
    {
        Assert.Throws<FormatException>(() => TravelJournalReceipt.ReadSidecar("{ \"version\": 2, \"events\": ["));
        Assert.Throws<FormatException>(() => TravelJournalReceipt.ReadSidecar("{ \"events\": [] }"));
        Assert.Throws<FormatException>(() => TravelJournalReceipt.ReadSidecar("{ \"version\": 2 }"));
        Assert.Throws<FormatException>(() => TravelJournalReceipt.ReadSidecar(Sample + "trailing"));
        // A row without the discriminator cannot be classified, so it is refused, never guessed.
        Assert.Throws<FormatException>(() => TravelJournalReceipt.ReadSidecar(
            "{ \"version\": 2, \"events\": [ { \"gameSeconds\": 1.0 } ] }"));
        var oversized = new string('x', TravelJournalReceipt.MaxSidecarBytes + 1);
        var refusal = Assert.Throws<FormatException>(() => TravelJournalReceipt.ReadSidecar(oversized));
        Assert.Contains("refusing a partial read", refusal.Message);
    }

    [Fact]
    public void AShrunkenOrForeignVersionLogInvalidatesTheWindowsAppendOffsets()
    {
        var sidecar = TravelJournalReceipt.ReadSidecar(Sample);
        Assert.Null(TravelJournalReceipt.CheckAppendBaseline(sidecar, 1));
        Assert.Contains("shrank", TravelJournalReceipt.CheckAppendBaseline(sidecar, 4));
        var v1 = TravelJournalReceipt.ReadSidecar("{ \"version\": 1, \"events\": [] }");
        Assert.Contains("schema version 1", TravelJournalReceipt.CheckAppendBaseline(v1, 0));
        // Offsets are per-window against the captured baseline; no global monotonic counter is assumed.
        Assert.Equal(new[] { 1, 2 }, TravelJournalReceipt.Appended(sidecar, 1).Select(row => row.Index).ToArray());
    }

    // --- comparison rules --------------------------------------------------------------------

    private static TravelJournalReceipt.LegacyEvent Legacy(int index, string kind, double seconds,
        string system = System1, string poi = "", string station = "")
        => new(index, kind, seconds, system, poi, station);

    [Fact]
    public void ACompatiblePairNeedsMatchingGuidsCountAndOrder()
    {
        var legacy = new[]
        {
            Legacy(0, TravelJournalReceipt.PoiArrivalKind, 10, poi: "poi-a"),
            Legacy(1, TravelJournalReceipt.PoiArrivalKind, 20, poi: "poi-b"),
        };
        Assert.Null(TravelJournalReceipt.CheckArrivalPair(new[] { "poi-a", "poi-b" }, legacy));
        Assert.Contains("Arrival 0 differs", TravelJournalReceipt.CheckArrivalPair(new[] { "poi-b", "poi-a" }, legacy));
        Assert.Contains("appended 2 PoiArrival row(s) for 1", TravelJournalReceipt.CheckArrivalPair(new[] { "poi-a" }, legacy));
        Assert.Contains("vacuous", TravelJournalReceipt.CheckArrivalPair(Array.Empty<string>(), legacy));
    }

    [Fact]
    public void ThePrefixLeadNeedsAnInFlightWindowAndADistinctEarlierLegacyTime()
    {
        var inFlight = new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 300.0, system: System2) };
        Assert.Null(TravelJournalReceipt.CheckPrefixLead(inFlight, System2, true, true, false, 420.0));
        // A lead claimed while the arrival was already observed is not a lead at all.
        Assert.Contains("already observed", TravelJournalReceipt.CheckPrefixLead(inFlight, System2, true, true, true, 420.0));
        // No source-only claim: without the two public facts, or without a later arrival time, it fails.
        Assert.Contains("Requested and Departed", TravelJournalReceipt.CheckPrefixLead(inFlight, System2, false, true, false, 420.0));
        Assert.Contains("never observed", TravelJournalReceipt.CheckPrefixLead(inFlight, System2, true, true, false, null));
        Assert.Contains("does not precede", TravelJournalReceipt.CheckPrefixLead(inFlight, System2, true, true, false, 299.0));
        Assert.Contains("recorded no jump-gate transit",
            TravelJournalReceipt.CheckPrefixLead(Array.Empty<TravelJournalReceipt.LegacyEvent>(), System2, true, true, false, 420.0));
    }

    [Fact]
    public void TheWormholeGapMustActuallyReproduceOverAnObservedArrival()
    {
        var unrelated = new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 10, system: System1) };
        Assert.Null(TravelJournalReceipt.CheckWormholeGap(unrelated, System2, apiWormholeArrivalObserved: true));
        Assert.Contains("vacuous", TravelJournalReceipt.CheckWormholeGap(unrelated, System2, apiWormholeArrivalObserved: false));
        var reproduced = new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 10, system: System2) };
        Assert.Contains("does not reproduce", TravelJournalReceipt.CheckWormholeGap(reproduced, System2, true));
    }

    [Fact]
    public void TheStationRowIsEitherAnEarlierInteriorToggleOrNoLegacyRowAtAll()
    {
        var interior = new[] { Legacy(0, TravelJournalReceipt.StationDockKind, 90.0, station: "station-a") };
        Assert.Null(TravelJournalReceipt.CheckStationDock(interior, "station-a", 95.0, out var outcome));
        Assert.Equal(TravelJournalReceipt.StationOutcome.InteriorPrecedesPhysical, outcome);
        Assert.Null(TravelJournalReceipt.CheckStationDock(Array.Empty<TravelJournalReceipt.LegacyEvent>(), "station-a", 95.0, out outcome));
        Assert.Equal(TravelJournalReceipt.StationOutcome.NoInteriorNoLegacyRow, outcome);
        Assert.Contains("contradicts the documented interior-before-physical shape",
            TravelJournalReceipt.CheckStationDock(new[] { Legacy(0, TravelJournalReceipt.StationDockKind, 99.0, station: "station-a") },
                "station-a", 95.0, out outcome));
        Assert.Contains("2 dock rows", TravelJournalReceipt.CheckStationDock(new[]
        {
            Legacy(0, TravelJournalReceipt.StationDockKind, 90.0, station: "station-a"),
            Legacy(1, TravelJournalReceipt.StationDockKind, 91.0, station: "station-a"),
        }, "station-a", 95.0, out outcome));
    }

    [Fact]
    public void TheLegacyBlindRowIsNeverAnEquivalenceAndNeverVacuous()
    {
        Assert.Null(TravelJournalReceipt.CheckLegacyBlind(Array.Empty<TravelJournalReceipt.LegacyEvent>(), apiCancellationObserved: true));
        Assert.Contains("vacuous", TravelJournalReceipt.CheckLegacyBlind(Array.Empty<TravelJournalReceipt.LegacyEvent>(), false));
        Assert.Contains("appended 1 row(s) for a cancelled route",
            TravelJournalReceipt.CheckLegacyBlind(new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 5) }, true));
    }

    [Fact]
    public void OnlyTheArchivedJournalsOwnFilePatternsAreAcceptedBesideTheSandboxSaves()
    {
        Assert.Null(TravelJournalReceipt.CheckLegacyFile("fixture-a.save.vgtraveljournal.json"));
        Assert.Null(TravelJournalReceipt.CheckLegacyFile("fixture-a.save.vgtraveljournal.corrupt.20260101120000.json"));
        Assert.Null(TravelJournalReceipt.CheckLegacyFile("fixture-a.save.vgtraveljournal.json.tmp"));
        Assert.Contains("Unexpected file", TravelJournalReceipt.CheckLegacyFile("fixture-a.save.vgtraveljournal.txt"));
        Assert.Contains("traversing", TravelJournalReceipt.CheckLegacyFile("../outside.save.vgtraveljournal.json"));
        Assert.Contains("empty journal file path", TravelJournalReceipt.CheckLegacyFile(""));
    }

    // --- native dwell rules ------------------------------------------------------------------

    [Fact]
    public void AnAnchoredPositiveDwellIsRequiredAndMustEqualItsOwnGameTimeDifference()
    {
        var facts = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: 100),
            Fact(TravelTransitionKind.Requested, TravelMode.InSystem, "poi-b", seconds: 140),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 150, dwell: 50),
            Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, "poi-b", seconds: 200),
        };
        Assert.Null(TravelJournalReceipt.CheckDwellAnchoring(facts, out int pairs, out double largest));
        Assert.Equal(1, pairs);
        Assert.Equal(50, largest);
    }

    [Fact]
    public void AWrongMissingNegativeOrZeroOnlyDwellIsRefused()
    {
        List<TravelTransition> With(double? dwell, double departAt = 150) => new()
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: 100),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: departAt, dwell: dwell),
        };
        Assert.Contains("instead of its own anchor difference",
            TravelJournalReceipt.CheckDwellAnchoring(With(49), out _, out _));
        Assert.Contains("reported no dwell", TravelJournalReceipt.CheckDwellAnchoring(With(null), out _, out _));
        // A negative dwell can never even reach a subscriber: the public contract refuses to
        // construct one, so the rule's negative branch is defence in depth behind that guarantee.
        Assert.Throws<ArgumentOutOfRangeException>(() => Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 150, dwell: -1));
        // Zero-only anchored dwell is not evidence: a strictly positive one is required.
        Assert.Contains("strictly positive", TravelJournalReceipt.CheckDwellAnchoring(With(0, departAt: 100), out _, out _));
        Assert.Contains("vacuous", TravelJournalReceipt.CheckDwellAnchoring(
            new List<TravelTransition> { Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a") }, out _, out _));
    }

    [Fact]
    public void ADwellIsNeverComputedAcrossASessionReplacementOrAfterItsAnchorIsSpent()
    {
        var replacement = Guid.NewGuid();
        var facts = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: 100),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 150, dwell: 50),
            // A departure in a REPLACEMENT session has no anchor of its own: a dwell there would be
            // a cross-session duration.
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-c", seconds: 400, dwell: 300, session: replacement),
        };
        Assert.Contains("no anchor in its own session", TravelJournalReceipt.CheckDwellAnchoring(facts, out _, out _));
        // The anchor is spent by its own departure; a second departure without a new arrival reports none.
        var spent = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: 100),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 150, dwell: 50),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-b", seconds: 160, dwell: null),
        };
        Assert.Null(TravelJournalReceipt.CheckDwellAnchoring(spent, out int pairs, out _));
        Assert.Equal(1, pairs);
    }

    [Fact]
    public void AClockRollbackCannotProduceANegativeOrInventedDwell()
    {
        // The anchor is later than the departure: the difference is negative, which the API reports
        // as unknown. A reported dwell there is refused rather than clamped.
        var facts = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, "poi-a", seconds: 500),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 100, dwell: 0),
        };
        Assert.Contains("instead of its own anchor difference", TravelJournalReceipt.CheckDwellAnchoring(facts, out _, out _));
    }

    // --- phase evaluation --------------------------------------------------------------------

    [Fact]
    public void EveryRequiredCaseMustPassWithResolvableEvidenceAndNamedLegacyRows()
    {
        Assert.Null(TravelJournalReceipt.Evaluate(PassingRows(), null, Trace()));
        Assert.StartsWith("PASS", TravelJournalReceipt.Summarize(PassingRows(), null, Trace()));
        // A compared case that never names the legacy rows it used cannot pass.
        var unnamed = PassingRows();
        int index = unnamed.FindIndex(row => row.Case == TravelJournalReceipt.InSystemCase);
        unnamed[index] = Row(TravelJournalReceipt.InSystemCase, TravelStationReceipt.Passed, detail: "comparison=compatible");
        Assert.Contains("without naming the legacy rows", TravelJournalReceipt.Evaluate(unnamed, null, Trace()));
    }

    [Fact]
    public void EmptyFailedMissingOrDuplicatedCoverageIsNeverAPass()
    {
        Assert.Contains("empty coverage", TravelJournalReceipt.Evaluate(Array.Empty<TravelStationReceipt.Row>(), null, Trace()));
        var failed = PassingRows();
        failed[0] = Row(failed[0].Case, TravelStationReceipt.Failed);
        Assert.Contains("Failed cases", TravelJournalReceipt.Evaluate(failed, null, Trace()));
        foreach (var required in new[] { TravelJournalReceipt.InSystemCase, TravelJournalReceipt.PrefixLeadCase,
            TravelJournalReceipt.WormholeGapCase, TravelJournalReceipt.DwellCase })
        {
            var missing = PassingRows().Where(row => row.Case != required).ToList();
            Assert.Contains("Required case did not run: " + required, TravelJournalReceipt.Evaluate(missing, null, Trace()));
        }
        var duplicated = PassingRows();
        duplicated.Add(Row(TravelJournalReceipt.WormholeGapCase, TravelStationReceipt.Passed));
        Assert.Contains("recorded 2 rows", TravelJournalReceipt.Evaluate(duplicated, null, Trace()));
        var notRun = PassingRows();
        notRun[1] = Row(notRun[1].Case, TravelStationReceipt.NotRun);
        Assert.Contains("is not-run", TravelJournalReceipt.Evaluate(notRun, null, Trace()));
        Assert.Contains("Pilot fault", TravelJournalReceipt.Evaluate(PassingRows(), "probe exploded", Trace()));
        var checkpoint = TravelJournalReceipt.SummarizeIncomplete(PassingRows(), TravelJournalReceipt.PrefixLeadCase);
        Assert.StartsWith(TravelStationReceipt.Incomplete, checkpoint);
        Assert.DoesNotContain("PASS", checkpoint);
    }

    [Fact]
    public void EvidenceMustResolveToRealEventsOfTheRowSession()
    {
        var foreign = new List<string> { string.Join("\t", 1, "travel", "case", Guid.NewGuid(), "", "Arrived", "InSystem", "", "", "", "1.000", "") };
        Assert.Contains("not in the trace for its session", TravelJournalReceipt.Evaluate(PassingRows(), null, foreign));
        var withoutEvidence = PassingRows();
        withoutEvidence[0] = Row(withoutEvidence[0].Case, TravelStationReceipt.Passed, evidence: "");
        Assert.Contains("no observed public events", TravelJournalReceipt.Evaluate(withoutEvidence, null, Trace()));
    }

    [Fact]
    public void ThePhaseKeepsItsOwnScopeAndNeverClosesTheOpenTravelCoverage()
    {
        var summary = TravelJournalReceipt.Summarize(PassingRows(), null, Trace());
        Assert.Contains("phase=" + TravelJournalReceipt.Phase, summary);
        Assert.Contains("never edited, rebuilt, reactivated, migrated or bridged", summary);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.Contains("recovered placement", summary);
        // Compatible pairs and driven discrepancies are declared, distinct and all required.
        Assert.Empty(TravelJournalReceipt.CompatiblePairCases.Intersect(TravelJournalReceipt.DrivenDiscrepancyCases));
        Assert.All(TravelJournalReceipt.CompatiblePairCases, id => Assert.Contains(id, TravelJournalReceipt.RequiredCases));
        Assert.All(TravelJournalReceipt.DrivenDiscrepancyCases, id => Assert.Contains(id, TravelJournalReceipt.RequiredCases));
        // It must not collide with the identities of the phases it reuses or of the consumer probes.
        Assert.Empty(TravelJournalReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.Empty(TravelJournalReceipt.RequiredCases.Intersect(TravelCrossSystemReceipt.RequiredCases));
        Assert.Empty(TravelJournalReceipt.RequiredCases.Intersect(AnimaTravelReceipt.RequiredCases));
        Assert.Empty(TravelJournalReceipt.RequiredCases.Intersect(EchoTravelReceipt.RequiredCases));
    }

    [Fact]
    public void TheDeclaredBudgetIsDerivedFromTheCallSitePlanAndFitsTheReservation()
    {
        Assert.Equal(TravelJournalReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences), TravelJournalReceipt.PhaseBudgetSeconds);
        Assert.True(TravelJournalReceipt.PhaseBudgetSeconds > 0);
        Assert.True(TravelJournalReceipt.PhaseBudgetSeconds <= TravelJournalReceipt.LauncherReservationSeconds);
        Assert.Equal(TravelJournalReceipt.CallSites.Sum(site => site.Invocations * site.Placements), TravelJournalReceipt.PlacementWaits);
        Assert.Equal(TravelJournalReceipt.CallSites.Sum(site => site.Invocations * site.Quiescences), TravelJournalReceipt.QuiescenceSamples);
        // The phase drives no route and performs no load of its own; it reuses the qualified phases.
        Assert.All(TravelJournalReceipt.CallSites.Where(site => site.Method.StartsWith("JournalCross", StringComparison.Ordinal)),
            site => Assert.Equal(TravelJournalReceipt.CrossSystemCases, site.Invocations));
    }
}
