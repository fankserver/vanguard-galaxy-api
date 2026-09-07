using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

// Native travel RECOVERY/CONTINUATION qualification pilot, phase travel-recovery-continuation-v1.
//
// It is a SEPARATE optional phase on top of travel-in-system-station-v1, travel-cross-system-v1
// and travel-resilience-v1. The cross-system phase deliberately drives ONE direct hop and requires
// its route to complete at the jump destination, so it can neither prove the post-gate chain
// continuation nor be reused for it; this phase drives a real multi-waypoint route instead.
//
// Like the other phases it ASSERTS only through the public ITravelEvents surface and DRIVES only
// actual vanilla entry points and Unity coroutines: it never invokes adapter callbacks, never
// assigns a location or a waypoint, never fabricates an arrival and never disables a production
// hook to manufacture an event.
public sealed partial class Plugin
{
    private bool TravelRecoverySelected => File.Exists(Path.Combine(_root!, "travel-recovery.enabled"));
    internal readonly List<TravelStationReceipt.Row> _rcRows = new();
    internal readonly List<string> _rcEvents = new();
    // Read-only native state sampled at the moment each public travel fact was delivered, keyed by
    // the API sequence of that fact. It is evidence about the loaded world, never a drive.
    internal readonly Dictionary<long, TravelRecoveryReceipt.NativeSnapshot> _rcSnapshots = new();
    internal List<TravelTransition>? PendingRecoveryTravel;
    internal List<StationTransition>? PendingRecoveryStation;
    // The immutable native owner of the case that is currently driving, captured at that case's own
    // fresh fixture load.
    internal NativeCaseOwner? RecoveryOwner;
    internal const string RecoveryStartCase = "phase-start";
    internal string _rcCase = RecoveryStartCase;
    internal string _rcDescription = "The phase is preparing its first case.";

    internal void RcCase(string id, string description) { _rcCase = id; _rcDescription = description; }
    internal void RcEndCase() => RcCase(TravelStationReceipt.NoActiveCase, "No case is driving.");
    internal void RcRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
        => _rcRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));

    // One recovery attempt is persisted the moment it STARTS and is rewritten in place with its
    // outcome, so an attempt that later throws can never erase an earlier attempt's outcome and an
    // external termination still finds the attempt history on disk. Returns the row index the
    // driver rewrites; attempts are always NOT-RUN rows (diagnostics, never coverage).
    internal int RcBeginAttempt(Guid? session, string detail)
    {
        _rcRows.Add(new TravelStationReceipt.Row(TravelRecoveryReceipt.RecoveryAttemptRow,
            TravelRecoveryReceipt.RecoveryAttemptDescription, TravelStationReceipt.NotRun, "",
            session?.ToString() ?? "", "", "", detail));
        RcCheckpoint();
        return _rcRows.Count - 1;
    }

    internal void RcCompleteAttempt(int index, Guid? session, string detail)
    {
        _rcRows[index] = new TravelStationReceipt.Row(TravelRecoveryReceipt.RecoveryAttemptRow,
            TravelRecoveryReceipt.RecoveryAttemptDescription, TravelStationReceipt.NotRun, "",
            session?.ToString() ?? "", "", "", detail);
        RcCheckpoint();
    }

    // Incremental, atomic checkpoint after every case: an external termination can then only leave
    // INCOMPLETE evidence behind, never a stale PASS and never an empty directory.
    internal void RcCheckpoint()
    {
        WriteAtomic("travel-recovery-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_rcRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-recovery-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_rcEvents));
        WriteAtomic("travel-recovery.txt", new[] { TravelRecoveryReceipt.SummarizeIncomplete(_rcRows, _rcCase) });
    }

    private IEnumerable<object?> CheckTravelRecovery()
    {
        if (!TravelRecoverySelected) yield break;
        var run = RunTravelRecovery().GetEnumerator();
        string? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
            // Iterators cannot catch around a yield, so the pilot body is stepped explicitly: a
            // fault is attributed to the case that was running and never loses its diagnostics.
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
            RcRecord(_rcCase, _rcDescription, TravelStationReceipt.Failed, RecoveryFaultPosition(), _api?.CurrentSession?.Id, null, "",
                fault.Split('\n')[0].Trim());
        }
        WriteAtomic("travel-recovery-receipt.tsv",
            new[] { TravelStationReceipt.ReceiptHeader }.Concat(_rcRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-recovery-events.tsv",
            new[] { TravelStationReceipt.EventsHeader }.Concat(_rcEvents));
        var failure = TravelRecoveryReceipt.Evaluate(_rcRows, fault, _rcEvents);
        WriteAtomic("travel-recovery.txt", new[] { TravelRecoveryReceipt.Summarize(_rcRows, fault, _rcEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "travel-recovery-fault.txt"), fault);
        Require(failure == null, "Native travel recovery/continuation phase " + TravelRecoveryReceipt.Phase + " failed: " + failure);
        Passed("native-travel-" + TravelRecoveryReceipt.Phase);
    }

    // Preserved with a failure: the actual native travel position and the captured owner's
    // identity/liveness when the phase faulted or timed out.
    private string RecoveryFaultPosition()
    {
        try { return RecoverySnapshot().ToDetail() + "; " + RecoveryOwnership(); }
        catch (Exception error) { return "native position unavailable: " + error.GetType().Name; }
    }

    internal string RecoveryOwnership()
    {
        var owner = RecoveryOwner;
        if (owner == null) return "owner=<none captured>";
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        return owner.Describe(_api?.CurrentSession?.Id,
            travelType == null ? null : SpGet(travelType, "Instance"), SpGet(_player, "current"), TravelStationDriver.Alive);
    }

    // True only when the live native travel manager and player are still the exact instances the
    // driving case captured, in the same session. Never throws: it is read during callbacks.
    private bool RecoveryOwned(object? liveManager)
    {
        try
        {
            var owner = RecoveryOwner;
            return owner != null && owner.CheckCurrent("an observation", _api?.CurrentSession?.Id,
                liveManager, SpGet(_player, "current"), TravelStationDriver.Alive) == null;
        }
        catch { return false; }
    }

    // Read-only sample of the loaded world at a public fact. Ship positions are deliberately never
    // read, so a direct teleport can never be mistaken for a departure, an arrival or a placement.
    internal TravelRecoveryReceipt.NativeSnapshot RecoverySnapshot()
    {
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        var manager = travelType == null ? null : SpGet(travelType, "Instance");
        var player = SpGet(_player, "current");
        if (!TravelStationDriver.Alive(manager) || player == null)
            return new TravelRecoveryReceipt.NativeSnapshot(false, false, false, false, 0, "<no travel manager or player>", false);
        var system = SpGet(player, "currentSystem");
        var poi = SpGet(player, "currentPointOfInterest");
        return new TravelRecoveryReceipt.NativeSnapshot(
            poi != null,
            NativeManagerReadyFor(manager!, poi),
            (bool)TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool))!,
            (bool)SpGet(manager!, "usingJumpgate")!,
            ((System.Collections.ICollection)SpGet(player, "waypoints")!).Count,
            system == null ? "<no player system>"
                : TravelStationReceipt.Location((string)SpGet(system, "guid")!, poi == null ? null : (string)SpGet(poi, "guid")!),
            RecoveryOwned(manager));
    }

    // Bounded read-only residual of the vanilla travel state, recorded beside a failure. It names
    // the flags an ordinary player cancel does NOT reset - notably isWarping, which only the end of
    // TravelInSystem clears - so a failed phase never claims to have left a quiet world. Nothing is
    // written to make it look clean.
    internal string RecoveryResidual()
    {
        try
        {
            var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
            var manager = travelType == null ? null : SpGet(travelType, "Instance");
            var player = SpGet(_player, "current");
            if (!TravelStationDriver.Alive(manager) || player == null) return "residual=<no travel manager or player>";
            return "residual={travelActive=" + (bool)TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool))!
                + ",isWarping=" + (bool)SpGet(manager!, "isWarping")!
                + ",usingJumpgate=" + (bool)SpGet(manager!, "usingJumpgate")!
                + ",waypoints=" + ((System.Collections.ICollection)SpGet(player, "waypoints")!).Count
                + "} (an ordinary cancel does not reset isWarping; a failed phase leaves no world a later phase may continue from)";
        }
        catch (Exception error) { return "residual=<unavailable: " + error.GetType().Name + ">"; }
    }

    // The native local POI manager is the initialized manager of EXACTLY the player's current POI.
    // This is the same shape the adapter's own readiness observation requires, read here only to
    // record what the world reported at that moment.
    internal static bool NativeManagerReadyFor(object manager, object? playerPoi)
    {
        var local = SpGet(manager, "localPoiManager");
        if (!TravelStationDriver.Alive(local)) return false;
        return ReferenceEquals(SpGet(local!, "poi"), playerPoi) && (bool)SpGet(local!, "initializedAndReady")!;
    }

    private IEnumerable<object?> RunTravelRecovery()
    {
        Require(TravelStationSelected, "The recovery/continuation phase requires the travel/station selection that enables the native travel capability.");
        var travel = ModApi.Travel;
        Require(travel != null, "Travel public service not exposed.");
        Require(_api!.Capabilities.Any(capability => capability.Name == "native-travel" && capability.Available), "native-travel capability not available.");
        Require(!travel!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        // The published phase budget is summed from the declared deadlines, and the two shared
        // harness waits are part of that sum: refuse to run if they no longer agree.
        Require(TravelRecoveryReceipt.ReadinessSeconds == WaitDeadlineSeconds && TravelRecoveryReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(TravelRecoveryReceipt.PhaseBudgetSeconds <= TravelRecoveryReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        var transitions = new List<TravelTransition>();
        var stationFacts = new List<StationTransition>();
        using (travel.Subscribe("qualification.travel.recovery", fact =>
        {
            transitions.Add(fact);
            _rcEvents.Add(TravelStationReceipt.TravelEventRow(_rcCase, fact));
            // Sampled inline, in the same native dispatch, so the snapshot describes the world at
            // the exact boundary that produced the fact.
            try { _rcSnapshots[fact.Sequence] = RecoverySnapshot(); }
            catch (Exception error)
            {
                _rcSnapshots[fact.Sequence] = new TravelRecoveryReceipt.NativeSnapshot(false, false, false, false, 0,
                    "<snapshot failed: " + error.GetType().Name + ">", false);
            }
        }))
        using (ModApi.Station!.Subscribe("qualification.station.recovery", fact =>
        {
            stationFacts.Add(fact);
            _rcEvents.Add(TravelStationReceipt.StationEventRow(_rcCase, fact));
        }))
        {
            PendingRecoveryTravel = transitions;
            PendingRecoveryStation = stationFacts;
            try
            {
                RcCheckpoint();
                var driver = new TravelRecoveryDriver(this);
                foreach (var step in driver.Run()) yield return step;
            }
            finally
            {
                PendingRecoveryTravel = null;
                PendingRecoveryStation = null;
            }
        }
    }
}
