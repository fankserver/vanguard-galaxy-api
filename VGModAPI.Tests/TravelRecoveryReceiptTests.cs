using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the native travel RECOVERY/CONTINUATION pilot uses to decide
/// PASS/FAIL. Each test reproduces a concrete defect a passing compilation would otherwise hide: a
/// "recovery" that is really an arrival, a placement observed without a loaded and initialized POI,
/// a route completed at the gate arrival while the native route still had a waypoint, a gate hop
/// that was not observed inside the native jump routine, and a phase that claims a pass without its
/// two required cases.
/// </summary>
public sealed class TravelRecoveryReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string System1 = "system-1";
    private const string System2 = "system-2";
    private const string Station = "station-1";
    private const string Target = "poi-target";
    private const string Gate1 = "gate-1";
    private const string Gate2 = "gate-2";
    private const string Follow = "poi-follow";
    private static long _sequence;

    private static TravelLocation At(string system, string? poi) => new(system, poi, system, poi);

    private static TravelTransition Fact(TravelTransitionKind kind, Guid? operation,
        (string System, string? Poi)? origin, (string System, string? Poi)? requested, (string System, string? Poi)? actual,
        double seconds, Guid? session = null, TravelMode mode = TravelMode.InSystem, double? dwell = null, long? sequence = null)
        => new(session ?? Session, operation, sequence ?? ++_sequence, kind, mode,
            origin == null ? null : At(origin.Value.System, origin.Value.Poi),
            requested == null ? null : At(requested.Value.System, requested.Value.Poi),
            actual == null ? null : At(actual.Value.System, actual.Value.Poi), seconds, dwell);

    // --- recovered placement -----------------------------------------------------------------

    // The genuine native stream: one leg requested and departed at the loaded origin, cancelled
    // after that departure, then an operation-less recovered placement at the destination POI the
    // native world already reports as current.
    private static List<TravelTransition> RecoveryFacts(Guid leg)
    {
        double clock = 0;
        return new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, leg, null, (System1, Target), null, clock += 1),
            Fact(TravelTransitionKind.Departed, leg, (System1, Station), null, null, clock += 1),
            Fact(TravelTransitionKind.Cancelled, leg, null, null, null, clock += 1),
            Fact(TravelTransitionKind.RecoveredPlacement, null, null, null, (System1, Target), clock += 1, mode: TravelMode.Unknown)
        };
    }

    private static Dictionary<long, TravelRecoveryReceipt.NativeSnapshot> RecoverySnapshots(IReadOnlyList<TravelTransition> facts)
    {
        var result = new Dictionary<long, TravelRecoveryReceipt.NativeSnapshot>();
        for (int index = 0; index < facts.Count; index++)
        {
            // Request at the loaded origin, departure with the origin unloaded, and the cancel and
            // the placement in the window where the destination POI is current and initialized.
            bool loaded = index == 0 || index >= 2;
            bool destination = index >= 2;
            result[facts[index].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(loaded, loaded,
                travelActive: index < 2, usingJumpgate: false, waypoints: index < 2 ? 1 : 0,
                locationKey: TravelStationReceipt.Location(System1, destination ? Target : index == 0 ? Station : null),
                ownedByCase: true);
        }
        return result;
    }

    // What the pilot samples in the frame the cancel is about to be issued: a LIVE native route at
    // the destination POI whose manager is initialized.
    private static TravelRecoveryReceipt.NativeSnapshot Acquisition(bool travelActive = true, bool managerReady = true,
        bool currentPoiKnown = true, bool usingJumpgate = false, bool owned = true, string? location = null)
        => new(currentPoiKnown, managerReady, travelActive, usingJumpgate, 1,
            location ?? TravelStationReceipt.Location(System1, Target), owned);

    [Fact]
    public void TheRecoveryStreamIsACancelledLegFollowedByAnOperationLessPlacement()
    {
        var facts = RecoveryFacts(Guid.NewGuid());
        Assert.Null(TravelRecoveryReceipt.CheckRecoveredPlacement(facts, Session, System1, Station, Target));
        Assert.Null(TravelRecoveryReceipt.CheckRecoveryEvidence(facts, RecoverySnapshots(facts),
            TravelStationReceipt.Location(System1, Target), Acquisition()));
    }

    [Fact]
    public void AnArrivalIsNeverAcceptedAsARecoveredPlacement()
    {
        var leg = Guid.NewGuid();
        var facts = RecoveryFacts(leg);
        // The exact defect this case exists to exclude: the leg completed normally, so the public
        // fact is an Arrived with the leg's operation, not a placement.
        facts[3] = Fact(TravelTransitionKind.Arrived, leg, (System1, Station), (System1, Target), (System1, Target), 9,
            sequence: facts[3].Sequence);
        Assert.Contains("instead of", TravelRecoveryReceipt.CheckRecoveredPlacement(facts, Session, System1, Station, Target));
        // A placement carrying an operation identity is refused by the public contract itself.
        Assert.Throws<ArgumentException>(() => Fact(TravelTransitionKind.RecoveredPlacement, leg, null, null, (System1, Target), 9));
    }

    [Fact]
    public void ARecoveredPlacementMayNotInventOriginRequestedModeOrDwell()
    {
        var baseline = RecoveryFacts(Guid.NewGuid());
        var invented = new List<TravelTransition>(baseline)
        {
            [3] = Fact(TravelTransitionKind.RecoveredPlacement, null, (System1, Station), null, (System1, Target), 9,
                mode: TravelMode.Unknown, sequence: baseline[3].Sequence)
        };
        Assert.Contains("invented an origin", TravelRecoveryReceipt.CheckRecoveredPlacement(invented, Session, System1, Station, Target));
        var moded = new List<TravelTransition>(baseline)
        {
            [3] = Fact(TravelTransitionKind.RecoveredPlacement, null, null, null, (System1, Target), 9,
                mode: TravelMode.InSystem, sequence: baseline[3].Sequence)
        };
        Assert.Contains("travel mode InSystem", TravelRecoveryReceipt.CheckRecoveredPlacement(moded, Session, System1, Station, Target));
        var dwelled = new List<TravelTransition>(baseline)
        {
            [3] = Fact(TravelTransitionKind.RecoveredPlacement, null, null, null, (System1, Target), 9,
                mode: TravelMode.Unknown, dwell: 5, sequence: baseline[3].Sequence)
        };
        Assert.Contains("dwell it cannot know", TravelRecoveryReceipt.CheckRecoveredPlacement(dwelled, Session, System1, Station, Target));
        var elsewhere = new List<TravelTransition>(baseline)
        {
            [3] = Fact(TravelTransitionKind.RecoveredPlacement, null, null, null, (System1, "poi-other"), 9,
                mode: TravelMode.Unknown, sequence: baseline[3].Sequence)
        };
        Assert.Contains("instead of the native current POI",
            TravelRecoveryReceipt.CheckRecoveredPlacement(elsewhere, Session, System1, Station, Target));
    }

    [Fact]
    public void ACancellationAfterDepartureMayNotClaimALocationAndItsLegMustStayOne()
    {
        var leg = Guid.NewGuid();
        var located = RecoveryFacts(leg);
        located[2] = Fact(TravelTransitionKind.Cancelled, leg, null, null, (System1, Station), 3, sequence: located[2].Sequence);
        Assert.Contains("instead of an unknown location",
            TravelRecoveryReceipt.CheckRecoveredPlacement(located, Session, System1, Station, Target));
        var split = RecoveryFacts(leg);
        split[2] = Fact(TravelTransitionKind.Cancelled, Guid.NewGuid(), null, null, null, 3, sequence: split[2].Sequence);
        Assert.Contains("do not share its operation identity",
            TravelRecoveryReceipt.CheckRecoveredPlacement(split, Session, System1, Station, Target));
        var foreign = RecoveryFacts(leg);
        foreign[1] = Fact(TravelTransitionKind.Departed, leg, (System1, Station), null, null, 2,
            session: Guid.NewGuid(), sequence: foreign[1].Sequence);
        Assert.Contains("Foreign-session", TravelRecoveryReceipt.CheckRecoveredPlacement(foreign, Session, System1, Station, Target));
    }

    [Fact]
    public void TheRecoveryNeedsTheNativeReadinessWindowItClaims()
    {
        var facts = RecoveryFacts(Guid.NewGuid());
        var key = TravelStationReceipt.Location(System1, Target);
        // A placement observed without a loaded, initialized POI is not a readiness recovery.
        var unloaded = RecoverySnapshots(facts);
        unloaded[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(false, false, false, false, 0, key, true);
        Assert.Contains("not observed with a current POI whose manager is initialized",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, unloaded, key, Acquisition()));
        // The cancel must be taken in the window, not before the destination POI became current.
        var early = RecoverySnapshots(facts);
        early[facts[2].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(false, false, true, false, 1, key, true);
        Assert.Contains("not observed in the native window", TravelRecoveryReceipt.CheckRecoveryEvidence(facts, early, key, Acquisition()));
        // The departure must really have unloaded its origin.
        var loadedDeparture = RecoverySnapshots(facts);
        loadedDeparture[facts[1].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, false, 1, key, true);
        Assert.Contains("origin was still loaded", TravelRecoveryReceipt.CheckRecoveryEvidence(facts, loadedDeparture, key, Acquisition()));
        // A route still running, a remaining waypoint, a jump routine or a replaced owner all refuse.
        var running = RecoverySnapshots(facts);
        running[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, false, 0, key, true);
        Assert.Contains("native travel was still active", TravelRecoveryReceipt.CheckRecoveryEvidence(facts, running, key, Acquisition()));
        var queued = RecoverySnapshots(facts);
        queued[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, false, false, 2, key, true);
        Assert.Contains("native waypoints remained", TravelRecoveryReceipt.CheckRecoveryEvidence(facts, queued, key, Acquisition()));
        var jumping = RecoverySnapshots(facts);
        jumping[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, false, true, 0, key, true);
        Assert.Contains("inside a native jump routine", TravelRecoveryReceipt.CheckRecoveryEvidence(facts, jumping, key, Acquisition()));
        var foreignOwner = RecoverySnapshots(facts);
        foreignOwner[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, false, false, 0, key, false);
        Assert.Contains("was not the instance this case captured",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, foreignOwner, key, Acquisition()));
        // An elsewhere-observed placement is not this case's recovery.
        var elsewhere = RecoverySnapshots(facts);
        elsewhere[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, false, false, 0,
            TravelStationReceipt.Location(System1, "poi-other"), true);
        Assert.Contains("instead of", TravelRecoveryReceipt.CheckRecoveryEvidence(facts, elsewhere, key, Acquisition()));
        // A missing snapshot is a missing proof, never a pass.
        Assert.Contains("No native snapshot",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, new Dictionary<long, TravelRecoveryReceipt.NativeSnapshot>(), key, Acquisition()));
    }

    [Fact]
    public void AReadinessWindowWithoutALiveNativeRouteIsRefused()
    {
        var facts = RecoveryFacts(Guid.NewGuid());
        var snapshots = RecoverySnapshots(facts);
        var key = TravelStationReceipt.Location(System1, Target);
        // The abandoned-route shape this rule exists to exclude: the native wait predicate of the
        // inspected build passes when no local manager is registered, so a route can end silently
        // and the manager can initialize afterwards. The destination POI is then current and ready
        // with nothing travelling, and a cancel there would interrupt no route at all.
        Assert.Contains("was not running when the readiness window was acquired",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, snapshots, key, Acquisition(travelActive: false)));
        // The other acquisition requirements are asserted at the same boundary.
        Assert.Contains("without a current POI whose manager is initialized",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, snapshots, key, Acquisition(managerReady: false)));
        Assert.Contains("without a current POI whose manager is initialized",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, snapshots, key, Acquisition(currentPoiKnown: false)));
        Assert.Contains("acquired inside a native jump routine",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, snapshots, key, Acquisition(usingJumpgate: true)));
        Assert.Contains("was not the instance this case captured",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, snapshots, key, Acquisition(owned: false)));
        Assert.Contains("acquired at",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, snapshots, key,
                Acquisition(location: TravelStationReceipt.Location(System1, "poi-other"))));
        // The post-cancel placement rule is unchanged and stays separate: the route must be gone
        // THERE, which is exactly the opposite of the acquisition requirement.
        var running = RecoverySnapshots(facts);
        running[facts[3].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, false, 0, key, true);
        Assert.Contains("native travel was still active",
            TravelRecoveryReceipt.CheckRecoveryEvidence(facts, running, key, Acquisition()));
    }

    [Fact]
    public void AttemptRowsArePersistedBoundedAndConsistentWithTheCase()
    {
        var rows = CaseRowsOnly();
        rows.Add(AttemptRow(TravelRecoveryReceipt.DescribeAttempt(1, Target, TravelRecoveryReceipt.AttemptArrivalFirstOutcome, "the native arrival ran first")));
        rows.Add(AttemptRow(TravelRecoveryReceipt.DescribeAttempt(2, "poi-second", TravelRecoveryReceipt.AttemptCancelledOutcome)));
        Assert.Null(TravelRecoveryReceipt.CheckAttempts(rows));
        Assert.Null(TravelRecoveryReceipt.Evaluate(rows, null, Trace()));
        // A passed case with no persisted attempt at all cannot be believed.
        Assert.Contains("without a single persisted attempt row", TravelRecoveryReceipt.CheckAttempts(CaseRowsOnly()));
        // Exactly one attempt may report the cancel the case claims.
        var twoCancels = new List<TravelStationReceipt.Row>(rows)
        {
            AttemptRow(TravelRecoveryReceipt.DescribeAttempt(3, "poi-third", TravelRecoveryReceipt.AttemptCancelledOutcome))
        };
        Assert.Contains("successful attempts", TravelRecoveryReceipt.CheckAttempts(twoCancels));
        // The bound is the COMMITTED one, and an attempt is never coverage.
        var overBound = new List<TravelStationReceipt.Row>(CaseRowsOnly());
        for (int index = 0; index <= TravelRecoveryReceipt.RecoveryAttempts; index++)
            overBound.Add(AttemptRow(TravelRecoveryReceipt.DescribeAttempt(index + 1, "poi-x", TravelRecoveryReceipt.AttemptArrivalFirstOutcome)));
        Assert.Contains("more than the committed bound", TravelRecoveryReceipt.CheckAttempts(overBound));
        var promoted = new List<TravelStationReceipt.Row>(CaseRowsOnly())
        {
            new(TravelRecoveryReceipt.RecoveryAttemptRow, "description", TravelStationReceipt.Passed, "", Session.ToString(), "", "",
                TravelRecoveryReceipt.DescribeAttempt(1, Target, TravelRecoveryReceipt.AttemptCancelledOutcome))
        };
        Assert.Contains("attempts are diagnostics, never coverage", TravelRecoveryReceipt.CheckAttempts(promoted));
    }

    /// <summary>
    /// The attempt schema itself is the evidence: a started row, a duplicate or gapped number, a
    /// foreign session, an unknown outcome string, a success that is not the last attempt and a
    /// forged bound are all refused, so an attempt log can never be satisfied by free text.
    /// </summary>
    [Fact]
    public void TheAttemptScheduleIsRejectedWhenItsShapeIsNotTheCommittedOne()
    {
        List<TravelStationReceipt.Row> With(params string[] details)
        {
            var rows = CaseRowsOnly();
            foreach (var detail in details) rows.Add(AttemptRow(detail));
            return rows;
        }
        string Miss(int number, string target = "poi-x") =>
            TravelRecoveryReceipt.DescribeAttempt(number, target, TravelRecoveryReceipt.AttemptArrivalFirstOutcome);
        string Success(int number) => TravelRecoveryReceipt.DescribeAttempt(number, Target, TravelRecoveryReceipt.AttemptCancelledOutcome);
        // Still in its started state: a failure artifact, never a terminal outcome.
        var started = With(TravelRecoveryReceipt.DescribeStartedAttempt(1, Target));
        Assert.Contains("no numbered terminal outcome", TravelRecoveryReceipt.CheckAttempts(started));
        Assert.NotNull(TravelRecoveryReceipt.Evaluate(started, null, Trace()));
        Assert.DoesNotContain("outcome=", TravelRecoveryReceipt.DescribeStartedAttempt(1, Target));
        // Duplicate and gapped numbering.
        Assert.Contains("is recorded twice", TravelRecoveryReceipt.CheckAttempts(With(Miss(1), Miss(1))));
        Assert.Contains("not numbered contiguously", TravelRecoveryReceipt.CheckAttempts(With(Miss(2), Success(3))));
        // A number outside the committed bound.
        Assert.Contains("outside the committed bound",
            TravelRecoveryReceipt.CheckAttempts(With(TravelRecoveryReceipt.DescribeAttempt(
                TravelRecoveryReceipt.RecoveryAttempts + 1, Target, TravelRecoveryReceipt.AttemptArrivalFirstOutcome))));
        // A foreign session cannot contribute an attempt to this case.
        var foreignSession = CaseRowsOnly();
        foreignSession.Add(new TravelStationReceipt.Row(TravelRecoveryReceipt.RecoveryAttemptRow, "description",
            TravelStationReceipt.NotRun, "", Guid.NewGuid().ToString(), "", "", Success(1)));
        Assert.Contains("belongs to session", TravelRecoveryReceipt.CheckAttempts(foreignSession));
        // Only known terminal outcomes, and the success must be the last attempt.
        Assert.Contains("unknown outcome", TravelRecoveryReceipt.CheckAttempts(
            With("attempt1={target=poi-x,outcome=it-worked-out-fine}")));
        Assert.Contains("not the last one", TravelRecoveryReceipt.CheckAttempts(With(Success(1), Miss(2))));
        // An attempt before the success must be a MISS the case may continue after, not a terminal
        // failure such as the still-running timeout or an unclosed abandoned leg.
        Assert.Contains("not a miss the case may continue after", TravelRecoveryReceipt.CheckAttempts(
            With(TravelRecoveryReceipt.DescribeAttempt(1, "poi-x", TravelRecoveryReceipt.AttemptTimeoutRunningOutcome), Success(2))));
        Assert.Contains("not a miss the case may continue after", TravelRecoveryReceipt.CheckAttempts(
            With(TravelRecoveryReceipt.DescribeAttempt(1, "poi-x", TravelRecoveryReceipt.AttemptUnclosedOutcome), Success(2))));
        // Every declared outcome is a distinct known kind, and the miss kinds are a subset.
        Assert.Equal(TravelRecoveryReceipt.AttemptOutcomes.Length, TravelRecoveryReceipt.AttemptOutcomes.Distinct().Count());
        Assert.All(TravelRecoveryReceipt.AttemptMissOutcomes, outcome => Assert.Contains(outcome, TravelRecoveryReceipt.AttemptOutcomes));
        Assert.DoesNotContain(TravelRecoveryReceipt.AttemptCancelledOutcome, TravelRecoveryReceipt.AttemptMissOutcomes);
    }

    /// <summary>
    /// The defect a missed attempt would otherwise cause: the abandoned leg stays pending, the next
    /// route request supersedes it, and the tracker truthfully publishes that leg's Cancelled INSIDE
    /// the next attempt's window. The exact four-fact rule then fails an otherwise good attempt, so
    /// the miss must close its own leg first and prove the closure belongs to its own operation.
    /// </summary>
    [Fact]
    public void AMissedAttemptMustCloseItsOwnLegBeforeTheNextWindowOpens()
    {
        var abandoned = Guid.NewGuid();
        double clock = 0;
        // What the miss window looks like after its own cleanup cancel: its own leg, closed.
        var closed = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, abandoned, null, (System1, Target), null, clock += 1),
            Fact(TravelTransitionKind.Departed, abandoned, (System1, Station), null, null, clock += 1),
            Fact(TravelTransitionKind.Cancelled, abandoned, null, null, null, clock += 1)
        };
        Assert.Null(TravelRecoveryReceipt.CheckMissCleanup(closed, cancelAccepted: true));
        // The cleanup may itself produce a recovered placement; that is allowed and is never the
        // case's positive coverage.
        var withPlacement = new List<TravelTransition>(closed)
        {
            Fact(TravelTransitionKind.RecoveredPlacement, null, null, null, (System1, Target), clock += 1, mode: TravelMode.Unknown)
        };
        Assert.Null(TravelRecoveryReceipt.CheckMissCleanup(withPlacement, true));
        // A refused cancel, an unclosed leg, a closure belonging to another operation and a new
        // stage started by the cleanup are all refused.
        Assert.Contains("was refused", TravelRecoveryReceipt.CheckMissCleanup(closed, cancelAccepted: false));
        Assert.Contains("closure it must end in", TravelRecoveryReceipt.CheckMissCleanup(closed.Take(2).ToList(), true));
        var unclosed = new List<TravelTransition>(closed) { [2] = Fact(TravelTransitionKind.Arrived, abandoned,
            (System1, Station), (System1, Target), (System1, Target), 3, sequence: closed[2].Sequence) };
        Assert.Contains("did not close its own leg", TravelRecoveryReceipt.CheckMissCleanup(unclosed, true));
        var foreignClosure = new List<TravelTransition>(closed)
        {
            [2] = Fact(TravelTransitionKind.Cancelled, Guid.NewGuid(), null, null, null, 3, sequence: closed[2].Sequence)
        };
        Assert.Contains("does not belong to the missed attempt's own operation",
            TravelRecoveryReceipt.CheckMissCleanup(foreignClosure, true));
        var newStage = new List<TravelTransition>(closed)
        {
            Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, (System1, "poi-next"), null, clock += 1)
        };
        Assert.Contains("started a new stage", TravelRecoveryReceipt.CheckMissCleanup(newStage, true));
        // The contaminated shape the closure prevents: a stale Cancelled of the previous leg in
        // front of an otherwise perfect window is refused by the case rule...
        var contaminated = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Cancelled, abandoned, null, null, null, clock += 1)
        };
        contaminated.AddRange(RecoveryFacts(Guid.NewGuid()));
        Assert.Contains("instead of", TravelRecoveryReceipt.CheckRecoveredPlacement(contaminated, Session, System1, Station, Target));
        Assert.Contains("need the four-fact recovery window", TravelRecoveryReceipt.CheckRecoveryEvidence(contaminated,
            RecoverySnapshots(contaminated), TravelStationReceipt.Location(System1, Target), Acquisition()));
        // ...while the clean window that opens AFTER the native closure is accepted unchanged.
        var clean = RecoveryFacts(Guid.NewGuid());
        Assert.Null(TravelRecoveryReceipt.CheckRecoveredPlacement(clean, Session, System1, Station, Target));
        Assert.Null(TravelRecoveryReceipt.CheckRecoveryEvidence(clean, RecoverySnapshots(clean),
            TravelStationReceipt.Location(System1, Target), Acquisition()));
        // The attempt that had to close an abandoned leg still reports a miss outcome, and an
        // unprovable closure reports its own terminal outcome instead.
        Assert.Contains(TravelRecoveryReceipt.AttemptRouteEndedOutcome, TravelRecoveryReceipt.AttemptMissOutcomes);
        Assert.DoesNotContain(TravelRecoveryReceipt.AttemptUnclosedOutcome, TravelRecoveryReceipt.AttemptMissOutcomes);
    }

    // --- post-gate continuation --------------------------------------------------------------

    private static TravelCrossSystemReceipt.ExpectedLeg[] ContinuationLegs() => new[]
    {
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, System1, Station, System1, Gate1, System1, Gate1),
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.JumpGate, System1, Gate1, System2, Gate2, System2, Gate2),
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, System2, Gate2, System2, Follow, System2, Follow)
    };

    // The genuine native stream of one multi-waypoint route: approach leg, gate hop, post-gate leg,
    // and exactly one RouteCompleted at the end.
    private static List<TravelTransition> ContinuationFacts(out Guid[] operations)
    {
        var approach = Guid.NewGuid();
        var jump = Guid.NewGuid();
        var post = Guid.NewGuid();
        operations = new[] { approach, jump, post };
        double clock = 0;
        return new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, approach, null, (System1, Gate1), null, clock += 1),
            Fact(TravelTransitionKind.Departed, approach, (System1, Station), null, null, clock += 1),
            Fact(TravelTransitionKind.Arrived, approach, (System1, Station), (System1, Gate1), (System1, Gate1), clock += 1),
            Fact(TravelTransitionKind.Requested, jump, null, (System2, Gate2), null, clock += 1, mode: TravelMode.JumpGate),
            Fact(TravelTransitionKind.Departed, jump, (System1, Gate1), null, null, clock += 1, mode: TravelMode.JumpGate),
            Fact(TravelTransitionKind.Arrived, jump, (System1, Gate1), (System2, Gate2), (System2, Gate2), clock += 1, mode: TravelMode.JumpGate),
            Fact(TravelTransitionKind.Requested, post, null, (System2, Follow), null, clock += 1),
            Fact(TravelTransitionKind.Departed, post, (System2, Gate2), null, null, clock += 1),
            Fact(TravelTransitionKind.Arrived, post, (System2, Gate2), (System2, Follow), (System2, Follow), clock += 1),
            Fact(TravelTransitionKind.RouteCompleted, post, null, null, (System2, Follow), clock += 1)
        };
    }

    private static Dictionary<long, TravelRecoveryReceipt.NativeSnapshot> ContinuationSnapshots(IReadOnlyList<TravelTransition> facts)
    {
        var result = new Dictionary<long, TravelRecoveryReceipt.NativeSnapshot>();
        for (int index = 0; index < facts.Count; index++)
        {
            bool jump = facts[index].Mode == TravelMode.JumpGate && facts[index].Kind != TravelTransitionKind.Requested;
            // One waypoint remains until the post-gate leg's own arrival consumes it.
            int waypoints = index >= 8 ? 0 : index >= 6 ? 1 : index >= 3 ? 1 : 2;
            bool completion = facts[index].Kind == TravelTransitionKind.RouteCompleted;
            result[facts[index].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(
                currentPoiKnown: true, managerReady: true, travelActive: !completion, usingJumpgate: jump,
                waypoints: completion ? 0 : waypoints,
                locationKey: TravelStationReceipt.Location(index >= 5 ? System2 : System1, null), ownedByCase: true);
        }
        return result;
    }

    [Fact]
    public void TheContinuationIsThreeLegsWithExactlyOneRouteCompletionAtTheEnd()
    {
        var facts = ContinuationFacts(out var operations);
        Assert.Null(TravelRecoveryReceipt.CheckContinuation(facts, Session, ContinuationLegs()));
        Assert.Null(TravelRecoveryReceipt.CheckContinuationEvidence(facts, ContinuationSnapshots(facts)));
        Assert.Equal(3, operations.Distinct().Count());
    }

    [Fact]
    public void ARouteCompletedAtTheGateArrivalIsRefused()
    {
        var facts = ContinuationFacts(out var operations);
        // The exact defect this case exists to exclude: the jump arrival closed the route although
        // the native route still had a follow-on waypoint.
        facts.Insert(6, Fact(TravelTransitionKind.RouteCompleted, operations[1], null, null, (System2, Gate2), 6.5,
            mode: TravelMode.JumpGate, sequence: facts[5].Sequence + 1));
        Assert.Contains("instead of", TravelRecoveryReceipt.CheckContinuation(facts, Session, ContinuationLegs()));
        // And a route whose only completion belongs to the gate hop is refused as well.
        var truncated = ContinuationFacts(out var shortOperations).Take(6).ToList();
        truncated.Add(Fact(TravelTransitionKind.RouteCompleted, shortOperations[1], null, null, (System2, Gate2), 7, mode: TravelMode.JumpGate));
        Assert.NotNull(TravelRecoveryReceipt.CheckContinuation(truncated, Session, ContinuationLegs()));
    }

    [Fact]
    public void AContinuationNeedsTheGateArrivalToStillHaveARemainingNativeWaypoint()
    {
        var facts = ContinuationFacts(out _);
        var snapshots = ContinuationSnapshots(facts);
        var gateArrival = facts.First(fact => fact.Mode == TravelMode.JumpGate && fact.Kind == TravelTransitionKind.Arrived);
        snapshots[gateArrival.Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, true, 0,
            TravelStationReceipt.Location(System2, Gate2), true);
        Assert.Contains("no remaining native waypoint", TravelRecoveryReceipt.CheckContinuationEvidence(facts, snapshots));
        // The gate hop must have been observed inside the native jump routine.
        var outsideJump = ContinuationSnapshots(facts);
        outsideJump[gateArrival.Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, false, 1,
            TravelStationReceipt.Location(System2, Gate2), true);
        Assert.Contains("not observed inside the native jump routine",
            TravelRecoveryReceipt.CheckContinuationEvidence(facts, outsideJump));
        // The post-gate leg must be observed OUTSIDE it.
        var stillJumping = ContinuationSnapshots(facts);
        stillJumping[facts[7].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, true, 1,
            TravelStationReceipt.Location(System2, Follow), true);
        Assert.Contains("post-gate in-system fact was observed inside the native jump routine",
            TravelRecoveryReceipt.CheckContinuationEvidence(facts, stillJumping));
        // The completion must be observed at the real end of the native route.
        var early = ContinuationSnapshots(facts);
        early[facts[9].Sequence] = new TravelRecoveryReceipt.NativeSnapshot(true, true, true, false, 1,
            TravelStationReceipt.Location(System2, Follow), true);
        Assert.Contains("before the native route really ended", TravelRecoveryReceipt.CheckContinuationEvidence(facts, early));
    }

    [Fact]
    public void TheContinuationRefusesAShapeThatIsNotGateThenInSystem()
    {
        var facts = ContinuationFacts(out _);
        var twoLegs = ContinuationLegs().Take(2).ToArray();
        Assert.Contains("expects an in-system approach", TravelRecoveryReceipt.CheckContinuation(facts, Session, twoLegs));
        var reordered = new[] { ContinuationLegs()[1], ContinuationLegs()[0], ContinuationLegs()[2] };
        Assert.Contains("expects an in-system approach", TravelRecoveryReceipt.CheckContinuation(facts, Session, reordered));
    }

    // --- phase evaluation --------------------------------------------------------------------

    private static List<TravelStationReceipt.Row> CaseRowsOnly() => TravelRecoveryReceipt.RequiredCases
        .Select((id, index) => new TravelStationReceipt.Row(id, "description", TravelStationReceipt.Passed, "identity",
            Session.ToString(), Guid.NewGuid().ToString(), "travel:" + (index + 1), "detail"))
        .ToList();

    private static TravelStationReceipt.Row AttemptRow(string detail) => new(TravelRecoveryReceipt.RecoveryAttemptRow,
        "description", TravelStationReceipt.NotRun, "", Session.ToString(), "", "", detail);

    // A complete receipt always carries the persisted attempt rows next to the case rows.
    private static List<TravelStationReceipt.Row> PassingRows()
    {
        var rows = CaseRowsOnly();
        rows.Add(AttemptRow(TravelRecoveryReceipt.DescribeAttempt(1, Target, TravelRecoveryReceipt.AttemptCancelledOutcome)));
        return rows;
    }

    private static List<string> Trace() => TravelRecoveryReceipt.RequiredCases
        .Select((id, index) => (index + 1) + "\ttravel\t" + id + "\t" + Session + "\t\tArrived\tInSystem\t\t\t\t1.000\t")
        .ToList();

    [Fact]
    public void EveryRequiredCaseMustPassWithResolvableEvidence()
    {
        Assert.Null(TravelRecoveryReceipt.Evaluate(PassingRows(), null, Trace()));
        var notRun = PassingRows();
        notRun[0] = new TravelStationReceipt.Row(notRun[0].Case, "description", TravelStationReceipt.NotRun, "", Session.ToString(), "", "", "no window");
        Assert.Contains("is not-run", TravelRecoveryReceipt.Evaluate(notRun, null, Trace()));
        var missing = PassingRows().Where(row => row.Case != TravelRecoveryReceipt.ContinuationCase).ToList();
        Assert.Contains("did not run", TravelRecoveryReceipt.Evaluate(missing, null, Trace()));
        var duplicated = PassingRows();
        duplicated.Add(duplicated[0]);
        Assert.Contains("recorded 2 rows", TravelRecoveryReceipt.Evaluate(duplicated, null, Trace()));
        Assert.Contains("Pilot fault", TravelRecoveryReceipt.Evaluate(PassingRows(), "boom", Trace()));
        Assert.Contains("empty coverage", TravelRecoveryReceipt.Evaluate(new List<TravelStationReceipt.Row>(), null, Trace()));
        // Evidence that is not in the trace of the row's own session is refused.
        Assert.Contains("not in the trace", TravelRecoveryReceipt.Evaluate(PassingRows(), null,
            Trace().Select(row => row.Replace(Session.ToString(), Guid.NewGuid().ToString())).ToList()));
    }

    [Fact]
    public void TheSummaryNeverClaimsRuntimeQualificationOrTheOtherPhasesCoverage()
    {
        var summary = TravelRecoveryReceipt.Summarize(PassingRows(), null, Trace());
        Assert.StartsWith("PASS", summary);
        Assert.Contains("phase=" + TravelRecoveryReceipt.Phase, summary);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.Contains("recovery-attempts=" + TravelRecoveryReceipt.RecoveryAttempts, summary);
        Assert.Contains("recovery-attempts=" + TravelRecoveryReceipt.RecoveryAttempts,
            TravelRecoveryReceipt.SummarizeIncomplete(PassingRows(), TravelRecoveryReceipt.RecoveredPlacementCase));
        Assert.Contains("does not widen " + TravelStationReceipt.Phase, summary);
        Assert.Contains(TravelResilienceReceipt.Phase, summary);
        Assert.Contains("INCOMPLETE", TravelRecoveryReceipt.SummarizeIncomplete(PassingRows(), TravelRecoveryReceipt.RecoveredPlacementCase));
        Assert.DoesNotContain("PASS", TravelRecoveryReceipt.SummarizeIncomplete(PassingRows(), "phase-start"));
        // The phase's case identities never collide with the phases it must not widen.
        Assert.Empty(TravelRecoveryReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.Empty(TravelRecoveryReceipt.RequiredCases.Intersect(TravelCrossSystemReceipt.RequiredCases));
        Assert.Empty(TravelRecoveryReceipt.RequiredCases.Intersect(TravelResilienceReceipt.RequiredCases));
    }

    [Fact]
    public void TheDeclaredBudgetIsDerivedFromTheDeadlinesAndFitsTheReservation()
    {
        Assert.Equal(TravelRecoveryReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences), TravelRecoveryReceipt.PhaseBudgetSeconds);
        Assert.True(TravelRecoveryReceipt.PhaseBudgetSeconds > 0);
        Assert.True(TravelRecoveryReceipt.PhaseBudgetSeconds <= TravelRecoveryReceipt.LauncherReservationSeconds);
        Assert.All(TravelRecoveryReceipt.PhaseWaits, wait => Assert.True(wait.Seconds > 0 && wait.Occurrences > 0));
        // The bounded recovery attempts are declared in the plan, not implicit: every attempt is a
        // complete real route with its own departure, window sampling and route boundary.
        Assert.Equal(TravelRecoveryReceipt.RecoveryAttempts + 3, TravelRecoveryReceipt.DepartureWaits);
        Assert.Equal(TravelRecoveryReceipt.RecoveryAttempts + 2, TravelRecoveryReceipt.ArrivalWaits);
        Assert.Equal(TravelRecoveryReceipt.RecoveryAttempts + 1, TravelRecoveryReceipt.BoundaryWaits);
        Assert.Contains(TravelRecoveryReceipt.PhaseWaits, wait => wait.Name == "readiness-placement"
            && wait.Seconds == TravelRecoveryReceipt.PlacementSeconds);
    }

    [Fact]
    public void AMissedNativeWindowIsRecordedPerAttemptRatherThanSmoothedOver()
    {
        var described = TravelRecoveryReceipt.DescribeAttempt(2, Target, "the native arrival ran before the readiness window could be sampled");
        Assert.Equal("attempt2={target=" + Target + ",outcome=the native arrival ran before the readiness window could be sampled}", described);
    }
}
