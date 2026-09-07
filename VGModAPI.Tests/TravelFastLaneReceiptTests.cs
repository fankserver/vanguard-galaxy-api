using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the native FAST-LANE pilot uses to decide PASS/FAIL. Each
/// test reproduces a concrete defect a passing compilation would otherwise hide: a chain that never
/// crossed two gates, a route completed at a gate arrival, a "fast lane" claimed from the unlock
/// flag or from a route containing gates rather than from the native multiplier the charge branch
/// sets, a 7x that never went away again, an unsafe gate or destination, and a receipt with a
/// fabricated, unknown or duplicated case identity.
/// </summary>
public sealed class TravelFastLaneReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string SystemA = "system-a";
    private const string SystemB = "system-b";
    private const string SystemC = "system-c";
    private const string Station = "station-a";
    private const string GateA = "gate-a";
    private const string GateB1 = "gate-b1";
    private const string GateB2 = "gate-b2";
    private const string GateC1 = "gate-c1";
    private const string Destination = "poi-destination";
    private static long _sequence;

    private static TravelLocation At(string system, string? poi) => new(system, poi, system, poi);

    private static TravelTransition Fact(TravelTransitionKind kind, Guid? operation,
        (string System, string? Poi)? origin, (string System, string? Poi)? requested, (string System, string? Poi)? actual,
        double seconds, TravelMode mode = TravelMode.InSystem, Guid? session = null, long? sequence = null)
        => new(session ?? Session, operation, sequence ?? ++_sequence, kind, mode,
            origin == null ? null : At(origin.Value.System, origin.Value.Poi),
            requested == null ? null : At(requested.Value.System, requested.Value.Poi),
            actual == null ? null : At(actual.Value.System, actual.Value.Poi), seconds, null);

    private static TravelCrossSystemReceipt.ExpectedLeg[] ChainLegs() => new[]
    {
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, SystemA, Station, SystemA, GateA, SystemA, GateA),
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.JumpGate, SystemA, GateA, SystemB, GateB1, SystemB, GateB1),
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, SystemB, GateB1, SystemB, GateB2, SystemB, GateB2),
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.JumpGate, SystemB, GateB2, SystemC, GateC1, SystemC, GateC1),
        new TravelCrossSystemReceipt.ExpectedLeg(TravelMode.InSystem, SystemC, GateC1, SystemC, Destination, SystemC, Destination)
    };

    // The genuine native stream of the two-gate chain: five legs, one RouteCompleted at the end.
    private static List<TravelTransition> ChainFacts(out Guid[] operations)
    {
        var legs = ChainLegs();
        operations = legs.Select(_ => Guid.NewGuid()).ToArray();
        double clock = 0;
        var facts = new List<TravelTransition>();
        for (int index = 0; index < legs.Length; index++)
        {
            var leg = legs[index];
            var operation = operations[index];
            facts.Add(Fact(TravelTransitionKind.Requested, operation, null, (leg.RequestedSystem, leg.RequestedPoi), null, clock += 1, leg.Mode));
            facts.Add(Fact(TravelTransitionKind.Departed, operation, (leg.OriginSystem, leg.OriginPoi), null, null, clock += 1, leg.Mode));
            facts.Add(Fact(TravelTransitionKind.Arrived, operation, (leg.OriginSystem, leg.OriginPoi),
                (leg.RequestedSystem, leg.RequestedPoi), (leg.ActualSystem, leg.ActualPoi), clock += 1, leg.Mode));
        }
        facts.Add(Fact(TravelTransitionKind.RouteCompleted, operations[^1], null, null, (SystemC, Destination), clock += 1));
        return facts;
    }

    // Snapshots of a healthy run: the multiplier is 7 only on the gate-to-gate leg, the jump legs
    // are inside the native jump routine, and the route ends quiet with the transient reset.
    private static Dictionary<long, TravelFastLaneReceipt.NativeSnapshot> ChainSnapshots(IReadOnlyList<TravelTransition> facts)
    {
        var result = new Dictionary<long, TravelFastLaneReceipt.NativeSnapshot>();
        for (int index = 0; index < facts.Count; index++)
        {
            int leg = index / 3;
            bool completion = facts[index].Kind == TravelTransitionKind.RouteCompleted;
            bool fastLane = !completion && leg == TravelFastLaneReceipt.FastLaneLegIndex;
            bool jump = !completion && facts[index].Mode == TravelMode.JumpGate
                && facts[index].Kind != TravelTransitionKind.Requested;
            float multiplier = fastLane ? TravelFastLaneReceipt.FastLaneMultiplier : TravelFastLaneReceipt.NormalMultiplier;
            result[facts[index].Sequence] = new TravelFastLaneReceipt.NativeSnapshot(
                currentPoiKnown: true, managerReady: true, travelActive: !completion, usingJumpgate: jump,
                multiplier: multiplier, fastLaneActive: multiplier > TravelFastLaneReceipt.NormalMultiplier,
                waypoints: completion ? 0 : Math.Max(0, 3 - leg), locationKey: TravelStationReceipt.Location(SystemB, null),
                ownedByCase: true);
        }
        return result;
    }

    private static TravelFastLaneReceipt.NativeSnapshot Snapshot(TravelFastLaneReceipt.NativeSnapshot source,
        float? multiplier = null, bool? fastLaneActive = null, bool? usingJumpgate = null, int? waypoints = null,
        bool? travelActive = null, bool? owned = null, bool? managerReady = null)
        => new(source.CurrentPoiKnown, managerReady ?? source.ManagerReady, travelActive ?? source.TravelActive,
            usingJumpgate ?? source.UsingJumpgate, multiplier ?? source.Multiplier,
            fastLaneActive ?? (multiplier.HasValue ? multiplier.Value > TravelFastLaneReceipt.NormalMultiplier : source.FastLaneActive),
            waypoints ?? source.Waypoints, source.LocationKey, owned ?? source.OwnedByCase);

    // --- route shape -------------------------------------------------------------------------

    [Fact]
    public void TheChainIsFiveLegsAcrossTwoGatesWithOneFinalCompletion()
    {
        var facts = ChainFacts(out var operations);
        Assert.Equal(TravelFastLaneReceipt.RouteFacts, facts.Count);
        Assert.Equal(TravelFastLaneReceipt.RouteLegs, operations.Distinct().Count());
        Assert.Null(TravelFastLaneReceipt.CheckGateChain(facts, Session, ChainLegs()));
        Assert.Null(TravelFastLaneReceipt.CheckFastLaneEvidence(facts, ChainSnapshots(facts), out float observed));
        Assert.Equal(TravelFastLaneReceipt.FastLaneMultiplier, observed);
    }

    [Fact]
    public void AShapeThatIsNotGateGateChainIsRefused()
    {
        var facts = ChainFacts(out _);
        // The post-gate continuation shape (one gate) is NOT this case.
        Assert.Contains("expects an in-system approach", TravelFastLaneReceipt.CheckGateChain(facts, Session, ChainLegs().Take(3).ToArray()));
        var reordered = ChainLegs();
        (reordered[1], reordered[2]) = (reordered[2], reordered[1]);
        Assert.Contains("expects an in-system approach", TravelFastLaneReceipt.CheckGateChain(facts, Session, reordered));
    }

    [Fact]
    public void ARouteCompletedBeforeTheFinalLegIsRefused()
    {
        var facts = ChainFacts(out var operations);
        // The exact defect: the first gate arrival closed the route although two waypoints remained.
        facts.Insert(6, Fact(TravelTransitionKind.RouteCompleted, operations[1], null, null, (SystemB, GateB1), 6.5,
            TravelMode.JumpGate, sequence: facts[5].Sequence + 1));
        Assert.Contains("instead of", TravelFastLaneReceipt.CheckGateChain(facts, Session, ChainLegs()));
        // A completion belonging to a gate hop is refused even in the right position.
        var hijacked = ChainFacts(out var ops);
        hijacked[^1] = Fact(TravelTransitionKind.RouteCompleted, ops[3], null, null, (SystemC, Destination), 99,
            TravelMode.JumpGate, sequence: hijacked[^1].Sequence);
        Assert.NotNull(TravelFastLaneReceipt.CheckGateChain(hijacked, Session, ChainLegs()));
    }

    // --- fast-lane proof ---------------------------------------------------------------------

    [Fact]
    public void TheFastLaneProofIsTheNativeMultiplierOnTheGateToGateLeg()
    {
        var facts = ChainFacts(out _);
        var legs = TravelFastLaneReceipt.Legs(facts);
        var fastLane = legs[TravelFastLaneReceipt.FastLaneLegIndex];
        // The exact defect this case exists to exclude: the chain ran, the save carries the unlock
        // flag and the route contained gates, but the native charge branch never engaged.
        foreach (var fact in fastLane)
        {
            var snapshots = ChainSnapshots(facts);
            snapshots[fact.Sequence] = Snapshot(snapshots[fact.Sequence], multiplier: TravelFastLaneReceipt.NormalMultiplier);
            Assert.Contains("the native fast-lane charge branch did not run for it",
                TravelFastLaneReceipt.CheckFastLaneEvidence(facts, snapshots, out _));
        }
        // A multiplier that is neither the resting value nor the charge branch's is refused too.
        var wrong = ChainSnapshots(facts);
        wrong[fastLane[1].Sequence] = Snapshot(wrong[fastLane[1].Sequence], multiplier: 3);
        Assert.Contains("instead of 7", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, wrong, out _));
        // The gate-to-gate leg must be an in-system leg outside the jump routine.
        var inJump = ChainSnapshots(facts);
        inJump[fastLane[0].Sequence] = Snapshot(inJump[fastLane[0].Sequence], usingJumpgate: true);
        Assert.Contains("observed inside the native jump routine", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, inJump, out _));
        // It must still have a queued waypoint, otherwise it was not a chain leg at all.
        var lastLeg = ChainSnapshots(facts);
        lastLeg[fastLane[0].Sequence] = Snapshot(lastLeg[fastLane[0].Sequence], waypoints: 0);
        Assert.Contains("no remaining native waypoint", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, lastLeg, out _));
    }

    [Fact]
    public void TheSevenTimesStateMustBeATransientAroundThatLegOnly()
    {
        var facts = ChainFacts(out _);
        var legs = TravelFastLaneReceipt.Legs(facts);
        // A world that is permanently in fast lane proves nothing about the branch: the approach leg
        // before it and the final leg after it must be observed at the resting multiplier.
        foreach (var leg in new[] { legs[0], legs[TravelFastLaneReceipt.RouteLegs - 1] })
        {
            var snapshots = ChainSnapshots(facts);
            snapshots[leg[0].Sequence] = Snapshot(snapshots[leg[0].Sequence], multiplier: TravelFastLaneReceipt.FastLaneMultiplier);
            Assert.Contains("is not the transient the charge branch sets",
                TravelFastLaneReceipt.CheckFastLaneEvidence(facts, snapshots, out _));
        }
        // The transient must be gone at the route boundary.
        var boundary = ChainSnapshots(facts);
        boundary[facts[^1].Sequence] = Snapshot(boundary[facts[^1].Sequence], multiplier: TravelFastLaneReceipt.FastLaneMultiplier);
        Assert.Contains("must be reset by then", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, boundary, out _));
        // The native flag and the multiplier are read from the same manager and must agree.
        var inconsistent = ChainSnapshots(facts);
        inconsistent[facts[0].Sequence] = Snapshot(inconsistent[facts[0].Sequence],
            multiplier: TravelFastLaneReceipt.NormalMultiplier, fastLaneActive: true);
        Assert.Contains("flag and multiplier disagree", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, inconsistent, out _));
    }

    [Fact]
    public void TheChainEvidenceNeedsOwnedSnapshotsAndTheJumpRoutineOnItsGateHops()
    {
        var facts = ChainFacts(out _);
        var foreign = ChainSnapshots(facts);
        foreign[facts[0].Sequence] = Snapshot(foreign[facts[0].Sequence], owned: false);
        Assert.Contains("not the instance this case captured", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, foreign, out _));
        var outsideJump = ChainSnapshots(facts);
        outsideJump[facts[4].Sequence] = Snapshot(outsideJump[facts[4].Sequence], usingJumpgate: false);
        Assert.Contains("not observed inside the native jump routine", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, outsideJump, out _));
        var running = ChainSnapshots(facts);
        running[facts[^1].Sequence] = Snapshot(running[facts[^1].Sequence], travelActive: true);
        Assert.Contains("before the native route really ended", TravelFastLaneReceipt.CheckFastLaneEvidence(facts, running, out _));
        Assert.Contains("No native snapshot", TravelFastLaneReceipt.CheckFastLaneEvidence(facts,
            new Dictionary<long, TravelFastLaneReceipt.NativeSnapshot>(), out _));
        Assert.Contains("five-leg window", TravelFastLaneReceipt.CheckFastLaneEvidence(facts.Take(7).ToList(),
            ChainSnapshots(facts), out _));
    }

    // --- fixture selection -------------------------------------------------------------------

    [Fact]
    public void OnlySafeUsableNonTutorialGatesMayBeTraversed()
    {
        TravelFastLaneReceipt.GateCandidate Gate(bool usable = true, bool hidden = false, bool dynamic = false,
            bool tutorial = false, bool hostile = false, bool story = false, int guards = 0,
            bool leaves = true, bool paired = true)
            => new(GateA, usable, hidden, dynamic, tutorial, hostile, story, guards, leaves, paired);
        Assert.Null(TravelFastLaneReceipt.RefuseFastLaneGate(Gate()));
        Assert.Equal("the native gate cannot be used", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(usable: false)));
        Assert.Equal("hidden", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(hidden: true)));
        Assert.Equal("dynamic-event POI", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(dynamic: true)));
        Assert.Equal("the one-way tutorial exit gate", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(tutorial: true)));
        Assert.Equal("the gate does not leave its own system", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(leaves: false)));
        Assert.Equal("the gate declares no paired target POI", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(paired: false)));
        Assert.Equal("owned by a faction hostile to the player", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(hostile: true)));
        Assert.Equal("story-mission location", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(story: true)));
        Assert.Equal("persisted guard descriptors (2)", TravelFastLaneReceipt.RefuseFastLaneGate(Gate(guards: 2)));
    }

    // --- phase evaluation --------------------------------------------------------------------

    private static List<TravelStationReceipt.Row> PassingRows() => TravelFastLaneReceipt.RequiredCases
        .Select((id, index) => new TravelStationReceipt.Row(id, "description", TravelStationReceipt.Passed, "identity",
            Session.ToString(), Guid.NewGuid().ToString(), "travel:" + (index + 1), "detail"))
        .ToList();

    private static List<string> Trace() => TravelFastLaneReceipt.RequiredCases
        .Select((id, index) => (index + 1) + "\ttravel\t" + id + "\t" + Session + "\t\tArrived\tInSystem\t\t\t\t1.000\t")
        .ToList();

    [Fact]
    public void EveryRequiredCaseMustPassAndNoForeignCaseMayAppear()
    {
        Assert.Null(TravelFastLaneReceipt.Evaluate(PassingRows(), null, Trace()));
        var notRun = PassingRows();
        notRun[0] = new TravelStationReceipt.Row(notRun[0].Case, "description", TravelStationReceipt.NotRun, "",
            Session.ToString(), "", "", "no two-gate chain");
        Assert.Contains("is not-run", TravelFastLaneReceipt.Evaluate(notRun, null, Trace()));
        var missing = PassingRows().Where(row => row.Case != TravelFastLaneReceipt.MultiplierCase).ToList();
        Assert.Contains("did not run", TravelFastLaneReceipt.Evaluate(missing, null, Trace()));
        var duplicated = PassingRows();
        duplicated.Add(duplicated[0]);
        Assert.Contains("recorded 2 rows", TravelFastLaneReceipt.Evaluate(duplicated, null, Trace()));
        // A fabricated or renamed identity is refused outright: this phase owns exactly two rows.
        var extra = PassingRows();
        extra.Add(new TravelStationReceipt.Row("fast-lane-bonus", "description", TravelStationReceipt.Passed, "",
            Session.ToString(), "", "travel:1", "detail"));
        Assert.Contains("Unknown case identity", TravelFastLaneReceipt.Evaluate(extra, null, Trace()));
        Assert.Contains("empty coverage", TravelFastLaneReceipt.Evaluate(new List<TravelStationReceipt.Row>(), null, Trace()));
        Assert.Contains("Pilot fault", TravelFastLaneReceipt.Evaluate(PassingRows(), "boom", Trace()));
        Assert.Contains("not in the trace", TravelFastLaneReceipt.Evaluate(PassingRows(), null,
            Trace().Select(row => row.Replace(Session.ToString(), Guid.NewGuid().ToString())).ToList()));
    }

    [Fact]
    public void TheSummaryPublishesTheProofAndClaimsNoOtherPhasesCoverage()
    {
        var summary = TravelFastLaneReceipt.Summarize(PassingRows(), null, Trace());
        Assert.StartsWith("PASS", summary);
        Assert.Contains("phase=" + TravelFastLaneReceipt.Phase, summary);
        Assert.Contains("fast-lane-multiplier=7", summary);
        Assert.Contains("only READ; no save, config or native flag is written", summary);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.Contains("does not widen " + TravelStationReceipt.Phase, summary);
        Assert.Contains(TravelRecoveryReceipt.Phase, summary);
        Assert.Contains("INCOMPLETE", TravelFastLaneReceipt.SummarizeIncomplete(PassingRows(), TravelFastLaneReceipt.GateChainCase));
        Assert.DoesNotContain("PASS", TravelFastLaneReceipt.SummarizeIncomplete(PassingRows(), "phase-start"));
        // The phase's case identities never collide with the phases it must not widen.
        Assert.Empty(TravelFastLaneReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.Empty(TravelFastLaneReceipt.RequiredCases.Intersect(TravelCrossSystemReceipt.RequiredCases));
        Assert.Empty(TravelFastLaneReceipt.RequiredCases.Intersect(TravelResilienceReceipt.RequiredCases));
        Assert.Empty(TravelFastLaneReceipt.RequiredCases.Intersect(TravelRecoveryReceipt.RequiredCases));
    }

    [Fact]
    public void TheDeclaredBudgetIsDerivedFromTheDriversOwnCallSitesAndFitsTheReservation()
    {
        Assert.Equal(TravelFastLaneReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences), TravelFastLaneReceipt.PhaseBudgetSeconds);
        Assert.True(TravelFastLaneReceipt.PhaseBudgetSeconds <= TravelFastLaneReceipt.LauncherReservationSeconds);
        Assert.All(TravelFastLaneReceipt.PhaseWaits, wait => Assert.True(wait.Seconds > 0 && wait.Occurrences > 0));
        // One route, no retries: three in-system arrivals, one handoff and one jump arrival per gate.
        Assert.Equal(TravelFastLaneReceipt.RouteLegs - TravelFastLaneReceipt.GateHops, TravelFastLaneReceipt.ArrivalWaits);
        Assert.Contains(TravelFastLaneReceipt.PhaseWaits, wait => wait.Name == "gate-handoff" && wait.Occurrences == TravelFastLaneReceipt.GateHops);
        Assert.Contains(TravelFastLaneReceipt.PhaseWaits, wait => wait.Name == "jump-arrival" && wait.Occurrences == TravelFastLaneReceipt.GateHops);
        Assert.Equal(1960, TravelFastLaneReceipt.PhaseBudgetSeconds);
    }
}
