using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

// Actual-consumer travel qualification probe, phase anima-travel-consumer-v1.
//
// It drives NOTHING of its own on the native travel surface. The gate and wormhole arrivals it
// compares against are driven and asserted by the already qualified phases
// travel-in-system-station-v1 and travel-cross-system-v1; this probe REUSES them in place, wraps
// them so they run before the mission pilot permanently disposes the consumer's observer, and
// checks what the INSTALLED consumer did with those public facts: its own visited-system registry,
// its own v4 sidecar, its session/leg latch and its documented degraded state.
//
// It never calls the consumer's recording API, never fabricates an arrival and never writes a
// consumer registry entry. Its only writes are ordinary sandbox saves through the harness's own
// vanilla save helper and ordinary sandbox slot loads.
public sealed partial class Plugin
{
    internal bool AnimaTravelProbeSelected => File.Exists(Path.Combine(_root!, "anima-travel.enabled"));
    private readonly List<TravelStationReceipt.Row> _atRows = new();
    private readonly List<string> _atEvents = new();
    // Non-null only while the probe owns a live subscription. Every hook the reused travel phases
    // call is inert when it is null, so an unselected run behaves exactly as before.
    private List<TravelTransition>? _atFacts;
    private string _atCase = "phase-start";
    private string _atDescription = "The consumer probe is preparing its first case.";
    private Guid _atWindowSession;
    private int _atWindowOffset;
    private List<AnimaTravelReceipt.VisitRecord> _atBaseline = new();
    private List<AnimaTravelReceipt.VisitRecord> _atFixtureBaseline = new();
    // The consumer case whose window is currently open, set only by a successful ready hook.
    private string? _atOpenCase;

    private void AtCase(string id, string description) { _atCase = id; _atDescription = description; }
    private void AtEndCase() { AtCase(TravelStationReceipt.NoActiveCase, "No consumer case is observing."); AtCheckpoint(); }
    private void AtRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
    {
        _atRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));
        AtCheckpoint();
    }

    // Incremental, atomic checkpoint after every case: an external termination can then only leave
    // INCOMPLETE evidence behind, never a stale PASS and never an empty directory.
    private void AtCheckpoint()
    {
        WriteAtomic("anima-travel-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_atRows.Select(row => row.ToTsv())));
        WriteAtomic("anima-travel-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_atEvents));
        WriteAtomic("anima-travel.txt", new[] { AnimaTravelReceipt.SummarizeIncomplete(_atRows, _atCase) });
    }

    private IEnumerable<object?> CheckAnimaTravelConsumer()
    {
        if (!AnimaTravelProbeSelected) yield break;
        var run = RunAnimaTravelConsumer().GetEnumerator();
        string? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
            // Iterators cannot catch around a yield, so the probe body is stepped explicitly: a
            // fault (including a failure of a reused travel phase) is attributed to the consumer
            // case that was observing and never loses its diagnostics.
            try
            {
                moved = run.MoveNext();
                if (moved) current = run.Current;
            }
            catch (Exception error) { fault = error.ToString(); break; }
            if (!moved) break;
            yield return current;
        }
        run.Dispose();
        if (fault != null)
        {
            _atRows.Add(new TravelStationReceipt.Row(_atCase, _atDescription, TravelStationReceipt.Failed, "",
                _api?.CurrentSession?.Id.ToString() ?? "", "", "", fault.Split('\n')[0].Trim()));
        }
        WriteAtomic("anima-travel-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_atRows.Select(row => row.ToTsv())));
        WriteAtomic("anima-travel-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_atEvents));
        var failure = AnimaTravelReceipt.Evaluate(_atRows, fault, _atEvents);
        WriteAtomic("anima-travel.txt", new[] { AnimaTravelReceipt.Summarize(_atRows, fault, _atEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "anima-travel-fault.txt"), fault);
        Require(failure == null, "Actual-consumer travel phase " + AnimaTravelReceipt.Phase + " failed: " + failure);
        // Recorded in result.txt AFTER both reused travel phases and BEFORE the mission pilot, so
        // the phase ordering itself is externally checkable evidence.
        Passed(AnimaTravelReceipt.Phase);
    }

    private IEnumerable<object?> RunAnimaTravelConsumer()
    {
        Require(AnimaSelected, "The consumer travel probe requires the installed Anima consumer selection.");
        Require(TravelStationSelected && TravelCrossSystemSelected,
            "The consumer travel probe requires both qualified native travel phases, whose arrivals it compares against.");
        Require(TravelWormholeFixtureSelected,
            "The consumer travel probe requires the opt-in wormhole fixture selection; without it the wormhole arrival can never be witnessed.");
        Require(ModApi.Travel != null, "Travel public service not exposed.");
        Require(ModApi.Services.Travel.Availability.IsAvailable, "native-travel capability not available.");
        Require(!ModApi.Travel!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        Require(AnimaTravelReceipt.ReadinessSeconds == WaitDeadlineSeconds && AnimaTravelReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(AnimaTravelReceipt.PhaseBudgetSeconds <= AnimaTravelReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        // The reused phases must still be pending: this probe exists to run them BEFORE the mission
        // pilot's final StopProvider permanently disposes the consumer's visit observer.
        Require(_travelStationPending && _travelCrossSystemPending,
            "The reused native travel phases already ran; the consumer probe must own their ordering.");
        var facts = new List<TravelTransition>();
        // Observed with NO foreign filter: every fact is captured with its session, operation and
        // public sequence, and a foreign fact is rejected by the validators instead of dropped.
        using (ModApi.Travel!.Subscribe("qualification.anima-travel", fact =>
        {
            facts.Add(fact);
            _atEvents.Add(TravelStationReceipt.TravelEventRow(_atCase, fact));
        }))
        {
            _atFacts = facts;
            try
            {
                AtCheckpoint();
                foreach (var frame in CheckTravelStation()) yield return frame;
                foreach (var frame in CheckTravelCrossSystem()) yield return frame;
                foreach (var frame in CaseRecordingDegraded()) yield return frame;
            }
            finally { _atFacts = null; }
        }
    }

    // --- hooks called by the reused native travel phases -----------------------------------

    // A consumer case that runs INSIDE a reused travel phase records its own failure in its own
    // receipt instead of faulting that phase: a consumer defect must never be attributed to the
    // native travel phase whose case actually passed. The consumer phase still fails, because a
    // failed row can never satisfy its required case.
    private IEnumerable<object?> AtGuarded(string caseId, string description, IEnumerable<object?> body)
    {
        var run = body.GetEnumerator();
        Exception? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
            try
            {
                moved = run.MoveNext();
                if (moved) current = run.Current;
            }
            catch (Exception error) { fault = error; break; }
            if (!moved) break;
            yield return current;
        }
        run.Dispose();
        if (fault == null) yield break;
        AtRecord(caseId, description, TravelStationReceipt.Failed, "", _api?.CurrentSession?.Id, null, "",
            fault.Message.Split('\n')[0].Trim());
        File.WriteAllText(Path.Combine(_root!, "anima-travel-fault.txt"), fault.ToString());
    }

    /// <summary>
    /// The in-system phase reached its own freshly loaded, bound session. The consumer's binding is
    /// recorded here and the quiet window opens on that session's first public fact.
    /// </summary>
    internal IEnumerable<object?> AnimaTravelInSystemReady(Guid session)
    {
        if (_atFacts == null) yield break;
        foreach (var frame in AtGuarded(AnimaTravelReceipt.BindingCase, AnimaTravelReceipt.BindingDescription,
            InSystemReady(session))) yield return frame;
    }

    private IEnumerable<object?> InSystemReady(Guid session)
    {
        AtCase(AnimaTravelReceipt.BindingCase, AnimaTravelReceipt.BindingDescription);
        foreach (var frame in AwaitPlacement(session)) yield return frame;
        foreach (var frame in Quiesce()) yield return frame;
        _atWindowSession = session;
        _atWindowOffset = SessionWindowOffset(session);
        _atBaseline = AnimaVisited();
        var anima = AnimaPlugin;
        Require(anima.enabled && (bool)SpGet(anima, "_active")!, "The installed consumer is not active.");
        Require((bool)SpGet(anima, "VisitHistoryRecording")!, "The installed consumer is not recording witnessed visits.");
        var observer = SpGet(anima, "_visitObserver");
        Require(observer != null && (bool)SpGet(observer!, "IsRecording")!, "The consumer's visit observer is absent or not recording.");
        Require(ReferenceEquals(SpGet(AnimaType("VGAnima.Patches.SaveLoadPatch"), "VisitObserver"), observer),
            "The consumer's load-safety hook does not own the same live visit observer.");
        Require(ModApi.Travel!.SessionId == session, "The public travel service is not bound to the phase's session.");
        var patches = ConsumerTravelPatches();
        Require(patches.Length == 0, "The consumer installed a direct native travel hook: " + string.Join(", ", patches));
        // The restored registry must be exactly what the loaded slot's own sidecar carries. The
        // fixture's own schema version is recorded, not required: only what the consumer WRITES
        // during this phase is a v4 claim.
        var restored = AnimaSidecarVisited("fixture-a", out int version, out bool present);
        var mismatch = AnimaTravelReceipt.CheckSameHistory(restored, _atBaseline, "restored fixture baseline");
        Require(mismatch == null, mismatch!);
        var placements = Window(_atWindowOffset).Where(fact => fact.Kind == TravelTransitionKind.InitialPlacement).ToArray();
        AtRecord(AnimaTravelReceipt.BindingCase, AnimaTravelReceipt.BindingDescription, TravelStationReceipt.Passed,
            "consumer=" + anima.Info.Metadata.GUID + " " + anima.Info.Metadata.Version, session, null,
            TravelStationReceipt.Evidence(placements, null),
            "recording=true; capability=native-travel available; consumerTravelPatches=0; fixtureSidecar="
            + (present ? "present v" + version : "absent")
            + "; baseline=" + AnimaTravelReceipt.Describe(_atBaseline));
        AtCase(AnimaTravelReceipt.QuietCase, AnimaTravelReceipt.QuietDescription);
        _atOpenCase = AnimaTravelReceipt.QuietCase;
    }

    /// <summary>
    /// The in-system phase finished all six of its own required cases. None of its facts may have
    /// grown the consumer's visited-system history.
    /// </summary>
    internal IEnumerable<object?> AnimaTravelInSystemCompleted()
    {
        if (_atFacts == null || _atOpenCase != AnimaTravelReceipt.QuietCase) yield break;
        _atOpenCase = null;
        foreach (var frame in AtGuarded(AnimaTravelReceipt.QuietCase, AnimaTravelReceipt.QuietDescription,
            InSystemCompleted())) yield return frame;
    }

    private IEnumerable<object?> InSystemCompleted()
    {
        foreach (var frame in Quiesce()) yield return frame;
        var window = Window(_atWindowOffset);
        var failure = AnimaTravelReceipt.CheckQuietWindow(window, _atWindowSession);
        Require(failure == null, failure!);
        var after = AnimaVisited();
        failure = AnimaTravelReceipt.CheckNoVisitGrowth(_atBaseline, after, "in-system phase facts");
        Require(failure == null, failure!);
        AtRecord(AnimaTravelReceipt.QuietCase, AnimaTravelReceipt.QuietDescription, TravelStationReceipt.Passed,
            "history=" + AnimaTravelReceipt.Describe(after), _atWindowSession, null,
            TravelStationReceipt.Evidence(window, null),
            "phase=" + TravelStationReceipt.Phase + "; observedFacts=" + window.Count
            + "; kinds=" + string.Join("/", window.Select(fact => fact.Kind.ToString()).Distinct().OrderBy(kind => kind, StringComparer.Ordinal))
            + "; visitedSystems=" + after.Count);
        AtEndCase();
    }

    /// <summary>The cross-system phase reached the freshly loaded session its own case will drive from.</summary>
    internal IEnumerable<object?> AnimaTravelCrossCaseReady(string crossCase, Guid session)
    {
        if (_atFacts == null) yield break;
        var consumerCase = ConsumerCaseFor(crossCase);
        if (consumerCase == null) yield break;
        foreach (var frame in AtGuarded(consumerCase, ConsumerDescriptionFor(consumerCase),
            CrossCaseReady(consumerCase, session))) yield return frame;
    }

    private IEnumerable<object?> CrossCaseReady(string consumerCase, Guid session)
    {
        AtCase(consumerCase, ConsumerDescriptionFor(consumerCase));
        foreach (var frame in AwaitPlacement(session)) yield return frame;
        foreach (var frame in Quiesce()) yield return frame;
        _atWindowSession = session;
        _atWindowOffset = SessionWindowOffset(session);
        _atBaseline = AnimaVisited();
        _atFixtureBaseline = _atBaseline;
        Require((bool)SpGet(AnimaPlugin, "VisitHistoryRecording")!, "The consumer stopped recording before the cross-system case.");
        Require(ModApi.Travel!.SessionId == session, "The public travel service is not bound to the case's session.");
        // The freshly loaded slot's own restored history is the baseline; the case's delta is
        // measured against it and never against an earlier case's world.
        var restored = AnimaSidecarVisited("fixture-a", out _, out bool present);
        var mismatch = AnimaTravelReceipt.CheckSameHistory(restored, _atBaseline, "restored fixture baseline");
        Require(mismatch == null, mismatch!);
        Logger.LogInfo("QA anima-travel baseline for " + consumerCase + ": sidecar=" + (present ? "present" : "absent")
            + "; " + AnimaTravelReceipt.Describe(_atBaseline));
        _atOpenCase = consumerCase;
    }

    /// <summary>
    /// The cross-system phase passed its own case. Everything from here is consumer evidence:
    /// the single visit increment, its persistence through a real save, the restored counts after
    /// a reload and the rollback to the earlier fixture's own counts.
    /// </summary>
    internal IEnumerable<object?> AnimaTravelCrossCaseCompleted(string crossCase)
    {
        if (_atFacts == null) yield break;
        var consumerCase = ConsumerCaseFor(crossCase);
        if (consumerCase == null || _atOpenCase != consumerCase) yield break;
        _atOpenCase = null;
        foreach (var frame in AtGuarded(consumerCase, ConsumerDescriptionFor(consumerCase),
            CrossCaseCompleted(consumerCase))) yield return frame;
    }

    private IEnumerable<object?> CrossCaseCompleted(string consumerCase)
    {
        bool gate = consumerCase == AnimaTravelReceipt.GateCase;
        var mode = gate ? TravelMode.JumpGate : TravelMode.Wormhole;
        var session = _atWindowSession;
        var baseline = _atBaseline;
        var fixtureBaseline = _atFixtureBaseline;
        foreach (var frame in Quiesce()) yield return frame;
        var window = Window(_atWindowOffset);
        var failure = AnimaTravelReceipt.CheckArrivalWindow(window, session, mode, out var arrival);
        Require(failure == null, failure!);
        var evidence = AnimaTravelReceipt.ArrivalEvidence.From(arrival!);
        var recorded = AnimaVisited();
        failure = AnimaTravelReceipt.CheckVisitIncrement(baseline, recorded, evidence);
        Require(failure == null, failure!);
        failure = AnimaTravelReceipt.CheckHistoryPreserved(fixtureBaseline, recorded, "witnessed arrival");
        Require(failure == null, failure!);

        // A real vanilla save through the harness's own helper; the consumer's own save hook writes
        // its sidecar. The probe never writes a consumer file itself.
        var slot = "qa-anima-travel-" + (gate ? "gate" : "wormhole");
        Save(slot, LifecycleEventKind.SaveSucceeded);
        var sidecarPath = AnimaSidecarPath(slot);
        failure = AnimaTravelReceipt.CheckSidecarPath(sidecarPath, _saveRoot!, slot);
        Require(failure == null, failure!);
        var persisted = AnimaSidecarVisited(slot, out int version, out bool present);
        Require(present, "The consumer wrote no sidecar beside the saved slot " + slot + ".");
        failure = AnimaTravelReceipt.CheckSidecarVersion(version, AnimaSidecarVersion);
        Require(failure == null, failure!);
        failure = AnimaTravelReceipt.CheckSameHistory(recorded, persisted, "persisted sidecar");
        Require(failure == null, failure!);
        // The persisted row is compared against the PUBLIC event directly, not only against memory.
        failure = AnimaTravelReceipt.CheckVisitIncrement(baseline, persisted, evidence);
        Require(failure == null, failure!);
        var savedSession = _api!.CurrentSession!.Id;
        AtRecord(consumerCase, ConsumerDescriptionFor(consumerCase), TravelStationReceipt.Passed,
            "system=" + evidence.ActualSystemId, session, arrival!.OperationId,
            TravelStationReceipt.Evidence(window, null),
            "mode=" + mode + "; arrivalSeq=" + arrival.Sequence + "; requestedSystem=" + (evidence.RequestedSystemId ?? "<none>")
            + "; label=" + (evidence.ActualSystemName == null ? "<unavailable: preserved>" : "observed")
            + "; before=" + AnimaTravelReceipt.Describe(baseline) + "; after=" + AnimaTravelReceipt.Describe(recorded)
            + "; sidecar=" + slot + " v" + version + " " + AnimaTravelReceipt.Describe(persisted));

        // The saved counts must survive a real reload BEFORE the next case's own fixture load
        // replaces the registry, so this proof can never be deferred.
        foreach (var frame in SpLoad(slot)) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        var reloadSession = _api.CurrentSession!.Id;
        foreach (var frame in AwaitPlacement(reloadSession)) yield return frame;
        foreach (var frame in Quiesce()) yield return frame;
        var restored = AnimaVisited();
        failure = AnimaTravelReceipt.CheckSameHistory(recorded, restored, "reloaded slot history");
        Require(failure == null, failure!);
        var observer = SpGet(AnimaPlugin, "_visitObserver");
        Require(observer != null, "The consumer's visit observer disappeared across the reload.");
        failure = AnimaTravelReceipt.CheckSessionReplacement(savedSession, reloadSession,
            (Guid?)SpGet(observer!, "_session"),
            // HashSet<Guid> implements only the GENERIC ICollection<Guid>; the strict counter reads
            // the set's own Count property and refuses to guess.
            AnimaTravelReceipt.StrictCount(SpGet(observer!, "_countedLegs"), "_countedLegs"));
        Require(failure == null, failure!);
        var reloadEvidence = TravelStationReceipt.Evidence(
            Window(SessionWindowOffset(reloadSession)).Where(fact => fact.SessionId == reloadSession), null);
        var reloadDetail = "slot=" + slot + "; savedSession=" + savedSession + "; restored=" + AnimaTravelReceipt.Describe(restored)
            + "; countedLegs=0";
        if (gate)
            AtRecord(AnimaTravelReceipt.GateReloadSubcase, AnimaTravelReceipt.GateReloadDescription, TravelStationReceipt.Passed,
                "history=" + AnimaTravelReceipt.Describe(restored), reloadSession, null, reloadEvidence, reloadDetail);

        // Loading the earlier fixture must roll the history back to that slot's own counts instead
        // of silently keeping the newer session's registry.
        foreach (var frame in SpLoad("fixture-a")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        var rollbackSession = _api.CurrentSession!.Id;
        foreach (var frame in AwaitPlacement(rollbackSession)) yield return frame;
        foreach (var frame in Quiesce()) yield return frame;
        var rolled = AnimaVisited();
        failure = AnimaTravelReceipt.CheckSameHistory(fixtureBaseline, rolled, "earlier fixture rollback");
        Require(failure == null, failure!);
        var rollbackEvidence = TravelStationReceipt.Evidence(
            Window(SessionWindowOffset(rollbackSession)).Where(fact => fact.SessionId == rollbackSession), null);
        var rollbackDetail = "fixture=fixture-a; rolledBackFrom=" + AnimaTravelReceipt.Describe(restored)
            + "; to=" + AnimaTravelReceipt.Describe(rolled);
        if (gate)
            AtRecord(AnimaTravelReceipt.GateRollbackSubcase, AnimaTravelReceipt.GateRollbackDescription, TravelStationReceipt.Passed,
                "history=" + AnimaTravelReceipt.Describe(rolled), rollbackSession, null, rollbackEvidence, rollbackDetail);
        else
            AtRecord(AnimaTravelReceipt.PersistenceCase, AnimaTravelReceipt.PersistenceDescription, TravelStationReceipt.Passed,
                "history=" + AnimaTravelReceipt.Describe(restored), reloadSession, null, reloadEvidence,
                reloadDetail + "; " + rollbackDetail + "; rollbackSession=" + rollbackSession);
        AtEndCase();
    }

    // --- controlled visit-only degradation --------------------------------------------------

    // The consumer's OWN documented visit-only fault path, injected deliberately after the positive
    // persistence proof: the observer's callback faults, which stops visit recording without
    // touching the mission provider. This is a controlled consumer fault, never a claim about the
    // API's payloads and never a physical travel proof. The API travel hub is deliberately NOT
    // faulted, because the later phases still need it.
    private IEnumerable<object?> CaseRecordingDegraded()
    {
        AtCase(AnimaTravelReceipt.DegradedCase, AnimaTravelReceipt.DegradedDescription);
        foreach (var frame in Settle()) yield return frame;
        var entrySession = _api!.CurrentSession!.Id;
        foreach (var frame in AwaitPlacement(entrySession)) yield return frame;
        foreach (var frame in Quiesce()) yield return frame;
        Require((bool)SpGet(AnimaPlugin, "VisitHistoryRecording")!, "The consumer was already degraded before the controlled fault.");
        // Authorized disposable prompt INPUT, after every genuine arrival/persistence proof: the
        // recognition window needs pre-existing qualifying counts, and no witnessed arrival can
        // legitimately produce them inside this phase.
        foreach (var frame in PrepareRegionalHistoryFixture()) yield return frame;
        var session = _api!.CurrentSession!.Id;
        var anima = AnimaPlugin;
        var before = AnimaVisited();
        Require((bool)SpGet(anima, "VisitHistoryRecording")!, "The consumer stopped recording before the controlled fault.");
        bool gatherBefore = AnimaRegionallyKnownPresent(out string gatherBeforeDetail);
        var observer = SpGet(anima, "_visitObserver")!;
        // Exactly the consumer's own documented observer-failure path.
        SpCall(observer, "Receive", new object[] { null! });
        foreach (var frame in Quiesce()) yield return frame;

        Require((bool)SpGet(observer, "Faulted")! && !(bool)SpGet(observer, "IsRecording")!, "The controlled fault did not latch the consumer's observer.");
        Require(SpGet(anima, "_visitObserver") == null, "The consumer kept a faulted visit observer.");
        Require(SpGet(AnimaType("VGAnima.Patches.SaveLoadPatch"), "VisitObserver") == null, "The consumer's load hook kept the faulted observer.");
        Require(!(bool)SpGet(anima, "VisitHistoryRecording")!, "The consumer still reports visit recording after the fault.");
        // The capability itself must remain available: this is a LOCAL consumer latch, not an API fault.
        Require(ModApi.Travel != null && ModApi.Services.Travel.Availability.IsAvailable,
            "The controlled consumer fault disabled the API travel capability.");
        Require(!ModApi.Travel!.IsDispatchingCallbacks, "The public travel surface is stuck dispatching after the consumer fault.");
        Require(anima.enabled && (bool)SpGet(anima, "_active")!, "The visit-only fault stopped the whole consumer.");
        var canWrite = SpGet(AnimaType("VGAnima.Patches.SaveWritePatch"), "CanWrite") as Delegate;
        Require(canWrite != null && (bool)canWrite.DynamicInvoke()!, "The consumer's save writes were disabled by a visit-only fault.");
        Require(((Harmony)SpGet(anima, "_loadSafetyHarmony")!).GetPatchedMethods().Count() == 2, "Both consumer load-safety hooks did not survive the fault.");
        Require(((Harmony)SpGet(anima, "_harmony")!).GetPatchedMethods().Any(), "The consumer's authoring hooks were removed by a visit-only fault.");
        var patches = ConsumerTravelPatches();
        Require(patches.Length == 0, "The degraded consumer installed a native travel fallback hook: " + string.Join(", ", patches));
        bool gatherAfter = AnimaRegionallyKnownPresent(out string gatherAfterDetail);
        // POSITIVE before, omitted after: an already-absent window would prove nothing.
        var window = AnimaTravelReceipt.CheckRegionalWindow(gatherBefore, gatherAfter);
        Require(window == null, window + " before=[" + gatherBeforeDetail + "] after=[" + gatherAfterDetail + "]");

        var preserved = AnimaVisited();
        var failure = AnimaTravelReceipt.CheckSameHistory(before, preserved, "history after the controlled fault");
        Require(failure == null, failure!);
        Save("qa-anima-travel-stopped", LifecycleEventKind.SaveSucceeded);
        var persisted = AnimaSidecarVisited("qa-anima-travel-stopped", out int version, out bool present);
        Require(present, "The degraded consumer stopped writing its sidecar.");
        failure = AnimaTravelReceipt.CheckSidecarVersion(version, AnimaSidecarVersion);
        Require(failure == null, failure!);
        failure = AnimaTravelReceipt.CheckSameHistory(before, persisted, "saved history after the controlled fault");
        Require(failure == null, failure!);
        var evidence = TravelStationReceipt.Evidence(
            Window(SessionWindowOffset(session)).Where(fact => fact.SessionId == session), null);
        AtRecord(AnimaTravelReceipt.DegradedCase, AnimaTravelReceipt.DegradedDescription, TravelStationReceipt.Passed,
            "history=" + AnimaTravelReceipt.Describe(preserved), session, null, evidence,
            "fault=consumer visit observer callback; recording=false; capabilityAvailable=true; providerActive=true; saveWrites=true;"
            + " loadSafetyHooks=2; consumerTravelPatches=0; regionallyKnownBefore=" + gatherBefore + " (" + gatherBeforeDetail + ")"
            + "; regionallyKnownAfter=" + gatherAfter + " (" + gatherAfterDetail + ")"
            + "; preserved=" + AnimaTravelReceipt.Describe(preserved) + "; savedSidecar=v" + version);
        AtEndCase();
    }

    // --- authorized disposable historical prompt input ---------------------------------------

    /// <summary>The probe's OWN sandbox slot; never a prepared fixture and never an original save.</summary>
    private const string RegionalFixtureSlot = "qa-anima-travel-regional";

    // Regional recognition needs pre-existing qualifying counts, and no arrival this phase can
    // legitimately drive would produce them (the builder's threshold is three visits to one system).
    // So AFTER every genuine arrival, save and rollback proof, the probe saves its own new sandbox
    // slot, adjusts THAT slot's consumer sidecar through the consumer's own reader/writer, and loads
    // it back through the consumer's real load path. This is synthetic prompt INPUT recorded as a
    // mandatory setup row: no arrival is observed, no counter is incremented by it, no registry API
    // is called and no original save or prepared fixture companion is touched.
    private IEnumerable<object?> PrepareRegionalHistoryFixture()
    {
        var anima = AnimaPlugin;
        var saved = AnimaVisited();
        Save(RegionalFixtureSlot, LifecycleEventKind.SaveSucceeded);
        var sidecarPath = AnimaSidecarPath(RegionalFixtureSlot);
        var failure = AnimaTravelReceipt.CheckSidecarPath(sidecarPath, _saveRoot!, RegionalFixtureSlot);
        Require(failure == null, failure!);
        foreach (var prepared in new[] { "fixture-a", "fixture-b" })
            Require(!SamePath(sidecarPath, AnimaSidecarPath(prepared)),
                "Refusing to write historical input beside a prepared fixture save.");
        Require(File.Exists(sidecarPath), "The consumer wrote no sidecar beside the probe's own saved slot.");
        // Refuse to rewrite anything the consumer itself cannot read at the current schema version:
        // an unknown/future/corrupt companion fails the case instead of being overwritten.
        var read = SpCall(SpGet(anima, "SidecarIO")!, "Read", sidecarPath);
        Require(SpGet(read, "Status")!.ToString() == "Loaded",
            "Refusing to rewrite a consumer sidecar the consumer cannot read: " + SpGet(read, "Status"));
        var schema = SpGet(read, "Schema")!;
        int version = (int)SpGet(schema, "Version")!;
        failure = AnimaTravelReceipt.CheckSidecarVersion(version, AnimaSidecarVersion);
        Require(failure == null, "Refusing to rewrite a consumer sidecar of another schema version: " + failure);

        var system = SpGet(CurrentPlayer, "currentSystem");
        Require(system != null, "The loaded world has no current system to give a pre-existing history.");
        var targetId = (string)SpGet(system!, "guid")!;
        int threshold = (int)SpGet(AnimaType("VGAnima.Llm.RegionallyKnownBuilder"), "MinVisitsThreshold")!;
        double gameSeconds = (double)SpGet(SpGet(anima, "Clock")!, "GameSeconds")!;
        var existing = saved.Where(record => record.SystemId == targetId).ToArray();
        var written = saved.Where(record => record.SystemId != targetId).ToList();
        // Raise the current system to the recognition threshold, keeping whatever label and
        // first-visit time the slot already recorded; a first visit stores the empty label, exactly
        // like an observed arrival with no public label would.
        written.Add(existing.Length == 1
            ? new AnimaTravelReceipt.VisitRecord(targetId, existing[0].Name,
                Math.Max(existing[0].Visits, threshold), existing[0].FirstSeconds, existing[0].LastSeconds)
            : new AnimaTravelReceipt.VisitRecord(targetId, string.Empty, threshold, gameSeconds, gameSeconds));
        failure = AnimaTravelReceipt.CheckSyntheticHistoryInput(saved, written, targetId, threshold);
        Require(failure == null, failure!);

        // Written through the consumer's OWN schema type and writer, so the file stays format-correct.
        var visitedType = AnimaType("VGAnima.Persistence.VisitedSystem");
        var visited = Array.CreateInstance(visitedType, written.Count);
        for (int index = 0; index < written.Count; index++)
            visited.SetValue(Activator.CreateInstance(visitedType, written[index].SystemId, written[index].Name,
                written[index].Visits, written[index].FirstSeconds, written[index].LastSeconds), index);
        var replacement = Activator.CreateInstance(AnimaType("VGAnima.Persistence.SidecarSchema"),
            version, SpGet(schema, "Entries"), visited)!;
        SpCall(SpGet(anima, "SidecarIO")!, "Write", sidecarPath, replacement);

        int offset = _atFacts!.Count;
        foreach (var frame in SpLoad(RegionalFixtureSlot)) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        var fixtureSession = _api!.CurrentSession!.Id;
        foreach (var frame in AwaitPlacement(fixtureSession)) yield return frame;
        foreach (var frame in Quiesce()) yield return frame;
        var restored = AnimaVisited();
        var loadWindow = Window(offset);
        failure = AnimaTravelReceipt.CheckSyntheticHistoryRestored(written, restored,
            loadWindow.Count(fact => fact.Kind == TravelTransitionKind.Arrived));
        Require(failure == null, failure!);
        var evidence = TravelStationReceipt.Evidence(loadWindow.Where(fact => fact.SessionId == fixtureSession), null);
        AtRecord(AnimaTravelReceipt.RegionalFixtureSubcase, AnimaTravelReceipt.RegionalFixtureDescription,
            TravelStationReceipt.Passed, "system=" + targetId, fixtureSession, null, evidence,
            "slot=" + RegionalFixtureSlot + " (probe-owned sandbox save); syntheticHistoricalInput=1 system to " + threshold
            + " visit(s); observedArrivals=0; sidecar=v" + version
            + "; savedSystems=" + saved.Count + "; writtenSystems=" + written.Count + "; restoredSystems=" + restored.Count
            + "; countedVisits=0 (fixture input is never a witnessed visit)");
    }

    // --- consumer reads ---------------------------------------------------------------------

    private BaseUnityPlugin AnimaPlugin => Chainloader.PluginInfos["vganima"].Instance;
    private Type AnimaType(string name) => AnimaPlugin.GetType().Assembly.GetType(name, true)!;
    private int AnimaSidecarVersion => (int)SpGet(AnimaType("VGAnima.Persistence.SidecarSchema"), "CurrentVersion")!;

    /// <summary>The consumer's live visited-system history, read from its own registry.</summary>
    private List<AnimaTravelReceipt.VisitRecord> AnimaVisited()
    {
        var registry = SpGet(AnimaPlugin, "PersistedRegistry")!;
        // The consumer's visited map is a Dictionary, which DOES implement the non-generic
        // IDictionary; its size is never assumed here, only its entries are read.
        return ((IDictionary)SpGet(registry, "VisitedSystems")!).Values.Cast<object>().Select(ReadVisit).ToList();
    }

    private static AnimaTravelReceipt.VisitRecord ReadVisit(object record)
        => new((string)SpGet(record, "Guid")!, (string)SpGet(record, "Name")!, (int)SpGet(record, "VisitCount")!,
            (double)SpGet(record, "FirstVisitGameSeconds")!, (double)SpGet(record, "LastVisitGameSeconds")!);

    // The consumer's own path resolver, so the probe reads exactly the companion the consumer wrote.
    private string AnimaSidecarPath(string save)
        => (string)AccessTools.Method(AnimaType("VGAnima.Persistence.SidecarPathResolver"), "From")
            .Invoke(null, new object[] { Path.Combine(_saveRoot!, save + ".save") })!;

    /// <summary>
    /// The consumer's own sidecar reader; a missing companion is reported, never invented. A
    /// present-but-unreadable companion is the consumer's own quarantine path and is a recorded
    /// failure here, never an empty baseline.
    /// </summary>
    private List<AnimaTravelReceipt.VisitRecord> AnimaSidecarVisited(string save, out int version, out bool present)
    {
        version = 0;
        var path = AnimaSidecarPath(save);
        present = File.Exists(path);
        if (!present) return new List<AnimaTravelReceipt.VisitRecord>();
        var result = SpCall(SpGet(AnimaPlugin, "SidecarIO")!, "Read", path);
        Require(SpGet(result, "Status")!.ToString() == "Loaded", "The consumer sidecar " + save + " is not readable: " + SpGet(result, "Status"));
        var schema = SpGet(result, "Schema")!;
        version = (int)SpGet(schema, "Version")!;
        var visited = SpGet(schema, "VisitedSystems") as IEnumerable;
        return visited == null ? new List<AnimaTravelReceipt.VisitRecord>() : visited.Cast<object>().Select(ReadVisit).ToList();
    }

    /// <summary>Live Harmony patches owned by the consumer that the public-API contract refuses.</summary>
    private static string[] ConsumerTravelPatches()
        => Harmony.GetAllPatchedMethods()
            .Where(method => Harmony.GetPatchInfo(method)?.Owners.Any(owner =>
                owner == "vganima" || owner.StartsWith("vganima.", StringComparison.Ordinal)) == true)
            .Select(method => AnimaTravelReceipt.RefuseConsumerTravelPatch(method.DeclaringType?.FullName ?? "", method.Name))
            .Where(refusal => refusal != null)
            .Select(refusal => refusal!)
            .ToArray();

    /// <summary>
    /// The consumer's OWN production decision, invoked exactly as its bar context path invokes it:
    /// <c>RegionalRecognition.ForCurrentContext()</c> reads the live recording gate, registry,
    /// journal bridge and clock itself, so the probe cannot supply a substitute flag or history and
    /// never re-implements the gate. Its result is handed to the consumer's own
    /// <c>ContextGatherer.Gather</c> and serialized with the consumer's own serializer, so the
    /// presence of the <c>regionally_known</c> key is the consumer's own output, not an annotation.
    /// </summary>
    private bool AnimaRegionallyKnownPresent(out string detail)
    {
        var anima = AnimaPlugin;
        var decision = AnimaType("VGAnima.Llm.RegionalRecognition").GetMethod("ForCurrentContext",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("VGAnima.Llm.RegionalRecognition", "ForCurrentContext");
        Require(decision.GetParameters().Length == 0,
            "The consumer's shared regional-recognition decision takes arguments; the probe must never supply its live state.");
        var regionallyKnown = decision.Invoke(null, null);
        var broker = Activator.CreateInstance(AnimaType("VGAnima.Llm.BrokerInfo"),
            "qa-anima-travel-probe", true, "qa-anima-travel-probe-seed", string.Empty)!;
        var gather = AnimaType("VGAnima.Llm.ContextGatherer").GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(method => method.Name == "Gather");
        var context = gather.Invoke(SpGet(anima, "Gatherer")!, new object?[]
        {
            SpGet(anima, "GameStateView"), broker, null, null, null, null, regionallyKnown
        })!;
        var json = SpJson(context);
        bool present = json.Contains("\"regionally_known\"");
        detail = "decision=VGAnima.Llm.RegionalRecognition.ForCurrentContext(); recording="
            + SpGet(anima, "VisitHistoryRecording")
            // Strict too: an omitted window is null, but a produced window that cannot report its
            // own size must fail rather than be reported as an unknown count.
            + "; decisionEntries=" + (regionallyKnown == null ? "null"
                : AnimaTravelReceipt.StrictCount(regionallyKnown, "RegionalRecognition.ForCurrentContext()").ToString())
            + "; contextKey=" + (present ? "present" : "omitted");
        return present;
    }

    // --- window and timing helpers ------------------------------------------------------------

    private List<TravelTransition> Window(int offset) => TravelStationReceipt.Window(_atFacts!, offset);

    // The index of the session's own first public fact, so a case window starts at that session's
    // placement and no earlier fact can satisfy it.
    private int SessionWindowOffset(Guid session)
    {
        for (int index = 0; index < _atFacts!.Count; index++)
            if (_atFacts[index].SessionId == session) return index;
        return _atFacts.Count;
    }

    private IEnumerable<object?> AwaitPlacement(Guid session)
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + AnimaTravelReceipt.PlacementSeconds;
        while (!_atFacts!.Any(fact => fact.SessionId == session && fact.Kind == TravelTransitionKind.InitialPlacement))
        {
            Require(_api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for the consumer window's placement fact.");
            Require(Time.realtimeSinceStartup < until, "Timed out waiting for the freshly loaded session's public placement fact.");
            yield return null;
        }
    }

    // The consumer subscribed at its own Awake, so it is dispatched before this probe for every
    // fact. Assertions are still taken only after the public dispatch is idle and no further fact
    // arrived for several frames, so the verifier can never race the consumer's own callback.
    private IEnumerable<object?> Quiesce()
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + AnimaTravelReceipt.QuiescenceSeconds;
        int observed = _atFacts!.Count;
        int stable = 0;
        while (stable < 3)
        {
            Require(Time.realtimeSinceStartup < until, "Timed out waiting for public travel callback quiescence.");
            yield return null;
            if (_atFacts.Count != observed) { observed = _atFacts.Count; stable = 0; }
            else if (ModApi.Travel?.IsDispatchingCallbacks == true) stable = 0;
            else stable++;
        }
    }

    private static string? ConsumerCaseFor(string crossCase)
        => crossCase == TravelCrossSystemReceipt.JumpGateCase ? AnimaTravelReceipt.GateCase
            : crossCase == TravelCrossSystemReceipt.WormholeCase ? AnimaTravelReceipt.WormholeCase : null;

    private static string ConsumerDescriptionFor(string consumerCase)
        => consumerCase == AnimaTravelReceipt.GateCase ? AnimaTravelReceipt.GateDescription : AnimaTravelReceipt.WormholeDescription;
}
