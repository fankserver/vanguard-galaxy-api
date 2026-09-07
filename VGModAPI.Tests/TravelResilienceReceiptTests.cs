using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the native travel RESILIENCE pilot uses to decide
/// PASS/FAIL. Each test reproduces a concrete defect a passing compilation would otherwise hide:
/// a cancellation that pretends the ship returned to its origin, a re-route departure that was not
/// observed at the actual native warp start, a restore/relink assignment that emitted a physical
/// dock fact, a stale replay that leaked into the replacement session, and a phase that claims a
/// pass without its three required cases.
/// </summary>
public sealed class TravelResilienceReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string System1 = "system-1";
    private const string Station = "station-1";
    private const string HopA = "poi-a";
    private const string HopB = "poi-b";
    private static long _sequence;

    private static TravelLocation At(string system, string? poi) => new(system, poi, system, poi);

    private static TravelTransition Fact(TravelTransitionKind kind, Guid? operation,
        (string System, string? Poi)? origin, (string System, string? Poi)? requested, (string System, string? Poi)? actual,
        double seconds, Guid? session = null, TravelMode mode = TravelMode.InSystem, long? sequence = null)
        => new(session ?? Session, operation, sequence ?? ++_sequence, kind, mode,
            origin == null ? null : At(origin.Value.System, origin.Value.Poi),
            requested == null ? null : At(requested.Value.System, requested.Value.Poi),
            actual == null ? null : At(actual.Value.System, actual.Value.Poi), seconds, null);

    private static StationTransition StationFact(StationTransitionKind kind, string? poi, Guid? session = null)
        => new(session ?? Session, ++_sequence, kind, poi == null ? null : At(System1, poi), 1);

    // The genuine native stream of the re-route case: a leg requested and departed at the loaded
    // origin, cancelled after that departure, then a new leg requested, departed from the unknown
    // origin, arrived and completed.
    private static List<TravelTransition> RerouteFacts(Guid abandoned, Guid rerouted)
    {
        double clock = 0;
        return new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, abandoned, null, (System1, HopA), null, clock += 1),
            Fact(TravelTransitionKind.Departed, abandoned, (System1, Station), null, null, clock += 1),
            Fact(TravelTransitionKind.Cancelled, abandoned, null, null, null, clock += 1),
            Fact(TravelTransitionKind.Requested, rerouted, null, (System1, HopB), null, clock += 1),
            Fact(TravelTransitionKind.Departed, rerouted, null, null, null, clock += 1),
            Fact(TravelTransitionKind.Arrived, rerouted, null, (System1, HopB), (System1, HopB), clock += 1),
            Fact(TravelTransitionKind.RouteCompleted, rerouted, null, null, (System1, HopB), clock += 1)
        };
    }

    // Snapshots for a healthy re-route: the first request is sampled at the loaded origin, every
    // later fact of the abandoned/re-routed pair while the origin is gone, and the re-routed
    // departure inside the running native warp loop.
    private static Dictionary<long, TravelResilienceReceipt.NativeSnapshot> RerouteSnapshots(IReadOnlyList<TravelTransition> facts)
    {
        var result = new Dictionary<long, TravelResilienceReceipt.NativeSnapshot>();
        for (int index = 0; index < facts.Count; index++)
        {
            bool loaded = index == 0 || index >= 5;
            bool travelling = index != 2;
            result[facts[index].Sequence] = new TravelResilienceReceipt.NativeSnapshot(loaded, loaded, travelling,
                index == 4, "Docked", TravelStationReceipt.Location(System1, loaded ? HopB : null), true);
        }
        return result;
    }

    private static string? CheckReroute(IReadOnlyList<TravelTransition> facts)
        => TravelResilienceReceipt.CheckReroute(facts, Session, System1, Station, HopA, HopB);

    private static TravelStationReceipt.Row Row(string id, string status, string evidence = "", Guid? session = null)
        => new(id, id + " description", status, "identity", (session ?? Session).ToString(), "", evidence, "detail");

    private static string EventRow(string surface, long sequence, Guid? session = null)
        => string.Join("\t", sequence, surface, "no-active-case", session ?? Session, "", "Departed", "InSystem", "", "", "", "1.000", "");

    private static (List<TravelStationReceipt.Row> Rows, List<string> Events) CompleteReceipt()
    {
        var rows = new List<TravelStationReceipt.Row>();
        var events = new List<string>();
        long sequence = 0;
        foreach (var id in TravelResilienceReceipt.RequiredCases)
        {
            sequence++;
            rows.Add(Row(id, TravelStationReceipt.Passed, "travel:" + sequence));
            events.Add(EventRow("travel", sequence));
        }
        // The mandatory subcase row is part of every complete receipt.
        foreach (var id in TravelResilienceReceipt.RequiredSubcaseRows)
            rows.Add(Row(id, TravelStationReceipt.Passed, "station:1"));
        events.Add(EventRow("station", 1));
        return (rows, events);
    }

    // --- empty-origin re-route ------------------------------------------------------------

    // --- accepted-request frame -----------------------------------------------------------

    [Fact]
    public void AnOrdinaryAcceptedRequestFrameIsAcceptedWithoutThrowing()
    {
        // The qa-83 probe defect: this healthy frame threw a NullReferenceException because the
        // driver formatted a failure message from a fact that does not exist on the success path.
        var accepted = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, (System1, HopA), null, 1)
        };
        Assert.Null(TravelResilienceReceipt.CheckRequestFrame(accepted, "the accepted request"));
        // A window that already carried an earlier cancelled leg still ends with the new request.
        var abandoned = Guid.NewGuid();
        var afterCancel = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Cancelled, abandoned, null, null, null, 1),
            Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, (System1, HopB), null, 2)
        };
        Assert.Null(TravelResilienceReceipt.CheckRequestFrame(afterCancel, "the accepted re-route request"));
    }

    [Fact]
    public void AnEmptyWronglyEndedOrTransportingRequestFrameFailsWithItsOwnDiagnostic()
    {
        var empty = TravelResilienceReceipt.CheckRequestFrame(Array.Empty<TravelTransition>(), "the accepted request");
        Assert.Contains("end the window with Requested", empty);
        Assert.Contains("observed []", empty);
        var wrongLast = TravelResilienceReceipt.CheckRequestFrame(new[]
        {
            Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, (System1, HopA), null, 1),
            Fact(TravelTransitionKind.Cancelled, Guid.NewGuid(), null, null, null, 2)
        }, "the accepted request");
        Assert.Contains("end the window with Requested", wrongLast);
        Assert.Contains("Cancelled", wrongLast);
        // A transport fact in the accepted-request frame could only be fabricated, even when the
        // window still ends with the request itself.
        foreach (var fabricated in new[] { TravelTransitionKind.Departed, TravelTransitionKind.Arrived, TravelTransitionKind.RouteCompleted })
        {
            var operation = Guid.NewGuid();
            var window = new[]
            {
                Fact(fabricated, operation, (System1, Station), (System1, HopA), (System1, HopA), 1),
                Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, (System1, HopA), null, 2)
            };
            var failure = TravelResilienceReceipt.CheckRequestFrame(window, "the accepted request");
            Assert.Contains("A transport fact was published for the accepted request", failure);
            Assert.Contains(fabricated.ToString(), failure);
        }
    }

    [Fact]
    public void GenuineEmptyOriginRerouteStreamPasses()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(CheckReroute(facts));
        Assert.Null(TravelResilienceReceipt.CheckRerouteEvidence(facts, RerouteSnapshots(facts)));
    }

    [Fact]
    public void CancellationAfterDepartureMayNotClaimTheOrigin()
    {
        // The defect: the ship already left, but the cancellation reports it back at its origin.
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        facts[2] = Fact(TravelTransitionKind.Cancelled, facts[2].OperationId, null, null, (System1, Station), 3,
            sequence: facts[2].Sequence);
        Assert.Contains("instead of an unknown location", CheckReroute(facts));
    }

    [Fact]
    public void TheRerouteDepartureMayNotReportAKnownOrigin()
    {
        // The defect: the empty-origin hop invents the origin it "left", which would make the
        // required boundary indistinguishable from an ordinary loaded-origin departure.
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        facts[4] = Fact(TravelTransitionKind.Departed, facts[4].OperationId, (System1, Station), null, null, 5,
            sequence: facts[4].Sequence);
        Assert.Contains("instead of the unknown origin", CheckReroute(facts));
    }

    [Fact]
    public void TheRerouteMayNotReuseTheAbandonedLegIdentity()
    {
        var operation = Guid.NewGuid();
        var facts = RerouteFacts(operation, operation);
        Assert.Contains("reused the abandoned leg's operation identity", CheckReroute(facts));
    }

    [Fact]
    public void ACancelledLegWithoutItsOwnDepartureIsNotThisCase()
    {
        // A cancel BEFORE any departure is the in-system phase's early-cancel case, not the
        // empty-origin boundary: the required kind sequence rejects it.
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        facts.RemoveAt(1);
        Assert.Contains("instead of", CheckReroute(facts));
    }

    [Fact]
    public void AForeignSessionFactInTheRerouteWindowIsRejectedNotFiltered()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        facts.Insert(3, Fact(TravelTransitionKind.Cancelled, Guid.NewGuid(), null, null, null, 3, Guid.NewGuid()));
        Assert.Contains("Foreign-session travel fact", CheckReroute(facts));
    }

    [Fact]
    public void ACrossSystemModeFactCannotSatisfyTheInSystemReroute()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        var departed = facts[4];
        facts[4] = new TravelTransition(departed.SessionId, departed.OperationId, departed.Sequence,
            TravelTransitionKind.Departed, TravelMode.JumpGate, null, null, null, departed.GameSeconds, null);
        Assert.Contains("observed mode JumpGate", CheckReroute(facts));
    }

    [Fact]
    public void TheRerouteDepartureMustBeSampledInsideTheRunningNativeWarpLoop()
    {
        // The defect: the departure was published during departure PREPARATION (no warp yet), which
        // is exactly the boundary the adapter contract forbids for this hop.
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = RerouteSnapshots(facts);
        snapshots[facts[4].Sequence] = new TravelResilienceReceipt.NativeSnapshot(false, false, true, false, "", "system-1:<empty space>", true);
        Assert.Contains("not observed at the actual native warp start",
            TravelResilienceReceipt.CheckRerouteEvidence(facts, snapshots));
    }

    [Fact]
    public void TheRerouteMustBeRequestedWhileTheOriginIsAlreadyUnloaded()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = RerouteSnapshots(facts);
        snapshots[facts[3].Sequence] = new TravelResilienceReceipt.NativeSnapshot(true, true, false, false, "", "system-1:station-1", true);
        Assert.Contains("while the native origin was still loaded",
            TravelResilienceReceipt.CheckRerouteEvidence(facts, snapshots));
    }

    [Fact]
    public void TheFirstLegMustHaveBeenRequestedFromALoadedOrigin()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = RerouteSnapshots(facts);
        snapshots[facts[0].Sequence] = new TravelResilienceReceipt.NativeSnapshot(false, false, false, false, "", "system-1:<empty space>", true);
        Assert.Contains("not requested from a loaded origin",
            TravelResilienceReceipt.CheckRerouteEvidence(facts, snapshots));
    }

    [Fact]
    public void AFactObservedInAForeignOrDestroyedWorldIsNotThisCasesEvidence()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = RerouteSnapshots(facts);
        var stale = snapshots[facts[5].Sequence];
        snapshots[facts[5].Sequence] = new TravelResilienceReceipt.NativeSnapshot(stale.CurrentPoiKnown, stale.LocalManagerAlive,
            stale.TravelActive, stale.Warping, stale.DockingState, stale.LocationKey, false);
        Assert.Contains("not the instance this case captured",
            TravelResilienceReceipt.CheckRerouteEvidence(facts, snapshots));
    }

    [Fact]
    public void EveryRerouteFactNeedsItsOwnNativeSnapshot()
    {
        var facts = RerouteFacts(Guid.NewGuid(), Guid.NewGuid());
        var snapshots = RerouteSnapshots(facts);
        snapshots.Remove(facts[6].Sequence);
        Assert.Contains("No native snapshot was recorded", TravelResilienceReceipt.CheckRerouteEvidence(facts, snapshots));
    }

    // --- restore/relink suppression -------------------------------------------------------

    [Fact]
    public void ASilentRestoreAssignmentWithOnlyInteriorFactsPasses()
    {
        var station = new List<StationTransition>
        {
            StationFact(StationTransitionKind.InteriorReady, Station),
            StationFact(StationTransitionKind.InteriorDestroyed, Station)
        };
        Assert.Null(TravelResilienceReceipt.CheckSuppressed("relink", new List<TravelTransition>(), station, out int interior));
        Assert.Equal(2, interior);
    }

    [Fact]
    public void ARestoreAssignmentThatEmittedAPhysicalDockFactFails()
    {
        var station = new List<StationTransition> { StationFact(StationTransitionKind.DockedPhysical, Station) };
        Assert.Contains("emitted physical station facts",
            TravelResilienceReceipt.CheckSuppressed("re-init", new List<TravelTransition>(), station, out _));
    }

    [Fact]
    public void ARestoreAssignmentThatEmittedATravelFactFails()
    {
        var travel = new List<TravelTransition> { Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, (System1, HopA), null, 1) };
        Assert.Contains("emitted travel facts",
            TravelResilienceReceipt.CheckSuppressed("re-init", travel, new List<StationTransition>(), out _));
    }

    [Fact]
    public void TheLoadWindowAcceptsAReplacedSessionPrefixButNoFreshPhysicalFact()
    {
        var previous = Guid.NewGuid();
        var window = new List<StationTransition>
        {
            StationFact(StationTransitionKind.Leaving, Station, previous),
            StationFact(StationTransitionKind.InteriorDestroyed, Station, previous),
            StationFact(StationTransitionKind.InteriorReady, Station)
        };
        Assert.Null(TravelResilienceReceipt.CheckLoadRestoreSuppression(window, Session, out int prior, out int interior));
        Assert.Equal(2, prior);
        Assert.Equal(1, interior);
        window.Add(StationFact(StationTransitionKind.DockedPhysical, Station));
        Assert.Contains("load restore emitted a physical station fact",
            TravelResilienceReceipt.CheckLoadRestoreSuppression(window, Session, out _, out _));
    }

    [Fact]
    public void AReplacedSessionStationFactAfterTheLoadBoundaryIsAStaleLeak()
    {
        var window = new List<StationTransition>
        {
            StationFact(StationTransitionKind.InteriorReady, Station),
            StationFact(StationTransitionKind.Leaving, Station, Guid.NewGuid())
        };
        Assert.Contains("interleaved after the load boundary",
            TravelResilienceReceipt.CheckLoadRestoreSuppression(window, Session, out _, out _));
    }

    [Fact]
    public void TheReinitBranchIsProvenByTheNativeDockingOptionThatWasUsed()
    {
        Assert.Null(TravelResilienceReceipt.CheckReinitBranch(true, "large", "small", "large", true));
        Assert.Null(TravelResilienceReceipt.CheckReinitBranch(false, "small", "small", "small", false));
        // A same-size-only run must never be reported as different-size coverage.
        Assert.Contains("selected a ship of the current docking size",
            TravelResilienceReceipt.CheckReinitBranch(true, "small", "small", "small", false));
        Assert.Contains("kept the same native docking option",
            TravelResilienceReceipt.CheckReinitBranch(true, "large", "small", "large", false));
        Assert.Contains("docked at a small option instead of the new ship's large size",
            TravelResilienceReceipt.CheckReinitBranch(true, "large", "small", "small", true));
        Assert.Contains("changed the native docking option",
            TravelResilienceReceipt.CheckReinitBranch(false, "small", "small", "small", true));
        Assert.Contains("selected a large ship for a small docking option",
            TravelResilienceReceipt.CheckReinitBranch(false, "large", "small", "small", false));
    }

    // --- stale-session replay -------------------------------------------------------------

    [Fact]
    public void ASilentReplayWithOnlyReplacementInteriorFactsPasses()
    {
        var station = new List<StationTransition> { StationFact(StationTransitionKind.InteriorReady, Station) };
        Assert.Null(TravelResilienceReceipt.CheckReplaySilence(new List<TravelTransition>(), station, Session, out int interior));
        Assert.Equal(1, interior);
    }

    [Fact]
    public void AReplayThatProducedAnyFactFails()
    {
        var travel = new List<TravelTransition> { Fact(TravelTransitionKind.Departed, Guid.NewGuid(), (System1, Station), null, null, 1) };
        Assert.Contains("produced travel facts",
            TravelResilienceReceipt.CheckReplaySilence(travel, new List<StationTransition>(), Session, out _));
        var station = new List<StationTransition> { StationFact(StationTransitionKind.Leaving, Station) };
        Assert.Contains("produced physical station facts",
            TravelResilienceReceipt.CheckReplaySilence(new List<TravelTransition>(), station, Session, out _));
    }

    [Fact]
    public void AReplacedSessionFactDuringTheReplayIsAStaleLeak()
    {
        var station = new List<StationTransition> { StationFact(StationTransitionKind.InteriorDestroyed, Station, Guid.NewGuid()) };
        Assert.Contains("leaked into the replacement session",
            TravelResilienceReceipt.CheckReplaySilence(new List<TravelTransition>(), station, Session, out _));
    }

    [Fact]
    public void AReplayRecordsItsActualVanillaExceptionInsteadOfHidingIt()
    {
        var described = TravelResilienceReceipt.DescribeReplay("beforeFirstAdvance", 3, false,
            "UnityEngine.MissingReferenceException", "Behaviour.Spacestation.Docking.DockingPad.UndockingProcedure");
        Assert.Contains("steps=3", described);
        Assert.Contains("completed=False", described);
        Assert.Contains("vanillaException=UnityEngine.MissingReferenceException at Behaviour.Spacestation.Docking.DockingPad.UndockingProcedure", described);
        Assert.Contains("vanillaException=none", TravelResilienceReceipt.DescribeReplay("midIteration", 5, true, null, null));
    }

    // --- phase evaluation ------------------------------------------------------------------

    [Fact]
    public void AllThreeRequiredCasesPassingWithEvidenceIsThePass()
    {
        var (rows, events) = CompleteReceipt();
        Assert.Null(TravelResilienceReceipt.Evaluate(rows, null, events));
        Assert.StartsWith("PASS", TravelResilienceReceipt.Summarize(rows, null, events));
    }

    [Fact]
    public void EmptyOrSkippedCoverageIsNeverAPass()
    {
        Assert.Contains("empty coverage is not a pass",
            TravelResilienceReceipt.Evaluate(Array.Empty<TravelStationReceipt.Row>(), null, Array.Empty<string>()));
        var skipped = TravelResilienceReceipt.RequiredCases.Select(id => Row(id, TravelStationReceipt.NotRun)).ToArray();
        Assert.Contains("is not-run", TravelResilienceReceipt.Evaluate(skipped, null, Array.Empty<string>()));
    }

    [Fact]
    public void AMissingFailedOrDuplicatedRequiredCaseIsAFailure()
    {
        var (rows, events) = CompleteReceipt();
        Assert.Contains("did not run", TravelResilienceReceipt.Evaluate(rows.Take(2).ToArray(), null, events));
        var failed = rows.Take(2).Append(Row(TravelResilienceReceipt.StaleReplayCase, TravelStationReceipt.Failed)).ToArray();
        Assert.Contains("Failed cases", TravelResilienceReceipt.Evaluate(failed, null, events));
        var duplicated = rows.Append(Row(TravelResilienceReceipt.RestoreDockCase, TravelStationReceipt.Passed, "travel:2")).ToArray();
        Assert.Contains("recorded 2 rows", TravelResilienceReceipt.Evaluate(duplicated, null, events));
    }

    [Fact]
    public void ARequiredCaseWithoutObservedEventsOrWithUnobservedEvidenceIsAFailure()
    {
        var (rows, events) = CompleteReceipt();
        var withoutEvidence = rows.Take(2).Append(Row(TravelResilienceReceipt.StaleReplayCase, TravelStationReceipt.Passed))
            .Append(Row(TravelResilienceReceipt.SameSizeReinitCase, TravelStationReceipt.Passed, "station:1")).ToArray();
        Assert.Contains("no observed public events", TravelResilienceReceipt.Evaluate(withoutEvidence, null, events));
        Assert.Contains("not in the trace for its session",
            TravelResilienceReceipt.Evaluate(rows, null, events.Take(1).ToArray()));
        var foreign = events.Select(row => row.Replace(Session.ToString(), Guid.NewGuid().ToString())).ToArray();
        Assert.Contains("not in the trace for its session", TravelResilienceReceipt.Evaluate(rows, null, foreign));
    }

    [Fact]
    public void TheSameSizeReinitSubcaseIsMandatoryAndNeverAnOptionalNotRun()
    {
        var (rows, events) = CompleteReceipt();
        Assert.Contains(TravelResilienceReceipt.SameSizeReinitCase, TravelResilienceReceipt.RequiredSubcaseRows);
        Assert.DoesNotContain(TravelResilienceReceipt.SameSizeReinitCase, TravelResilienceReceipt.RequiredCases);
        Assert.Null(TravelResilienceReceipt.Evaluate(rows, null, events));
        var missing = rows.Where(row => row.Case != TravelResilienceReceipt.SameSizeReinitCase).ToArray();
        Assert.Contains("Required subcase did not run", TravelResilienceReceipt.Evaluate(missing, null, events));
        var notRun = missing.Append(Row(TravelResilienceReceipt.SameSizeReinitCase, TravelStationReceipt.NotRun)).ToArray();
        Assert.Contains("Required subcase is not-run", TravelResilienceReceipt.Evaluate(notRun, null, events));
        var duplicated = rows.Append(Row(TravelResilienceReceipt.SameSizeReinitCase, TravelStationReceipt.Passed, "station:1")).ToArray();
        Assert.Contains("Required subcase recorded 2 rows", TravelResilienceReceipt.Evaluate(duplicated, null, events));
        var summary = TravelResilienceReceipt.Summarize(rows, null, events);
        Assert.Contains("required-subcases=" + TravelResilienceReceipt.SameSizeReinitCase, summary);
        Assert.Contains("required-subcase " + TravelResilienceReceipt.SameSizeReinitCase + "=passed", summary);
        // A mandatory subcase row is never coverage of a required case identity.
        var withoutRequired = rows.Where(row => row.Case != TravelResilienceReceipt.RestoreDockCase).ToArray();
        Assert.Contains("did not run", TravelResilienceReceipt.Evaluate(withoutRequired, null, events));
    }

    [Fact]
    public void TheCurrentShipReinitMustReplaceTheUnitAndKeepItsOptionAndData()
    {
        Assert.Null(TravelResilienceReceipt.CheckCurrentShipReinit(true, true, false, "small", "small"));
        Assert.Contains("changed the player's native ship data",
            TravelResilienceReceipt.CheckCurrentShipReinit(true, false, false, "small", "small"));
        Assert.Contains("did not replace the native ship unit",
            TravelResilienceReceipt.CheckCurrentShipReinit(false, true, false, "small", "small"));
        Assert.Contains("did not take the same-size branch",
            TravelResilienceReceipt.CheckCurrentShipReinit(true, true, true, "small", "small"));
        Assert.Contains("instead of the unchanged small one",
            TravelResilienceReceipt.CheckCurrentShipReinit(true, true, false, "small", "large"));
    }

    [Fact]
    public void APilotFaultIsReportedAfterTheAttributedCaseFailure()
    {
        var (rows, events) = CompleteReceipt();
        Assert.Contains("Pilot fault", TravelResilienceReceipt.Evaluate(rows, "boom", events));
        var failed = rows.Take(2).Append(Row(TravelResilienceReceipt.StaleReplayCase, TravelStationReceipt.Failed)).ToArray();
        Assert.Contains("Failed cases", TravelResilienceReceipt.Evaluate(failed, "boom", events));
    }

    [Fact]
    public void ACheckpointIsNeverAPassClaim()
    {
        var (rows, _) = CompleteReceipt();
        var checkpoint = TravelResilienceReceipt.SummarizeIncomplete(rows, TravelResilienceReceipt.StaleReplayCase);
        Assert.StartsWith(TravelStationReceipt.Incomplete, checkpoint);
        Assert.DoesNotContain("PASS", checkpoint);
        Assert.Contains("phase=" + TravelResilienceReceipt.Phase, checkpoint);
        Assert.Contains("activeCase=" + TravelResilienceReceipt.StaleReplayCase, checkpoint);
    }

    [Fact]
    public void ThePublishedBudgetIsDerivedFromTheDeclaredWaitsAndFitsTheReservation()
    {
        Assert.Equal(TravelResilienceReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences),
            TravelResilienceReceipt.PhaseBudgetSeconds);
        Assert.True(TravelResilienceReceipt.PhaseBudgetSeconds > 0);
        Assert.True(TravelResilienceReceipt.PhaseBudgetSeconds <= TravelResilienceReceipt.LauncherReservationSeconds,
            "The declared phase budget must fit the launcher reservation.");
        // The load/binding waits are derived from the case count plus the stale case's own
        // replacement load and the restoring load, not hand-typed.
        Assert.Equal(TravelResilienceReceipt.LoadWaitsPerCase * TravelResilienceReceipt.RequiredCases.Length
            + TravelResilienceReceipt.ReplacementLoadWaits + TravelResilienceReceipt.RestoringLoadWaits,
            TravelResilienceReceipt.PhaseWaits.Single(wait => wait.Name == "fixture-load-and-binding").Occurrences);
        Assert.Equal(TravelResilienceReceipt.RerouteSettles + TravelResilienceReceipt.RestoreSettles
            + TravelResilienceReceipt.StaleSettles + TravelResilienceReceipt.PhaseLevelSettles,
            TravelResilienceReceipt.PhaseWaits.Single(wait => wait.Name == "settle").Occurrences);
    }

    [Fact]
    public void ThisPhaseNeverStandsInForTheOtherTravelPhases()
    {
        Assert.NotEqual(TravelStationReceipt.Phase, TravelResilienceReceipt.Phase);
        Assert.NotEqual(TravelCrossSystemReceipt.Phase, TravelResilienceReceipt.Phase);
        Assert.Empty(TravelResilienceReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.Empty(TravelResilienceReceipt.RequiredCases.Intersect(TravelCrossSystemReceipt.RequiredCases));
        Assert.Equal(new[] { "empty-origin-reroute", "restore-relink-dock", "stale-session-replay" },
            TravelResilienceReceipt.RequiredCases);
        var (rows, events) = CompleteReceipt();
        var summary = TravelResilienceReceipt.Summarize(rows, null, events);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.Contains(TravelStationReceipt.Phase, summary);
        Assert.Contains(TravelCrossSystemReceipt.Phase, summary);
    }
}
