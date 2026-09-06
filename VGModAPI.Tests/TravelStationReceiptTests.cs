using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the native travel/station pilot uses to decide PASS/FAIL.
/// Each test reproduces a concrete defect a passing compilation would otherwise hide.
/// </summary>
public sealed class TravelStationReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private const string System = "system-1";
    private const string Station = "station-1";
    private const string First = "poi-1";
    private const string Second = "poi-2";
    private static long _sequence;

    private static TravelLocation At(string? poi) => new(System, poi, "System", poi);
    private static TravelTransition Fact(TravelTransitionKind kind, Guid? operation, string? origin, string? requested, string? actual,
        double seconds = 1, Guid? session = null, long? sequence = null, TravelMode mode = TravelMode.InSystem)
        => new(session ?? Session, operation, sequence ?? ++_sequence, kind, mode,
            origin == null ? null : At(origin), requested == null ? null : At(requested), actual == null ? null : At(actual), seconds, null);
    private static StationTransition StationFact(StationTransitionKind kind, string? station, long sequence, double seconds = 1, Guid? session = null)
        => new(session ?? Session, sequence, kind, station == null ? null : At(station), seconds);
    private static TravelStationReceipt.Row Row(string id, string status, string evidence = "", Guid? session = null)
        => new(id, id + " description", status, "identity", (session ?? Session).ToString(), Guid.NewGuid().ToString(), evidence, "detail");
    // A synthetic public-event trace row; the case label is only the observation context.
    private static string EventRow(string surface, long sequence, string caseLabel, Guid? session = null)
        => string.Join("\t", sequence, surface, caseLabel, session ?? Session, "", "Arrived", "InSystem", "", "", "", "1.000", "");
    // Every required case passed, each referencing its own observed event by surface and sequence.
    private static (List<TravelStationReceipt.Row> Rows, List<string> Events) CompleteReceipt()
    {
        var rows = new List<TravelStationReceipt.Row>();
        var events = new List<string>();
        long sequence = 0;
        foreach (var id in TravelStationReceipt.RequiredCases)
        {
            sequence++;
            rows.Add(Row(id, TravelStationReceipt.Passed, "travel:" + sequence));
            events.Add(EventRow("travel", sequence, id));
        }
        return (rows, events);
    }

    private static List<TravelTransition> Hop(Guid operation, string origin, string destination, ref double clock)
        => new()
        {
            Fact(TravelTransitionKind.Requested, operation, null, destination, null, clock += 1),
            Fact(TravelTransitionKind.Departed, operation, origin, null, null, clock += 1),
            Fact(TravelTransitionKind.Arrived, operation, origin, destination, destination, clock += 1)
        };

    private static List<TravelTransition> Route(string origin, params string[] hops)
    {
        double clock = 0;
        var facts = new List<TravelTransition>();
        Guid last = Guid.Empty;
        var from = origin;
        foreach (var hop in hops)
        {
            last = Guid.NewGuid();
            facts.AddRange(Hop(last, from, hop, ref clock));
            from = hop;
        }
        facts.Add(Fact(TravelTransitionKind.RouteCompleted, last, null, null, hops[hops.Length - 1], clock += 1));
        return facts;
    }

    [Fact]
    public void InitialPlacementWindowClearedAfterTheLoadIsNotAPass()
    {
        // The defect: clearing the buffer after the fixture load erases the fresh session's own
        // InitialPlacement, so the case can never legitimately pass.
        Assert.NotNull(TravelStationReceipt.CheckInitialPlacement(Array.Empty<TravelTransition>(), Session, System, Station));
        var late = new[] { Fact(TravelTransitionKind.Arrived, Guid.NewGuid(), Station, First, First, sequence: 7) };
        Assert.Contains("not InitialPlacement", TravelStationReceipt.CheckInitialPlacement(late, Session, System, Station));
    }

    [Fact]
    public void InitialPlacementRejectsFabricatedArrivalsForeignSessionsAndWrongLocation()
    {
        var placement = Fact(TravelTransitionKind.InitialPlacement, null, null, null, Station, sequence: 1);
        Assert.Null(TravelStationReceipt.CheckInitialPlacement(new[] { placement }, Session, System, Station));
        Assert.NotNull(TravelStationReceipt.CheckInitialPlacement(new[] { placement }, Session, System, First));
        Assert.NotNull(TravelStationReceipt.CheckInitialPlacement(
            new[] { placement, Fact(TravelTransitionKind.RouteCompleted, Guid.NewGuid(), null, null, Station, sequence: 2) }, Session, System, Station));
        Assert.NotNull(TravelStationReceipt.CheckInitialPlacement(
            new[] { Fact(TravelTransitionKind.InitialPlacement, null, null, null, Station, sequence: 1, session: Guid.NewGuid()) }, Session, System, Station));
        // A placement that is not the session's first public fact means earlier history leaked in.
        Assert.Contains("first public fact", TravelStationReceipt.CheckInitialPlacement(
            new[] { Fact(TravelTransitionKind.InitialPlacement, null, null, null, Station, sequence: 4) }, Session, System, Station)!);
    }

    [Fact]
    public void SingleLegFactsCannotSatisfyATwoHopChain()
    {
        // The defect: requesting one destination produces one leg; calling TravelToNextWaypoint
        // twice cannot turn that into a chained route.
        var single = Route(Station, First);
        Assert.Null(TravelStationReceipt.CheckRoute(single, Session, System, Station, new[] { First }));
        var failure = TravelStationReceipt.CheckRoute(single, Session, System, Station, new[] { First, Station });
        Assert.NotNull(failure);
        Assert.Contains("instead of", failure);
    }

    [Fact]
    public void ChainedRouteRequiresOrderedPerHopFactsWithDistinctOperations()
    {
        var chain = Route(First, Second, Station);
        Assert.Null(TravelStationReceipt.CheckRoute(chain, Session, System, First, new[] { Second, Station }));
        // Both hops reported under one operation identity is a fabricated chain, not two legs.
        var shared = chain.ToList();
        shared[3] = Fact(TravelTransitionKind.Requested, chain[0].OperationId, null, Station, null, chain[3].GameSeconds, sequence: chain[3].Sequence);
        shared[4] = Fact(TravelTransitionKind.Departed, chain[0].OperationId, Second, null, null, chain[4].GameSeconds, sequence: chain[4].Sequence);
        shared[5] = Fact(TravelTransitionKind.Arrived, chain[0].OperationId, Second, Station, Station, chain[5].GameSeconds, sequence: chain[5].Sequence);
        Assert.Contains("reused an earlier operation identity",
            TravelStationReceipt.CheckRoute(shared, Session, System, First, new[] { Second, Station })!);
        var extraCompletion = chain.ToList();
        extraCompletion.Insert(3, Fact(TravelTransitionKind.RouteCompleted, chain[0].OperationId, null, null, Second, chain[2].GameSeconds));
        Assert.NotNull(TravelStationReceipt.CheckRoute(extraCompletion, Session, System, First, new[] { Second, Station }));
    }

    [Fact]
    public void EarlierCaseHistoryInTheWindowFailsInsteadOfSatisfyingTheNewCase()
    {
        // The defect: using the whole session history lets a previous case's RouteCompleted (or a
        // previous arrival) satisfy the current case's wait and assertions.
        var contaminated = new List<TravelTransition> { Fact(TravelTransitionKind.RouteCompleted, Guid.NewGuid(), null, null, Station, 0.5) };
        contaminated.AddRange(Route(Station, First));
        Assert.NotNull(TravelStationReceipt.CheckRoute(contaminated, Session, System, Station, new[] { First }));
        var foreign = Route(Station, First);
        foreign[1] = Fact(TravelTransitionKind.Departed, foreign[0].OperationId, Station, null, null, foreign[1].GameSeconds,
            session: Guid.NewGuid(), sequence: foreign[1].Sequence);
        Assert.Contains("Foreign-session", TravelStationReceipt.CheckRoute(foreign, Session, System, Station, new[] { First })!);
    }

    [Fact]
    public void RouteRequiresRealOriginDestinationAndInSystemMode()
    {
        var wrongOrigin = Route(Station, First);
        wrongOrigin[1] = Fact(TravelTransitionKind.Departed, wrongOrigin[0].OperationId, Second, null, null, wrongOrigin[1].GameSeconds,
            sequence: wrongOrigin[1].Sequence);
        Assert.Contains("departed from", TravelStationReceipt.CheckRoute(wrongOrigin, Session, System, Station, new[] { First })!);
        var wrongMode = Route(Station, First);
        wrongMode[2] = Fact(TravelTransitionKind.Arrived, wrongMode[0].OperationId, Station, First, First, wrongMode[2].GameSeconds,
            sequence: wrongMode[2].Sequence, mode: TravelMode.JumpGate);
        Assert.Contains("mode", TravelStationReceipt.CheckRoute(wrongMode, Session, System, Station, new[] { First })!);
        var wrongCompletion = Route(Station, First);
        wrongCompletion[3] = Fact(TravelTransitionKind.RouteCompleted, wrongCompletion[0].OperationId, null, null, Second,
            wrongCompletion[3].GameSeconds, sequence: wrongCompletion[3].Sequence);
        Assert.Contains("RouteCompleted reports", TravelStationReceipt.CheckRoute(wrongCompletion, Session, System, Station, new[] { First })!);
    }

    [Fact]
    public void EarlyCancelRequiresTheUnchangedOriginAndRejectsAnyDeparture()
    {
        var operation = Guid.NewGuid();
        var clean = new[]
        {
            Fact(TravelTransitionKind.Requested, operation, null, First, null, 1),
            Fact(TravelTransitionKind.Cancelled, operation, null, null, Station, 1)
        };
        Assert.Null(TravelStationReceipt.CheckEarlyCancel(clean, Session, System, Station, First));
        var departed = new[]
        {
            clean[0],
            Fact(TravelTransitionKind.Departed, operation, Station, null, null, 1),
            clean[1]
        };
        Assert.NotNull(TravelStationReceipt.CheckEarlyCancel(departed, Session, System, Station, First));
        var movedOrigin = new[] { clean[0], Fact(TravelTransitionKind.Cancelled, operation, null, null, First, 1) };
        Assert.Contains("unchanged origin", TravelStationReceipt.CheckEarlyCancel(movedOrigin, Session, System, Station, First)!);
        var foreignOperation = new[] { clean[0], Fact(TravelTransitionKind.Cancelled, Guid.NewGuid(), null, null, Station, 1) };
        Assert.Contains("operation identity", TravelStationReceipt.CheckEarlyCancel(foreignOperation, Session, System, Station, First)!);
    }

    [Fact]
    public void StationPhaseChecksPhysicalFactsAndIgnoresInteriorOrdering()
    {
        var undock = new[]
        {
            StationFact(StationTransitionKind.InteriorDestroyed, Station, 1),
            StationFact(StationTransitionKind.Undocking, Station, 2),
            StationFact(StationTransitionKind.Leaving, Station, 3)
        };
        Assert.Null(TravelStationReceipt.CheckStationPhase(undock, Session, System, Station,
            new[] { StationTransitionKind.Undocking, StationTransitionKind.Leaving }));
        // Interior readiness before the physical dock is native behaviour, not an ordering defect.
        var dock = new[]
        {
            StationFact(StationTransitionKind.InteriorReady, Station, 1),
            StationFact(StationTransitionKind.DockedPhysical, Station, 2)
        };
        Assert.Null(TravelStationReceipt.CheckStationPhase(dock, Session, System, Station, new[] { StationTransitionKind.DockedPhysical }));
        Assert.NotNull(TravelStationReceipt.CheckStationPhase(new[] { dock[0] }, Session, System, Station,
            new[] { StationTransitionKind.DockedPhysical }));
        var wrongStation = new[] { StationFact(StationTransitionKind.DockedPhysical, First, 2) };
        Assert.Contains("instead of", TravelStationReceipt.CheckStationPhase(wrongStation, Session, System, Station,
            new[] { StationTransitionKind.DockedPhysical })!);
        var foreignSession = new[] { StationFact(StationTransitionKind.DockedPhysical, Station, 2, session: Guid.NewGuid()) };
        Assert.Contains("Foreign-session", TravelStationReceipt.CheckStationPhase(foreignSession, Session, System, Station,
            new[] { StationTransitionKind.DockedPhysical })!);
    }

    [Fact]
    public void EmptyOrSkippedCoverageIsNeverAPass()
    {
        var events = CompleteReceipt().Events;
        Assert.NotNull(TravelStationReceipt.Evaluate(Array.Empty<TravelStationReceipt.Row>(), null, events));
        var allSkipped = TravelStationReceipt.RequiredCases.Select(id => Row(id, TravelStationReceipt.NotRun)).ToArray();
        Assert.Contains("is not-run", TravelStationReceipt.Evaluate(allSkipped, null, events)!);
        Assert.StartsWith("FAIL", TravelStationReceipt.Summarize(allSkipped, null, events));
    }

    [Fact]
    public void MissingDuplicatedOrFailedRequiredCasesFailThePhase()
    {
        var (complete, events) = CompleteReceipt();
        Assert.Null(TravelStationReceipt.Evaluate(complete, null, events));
        Assert.StartsWith("PASS", TravelStationReceipt.Summarize(complete, null, events));
        var missing = complete.Where(r => r.Case != TravelStationReceipt.RequiredCases[0]).ToList();
        Assert.Contains(TravelStationReceipt.RequiredCases[0], TravelStationReceipt.Evaluate(missing, null, events)!);
        var duplicated = complete.Concat(new[] { Row(TravelStationReceipt.RequiredCases[0], TravelStationReceipt.Passed) }).ToList();
        Assert.Contains("2 rows", TravelStationReceipt.Evaluate(duplicated, null, events)!);
        var failed = complete.Where(r => r.Case != TravelStationReceipt.RequiredCases[1])
            .Concat(new[] { Row(TravelStationReceipt.RequiredCases[1], TravelStationReceipt.Failed) }).ToList();
        Assert.Contains("Failed cases", TravelStationReceipt.Evaluate(failed, null, events)!);
        var unknown = complete.Concat(new[] { Row("optional", "skipped") }).ToList();
        Assert.Contains("Unknown case status", TravelStationReceipt.Evaluate(unknown, null, events)!);
    }

    [Fact]
    public void OptionalNotRunCellsNeitherFailNorSubstituteForRequiredCoverage()
    {
        var (complete, events) = CompleteReceipt();
        var rows = complete.Concat(new[] { Row("cross-system-jumpgate", TravelStationReceipt.NotRun) }).ToList();
        Assert.Null(TravelStationReceipt.Evaluate(rows, null, events));
        var summary = TravelStationReceipt.Summarize(rows, null, events);
        Assert.StartsWith("PASS", summary);
        Assert.Contains("phase=" + TravelStationReceipt.Phase, summary);
        Assert.Contains("optional-not-run=cross-system-jumpgate", summary);
        var onlyOptional = new[] { Row("cross-system-jumpgate", TravelStationReceipt.NotRun) };
        Assert.NotNull(TravelStationReceipt.Evaluate(onlyOptional, null, events));
    }

    [Fact]
    public void APilotFaultFailsThePhaseAndStaysInTheSummary()
    {
        var (complete, events) = CompleteReceipt();
        var failure = TravelStationReceipt.Evaluate(complete, "InvalidOperationException: timed out", events);
        Assert.Contains("Pilot fault", failure!);
        var summary = TravelStationReceipt.Summarize(complete, "InvalidOperationException: timed out", events);
        Assert.StartsWith("FAIL", summary);
        Assert.Contains("fault=InvalidOperationException: timed out", summary);
    }

    // F2: native cases legitimately overlap in time. The dock window opens before the chained
    // route, so the dock's own facts are observed while "chained-route" is the active label.
    [Fact]
    public void OverlappingDockAndChainedRouteEvidenceIsAcceptedByReferenceNotByLabel()
    {
        var (rows, events) = CompleteReceipt();
        var dockIndex = rows.FindIndex(r => r.Case == "station-dock");
        // The dock's DockedPhysical is emitted on the station surface while the chained route drives.
        rows[dockIndex] = Row("station-dock", TravelStationReceipt.Passed, "station:3");
        events.Add(EventRow("station", 3, "chained-route"));
        Assert.Null(TravelStationReceipt.CheckEvidence(rows, events));
        Assert.Null(TravelStationReceipt.Evaluate(rows, null, events));
        Assert.StartsWith("PASS", TravelStationReceipt.Summarize(rows, null, events));
    }

    [Fact]
    public void MissingOrForeignSessionEvidenceStillFailsTheRequiredCase()
    {
        var (rows, events) = CompleteReceipt();
        var dockIndex = rows.FindIndex(r => r.Case == "station-dock");
        // No dock event was ever observed: the case cannot pass on its claim alone.
        rows[dockIndex] = Row("station-dock", TravelStationReceipt.Passed, "station:3");
        Assert.Contains("not in the trace", TravelStationReceipt.Evaluate(rows, null, events)!);
        // An event exists, but for another session.
        var foreign = new List<string>(events) { EventRow("station", 3, "chained-route", Guid.NewGuid()) };
        Assert.Contains("not in the trace", TravelStationReceipt.Evaluate(rows, null, foreign)!);
        // No evidence at all for a required case is a failure too.
        rows[dockIndex] = Row("station-dock", TravelStationReceipt.Passed);
        Assert.Contains("no observed public events", TravelStationReceipt.Evaluate(rows, null, events)!);
    }

    // F4: a case window keeps foreign-session facts so the validators can reject them.
    [Fact]
    public void CaseWindowsKeepForeignFactsAndOnlyTheLoadBoundaryToleratesReplacedSessions()
    {
        var other = Guid.NewGuid();
        var observed = new List<TravelTransition>
        {
            Fact(TravelTransitionKind.Arrived, Guid.NewGuid(), Station, First, First, 1, session: other, sequence: 4),
            Fact(TravelTransitionKind.InitialPlacement, null, null, null, Station, 2, sequence: 1)
        };
        // Window does not filter: the foreign fact survives into the case window and is rejected.
        var window = TravelStationReceipt.Window(observed, 0);
        Assert.Equal(2, window.Count);
        Assert.Contains("Foreign-session", TravelStationReceipt.CheckInitialPlacement(window, Session, System, Station)!);
        // The load boundary explicitly tolerates the replaced session's facts BEFORE the fresh one.
        Assert.Null(TravelStationReceipt.CheckLoadBoundary(observed, Session, out int freshIndex, out int prior));
        Assert.Equal(1, freshIndex);
        Assert.Equal(1, prior);
        Assert.Null(TravelStationReceipt.CheckInitialPlacement(TravelStationReceipt.Window(observed, freshIndex), Session, System, Station));
        // A foreign fact AFTER the fresh session's first fact is never tolerated.
        observed.Add(Fact(TravelTransitionKind.Arrived, Guid.NewGuid(), Station, First, First, 3, session: other, sequence: 5));
        Assert.NotNull(TravelStationReceipt.CheckInitialPlacement(TravelStationReceipt.Window(observed, freshIndex), Session, System, Station));
        // A session that never produced a fact is a boundary failure, not a silent pass.
        Assert.NotNull(TravelStationReceipt.CheckLoadBoundary(
            new[] { observed[0] }, Session, out _, out _));
    }

    // F3: the declared phase budget must cover every declared bounded wait of the phase.
    [Fact]
    public void PhaseBudgetCoversTheDeclaredBoundedWaitsAndIsPublishedInBothSummaries()
    {
        var worstCase = TravelStationReceipt.ReadinessSeconds
            + TravelStationReceipt.InitialDockSettleSeconds + TravelStationReceipt.UndockLeavingSeconds
            + TravelStationReceipt.UndockRoutineSeconds + 3 * TravelStationReceipt.ArrivalSeconds
            + 2 * TravelStationReceipt.BoundarySeconds + TravelStationReceipt.DockSeconds
            + 3 * TravelStationReceipt.TravelReadySeconds + 12 * TravelStationReceipt.SettleSeconds;
        Assert.Equal(TravelStationReceipt.PhaseBudgetSeconds, worstCase);
        var (rows, events) = CompleteReceipt();
        Assert.Contains("budgetSeconds=" + TravelStationReceipt.PhaseBudgetSeconds.ToString("F0"), TravelStationReceipt.Summarize(rows, null, events));
        var incomplete = TravelStationReceipt.SummarizeIncomplete(rows, "station-dock");
        Assert.StartsWith(TravelStationReceipt.Incomplete, incomplete);
        Assert.DoesNotContain("PASS", incomplete);
        Assert.Contains("activeCase=station-dock", incomplete);
        Assert.Contains("budgetSeconds=" + TravelStationReceipt.PhaseBudgetSeconds.ToString("F0"), incomplete);
    }

    [Fact]
    public void EvidenceIsFormattedAndParsedBySurfaceAndApiSequence()
    {
        var travel = new[]
        {
            Fact(TravelTransitionKind.Requested, Guid.NewGuid(), null, First, null, 1, sequence: 7),
            Fact(TravelTransitionKind.Arrived, Guid.NewGuid(), Station, First, First, 2, sequence: 8)
        };
        var station = new[] { StationFact(StationTransitionKind.DockedPhysical, Station, 3) };
        var evidence = TravelStationReceipt.Evidence(travel, station);
        Assert.Equal("travel:7,8;station:3", evidence);
        Assert.Equal(new[] { "travel:7", "travel:8", "station:3" },
            TravelStationReceipt.EvidenceReferences(evidence).Select(r => r.Key + ":" + r.Value).ToArray());
        Assert.Empty(TravelStationReceipt.EvidenceReferences(""));
    }

    [Fact]
    public void ReceiptRowsAndEventRowsStayTabSeparatedAndCarryIdentities()
    {
        var row = new TravelStationReceipt.Row("case", "line one\nline two", TravelStationReceipt.Passed, "id", Session.ToString(), "", "travel:1", "a\tb");
        var columns = row.ToTsv().Split('\t');
        Assert.Equal(TravelStationReceipt.ReceiptHeader.Split('\t').Length, columns.Length);
        Assert.Equal("line one line two", columns[1]);
        Assert.Equal(Session.ToString(), columns[4]);
        var operation = Guid.NewGuid();
        var eventColumns = TravelStationReceipt.TravelEventRow("in-system-route",
            Fact(TravelTransitionKind.Arrived, operation, Station, First, First, 12.5, sequence: 9)).Split('\t');
        Assert.Equal(TravelStationReceipt.EventsHeader.Split('\t').Length, eventColumns.Length);
        Assert.Equal("9", eventColumns[0]);
        Assert.Equal(operation.ToString(), eventColumns[4]);
        Assert.Equal("Arrived", eventColumns[5]);
        Assert.Equal("12.500", eventColumns[10]);
        var stationColumns = TravelStationReceipt.StationEventRow("station-dock",
            StationFact(StationTransitionKind.DockedPhysical, Station, 3, 20)).Split('\t');
        Assert.Equal(TravelStationReceipt.EventsHeader.Split('\t').Length, stationColumns.Length);
        Assert.Equal("station", stationColumns[1]);
        Assert.Equal(TravelStationReceipt.Location(System, Station), stationColumns[9]);
    }
}
