using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

// Native travel RESILIENCE qualification pilot, phase travel-resilience-v1.
//
// It is a SEPARATE optional phase on top of travel-in-system-station-v1 and
// travel-cross-system-v1: both keep their own required cases and keep recording these three
// matrix cells as their own optional NOT-RUN rows, so nothing here turns an earlier NOT-RUN into
// a coverage claim.
//
// Like the other phases it ASSERTS only through the public ITravelEvents / IStationEvents
// surfaces and DRIVES only actual vanilla entry points and Unity coroutines: it never invokes
// adapter callbacks, never assigns a location, never fabricates an arrival or a dock, and never
// writes a native docking state field to simulate one. Its three required cases are the three
// residual travel-matrix cells: the empty-origin re-route departure boundary, the restore/relink
// Dock() suppression path, and stale-session replay of an old coroutine after a replacement load.
public sealed partial class Plugin
{
    private bool TravelResilienceSelected => File.Exists(Path.Combine(_root!, "travel-resilience.enabled"));
    internal readonly List<TravelStationReceipt.Row> _rsRows = new();
    internal readonly List<string> _rsEvents = new();
    // Read-only native state sampled at the moment each public travel fact was delivered, keyed by
    // the API sequence of that fact. It is evidence about the loaded world, never a drive.
    internal readonly Dictionary<long, TravelResilienceReceipt.NativeSnapshot> _rsSnapshots = new();
    internal List<TravelTransition>? PendingResilienceTravel;
    internal List<StationTransition>? PendingResilienceStation;
    // The immutable native owner of the case that is currently driving, captured at that case's own
    // fresh fixture load. Observation snapshots record whether the live world is still that owner.
    internal NativeCaseOwner? ResilienceOwner;
    internal const string ResilienceStartCase = "phase-start";
    internal string _rsCase = ResilienceStartCase;
    internal string _rsDescription = "The phase is preparing its first case.";

    // The active label is only the observation context of the event trace; case rows always carry
    // their own identity, and the label is reset between cases so no row can claim another case's
    // facts.
    internal void RsCase(string id, string description) { _rsCase = id; _rsDescription = description; }
    internal void RsEndCase() => RsCase(TravelStationReceipt.NoActiveCase, "No case is driving.");
    internal void RsRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
        => _rsRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));

    // Incremental, atomic checkpoint after every case: an external termination (a launcher kill or
    // timeout) can then only leave INCOMPLETE evidence behind, never a stale PASS and never an
    // empty directory.
    internal void RsCheckpoint()
    {
        WriteAtomic("travel-resilience-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_rsRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-resilience-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_rsEvents));
        WriteAtomic("travel-resilience.txt", new[] { TravelResilienceReceipt.SummarizeIncomplete(_rsRows, _rsCase) });
    }

    private IEnumerable<object?> CheckTravelResilience()
    {
        if (!TravelResilienceSelected) yield break;
        var run = RunTravelResilience().GetEnumerator();
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
            RsRecord(_rsCase, _rsDescription, TravelStationReceipt.Failed, ResilienceFaultPosition(), _api?.CurrentSession?.Id, null, "",
                fault.Split('\n')[0].Trim());
        }
        WriteAtomic("travel-resilience-receipt.tsv",
            new[] { TravelStationReceipt.ReceiptHeader }.Concat(_rsRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-resilience-events.tsv",
            new[] { TravelStationReceipt.EventsHeader }.Concat(_rsEvents));
        var failure = TravelResilienceReceipt.Evaluate(_rsRows, fault, _rsEvents);
        WriteAtomic("travel-resilience.txt", new[] { TravelResilienceReceipt.Summarize(_rsRows, fault, _rsEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "travel-resilience-fault.txt"), fault);
        Require(failure == null, "Native travel resilience phase " + TravelResilienceReceipt.Phase + " failed: " + failure);
        Passed("native-travel-" + TravelResilienceReceipt.Phase);
    }

    // Preserved with a failure: the actual native travel/dock position and the captured owner's
    // identity/liveness when the phase faulted or timed out.
    private string ResilienceFaultPosition()
    {
        try { return ResilienceSnapshot().ToDetail() + "; " + ResilienceOwnership(); }
        catch (Exception error) { return "native position unavailable: " + error.GetType().Name; }
    }

    // Identity, liveness and session of the captured owner against the live world. This is the
    // diagnostic that distinguishes "the native call refused" from "the probe held a manager a
    // later fixture load destroyed" (the qa-80 failure mode).
    internal string ResilienceOwnership()
    {
        var owner = ResilienceOwner;
        if (owner == null) return "owner=<none captured>";
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        return owner.Describe(_api?.CurrentSession?.Id,
            travelType == null ? null : SpGet(travelType, "Instance"), SpGet(_player, "current"), TravelStationDriver.Alive);
    }

    // True only when the live native travel manager and player are still the exact instances the
    // driving case captured, in the same session. Never throws: it is read during callbacks.
    private bool ResilienceOwned(object? liveManager)
    {
        try
        {
            var owner = ResilienceOwner;
            return owner != null && owner.CheckCurrent("an observation", _api?.CurrentSession?.Id,
                liveManager, SpGet(_player, "current"), TravelStationDriver.Alive) == null;
        }
        catch { return false; }
    }

    // Read-only sample of the loaded world at a public fact. An unloaded origin (no current POI)
    // and a loaded POI are both supported; ship positions are deliberately never read, so a direct
    // teleport can never be mistaken for a departure or an arrival.
    internal TravelResilienceReceipt.NativeSnapshot ResilienceSnapshot()
    {
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        var manager = travelType == null ? null : SpGet(travelType, "Instance");
        var player = SpGet(_player, "current");
        if (!TravelStationDriver.Alive(manager) || player == null)
            return new TravelResilienceReceipt.NativeSnapshot(false, false, false, false, "", "<no travel manager or player>", false);
        var local = SpGet(manager!, "localPoiManager");
        var system = SpGet(player, "currentSystem");
        var poi = SpGet(player, "currentPointOfInterest");
        var shipData = SpGet(player, "currentSpaceShip");
        return new TravelResilienceReceipt.NativeSnapshot(
            poi != null,
            TravelStationDriver.Alive(local),
            (bool)TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool))!,
            (bool)SpGet(manager!, "isWarping")!,
            shipData == null ? "" : SpGet(shipData, "dockingState")?.ToString() ?? "",
            system == null ? "<no player system>"
                : TravelStationReceipt.Location((string)SpGet(system, "guid")!, poi == null ? null : (string)SpGet(poi, "guid")!),
            ResilienceOwned(manager));
    }

    private IEnumerable<object?> RunTravelResilience()
    {
        Require(TravelStationSelected, "The resilience phase requires the travel/station selection that enables the native travel capability.");
        var travel = ModApi.Travel;
        var station = ModApi.Station;
        Require(travel != null && station != null, "Travel/Station public services not exposed.");
        Require(_api!.Capabilities.Any(capability => capability.Name == "native-travel" && capability.Available), "native-travel capability not available.");
        Require(!travel!.IsDispatchingCallbacks && !station!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        // The published phase budget is summed from the declared deadlines, and the two shared
        // harness waits are part of that sum: refuse to run if they no longer agree.
        Require(TravelResilienceReceipt.ReadinessSeconds == WaitDeadlineSeconds && TravelResilienceReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(TravelResilienceReceipt.PhaseBudgetSeconds <= TravelResilienceReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        var transitions = new List<TravelTransition>();
        var stationFacts = new List<StationTransition>();
        using (travel.Subscribe("qualification.travel.resilience", fact =>
        {
            transitions.Add(fact);
            _rsEvents.Add(TravelStationReceipt.TravelEventRow(_rsCase, fact));
            // Sampled inline, in the same native dispatch, so the snapshot describes the world at
            // the exact boundary that produced the fact.
            try { _rsSnapshots[fact.Sequence] = ResilienceSnapshot(); }
            catch (Exception error)
            {
                _rsSnapshots[fact.Sequence] = new TravelResilienceReceipt.NativeSnapshot(false, false, false, false, "",
                    "<snapshot failed: " + error.GetType().Name + ">", false);
            }
        }))
        using (station!.Subscribe("qualification.station.resilience", fact =>
        {
            stationFacts.Add(fact);
            _rsEvents.Add(TravelStationReceipt.StationEventRow(_rsCase, fact));
        }))
        {
            PendingResilienceTravel = transitions;
            PendingResilienceStation = stationFacts;
            try
            {
                RsCheckpoint();
                var driver = new TravelResilienceDriver(this);
                foreach (var step in driver.Run()) yield return step;
            }
            finally
            {
                PendingResilienceTravel = null;
                PendingResilienceStation = null;
            }
        }
    }
}
