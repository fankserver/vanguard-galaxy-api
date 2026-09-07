using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the ACTUAL-CONSUMER Echo arrival-snap probe (phase
/// <see cref="Phase"/>). It contains no Unity, BepInEx or reflection dependency, so the exact rules
/// that decide PASS/FAIL — what a genuine arrival-snap looks like in the installed consumer's own
/// timer write and in the next native idle decision — are host regressions rather than prose.
///
/// This phase never widens the native travel phases. It REUSES them: the route completions it
/// reasons about are driven and asserted by <see cref="TravelStationReceipt"/>'s and
/// <see cref="TravelCrossSystemReceipt"/>'s own cases, plus one Echo-owned in-system route
/// pair for the supersession and degradation cases. A pass requires the underlying travel cases
/// to pass and demonstrates consumer handling of those facts; it is not additional travel
/// coverage or full in-game acceptance.
/// </summary>
internal static class EchoTravelReceipt
{
    /// <summary>Identity of this consumer-observation phase, distinct from native travel phases.</summary>
    internal const string Phase = "echo-travel-consumer-v1";

    internal const string BindingCase = "arrival-snap-binding";
    internal const string QuietCase = "no-snap-quiet";
    internal const string InSystemCase = "in-system-final-snap";
    internal const string GateCase = "gate-final-snap";
    internal const string WormholeCase = "wormhole-final-snap";
    internal const string SupersessionCase = "earlier-subscriber-supersession";
    internal const string DegradedCase = "snap-stop-degradation";
    /// <summary>Mandatory setup row: the declared, bounded qualification controls this phase used.</summary>
    internal const string ControlsSubcase = "declared-probe-controls";

    internal const string BindingDescription = "The installed Echo 0.7.0 consumer holds a live arrival-snap subscription to the PUBLIC travel surface of the freshly loaded session, its timing configuration entries are present, and its retired timing hook is absent: no Harmony patch declared by AutopilotTimingPatches touches the native route boundary. Echo's unrelated features keep their own patches, which are recorded, not refused.";
    internal const string QuietDescription = "With autopilot disengaged, the whole qualified in-system phase - placement, requests, cancellation, intermediate arrivals AND final route completions - produces no consumer timer write at all, and the native idle cycle takes no snap-driven decision.";
    internal const string InSystemDescription = "A qualified in-system final route completion makes the INSTALLED consumer's own ApplyArrivalSnap run in the SAME frame as the public fact, moving the native idle timer from positive to exactly zero, and the NEXT native IdleManager.Update reaches its FindActivity decision boundary from that zeroed timer.";
    internal const string GateDescription = "The same proof for a qualified cross-system jump-gate final route completion, whose ETA is unavailable and which the retired native hook covered.";
    internal const string WormholeDescription = "The same proof for a qualified cross-system wormhole final route completion.";
    internal const string SupersessionDescription = "When an EARLIER-registered subscriber starts a real native route from inside the same synchronous dispatch of an old RouteCompleted, the installed consumer's own write-time guard refuses the timer write while native travel is active or waypoints remain, leaves the timer untouched, and no snap-driven idle decision happens while the ship is busy; the new route's own completion then snaps under its own distinct operation identity.";
    internal const string DegradedDescription = "Stopping the arrival-snap subscription disables ONLY the snap: a later qualified route completion writes no timer, the consumer keeps its ETA-sync and unrelated patches, and it installs no direct native timing hook as a fallback.";
    internal const string ControlsDescription = "Mandatory setup row recording every declared, bounded control this phase used: the sandbox-only Echo configuration, the native idle-timer seeding, the bounded autopilot engagement, the read-only observation probes, the counted FindActivity body suppression and the controlled subscription reordering, with their exact counts.";

    /// <summary>
    /// The phase passes only when EVERY one of these case identities has exactly one PASSED row.
    /// A missing, not-run or failed required case is a phase failure: a consumer that never wrote a
    /// timer can never report an empty PASS.
    /// </summary>
    internal static readonly string[] RequiredCases =
    {
        BindingCase, QuietCase, InSystemCase, GateCase, WormholeCase, SupersessionCase, DegradedCase
    };

    /// <summary>Mandatory SUBCASE rows, checked by the launcher separately from the case identities.</summary>
    internal static readonly string[] RequiredSubcaseRows = { ControlsSubcase };

    // Declared per-wait deadlines (seconds). These are the SINGLE source the pilot's waits use, and
    // the phase budget is summed from the call-site plan below.
    internal const float ReadinessSeconds = 90;   // shared harness Wait deadline (the probe's own slot loads)
    internal const float SettleSeconds = 2;       // shared harness Settle grace period
    /// <summary>Bounded wait for the public dispatch to be idle and no further fact to arrive.</summary>
    internal const float QuiescenceSeconds = 20;
    /// <summary>Bounded wait for the freshly loaded session's own public placement fact.</summary>
    internal const float PlacementSeconds = 30;
    /// <summary>Native delayTravelAttempt window after a warp start, before travel availability is sampled.</summary>
    internal const float TravelReadySeconds = 4;
    /// <summary>Native undock routine (Undocking -> Leaving) of the Echo-owned route setup.</summary>
    internal const float UndockSeconds = 60;
    /// <summary>One Echo-owned in-system leg until its native arrival.</summary>
    internal const float ArrivalSeconds = 240;
    /// <summary>The TravelToNextWaypoint final-route boundary that produces RouteCompleted.</summary>
    internal const float BoundarySeconds = 60;
    /// <summary>Bounded wait for the next native IdleManager.Update after an observed snap.</summary>
    internal const float IdleDecisionSeconds = 30;
    /// <summary>Positive value the probe seeds into the native idle timer so a case never starts from an expired cycle.</summary>
    internal const float IdleTimerSeedSeconds = 300;
    /// <summary>Process time the launcher reserves for this phase (mirrors $EchoTravelBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 1800;

    /// <summary>The cross-system cases whose consumer evidence this phase takes: gate and wormhole.</summary>
    internal const int CrossSystemCases = 2;
    /// <summary>Echo-owned in-system legs: the superseded route and the route its supersession started, plus the degradation route.</summary>
    internal const int OwnedRoutes = 3;

    /// <summary>
    /// One pilot method that performs bounded waits, with the number of times the phase INVOKES it
    /// and the call sites it contains. The budget is derived from this plan and a host test
    /// re-counts the pilot's actual source call sites against it.
    /// </summary>
    internal sealed class CallSitePlan
    {
        internal string Method { get; }
        internal int Invocations { get; }
        internal int Loads { get; }
        /// <summary>Shared harness readiness waits (service binding), same deadline as a load.</summary>
        internal int Bindings { get; }
        internal int Placements { get; }
        internal int Quiescences { get; }
        internal int Settles { get; }
        internal int Undocks { get; }
        internal int TravelReady { get; }
        internal int Arrivals { get; }
        internal int Boundaries { get; }
        internal int IdleDecisions { get; }
        internal CallSitePlan(string method, int invocations, int loads = 0, int bindings = 0, int placements = 0, int quiescences = 0,
            int settles = 0, int undocks = 0, int travelReady = 0, int arrivals = 0, int boundaries = 0, int idleDecisions = 0)
        {
            Method = method; Invocations = invocations; Loads = loads; Bindings = bindings; Placements = placements;
            Quiescences = quiescences; Settles = settles; Undocks = undocks; TravelReady = travelReady;
            Arrivals = arrivals; Boundaries = boundaries; IdleDecisions = idleDecisions;
        }
    }

    /// <summary>
    /// Every waiting call site of the probe, per method. The two cross-system hooks run once per
    /// cross-system case; everything else once.
    /// </summary>
    internal static readonly CallSitePlan[] CallSites =
    {
        // binding: the fresh in-system session's placement, then a quiescence before the baseline.
        new("EchoInSystemReady", 1, placements: 1, quiescences: 1),
        // no-snap-quiet: one quiescence before the counters are read.
        new("EchoInSystemCompleted", 1, quiescences: 1),
        // per cross-system case: the case session's placement and a quiescence before arming.
        new("EchoCrossCaseReady", CrossSystemCases, placements: 1, quiescences: 1),
        // per cross-system case: a quiescence, then the idle-decision wait for each of the two
        // observed completions (the in-system approach and the cross hop).
        new("EchoCrossCaseCompleted", CrossSystemCases, quiescences: 1, idleDecisions: 2),
        // the Echo-owned session: its own fixture load, readiness, placement, settle and undock.
        new("PrepareOwnedRoutes", 1, loads: 1, bindings: 1, placements: 1, quiescences: 1, settles: 1),
        new("UndockForEcho", 1, undocks: 1),
        // one owned leg: an availability sample, the arrival, the final boundary, a settle and a
        // quiescence. Driven for the superseded leg and for the degradation leg.
        new("DriveOwnedRoute", OwnedRoutes - 1, travelReady: 1, arrivals: 1, boundaries: 1, settles: 1, quiescences: 1),
        // the leg the earlier subscriber started: the same arrival/boundary waits without a drive.
        new("AwaitOwnedRoute", 1, arrivals: 1, boundaries: 1, settles: 1, quiescences: 1),
        // supersession: the idle-decision wait of its positive completion.
        new("CaseSupersession", 1, idleDecisions: 1)
    };

    internal static readonly int ProbeLoads = CallSites.Sum(site => site.Invocations * site.Loads);
    internal static readonly int BindingWaits = CallSites.Sum(site => site.Invocations * site.Bindings);
    internal static readonly int PlacementWaits = CallSites.Sum(site => site.Invocations * site.Placements);
    internal static readonly int QuiescenceSamples = CallSites.Sum(site => site.Invocations * site.Quiescences);
    internal static readonly int Settles = CallSites.Sum(site => site.Invocations * site.Settles);
    internal static readonly int Undocks = CallSites.Sum(site => site.Invocations * site.Undocks);
    internal static readonly int TravelReadySamples = CallSites.Sum(site => site.Invocations * site.TravelReady);
    internal static readonly int ArrivalWaits = CallSites.Sum(site => site.Invocations * site.Arrivals);
    internal static readonly int BoundaryWaits = CallSites.Sum(site => site.Invocations * site.Boundaries);
    internal static readonly int IdleDecisionWaits = CallSites.Sum(site => site.Invocations * site.IdleDecisions);

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    /// <summary>Worst case for this phase ALONE; the reused native phases keep their own reservations.</summary>
    internal static readonly PhaseWait[] PhaseWaits =
    {
        new("probe-slot-load", ReadinessSeconds, ProbeLoads),
        new("service-readiness", ReadinessSeconds, BindingWaits),
        new("session-placement", PlacementSeconds, PlacementWaits),
        new("callback-quiescence", QuiescenceSeconds, QuiescenceSamples),
        new("settle", SettleSeconds, Settles),
        new("undock", UndockSeconds, Undocks),
        new("travel-availability", TravelReadySeconds, TravelReadySamples),
        new("arrival", ArrivalSeconds, ArrivalWaits),
        new("route-boundary", BoundarySeconds, BoundaryWaits),
        new("idle-decision", IdleDecisionSeconds, IdleDecisionWaits)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    // --- observed consumer/native facts -----------------------------------------------------

    /// <summary>
    /// One recorded run of the INSTALLED consumer's own <c>ApplyArrivalSnap</c>, sampled by
    /// read-only probes around it. The probes never call it and never change its decision.
    /// </summary>
    internal readonly struct SnapObservation
    {
        /// <summary>Unity frame the consumer's write ran in.</summary>
        internal int Frame { get; }
        /// <summary>Native idle timer immediately before the consumer's write.</summary>
        internal float TimerBefore { get; }
        /// <summary>Native idle timer immediately after it.</summary>
        internal float TimerAfter { get; }
        /// <summary>Native <c>TravelActive()</c> read by the probe at the same boundary.</summary>
        internal bool TravelActive { get; }
        /// <summary>Remaining native waypoints read by the probe at the same boundary; negative when unreadable.</summary>
        internal int RemainingWaypoints { get; }
        internal SnapObservation(int frame, float timerBefore, float timerAfter, bool travelActive, int remainingWaypoints)
        {
            Frame = frame; TimerBefore = timerBefore; TimerAfter = timerAfter;
            TravelActive = travelActive; RemainingWaypoints = remainingWaypoints;
        }
        internal bool WroteTimer => TimerAfter == 0f && TimerBefore != 0f;
        internal string Describe() => "frame=" + Frame.ToString(CultureInfo.InvariantCulture)
            + ",timer=" + TimerBefore.ToString("F3", CultureInfo.InvariantCulture)
            + "->" + TimerAfter.ToString("F3", CultureInfo.InvariantCulture)
            + ",travelActive=" + TravelActive + ",waypoints=" + RemainingWaypoints.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One recorded native <c>IdleManager.Update</c> tick.</summary>
    internal readonly struct IdleObservation
    {
        internal int Frame { get; }
        internal float TimerBefore { get; }
        internal float TimerAfter { get; }
        /// <summary>Whether the native update really reached its <c>FindActivity</c> decision.</summary>
        internal bool FindActivityInvoked { get; }
        internal IdleObservation(int frame, float timerBefore, float timerAfter, bool findActivityInvoked)
        {
            Frame = frame; TimerBefore = timerBefore; TimerAfter = timerAfter; FindActivityInvoked = findActivityInvoked;
        }
        internal string Describe() => "frame=" + Frame.ToString(CultureInfo.InvariantCulture)
            + ",timer=" + TimerBefore.ToString("F3", CultureInfo.InvariantCulture)
            + "->" + TimerAfter.ToString("F3", CultureInfo.InvariantCulture)
            + ",findActivity=" + FindActivityInvoked;
    }

    /// <summary>The witnessed public fact a case attributes its consumer evidence to.</summary>
    internal readonly struct RouteFact
    {
        internal Guid SessionId { get; }
        internal Guid OperationId { get; }
        internal long Sequence { get; }
        internal int Frame { get; }
        internal RouteFact(Guid sessionId, Guid operationId, long sequence, int frame)
        {
            SessionId = sessionId; OperationId = operationId; Sequence = sequence; Frame = frame;
        }
        internal string Describe() => "op=" + OperationId + ",seq=" + Sequence.ToString(CultureInfo.InvariantCulture)
            + ",frame=" + Frame.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A positive snap: the consumer's own write ran in the SAME frame as the public fact it
    /// belongs to, moved the native timer from strictly positive to exactly zero while the native
    /// world really was quiet, and the NEXT native idle tick reached its FindActivity decision from
    /// that zeroed timer. Any of those missing is a failure, never a softer pass.
    /// </summary>
    internal static string? CheckPositiveSnap(RouteFact fact, IReadOnlyList<SnapObservation> snaps,
        IReadOnlyList<IdleObservation> idle, Guid session)
    {
        if (fact.SessionId != session) return "The route completion belongs to session " + fact.SessionId + ", not the case session " + session + ".";
        var owned = snaps.Where(snap => snap.Frame == fact.Frame).ToArray();
        if (owned.Length == 0)
            return "The installed consumer never ran its own timer write for " + fact.Describe()
                + " (observed writes: [" + string.Join("; ", snaps.Select(snap => snap.Describe())) + "]).";
        if (owned.Length != 1) return "The consumer's timer write ran " + owned.Length + " times in the fact's frame.";
        var apply = owned[0];
        if (apply.TimerBefore <= 0f)
            return "The native idle timer was not positive before the write (" + apply.Describe() + "), so a zero afterwards proves nothing.";
        if (apply.TimerAfter != 0f) return "The consumer did not zero the native idle timer: " + apply.Describe() + ".";
        if (apply.TravelActive || apply.RemainingWaypoints != 0)
            return "The consumer wrote the timer while the native world was still travelling: " + apply.Describe() + ".";
        var next = idle.Where(tick => tick.Frame > apply.Frame).OrderBy(tick => tick.Frame).ToArray();
        if (next.Length == 0) return "No native idle update was observed after the write at frame " + apply.Frame + ".";
        if (next[0].TimerBefore != 0f)
            return "The next native idle update did not start from the zeroed timer: " + next[0].Describe() + ".";
        if (!next[0].FindActivityInvoked)
            return "The next native idle update did not reach its FindActivity decision: " + next[0].Describe() + ".";
        return null;
    }

    /// <summary>
    /// A quiet window: the consumer performed NO timer write although the qualified phase really
    /// produced the facts, and the native idle cycle took no snap-driven decision. An empty window
    /// can never satisfy it.
    /// </summary>
    internal static string? CheckQuietWindow(IReadOnlyList<SnapObservation> snaps, IReadOnlyList<IdleObservation> idle,
        int routeCompletions, int otherFacts)
    {
        if (routeCompletions <= 0) return "The window carried no final route completion, so a no-snap claim would be vacuous.";
        if (otherFacts <= 0) return "The window carried no request/cancellation/arrival fact, so a no-snap claim would be vacuous.";
        if (snaps.Count != 0)
            return "The consumer ran " + snaps.Count + " timer write(s) in a window that must stay quiet: ["
                + string.Join("; ", snaps.Select(snap => snap.Describe())) + "].";
        var decided = idle.FirstOrDefault(tick => tick.FindActivityInvoked && tick.TimerBefore == 0f);
        if (decided.FindActivityInvoked)
            return "A snap-driven native idle decision happened in a quiet window: " + decided.Describe() + ".";
        return null;
    }

    /// <summary>
    /// A refused write: the consumer's own write-time guard ran and left the native timer exactly
    /// as it found it, because the world had moved on. A refusal that also zeroed the timer, or a
    /// refusal recorded while the world was actually quiet, is a failure.
    /// </summary>
    internal static string? CheckRefusedSnap(RouteFact fact, IReadOnlyList<SnapObservation> snaps,
        IReadOnlyList<IdleObservation> idle)
    {
        var owned = snaps.Where(snap => snap.Frame == fact.Frame).ToArray();
        if (owned.Length != 1)
            return "Expected exactly one consumer write attempt in the superseded completion's frame, observed " + owned.Length + ".";
        var apply = owned[0];
        if (!apply.TravelActive && apply.RemainingWaypoints == 0)
            return "The earlier subscriber did not actually leave native travel busy: " + apply.Describe() + "; the refusal would prove nothing.";
        if (apply.TimerAfter != apply.TimerBefore)
            return "The consumer changed the native idle timer while refusing the write: " + apply.Describe() + ".";
        if (apply.TimerBefore <= 0f)
            return "The native idle timer was not positive at the refusal (" + apply.Describe() + "), so an unchanged value proves nothing.";
        var decided = idle.FirstOrDefault(tick => tick.Frame > apply.Frame && tick.FindActivityInvoked && tick.TimerBefore == 0f);
        if (decided.FindActivityInvoked)
            return "A snap-driven native idle decision happened while the ship was still busy: " + decided.Describe() + ".";
        return null;
    }

    /// <summary>
    /// The controlled subscription reordering really produced exactly one live consumer observer,
    /// registered after the probe's earlier one, with the consumer's previous observer disposed.
    /// </summary>
    internal static string? CheckSubscriptionReorder(bool previousDisposed, bool consumerListening, bool earlierRegisteredFirst)
    {
        if (!previousDisposed) return "The consumer's previous arrival-snap observer was not disposed before the reordering.";
        if (!earlierRegisteredFirst) return "The probe's observer was not registered ahead of the consumer's fresh subscription.";
        if (!consumerListening) return "The consumer's freshly bound arrival-snap observer is not listening.";
        return null;
    }

    /// <summary>
    /// Null when a live Harmony patch owned by the consumer is acceptable, otherwise the exact
    /// reason it is refused.
    ///
    /// <para>Arrival-snap must use the public travel observer, not a timing-class patch on
    /// <c>TravelManager.TravelToNextWaypoint</c>. Unrelated refinery/auto-refine routing may patch
    /// that method from its own class, so refusal must include the declaring patch class rather
    /// than reject every Echo patch on the method.</para>
    /// </summary>
    internal const string TimingPatchClass = "VGEcho.Patches.AutopilotTimingPatches";
    internal static string? RefuseEchoTimingPatch(string declaringPatchClass, string patchedType, string patchedMethod)
    {
        var patchClass = declaringPatchClass ?? string.Empty;
        var type = patchedType ?? string.Empty;
        var method = patchedMethod ?? string.Empty;
        if (patchClass != TimingPatchClass) return null;
        // The timing class may patch IdleManager.Update for ETA-sync; any other patch declared
        // by that class is refused.
        if (type == "Behaviour.Gameplay.IdleManager" && method == "Update") return null;
        return "retired timing hook restored: " + patchClass + " patches " + type + "." + method;
    }

    /// <summary>
    /// PREFLIGHT for every fixture this phase loads, evaluated BEFORE any unarmed setup wait.
    ///
    /// <para>The suppression accounting (<see cref="CheckSuppressionAccounting"/>) is only meaningful
    /// while every native idle decision outside an armed window is impossible, which requires the
    /// loaded fixture's own autopilot to be disengaged. A save that loads with autopilot ENGAGED
    /// would take autonomous decisions during the multi-minute unarmed setup, and the phase would
    /// fail much later with a diagnostic pointing at the suppression logic instead of at the
    /// fixture. So it is refused here, with the slot and session named.</para>
    ///
    /// <para>It fails CLOSED: the probe never disengages an autopilot state it did not create, and
    /// never relaxes the no-autonomous-decision signal to accommodate one.</para>
    /// </summary>
    internal static string? CheckFixturePreflight(string slot, Guid session, bool autoPlayEngaged,
        bool autoPlayUnlocked, bool requiresEngagement)
    {
        if (autoPlayEngaged)
            return "Fixture '" + slot + "' (session " + session + ") loaded with the native autopilot ALREADY ENGAGED. "
                + "This phase requires an autopilot-off fixture: every idle decision outside its own armed windows must be "
                + "impossible, and the probe refuses to disengage a state it did not create. Prepare a save whose autopilot "
                + "is off, or qualify this consumer on a different fixture.";
        if (requiresEngagement && !autoPlayUnlocked)
            return "Fixture '" + slot + "' (session " + session + ") has never unlocked autopilot, so the consumer's own "
                + "arrival-snap gate can never open; this case cannot be qualified on this save.";
        return null;
    }

    /// <summary>What the probe may do with the autopilot it engaged, when a case ends or faults.</summary>
    internal enum AutopilotRelease
    {
        /// <summary>The probe never engaged it; nothing to undo.</summary>
        NothingEngaged,
        /// <summary>The exact owned player is still the live current one in the owning session.</summary>
        Release,
        /// <summary>Another player object is current now; a replacement is never adopted.</summary>
        OwnerReplaced,
        /// <summary>The owned player was destroyed (Unity fake-null); nothing to write.</summary>
        OwnerDestroyed,
        /// <summary>The session was replaced; the live world is not the one the probe engaged.</summary>
        SessionReplaced,
    }

    /// <summary>
    /// Safety cleanup decision for the autopilot the probe engaged. A failure inside a consumer hook
    /// must not leave the native autopilot running past the case, but the cleanup may only write to
    /// the EXACT player it engaged, while that player is still the live current one and its session
    /// is still current. A replaced or destroyed owner, or a replaced session, is left untouched.
    /// </summary>
    internal static AutopilotRelease DecideAutopilotRelease(bool engagedByProbe, bool ownerStillCurrent,
        bool ownerAlive, Guid ownedSession, Guid? currentSession)
    {
        if (!engagedByProbe) return AutopilotRelease.NothingEngaged;
        if (!ownerAlive) return AutopilotRelease.OwnerDestroyed;
        if (currentSession == null || currentSession.Value != ownedSession) return AutopilotRelease.SessionReplaced;
        if (!ownerStillCurrent) return AutopilotRelease.OwnerReplaced;
        return AutopilotRelease.Release;
    }

    /// <summary>The declared, bounded controls this phase used, recorded as a mandatory setup row.</summary>
    internal static string DescribeControls(int seededTimerWrites, int suppressedFindActivity, int autopilotEngagements,
        int reorderings, bool etaSyncDisabled, float seedSeconds, int autopilotReleases = 0,
        IReadOnlyList<string>? releasesDeclined = null)
        => "autopilotReleases=" + autopilotReleases.ToString(CultureInfo.InvariantCulture)
            + "; autopilotReleasesDeclined=[" + string.Join(",", releasesDeclined ?? Array.Empty<string>()) + "]"
            + "; sandboxConfig=[TimingEnabled=true,ArrivalSnap=true,EtaSync=" + (etaSyncDisabled ? "false" : "true")
            + "]; idleTimerSeed=" + seedSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s x" + seededTimerWrites
            + "; autopilotEngagements=" + autopilotEngagements
            + "; suppressedFindActivityBodies=" + suppressedFindActivity
            + "; subscriptionReorderings=" + reorderings
            + "; observationProbes=read-only around VGEcho ApplyArrivalSnap and native IdleManager.Update"
            + "; neverCalled=[ApplyArrivalSnap, Observe, IdleManager.Update, IdleManager.FindActivity]";

    /// <summary>
    /// A suppressed FindActivity body proves the DECISION BOUNDARY was reached, never that the
    /// autonomous action ran. The counts must agree: the phase may not claim more decisions than it
    /// suppressed, and it may not have suppressed a body it never counted.
    /// </summary>
    internal static string? CheckSuppressionAccounting(int findActivityInvocations, int suppressedBodies)
    {
        if (suppressedBodies != findActivityInvocations)
            return "The counted native FindActivity invocations (" + findActivityInvocations
                + ") and the suppressed bodies (" + suppressedBodies + ") disagree; the decision-boundary claim is unaccounted.";
        return null;
    }

    // --- shared receipt plumbing -----------------------------------------------------------

    /// <summary>
    /// Every passed case must reference real observed events of its own session, and every required
    /// case must reference at least one.
    /// </summary>
    internal static string? CheckEvidence(IReadOnlyList<TravelStationReceipt.Row> rows, IReadOnlyList<string> eventRows)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in eventRows)
        {
            var columns = row.Split('\t');
            if (columns.Length != TravelStationReceipt.EventsHeader.Split('\t').Length) return "Malformed event row: " + row;
            observed.Add(columns[1] + ":" + columns[0] + ":" + columns[3]);
        }
        foreach (var row in rows.Where(candidate => candidate.Status == TravelStationReceipt.Passed))
        {
            var references = TravelStationReceipt.EvidenceReferences(row.Evidence);
            if (references.Count == 0)
            {
                if (RequiredCases.Contains(row.Case) || RequiredSubcaseRows.Contains(row.Case))
                    return "Required case has no observed public events: " + row.Case + ".";
                continue;
            }
            foreach (var reference in references)
                if (!observed.Contains(reference.Key + ":" + reference.Value + ":" + row.Session))
                    return "Case " + row.Case + " references an event that is not in the trace for its session: "
                        + reference.Key + ":" + reference.Value + ".";
        }
        return null;
    }

    /// <summary>Null when the phase is satisfied, otherwise the exact reason it is not.</summary>
    internal static string? Evaluate(IReadOnlyList<TravelStationReceipt.Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        if (rows.Count == 0) return "No case rows were recorded; empty coverage is not a pass.";
        var unknown = rows.FirstOrDefault(row => row.Status != TravelStationReceipt.Passed
            && row.Status != TravelStationReceipt.Failed && row.Status != TravelStationReceipt.NotRun);
        if (unknown != null) return "Unknown case status '" + unknown.Status + "' for " + unknown.Case + ".";
        var failed = rows.Where(row => row.Status == TravelStationReceipt.Failed).Select(row => row.Case).ToArray();
        if (failed.Length > 0) return "Failed cases: " + string.Join(", ", failed) + ".";
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            if (matches.Length == 0) return "Required case did not run: " + required + ".";
            if (matches.Length > 1) return "Required case recorded " + matches.Length + " rows: " + required + ".";
            if (matches[0].Status != TravelStationReceipt.Passed) return "Required case is " + matches[0].Status + ": " + required + ".";
        }
        foreach (var subcase in RequiredSubcaseRows)
        {
            var matches = rows.Where(row => row.Case == subcase).ToArray();
            if (matches.Length == 0) return "Required subcase did not run: " + subcase + ".";
            if (matches.Length > 1) return "Required subcase recorded " + matches.Length + " rows: " + subcase + ".";
            if (matches[0].Status != TravelStationReceipt.Passed) return "Required subcase is " + matches[0].Status + ": " + subcase + ".";
        }
        var evidence = CheckEvidence(rows, eventRows);
        if (evidence != null) return evidence;
        if (!string.IsNullOrEmpty(fault)) return "Pilot fault: " + TravelStationReceipt.Clean(fault);
        return null;
    }

    /// <summary>The receipt written while cases are still running: never PASS.</summary>
    internal static string SummarizeIncomplete(IReadOnlyList<TravelStationReceipt.Row> rows, string activeCase)
    {
        var text = new StringBuilder();
        text.AppendLine(TravelStationReceipt.Incomplete)
            .AppendLine("phase=" + Phase)
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("required-subcases=" + string.Join(",", RequiredSubcaseRows))
            .AppendLine("activeCase=" + TravelStationReceipt.Clean(activeCase))
            .AppendLine("rows=" + rows.Count + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun))
            .AppendLine("result=pilot still running or externally terminated; this is not a pass.");
        return text.ToString();
    }

    internal static string Summarize(IReadOnlyList<TravelStationReceipt.Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        var failure = Evaluate(rows, fault, eventRows);
        var text = new StringBuilder();
        text.AppendLine(failure == null ? "PASS" : "FAIL")
            .AppendLine("phase=" + Phase)
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("required-subcases=" + string.Join(",", RequiredSubcaseRows))
            .AppendLine("rows=" + rows.Count
                + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun));
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            text.AppendLine("required-case " + required + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        foreach (var subcase in RequiredSubcaseRows)
        {
            var matches = rows.Where(row => row.Case == subcase).ToArray();
            text.AppendLine("required-subcase " + subcase + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        var optional = rows.Where(row => !RequiredCases.Contains(row.Case) && !RequiredSubcaseRows.Contains(row.Case)).ToArray();
        text.AppendLine("optional-not-run=" + string.Join(",", optional.Where(row => row.Status == TravelStationReceipt.NotRun).Select(row => row.Case)));
        text.AppendLine("fault=" + (string.IsNullOrEmpty(fault) ? "none" : TravelStationReceipt.Clean(fault)));
        text.AppendLine("result=" + (failure ?? "phase satisfied"));
        text.AppendLine("Actual-consumer evidence over the public travel surface only. The route completions it reacts to are qualified by "
            + TravelStationReceipt.Phase + " and " + TravelCrossSystemReceipt.Phase
            + " plus this phase's own owned in-system legs; a suppressed FindActivity body proves the native idle DECISION BOUNDARY was reached, never that the autonomous action executed.");
        text.AppendLine("RuntimeQualified=false; #12 open pending owner in-game qualification and the remaining consumer reconciliation.");
        return text.ToString();
    }
}
