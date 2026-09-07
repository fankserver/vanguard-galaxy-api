using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the ACTUAL-CONSUMER travel probe uses to decide PASS/FAIL.
/// Each test reproduces a concrete way a consumer could look correct while being wrong: counting a
/// non-arrival, counting the requested nominal destination instead of the actual one, adopting a
/// foreign session's fact, losing a saved count, keeping the newer session's history after a
/// rollback, or reporting a pass with no witnessed callback at all.
/// </summary>
public sealed class AnimaTravelReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string Origin = "system-origin";
    private const string Destination = "system-destination";
    private const string Nominal = "system-nominal";
    private static long _sequence;

    private static AnimaTravelReceipt.VisitRecord Visit(string system, int visits, double first, double last, string name = "Named")
        => new(system, name, visits, first, last);

    private static TravelLocation At(string system, string? poi = "poi", string? name = "Named")
        => new(system, poi, name, poi);

    private static TravelTransition Fact(TravelTransitionKind kind, TravelMode mode, string? actual,
        double seconds = 100, Guid? session = null, Guid? operation = null, string? requested = null, string? actualName = "Named")
        => new(session ?? Session,
            kind is TravelTransitionKind.InitialPlacement or TravelTransitionKind.RecoveredPlacement ? null : operation ?? Guid.NewGuid(),
            ++_sequence, kind, mode, null,
            requested == null ? null : At(requested),
            actual == null ? null : At(actual, "poi", actualName), seconds, null);

    private static AnimaTravelReceipt.ArrivalEvidence Arrival(TravelTransition fact) => AnimaTravelReceipt.ArrivalEvidence.From(fact);

    private static TravelStationReceipt.Row Row(string id, string status, string evidence = "travel:1", Guid? session = null)
        => new(id, id + " description", status, "identity", (session ?? Session).ToString(), "", evidence, "detail");

    private static List<string> Trace(params (string Surface, long Sequence, Guid Session)[] events)
        => events.Select(entry => string.Join("\t", entry.Sequence, entry.Surface, "case", entry.Session,
            "", "Arrived", "JumpGate", "", "", "", "1.000", "")).ToList();

    private static List<TravelStationReceipt.Row> PassingRows()
        => AnimaTravelReceipt.RequiredCases.Concat(AnimaTravelReceipt.RequiredSubcaseRows)
            .Select(id => Row(id, TravelStationReceipt.Passed)).ToList();

    // --- witnessed arrival -> exactly one visit ------------------------------------------

    [Fact]
    public void WitnessedCrossSystemArrivalCountsOneVisitAtTheActualSystem()
    {
        var before = new[] { Visit(Origin, 2, 10, 50) };
        var after = new[] { Visit(Origin, 2, 10, 50), Visit(Destination, 1, 900, 900) };
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination, seconds: 900, requested: Destination);
        Assert.Null(AnimaTravelReceipt.CheckVisitIncrement(before, after, Arrival(arrival)));
    }

    [Fact]
    public void RepeatVisitIncrementsWithoutRewritingTheFirstVisitTime()
    {
        var before = new[] { Visit(Destination, 1, 10, 10) };
        var after = new[] { Visit(Destination, 2, 10, 900) };
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.Wormhole, Destination, seconds: 900);
        Assert.Null(AnimaTravelReceipt.CheckVisitIncrement(before, after, Arrival(arrival)));
        var rewritten = new[] { Visit(Destination, 2, 900, 900) };
        Assert.Contains("first-visit time", AnimaTravelReceipt.CheckVisitIncrement(before, rewritten, Arrival(arrival)));
    }

    [Fact]
    public void TwoVisitsForOneWitnessedArrivalAreRefused()
    {
        var before = new[] { Visit(Destination, 1, 10, 10) };
        var after = new[] { Visit(Destination, 3, 10, 900) };
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination, seconds: 900);
        Assert.Contains("recorded 3 visit(s) instead of 2", AnimaTravelReceipt.CheckVisitIncrement(before, after, Arrival(arrival)));
    }

    [Fact]
    public void CountingTheRequestedNominalDestinationInsteadOfTheActualArrivalIsRefused()
    {
        var before = Array.Empty<AnimaTravelReceipt.VisitRecord>();
        var redirected = Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination, seconds: 900, requested: Nominal);
        var nominalCounted = new[] { Visit(Nominal, 1, 900, 900) };
        Assert.Contains("is absent from the consumer history", AnimaTravelReceipt.CheckVisitIncrement(before, nominalCounted, Arrival(redirected)));
        var both = new[] { Visit(Destination, 1, 900, 900), Visit(Nominal, 1, 900, 900) };
        Assert.Contains("requested nominal system", AnimaTravelReceipt.CheckVisitIncrement(before, both, Arrival(redirected)));
        var actualOnly = new[] { Visit(Destination, 1, 900, 900) };
        Assert.Null(AnimaTravelReceipt.CheckVisitIncrement(before, actualOnly, Arrival(redirected)));
    }

    [Fact]
    public void TheRecordedVisitTimeMustBeThePublicEventGameTime()
    {
        var before = Array.Empty<AnimaTravelReceipt.VisitRecord>();
        var after = new[] { Visit(Destination, 1, 901, 901) };
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination, seconds: 900);
        Assert.Contains("is not the public event's game time", AnimaTravelReceipt.CheckVisitIncrement(before, after, Arrival(arrival)));
    }

    [Fact]
    public void AnUnavailablePublicLabelPreservesTheStoredLabelInsteadOfInventingOne()
    {
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.Wormhole, Destination, seconds: 900, actualName: null);
        var before = new[] { Visit(Destination, 1, 10, 10, "Stored") };
        Assert.Null(AnimaTravelReceipt.CheckVisitIncrement(before, new[] { Visit(Destination, 2, 10, 900, "Stored") }, Arrival(arrival)));
        Assert.Contains("preserved/observed 'Stored'",
            AnimaTravelReceipt.CheckVisitIncrement(before, new[] { Visit(Destination, 2, 10, 900, "Generated") }, Arrival(arrival)));
        // A first visit with no observed label stores the empty label, never a lazily generated one.
        Assert.Null(AnimaTravelReceipt.CheckVisitIncrement(Array.Empty<AnimaTravelReceipt.VisitRecord>(),
            new[] { Visit(Destination, 1, 900, 900, "") }, Arrival(arrival)));
    }

    [Fact]
    public void AnInSystemArrivalCanNeverClaimACrossSystemVisit()
    {
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, Destination);
        Assert.Contains("only be claimed for a cross-system arrival",
            AnimaTravelReceipt.CheckVisitIncrement(Array.Empty<AnimaTravelReceipt.VisitRecord>(),
                new[] { Visit(Destination, 1, 100, 100) }, Arrival(arrival)));
    }

    [Fact]
    public void AVisitAddedForAnUnrelatedSystemIsRefused()
    {
        var before = Array.Empty<AnimaTravelReceipt.VisitRecord>();
        var after = new[] { Visit(Destination, 1, 900, 900), Visit(Origin, 1, 900, 900) };
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination, seconds: 900);
        Assert.Contains("An unrelated system", AnimaTravelReceipt.CheckVisitIncrement(before, after, Arrival(arrival)));
    }

    // --- non-arrival facts ----------------------------------------------------------------

    [Fact]
    public void NonArrivalFactsMayNotGrowTheHistory()
    {
        var before = new[] { Visit(Origin, 1, 10, 10) };
        Assert.Null(AnimaTravelReceipt.CheckNoVisitGrowth(before, new[] { Visit(Origin, 1, 10, 10) }, "quiet"));
        Assert.Contains("quiet", AnimaTravelReceipt.CheckNoVisitGrowth(before, new[] { Visit(Origin, 2, 10, 60) }, "quiet"));
        Assert.Contains("unexpected system(s)",
            AnimaTravelReceipt.CheckNoVisitGrowth(before, new[] { Visit(Origin, 1, 10, 10), Visit(Destination, 1, 60, 60) }, "quiet"));
    }

    [Fact]
    public void TheQuietWindowMustActuallyCarryTheDrivenNonTravelFacts()
    {
        var window = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.InitialPlacement, TravelMode.Unknown, Origin),
            Fact(TravelTransitionKind.Requested, TravelMode.InSystem, null, requested: Origin),
            Fact(TravelTransitionKind.Cancelled, TravelMode.InSystem, Origin),
            Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, Origin),
            Fact(TravelTransitionKind.RouteCompleted, TravelMode.InSystem, Origin)
        };
        Assert.Null(AnimaTravelReceipt.CheckQuietWindow(window, Session));
        Assert.Contains("no public facts", AnimaTravelReceipt.CheckQuietWindow(Array.Empty<TravelTransition>(), Session));
        Assert.Contains("vacuous", AnimaTravelReceipt.CheckQuietWindow(window.Take(3).ToList(), Session));
        var leaked = window.Concat(new[] { Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination) }).ToList();
        Assert.Contains("cross-system arrival", AnimaTravelReceipt.CheckQuietWindow(leaked, Session));
        var foreign = window.Concat(new[] { Fact(TravelTransitionKind.Requested, TravelMode.InSystem, null, requested: Origin, session: Guid.NewGuid()) }).ToList();
        Assert.Contains("Foreign-session", AnimaTravelReceipt.CheckQuietWindow(foreign, Session));
    }

    // --- witnessed window -----------------------------------------------------------------

    [Fact]
    public void AMissingConsumerCallbackWindowCanNeverPass()
    {
        var window = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, TravelMode.JumpGate, null, requested: Destination),
            Fact(TravelTransitionKind.Departed, TravelMode.JumpGate, Origin)
        };
        Assert.Contains("No witnessed JumpGate arrival",
            AnimaTravelReceipt.CheckArrivalWindow(window, Session, TravelMode.JumpGate, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void AForeignSessionFactIsReportedInsteadOfFilteredAway()
    {
        var window = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination),
            Fact(TravelTransitionKind.Arrived, TravelMode.JumpGate, Destination, session: Guid.NewGuid())
        };
        Assert.Contains("Foreign-session public fact",
            AnimaTravelReceipt.CheckArrivalWindow(window, Session, TravelMode.JumpGate, out _));
    }

    [Fact]
    public void ExactlyOneArrivalOfTheCaseModeIsAccepted()
    {
        var arrival = Fact(TravelTransitionKind.Arrived, TravelMode.Wormhole, Destination);
        var window = new List<TravelTransition> { Fact(TravelTransitionKind.Arrived, TravelMode.InSystem, Origin), arrival };
        Assert.Null(AnimaTravelReceipt.CheckArrivalWindow(window, Session, TravelMode.Wormhole, out var found));
        Assert.Same(arrival, found);
        window.Add(Fact(TravelTransitionKind.Arrived, TravelMode.Wormhole, Destination));
        Assert.Contains("carries 2 Wormhole arrivals", AnimaTravelReceipt.CheckArrivalWindow(window, Session, TravelMode.Wormhole, out _));
    }

    // --- persistence, replacement and rollback ---------------------------------------------

    [Fact]
    public void ReloadMustRestoreExactlyTheSavedCounts()
    {
        var saved = new[] { Visit(Origin, 2, 10, 50), Visit(Destination, 1, 900, 900) };
        Assert.Null(AnimaTravelReceipt.CheckSameHistory(saved, saved.ToArray(), "reload"));
        Assert.Contains("is missing", AnimaTravelReceipt.CheckSameHistory(saved, new[] { saved[0] }, "reload"));
        Assert.Contains("became", AnimaTravelReceipt.CheckSameHistory(saved, new[] { saved[0], Visit(Destination, 1, 900, 950) }, "reload"));
    }

    [Fact]
    public void SlotRollbackMustRestoreTheEarlierFixtureCountsInsteadOfKeepingTheNewerHistory()
    {
        var fixtureBaseline = new[] { Visit(Origin, 2, 10, 50) };
        var newerHistory = new[] { Visit(Origin, 2, 10, 50), Visit(Destination, 1, 900, 900) };
        Assert.Contains("unexpected system(s) " + Destination,
            AnimaTravelReceipt.CheckSameHistory(fixtureBaseline, newerHistory, "earlier fixture rollback"));
        Assert.Null(AnimaTravelReceipt.CheckSameHistory(fixtureBaseline, fixtureBaseline.ToArray(), "earlier fixture rollback"));
    }

    [Fact]
    public void RecordedHistoryIsNeverTruncatedByANewArrival()
    {
        var baseline = new[] { Visit(Origin, 2, 10, 50) };
        Assert.Null(AnimaTravelReceipt.CheckHistoryPreserved(baseline, new[] { baseline[0], Visit(Destination, 1, 900, 900) }, "arrival"));
        Assert.Contains("was truncated", AnimaTravelReceipt.CheckHistoryPreserved(baseline, new[] { Visit(Destination, 1, 900, 900) }, "arrival"));
        Assert.Contains("lost visits", AnimaTravelReceipt.CheckHistoryPreserved(baseline, new[] { Visit(Origin, 1, 10, 50) }, "arrival"));
    }

    [Fact]
    public void ASessionReplacementMustResetTheConsumerLegLatch()
    {
        var saved = Guid.NewGuid();
        var restored = Guid.NewGuid();
        Assert.Null(AnimaTravelReceipt.CheckSessionReplacement(saved, restored, restored, 0));
        Assert.Contains("did not replace the session", AnimaTravelReceipt.CheckSessionReplacement(saved, saved, saved, 0));
        Assert.Contains("counted leg(s)", AnimaTravelReceipt.CheckSessionReplacement(saved, restored, restored, 1));
        Assert.Contains("still latched to session", AnimaTravelReceipt.CheckSessionReplacement(saved, restored, saved, 0));
    }

    [Fact]
    public void OnlyThePairedSidecarOfTheWrittenSaveIsAcceptedAsPersistenceEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgmodapi-anima-travel-tests");
        Assert.Null(AnimaTravelReceipt.CheckSidecarPath(Path.Combine(root, "qa-slot.save.vganima.json"), root, "qa-slot"));
        Assert.Contains("is not the paired companion",
            AnimaTravelReceipt.CheckSidecarPath(Path.Combine(root, "fixture-a.save.vganima.json"), root, "qa-slot"));
        Assert.Contains("is not the paired companion",
            AnimaTravelReceipt.CheckSidecarPath(Path.Combine(root, "other", "qa-slot.save.vganima.json"), root, "qa-slot"));
        Assert.Contains("No consumer sidecar path", AnimaTravelReceipt.CheckSidecarPath("", root, "qa-slot"));
    }

    [Fact]
    public void ThePersistedSchemaMustStillBeTheCurrentConsumerVersion()
    {
        Assert.Null(AnimaTravelReceipt.CheckSidecarVersion(4, 4));
        Assert.Contains("declares schema version 3", AnimaTravelReceipt.CheckSidecarVersion(3, 4));
    }

    // --- consumer contract: no native travel fallback ---------------------------------------

    [Fact]
    public void ADirectNativeTravelOrSystemEntryHookIsRefused()
    {
        Assert.Null(AnimaTravelReceipt.RefuseConsumerTravelPatch("Source.Util.SaveGame", "Store"));
        Assert.Null(AnimaTravelReceipt.RefuseConsumerTravelPatch("Source.MissionSystem.Mission", "FromJson"));
        Assert.Contains("native travel/station owner", AnimaTravelReceipt.RefuseConsumerTravelPatch("Behaviour.Managers.TravelManager", "JumpToSystem"));
        Assert.Contains("native travel/station owner", AnimaTravelReceipt.RefuseConsumerTravelPatch("Behaviour.Travel.JumpGateManager", "SpaceshipHasArrived"));
        Assert.Contains("native travel member", AnimaTravelReceipt.RefuseConsumerTravelPatch("Some.Other.Type", "SetSystemEntry"));
        Assert.Contains("native travel member", AnimaTravelReceipt.RefuseConsumerTravelPatch("Some.Other.Type", "TravelToNextWaypoint"));
    }

    // --- phase evaluation -------------------------------------------------------------------

    [Fact]
    public void EveryRequiredCaseAndSubcaseMustPassWithResolvableEvidence()
    {
        var rows = PassingRows();
        var trace = Trace(("travel", 1, Session));
        Assert.Null(AnimaTravelReceipt.Evaluate(rows, null, trace));
        Assert.StartsWith("PASS", AnimaTravelReceipt.Summarize(rows, null, trace));
    }

    [Fact]
    public void EmptyOrFailedCoverageIsNeverAPass()
    {
        var trace = Trace(("travel", 1, Session));
        Assert.Contains("empty coverage", AnimaTravelReceipt.Evaluate(Array.Empty<TravelStationReceipt.Row>(), null, trace));
        var failed = PassingRows();
        failed[2] = Row(failed[2].Case, TravelStationReceipt.Failed);
        Assert.Contains("Failed cases", AnimaTravelReceipt.Evaluate(failed, null, trace));
        var missingCase = PassingRows().Where(row => row.Case != AnimaTravelReceipt.WormholeCase).ToList();
        Assert.Contains("Required case did not run: " + AnimaTravelReceipt.WormholeCase,
            AnimaTravelReceipt.Evaluate(missingCase, null, trace));
        var missingSubcase = PassingRows().Where(row => row.Case != AnimaTravelReceipt.GateRollbackSubcase).ToList();
        Assert.Contains("Required subcase did not run: " + AnimaTravelReceipt.GateRollbackSubcase,
            AnimaTravelReceipt.Evaluate(missingSubcase, null, trace));
        var notRun = PassingRows();
        notRun[0] = Row(notRun[0].Case, TravelStationReceipt.NotRun);
        Assert.Contains("Required case is not-run", AnimaTravelReceipt.Evaluate(notRun, null, trace));
        var duplicated = PassingRows();
        duplicated.Add(Row(AnimaTravelReceipt.GateCase, TravelStationReceipt.Passed));
        Assert.Contains("Required case recorded 2 rows", AnimaTravelReceipt.Evaluate(duplicated, null, trace));
    }

    [Fact]
    public void EvidenceMustResolveToRealEventsOfTheRowSession()
    {
        var rows = PassingRows();
        Assert.Contains("references an event that is not in the trace for its session",
            AnimaTravelReceipt.Evaluate(rows, null, Trace(("travel", 1, Guid.NewGuid()))));
        var withoutEvidence = PassingRows();
        withoutEvidence[0] = Row(withoutEvidence[0].Case, TravelStationReceipt.Passed, evidence: "");
        Assert.Contains("no observed public events", AnimaTravelReceipt.Evaluate(withoutEvidence, null, Trace(("travel", 1, Session))));
        var subcaseWithoutEvidence = PassingRows();
        int index = subcaseWithoutEvidence.FindIndex(row => row.Case == AnimaTravelReceipt.GateReloadSubcase);
        subcaseWithoutEvidence[index] = Row(AnimaTravelReceipt.GateReloadSubcase, TravelStationReceipt.Passed, evidence: "");
        Assert.Contains("no observed public events", AnimaTravelReceipt.Evaluate(subcaseWithoutEvidence, null, Trace(("travel", 1, Session))));
    }

    [Fact]
    public void APilotFaultIsReportedAndACheckpointIsNeverAPass()
    {
        var rows = PassingRows();
        var trace = Trace(("travel", 1, Session));
        Assert.Contains("Pilot fault", AnimaTravelReceipt.Evaluate(rows, "consumer probe exploded", trace));
        var checkpoint = AnimaTravelReceipt.SummarizeIncomplete(rows, AnimaTravelReceipt.GateCase);
        Assert.StartsWith(TravelStationReceipt.Incomplete, checkpoint);
        Assert.DoesNotContain("PASS", checkpoint);
        Assert.Contains("required-subcases=" + string.Join(",", AnimaTravelReceipt.RequiredSubcaseRows), checkpoint);
    }

    [Fact]
    public void TheDeclaredBudgetIsDerivedFromTheDeclaredWaitsAndFitsTheLauncherReservation()
    {
        Assert.Equal(AnimaTravelReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences), AnimaTravelReceipt.PhaseBudgetSeconds);
        Assert.True(AnimaTravelReceipt.PhaseBudgetSeconds > 0);
        Assert.True(AnimaTravelReceipt.PhaseBudgetSeconds <= AnimaTravelReceipt.LauncherReservationSeconds);
        // The probe reuses the two travel phases in place, so it must not reserve their budgets again.
        Assert.True(AnimaTravelReceipt.LauncherReservationSeconds < TravelCrossSystemReceipt.LauncherReservationSeconds);
        // Every multiplicity comes from the per-method call-site plan, never a hand-typed number.
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Invocations * site.Loads), AnimaTravelReceipt.ProbeLoads);
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Invocations * site.Placements), AnimaTravelReceipt.PlacementWaits);
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Invocations * site.Quiescences), AnimaTravelReceipt.QuiescenceSamples);
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Invocations * site.Settles), AnimaTravelReceipt.Settles);
        // Each cross-system hook runs once per cross-system case.
        Assert.All(AnimaTravelReceipt.CallSites.Where(site => site.Method.StartsWith("CrossCase", StringComparison.Ordinal)),
            site => Assert.Equal(AnimaTravelReceipt.CrossSystemCases, site.Invocations));
    }

    /// <summary>
    /// The published budget is only honest while the declared plan matches the pilot's ACTUAL
    /// waiting call sites. Undercounting them is exactly how a phase publishes a worst case it
    /// cannot keep, so the plan is re-derived here from the pilot source itself.
    /// </summary>
    [Fact]
    public void TheCallSitePlanMatchesThePilotSource()
    {
        var source = File.ReadAllLines(PilotSourcePath());
        var declaration = new Regex(@"^    (?:private|internal)[^=]*?\b(?<name>\w+)\s*\(", RegexOptions.Compiled);
        var observed = AnimaTravelReceipt.CallSites.ToDictionary(site => site.Method, _ => (Loads: 0, Placements: 0, Quiescences: 0, Settles: 0));
        var method = string.Empty;
        foreach (var line in source)
        {
            var match = declaration.Match(line);
            if (match.Success) method = match.Groups["name"].Value;
            if (!observed.TryGetValue(method, out var counts)) continue;
            if (line.Contains("foreach (var frame in SpLoad(", StringComparison.Ordinal)) counts.Loads++;
            if (line.Contains("foreach (var frame in AwaitPlacement(", StringComparison.Ordinal)) counts.Placements++;
            if (line.Contains("foreach (var frame in Quiesce())", StringComparison.Ordinal)) counts.Quiescences++;
            if (line.Contains("foreach (var frame in Settle())", StringComparison.Ordinal)) counts.Settles++;
            observed[method] = counts;
        }
        foreach (var site in AnimaTravelReceipt.CallSites)
        {
            var counts = observed[site.Method];
            Assert.Equal((site.Loads, site.Placements, site.Quiescences, site.Settles),
                (counts.Loads, counts.Placements, counts.Quiescences, counts.Settles));
        }
        // No waiting call site may live in a pilot method the plan does not account for.
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Loads),
            source.Count(line => line.Contains("foreach (var frame in SpLoad(", StringComparison.Ordinal)));
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Placements),
            source.Count(line => line.Contains("foreach (var frame in AwaitPlacement(", StringComparison.Ordinal)));
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Quiescences),
            source.Count(line => line.Contains("foreach (var frame in Quiesce())", StringComparison.Ordinal)));
        Assert.Equal(AnimaTravelReceipt.CallSites.Sum(site => site.Settles),
            source.Count(line => line.Contains("foreach (var frame in Settle())", StringComparison.Ordinal)));
    }

    private static string PilotSourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "QualificationRunner", "AnimaTravelPilot.cs");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Could not locate tools/QualificationRunner/AnimaTravelPilot.cs from " + AppContext.BaseDirectory + ".");
    }

    [Fact]
    public void ThePhaseNeverClaimsTheReusedTravelPhasesOwnCoverage()
    {
        var summary = AnimaTravelReceipt.Summarize(PassingRows(), null, Trace(("travel", 1, Session)));
        Assert.Contains("phase=" + AnimaTravelReceipt.Phase, summary);
        Assert.Contains(TravelCrossSystemReceipt.Phase, summary);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.DoesNotContain(AnimaTravelReceipt.Phase, TravelCrossSystemReceipt.RequiredCases);
        Assert.Empty(AnimaTravelReceipt.RequiredCases.Intersect(TravelCrossSystemReceipt.RequiredCases));
        Assert.Empty(AnimaTravelReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.Empty(AnimaTravelReceipt.RequiredCases.Intersect(AnimaTravelReceipt.RequiredSubcaseRows));
    }
}
