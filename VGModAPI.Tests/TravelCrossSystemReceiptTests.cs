using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the native cross-system travel pilot uses to decide
/// PASS/FAIL. Each test reproduces a concrete defect a passing compilation would otherwise hide,
/// including the ones that would let a jump be "observed" from the in-system arrival path or let
/// this phase claim the in-system phase's coverage.
/// </summary>
public sealed class TravelCrossSystemReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string Origin = "system-1";
    private const string Target = "system-2";
    private const string Station = "station-1";
    private const string Gate = "gate-1";
    private const string PairedGate = "gate-2";
    private static long _sequence;

    private static TravelLocation At(string system, string? poi) => new(system, poi, system, poi);

    private static TravelTransition Fact(TravelTransitionKind kind, Guid? operation, TravelMode mode,
        (string System, string? Poi)? origin, (string System, string? Poi)? requested, (string System, string? Poi)? actual,
        double seconds, Guid? session = null, long? sequence = null)
        => new(session ?? Session, operation, sequence ?? ++_sequence, kind, mode,
            origin == null ? null : At(origin.Value.System, origin.Value.Poi),
            requested == null ? null : At(requested.Value.System, requested.Value.Poi),
            actual == null ? null : At(actual.Value.System, actual.Value.Poi), seconds, null);

    private static TravelCrossSystemReceipt.ExpectedLeg ApproachLeg()
        => new(TravelMode.InSystem, Origin, Station, Origin, Gate, Origin, Gate);

    private static TravelCrossSystemReceipt.ExpectedLeg CrossLeg(string actualSystem = Target, string? actualPoi = PairedGate)
        => new(TravelMode.JumpGate, Origin, Gate, Target, PairedGate, actualSystem, actualPoi);

    // The genuine native stream of one cross-system case: an in-system approach route to the gate,
    // then the jump route the owned iterator produces.
    private static List<TravelTransition> CrossSystemFacts(Guid approach, Guid cross,
        string actualSystem = Target, string? actualPoi = PairedGate)
    {
        double clock = 0;
        return new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, approach, TravelMode.InSystem, null, (Origin, Gate), null, clock += 1),
            Fact(TravelTransitionKind.Departed, approach, TravelMode.InSystem, (Origin, Station), null, null, clock += 1),
            Fact(TravelTransitionKind.Arrived, approach, TravelMode.InSystem, (Origin, Station), (Origin, Gate), (Origin, Gate), clock += 1),
            Fact(TravelTransitionKind.RouteCompleted, approach, TravelMode.InSystem, null, null, (Origin, Gate), clock += 1),
            Fact(TravelTransitionKind.Requested, cross, TravelMode.JumpGate, null, (Target, PairedGate), null, clock += 1),
            Fact(TravelTransitionKind.Departed, cross, TravelMode.JumpGate, (Origin, Gate), null, null, clock += 1),
            Fact(TravelTransitionKind.Arrived, cross, TravelMode.JumpGate, (Origin, Gate), (Target, PairedGate), (actualSystem, actualPoi), clock += 1),
            Fact(TravelTransitionKind.RouteCompleted, cross, TravelMode.JumpGate, null, null, (actualSystem, actualPoi), clock += 1)
        };
    }

    private static IReadOnlyList<IReadOnlyList<TravelCrossSystemReceipt.ExpectedLeg>> Routes(
        params TravelCrossSystemReceipt.ExpectedLeg[][] routes) => routes;

    // Snapshots for a healthy jump: only the cross-system facts are sampled inside the running
    // native jump iterator, and the arrival is at the mode's own initialized manager.
    private static Dictionary<long, TravelCrossSystemReceipt.NativeSnapshot> Snapshots(
        IEnumerable<TravelTransition> facts, string actualSystem = Target, string? actualPoi = PairedGate)
    {
        var result = new Dictionary<long, TravelCrossSystemReceipt.NativeSnapshot>();
        foreach (var fact in facts)
        {
            bool cross = fact.Mode == TravelMode.JumpGate && fact.Kind != TravelTransitionKind.RouteCompleted;
            result[fact.Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(cross, cross,
                cross ? TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.JumpGate) : "Behaviour.Travel.StationManager",
                TravelStationReceipt.Location(actualSystem, actualPoi), true);
        }
        return result;
    }

    private static TravelStationReceipt.Row Row(string id, string status, string evidence = "", Guid? session = null)
        => new(id, id + " description", status, "identity", (session ?? Session).ToString(), Guid.NewGuid().ToString(), evidence, "detail");

    private static string EventRow(string surface, long sequence, string caseLabel, Guid? session = null)
        => string.Join("\t", sequence, surface, caseLabel, session ?? Session, "", "Arrived", "JumpGate", "", "", "", "1.000", "");

    private static (List<TravelStationReceipt.Row> Rows, List<string> Events) CompleteReceipt()
    {
        var rows = new List<TravelStationReceipt.Row>();
        var events = new List<string>();
        long sequence = 0;
        foreach (var id in TravelCrossSystemReceipt.RequiredCases)
        {
            sequence++;
            rows.Add(Row(id, TravelStationReceipt.Passed, "travel:" + sequence));
            events.Add(EventRow("travel", sequence, id));
        }
        return (rows, events);
    }

    [Fact]
    public void GenuineTwoRouteCrossSystemStreamPasses()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(TravelCrossSystemReceipt.CheckRoutes(facts, Session, Routes(new[] { ApproachLeg() }, new[] { CrossLeg() })));
        Assert.Null(TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, Snapshots(facts), TravelMode.JumpGate));
    }

    [Fact]
    public void ASingleRouteStreamCannotSatisfyTheApproachAndTheJump()
    {
        // The defect: only the in-system approach ran (the ship never crossed the gate) but the
        // case still claims a cross-system hop.
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid()).Take(4).ToList();
        Assert.NotNull(TravelCrossSystemReceipt.CheckRoutes(facts, Session, Routes(new[] { ApproachLeg() }, new[] { CrossLeg() })));
        Assert.Contains("No fact of the cross-system mode", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, Snapshots(facts), TravelMode.JumpGate));
    }

    [Fact]
    public void ArrivalWithoutADepartureIsNotACrossSystemHop()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        facts.RemoveAt(5); // the jump's Departed
        Assert.Contains("instead of", TravelCrossSystemReceipt.CheckRoutes(facts, Session, Routes(new[] { ApproachLeg() }, new[] { CrossLeg() })));
    }

    [Fact]
    public void EachLegNeedsItsOwnOperationIdentity()
    {
        var shared = Guid.NewGuid();
        var facts = CrossSystemFacts(shared, shared);
        Assert.Contains("reused an earlier operation identity", TravelCrossSystemReceipt.CheckRoutes(facts, Session, Routes(new[] { ApproachLeg() }, new[] { CrossLeg() })));
    }

    [Fact]
    public void TheRawRequestedDestinationIsPreservedEvenWhenTheActualLocationDiffers()
    {
        // The tutorial exit rewrites the gate target in flight: the actual arrival may differ, but
        // the request must still carry the RAW gate guids, and the arrival must keep them.
        const string rewrittenSystem = "system-3";
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid(), rewrittenSystem, "poi-3");
        var routes = Routes(new[] { ApproachLeg() }, new[] { CrossLeg(rewrittenSystem, "poi-3") });
        Assert.Null(TravelCrossSystemReceipt.CheckRoutes(facts, Session, routes));
        // The defect: the reducer replaces the request with the actual destination.
        var rewritten = facts.ToList();
        rewritten[4] = Fact(TravelTransitionKind.Requested, facts[4].OperationId, TravelMode.JumpGate, null,
            (rewrittenSystem, "poi-3"), null, facts[4].GameSeconds, sequence: facts[4].Sequence);
        Assert.Contains("Leg requested", TravelCrossSystemReceipt.CheckRoutes(rewritten, Session, routes));
    }

    [Fact]
    public void ForeignSessionFactsAreRejectedNotIgnored()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        facts.Insert(4, Fact(TravelTransitionKind.Arrived, Guid.NewGuid(), TravelMode.JumpGate, (Origin, Gate),
            (Target, PairedGate), (Target, PairedGate), 4.5, session: Guid.NewGuid()));
        Assert.Contains("Foreign-session", TravelCrossSystemReceipt.CheckRoutes(facts, Session, Routes(new[] { ApproachLeg() }, new[] { CrossLeg() })));
    }

    [Fact]
    public void RouteCompletedMustBelongToTheFinalNativeJumpChain()
    {
        var approach = Guid.NewGuid();
        var facts = CrossSystemFacts(approach, Guid.NewGuid());
        var routes = Routes(new[] { ApproachLeg() }, new[] { CrossLeg() });
        var misattributed = facts.ToList();
        misattributed[7] = Fact(TravelTransitionKind.RouteCompleted, approach, TravelMode.JumpGate, null, null,
            (Target, PairedGate), facts[7].GameSeconds, sequence: facts[7].Sequence);
        Assert.Contains("RouteCompleted belongs to", TravelCrossSystemReceipt.CheckRoutes(misattributed, Session, routes));
        var wrongMode = facts.ToList();
        wrongMode[7] = Fact(TravelTransitionKind.RouteCompleted, facts[7].OperationId, TravelMode.InSystem, null, null,
            (Target, PairedGate), facts[7].GameSeconds, sequence: facts[7].Sequence);
        Assert.Contains("RouteCompleted reports mode", TravelCrossSystemReceipt.CheckRoutes(wrongMode, Session, routes));
    }

    [Fact]
    public void SequencesAndGameTimeMustNotMoveBackwards()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        var routes = Routes(new[] { ApproachLeg() }, new[] { CrossLeg() });
        var repeated = facts.ToList();
        repeated[6] = Fact(TravelTransitionKind.Arrived, facts[6].OperationId, TravelMode.JumpGate, (Origin, Gate),
            (Target, PairedGate), (Target, PairedGate), facts[6].GameSeconds, sequence: facts[5].Sequence);
        Assert.Contains("sequences are not strictly increasing", TravelCrossSystemReceipt.CheckRoutes(repeated, Session, routes));
    }

    [Fact]
    public void AnArrivalObservedOutsideTheRunningJumpIteratorIsNotCrossSystemEvidence()
    {
        // The defect this phase exists to exclude: an "arrival" attributed to the cross-system hop
        // that was actually observed through the in-system SpaceshipHasArrived path, which the
        // inspected JumpToSystem/JumpToWormhole routines never call.
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = Snapshots(facts);
        snapshots[facts[6].Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(false, true,
            TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.JumpGate), TravelStationReceipt.Location(Target, PairedGate), true);
        Assert.Contains("not observed from the running native jump iterator",
            TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, snapshots, TravelMode.JumpGate));
    }

    [Fact]
    public void TheJumpArrivalMustHappenAtTheModesOwnInitializedManager()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        var wrongManager = Snapshots(facts);
        wrongManager[facts[6].Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(true, true,
            TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.Wormhole), TravelStationReceipt.Location(Target, PairedGate), true);
        Assert.Contains("did not happen at an initialized", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, wrongManager, TravelMode.JumpGate));
        var notReady = Snapshots(facts);
        notReady[facts[6].Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(true, true,
            TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.JumpGate), TravelStationReceipt.Location(Target, PairedGate), false);
        Assert.Contains("did not happen at an initialized", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, notReady, TravelMode.JumpGate));
    }

    [Fact]
    public void TheJumpArrivalMustAgreeWithTheLoadedWorld()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = Snapshots(facts);
        snapshots[facts[6].Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(true, true,
            TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.JumpGate), TravelStationReceipt.Location(Origin, Gate), true);
        Assert.Contains("while the loaded world reports", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, snapshots, TravelMode.JumpGate));
    }

    [Fact]
    public void EmptySpaceArrivalsAreASupportedSnapshotLocation()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid(), Target, null);
        Assert.Null(TravelCrossSystemReceipt.CheckRoutes(facts, Session, Routes(new[] { ApproachLeg() }, new[] { CrossLeg(Target, null) })));
        Assert.Null(TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, Snapshots(facts, Target, null), TravelMode.JumpGate));
    }

    [Fact]
    public void AnInSystemFactObservedInsideAJumpIsRejected()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = Snapshots(facts);
        snapshots[facts[2].Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(true, true, "", TravelStationReceipt.Location(Origin, Gate), true);
        Assert.Contains("inside a running jump iterator", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, snapshots, TravelMode.JumpGate));
    }

    [Fact]
    public void EveryObservedFactNeedsItsOwnNativeSnapshot()
    {
        var facts = CrossSystemFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = Snapshots(facts);
        snapshots.Remove(facts[6].Sequence);
        Assert.Contains("No native snapshot was recorded", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, snapshots, TravelMode.JumpGate));
        Assert.Contains("Not a cross-system travel mode", TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, Snapshots(facts), TravelMode.InSystem));
        Assert.Throws<ArgumentOutOfRangeException>(() => TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.InSystem));
    }

    [Fact]
    public void BothCrossSystemCasesAreMandatory()
    {
        var (rows, events) = CompleteReceipt();
        Assert.Null(TravelCrossSystemReceipt.Evaluate(rows, null, events));
        foreach (var required in TravelCrossSystemReceipt.RequiredCases)
        {
            var missing = rows.Where(row => row.Case != required).ToList();
            Assert.Contains("Required case did not run: " + required, TravelCrossSystemReceipt.Evaluate(missing, null, events));
            var skipped = rows.Where(row => row.Case != required).Append(Row(required, TravelStationReceipt.NotRun)).ToList();
            Assert.Contains("Required case is not-run: " + required, TravelCrossSystemReceipt.Evaluate(skipped, null, events));
            var duplicated = rows.Append(Row(required, TravelStationReceipt.Passed, "travel:1")).ToList();
            Assert.Contains("Required case recorded 2 rows: " + required, TravelCrossSystemReceipt.Evaluate(duplicated, null, events));
        }
        Assert.Contains("empty coverage is not a pass", TravelCrossSystemReceipt.Evaluate(Array.Empty<TravelStationReceipt.Row>(), null, events));
        Assert.Contains("Failed cases", TravelCrossSystemReceipt.Evaluate(
            rows.Where(row => row.Case != TravelCrossSystemReceipt.WormholeCase)
                .Append(Row(TravelCrossSystemReceipt.WormholeCase, TravelStationReceipt.Failed)).ToList(), null, events));
        Assert.Contains("Pilot fault", TravelCrossSystemReceipt.Evaluate(rows, "boom", events));
    }

    [Fact]
    public void CaseEvidenceMustResolveInTheTraceForItsOwnSession()
    {
        var (rows, events) = CompleteReceipt();
        Assert.Contains("references an event that is not in the trace",
            TravelCrossSystemReceipt.Evaluate(rows, null, events.Take(1).ToList()));
        var foreign = events.Select(row => row.Replace(Session.ToString(), Guid.NewGuid().ToString())).ToList();
        Assert.NotNull(TravelCrossSystemReceipt.Evaluate(rows, null, foreign));
        var withoutEvidence = rows.Where(row => row.Case != TravelCrossSystemReceipt.WormholeCase)
            .Append(Row(TravelCrossSystemReceipt.WormholeCase, TravelStationReceipt.Passed)).ToList();
        Assert.Contains("has no observed public events", TravelCrossSystemReceipt.Evaluate(withoutEvidence, null, events));
        Assert.Contains("Malformed event row", TravelCrossSystemReceipt.Evaluate(rows, null, new List<string> { "one\ttwo" }));
    }

    [Fact]
    public void CheckpointsAreNeverAPassAndTheSummaryDeclaresThePhase()
    {
        var (rows, events) = CompleteReceipt();
        var checkpoint = TravelCrossSystemReceipt.SummarizeIncomplete(rows, TravelCrossSystemReceipt.WormholeCase);
        Assert.StartsWith(TravelStationReceipt.Incomplete, checkpoint);
        Assert.DoesNotContain("PASS", checkpoint);
        var summary = TravelCrossSystemReceipt.Summarize(rows, null, events);
        Assert.StartsWith("PASS", summary);
        Assert.Contains("phase=" + TravelCrossSystemReceipt.Phase, summary);
        Assert.Contains("budgetSeconds=" + TravelCrossSystemReceipt.PhaseBudgetSeconds.ToString("F0"), summary);
        Assert.Contains("required=" + string.Join(",", TravelCrossSystemReceipt.RequiredCases), summary);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.StartsWith("FAIL", TravelCrossSystemReceipt.Summarize(
            rows.Take(1).ToList(), null, events));
    }

    [Fact]
    public void FixturePreparationMustNotProduceAnyTravelFact()
    {
        // The defect: "preparing" a fixture by moving the ship, or a creation window that silently
        // swallows a request/departure/arrival, would turn setup into fake travel evidence.
        Assert.Null(TravelCrossSystemReceipt.CheckFixtureCreationWindow(Array.Empty<TravelTransition>()));
        foreach (var kind in new[] { TravelTransitionKind.Requested, TravelTransitionKind.Departed,
            TravelTransitionKind.Arrived, TravelTransitionKind.RouteCompleted })
        {
            var fact = Fact(kind, Guid.NewGuid(), TravelMode.Wormhole, (Origin, Station), (Target, PairedGate), (Target, PairedGate), 1);
            Assert.Contains("Fixture preparation is not travel", TravelCrossSystemReceipt.CheckFixtureCreationWindow(new[] { fact }));
        }
        var placement = Fact(TravelTransitionKind.InitialPlacement, null, TravelMode.Unknown, null, null, (Origin, Station), 1);
        Assert.NotNull(TravelCrossSystemReceipt.CheckFixtureCreationWindow(new[] { placement }));
    }

    [Fact]
    public void FixturePreparationMustCreateExactlyTheConnectedPairItClaims()
    {
        Assert.Null(TravelCrossSystemReceipt.CheckFixtureCreation(0, 2, Origin, Origin, "wh-1", Target, "wh-2"));
        Assert.Contains("instead of adding exactly two", TravelCrossSystemReceipt.CheckFixtureCreation(0, 1, Origin, Origin, "wh-1", Target, "wh-2"));
        Assert.Contains("instead of adding exactly two", TravelCrossSystemReceipt.CheckFixtureCreation(0, 3, Origin, Origin, "wh-1", Target, "wh-2"));
        Assert.Contains("two distinct native wormhole identities", TravelCrossSystemReceipt.CheckFixtureCreation(0, 2, Origin, Origin, "wh-1", Target, "wh-1"));
        Assert.Contains("two distinct native wormhole identities", TravelCrossSystemReceipt.CheckFixtureCreation(0, 2, Origin, Origin, "", Target, "wh-2"));
        Assert.Contains("two distinct native systems", TravelCrossSystemReceipt.CheckFixtureCreation(0, 2, Origin, Origin, "wh-1", Origin, "wh-2"));
        Assert.Contains("instead of the player's current system", TravelCrossSystemReceipt.CheckFixtureCreation(0, 2, Origin, Target, "wh-1", "system-3", "wh-2"));
        var detail = TravelCrossSystemReceipt.DescribeFixtureCreation(0, 2, Origin, "wh-1", Target, "wh-2", 0);
        Assert.Contains("factory=" + TravelCrossSystemReceipt.WormholeFactorySignature, detail);
        Assert.Contains("wormholesBefore=0; wormholesAfter=2", detail);
        Assert.Contains("travelFactsDuringCreation=0", detail);
        Assert.Contains("fixture preparation only, not travel evidence", detail);
    }

    [Fact]
    public void FixturePreparationIsNeverCoverageAndAMissingWormholeCaseStillFails()
    {
        // Flag off: the phase must keep reporting the honest mandatory NOT-RUN failure, and a
        // preparation row can never stand in for the wormhole case it prepared for.
        Assert.DoesNotContain(TravelCrossSystemReceipt.WormholeFixtureCase, TravelCrossSystemReceipt.RequiredCases);
        Assert.DoesNotContain(TravelCrossSystemReceipt.WormholeFixtureCase, TravelStationReceipt.RequiredCases);
        var (rows, events) = CompleteReceipt();
        var setup = Row(TravelCrossSystemReceipt.WormholeFixtureCase, TravelStationReceipt.Passed);
        var withoutWormhole = rows.Where(row => row.Case != TravelCrossSystemReceipt.WormholeCase).Append(setup).ToList();
        Assert.Contains("Required case did not run: " + TravelCrossSystemReceipt.WormholeCase,
            TravelCrossSystemReceipt.Evaluate(withoutWormhole, null, events));
        var skipped = rows.Where(row => row.Case != TravelCrossSystemReceipt.WormholeCase)
            .Append(Row(TravelCrossSystemReceipt.WormholeCase, TravelStationReceipt.NotRun)).Append(setup).ToList();
        Assert.Contains("Required case is not-run: " + TravelCrossSystemReceipt.WormholeCase,
            TravelCrossSystemReceipt.Evaluate(skipped, null, events));
        // With both mandatory cases genuinely passed, the extra preparation row is accepted and is
        // reported as an optional row, never as a required one.
        var complete = rows.Append(setup).ToList();
        Assert.Null(TravelCrossSystemReceipt.Evaluate(complete, null, events));
        var summary = TravelCrossSystemReceipt.Summarize(complete, null, events);
        Assert.Contains("rows=3 passed=3 failed=0 notRun=0", summary);
        Assert.DoesNotContain("required-case " + TravelCrossSystemReceipt.WormholeFixtureCase, summary);
        // A failed preparation fails the phase instead of being ignored.
        Assert.Contains("Failed cases: " + TravelCrossSystemReceipt.WormholeFixtureCase,
            TravelCrossSystemReceipt.Evaluate(rows.Append(Row(TravelCrossSystemReceipt.WormholeFixtureCase, TravelStationReceipt.Failed)).ToList(), null, events));
    }

    [Fact]
    public void ThePhaseBudgetIsSummedFromItsDeclaredDeadlinesAndFitsTheReservation()
    {
        Assert.Equal(TravelCrossSystemReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences), TravelCrossSystemReceipt.PhaseBudgetSeconds);
        Assert.True(TravelCrossSystemReceipt.PhaseBudgetSeconds > 0);
        Assert.True(TravelCrossSystemReceipt.PhaseBudgetSeconds <= TravelCrossSystemReceipt.LauncherReservationSeconds);
        Assert.Contains(TravelCrossSystemReceipt.PhaseWaits, wait => wait.Seconds == TravelCrossSystemReceipt.JumpArrivalSeconds);
        // The opt-in fixture creation adds its own bounded wait and settle to the published budget.
        Assert.Contains(TravelCrossSystemReceipt.PhaseWaits, wait => wait.Name == "wormhole-fixture-creation"
            && wait.Seconds == TravelCrossSystemReceipt.FixtureCreationSeconds && wait.Occurrences == 1);
    }

    [Fact]
    public void EveryPerCaseWaitIsDerivedFromTheCaseCountAndTheDriversCallSites()
    {
        // The defect this reproduces: the driver awaits a route boundary TWICE per case (after the
        // in-system approach and after the cross-system hop), but the plan hand-declared one per
        // case, so the published budget was 120s short and a worst-case run could be killed by the
        // launcher. Every occurrence is checked against the case count and the per-call-site
        // multiplicity here instead of a hand-copied number.
        int cases = TravelCrossSystemReceipt.RequiredCases.Length;
        Assert.Equal(2, cases);
        Assert.Equal(2, TravelCrossSystemReceipt.RouteBoundariesPerCase);
        Assert.Equal(2, TravelCrossSystemReceipt.LoadWaitsPerCase);
        Assert.Equal(3, TravelCrossSystemReceipt.SettlesPerCase);
        int Occurrences(string name) => Assert.Single(TravelCrossSystemReceipt.PhaseWaits, wait => wait.Name == name).Occurrences;
        Assert.Equal(TravelCrossSystemReceipt.LoadWaitsPerCase * cases + TravelCrossSystemReceipt.RestoringLoadWaits, Occurrences("fixture-load-and-binding"));
        Assert.Equal(cases, Occurrences("undock"));
        Assert.Equal(cases, Occurrences("travel-availability"));
        Assert.Equal(cases, Occurrences("approach-arrival"));
        Assert.Equal(cases, Occurrences("jump-handoff"));
        Assert.Equal(cases, Occurrences("jump-arrival"));
        // One approach boundary plus one cross-system boundary, for each case.
        Assert.Equal(4, Occurrences("route-boundary"));
        Assert.Equal(TravelCrossSystemReceipt.RouteBoundariesPerCase * cases, Occurrences("route-boundary"));
        Assert.Equal(1, Occurrences("wormhole-fixture-creation"));
        Assert.Equal(8, Occurrences("settle"));
        Assert.Equal(TravelCrossSystemReceipt.SettlesPerCase * cases + TravelCrossSystemReceipt.PhaseLevelSettles, Occurrences("settle"));
        // The published worst case and the reservation, both explicit so neither can drift.
        Assert.Equal(2184f, TravelCrossSystemReceipt.PhaseBudgetSeconds);
        Assert.Equal(2400f, TravelCrossSystemReceipt.LauncherReservationSeconds);
    }

    [Fact]
    public void TheCrossSystemPhaseNeverWidensTheInSystemPhase()
    {
        // The in-system phase keeps its own six mandatory cases and keeps recording the
        // cross-system cells as optional NOT-RUN rows: a passing cross-system receipt must never
        // stand in for them, and neither phase's rows can satisfy the other's required list.
        Assert.Equal(new[] { "initial-placement", "station-undock", "in-system-route", "early-cancel", "chained-route", "station-dock" },
            TravelStationReceipt.RequiredCases);
        Assert.Empty(TravelCrossSystemReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.NotEqual(TravelStationReceipt.Phase, TravelCrossSystemReceipt.Phase);
        var (crossRows, crossEvents) = CompleteReceipt();
        Assert.NotNull(TravelStationReceipt.Evaluate(crossRows, null, crossEvents));
        var stationRows = TravelStationReceipt.RequiredCases.Select((id, index) => Row(id, TravelStationReceipt.Passed, "travel:" + (index + 1))).ToList();
        var stationEvents = TravelStationReceipt.RequiredCases.Select((id, index) => EventRow("travel", index + 1, id)).ToList();
        Assert.Null(TravelStationReceipt.Evaluate(stationRows, null, stationEvents));
        Assert.NotNull(TravelCrossSystemReceipt.Evaluate(stationRows, null, stationEvents));
    }
}
