using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

// Actual-consumer Echo arrival-snap qualification probe, phase echo-travel-consumer-v1.
//
// It proves what the INSTALLED consumer does with the API's verified RouteCompleted fact: its own
// ApplyArrivalSnap writing the native idle timer, and the NEXT native IdleManager.Update reaching
// its FindActivity decision from that write. Nothing here calls ApplyArrivalSnap, Observe,
// IdleManager.Update or FindActivity; the probe only observes them through read-only Harmony
// prefixes/postfixes that never change a consumer decision or a native outcome.
//
// Declared, bounded controls (all recorded in the mandatory declared-probe-controls row):
//   * the sandbox-only Echo configuration (master + arrival-snap on, ETA-sync off so an ETA write
//     can never be mistaken for a snap),
//   * seeding the native idle timer to a positive value so a case never starts from an expired
//     cycle and a natural expiry can never be mistaken for a snap-driven decision,
//   * engaging the native autopilot at bounded points (the consumer's own gate),
//   * a counted suppression of the native FindActivity BODY while the probe is armed, so reaching
//     the decision boundary cannot launch an uncontrolled autonomous route. That proves the
//     boundary was reached, never that the autonomous action executed,
//   * one controlled subscription reordering that disposes the consumer's own observer, registers
//     the probe's earlier one and then invokes the consumer's OWN production binder.
public sealed partial class Plugin
{
    internal bool EchoTravelProbeSelected => File.Exists(Path.Combine(_root!, "echo-travel.enabled"));
    private readonly List<TravelStationReceipt.Row> _etRows = new();
    private readonly List<string> _etEvents = new();
    // Non-null only while the probe owns a live subscription; every reused-phase hook is inert
    // otherwise, so an unselected run behaves exactly as before.
    private List<TravelTransition>? _etFacts;
    private readonly Dictionary<long, int> _etFactFrames = new();
    private string _etCase = "phase-start";
    private string _etDescription = "The Echo consumer probe is preparing its first case.";
    private Guid _etWindowSession;
    private int _etWindowOffset;
    private int _etSnapOffset;
    private int _etIdleOffset;
    private bool _etInSystemRecorded;
    private string? _etOpenCase;

    // --- read-only observation state (static: Harmony patches are static) --------------------

    private static Plugin? _echoOwner;
    private static int _echoThread;
    /// <summary>Recording is live for the whole phase; arming adds the seeding and the guard.</summary>
    private static bool _echoRecording;
    private static bool _echoArmed;
    private static Guid _echoSession;
    private static MethodInfo? _echoIdleTimerGet;
    private static MethodInfo? _echoIdleTimerSet;
    private static readonly List<EchoTravelReceipt.SnapObservation> _echoSnaps = new();
    private static readonly List<EchoTravelReceipt.IdleObservation> _echoIdle = new();
    private static int _echoFindActivityInvocations, _echoSuppressedBodies, _echoSeededWrites, _echoAutopilotEngagements, _echoReorderings;
    private static bool _echoFindActivityInWindow;

    private readonly struct EchoApplyEntry
    {
        internal float Timer { get; }
        internal bool TravelActive { get; }
        internal int RemainingWaypoints { get; }
        internal EchoApplyEntry(float timer, bool travelActive, int remainingWaypoints)
        {
            Timer = timer; TravelActive = travelActive; RemainingWaypoints = remainingWaypoints;
        }
    }

    private void EtCase(string id, string description) { _etCase = id; _etDescription = description; }
    private void EtEndCase() { EtCase(TravelStationReceipt.NoActiveCase, "No Echo consumer case is observing."); EtCheckpoint(); }
    private void EtRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
    {
        _etRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));
        EtCheckpoint();
    }

    private void EtCheckpoint()
    {
        WriteAtomic("echo-travel-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_etRows.Select(row => row.ToTsv())));
        WriteAtomic("echo-travel-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_etEvents));
        WriteAtomic("echo-travel.txt", new[] { EchoTravelReceipt.SummarizeIncomplete(_etRows, _etCase) });
    }

    private IEnumerable<object?> CheckEchoTravelConsumer()
    {
        if (!EchoTravelProbeSelected) yield break;
        var run = RunEchoTravelConsumer().GetEnumerator();
        string? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
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
            _etRows.Add(new TravelStationReceipt.Row(_etCase, _etDescription, TravelStationReceipt.Failed, "",
                _api?.CurrentSession?.Id.ToString() ?? "", "", "", fault.Split('\n')[0].Trim()));
        }
        WriteAtomic("echo-travel-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_etRows.Select(row => row.ToTsv())));
        WriteAtomic("echo-travel-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_etEvents));
        var failure = EchoTravelReceipt.Evaluate(_etRows, fault, _etEvents);
        WriteAtomic("echo-travel.txt", new[] { EchoTravelReceipt.Summarize(_etRows, fault, _etEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "echo-travel-fault.txt"), fault);
        Require(failure == null, "Actual-consumer Echo travel phase " + EchoTravelReceipt.Phase + " failed: " + failure);
        Passed(EchoTravelReceipt.Phase);
    }

    private IEnumerable<object?> RunEchoTravelConsumer()
    {
        Require(!AnimaTravelProbeSelected,
            "The Anima and Echo consumer travel probes both own the reused travel phases; they are refused together at Prepare.");
        Require(TravelStationSelected && TravelCrossSystemSelected && TravelWormholeFixtureSelected,
            "The Echo consumer probe requires both qualified native travel phases and the wormhole fixture selection.");
        Require(ModApi.Travel != null, "Travel public service not exposed.");
        Require(_api!.Capabilities.Any(capability => capability.Name == "native-travel" && capability.Available), "native-travel capability not available.");
        Require(!ModApi.Travel!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        Require(EchoTravelReceipt.ReadinessSeconds == WaitDeadlineSeconds && EchoTravelReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(EchoTravelReceipt.PhaseBudgetSeconds <= EchoTravelReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        Require(_travelStationPending && _travelCrossSystemPending,
            "The reused native travel phases already ran; the Echo consumer probe must own their ordering.");

        var harmony = new Harmony("vgmodapi.qualification.echo");
        var facts = new List<TravelTransition>();
        InstallEchoProbes(harmony);
        try
        {
            using (ModApi.Travel!.Subscribe("qualification.echo", fact =>
            {
                facts.Add(fact);
                _etFactFrames[fact.Sequence] = Time.frameCount;
                _etEvents.Add(TravelStationReceipt.TravelEventRow(_etCase, fact));
            }))
            {
                _etFacts = facts;
                try
                {
                    EtCheckpoint();
                    foreach (var frame in CheckTravelStation()) yield return frame;
                    foreach (var frame in CheckTravelCrossSystem()) yield return frame;
                    foreach (var frame in PrepareOwnedRoutes()) yield return frame;
                    foreach (var frame in CaseSupersession()) yield return frame;
                    foreach (var frame in CaseSnapStopped()) yield return frame;
                    RecordDeclaredControls();
                }
                finally { _etFacts = null; }
            }
        }
        finally
        {
            EchoDisarm();
            _echoRecording = false;
            _echoOwner = null;
            try { harmony.UnpatchSelf(); }
            catch (Exception error) { Logger.LogWarning("Echo observation probes did not unpatch cleanly: " + error.GetType().Name); }
        }
    }

    // --- read-only observation probes --------------------------------------------------------

    private void InstallEchoProbes(Harmony harmony)
    {
        _echoOwner = this;
        _echoThread = Thread.CurrentThread.ManagedThreadId;
        _echoSnaps.Clear();
        _echoIdle.Clear();
        _echoFindActivityInvocations = _echoSuppressedBodies = _echoSeededWrites = _echoAutopilotEngagements = _echoReorderings = 0;
        var idleType = AccessTools.TypeByName("Behaviour.Gameplay.IdleManager") ?? throw new MissingMemberException("Behaviour.Gameplay.IdleManager", "type");
        _echoIdleTimerGet = AccessTools.PropertyGetter(idleType, "updateTimer") ?? throw new MissingMemberException(idleType.FullName, "get_updateTimer");
        _echoIdleTimerSet = AccessTools.PropertySetter(idleType, "updateTimer") ?? throw new MissingMemberException(idleType.FullName, "set_updateTimer");
        Require(_echoIdleTimerGet.ReturnType == typeof(float), "Native idle timer is not a float.");
        var apply = AccessTools.Method(EchoType("VGEcho.Patches.AutopilotTimingPatches"), "ApplyArrivalSnap");
        Require(apply != null && apply.IsStatic && apply.ReturnType == typeof(void) && apply.GetParameters().Length == 0,
            "The installed consumer's ApplyArrivalSnap does not have its declared shape.");
        var update = AccessTools.Method(idleType, "Update");
        var findActivity = AccessTools.Method(idleType, "FindActivity");
        Require(update != null && findActivity != null, "Native IdleManager.Update/FindActivity missing.");
        harmony.Patch(apply,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(EchoApplyEntering)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(EchoApplyExited)));
        harmony.Patch(update,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(EchoIdleEntering)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(EchoIdleExited)));
        harmony.Patch(findActivity, prefix: new HarmonyMethod(typeof(Plugin), nameof(EchoFindActivityEntering)));
        _echoRecording = true;
    }

    private static float EchoIdleTimer(object idle) => (float)_echoIdleTimerGet!.Invoke(idle, null)!;

    private static object? EchoLiveIdle()
    {
        var idleType = AccessTools.TypeByName("Behaviour.Gameplay.IdleManager");
        var current = idleType == null ? null : SpGet(idleType, "Current");
        return TravelStationDriver.Alive(current) ? current : null;
    }

    // Read-only capture around the consumer's OWN write. It records what the consumer saw and what
    // it left behind; it never changes the decision and never returns false.
    private static void EchoApplyEntering(out object? __state)
    {
        __state = null;
        if (!_echoRecording) return;
        try
        {
            var idle = EchoLiveIdle();
            var player = _echoOwner == null ? null : SpGet(_echoOwner._player, "current");
            var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
            var travel = travelType == null ? null : SpGet(travelType, "Current");
            var waypoints = player == null ? null : SpGet(player, "waypoints") as ICollection;
            __state = new EchoApplyEntry(
                idle == null ? float.NaN : EchoIdleTimer(idle),
                TravelStationDriver.Alive(travel) && (bool)TravelStationDriver.CallExact(travel!, "TravelActive", typeof(bool))!,
                waypoints?.Count ?? -1);
        }
        catch { __state = null; }
    }

    private static void EchoApplyExited(object? __state)
    {
        if (!_echoRecording || __state is not EchoApplyEntry entry) return;
        try
        {
            var idle = EchoLiveIdle();
            _echoSnaps.Add(new EchoTravelReceipt.SnapObservation(Time.frameCount, entry.Timer,
                idle == null ? float.NaN : EchoIdleTimer(idle), entry.TravelActive, entry.RemainingWaypoints));
        }
        catch { }
    }

    private static void EchoIdleEntering(object __instance, out object? __state)
    {
        __state = null;
        if (!_echoRecording) return;
        _echoFindActivityInWindow = false;
        try { __state = EchoIdleTimer(__instance); }
        catch { __state = null; }
    }

    // The ONLY native write this probe performs: re-seeding the idle timer to a declared positive
    // value while armed, so a case never begins from an expired cycle and a natural expiry can
    // never be mistaken for a snap-driven decision. The consumer reads no timer to decide anything.
    private static void EchoIdleExited(object __instance, object? __state)
    {
        if (!_echoRecording || __state is not float before) return;
        try
        {
            float after = EchoIdleTimer(__instance);
            _echoIdle.Add(new EchoTravelReceipt.IdleObservation(Time.frameCount, before, after, _echoFindActivityInWindow));
            if (_echoArmed && after <= 0f)
            {
                _echoIdleTimerSet!.Invoke(__instance, new object[] { EchoTravelReceipt.IdleTimerSeedSeconds });
                _echoSeededWrites++;
            }
        }
        catch { }
        finally { _echoFindActivityInWindow = false; }
    }

    // Counts the REAL native decision boundary. While the probe is armed for its own session on the
    // main thread, the BODY is suppressed so reaching the boundary cannot launch an uncontrolled
    // autonomous route; the counted suppression is published in the receipt and is never presented
    // as proof that the autonomous action executed. TravelActive guards are never touched.
    private static bool EchoFindActivityEntering()
    {
        if (!_echoRecording) return true;
        _echoFindActivityInvocations++;
        _echoFindActivityInWindow = true;
        if (!_echoArmed) return true;
        if (Thread.CurrentThread.ManagedThreadId != _echoThread) return true;
        if (_echoOwner?._api?.CurrentSession?.Id != _echoSession) return true;
        _echoSuppressedBodies++;
        return false;
    }

    private void EchoArm(Guid session)
    {
        _echoSession = session;
        _echoArmed = true;
        SeedIdleTimer();
    }

    private void EchoDisarm() => _echoArmed = false;

    private void SeedIdleTimer()
    {
        var idle = EchoLiveIdle();
        if (idle == null) return;
        _echoIdleTimerSet!.Invoke(idle, new object[] { EchoTravelReceipt.IdleTimerSeedSeconds });
        _echoSeededWrites++;
    }

    /// <summary>Engages or releases the native autopilot, the consumer's own arrival-snap gate.</summary>
    private void SetAutopilot(bool engaged)
    {
        var player = CurrentPlayer;
        AccessTools.Field(_player, "autoPlay").SetValue(player, engaged);
        if (engaged) _echoAutopilotEngagements++;
    }

    // --- consumer reads -----------------------------------------------------------------------

    private BaseUnityPlugin EchoPlugin => Chainloader.PluginInfos["vgecho"].Instance;
    private Type EchoType(string name) => EchoPlugin.GetType().Assembly.GetType(name, true)!;
    private object? EchoObserver => SpGet(EchoPlugin, "_arrivalSnap");
    private bool EchoListening => EchoObserver is { } observer && (bool)SpGet(observer, "IsListening")!;

    /// <summary>Live Harmony patches owned by the consumer that the arrival-snap contract refuses.</summary>
    private static string[] EchoRefusedPatches()
    {
        var refusals = new List<string>();
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) continue;
            foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
            {
                if (patch.owner != "vgecho") continue;
                var refusal = EchoTravelReceipt.RefuseEchoTimingPatch(patch.PatchMethod.DeclaringType?.FullName ?? "",
                    method.DeclaringType?.FullName ?? "", method.Name);
                if (refusal != null) refusals.Add(refusal);
            }
        }
        return refusals.ToArray();
    }

    /// <summary>Every live Harmony patch the consumer owns, for the receipt's honest record.</summary>
    private static string[] EchoOwnedPatches()
        => Harmony.GetAllPatchedMethods()
            .Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains("vgecho") == true)
            .Select(method => (method.DeclaringType?.FullName ?? "") + "." + method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    // --- hooks called by the reused native travel phases ---------------------------------------

    private IEnumerable<object?> EtGuarded(string caseId, string description, IEnumerable<object?> body)
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
        EtRecord(caseId, description, TravelStationReceipt.Failed, "", _api?.CurrentSession?.Id, null, "",
            fault.Message.Split('\n')[0].Trim());
        File.WriteAllText(Path.Combine(_root!, "echo-travel-fault.txt"), fault.ToString());
    }

    internal IEnumerable<object?> EchoTravelInSystemReady(Guid session)
    {
        if (_etFacts == null) yield break;
        foreach (var frame in EtGuarded(EchoTravelReceipt.BindingCase, EchoTravelReceipt.BindingDescription,
            EchoInSystemReady(session))) yield return frame;
    }

    private IEnumerable<object?> EchoInSystemReady(Guid session)
    {
        EtCase(EchoTravelReceipt.BindingCase, EchoTravelReceipt.BindingDescription);
        foreach (var frame in AwaitEchoPlacement(session)) yield return frame;
        foreach (var frame in EchoQuiesce()) yield return frame;
        _etWindowSession = session;
        _etWindowOffset = SessionWindowOffset(session, _etFacts!);
        _etSnapOffset = _echoSnaps.Count;
        _etIdleOffset = _echoIdle.Count;
        var echo = EchoPlugin;
        Require(echo.enabled, "The installed Echo consumer is disabled.");
        Require(echo.Info.Metadata.Version.ToString(3) == EchoPinnedVersion,
            "The installed Echo consumer is " + echo.Info.Metadata.Version + ", not the pinned " + EchoPinnedVersion + ".");
        Require(EchoListening, "The consumer's arrival-snap subscription is absent or not listening.");
        Require(ModApi.Travel!.SessionId == session, "The public travel service is not bound to the phase's session.");
        foreach (var entry in new[] { "CfgAutopilotTiming", "CfgAutopilotArrivalSnap", "CfgAutopilotEtaSync" })
            Require(SpGet(echo, entry) != null, "The consumer's " + entry + " configuration entry is missing.");
        Require((bool)SpGet(SpGet(echo, "CfgAutopilotTiming")!, "Value")!
            && (bool)SpGet(SpGet(echo, "CfgAutopilotArrivalSnap")!, "Value")!
            && !(bool)SpGet(SpGet(echo, "CfgAutopilotEtaSync")!, "Value")!,
            "The sandbox Echo configuration is not the declared master-on / arrival-snap-on / ETA-sync-off shape.");
        var refusals = EchoRefusedPatches();
        Require(refusals.Length == 0, "The consumer restored a retired native timing hook: " + string.Join(", ", refusals));
        var placements = Window(_etWindowOffset, _etFacts!).Where(fact => fact.Kind == TravelTransitionKind.InitialPlacement).ToArray();
        EtRecord(EchoTravelReceipt.BindingCase, EchoTravelReceipt.BindingDescription, TravelStationReceipt.Passed,
            "consumer=" + echo.Info.Metadata.GUID + " " + echo.Info.Metadata.Version, session, null,
            TravelStationReceipt.Evidence(placements, null),
            "listening=true; timingHookRefusals=0; ownedPatches=[" + string.Join(" ", EchoOwnedPatches()) + "]");
        EtCase(EchoTravelReceipt.QuietCase, EchoTravelReceipt.QuietDescription);
        _etOpenCase = EchoTravelReceipt.QuietCase;
    }

    internal IEnumerable<object?> EchoTravelInSystemCompleted()
    {
        if (_etFacts == null || _etOpenCase != EchoTravelReceipt.QuietCase) yield break;
        _etOpenCase = null;
        foreach (var frame in EtGuarded(EchoTravelReceipt.QuietCase, EchoTravelReceipt.QuietDescription,
            EchoInSystemCompleted())) yield return frame;
    }

    private IEnumerable<object?> EchoInSystemCompleted()
    {
        foreach (var frame in EchoQuiesce()) yield return frame;
        var window = Window(_etWindowOffset, _etFacts!);
        var completions = window.Count(fact => fact.Kind == TravelTransitionKind.RouteCompleted);
        var others = window.Count(fact => fact.Kind is TravelTransitionKind.Requested
            or TravelTransitionKind.Cancelled or TravelTransitionKind.Arrived);
        // The consumer's autopilot gate is disengaged for the whole in-system phase, which is
        // exactly the documented no-snap state.
        Require(!(bool)SpGet(CurrentPlayer, "autoPlay")!, "The native autopilot was engaged during the quiet window.");
        var failure = EchoTravelReceipt.CheckQuietWindow(EchoSnaps(_etSnapOffset), EchoIdleTicks(_etIdleOffset), completions, others);
        Require(failure == null, failure!);
        EtRecord(EchoTravelReceipt.QuietCase, EchoTravelReceipt.QuietDescription, TravelStationReceipt.Passed,
            "autopilot=disengaged", _etWindowSession, null, TravelStationReceipt.Evidence(window, null),
            "phase=" + TravelStationReceipt.Phase + "; routeCompletions=" + completions + "; otherFacts=" + others
            + "; consumerWrites=0; idleTicks=" + EchoIdleTicks(_etIdleOffset).Count);
        EtEndCase();
    }

    internal IEnumerable<object?> EchoTravelCrossCaseReady(string crossCase, Guid session)
    {
        if (_etFacts == null) yield break;
        var consumerCase = EchoCaseFor(crossCase);
        if (consumerCase == null) yield break;
        foreach (var frame in EtGuarded(consumerCase, EchoDescriptionFor(consumerCase),
            EchoCrossCaseReady(consumerCase, session))) yield return frame;
    }

    private IEnumerable<object?> EchoCrossCaseReady(string consumerCase, Guid session)
    {
        EtCase(consumerCase, EchoDescriptionFor(consumerCase));
        foreach (var frame in AwaitEchoPlacement(session)) yield return frame;
        foreach (var frame in EchoQuiesce()) yield return frame;
        _etWindowSession = session;
        _etWindowOffset = SessionWindowOffset(session, _etFacts!);
        _etSnapOffset = _echoSnaps.Count;
        _etIdleOffset = _echoIdle.Count;
        Require(EchoListening, "The consumer stopped listening before the cross-system case.");
        Require((bool)SpGet(CurrentPlayer, "autoPlayUnlocked")!,
            "The fixture player has never unlocked autopilot, so the consumer's own gate can never open; this case cannot be qualified on this save.");
        SetAutopilot(true);
        EchoArm(session);
        _etOpenCase = consumerCase;
    }

    internal IEnumerable<object?> EchoTravelCrossCaseCompleted(string crossCase)
    {
        if (_etFacts == null) yield break;
        var consumerCase = EchoCaseFor(crossCase);
        if (consumerCase == null || _etOpenCase != consumerCase) yield break;
        _etOpenCase = null;
        foreach (var frame in EtGuarded(consumerCase, EchoDescriptionFor(consumerCase),
            EchoCrossCaseCompleted(consumerCase))) yield return frame;
    }

    private IEnumerable<object?> EchoCrossCaseCompleted(string consumerCase)
    {
        var session = _etWindowSession;
        var mode = consumerCase == EchoTravelReceipt.GateCase ? TravelMode.JumpGate : TravelMode.Wormhole;
        foreach (var frame in EchoQuiesce()) yield return frame;
        var window = Window(_etWindowOffset, _etFacts!);
        // The in-system approach leg of the FIRST cross-system case that has one is this phase's
        // in-system positive; it is a different route (and a different fact) from the cross hop.
        var approach = window.FirstOrDefault(fact => fact.Kind == TravelTransitionKind.RouteCompleted && fact.Mode == TravelMode.InSystem);
        if (!_etInSystemRecorded && approach != null)
        {
            foreach (var frame in AwaitIdleDecision(approach)) yield return frame;
            var failure = EchoTravelReceipt.CheckPositiveSnap(EchoFact(approach), EchoSnaps(_etSnapOffset), EchoIdleTicks(_etIdleOffset), session);
            Require(failure == null, failure!);
            EtRecord(EchoTravelReceipt.InSystemCase, EchoTravelReceipt.InSystemDescription, TravelStationReceipt.Passed,
                "mode=InSystem", session, approach.OperationId,
                TravelStationReceipt.Evidence(new[] { approach }, null), DescribeSnap(EchoFact(approach)));
            _etInSystemRecorded = true;
        }
        var crossing = window.FirstOrDefault(fact => fact.Kind == TravelTransitionKind.RouteCompleted && fact.Mode == mode);
        Require(crossing != null, "The reused cross-system case produced no " + mode + " route completion for the consumer to react to.");
        foreach (var frame in AwaitIdleDecision(crossing!)) yield return frame;
        var crossFailure = EchoTravelReceipt.CheckPositiveSnap(EchoFact(crossing!), EchoSnaps(_etSnapOffset), EchoIdleTicks(_etIdleOffset), session);
        Require(crossFailure == null, crossFailure!);
        EtRecord(consumerCase, EchoDescriptionFor(consumerCase), TravelStationReceipt.Passed,
            "mode=" + mode, session, crossing!.OperationId,
            TravelStationReceipt.Evidence(new[] { crossing }, null), DescribeSnap(EchoFact(crossing)));
        EchoDisarm();
        SetAutopilot(false);
        EtEndCase();
    }

    // --- Echo-owned routes ----------------------------------------------------------------------

    private const string EchoPinnedVersion = "0.7.0";
    private object? _etTargetA, _etTargetB, _etTargetC;

    /// <summary>Loads the fixture the owned cases drive from and performs the player's own undock.</summary>
    private IEnumerable<object?> PrepareOwnedRoutes()
    {
        EtCase(EchoTravelReceipt.SupersessionCase, EchoTravelReceipt.SupersessionDescription);
        foreach (var frame in SpLoad("fixture-a")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        var session = _api!.CurrentSession!.Id;
        foreach (var frame in AwaitEchoPlacement(session)) yield return frame;
        foreach (var frame in EchoQuiesce()) yield return frame;
        foreach (var frame in Wait(() => ModApi.Travel?.SessionId == session
            && ModApi.Travel.CurrentLocation != null && NativeTravelReady(), "travel service binding and native POI readiness")) yield return frame;
        _etSnapOffset = _echoSnaps.Count;
        _etIdleOffset = _echoIdle.Count;
        _etWindowSession = session;
        Require((bool)SpGet(CurrentPlayer, "autoPlayUnlocked")!,
            "The fixture player has never unlocked autopilot; the owned Echo cases cannot be qualified on this save.");
        var targets = SafeInSystemTargets();
        Require(targets.Length >= 2, "The owned Echo cases need two safe in-system targets (" + SafeTargetSelection + ").");
        _etTargetA = targets[0];
        _etTargetB = targets[1];
        _etTargetC = targets[0];
        foreach (var frame in UndockForEcho()) yield return frame;
        SetAutopilot(true);
        EchoArm(session);
    }

    // The player's own exit action, exactly as the qualified in-system phase drives it.
    private IEnumerable<object?> UndockForEcho()
    {
        var gameplay = AccessTools.TypeByName("GameplayManager");
        var ship = SpGet(SpGet(gameplay, "Instance")!, "spaceShip");
        Require(TravelStationDriver.Alive(ship), "The fixture has no live player ship to undock.");
        var data = SpGet(ship!, "spaceShipData");
        if (data == null || SpGet(data, "dockingState")?.ToString() != "Docked") yield break;
        var exteriorType = AccessTools.TypeByName("SpacestationExteriorManager");
        var interiorType = AccessTools.TypeByName("Behaviour.UI.Spacestation.SpaceStationInterior");
        var exterior = SpGet(exteriorType, "Instance");
        Require(TravelStationDriver.Alive(exterior), "The docked fixture has no live station exterior to exit.");
        var interior = SpGet(interiorType, "instance");
        if (TravelStationDriver.Alive(interior)) TravelStationDriver.Bind(interiorType, "ExitSpacestation", typeof(void)).Invoke(interior!, null);
        else TravelStationDriver.Bind(exteriorType, "StartUndocking", typeof(void)).Invoke(exterior!, null);
        foreach (var frame in AwaitEcho(() => SpGet(SpGet(ship!, "spaceShipData")!, "dockingState")?.ToString() != "Docked"
            && !TravelStationDriver.Alive(SpGet(exterior!, "undockingRoutine")),
            EchoTravelReceipt.UndockSeconds, "the native undock before the owned Echo routes")) yield return frame;
    }

    /// <summary>
    /// The controlled subscription reordering plus the two owned legs: an earlier-registered
    /// subscriber starts a REAL native route from inside the old completion's dispatch, so the
    /// consumer's own write-time guard must refuse; the new route then completes and snaps.
    /// </summary>
    private IEnumerable<object?> CaseSupersession()
    {
        EtCase(EchoTravelReceipt.SupersessionCase, EchoTravelReceipt.SupersessionDescription);
        var session = _etWindowSession;
        var previous = EchoObserver;
        Require(previous != null, "The consumer holds no arrival-snap observer to reorder.");
        ((IDisposable)previous!).Dispose();
        bool previousDisposed = !(bool)SpGet(previous, "IsListening")!;
        Guid? supersededOperation = null;
        int supersessions = 0;
        bool? superseding = null;
        using (ModApi.Travel!.Subscribe("qualification.echo.earlier", fact =>
        {
            if (fact.Kind != TravelTransitionKind.RouteCompleted) return;
            if (supersededOperation == null || fact.OperationId != supersededOperation || supersessions != 0) return;
            supersessions++;
            // A REAL native route request from inside the same synchronous dispatch, exactly the
            // race the consumer's write-time guard exists for. No teleport, no fabricated fact.
            var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
            superseding = (bool)TravelStationDriver.Bind(travelType, "TryInitiateTravel",
                typeof(bool), AccessTools.TypeByName("Source.Galaxy.MapPointOfInterest"))
                .Invoke(SpGet(travelType, "Instance")!, new[] { _etTargetB })!;
        }))
        {
            // The consumer's OWN production binder creates the fresh subscription, so it lands
            // after the probe's earlier one in the hub's dispatch order.
            AccessTools.Method(EchoPlugin.GetType(), "BindArrivalSnap").Invoke(EchoPlugin, null);
            _echoReorderings++;
            var reorder = EchoTravelReceipt.CheckSubscriptionReorder(previousDisposed, EchoListening, true);
            Require(reorder == null, reorder!);
            Require(!ReferenceEquals(EchoObserver, previous), "The consumer kept its disposed observer after re-binding.");

            int snapOffset = _echoSnaps.Count;
            int idleOffset = _echoIdle.Count;
            int offset = _etFacts!.Count;
            SeedIdleTimer();
            // Leg A: a real native in-system route. Its operation identity is pinned from the
            // public Requested fact, so the earlier subscriber can only supersede THAT completion.
            foreach (var frame in DriveOwnedRoute(_etTargetA!, offset, fact => supersededOperation = fact)) yield return frame;
            var legA = Window(offset, _etFacts!).Where(fact => fact.Kind == TravelTransitionKind.RouteCompleted).ToArray();
            Require(legA.Length == 1, "The superseded leg produced " + legA.Length + " route completions.");
            Require(supersessions == 1, "The earlier subscriber did not start its real native route inside the superseded completion's dispatch.");
            Require(superseding == true, "Native TryInitiateTravel refused the earlier subscriber's route, so no supersession happened.");
            var refusal = EchoTravelReceipt.CheckRefusedSnap(EchoFact(legA[0]), EchoSnaps(snapOffset), EchoIdleTicks(idleOffset));
            Require(refusal == null, refusal!);

            // Leg B is already running: it was started by the earlier subscriber, so the probe only
            // waits for the public facts of that separately owned operation.
            int secondOffset = _etFacts.Count;
            foreach (var frame in AwaitOwnedRoute(secondOffset)) yield return frame;
            var legB = Window(secondOffset, _etFacts!).Where(fact => fact.Kind == TravelTransitionKind.RouteCompleted).ToArray();
            Require(legB.Length == 1, "The superseding leg produced " + legB.Length + " route completions.");
            Require(legB[0].OperationId != legA[0].OperationId, "The superseding route reused the superseded operation identity.");
            foreach (var frame in AwaitIdleDecision(legB[0])) yield return frame;
            var positive = EchoTravelReceipt.CheckPositiveSnap(EchoFact(legB[0]), EchoSnaps(snapOffset), EchoIdleTicks(idleOffset), session);
            Require(positive == null, positive!);
            EtRecord(EchoTravelReceipt.SupersessionCase, EchoTravelReceipt.SupersessionDescription, TravelStationReceipt.Passed,
                "superseded=" + legA[0].OperationId, session, legB[0].OperationId,
                TravelStationReceipt.Evidence(new[] { legA[0], legB[0] }, null),
                "reordering=consumer observer disposed, probe observer registered, consumer BindArrivalSnap re-invoked; "
                + "refusedWrite=" + EchoSnaps(snapOffset).First(snap => snap.Frame == EchoFact(legA[0]).Frame).Describe()
                + "; superseding=" + DescribeSnap(EchoFact(legB[0])));
        }
        EtEndCase();
    }

    /// <summary>
    /// Stopping the subscription disables ONLY the snap: a later real route completion writes no
    /// timer while every other consumer patch stays installed and no native timing hook returns.
    /// </summary>
    private IEnumerable<object?> CaseSnapStopped()
    {
        EtCase(EchoTravelReceipt.DegradedCase, EchoTravelReceipt.DegradedDescription);
        var session = _etWindowSession;
        var observer = EchoObserver;
        Require(observer != null, "The consumer holds no arrival-snap observer to stop.");
        ((IDisposable)observer!).Dispose();
        Require(!EchoListening, "The consumer's arrival-snap observer is still listening after its documented stop.");
        var owned = EchoOwnedPatches();
        Require(owned.Contains("Behaviour.Gameplay.IdleManager.Update"), "The consumer's ETA-sync patch disappeared with the snap.");
        Require(owned.Length >= 2, "The consumer's unrelated patches disappeared with the snap: [" + string.Join(" ", owned) + "]");
        var refusals = EchoRefusedPatches();
        Require(refusals.Length == 0, "The stopped consumer installed a direct native timing hook: " + string.Join(", ", refusals));

        int snapOffset = _echoSnaps.Count;
        int offset = _etFacts!.Count;
        SeedIdleTimer();
        foreach (var frame in DriveOwnedRoute(_etTargetC!, offset, _ => { })) yield return frame;
        var completions = Window(offset, _etFacts!).Where(fact => fact.Kind == TravelTransitionKind.RouteCompleted).ToArray();
        Require(completions.Length == 1, "The degradation leg produced " + completions.Length + " route completions.");
        Require(EchoSnaps(snapOffset).Count == 0, "The stopped consumer still ran its timer write: ["
            + string.Join("; ", EchoSnaps(snapOffset).Select(snap => snap.Describe())) + "].");
        EtRecord(EchoTravelReceipt.DegradedCase, EchoTravelReceipt.DegradedDescription, TravelStationReceipt.Passed,
            "listening=false", session, completions[0].OperationId,
            TravelStationReceipt.Evidence(completions, null),
            "consumerWrites=0 after the documented stop; ownedPatches=[" + string.Join(" ", owned)
            + "]; timingHookRefusals=0");
        // Restore the consumer's own production binding so the sandbox is left as the game found it.
        AccessTools.Method(EchoPlugin.GetType(), "BindArrivalSnap").Invoke(EchoPlugin, null);
        _echoReorderings++;
        EchoDisarm();
        SetAutopilot(false);
        EtEndCase();
    }

    /// <summary>Mandatory setup row: every declared control this phase used, with its exact counts.</summary>
    private void RecordDeclaredControls()
    {
        EtCase(EchoTravelReceipt.ControlsSubcase, EchoTravelReceipt.ControlsDescription);
        var accounting = EchoTravelReceipt.CheckSuppressionAccounting(_echoFindActivityInvocations, _echoSuppressedBodies);
        Require(accounting == null, accounting!);
        Require(!(bool)SpGet(CurrentPlayer, "autoPlay")!, "The probe left the native autopilot engaged.");
        var session = _etWindowSession;
        var evidence = TravelStationReceipt.Evidence(
            _etFacts!.Where(fact => fact.SessionId == session && fact.Kind == TravelTransitionKind.InitialPlacement), null);
        EtRecord(EchoTravelReceipt.ControlsSubcase, EchoTravelReceipt.ControlsDescription, TravelStationReceipt.Passed,
            "controls=declared", session, null, evidence,
            EchoTravelReceipt.DescribeControls(_echoSeededWrites, _echoSuppressedBodies, _echoAutopilotEngagements,
                _echoReorderings, etaSyncDisabled: true, EchoTravelReceipt.IdleTimerSeedSeconds));
        EtEndCase();
    }

    /// <summary>
    /// One Echo-owned in-system leg through the real player entry point, then the public facts of
    /// that leg. The probe never teleports and never fabricates a fact.
    /// </summary>
    private IEnumerable<object?> DriveOwnedRoute(object target, int offset, Action<Guid?> requested)
    {
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        var poiType = AccessTools.TypeByName("Source.Galaxy.MapPointOfInterest");
        var travel = SpGet(travelType, "Instance");
        Require(TravelStationDriver.Alive(travel), "No live native travel manager for the owned Echo route.");
        // Native travel refuses for three real seconds after a warp start; sample availability after it.
        foreach (var frame in PollEcho(EchoTravelReceipt.TravelReadySeconds)) yield return frame;
        Require((bool)TravelStationDriver.Bind(travelType, "CanWeTravel", typeof(bool), poiType).Invoke(travel!, new[] { target })!,
            "Native CanWeTravel refused the owned Echo route.");
        Require((bool)TravelStationDriver.Bind(travelType, "TryInitiateTravel", typeof(bool), poiType).Invoke(travel!, new[] { target })!,
            "Native TryInitiateTravel refused the owned Echo route.");
        // The request fact is emitted synchronously inside the call above, so the leg identity is
        // known before anything can complete.
        var request = Window(offset, _etFacts!).FirstOrDefault(fact => fact.Kind == TravelTransitionKind.Requested);
        Require(request != null, "The owned Echo route produced no public request fact.");
        requested(request!.OperationId);
        foreach (var frame in AwaitEcho(() => Window(offset, _etFacts!).Any(fact => fact.Kind == TravelTransitionKind.Arrived),
            EchoTravelReceipt.ArrivalSeconds, "the owned Echo route's native arrival")) yield return frame;
        foreach (var frame in AwaitEcho(() => Window(offset, _etFacts!).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
            EchoTravelReceipt.BoundarySeconds, "the owned Echo route's final boundary")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        foreach (var frame in EchoQuiesce()) yield return frame;
    }

    /// <summary>Waits for the public facts of a leg another subscriber started.</summary>
    private IEnumerable<object?> AwaitOwnedRoute(int offset)
    {
        foreach (var frame in AwaitEcho(() => Window(offset, _etFacts!).Any(fact => fact.Kind == TravelTransitionKind.Arrived),
            EchoTravelReceipt.ArrivalSeconds, "the superseding route's native arrival")) yield return frame;
        foreach (var frame in AwaitEcho(() => Window(offset, _etFacts!).Any(fact => fact.Kind == TravelTransitionKind.RouteCompleted),
            EchoTravelReceipt.BoundarySeconds, "the superseding route's final boundary")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        foreach (var frame in EchoQuiesce()) yield return frame;
    }

    private static IEnumerable<object?> PollEcho(float seconds)
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until) yield return null;
    }

    // --- shared helpers -------------------------------------------------------------------------

    private EchoTravelReceipt.RouteFact EchoFact(TravelTransition fact)
        => new(fact.SessionId, fact.OperationId ?? Guid.Empty, fact.Sequence,
            _etFactFrames.TryGetValue(fact.Sequence, out int frame) ? frame : -1);

    private string DescribeSnap(EchoTravelReceipt.RouteFact fact)
    {
        var snap = EchoSnaps(_etSnapOffset).FirstOrDefault(observation => observation.Frame == fact.Frame);
        var next = EchoIdleTicks(_etIdleOffset).Where(tick => tick.Frame > fact.Frame).OrderBy(tick => tick.Frame).FirstOrDefault();
        return "fact=" + fact.Describe() + "; consumerWrite=" + snap.Describe() + "; nextIdle=" + next.Describe();
    }

    private static List<EchoTravelReceipt.SnapObservation> EchoSnaps(int offset)
        => TravelStationReceipt.Window(_echoSnaps, offset);

    private static List<EchoTravelReceipt.IdleObservation> EchoIdleTicks(int offset)
        => TravelStationReceipt.Window(_echoIdle, offset);

    private static List<T> Window<T>(int offset, IReadOnlyList<T> observed) => TravelStationReceipt.Window(observed, offset);

    private static int SessionWindowOffset(Guid session, IReadOnlyList<TravelTransition> facts)
    {
        for (int index = 0; index < facts.Count; index++)
            if (facts[index].SessionId == session) return index;
        return facts.Count;
    }

    private IEnumerable<object?> AwaitEchoPlacement(Guid session)
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + EchoTravelReceipt.PlacementSeconds;
        while (!_etFacts!.Any(fact => fact.SessionId == session && fact.Kind == TravelTransitionKind.InitialPlacement))
        {
            Require(_api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for the Echo window's placement fact.");
            Require(Time.realtimeSinceStartup < until, "Timed out waiting for the freshly loaded session's public placement fact.");
            yield return null;
        }
    }

    private IEnumerable<object?> EchoQuiesce()
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + EchoTravelReceipt.QuiescenceSeconds;
        int observed = _etFacts!.Count;
        int stable = 0;
        while (stable < 3)
        {
            Require(Time.realtimeSinceStartup < until, "Timed out waiting for public travel callback quiescence.");
            yield return null;
            if (_etFacts.Count != observed) { observed = _etFacts.Count; stable = 0; }
            else if (ModApi.Travel?.IsDispatchingCallbacks == true) stable = 0;
            else stable++;
        }
    }

    /// <summary>Bounded wait for the native idle update that follows an observed consumer write.</summary>
    private IEnumerable<object?> AwaitIdleDecision(TravelTransition fact)
    {
        int frame = _etFactFrames.TryGetValue(fact.Sequence, out int recorded) ? recorded : Time.frameCount;
        foreach (var step in AwaitEcho(() => _echoIdle.Any(tick => tick.Frame > frame),
            EchoTravelReceipt.IdleDecisionSeconds, "the next native idle update after the consumer's write")) yield return step;
    }

    private IEnumerable<object?> AwaitEcho(Func<bool> ready, float seconds, string description)
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + seconds;
        while (!ready())
        {
            Require(_api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for " + description + ".");
            Require(Time.realtimeSinceStartup < until, "Timed out after " + seconds + "s waiting for " + description + ".");
            yield return null;
        }
    }

    private static string? EchoCaseFor(string crossCase)
        => crossCase == TravelCrossSystemReceipt.JumpGateCase ? EchoTravelReceipt.GateCase
            : crossCase == TravelCrossSystemReceipt.WormholeCase ? EchoTravelReceipt.WormholeCase : null;

    private static string EchoDescriptionFor(string consumerCase)
        => consumerCase == EchoTravelReceipt.GateCase ? EchoTravelReceipt.GateDescription : EchoTravelReceipt.WormholeDescription;
}
