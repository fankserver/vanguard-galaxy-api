using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

// Native FAST-LANE gate-to-gate qualification pilot, phase travel-fast-lane-v1.
//
// It is a SEPARATE optional phase on top of the four existing travel phases. The post-gate
// continuation phase cannot reach the native fast lane by construction: the inspected
// GamePlayer.DoFastLaneTravel() is true only when the NEXT waypoint is a usable JumpGate, and that
// phase deliberately ends at a safe non-gate POI. This phase drives one real planner route across
// TWO gates into a third system instead, so the intermediate system's next waypoint is another gate
// and the native charge branch (TheGate.ChargeFastLaneTravelToNextGate, travelMultiplier = 7f) runs.
//
// Like the other phases it ASSERTS only through the public ITravelEvents surface and DRIVES only
// actual vanilla entry points and Unity coroutines: it never invokes adapter callbacks, never
// assigns a location or a waypoint, never fabricates an arrival and never disables a production
// hook. It also never WRITES the fixture's fast-lane unlock flag or any other native/save state:
// the flag is only read, and a locked fixture produces an honest NOT-RUN.
public sealed partial class Plugin
{
    private bool TravelFastLaneSelected => File.Exists(Path.Combine(_root!, "travel-fast-lane.enabled"));
    internal readonly List<TravelStationReceipt.Row> _flRows = new();
    internal readonly List<string> _flEvents = new();
    // Read-only native state sampled at the moment each public travel fact was delivered, keyed by
    // the API sequence of that fact. It is evidence about the loaded world, never a drive.
    internal readonly Dictionary<long, TravelFastLaneReceipt.NativeSnapshot> _flSnapshots = new();
    internal List<TravelTransition>? PendingFastLaneTravel;
    internal List<StationTransition>? PendingFastLaneStation;
    internal NativeCaseOwner? FastLaneOwner;
    internal const string FastLaneStartCase = "phase-start";
    internal string _flCase = FastLaneStartCase;
    internal string _flDescription = "The phase is preparing its route.";

    internal void FlCase(string id, string description) { _flCase = id; _flDescription = description; }
    internal void FlEndCase() => FlCase(TravelStationReceipt.NoActiveCase, "No case is driving.");
    internal void FlRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
        => _flRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));

    // Incremental, atomic checkpoint after every case: an external termination can then only leave
    // INCOMPLETE evidence behind, never a stale PASS and never an empty directory.
    internal void FlCheckpoint()
    {
        WriteAtomic("travel-fast-lane-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_flRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-fast-lane-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_flEvents));
        WriteAtomic("travel-fast-lane.txt", new[] { TravelFastLaneReceipt.SummarizeIncomplete(_flRows, _flCase) });
    }

    private IEnumerable<object?> CheckTravelFastLane()
    {
        if (!TravelFastLaneSelected) yield break;
        var run = RunTravelFastLane().GetEnumerator();
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
            FlRecord(_flCase, _flDescription, TravelStationReceipt.Failed, FastLaneFaultPosition(), _api?.CurrentSession?.Id, null, "",
                fault.Split('\n')[0].Trim());
        }
        WriteAtomic("travel-fast-lane-receipt.tsv",
            new[] { TravelStationReceipt.ReceiptHeader }.Concat(_flRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-fast-lane-events.tsv",
            new[] { TravelStationReceipt.EventsHeader }.Concat(_flEvents));
        var failure = TravelFastLaneReceipt.Evaluate(_flRows, fault, _flEvents);
        WriteAtomic("travel-fast-lane.txt", new[] { TravelFastLaneReceipt.Summarize(_flRows, fault, _flEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "travel-fast-lane-fault.txt"), fault);
        Require(failure == null, "Native fast-lane phase " + TravelFastLaneReceipt.Phase + " failed: " + failure);
        Passed("native-travel-" + TravelFastLaneReceipt.Phase);
    }

    private string FastLaneFaultPosition()
    {
        try { return FastLaneSnapshot().ToDetail() + "; " + FastLaneOwnership(); }
        catch (Exception error) { return "native position unavailable: " + error.GetType().Name; }
    }

    internal string FastLaneOwnership()
    {
        var owner = FastLaneOwner;
        if (owner == null) return "owner=<none captured>";
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        return owner.Describe(_api?.CurrentSession?.Id,
            travelType == null ? null : SpGet(travelType, "Instance"), SpGet(_player, "current"), TravelStationDriver.Alive);
    }

    // True only when the live native travel manager and player are still the exact instances the
    // driving case captured, in the same session. Never throws: it is read during callbacks.
    private bool FastLaneOwned(object? liveManager)
    {
        try
        {
            var owner = FastLaneOwner;
            return owner != null && owner.CheckCurrent("an observation", _api?.CurrentSession?.Id,
                liveManager, SpGet(_player, "current"), TravelStationDriver.Alive) == null;
        }
        catch { return false; }
    }

    // Read-only sample of the loaded world at a public fact, including the native travel multiplier
    // the fast-lane charge branch sets. Ship positions are deliberately never read, and nothing is
    // written.
    internal TravelFastLaneReceipt.NativeSnapshot FastLaneSnapshot()
    {
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        var manager = travelType == null ? null : SpGet(travelType, "Instance");
        var player = SpGet(_player, "current");
        if (!TravelStationDriver.Alive(manager) || player == null)
            return new TravelFastLaneReceipt.NativeSnapshot(false, false, false, false, 0, false, 0, "<no travel manager or player>", false);
        var system = SpGet(player, "currentSystem");
        var poi = SpGet(player, "currentPointOfInterest");
        return new TravelFastLaneReceipt.NativeSnapshot(
            poi != null,
            NativeManagerReadyFor(manager!, poi),
            (bool)TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool))!,
            (bool)SpGet(manager!, "usingJumpgate")!,
            (float)SpGet(manager!, "travelMultiplier")!,
            (bool)SpGet(manager!, "fastLaneTravelActive")!,
            ((ICollection)SpGet(player, "waypoints")!).Count,
            system == null ? "<no player system>"
                : TravelStationReceipt.Location((string)SpGet(system, "guid")!, poi == null ? null : (string)SpGet(poi, "guid")!),
            FastLaneOwned(manager));
    }

    private IEnumerable<object?> RunTravelFastLane()
    {
        Require(TravelStationSelected, "The fast-lane phase requires the travel/station selection that enables the native travel capability.");
        var travel = ModApi.Travel;
        Require(travel != null, "Travel public service not exposed.");
        Require(_api!.Capabilities.Any(capability => capability.Name == "native-travel" && capability.Available), "native-travel capability not available.");
        Require(!travel!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        Require(TravelFastLaneReceipt.ReadinessSeconds == WaitDeadlineSeconds && TravelFastLaneReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(TravelFastLaneReceipt.PhaseBudgetSeconds <= TravelFastLaneReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        var transitions = new List<TravelTransition>();
        var stationFacts = new List<StationTransition>();
        using (travel.Subscribe("qualification.travel.fast-lane", fact =>
        {
            transitions.Add(fact);
            _flEvents.Add(TravelStationReceipt.TravelEventRow(_flCase, fact));
            // Sampled inline, in the same native dispatch, so the snapshot describes the world at
            // the exact boundary that produced the fact - which is the only place the fast-lane
            // transient can be observed.
            try { _flSnapshots[fact.Sequence] = FastLaneSnapshot(); }
            catch (Exception error)
            {
                _flSnapshots[fact.Sequence] = new TravelFastLaneReceipt.NativeSnapshot(false, false, false, false, 0, false, 0,
                    "<snapshot failed: " + error.GetType().Name + ">", false);
            }
        }))
        using (ModApi.Station!.Subscribe("qualification.station.fast-lane", fact =>
        {
            stationFacts.Add(fact);
            _flEvents.Add(TravelStationReceipt.StationEventRow(_flCase, fact));
        }))
        {
            PendingFastLaneTravel = transitions;
            PendingFastLaneStation = stationFacts;
            try
            {
                FlCheckpoint();
                var driver = new TravelFastLaneDriver(this);
                foreach (var step in driver.Run()) yield return step;
            }
            finally
            {
                PendingFastLaneTravel = null;
                PendingFastLaneStation = null;
            }
        }
    }
}
