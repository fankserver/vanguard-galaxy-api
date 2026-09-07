using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the exact rules the ACTUAL-CONSUMER Echo arrival-snap probe uses to decide
/// PASS/FAIL. Each test reproduces a concrete way the probe could look green while proving nothing:
/// a timer that was already zero, a write in the wrong frame, a next idle tick that never reached
/// its decision, an empty "quiet" window, a refusal recorded while the world was actually idle, or
/// a blanket patch rule that would falsely refuse Echo's unrelated automation.
/// </summary>
public sealed class EchoTravelReceiptTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Operation = Guid.NewGuid();

    private static EchoTravelReceipt.RouteFact Fact(int frame = 10, Guid? session = null, Guid? operation = null)
        => new(session ?? Session, operation ?? Operation, 7, frame);

    private static EchoTravelReceipt.SnapObservation Snap(int frame, float before, float after,
        bool travelActive = false, int waypoints = 0)
        => new(frame, before, after, travelActive, waypoints);

    private static EchoTravelReceipt.IdleObservation Idle(int frame, float before, float after, bool findActivity)
        => new(frame, before, after, findActivity);

    private static TravelStationReceipt.Row Row(string id, string status, string evidence = "travel:1", Guid? session = null)
        => new(id, id + " description", status, "identity", (session ?? Session).ToString(), "", evidence, "detail");

    private static List<string> Trace(Guid session)
        => new() { string.Join("\t", 1, "travel", "case", session, "", "RouteCompleted", "InSystem", "", "", "", "1.000", "") };

    private static List<TravelStationReceipt.Row> PassingRows()
        => EchoTravelReceipt.RequiredCases.Concat(EchoTravelReceipt.RequiredSubcaseRows)
            .Select(id => Row(id, TravelStationReceipt.Passed)).ToList();

    // --- the positive snap ------------------------------------------------------------------

    [Fact]
    public void APositiveSnapNeedsTheConsumerWriteInTheFactsFrameAndTheNextIdleDecision()
    {
        var snaps = new[] { Snap(10, 300f, 0f) };
        var idle = new[] { Idle(9, 300f, 299f, false), Idle(11, 0f, -0.02f, true) };
        Assert.Null(EchoTravelReceipt.CheckPositiveSnap(Fact(), snaps, idle, Session));
    }

    [Fact]
    public void AMissingConsumerWriteIsNeverAPass()
    {
        Assert.Contains("never ran its own timer write",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), Array.Empty<EchoTravelReceipt.SnapObservation>(),
                new[] { Idle(11, 0f, -0.02f, true) }, Session));
        // A write in a different frame is not this fact's write.
        Assert.Contains("never ran its own timer write",
            EchoTravelReceipt.CheckPositiveSnap(Fact(frame: 10), new[] { Snap(4, 300f, 0f) },
                new[] { Idle(11, 0f, -0.02f, true) }, Session));
    }

    [Fact]
    public void AnAlreadyZeroOrUnchangedTimerCannotProveASnap()
    {
        var idle = new[] { Idle(11, 0f, -0.02f, true) };
        Assert.Contains("was not positive before the write",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), new[] { Snap(10, 0f, 0f) }, idle, Session));
        Assert.Contains("did not zero the native idle timer",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), new[] { Snap(10, 300f, 300f) }, idle, Session));
    }

    [Fact]
    public void AWriteWhileTheWorldIsStillTravellingIsRefused()
    {
        var idle = new[] { Idle(11, 0f, -0.02f, true) };
        Assert.Contains("still travelling",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), new[] { Snap(10, 300f, 0f, travelActive: true) }, idle, Session));
        Assert.Contains("still travelling",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), new[] { Snap(10, 300f, 0f, waypoints: 1) }, idle, Session));
    }

    [Fact]
    public void TheNextNativeIdleTickMustStartFromTheZeroedTimerAndReachItsDecision()
    {
        var snaps = new[] { Snap(10, 300f, 0f) };
        Assert.Contains("No native idle update was observed",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), snaps, new[] { Idle(9, 300f, 299f, false) }, Session));
        Assert.Contains("did not start from the zeroed timer",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), snaps, new[] { Idle(11, 300f, 299f, true) }, Session));
        Assert.Contains("did not reach its FindActivity decision",
            EchoTravelReceipt.CheckPositiveSnap(Fact(), snaps, new[] { Idle(11, 0f, -0.02f, false) }, Session));
    }

    [Fact]
    public void AForeignSessionFactCanNeverCarryAPositiveSnap()
    {
        Assert.Contains("not the case session", EchoTravelReceipt.CheckPositiveSnap(
            Fact(session: Guid.NewGuid()), new[] { Snap(10, 300f, 0f) }, new[] { Idle(11, 0f, -0.02f, true) }, Session));
    }

    // --- the quiet window -------------------------------------------------------------------

    [Fact]
    public void AQuietWindowMustCarryRealFactsAndNoConsumerWrite()
    {
        var idle = new[] { Idle(5, 300f, 299f, false) };
        Assert.Null(EchoTravelReceipt.CheckQuietWindow(Array.Empty<EchoTravelReceipt.SnapObservation>(), idle, 2, 5));
        Assert.Contains("no final route completion",
            EchoTravelReceipt.CheckQuietWindow(Array.Empty<EchoTravelReceipt.SnapObservation>(), idle, 0, 5));
        Assert.Contains("no request/cancellation/arrival",
            EchoTravelReceipt.CheckQuietWindow(Array.Empty<EchoTravelReceipt.SnapObservation>(), idle, 2, 0));
        Assert.Contains("timer write(s) in a window that must stay quiet",
            EchoTravelReceipt.CheckQuietWindow(new[] { Snap(10, 300f, 0f) }, idle, 2, 5));
        Assert.Contains("snap-driven native idle decision happened in a quiet window",
            EchoTravelReceipt.CheckQuietWindow(Array.Empty<EchoTravelReceipt.SnapObservation>(),
                new[] { Idle(5, 0f, -0.1f, true) }, 2, 5));
    }

    // --- the superseded write ---------------------------------------------------------------

    [Fact]
    public void ARefusedWriteMustLeaveTheTimerUntouchedWhileTheWorldIsReallyBusy()
    {
        var busy = new[] { Snap(10, 300f, 300f, travelActive: true, waypoints: 1) };
        Assert.Null(EchoTravelReceipt.CheckRefusedSnap(Fact(), busy, new[] { Idle(11, 300f, 299f, false) }));
        Assert.Contains("did not actually leave native travel busy",
            EchoTravelReceipt.CheckRefusedSnap(Fact(), new[] { Snap(10, 300f, 300f) }, Array.Empty<EchoTravelReceipt.IdleObservation>()));
        Assert.Contains("changed the native idle timer while refusing",
            EchoTravelReceipt.CheckRefusedSnap(Fact(), new[] { Snap(10, 300f, 0f, travelActive: true, waypoints: 1) },
                Array.Empty<EchoTravelReceipt.IdleObservation>()));
        Assert.Contains("while the ship was still busy",
            EchoTravelReceipt.CheckRefusedSnap(Fact(), busy, new[] { Idle(12, 0f, -0.1f, true) }));
        Assert.Contains("Expected exactly one consumer write attempt",
            EchoTravelReceipt.CheckRefusedSnap(Fact(), Array.Empty<EchoTravelReceipt.SnapObservation>(),
                Array.Empty<EchoTravelReceipt.IdleObservation>()));
    }

    [Fact]
    public void TheControlledSubscriptionReorderingMustLeaveExactlyOneLiveConsumerObserver()
    {
        Assert.Null(EchoTravelReceipt.CheckSubscriptionReorder(true, true, true));
        Assert.Contains("was not disposed", EchoTravelReceipt.CheckSubscriptionReorder(false, true, true));
        Assert.Contains("not registered ahead", EchoTravelReceipt.CheckSubscriptionReorder(true, true, false));
        Assert.Contains("is not listening", EchoTravelReceipt.CheckSubscriptionReorder(true, false, true));
    }

    // --- the consumer's own patch contract ---------------------------------------------------

    [Fact]
    public void OnlyTheRetiredTimingHookIsRefusedAndEchosUnrelatedAutomationIsNot()
    {
        // The retired hook: the timing patch class back on the native route boundary.
        Assert.Contains("retired timing hook restored", EchoTravelReceipt.RefuseEchoTimingPatch(
            EchoTravelReceipt.TimingPatchClass, "Behaviour.Managers.TravelManager", "TravelToNextWaypoint"));
        // Its own ETA-sync postfix is exactly what the timing class is still allowed to own.
        Assert.Null(EchoTravelReceipt.RefuseEchoTimingPatch(
            EchoTravelReceipt.TimingPatchClass, "Behaviour.Gameplay.IdleManager", "Update"));
        // The unrelated refinery automation legitimately owns its own patch on the SAME native
        // method; a blanket "no Echo patch here" rule would be false.
        Assert.Null(EchoTravelReceipt.RefuseEchoTimingPatch(
            "VGEcho.Patches.AutopilotRefineryPatches", "Behaviour.Managers.TravelManager", "TravelToNextWaypoint"));
        Assert.Null(EchoTravelReceipt.RefuseEchoTimingPatch(
            "VGEcho.Patches.AutopilotStackPatches", "Behaviour.Gameplay.IdleManager", "FindActivity"));
    }

    [Fact]
    public void ASuppressedDecisionBodyMustBeAccountedAgainstTheCountedInvocations()
    {
        Assert.Null(EchoTravelReceipt.CheckSuppressionAccounting(4, 4));
        Assert.Contains("unaccounted", EchoTravelReceipt.CheckSuppressionAccounting(4, 3));
        Assert.Contains("unaccounted", EchoTravelReceipt.CheckSuppressionAccounting(0, 1));
    }

    // --- fixture preflight and the autopilot safety cleanup ----------------------------------

    [Fact]
    public void AFixtureThatLoadsWithAutopilotEngagedIsRefusedBeforeAnyUnarmedSetup()
    {
        var session = Guid.NewGuid();
        Assert.Null(EchoTravelReceipt.CheckFixturePreflight("fixture-a", session,
            autoPlayEngaged: false, autoPlayUnlocked: true, requiresEngagement: true));
        var refusal = EchoTravelReceipt.CheckFixturePreflight("fixture-a", session,
            autoPlayEngaged: true, autoPlayUnlocked: true, requiresEngagement: false);
        // The diagnostic names the fixture and session and points at the FIXTURE, not at the
        // suppression accounting that would otherwise fail minutes later.
        Assert.Contains("fixture-a", refusal);
        Assert.Contains(session.ToString(), refusal);
        Assert.Contains("ALREADY ENGAGED", refusal);
        Assert.Contains("refuses to disengage a state it did not create", refusal);
    }

    [Fact]
    public void OnlyACaseThatMustEngageAutopilotRequiresItUnlocked()
    {
        var session = Guid.NewGuid();
        Assert.Contains("never unlocked autopilot", EchoTravelReceipt.CheckFixturePreflight("gate", session,
            autoPlayEngaged: false, autoPlayUnlocked: false, requiresEngagement: true));
        // The quiet window never engages it, so a locked fixture is not refused there.
        Assert.Null(EchoTravelReceipt.CheckFixturePreflight("fixture-a", session,
            autoPlayEngaged: false, autoPlayUnlocked: false, requiresEngagement: false));
    }

    [Fact]
    public void TheAutopilotCleanupWritesOnlyToTheExactOwnedPlayerOfTheOwningSession()
    {
        var owned = Guid.NewGuid();
        Assert.Equal(EchoTravelReceipt.AutopilotRelease.Release, EchoTravelReceipt.DecideAutopilotRelease(
            engagedByProbe: true, ownerStillCurrent: true, ownerAlive: true, owned, owned));
        Assert.Equal(EchoTravelReceipt.AutopilotRelease.NothingEngaged, EchoTravelReceipt.DecideAutopilotRelease(
            engagedByProbe: false, ownerStillCurrent: true, ownerAlive: true, owned, owned));
        // A replacement is never adopted, a destroyed owner is never written, and a replaced
        // session's world is left exactly as the probe found it.
        Assert.Equal(EchoTravelReceipt.AutopilotRelease.OwnerReplaced, EchoTravelReceipt.DecideAutopilotRelease(
            engagedByProbe: true, ownerStillCurrent: false, ownerAlive: true, owned, owned));
        Assert.Equal(EchoTravelReceipt.AutopilotRelease.OwnerDestroyed, EchoTravelReceipt.DecideAutopilotRelease(
            engagedByProbe: true, ownerStillCurrent: true, ownerAlive: false, owned, owned));
        Assert.Equal(EchoTravelReceipt.AutopilotRelease.SessionReplaced, EchoTravelReceipt.DecideAutopilotRelease(
            engagedByProbe: true, ownerStillCurrent: true, ownerAlive: true, owned, Guid.NewGuid()));
        Assert.Equal(EchoTravelReceipt.AutopilotRelease.SessionReplaced, EchoTravelReceipt.DecideAutopilotRelease(
            engagedByProbe: true, ownerStillCurrent: true, ownerAlive: true, owned, null));
    }

    /// <summary>
    /// The cleanup only matters if it is really wired to every path that can leave the autopilot
    /// engaged: both cross-system consumer hooks' fault paths and the phase's own outer finally,
    /// after the failure row is written.
    /// </summary>
    [Fact]
    public void TheAutopilotCleanupIsWiredToTheHookFaultPathsAndTheOuterFinally()
    {
        var source = File.ReadAllText(PilotSourcePath());
        Assert.Equal(2, Regex.Matches(source, @"EchoCrossCase\w+\(consumerCase[^)]*\), ReleaseAutopilotSafely\)").Count);
        // EtGuarded writes the failed row and the fault file BEFORE invoking the cleanup.
        var guard = source.Substring(source.IndexOf("if (fault == null) yield break;", StringComparison.Ordinal));
        int row = guard.IndexOf("EtRecord(caseId", StringComparison.Ordinal);
        int cleanup = guard.IndexOf("onFault?.Invoke();", StringComparison.Ordinal);
        Assert.True(row >= 0 && cleanup > row, "The guard must record the failure before cleaning native state.");
        // The outer finally is the last resort for a fault that skipped a hook, including one raised
        // inside a synchronous API callback.
        int outerFinally = source.IndexOf("        finally\n        {\n            // Last-resort safety cleanup", StringComparison.Ordinal);
        Assert.True(outerFinally > 0, "The phase's outer finally must run the safety cleanup.");
        Assert.Contains("ReleaseAutopilotSafely();\n            EchoDisarm();", source);
        // Every fixture load the phase performs is preflighted before its unarmed setup.
        Assert.Equal(3, Regex.Matches(source, @"RequireFixturePreflight\(").Count - 1);
        // The engagement captures its owner; nothing else writes the native autopilot field.
        Assert.Equal(2, Regex.Matches(source, @"AccessTools\.Field\(_player, ""autoPlay""\)\.SetValue").Count);
    }

    [Fact]
    public void TheDeclaredControlsAreNamedInTheReceiptRow()
    {
        var controls = EchoTravelReceipt.DescribeControls(6, 4, 2, 2, etaSyncDisabled: true,
            EchoTravelReceipt.IdleTimerSeedSeconds, autopilotReleases: 2,
            releasesDeclined: new[] { "SessionReplaced" });
        Assert.Contains("autopilotReleases=2", controls);
        Assert.Contains("autopilotReleasesDeclined=[SessionReplaced]", controls);
        Assert.Contains("EtaSync=false", controls);
        Assert.Contains("idleTimerSeed=300s x6", controls);
        Assert.Contains("suppressedFindActivityBodies=4", controls);
        Assert.Contains("subscriptionReorderings=2", controls);
        Assert.Contains("neverCalled=[ApplyArrivalSnap, Observe, IdleManager.Update, IdleManager.FindActivity]", controls);
    }

    // --- phase evaluation --------------------------------------------------------------------

    [Fact]
    public void EveryRequiredCaseAndSubcaseMustPassWithResolvableEvidence()
    {
        var rows = PassingRows();
        Assert.Null(EchoTravelReceipt.Evaluate(rows, null, Trace(Session)));
        Assert.StartsWith("PASS", EchoTravelReceipt.Summarize(rows, null, Trace(Session)));
    }

    [Fact]
    public void EmptyFailedOrIncompleteCoverageIsNeverAPass()
    {
        var trace = Trace(Session);
        Assert.Contains("empty coverage", EchoTravelReceipt.Evaluate(Array.Empty<TravelStationReceipt.Row>(), null, trace));
        var failed = PassingRows();
        failed[2] = Row(failed[2].Case, TravelStationReceipt.Failed);
        Assert.Contains("Failed cases", EchoTravelReceipt.Evaluate(failed, null, trace));
        var missing = PassingRows().Where(row => row.Case != EchoTravelReceipt.WormholeCase).ToList();
        Assert.Contains("Required case did not run: " + EchoTravelReceipt.WormholeCase, EchoTravelReceipt.Evaluate(missing, null, trace));
        var missingSubcase = PassingRows().Where(row => row.Case != EchoTravelReceipt.ControlsSubcase).ToList();
        Assert.Contains("Required subcase did not run: " + EchoTravelReceipt.ControlsSubcase,
            EchoTravelReceipt.Evaluate(missingSubcase, null, trace));
        var notRun = PassingRows();
        notRun[0] = Row(notRun[0].Case, TravelStationReceipt.NotRun);
        Assert.Contains("Required case is not-run", EchoTravelReceipt.Evaluate(notRun, null, trace));
        var duplicated = PassingRows();
        duplicated.Add(Row(EchoTravelReceipt.GateCase, TravelStationReceipt.Passed));
        Assert.Contains("Required case recorded 2 rows", EchoTravelReceipt.Evaluate(duplicated, null, trace));
        Assert.Contains("Pilot fault", EchoTravelReceipt.Evaluate(PassingRows(), "probe exploded", trace));
        var checkpoint = EchoTravelReceipt.SummarizeIncomplete(PassingRows(), EchoTravelReceipt.GateCase);
        Assert.StartsWith(TravelStationReceipt.Incomplete, checkpoint);
        Assert.DoesNotContain("PASS", checkpoint);
    }

    [Fact]
    public void EvidenceMustResolveToRealEventsOfTheRowSession()
    {
        Assert.Contains("references an event that is not in the trace for its session",
            EchoTravelReceipt.Evaluate(PassingRows(), null, Trace(Guid.NewGuid())));
        var withoutEvidence = PassingRows();
        withoutEvidence[0] = Row(withoutEvidence[0].Case, TravelStationReceipt.Passed, evidence: "");
        Assert.Contains("no observed public events", EchoTravelReceipt.Evaluate(withoutEvidence, null, Trace(Session)));
    }

    [Fact]
    public void ThePhaseNeverClaimsTheReusedTravelPhasesOwnCoverage()
    {
        var summary = EchoTravelReceipt.Summarize(PassingRows(), null, Trace(Session));
        Assert.Contains("phase=" + EchoTravelReceipt.Phase, summary);
        Assert.Contains(TravelCrossSystemReceipt.Phase, summary);
        Assert.Contains("never that the autonomous action executed", summary);
        Assert.Contains("RuntimeQualified=false", summary);
        Assert.Empty(EchoTravelReceipt.RequiredCases.Intersect(TravelCrossSystemReceipt.RequiredCases));
        Assert.Empty(EchoTravelReceipt.RequiredCases.Intersect(TravelStationReceipt.RequiredCases));
        Assert.Empty(EchoTravelReceipt.RequiredCases.Intersect(EchoTravelReceipt.RequiredSubcaseRows));
        // The two consumer probes own the same reused phases, so their identities must stay distinct.
        Assert.Empty(EchoTravelReceipt.RequiredCases.Intersect(AnimaTravelReceipt.RequiredCases));
    }

    // --- budget ------------------------------------------------------------------------------

    [Fact]
    public void TheDeclaredBudgetIsDerivedFromTheCallSitePlanAndFitsTheReservation()
    {
        Assert.Equal(EchoTravelReceipt.PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences), EchoTravelReceipt.PhaseBudgetSeconds);
        Assert.True(EchoTravelReceipt.PhaseBudgetSeconds > 0);
        Assert.True(EchoTravelReceipt.PhaseBudgetSeconds <= EchoTravelReceipt.LauncherReservationSeconds);
        Assert.Equal(EchoTravelReceipt.CallSites.Sum(site => site.Invocations * site.Loads), EchoTravelReceipt.ProbeLoads);
        Assert.Equal(EchoTravelReceipt.CallSites.Sum(site => site.Invocations * site.IdleDecisions), EchoTravelReceipt.IdleDecisionWaits);
        Assert.All(EchoTravelReceipt.CallSites.Where(site => site.Method.StartsWith("EchoCrossCase", StringComparison.Ordinal)),
            site => Assert.Equal(EchoTravelReceipt.CrossSystemCases, site.Invocations));
    }

    /// <summary>
    /// The published budget is only honest while the declared plan matches the pilot's ACTUAL
    /// waiting call sites, so the plan is re-derived here from the pilot source itself.
    /// </summary>
    [Fact]
    public void TheCallSitePlanMatchesThePilotSource()
    {
        var source = File.ReadAllLines(PilotSourcePath());
        var declaration = new Regex(@"^    (?:private|internal)[^=]*?\b(?<name>\w+)\s*\(", RegexOptions.Compiled);
        var counts = EchoTravelReceipt.CallSites.ToDictionary(site => site.Method,
            _ => new int[9]);
        var method = string.Empty;
        var patterns = new (string Token, int Index)[]
        {
            ("foreach (var frame in SpLoad(", 0),
            ("foreach (var frame in Wait(", 1),
            ("foreach (var frame in AwaitEchoPlacement(", 2),
            ("foreach (var frame in EchoQuiesce()", 3),
            ("foreach (var frame in Settle()", 4),
            ("EchoTravelReceipt.UndockSeconds", 5),
            ("EchoTravelReceipt.TravelReadySeconds", 6),
            ("EchoTravelReceipt.ArrivalSeconds", 7),
            ("EchoTravelReceipt.BoundarySeconds", 8),
        };
        int idleDecisionSites = 0;
        foreach (var line in source)
        {
            var match = declaration.Match(line);
            if (match.Success) method = match.Groups["name"].Value;
            if (line.Contains("foreach (var frame in AwaitIdleDecision(", StringComparison.Ordinal)) idleDecisionSites++;
            if (!counts.TryGetValue(method, out var bucket)) continue;
            foreach (var (token, index) in patterns)
                if (line.Contains(token, StringComparison.Ordinal)) bucket[index]++;
        }
        foreach (var site in EchoTravelReceipt.CallSites)
        {
            var bucket = counts[site.Method];
            Assert.Equal((site.Loads, site.Bindings, site.Placements, site.Quiescences, site.Settles,
                site.Undocks, site.TravelReady, site.Arrivals, site.Boundaries),
                (bucket[0], bucket[1], bucket[2], bucket[3], bucket[4], bucket[5], bucket[6], bucket[7], bucket[8]));
        }
        Assert.Equal(EchoTravelReceipt.CallSites.Sum(site => site.IdleDecisions), idleDecisionSites);
        // No waiting call site may live in a pilot method the plan does not account for.
        foreach (var (token, index) in patterns)
            Assert.Equal(EchoTravelReceipt.CallSites.Sum(site => index switch
            {
                0 => site.Loads, 1 => site.Bindings, 2 => site.Placements, 3 => site.Quiescences, 4 => site.Settles,
                5 => site.Undocks, 6 => site.TravelReady, 7 => site.Arrivals, _ => site.Boundaries,
            }), source.Count(line => line.Contains(token, StringComparison.Ordinal)));
    }

    private static string PilotSourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "QualificationRunner", "EchoTravelPilot.cs");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Could not locate tools/QualificationRunner/EchoTravelPilot.cs from " + AppContext.BaseDirectory + ".");
    }
}
