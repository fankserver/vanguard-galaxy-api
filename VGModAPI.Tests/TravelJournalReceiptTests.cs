using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    private static TravelJournalReceipt.LegacyBaseline Baseline(string json, string slot = "qa-journal-baseline")
        => new(slot, TravelJournalReceipt.ReadSidecar(json));

    private static string Document(params string[] rows) => "{ \"version\": 2, \"events\": [" + string.Join(",", rows) + "] }";

    private static string PoiRow(string poi, double seconds = 10) =>
        "{ \"$kind\": \"PoiArrival\", \"gameSeconds\": " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", \"systemGuid\": \"" + System1 + "\", \"poiGuid\": \"" + poi + "\" }";

    private static string DockRow(string station, double seconds = 5) =>
        "{ \"$kind\": \"StationDock\", \"gameSeconds\": " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", \"systemGuid\": \"" + System1 + "\", \"stationGuid\": \"" + station + "\" }";

    [Fact]
    public void AShrunkenOrForeignVersionLogInvalidatesTheWindowsAppendOffsets()
    {
        var sidecar = TravelJournalReceipt.ReadSidecar(Sample);
        var wholeDocument = Baseline(Sample);
        Assert.Null(TravelJournalReceipt.CheckAppendBaseline(sidecar, wholeDocument));
        var oversized = new TravelJournalReceipt.LegacyBaseline("slot",
            TravelJournalReceipt.ReadSidecar(Document(PoiRow("a"), PoiRow("b"), PoiRow("c"), PoiRow("d"))));
        Assert.Contains("shrank", TravelJournalReceipt.CheckAppendBaseline(sidecar, oversized));
        var v1 = TravelJournalReceipt.ReadSidecar("{ \"version\": 1, \"events\": [] }");
        Assert.Contains("schema version 1", TravelJournalReceipt.CheckAppendBaseline(v1, Baseline("{ \"version\": 2, \"events\": [] }")));
    }

    /// <summary>
    /// The HIGH finding of the review: a fresh load makes the archive append its own rows before any
    /// case drives anything. A window whose baseline is the real post-load state must not count them.
    /// </summary>
    [Fact]
    public void LoadTimeLegacyRowsBelongToTheBaselineAndAreNeverCountedAsAWindowsAppends()
    {
        // The load itself opened the interior and re-entered a POI: two rows, before any trip.
        var afterLoad = Document(DockRow("station-a"), PoiRow("poi-loaded"));
        var baseline = Baseline(afterLoad);
        Assert.Equal(2, baseline.Count);
        // One real trip later, only the trip's own row is an append.
        var afterTrip = TravelJournalReceipt.ReadSidecar(Document(DockRow("station-a"), PoiRow("poi-loaded"), PoiRow("poi-a", 120)));
        Assert.Null(TravelJournalReceipt.CheckAppendBaseline(afterTrip, baseline));
        var appended = TravelJournalReceipt.Appended(afterTrip, baseline);
        Assert.Equal(new[] { "poi-a" }, appended.Select(row => row.PoiGuid).ToArray());
        Assert.Null(TravelJournalReceipt.CheckArrivalPair(new[] { "poi-a" }, appended));
        // With the old zero baseline the same document would have carried the load-time rows into
        // the window and broken the pair.
        var withoutBaseline = TravelJournalReceipt.Appended(afterTrip, Baseline("{ \"version\": 2, \"events\": [] }"));
        Assert.Equal(3, withoutBaseline.Count);
        Assert.NotNull(TravelJournalReceipt.CheckArrivalPair(new[] { "poi-a" }, withoutBaseline));
    }

    [Fact]
    public void ABaselinePrefixThatChangedMeansTheStoreWasResetAndTheWindowIsRefused()
    {
        var baseline = Baseline(Document(DockRow("station-a"), PoiRow("poi-loaded")));
        // Same COUNT, different content: a reset store refilled by a replacement load.
        var reset = TravelJournalReceipt.ReadSidecar(Document(DockRow("station-b"), PoiRow("poi-other"), PoiRow("poi-a", 120)));
        var failure = TravelJournalReceipt.CheckAppendBaseline(reset, baseline);
        Assert.Contains("baseline row #0 changed", failure);
        Assert.Contains("not comparable", failure);
        // Even an identical-looking row with a different time is a different row.
        var retimed = TravelJournalReceipt.ReadSidecar(Document(DockRow("station-a", 6), PoiRow("poi-loaded")));
        Assert.Contains("changed", TravelJournalReceipt.CheckAppendBaseline(retimed, baseline));
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

    private static TravelJournalReceipt.InFlightCapture Capture(bool requested = true, bool departed = true,
        bool arrived = false, int frame = 100, bool jumpRunning = true)
        => new(requested, departed, arrived, frame, jumpRunning);

    [Fact]
    public void ThePrefixLeadNeedsFlagsCapturedAtTheSaveAndAnEarlierSaveFrame()
    {
        var inFlight = new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 300.0, system: System2) };
        Assert.Null(TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(), apiArrivedFrame: 140, 420.0));
        // Flags are what the phase saw AT the save; a departure not yet observed there is no lead.
        Assert.Contains("had not observed both the Requested and the Departed",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(departed: false), 140, 420.0));
        Assert.Contains("already observed",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(arrived: true), 140, 420.0));
        Assert.Contains("jump routine was not running",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(jumpRunning: false), 140, 420.0));
        Assert.Contains("recorded no jump-gate transit",
            TravelJournalReceipt.CheckPrefixLead(Array.Empty<TravelJournalReceipt.LegacyEvent>(), System2, Capture(), 140, 420.0));
        Assert.Contains("never observed", TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(), 140, null));
    }

    [Fact]
    public void TheLeadIsFrameOrderingNotClockAdvance()
    {
        var inFlight = new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 300.0, system: System2) };
        // The native clock can stand still across a jump: an EQUAL game time is still a valid lead,
        // because the ordering proof is the captured frame.
        Assert.Null(TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(frame: 100), apiArrivedFrame: 101, 300.0));
        // A legacy row AFTER the arrival is still wrong.
        Assert.Contains("is later than the public arrival time",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(frame: 100), 101, 299.0));
        // No frame ordering, no proof.
        Assert.Contains("not before the public arrival's frame",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(frame: 200), 101, 420.0));
        Assert.Contains("No frame was captured at the in-flight save",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(frame: 0), 101, 420.0));
        Assert.Contains("No frame was captured for the public arrival",
            TravelJournalReceipt.CheckPrefixLead(inFlight, System2, Capture(frame: 100), 0, 420.0));
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
        Assert.Null(TravelJournalReceipt.CheckLegacyBlind(Array.Empty<TravelJournalReceipt.LegacyEvent>(),
            apiCancellationObserved: true, boundarySampled: true));
        Assert.Contains("vacuous", TravelJournalReceipt.CheckLegacyBlind(Array.Empty<TravelJournalReceipt.LegacyEvent>(), false, true));
        // Without its own boundary samples the empty set is an inference from the whole phase.
        Assert.Contains("never sampled at its own boundaries",
            TravelJournalReceipt.CheckLegacyBlind(Array.Empty<TravelJournalReceipt.LegacyEvent>(), true, boundarySampled: false));
        Assert.Contains("appended 1 row(s) between the cancel boundaries",
            TravelJournalReceipt.CheckLegacyBlind(new[] { Legacy(0, TravelJournalReceipt.JumpgateTransitKind, 5) }, true, true));
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
    public void AClockRollbackReportsNoDwellAtAllAndAReportedOneIsRefused()
    {
        // The same session's anchor is LATER than the departure. The adapter's own guard
        // (now >= _since) reports nothing there, so "no dwell" is the contract, not a defect:
        // a rolled-back interval is UNKNOWN, not missing.
        List<TravelTransition> RolledBack(double? dwell) => new()
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: 100),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 150, dwell: 50),
            Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, "poi-b", seconds: 500),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-b", seconds: 100, dwell: dwell),
        };
        Assert.Null(TravelJournalReceipt.CheckDwellAnchoring(RolledBack(null), out int pairs, out _));
        // The positive pair before the rollback is still required and still counted.
        Assert.Equal(1, pairs);
        // Any reported dwell on a rolled-back anchor is an invention, whether clamped to zero or not.
        Assert.Contains("clock rollback", TravelJournalReceipt.CheckDwellAnchoring(RolledBack(0), out _, out _));
        Assert.Contains("clock rollback", TravelJournalReceipt.CheckDwellAnchoring(RolledBack(400), out _, out _));
        // A rollback never satisfies the phase on its own: without a positive pair it stays vacuous.
        var onlyRollback = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, "poi-a", seconds: 500),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: 100, dwell: null),
        };
        Assert.Contains("vacuous", TravelJournalReceipt.CheckDwellAnchoring(onlyRollback, out _, out _));
    }

    [Fact]
    public void TheDwellComparisonIsExactBecauseTheGameClockIsConstantAcrossOnePairsReads()
    {
        // The adapter reads GamePlayer.elapsedTime separately for the tracker call and for the
        // emitted fact; that clock advances once per frame, and both reads of one pair happen in the
        // same frame, so the difference is exact and no epsilon is warranted.
        Assert.Equal(0, TravelJournalReceipt.DwellToleranceSeconds);
        // Values whose difference is not representable at F3 precision: the rule compares the raw
        // doubles, so the exact difference passes with no epsilon...
        const double anchor = 1234.5678901234;
        const double departure = 1300.1234567891;
        var exact = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: anchor),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: departure, dwell: departure - anchor),
        };
        Assert.Null(TravelJournalReceipt.CheckDwellAnchoring(exact, out _, out double largest));
        Assert.Equal(departure - anchor, largest);
        // ...while a value that merely agrees to millisecond rounding does not.
        var rounded = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, "poi-a", seconds: anchor),
            Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-a", seconds: departure,
                dwell: Math.Round(departure - anchor, 3)),
        };
        Assert.Contains("instead of its own anchor difference", TravelJournalReceipt.CheckDwellAnchoring(rounded, out _, out _));
        // The receipt carries full round-trip precision, so a reader can redo the subtraction.
        Assert.Equal((departure - anchor).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            TravelJournalReceipt.Exact(departure - anchor));
        Assert.Contains("E", TravelJournalReceipt.Exact(1e-9));
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
        Assert.Equal(TravelJournalReceipt.CallSites.Sum(site => site.Invocations * site.InFlightDepartures), TravelJournalReceipt.InFlightDepartureWaits);
        // The bounded in-flight departure wait the prefix-lead case adds is declared, not implicit.
        Assert.Equal(1, TravelJournalReceipt.InFlightDepartureWaits);
        Assert.Contains(TravelJournalReceipt.PhaseWaits, wait => wait.Name == "in-flight-departure"
            && wait.Seconds == TravelJournalReceipt.InFlightDepartureSeconds && wait.Occurrences == 1);
        // Only the jump-gate case has an in-flight hook, so this site runs ONCE, not once per case.
        Assert.Equal(1, TravelJournalReceipt.CallSites.Single(site => site.Method == "JournalCrossInFlight").Invocations);
        // The phase drives no route and performs no load of its own; it reuses the qualified phases.
        Assert.All(TravelJournalReceipt.CallSites.Where(site => site.Method is "JournalCrossCaseReady" or "JournalCrossCaseCompleted"),
            site => Assert.Equal(TravelJournalReceipt.CrossSystemCases, site.Invocations));
        // Every real save the phase takes is declared, counted by ACTUAL invocations rather than by
        // lexical call site: 1 in-system ready + 1 in-system completed + 2 cancel boundaries
        // + 2 cross ready (one per case) + 1 in-flight + 1 wormhole completion = 8. Saves are
        // synchronous, so they carry no wait and no budget term.
        Assert.Equal(8, TravelJournalReceipt.Saves);
        Assert.Equal(new[] { 1, 1, 2, 2, 1, 1, 0 }, TravelJournalReceipt.CallSites.Select(site => site.SavingInvocations).ToArray());
        // A site never claims more saving invocations than it has invocations, and the completion
        // site saves on the wormhole case only.
        Assert.All(TravelJournalReceipt.CallSites, site => Assert.True(site.SavingInvocations <= site.Invocations));
        var completed = TravelJournalReceipt.CallSites.Single(site => site.Method == "JournalCrossCaseCompleted");
        Assert.Equal(TravelJournalReceipt.CrossSystemCases, completed.Invocations);
        Assert.Equal(1, completed.SavingInvocations);
        Assert.All(TravelJournalReceipt.PhaseWaits, wait => Assert.True(wait.Seconds > 0));
    }

    /// <summary>
    /// The cancel boundary samples sit INSIDE the in-system window. Its own baseline must survive
    /// them: the enclosing window still subtracts what it captured at its Ready hook, so all three
    /// in-system arrivals (route plus two chained hops) still pair with all three legacy rows. This
    /// is the shape the pilot links together, expressed over the rules the pilot calls.
    /// </summary>
    [Fact]
    public void ACancelBoundarySampleNeverBecomesTheEnclosingWindowsBaseline()
    {
        // What the fresh load itself wrote, before any case drove anything.
        var windowBaseline = Baseline(Document(DockRow("station-a"), PoiRow("poi-loaded")), "qa-journal-baseline-in-system");
        // The in-system route ran; the cancel case then sampled its own two boundaries.
        var atCancel = Document(DockRow("station-a"), PoiRow("poi-loaded"), PoiRow("poi-route", 120));
        var cancelBaseline = Baseline(atCancel, "qa-journal-cancel-before");
        var afterCancel = TravelJournalReceipt.ReadSidecar(atCancel);
        Assert.Null(TravelJournalReceipt.CheckAppendBaseline(afterCancel, cancelBaseline));
        Assert.Empty(TravelJournalReceipt.Appended(afterCancel, cancelBaseline));
        Assert.Null(TravelJournalReceipt.CheckLegacyBlind(TravelJournalReceipt.Appended(afterCancel, cancelBaseline),
            apiCancellationObserved: true, boundarySampled: true));
        // The chained route then added its two hops and the window closed.
        var atCompletion = TravelJournalReceipt.ReadSidecar(Document(DockRow("station-a"), PoiRow("poi-loaded"),
            PoiRow("poi-route", 120), PoiRow("poi-hop-1", 200), PoiRow("poi-hop-2", 260)));
        var arrivals = new[] { "poi-route", "poi-hop-1", "poi-hop-2" };
        // The window's OWN baseline still applies across the cancel: prefix and count are unchanged.
        Assert.Equal(2, windowBaseline.Count);
        Assert.Null(TravelJournalReceipt.CheckAppendBaseline(atCompletion, windowBaseline));
        var windowAppended = TravelJournalReceipt.Appended(atCompletion, windowBaseline);
        Assert.Equal(arrivals, windowAppended.Select(row => row.PoiGuid).ToArray());
        Assert.Null(TravelJournalReceipt.CheckArrivalPair(arrivals, windowAppended));
        // The defect this guards against: closing the window against the CANCEL sample drops the
        // route row, so three public arrivals face two legacy rows and the required case fails.
        var clobbered = TravelJournalReceipt.Appended(atCompletion, cancelBaseline);
        Assert.Equal(2, clobbered.Count);
        Assert.NotNull(TravelJournalReceipt.CheckArrivalPair(arrivals, clobbered));
    }

    /// <summary>
    /// The rules above only hold if the pilot really keeps the two baselines in separate state. The
    /// pilot needs Unity, so its wiring is asserted over its source: the capture helper assigns no
    /// phase state, only the two Ready hooks own the window baseline, and every compared save names
    /// the baseline it is checked against.
    /// </summary>
    [Fact]
    public void ThePilotKeepsTheWindowBaselineAndTheCancelSampleInSeparateState()
    {
        var source = File.ReadAllText(JournalPilotSourcePath());
        int capture = source.IndexOf("private TravelJournalReceipt.LegacyBaseline CaptureLegacyBaseline(", StringComparison.Ordinal);
        Assert.True(capture > 0, "The pilot must still capture baselines through one helper.");
        var helper = source.Substring(capture, source.IndexOf("\n    }", capture, StringComparison.Ordinal) - capture);
        // Pure with respect to phase state: a nested sample can never replace an enclosing baseline.
        Assert.DoesNotContain("_tjWindowBaseline", helper);
        Assert.DoesNotContain("_tjCancelBefore", helper);
        // Exactly the two Ready hooks assign the window baseline; nothing else writes it.
        Assert.Equal(2, Regex.Matches(source, @"_tjWindowBaseline = ").Count);
        Assert.Contains("_tjWindowBaseline = CaptureLegacyBaseline(\"qa-journal-baseline-in-system\")", source);
        Assert.Contains("_tjWindowBaseline = CaptureLegacyBaseline(\"qa-journal-baseline-\" + crossCase)", source);
        // The cancel boundary owns a SEPARATE baseline and restores no global one.
        Assert.Contains("_tjCancelBefore = CaptureLegacyBaseline(\"qa-journal-cancel-before\")", source);
        Assert.Contains("SaveAndReadLegacy(\"qa-journal-cancel-after\", _tjCancelBefore)", source);
        // Every compared save names its baseline explicitly; none reads an ambient "current" one.
        var compared = Regex.Matches(source, "SaveAndReadLegacy\\(\"[^\"]+\", _tj\\w+\\)").Count;
        Assert.Equal(4, compared);
        Assert.Equal(compared + 1, Regex.Matches(source, @"SaveAndReadLegacy\(").Count);
        // Declared saving invocations equal the pilot's real ones: three lexical capture sites, of
        // which the cross-system one runs once per case, plus the four compared saves.
        Assert.Equal(3, Regex.Matches(source, "CaptureLegacyBaseline\\(\"").Count);
        Assert.Equal(TravelJournalReceipt.Saves, 2 + TravelJournalReceipt.CrossSystemCases + compared);
    }

    /// <summary>
    /// The cancel boundary hook must sit INSIDE the cancel case's quiet window, so an unsolicited
    /// native route during its own quiesce is still caught by that case's unsolicited-travel check
    /// instead of escaping it.
    /// </summary>
    [Fact]
    public void TheCancelBoundaryHookRunsInsideTheCancelCasesQuietWindow()
    {
        var source = File.ReadAllText(StationDriverSourcePath());
        int start = source.IndexOf("private IEnumerable<object?> CaseEarlyCancel()", StringComparison.Ordinal);
        Assert.True(start > 0, "The qualified cancel case must still exist.");
        var body = source.Substring(start, source.IndexOf("private IEnumerable<object?> CaseChainedRoute()", start, StringComparison.Ordinal) - start);
        int offset = body.IndexOf("int offset = Travel.Count;", StringComparison.Ordinal);
        int hook = body.IndexOf("TravelJournalCancelBoundary(\"before\")", StringComparison.Ordinal);
        int ready = body.IndexOf("foreach (var frame in ReadyToTravel(target))", StringComparison.Ordinal);
        int check = body.IndexOf("CheckNoUnsolicitedTravel(", StringComparison.Ordinal);
        Assert.True(offset > 0 && hook > offset, "The quiet window must open before the boundary sample.");
        Assert.True(hook < ready && ready < check, "The boundary sample must stay inside the checked quiet window.");
        int after = body.IndexOf("TravelJournalCancelBoundary(\"after\")", StringComparison.Ordinal);
        Assert.True(after > check, "The closing boundary sample must run after the case's own assertions.");
    }

    /// <summary>A travel fact inside that quiet window is a failure, never an ignored sample.</summary>
    [Fact]
    public void AnUnsolicitedTravelFactInsideTheCancelWindowIsRefused()
    {
        var unsolicited = new[] { Fact(TravelTransitionKind.Departed, TravelMode.InSystem, "poi-x", seconds: 5) };
        Assert.NotNull(TravelStationReceipt.CheckNoUnsolicitedTravel("the cancel case", unsolicited, false, "detail"));
        Assert.Null(TravelStationReceipt.CheckNoUnsolicitedTravel("the cancel case", Array.Empty<TravelTransition>(), false, "detail"));
    }

    private static string JournalPilotSourcePath() => QualificationSourcePath("TravelJournalPilot.cs");
    private static string StationDriverSourcePath() => QualificationSourcePath("TravelStationDriver.cs");

    private static string QualificationSourcePath(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "QualificationRunner", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Could not locate tools/QualificationRunner/" + fileName + " from " + AppContext.BaseDirectory + ".");
    }
}
